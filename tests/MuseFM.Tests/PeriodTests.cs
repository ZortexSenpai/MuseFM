using System.Net;
using MuseFM.Services;

namespace MuseFM.Tests;

/// <summary>Calendar and custom periods, and statistics of one album or track.</summary>
public class PeriodTests : IClassFixture<MuseFmFactory>
{
    private readonly MuseFmFactory _factory;

    public PeriodTests(MuseFmFactory factory) => _factory = factory;

    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    // Wednesday, 19 Nov 2026, 15:00 in Zurich.
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 14, 0, 0, TimeSpan.Zero);

    private static long Utc(int year, int month, int day, int hour = 0) =>
        new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [Theory]
    [InlineData("week", PeriodKind.Preset, "week", "7 days")]
    [InlineData("all", PeriodKind.Preset, "all", "All time")]
    [InlineData("2024", PeriodKind.Year, "2024", "2024")]
    [InlineData("2024-03", PeriodKind.Month, "2024-03", "March 2024")]
    [InlineData("2024-03-01..2024-03-10", PeriodKind.Custom, "2024-03-01..2024-03-10", "1–10 Mar 2024")]
    [InlineData("2024-04-15..2024-03-01", PeriodKind.Custom, "2024-03-01..2024-04-15", "1 Mar – 15 Apr 2024")]
    [InlineData("2023-12-30..2024-01-02", PeriodKind.Custom, "2023-12-30..2024-01-02", "30 Dec 2023 – 2 Jan 2024")]
    [InlineData("1969", PeriodKind.Preset, "month", "30 days")]
    [InlineData("2024-13", PeriodKind.Preset, "month", "30 days")]
    [InlineData("nonsense", PeriodKind.Preset, "month", "30 days")]
    [InlineData(null, PeriodKind.Preset, "month", "30 days")]
    public void Resolve_ReadsPresetsYearsMonthsAndSpans(string? value, PeriodKind kind, string slug, string label)
    {
        var period = StatPeriod.Resolve(value, StatRange.Month, Zurich, now: Now);
        Assert.Equal((kind, slug, label), (period.Kind, period.Slug(), period.Label()));
        // The slug reads back as the same period.
        Assert.Equal(slug, StatPeriod.Resolve(period.Slug(), StatRange.All, Zurich, now: Now).Slug());
    }

    [Fact]
    public void Resolve_DateFormWinsOverTheRange()
    {
        var period = StatPeriod.Resolve("2020", StatRange.Month, Zurich, "2025-06-01", "2025-06-30", Now);
        Assert.Equal(("2025-06-01..2025-06-30", 30), (period.Slug(), period.Days));
        Assert.Equal("2020", StatPeriod.Resolve("2020", StatRange.Month, Zurich, "garbage", "2025-06-30", Now).Slug());
    }

    [Fact]
    public void CalendarBounds_FollowTheLocalCalendarAcrossDst()
    {
        // Zurich is UTC+1 in winter and UTC+2 in summer.
        var year = StatPeriod.ForYear(2024, Zurich, Now);
        Assert.Equal((Utc(2023, 12, 31, 23), Utc(2024, 12, 31, 23)), (year.From!.Value, year.To!.Value));

        var july = StatPeriod.ForMonth(2024, 7, Zurich, Now);
        Assert.Equal((Utc(2024, 6, 30, 22), Utc(2024, 7, 31, 22)), (july.From!.Value, july.To!.Value));
        Assert.False(july.IsOngoing(Now));
        Assert.True(StatPeriod.ForYear(2026, Zurich, Now).IsOngoing(Now));
    }

    [Fact]
    public void Step_MovesByYearsMonthsAndSpansButNotIntoTheFuture()
    {
        Assert.Equal("2024-02", StatPeriod.ForMonth(2024, 3, Zurich, Now).Step(-1, Zurich, Now)!.Slug());
        Assert.Equal("2025-01", StatPeriod.ForMonth(2024, 12, Zurich, Now).Step(1, Zurich, Now)!.Slug());
        Assert.Equal("2023", StatPeriod.ForYear(2024, Zurich, Now).Step(-1, Zurich, Now)!.Slug());
        Assert.Null(StatPeriod.ForYear(2026, Zurich, Now).Step(1, Zurich, Now));
        Assert.Null(StatPeriod.ForMonth(2026, 11, Zurich, Now).Step(1, Zurich, Now));
        Assert.Null(StatPeriod.ForYear(1970, Zurich, Now).Step(-1, Zurich, Now));
        Assert.Null(StatPeriod.Create(StatRange.Week, Zurich, Now).Step(-1, Zurich, Now));

        var span = StatPeriod.ForDays(new DateOnly(2024, 3, 1), new DateOnly(2024, 3, 10), Zurich, Now);
        Assert.Equal("2024-02-20..2024-02-29", span.Step(-1, Zurich, Now)!.Slug());
    }

    [Fact]
    public void Previous_IsTheYearMonthOrSpanBefore_CutAtTheSamePointWhileRunning()
    {
        var done = StatPeriod.ForYear(2025, Zurich, Now).Previous(Zurich, Now)!.Value;
        Assert.Equal(("2024", Utc(2023, 12, 31, 23), Utc(2024, 12, 31, 23)), (done.Label, done.Window.From!.Value, done.Window.To!.Value));

        var running = StatPeriod.ForMonth(2026, 11, Zurich, Now).Previous(Zurich, Now)!.Value;
        Assert.Equal("October 2026 up to the same day", running.Label);
        Assert.Equal(StatPeriod.ForMonth(2026, 10, Zurich, Now).From, running.Window.From);
        Assert.Equal(running.Window.From + (Now.ToUnixTimeSeconds() - StatPeriod.ForMonth(2026, 11, Zurich, Now).From), running.Window.To);

        var span = StatPeriod.ForDays(new DateOnly(2024, 3, 1), new DateOnly(2024, 3, 10), Zurich, Now).Previous(Zurich, Now)!.Value;
        Assert.Equal(("previous 10 days", StatPeriod.ForDays(new DateOnly(2024, 2, 20), new DateOnly(2024, 2, 20), Zurich, Now).From), (span.Label, span.Window.From));

        Assert.Null(StatPeriod.Create(StatRange.All, Zurich, Now).Previous(Zurich, Now));
    }

    [Fact]
    public void Buckets_FitTheLengthOfThePeriod()
    {
        Assert.Equal(12, StatsService.Buckets(StatPeriod.ForYear(2024, Zurich, Now), new Dictionary<int, int>()).Count);
        Assert.Equal(29, StatsService.Buckets(StatPeriod.ForMonth(2024, 2, Zurich, Now), new Dictionary<int, int>()).Count);
        Assert.Equal(10, StatsService.Buckets(StatPeriod.ForDays(new(2024, 3, 1), new(2024, 3, 10), Zurich, Now), new Dictionary<int, int>()).Count);
        Assert.Equal(4, StatsService.Buckets(StatPeriod.ForDays(new(2024, 3, 1), new(2024, 6, 10), Zurich, Now), new Dictionary<int, int>()).Count);
        Assert.Equal(4, StatsService.Buckets(StatPeriod.ForDays(new(2021, 3, 1), new(2024, 6, 10), Zurich, Now), new Dictionary<int, int>()).Count);

        var march = StatPeriod.ForMonth(2024, 3, Zurich, Now);
        var day = StatPeriod.DayNumber(new DateOnly(2024, 3, 5));
        var buckets = StatsService.Buckets(march, new Dictionary<int, int> { [day] = 3, [day - 10] = 7 });
        Assert.Equal(3, buckets[4].Count);
        Assert.Equal(3, buckets.Sum(b => b.Count));
        Assert.Equal(("1 Mar", "5"), (buckets[0].Label, buckets[4].Label));
    }

    [Fact]
    public async Task AlbumAndTrackStatistics_CountOnlyTheirOwnScrobbles()
    {
        var (user, _) = await _factory.CreateUserAsync("subject-user");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ScrobbleInput S(int daysAgo, string title, string? album) => new()
        {
            Title = title, Artists = ["Bicep"], Album = album, ListenedAt = now - daysAgo * 86400L - 60,
        };
        await _factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>().IngestAsync(user.Id,
        [
            S(1, "Glue", "Bicep"), S(2, "Glue", "Bicep"), S(3, "Glue", "Singles"), S(4, "Atlas", "Isles"),
            S(5, "Saku", "Isles"), S(400, "Saku", "Isles"),
        ]));

        var result = await _factory.WithScopeAsync(async sp =>
        {
            var stats = sp.GetRequiredService<StatsService>();
            var db = sp.GetRequiredService<MuseFM.Data.AppDbContext>();
            var glue = db.Tracks.Single(t => t.Title == "Glue").Id;
            var isles = db.Albums.Single(a => a.Title == "Isles").Id;
            var month = StatPeriod.Create(StatRange.Month, TimeZoneInfo.Utc).Window;
            return new
            {
                Glue = await stats.SummaryAsync(user.Id, StatSubject.Track(glue), month),
                Isles = await stats.SummaryAsync(user.Id, StatSubject.Album(isles), month),
                IslesTracks = await stats.TopTracksAsync(user.Id, default, 1, 10, StatSubject.Album(isles)),
                GlueAlbums = await stats.TopAlbumsAsync(user.Id, default, 1, 10, StatSubject.Track(glue)),
                IslesHistory = await stats.RecentAsync(user.Id, 1, 10, StatSubject.Album(isles), month),
                Track = await stats.TrackAsync(glue),
                Album = await stats.AlbumAsync(isles),
            };
        });

        Assert.Equal((3, 3), (result.Glue!.Scrobbles, result.Glue.AllTimeScrobbles));
        Assert.Equal((2, 3), (result.Isles!.Scrobbles, result.Isles.AllTimeScrobbles));
        Assert.Equal([("Saku", 2), ("Atlas", 1)], result.IslesTracks.Items.Select(e => (e.Title, e.Count)));
        Assert.Equal([("Bicep", 2), ("Singles", 1)], result.GlueAlbums.Items.Select(e => (e.Title, e.Count)));
        Assert.Equal(2, result.IslesHistory.Total);
        Assert.Equal(["Bicep"], result.Track!.Artists.Select(a => a.Name));
        Assert.Equal("Isles", result.Album!.Title);
    }

    [Fact]
    public async Task AlbumAndTrackPages_RenderWithEveryKindOfPeriod()
    {
        var (user, _) = await _factory.CreateUserAsync("pages-user");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await _factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>().IngestAsync(user.Id,
        [
            new ScrobbleInput { Title = "Glue", Artists = ["Bicep"], Album = "Bicep", ListenedAt = now - 3600 },
            new ScrobbleInput { Title = "Atlas", Artists = ["Bicep"], Album = "Isles", ListenedAt = now - 7200 },
        ]));
        var (trackId, albumId, otherTrack) = await _factory.WithScopeAsync(sp =>
        {
            var db = sp.GetRequiredService<MuseFM.Data.AppDbContext>();
            return Task.FromResult((db.Tracks.Single(t => t.Title == "Glue").Id, db.Albums.Single(a => a.Title == "Bicep").Id, db.Tracks.Single(t => t.Title == "Atlas").Id));
        });

        var client = _factory.CreatePlainClient();
        var year = DateTime.UtcNow.Year;
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        foreach (var url in new[]
                 {
                     $"/u/pages-user/track/{trackId}", $"/u/pages-user/album/{albumId}", $"/u/pages-user/track/{trackId}?range={year}",
                     $"/u/pages-user/album/{albumId}?range=week&p=2", $"/u/pages-user?range={year}", $"/u/pages-user?range={year}-01",
                     $"/u/pages-user?from=2020-01-01&to={today}", $"/u/pages-user/tracks?range=2020-01-01..{today}", $"/u/pages-user/insights?range={year}",
                     $"/u/pages-user/artists?range=1999", "/u/pages-user/compare",
                 })
        {
            var response = await client.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)response.StatusCode}");
        }

        var page = await client.GetStringAsync($"/u/pages-user/track/{trackId}");
        Assert.Contains($"/u/pages-user/album/{albumId}", page);
        var overview = await client.GetStringAsync($"/u/pages-user?range={year}");
        Assert.Contains($"/u/pages-user/track/{trackId}", overview);
        Assert.Contains($"<h2>{year}</h2>", overview);

        // Albums and tracks the user never scrobbled don't have a page.
        await _factory.CreateUserAsync("pages-other");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/u/pages-other/track/{trackId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/u/pages-user/album/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/u/pages-user/track/{otherTrack}")).StatusCode);
    }
}
