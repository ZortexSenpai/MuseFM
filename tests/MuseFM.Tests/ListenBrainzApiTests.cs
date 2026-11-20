using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MuseFM.Tests;

public class ListenBrainzApiTests : IClassFixture<MuseFmFactory>
{
    private readonly MuseFmFactory _factory;

    public ListenBrainzApiTests(MuseFmFactory factory) => _factory = factory;

    private static object Listen(long ts, string artist, string track, string? release = null, object? info = null) => new
    {
        listened_at = ts,
        track_metadata = new { artist_name = artist, track_name = track, release_name = release, additional_info = info },
    };

    private static long Ago(int minutes) => DateTimeOffset.UtcNow.AddMinutes(-minutes).ToUnixTimeSeconds();

    [Fact]
    public async Task ValidateToken_ReportsUserForValidToken()
    {
        var (_, token) = await _factory.CreateUserAsync("lb-validate");
        var client = _factory.CreatePlainClient();

        var valid = await client.GetFromJsonAsync<JsonElement>($"/apis/listenbrainz/1/validate-token?token={token}");
        Assert.True(valid.GetProperty("valid").GetBoolean());
        Assert.Equal("lb-validate", valid.GetProperty("user_name").GetString());

        var invalid = await client.GetFromJsonAsync<JsonElement>("/apis/listenbrainz/1/validate-token?token=nope");
        Assert.False(invalid.GetProperty("valid").GetBoolean());

        var viaHeader = _factory.CreateTokenClient(token);
        var headerResult = await viaHeader.GetFromJsonAsync<JsonElement>("/apis/listenbrainz/1/validate-token");
        Assert.True(headerResult.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task SubmitListens_RequiresValidToken()
    {
        var client = _factory.CreateTokenClient("not-a-key");
        var response = await client.PostAsJsonAsync("/apis/listenbrainz/1/submit-listens",
            new { listen_type = "single", payload = new[] { Listen(Ago(5), "A", "B") } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SubmitListens_StoresSingleAndImportAndSkipsDuplicates()
    {
        var (_, token) = await _factory.CreateUserAsync("lb-submit");
        var client = _factory.CreateTokenClient(token);
        var t1 = Ago(30);
        var t2 = Ago(20);
        var t3 = Ago(10);

        var single = await client.PostAsJsonAsync("/apis/listenbrainz/1/submit-listens", new
        {
            listen_type = "single",
            payload = new[] { Listen(t1, "Daft Punk feat. Pharrell Williams", "Get Lucky", "Random Access Memories", new { duration_ms = 248000, submission_client = "test-client" }) },
        });
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);

        var import = await client.PostAsJsonAsync("/apis/listenbrainz/1/submit-listens", new
        {
            listen_type = "import",
            payload = new[]
            {
                Listen(t1, "Daft Punk", "Get Lucky"), // duplicate timestamp
                Listen(t2, "Röyksopp", "Eple", info: new { artist_names = new[] { "Röyksopp" } }),
                Listen(t3, "Daft Punk", "Get Lucky (feat. Pharrell Williams)", "Random Access Memories"),
            },
        });
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        var listens = await client.GetFromJsonAsync<JsonElement>("/apis/listenbrainz/1/user/lb-submit/listens");
        var payload = listens.GetProperty("payload");
        Assert.Equal(3, payload.GetProperty("count").GetInt32());
        Assert.Equal(t3, payload.GetProperty("latest_listen_ts").GetInt64());

        var newest = payload.GetProperty("listens")[0].GetProperty("track_metadata");
        Assert.Equal("Get Lucky", newest.GetProperty("track_name").GetString());
        Assert.Equal("Daft Punk feat. Pharrell Williams", newest.GetProperty("artist_name").GetString());
        Assert.Equal(248000, newest.GetProperty("additional_info").GetProperty("duration_ms").GetInt32());

        var count = await client.GetFromJsonAsync<JsonElement>("/apis/listenbrainz/1/user/lb-submit/listen-count");
        Assert.Equal(3, count.GetProperty("payload").GetProperty("count").GetInt32());

        // "Get Lucky" by "Daft Punk feat. Pharrell" and "Get Lucky (feat. Pharrell)" are the same track.
        var trackCount = await _factory.WithScopeAsync(sp =>
            Task.FromResult(sp.GetRequiredService<MuseFM.Data.AppDbContext>().Tracks.Count(t => t.Title == "Get Lucky")));
        Assert.Equal(1, trackCount);
    }

    [Fact]
    public async Task Listens_BetweenMinAndMaxTs_AreATimeRange()
    {
        var (_, token) = await _factory.CreateUserAsync("lb-range");
        var client = _factory.CreateTokenClient(token);
        var times = new[] { Ago(40), Ago(30), Ago(20), Ago(10) };
        var import = await client.PostAsJsonAsync("/apis/listenbrainz/1/submit-listens", new
        {
            listen_type = "import",
            payload = times.Select((t, i) => Listen(t, "Bonobo", $"Track {i}")).ToArray(),
        });
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        // What multi-scrobbler asks before scrobbling: is there already a listen around this time?
        var range = await client.GetFromJsonAsync<JsonElement>(
            $"/apis/listenbrainz/1/user/lb-range/listens?min_ts={times[0]}&max_ts={times[3]}");
        var listens = range.GetProperty("payload").GetProperty("listens");
        Assert.Equal(["Track 2", "Track 1"],
            listens.EnumerateArray().Select(l => l.GetProperty("track_metadata").GetProperty("track_name").GetString()));

        var empty = await client.GetFromJsonAsync<JsonElement>(
            $"/apis/listenbrainz/1/user/lb-range/listens?min_ts={times[3]}&max_ts={times[3] + 600}");
        Assert.Equal(0, empty.GetProperty("payload").GetProperty("count").GetInt32());

        var backwards = await client.GetAsync($"/apis/listenbrainz/1/user/lb-range/listens?min_ts={times[3]}&max_ts={times[0]}");
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
    }

    [Fact]
    public async Task Listens_OmitMissingValuesInsteadOfSendingNull()
    {
        // multi-scrobbler destructures e.g. `artist_mbids = []`; a JS default only covers undefined, so null crashes it.
        var (_, token) = await _factory.CreateUserAsync("lb-nulls");
        var client = _factory.CreateTokenClient(token);
        var import = await client.PostAsJsonAsync("/apis/listenbrainz/1/submit-listens", new
        {
            listen_type = "import",
            payload = new[] { Listen(Ago(5), "Rameses B • Ida", "Overcome", info: new { artist_names = new[] { "Rameses B", "Ida" } }) },
        });
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        var playingNow = await client.PostAsJsonAsync("/apis/listenbrainz/1/submit-listens",
            new { listen_type = "playing_now", payload = new[] { new { track_metadata = new { artist_name = "Ida", track_name = "Overcome" } } } });
        Assert.Equal(HttpStatusCode.OK, playingNow.StatusCode);

        var listens = await client.GetFromJsonAsync<JsonElement>("/apis/listenbrainz/1/user/lb-nulls/listens");
        var playing = await client.GetFromJsonAsync<JsonElement>("/apis/listenbrainz/1/user/lb-nulls/playing-now");
        foreach (var metadata in new[] { listens, playing }.Select(r => r.GetProperty("payload").GetProperty("listens")[0].GetProperty("track_metadata")))
        {
            Assert.DoesNotContain(metadata.EnumerateObject(), p => p.Value.ValueKind == JsonValueKind.Null);
            Assert.DoesNotContain(metadata.GetProperty("additional_info").EnumerateObject(), p => p.Value.ValueKind == JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task SubmitListens_RejectsMissingMetadata()
    {
        var (_, token) = await _factory.CreateUserAsync("lb-invalid");
        var client = _factory.CreateTokenClient(token);

        var noTimestamp = await client.PostAsJsonAsync("/apis/listenbrainz/1/submit-listens", new
        {
            listen_type = "single",
            payload = new[] { new { track_metadata = new { artist_name = "A", track_name = "B" } } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, noTimestamp.StatusCode);

        var noArtist = await client.PostAsJsonAsync("/apis/listenbrainz/1/submit-listens", new
        {
            listen_type = "single",
            payload = new[] { Listen(Ago(1), "", "B") },
        });
        Assert.Equal(HttpStatusCode.BadRequest, noArtist.StatusCode);

        var badJson = await client.PostAsync("/apis/listenbrainz/1/submit-listens", new StringContent("{nope", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);
    }

    [Fact]
    public async Task PlayingNow_IsReturnedButNotStored()
    {
        var (_, token) = await _factory.CreateUserAsync("lb-playing");
        var client = _factory.CreateTokenClient(token);

        var response = await client.PostAsJsonAsync("/apis/listenbrainz/1/submit-listens", new
        {
            listen_type = "playing_now",
            payload = new[] { new { track_metadata = new { artist_name = "Moderat", track_name = "A New Error" } } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var playing = await client.GetFromJsonAsync<JsonElement>("/apis/listenbrainz/1/user/lb-playing/playing-now");
        var listens = playing.GetProperty("payload").GetProperty("listens");
        Assert.Equal(1, listens.GetArrayLength());
        Assert.Equal("A New Error", listens[0].GetProperty("track_metadata").GetProperty("track_name").GetString());

        var count = await client.GetFromJsonAsync<JsonElement>("/apis/listenbrainz/1/user/lb-playing/listen-count");
        Assert.Equal(0, count.GetProperty("payload").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task PrivateProfiles_AreOnlyVisibleWithOwnToken()
    {
        var (_, token) = await _factory.CreateUserAsync("lb-private", isPublic: false);
        var (_, otherToken) = await _factory.CreateUserAsync("lb-other");

        var anonymous = await _factory.CreatePlainClient().GetAsync("/apis/listenbrainz/1/user/lb-private/listens");
        Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);

        var other = await _factory.CreateTokenClient(otherToken).GetAsync("/apis/listenbrainz/1/user/lb-private/listens");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);

        var own = await _factory.CreateTokenClient(token).GetAsync("/apis/listenbrainz/1/user/lb-private/listens");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }
}
