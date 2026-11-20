using Microsoft.EntityFrameworkCore;
using MuseFM.Data;

namespace MuseFM.Services.Metadata;

public enum FetchMode
{
    /// <summary>
    /// Everything without complete metadata: never looked up, missing a photo, cover or biography, or looked up before
    /// genres, release years and track lengths were (<see cref="MetadataService.Revision"/>).
    /// </summary>
    Missing,

    /// <summary>Everything, e.g. after changing the connector order.</summary>
    All,
}

/// <param name="Total">Null while the list of items is still being built.</param>
public sealed record FetchProgress(FetchMode Mode, int? Total, int Done, DateTime StartedAt, DateTime? FinishedAt, bool Stopped, string? Error)
{
    public bool IsRunning => FinishedAt is null;
}

/// <summary>
/// Looks up metadata for the whole catalog in the background, started from the admin dashboard.
/// Most listened artists and albums go first, so what people see most fills in first. One run at a time;
/// a run is not resumed after a restart (items are still looked up when they are shown).
/// </summary>
public sealed class MetadataFetchJob(
    IDbContextFactory<AppDbContext> dbFactory,
    MetadataService metadata,
    IHostApplicationLifetime lifetime,
    ILogger<MetadataFetchJob> logger)
{
    // One less than MetadataService's concurrent lookups, so pages being viewed still get a turn.
    private const int Parallelism = 3;

    private readonly Lock _lock = new();
    private Run? _run;

    /// <summary>The current or most recent run, or null if none was started since the app started.</summary>
    public FetchProgress? Progress
    {
        get
        {
            lock (_lock) return _run?.Snapshot();
        }
    }

    /// <returns>False when a run is already in progress.</returns>
    public bool TryStart(FetchMode mode)
    {
        lock (_lock)
        {
            if (_run is { FinishedAt: null }) return false;
            var run = new Run(mode, CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping));
            _run = run;
            _ = Task.Run(() => ExecuteAsync(run));
            return true;
        }
    }

    /// <summary>Stops after the lookups in progress.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_run is { FinishedAt: null } run) run.Cancellation.Cancel();
        }
    }

    private async Task ExecuteAsync(Run run)
    {
        var ct = run.Cancellation.Token;
        try
        {
            var items = await ListAsync(run.Mode, ct);
            lock (_lock) run.Total = items.Count;
            logger.LogInformation("Fetching metadata for {Count} artists and albums ({Mode})", items.Count, run.Mode);

            await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (item, token) =>
            {
                await metadata.FetchAsync(item.Kind, item.Id, token);
                Interlocked.Increment(ref run.Done);
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_lock) run.Stopped = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fetching metadata failed");
            lock (_lock) run.Error = ex.Message;
        }
        finally
        {
            lock (_lock)
            {
                run.FinishedAt = DateTime.UtcNow;
                run.Cancellation.Dispose();
            }

            logger.LogInformation("Metadata fetch finished: {Done} of {Total} looked up", Volatile.Read(ref run.Done), run.Total);
        }
    }

    /// <summary>Artists and albums alternately, each ordered by scrobbles; entries nobody has played any more come last.</summary>
    private async Task<List<(MediaKind Kind, int Id)>> ListAsync(FetchMode mode, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var artists = await db.Scrobbles.AsNoTracking()
            .Join(db.TrackArtists, s => s.TrackId, ta => ta.TrackId, (s, ta) => ta.ArtistId)
            .GroupBy(id => id)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToListAsync(ct);
        var albums = await db.Scrobbles.AsNoTracking()
            .Where(s => s.AlbumId != null)
            .GroupBy(s => s.AlbumId!.Value)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToListAsync(ct);
        AppendRest(artists, await db.Artists.Select(a => a.Id).ToListAsync(ct));
        AppendRest(albums, await db.Albums.Select(a => a.Id).ToListAsync(ct));

        if (mode == FetchMode.Missing)
        {
            const int current = MetadataService.Revision;
            var completeArtists = (await db.ArtistMetadata.Where(m => m.ImageFile != null && m.Bio != null && m.Revision >= current)
                .Select(m => m.ArtistId).ToListAsync(ct)).ToHashSet();
            var completeAlbums = (await db.AlbumMetadata.Where(m => m.ImageFile != null && m.Revision >= current)
                .Select(m => m.AlbumId).ToListAsync(ct)).ToHashSet();
            artists.RemoveAll(completeArtists.Contains);
            albums.RemoveAll(completeAlbums.Contains);
        }

        var items = new List<(MediaKind, int)>(artists.Count + albums.Count);
        for (var i = 0; i < Math.Max(artists.Count, albums.Count); i++)
        {
            if (i < artists.Count) items.Add((MediaKind.Artist, artists[i]));
            if (i < albums.Count) items.Add((MediaKind.Album, albums[i]));
        }

        return items;
    }

    private static void AppendRest(List<int> ranked, List<int> all)
    {
        var seen = ranked.ToHashSet();
        ranked.AddRange(all.Where(id => !seen.Contains(id)));
    }

    private sealed class Run(FetchMode mode, CancellationTokenSource cancellation)
    {
        public readonly FetchMode Mode = mode;
        public readonly CancellationTokenSource Cancellation = cancellation;
        public readonly DateTime StartedAt = DateTime.UtcNow;
        public int Done;
        public int? Total;
        public DateTime? FinishedAt;
        public bool Stopped;
        public string? Error;

        public FetchProgress Snapshot() => new(Mode, Total, Volatile.Read(ref Done), StartedAt, FinishedAt, Stopped, Error);
    }
}
