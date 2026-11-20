using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Tests;

public class MatchingTests : IClassFixture<MuseFmFactory>
{
    private readonly MuseFmFactory _factory;
    private static long _clock = 1_700_000_000;

    public MatchingTests(MuseFmFactory factory) => _factory = factory;

    [Theory]
    [InlineData("Björk", "Bjork")]
    [InlineData("AC/DC", "AC DC")]
    [InlineData("Guns N' Roses", "Guns N’ Roses")]
    [InlineData("The Beatles", "beatles")]
    [InlineData("Simon & Garfunkel", "Simon and Garfunkel")]
    [InlineData("Florence + the Machine", "Florence and the Machine")]
    [InlineData("Sigur Rós", "Sigur Ros")]
    [InlineData("Røyksopp", "Royksopp")]
    [InlineData("ＡＩＲ", "Air")] // full-width letters
    public void ArtistKey_IgnoresSpellingDetails(string a, string b) => Assert.Equal(TextKey.ArtistKey(a), TextKey.ArtistKey(b));

    [Theory]
    [InlineData("が", "か")] // a dakuten makes a different syllable
    [InlineData("हिंदी", "हदी")] // Devanagari vowel signs are part of the word
    [InlineData("Air", "Fair")]
    [InlineData("!!!", "???")]
    public void ArtistKey_KeepsDifferentNamesApart(string a, string b) => Assert.NotEqual(TextKey.ArtistKey(a), TextKey.ArtistKey(b));

    [Theory]
    [InlineData("Teardrop - 2011 Remaster", "Teardrop")]
    [InlineData("Teardrop (Remastered 2009)", "teardrop")]
    [InlineData("Get Lucky (Radio Edit)", "Get Lucky")]
    [InlineData("Strobe [Original Mix]", "Strobe")]
    [InlineData("Help! - Mono", "Help")]
    [InlineData("Song (Album Version) - 2014 Remaster", "Song")]
    [InlineData("Don't Stop Me Now", "Dont Stop Me Now")]
    public void TitleKey_IgnoresReleaseTags(string a, string b) => Assert.Equal(TextKey.TitleKey(a), TextKey.TitleKey(b));

    [Theory]
    [InlineData("Teardrop (Live)", "Teardrop")]
    [InlineData("Teardrop (Instrumental)", "Teardrop")]
    [InlineData("Strobe (Club Mix)", "Strobe")]
    [InlineData("Another Brick in the Wall, Pt. 1", "Another Brick in the Wall, Pt. 2")]
    [InlineData("Credit", "Cr")] // tags are whole trailing parts, never substrings
    [InlineData("Meditation", "Mation")]
    public void TitleKey_KeepsDifferentRecordingsApart(string a, string b) => Assert.NotEqual(TextKey.TitleKey(a), TextKey.TitleKey(b));

    [Fact]
    public void AlbumKey_FoldsEditions()
    {
        Assert.Equal(TextKey.AlbumKey("Mezzanine"), TextKey.AlbumKey("Mezzanine (Deluxe Edition)"));
        Assert.Equal(TextKey.AlbumKey("Isles"), TextKey.AlbumKey("Isles [Remastered]"));
        Assert.Equal(TextKey.AlbumKey("OK Computer"), TextKey.AlbumKey("OK Computer - 20th Anniversary Edition"));
        Assert.Equal("deluxe", TextKey.AlbumKey("Deluxe")); // never strips the whole title
        Assert.NotEqual("", TextKey.TitleKey("(Radio Edit)"));
    }

    [Theory]
    [InlineData("Bicep", "Saku (with Clara La San)", "Saku", new[] { "Bicep", "Clara La San" })]
    [InlineData("Bicep", "Saku feat. Clara La San", "Saku", new[] { "Bicep", "Clara La San" })]
    [InlineData("Bicep", "Saku - feat. Clara La San", "Saku", new[] { "Bicep", "Clara La San" })]
    [InlineData("Bicep", "Saku ft. Clara La San - Radio Edit", "Saku - Radio Edit", new[] { "Bicep", "Clara La San" })]
    [InlineData("Bicep", "Saku [ft Clara La San]", "Saku", new[] { "Bicep", "Clara La San" })]
    [InlineData("Bicep (feat. Clara La San)", "Saku", "Saku", new[] { "Bicep", "Clara La San" })]
    [InlineData("Bjork feat. Björk", "Song", "Song", new[] { "Bjork" })]
    [InlineData("Some Band", "Six Ft Deep", "Six Ft Deep", new[] { "Some Band" })] // a bare "ft" outside brackets is a word
    public void Parse_FindsFeaturedArtistsInMoreShapes(string artist, string title, string expectedTitle, string[] expectedArtists)
    {
        var (parsedTitle, artists) = MetadataParser.Parse(title, [artist]);
        Assert.Equal(expectedTitle, parsedTitle);
        Assert.Equal(expectedArtists, artists);
    }

    [Fact]
    public void Credit_MatchesOnTheMainArtist()
    {
        static bool Match(string[] a, string[] b) => new ArtistCredit(a).Matches(new ArtistCredit(b));
        Assert.True(Match(["Bicep"], ["Bicep", "Clara La San"]));
        Assert.True(Match(["B", "A"], ["A", "B"]));
        Assert.True(Match(["Daft Punk & Pharrell"], ["Daft Punk"])); // a combined name counts by its parts
        Assert.False(Match(["A", "C"], ["B", "C"])); // only a featured artist in common
        Assert.False(Match(["Paul Simon"], ["Simon & Garfunkel"]));
    }

    private ScrobbleInput S(string title, string[] artists, string? album = null) =>
        new() { Title = title, Artists = artists, Album = album, ListenedAt = Interlocked.Increment(ref _clock) };

    private Task IngestAsync(string userId, params ScrobbleInput[] inputs) =>
        _factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>().IngestAsync(userId, inputs));

    private Task<List<(string Title, string[] Artists, int Scrobbles)>> TracksAsync(string title) =>
        _factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var key = TextKey.TitleKey(title);
            var rows = await db.Tracks.Where(t => t.Key == key).OrderBy(t => t.Id)
                .Select(t => new
                {
                    t.Title,
                    Artists = t.Artists.OrderBy(a => a.Position).Select(a => a.Artist.Name).ToList(),
                    Scrobbles = db.Scrobbles.Count(s => s.TrackId == t.Id),
                })
                .ToListAsync();
            return rows.Select(r => (r.Title, r.Artists.ToArray(), r.Scrobbles)).ToList();
        });

    [Fact]
    public async Task FeaturedArtistVariants_AreOneTrack()
    {
        var (user, _) = await _factory.CreateUserAsync("feat-user");
        await IngestAsync(user.Id, S("Saku", ["Bicep"], "Isles"));
        await IngestAsync(user.Id, S("Saku", ["Bicep feat. Clara La San"], "Isles"));
        await IngestAsync(user.Id, S("Saku (feat. Clara La San)", ["Bicep"]));
        await IngestAsync(user.Id, S("Saku (with Clara La San)", ["Bicep"]));
        await IngestAsync(user.Id, S("Saku - 2021 Remaster", ["Bicep", "Clara La San"], "Isles (Deluxe Edition)"));
        // Within one submission too.
        await IngestAsync(user.Id, S("Glue", ["Bicep"]), S("Glue (Radio Edit)", ["BICEP"]), S("Glue", ["Bicep feat. Someone"]));

        var saku = Assert.Single(await TracksAsync("Saku"));
        Assert.Equal(("Saku", 5), (saku.Title, saku.Scrobbles));
        Assert.Equal(["Bicep", "Clara La San"], saku.Artists);
        var glue = Assert.Single(await TracksAsync("Glue"));
        Assert.Equal(new[] { "Bicep", "Someone" }, glue.Artists);
        Assert.Equal(3, glue.Scrobbles);

        var isles = await _factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Albums
            .Where(a => a.Key == TextKey.AlbumKey("Isles"))
            .Select(a => new { a.Title, Artists = a.Artists.Select(x => x.Artist.Name).ToList() })
            .ToListAsync());
        Assert.Equal("Isles", Assert.Single(isles).Title);
        Assert.Equal(["Bicep"], isles[0].Artists); // not "Bicep, Clara La San" because of a featured track
    }

    [Fact]
    public async Task DifferentSongs_StaySeparate()
    {
        var (user, _) = await _factory.CreateUserAsync("separate-user");
        await IngestAsync(user.Id,
            S("Intro", ["Artist One feat. Guest"]),
            S("Intro", ["Artist Two feat. Guest"]),
            S("Teardrop", ["Massive Attack"]),
            S("Teardrop (Live)", ["Massive Attack"]),
            S("Jóga", ["Björk"]),
            S("Joga", ["Bjork"]));

        Assert.Equal(2, (await TracksAsync("Intro")).Count);
        Assert.Single(await TracksAsync("Teardrop"));
        Assert.Single(await TracksAsync("Teardrop (Live)"));
        var joga = Assert.Single(await TracksAsync("Joga"));
        Assert.Equal(("Jóga", 2), (joga.Title, joga.Scrobbles)); // the first spelling seen is kept
        Assert.Equal(["Björk"], joga.Artists);
    }

    [Fact]
    public async Task Upgrade_MergesAnOldCatalog()
    {
        using var factory = new MuseFmFactory(null);
        var (user, _) = await factory.CreateUserAsync("upgrader");
        await factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            Artist A(string name) => new() { Name = name, Key = TextKey.Key(name) }; // version 1 keys
            Track T(string title, params Artist[] artists)
            {
                var track = new Track { Title = title, Key = TextKey.Key(title) + string.Join("", artists.Select(a => a.Key)) };
                for (var i = 0; i < artists.Length; i++) track.Artists.Add(new TrackArtist { Artist = artists[i], Position = i });
                return track;
            }

            Album L(string title, params Artist[] artists)
            {
                var album = new Album { Title = title, Key = TextKey.Key(title) + string.Join("", artists.Select(a => a.Key)) };
                for (var i = 0; i < artists.Length; i++) album.Artists.Add(new AlbumArtist { Artist = artists[i], Position = i });
                return album;
            }

            var (bjork, bjorkPlain, bicep, clara) = (A("Björk"), A("Bjork"), A("Bicep"), A("Clara La San"));
            foreach (var artist in new[] { bjork, bjorkPlain, bicep, clara })
            {
                db.Artists.Add(artist);
                await db.SaveChangesAsync(); // ids in this order: the oldest row survives a merge
            }

            var tracks = new[] { T("Jóga", bjork), T("Joga", bjorkPlain), T("Saku", bicep), T("Saku", bicep, clara), T("Atlas (with Clara La San)", bicep) };
            var albums = new[] { L("Homogenic", bjork), L("Homogenic", bjorkPlain), L("Isles", bicep, clara), L("Isles", bicep) };
            foreach (var entity in tracks.Cast<object>().Concat(albums))
            {
                db.Add(entity);
                await db.SaveChangesAsync();
            }

            var plays = new (Track Track, Album Album)[]
            {
                (tracks[0], albums[0]), (tracks[1], albums[1]), (tracks[2], albums[3]), (tracks[3], albums[2]), (tracks[4], albums[3]),
            };
            for (var i = 0; i < plays.Length; i++)
                db.Scrobbles.Add(new Scrobble { UserId = user.Id, Track = plays[i].Track, Album = plays[i].Album, ListenedAt = 1_600_000_000 + i });
            (await db.AppState.SingleAsync(s => s.Name == "CatalogVersion")).Value = "1";
            await db.SaveChangesAsync();

            await CatalogMaintenance.UpgradeAsync(db, sp.GetRequiredService<DataPaths>(), NullLogger.Instance);
            return 0;
        });

        var catalog = await factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            return new
            {
                Artists = await db.Artists.OrderBy(a => a.Id).Select(a => new { a.Name, a.Key }).ToListAsync(),
                Tracks = await db.Tracks.OrderBy(t => t.Id).Select(t => new
                {
                    t.Title,
                    t.Key,
                    Artists = string.Join(", ", t.Artists.OrderBy(a => a.Position).Select(a => a.Artist.Name)),
                    Scrobbles = db.Scrobbles.Count(s => s.TrackId == t.Id),
                }).ToListAsync(),
                Albums = await db.Albums.OrderBy(a => a.Id).Select(a => new
                {
                    a.Title,
                    Artists = string.Join(", ", a.Artists.OrderBy(x => x.Position).Select(x => x.Artist.Name)),
                    Scrobbles = db.Scrobbles.Count(s => s.AlbumId == a.Id),
                }).ToListAsync(),
                Scrobbles = await db.Scrobbles.CountAsync(),
                Version = (await db.AppState.SingleAsync(s => s.Name == "CatalogVersion")).Value,
            };
        });

        Assert.Equal(["Björk", "Bicep", "Clara La San"], catalog.Artists.Select(a => a.Name));
        Assert.All(catalog.Artists, a => Assert.Equal(TextKey.ArtistKey(a.Name), a.Key));
        Assert.Equal(
            [("Jóga", "Björk", 2), ("Saku", "Bicep, Clara La San", 2), ("Atlas", "Bicep, Clara La San", 1)],
            catalog.Tracks.Select(t => (t.Title, t.Artists, t.Scrobbles)));
        Assert.Equal(TextKey.TitleKey("Saku"), catalog.Tracks[1].Key);
        Assert.Equal([("Homogenic", "Björk", 2), ("Isles", "Bicep", 3)], catalog.Albums.Select(a => (a.Title, a.Artists, a.Scrobbles)));
        Assert.Equal((5, "2"), (catalog.Scrobbles, catalog.Version));

        // New scrobbles land on the merged rows.
        await factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>().IngestAsync(user.Id,
            new ScrobbleInput { Title = "Saku", Artists = ["Bicep"], Album = "Isles", ListenedAt = 1_600_000_100 }));
        var counts = await factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            return (await db.Tracks.CountAsync(), await db.Albums.CountAsync(), await db.Artists.CountAsync());
        });
        Assert.Equal((3, 2, 3), counts);
    }
}
