using System.Collections.Concurrent;

namespace MuseFM.Services;

public sealed record NowPlaying(
    string Title,
    IReadOnlyList<string> Artists,
    string? Album,
    int? LengthSeconds,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    string? Origin);

/// <summary>In-memory "now playing" state per user. Entries expire after the track length (or 5 minutes).</summary>
public sealed class NowPlayingStore
{
    private static readonly TimeSpan DefaultLength = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxLength = TimeSpan.FromHours(2);
    private readonly ConcurrentDictionary<string, NowPlaying> _entries = new();

    public bool Set(string userId, ScrobbleInput input)
    {
        var (title, artists) = MetadataParser.Parse(input.Title, input.Artists);
        if (title.Length == 0 || artists.Count == 0) return false;

        var now = DateTimeOffset.UtcNow;
        var length = input.LengthSeconds is > 0 ? TimeSpan.FromSeconds(input.LengthSeconds.Value) : DefaultLength;
        if (length > MaxLength) length = MaxLength;
        var album = TextKey.Clean(input.Album);
        _entries[userId] = new NowPlaying(title, artists, album.Length == 0 ? null : album, input.LengthSeconds, now, now + length, input.Origin);
        return true;
    }

    public NowPlaying? Get(string userId)
    {
        if (!_entries.TryGetValue(userId, out var entry)) return null;
        if (entry.ExpiresAt > DateTimeOffset.UtcNow) return entry;
        _entries.TryRemove(new KeyValuePair<string, NowPlaying>(userId, entry));
        return null;
    }

    public void Clear(string userId) => _entries.TryRemove(userId, out _);
}
