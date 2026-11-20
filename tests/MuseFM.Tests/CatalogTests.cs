using System.Net;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Tests;

/// <summary>Renaming and merging catalog entries, and scrobbles that arrive under the old names afterwards.</summary>
public class CatalogTests : IClassFixture<MuseFmFactory>
{
    private readonly MuseFmFactory _factory;

    public CatalogTests(MuseFmFactory factory) => _factory = factory;

    private static long Ago(int minutes) => DateTimeOffset.UtcNow.AddMinutes(-minutes).ToUnixTimeSeconds();

    private static ScrobbleInput S(string artist, string title, int minutesAgo, string? album = null) =>
        new() { Title = title, Artists = [artist], Album = album, ListenedAt = Ago(minutesAgo) };

    private Task IngestAsync(string userId, params ScrobbleInput[] scrobbles) =>
        _factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>().IngestAsync(userId, scrobbles));

    private Task<T> Db<T>(Func<AppDbContext, Task<T>> query) => _factory.WithScopeAsync(sp => query(sp.GetRequiredService<AppDbContext>()));

    private Task<CatalogEdit> Edit(Func<CatalogEditor, Task<CatalogEdit>> edit) => _factory.WithScopeAsync(sp => edit(sp.GetRequiredService<CatalogEditor>()));

    private Task<int> ArtistId(string name) => Db(db => db.Artists.Where(a => a.Name == name).Select(a => a.Id).SingleAsync());

    [Fact]
    public async Task RenamedArtist_KeepsReceivingScrobblesUnderTheOldName()
    {
        var (user, _) = await _factory.CreateUserAsync("rename-user");
        await IngestAsync(user.Id, S("Sufjan Stevns", "Chicago", 30, "Illinois"));
        var id = await ArtistId("Sufjan Stevns");

        var edit = await Edit(e => e.RenameAsync(ChartKind.Artists, id, "Sufjan Stevens"));
        Assert.True(edit.Ok, edit.Message);
        await IngestAsync(user.Id, S("Sufjan Stevns", "Chicago", 20, "Illinois"), S("Sufjan Stevens", "Casimir Pulaski Day", 10, "Illinois"));

        var (names, chicago, albums) = await Db(async db => (
            await db.Artists.Where(a => a.Name.StartsWith("Sufjan")).Select(a => a.Name).ToListAsync(),
            await db.Scrobbles.CountAsync(s => s.UserId == user.Id && s.Track.Title == "Chicago"),
            await db.Albums.CountAsync(a => a.Title == "Illinois")));
        Assert.Equal(["Sufjan Stevens"], names);
        Assert.Equal(2, chicago);
        Assert.Equal(1, albums);
        Assert.Equal(["Sufjan Stevns"], (await _factory.WithScopeAsync(sp => sp.GetRequiredService<CatalogEditor>().AliasesAsync(ChartKind.Artists, id))).Select(a => a.Name));
    }

    [Fact]
    public async Task RenamingOntoAnExistingArtist_SuggestsAMergeInstead()
    {
        var (user, _) = await _factory.CreateUserAsync("clash-user");
        await IngestAsync(user.Id, S("Clash One", "A", 30), S("Clash Two", "B", 20));
        var (one, two) = (await ArtistId("Clash One"), await ArtistId("Clash Two"));

        var edit = await Edit(e => e.RenameAsync(ChartKind.Artists, one, "clash two"));
        Assert.False(edit.Ok);
        Assert.Equal(two, edit.Id);
        Assert.Equal("Clash One", await Db(db => db.Artists.Where(a => a.Id == one).Select(a => a.Name).SingleAsync()));
        Assert.False((await Edit(e => e.RenameAsync(ChartKind.Artists, one, "  "))).Ok);
    }

    [Fact]
    public async Task MergingArtists_MergesTheirDuplicateTracksAndAlbums()
    {
        var (user, _) = await _factory.CreateUserAsync("merge-artists");
        await IngestAsync(user.Id,
            S("Ye", "Stronger", 40, "Graduation"), S("Kanye West", "Stronger", 30, "Graduation"),
            S("Kanye West", "Homecoming", 20, "Graduation"), S("Ye", "Vultures", 15));
        await _factory.WithScopeAsync(async sp =>
        {
            var feedback = sp.GetRequiredService<FeedbackService>();
            var db = sp.GetRequiredService<AppDbContext>();
            foreach (var id in await db.Tracks.Where(t => t.Title == "Stronger").Select(t => t.Id).ToListAsync())
                await feedback.SetAsync(user.Id, id, TrackFeedback.Love);
            return 0;
        });
        var (ye, kanye) = (await ArtistId("Ye"), await ArtistId("Kanye West"));

        var edit = await Edit(e => e.MergeAsync(ChartKind.Artists, ye, kanye));
        Assert.True(edit.Ok, edit.Message);
        Assert.Contains("1 track and 1 album that were duplicates", edit.Message);

        // A scrobble under the old name lands on the merged artist, track and album.
        await IngestAsync(user.Id, S("Ye", "Stronger", 5, "Graduation"));
        var result = await Db(async db => new
        {
            Artists = await db.Artists.CountAsync(a => a.Name == "Ye" || a.Name == "Kanye West"),
            Stronger = await db.Tracks.Where(t => t.Title == "Stronger").Select(t => new { t.Id, Scrobbles = db.Scrobbles.Count(s => s.TrackId == t.Id) }).ToListAsync(),
            Albums = await db.Albums.CountAsync(a => a.Title == "Graduation"),
            Vultures = await db.Tracks.Where(t => t.Title == "Vultures").SelectMany(t => t.Artists).Select(a => a.Artist.Name).SingleAsync(),
            Loves = await db.TrackFeedback.CountAsync(f => f.UserId == user.Id),
        });
        Assert.Equal(1, result.Artists);
        Assert.Equal(3, Assert.Single(result.Stronger).Scrobbles);
        Assert.Equal(1, result.Albums);
        Assert.Equal("Kanye West", result.Vultures);
        Assert.Equal(1, result.Loves);
    }

    [Fact]
    public async Task MergingAMisattributedTrack_RemovesTheWrongArtistForGood()
    {
        var (user, _) = await _factory.CreateUserAsync("merge-tracks");
        await IngestAsync(user.Id, S("Wrong Artist", "Teardrop", 30), S("Massive Attack", "Teardrop", 20), S("Massive Attack", "Angel", 10));
        var (wrong, right) = await Db(async db => (
            await db.Tracks.Where(t => t.Title == "Teardrop" && t.Artists.Any(a => a.Artist.Name == "Wrong Artist")).Select(t => t.Id).SingleAsync(),
            await db.Tracks.Where(t => t.Title == "Teardrop" && t.Artists.Any(a => a.Artist.Name == "Massive Attack")).Select(t => t.Id).SingleAsync()));
        await _factory.WithScopeAsync(async sp =>
        {
            await sp.GetRequiredService<FeedbackService>().SetAsync(user.Id, wrong, TrackFeedback.Love);
            return 0;
        });

        var edit = await Edit(e => e.MergeAsync(ChartKind.Tracks, wrong, right));
        Assert.True(edit.Ok, edit.Message);
        Assert.Equal(right, edit.Id);

        await IngestAsync(user.Id, S("Wrong Artist", "Teardrop", 5));
        var result = await Db(async db => new
        {
            WrongArtist = await db.Artists.AnyAsync(a => a.Name == "Wrong Artist"),
            Scrobbles = await db.Scrobbles.CountAsync(s => s.TrackId == right),
            Credits = await db.TrackArtists.Where(ta => ta.TrackId == right).Select(ta => ta.Artist.Name).ToListAsync(),
            Loved = await db.TrackFeedback.Where(f => f.UserId == user.Id).Select(f => f.TrackId).SingleAsync(),
        });
        Assert.False(result.WrongArtist);
        Assert.Equal(3, result.Scrobbles);
        Assert.Equal(["Massive Attack"], result.Credits);
        Assert.Equal(right, result.Loved);
        Assert.False((await Edit(e => e.MergeAsync(ChartKind.Tracks, right, right))).Ok);
    }

    [Fact]
    public async Task MergingAlbums_RedirectsTheOldTitle()
    {
        var (user, _) = await _factory.CreateUserAsync("merge-albums");
        await IngestAsync(user.Id, S("Burial", "Archangel", 30, "Untrue"), S("Burial", "Near Dark", 20, "Untrue (Hyperdub)"));
        var (source, target) = await Db(async db => (
            await db.Albums.Where(a => a.Title == "Untrue (Hyperdub)").Select(a => a.Id).SingleAsync(),
            await db.Albums.Where(a => a.Title == "Untrue").Select(a => a.Id).SingleAsync()));

        Assert.True((await Edit(e => e.MergeAsync(ChartKind.Albums, source, target))).Ok);
        await IngestAsync(user.Id, S("Burial", "Ghost Hardware", 10, "Untrue (Hyperdub)"));
        Assert.Equal(3, await Db(db => db.Scrobbles.CountAsync(s => s.UserId == user.Id && s.AlbumId == target)));
        Assert.False(await Db(db => db.Albums.AnyAsync(a => a.Title == "Untrue (Hyperdub)")));
    }

    [Fact]
    public async Task MusicBrainzIds_CanBeSetAndCleared()
    {
        var (user, _) = await _factory.CreateUserAsync("mbid-admin");
        await IngestAsync(user.Id, S("Aphex Twin", "Xtal", 10));
        var id = await ArtistId("Aphex Twin");

        Assert.False((await Edit(e => e.SetMusicBrainzIdAsync(ChartKind.Artists, id, "not-a-uuid"))).Ok);
        Assert.True((await Edit(e => e.SetMusicBrainzIdAsync(ChartKind.Artists, id, "F22942A1-6F70-4F48-866E-238CB2308FBD"))).Ok);
        Assert.Equal("f22942a1-6f70-4f48-866e-238cb2308fbd", await Db(db => db.Artists.Where(a => a.Id == id).Select(a => a.MusicBrainzId).SingleAsync()));
        Assert.True((await Edit(e => e.SetMusicBrainzIdAsync(ChartKind.Artists, id, ""))).Ok);
        Assert.Null(await Db(db => db.Artists.Where(a => a.Id == id).Select(a => a.MusicBrainzId).SingleAsync()));
    }

    [Fact]
    public async Task AdminPages_SearchRenameAndMerge()
    {
        await _factory.CreateUserAsync("catalog-admin", admin: true);
        var (listener, _) = await _factory.CreateUserAsync("catalog-listener");
        await IngestAsync(listener.Id, S("Boards of Canda", "Roygbiv", 30), S("Boards of Canada", "Dayvan Cowboy", 20));
        var (typo, real) = (await ArtistId("Boards of Canda"), await ArtistId("Boards of Canada"));

        var admin = await _factory.SignInAsync("catalog-admin");
        var search = await admin.GetStringAsync("/admin/catalog?kind=artists&q=boards");
        Assert.Contains($"/admin/catalog/artist/{typo}", search);
        Assert.Contains($"/admin/catalog/artist/{real}", search);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/admin/catalog/track/{await Db(db => db.Tracks.Where(t => t.Title == "Roygbiv").Select(t => t.Id).SingleAsync())}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/admin/catalog/artist/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/admin/catalog/song/{typo}")).StatusCode);

        var page = $"/admin/catalog/artist/{typo}";
        var merge = await MuseFmFactory.PostFormAsync(admin, page, $"{page}?handler=Merge", new() { ["targetId"] = real.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, merge.StatusCode);
        Assert.Equal($"/admin/catalog/artist/{real}", merge.Headers.Location!.OriginalString);
        var merged = WebUtility.HtmlDecode(await admin.GetStringAsync(merge.Headers.Location.OriginalString));
        Assert.Contains("Merged “Boards of Canda” into “Boards of Canada”.", merged);
        Assert.Contains("Boards of Canda", merged); // listed as an alias

        var target = $"/admin/catalog/artist/{real}";
        var rename = await MuseFmFactory.PostFormAsync(admin, target, $"{target}?handler=Rename", new() { ["name"] = "Boards Of Canada" });
        Assert.Equal(HttpStatusCode.Redirect, rename.StatusCode);
        Assert.Equal("Boards Of Canada", await Db(db => db.Artists.Where(a => a.Id == real).Select(a => a.Name).SingleAsync()));

        // Only admins get in.
        var visitor = await _factory.SignInAsync("catalog-listener");
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync("/admin/catalog")).StatusCode);
        Assert.DoesNotContain("/admin/catalog/", await visitor.GetStringAsync($"/u/catalog-listener/artist/{real}"));
        Assert.Contains($"href=\"/admin/catalog/artist/{real}\"", await admin.GetStringAsync($"/u/catalog-listener/artist/{real}"));
    }
}
