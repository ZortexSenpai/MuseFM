using MuseFM.Data.Entities;

namespace MuseFM.Services.Metadata;

/// <summary>A track used to identify an artist or album at sources that match on recordings (ListenBrainz).</summary>
public sealed record SampleTrack(string Title, string Artist);

/// <param name="MusicBrainzId">Artist MBID if already known (from the cache or an earlier connector).</param>
/// <param name="NeedImage">False when an earlier connector already supplied a photo.</param>
/// <param name="NeedBio">False when an earlier connector already supplied a biography.</param>
/// <param name="NeedGenres">False when an earlier connector already supplied genres.</param>
public sealed record ArtistQuery(
    string Name, string? MusicBrainzId, IReadOnlyList<SampleTrack> Tracks, bool NeedImage, bool NeedBio, bool NeedGenres = true);

/// <param name="Artists">Album artists in credit order.</param>
/// <param name="MusicBrainzId">Release MBID if already known.</param>
/// <param name="NeedImage">False when there is a cover already and only the album's details are asked for.</param>
public sealed record AlbumQuery(
    string Title, IReadOnlyList<string> Artists, string? MusicBrainzId, IReadOnlyList<SampleTrack> Tracks, bool NeedImage = true);

/// <summary>How long a track of an album is, by its title there.</summary>
public sealed record TrackLength(string Title, int Seconds);

public sealed record Biography(string Text, string Source, string? Url);

/// <summary>A downloaded and validated image.</summary>
public sealed record ImageData(byte[] Bytes, string Extension, string Source);

/// <param name="Pending">The source is still looking for something it may have shortly: ask again soon instead of in two weeks.</param>
/// <param name="Genres">Cleaned with <see cref="GenreNames.Clean"/>, the most fitting first.</param>
public sealed record ArtistLookup(string? MusicBrainzId, Biography? Bio, ImageData? Image, bool Pending = false, IReadOnlyList<string>? Genres = null);

/// <param name="Pending">See <see cref="ArtistLookup.Pending"/>.</param>
/// <param name="Year">The year the album first came out.</param>
/// <param name="Genres">See <see cref="ArtistLookup.Genres"/>.</param>
/// <param name="Tracks">The album's tracks with their lengths, used to fill in lengths that no client reported.</param>
public sealed record AlbumLookup(
    string? MusicBrainzId, ImageData? Image, bool Pending = false, int? Year = null, IReadOnlyList<string>? Genres = null,
    IReadOnlyList<TrackLength>? Tracks = null);

/// <summary>
/// One metadata source. Implementations return null when the source has no match, and throw
/// <see cref="MetadataSourceException"/> when it could not be asked (network, credentials, rate limits).
/// </summary>
public interface IMetadataProvider
{
    ConnectorKind Kind { get; }

    Task<ArtistLookup?> FindArtistAsync(ConnectorConfig config, ArtistQuery query, CancellationToken ct);

    Task<AlbumLookup?> FindAlbumAsync(ConnectorConfig config, AlbumQuery query, CancellationToken ct);

    /// <summary>Checks the settings with a cheap request and returns a confirmation for the admin.</summary>
    Task<string> TestAsync(ConnectorConfig config, CancellationToken ct);
}

public sealed class MetadataSourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Spaces out and limits concurrent requests to one remote service.</summary>
public sealed class RateGate(TimeSpan interval, int concurrency = 1)
{
    private readonly SemaphoreSlim _slots = new(concurrency, concurrency);
    private readonly SemaphoreSlim _clock = new(1, 1);
    private long _nextStart;

    /// <summary>Waits for a slot; dispose the result when the request has finished.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        try
        {
            if (interval > TimeSpan.Zero)
            {
                await _clock.WaitAsync(ct);
                try
                {
                    var now = Environment.TickCount64;
                    if (_nextStart > now) await Task.Delay(TimeSpan.FromMilliseconds(_nextStart - now), ct);
                    _nextStart = Math.Max(now, _nextStart) + (long)interval.TotalMilliseconds;
                }
                finally
                {
                    _clock.Release();
                }
            }

            return new Slot(_slots);
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) slots.Release();
        }
    }
}
