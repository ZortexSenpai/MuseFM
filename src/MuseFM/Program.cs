using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MuseFM;
using MuseFM.Api;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;
using MuseFM.Services.Import;
using MuseFM.Services.Metadata;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MuseFmOptions>(builder.Configuration.GetSection(MuseFmOptions.Section));
// Resolved lazily so tests and hosting environments can override the data directory.
builder.Services.AddSingleton(sp =>
    new DataPaths(sp.GetRequiredService<IConfiguration>()[$"{MuseFmOptions.Section}:DataDirectory"] ?? "data"));

// Also registers AppDbContext as a scoped service.
builder.Services.AddDbContextFactory<AppDbContext>((sp, options) =>
    options.UseSqlite($"Data Source={sp.GetRequiredService<DataPaths>().Database}"));

// Persist cookie-encryption keys in the data volume so logins survive container restarts.
builder.Services.AddDataProtection().SetApplicationName("MuseFM");
builder.Services.AddOptions<KeyManagementOptions>()
    .Configure<DataPaths, ILoggerFactory>((options, paths, loggers) =>
        options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(paths.Keys), loggers));

builder.Services
    .AddIdentity<AppUser, IdentityRole>(options =>
    {
        options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._";
        options.User.RequireUniqueEmail = false;
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 10;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddUserManager<AppUserManager>()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "musefm.auth";
    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
    options.AccessDeniedPath = "/login";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
    options.Events.OnValidatePrincipal = SecurityStamps.ValidatePrincipalAsync;
});

// Sign-ins that a password change, a new role or a deleted account made invalid end at once (see SecurityStamps).
// This check against the database is the fallback for changes made before a restart.
builder.Services.AddSingleton<SecurityStamps>();
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Roles.AdminPolicy, policy => policy.RequireRole(Roles.Admin));

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Settings");
    options.Conventions.AuthorizeFolder("/Admin", Roles.AdminPolicy);
});

// MuseFM is usually run behind a reverse proxy (Traefik, Caddy, nginx...). Its X-Forwarded-* headers say which address
// and scheme the visitor used; with MuseFM:TrustedProxies set, they are only believed when they come from there.
builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<MuseFmOptions>>((options, musefm) =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var entry in (musefm.Value.TrustedProxies ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (System.Net.IPNetwork.TryParse(entry, out var network)) options.KnownIPNetworks.Add(network);
        else if (System.Net.IPAddress.TryParse(entry, out var address)) options.KnownProxies.Add(address);
        else throw new InvalidOperationException($"MuseFM:TrustedProxies: \"{entry}\" is not an IP address or a network such as 172.18.0.0/16.");
    }
});

builder.Services.AddScoped<ScrobbleService>();
builder.Services.AddScoped<ApiKeyService>();
builder.Services.AddScoped<StatsService>();
builder.Services.AddScoped<InsightsService>();
builder.Services.AddScoped<FeedbackService>();
builder.Services.AddScoped<ObsessionService>();
builder.Services.AddScoped<CatalogEditor>();
builder.Services.AddScoped<SocialService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<ProfileImages>();
builder.Services.AddSingleton<NowPlayingStore>();
builder.Services.AddSingleton<ImportQueue>();
builder.Services.AddHostedService<ImportWorker>();
builder.Services.AddHttpClient<LastFmClient>(client =>
{
    client.BaseAddress = new Uri("https://ws.audioscrobbler.com/2.0/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MuseFM/1.0");
});
// Users type in the address of their Maloja server. Unless they may reach the server's own network (see ImportWorker),
// the request goes through a client that only connects to public addresses.
foreach (var name in new[] { MalojaClient.PublicClientName, MalojaClient.AnyAddressClientName })
{
    var maloja = builder.Services.AddHttpClient(name, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MuseFM/1.0");
    });
    if (name == MalojaClient.PublicClientName) maloja.ConfigurePrimaryHttpMessageHandler(PublicInternet.CreateHandler);
}

// Metadata connectors: covers, artist photos and biographies from Navidrome, Maloja, ListenBrainz and Last.fm.
builder.Services.AddSingleton<ConnectorSettings>();
builder.Services.AddSingleton<MetadataService>();
builder.Services.AddSingleton<MetadataFetchJob>();
builder.Services.AddSingleton<ScrobbleMetadataFetcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScrobbleMetadataFetcher>());
builder.Services.AddScoped<IMetadataProvider, NavidromeProvider>();
builder.Services.AddScoped<IMetadataProvider, MalojaProvider>();
builder.Services.AddScoped<IMetadataProvider, ListenBrainzProvider>();
builder.Services.AddScoped<IMetadataProvider, LastFmProvider>();
builder.Services.AddHttpClient(MetadataHttp.ClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(MetadataHttp.UserAgent);
});

var app = builder.Build();

await DbInitializer.InitializeAsync(app.Services);
await app.Services.GetRequiredService<ConnectorSettings>().LoadAsync();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/status/500");
    // Only sent over HTTPS (as reported by the reverse proxy): browsers then stop trying plain HTTP for this address.
    app.UseHsts();
}

// Browser-side safety nets, for every response. The policy leaves scripts and styles alone (pages still use inline
// handlers) but closes what an injected tag could otherwise do: change the base address, post a form elsewhere, embed
// a plugin, or frame the site on another one.
app.Use((context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "same-origin";
    headers.ContentSecurityPolicy = "base-uri 'self'; form-action 'self'; frame-ancestors 'self'; object-src 'none'";
    return next(context);
});
app.UseStatusCodePagesWithReExecute("/status/{0}");
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
// The viewer's display preferences ride along in the auth cookie; expose them to views and formatting helpers.
app.Use((context, next) =>
{
    ViewerPreferences.Current = ViewerPreferences.FromClaims(context.User);
    return next(context);
});
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Text("ok"));
app.MapListenBrainzApi();
app.MapAudioscrobblerApi();
app.MapExportEndpoints();
app.MapMediaEndpoints();
app.MapRazorPages();

app.Run();

public partial class Program;
