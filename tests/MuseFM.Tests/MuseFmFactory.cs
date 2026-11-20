using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;
using MuseFM.Services.Import;

namespace MuseFM.Tests;

/// <summary>Runs the real app against a throwaway SQLite database in a temp directory.</summary>
public sealed partial class MuseFmFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;
    private readonly Action<IServiceCollection>? _services;

    // xUnit class fixtures need exactly one public constructor.
    public MuseFmFactory() : this(null)
    {
    }

    internal MuseFmFactory(Dictionary<string, string?>? settings, Action<IServiceCollection>? services = null)
    {
        _settings = settings ?? [];
        _services = services;
    }

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "musefm-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("MuseFM:DataDirectory", DataDirectory);
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);
        if (_services is not null) builder.ConfigureTestServices(_services);
    }

    public HttpClient CreatePlainClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>Creates a user directly through Identity and returns a fresh API key for it.</summary>
    public async Task<(AppUser User, string Token)> CreateUserAsync(string username, string password = "password123", bool isPublic = true, bool admin = false)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = username, IsPublic = isPublic };
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
        if (admin) await users.AddToRoleAsync(user, Roles.Admin);
        var (_, token) = await scope.ServiceProvider.GetRequiredService<ApiKeyService>().CreateAsync(user.Id, "test");
        return (user, token);
    }

    public HttpClient CreateTokenClient(string token)
    {
        var client = CreatePlainClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", token);
        return client;
    }

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>Queues an import job like the import page does and waits for it to finish.</summary>
    public async Task<ImportJob> RunImportAsync(ImportJob job)
    {
        await WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.ImportJobs.Add(job);
            await db.SaveChangesAsync();
            sp.GetRequiredService<ImportQueue>().Enqueue(job.Id);
            return 0;
        });

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var current = await WithScopeAsync(sp =>
                sp.GetRequiredService<AppDbContext>().ImportJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id));
            if (current.Status is ImportStatus.Completed or ImportStatus.Failed) return current;
            await Task.Delay(100);
        }

        throw new TimeoutException("Import job did not finish.");
    }

    /// <summary>Signs in through the login form.</summary>
    public async Task<HttpClient> SignInAsync(string username, string password = "password123")
    {
        var client = CreatePlainClient();
        var token = await GetAntiforgeryTokenAsync(client, "/login");
        var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["Input.Username"] = username, ["Input.Password"] = password,
        }));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    /// <summary>Posts a form from <paramref name="page"/> with its antiforgery token.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string page, string action, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await GetAntiforgeryTokenAsync(client, page);
        return await client.PostAsync(action, new FormUrlEncodedContent(fields));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    /// <summary>GETs a page and returns the antiforgery token from its first form.</summary>
    public static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var match = AntiforgeryField().Match(html);
        Assert.True(match.Success, $"No antiforgery token on {url}");
        return match.Groups[1].Value;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
