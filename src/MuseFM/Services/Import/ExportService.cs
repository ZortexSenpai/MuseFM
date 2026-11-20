using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;

namespace MuseFM.Services.Import;

/// <param name="ArtistMbids">MusicBrainz ids in the order of <paramref name="Artists"/>; null where unknown.</param>
public sealed record ExportScrobble(
    long ListenedAt,
    string Title,
    IReadOnlyList<string> Artists,
    string? Album,
    IReadOnlyList<string> AlbumArtists,
    int? LengthSeconds,
    int? DurationSeconds,
    string? Origin,
    string? TrackMbid = null,
    string? AlbumMbid = null,
    IReadOnlyList<string?>? ArtistMbids = null);

/// <summary>A love (score 1) or hate (-1) of a track.</summary>
public sealed record ExportFeedback(long CreatedAt, int Score, string Title, IReadOnlyList<string> Artists, string? TrackMbid);

/// <summary>Streams a user's full history in batches, oldest first.</summary>
public sealed class ExportService(AppDbContext db)
{
    private const int BatchSize = 2000;

    public Task WriteMalojaAsync(string userId, Stream output, CancellationToken ct) =>
        MalojaFormat.WriteAsync(ReadBatchesAsync(userId, ct), output, ct);

    public Task WriteLastFmCsvAsync(string userId, Stream output, CancellationToken ct) =>
        LastFmCsvFormat.WriteAsync(ReadBatchesAsync(userId, ct), output, ct);

    /// <summary>Listens and feedback as a .zip laid out like a ListenBrainz export.</summary>
    public async Task WriteListenBrainzAsync(string userId, string userName, Stream output, CancellationToken ct)
    {
        // ZipArchive writes synchronously, which the response body doesn't allow: build the archive in a temp file first.
        var path = Path.GetTempFileName();
        await using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        await ListenBrainzFormat.WriteAsync(ReadBatchesAsync(userId, ct), await FeedbackAsync(userId, ct), userName, file, ct);
        file.Position = 0;
        await file.CopyToAsync(output, ct);
    }

    public async Task<IReadOnlyList<ExportFeedback>> FeedbackAsync(string userId, CancellationToken ct = default)
    {
        var rows = await db.TrackFeedback.AsNoTracking()
            .Where(f => f.UserId == userId)
            .OrderBy(f => f.CreatedAt)
            .Select(f => new { f.CreatedAt, f.Score, f.TrackId, f.Track.Title, f.Track.MusicBrainzId })
            .ToListAsync(ct);
        var trackIds = rows.Select(r => r.TrackId).ToList();
        var artists = (await db.TrackArtists.AsNoTracking()
                .Where(ta => trackIds.Contains(ta.TrackId))
                .OrderBy(ta => ta.Position)
                .Select(ta => new { ta.TrackId, ta.Artist.Name })
                .ToListAsync(ct))
            .ToLookup(x => x.TrackId, x => x.Name);
        return rows.Select(r => new ExportFeedback(r.CreatedAt, r.Score, r.Title, artists[r.TrackId].ToList(), r.MusicBrainzId)).ToList();
    }

    public async IAsyncEnumerable<IReadOnlyList<ExportScrobble>> ReadBatchesAsync(string userId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var after = long.MinValue;
        while (true)
        {
            // Keyset pagination: (UserId, ListenedAt) is unique.
            var rows = await db.Scrobbles.AsNoTracking()
                .Where(s => s.UserId == userId && s.ListenedAt > after)
                .OrderBy(s => s.ListenedAt)
                .Take(BatchSize)
                .Select(s => new
                {
                    s.ListenedAt,
                    s.TrackId,
                    s.Track.Title,
                    s.Track.LengthSeconds,
                    TrackMbid = s.Track.MusicBrainzId,
                    s.AlbumId,
                    Album = s.Album != null ? s.Album.Title : null,
                    AlbumMbid = s.Album != null ? s.Album.MusicBrainzId : null,
                    s.DurationSeconds,
                    s.Origin,
                })
                .ToListAsync(ct);
            if (rows.Count == 0) yield break;

            var trackIds = rows.Select(r => r.TrackId).Distinct().ToList();
            var albumIds = rows.Where(r => r.AlbumId != null).Select(r => r.AlbumId!.Value).Distinct().ToList();
            var trackArtists = (await db.TrackArtists.AsNoTracking()
                    .Where(ta => trackIds.Contains(ta.TrackId))
                    .OrderBy(ta => ta.Position)
                    .Select(ta => new { ta.TrackId, ta.Artist.Name, ta.Artist.MusicBrainzId })
                    .ToListAsync(ct))
                .ToLookup(x => x.TrackId);
            var albumArtists = (await db.AlbumArtists.AsNoTracking()
                    .Where(aa => albumIds.Contains(aa.AlbumId))
                    .OrderBy(aa => aa.Position)
                    .Select(aa => new { aa.AlbumId, aa.Artist.Name })
                    .ToListAsync(ct))
                .ToLookup(x => x.AlbumId, x => x.Name);

            yield return rows.Select(r => new ExportScrobble(
                r.ListenedAt,
                r.Title,
                trackArtists[r.TrackId].Select(a => a.Name).ToList(),
                r.Album,
                r.AlbumId is { } albumId ? albumArtists[albumId].ToList() : [],
                r.LengthSeconds,
                r.DurationSeconds,
                r.Origin,
                r.TrackMbid,
                r.AlbumMbid,
                trackArtists[r.TrackId].Select(a => a.MusicBrainzId).ToList())).ToList();

            after = rows[^1].ListenedAt;
        }
    }
}
