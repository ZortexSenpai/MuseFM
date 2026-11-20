using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages.Users;

/// <summary>/u/{username}/insights: when and how someone listens.</summary>
public sealed class InsightsModel(UserManager<AppUser> users, InsightsService insights, StatsService stats) : UserPageModel(users)
{
    private const int ListCount = 5;
    private const int GenreCount = 10;

    public StatPeriod Period { get; private set; } = null!;
    public int FirstYear { get; private set; }
    public ListeningYear Year { get; private set; } = null!;
    public ListeningPatterns Patterns { get; private set; } = null!;
    public ListeningTime Listening { get; private set; } = null!;

    /// <summary>Artists the owner came back to within the period.</summary>
    public IReadOnlyList<Comeback> Rediscovered { get; private set; } = [];

    /// <summary>Favourites of all time that have gone quiet; these do not depend on the period.</summary>
    public IReadOnlyList<Forgotten> ForgottenArtists { get; private set; } = [];

    public IReadOnlyList<Forgotten> ForgottenTracks { get; private set; } = [];
    public Repeats Repeats { get; private set; } = null!;
    public Regulars Regulars { get; private set; } = null!;

    /// <summary>Genres and release decades, as far as the metadata connectors supplied them.</summary>
    public MusicMix Mix { get; private set; } = null!;

    /// <summary>Round numbers of scrobbles and today's date in earlier years; like the forgotten favourites, whatever the period.</summary>
    public Milestones Milestones { get; private set; } = null!;

    public IReadOnlyList<DayInHistory> OnThisDay { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string username, string? range, CancellationToken ct)
    {
        if (!await LoadOwnerAsync(username)) return NotFound();

        // Patterns need some data to say anything, so this page defaults to the past 12 months.
        Period = ResolvePeriod(range, StatRange.Year);
        FirstYear = await stats.FirstYearAsync(Owner.Id, Zone, ct);
        (Year, Patterns) = await insights.GetInsightsAsync(Owner.Id, Period, Zone, ViewerPreferences.Current.FirstDayOfWeek, ct);
        Listening = await insights.ListeningTimeAsync(Owner.Id, Period.Window, ct);
        Rediscovered = await insights.RediscoveredAsync(Owner.Id, Period.Window, ListCount, ct);
        ForgottenArtists = await insights.ForgottenAsync(Owner.Id, ChartKind.Artists, ListCount, ct);
        ForgottenTracks = await insights.ForgottenAsync(Owner.Id, ChartKind.Tracks, ListCount, ct);
        Repeats = await insights.RepeatsAsync(Owner.Id, Period.Window, Zone, ct);
        Regulars = await insights.RegularsAsync(Owner.Id, Period.Window, Zone, ListCount, ViewerPreferences.Current.FirstDayOfWeek, ct);
        Mix = await insights.MusicMixAsync(Owner.Id, Period.Window, GenreCount, ct);
        Milestones = await insights.MilestonesAsync(Owner.Id, ct);
        OnThisDay = await insights.OnThisDayAsync(Owner.Id, Zone, ct);
        return Page();
    }

    /// <summary>Day names in the order of the viewer's week.</summary>
    private string[] Weekdays => Enumerable.Range(0, 7)
        .Select(i => CultureInfo.InvariantCulture.DateTimeFormat.DayNames[((int)Patterns.FirstDayOfWeek + i) % 7])
        .ToArray();

    public string PeakHour => Ui.HourSpan(Array.IndexOf(Patterns.ByHour, Patterns.ByHour.Max()));

    public string PeakWeekday => Weekdays[Array.IndexOf(Patterns.ByWeekday, Patterns.ByWeekday.Max())];

    public ChartModel HourChart => new("Scrobbles per hour of the day", "Hour", [new ChartSeries("Scrobbles", "current")],
        Enumerable.Range(0, 24).Select(h => new ChartGroup(h % 3 == 0 ? Ui.HourTick(h) : "", Ui.HourSpan(h), [Patterns.ByHour[h]])).ToList());

    public ChartModel WeekdayChart => new("Scrobbles per day of the week", "Day", [new ChartSeries("Scrobbles", "current")],
        Enumerable.Range(0, 7).Select(d => new ChartGroup(Weekdays[d][..3], Weekdays[d], [Patterns.ByWeekday[d]])).ToList());

    public HeatmapModel WeekHeatmap => new("Scrobbles by day of the week and hour",
        Weekdays.Select(d => d[..3]).ToList(),
        Enumerable.Range(0, 24).Select(h => h % 3 == 0 ? Ui.HourTick(h) : "").ToList(),
        Enumerable.Range(0, 24).Select(Ui.HourSpan).ToList(),
        Patterns.ByWeekdayHour);

    public TopListModel RediscoveredList => new(UserName, ChartKind.Artists, Rediscovered.Select(c => c.Entry).ToList(),
        Rediscovered.ToDictionary(c => c.Entry.Id, c => $"back after {Ui.Stretch(c.SilentDays)}"));

    public TopListModel ForgottenList(IReadOnlyList<Forgotten> forgotten, ChartKind kind) => new(UserName, kind, forgotten.Select(f => f.Entry).ToList(),
        forgotten.ToDictionary(f => f.Entry.Id, f => $"last played {Ui.Date(f.LastListen, Zone)}"));

    public ChartModel DecadeChart => new("Scrobbles by the decade their album came out", "Decade", [new ChartSeries("Scrobbles", "current")],
        Mix.Decades.Select(d => new ChartGroup(DecadeLabel(d.Decade), $"{d.Decade}–{d.Decade + 9}", [d.Plays])).ToList());

    /// <summary>"90s", but "1890s" and "2020s" where two digits could be either century.</summary>
    private static string DecadeLabel(int decade) =>
        decade is >= 1930 and < 2000 ? $"{decade % 100}s" : decade.ToString(CultureInfo.InvariantCulture) + "s";

    public ChartModel DiscoveryChart => new("Artists heard for the first time", "Period", [new ChartSeries("New artists", "current")],
        Patterns.Discoveries.Select(b => new ChartGroup(b.Label, b.Tooltip, [b.Count])).ToList(), "new artists");
}
