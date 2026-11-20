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
