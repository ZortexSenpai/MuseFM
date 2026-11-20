using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MuseFM.Data.Entities;
using MuseFM.Services;
using MuseFM.Services.Metadata;

namespace MuseFM.Pages.Users;

/// <summary>/u/{username}/album/{id}: one album in someone's listening.</summary>
public sealed class AlbumModel(UserManager<AppUser> users, StatsService stats, FeedbackService feedback, MetadataService metadata) : UserPageModel(users)
{
    private const int TrackCount = 50;
    private const int HistorySize = 20;

    public CatalogItem Album { get; private set; } = null!;
    public ListenSummary Summary { get; private set; } = null!;
    public StatPeriod Period { get; private set; } = null!;
    public int FirstYear { get; private set; }
    public bool ShowCover { get; private set; }
    public IReadOnlyList<ActivityBucket> Activity { get; private set; } = [];
    public IReadOnlyList<ChartEntry> Tracks { get; private set; } = [];
    public Paged<ScrobbleRow> History { get; private set; } = null!;
    public IReadOnlySet<int> Loved { get; private set; } = new HashSet<int>();

    public async Task<IActionResult> OnGetAsync(string username, int id, string? range, int p = 1, CancellationToken ct = default)
    {
        if (!await LoadOwnerAsync(username)) return NotFound();

        // Like artist pages, an album page is about the whole history with it.
        Period = ResolvePeriod(range, StatRange.All);
        var subject = StatSubject.Album(id);
        var summary = await stats.SummaryAsync(Owner.Id, subject, Period.Window, ct);
        if (summary is null || await stats.AlbumAsync(id, ct) is not { } album) return NotFound();

        Album = album;
        Summary = summary;
        FirstYear = await stats.FirstYearAsync(Owner.Id, Zone, ct);
        ShowCover = metadata.Enabled && ViewerPreferences.Current.ShowArtwork;
        Activity = await stats.ActivityAsync(Owner.Id, Period, subject, ct);
        Tracks = (await stats.TopTracksAsync(Owner.Id, Period.Window, 1, TrackCount, subject, ct)).Items;
        History = await stats.RecentAsync(Owner.Id, Math.Max(1, p), HistorySize, subject, Period.Window, ct);
        Loved = await feedback.LovedAmongAsync(Owner.Id, History.Items.Select(r => r.TrackId), ct);
        return Page();
    }
}
