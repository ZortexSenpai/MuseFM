using System.Globalization;

namespace MuseFM.Services;

/// <summary>The rolling presets offered on every statistics page.</summary>
public enum StatRange
{
    Week,
    Month,
    Year,
    All,
}

public enum PeriodKind
{
    /// <summary>A rolling preset (7 days, 30 days, 12 months) or all time.</summary>
    Preset,

    /// <summary>A calendar year.</summary>
    Year,

    /// <summary>A calendar month.</summary>
    Month,

    /// <summary>Any span of whole days.</summary>
    Custom,
}

/// <summary>
/// A statistics window of whole local days of the profile owner: a rolling preset, a calendar year or month, or any span
/// of days. Calendar and custom bounds are exact across DST changes; presets, and the per-day grouping done in SQL
/// (<see cref="Offset"/>), use the zone's current UTC offset, which is exact except around DST changes.
/// </summary>
public sealed class StatPeriod
{
    private const int UnixEpochDayNumber = 719162; // DateOnly(1970, 1, 1).DayNumber
    private const string SpanSeparator = "..";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly DateOnly Earliest = new(1970, 1, 1);
    private static readonly DateOnly Latest = new(9998, 12, 31);

    private StatPeriod(PeriodKind kind, StatRange? preset, DateOnly? firstDay, DateOnly lastDay, long? from, long? to, long offset, DateOnly today)
    {
        Kind = kind;
        Preset = preset;
        FirstDay = firstDay;
        LastDay = lastDay;
        From = from;
        To = to;
        Offset = offset;
        TodayDate = today;
    }

    public PeriodKind Kind { get; }

    /// <summary>The preset; null for calendar and custom periods.</summary>
    public StatRange? Preset { get; }

    /// <summary>First local day; null for all time.</summary>
    public DateOnly? FirstDay { get; }

    /// <summary>Last local day, inclusive. Today for presets.</summary>
    public DateOnly LastDay { get; }

    /// <summary>Inclusive start as Unix time; null for all time.</summary>
    public long? From { get; }

    /// <summary>Exclusive end as Unix time; null for presets, which run up to now.</summary>
    public long? To { get; }

    /// <summary>The zone's current UTC offset in seconds, for grouping by local day in SQL.</summary>
    public long Offset { get; }

    public DateOnly TodayDate { get; }

    /// <summary>Local day number (days since 1970-01-01) of today.</summary>
    public int Today => DayNumber(TodayDate);

    public StatWindow Window => new(From, To);

    public bool IsAllTime => Preset == StatRange.All;

    /// <summary>The period has not ended yet.</summary>
    public bool IsOngoing(DateTimeOffset? now = null) => To is not { } to || (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() < to;

    // ---- creating ----

    public static StatPeriod Create(StatRange range, TimeZoneInfo zone, DateTimeOffset? now = null)
    {
        var (offset, today) = Clock(zone, now);
        DateOnly? first = range switch
        {
            StatRange.Week => today.AddDays(-6),
            StatRange.Month => today.AddDays(-29),
            StatRange.Year => FirstOfMonth(today).AddMonths(-11),
            _ => null,
        };
        long? from = first is { } day ? DayNumber(day) * 86400L - offset : null;
        return new StatPeriod(PeriodKind.Preset, range, first, today, from, null, offset, today);
    }

    public static StatPeriod ForYear(int year, TimeZoneInfo zone, DateTimeOffset? now = null)
    {
        year = Math.Clamp(year, Earliest.Year, Latest.Year);
        return Span(PeriodKind.Year, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), zone, now);
    }

    public static StatPeriod ForMonth(int year, int month, TimeZoneInfo zone, DateTimeOffset? now = null)
    {
        var first = Clamp(new DateOnly(Math.Clamp(year, Earliest.Year, Latest.Year), Math.Clamp(month, 1, 12), 1));
        return Span(PeriodKind.Month, first, first.AddMonths(1).AddDays(-1), zone, now);
    }

    /// <summary>The days from <paramref name="first"/> to <paramref name="last"/>, both included (swapped when reversed).</summary>
    public static StatPeriod ForDays(DateOnly first, DateOnly last, TimeZoneInfo zone, DateTimeOffset? now = null)
    {
        if (last < first) (first, last) = (last, first);
        return Span(PeriodKind.Custom, Clamp(first), Clamp(last), zone, now);
    }

    private static StatPeriod Span(PeriodKind kind, DateOnly first, DateOnly last, TimeZoneInfo zone, DateTimeOffset? now)
    {
        var (offset, today) = Clock(zone, now);
        var clock = new LocalClock(zone);
        return new StatPeriod(kind, null, first, last, clock.StartOf(first), clock.StartOf(last.AddDays(1)), offset, today);
    }

    private static (long Offset, DateOnly Today) Clock(TimeZoneInfo zone, DateTimeOffset? now)
    {
        var instant = now ?? DateTimeOffset.UtcNow;
        var offset = (long)zone.GetUtcOffset(instant).TotalSeconds;
        return (offset, ToDate((int)Math.Floor((instant.ToUnixTimeSeconds() + offset) / 86400.0)));
    }

    private static DateOnly Clamp(DateOnly day) => day < Earliest ? Earliest : day > Latest ? Latest : day;

    // ---- URLs ----

    /// <summary>
    /// Reads a period from a URL: a preset ("week", "month", "year", "all"), a year ("2024"), a month ("2024-03") or a span
    /// of days ("2024-03-01..2024-04-15"). A <paramref name="from"/>/<paramref name="to"/> pair (from a date form) wins
    /// over <paramref name="value"/>; anything unreadable gives <paramref name="fallback"/>.
    /// </summary>
    public static StatPeriod Resolve(string? value, StatRange fallback, TimeZoneInfo zone, string? from = null, string? to = null, DateTimeOffset? now = null)
    {
        if (TryParseDay(from, out var first) && TryParseDay(to, out var last)) return ForDays(first, last, zone, now);
        if (TryParseDay(from, out first)) return ForDays(first, first, zone, now);

        var text = value?.Trim() ?? "";
        if (TryParsePreset(text, out var preset)) return Create(preset, zone, now);
        if (text.Length == 4 && int.TryParse(text, NumberStyles.None, Inv, out var year) && year >= Earliest.Year)
            return ForYear(year, zone, now);
        if (text.Length == 7 && DateOnly.TryParseExact(text + "-01", "yyyy-MM-dd", Inv, DateTimeStyles.None, out var month) && month >= Earliest)
            return ForMonth(month.Year, month.Month, zone, now);
        var parts = text.Split(SpanSeparator);
        if (parts.Length == 2 && TryParseDay(parts[0], out first) && TryParseDay(parts[1], out last))
            return ForDays(first, last, zone, now);
        return Create(fallback, zone, now);
    }

    private static bool TryParseDay(string? value, out DateOnly day) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", Inv, DateTimeStyles.None, out day) && day >= Earliest;

    /// <summary>A preset slug; unknown values give 30 days. Used for the default-range preference.</summary>
    public static StatRange Parse(string? value) => TryParsePreset(value, out var range) ? range : StatRange.Month;

    public static bool TryParsePreset(string? value, out StatRange range)
    {
        StatRange? parsed = value?.Trim().ToLowerInvariant() switch
        {
            "week" or "7d" => StatRange.Week,
            "month" or "30d" => StatRange.Month,
            "year" or "12m" => StatRange.Year,
            "all" or "alltime" => StatRange.All,
            _ => null,
        };
        range = parsed ?? StatRange.Month;
        return parsed is not null;
    }

    public static string Slug(StatRange range) => range.ToString().ToLowerInvariant();

    public static string Label(StatRange range) => range switch
    {
        StatRange.Week => "7 days",
        StatRange.Month => "30 days",
        StatRange.Year => "12 months",
        _ => "All time",
    };

    /// <summary>The URL value of this period, read back by <see cref="Resolve"/>.</summary>
    public string Slug() => Kind switch
    {
        PeriodKind.Preset => Slug(Preset!.Value),
        PeriodKind.Year => FirstDay!.Value.ToString("yyyy", Inv),
        PeriodKind.Month => FirstDay!.Value.ToString("yyyy-MM", Inv),
        _ => FirstDay!.Value.ToString("yyyy-MM-dd", Inv) + SpanSeparator + LastDay.ToString("yyyy-MM-dd", Inv),
    };

    /// <summary>"7 days", "2024", "March 2024", "1 Mar – 15 Apr 2024".</summary>
    public string Label() => Kind switch
    {
        PeriodKind.Preset => Label(Preset!.Value),
        PeriodKind.Year => FirstDay!.Value.ToString("yyyy", Inv),
        PeriodKind.Month => FirstDay!.Value.ToString("MMMM yyyy", Inv),
        _ => DaySpan(FirstDay!.Value, LastDay),
    };

    public static string DaySpan(DateOnly first, DateOnly last)
    {
        if (first == last) return first.ToString("d MMM yyyy", Inv);
        if (first.Year != last.Year) return $"{first.ToString("d MMM yyyy", Inv)} – {last.ToString("d MMM yyyy", Inv)}";
        if (first.Month != last.Month) return $"{first.ToString("d MMM", Inv)} – {last.ToString("d MMM yyyy", Inv)}";
        return $"{first.Day}–{last.ToString("d MMM yyyy", Inv)}";
    }

    /// <summary>Number of days covered; for all time, null.</summary>
    public int? Days => FirstDay is { } first ? LastDay.DayNumber - first.DayNumber + 1 : null;

    // ---- navigating ----

    /// <summary>
    /// The calendar or custom period <paramref name="steps"/> before (negative) or after this one: years step by years,
    /// months by months and spans by their length. Null for presets, before 1970, and for periods that start after today.
    /// </summary>
    public StatPeriod? Step(int steps, TimeZoneInfo zone, DateTimeOffset? now = null)
    {
        if (FirstDay is not { } first || Kind == PeriodKind.Preset) return null;
        try
        {
            var start = Kind switch
            {
                PeriodKind.Year => first.AddYears(steps),
                PeriodKind.Month => first.AddMonths(steps),
                _ => first.AddDays(steps * Days!.Value),
            };
            if (start < Earliest || start > Clock(zone, now).Today) return null;
            return Kind switch
            {
                PeriodKind.Year => ForYear(start.Year, zone, now),
                PeriodKind.Month => ForMonth(start.Year, start.Month, zone, now),
                _ => ForDays(start, start.AddDays(Days!.Value - 1), zone, now),
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// The period to compare this one with: the same length right before a preset, the previous year or month, or the same
    /// number of days before a span. While this period is still running the previous one is cut at the same point, so the
    /// two compare like for like. Null for all time.
    /// </summary>
    public (StatWindow Window, string Label)? Previous(TimeZoneInfo zone, DateTimeOffset? now = null)
    {
        if (From is not { } from) return null;
        var instant = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();

        if (Kind == PeriodKind.Preset)
        {
            var length = instant - from;
            var label = Preset switch
            {
                StatRange.Week => "previous 7 days",
                StatRange.Month => "previous 30 days",
                _ => "previous 12 months",
            };
            return (new StatWindow(from - length, from), label);
        }

        var before = Kind switch
        {
            PeriodKind.Year => ForYear(FirstDay!.Value.Year - 1, zone, now),
            PeriodKind.Month => ForMonth(FirstDay!.Value.AddMonths(-1).Year, FirstDay.Value.AddMonths(-1).Month, zone, now),
            _ => ForDays(FirstDay!.Value.AddDays(-Days!.Value), FirstDay.Value.AddDays(-1), zone, now),
        };
        var name = Kind == PeriodKind.Custom ? $"previous {Days} days" : before.Label();
        if (!IsOngoing(now)) return (before.Window, name);
        var cut = Math.Clamp(before.From!.Value + (instant - from), before.From.Value, before.To!.Value);
        return (new StatWindow(before.From, cut), $"{name} up to the same day");
    }

    // ---- days ----

    public static DateOnly ToDate(int unixDay) => DateOnly.FromDayNumber(UnixEpochDayNumber + unixDay);

    public static int DayNumber(DateOnly date) => date.DayNumber - UnixEpochDayNumber;

    public static DateOnly FirstOfMonth(DateOnly date) => new(date.Year, date.Month, 1);
}
