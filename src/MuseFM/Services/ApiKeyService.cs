using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;

namespace MuseFM.Services;

/// <summary>API keys (ListenBrainz tokens / Last.fm passwords) and Last.fm session keys. Only hashes are stored.</summary>
public sealed class ApiKeyService(AppDbContext db)
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    public static string Hash(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret.Trim())));

    /// <summary>UUID-shaped like ListenBrainz tokens, since some clients validate the format.</summary>
    public static string GenerateToken() => new Guid(RandomNumberGenerator.GetBytes(16)).ToString("D");

    /// <summary>32 hex characters like Last.fm session keys.</summary>
    public static string GenerateSessionKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public async Task<(ApiKey Key, string Token)> CreateAsync(string userId, string? name, CancellationToken ct = default)
    {
        var token = GenerateToken();
        var cleanName = TextKey.Clean(name);
        var key = new ApiKey
        {
            UserId = userId,
            Name = cleanName.Length == 0 ? "API key" : cleanName[..Math.Min(cleanName.Length, 64)],
            KeyHash = Hash(token),
            Prefix = token[..8],
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        return (key, token);
    }

    public async Task<ApiKey?> ValidateAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = Hash(token);
        var key = await db.ApiKeys.AsNoTracking().Include(k => k.User).FirstOrDefaultAsync(k => k.KeyHash == hash, ct);
        if (key is null) return null;

        var now = DateTime.UtcNow;
        if (key.LastUsedAt is null || now - key.LastUsedAt.Value > TouchInterval)
            await db.ApiKeys.Where(k => k.Id == key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, (DateTime?)now), ct);
        return key;
    }

    public Task<List<ApiKey>> ListAsync(string userId, CancellationToken ct = default) =>
        db.ApiKeys.AsNoTracking().Where(k => k.UserId == userId).OrderByDescending(k => k.CreatedAt).ToListAsync(ct);

    public async Task<bool> RevokeAsync(string userId, int id, CancellationToken ct = default) =>
        await db.ApiKeys.Where(k => k.Id == id && k.UserId == userId).ExecuteDeleteAsync(ct) > 0;

    public async Task<string> CreateSessionAsync(string userId, int? apiKeyId, string? client, CancellationToken ct = default)
    {
        var sessionKey = GenerateSessionKey();
        var cleanClient = TextKey.Clean(client);
        db.ScrobblerSessions.Add(new ScrobblerSession
        {
            UserId = userId,
            ApiKeyId = apiKeyId,
            KeyHash = Hash(sessionKey),
            Client = cleanClient.Length == 0 ? null : cleanClient[..Math.Min(cleanClient.Length, 128)],
        });
        await db.SaveChangesAsync(ct);
        return sessionKey;
    }

    public async Task<ScrobblerSession?> ValidateSessionAsync(string? sessionKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionKey)) return null;
        var hash = Hash(sessionKey);
        var session = await db.ScrobblerSessions.AsNoTracking().Include(s => s.User).FirstOrDefaultAsync(s => s.KeyHash == hash, ct);
        if (session is null) return null;

        var now = DateTime.UtcNow;
        if (session.LastUsedAt is null || now - session.LastUsedAt.Value > TouchInterval)
            await db.ScrobblerSessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedAt, (DateTime?)now), ct);
        return session;
    }

    public Task<List<ScrobblerSession>> ListSessionsAsync(string userId, CancellationToken ct = default) =>
        db.ScrobblerSessions.AsNoTracking().Include(s => s.ApiKey)
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

    public async Task<bool> RevokeSessionAsync(string userId, int id, CancellationToken ct = default) =>
        await db.ScrobblerSessions.Where(s => s.Id == id && s.UserId == userId).ExecuteDeleteAsync(ct) > 0;

    /// <summary>
    /// Ends the sessions that were started with the account password, when that password is replaced: whoever knew the
    /// old one must not keep access. Sessions started with an API key go on, they end when their key is revoked.
    /// </summary>
    public Task RevokePasswordSessionsAsync(string userId, CancellationToken ct = default) =>
        db.ScrobblerSessions.Where(s => s.UserId == userId && s.ApiKeyId == null).ExecuteDeleteAsync(ct);
}
