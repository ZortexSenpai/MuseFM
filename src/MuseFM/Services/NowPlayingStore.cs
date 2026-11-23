using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace MuseFM.Services;

/// <param name="Input">What the client sent, to find the track in the catalog the way its scrobble will be matched.</param>
public sealed record NowPlaying(
    string Title,
    IReadOnlyList<string> Artists,
    string? Album,
    int? LengthSeconds,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    string? Origin,
    ScrobbleInput Input);

/// <summary>
/// In-memory "now playing" state per user. Neither scrobbling protocol can say that playback stopped, so an entry
/// lasts for the track's length; an update that carries no length lasts for
/// <see cref="MuseFmOptions.NowPlayingTimeoutSeconds"/> and relies on the client repeating it while the track plays.
/// </summary>
public sealed class NowPlayingStore(IOptions<MuseFmOptions> options, TimeProvider? time = null)
{
    private static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxLength = TimeSpan.FromHours(2);
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(
        Math.Clamp(options.Value.NowPlayingTimeoutSeconds, MinTimeout.TotalSeconds, MaxLength.TotalSeconds));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, NowPlaying> _entries = new();

    public bool Set(string userId, ScrobbleInput input)
    {
        var (title, artists) = MetadataParser.Parse(input.Title, input.Artists);
        if (title.Length == 0 || artists.Count == 0) return false;

        var now = _time.GetUtcNow();
        var length = input.LengthSeconds is > 0 ? TimeSpan.FromSeconds(input.LengthSeconds.Value) : _timeout;
        if (length > MaxLength) length = MaxLength;
        var album = TextKey.Clean(input.Album);
        _entries[userId] = new NowPlaying(title, artists, album.Length == 0 ? null : album, input.LengthSeconds, now, now + length, input.Origin, input);
        return true;
    }

    public NowPlaying? Get(string userId)
    {
        if (!_entries.TryGetValue(userId, out var entry)) return null;
        if (entry.ExpiresAt > _time.GetUtcNow()) return entry;
        _entries.TryRemove(new KeyValuePair<string, NowPlaying>(userId, entry));
        return null;
    }

    public void Clear(string userId) => _entries.TryRemove(userId, out _);
}
