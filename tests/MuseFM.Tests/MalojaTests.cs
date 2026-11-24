using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;
using MuseFM.Services.Import;
using MuseFM.Services.Metadata;

namespace MuseFM.Tests;

public class MalojaTests
{
    private const string Host = "maloja.test";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1, 4, 5, 6];

    /// <summary>A Maloja server: the read API over a list of scrobbles, plus whatever <paramref name="extra"/> answers.</summary>
    private sealed class FakeMaloja(List<object> scrobbles, Func<HttpRequestMessage, HttpResponseMessage?>? extra = null, bool ignoresRanges = false)
        : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(uri);
            return Task.FromResult(Route(uri) ?? extra?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private HttpResponseMessage? Route(Uri uri)
        {
            if (uri.Host != Host) return null;
            var query = HttpUtility.ParseQueryString(uri.Query);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return uri.AbsolutePath switch
            {
                "/apis/mlj_1/serverinfo" => Json(new { name = "Generic Maloja User", version = new[] { "3", "2", "10" }, versionstring = "3.2.10" }),
                "/apis/mlj_1/numscrobbles" => Json(new
                {
                    status = "ok",
                    amount = Times().Count(t => t <= (query["to"] is { } to ? End(to) : now)),
                }),
                "/apis/mlj_1/scrobbles" => Json(new
                {
                    status = "ok",
                    list = scrobbles.Where(s => ignoresRanges || InMonth(Time(s), query["in"]!)).OrderByDescending(Time).ToList(),
                }),
                _ => null,
            };
        }

        private IEnumerable<long> Times() => scrobbles.Select(Time);

        private static long Time(object scrobble) => (long)scrobble.GetType().GetProperty("time")!.GetValue(scrobble)!;

        private static DateTimeOffset Start(string month) =>
            DateTimeOffset.ParseExact(month + "/01", "yyyy'/'MM'/'dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        private static long End(string month) => Start(month).AddMonths(1).ToUnixTimeSeconds() - 1;

        private static bool InMonth(long time, string month) => time >= Start(month).ToUnixTimeSeconds() && time <= End(month);
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode status, string? location = null)
    {
        var response = new HttpResponseMessage(status);
        if (location is not null) response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static object Scrobble(long time, string title, string[] artists, string? album, string origin = "client:navidrome") => new
    {
        time,
        track = new
        {
            artists,
            title,
            album = album is null ? null : new { albumtitle = album, artists = artists[..1] },
            length = 300,
        },
        duration = 280,
        origin,
    };

    private static MuseFmFactory Factory(HttpMessageHandler http) =>
        new(null, s => s.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => http)));

    private static async Task<HttpClient> RegisterAsync(MuseFmFactory factory)
    {
        // The first account is the administrator.
        var client = factory.CreatePlainClient();
        var token = await MuseFmFactory.GetAntiforgeryTokenAsync(client, "/register");
        var response = await client.PostAsync("/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["Input.Username"] = "listener",
            ["Input.Password"] = "password123", ["Input.ConfirmPassword"] = "password123",
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    /// <summary>Starts an import from the Import page and returns the page's status message.</summary>
    private static async Task<string> StartImportAsync(HttpClient client, string address, string from = "", string to = "")
    {
        var token = await MuseFmFactory.GetAntiforgeryTokenAsync(client, "/settings/import");
        var response = await client.PostAsync("/settings/import?handler=Maloja", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["malojaUrl"] = address, ["from"] = from, ["to"] = to,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var html = await client.GetStringAsync("/settings/import");
        var message = System.Text.RegularExpressions.Regex.Match(html, "<div class=\"alert (?:success|error)\">([^<]*)</div>");
        return WebUtility.HtmlDecode(message.Groups[1].Value);
    }

    private static async Task<ImportJob> WaitForJobAsync(MuseFmFactory factory, int skip = 0)
    {
        for (var i = 0; i < 300; i++)
        {
            var job = await factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().ImportJobs.AsNoTracking()
                .OrderBy(j => j.Id).Skip(skip).FirstOrDefaultAsync());
            if (job is { Status: ImportStatus.Completed or ImportStatus.Failed }) return job;
            await Task.Delay(100);
        }

        throw new TimeoutException("The import did not finish.");
    }

    [Fact]
    public async Task Import_ReadsTheWholeHistoryMonthByMonth_AndStopsAtTheOldestScrobble()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var oldest = now - 730 * 86400L;
        var http = new FakeMaloja(
        [
            Scrobble(now - 120, "Angel", ["Massive Attack"], "Mezzanine"),
            Scrobble(now - 60, "Teardrop", ["Massive Attack", "Elizabeth Fraser"], "Mezzanine"),
            Scrobble(now - 95 * 86400L, "Glory Box", ["Portishead"], "Dummy", origin: "client:web"),
            Scrobble(oldest, "Unfinished Sympathy", ["Massive Attack"], null),
        ]);
        using var factory = Factory(http);
        var client = await RegisterAsync(factory);

        // No scheme, a trailing slash: tidied up to the server's address.
        Assert.Equal("Import from the Maloja server at http://maloja.test:42010 started.", await StartImportAsync(client, " maloja.test:42010/ "));
        var job = await WaitForJobAsync(factory);

        Assert.Equal(ImportStatus.Completed, job.Status);
        Assert.Equal(ImportKind.MalojaApi, job.Kind);
        Assert.Equal("http://maloja.test:42010", job.Source);
        Assert.Equal((4, 4, 0, 0), (job.Total, job.Imported, job.Duplicates, job.Failed));

        var scrobbles = await factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Scrobbles.AsNoTracking()
            .OrderBy(s => s.ListenedAt)
            .Select(s => new { s.Track.Title, Album = s.Album == null ? null : s.Album.Title, s.Origin, s.DurationSeconds, Artists = s.Track.Artists.Count })
            .ToListAsync());
        Assert.Equal(["Unfinished Sympathy", "Glory Box", "Angel", "Teardrop"], scrobbles.Select(s => s.Title));
        Assert.Equal([null, "Dummy", "Mezzanine", "Mezzanine"], scrobbles.Select(s => s.Album));
        Assert.Equal("client:web", scrobbles[1].Origin);
        Assert.Equal(280, scrobbles[1].DurationSeconds);
        Assert.Equal(2, scrobbles[3].Artists); // featured artist kept

        // The walk back in time ends with the month of the oldest scrobble, after one check that nothing older is left.
        var months = http.Requests.Where(u => u.AbsolutePath == "/apis/mlj_1/scrobbles").Select(u => HttpUtility.ParseQueryString(u.Query)["in"]).ToList();
        var oldestMonth = DateTimeOffset.FromUnixTimeSeconds(oldest).ToString("yyyy'/'MM", CultureInfo.InvariantCulture);
        Assert.Equal(oldestMonth, months[^1]);
        Assert.Equal(months.Count, months.Distinct().Count());
        Assert.InRange(months.Count, 25, 27);

        // Importing again is safe: everything is already there.
        await StartImportAsync(client, "http://maloja.test:42010");
        var again = await WaitForJobAsync(factory, skip: 1);
        Assert.Equal((ImportStatus.Completed, 0, 4), (again.Status, again.Imported, again.Duplicates));
    }

    [Fact]
    public async Task Import_OfATimeframe_ReadsOnlyItsMonths_AndImportsOnlyItsDays()
    {
        long At(int year, int month, int day, int hour = 12) => new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var http = new FakeMaloja(
        [
            Scrobble(At(2024, 1, 5), "Later", ["Portishead"], "Third"),
            Scrobble(At(2023, 6, 20), "Same month, after", ["Portishead"], "Third"),
            Scrobble(At(2023, 6, 10, 23), "Last day", ["Portishead"], "Dummy"),
            Scrobble(At(2023, 5, 15), "In between", ["Portishead"], "Dummy"),
            Scrobble(At(2023, 4, 20, 0), "First day", ["Portishead"], "Dummy"),
            Scrobble(At(2023, 4, 19, 23), "Same month, before", ["Portishead"], "Dummy"),
            Scrobble(At(2021, 3, 1), "Earlier", ["Portishead"], "Dummy"),
        ]);
        using var factory = Factory(http);
        var client = await RegisterAsync(factory);

        await StartImportAsync(client, "http://maloja.test:42010", from: "2023-04-20", to: "2023-06-10");
        var job = await WaitForJobAsync(factory);

        Assert.Equal((ImportStatus.Completed, 3, 0, 0), (job.Status, job.Imported, job.Duplicates, job.Failed));
        Assert.Equal((At(2023, 4, 20, 0), At(2023, 6, 11, 0)), (job.ListenedFrom, job.ListenedBefore));
        var titles = await factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Scrobbles.AsNoTracking()
            .OrderBy(s => s.ListenedAt).Select(s => s.Track.Title).ToListAsync());
        Assert.Equal(["First day", "In between", "Last day"], titles);

        // Whole months come back, so the count covers what shares a month with the timeframe - and nothing beyond.
        Assert.Equal(5, job.Total);
        var months = http.Requests.Where(u => u.AbsolutePath == "/apis/mlj_1/scrobbles").Select(u => HttpUtility.ParseQueryString(u.Query)["in"]).ToList();
        Assert.Equal(["2023/06", "2023/05", "2023/04"], months);
    }

    [Fact]
    public async Task Import_ExplainsWhatWentWrong()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var http = new FakeMaloja([Scrobble(now - 400 * 86400L, "Song", ["Band"], null)], ignoresRanges: true);
        using var factory = Factory(http);
        var client = await RegisterAsync(factory);

        Assert.Equal("Enter the address of your Maloja server, for example http://192.168.1.10:42010 or https://maloja.example.com.",
            await StartImportAsync(client, "ftp://maloja.test"));
        Assert.Equal("Leave the user name and password out of the address.", await StartImportAsync(client, "https://me:secret@maloja.test"));

        await StartImportAsync(client, "http://elsewhere.test");
        var missing = await WaitForJobAsync(factory);
        Assert.Equal(ImportStatus.Failed, missing.Status);
        Assert.StartsWith("No Maloja server found at http://elsewhere.test.", missing.Message);

        // A server that answers every month with the whole history would make the walk back in time endless.
        await StartImportAsync(client, "http://maloja.test:42010");
        var ignoring = await WaitForJobAsync(factory, skip: 1);
        Assert.Equal(ImportStatus.Failed, ignoring.Status);
        Assert.StartsWith("This Maloja server does not support time ranges.", ignoring.Message);
        Assert.Equal(0, ignoring.Imported);
    }

    /// <summary>A response body that never ends.</summary>
    private sealed class EndlessSpaces : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer.AsSpan(offset, count).Fill((byte)' ');
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Import_GivesUpOnAnAnswerWithoutEnd()
    {
        var http = new FakeMaloja([], request => request.RequestUri!.Host == "endless.test"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessSpaces()) }
            : null);
        using var factory = Factory(http);
        var client = await RegisterAsync(factory);

        await StartImportAsync(client, "http://endless.test");
        var job = await WaitForJobAsync(factory);
        Assert.Equal(ImportStatus.Failed, job.Status);
        Assert.Equal("Maloja sent more than 64 MB in one answer. Upload a Maloja export file instead.", job.Message);
    }

    /// <summary>
    /// Something listening on this machine only, as a stand-in for a service in the server's own network. It answers
    /// 404 to everything, like a web server that is not a Maloja.
    /// </summary>
    private sealed class LocalService : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public LocalService()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                        var stream = connection.GetStream();
                        var buffer = new byte[8192];
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        Paths.Enqueue(Encoding.ASCII.GetString(buffer, 0, read).Split(' ') is [_, var path, ..] ? path : "");
                        await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException or IOException)
                {
                    // Stopped.
                }
            });
        }

        public int Port { get; }
        public ConcurrentQueue<string> Paths { get; } = new();

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    // The next three run against the real HTTP clients, no fake in between: the check sits where connections are made.

    [Theory]
    [InlineData("http://127.0.0.1:{0}")]
    [InlineData("http://localhost:{0}")] // a name that resolves to a local address
    [InlineData("http://[::1]:{0}")]
    [InlineData("http://[::ffff:127.0.0.1]:{0}")]
    public async Task Import_OnlyReachesTheServersOwnNetwork_ForAdministrators(string addressFormat)
    {
        using var local = new LocalService();
        var address = string.Format(CultureInfo.InvariantCulture, addressFormat, local.Port);
        using var factory = new MuseFmFactory();
        var admin = await RegisterAsync(factory);
        await factory.CreateUserAsync("guest");
        var guest = await factory.SignInAsync("guest");

        // An ordinary account: nothing is sent, and the message says no more than that.
        await StartImportAsync(guest, address);
        var refused = await WaitForJobAsync(factory);
        Assert.Equal(ImportStatus.Failed, refused.Status);
        Assert.Equal($"{refused.Source} is not an address on the public internet. Only administrators can import from a server on a private or local network.",
            refused.Message);
        Assert.Empty(local.Paths);

        // The administrator's own Maloja usually runs right there. (This one is not a Maloja.)
        await StartImportAsync(admin, address);
        var allowed = await WaitForJobAsync(factory, skip: 1);
        Assert.DoesNotContain("public internet", allowed.Message);
        // The stand-in listens on IPv4 only.
        if (!address.Contains('[')) Assert.Equal(["/apis/mlj_1/serverinfo"], local.Paths);
    }

    [Fact]
    public async Task Import_ReachesTheServersOwnNetwork_ForEveryone_WhenSetUpThatWay()
    {
        using var local = new LocalService();
        using var factory = new MuseFmFactory(new() { ["MuseFM:ImportFromPrivateAddresses"] = "true" });
        await RegisterAsync(factory);
        await factory.CreateUserAsync("guest");

        await StartImportAsync(await factory.SignInAsync("guest"), $"http://127.0.0.1:{local.Port}");
        var job = await WaitForJobAsync(factory);
        Assert.StartsWith($"No Maloja server found at http://127.0.0.1:{local.Port}.", job.Message);
        Assert.Equal(["/apis/mlj_1/serverinfo"], local.Paths);
    }

    [Fact]
    public async Task Import_DoesNotSayWhyAServerCouldNotBeReached()
    {
        int port;
        using (var local = new LocalService()) port = local.Port;
        using var factory = new MuseFmFactory();
        var admin = await RegisterAsync(factory);

        // "Connection refused" next to "HTTP 404" would tell which ports are open.
        await StartImportAsync(admin, $"http://127.0.0.1:{port}");
        var job = await WaitForJobAsync(factory);
        Assert.Equal($"The Maloja server at http://127.0.0.1:{port} could not be reached. Check the address and that the server is running.", job.Message);
    }

    [Theory]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", true)]
    [InlineData("172.15.255.255", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("100.63.0.1", true)]
    [InlineData("64:ff9b::5db8:d822", true)] // NAT64 for 93.184.216.34
    [InlineData("127.0.0.1", false)]
    [InlineData("127.8.8.8", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)] // cloud instance metadata
    [InlineData("100.64.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:192.168.1.10", false)]
    [InlineData("::127.0.0.1", false)]
    [InlineData("64:ff9b::a00:1", false)] // NAT64 for 10.0.0.1
    [InlineData("fe80::1", false)]
    [InlineData("fd12:3456:789a::1", false)]
    [InlineData("ff02::1", false)]
    public void OnlyOrdinaryInternetAddresses_ArePublic(string address, bool expected) =>
        Assert.Equal(expected, PublicInternet.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("maloja:42010", "http://maloja:42010")]
    [InlineData("https://music.example.com/maloja/apis/mlj_1/", "https://music.example.com/maloja")]
    [InlineData("HTTP://Maloja.Example.com:443/", "http://maloja.example.com:443")]
    public void ServerAddresses_AreTidiedUp(string typed, string expected)
    {
        Assert.True(MalojaClient.TryNormalizeUrl(typed, out var url, out _));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://maloja.test/?key=1")]
    public void ServerAddresses_MustBeHttpServers(string typed) => Assert.False(MalojaClient.TryNormalizeUrl(typed, out _, out _));

    [Fact]
    public async Task Connector_SuppliesArtistImages_WaitsForMaloja_AndIgnoresPlaceholders()
    {
        var imageRequests = 0;
        var http = new FakeMaloja([], request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host != Host) return null;
            var query = HttpUtility.ParseQueryString(uri.Query);
            switch (uri.AbsolutePath)
            {
                case "/apis/mlj_1/search" when query["query"] == "Massive Attack":
                    return Json(new
                    {
                        artists = new[]
                        {
                            new { artist = "Massive Attack Tribute Band", image = "/image?artist_id=9" },
                            new { artist = "Massive Attack", image = "/image?artist_id=3" },
                        },
                        tracks = Array.Empty<object>(),
                        albums = Array.Empty<object>(),
                    });
                case "/apis/mlj_1/search" when query["query"] is "Mezzanine" or "Protection":
                    return Json(new
                    {
                        artists = Array.Empty<object>(),
                        tracks = Array.Empty<object>(),
                        albums = new[]
                        {
                            new { album = new { albumtitle = "Mezzanine", artists = new[] { "Massive Attack" } }, image = "/image?album_id=5" },
                            new { album = new { albumtitle = "Protection", artists = new[] { "Massive Attack" } }, image = "/image?album_id=6" },
                        },
                    });
                case "/image" when query["artist_id"] == "3":
                    // Maloja is still looking the first time it is asked.
                    return Interlocked.Increment(ref imageRequests) == 1
                        ? Status(HttpStatusCode.Accepted)
                        : Status(HttpStatusCode.TemporaryRedirect, "/images/artists/massive-attack.jpg");
                case "/images/artists/massive-attack.jpg":
                    var image = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Jpeg) };
                    image.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    return image;
                case "/image" when query["album_id"] == "5":
                    return Status(HttpStatusCode.TemporaryRedirect, "https://generative-placeholders.glitch.me/image?width=300&height=300&style=joy-division&colors=5");
                case "/image" when query["album_id"] == "6":
                    return Status(HttpStatusCode.Accepted);
                default:
                    return null;
            }
        });
        using var factory = Factory(http);
        var admin = await RegisterAsync(factory);

        var token = await MuseFmFactory.GetAntiforgeryTokenAsync(admin, "/admin/metadata");
        var saved = await admin.PostAsync("/admin/metadata?handler=Save&kind=Maloja", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["Enabled"] = "true", ["BaseUrl"] = "http://maloja.test:42010/",
        }));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var page = await admin.GetStringAsync("/admin/metadata");
        Assert.Contains("Maloja saved and enabled. Connected to Maloja 3.2.10.", page);
        Assert.Equal("http://maloja.test:42010", factory.Services.GetRequiredService<ConnectorSettings>().Get(ConnectorKind.Maloja).BaseUrl);

        var (artistId, mezzanineId, protectionId) = await factory.WithScopeAsync(async sp =>
        {
            var user = await sp.GetRequiredService<UserManager<AppUser>>().FindByNameAsync("listener");
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await sp.GetRequiredService<ScrobbleService>().IngestAsync(user!.Id,
            [
                new ScrobbleInput { Title = "Teardrop", Artists = ["Massive Attack"], Album = "Mezzanine", ListenedAt = now - 600 },
                new ScrobbleInput { Title = "Karmacoma", Artists = ["Massive Attack"], Album = "Protection", ListenedAt = now - 300 },
            ]);
            var db = sp.GetRequiredService<AppDbContext>();
            return (
                (await db.Artists.SingleAsync(a => a.Name == "Massive Attack")).Id,
                (await db.Albums.SingleAsync(a => a.Title == "Mezzanine")).Id,
                (await db.Albums.SingleAsync(a => a.Title == "Protection")).Id);
        });

        var metadata = factory.Services.GetRequiredService<MetadataService>();
        await metadata.FetchAsync(MediaKind.Artist, artistId);
        await metadata.FetchAsync(MediaKind.Album, mezzanineId);
        await metadata.FetchAsync(MediaKind.Album, protectionId);

        var (artist, mezzanine, protection) = await factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            return (
                await db.ArtistMetadata.AsNoTracking().SingleAsync(m => m.ArtistId == artistId),
                await db.AlbumMetadata.AsNoTracking().SingleAsync(m => m.AlbumId == mezzanineId),
                await db.AlbumMetadata.AsNoTracking().SingleAsync(m => m.AlbumId == protectionId));
        });

        // The photo of the exact artist, after Maloja finished looking for it.
        Assert.Equal("Maloja", artist.ImageSource);
        Assert.Null(artist.Bio);
        Assert.Equal(2, imageRequests);
        var served = await factory.CreatePlainClient().GetByteArrayAsync($"/media/artist/{artistId}");
        Assert.Equal(Jpeg, served);

        // A generated placeholder is no cover, and it is never downloaded.
        Assert.Null(mezzanine.ImageFile);
        Assert.True(mezzanine.RefreshAfter > DateTime.UtcNow.AddDays(10));
        Assert.DoesNotContain(http.Requests, u => u.Host == "generative-placeholders.glitch.me");

        // Still looking after a few tries: asked again within the hour rather than in two weeks.
        Assert.Null(protection.ImageFile);
        Assert.True(protection.RefreshAfter < DateTime.UtcNow.AddHours(2));
        Assert.Equal(3, http.Requests.Count(u => u.Query == "?album_id=6"));
        Assert.Null(factory.Services.GetRequiredService<ConnectorSettings>().Health(ConnectorKind.Maloja)?.Error);
    }
}
