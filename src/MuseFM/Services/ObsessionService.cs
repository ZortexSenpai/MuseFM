using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;

namespace MuseFM.Services;

/// <summary>How long an obsession stays on the profile: some days, some months, or (neither) until the user changes it.</summary>
public sealed record ObsessionLength(string Slug, string Label, int Days = 0, int Months = 0)
{
    public DateTime? EndFrom(DateTime start) =>
        Days > 0 ? start.AddDays(Days) : Months > 0 ? start.AddMonths(Months) : null;
}

/// <param name="Scrobbles">How often the user has played the track, in all.</param>
/// <param name="EndsAt">When it stops being shown; null when it stays until the user changes it.</param>
public sealed record CurrentObsession(CatalogItem Track, DateTime StartedAt, DateTime? EndsAt, int Scrobbles);

/// <summary>Obsessions: the one track a user is hooked on right now, shown on their profile for as long as they chose.</summary>
public sealed class ObsessionService(AppDbContext db, StatsService stats)
{
    public static IReadOnlyList<ObsessionLength> Lengths { get; } =
    [
        new("1d", "for 1 day", Days: 1),
        new("3d", "for 3 days", Days: 3),
        new("1w", "for 1 week", Days: 7),
        new("2w", "for 2 weeks", Days: 14),
        new("1m", "for 1 month", Months: 1),
        new("3m", "for 3 months", Months: 3),
        new("always", "until I change it"),
    ];

    public const string DefaultLength = "1w";

    public static ObsessionLength? FindLength(string? slug) => Lengths.FirstOrDefault(l => l.Slug == slug);

    private IQueryable<Obsession> Active(string userId, DateTime now) =>
        db.Obsessions.Where(o => o.UserId == userId && o.StartedAt <= now && (o.EndsAt == null || o.EndsAt > now));

    /// <summary>The obsession the user's profile shows now, if any.</summary>
    public async Task<CurrentObsession?> CurrentAsync(string userId, CancellationToken ct = default)
    {
        var obsession = await Active(userId, DateTime.UtcNow).AsNoTracking()
            .OrderByDescending(o => o.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (obsession is null || await stats.TrackAsync(obsession.TrackId, ct) is not { } track) return null;
        var summary = await stats.SummaryAsync(userId, StatSubject.Track(track.Id), default, ct);
        return new CurrentObsession(track, obsession.StartedAt, obsession.EndsAt, summary?.AllTimeScrobbles ?? 0);
    }

    /// <summary>
    /// Makes <paramref name="trackId"/> the user's obsession for <paramref name="length"/>, from now. The current
    /// obsession ends; if it is the same track, it carries on with the new length and keeps its start.
    /// </summary>
    public async Task SetAsync(string userId, int trackId, ObsessionLength length, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var active = await Active(userId, now).ToListAsync(ct);
        var same = active.Where(o => o.TrackId == trackId).MaxBy(o => o.StartedAt);
        foreach (var other in active.Where(o => o != same)) other.EndsAt = now;
        if (same is not null) same.EndsAt = length.EndFrom(now);
        else db.Obsessions.Add(new Obsession { UserId = userId, TrackId = trackId, StartedAt = now, EndsAt = length.EndFrom(now) });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Takes the current obsession off the profile now.</summary>
    public Task EndAsync(string userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return Active(userId, now).ExecuteUpdateAsync(s => s.SetProperty(o => o.EndsAt, now), ct);
    }
}
