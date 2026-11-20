using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;

namespace MuseFM.Services;

public enum ChartKind
{
    Artists,
    Albums,
    Tracks,
}

/// <summary>A span of Unix time: From inclusive, To exclusive; either end may be open.</summary>
public readonly record struct StatWindow(long? From, long? To = null)
{
    public static implicit operator StatWindow(long? from) => new(from);
}

/// <summary>Restricts statistics to the scrobbles of one artist, album or track.</summary>
public readonly record struct StatSubject(ChartKind Kind, int Id)
{
    public static StatSubject Artist(int id) => new(ChartKind.Artists, id);

    public static StatSubject Album(int id) => new(ChartKind.Albums, id);

    public static StatSubject Track(int id) => new(ChartKind.Tracks, id);
}

public sealed record ArtistRef(int Id, string Name);

public sealed record ChartEntry(int Rank, int Id, string Title, IReadOnlyList<ArtistRef> Artists, int Count);

public sealed record ScrobbleRow(
    long Id,
    long ListenedAt,
    int TrackId,
    string Title,
    IReadOnlyList<ArtistRef> Artists,
    int? AlbumId,
    string? Album,
    int? LengthSeconds,
    string? Origin,
    string? TrackMbid = null,
    string? AlbumMbid = null);

public sealed record Totals(int Scrobbles, int Artists, int Albums, int Tracks);

public sealed record ActivityBucket(string Label, string Tooltip, int Count);

/// <summary>How much a user listened to an artist, album or track.</summary>
/// <param name="Scrobbles">Scrobbles within the requested window.</param>
/// <param name="FirstListen">First scrobble ever (not limited to the window).</param>
/// <param name="LastListen">Latest scrobble ever.</param>
public sealed record ListenSummary(int Scrobbles, int AllTimeScrobbles, long FirstListen, long LastListen);

/// <summary>An album or track of the catalog with its credited artists.</summary>
public sealed record CatalogItem(int Id, string Title, IReadOnlyList<ArtistRef> Artists, int? LengthSeconds, string? MusicBrainzId);

public sealed class Paged<T>(IReadOnlyList<T> items, int page, int pageSize, int total)
{
    public IReadOnlyList<T> Items { get; } = items;
    public int Page { get; } = page;
    public int PageSize { get; } = pageSize;
    public int Total { get; } = total;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

/// <summary>Read-side queries for profiles, charts and the read APIs.</summary>
public sealed class StatsService(AppDbContext db)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed class IdCount
    {
        public int Id { get; init; }
        public int Count { get; init; }
    }

    private IQueryable<Scrobble> Scrobbles(string userId, StatWindow window)
    {
        var query = db.Scrobbles.AsNoTracking().Where(s => s.UserId == userId);
        if (window.From is { } start) query = query.Where(s => s.ListenedAt >= start);
        if (window.To is { } end) query = query.Where(s => s.ListenedAt < end);
        return query;
    }

    /// <summary>Scrobbles of one artist (any credited track), album or track.</summary>
    private IQueryable<Scrobble> Of(IQueryable<Scrobble> query, StatSubject? subject) => subject switch
    {
        { Kind: ChartKind.Artists, Id: var id } => query.Where(s => db.TrackArtists.Any(ta => ta.TrackId == s.TrackId && ta.ArtistId == id)),
        { Kind: ChartKind.Albums, Id: var id } => query.Where(s => s.AlbumId == id),
        { Kind: ChartKind.Tracks, Id: var id } => query.Where(s => s.TrackId == id),
        _ => query,
    };

    private IQueryable<IdCount> ArtistCounts(StatWindow window, string userId) =>
        Scrobbles(userId, window)
            .Join(db.TrackArtists, s => s.TrackId, ta => ta.TrackId, (s, ta) => ta.ArtistId)
            .GroupBy(id => id)
            .Select(g => new IdCount { Id = g.Key, Count = g.Count() });

    private IQueryable<IdCount> TrackCounts(StatWindow window, string userId, StatSubject? subject) =>
        Of(Scrobbles(userId, window), subject)
            .GroupBy(s => s.TrackId)
            .Select(g => new IdCount { Id = g.Key, Count = g.Count() });

    /// <summary>For an artist: the albums credited to them (not every album a featured track appears on).</summary>
    private IQueryable<IdCount> AlbumCounts(StatWindow window, string userId, StatSubject? subject)
    {
        var query = Scrobbles(userId, window).Where(s => s.AlbumId != null);
        query = subject is { Kind: ChartKind.Artists, Id: var artistId }
            ? query.Where(s => db.AlbumArtists.Any(aa => aa.AlbumId == s.AlbumId && aa.ArtistId == artistId))
            : Of(query, subject);
        return query.GroupBy(s => s.AlbumId!.Value).Select(g => new IdCount { Id = g.Key, Count = g.Count() });
    }

    private IQueryable<IdCount> Counts(ChartKind kind, StatWindow window, string userId) => kind switch
    {
        ChartKind.Artists => ArtistCounts(window, userId),
        ChartKind.Albums => AlbumCounts(window, userId, null),
        _ => TrackCounts(window, userId, null),
    };

    /// <summary>Chart position and count of every artist, album or track in the window, ranked like the charts.</summary>
    public async Task<Dictionary<int, (int Rank, int Count)>> RanksAsync(ChartKind kind, string userId, StatWindow window, CancellationToken ct = default)
    {
        var rows = await Counts(kind, window, userId).OrderByDescending(x => x.Count).ThenBy(x => x.Id).ToListAsync(ct);
        return rows.Select((r, i) => (r.Id, Rank: i + 1, r.Count)).ToDictionary(r => r.Id, r => (r.Rank, r.Count));
    }

    public Task<int> CountAsync(string userId, long? from = null, CancellationToken ct = default) =>
        Scrobbles(userId, from).CountAsync(ct);

    public Task<long?> LastScrobbleAsync(string userId, CancellationToken ct = default) =>
        Scrobbles(userId, null).MaxAsync(s => (long?)s.ListenedAt, ct);

    /// <summary>The local year of the user's first scrobble, or the current year without any.</summary>
    public async Task<int> FirstYearAsync(string userId, TimeZoneInfo zone, CancellationToken ct = default)
    {
        var first = await Scrobbles(userId, null).MinAsync(s => (long?)s.ListenedAt, ct);
        var instant = first is { } unix ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.UtcNow;
        return TimeZoneInfo.ConvertTime(instant, zone).Year;
    }

    public async Task<Totals> GetTotalsAsync(string userId, StatWindow window, CancellationToken ct = default)
    {
        var query = Scrobbles(userId, window);
        var scrobbles = await query.CountAsync(ct);
        if (scrobbles == 0) return new Totals(0, 0, 0, 0);

        var tracks = await query.Select(s => s.TrackId).Distinct().CountAsync(ct);
        var albums = await query.Where(s => s.AlbumId != null).Select(s => s.AlbumId).Distinct().CountAsync(ct);
        var artists = await query
            .Join(db.TrackArtists, s => s.TrackId, ta => ta.TrackId, (s, ta) => ta.ArtistId)
            .Distinct()
            .CountAsync(ct);
        return new Totals(scrobbles, artists, albums, tracks);
    }

    public async Task<Paged<ChartEntry>> TopArtistsAsync(string userId, StatWindow window, int page, int pageSize, CancellationToken ct = default)
    {
        var grouped = ArtistCounts(window, userId);

        var total = await grouped.CountAsync(ct);
        var rows = await grouped
            .OrderByDescending(x => x.Count).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var names = await db.Artists.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var offset = (page - 1) * pageSize;
        var items = rows.Select((r, i) => new ChartEntry(offset + i + 1, r.Id, names.GetValueOrDefault(r.Id, "?"), [], r.Count)).ToList();
        return new Paged<ChartEntry>(items, page, pageSize, total);
    }

    /// <param name="subject">Only tracks of this artist, or only tracks scrobbled from this album.</param>
    public async Task<Paged<ChartEntry>> TopTracksAsync(
        string userId, StatWindow window, int page, int pageSize, StatSubject? subject = null, CancellationToken ct = default)
    {
        var grouped = TrackCounts(window, userId, subject);

        var total = await grouped.CountAsync(ct);
        var rows = await grouped
            .OrderByDescending(x => x.Count).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var titles = await db.Tracks.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Title, ct);
        var artists = await TrackArtistsAsync(ids, ct);
        var offset = (page - 1) * pageSize;
        var items = rows.Select((r, i) => new ChartEntry(
            offset + i + 1, r.Id, titles.GetValueOrDefault(r.Id, "?"), artists.GetValueOrDefault(r.Id) ?? [], r.Count)).ToList();
        return new Paged<ChartEntry>(items, page, pageSize, total);
    }

    /// <param name="subject">Only albums credited to this artist, or only albums this track was scrobbled from.</param>
    public async Task<Paged<ChartEntry>> TopAlbumsAsync(
        string userId, StatWindow window, int page, int pageSize, StatSubject? subject = null, CancellationToken ct = default)
    {
        var grouped = AlbumCounts(window, userId, subject);

        var total = await grouped.CountAsync(ct);
        var rows = await grouped
            .OrderByDescending(x => x.Count).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var titles = await db.Albums.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Title, ct);
        var artists = await AlbumArtistsAsync(ids, ct);
        var offset = (page - 1) * pageSize;
        var items = rows.Select((r, i) => new ChartEntry(
            offset + i + 1, r.Id, titles.GetValueOrDefault(r.Id, "?"), artists.GetValueOrDefault(r.Id) ?? [], r.Count)).ToList();
        return new Paged<ChartEntry>(items, page, pageSize, total);
    }

    /// <summary>Scrobbles newest first, optionally of one artist, album or track and within a window.</summary>
    public async Task<Paged<ScrobbleRow>> RecentAsync(
        string userId, int page, int pageSize, StatSubject? subject = null, StatWindow window = default, CancellationToken ct = default)
    {
        var query = Of(Scrobbles(userId, window), subject);
        var total = await query.CountAsync(ct);
        var rows = await ToRowsAsync(query.OrderByDescending(s => s.ListenedAt).Skip((page - 1) * pageSize).Take(pageSize), ct);
        return new Paged<ScrobbleRow>(rows, page, pageSize, total);
    }

    /// <summary>The user's nth scrobble in order of time (1 is the first); null when there are fewer.</summary>
    public async Task<ScrobbleRow?> NthAsync(string userId, int number, CancellationToken ct = default) =>
        (await ToRowsAsync(Scrobbles(userId, null).OrderBy(s => s.ListenedAt).Skip(number - 1).Take(1), ct)).FirstOrDefault();

    /// <summary>ListenBrainz-style paging: newest first, or oldest-after-min_ts when only min_ts is given.</summary>
    public async Task<List<ScrobbleRow>> ListensAsync(string userId, int count, long? maxTs, long? minTs, CancellationToken ct = default)
    {
        var query = Scrobbles(userId, null);
        if (maxTs is { } max) query = query.Where(s => s.ListenedAt < max);
        if (minTs is { } min) query = query.Where(s => s.ListenedAt > min);
        var ascending = minTs is not null && maxTs is null;
        query = ascending ? query.OrderBy(s => s.ListenedAt) : query.OrderByDescending(s => s.ListenedAt);
        var rows = await ToRowsAsync(query.Take(count), ct);
        if (ascending) rows.Reverse();
        return rows;
    }

    /// <summary>
    /// The latest scrobbles of several users together, newest first, before an optional (time, id) cursor; for the feed.
    /// </summary>
    public async Task<List<(string UserId, ScrobbleRow Row)>> ListensOfAsync(
        IReadOnlyList<string> userIds, (long ListenedAt, long Id)? before, int count, CancellationToken ct = default)
    {
        var query = db.Scrobbles.AsNoTracking().Where(s => userIds.Contains(s.UserId));
        if (before is var (at, id)) query = query.Where(s => s.ListenedAt < at || (s.ListenedAt == at && s.Id < id));
        var page = await query.OrderByDescending(s => s.ListenedAt).ThenByDescending(s => s.Id).Take(count)
            .Select(s => new { s.Id, s.UserId }).ToListAsync(ct);
        var ids = page.Select(p => p.Id).ToList();
        var rows = (await ToRowsAsync(db.Scrobbles.AsNoTracking().Where(s => ids.Contains(s.Id)), ct)).ToDictionary(r => r.Id);
        return page.Select(p => (p.UserId, rows[p.Id])).ToList();
    }

    /// <summary>How much the user listened to an artist, album or track; null when they never scrobbled it.</summary>
    public async Task<ListenSummary?> SummaryAsync(string userId, StatSubject subject, StatWindow window, CancellationToken ct = default)
    {
        var allTime = Of(Scrobbles(userId, null), subject);
        // All rows share the user id, so this is a single group.
        var span = await allTime
            .GroupBy(s => s.UserId)
            .Select(g => new { First = g.Min(s => s.ListenedAt), Last = g.Max(s => s.ListenedAt), Count = g.Count() })
            .FirstOrDefaultAsync(ct);
        if (span is null) return null;
        var count = window == default ? span.Count : await Of(Scrobbles(userId, window), subject).CountAsync(ct);
        return new ListenSummary(count, span.Count, span.First, span.Last);
    }

    public Task<string?> ArtistNameAsync(int id, CancellationToken ct = default) =>
        db.Artists.AsNoTracking().Where(a => a.Id == id).Select(a => a.Name).FirstOrDefaultAsync(ct);

    /// <summary>MusicBrainz ids of these albums, from scrobbles or else from the metadata connectors.</summary>
    public async Task<Dictionary<int, string>> AlbumMbidsAsync(IEnumerable<int> albumIds, CancellationToken ct = default)
    {
        var ids = albumIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await db.Albums.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, Mbid = a.MusicBrainzId ?? db.AlbumMetadata.Where(m => m.AlbumId == a.Id).Select(m => m.MusicBrainzId).FirstOrDefault() })
            .ToListAsync(ct);
        return rows.Where(r => r.Mbid != null).ToDictionary(r => r.Id, r => r.Mbid!);
    }

    /// <summary>MusicBrainz id and length of these tracks.</summary>
    public async Task<Dictionary<int, (string? Mbid, int? LengthSeconds)>> TrackDetailsAsync(IEnumerable<int> trackIds, CancellationToken ct = default)
    {
        var ids = trackIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.Tracks.AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => (t.MusicBrainzId, t.LengthSeconds), ct);
    }

    /// <summary>MusicBrainz ids of these artists, where known.</summary>
    public async Task<Dictionary<int, string>> ArtistMbidsAsync(IEnumerable<int> artistIds, CancellationToken ct = default)
    {
        var ids = artistIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.Artists.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.MusicBrainzId != null)
            .ToDictionaryAsync(a => a.Id, a => a.MusicBrainzId!, ct);
    }

    public async Task<CatalogItem?> AlbumAsync(int id, CancellationToken ct = default)
    {
        // The release id clients sent, else the one a metadata connector found.
        var album = await db.Albums.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new { a.Id, a.Title, Mbid = a.MusicBrainzId ?? db.AlbumMetadata.Where(m => m.AlbumId == a.Id).Select(m => m.MusicBrainzId).FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        if (album is null) return null;
        var artists = await AlbumArtistsAsync([id], ct);
        return new CatalogItem(album.Id, album.Title, artists.GetValueOrDefault(id) ?? [], null, album.Mbid);
    }

    public async Task<CatalogItem?> TrackAsync(int id, CancellationToken ct = default)
    {
        var track = await db.Tracks.AsNoTracking().Where(t => t.Id == id).Select(t => new { t.Id, t.Title, t.LengthSeconds, t.MusicBrainzId }).FirstOrDefaultAsync(ct);
        if (track is null) return null;
        var artists = await TrackArtistsAsync([id], ct);
        return new CatalogItem(track.Id, track.Title, artists.GetValueOrDefault(id) ?? [], track.LengthSeconds, track.MusicBrainzId);
    }

    public async Task<IReadOnlyList<ActivityBucket>> ActivityAsync(string userId, StatPeriod period, StatSubject? subject = null, CancellationToken ct = default)
    {
        var offset = period.Offset;
        var perDay = await Of(Scrobbles(userId, period.Window), subject)
            .GroupBy(s => (s.ListenedAt + offset) / 86400)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return Buckets(period, perDay.ToDictionary(x => (int)x.Day, x => x.Count));
    }

    /// <summary>
    /// Groups per-day counts (keyed by local day number since 1970) into the buckets of a period: days for up to two
    /// months, months for up to two years, years beyond that. All time starts at the first day with scrobbles.
    /// </summary>
    public static IReadOnlyList<ActivityBucket> Buckets(StatPeriod period, IReadOnlyDictionary<int, int> days)
    {
        switch (period.Kind, period.Preset)
        {
            case (PeriodKind.Preset, StatRange.Week):
                return DayBuckets(period.FirstDay!.Value, period.LastDay, days, (_, date) => date.ToString("ddd", Inv));
            case (PeriodKind.Preset, StatRange.Month):
                return DayBuckets(period.FirstDay!.Value, period.LastDay, days, (i, date) => i % 5 == 4 || i == 0 ? date.ToString("d MMM", Inv) : "");
            case (PeriodKind.Year, _):
            case (PeriodKind.Preset, StatRange.Year):
                return MonthBuckets(period.FirstDay!.Value, period.FirstDay.Value.AddMonths(11), days);
            case (PeriodKind.Month, _):
                return DayBuckets(period.FirstDay!.Value, period.LastDay, days, (i, date) => i == 0 ? date.ToString("d MMM", Inv) : (i + 1) % 5 == 0 ? date.Day.ToString(Inv) : "");
        }

        if (period.FirstDay is { } first)
        {
            // A custom span.
            var length = period.Days!.Value;
            if (length <= 62)
            {
                var every = length <= 7 ? 1 : (int)Math.Ceiling(length / 6.0);
                return DayBuckets(first, period.LastDay, days, (i, date) => length <= 7 ? date.ToString("ddd d", Inv) : i % every == 0 ? date.ToString("d MMM", Inv) : "");
            }

            return length <= 731
                ? MonthBuckets(StatPeriod.FirstOfMonth(first), StatPeriod.FirstOfMonth(period.LastDay), days)
                : YearBuckets(first.Year, period.LastDay.Year, days);
        }

        // All time.
        if (days.Count == 0) return [];
        var start = StatPeriod.ToDate(days.Keys.Min());
        var end = StatPeriod.ToDate(Math.Max(period.Today, days.Keys.Max()));
        return end.DayNumber - start.DayNumber <= 730
            ? MonthBuckets(StatPeriod.FirstOfMonth(start), StatPeriod.FirstOfMonth(end), days)
            : YearBuckets(start.Year, end.Year, days);
    }

    private static List<ActivityBucket> DayBuckets(DateOnly first, DateOnly last, IReadOnlyDictionary<int, int> days, Func<int, DateOnly, string> label)
    {
        var buckets = new List<ActivityBucket>();
        for (var (date, i) = (first, 0); date <= last; date = date.AddDays(1), i++)
            buckets.Add(new ActivityBucket(label(i, date), date.ToString("ddd, d MMM yyyy", Inv), days.GetValueOrDefault(StatPeriod.DayNumber(date))));
        return buckets;
    }

    private static List<ActivityBucket> MonthBuckets(DateOnly firstMonth, DateOnly lastMonth, IReadOnlyDictionary<int, int> days)
    {
        var buckets = new List<ActivityBucket>();
        for (var month = firstMonth; month <= lastMonth; month = month.AddMonths(1))
        {
            var first = StatPeriod.DayNumber(month);
            var last = StatPeriod.DayNumber(month.AddMonths(1)) - 1;
            var count = days.Where(d => d.Key >= first && d.Key <= last).Sum(d => d.Value);
            var label = month.Month == 1 ? month.ToString("MMM yy", Inv) : month.ToString("MMM", Inv);
            buckets.Add(new ActivityBucket(label, month.ToString("MMMM yyyy", Inv), count));
        }

        return buckets;
    }

    private static List<ActivityBucket> YearBuckets(int firstYear, int lastYear, IReadOnlyDictionary<int, int> days)
    {
        var perYear = days.GroupBy(d => StatPeriod.ToDate(d.Key).Year).ToDictionary(g => g.Key, g => g.Sum(x => x.Value));
        return Enumerable.Range(firstYear, lastYear - firstYear + 1)
            .Select(year => new ActivityBucket(year.ToString(Inv), year.ToString(Inv), perYear.GetValueOrDefault(year)))
            .ToList();
    }

    private async Task<List<ScrobbleRow>> ToRowsAsync(IQueryable<Scrobble> query, CancellationToken ct)
    {
        var raw = await query
            .Select(s => new
            {
                s.Id,
                s.ListenedAt,
                s.TrackId,
                s.Track.Title,
                s.Track.LengthSeconds,
                TrackMbid = s.Track.MusicBrainzId,
                s.AlbumId,
                Album = s.Album != null ? s.Album.Title : null,
                AlbumMbid = s.Album != null ? s.Album.MusicBrainzId : null,
                s.Origin,
            })
            .ToListAsync(ct);
        var artists = await TrackArtistsAsync(raw.Select(r => r.TrackId), ct);
        return raw.Select(r => new ScrobbleRow(
            r.Id, r.ListenedAt, r.TrackId, r.Title, artists.GetValueOrDefault(r.TrackId) ?? [],
            r.AlbumId, r.Album, r.LengthSeconds, r.Origin, r.TrackMbid, r.AlbumMbid)).ToList();
    }

    public async Task<Dictionary<int, IReadOnlyList<ArtistRef>>> TrackArtistsAsync(IEnumerable<int> trackIds, CancellationToken ct = default)
    {
        var ids = trackIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await db.TrackArtists.AsNoTracking()
            .Where(ta => ids.Contains(ta.TrackId))
            .OrderBy(ta => ta.Position)
            .Select(ta => new { ta.TrackId, ta.ArtistId, ta.Artist.Name })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.TrackId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ArtistRef>)g.Select(r => new ArtistRef(r.ArtistId, r.Name)).ToList());
    }

    public async Task<Dictionary<int, IReadOnlyList<ArtistRef>>> AlbumArtistsAsync(IEnumerable<int> albumIds, CancellationToken ct = default)
    {
        var ids = albumIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await db.AlbumArtists.AsNoTracking()
            .Where(aa => ids.Contains(aa.AlbumId))
            .OrderBy(aa => aa.Position)
            .Select(aa => new { aa.AlbumId, aa.ArtistId, aa.Artist.Name })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.AlbumId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ArtistRef>)g.Select(r => new ArtistRef(r.ArtistId, r.Name)).ToList());
    }
}
