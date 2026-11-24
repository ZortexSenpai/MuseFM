using System.Net;
using MuseFM.Pages;
using MuseFM.Services;

namespace MuseFM.Tests;

public class InsightsTests : IClassFixture<MuseFmFactory>
{
    private readonly MuseFmFactory _factory;

    public InsightsTests(MuseFmFactory factory) => _factory = factory;

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static long At(DateOnly day, int hour, int minute = 30) =>
        new DateTimeOffset(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.Zero).ToUnixTimeSeconds();

    private static int Weekday(DateOnly day) => ((int)day.DayOfWeek + 6) % 7;

    private static ScrobbleInput S(string artist, string title, long at) => new() { Title = title, Artists = [artist], ListenedAt = at };

    private Task IngestAsync(string userId, params ScrobbleInput[] scrobbles) =>
        _factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>().IngestAsync(userId, scrobbles));

    [Fact]
    public async Task Insights_CountHoursWeekdaysStreaksAndDiscoveries()
    {
        var (user, _) = await _factory.CreateUserAsync("insights-user");
        await IngestAsync(user.Id,
            S("Alpha", "One", At(Today, 0)),
            S("Alpha", "Two", At(Today.AddDays(-1), 21)),
            S("Alpha", "One", At(Today.AddDays(-2), 21)),
            S("Beta", "Three", At(Today.AddDays(-2), 9)),
            S("Alpha", "Two", At(Today.AddDays(-4), 21)),
            S("Beta", "Three", At(Today.AddDays(-40), 9)),
            S("Gamma", "Four", At(Today.AddDays(-400), 12)));

        var (year, month) = await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
            .GetInsightsAsync(user.Id, StatPeriod.Create(StatRange.Month, TimeZoneInfo.Utc), TimeZoneInfo.Utc));

        Assert.Equal((5, 30, 4), (month.Scrobbles, month.Days, month.ActiveDays));
        Assert.Equal((3, 1, 1), (month.ByHour[21], month.ByHour[9], month.ByHour[0]));
        Assert.Equal(2, month.ByWeekday[Weekday(Today.AddDays(-2))]);
        Assert.Equal(2, month.ByWeekdayHour[Weekday(Today.AddDays(-2)), 21] + month.ByWeekdayHour[Weekday(Today.AddDays(-2)), 9]);
        Assert.Equal(new DayCount(Today.AddDays(-2), 2), month.BusiestDay);
        Assert.Equal(1, month.NewArtists); // Alpha; Beta and Gamma were first heard before the range
        Assert.Equal(4, month.NewArtistPlays);
        Assert.Equal(1, month.Discoveries.Sum(b => b.Count));
        Assert.Equal(5, month.ArtistPlays);
        Assert.Equal([("top1", 4, "Alpha"), ("top5", 1, "Beta")], month.ArtistShare.Select(s => (s.Key, s.Plays, string.Join(",", s.Artists))));

        Assert.Equal(new Streak(Today.AddDays(-2), Today), year.CurrentStreak);
        Assert.Equal(new Streak(Today.AddDays(-2), Today), year.LongestStreak);
        Assert.Equal(5, year.ActiveDays);
        Assert.Equal(DayOfWeek.Monday, year.Days[0].Day.DayOfWeek);
        Assert.Equal(Today, year.Days[^1].Day);
        Assert.InRange(year.Days.Count, 365, 371);

        // All time reaches back to the oldest scrobble.
        var (_, allTime) = await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
            .GetInsightsAsync(user.Id, StatPeriod.Create(StatRange.All, TimeZoneInfo.Utc), TimeZoneInfo.Utc));
        Assert.Equal((7, 401, 3), (allTime.Scrobbles, allTime.Days, allTime.NewArtists));
    }

    [Fact]
    public async Task ListeningTime_AddsUpLengthsAndSplitsSessions()
    {
        var (user, _) = await _factory.CreateUserAsync("time-user");
        var day = Today.AddDays(-3);
        await IngestAsync(user.Id,
            S("Alpha", "One", At(day, 10, 0)) with { LengthSeconds = 300 },
            // What the client says was played wins over the track's length.
            S("Alpha", "Two", At(day, 10, 5)) with { LengthSeconds = 240, DurationSeconds = 100 },
            // No length: counts as an average one (200 s). 13 minutes after "Two" ended, so the same session.
            S("Beta", "Three", At(day, 10, 20)),
            S("Beta", "Three", At(day, 12, 0)));

        var time = await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
            .ListeningTimeAsync(user.Id, StatPeriod.Create(StatRange.Month, TimeZoneInfo.Utc).Window));

        Assert.Equal((4, 2, 800L), (time.Scrobbles, time.Timed, time.Seconds));
        Assert.True(time.IsKnown && time.IsEstimate);
        Assert.Equal((2, 1600L), (time.Sessions, time.SessionSeconds));
        Assert.Equal(new Session(At(day, 10, 0), At(day, 10, 20) + 200, 3), time.LongestSession);

        var none = await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
            .ListeningTimeAsync(user.Id, new StatWindow(At(Today.AddDays(1), 0))));
        Assert.Equal((0, 0, false, null), (none.Scrobbles, none.Sessions, none.IsKnown, none.LongestSession));

        var page = await _factory.CreatePlainClient().GetStringAsync("/u/time-user/insights?range=month");
        Assert.Contains("&#x2248; 13 min", page); // Razor writes "≈" as an entity
        Assert.Contains("length known for 50% of scrobbles", page);
    }

    [Fact]
    public async Task RediscoveredAndForgotten_FollowLongSilences()
    {
        var (user, _) = await _factory.CreateUserAsync("silence-user");
        var longAgo = Today.AddDays(-400);
        await IngestAsync(user.Id,
            [
                // A favourite that went quiet.
                .. Enumerable.Range(0, 5).Select(i => S("Old", "Dusty", At(longAgo, 8, i))),
                // Heard twice, then again after 298 days.
                S("Back", "First", At(Today.AddDays(-300), 9, 0)),
                S("Back", "First", At(Today.AddDays(-300), 9, 30)),
                .. Enumerable.Range(0, 3).Select(i => S("Back", "Again", At(Today.AddDays(-2), 10, i * 5))),
                // Never away for long, and never heard before.
                S("Steady", "Always", At(Today.AddDays(-100), 9)),
                S("Steady", "Always", At(Today.AddDays(-1), 9)),
                S("Fresh", "Debut", At(Today.AddDays(-1), 11)),
            ]);

        foreach (var range in new[] { StatRange.Month, StatRange.All })
        {
            var back = Assert.Single(await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
                .RediscoveredAsync(user.Id, StatPeriod.Create(range, TimeZoneInfo.Utc).Window, 5)));
            Assert.Equal(("Back", 3, 298, At(Today.AddDays(-2), 10, 0)), (back.Entry.Title, back.Entry.Count, back.SilentDays, back.ReturnedAt));
        }

        // The return is outside this window.
        Assert.Empty(await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
            .RediscoveredAsync(user.Id, new StatWindow(At(Today.AddDays(-350), 0), At(Today.AddDays(-250), 0)), 5)));

        var artist = Assert.Single(await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>().ForgottenAsync(user.Id, ChartKind.Artists, 5)));
        Assert.Equal(("Old", 5, At(longAgo, 8, 4)), (artist.Entry.Title, artist.Entry.Count, artist.LastListen));
        var track = Assert.Single(await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>().ForgottenAsync(user.Id, ChartKind.Tracks, 5)));
        Assert.Equal(("Dusty", "Old", 5), (track.Entry.Title, track.Entry.Artists.Single().Name, track.Entry.Count));

        var page = await _factory.CreatePlainClient().GetStringAsync("/u/silence-user/insights?range=month");
        Assert.Contains("Rediscovered artists", page);
        Assert.Contains("back after 9 months", page);
        Assert.Contains("Forgotten favourites", page);
        Assert.Contains("not played for 6 months", page);
    }

    [Fact]
    public async Task Repeats_FindTheMostPlayedDayAndTheLongestRun()
    {
        var (user, _) = await _factory.CreateUserAsync("repeat-user");
        var day = Today.AddDays(-5);
        await IngestAsync(user.Id,
            S("Alpha", "Loop", At(day, 8, 0)),
            S("Alpha", "Loop", At(day, 8, 5)),
            S("Beta", "Other", At(day, 8, 10)),
            S("Alpha", "Loop", At(day, 8, 15)),
            // A run of three across midnight, two of them on the next day.
            S("Beta", "Other", At(day, 23, 50)),
            S("Beta", "Other", At(day.AddDays(1), 0, 0)),
            S("Beta", "Other", At(day.AddDays(1), 0, 5)),
            S("Gamma", "Once", At(day.AddDays(1), 9)));

        var repeats = await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
            .RepeatsAsync(user.Id, StatPeriod.Create(StatRange.Month, TimeZoneInfo.Utc).Window, TimeZoneInfo.Utc));

        Assert.Equal((8, 3, 1), (repeats.Scrobbles, repeats.Tracks, repeats.PlayedOnce));
        Assert.Equal(("Loop", day, 3), (repeats.MostInADay!.Track.Title, repeats.MostInADay.Day, repeats.MostInADay.Plays));
        Assert.Equal(("Other", 3, At(day, 23, 50)), (repeats.LongestRun!.Track.Title, repeats.LongestRun.Plays, repeats.LongestRun.Start));

        // Nothing is repeated in a window with a single scrobble.
        var single = await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
            .RepeatsAsync(user.Id, new StatWindow(At(day.AddDays(1), 8)), TimeZoneInfo.Utc));
        Assert.Equal((1, 1, 1, null, null), (single.Scrobbles, single.Tracks, single.PlayedOnce, single.MostInADay, single.LongestRun));
    }

    [Fact]
    public async Task Regulars_CountDaysPlayedAndWeeksAtNumberOne()
    {
        var (user, _) = await _factory.CreateUserAsync("regular-user");
        var monday = Today.AddDays(-Weekday(Today) - 21);
        await IngestAsync(user.Id,
            // Week 1: Alpha leads 3 to 1. Week 2: Beta leads 4 to 1. Week 3: only Alpha.
            S("Alpha", "One", At(monday, 9, 0)),
            S("Alpha", "One", At(monday, 9, 10)),
            S("Alpha", "Two", At(monday.AddDays(1), 9)),
            S("Beta", "Three", At(monday.AddDays(6), 9)),
            S("Beta", "Three", At(monday.AddDays(7), 9, 0)),
            S("Beta", "Three", At(monday.AddDays(7), 9, 10)),
            S("Alpha", "One", At(monday.AddDays(8), 9)),
            S("Beta", "Three", At(monday.AddDays(13), 9, 0)),
            S("Beta", "Three", At(monday.AddDays(13), 9, 10)),
            S("Alpha", "One", At(monday.AddDays(14), 9)));

        var regulars = await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
            .RegularsAsync(user.Id, new StatWindow(At(monday, 0, 0)), TimeZoneInfo.Utc, 5));

        Assert.Equal(3, regulars.Weeks);
        Assert.Equal([("Alpha", 4), ("Beta", 3)], regulars.MostDays.Select(e => (e.Title, e.Count)));
        Assert.Equal([("Alpha", 2), ("Beta", 1)], regulars.WeeksAtTop.Select(e => (e.Title, e.Count)));
        Assert.Equal([1, 2], regulars.WeeksAtTop.Select(e => e.Rank));

        // With weeks starting on Sunday, Beta's second Sunday joins Alpha's lone Monday and outplays it.
        var sundays = await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
            .RegularsAsync(user.Id, new StatWindow(At(monday, 0, 0)), TimeZoneInfo.Utc, 1, DayOfWeek.Sunday));
        Assert.Equal(3, sundays.Weeks);
        Assert.Equal([("Beta", 2)], sundays.WeeksAtTop.Select(e => (e.Title, e.Count)));
    }

    [Fact]
    public async Task MilestonesAndOnThisDay_LookBack()
    {
        var (user, _) = await _factory.CreateUserAsync("history-user");
        var lastYear = Today.AddYears(-1);
        var start = At(Today.AddYears(-3), 12, 0);
        await IngestAsync(user.Id,
            [
                // 1,001 scrobbles a minute apart, three years ago: the first one and the thousandth are milestones.
                .. Enumerable.Range(0, 1001).Select(i => S("Filler", i == 999 ? "Thousandth" : $"Song {i % 50}", start + i * 60L)),
                S("Alpha", "One", At(lastYear, 9, 0)),
                S("Alpha", "Two", At(lastYear, 9, 10)),
                S("Beta", "Three", At(lastYear, 9, 20)),
                S("Beta", "Three", At(lastYear, 9, 30)),
                S("Alpha", "One", At(lastYear, 9, 40)),
                S("Gamma", "Elsewhere", At(lastYear.AddDays(1), 9)),
            ]);

        var milestones = await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>().MilestonesAsync(user.Id));
        Assert.Equal((1007, 5000), (milestones.Scrobbles, milestones.Next));
        Assert.Equal([(1000, "Thousandth"), (1, "Song 0")], milestones.Reached.Select(m => (m.Number, m.Scrobble.Title)));
        Assert.Equal(start + 999 * 60L, milestones.Reached[0].Scrobble.ListenedAt);

        var days = await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>().OnThisDayAsync(user.Id, TimeZoneInfo.Utc));
        // Feb 29 has no counterpart in most years; every other date has one a year and three years ago.
        if (Today is not { Month: 2, Day: 29 })
        {
            Assert.Equal([lastYear, Today.AddYears(-3)], days.Select(d => d.Day));
            Assert.Equal((5, "Alpha", 3), (days[0].Scrobbles, days[0].TopArtist.Title, days[0].TopArtist.Count));
            Assert.Equal(("One", 2, "Alpha"), (days[0].TopTrack.Title, days[0].TopTrack.Count, days[0].TopTrack.Artists.Single().Name));
            Assert.Equal("Filler", days[1].TopArtist.Title);
        }

        var page = await _factory.CreatePlainClient().GetStringAsync("/u/history-user/insights");
        Assert.Contains("Milestones", page);
        Assert.Contains("3,993 to go until 5,000", page);
        Assert.Contains("Thousandth", page);
        Assert.Contains("On this day", page);
    }

    [Fact]
    public async Task TrackPage_ShowsReleaseYearAndGenresOfItsAlbums()
    {
        var (user, _) = await _factory.CreateUserAsync("facts-user");
        var day = Today.AddDays(-2);
        ScrobbleInput On(string artist, string title, string? album, int minute) => S(artist, title, At(day, 10, minute)) with { Album = album };
        await IngestAsync(user.Id,
            // Mostly played from a best-of that came out later and has no genres.
            On("Facts Alpha", "Song", "Facts Best Of", 0), On("Facts Alpha", "Song", "Facts Best Of", 1), On("Facts Alpha", "Song", "Facts Original", 2),
            // No album: takes the genres of Alpha's albums, and has no year.
            On("Facts Alpha", "Loose", null, 3),
            // Nothing is known about this one.
            On("Facts Delta", "Nothing", null, 4));
        await _factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MuseFM.Data.AppDbContext>();
            int Album(string title) => db.Albums.Single(a => a.Title == title).Id;
            var now = DateTime.UtcNow;
            db.AlbumMetadata.AddRange(
                new() { AlbumId = Album("Facts Best Of"), ReleaseYear = 2005, UpdatedAt = now, RefreshAfter = now },
                new() { AlbumId = Album("Facts Original"), Genres = "Rock\nBlues Rock", ReleaseYear = 1979, UpdatedAt = now, RefreshAfter = now });
            return await db.SaveChangesAsync();
        });

        async Task<(int Id, TrackFacts Facts)> FactsAsync(string title) => await _factory.WithScopeAsync(async sp =>
        {
            var stats = sp.GetRequiredService<StatsService>();
            var id = sp.GetRequiredService<MuseFM.Data.AppDbContext>().Tracks.Single(t => t.Title == title).Id;
            return (id, await stats.TrackFactsAsync(user.Id, (await stats.TrackAsync(id))!));
        });

        var (song, songFacts) = await FactsAsync("Song");
        Assert.Equal((1979, "Facts Original", true), (songFacts.Release!.Year, songFacts.Release.Album, songFacts.Release.Listened));
        Assert.Equal(["Rock", "Blues Rock"], songFacts.Genres);
        var (_, looseFacts) = await FactsAsync("Loose");
        Assert.Null(looseFacts.Release);
        Assert.Equal(["Blues Rock", "Rock"], looseFacts.Genres);
        var (nothing, nothingFacts) = await FactsAsync("Nothing");
        Assert.Equal((null, 0), (nothingFacts.Release, nothingFacts.Genres.Count));

        var client = _factory.CreatePlainClient();
        var page = await client.GetStringAsync($"/u/facts-user/track/{song}");
        Assert.Contains("<dt>Released</dt>", page);
        Assert.Contains("1979 <span class=\"muted\">on</span>", page);
        Assert.Contains(">Facts Original</a>", page);
        Assert.Contains("<dt>Genres</dt>", page);
        Assert.DoesNotContain("class=\"about\"", await client.GetStringAsync($"/u/facts-user/track/{nothing}"));

        // Someone who only played it from the best-of has no page for the original album to link to.
        var (other, _) = await _factory.CreateUserAsync("facts-other");
        await IngestAsync(other.Id, On("Facts Alpha", "Song", "Facts Best Of", 5));
        var otherPage = await client.GetStringAsync($"/u/facts-other/track/{song}");
        Assert.Contains("1979 <span class=\"muted\">on</span>", otherPage);
        Assert.Contains("Facts Original", otherPage);
        Assert.DoesNotContain(">Facts Original</a>", otherPage);
    }

    [Fact]
    public async Task MusicMix_CountsGenresAndReleaseDecades()
    {
        var (user, _) = await _factory.CreateUserAsync("mix-user");
        var day = Today.AddDays(-2);
        ScrobbleInput On(string artist, string title, string? album, int minute) => S(artist, title, At(day, 10, minute)) with { Album = album };
        await IngestAsync(user.Id,
            On("Mix Alpha", "A1", "Mix Rocks", 0), On("Mix Alpha", "A2", "Mix Rocks", 1), On("Mix Alpha", "A1", "Mix Rocks", 2),
            // No album: takes the genres of Alpha's albums.
            On("Mix Alpha", "Loose", null, 3),
            // The album has no genres, its artist has.
            On("Mix Beta", "B1", "Mix Unknown", 4), On("Mix Beta", "B1", "Mix Unknown", 5),
            On("Mix Gamma", "C1", "Mix Beats", 6), On("Mix Gamma", "C2", "Mix Beats", 7), On("Mix Gamma", "C1", "Mix Beats", 8),
            // Nothing is known about this one.
            On("Mix Delta", "D1", null, 9));
        await _factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MuseFM.Data.AppDbContext>();
            int Album(string title) => db.Albums.Single(a => a.Title == title).Id;
            var now = DateTime.UtcNow;
            db.AlbumMetadata.AddRange(
                new() { AlbumId = Album("Mix Rocks"), Genres = "Rock\nIndie", ReleaseYear = 1994, UpdatedAt = now, RefreshAfter = now },
                new() { AlbumId = Album("Mix Unknown"), UpdatedAt = now, RefreshAfter = now },
                new() { AlbumId = Album("Mix Beats"), Genres = "Hip-Hop", ReleaseYear = 2020, UpdatedAt = now, RefreshAfter = now });
            db.ArtistMetadata.Add(new() { ArtistId = db.Artists.Single(a => a.Name == "Mix Beta").Id, Genres = "hip hop", UpdatedAt = now, RefreshAfter = now });
            return await db.SaveChangesAsync();
        });

        var mix = await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
            .MusicMixAsync(user.Id, StatPeriod.Create(StatRange.Month, TimeZoneInfo.Utc).Window, 10));

        Assert.Equal((10, 9, 6, 3), (mix.Scrobbles, mix.WithGenre, mix.WithYear, mix.GenreCount));
        // "Hip-Hop" and "hip hop" are one genre, shown the way most plays spell it.
        Assert.Equal([new GenreCount("Hip-Hop", 5), new GenreCount("Indie", 4), new GenreCount("Rock", 4)], mix.Genres);
        Assert.Equal([new DecadeCount(1990, 3), new DecadeCount(2000, 0), new DecadeCount(2010, 0), new DecadeCount(2020, 3)], mix.Decades);
        Assert.Equal(2020, mix.MedianYear);
        Assert.Equal([new GenreCount("Hip-Hop", 5)], (await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
            .MusicMixAsync(user.Id, default, 1))).Genres);

        var page = await _factory.CreatePlainClient().GetStringAsync("/u/mix-user/insights?range=month");
        Assert.Contains("Top genres", page);
        Assert.Contains("known for 90% of scrobbles", page);
        Assert.Contains("Release decades", page);
        Assert.Contains("<strong>2020</strong> or later", page);

        // Without any genre or year the cards are left out.
        var (other, _) = await _factory.CreateUserAsync("mix-none");
        await IngestAsync(other.Id, On("Mix Delta", "D1", null, 30));
        Assert.DoesNotContain("Top genres", await _factory.CreatePlainClient().GetStringAsync("/u/mix-none/insights?range=month"));
    }

    [Fact]
    public async Task Compare_PutsAFinishedWeekNextToTheWeekBefore()
    {
        var (user, _) = await _factory.CreateUserAsync("compare-user");
        var thisMonday = Today.AddDays(-Weekday(Today));
        var lastMonday = thisMonday.AddDays(-7);
        await IngestAsync(user.Id,
            S("Beta", "Three", At(lastMonday.AddDays(-6), 8)), // Tuesday of the week before
            S("Alpha", "One", At(lastMonday, 10)),
            S("Alpha", "Two", At(lastMonday.AddDays(2), 21)),
            S("Beta", "Three", At(lastMonday.AddDays(2), 21, 50)));

        var week = await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
            .CompareAsync(user.Id, ComparePeriod.Week, 1, TimeZoneInfo.Utc));

        Assert.False(week.Ongoing);
        Assert.Equal("Week of " + lastMonday.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture), week.Current.Name);
        Assert.Equal((3, 1, 1), (week.CurrentTotals.Scrobbles, week.PreviousTotals.Scrobbles, week.PreviousScrobblesInFull));
        Assert.Equal([1, 0, 2, 0, 0, 0, 0], week.Buckets.Select(b => b.Current));
        Assert.Equal([0, 1, 0, 0, 0, 0, 0], week.Buckets.Select(b => b.Previous));
        Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"], week.Buckets.Select(b => b.Label));
        Assert.Equal(7, week.CurrentBucketsSoFar);
        Assert.Equal((1, 1), (week.NewArtists, week.PreviousNewArtists));

        Assert.Equal(["Alpha", "Beta"], week.TopArtists.Select(r => r.Entry.Title));
        Assert.Equal((null, 0), (week.TopArtists[0].PreviousRank, week.TopArtists[0].PreviousCount));
        Assert.Equal((1, 1), (week.TopArtists[1].PreviousRank!.Value, week.TopArtists[1].PreviousCount));
        Assert.Equal("new", Ui.Movement(week.TopArtists[0].Entry.Rank, week.TopArtists[0].PreviousRank).Css);
        Assert.Equal("▼1", Ui.Movement(week.TopArtists[1].Entry.Rank, week.TopArtists[1].PreviousRank).Text);

        var today = await _factory.WithScopeAsync(sp => sp.GetRequiredService<InsightsService>()
            .CompareAsync(user.Id, ComparePeriod.Day, 0, TimeZoneInfo.Utc));
        Assert.True(today.Ongoing);
        Assert.Equal(("Today", "Yesterday", 24), (today.Current.Name, today.Previous.Name, today.Buckets.Count));
        Assert.Equal(DateTime.UtcNow.Hour + 1, today.CurrentBucketsSoFar);
    }

    [Fact]
    public async Task Pages_RenderChartsAndKeepNamesInert()
    {
        var (user, _) = await _factory.CreateUserAsync("pages-user");
        await IngestAsync(user.Id,
            S("Evil <b>bold</b>", "Track", At(Today.AddDays(-1), 20)),
            S("Alpha", "One", At(Today.AddDays(-8), 20)));
        var client = _factory.CreatePlainClient();

        var insights = await client.GetStringAsync("/u/pages-user/insights");
        Assert.Contains("class=\"heatmap\"", insights);
        Assert.Contains("class=\"cal-grid\"", insights);
        Assert.Contains("Evil &lt;b&gt;bold&lt;/b&gt;", insights);
        Assert.DoesNotContain("<b>bold</b>", insights);

        var compare = await client.GetStringAsync("/u/pages-user/compare?period=week&offset=0");
        Assert.Contains("line-chart", compare);
        Assert.Contains("columns-chart", compare);

        foreach (var url in new[]
                 {
                     "/u/pages-user/insights?range=week", "/u/pages-user/insights?range=all", "/u/pages-user/compare?period=day",
                     "/u/pages-user/compare?period=month&offset=2", "/u/pages-user/compare?period=year&offset=1",
                     "/u/pages-user/compare?period=week&offset=99999", "/u/pages-user/compare?period=bogus&offset=-3", "/u/pages-user",
                 })
        {
            var response = await client.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)response.StatusCode}");
        }
    }

    [Fact]
    public void LocalClock_UsesTheOffsetOfEachMoment()
    {
        var clock = new LocalClock(TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"));
        // Summer time (UTC+2) and winter time (UTC+1), whatever the current offset is.
        Assert.Equal(new DateTime(2026, 7, 1, 14, 0, 0), clock.ToLocal(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()));
        Assert.Equal(new DateTime(2026, 1, 15, 13, 0, 0), clock.ToLocal(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()));
        // 02:30 on 29 March 2026 does not exist there (02:00 jumps to 03:00): it moves forward to 03:00 CEST.
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), clock.ToUnix(new DateTime(2026, 3, 29, 2, 30, 0)));
    }

    [Fact]
    public void ChartHelpers()
    {
        Assert.Equal([0, 2, 4, 6], Ui.NiceScale(5).Ticks);
        Assert.Equal([0, 1], Ui.NiceScale(1).Ticks);
        Assert.Equal([0, 10, 20, 30, 40], Ui.NiceScale(37).Ticks);
        Assert.Equal(1500, Ui.NiceScale(1234).Top);
        Assert.Equal("12.3K", Ui.Compact(12_345));
        Assert.Equal(("▲ 50%", "▼ 25%", "±0%", "▲ from 0"), (Ui.Delta(3, 2).Text, Ui.Delta(3, 4).Text, Ui.Delta(5, 5).Text, Ui.Delta(1, 0).Text));
        Assert.Equal(("▲2", "–"), (Ui.Movement(1, 3).Text, Ui.Movement(4, 4).Text));
        Assert.Equal([0, 1, 2, 4, 4], new[] { 0, 1, 10, 99, 100 }.Select(v => Ui.HeatLevel(v, 100)));
        Assert.Equal(("0 min", "1 min", "59 min", "1 h 0 min", "99 h 59 min", "1,320 h"),
            (Ui.Hours(0), Ui.Hours(20), Ui.Hours(3599), Ui.Hours(3600), Ui.Hours(359_999), Ui.Hours(1320 * 3600 + 5)));
        Assert.Equal(("1 day", "59 days", "6 months", "2 years", "2.5 years"), (Ui.Stretch(1), Ui.Stretch(59), Ui.Stretch(180), Ui.Stretch(731), Ui.Stretch(913)));
    }
}
