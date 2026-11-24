using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MuseFM.Data.Entities;
using MuseFM.Services;
using MuseFM.Services.Metadata;

namespace MuseFM.Pages.Users;

/// <summary>/u/{username}/track/{id}: one track in someone's listening.</summary>
public sealed class TrackModel(
    UserManager<AppUser> users, StatsService stats, FeedbackService feedback, ObsessionService obsessions, MetadataService metadata) : UserPageModel(users)
{
    private const int AlbumCount = 25;
    private const int HistorySize = 20;

    public CatalogItem Track { get; private set; } = null!;
    public ListenSummary Summary { get; private set; } = null!;
    public StatPeriod Period { get; private set; } = null!;
    public int FirstYear { get; private set; }
    public bool ShowCover { get; private set; }

    /// <summary>Release year and genres, as far as the metadata connectors knew them.</summary>
    public TrackFacts Facts { get; private set; } = null!;
    public IReadOnlyList<ActivityBucket> Activity { get; private set; } = [];
    public IReadOnlyList<ChartEntry> Albums { get; private set; } = [];
    public Paged<ScrobbleRow> History { get; private set; } = null!;

    /// <summary>The owner's feedback: 1 loved, -1 disliked (from ListenBrainz), null none.</summary>
    public int? Score { get; private set; }

    /// <summary>The owner's current obsession, which may be another track.</summary>
    public CurrentObsession? Obsession { get; private set; }

    public bool IsObsession => Obsession?.Track.Id == Track.Id;

    /// <summary>Scrobbles × track length, when the length is known.</summary>
    public TimeSpan? ListeningTime => Track.LengthSeconds is { } length ? TimeSpan.FromSeconds((double)length * Summary.AllTimeScrobbles) : null;

    public async Task<IActionResult> OnGetAsync(string username, int id, string? range, int p = 1, CancellationToken ct = default)
    {
        if (!await LoadOwnerAsync(username)) return NotFound();

        Period = ResolvePeriod(range, StatRange.All);
        var subject = StatSubject.Track(id);
        var summary = await stats.SummaryAsync(Owner.Id, subject, Period.Window, ct);
        if (summary is null || await stats.TrackAsync(id, ct) is not { } track) return NotFound();

        Track = track;
        Summary = summary;
        FirstYear = await stats.FirstYearAsync(Owner.Id, Zone, ct);
        ShowCover = metadata.Enabled && ViewerPreferences.Current.ShowArtwork;
        Facts = await stats.TrackFactsAsync(Owner.Id, track, ct);
        Activity = await stats.ActivityAsync(Owner.Id, Period, subject, ct);
        Albums = (await stats.TopAlbumsAsync(Owner.Id, Period.Window, 1, AlbumCount, subject, ct)).Items;
        History = await stats.RecentAsync(Owner.Id, Math.Max(1, p), HistorySize, subject, Period.Window, ct);
        Score = await feedback.ScoreAsync(Owner.Id, id, ct);
        Obsession = await obsessions.CurrentAsync(Owner.Id, ct);
        return Page();
    }

    /// <summary>Makes the track the owner's obsession, shown on their profile for the chosen <paramref name="length"/>.</summary>
    public async Task<IActionResult> OnPostObsessionAsync(string username, int id, string? length, CancellationToken ct)
    {
        if (!await LoadOwnerAsync(username) || !IsOwner) return NotFound();
        if (await stats.TrackAsync(id, ct) is null) return NotFound();
        if (ObsessionService.FindLength(length) is not { } chosen) return BadRequest();
        await obsessions.SetAsync(Owner.Id, id, chosen, ct);
        return RedirectToPage(new { username = UserName, id });
    }

    /// <summary>Takes the owner's current obsession off their profile.</summary>
    public async Task<IActionResult> OnPostEndObsessionAsync(string username, int id, string? returnUrl, CancellationToken ct)
    {
        if (!await LoadOwnerAsync(username) || !IsOwner) return NotFound();
        await obsessions.EndAsync(Owner.Id, ct);
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage(new { username = UserName, id });
    }

    /// <summary>Loves (score 1) or unloves (0) the track; only the profile owner can.</summary>
    public async Task<IActionResult> OnPostLoveAsync(string username, int id, int score, string? returnUrl, CancellationToken ct)
    {
        if (!await LoadOwnerAsync(username) || !IsOwner) return NotFound();
        if (await stats.TrackAsync(id, ct) is null) return NotFound();
        await feedback.SetAsync(Owner.Id, id, score == TrackFeedback.Love ? TrackFeedback.Love : 0, ct: ct);
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage(new { username = UserName, id });
    }
}
