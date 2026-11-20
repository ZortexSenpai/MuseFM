using System.Net;
using System.Security.Claims;
using MuseFM.Data.Entities;
using MuseFM.Pages;
using MuseFM.Services;
using MuseFM.Services.Metadata;

namespace MuseFM.Tests;

public class PreferencesTests
{
    private static async Task<HttpClient> RegisterAsync(MuseFmFactory factory, string username)
    {
        var client = factory.CreatePlainClient();
        var token = await MuseFmFactory.GetAntiforgeryTokenAsync(client, "/register");
        var response = await client.PostAsync("/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["Input.Username"] = username,
            ["Input.Password"] = "password123", ["Input.ConfirmPassword"] = "password123",
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private static async Task SaveAsync(HttpClient client, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await MuseFmFactory.GetAntiforgeryTokenAsync(client, "/settings/preferences");
        var response = await client.PostAsync("/settings/preferences", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Preferences_ChangeHowPagesLook_AndSurviveANewLogin()
    {
        using var factory = new MuseFmFactory(null);
        var client = await RegisterAsync(factory, "picky");

        // Visitors and new users get the defaults: follow the OS, violet, 24-hour clock, 30 days.
        var anonymous = await factory.CreatePlainClient().GetStringAsync("/");
        Assert.Contains("data-accent=\"violet\"", anonymous);
        Assert.DoesNotContain("data-theme=\"dark\"", anonymous);
        Assert.Contains("<meta name=\"color-scheme\" content=\"light dark\">", anonymous);

        var user = (await factory.WithScopeAsync(sp => sp.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>().FindByNameAsync("picky")))!;
        var lastYearish = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-3).AddHours(21).AddMinutes(30), TimeSpan.Zero).ToUnixTimeSeconds();
        await factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>().IngestAsync(user.Id,
            new ScrobbleInput { Title = "Song", Artists = ["Band"], Album = "Record", ListenedAt = lastYearish }));
        // Artwork needs an active connector; nothing is fetched while rendering these pages.
        await factory.Services.GetRequiredService<ConnectorSettings>().SaveAsync(
            new ConnectorConfig(ConnectorKind.Navidrome, true, 0, "http://navidrome.invalid", "u", "p"));
        Assert.Contains("/media/album/", await client.GetStringAsync("/u/picky"));

        await SaveAsync(client, new()
        {
            ["Input.Theme"] = "dark", ["Input.Accent"] = "teal", ["Input.Clock12"] = "true", ["Input.WeekStartsSunday"] = "true",
            ["Input.ShowArtwork"] = "false", ["Input.DefaultRange"] = "year",
        });

        var settings = await client.GetStringAsync("/settings/preferences");
        Assert.Contains("Preferences saved.", settings);
        Assert.Contains("<html lang=\"en\" data-theme=\"dark\" data-accent=\"teal\">", settings);
        Assert.Contains("value=\"teal\" checked=\"checked\"", settings);

        var profile = await client.GetStringAsync("/u/picky");
        Assert.Contains("<h2>12 months</h2>", profile); // default period
        Assert.Contains("9:30 PM", profile); // 12-hour clock (full time in the tooltip)
        Assert.DoesNotContain("/media/", profile); // artwork switched off

        var insights = await client.GetStringAsync("/u/picky/insights");
        Assert.True(insights.IndexOf("class=\"hm-row\">Sun<", StringComparison.Ordinal) < insights.IndexOf("class=\"hm-row\">Mon<", StringComparison.Ordinal));
        Assert.Contains("9p", insights); // hour axis

        // Unknown values fall back to the defaults.
        await SaveAsync(client, new() { ["Input.Theme"] = "neon", ["Input.Accent"] = "\"><script>", ["Input.DefaultRange"] = "forever" });
        Assert.Contains("<html lang=\"en\" data-accent=\"violet\">", await client.GetStringAsync("/settings/preferences"));

        // The other themes go by name, and tell the browser whether they are light or dark.
        await SaveAsync(client, new() { ["Input.Theme"] = "midnight", ["Input.Accent"] = "blue" });
        var midnight = await client.GetStringAsync("/settings/preferences");
        Assert.Contains("<html lang=\"en\" data-theme=\"midnight\" data-accent=\"blue\">", midnight);
        Assert.Contains("<meta name=\"color-scheme\" content=\"dark\">", midnight);
        Assert.Contains("value=\"midnight\" data-scheme=\"dark\" checked=\"checked\"", midnight);
        await SaveAsync(client, new() { ["Input.Theme"] = "kraft" });
        var kraft = await client.GetStringAsync("/settings/preferences");
        Assert.Contains("data-theme=\"kraft\"", kraft);
        Assert.Contains("<meta name=\"color-scheme\" content=\"light\">", kraft);

        // Saved in the account: a new login on another device gets them.
        await SaveAsync(client, new() { ["Input.Theme"] = "light", ["Input.Accent"] = "amber" });
        var other = factory.CreatePlainClient();
        var token = await MuseFmFactory.GetAntiforgeryTokenAsync(other, "/login");
        await other.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["Input.Username"] = "picky", ["Input.Password"] = "password123",
        }));
        Assert.Contains("data-theme=\"light\" data-accent=\"amber\"", await other.GetStringAsync("/u/picky"));
    }

    [Fact]
    public void Claim_RoundTrips()
    {
        var user = new AppUser { Theme = "dark", Accent = "rose", Clock12 = true, WeekStartsSunday = true, HideArtwork = true, DefaultRange = "all" };
        var prefs = ViewerPreferences.From(user);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ViewerPreferences.ClaimType, prefs.ToClaim())]));
        Assert.Equal(prefs, ViewerPreferences.FromClaims(principal));
        Assert.Equal(new ViewerPreferences(ThemeMode.Dark, "rose", true, DayOfWeek.Sunday, false, StatRange.All), prefs);
        Assert.Equal(ViewerPreferences.Default, ViewerPreferences.FromClaims(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public async Task Themes_ParseByName_AndHaveStylesInSiteCss()
    {
        using var factory = new MuseFmFactory(null);
        var css = await factory.CreatePlainClient().GetStringAsync("/css/site.css");
        foreach (var (mode, _, _) in ViewerPreferences.Themes)
        {
            var id = ViewerPreferences.ThemeId(mode);
            Assert.Equal(mode, ViewerPreferences.ParseTheme(id));
            Assert.Equal(mode, ViewerPreferences.ParseTheme(id.ToUpperInvariant()));
            Assert.Equal(mode, ViewerPreferences.FromClaims(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ViewerPreferences.ClaimType, (ViewerPreferences.Default with { Theme = mode }).ToClaim())]))).Theme);
            if (mode != ThemeMode.System) Assert.Contains($"[data-theme=\"{id}\"]", css);
        }
        Assert.Equal(Enum.GetValues<ThemeMode>(), ViewerPreferences.Themes.Select(t => t.Mode));
        Assert.Equal(ThemeMode.System, ViewerPreferences.ParseTheme("3")); // names only, not enum numbers
        Assert.Equal(ThemeMode.System, ViewerPreferences.ParseTheme(null));
        Assert.Equal(("light dark", "light", "dark"), (ViewerPreferences.ColorSchemeOf(ThemeMode.System),
            ViewerPreferences.ColorSchemeOf(ThemeMode.Newsprint), ViewerPreferences.ColorSchemeOf(ThemeMode.Black)));
    }

    [Fact]
    public void Clock_FollowsThePreference()
    {
        Assert.Equal(("21:00", "23:00–00:00", "09"), (Ui.Hour(21), Ui.HourSpan(23), Ui.HourTick(9)));
        ViewerPreferences.Current = ViewerPreferences.Default with { Clock12 = true };
        try
        {
            Assert.Equal(("9 PM", "12 AM", "11 PM–12 AM", "9p", "12a"), (Ui.Hour(21), Ui.Hour(0), Ui.HourSpan(23), Ui.HourTick(21), Ui.HourTick(0)));
        }
        finally
        {
            ViewerPreferences.Current = ViewerPreferences.Default;
        }
    }

    [Fact]
    public async Task Weeks_CanStartOnSunday()
    {
        Assert.Equal((0, 6, 1), (LocalClock.Weekday(DayOfWeek.Sunday, DayOfWeek.Sunday), LocalClock.Weekday(DayOfWeek.Saturday, DayOfWeek.Sunday), LocalClock.Weekday(DayOfWeek.Monday, DayOfWeek.Sunday)));

        using var factory = new MuseFmFactory(null);
        var (user, _) = await factory.CreateUserAsync("sunday-user");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>().IngestAsync(user.Id,
            new ScrobbleInput { Title = "Song", Artists = ["Band"], ListenedAt = new DateTimeOffset(today.ToDateTime(new TimeOnly(0, 5)), TimeSpan.Zero).ToUnixTimeSeconds() }));

        var (year, patterns) = await factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
            .GetInsightsAsync(user.Id, StatPeriod.Create(StatRange.Month, TimeZoneInfo.Utc), TimeZoneInfo.Utc, DayOfWeek.Sunday));
        Assert.Equal(DayOfWeek.Sunday, year.Days[0].Day.DayOfWeek);
        Assert.Equal(1, patterns.ByWeekday[(int)today.DayOfWeek]); // Sunday-based index equals DayOfWeek

        var week = await factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
            .CompareAsync(user.Id, ComparePeriod.Week, 0, TimeZoneInfo.Utc, DayOfWeek.Sunday));
        Assert.Equal(DayOfWeek.Sunday, DateTimeOffset.FromUnixTimeSeconds(week.Current.From).UtcDateTime.DayOfWeek);
        Assert.Equal(["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"], week.Buckets.Select(b => b.Label));
        Assert.Equal(1, week.CurrentTotals.Scrobbles);
    }
}
