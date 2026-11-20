using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages.Users;

public sealed class ProfileModel(
    UserManager<AppUser> users, StatsService stats, FeedbackService feedback, ObsessionService obsessions, NowPlayingStore nowPlaying) : UserPageModel(users)
{
    private const int TopCount = 10;
    private const int RecentCount = 15;

    public StatPeriod Period { get; private set; } = null!;
    public int FirstYear { get; private set; }
    public Totals Totals { get; private set; } = new(0, 0, 0, 0);

    /// <summary>The period before this one (see <see cref="StatPeriod.Previous"/>); null for all time.</summary>
    public Totals? PreviousTotals { get; private set; }

    public string PreviousLabel { get; private set; } = "";
    public int AllTimeScrobbles { get; private set; }
    public IReadOnlyList<ActivityBucket> Activity { get; private set; } = [];
    public IReadOnlyList<ChartEntry> TopArtists { get; private set; } = [];
    public IReadOnlyList<ChartEntry> TopAlbums { get; private set; } = [];
    public IReadOnlyList<ChartEntry> TopTracks { get; private set; } = [];
    public IReadOnlyList<ScrobbleRow> Recent { get; private set; } = [];
    public IReadOnlySet<int> Loved { get; private set; } = new HashSet<int>();
    public NowPlaying? NowPlaying { get; private set; }
    public CurrentObsession? Obsession { get; private set; }

    public async Task<IActionResult> OnGetAsync(string username, string? range, CancellationToken ct)
    {
        if (!await LoadOwnerAsync(username)) return NotFound();

        Period = ResolvePeriod(range, ViewerPreferences.Current.DefaultRange ?? StatRange.Month);
        FirstYear = await stats.FirstYearAsync(Owner.Id, Zone, ct);
        NowPlaying = nowPlaying.Get(Owner.Id);
        Obsession = await obsessions.CurrentAsync(Owner.Id, ct);
        Totals = await stats.GetTotalsAsync(Owner.Id, Period.Window, ct);
        if (Period.Previous(Zone) is { } previous)
        {
            PreviousTotals = await stats.GetTotalsAsync(Owner.Id, previous.Window, ct);
            PreviousLabel = previous.Label;
        }

        Activity = await stats.ActivityAsync(Owner.Id, Period, ct: ct);
        TopArtists = (await stats.TopArtistsAsync(Owner.Id, Period.Window, 1, TopCount, ct)).Items;
        TopAlbums = (await stats.TopAlbumsAsync(Owner.Id, Period.Window, 1, TopCount, ct: ct)).Items;
        TopTracks = (await stats.TopTracksAsync(Owner.Id, Period.Window, 1, TopCount, ct: ct)).Items;
        var recent = await stats.RecentAsync(Owner.Id, 1, RecentCount, ct: ct);
        Recent = recent.Items;
        Loved = await feedback.LovedAmongAsync(Owner.Id, Recent.Select(r => r.TrackId), ct);
        AllTimeScrobbles = recent.Total;
        return Page();
    }
}
