using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Tests;

/// <summary>Creating, editing and deleting users in the admin dashboard.</summary>
public class AdminUsersTests : IClassFixture<MuseFmFactory>
{
    private readonly MuseFmFactory _factory;

    public AdminUsersTests(MuseFmFactory factory) => _factory = factory;

    private async Task<HttpClient> AdminAsync(string name)
    {
        await _factory.CreateUserAsync(name, admin: true);
        return await _factory.SignInAsync(name);
    }

    private Task<AppUser?> FindAsync(string name) =>
        _factory.WithScopeAsync(sp => sp.GetRequiredService<UserManager<AppUser>>().FindByNameAsync(name));

    private Task<bool> IsAdminAsync(AppUser user) =>
        _factory.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<AppUser>>();
            return await users.IsInRoleAsync((await users.FindByIdAsync(user.Id))!, Roles.Admin);
        });

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string name, string password = "password123", string? confirm = null,
        string zone = "Europe/Zurich", bool isPublic = false, bool admin = false) =>
        MuseFmFactory.PostFormAsync(client, "/admin/users/new", "/admin/users/new", new()
        {
            ["Input.UserName"] = name,
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = confirm ?? password,
            ["Input.TimeZoneId"] = zone,
            ["Input.IsPublic"] = isPublic ? "true" : "false",
            ["Input.IsAdmin"] = admin ? "true" : "false",
        });

    private static Task<HttpResponseMessage> SaveAccountAsync(HttpClient client, AppUser user, string name, bool isPublic = true, string zone = "UTC", bool admin = false) =>
        MuseFmFactory.PostFormAsync(client, $"/admin/users/{user.Id}", $"/admin/users/{user.Id}?handler=Account", new()
        {
            ["Account.UserName"] = name,
            ["Account.IsPublic"] = isPublic ? "true" : "false",
            ["Account.TimeZoneId"] = zone,
            ["Account.IsAdmin"] = admin ? "true" : "false",
        });

    [Fact]
    public async Task CreatedUsers_CanSignIn()
    {
        var admin = await AdminAsync("users-creator");

        var created = await CreateAsync(admin, "newbie", isPublic: false, admin: true);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var user = (await FindAsync("newbie"))!;
        Assert.Equal($"/admin/users/{user.Id}", created.Headers.Location!.OriginalString);
        Assert.Equal((false, "Europe/Zurich"), (user.IsPublic, user.TimeZoneId));
        Assert.True(await IsAdminAsync(user));
        Assert.Contains("Created newbie.", WebUtility.HtmlDecode(await admin.GetStringAsync(created.Headers.Location.OriginalString)));
        await _factory.SignInAsync("newbie");

        // Mistakes come back on the form, and nothing is created.
        Assert.Contains("Username 'newbie' is already taken.", WebUtility.HtmlDecode(await (await CreateAsync(admin, "newbie")).Content.ReadAsStringAsync()));
        Assert.Contains("Use only letters, digits, dots, dashes and underscores.", await (await CreateAsync(admin, "no spaces")).Content.ReadAsStringAsync());
        Assert.Contains("The passwords do not match.", await (await CreateAsync(admin, "typo-user", confirm: "password124")).Content.ReadAsStringAsync());
        Assert.Contains("Unknown time zone.", await (await CreateAsync(admin, "zone-user", zone: "Mars/Olympus")).Content.ReadAsStringAsync());
        Assert.Contains("at least 8 characters", await (await CreateAsync(admin, "short-user", password: "short")).Content.ReadAsStringAsync());
        foreach (var name in new[] { "no spaces", "typo-user", "zone-user", "short-user" }) Assert.Null(await FindAsync(name));
    }

    [Fact]
    public async Task EditingAUser_ChangesNameSettingsAndRole()
    {
        var admin = await AdminAsync("users-editor");
        var (target, _) = await _factory.CreateUserAsync("edit-me");
        await _factory.CreateUserAsync("taken-name");
        await _factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>()
            .IngestAsync(target.Id, new ScrobbleInput { Title = "Kerala", Artists = ["Bonobo"], ListenedAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds() }));

        var saved = await SaveAccountAsync(admin, target, "edited", isPublic: false, zone: "America/New_York", admin: true);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var user = (await FindAsync("edited"))!;
        Assert.Equal((target.Id, false, "America/New_York"), (user.Id, user.IsPublic, user.TimeZoneId));
        Assert.True(await IsAdminAsync(user));
        Assert.Contains("The profile is now at /u/edited", await admin.GetStringAsync($"/admin/users/{user.Id}"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/u/edited")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/u/edit-me")).StatusCode);

        var clash = await SaveAccountAsync(admin, user, "taken-name");
        Assert.Equal(HttpStatusCode.OK, clash.StatusCode);
        Assert.Contains("Username 'taken-name' is already taken.", WebUtility.HtmlDecode(await clash.Content.ReadAsStringAsync()));
        Assert.Equal("edited", (await _factory.WithScopeAsync(sp => sp.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(target.Id)))!.UserName);

        await SaveAccountAsync(admin, user, "edited", admin: false);
        Assert.False(await IsAdminAsync(user));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/admin/users/no-such-id")).StatusCode);
    }

    [Fact]
    public async Task SettingAPassword_AndDeletingAUser()
    {
        var admin = await AdminAsync("users-deleter");
        var (target, _) = await _factory.CreateUserAsync("forgetful");
        await _factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>()
            .IngestAsync(target.Id, new ScrobbleInput { Title = "Archangel", Artists = ["Burial"], ListenedAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds() }));
        var page = $"/admin/users/{target.Id}";

        var mismatch = await MuseFmFactory.PostFormAsync(admin, page, $"{page}?handler=Password",
            new() { ["Password.NewPassword"] = "new-password-1", ["Password.ConfirmPassword"] = "new-password-2" });
        Assert.Contains("The passwords do not match.", await mismatch.Content.ReadAsStringAsync());
        var reset = await MuseFmFactory.PostFormAsync(admin, page, $"{page}?handler=Password",
            new() { ["Password.NewPassword"] = "new-password-1", ["Password.ConfirmPassword"] = "new-password-1" });
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        await _factory.SignInAsync("forgetful", "new-password-1");

        var unconfirmed = await MuseFmFactory.PostFormAsync(admin, page, $"{page}?handler=Delete", new() { ["confirmName"] = "forgetfu" });
        Assert.Contains("Type forgetful to confirm.", await unconfirmed.Content.ReadAsStringAsync());
        Assert.NotNull(await FindAsync("forgetful"));

        var deleted = await MuseFmFactory.PostFormAsync(admin, page, $"{page}?handler=Delete", new() { ["confirmName"] = "forgetful" });
        Assert.Equal("/admin", deleted.Headers.Location!.OriginalString);
        Assert.Contains("Deleted forgetful and all of their data.", await admin.GetStringAsync("/admin"));
        Assert.Null(await FindAsync("forgetful"));
        Assert.Equal(0, await _factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Scrobbles.CountAsync(s => s.UserId == target.Id)));
    }

    [Fact]
    public async Task Admins_CantDemoteOrDeleteThemselves()
    {
        var admin = await AdminAsync("users-self");
        var me = (await FindAsync("users-self"))!;
        var page = $"/admin/users/{me.Id}";
        Assert.Contains("disabled=\"disabled\"", await admin.GetStringAsync(page));

        await SaveAccountAsync(admin, me, "users-self", admin: false);
        Assert.True(await IsAdminAsync(me));

        await MuseFmFactory.PostFormAsync(admin, page, $"{page}?handler=Delete", new() { ["confirmName"] = "users-self" });
        await MuseFmFactory.PostFormAsync(admin, page, $"{page}?handler=Password",
            new() { ["Password.NewPassword"] = "new-password-1", ["Password.ConfirmPassword"] = "new-password-1" });
        Assert.NotNull(await FindAsync("users-self"));
        await _factory.SignInAsync("users-self");

        // Renaming oneself keeps the session going under the new name.
        await SaveAccountAsync(admin, me, "users-self-renamed");
        Assert.Contains("href=\"/u/users-self-renamed\"", await admin.GetStringAsync("/admin"));
    }

    [Fact]
    public async Task DemotedAdmins_LoseTheAdminPagesAtOnce()
    {
        var boss = await AdminAsync("users-boss");
        var (target, _) = await _factory.CreateUserAsync("users-demoted", admin: true);
        var demoted = await _factory.SignInAsync("users-demoted");
        Assert.Equal(HttpStatusCode.OK, (await demoted.GetAsync("/admin")).StatusCode);

        await SaveAccountAsync(boss, target, "users-demoted", admin: false);
        Assert.Contains("users-demoted needs to sign in again.", await boss.GetStringAsync($"/admin/users/{target.Id}"));

        // The role travels in the sign-in cookie: the one from before must not open the admin pages any more, or the
        // demoted admin could still make another admin account.
        Assert.Equal(HttpStatusCode.Redirect, (await demoted.GetAsync("/admin")).StatusCode);
        var attempt = await demoted.PostAsync("/admin/users/new", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.UserName"] = "users-backdoor", ["Input.Password"] = "password123", ["Input.ConfirmPassword"] = "password123",
            ["Input.TimeZoneId"] = "UTC", ["Input.IsAdmin"] = "true",
        }));
        Assert.NotEqual(HttpStatusCode.OK, attempt.StatusCode);
        Assert.Null(await FindAsync("users-backdoor"));

        // Signed in again, they are an ordinary user. The admin who made the change is not affected.
        var again = await _factory.SignInAsync("users-demoted");
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await again.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await boss.GetAsync("/admin")).StatusCode);

        // The other way round a promotion needs a new sign-in as well, then it applies.
        await SaveAccountAsync(boss, target, "users-demoted", admin: true);
        Assert.Equal(HttpStatusCode.Redirect, (await again.GetAsync("/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await _factory.SignInAsync("users-demoted")).GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task ANewPasswordOrADeletedAccount_EndsSignInsAtOnce()
    {
        var boss = await AdminAsync("users-locksmith");
        var (target, _) = await _factory.CreateUserAsync("users-locked-out", admin: true);
        var session = await _factory.SignInAsync("users-locked-out");
        var page = $"/admin/users/{target.Id}";

        var reset = await MuseFmFactory.PostFormAsync(boss, page, $"{page}?handler=Password",
            new() { ["Password.NewPassword"] = "new-password-1", ["Password.ConfirmPassword"] = "new-password-1" });
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Contains("users-locked-out is signed out everywhere", await boss.GetStringAsync(page));
        Assert.Equal(HttpStatusCode.Redirect, (await session.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await session.GetAsync("/settings")).StatusCode);

        var fresh = await _factory.SignInAsync("users-locked-out", "new-password-1");
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/admin")).StatusCode);
        await MuseFmFactory.PostFormAsync(boss, page, $"{page}?handler=Delete", new() { ["confirmName"] = "users-locked-out" });
        Assert.Null(await FindAsync("users-locked-out"));
        Assert.Equal(HttpStatusCode.Redirect, (await fresh.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task NonAdmins_AreKeptOut()
    {
        var (victim, _) = await _factory.CreateUserAsync("users-victim");
        await _factory.CreateUserAsync("users-regular");
        var regular = await _factory.SignInAsync("users-regular");

        Assert.Equal(HttpStatusCode.Redirect, (await regular.GetAsync("/admin/users/new")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await regular.GetAsync($"/admin/users/{victim.Id}")).StatusCode);
        var token = await MuseFmFactory.GetAntiforgeryTokenAsync(regular, "/settings");
        var attempt = await regular.PostAsync($"/admin/users/{victim.Id}?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["confirmName"] = "users-victim",
        }));
        Assert.Equal(HttpStatusCode.Redirect, attempt.StatusCode);
        Assert.NotNull(await FindAsync("users-victim"));
    }
}
