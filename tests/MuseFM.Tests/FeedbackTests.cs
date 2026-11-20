using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;
using MuseFM.Services.Import;

namespace MuseFM.Tests;

/// <summary>MusicBrainz ids on the catalog, and loved tracks from the APIs, the pages and imports.</summary>
public class FeedbackTests : IClassFixture<MuseFmFactory>
{
    private const string Recording = "8f3471b5-7e6a-48da-86a9-c1c07a0f47ae";
    private const string OtherRecording = "0d4c2e8f-63a5-4a7d-a5c4-6f3a1d0e9b21";
    private const string GlueRecording = "3c9e5b7a-1d2f-4a6b-9c8d-7e6f5a4b3c2d";
    private const string Release = "9a7f1e8c-2b6d-4c3e-8f1a-5d4b3c2a1e0f";
    private const string Bicep = "4d1cbaa6-9d1d-4d2a-9b7a-0a0d0c2e3f41";
    private const string Clara = "b3e7f9a1-2c4d-4e6f-8a0b-1c2d3e4f5a6b";
    private const string FeedbackUrl = "/apis/listenbrainz/1/feedback/recording-feedback";

    private readonly MuseFmFactory _factory;

    public FeedbackTests(MuseFmFactory factory) => _factory = factory;

    private static long Ago(int minutes) => DateTimeOffset.UtcNow.AddMinutes(-minutes).ToUnixTimeSeconds();

    private static object Saku(long ts, string recording) => new
    {
        listened_at = ts,
        track_metadata = new
        {
            artist_name = "Bicep feat. Clara La San",
            track_name = "Saku",
            release_name = "Isles",
            additional_info = new
            {
                artist_names = new[] { "Bicep", "Clara La San" },
                artist_mbids = new[] { Bicep, Clara },
                recording_mbid = recording,
                release_mbid = Release,
            },
        },
    };

    private Task<T> Db<T>(Func<AppDbContext, Task<T>> query) => _factory.WithScopeAsync(sp => query(sp.GetRequiredService<AppDbContext>()));

    [Fact]
    public async Task SubmitListens_KeepsMusicBrainzIds_AndListensReturnThem()
    {
        var (_, token) = await _factory.CreateUserAsync("mbid-user");
        var client = _factory.CreateTokenClient(token);
        var response = await client.PostAsJsonAsync("/apis/listenbrainz/1/submit-listens",
            new { listen_type = "import", payload = new[] { Saku(Ago(10), Recording), Saku(Ago(5), OtherRecording) } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var (track, album, artists) = await Db(async db => (
            await db.Tracks.SingleAsync(t => t.Title == "Saku"),
            await db.Albums.SingleAsync(a => a.Title == "Isles"),
            await db.Artists.Where(a => a.Name == "Bicep" || a.Name == "Clara La San").ToDictionaryAsync(a => a.Name, a => a.MusicBrainzId)));
        // The first recording id stays; a later scrobble of the same track doesn't replace it.
        Assert.Equal(Recording, track.MusicBrainzId);
        Assert.Equal(Release, album.MusicBrainzId);
        Assert.Equal(Bicep, artists["Bicep"]);
        Assert.Equal(Clara, artists["Clara La San"]);

        var listens = await client.GetFromJsonAsync<JsonElement>("/apis/listenbrainz/1/user/mbid-user/listens");
        var info = listens.GetProperty("payload").GetProperty("listens")[0].GetProperty("track_metadata").GetProperty("additional_info");
        Assert.Equal(Recording, info.GetProperty("recording_mbid").GetString());
        Assert.Equal(Release, info.GetProperty("release_mbid").GetString());
        Assert.Equal([Bicep, Clara], info.GetProperty("artist_mbids").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task RecordingFeedback_LovesHatesAndClearsByMbid()
    {
        var (user, token) = await _factory.CreateUserAsync("lb-feedback");
        var client = _factory.CreateTokenClient(token);
        await client.PostAsJsonAsync("/apis/listenbrainz/1/submit-listens", new { listen_type = "single", payload = new[] { Saku(Ago(3), Recording) } });

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(FeedbackUrl, new { recording_mbid = Recording, score = 1 })).StatusCode);
        var loved = await client.GetFromJsonAsync<JsonElement>("/apis/listenbrainz/1/feedback/user/lb-feedback/get-feedback?metadata=true");
        Assert.Equal(1, loved.GetProperty("total_count").GetInt32());
        var item = loved.GetProperty("feedback")[0];
        Assert.Equal((Recording, 1), (item.GetProperty("recording_mbid").GetString(), item.GetProperty("score").GetInt32()));
        Assert.Equal("Saku", item.GetProperty("track_metadata").GetProperty("track_name").GetString());

        await client.PostAsJsonAsync(FeedbackUrl, new { recording_mbid = Recording, score = -1 });
        var hated = await client.GetFromJsonAsync<JsonElement>("/apis/listenbrainz/1/feedback/user/lb-feedback/get-feedback?score=-1");
        Assert.Equal(1, hated.GetProperty("count").GetInt32());
        var nowLoved = await client.GetFromJsonAsync<JsonElement>("/apis/listenbrainz/1/feedback/user/lb-feedback/get-feedback?score=1");
        Assert.Equal(0, nowLoved.GetProperty("count").GetInt32());

        await client.PostAsJsonAsync(FeedbackUrl, new { recording_mbid = Recording, score = 0 });
        Assert.Equal(0, await Db(db => db.TrackFeedback.CountAsync(f => f.UserId == user.Id)));

        // Unknown recordings are accepted (clients would retry forever otherwise) but there is nothing to store.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(FeedbackUrl, new { recording_mbid = OtherRecording, score = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(FeedbackUrl, new { recording_msid = OtherRecording, score = 1 })).StatusCode);
        Assert.Equal(0, await Db(db => db.TrackFeedback.CountAsync(f => f.UserId == user.Id)));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(FeedbackUrl, new { recording_mbid = "nope", score = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(FeedbackUrl, new { recording_mbid = Recording, score = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(FeedbackUrl, new { score = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreatePlainClient().PostAsJsonAsync(FeedbackUrl, new { recording_mbid = Recording, score = 1 })).StatusCode);
    }

    [Fact]
    public async Task TrackLove_ViaLastFmApi_AddsUnknownTracks_AndUnloveRemoves()
    {
        var (user, token) = await _factory.CreateUserAsync("as-love");
        var client = _factory.CreatePlainClient();
        var login = await client.PostAsync("/apis/audioscrobbler/2.0/", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["method"] = "auth.getMobileSession", ["username"] = "as-love", ["password"] = token,
        }));
        var sk = XDocument.Parse(await login.Content.ReadAsStringAsync()).Root!.Element("session")!.Element("key")!.Value;

        async Task<HttpResponseMessage> Call(string method, string format = "xml") =>
            await client.PostAsync("/apis/audioscrobbler/2.0/", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["method"] = method, ["sk"] = sk, ["artist"] = "Floating Points", ["track"] = "Silhouettes (I, II & III)", ["format"] = format,
            }));

        var love = await Call("track.love");
        Assert.Equal("ok", XDocument.Parse(await love.Content.ReadAsStringAsync()).Root!.Attribute("status")!.Value);
        Assert.Equal("Silhouettes (I, II & III)", await Db(db => db.TrackFeedback.Where(f => f.UserId == user.Id).Select(f => f.Track.Title).SingleAsync()));
        Assert.Contains("Silhouettes (I, II &amp; III)", await client.GetStringAsync("/u/as-love/loved"));

        var unlove = await Call("track.unlove", "json");
        Assert.Equal("{}", await unlove.Content.ReadAsStringAsync());
        Assert.Equal(0, await Db(db => db.TrackFeedback.CountAsync(f => f.UserId == user.Id)));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/apis/audioscrobbler/2.0/", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["method"] = "track.love", ["sk"] = "wrong", ["artist"] = "A", ["track"] = "B" }))).StatusCode);
    }

    [Fact]
    public async Task LoveButton_WorksForTheOwnerOnly()
    {
        var (owner, _) = await _factory.CreateUserAsync("love-owner");
        await _factory.CreateUserAsync("love-visitor");
        await _factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>()
            .IngestAsync(owner.Id, new ScrobbleInput { Title = "Kerala", Artists = ["Bonobo"], ListenedAt = Ago(30) }));
        var trackId = await Db(db => db.Tracks.Where(t => t.Title == "Kerala").Select(t => t.Id).SingleAsync());
        var page = $"/u/love-owner/track/{trackId}";

        var client = await _factory.SignInAsync("love-owner");
        var response = await MuseFmFactory.PostFormAsync(client, page, $"{page}?handler=Love",
            new() { ["score"] = "1", ["returnUrl"] = "/u/love-owner/scrobbles" });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/u/love-owner/scrobbles", response.Headers.Location!.OriginalString);
        Assert.Contains("love-button loved", await client.GetStringAsync("/u/love-owner/scrobbles"));
        Assert.Contains("aria-pressed=\"true\"", await client.GetStringAsync(page));

        // Off-site return URLs are ignored.
        var unlove = await MuseFmFactory.PostFormAsync(client, page, $"{page}?handler=Love", new() { ["score"] = "0", ["returnUrl"] = "https://example.com/" });
        Assert.Equal(page, unlove.Headers.Location!.OriginalString);
        Assert.Equal(0, await Db(db => db.TrackFeedback.CountAsync(f => f.UserId == owner.Id)));

        var visitor = await _factory.SignInAsync("love-visitor");
        var refused = await MuseFmFactory.PostFormAsync(visitor, page, $"{page}?handler=Love", new() { ["score"] = "1" });
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.DoesNotContain("love-button", await visitor.GetStringAsync(page));
    }

    [Fact]
    public async Task ListenBrainzExport_RoundTripsListensMbidsAndLoves()
    {
        var (source, token) = await _factory.CreateUserAsync("lb-export-source");
        var (target, _) = await _factory.CreateUserAsync("lb-export-target");
        await _factory.CreateTokenClient(token).PostAsJsonAsync("/apis/listenbrainz/1/submit-listens", new { listen_type = "single", payload = new[] { Saku(Ago(20), Recording) } });
        await _factory.WithScopeAsync(async sp =>
        {
            await sp.GetRequiredService<ScrobbleService>().IngestAsync(source.Id, new ScrobbleInput { Title = "Glue", Artists = ["Bicep"], ListenedAt = Ago(10) });
            var feedback = sp.GetRequiredService<FeedbackService>();
            var db = sp.GetRequiredService<AppDbContext>();
            foreach (var id in await db.Tracks.Where(t => t.Title == "Saku" || t.Title == "Glue").Select(t => t.Id).ToListAsync())
                await feedback.SetAsync(source.Id, id, TrackFeedback.Love, Ago(1));
            return 0;
        });

        var zip = Path.Combine(_factory.Services.GetRequiredService<DataPaths>().Imports, "lb-roundtrip.zip");
        await _factory.WithScopeAsync(async sp =>
        {
            await using var file = File.Create(zip);
            await sp.GetRequiredService<ExportService>().WriteListenBrainzAsync(source.Id, "lb-export-source", file, CancellationToken.None);
            return 0;
        });
        using (var archive = ZipFile.OpenRead(zip))
        {
            var listens = ReadLines(archive, ListenBrainzFormat.ListensEntry);
            Assert.Equal(2, listens.Count);
            Assert.Equal(Recording, listens[0].GetProperty("track_metadata").GetProperty("additional_info").GetProperty("recording_mbid").GetString());
            Assert.Equal(2, ReadLines(archive, ListenBrainzFormat.FeedbackEntry).Count);
        }

        var job = await _factory.RunImportAsync(new ImportJob { UserId = target.Id, Kind = ImportKind.ListenBrainzJson, Source = "lb.zip", StoredFile = zip });
        Assert.Equal(ImportStatus.Completed, job.Status);
        Assert.Equal(2, job.Imported);
        Assert.Contains("Loved tracks: 2 imported.", job.Message);
        Assert.Equal(2, await Db(db => db.TrackFeedback.CountAsync(f => f.UserId == target.Id && f.Score == TrackFeedback.Love)));
    }

    [Fact]
    public async Task ListenBrainzFeedback_WithOnlyMbids_MatchesScrobbledRecordings()
    {
        var (user, _) = await _factory.CreateUserAsync("lb-official");
        var zip = Path.Combine(_factory.Services.GetRequiredService<DataPaths>().Imports, "lb-official.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            // ListenBrainz's own export: listens per month, matched MBIDs in mbid_mapping, feedback by MBID only.
            Write(archive, "listens/2024/1.jsonl", """
                {"listened_at": 1704100000, "track_metadata": {"artist_name": "Bicep", "track_name": "Glue", "mbid_mapping": {"recording_mbid": "3c9e5b7a-1d2f-4a6b-9c8d-7e6f5a4b3c2d", "artist_mbids": ["4d1cbaa6-9d1d-4d2a-9b7a-0a0d0c2e3f41"]}}}
                """);
            Write(archive, "feedback.jsonl", """
                {"created": 1704200000, "recording_mbid": "3c9e5b7a-1d2f-4a6b-9c8d-7e6f5a4b3c2d", "recording_msid": null, "score": 1}
                {"created": 1704200001, "recording_mbid": "0d4c2e8f-63a5-4a7d-a5c4-6f3a1d0e9b21", "recording_msid": null, "score": 1}
                """);
        }

        var job = await _factory.RunImportAsync(new ImportJob { UserId = user.Id, Kind = ImportKind.ListenBrainzJson, Source = "lb.zip", StoredFile = zip });
        Assert.Equal(ImportStatus.Completed, job.Status);
        Assert.Contains("Loved tracks: 1 imported, 1 without a matching track.", job.Message);
        var love = await Db(db => db.TrackFeedback.Where(f => f.UserId == user.Id).Select(f => new { f.Track.Title, f.CreatedAt }).SingleAsync());
        Assert.Equal(("Glue", 1704200000L), (love.Title, love.CreatedAt));
        Assert.Equal(Bicep, await Db(db => db.Artists.Where(a => a.Name == "Bicep").Select(a => a.MusicBrainzId).SingleAsync()));
        Assert.Equal(GlueRecording, await Db(db => db.Tracks.Where(t => t.Title == "Glue").Select(t => t.MusicBrainzId).SingleAsync()));
    }

    [Fact]
    public void LastFmCsv_ReadsMusicBrainzColumns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"musefm-mbid-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, $"""
            uts,utc_time,artist,artist_mbid,album,album_mbid,track,track_mbid
            1704100000,"01 Jan 2024, 09:06",Bicep,{Bicep},Isles,{Release},Saku,{Recording}
            """);
        try
        {
            var input = LastFmCsvFormat.Read(path).Single().Input!;
            Assert.Equal((Recording, Release), (input.TrackMbid, input.AlbumMbid));
            Assert.Equal([Bicep], input.ArtistMbids!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static List<JsonElement> ReadLines(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}
