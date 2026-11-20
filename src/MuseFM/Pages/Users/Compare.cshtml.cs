using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages.Users;

/// <summary>/u/{username}/compare: a day, week, month or year next to the one before it.</summary>
public sealed class CompareModel(UserManager<AppUser> users, InsightsService insights) : UserPageModel(users)
{
    public Comparison Data { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(string username, string? period, int offset = 0, CancellationToken ct = default)
    {
        if (!await LoadOwnerAsync(username)) return NotFound();
        var parsed = period?.ToLowerInvariant() switch
        {
            "day" => ComparePeriod.Day,
            "month" => ComparePeriod.Month,
            "year" => ComparePeriod.Year,
            _ => ComparePeriod.Week,
        };
        Data = await insights.CompareAsync(Owner.Id, parsed, offset, Zone, ViewerPreferences.Current.FirstDayOfWeek, ct);
        return Page();
    }

    public string CompareUrl(ComparePeriod period, int offset = 0) =>
        $"{ProfileUrl}/compare?period={period.ToString().ToLowerInvariant()}" + (offset > 0 ? $"&offset={offset}" : "");

    /// <summary>"day", "week"...</summary>
    public string Unit => Data.Period.ToString().ToLowerInvariant();

    public string BucketName => Data.Period switch
    {
        ComparePeriod.Day => "Hour",
        ComparePeriod.Year => "Month",
        _ => "Day",
    };

    private IReadOnlyList<ChartSeries> Series(int current, int previous) =>
    [
        new(Data.Current.Name, "current", Ui.Number(current)),
        new(Data.Previous.Name, "previous", Ui.Number(previous)),
    ];

    /// <summary>Running totals: is this period ahead of the last one?</summary>
    public ChartModel PaceChart
    {
        get
        {
            var buckets = Data.Buckets;
            int current = 0, previous = 0;
            var groups = new List<ChartGroup>(buckets.Count);
            for (var i = 0; i < buckets.Count; i++)
            {
                current += buckets[i].Current;
                previous += buckets[i].Previous;
                var title = Data.Period == ComparePeriod.Day ? $"By {Ui.Hour(i + 1)}" : $"By the end of {buckets[i].Title}";
                groups.Add(new ChartGroup(Label(i), title, [i < Data.CurrentBucketsSoFar ? current : null, previous]));
            }

            return new ChartModel($"Running total of scrobbles, {Data.Current.Name} against {Data.Previous.Name}", BucketName,
                Series(Data.CurrentTotals.Scrobbles, Data.PreviousScrobblesInFull), groups);
        }
    }

    public ChartModel BucketChart => new($"Scrobbles per {BucketName.ToLowerInvariant()}, {Data.Current.Name} against {Data.Previous.Name}", BucketName,
        Series(Data.CurrentTotals.Scrobbles, Data.PreviousScrobblesInFull),
        Data.Buckets.Select((b, i) => new ChartGroup(Label(i), Data.Period == ComparePeriod.Day ? Ui.HourSpan(i) : b.Title,
            [i < Data.CurrentBucketsSoFar ? b.Current : null, b.Previous])).ToList());

    /// <summary>Axis label; hours follow the viewer's clock.</summary>
    private string Label(int i) => Data.Period == ComparePeriod.Day ? (i % 3 == 0 ? Ui.HourTick(i) : "") : Data.Buckets[i].Label;
}
