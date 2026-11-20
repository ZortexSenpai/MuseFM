using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuseFM.Data.Entities;
using MuseFM.Services;
using MuseFM.Services.Metadata;

namespace MuseFM.Pages.Admin;

public sealed class MetadataModel(
    ConnectorSettings settings,
    MetadataService metadata,
    MetadataFetchJob fetchJob,
    IEnumerable<IMetadataProvider> providers,
    ILogger<MetadataModel> logger) : PageModel
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    /// <param name="SecretLabel">Null when the connector has no secret.</param>
    public sealed record ConnectorInfo(string Name, string Provides, string? SecretLabel, bool HasUrl);

    /// <summary>The cache card, which is also reloaded on its own while a fetch runs.</summary>
    public sealed record CacheView(MetadataCacheStats Stats, FetchProgress? Fetch, bool CanFetch);

    public sealed class ConnectorForm
    {
        public bool Enabled { get; set; }
        public string? BaseUrl { get; set; }
        public string? Username { get; set; }

        /// <summary>Empty keeps the saved secret; secrets are never sent back to the browser.</summary>
        public string? Secret { get; set; }

        public bool ClearSecret { get; set; }
    }

    public ConnectorSettings Settings => settings;
    public CacheView Cache { get; private set; } = null!;

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public bool StatusIsError { get; set; }

    public static ConnectorInfo Describe(ConnectorKind kind) => kind switch
    {
        ConnectorKind.Navidrome => new("Navidrome",
            "Album covers from your own library, plus artist images and biographies from Navidrome's agents.", "Password", HasUrl: true),
        ConnectorKind.Maloja => new("Maloja",
            "Artist images and album covers from your Maloja server: pictures you added there and the ones Maloja found itself.",
            null, HasUrl: true),
        ConnectorKind.ListenBrainz => new("ListenBrainz",
            "Album covers from the Cover Art Archive, biographies from Wikipedia and artist photos from Wikimedia Commons, matched through MusicBrainz.",
            "User token", HasUrl: false),
        _ => new("Last.fm", "Artist biographies and album covers. Last.fm no longer provides artist photos.", "API key", HasUrl: false),
    };

    public async Task OnGetAsync(CancellationToken ct) => Cache = await LoadCacheAsync(ct);

    public async Task<IActionResult> OnGetCacheAsync(CancellationToken ct) => Partial("_MetadataCache", await LoadCacheAsync(ct));

    public async Task<IActionResult> OnPostSaveAsync(ConnectorKind kind, ConnectorForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid || !Enum.IsDefined(kind)) return BadRequest();
        var (config, error) = Apply(kind, form);
        if (error is not null) return Message(error, error: true);

        await settings.SaveAsync(config, ct);
        // Items that found nothing before may find something now.
        await metadata.RetryMissingAsync(ct);

        var name = Describe(kind).Name;
        if (!config.Enabled) return Message($"{name} saved. It is disabled.");
        if (settings.Missing(config) is { } missing) return Message($"{name} saved, but it stays inactive until it has {missing}.", error: true);

        var (ok, result) = await TestAsync(config, ct);
        return ok
            ? Message($"{name} saved and enabled. {result}")
            : Message($"{name} saved, but the connection test failed: {result}", error: true);
    }

    public async Task<IActionResult> OnPostMoveAsync(ConnectorKind kind, int delta, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind)) return BadRequest();
        await settings.MoveAsync(kind, Math.Sign(delta), ct);
        return RedirectToPage();
    }

    public IActionResult OnPostFetch(FetchMode mode)
    {
        if (!Enum.IsDefined(mode)) return BadRequest();
        if (!settings.AnyActive) return Message("Enable and set up a connector first.", error: true);
        if (!fetchJob.TryStart(mode)) return Message("A fetch is already running.", error: true);
        return Message(mode == FetchMode.Missing
            ? "Fetching metadata for every artist and album that is still missing some. It continues in the background if you leave this page."
            : "Fetching metadata for every artist and album again. It continues in the background if you leave this page.");
    }

    public IActionResult OnPostStop()
    {
        fetchJob.Stop();
        return Message("The fetch was stopped. Lookups that had already started still finish in the background.");
    }

    public async Task<IActionResult> OnPostClearAsync(CancellationToken ct)
    {
        fetchJob.Stop();
        await metadata.ClearAsync(ct);
        return Message("The metadata cache was cleared.");
    }

    private async Task<CacheView> LoadCacheAsync(CancellationToken ct) =>
        new(await metadata.StatsAsync(ct), fetchJob.Progress, settings.AnyActive);

    private (ConnectorConfig Config, string? Error) Apply(ConnectorKind kind, ConnectorForm form)
    {
        var current = settings.Get(kind);
        var info = Describe(kind);
        var secret = info.SecretLabel is null || form.ClearSecret ? null
            : string.IsNullOrWhiteSpace(form.Secret) ? current.Secret
            : kind == ConnectorKind.Navidrome ? form.Secret // passwords are taken as typed
            : form.Secret.Trim();
        if (!info.HasUrl) return (current with { Enabled = form.Enabled, Secret = secret }, null);

        var baseUrl = form.BaseUrl?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(baseUrl)
            && (baseUrl.Length > 512
                || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || uri.Query.Length > 0))
            return (current, $"The {info.Name} server URL must start with http:// or https://, for example https://music.example.com.");

        var username = kind == ConnectorKind.Navidrome ? TextKey.Clean(form.Username) : "";
        if (username.Length > 256) return (current, "The username is too long.");

        return (current with
        {
            Enabled = form.Enabled,
            BaseUrl = string.IsNullOrEmpty(baseUrl) ? null : baseUrl,
            Username = username.Length == 0 ? null : username,
            Secret = secret,
        }, null);
    }

    private async Task<(bool Ok, string Message)> TestAsync(ConnectorConfig config, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TestTimeout);
        try
        {
            var result = await providers.Single(p => p.Kind == config.Kind).TestAsync(config, timeout.Token);
            settings.ReportSuccess(config.Kind);
            return (true, result);
        }
        catch (MetadataSourceException ex)
        {
            settings.ReportError(config.Kind, ex.Message);
            return (false, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            settings.ReportError(config.Kind, "No answer in time.");
            return (false, $"no answer within {TestTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Testing the {Connector} connector failed", config.Kind);
            settings.ReportError(config.Kind, $"Unexpected error: {ex.Message}");
            return (false, $"unexpected error: {ex.Message}");
        }
    }

    private RedirectToPageResult Message(string text, bool error = false)
    {
        StatusMessage = text;
        StatusIsError = error;
        return RedirectToPage();
    }
}
