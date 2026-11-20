using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MuseFM.Data;
using MuseFM.Data.Entities;

namespace MuseFM.Services.Metadata;

/// <summary>A connector's settings with its secret decrypted. Only ever held in memory.</summary>
public sealed record ConnectorConfig(ConnectorKind Kind, bool Enabled, int Position, string? BaseUrl, string? Username, string? Secret);

/// <summary>Outcome of the most recent requests to a connector, shown in the admin dashboard.</summary>
public sealed record ConnectorHealth(DateTime? LastSuccess, DateTime? LastError, string? Error)
{
    public bool IsFailing => LastError is { } error && (LastSuccess is null || error > LastSuccess);
}

/// <summary>
/// Server-wide metadata connector settings. They are loaded once at startup and cached in memory;
/// secrets are stored encrypted with ASP.NET Core data protection (keys live in the data volume).
/// </summary>
public sealed class ConnectorSettings(
    IDbContextFactory<AppDbContext> dbFactory,
    IDataProtectionProvider protection,
    IOptions<MuseFmOptions> options,
    ILogger<ConnectorSettings> logger)
{
    // Default order: your own servers first, then the open MusicBrainz ecosystem, then Last.fm.
    private static readonly ConnectorKind[] DefaultOrder = [ConnectorKind.Navidrome, ConnectorKind.Maloja, ConnectorKind.ListenBrainz, ConnectorKind.LastFm];

    private readonly IDataProtector _protector = protection.CreateProtector("MuseFM.MetadataConnectors.v1");
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ConcurrentDictionary<ConnectorKind, ConnectorHealth> _health = new();
    private volatile IReadOnlyList<ConnectorConfig> _all = Build([]);

    /// <summary>All connectors in query order.</summary>
    public IReadOnlyList<ConnectorConfig> All => _all;

    /// <summary>Enabled and fully configured connectors, in query order.</summary>
    public IReadOnlyList<ConnectorConfig> Active => _all.Where(c => c.Enabled && Missing(c) is null).ToList();

    public bool AnyActive => _all.Any(c => c.Enabled && Missing(c) is null);

    public ConnectorConfig Get(ConnectorKind kind) => _all.First(c => c.Kind == kind);

    public string? EnvironmentLastFmApiKey => NullIfBlank(options.Value.LastFmApiKey);

    /// <summary>Key for all Last.fm calls (metadata and history import): the admin setting, else MuseFM__LastFmApiKey.</summary>
    public string? LastFmApiKey => LastFmKey(Get(ConnectorKind.LastFm));

    public string? LastFmKey(ConnectorConfig lastFm) => NullIfBlank(lastFm.Secret) ?? EnvironmentLastFmApiKey;

    /// <summary>What a connector still needs before it can be used, or null when it is complete.</summary>
    public string? Missing(ConnectorConfig c) => c.Kind switch
    {
        ConnectorKind.Navidrome or ConnectorKind.Maloja when string.IsNullOrEmpty(c.BaseUrl) => "a server URL",
        ConnectorKind.Navidrome when string.IsNullOrEmpty(c.Username) || string.IsNullOrEmpty(c.Secret) => "a username and password",
        ConnectorKind.LastFm when LastFmKey(c) is null => "an API key",
        ConnectorKind.ListenBrainz when string.IsNullOrEmpty(c.Secret) => "a user token",
        _ => null,
    };

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.MetadataConnectors.AsNoTracking().ToListAsync(ct);
        _all = Build(rows.Select(r => new ConnectorConfig(r.Kind, r.Enabled, r.Position, r.BaseUrl, r.Username, Unprotect(r))));
    }

    public Task SaveAsync(ConnectorConfig config, CancellationToken ct = default)
    {
        _health.TryRemove(config.Kind, out _);
        return SaveAllAsync(_all.Select(c => c.Kind == config.Kind ? config : c).ToList(), ct);
    }

    /// <summary>Moves a connector up (negative delta) or down in the query order.</summary>
    public Task MoveAsync(ConnectorKind kind, int delta, CancellationToken ct = default)
    {
        var list = _all.ToList();
        var from = list.FindIndex(c => c.Kind == kind);
        var to = Math.Clamp(from + delta, 0, list.Count - 1);
        if (from < 0 || from == to) return Task.CompletedTask;
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
        return SaveAllAsync(list, ct);
    }

    public ConnectorHealth? Health(ConnectorKind kind) => _health.GetValueOrDefault(kind);

    public void ReportSuccess(ConnectorKind kind) =>
        _health.AddOrUpdate(kind, _ => new ConnectorHealth(DateTime.UtcNow, null, null), (_, h) => h with { LastSuccess = DateTime.UtcNow });

    /// <returns>True when the connector was working (or unused) until now.</returns>
    public bool ReportError(ConnectorKind kind, string message)
    {
        var wasFailing = Health(kind)?.IsFailing == true;
        _health.AddOrUpdate(kind, _ => new ConnectorHealth(null, DateTime.UtcNow, message), (_, h) => h with { LastError = DateTime.UtcNow, Error = message });
        return !wasFailing;
    }

    private async Task SaveAllAsync(IReadOnlyList<ConnectorConfig> ordered, CancellationToken ct)
    {
        await _saveGate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.MetadataConnectors.ToDictionaryAsync(r => r.Kind, ct);
            for (var i = 0; i < ordered.Count; i++)
            {
                var c = ordered[i];
                if (!rows.TryGetValue(c.Kind, out var row))
                {
                    row = new MetadataConnector { Kind = c.Kind };
                    db.MetadataConnectors.Add(row);
                }

                row.Enabled = c.Enabled;
                row.Position = i;
                row.BaseUrl = c.BaseUrl;
                row.Username = c.Username;
                row.ProtectedSecret = string.IsNullOrEmpty(c.Secret) ? null : _protector.Protect(c.Secret);
                row.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            _all = Build(ordered.Select((c, i) => c with { Position = i }));
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private string? Unprotect(MetadataConnector row)
    {
        if (row.ProtectedSecret is null) return null;
        try
        {
            return _protector.Unprotect(row.ProtectedSecret);
        }
        catch (CryptographicException ex)
        {
            // The data-protection keys were lost (e.g. a new volume): the secret has to be entered again.
            logger.LogWarning(ex, "The saved secret of the {Connector} connector cannot be decrypted; please enter it again", row.Kind);
            return null;
        }
    }

    /// <summary>One entry per connector kind, ordered by saved position, then by the default order.</summary>
    private static IReadOnlyList<ConnectorConfig> Build(IEnumerable<ConnectorConfig> saved)
    {
        var byKind = saved.ToDictionary(c => c.Kind);
        return DefaultOrder
            .Select((kind, i) => byKind.GetValueOrDefault(kind) ?? new ConnectorConfig(kind, false, int.MaxValue / 2 + i, null, null, null))
            .OrderBy(c => c.Position)
            .ThenBy(c => Array.IndexOf(DefaultOrder, c.Kind))
            .Select((c, i) => c with { Position = i })
            .ToList();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
