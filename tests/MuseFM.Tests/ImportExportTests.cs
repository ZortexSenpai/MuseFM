using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;
using MuseFM.Services.Import;

namespace MuseFM.Tests;

public class ImportExportTests : IClassFixture<MuseFmFactory>, IDisposable
{
    private readonly MuseFmFactory _factory;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "musefm-import-tests", Guid.NewGuid().ToString("N"));

    public ImportExportTests(MuseFmFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private const string MalojaSample = """
        {
          "maloja": { "export_time": 1700000000 },
          "scrobbles": [
            { "time": 1600000000, "track": { "artists": ["Massive Attack"], "title": "Teardrop", "album": { "albumtitle": "Mezzanine", "artists": ["Massive Attack"] }, "length": 330 }, "duration": 320, "origin": "client:navidrome" },
            { "time": 1600000400, "track": { "artists": ["Massive Attack", "Elizabeth Fraser"], "title": "Black Milk", "album": null, "length": null }, "duration": null, "origin": "client:web" },
            { "time": 1600000800, "track": { "artists": [], "title": "Broken" } }
          ]
        }
        """;

    [Fact]
    public void Detect_RecognizesFormats()
    {
        Assert.Equal(ImportKind.MalojaJson, ImportFormats.Detect(WriteFile("maloja_export_1.json", MalojaSample)));
        Assert.Equal(ImportKind.LastFmCsv, ImportFormats.Detect(WriteFile("scrobbles.csv", "a,b,c,d")));
        Assert.Equal(ImportKind.LastFmStatsJson, ImportFormats.Detect(WriteFile("lastfmstats-x.json", """{"username":"x","scrobbles":[{"track":"t","artist":"a","date":1600000000000}]}""")));
        Assert.Equal(ImportKind.ListenBrainzJson, ImportFormats.Detect(WriteFile("lb.json", """[{"listened_at":1,"track_metadata":{}}]""")));
        Assert.Null(ImportFormats.Detect(WriteFile("other.json", """{"hello":"world"}""")));

        // Spotify: either export as a .json file, or the .zip Spotify sends; any other .zip is a ListenBrainz export.
        Assert.Equal(ImportKind.SpotifyJson, ImportFormats.Detect(WriteFile("Streaming_History_Audio_2023_0.json", SpotifyExtended)));
        Assert.Equal(ImportKind.SpotifyJson, ImportFormats.Detect(WriteFile("StreamingHistory_music_0.json", SpotifyAccountData)));
        Assert.Equal(ImportKind.SpotifyJson, ImportFormats.Detect(WriteZip("my_spotify_data.zip",
            ("Spotify Extended Streaming History/ReadMeFirst.pdf", "%PDF"), ("Spotify Extended Streaming History/Streaming_History_Audio_2023_0.json", SpotifyExtended))));
        Assert.Equal(ImportKind.ListenBrainzJson, ImportFormats.Detect(WriteZip("listenbrainz.zip", ("listens/2023/1.jsonl", Listen))));
        Assert.Null(ImportFormats.Detect(WriteFile("list.json", """[{"hello":"world"}]""")));
    }

    // Ends of plays in UTC. In Zurich (UTC+2 in May), "Roygbiv" began on 30 April 23:55, "Kid A" on 1 May 00:06 and
    // "Idioteque" on 3 May 00:29.
    private const string SpotifyExtended = """
        [
          { "ts": "2023-04-30T21:59:00Z", "platform": "android", "ms_played": 200000, "master_metadata_track_name": "Roygbiv", "master_metadata_album_artist_name": "Boards of Canada", "master_metadata_album_album_name": "Music Has the Right to Children", "spotify_track_uri": "spotify:track:1", "episode_name": null, "skipped": false },
          { "ts": "2023-04-30T22:10:00Z", "platform": "android", "ms_played": 240000, "master_metadata_track_name": "Kid A", "master_metadata_album_artist_name": "Radiohead", "master_metadata_album_album_name": "Kid A", "spotify_track_uri": "spotify:track:2", "episode_name": null, "skipped": false },
          { "ts": "2023-05-02T10:00:00Z", "platform": "osx", "ms_played": 180000, "master_metadata_track_name": "Treefingers", "master_metadata_album_artist_name": "Radiohead", "master_metadata_album_album_name": "Kid A", "spotify_track_uri": "spotify:track:3", "episode_name": null, "skipped": false },
          { "ts": "2023-05-02T10:01:00Z", "platform": "osx", "ms_played": 29999, "master_metadata_track_name": "Optimistic", "master_metadata_album_artist_name": "Radiohead", "master_metadata_album_album_name": "Kid A", "spotify_track_uri": "spotify:track:4", "episode_name": null, "skipped": true },
          { "ts": "2023-05-02T12:00:00Z", "platform": "osx", "ms_played": 1800000, "master_metadata_track_name": null, "master_metadata_album_artist_name": null, "master_metadata_album_album_name": null, "spotify_track_uri": null, "episode_name": "An episode", "episode_show_name": "A podcast", "skipped": false },
          { "ts": "2023-05-02T22:30:00Z", "platform": "osx", "ms_played": 60000, "master_metadata_track_name": "Idioteque", "master_metadata_album_artist_name": "Radiohead", "master_metadata_album_album_name": "Kid A", "spotify_track_uri": "spotify:track:5", "episode_name": null, "skipped": false },
          { "ts": "not a date", "ms_played": 60000, "master_metadata_track_name": "Morning Bell", "master_metadata_album_artist_name": "Radiohead" }
        ]
        """;

    private const string SpotifyAccountData = """
        [
          { "endTime": "2023-05-01 12:34", "artistName": "Burial", "trackName": "Archangel", "msPlayed": 238000 },
          { "endTime": "2023-05-01 12:35", "artistName": "Burial", "trackName": "Near Dark", "msPlayed": 1200 },
          { "endTime": "2023-05-01 12:40", "artistName": "Unknown Artist", "trackName": "Unknown Track", "msPlayed": 200000 }
        ]
        """;

    [Fact]
    public void Spotify_ReadsBothExports_AndLeavesOutShortPlaysAndPodcasts()
    {
        var rows = SpotifyFormat.Read(WriteFile("Streaming_History_Audio_2023_1.json", SpotifyExtended)).ToList();

        // The play of under 30 seconds and the podcast episode are not there at all; the play without a date failed.
        Assert.Equal(["Roygbiv", "Kid A", "Treefingers", "Idioteque"], rows.Where(r => r.Input is not null).Select(r => r.Input!.Title));
        Assert.Contains("Morning Bell", Assert.Single(rows, r => r.Input is null).Error);
        var kidA = rows[1].Input!;
        Assert.Equal(("Radiohead", "Kid A", 240, "import:spotify"), (kidA.Artists.Single(), kidA.Album, kidA.DurationSeconds, kidA.Origin));
        // Spotify notes the end of a play; the scrobble is dated to its start.
        Assert.Equal(new DateTimeOffset(2023, 4, 30, 22, 6, 0, TimeSpan.Zero).ToUnixTimeSeconds(), kidA.ListenedAt);

        var account = Assert.Single(SpotifyFormat.Read(WriteFile("StreamingHistory_music_1.json", SpotifyAccountData))).Input!;
        Assert.Equal(("Archangel", "Burial", null, 238), (account.Title, account.Artists.Single(), account.Album, account.DurationSeconds));
        Assert.Equal(new DateTimeOffset(2023, 5, 1, 12, 30, 2, TimeSpan.Zero).ToUnixTimeSeconds(), account.ListenedAt);

        Assert.Throws<InvalidDataException>(() => SpotifyFormat.Read(WriteFile("Userdata.json", """{"username":"me"}""")).ToList());
    }

    [Fact]
    public void Spotify_ReadsTheHistoryFilesOfAnArchive_WithinTheSizeLimits()
    {
        var archive = WriteZip("spotify.zip",
            ("MyData/Playlist1.json", """{"playlists":[]}"""),
            ("MyData/StreamingHistory_music_0.json", SpotifyAccountData),
            ("MyData/StreamingHistory_podcast_0.json", """[{ "endTime": "2023-05-01 13:00", "podcastName": "A podcast", "episodeName": "An episode", "msPlayed": 900000 }]"""),
            ("MyData/endsong_0.json", SpotifyExtended));
        Assert.Equal(["Archangel", "Roygbiv", "Kid A", "Treefingers", "Idioteque"],
            SpotifyFormat.Read(archive).Where(r => r.Input is not null).Select(r => r.Input!.Title));

        Assert.Contains("too large to import", Assert.Throws<InvalidDataException>(() => SpotifyFormat.Read(archive, maxDocumentBytes: 1000).ToList()).Message);
        Assert.Contains("unpacks to more than", Assert.Throws<InvalidDataException>(() => SpotifyFormat.Read(archive, maxArchiveBytes: 1000).ToList()).Message);
        Assert.Contains("no Spotify streaming history",
            Assert.Throws<InvalidDataException>(() => SpotifyFormat.Read(WriteZip("other.zip", ("MyData/Playlist1.json", "{}"))).ToList()).Message);
    }

    [Fact]
    public void Timeframe_IsWholeDaysInTheUsersTimeZone()
    {
        var zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
        long Utc(int month, int day, int hour) => new DateTimeOffset(2023, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        Assert.True(ImportRange.TryParse("2023-05-01", "2023-05-02", zurich, out var range, out _));
        Assert.Equal(new ImportRange(Utc(4, 30, 22), Utc(5, 2, 22)), range);
        Assert.True(range.Contains(Utc(4, 30, 22)));
        Assert.True(range.Contains(Utc(5, 2, 22) - 1));
        Assert.False(range.Contains(Utc(4, 30, 22) - 1));
        Assert.False(range.Contains(Utc(5, 2, 22)));

        // Either end can be left open, and so can both.
        Assert.True(ImportRange.TryParse("2023-05-01", " ", zurich, out range, out _));
        Assert.Equal(new ImportRange(Utc(4, 30, 22), null), range);
        Assert.True(ImportRange.TryParse(null, "2023-01-31", TimeZoneInfo.Utc, out range, out _));
        Assert.Equal(new ImportRange(null, Utc(2, 1, 0)), range);
        Assert.True(ImportRange.TryParse(null, null, zurich, out range, out _));
        Assert.True(range.Contains(0) && range.Contains(long.MaxValue));

        Assert.False(ImportRange.TryParse("2023-05-02", "2023-05-01", zurich, out _, out var error));
        Assert.Contains("after its last day", error);
        Assert.False(ImportRange.TryParse("01.05.2023", null, zurich, out _, out error));
        Assert.NotNull(error);
        Assert.False(ImportRange.TryParse("0001-01-01", null, zurich, out _, out _));
    }

    [Fact]
    public async Task SpotifyUpload_ImportsOnlyTheChosenTimeframe()
    {
        var (user, _) = await _factory.CreateUserAsync("import-spotify");
        await _factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.TimeZoneId, "Europe/Zurich")));
        var client = await _factory.SignInAsync("import-spotify");
        var archive = WriteZip("my_spotify_data.zip", ("Spotify Extended Streaming History/Streaming_History_Audio_2023_0.json", SpotifyExtended));

        async Task<ImportJob> UploadAsync(string from, string to, int skip)
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent(await MuseFmFactory.GetAntiforgeryTokenAsync(client, "/settings/import")), "__RequestVerificationToken" },
                { new ByteArrayContent(await File.ReadAllBytesAsync(archive)), "file", "my_spotify_data.zip" },
                { new StringContent(""), "format" },
                { new StringContent(from), "from" },
                { new StringContent(to), "to" },
            };
            Assert.Equal(System.Net.HttpStatusCode.Redirect, (await client.PostAsync("/settings/import?handler=Upload", form)).StatusCode);
            for (var i = 0; i < 300; i++)
            {
                var job = await _factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().ImportJobs.AsNoTracking()
                    .Where(j => j.UserId == user.Id).OrderBy(j => j.Id).Skip(skip).FirstOrDefaultAsync());
                if (job is { Status: ImportStatus.Completed or ImportStatus.Failed }) return job;
                await Task.Delay(100);
            }

            throw new TimeoutException("The import did not finish.");
        }

        // 1 and 2 May in Zurich: what began late on 30 April or early on 3 May, local time, stays out.
        var job = await UploadAsync("2023-05-01", "2023-05-02", skip: 0);
        Assert.Equal((ImportStatus.Completed, ImportKind.SpotifyJson), (job.Status, job.Kind));
        Assert.Equal((2, 0, 1), (job.Imported, job.Duplicates, job.Failed));
        var titles = await _factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Scrobbles.AsNoTracking()
            .Where(s => s.UserId == user.Id).OrderBy(s => s.ListenedAt).Select(s => s.Track.Title).ToListAsync());
        Assert.Equal(["Kid A", "Treefingers"], titles);

        var page = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/settings/import"));
        Assert.Contains("Import of my_spotify_data.zip (Spotify streaming history) started.", page);
        Assert.Contains("Spotify streaming history · 1 May 2023 – 2 May 2023", page);

        // Without a timeframe the rest follows; a timeframe that makes no sense starts nothing.
        var all = await UploadAsync("", "", skip: 1);
        Assert.Equal((2, 2, 1), (all.Imported, all.Duplicates, all.Failed));
        Assert.Null(all.ListenedFrom ?? all.ListenedBefore);
        using var backwards = new MultipartFormDataContent
        {
            { new StringContent(await MuseFmFactory.GetAntiforgeryTokenAsync(client, "/settings/import")), "__RequestVerificationToken" },
            { new ByteArrayContent(await File.ReadAllBytesAsync(archive)), "file", "my_spotify_data.zip" },
            { new StringContent("2023-05-02"), "from" },
            { new StringContent("2023-05-01"), "to" },
        };
        await client.PostAsync("/settings/import?handler=Upload", backwards);
        Assert.Contains("The first day of the timeframe is after its last day.", await client.GetStringAsync("/settings/import"));
        Assert.Equal(2, await _factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().ImportJobs.CountAsync(j => j.UserId == user.Id)));
    }

    private sealed class FakeLastFm(Func<System.Collections.Specialized.NameValueCollection, string> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(answer(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)), Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task LastFmImport_AsksLastFmForTheTimeframe_AndDropsWhatLiesOutside()
    {
        const long from = 1_700_000_000, before = from + 86_400;
        string Track(string name, long uts) =>
            $$$"""{"name":"{{{name}}}","artist":{"#text":"Autechre","mbid":""},"album":{"#text":"Amber","mbid":""},"mbid":"","date":{"uts":"{{{uts}}}"}}""";
        var asked = new System.Collections.Concurrent.ConcurrentQueue<(string? From, string? To)>();
        var http = new FakeLastFm(query =>
        {
            if (query["method"] != "user.getrecenttracks") return """{"lovedtracks":{"track":[],"@attr":{"totalPages":"1"}}}""";
            asked.Enqueue((query["from"], query["to"]));
            // Whether Last.fm counts the two ends as inside is not documented: here it does.
            var tracks = string.Join(",", Track("Too late", before), Track("Last", before - 1), Track("First", from), Track("Too early", from - 1));
            return $$$$"""{"recenttracks":{"track":[{{{{tracks}}}}],"@attr":{"page":"1","totalPages":"1","total":"4"}}}""";
        });
        using var factory = new MuseFmFactory(new() { ["MuseFM:LastFmApiKey"] = "key" },
            s => s.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => http)));
        var (user, _) = await factory.CreateUserAsync("import-lastfm");

        var job = await factory.RunImportAsync(new ImportJob
        {
            UserId = user.Id, Kind = ImportKind.LastFmApi, Source = "someone", ListenedFrom = from, ListenedBefore = before,
        });

        Assert.Equal((ImportStatus.Completed, 2, 0), (job.Status, job.Imported, job.Failed));
        Assert.Equal(((from - 1).ToString(), before.ToString()), Assert.Single(asked));
        var titles = await factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Scrobbles.AsNoTracking()
            .OrderBy(s => s.ListenedAt).Select(s => s.Track.Title).ToListAsync());
        Assert.Equal(["First", "Last"], titles);
    }

    [Fact]
    public void Maloja_ParsesScrobbles()
    {
        var rows = MalojaFormat.Read(WriteFile("maloja_export_2.json", MalojaSample)).ToList();

        Assert.Equal(3, rows.Count);
        var first = rows[0].Input!;
        Assert.Equal("Teardrop", first.Title);
        Assert.Equal("Mezzanine", first.Album);
        Assert.Equal(330, first.LengthSeconds);
        Assert.Equal(320, first.DurationSeconds);
        Assert.Equal(1600000000, first.ListenedAt);
        Assert.Equal(["Massive Attack", "Elizabeth Fraser"], rows[1].Input!.Artists);
        Assert.Null(rows[2].Input);
    }

    [Fact]
    public void LastFmCsv_ParsesHeaderlessAndHeaderVariants()
    {
        var headerless = LastFmCsvFormat.Read(WriteFile("a.csv",
            "Portishead,Dummy,Roads,31 Jan 2021 12:34\n\"Crosby, Stills & Nash\",\"CSN\",\"Wooden Ships\",01 Feb 2021 08:00\nbroken row\n")).ToList();
        Assert.Equal(3, headerless.Count);
        Assert.Equal("Roads", headerless[0].Input!.Title);
        Assert.Equal(new DateTimeOffset(2021, 1, 31, 12, 34, 0, TimeSpan.Zero).ToUnixTimeSeconds(), headerless[0].Input!.ListenedAt);
        Assert.Equal("Crosby, Stills & Nash", headerless[1].Input!.Artists[0]);
        Assert.NotNull(headerless[2].Error);

        var withHeader = LastFmCsvFormat.Read(WriteFile("b.csv",
            "uts,utc_time,artist,artist_mbid,album,album_mbid,track,track_mbid\n1612137600,\"01 Feb 2021, 00:00\",Burial,,Untrue,,Archangel,\n")).ToList();
        Assert.Single(withHeader);
        Assert.Equal(1612137600, withHeader[0].Input!.ListenedAt);
        Assert.Equal("Archangel", withHeader[0].Input!.Title);
        Assert.Equal("Untrue", withHeader[0].Input!.Album);
    }

    [Fact]
    public void LastFmStats_ConvertsMilliseconds()
    {
        var rows = LastFmStatsFormat.Read(WriteFile("lastfmstats-me.json",
            """{"username":"me","scrobbles":[{"track":"Windowlicker","artist":"Aphex Twin","album":"Windowlicker","albumId":"","date":1600000000000}]}""")).ToList();
        Assert.Equal(1600000000, rows.Single().Input!.ListenedAt);
    }

    [Fact]
    public void ListenBrainz_ParsesJsonLines()
    {
        var rows = ListenBrainzFormat.Read(WriteFile("listens.jsonl",
            """
            {"listened_at": 1600000000, "track_metadata": {"artist_name": "Caribou", "track_name": "Sun", "release_name": "Swim"}}
            {"listened_at": "2020-09-13T12:30:00Z", "track_metadata": {"artist_name": "Caribou", "track_name": "Odessa"}}
            """)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Swim", rows[0].Input!.Album);
        Assert.Equal(new DateTimeOffset(2020, 9, 13, 12, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds(), rows[1].Input!.ListenedAt);
    }

    [Fact]
    public async Task ImportJob_RunsInBackground_AndExportRoundTrips()
    {
        var (source, _) = await _factory.CreateUserAsync("import-source");
        var (target, _) = await _factory.CreateUserAsync("import-target");

        // Queue a Maloja import the same way the upload page does.
        var paths = _factory.Services.GetRequiredService<DataPaths>();
        var stored = Path.Combine(paths.Imports, "test-upload.json");
        await File.WriteAllTextAsync(stored, MalojaSample);
        var job = await RunJobAsync(new ImportJob { UserId = source.Id, Kind = ImportKind.MalojaJson, Source = "maloja_export.json", StoredFile = stored });

        Assert.Equal(ImportStatus.Completed, job.Status);
        Assert.Equal(2, job.Imported);
        Assert.Equal(1, job.Failed);
        Assert.False(File.Exists(stored), "Upload should be deleted after import");

        // Export as Maloja JSON and import that into a second user.
        var exported = Path.Combine(_dir, "maloja_export_roundtrip.json");
        await _factory.WithScopeAsync(async sp =>
        {
            await using var file = File.Create(exported);
            await sp.GetRequiredService<ExportService>().WriteMalojaAsync(source.Id, file, CancellationToken.None);
            return 0;
        });
        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(exported)))
        {
            var black = doc.RootElement.GetProperty("scrobbles")[1].GetProperty("track");
            Assert.Equal(2, black.GetProperty("artists").GetArrayLength());
        }

        Assert.Equal(ImportKind.MalojaJson, ImportFormats.Detect(exported));
        var rows = MalojaFormat.Read(exported).Select(r => r.Input!).ToList();
        var outcomes = await _factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>().IngestAsync(target.Id, rows));
        Assert.All(outcomes, o => Assert.Equal(ScrobbleOutcome.Accepted, o));

        // Re-importing the same data is a no-op.
        var again = await _factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>().IngestAsync(target.Id, rows));
        Assert.All(again, o => Assert.Equal(ScrobbleOutcome.Duplicate, o));

        // Last.fm CSV export parses back to the same tracks.
        var csv = Path.Combine(_dir, "export.csv");
        await _factory.WithScopeAsync(async sp =>
        {
            await using var file = File.Create(csv);
            await sp.GetRequiredService<ExportService>().WriteLastFmCsvAsync(source.Id, file, CancellationToken.None);
            return 0;
        });
        var csvRows = LastFmCsvFormat.Read(csv).Select(r => r.Input!).ToList();
        Assert.Equal(2, csvRows.Count);
        Assert.Equal("Massive Attack feat. Elizabeth Fraser", csvRows[1].Artists[0]);
        Assert.Equal(["Massive Attack", "Elizabeth Fraser"], MetadataParser.Parse(csvRows[1].Title, csvRows[1].Artists).Artists);
    }

    [Fact]
    public async Task ImportJob_WithBrokenFile_Fails()
    {
        var (user, _) = await _factory.CreateUserAsync("import-broken");
        var stored = Path.Combine(_factory.Services.GetRequiredService<DataPaths>().Imports, "broken.json");
        await File.WriteAllTextAsync(stored, "{ \"scrobbles\": [ { \"time\": ");

        var job = await RunJobAsync(new ImportJob { UserId = user.Id, Kind = ImportKind.MalojaJson, Source = "broken.json", StoredFile = stored });

        Assert.Equal(ImportStatus.Failed, job.Status);
        Assert.False(string.IsNullOrEmpty(job.Message));
    }

    private const string Listen = """{"listened_at":1700000000,"track_metadata":{"artist_name":"Bonobo","track_name":"Kerala"}}""";

    private string WriteZip(string name, params (string Name, string Content)[] files)
    {
        var path = Path.Combine(_dir, name);
        using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var file in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(file.Name).Open());
            writer.Write(file.Content);
        }

        return path;
    }

    [Fact]
    public void Imports_StopAtTheirSizeLimits_InsteadOfFillingMemory()
    {
        // A few kilobytes of .zip can unpack to gigabytes: what counts is what comes out, per file and in all.
        var padding = new string(' ', 5000);
        var bomb = WriteZip("bomb.zip", ("listens/2023/1.jsonl", Listen + "\n" + padding));
        Assert.Contains("too large to import", Assert.Throws<InvalidDataException>(() => ListenBrainzFormat.Read(bomb, maxDocumentBytes: 1000).ToList()).Message);
        Assert.Single(ListenBrainzFormat.Read(bomb, maxDocumentBytes: 10_000));

        var many = WriteZip("many.zip", Enumerable.Range(1, 6).Select(i => ($"listens/2023/{i}.jsonl", Listen + "\n" + new string(' ', 500))).ToArray());
        Assert.Contains("unpacks to more than", Assert.Throws<InvalidDataException>(() => ListenBrainzFormat.Read(many, 1000, maxArchiveBytes: 2000).ToList()).Message);
        Assert.Equal(6, ListenBrainzFormat.Read(many, 1000, maxArchiveBytes: 10_000).Count());

        var feedback = WriteZip("feedback.zip", ("feedback.jsonl", """{"recording_mbid":"8f3471b5-7e6a-48da-86a9-c1c07a0f47ae","score":1,"created":1700000000}""" + "\n" + padding));
        Assert.Throws<InvalidDataException>(() => ListenBrainzFormat.ReadFeedback(feedback, maxEntryBytes: 1000).ToList());
        Assert.Single(ListenBrainzFormat.ReadFeedback(feedback));

        // JSON that is one document is held in memory as a whole; lines are read one at a time, however many there are.
        var array = WriteFile("array.json", "[" + Listen + "," + Listen.Replace("1700000000", "1700000100") + "]" + padding);
        Assert.Contains("too large to import in one piece", Assert.Throws<InvalidDataException>(() => ListenBrainzFormat.Read(array, maxDocumentBytes: 1000).ToList()).Message);
        Assert.Equal(2, ListenBrainzFormat.Read(array).Count());
        var lines = WriteFile("lines.jsonl", string.Join("\n", Enumerable.Range(0, 50).Select(i => Listen.Replace("1700000000", (1700000000 + i).ToString()))));
        Assert.Equal(50, ListenBrainzFormat.Read(lines, maxDocumentBytes: 1000).Count());

        var export = WriteFile("maloja_export.json", MalojaSample);
        Assert.Throws<InvalidDataException>(() => ImportLimits.OpenDocument(export, maxBytes: 100));
        using var opened = ImportLimits.OpenDocument(export);
    }

    [Fact]
    public async Task Imports_AreLimitedPerUser()
    {
        var (user, _) = await _factory.CreateUserAsync("import-hoarder");
        var client = await _factory.SignInAsync("import-hoarder");
        // Waiting jobs, as if the worker were busy with someone else's import.
        await _factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            for (var i = 0; i < ImportLimits.MaxPendingJobs; i++)
                db.ImportJobs.Add(new ImportJob { UserId = user.Id, Kind = ImportKind.MalojaApi, Source = "https://maloja.example" });
            return await db.SaveChangesAsync();
        });

        var response = await MuseFmFactory.PostFormAsync(client, "/settings/import", "/settings/import?handler=Maloja", new() { ["malojaUrl"] = "https://maloja.example" });
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("You already have 3 imports waiting or running.", await client.GetStringAsync("/settings/import"));
        Assert.Equal(ImportLimits.MaxPendingJobs, await _factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().ImportJobs.CountAsync(j => j.UserId == user.Id)));
    }

    private Task<ImportJob> RunJobAsync(ImportJob job) => _factory.RunImportAsync(job);
}
