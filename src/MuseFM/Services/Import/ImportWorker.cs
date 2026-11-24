using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services.Metadata;

namespace MuseFM.Services.Import;

public sealed class ImportQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();

    public void Enqueue(int jobId) => _channel.Writer.TryWrite(jobId);

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Processes import jobs one at a time in the background. Jobs interrupted by a restart are picked up again;
/// re-running is safe because scrobbles the user already has are skipped.
/// </summary>
public sealed class ImportWorker(
    IServiceScopeFactory scopes,
    IDbContextFactory<AppDbContext> dbFactory,
    ImportQueue queue,
    ILogger<ImportWorker> logger) : BackgroundService
{
    private const int ChunkSize = 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(stoppingToken))
        {
            var pending = await db.ImportJobs
                .Where(j => j.Status == ImportStatus.Queued || j.Status == ImportStatus.Running)
                .OrderBy(j => j.Id)
                .Select(j => j.Id)
                .ToListAsync(stoppingToken);
            foreach (var id in pending) queue.Enqueue(id);
        }

        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Import job {JobId} crashed", jobId);
            }
        }
    }

    private async Task RunAsync(int jobId, CancellationToken ct)
    {
        await using var jobDb = await dbFactory.CreateDbContextAsync(ct);
        var job = await jobDb.ImportJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status is ImportStatus.Completed or ImportStatus.Failed) return;

        job.Status = ImportStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        job.Processed = job.Imported = job.Duplicates = job.Failed = 0;
        job.Total = null;
        job.Message = null;
        job.ErrorSample = null;
        await jobDb.SaveChangesAsync(ct);
        logger.LogInformation("Import job {JobId} ({Kind}) started for user {UserId}", job.Id, job.Kind, job.UserId);

        using var scope = scopes.CreateScope();
        var scrobbles = scope.ServiceProvider.GetRequiredService<ScrobbleService>();
        var feedback = (Stored: 0, Unmatched: 0);
        var range = new ImportRange(job.ListenedFrom, job.ListenedBefore);
        try
        {
            if (job.Kind == ImportKind.LastFmApi)
            {
                var client = scope.ServiceProvider.GetRequiredService<LastFmClient>();
                await foreach (var page in client.GetRecentTracksAsync(job.Source, range, ct))
                {
                    job.Total = page.Total;
                    await IngestAsync(job, page.Rows, range, scrobbles, ct);
                    job.Message = $"Fetched page {page.Page} of {Math.Max(page.TotalPages, 1)}";
                    await jobDb.SaveChangesAsync(ct);
                }

                job.Message = "Fetching loved tracks";
                await jobDb.SaveChangesAsync(ct);
                var loves = await client.GetLovedTracksAsync(job.Source, ct);
                feedback = await ImportFeedbackAsync(job.UserId,
                    loves.Select(l => new FeedbackImport(MetadataText.Mbid(l.Track.MusicBrainzId), l.Track, TrackFeedback.Love, l.LovedAt)).ToList(),
                    scope.ServiceProvider, ct);
            }
            else if (job.Kind == ImportKind.MalojaApi)
            {
                var client = new MalojaClient(scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(
                    await MayReachPrivateAddressesAsync(job.UserId, scope.ServiceProvider)
                        ? MalojaClient.AnyAddressClientName
                        : MalojaClient.PublicClientName));
                await foreach (var page in client.GetScrobblesAsync(job.Source, range, ct))
                {
                    job.Total = page.Total;
                    await IngestAsync(job, page.Rows, range, scrobbles, ct);
                    job.Message = $"Fetched back to {page.Month.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}";
                    await jobDb.SaveChangesAsync(ct);
                }
            }
            else
            {
                if (job.StoredFile is null || !File.Exists(job.StoredFile))
                    throw new InvalidDataException("The uploaded file is no longer available. Please upload it again.");

                foreach (var chunk in ImportFormats.Read(job.Kind, job.StoredFile).Chunk(ChunkSize))
                {
                    await IngestAsync(job, chunk, range, scrobbles, ct);
                    await jobDb.SaveChangesAsync(ct);
                }

                // After the listens, so feedback can find recordings by the MusicBrainz ids they brought.
                if (job.Kind == ImportKind.ListenBrainzJson)
                    feedback = await ImportFeedbackAsync(job.UserId, ListenBrainzFormat.ReadFeedback(job.StoredFile).ToList(), scope.ServiceProvider, ct);
            }

            job.Status = ImportStatus.Completed;
            job.Message = $"Imported {job.Imported:N0} scrobbles, skipped {job.Duplicates:N0} already present"
                          + (job.Failed > 0 ? $", {job.Failed:N0} could not be read." : ".")
                          + (feedback.Stored + feedback.Unmatched > 0 ? $" Loved tracks: {feedback.Stored:N0} imported" : "")
                          + (feedback.Unmatched > 0 ? $", {feedback.Unmatched:N0} without a matching track." : feedback.Stored > 0 ? "." : "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: leave the job "Running" so it is resumed on the next start.
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or LastFmException or MalojaException or HttpRequestException or IOException)
        {
            logger.LogWarning(ex, "Import job {JobId} failed", job.Id);
            job.Status = ImportStatus.Failed;
            job.Message = Truncate(ex.Message, 1000);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import job {JobId} failed unexpectedly", job.Id);
            job.Status = ImportStatus.Failed;
            job.Message = Truncate($"Unexpected error: {ex.Message}", 1000);
        }

        job.FinishedAt = DateTime.UtcNow;
        await jobDb.SaveChangesAsync(CancellationToken.None);
        DeleteUpload(job.StoredFile);
        logger.LogInformation("Import job {JobId} finished: {Status} - {Message}", job.Id, job.Status, job.Message);
    }

    /// <summary>
    /// Whether the user may make MuseFM connect to its own network: administrators, who can do that with the metadata
    /// connectors anyway, or everyone when the server is set up that way.
    /// </summary>
    private static async Task<bool> MayReachPrivateAddressesAsync(string userId, IServiceProvider services)
    {
        if (services.GetRequiredService<IOptions<MuseFmOptions>>().Value.ImportFromPrivateAddresses) return true;
        var users = services.GetRequiredService<UserManager<AppUser>>();
        return await users.FindByIdAsync(userId) is { } user && await users.IsInRoleAsync(user, Roles.Admin);
    }

    /// <summary>Stores the scrobbles among the rows that lie in the timeframe of the import; the others are passed over.</summary>
    private static async Task IngestAsync(
        ImportJob job, IReadOnlyList<ImportRow> rows, ImportRange range, ScrobbleService scrobbles, CancellationToken ct)
    {
        var inputs = new List<ScrobbleInput>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Input is null)
            {
                job.Failed++;
                job.ErrorSample ??= Truncate(row.Error, 1000);
            }
            else if (range.Contains(row.Input.ListenedAt))
            {
                inputs.Add(row.Input);
            }
        }

        var outcomes = await scrobbles.IngestAsync(job.UserId, inputs, ct, fetchMetadata: false);
        foreach (var outcome in outcomes)
        {
            switch (outcome)
            {
                case ScrobbleOutcome.Accepted: job.Imported++; break;
                case ScrobbleOutcome.Duplicate: job.Duplicates++; break;
                default: job.Failed++; break;
            }
        }

        if (job.ErrorSample is null && outcomes.Any(o => o == ScrobbleOutcome.Invalid))
            job.ErrorSample = "Some entries had no artist, no title or an impossible date.";
        job.Processed += rows.Count;
    }

    /// <summary>
    /// Stores imported loves and hates. Each is matched by its MusicBrainz recording id first, then by track names (adding
    /// the track when it is new); feedback that names only a recording nobody scrobbled is skipped.
    /// </summary>
    private static async Task<(int Stored, int Unmatched)> ImportFeedbackAsync(
        string userId, IReadOnlyList<FeedbackImport> rows, IServiceProvider services, CancellationToken ct)
    {
        var feedback = services.GetRequiredService<FeedbackService>();
        var scrobbles = services.GetRequiredService<ScrobbleService>();
        int stored = 0, unmatched = 0;
        var byName = new List<(FeedbackImport Row, TrackRef Track)>();
        foreach (var row in rows)
        {
            if (row.RecordingMbid is { } mbid && await feedback.TrackByMbidAsync(mbid, ct) is { } trackId)
            {
                await feedback.SetAsync(userId, trackId, row.Score, row.CreatedAt, ct);
                stored++;
            }
            else if (row.Track is { } track)
            {
                byName.Add((row, track));
            }
            else
            {
                unmatched++;
            }
        }

        foreach (var chunk in byName.Chunk(500))
        {
            var ids = await scrobbles.ResolveTracksAsync(chunk.Select(x => x.Track).ToList(), create: true, ct);
            for (var i = 0; i < chunk.Length; i++)
            {
                if (ids[i] is not { } trackId)
                {
                    unmatched++;
                    continue;
                }

                await feedback.SetAsync(userId, trackId, chunk[i].Row.Score, chunk[i].Row.CreatedAt, ct);
                stored++;
            }
        }

        return (stored, unmatched);
    }

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];

    private void DeleteUpload(string? path)
    {
        if (path is null) return;
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not delete import upload {Path}", path);
        }
    }
}
