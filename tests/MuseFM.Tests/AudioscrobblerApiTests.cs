using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;

namespace MuseFM.Tests;

public class AudioscrobblerApiTests : IClassFixture<MuseFmFactory>
{
    private const string Endpoint = "/apis/audioscrobbler/2.0/";
    private readonly MuseFmFactory _factory;

    public AudioscrobblerApiTests(MuseFmFactory factory) => _factory = factory;

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values) =>
        new(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)));

    private async Task<string> LoginAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsync(Endpoint, Form(
            ("method", "auth.getMobileSession"), ("username", username), ("password", password),
            ("api_key", "anything"), ("api_sig", "ignored")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", xml.Root!.Attribute("status")!.Value);
        Assert.Equal(username, xml.Root.Element("session")!.Element("name")!.Value);
        return xml.Root.Element("session")!.Element("key")!.Value;
    }

    [Fact]
    public async Task GetMobileSession_AcceptsApiKeyOrAccountPassword()
    {
        var (_, token) = await _factory.CreateUserAsync("as-login", password: "correct-horse");
        var client = _factory.CreatePlainClient();

        var withKey = await LoginAsync(client, "as-login", token);
        var withPassword = await LoginAsync(client, "as-login", "correct-horse");
        Assert.Equal(32, withKey.Length);
        Assert.NotEqual(withKey, withPassword);

        var wrong = await client.PostAsync(Endpoint, Form(("method", "auth.getMobileSession"), ("username", "as-login"), ("password", "wrong"), ("format", "json")));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        var error = await wrong.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, error.GetProperty("error").GetInt32());
    }

    private static async Task<HttpStatusCode> UserInfoAsync(HttpClient client, string sk) =>
        (await client.PostAsync(Endpoint, Form(("method", "user.getInfo"), ("sk", sk)))).StatusCode;

    [Fact]
    public async Task ChangingThePassword_EndsSessionsStartedWithIt()
    {
        // Private, so that reading the profile takes a valid session.
        var (_, token) = await _factory.CreateUserAsync("as-changer", password: "correct-horse", isPublic: false);
        var client = _factory.CreatePlainClient();
        var withPassword = await LoginAsync(client, "as-changer", "correct-horse");
        var withKey = await LoginAsync(client, "as-changer", token);
        Assert.Equal(HttpStatusCode.OK, await UserInfoAsync(client, withPassword));

        var browser = await _factory.SignInAsync("as-changer", "correct-horse");
        var elsewhere = await _factory.SignInAsync("as-changer", "correct-horse");
        var changed = await MuseFmFactory.PostFormAsync(browser, "/settings", "/settings?handler=Password", new()
        {
            ["Password.CurrentPassword"] = "correct-horse", ["Password.NewPassword"] = "battery-staple", ["Password.ConfirmPassword"] = "battery-staple",
        });
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);

        // Whoever knew the old password is out: their scrobbler session and their browser. The API key's session and
        // the browser that made the change carry on.
        Assert.Equal(HttpStatusCode.NotFound, await UserInfoAsync(client, withPassword));
        var scrobble = await client.PostAsync(Endpoint, Form(("method", "track.scrobble"), ("sk", withPassword),
            ("artist", "Intruder"), ("track", "Still Here"), ("timestamp", DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds().ToString())));
        Assert.Equal(HttpStatusCode.Forbidden, scrobble.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await UserInfoAsync(client, withKey));
        Assert.Equal(HttpStatusCode.Redirect, (await elsewhere.GetAsync("/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/settings")).StatusCode);
        await LoginAsync(client, "as-changer", "battery-staple");
    }

    [Fact]
    public async Task APasswordSetByAnAdmin_EndsSessionsStartedWithTheOldOne()
    {
        var (user, token) = await _factory.CreateUserAsync("as-reset", password: "correct-horse", isPublic: false);
        await _factory.CreateUserAsync("as-reset-admin", admin: true);
        var client = _factory.CreatePlainClient();
        var withPassword = await LoginAsync(client, "as-reset", "correct-horse");
        var withKey = await LoginAsync(client, "as-reset", token);

        var admin = await _factory.SignInAsync("as-reset-admin");
        var reset = await MuseFmFactory.PostFormAsync(admin, $"/admin/users/{user.Id}", $"/admin/users/{user.Id}?handler=Password",
            new() { ["Password.NewPassword"] = "battery-staple", ["Password.ConfirmPassword"] = "battery-staple" });
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, await UserInfoAsync(client, withPassword));
        Assert.Equal(HttpStatusCode.OK, await UserInfoAsync(client, withKey));
    }

    [Fact]
    public async Task Scrobble_BatchInXml()
    {
        var (_, token) = await _factory.CreateUserAsync("as-batch");
        var client = _factory.CreatePlainClient();
        var sk = await LoginAsync(client, "as-batch", token);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var response = await client.PostAsync(Endpoint, Form(
            ("method", "track.scrobble"), ("sk", sk),
            ("artist[0]", "Bonobo"), ("track[0]", "Kerala"), ("album[0]", "Migration"), ("timestamp[0]", (now - 600).ToString()), ("duration[0]", "250"),
            ("artist[1]", "Bonobo"), ("track[1]", "Cirrus"), ("albumArtist[1]", "Bonobo"), ("album[1]", "The North Borders"), ("timestamp[1]", (now - 300).ToString()),
            ("artist[2]", ""), ("track[2]", "No Artist"), ("timestamp[2]", (now - 100).ToString())));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var scrobbles = xml.Root!.Element("scrobbles")!;
        Assert.Equal("2", scrobbles.Attribute("accepted")!.Value);
        Assert.Equal("1", scrobbles.Attribute("ignored")!.Value);
        Assert.Equal("1", scrobbles.Elements("scrobble").Last().Element("ignoredMessage")!.Attribute("code")!.Value);

        var count = await client.GetFromJsonAsync<JsonElement>("/apis/listenbrainz/1/user/as-batch/listen-count");
        Assert.Equal(2, count.GetProperty("payload").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Scrobble_SingleInJson_AndDuplicateIsAccepted()
    {
        var (_, token) = await _factory.CreateUserAsync("as-json");
        var client = _factory.CreatePlainClient();
        var sk = await LoginAsync(client, "as-json", token);
        var ts = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60).ToString();

        for (var i = 0; i < 2; i++)
        {
            var response = await client.PostAsync(Endpoint, Form(
                ("method", "track.scrobble"), ("format", "json"), ("sk", sk), ("artist", "Jon Hopkins"), ("track", "Open Eye Signal"), ("timestamp", ts)));
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var scrobbles = json.GetProperty("scrobbles");
            Assert.Equal(1, scrobbles.GetProperty("@attr").GetProperty("accepted").GetInt32());
            // Single scrobble: object, not array (Last.fm quirk).
            Assert.Equal("Open Eye Signal", scrobbles.GetProperty("scrobble").GetProperty("track").GetProperty("#text").GetString());
        }

        var count = await client.GetFromJsonAsync<JsonElement>("/apis/listenbrainz/1/user/as-json/listen-count");
        Assert.Equal(1, count.GetProperty("payload").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task InvalidSessionKey_ReturnsError9()
    {
        var client = _factory.CreatePlainClient();
        var response = await client.PostAsync(Endpoint, Form(("method", "track.scrobble"), ("sk", "deadbeef"), ("artist", "A"), ("track", "B"), ("timestamp", "1700000000")));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("failed", xml.Root!.Attribute("status")!.Value);
        Assert.Equal("9", xml.Root.Element("error")!.Attribute("code")!.Value);
    }

    [Fact]
    public async Task UpdateNowPlaying_ShowsUpInPlayingNow()
    {
        var (_, token) = await _factory.CreateUserAsync("as-nowplaying");
        var client = _factory.CreatePlainClient();
        var sk = await LoginAsync(client, "as-nowplaying", token);

        var response = await client.PostAsync(Endpoint, Form(("method", "track.updateNowPlaying"), ("sk", sk), ("artist", "Four Tet"), ("track", "Baby"), ("duration", "300")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<nowplaying>", await response.Content.ReadAsStringAsync());

        var playing = await client.GetFromJsonAsync<JsonElement>("/apis/listenbrainz/1/user/as-nowplaying/playing-now");
        Assert.Equal("Baby", playing.GetProperty("payload").GetProperty("listens")[0].GetProperty("track_metadata").GetProperty("track_name").GetString());
    }

    [Fact]
    public async Task RevokingApiKey_EndsItsSessions()
    {
        var (user, token) = await _factory.CreateUserAsync("as-revoke");
        var client = _factory.CreatePlainClient();
        var sk = await LoginAsync(client, "as-revoke", token);

        await _factory.WithScopeAsync(async sp =>
        {
            var keys = sp.GetRequiredService<MuseFM.Services.ApiKeyService>();
            foreach (var key in await keys.ListAsync(user.Id)) await keys.RevokeAsync(user.Id, key.Id);
            return 0;
        });

        var response = await client.PostAsync(Endpoint, Form(("method", "track.updateNowPlaying"), ("sk", sk), ("artist", "A"), ("track", "B")));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsError3()
    {
        var response = await _factory.CreatePlainClient().GetAsync(Endpoint + "?method=artist.getInfo&format=json");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, json.GetProperty("error").GetInt32());
    }
}
