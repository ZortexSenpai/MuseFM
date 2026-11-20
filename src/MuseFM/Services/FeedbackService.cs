using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;

namespace MuseFM.Services;

public sealed record FeedbackRow(int TrackId, string Title, IReadOnlyList<ArtistRef> Artists, string? MusicBrainzId, int Score, long CreatedAt, int Scrobbles);

/// <summary>Loves and hates of tracks: Last.fm loves, ListenBrainz recording feedback and the love buttons.</summary>
public sealed class FeedbackService(AppDbContext db, StatsService stats)
{
    /// <summary>Sets a user's feedback on a track: <see cref="TrackFeedback.Love"/>, <see cref="TrackFeedback.Hate"/>, or 0 to clear it.</summary>
    /// <param name="at">When it was given; now if null. Repeating the same feedback keeps the original time.</param>
    public async Task SetAsync(string userId, int trackId, int score, long? at = null, CancellationToken ct = default)
    {
        if (score == 0)
        {
            await db.TrackFeedback.Where(f => f.UserId == userId && f.TrackId == trackId).ExecuteDeleteAsync(ct);
            return;
        }

        score = Math.Sign(score);
        var when = at ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // One statement, so two clients loving the same track at once can't collide on the key.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "TrackFeedback" ("UserId", "TrackId", "Score", "CreatedAt") VALUES ({userId}, {trackId}, {score}, {when})
            ON CONFLICT ("UserId", "TrackId") DO UPDATE SET
                "CreatedAt" = CASE WHEN "Score" = excluded."Score" THEN MIN("CreatedAt", excluded."CreatedAt") ELSE excluded."CreatedAt" END,
                "Score" = excluded."Score"
            """, ct);
    }

    public Task<int?> ScoreAsync(string userId, int trackId, CancellationToken ct = default) =>
        db.TrackFeedback.AsNoTracking().Where(f => f.UserId == userId && f.TrackId == trackId).Select(f => (int?)f.Score).FirstOrDefaultAsync(ct);

    /// <summary>Which of these tracks the user loves.</summary>
    public async Task<IReadOnlySet<int>> LovedAmongAsync(string userId, IEnumerable<int> trackIds, CancellationToken ct = default)
    {
        var ids = trackIds.Distinct().ToList();
        if (ids.Count == 0) return new HashSet<int>();
        return (await db.TrackFeedback.AsNoTracking()
                .Where(f => f.UserId == userId && f.Score == TrackFeedback.Love && ids.Contains(f.TrackId))
                .Select(f => f.TrackId)
                .ToListAsync(ct))
            .ToHashSet();
    }

    public Task<int> CountAsync(string userId, int score = TrackFeedback.Love, CancellationToken ct = default) =>
        db.TrackFeedback.CountAsync(f => f.UserId == userId && f.Score == score, ct);

    /// <summary>The user's loved (or hated) tracks, newest first; <paramref name="score"/> 0 lists both.</summary>
    public async Task<Paged<FeedbackRow>> ListAsync(string userId, int score, int page, int pageSize, CancellationToken ct = default)
    {
        var (items, total) = await SliceAsync(userId, score, (page - 1) * pageSize, pageSize, ct);
        return new Paged<FeedbackRow>(items, page, pageSize, total);
    }

    /// <summary>Like <see cref="ListAsync"/>, by offset.</summary>
    public async Task<(IReadOnlyList<FeedbackRow> Items, int Total)> SliceAsync(string userId, int score, int skip, int take, CancellationToken ct = default)
    {
        var query = db.TrackFeedback.AsNoTracking().Where(f => f.UserId == userId);
        if (score != 0) query = query.Where(f => f.Score == score);
        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.TrackId)
            .Skip(skip).Take(take)
            .Select(f => new
            {
                f.TrackId,
                f.Track.Title,
                f.Track.MusicBrainzId,
                f.Score,
                f.CreatedAt,
                Scrobbles = db.Scrobbles.Count(s => s.UserId == userId && s.TrackId == f.TrackId),
            })
            .ToListAsync(ct);
        var artists = await stats.TrackArtistsAsync(rows.Select(r => r.TrackId), ct);
        var items = rows.Select(r => new FeedbackRow(
            r.TrackId, r.Title, artists.GetValueOrDefault(r.TrackId) ?? [], r.MusicBrainzId, r.Score, r.CreatedAt, r.Scrobbles)).ToList();
        return (items, total);
    }

    /// <summary>The track that was scrobbled with this MusicBrainz recording id, if any.</summary>
    public Task<int?> TrackByMbidAsync(string mbid, CancellationToken ct = default) =>
        db.Tracks.AsNoTracking().Where(t => t.MusicBrainzId == mbid).OrderBy(t => t.Id).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);
}
