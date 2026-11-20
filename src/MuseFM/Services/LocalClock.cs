namespace MuseFM.Services;

/// <summary>
/// Converts between Unix time and the wall-clock time of a time zone, including DST changes.
/// UTC offsets are cached per hour, so converting large numbers of scrobbles stays cheap. Not thread-safe.
/// </summary>
public sealed class LocalClock(TimeZoneInfo zone)
{
    private readonly Dictionary<long, long> _offsets = [];

    public TimeZoneInfo Zone => zone;

    public long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public DateOnly Today => DateOnly.FromDateTime(ToLocal(Now));

    public DateTime ToLocal(long unix)
    {
        var hour = Math.DivRem(unix, 3600, out var rest) - (rest < 0 ? 1 : 0);
        if (!_offsets.TryGetValue(hour, out var offset))
        {
            offset = (long)zone.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(hour * 3600)).TotalSeconds;
            _offsets[hour] = offset;
        }

        return new DateTime(DateTime.UnixEpoch.Ticks + (unix + offset) * TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);
    }

    /// <summary>Unix time of a local wall-clock time. A time skipped by a DST change moves forward to the first valid one.</summary>
    public long ToUnix(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var i = 0; i < 8 && zone.IsInvalidTime(local); i++) local = local.AddMinutes(30);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUnixTimeSeconds();
    }

    public long StartOf(DateOnly day) => ToUnix(day.ToDateTime(TimeOnly.MinValue));

    /// <summary>Position of a day in a week starting on <paramref name="first"/> (Monday unless the viewer prefers Sunday): 0 ... 6.</summary>
    public static int Weekday(DayOfWeek day, DayOfWeek first = DayOfWeek.Monday) => ((int)day - (int)first + 7) % 7;
}
