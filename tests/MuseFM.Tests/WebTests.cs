using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace MuseFM.Tests;

public class WebTests
{
    private static async Task<HttpResponseMessage> RegisterAsync(HttpClient client, string username, string password = "password123", string tokenPage = "/register")
    {
        var token = await MuseFmFactory.GetAntiforgeryTokenAsync(client, tokenPage);
        return await client.PostAsync("/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Username"] = username,
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = password,
        }));
    }

    [Fact]
    public async Task FirstUserBecomesAdmin_AndRegistrationCanBeDisabled()
    {
        using var factory = new MuseFmFactory(new() { ["MuseFM:AllowRegistration"] = "false" });

        // Registration stays open until the first account exists.
        var admin = factory.CreatePlainClient();
        var response = await RegisterAsync(admin, "admin");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/u/admin", response.Headers.Location!.OriginalString);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/u/admin")).StatusCode);

        // The register page shows no form any more; a crafted POST must still be refused.
        var second = factory.CreatePlainClient();
        Assert.DoesNotContain("Input.Password", await second.GetStringAsync("/register"));
        var blocked = await RegisterAsync(second, "second", tokenPage: "/login");
        Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
        Assert.Contains("Registration is disabled", await blocked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync("/u/second")).StatusCode);
    }

    [Fact]
    public async Task Pages_RenderForSignedInUser()
    {
        using var factory = new MuseFmFactory(null);
        var client = factory.CreatePlainClient();
        await RegisterAsync(client, "viewer");
        await RegisterAsync(factory.CreatePlainClient(), "regular");

        foreach (var url in new[]
                 {
                     "/u/viewer", "/u/viewer?range=week", "/u/viewer?range=year", "/u/viewer?range=all",
                     "/u/viewer/scrobbles", "/u/viewer/artists", "/u/viewer/albums?range=all", "/u/viewer/tracks",
                     "/settings", "/settings/api-keys", "/settings/import", "/settings/export",
                     "/search", "/search?q=reg", "/settings/export/maloja", "/settings/export/lastfm", "/settings/export/listenbrainz",
                     "/u/viewer/loved", "/u/viewer/friends", "/u/viewer/taste", "/u/viewer/taste?with=regular", "/u/regular/taste", "/feed",
                     "/admin/catalog", "/admin/catalog?kind=tracks&q=x",
                 })
        {
            var response = await client.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)response.StatusCode}");
        }

        // Non-admins cannot open the admin page.
        var regular = factory.CreatePlainClient();
        var login = await MuseFmFactory.GetAntiforgeryTokenAsync(regular, "/login");
        await regular.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = login, ["Input.Username"] = "regular", ["Input.Password"] = "password123",
        }));
        var adminPage = await regular.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, adminPage.StatusCode);
    }

    [Fact]
    public async Task CreatingApiKeyInSettings_ShowsAWorkingKeyOnce()
    {
        using var factory = new MuseFmFactory(null);
        var client = factory.CreatePlainClient();
        await RegisterAsync(client, "keyholder");

        var token = await MuseFmFactory.GetAntiforgeryTokenAsync(client, "/settings/api-keys");
        var response = await client.PostAsync("/settings/api-keys?handler=Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["name"] = "Navidrome",
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var key = System.Text.RegularExpressions.Regex.Match(html, "<code id=\"new-token\">([^<]+)</code>").Groups[1].Value;
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", key);

        var validate = await factory.CreatePlainClient().GetStringAsync($"/apis/listenbrainz/1/validate-token?token={key}");
        Assert.Contains("\"valid\":true", validate);

        // Reloading the page lists the key but never shows it again.
        var reloaded = await client.GetStringAsync("/settings/api-keys");
        Assert.Contains("Navidrome", reloaded);
        Assert.DoesNotContain(key, reloaded);
    }

    [Fact]
    public async Task PrivateProfiles_AreHiddenFromOthers()
    {
        using var factory = new MuseFmFactory(null);
        await factory.CreateUserAsync("first-admin");
        await factory.CreateUserAsync("hidden", isPublic: false);

        var anonymous = factory.CreatePlainClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/u/hidden")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/u/nobody")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/u/first-admin/whatever")).StatusCode);
        Assert.DoesNotContain("/u/hidden\"", await anonymous.GetStringAsync("/search"));
        Assert.Contains("/u/first-admin\"", await anonymous.GetStringAsync("/search"));
    }

    [Fact]
    public async Task SettingsRequireLogin()
    {
        using var factory = new MuseFmFactory(null);
        var response = await factory.CreatePlainClient().GetAsync("/settings/api-keys");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task Responses_CarryTheSecurityHeaders()
    {
        using var factory = new MuseFmFactory(null);
        var client = factory.CreatePlainClient();

        foreach (var url in new[] { "/login", "/css/site.css", "/apis/listenbrainz/1/validate-token?token=x", "/no-such-page" })
        {
            var response = await client.GetAsync(url);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("same-origin", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("base-uri 'self'; form-action 'self'; frame-ancestors 'self'; object-src 'none'",
                response.Headers.GetValues("Content-Security-Policy").Single());
        }
    }

    /// <summary>Gives requests the remote address named in their X-Test-Remote header; the test server has none.</summary>
    private sealed class RemoteAddresses : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Remote"].ToString());
                return following(context);
            });
            next(app);
        };
    }

    [Theory]
    [InlineData("10.1.2.3", true)] // in the trusted network
    [InlineData("192.0.2.7", true)] // the trusted address
    [InlineData("::ffff:10.1.2.3", true)] // the same proxy, as a dual-stack socket reports it
    [InlineData("203.0.113.9", false)]
    [InlineData("192.0.2.8", false)]
    public async Task ForwardedHeaders_AreOnlyBelieved_FromTrustedProxies(string remote, bool trusted)
    {
        using var factory = new MuseFmFactory(new() { ["MuseFM:TrustedProxies"] = "10.0.0.0/8, 192.0.2.7" },
            services => services.AddSingleton<IStartupFilter, RemoteAddresses>());
        await factory.CreateUserAsync("proxied");

        var request = new HttpRequestMessage(HttpMethod.Get, "/apis/audioscrobbler/2.0/?method=user.getInfo&user=proxied&format=json");
        request.Headers.Add("X-Test-Remote", remote);
        request.Headers.Add("X-Forwarded-Host", "music.example");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var body = await (await factory.CreatePlainClient().SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains(trusted ? "\"https://music.example/u/proxied\"" : "\"http://localhost/u/proxied\"", body);
    }

    [Fact]
    public async Task ForwardedHeaders_AreBelievedFromAnyone_UnlessProxiesAreNamed()
    {
        using var factory = new MuseFmFactory(null);
        await factory.CreateUserAsync("proxied");

        var request = new HttpRequestMessage(HttpMethod.Get, "/apis/audioscrobbler/2.0/?method=user.getInfo&user=proxied&format=json");
        request.Headers.Add("X-Forwarded-Host", "music.example");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var body = await (await factory.CreatePlainClient().SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains("\"https://music.example/u/proxied\"", body);
    }
}
