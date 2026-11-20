using Microsoft.EntityFrameworkCore;

namespace MuseFM.Services;

/// <param name="Number">Position in the user's history: 1 is the first scrobble.</param>
public sealed record Milestone(int Number, ScrobbleRow Scrobble);

/// <param name="Reached">Round numbers the user has passed, the latest first.</param>
/// <param name="Next">The round number they are heading for.</param>
public sealed record Milestones(int Scrobbles, IReadOnlyList<Milestone> Reached, int Next);

/// <summary>Today's date in an earlier year. The entries' counts are scrobbles on that day.</summary>
public sealed record DayInHistory(DateOnly Day, int Scrobbles, ChartEntry TopArtist, ChartEntry TopTrack);

public sealed record TrackDay(CatalogItem Track, DateOnly Day, int Plays);

/// <summary>The same track scrobbled several times with nothing else in between.</summary>
public sealed record TrackRun(CatalogItem Track, int Plays, long Start);

/// <summary>How much a user replays tracks within a range.</summary>
/// <param name="PlayedOnce">Tracks with exactly one scrobble in the range.</param>
/// <param name="MostInADay">Null unless some track was played at least twice on one day; likewise <paramref name="LongestRun"/>.</param>
public sealed record Repeats(int Scrobbles, int Tracks, int PlayedOnce, TrackDay? MostInADay, TrackRun? LongestRun);

/// <summary>The artists a user keeps coming back to within a range.</summary>
/// <param name="MostDays">Count: days with at least one play.</param>
/// <param name="WeeksAtTop">Count: weeks in which the artist was the most played one.</param>
/// <param name="Weeks">Weeks with scrobbles in the range.</param>
public sealed record Regulars(IReadOnlyList<ChartEntry> MostDays, IReadOnlyList<ChartEntry> WeeksAtTop, int Weeks);

public sealed partial class InsightsService
{
    /// <summary>1, 1,000, 5,000, 10,000, 25,000, 50,000, 75,000, 100,000 and every 50,000 after that.</summary>
    internal static IEnumerable<int> MilestoneNumbers()
    {
        foreach (var number in (int[])[1, 1_000, 5_000, 10_000, 25_000, 50_000, 75_000, 100_000]) yield return number;
        for (var number = 150_000; ; number += 50_000) yield return number;
    }

    public async Task<Milestones> MilestonesAsync(string userId, CancellationToken ct = default)
    {
        var total = await stats.CountAsync(userId, ct: ct);
        var reached = new List<Milestone>();
        var next = 1;
        foreach (var number in MilestoneNumbers())
        {
            next = number;
            if (number > total) break;
            if (await stats.NthAsync(userId, number, ct) is { } scrobble) reached.Add(new Milestone(number, scrobble));
        }

        reached.Reverse();
        return new Milestones(total, reached, next);
    }

    /// <summary>What the user played on today's date in each earlier year, the latest year first; years without scrobbles are left out.</summary>
    public async Task<IReadOnlyList<DayInHistory>> OnThisDayAsync(string userId, TimeZoneInfo zone, CancellationToken ct = default)
    {
        var clock = new LocalClock(zone);
        var today = clock.Today;
        var first = await db.Scrobbles.AsNoTracking().Where(s => s.UserId == userId).MinAsync(s => (long?)s.ListenedAt, ct);
        if (first is null) return [];

        var days = new List<(DateOnly Day, List<int> Tracks)>();
        for (var year = today.Year - 1; year >= clock.ToLocal(first.Value).Year; year--)
        {
            if (today is { Month: 2, Day: 29 } && !DateTime.IsLeapYear(year)) continue;
            var day = new DateOnly(year, today.Month, today.Day);
            var (from, to) = (clock.StartOf(day), clock.StartOf(day.AddDays(1)));
            var tracks = await db.Scrobbles.AsNoTracking()
                .Where(s => s.UserId == userId && s.ListenedAt >= from && s.ListenedAt < to)
                .Select(s => s.TrackId)
                .ToListAsync(ct);
            if (tracks.Count > 0) days.Add((day, tracks));
        }

        var credits = await stats.TrackArtistsAsync(days.SelectMany(d => d.Tracks), ct);
        var tops = days.Select(d => (
                d.Day,
                d.Tracks.Count,
                Track: d.Tracks.GroupBy(id => id).Select(g => (Id: g.Key, Plays: g.Count())).OrderByDescending(t => t.Plays).ThenBy(t => t.Id).First(),
                Artist: d.Tracks.SelectMany(id => credits.GetValueOrDefault(id) ?? []).GroupBy(a => a).Select(g => (Ref: g.Key, Plays: g.Count()))
                    .OrderByDescending(a => a.Plays).ThenBy(a => a.Ref.Id).FirstOrDefault()))
            .ToList();
        var trackIds = tops.Select(t => t.Track.Id).ToList();
        var titles = await db.Tracks.AsNoTracking().Where(t => trackIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Title, ct);

        return tops
            .Select(t => new DayInHistory(
                t.Day,
                t.Count,
                new ChartEntry(1, t.Artist.Ref?.Id ?? 0, t.Artist.Ref?.Name ?? "?", [], t.Artist.Plays),
                new ChartEntry(1, t.Track.Id, titles.GetValueOrDefault(t.Track.Id, "?"), credits.GetValueOrDefault(t.Track.Id) ?? [], t.Track.Plays)))
            .ToList();
    }

    public async Task<Repeats> RepeatsAsync(string userId, StatWindow window, TimeZoneInfo zone, CancellationToken ct = default)
    {
        var query = db.Scrobbles.AsNoTracking().Where(s => s.UserId == userId);
        if (window.From is { } from) query = query.Where(s => s.ListenedAt >= from);
        if (window.To is { } to) query = query.Where(s => s.ListenedAt < to);
        var plays = await query.OrderBy(s => s.ListenedAt).Select(s => new { s.ListenedAt, s.TrackId }).ToListAsync(ct);

        var clock = new LocalClock(zone);
        var perTrack = new Dictionary<int, int>();
        var perDay = new Dictionary<(DateOnly Day, int Track), int>();
        // Later ones win ties, so the most recent day and run are shown.
        (DateOnly Day, int Track, int Plays) bestDay = default;
        (int Track, int Plays, long Start) bestRun = default, run = default;
        foreach (var play in plays)
        {
            perTrack[play.TrackId] = perTrack.GetValueOrDefault(play.TrackId) + 1;

            var key = (DateOnly.FromDateTime(clock.ToLocal(play.ListenedAt)), play.TrackId);
            var onDay = perDay[key] = perDay.GetValueOrDefault(key) + 1;
            if (onDay >= bestDay.Plays) bestDay = (key.Item1, play.TrackId, onDay);

            run = run.Plays > 0 && run.Track == play.TrackId ? run with { Plays = run.Plays + 1 } : (play.TrackId, 1, play.ListenedAt);
            if (run.Plays >= bestRun.Plays) bestRun = run;
        }

        return new Repeats(
            plays.Count,
            perTrack.Count,
            perTrack.Count(t => t.Value == 1),
            bestDay.Plays >= 2 && await stats.TrackAsync(bestDay.Track, ct) is { } dayTrack ? new TrackDay(dayTrack, bestDay.Day, bestDay.Plays) : null,
            bestRun.Plays >= 2 && await stats.TrackAsync(bestRun.Track, ct) is { } runTrack ? new TrackRun(runTrack, bestRun.Plays, bestRun.Start) : null);
    }

    /// <summary>The artists played on the most days, and those that were the most played artist of the most weeks.</summary>
    public async Task<Regulars> RegularsAsync(
        string userId, StatWindow window, TimeZoneInfo zone, int count, DayOfWeek firstDayOfWeek = DayOfWeek.Monday, CancellationToken ct = default)
    {
        var query = db.Scrobbles.AsNoTracking().Where(s => s.UserId == userId);
        if (window.From is { } from) query = query.Where(s => s.ListenedAt >= from);
        if (window.To is { } to) query = query.Where(s => s.ListenedAt < to);
        var credits = await query
            .Join(db.TrackArtists, s => s.TrackId, ta => ta.TrackId, (s, ta) => new { ta.ArtistId, s.ListenedAt })
            .ToListAsync(ct);

        var clock = new LocalClock(zone);
        var days = new Dictionary<int, HashSet<int>>();
        var weeks = new Dictionary<int, Dictionary<int, int>>();
        foreach (var credit in credits)
        {
            var day = DateOnly.FromDateTime(clock.ToLocal(credit.ListenedAt));
            if (!days.TryGetValue(credit.ArtistId, out var played)) days[credit.ArtistId] = played = [];
            played.Add(day.DayNumber);

            var week = day.DayNumber - LocalClock.Weekday(day.DayOfWeek, firstDayOfWeek);
            if (!weeks.TryGetValue(week, out var inWeek)) weeks[week] = inWeek = [];
            inWeek[credit.ArtistId] = inWeek.GetValueOrDefault(credit.ArtistId) + 1;
        }

        // Ties go to the lower id, like in the charts.
        var mostDays = days.Select(d => (Id: d.Key, Count: d.Value.Count))
            .OrderByDescending(d => d.Count).ThenBy(d => d.Id).Take(count).ToList();
        var atTop = weeks.Values.Select(w => w.OrderByDescending(a => a.Value).ThenBy(a => a.Key).First().Key)
            .GroupBy(id => id).Select(g => (Id: g.Key, Count: g.Count()))
            .OrderByDescending(a => a.Count).ThenBy(a => a.Id).Take(count).ToList();

        var ids = mostDays.Concat(atTop).Select(a => a.Id).Distinct().ToList();
        var names = await db.Artists.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        List<ChartEntry> Entries(List<(int Id, int Count)> rows) =>
            rows.Select((r, i) => new ChartEntry(i + 1, r.Id, names.GetValueOrDefault(r.Id, "?"), [], r.Count)).ToList();

        return new Regulars(Entries(mostDays), Entries(atTop), weeks.Count);
    }
}
