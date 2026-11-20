using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;
using MuseFM.Services.Metadata;

namespace MuseFM.Tests;

public class MetadataTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1, 4, 5, 6];

    private const string NavidromeUrl = "http://navidrome.test/nd";
    private const string NavidromePassword = "nd pass 1";
    private const string ArtistMbid = "10adbe5e-a2c0-4bf3-8249-2b4cbf6e6ca8";
    private const string ReleaseMbid = "a3f1b5c3-6a0e-4b8e-9e5c-2d2f8c0b1d11";
    private const string CaaReleaseMbid = "b4f2c6d4-7b1f-4c9f-8f6d-3e3f9d1c2e22";

    /// <summary>Answers outgoing HTTP requests of the app; unknown URLs get 404.</summary>
    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage?> route) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Uri> _requests = new();

        /// <summary>Added to every answer, to simulate a slow source.</summary>
        public TimeSpan Delay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _requests.Enqueue(request.RequestUri!);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return route(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        /// <summary>Requests made so far to <paramref name="host"/>, oldest first.</summary>
        public IReadOnlyList<Uri> To(string host) => _requests.Where(u => u.Host == host).ToList();
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Image(byte[] bytes, string type, bool placeholder = false)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        if (placeholder) response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        else response.Headers.ETag = new EntityTagHeaderValue("\"art\"");
        return response;
    }

    private static string Subsonic(string body) =>
        """{"subsonic-response":{"status":"ok","version":"1.16.1","type":"navidrome","serverVersion":"0.58.0" """ + body + "}}";

    /// <summary>A Navidrome server knowing Massive Attack and Mezzanine. <paramref name="placeholderCovers"/> serves its generic artwork instead.</summary>
    private static HttpResponseMessage? Navidrome(HttpRequestMessage request, bool placeholderCovers = false)
    {
        var uri = request.RequestUri!;
        if (uri.Host != "navidrome.test") return null;
        var query = HttpUtility.ParseQueryString(uri.Query);
        return uri.AbsolutePath switch
        {
            "/nd/rest/ping" => Json(Subsonic("")),
            "/nd/rest/search3" when query["artistCount"] != "0" => Json(Subsonic("""
                ,"searchResult3":{"artist":[{"id":"ar0","name":"Massive Attack Tribute Band"},{"id":"ar1","name":"Massive Attack","coverArt":"ar-ar1"}]}
                """)),
            "/nd/rest/search3" => Json(Subsonic("""
                ,"searchResult3":{"album":[{"id":"al0","name":"Mezzanine","artist":"Someone Else","coverArt":"al-al0"},
                                           {"id":"al1","name":"Mezzanine (Deluxe)","artist":"Massive Attack","coverArt":"al-al1","year":2019,
                                            "genre":"Trip-Hop","genres":[{"name":"Trip-Hop"},{"name":" Electronic "},{"name":"trip hop"}],
                                            "originalReleaseDate":{"year":1998,"month":4,"day":20},"releaseDate":{}}]}
                """)),
            "/nd/rest/getAlbum" when query["id"] == "al1" => Json(Subsonic("""
                ,"album":{"id":"al1","name":"Mezzanine (Deluxe)","song":[{"id":"s1","title":"Angel","duration":379},
                  {"id":"s3","title":"Teardrop (Remastered)","duration":330},{"id":"s9","title":"Untimed"}]}
                """)),
            "/nd/rest/getArtistInfo2" when query["id"] == "ar1" => Json(Subsonic($$"""
                ,"artistInfo2":{"biography":"Massive Attack are a trip hop group.\nFormed in Bristol. <a href=\"https://www.last.fm/music/Massive+Attack\">Read more on Last.fm</a>",
                  "lastFmUrl":"https://www.last.fm/music/Massive+Attack","musicBrainzId":"{{ArtistMbid}}","largeImageUrl":"http://navidrome.test/share/img/x"}
                """)),
            "/nd/rest/getCoverArt" when query["id"] is "ar-ar1" or "al-al1" => Image(Png, "image/png", placeholderCovers),
            _ => null,
        };
    }

    /// <summary>ListenBrainz (token "lb-token"), Cover Art Archive, Wikidata, Wikipedia and Commons, knowing Massive Attack's Mezzanine.</summary>
    private static HttpResponseMessage? MetaBrainz(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        var query = HttpUtility.ParseQueryString(uri.Query);
        return uri.Host switch
        {
            "api.listenbrainz.org" when request.Headers.Authorization?.ToString() != "Token lb-token" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            "api.listenbrainz.org" => uri.AbsolutePath switch
            {
                "/1/validate-token" => Json("""{"valid":true,"user_name":"tester"}"""),
                "/1/metadata/lookup/" when query["recording_name"] is not ("Teardrop" or "Angel") => Json("{}"),
                "/1/metadata/lookup/" when query["release_name"] == "Mezzanine" => Json($$"""
                    {"artist_credit_name":"Massive Attack","artist_mbids":["{{ArtistMbid}}"],"recording_name":"{{query["recording_name"]}}",
                     "release_mbid":"{{ReleaseMbid}}","release_name":"Mezzanine",
                     "metadata":{"release":{"caa_id":829521842,"caa_release_mbid":"{{CaaReleaseMbid}}","name":"Mezzanine","year":1998},
                       "tag":{"release_group":[{"tag":"downtempo","count":3,"genre_mbid":"cc38aba3-48ac-4a22-9a48-ec5a8a2b1f2d"},{"tag":"1998","count":9},
                         {"tag":"trip hop","count":12,"genre_mbid":"45eb1d9c-588c-4dc8-9394-a14b7c4b02bc"}]} } }
                    """),
                "/1/metadata/lookup/" => Json($$"""{"artist_credit_name":"Massive Attack","artist_mbids":["{{ArtistMbid}}"],"release_name":"Teardrop"}"""),
                "/1/metadata/artist/" when query["artist_mbids"] == ArtistMbid => Json($$"""
                    [{"artist_mbid":"{{ArtistMbid}}","name":"Massive Attack","rels":{"wikidata":"https://www.wikidata.org/wiki/Q357418"},
                      "tag":{"artist":[{"tag":"british","count":40},{"tag":"electronic","count":9,"genre_mbid":"89255676-1f14-4dd8-bbad-fca839d6aff4"},
                        {"tag":"trip hop","count":20,"genre_mbid":"45eb1d9c-588c-4dc8-9394-a14b7c4b02bc"}]} }]
                    """),
                _ => null,
            },
            "coverartarchive.org" when uri.AbsolutePath == $"/release/{CaaReleaseMbid}/829521842-500.jpg" => Image(Jpeg, "image/jpeg"),
            "www.wikidata.org" when query["ids"] == "Q357418" => Json("""
                {"entities":{"Q357418":{"id":"Q357418","sitelinks":{"enwiki":{"site":"enwiki","title":"Massive Attack"}},
                  "claims":{"P18":[{"mainsnak":{"datavalue":{"value":"Massive Attack composite.jpg","type":"string"}}}]}}}}
                """),
            "en.wikipedia.org" when query["titles"] == "Massive Attack" => Json("""
                {"query":{"pages":[{"title":"Massive Attack","fullurl":"https://en.wikipedia.org/wiki/Massive_Attack",
                  "extract":"Massive Attack are an English trip hop collective.\nThey released their debut album in 1991."}]}}
                """),
            "commons.wikimedia.org" when uri.AbsolutePath == "/wiki/Special:FilePath/Massive_Attack_composite.jpg" => Image(Jpeg, "image/jpeg"),
            _ => null,
        };
    }

    private static Task<string> EnableListenBrainzAsync(HttpClient admin) =>
        SaveConnectorAsync(admin, ConnectorKind.ListenBrainz, new() { ["Enabled"] = "true", ["Secret"] = " lb-token " });

    private static async Task<HttpClient> SignInAdminAsync(MuseFmFactory factory)
    {
        // The first account becomes the administrator.
        var client = factory.CreatePlainClient();
        var token = await MuseFmFactory.GetAntiforgeryTokenAsync(client, "/register");
        var response = await client.PostAsync("/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Username"] = "admin",
            ["Input.Password"] = "password123",
            ["Input.ConfirmPassword"] = "password123",
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    /// <summary>Posts a form of the admin page and returns the status message it shows afterwards.</summary>
    private static async Task<string> PostAdminAsync(HttpClient admin, string query, Dictionary<string, string>? fields = null)
    {
        fields ??= [];
        fields["__RequestVerificationToken"] = await MuseFmFactory.GetAntiforgeryTokenAsync(admin, "/admin/metadata");
        var response = await admin.PostAsync($"/admin/metadata?{query}", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var html = await admin.GetStringAsync("/admin/metadata");
        var message = System.Text.RegularExpressions.Regex.Match(html, "<div class=\"alert (?:success|error)\">([^<]*)</div>");
        Assert.True(message.Success, $"No status message after {query}");
        return WebUtility.HtmlDecode(message.Groups[1].Value);
    }

    private static Task<string> SaveConnectorAsync(HttpClient admin, ConnectorKind kind, Dictionary<string, string> fields) =>
        PostAdminAsync(admin, $"handler=Save&kind={kind}", fields);

    private static async Task<FetchProgress> WaitForFetchAsync(MuseFmFactory factory)
    {
        var job = factory.Services.GetRequiredService<MetadataFetchJob>();
        for (var i = 0; i < 300; i++)
        {
            if (job.Progress is { IsRunning: false } done) return done;
            await Task.Delay(50);
        }

        throw new TimeoutException("The metadata fetch did not finish.");
    }

    private static Task<string> EnableNavidromeAsync(HttpClient admin) => SaveConnectorAsync(admin, ConnectorKind.Navidrome, new()
    {
        ["Enabled"] = "true", ["BaseUrl"] = NavidromeUrl + "/", ["Username"] = "nd-user", ["Secret"] = NavidromePassword,
    });

    private static async Task<(int ArtistId, int AlbumId)> ScrobbleMezzanineAsync(MuseFmFactory factory)
    {
        return await factory.WithScopeAsync(async sp =>
        {
            var user = await sp.GetRequiredService<UserManager<AppUser>>().FindByNameAsync("admin");
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await sp.GetRequiredService<ScrobbleService>().IngestAsync(user!.Id,
            [
                new ScrobbleInput { Title = "Teardrop", Artists = ["Massive Attack"], Album = "Mezzanine", ListenedAt = now - 600 },
                new ScrobbleInput { Title = "Angel", Artists = ["Massive Attack"], Album = "Mezzanine", ListenedAt = now - 300 },
            ]);
            var db = sp.GetRequiredService<AppDbContext>();
            return (
                (await db.Artists.SingleAsync(a => a.Name == "Massive Attack")).Id,
                (await db.Albums.SingleAsync(a => a.Title == "Mezzanine")).Id);
        });
    }

    [Fact]
    public async Task Navidrome_SuppliesCoversArtistPhotoAndBiography()
    {
        var http = new FakeHttp(r => Navidrome(r));
        using var factory = new MuseFmFactory(null, s => s.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => http)));
        var admin = await SignInAdminAsync(factory);
        var (artistId, albumId) = await ScrobbleMezzanineAsync(factory);

        // Without an active connector pages render no artwork at all.
        Assert.DoesNotContain("/media/", await admin.GetStringAsync("/u/admin"));

        Assert.Equal("Navidrome saved and enabled. Connected to Navidrome 0.58.0.", await EnableNavidromeAsync(admin));
        var settingsPage = await admin.GetStringAsync("/admin/metadata");
        Assert.DoesNotContain(NavidromePassword, settingsPage);
        Assert.Contains("value=\"http://navidrome.test/nd\"", settingsPage);

        var profile = await admin.GetStringAsync("/u/admin");
        Assert.Contains($"/media/album/{albumId}", profile);
        Assert.Contains($"/media/artist/{artistId}", profile);

        var cover = await admin.GetAsync($"/media/album/{albumId}");
        Assert.Equal("image/png", cover.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await cover.Content.ReadAsByteArrayAsync());
        Assert.NotNull(cover.Headers.ETag);

        // A revalidation with the ETag is answered with 304, and the cover is not fetched again.
        var revalidate = new HttpRequestMessage(HttpMethod.Get, $"/media/album/{albumId}");
        revalidate.Headers.IfNoneMatch.Add(cover.Headers.ETag);
        Assert.Equal(HttpStatusCode.NotModified, (await admin.SendAsync(revalidate)).StatusCode);
        Assert.Single(http.To("navidrome.test"), u => u.AbsolutePath == "/nd/rest/getCoverArt" && u.Query.Contains("al-al1"));

        var artistPage = await admin.GetStringAsync($"/u/admin/artist/{artistId}");
        Assert.Contains("Massive Attack are a trip hop group.", artistPage);
        Assert.Contains("Formed in Bristol.", artistPage);
        Assert.DoesNotContain("Read more on Last.fm", artistPage);
        Assert.Contains("href=\"https://www.last.fm/music/Massive+Attack\"", WebUtility.HtmlDecode(artistPage)); // Razor writes '+' as &#x2B;
        Assert.Contains($"https://musicbrainz.org/artist/{ArtistMbid}", artistPage);
        Assert.Equal(Png, await admin.GetByteArrayAsync($"/media/artist/{artistId}"));

        // The album's tags in the library: the year of the original release, its genres, and the lengths of its tracks.
        var (album, lengths) = await factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            return (await db.AlbumMetadata.SingleAsync(m => m.AlbumId == albumId), await db.Tracks.ToDictionaryAsync(t => t.Title, t => t.LengthSeconds));
        });
        Assert.Equal((1998, "Trip-Hop\nElectronic", MetadataService.Revision), (album.ReleaseYear, album.Genres, album.Revision));
        Assert.Equal((330, 379), (lengths["Teardrop"], lengths["Angel"]));

        // Subsonic token auth: md5(password + salt), never the password itself.
        foreach (var uri in http.To("navidrome.test"))
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            Assert.Equal("nd-user", query["u"]);
            Assert.Null(query["p"]);
            Assert.Equal(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(NavidromePassword + query["s"]))), query["t"]);
        }

        // Clearing the cache removes rows and files.
        var token = await MuseFmFactory.GetAntiforgeryTokenAsync(admin, "/admin/metadata");
        await admin.PostAsync("/admin/metadata?handler=Clear", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Empty(Directory.GetFiles(Path.Combine(factory.DataDirectory, "images")));
        Assert.Equal(0, await factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().AlbumMetadata.CountAsync()));
    }

    [Fact]
    public async Task NavidromePlaceholders_FallBackToListenBrainz()
    {
        var http = new FakeHttp(r => Navidrome(r, placeholderCovers: true) ?? MetaBrainz(r));
        using var factory = new MuseFmFactory(null, s => s.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => http)));
        var admin = await SignInAdminAsync(factory);
        var (artistId, albumId) = await ScrobbleMezzanineAsync(factory);

        await EnableNavidromeAsync(admin);
        Assert.Equal("ListenBrainz saved and enabled. The token belongs to ListenBrainz user tester.", await EnableListenBrainzAsync(admin));

        // Album: Navidrome only has its generic artwork, so the Cover Art Archive cover is used.
        Assert.Equal(Jpeg, await admin.GetByteArrayAsync($"/media/album/{albumId}"));
        var album = await factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().AlbumMetadata.SingleAsync(m => m.AlbumId == albumId));
        Assert.Equal("Cover Art Archive", album.ImageSource);
        Assert.Equal(ReleaseMbid, album.MusicBrainzId);
        // Year and genres are Navidrome's, which was asked first.
        Assert.Equal((1998, "Trip-Hop\nElectronic"), (album.ReleaseYear, album.Genres));
        Assert.Contains(http.To("navidrome.test"), u => u.AbsolutePath == "/nd/rest/getCoverArt" && u.Query.Contains("al-al1"));

        // Artist: biography from Navidrome (asked first), photo from Commons. Navidrome's MBID saves the ListenBrainz lookup.
        Assert.Equal(Jpeg, await admin.GetByteArrayAsync($"/media/artist/{artistId}"));
        Assert.Contains("Massive Attack are a trip hop group.", await admin.GetStringAsync($"/u/admin/artist/{artistId}"));
        var artist = await factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().ArtistMetadata.SingleAsync(m => m.ArtistId == artistId));
        Assert.Equal(("Navidrome", "Wikimedia Commons"), (artist.BioSource, artist.ImageSource));
        // Navidrome has no genres for artists: MusicBrainz' genres, the most used first, without tags that are not genres.
        Assert.Equal("Trip Hop\nElectronic", artist.Genres);
        Assert.Empty(http.To("en.wikipedia.org"));
        Assert.All(http.To("api.listenbrainz.org").Where(u => u.AbsolutePath == "/1/metadata/lookup/"), u => Assert.Contains("release_name=", u.Query));
    }

    [Fact]
    public async Task ListenBrainz_IdentifiesArtistsThroughTheirTracks()
    {
        var http = new FakeHttp(MetaBrainz);
        using var factory = new MuseFmFactory(null, s => s.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => http)));
        var admin = await SignInAdminAsync(factory);
        var (artistId, albumId) = await ScrobbleMezzanineAsync(factory);
        await EnableListenBrainzAsync(admin);

        // ListenBrainz is paced at one request per second, so let the lookup finish before checking the page.
        Assert.Equal(Jpeg, await admin.GetByteArrayAsync($"/media/artist/{artistId}"));
        var page = await admin.GetStringAsync($"/u/admin/artist/{artistId}");
        Assert.Contains("<p>Massive Attack are an English trip hop collective.</p>", page);
        Assert.Contains("<p>They released their debut album in 1991.</p>", page); // behind "Read more"
        Assert.Contains("href=\"https://en.wikipedia.org/wiki/Massive_Attack\"", page);
        var artist = await factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().ArtistMetadata.SingleAsync(m => m.ArtistId == artistId));
        Assert.Equal((ArtistMbid, "Trip Hop\nElectronic"), (artist.MusicBrainzId, artist.Genres));

        // The album lookup also brings the release year and the release group's genres.
        Assert.Equal(Jpeg, await admin.GetByteArrayAsync($"/media/album/{albumId}"));
        var album = await factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().AlbumMetadata.SingleAsync(m => m.AlbumId == albumId));
        Assert.Equal((1998, "Trip Hop\nDowntempo", ReleaseMbid), (album.ReleaseYear, album.Genres, album.MusicBrainzId));
    }

    [Fact]
    public async Task FetchMissing_LooksUpTheWholeCatalogInTheBackground()
    {
        var http = new FakeHttp(r => Navidrome(r));
        using var factory = new MuseFmFactory(null, s => s.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => http)));
        var admin = await SignInAdminAsync(factory);
        await ScrobbleMezzanineAsync(factory);
        await EnableNavidromeAsync(admin);
        var metadata = factory.Services.GetRequiredService<MetadataService>();

        // Nothing has been shown yet, so nothing has been looked up.
        Assert.Equal(new MetadataCacheStats(1, 0, 0, 0, 1, 0, 0, 0, Tracks: 2), await metadata.StatsAsync());

        Assert.StartsWith("Fetching metadata for every artist and album that is still missing some.", await PostAdminAsync(admin, "handler=Fetch&mode=Missing"));
        var run = await WaitForFetchAsync(factory);
        Assert.Equal((FetchMode.Missing, 2, 2, false), (run.Mode, run.Total, run.Done, run.Stopped));
        var stats = await metadata.StatsAsync();
        Assert.Equal((1, 1, 1, 1, 1, 1), (stats.ArtistsLookedUp, stats.ArtistsWithImage, stats.ArtistsWithBio, stats.AlbumsLookedUp, stats.AlbumsWithImage, stats.Albums));
        Assert.Equal((0, 1, 1, 2, 2), (stats.ArtistsWithGenres, stats.AlbumsWithGenres, stats.AlbumsWithYear, stats.Tracks, stats.TracksWithLength));

        var card = await admin.GetStringAsync("/admin/metadata?handler=Cache");
        Assert.StartsWith("<section class=\"card\" id=\"metadata-cache\"", card.TrimStart());
        Assert.DoesNotContain("is-running", card);
        Assert.Contains("The last fetch looked up 2 artists and albums", card);

        // Everything is complete now, so "Fetch missing" has nothing to do...
        await PostAdminAsync(admin, "handler=Fetch&mode=Missing");
        Assert.Equal(0, (await WaitForFetchAsync(factory)).Total);
        Assert.Contains("had nothing to do", await admin.GetStringAsync("/admin/metadata?handler=Cache"));

        // ...while "Refetch all" asks the sources again.
        var coversBefore = http.To("navidrome.test").Count(u => u.AbsolutePath == "/nd/rest/getCoverArt");
        await PostAdminAsync(admin, "handler=Fetch&mode=All");
        Assert.Equal((2, 2), ((await WaitForFetchAsync(factory)).Total, (await WaitForFetchAsync(factory)).Done));
        Assert.Equal(coversBefore + 2, http.To("navidrome.test").Count(u => u.AbsolutePath == "/nd/rest/getCoverArt"));
    }

    [Fact]
    public async Task FetchMissing_CatchesUpRowsFromBeforeGenresYearsAndLengths()
    {
        var http = new FakeHttp(r => Navidrome(r));
        using var factory = new MuseFmFactory(null, s => s.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => http)));
        var admin = await SignInAdminAsync(factory);
        var (artistId, albumId) = await ScrobbleMezzanineAsync(factory);
        await EnableNavidromeAsync(admin);
        await PostAdminAsync(admin, "handler=Fetch&mode=Missing");
        await WaitForFetchAsync(factory);

        // The cache as an older version left it: pictures and biography, but no details. One track's length came from a client.
        await factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            await db.ArtistMetadata.ExecuteUpdateAsync(s => s.SetProperty(m => m.Revision, 0));
            await db.AlbumMetadata.ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Revision, 0).SetProperty(m => m.ReleaseYear, (int?)null).SetProperty(m => m.Genres, (string?)null));
            await db.Tracks.Where(t => t.Title == "Teardrop").ExecuteUpdateAsync(s => s.SetProperty(t => t.LengthSeconds, (int?)null));
            return await db.Tracks.Where(t => t.Title == "Angel").ExecuteUpdateAsync(s => s.SetProperty(t => t.LengthSeconds, 400));
        });
        int Requests(string path) => http.To("navidrome.test").Count(u => u.AbsolutePath == path);
        var (covers, artistInfos, albums) = (Requests("/nd/rest/getCoverArt"), Requests("/nd/rest/getArtistInfo2"), Requests("/nd/rest/getAlbum"));

        await PostAdminAsync(admin, "handler=Fetch&mode=Missing");
        var run = await WaitForFetchAsync(factory);
        Assert.Equal((2, 2), (run.Total, run.Done));

        var (artist, album, lengths) = await factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            return (await db.ArtistMetadata.SingleAsync(m => m.ArtistId == artistId), await db.AlbumMetadata.SingleAsync(m => m.AlbumId == albumId),
                await db.Tracks.ToDictionaryAsync(t => t.Title, t => t.LengthSeconds));
        });
        Assert.Equal((1998, "Trip-Hop\nElectronic", MetadataService.Revision), (album.ReleaseYear, album.Genres, album.Revision));
        Assert.Equal((330, 400), (lengths["Teardrop"], lengths["Angel"])); // a client's length stays
        Assert.Equal(MetadataService.Revision, artist.Revision);
        Assert.True(artist.ImageFile is not null && artist.Bio is not null && album.ImageFile is not null);
        // Only the album's details were asked for: the pictures and the biography were there already.
        Assert.Equal((covers, artistInfos, albums + 1), (Requests("/nd/rest/getCoverArt"), Requests("/nd/rest/getArtistInfo2"), Requests("/nd/rest/getAlbum")));
        Assert.Equal(Png, await admin.GetByteArrayAsync($"/media/album/{albumId}"));

        // Caught up: nothing is missing any more.
        await PostAdminAsync(admin, "handler=Fetch&mode=Missing");
        Assert.Equal(0, (await WaitForFetchAsync(factory)).Total);

        var card = await admin.GetStringAsync("/admin/metadata?handler=Cache");
        Assert.Contains("Year / length", card);
    }

    [Fact]
    public async Task NewScrobbles_AreLookedUpInTheBackground_ButImportsAreNot()
    {
        var http = new FakeHttp(r => Navidrome(r));
        using var factory = new MuseFmFactory(null, s => s.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => http)));
        var admin = await SignInAdminAsync(factory);
        await EnableNavidromeAsync(admin);
        var metadata = factory.Services.GetRequiredService<MetadataService>();

        async Task IngestAsync(ScrobbleInput input, bool fetchMetadata = true) => await factory.WithScopeAsync(async sp =>
        {
            var user = await sp.GetRequiredService<UserManager<AppUser>>().FindByNameAsync("admin");
            return await sp.GetRequiredService<ScrobbleService>().IngestAsync(user!.Id, [input], fetchMetadata: fetchMetadata);
        });

        async Task<MetadataCacheStats> WaitForLookupsAsync(int artists, int albums)
        {
            for (var i = 0; i < 300; i++)
            {
                var stats = await metadata.StatsAsync();
                if (stats.ArtistsLookedUp >= artists && stats.AlbumsLookedUp >= albums) return stats;
                await Task.Delay(50);
            }

            throw new TimeoutException("The scrobbled artists and albums were not looked up.");
        }

        int Requests(string path) => http.To("navidrome.test").Count(u => u.AbsolutePath == path);

        // What an import brings in waits for "Fetch missing" or for someone to look at it.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await IngestAsync(new ScrobbleInput { Title = "Creep", Artists = ["Radiohead"], Album = "Pablo Honey", ListenedAt = now - 900 }, fetchMetadata: false);

        // A scrobble is looked up without anyone opening a page.
        await ScrobbleMezzanineAsync(factory);
        var stats = await WaitForLookupsAsync(1, 1);
        Assert.Equal(new MetadataCacheStats(2, 1, 1, 1, 2, 1, 1, Png.Length * 2, AlbumsWithGenres: 1, AlbumsWithYear: 1, Tracks: 3, TracksWithLength: 2), stats);

        // Scrobbling them again asks nobody; the new artist queued after it is looked up once that was decided.
        await IngestAsync(new ScrobbleInput { Title = "Risingson", Artists = ["Massive Attack"], Album = "Mezzanine", ListenedAt = now - 200 });
        await IngestAsync(new ScrobbleInput { Title = "Glory Box", Artists = ["Portishead"], ListenedAt = now - 100 });
        Assert.Equal(2, (await WaitForLookupsAsync(2, 1)).ArtistsLookedUp);
        Assert.Equal((1, 2), (Requests("/nd/rest/getArtistInfo2"), Requests("/nd/rest/getCoverArt")));
    }

    [Fact]
    public async Task Tracks_ShowTheirAlbumCover_AndSlowLookupsArePending()
    {
        var http = new FakeHttp(r => Navidrome(r));
        using var factory = new MuseFmFactory(null, s => s.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => http)));
        var admin = await SignInAdminAsync(factory);
        await ScrobbleMezzanineAsync(factory);
        var (teardrop, demo) = await factory.WithScopeAsync(async sp =>
        {
            var user = await sp.GetRequiredService<UserManager<AppUser>>().FindByNameAsync("admin");
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // Scrobbled without an album this time, and a track that never had one.
            await sp.GetRequiredService<ScrobbleService>().IngestAsync(user!.Id,
            [
                new ScrobbleInput { Title = "Teardrop", Artists = ["Massive Attack"], ListenedAt = now - 120 },
                new ScrobbleInput { Title = "Demo", Artists = ["Massive Attack"], ListenedAt = now - 60 },
            ]);
            var db = sp.GetRequiredService<AppDbContext>();
            return ((await db.Tracks.SingleAsync(t => t.Title == "Teardrop")).Id, (await db.Tracks.SingleAsync(t => t.Title == "Demo")).Id);
        });
        await EnableNavidromeAsync(admin);

        var profile = await admin.GetStringAsync("/u/admin");
        Assert.Contains($"/media/track/{teardrop}", profile); // top tracks, and the album-less recent scrobble
        Assert.Contains($"/media/track/{demo}", profile);

        // A lookup slower than the wait is reported as pending and finishes in the background.
        var metadata = factory.Services.GetRequiredService<MetadataService>();
        http.Delay = TimeSpan.FromMilliseconds(700);
        Assert.Equal(MediaLookup.None with { Pending = true }, await metadata.GetImageAsync(MediaKind.Track, teardrop, TimeSpan.FromMilliseconds(50)));
        Assert.NotNull((await metadata.GetImageAsync(MediaKind.Track, teardrop, TimeSpan.FromSeconds(10))).Image);
        http.Delay = TimeSpan.Zero;

        var cover = await admin.GetAsync($"/media/track/{teardrop}");
        Assert.Equal(Png, await cover.Content.ReadAsByteArrayAsync()); // Mezzanine's cover
        var none = await (await admin.GetAsync($"/media/track/{demo}")).Content.ReadAsByteArrayAsync();
        Assert.Equal((byte)1, none[6]); // the 1×1 "no image" placeholder (GIF width at byte 6), not the 2×1 "pending" one
        Assert.Equal(MediaLookup.None, await metadata.GetImageAsync(MediaKind.Track, 999_999, TimeSpan.Zero));
    }

    [Fact]
    public async Task NoCover_ServesTransparentPlaceholder_AndIsNotRetriedImmediately()
    {
        var http = new FakeHttp(r => Navidrome(r, placeholderCovers: true));
        using var factory = new MuseFmFactory(null, s => s.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => http)));
        var admin = await SignInAdminAsync(factory);
        var (_, albumId) = await ScrobbleMezzanineAsync(factory);
        await EnableNavidromeAsync(admin);

        var first = await admin.GetAsync($"/media/album/{albumId}");
        Assert.Equal("image/gif", first.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache", first.Headers.CacheControl!.ToString());
        await admin.GetAsync($"/media/album/{albumId}");
        Assert.Single(http.To("navidrome.test"), u => u.AbsolutePath == "/nd/rest/getCoverArt");

        var missing = await admin.GetAsync("/media/album/999999");
        Assert.Equal("image/gif", missing.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task LastFm_UsesEnvironmentKeyUntilAnAdminKeyIsSaved()
    {
        var http = new FakeHttp(request =>
        {
            var uri = request.RequestUri!;
            var query = HttpUtility.ParseQueryString(uri.Query);
            if (uri.Host == "lastfm.freetls.fastly.net") return Image(Png, "image/png");
            if (uri.Host != "ws.audioscrobbler.com") return null;
            return query["method"] switch
            {
                "artist.getInfo" when query["artist"] == "Radiohead" => Json("""{"artist":{"name":"Radiohead"}}"""),
                "artist.getInfo" when query["artist"] == "Massive Attack" => Json("""
                    {"artist":{"name":"Massive Attack","mbid":"","url":"https://www.last.fm/music/Massive+Attack",
                      "image":[{"#text":"https://lastfm.freetls.fastly.net/i/u/300x300/2a96cbd8b46e442fc41c2b86b821562f.png","size":"extralarge"}],
                      "tags":{"tag":[{"name":"trip-hop","url":"https://www.last.fm/tag/trip-hop"},{"name":"seen live"},{"name":"electronic"},{"name":"90s"}]},
                      "bio":{"summary":"Short.","content":"Bristol &amp; beyond.\n\n <a href=\"https://www.last.fm/music/Massive+Attack\">Read more on Last.fm</a>. User-contributed text is available under the Creative Commons By-SA License; additional terms may apply."}}}
                    """),
                "album.getInfo" => Json("""
                    {"album":{"name":"Mezzanine","tracks":{"track":[{"name":"Angel","duration":379},{"name":"Teardrop","duration":"330"},{"name":"Inertia Creeps","duration":null}]},
                      "image":[{"#text":"https://lastfm.freetls.fastly.net/i/u/64s/a.png","size":"small"},
                      {"#text":"https://lastfm.freetls.fastly.net/i/u/300x300/a.png","size":"extralarge"},{"#text":"","size":"mega"}]}}
                    """),
                _ => Json("""{"error":6,"message":"Not found"}"""),
            };
        });
        using var factory = new MuseFmFactory(
            new() { ["MuseFM:LastFmApiKey"] = "env-key" },
            s => s.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => http)));
        var admin = await SignInAdminAsync(factory);
        var (artistId, albumId) = await ScrobbleMezzanineAsync(factory);

        Assert.Equal("Last.fm saved and enabled. The API key from MuseFM__LastFmApiKey works.",
            await SaveConnectorAsync(admin, ConnectorKind.LastFm, new() { ["Enabled"] = "true" }));

        var artistPage = await admin.GetStringAsync($"/u/admin/artist/{artistId}");
        Assert.Contains("<p>Bristol &amp; beyond.</p>", artistPage);
        Assert.DoesNotContain("Creative Commons", artistPage);
        // Last.fm's star placeholder is not an artist photo.
        Assert.Equal("image/gif", (await admin.GetAsync($"/media/artist/{artistId}")).Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await admin.GetByteArrayAsync($"/media/album/{albumId}"));
        Assert.Contains(http.To("lastfm.freetls.fastly.net"), u => u.AbsolutePath == "/i/u/300x300/a.png");
        Assert.All(http.To("ws.audioscrobbler.com"), u => Assert.Equal("env-key", HttpUtility.ParseQueryString(u.Query)["api_key"]));

        // Listeners' tags stand in for genres, and the album's track list gives the tracks their lengths.
        var (genres, lengths) = await factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            return ((await db.ArtistMetadata.SingleAsync(m => m.ArtistId == artistId)).Genres, await db.Tracks.ToDictionaryAsync(t => t.Title, t => t.LengthSeconds));
        });
        Assert.Equal("Trip-Hop\nElectronic", genres);
        Assert.Equal((330, 379), (lengths["Teardrop"], lengths["Angel"]));

        // A key saved in the dashboard wins, also for the history import.
        Assert.Equal("Last.fm saved and enabled. The API key works.",
            await SaveConnectorAsync(admin, ConnectorKind.LastFm, new() { ["Enabled"] = "true", ["Secret"] = "admin-key" }));
        Assert.Equal("admin-key", HttpUtility.ParseQueryString(http.To("ws.audioscrobbler.com").Last().Query)["api_key"]);
        Assert.Equal("admin-key", factory.Services.GetRequiredService<ConnectorSettings>().LastFmApiKey);
    }

    [Fact]
    public async Task Admin_CanReorderConnectors_AndIncompleteOnesStayInactive()
    {
        using var factory = new MuseFmFactory(null);
        var admin = await SignInAdminAsync(factory);
        var settings = factory.Services.GetRequiredService<ConnectorSettings>();
        Assert.Equal([ConnectorKind.Navidrome, ConnectorKind.Maloja, ConnectorKind.ListenBrainz, ConnectorKind.LastFm], settings.All.Select(c => c.Kind));

        var token = await MuseFmFactory.GetAntiforgeryTokenAsync(admin, "/admin/metadata");
        await admin.PostAsync("/admin/metadata?handler=Move&kind=LastFm&delta=-1",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal([ConnectorKind.Navidrome, ConnectorKind.Maloja, ConnectorKind.LastFm, ConnectorKind.ListenBrainz], settings.All.Select(c => c.Kind));

        Assert.Equal("Maloja saved, but it stays inactive until it has a server URL.",
            await SaveConnectorAsync(admin, ConnectorKind.Maloja, new() { ["Enabled"] = "true" }));
        Assert.Equal("Navidrome saved, but it stays inactive until it has a username and password.",
            await SaveConnectorAsync(admin, ConnectorKind.Navidrome, new() { ["Enabled"] = "true", ["BaseUrl"] = NavidromeUrl }));
        Assert.Equal("The Navidrome server URL must start with http:// or https://, for example https://music.example.com.",
            await SaveConnectorAsync(admin, ConnectorKind.Navidrome, new() { ["Enabled"] = "true", ["BaseUrl"] = "javascript:alert(1)" }));
        Assert.False(settings.AnyActive);
        Assert.DoesNotContain("/media/", await admin.GetStringAsync("/u/admin"));
        Assert.Equal("Enable and set up a connector first.", await PostAdminAsync(admin, "handler=Fetch&mode=Missing"));

        // Settings survive a restart (they are re-read from the database).
        await settings.LoadAsync();
        Assert.Equal([ConnectorKind.Navidrome, ConnectorKind.Maloja, ConnectorKind.LastFm, ConnectorKind.ListenBrainz], settings.All.Select(c => c.Kind));
        Assert.Equal(NavidromeUrl, settings.Get(ConnectorKind.Navidrome).BaseUrl);

        // A connector added in an update joins existing installations at the end, keeping the admin's order.
        await factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().MetadataConnectors.Where(c => c.Kind == ConnectorKind.Maloja).ExecuteDeleteAsync());
        await settings.LoadAsync();
        Assert.Equal([ConnectorKind.Navidrome, ConnectorKind.LastFm, ConnectorKind.ListenBrainz, ConnectorKind.Maloja], settings.All.Select(c => c.Kind));

        // Regular users cannot open the page.
        await factory.CreateUserAsync("regular");
        var regular = factory.CreatePlainClient();
        var login = await MuseFmFactory.GetAntiforgeryTokenAsync(regular, "/login");
        await regular.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = login, ["Input.Username"] = "regular", ["Input.Password"] = "password123",
        }));
        Assert.Equal(HttpStatusCode.Redirect, (await regular.GetAsync("/admin/metadata")).StatusCode);
    }

    [Fact]
    public void GenreNames_CleanAndMergeSpellings()
    {
        Assert.Equal(["Hip-Hop", "Drum & Bass"], GenreNames.Clean(["  Hip-Hop ", "hip hop", "Seen Live", "1998", "80s", null, "", "Drum  &  Bass", new string('x', 60)]));
        Assert.Equal(["Trip Hop", "R&B", "IDM"], GenreNames.Clean(["trip hop", "r&b", "IDM"], titleCase: true));
        Assert.Equal(GenreNames.MaxPerItem, GenreNames.Clean(Enumerable.Range(0, 9).Select(i => $"genre {(char)('a' + i)}")).Count);
        Assert.Equal(("hip hop", "hip hop", "rock"), (GenreNames.Key("Hip-Hop"), GenreNames.Key(" hip_hop/ "), GenreNames.Key("ROCK")));
        Assert.Equal(["A", "B"], GenreNames.Split(GenreNames.Join(["A", "B"])));
        Assert.Null(GenreNames.Join([]));
    }

    [Fact]
    public void CleanBio_StripsMarkupAndLastFmFooter()
    {
        Assert.Equal("First paragraph & more.\n\nSecond one.",
            MetadataText.CleanBio("<p>First   paragraph &amp; <b>more</b>.</p>\n<p>Second one.</p>"));
        Assert.Equal("Short bio.",
            MetadataText.CleanBio("Short bio. <a href=\"https://www.last.fm/music/X\">Read more on Last.fm</a>. User-contributed text is available under the Creative Commons By-SA License; additional terms may apply."));
        Assert.Null(MetadataText.CleanBio(" <a href=\"https://www.last.fm/music/X\">Read more on Last.fm</a>"));
        Assert.Equal("Taken <literally> &amp; kept.\n\nNext.", MetadataText.CleanBio("Taken <literally> &amp; kept.\nNext.", html: false));
        Assert.Null(MetadataText.Mbid("not-an-id"));
        Assert.Null(MetadataText.HttpUrl("javascript:alert(1)"));
    }

    [Theory]
    [InlineData("The Beatles", "Beatles", true)]
    [InlineData("Simon & Garfunkel", "Simon and Garfunkel", true)]
    [InlineData("Björk", "Bjork", true)]
    [InlineData("AC/DC", "ac dc", true)]
    [InlineData("!!!", "!!!", true)]
    [InlineData("Air", "Fairground Attraction", false)]
    public void NameMatch_IsLenientButNotFuzzy(string a, string b, bool same) => Assert.Equal(same, NameMatch.Same(a, b));

    [Fact]
    public void NameMatch_TitlesAndCredits()
    {
        Assert.True(NameMatch.SameTitle("Mezzanine (Deluxe)", "Mezzanine"));
        Assert.True(NameMatch.SameTitle("OK Computer [Remastered]", "OK Computer"));
        Assert.False(NameMatch.SameTitle("Mezzanine", "Heligoland"));
        Assert.True(NameMatch.Credits("Bicep feat. Clara La San", "Clara La San"));
        Assert.False(NameMatch.Credits("Fairport Convention", "Air"));
    }
}
