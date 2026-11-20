using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;

namespace MuseFM.Services;

public sealed record DayCount(DateOnly Day, int Count);

public sealed record Streak(DateOnly Start, DateOnly End)
{
    public int Days => End.DayNumber - Start.DayNumber + 1;
}

/// <summary>Daily scrobbles of the past 53 weeks (aligned to the first day of the week, ending today) with streaks.</summary>
public sealed record ListeningYear(IReadOnlyList<DayCount> Days, int ActiveDays, Streak? LongestStreak, Streak? CurrentStreak, DayCount? BusiestDay);

/// <summary>A share of all artist plays: the top artist, ranks 2-5, ranks 6-10, or everyone else.</summary>
/// <param name="Key">Color role: "top1", "top5", "top10" or "rest".</param>
public sealed record ShareSlice(string Key, string Label, int Plays, IReadOnlyList<string> Artists);

/// <summary>When a user listens, and to how many artists, within a range. Times are local to the profile.</summary>
/// <param name="NewArtistPlays">Plays of the artists first heard in the range, out of <paramref name="ArtistPlays"/>.</param>
/// <param name="ByWeekday">Seven counts, starting on <paramref name="FirstDayOfWeek"/>; ByWeekdayHour's rows likewise.</param>
public sealed record ListeningPatterns(
    DayOfWeek FirstDayOfWeek,
    int Scrobbles,
    int Days,
    int ActiveDays,
    DayCount? BusiestDay,
    int[] ByHour,
    int[] ByWeekday,
    int[,] ByWeekdayHour,
    int NewArtists,
    int NewArtistPlays,
    IReadOnlyList<ActivityBucket> Discoveries,
    int ArtistPlays,
    IReadOnlyList<ShareSlice> ArtistShare);

/// <summary>A scrobble and how long it played: what the client reported, else the track's length; null when unknown.</summary>
public readonly record struct Play(long At, int? Seconds);

/// <summary>One sitting: scrobbles with at most <see cref="InsightsService.SessionGapSeconds"/> of silence between them.</summary>
/// <param name="End">When the last track ended.</param>
public sealed record Session(long Start, long End, int Scrobbles)
{
    public long Seconds => End - Start;
}

/// <summary>How long a user listened within a range, and in how many sittings.</summary>
/// <param name="Timed">Scrobbles with a known length; the others count as an average one in <paramref name="Seconds"/>.</param>
/// <param name="Seconds">Time spent listening. Meaningless when nothing is <paramref name="Timed"/>.</param>
/// <param name="SessionSeconds">Length of all sessions together, short pauses included.</param>
public sealed record ListeningTime(int Scrobbles, int Timed, long Seconds, int Sessions, long SessionSeconds, Session? LongestSession)
{
    public bool IsKnown => Timed > 0;

    public bool IsEstimate => Timed < Scrobbles;
}

/// <summary>An artist the user came back to after a long silence.</summary>
/// <param name="Entry">Count: plays within the range since coming back.</param>
/// <param name="SilentDays">Days without a single play before <paramref name="ReturnedAt"/>.</param>
public sealed record Comeback(ChartEntry Entry, int SilentDays, long ReturnedAt);

/// <summary>An artist or track the user played a lot, but not lately.</summary>
/// <param name="Entry">Count: plays of all time.</param>
public sealed record Forgotten(ChartEntry Entry, long LastListen);

public enum ComparePeriod
{
    Day,
    Week,
    Month,
    Year,
}

/// <param name="Name">Short name for legends: "This week", or "Week of 3 Nov" for older ones.</param>
/// <param name="Description">The dates: "17–23 Nov 2026".</param>
public sealed record CompareSide(string Name, string Description, long From, long To);

/// <summary>Scrobbles of one hour, day or month in both periods.</summary>
public sealed record CompareBucket(string Label, string Title, int Current, int Previous);

/// <param name="PreviousRank">Null when it was not played in the previous period.</param>
public sealed record RankedEntry(ChartEntry Entry, int? PreviousRank, int PreviousCount);

/// <summary>A calendar period next to the one before it.</summary>
/// <param name="Ongoing">The current period has not ended; previous figures are then cut at the same point.</param>
/// <param name="PreviousTotals">The previous period up to the same point, so the numbers compare like for like.</param>
/// <param name="PreviousScrobblesInFull">All scrobbles of the previous period.</param>
/// <param name="CurrentBucketsSoFar">Buckets of the current period that have begun.</param>
public sealed record Comparison(
    ComparePeriod Period,
    int Offset,
    CompareSide Current,
    CompareSide Previous,
    bool Ongoing,
    Totals CurrentTotals,
    Totals PreviousTotals,
    int PreviousScrobblesInFull,
    int NewArtists,
    int PreviousNewArtists,
    IReadOnlyList<CompareBucket> Buckets,
    int CurrentBucketsSoFar,
    IReadOnlyList<RankedEntry> TopArtists,
    IReadOnlyList<RankedEntry> TopAlbums,
    IReadOnlyList<RankedEntry> TopTracks);

/// <summary>Listening patterns and period comparisons. Times are bucketed in the profile's time zone, DST-correct.</summary>
public sealed partial class InsightsService(AppDbContext db, StatsService stats)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const int CompareTop = 10;

    /// <summary>More silence than this between two tracks starts a new session.</summary>
    public const int SessionGapSeconds = 30 * 60;

    /// <summary>Days without a play after which an artist or track counts as forgotten, and a return as a rediscovery.</summary>
    public const int SilenceDays = 180;

    /// <summary>All-time plays an artist or track needs to count as a favourite.</summary>
    public const int FavouritePlays = 5;

    /// <summary>Stands in for a track's length in sessions when no length is known at all.</summary>
    private const int TypicalTrackSeconds = 210;

    public async Task<ListeningTime> ListeningTimeAsync(string userId, StatWindow window, CancellationToken ct = default)
    {
        var query = db.Scrobbles.AsNoTracking().Where(s => s.UserId == userId);
        if (window.From is { } from) query = query.Where(s => s.ListenedAt >= from);
        if (window.To is { } to) query = query.Where(s => s.ListenedAt < to);
        var rows = await query
            .OrderBy(s => s.ListenedAt)
            .Select(s => new { s.ListenedAt, Seconds = s.DurationSeconds ?? s.Track.LengthSeconds })
            .ToListAsync(ct);
        return Measure(rows.Select(r => new Play(r.ListenedAt, r.Seconds)).ToList());
    }

    /// <summary>Adds up listening time and splits the plays, oldest first, into sessions.</summary>
    internal static ListeningTime Measure(IReadOnlyList<Play> plays)
    {
        var timed = 0;
        long known = 0;
        foreach (var play in plays)
        {
            if (play.Seconds is not > 0) continue;
            timed++;
            known += play.Seconds.Value;
        }

        var typical = timed > 0 ? (int)(known / timed) : TypicalTrackSeconds;
        var total = known + (long)(plays.Count - timed) * typical;

        var sessions = 0;
        long sessionSeconds = 0;
        Session? longest = null;
        long start = 0, end = 0;
        var count = 0;

        void Close()
        {
            if (count == 0) return;
            var session = new Session(start, end, count);
            sessions++;
            sessionSeconds += session.Seconds;
            if (longest is null || session.Seconds >= longest.Seconds) longest = session; // ties: the most recent
        }

        foreach (var play in plays)
        {
            var ends = play.At + (play.Seconds is > 0 ? play.Seconds.Value : typical);
            if (count > 0 && play.At - end > SessionGapSeconds)
            {
                Close();
                count = 0;
            }

            if (count == 0) (start, end) = (play.At, ends);
            else end = Math.Max(end, ends);
            count++;
        }

        Close();
        return new ListeningTime(plays.Count, timed, total, sessions, sessionSeconds, longest);
    }

    /// <summary>
    /// Artists played within the window after at least <see cref="SilenceDays"/> without a play, by plays since they came
    /// back (the latest return, when there are several).
    /// </summary>
    public async Task<IReadOnlyList<Comeback>> RediscoveredAsync(string userId, StatWindow window, int count, CancellationToken ct = default)
    {
        var plays = db.Scrobbles.AsNoTracking().Where(s => s.UserId == userId);
        if (window.To is { } to) plays = plays.Where(s => s.ListenedAt < to);
        // The whole history up to the end of the window, grouped here: narrowing it in SQL to the artists heard in the
        // window (IN or EXISTS over the same join) takes SQLite tens of seconds, loading it all a few milliseconds.
        var credits = await plays
            .Join(db.TrackArtists, s => s.TrackId, ta => ta.TrackId, (s, ta) => new { ta.ArtistId, s.ListenedAt })
            .ToListAsync(ct);

        var silence = SilenceDays * 86400L;
        var earliest = window.From ?? long.MinValue;
        var found = new List<(int Id, int Plays, long Silent, long ReturnedAt)>();
        foreach (var artist in credits.GroupBy(c => c.ArtistId))
        {
            var stamps = artist.Select(c => c.ListenedAt).Order().ToList();
            for (var i = stamps.Count - 1; i > 0 && stamps[i] >= earliest; i--)
            {
                if (stamps[i] - stamps[i - 1] < silence) continue;
                found.Add((artist.Key, stamps.Count - i, stamps[i] - stamps[i - 1], stamps[i]));
                break;
            }
        }

        var top = found.OrderByDescending(f => f.Plays).ThenByDescending(f => f.Silent).ThenBy(f => f.Id).Take(count).ToList();
        var ids = top.Select(f => f.Id).ToList();
        var names = await db.Artists.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        return top
            .Select((f, i) => new Comeback(new ChartEntry(i + 1, f.Id, names.GetValueOrDefault(f.Id, "?"), [], f.Plays), (int)(f.Silent / 86400), f.ReturnedAt))
            .ToList();
    }

    /// <summary>
    /// The most played artists or tracks of all time (<see cref="FavouritePlays"/> or more) that the user has not played
    /// for <see cref="SilenceDays"/>.
    /// </summary>
    public async Task<IReadOnlyList<Forgotten>> ForgottenAsync(string userId, ChartKind kind, int count, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - SilenceDays * 86400L;
        var plays = db.Scrobbles.AsNoTracking().Where(s => s.UserId == userId);
        var perItem = kind == ChartKind.Artists
            ? plays.Join(db.TrackArtists, s => s.TrackId, ta => ta.TrackId, (s, ta) => new { Id = ta.ArtistId, s.ListenedAt })
            : plays.Select(s => new { Id = s.TrackId, s.ListenedAt });
        var rows = await perItem
            .GroupBy(x => x.Id)
            .Select(g => new { Id = g.Key, Count = g.Count(), Last = g.Max(x => x.ListenedAt) })
            .Where(x => x.Count >= FavouritePlays && x.Last < cutoff)
            .OrderByDescending(x => x.Count).ThenBy(x => x.Id)
            .Take(count)
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var titles = kind == ChartKind.Artists
            ? await db.Artists.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct)
            : await db.Tracks.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Title, ct);
        var artists = kind == ChartKind.Artists ? [] : await stats.TrackArtistsAsync(ids, ct);
        return rows
            .Select((r, i) => new Forgotten(new ChartEntry(i + 1, r.Id, titles.GetValueOrDefault(r.Id, "?"), artists.GetValueOrDefault(r.Id) ?? [], r.Count), r.Last))
            .ToList();
    }

    public async Task<(ListeningYear Year, ListeningPatterns Patterns)> GetInsightsAsync(
        string userId, StatPeriod period, TimeZoneInfo zone, DayOfWeek firstDayOfWeek = DayOfWeek.Monday, CancellationToken ct = default)
    {
        var clock = new LocalClock(zone);
        var today = clock.Today;
        // 53 week columns, the last one being the current week.
        var calendarStart = today.AddDays(-LocalClock.Weekday(today.DayOfWeek, firstDayOfWeek) - 52 * 7);
        long? loadFrom = period.From is { } from ? Math.Min(from, clock.StartOf(calendarStart)) : null;
        var stamps = await TimestampsAsync(userId, new StatWindow(loadFrom), ct);
        var perDay = PerDay(stamps, clock);

        // A current streak that reaches back to the start of the loaded data may go back further.
        var current = CurrentStreak(perDay, today);
        if (current is not null && loadFrom is { } loaded && current.Start <= DateOnly.FromDateTime(clock.ToLocal(loaded)))
            current = CurrentStreak(PerDay(await TimestampsAsync(userId, default, ct), clock), today);

        var yearDays = new List<DayCount>();
        for (var day = calendarStart; day <= today; day = day.AddDays(1))
            yearDays.Add(new DayCount(day, perDay.GetValueOrDefault(day)));
        var year = new ListeningYear(yearDays, yearDays.Count(d => d.Count > 0), LongestStreak(yearDays), current, Busiest(yearDays));

        return (year, await PatternsAsync(userId, period, clock, firstDayOfWeek, stamps, ct));
    }

    private async Task<ListeningPatterns> PatternsAsync(
        string userId, StatPeriod period, LocalClock clock, DayOfWeek firstDayOfWeek, List<long> stamps, CancellationToken ct)
    {
        var byHour = new int[24];
        var byWeekday = new int[7];
        var grid = new int[7, 24];
        var days = new Dictionary<DateOnly, int>();
        var scrobbles = 0;
        long? first = null;
        foreach (var stamp in stamps)
        {
            if (stamp < period.From || stamp >= period.To) continue;
            var local = clock.ToLocal(stamp);
            var weekday = LocalClock.Weekday(local.DayOfWeek, firstDayOfWeek);
            byHour[local.Hour]++;
            byWeekday[weekday]++;
            grid[weekday, local.Hour]++;
            var day = DateOnly.FromDateTime(local);
            days[day] = days.GetValueOrDefault(day) + 1;
            scrobbles++;
            if (first is null || stamp < first) first = stamp;
        }

        var today = clock.Today;
        var startDay = period.FirstDay ?? (first is { } start ? DateOnly.FromDateTime(clock.ToLocal(start)) : today);
        var endDay = period.LastDay < today ? period.LastDay : today;
        var busiest = days.Count == 0 ? null : days.OrderByDescending(d => d.Value).ThenByDescending(d => d.Key).Select(d => new DayCount(d.Key, d.Value)).First();

        // Artists heard for the first time ever within the range, bucketed like the activity chart.
        var firstListens = await FirstListensAsync(userId, ct);
        var discovered = firstListens.Where(f => f.Value >= (period.From ?? long.MinValue) && f.Value < (period.To ?? long.MaxValue)).ToList();
        var discoveryDays = discovered.GroupBy(f => (int)Math.Floor((f.Value + period.Offset) / 86400.0)).ToDictionary(g => g.Key, g => g.Count());

        var plays = await stats.RanksAsync(ChartKind.Artists, userId, period.Window, ct);
        var ranks = plays.OrderBy(r => r.Value.Rank).ToList();
        var topIds = ranks.Take(10).Select(r => r.Key).ToList();
        var names = await db.Artists.AsNoTracking().Where(a => topIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        ShareSlice Slice(string key, string label, int skip, int take)
        {
            var part = ranks.Skip(skip).Take(take).ToList();
            return new ShareSlice(key, label, part.Sum(p => p.Value.Count), part.Select(p => names.GetValueOrDefault(p.Key, "?")).ToList());
        }

        var others = Math.Max(0, ranks.Count - 10);
        var share = new List<ShareSlice>
        {
            Slice("top1", "#1", 0, 1),
            Slice("top5", "#2–5", 1, 4),
            Slice("top10", "#6–10", 5, 5),
            new("rest", $"{others.ToString("N0", Inv)} other artist{(others == 1 ? "" : "s")}", ranks.Skip(10).Sum(r => r.Value.Count), []),
        };

        return new ListeningPatterns(
            firstDayOfWeek,
            scrobbles,
            Math.Max(1, endDay.DayNumber - startDay.DayNumber + 1),
            days.Count,
            busiest,
            byHour,
            byWeekday,
            grid,
            discovered.Count,
            discovered.Sum(f => plays.GetValueOrDefault(f.Key).Count),
            StatsService.Buckets(period, discoveryDays),
            ranks.Sum(r => r.Value.Count),
            share.Where(s => s.Plays > 0).ToList());
    }

    public async Task<Comparison> CompareAsync(
        string userId, ComparePeriod period, int offset, TimeZoneInfo zone, DayOfWeek firstDayOfWeek = DayOfWeek.Monday, CancellationToken ct = default)
    {
        var clock = new LocalClock(zone);
        offset = Math.Clamp(offset, 0, MaxOffset(period));
        var today = clock.Today;
        var start = PeriodStart(period, today, offset, firstDayOfWeek);
        var end = Advance(period, start, 1);
        var previousStart = Advance(period, start, -1);

        var current = new CompareSide(Name(period, start, offset, previous: false), Describe(period, start, end), clock.ToUnix(start), clock.ToUnix(end));
        var previous = new CompareSide(Name(period, previousStart, offset, previous: true), Describe(period, previousStart, start), clock.ToUnix(previousStart), current.From);

        var now = clock.Now;
        var ongoing = now < current.To;
        var previousCut = ongoing ? Math.Clamp(previous.From + (now - current.From), previous.From, previous.To) : previous.To;

        var bucketCount = period switch
        {
            ComparePeriod.Day => 24,
            ComparePeriod.Week => 7,
            ComparePeriod.Month => Math.Max(DateTime.DaysInMonth(start.Year, start.Month), DateTime.DaysInMonth(previousStart.Year, previousStart.Month)),
            _ => 12,
        };
        var currentCounts = new int[bucketCount];
        var previousCounts = new int[bucketCount];
        var previousInFull = 0;
        foreach (var stamp in await TimestampsAsync(userId, new StatWindow(previous.From, current.To), ct))
        {
            var isCurrent = stamp >= current.From;
            var index = Index(period, clock.ToLocal(stamp), isCurrent ? start : previousStart);
            if (index < 0 || index >= bucketCount) continue;
            if (isCurrent)
            {
                currentCounts[index]++;
            }
            else
            {
                previousCounts[index]++;
                previousInFull++;
            }
        }

        var buckets = Enumerable.Range(0, bucketCount)
            .Select(i => new CompareBucket(BucketLabel(period, i, firstDayOfWeek), BucketTitle(period, i, firstDayOfWeek), currentCounts[i], previousCounts[i]))
            .ToList();
        var soFar = ongoing ? Math.Min(bucketCount, Index(period, clock.ToLocal(now), start) + 1) : bucketCount;

        var currentWindow = new StatWindow(current.From, current.To);
        var firstListens = await FirstListensAsync(userId, ct);

        return new Comparison(
            period,
            offset,
            current,
            previous,
            ongoing,
            await stats.GetTotalsAsync(userId, currentWindow, ct),
            await stats.GetTotalsAsync(userId, new StatWindow(previous.From, previousCut), ct),
            previousInFull,
            firstListens.Values.Count(t => t >= current.From && t < current.To),
            firstListens.Values.Count(t => t >= previous.From && t < previousCut),
            buckets,
            soFar,
            await RankedAsync(ChartKind.Artists, userId, currentWindow, previous, ct),
            await RankedAsync(ChartKind.Albums, userId, currentWindow, previous, ct),
            await RankedAsync(ChartKind.Tracks, userId, currentWindow, previous, ct));
    }

    /// <summary>The current top entries, each with its place in the whole previous period.</summary>
    private async Task<IReadOnlyList<RankedEntry>> RankedAsync(ChartKind kind, string userId, StatWindow window, CompareSide previous, CancellationToken ct)
    {
        var top = kind switch
        {
            ChartKind.Artists => await stats.TopArtistsAsync(userId, window, 1, CompareTop, ct),
            ChartKind.Albums => await stats.TopAlbumsAsync(userId, window, 1, CompareTop, ct: ct),
            _ => await stats.TopTracksAsync(userId, window, 1, CompareTop, ct: ct),
        };
        var before = await stats.RanksAsync(kind, userId, new StatWindow(previous.From, previous.To), ct);
        return top.Items
            .Select(e => before.TryGetValue(e.Id, out var was) ? new RankedEntry(e, was.Rank, was.Count) : new RankedEntry(e, null, 0))
            .ToList();
    }

    private Task<List<long>> TimestampsAsync(string userId, StatWindow window, CancellationToken ct)
    {
        var query = db.Scrobbles.AsNoTracking().Where(s => s.UserId == userId);
        if (window.From is { } from) query = query.Where(s => s.ListenedAt >= from);
        if (window.To is { } to) query = query.Where(s => s.ListenedAt < to);
        return query.Select(s => s.ListenedAt).ToListAsync(ct);
    }

    /// <summary>When the user first heard each artist, by artist id.</summary>
    private Task<Dictionary<int, long>> FirstListensAsync(string userId, CancellationToken ct) =>
        db.Scrobbles.AsNoTracking()
            .Where(s => s.UserId == userId)
            .Join(db.TrackArtists, s => s.TrackId, ta => ta.TrackId, (s, ta) => new { ta.ArtistId, s.ListenedAt })
            .GroupBy(x => x.ArtistId)
            .Select(g => new { Id = g.Key, First = g.Min(x => x.ListenedAt) })
            .ToDictionaryAsync(x => x.Id, x => x.First, ct);

    private static Dictionary<DateOnly, int> PerDay(List<long> stamps, LocalClock clock)
    {
        var days = new Dictionary<DateOnly, int>();
        foreach (var stamp in stamps)
        {
            var day = DateOnly.FromDateTime(clock.ToLocal(stamp));
            days[day] = days.GetValueOrDefault(day) + 1;
        }

        return days;
    }

    /// <summary>Consecutive days with scrobbles up to today, or up to yesterday while today has none yet.</summary>
    internal static Streak? CurrentStreak(IReadOnlyDictionary<DateOnly, int> days, DateOnly today)
    {
        var end = days.ContainsKey(today) ? today : today.AddDays(-1);
        if (!days.ContainsKey(end)) return null;
        var start = end;
        while (days.ContainsKey(start.AddDays(-1))) start = start.AddDays(-1);
        return new Streak(start, end);
    }

    internal static Streak? LongestStreak(IReadOnlyList<DayCount> days)
    {
        Streak? best = null;
        DateOnly? runStart = null;
        for (var i = 0; i <= days.Count; i++)
        {
            var active = i < days.Count && days[i].Count > 0;
            if (active && runStart is null) runStart = days[i].Day;
            if (active || runStart is null) continue;
            var run = new Streak(runStart.Value, days[i - 1].Day);
            if (best is null || run.Days >= best.Days) best = run; // ties: the most recent
            runStart = null;
        }

        return best;
    }

    private static DayCount? Busiest(IReadOnlyList<DayCount> days) =>
        days.Where(d => d.Count > 0).OrderByDescending(d => d.Count).ThenByDescending(d => d.Day).FirstOrDefault();

    private static int MaxOffset(ComparePeriod period) => period switch
    {
        ComparePeriod.Day => 3660,
        ComparePeriod.Week => 520,
        ComparePeriod.Month => 240,
        _ => 50,
    };

    private static DateTime PeriodStart(ComparePeriod period, DateOnly today, int offset, DayOfWeek firstDayOfWeek)
    {
        var midnight = today.ToDateTime(TimeOnly.MinValue);
        return period switch
        {
            ComparePeriod.Day => midnight.AddDays(-offset),
            ComparePeriod.Week => midnight.AddDays(-LocalClock.Weekday(today.DayOfWeek, firstDayOfWeek) - 7 * offset),
            ComparePeriod.Month => new DateTime(today.Year, today.Month, 1).AddMonths(-offset),
            _ => new DateTime(today.Year - offset, 1, 1),
        };
    }

    private static DateTime Advance(ComparePeriod period, DateTime start, int count) => period switch
    {
        ComparePeriod.Day => start.AddDays(count),
        ComparePeriod.Week => start.AddDays(7 * count),
        ComparePeriod.Month => start.AddMonths(count),
        _ => start.AddYears(count),
    };

    private static int Index(ComparePeriod period, DateTime local, DateTime start) => period switch
    {
        ComparePeriod.Day => local.Hour,
        ComparePeriod.Week => (local.Date - start).Days,
        ComparePeriod.Month => local.Day - 1,
        _ => local.Month - 1,
    };

    private static string BucketLabel(ComparePeriod period, int i, DayOfWeek firstDayOfWeek) => period switch
    {
        ComparePeriod.Day => i % 3 == 0 ? i.ToString("00", Inv) : "",
        ComparePeriod.Week => CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames[((int)firstDayOfWeek + i) % 7],
        ComparePeriod.Month => i == 0 || (i + 1) % 5 == 0 ? (i + 1).ToString(Inv) : "",
        _ => CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames[i],
    };

    private static string BucketTitle(ComparePeriod period, int i, DayOfWeek firstDayOfWeek) => period switch
    {
        ComparePeriod.Day => $"{i:00}:00–{(i + 1) % 24:00}:00",
        ComparePeriod.Week => CultureInfo.InvariantCulture.DateTimeFormat.DayNames[((int)firstDayOfWeek + i) % 7],
        ComparePeriod.Month => $"Day {i + 1}",
        _ => CultureInfo.InvariantCulture.DateTimeFormat.MonthNames[i],
    };

    private static string Name(ComparePeriod period, DateTime start, int offset, bool previous) => (period, offset, previous) switch
    {
        (ComparePeriod.Day, 0, false) => "Today",
        (ComparePeriod.Day, 0, true) => "Yesterday",
        (ComparePeriod.Week, 0, false) => "This week",
        (ComparePeriod.Week, 0, true) => "Last week",
        (ComparePeriod.Month, 0, false) => "This month",
        (ComparePeriod.Month, 0, true) => "Last month",
        (ComparePeriod.Year, 0, false) => "This year",
        (ComparePeriod.Year, 0, true) => "Last year",
        (ComparePeriod.Day, _, _) => start.ToString("ddd d MMM", Inv),
        (ComparePeriod.Week, _, _) => "Week of " + start.ToString("d MMM", Inv),
        (ComparePeriod.Month, _, _) => start.ToString("MMM yyyy", Inv),
        _ => start.ToString("yyyy", Inv),
    };

    private static string Describe(ComparePeriod period, DateTime start, DateTime end)
    {
        var last = end.AddDays(-1);
        return period switch
        {
            ComparePeriod.Day => start.ToString("ddd, d MMM yyyy", Inv),
            ComparePeriod.Week when start.Year != last.Year => $"{start.ToString("d MMM yyyy", Inv)} – {last.ToString("d MMM yyyy", Inv)}",
            ComparePeriod.Week when start.Month != last.Month => $"{start.ToString("d MMM", Inv)} – {last.ToString("d MMM yyyy", Inv)}",
            ComparePeriod.Week => $"{start.Day}–{last.ToString("d MMM yyyy", Inv)}",
            ComparePeriod.Month => start.ToString("MMMM yyyy", Inv),
            _ => start.ToString("yyyy", Inv),
        };
    }
}
