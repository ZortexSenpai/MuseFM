using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;

namespace MuseFM.Services.Metadata;

public enum MediaKind
{
    Artist,
    Album,

    /// <summary>Shows the cover of the album the track is most often scrobbled with; tracks have no artwork of their own.</summary>
    Track,
}

public sealed record CachedImage(string Path, string ContentType, string ETag, DateTimeOffset LastModified);

/// <param name="Pending">No image yet, but a lookup is still running: worth asking again shortly.</param>
public sealed record MediaLookup(CachedImage? Image, bool Pending)
{
    public static readonly MediaLookup None = new(null, false);
}

/// <param name="ArtistsLookedUp">Artists the connectors were asked about, whether or not they found anything.</param>
/// <param name="TracksWithLength">Tracks whose length is known, from a client or from an album's track list.</param>
public sealed record MetadataCacheStats(
    int Artists, int ArtistsLookedUp, int ArtistsWithImage, int ArtistsWithBio,
    int Albums, int AlbumsLookedUp, int AlbumsWithImage,
    long ImageBytes,
    int ArtistsWithGenres = 0, int AlbumsWithGenres = 0, int AlbumsWithYear = 0, int Tracks = 0, int TracksWithLength = 0);

/// <summary>
/// Serves cached covers, artist photos and biographies, and fetches missing or outdated ones from the active
/// connectors, along with genres, release years and track lengths for the statistics. Fetches run in the background, one per item at a time, so a request only waits briefly and a
/// slow source never blocks a page for long.
/// </summary>
public sealed class MetadataService(
    IServiceScopeFactory scopes,
    IDbContextFactory<AppDbContext> dbFactory,
    ConnectorSettings settings,
    DataPaths paths,
    IHostApplicationLifetime lifetime,
    ILogger<MetadataService> logger)
{
    /// <summary>
    /// The current generation of lookups: 1 added genres, release years and track lengths. Rows cached by an older one
    /// are looked up again by "fetch missing", keeping the picture and biography they have.
    /// </summary>
    public const int Revision = 1;

    private static readonly TimeSpan CompleteRefresh = TimeSpan.FromDays(90);
    private static readonly TimeSpan IncompleteRetry = TimeSpan.FromDays(14);
    // After a connector error, or when a connector said it is still looking.
    private static readonly TimeSpan SoonRetry = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<(MediaKind Kind, int Id), Lazy<Task>> _running = new();
    private readonly SemaphoreSlim _slots = new(4, 4);

    /// <summary>True when at least one connector is active, i.e. pages should show artwork.</summary>
    public bool Enabled => settings.AnyActive;

    /// <summary>
    /// The cached metadata of an artist. When nothing is cached yet it is fetched first, waiting at most
    /// <paramref name="wait"/>; outdated data is returned right away and refreshed in the background.
    /// </summary>
    public async Task<ArtistMetadata?> GetArtistAsync(int artistId, TimeSpan wait, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ArtistMetadata.AsNoTracking().FirstOrDefaultAsync(m => m.ArtistId == artistId, ct);
        if (!IsDue(row?.RefreshAfter)) return row;

        var refresh = Refresh(MediaKind.Artist, artistId);
        if (row is not null && (row.ImageFile is not null || row.Bio is not null)) return row;
        if (!await WaitAsync(refresh, wait, ct)) return row;
        return await db.ArtistMetadata.AsNoTracking().FirstOrDefaultAsync(m => m.ArtistId == artistId, ct);
    }

    /// <summary>
    /// The cached image of an artist, album or track, fetching it first (waiting at most <paramref name="wait"/>) when
    /// there is none. A lookup that takes longer goes on in the background and the result says it is pending.
    /// </summary>
    public async Task<MediaLookup> GetImageAsync(MediaKind kind, int id, TimeSpan wait, CancellationToken ct = default)
    {
        if (kind == MediaKind.Track)
            return await AlbumOfTrackAsync(id, ct) is { } albumId ? await GetImageAsync(MediaKind.Album, albumId, wait, ct) : MediaLookup.None;

        var (exists, file, refreshAfter) = await FindImageAsync(kind, id, ct);
        if (!exists) return MediaLookup.None;

        var image = Load(file);
        if (image is not null)
        {
            if (IsDue(refreshAfter)) _ = Refresh(kind, id); // stale: serve it and refresh in the background (never faults)
            return new MediaLookup(image, false);
        }

        // Nothing cached: look it up if it's due (or already being looked up, e.g. for the artist page) and wait briefly.
        var running = _running.TryGetValue((kind, id), out var lookup) ? lookup.Value : null;
        if (running is null && (IsDue(refreshAfter) || (file is not null && Enabled))) running = Refresh(kind, id);
        if (running is null) return MediaLookup.None;
        if (!await WaitAsync(running, wait, ct)) return new MediaLookup(null, true);
        return new MediaLookup(Load((await FindImageAsync(kind, id, ct)).File), false);
    }

    /// <summary>The album a track is most often scrobbled with, by anyone.</summary>
    private async Task<int?> AlbumOfTrackAsync(int trackId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Scrobbles.AsNoTracking()
            .Where(s => s.TrackId == trackId && s.AlbumId != null)
            .GroupBy(s => s.AlbumId)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => g.Key)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Looks the item up now, whatever is cached, and waits until that is done.</summary>
    public Task FetchAsync(MediaKind kind, int id, CancellationToken ct = default) => Refresh(kind, id).WaitAsync(ct);

    /// <summary>Looks the item up unless that was done before, whatever came of it, and waits until that is done.</summary>
    public async Task FetchNewAsync(MediaKind kind, int id, CancellationToken ct = default)
    {
        if (!Enabled) return;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var lookedUp = kind == MediaKind.Artist
                ? await db.ArtistMetadata.AnyAsync(m => m.ArtistId == id, ct)
                : await db.AlbumMetadata.AnyAsync(m => m.AlbumId == id, ct);
            if (lookedUp) return;
        }

        await FetchAsync(kind, id, ct);
    }

    /// <summary>Makes everything that is incomplete due for another try when next shown, e.g. after a connector was added.</summary>
    public async Task RetryMissingAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        await db.ArtistMetadata.Where(m => m.ImageFile == null || m.Bio == null || m.Revision < Revision)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.RefreshAfter, now), ct);
        await db.AlbumMetadata.Where(m => m.ImageFile == null || m.Revision < Revision)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.RefreshAfter, now), ct);
    }

    /// <summary>Deletes all cached metadata and images; they are fetched again when next shown.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ArtistMetadata.ExecuteDeleteAsync(ct);
        await db.AlbumMetadata.ExecuteDeleteAsync(ct);
        foreach (var file in Directory.EnumerateFiles(paths.Images)) TryDelete(file);
    }

    public async Task<MetadataCacheStats> StatsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return new MetadataCacheStats(
            await db.Artists.CountAsync(ct),
            await db.ArtistMetadata.CountAsync(ct),
            await db.ArtistMetadata.CountAsync(m => m.ImageFile != null, ct),
            await db.ArtistMetadata.CountAsync(m => m.Bio != null, ct),
            await db.Albums.CountAsync(ct),
            await db.AlbumMetadata.CountAsync(ct),
            await db.AlbumMetadata.CountAsync(m => m.ImageFile != null, ct),
            new DirectoryInfo(paths.Images).EnumerateFiles().Sum(f => f.Length),
            await db.ArtistMetadata.CountAsync(m => m.Genres != null, ct),
            await db.AlbumMetadata.CountAsync(m => m.Genres != null, ct),
            await db.AlbumMetadata.CountAsync(m => m.ReleaseYear != null, ct),
            await db.Tracks.CountAsync(ct),
            await db.Tracks.CountAsync(t => t.LengthSeconds != null, ct));
    }

    private bool IsDue(DateTime? refreshAfter) => Enabled && (refreshAfter is null || refreshAfter <= DateTime.UtcNow);

    private static async Task<bool> WaitAsync(Task task, TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await task.WaitAsync(wait, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Starts a fetch for the item unless one is already running, and returns it. The task never faults.</summary>
    private Task Refresh(MediaKind kind, int id) =>
        _running.GetOrAdd((kind, id), key => new Lazy<Task>(() => Task.Run(() => RunAsync(key)))).Value;

    private async Task RunAsync((MediaKind Kind, int Id) key)
    {
        // Not tied to the request that triggered it: the result is cached for everyone.
        var ct = lifetime.ApplicationStopping;
        try
        {
            await _slots.WaitAsync(ct);
            try
            {
                await ResolveAsync(key.Kind, key.Id, ct);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fetching metadata for {Kind} {Id} failed", key.Kind, key.Id);
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
    }

    private async Task ResolveAsync(MediaKind kind, int id, CancellationToken ct)
    {
        var connectors = settings.Active;
        if (connectors.Count == 0) return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var providers = scope.ServiceProvider.GetServices<IMetadataProvider>().ToDictionary(p => p.Kind);
        var replaced = kind == MediaKind.Artist
            ? await ResolveArtistAsync(db, connectors, providers, id, ct)
            : await ResolveAlbumAsync(db, connectors, providers, id, ct);
        if (replaced is not null) TryDelete(ImagePath(replaced));
    }

    /// <returns>The previous image file when it was replaced and can be deleted.</returns>
    private async Task<string?> ResolveArtistAsync(
        AppDbContext db, IReadOnlyList<ConnectorConfig> connectors, Dictionary<ConnectorKind, IMetadataProvider> providers, int id, CancellationToken ct)
    {
        var artist = await db.Artists.AsNoTracking().Where(a => a.Id == id).Select(a => new { a.Name, a.MusicBrainzId }).FirstOrDefaultAsync(ct);
        if (artist is null) return null;
        var name = artist.Name;

        // Tracks credited to this artist alone identify them best.
        var tracks = await db.TrackArtists.AsNoTracking()
            .Where(ta => ta.ArtistId == id)
            .OrderBy(ta => ta.Track.Artists.Count).ThenBy(ta => ta.TrackId)
            .Select(ta => ta.Track.Title)
            .Take(3)
            .ToListAsync(ct);
        var samples = tracks.Select(t => new SampleTrack(t, name)).ToList();
        var row = await db.ArtistMetadata.FirstOrDefaultAsync(m => m.ArtistId == id, ct);

        // An id sent with scrobbles (or set by an admin) beats one found by a name search.
        var mbid = artist.MusicBrainzId ?? row?.MusicBrainzId;
        // A row from before genres were looked up only lacks those: no need to fetch its picture and biography again.
        var catchingUp = row is not null && row.Revision < Revision;
        var keepImage = catchingUp && row!.ImageFile is not null && File.Exists(ImagePath(row.ImageFile));
        var keepBio = catchingUp && row!.Bio is not null;
        Biography? bio = null;
        ImageData? image = null;
        IReadOnlyList<string>? genres = null;
        var retrySoon = false;
        var failed = false;
        foreach (var connector in connectors)
        {
            var query = new ArtistQuery(name, mbid, samples, NeedImage: image is null && !keepImage, NeedBio: bio is null && !keepBio, NeedGenres: genres is null);
            if (!query.NeedImage && !query.NeedBio && !query.NeedGenres) break;
            var (found, error) = await AskAsync(connector, MediaKind.Artist, id, () => providers[connector.Kind].FindArtistAsync(connector, query, ct), ct);
            retrySoon |= error || found?.Pending == true;
            failed |= error;
            if (found is null) continue;
            mbid ??= found.MusicBrainzId;
            bio ??= found.Bio;
            image ??= found.Image;
            genres ??= found.Genres is { Count: > 0 } ? found.Genres : null;
        }

        if (row is null)
        {
            row = new ArtistMetadata { ArtistId = id };
            db.ArtistMetadata.Add(row);
        }

        var replaced = await ApplyImageAsync(MediaKind.Artist, id, image, row.ImageFile, ct);
        if (image is not null)
        {
            row.ImageFile = replaced.File;
            row.ImageSource = image.Source;
        }
        else if (replaced.File is null)
        {
            row.ImageFile = row.ImageSource = null;
        }

        if (bio is not null)
        {
            row.Bio = bio.Text;
            row.BioSource = bio.Source;
            row.BioUrl = bio.Url;
        }

        if (genres is not null) row.Genres = GenreNames.Join(genres);
        row.MusicBrainzId = mbid;
        // After a connector error the row stays behind, so the next "fetch missing" asks again.
        if (!failed) row.Revision = Revision;
        row.UpdatedAt = DateTime.UtcNow;
        row.RefreshAfter = row.UpdatedAt
            + ((image is not null || keepImage) && (bio is not null || keepBio) ? CompleteRefresh : retrySoon ? SoonRetry : IncompleteRetry);
        await db.SaveChangesAsync(ct);
        return replaced.Previous;
    }

    private async Task<string?> ResolveAlbumAsync(
        AppDbContext db, IReadOnlyList<ConnectorConfig> connectors, Dictionary<ConnectorKind, IMetadataProvider> providers, int id, CancellationToken ct)
    {
        var album = await db.Albums.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new { a.Title, a.MusicBrainzId, Artists = a.Artists.OrderBy(x => x.Position).Select(x => x.Artist.Name).ToList() })
            .FirstOrDefaultAsync(ct);
        if (album is null) return null;

        var trackIds = await db.Scrobbles.AsNoTracking().Where(s => s.AlbumId == id).Select(s => s.TrackId).Distinct().ToListAsync(ct);
        var sampleIds = trackIds.Take(3).ToList();
        var tracks = await db.Tracks.AsNoTracking()
            .Where(t => sampleIds.Contains(t.Id))
            .Select(t => new { t.Title, Artist = t.Artists.OrderBy(x => x.Position).Select(x => x.Artist.Name).FirstOrDefault() })
            .ToListAsync(ct);
        var samples = tracks.Select(t => new SampleTrack(t.Title, t.Artist ?? album.Artists.FirstOrDefault() ?? "")).ToList();
        var row = await db.AlbumMetadata.FirstOrDefaultAsync(m => m.AlbumId == id, ct);

        var mbid = album.MusicBrainzId ?? row?.MusicBrainzId;
        // A row from before years, genres and track lengths were looked up only lacks those: its cover can stay.
        var keepImage = row is not null && row.Revision < Revision && row.ImageFile is not null && File.Exists(ImagePath(row.ImageFile));
        ImageData? image = null;
        int? year = null;
        IReadOnlyList<string>? genres = null;
        IReadOnlyList<TrackLength>? lengths = null;
        var retrySoon = false;
        var failed = false;
        var thisYear = DateTime.UtcNow.Year;
        foreach (var connector in connectors)
        {
            var needImage = image is null && !keepImage;
            if (!needImage && year is not null && genres is not null && lengths is not null) break;
            var query = new AlbumQuery(album.Title, album.Artists, mbid, samples, needImage);
            var (found, error) = await AskAsync(connector, MediaKind.Album, id, () => providers[connector.Kind].FindAlbumAsync(connector, query, ct), ct);
            retrySoon |= error || found?.Pending == true;
            failed |= error;
            if (found is null) continue;
            mbid ??= found.MusicBrainzId;
            image ??= found.Image;
            year ??= found.Year is > 1000 && found.Year <= thisYear + 1 ? found.Year : null;
            genres ??= found.Genres is { Count: > 0 } ? found.Genres : null;
            lengths ??= found.Tracks is { Count: > 0 } ? found.Tracks : null;
        }

        if (row is null)
        {
            row = new AlbumMetadata { AlbumId = id };
            db.AlbumMetadata.Add(row);
        }

        var replaced = await ApplyImageAsync(MediaKind.Album, id, image, row.ImageFile, ct);
        if (image is not null)
        {
            row.ImageFile = replaced.File;
            row.ImageSource = image.Source;
        }
        else if (replaced.File is null)
        {
            row.ImageFile = row.ImageSource = null;
        }

        if (year is not null) row.ReleaseYear = year;
        if (genres is not null) row.Genres = GenreNames.Join(genres);
        if (lengths is not null) await ApplyLengthsAsync(db, trackIds, lengths, ct);
        row.MusicBrainzId = mbid;
        if (!failed) row.Revision = Revision;
        row.UpdatedAt = DateTime.UtcNow;
        row.RefreshAfter = row.UpdatedAt + (image is not null || keepImage ? CompleteRefresh : retrySoon ? SoonRetry : IncompleteRetry);
        await db.SaveChangesAsync(ct);
        return replaced.Previous;
    }

    /// <summary>
    /// Gives the tracks scrobbled from an album the lengths of its track list, matched by title. A length a client
    /// reported is never replaced.
    /// </summary>
    private static async Task ApplyLengthsAsync(AppDbContext db, List<int> trackIds, IReadOnlyList<TrackLength> lengths, CancellationToken ct)
    {
        const int maxSeconds = 24 * 60 * 60;
        var unknown = await db.Tracks.Where(t => trackIds.Contains(t.Id) && t.LengthSeconds == null).ToListAsync(ct);
        foreach (var track in unknown)
        {
            var match = lengths.FirstOrDefault(l => l.Seconds is > 0 and <= maxSeconds && NameMatch.SameTitle(l.Title, track.Title));
            if (match is not null) track.LengthSeconds = match.Seconds;
        }
    }

    private async Task<(T? Result, bool Failed)> AskAsync<T>(ConnectorConfig connector, MediaKind kind, int id, Func<Task<T?>> ask, CancellationToken ct)
        where T : class
    {
        try
        {
            var result = await ask();
            settings.ReportSuccess(connector.Kind);
            return (result, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex is MetadataSourceException ? ex.Message : $"Unexpected error: {ex.Message}";
            // Log when a connector starts failing rather than for every item while it is down.
            if (settings.ReportError(connector.Kind, message) || ex is not MetadataSourceException)
                logger.LogWarning(ex, "Metadata connector {Connector} failed for {Kind} {Id}: {Message}", connector.Kind, kind, id, message);
            return (null, true);
        }
    }

    /// <summary>
    /// Writes a newly found image. Keeps the current one when nothing new was found but its file still exists.
    /// </summary>
    /// <returns>The file to reference, and the previous file when it should be deleted after saving.</returns>
    private async Task<(string? File, string? Previous)> ApplyImageAsync(MediaKind kind, int id, ImageData? image, string? current, CancellationToken ct)
    {
        if (image is null) return (current is not null && File.Exists(ImagePath(current)) ? current : null, null);

        var hash = Convert.ToHexStringLower(SHA256.HashData(image.Bytes))[..16];
        var file = $"{kind.ToString().ToLowerInvariant()}-{id}-{hash}{image.Extension}";
        var path = ImagePath(file);
        if (!File.Exists(path))
        {
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, image.Bytes, ct);
            File.Move(temp, path, overwrite: true);
        }

        return (file, current is not null && current != file ? current : null);
    }

    private async Task<(bool Exists, string? File, DateTime? RefreshAfter)> FindImageAsync(MediaKind kind, int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (kind == MediaKind.Artist)
        {
            var artist = await db.ArtistMetadata.AsNoTracking().Where(m => m.ArtistId == id)
                .Select(m => new { m.ImageFile, m.RefreshAfter }).FirstOrDefaultAsync(ct);
            return artist is not null
                ? (true, artist.ImageFile, artist.RefreshAfter)
                : (await db.Artists.AnyAsync(a => a.Id == id, ct), null, null);
        }

        var album = await db.AlbumMetadata.AsNoTracking().Where(m => m.AlbumId == id)
            .Select(m => new { m.ImageFile, m.RefreshAfter }).FirstOrDefaultAsync(ct);
        return album is not null
            ? (true, album.ImageFile, album.RefreshAfter)
            : (await db.Albums.AnyAsync(a => a.Id == id, ct), null, null);
    }

    private CachedImage? Load(string? file)
    {
        if (file is null) return null;
        var info = new FileInfo(ImagePath(file));
        if (!info.Exists) return null;
        // File names end in a content hash, which doubles as the ETag.
        var name = Path.GetFileNameWithoutExtension(info.Name);
        return new CachedImage(info.FullName, MetadataHttp.ContentType(info.Extension), $"\"{name[(name.LastIndexOf('-') + 1)..]}\"", info.LastWriteTimeUtc);
    }

    private string ImagePath(string file) => Path.Combine(paths.Images, Path.GetFileName(file));

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete cached image {Path}", path);
        }
    }
}
