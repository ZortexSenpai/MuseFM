using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages.Users;

/// <summary>/u/{username}/loved: the tracks someone loves, or (from ListenBrainz) dislikes.</summary>
public sealed class LovedModel(UserManager<AppUser> users, FeedbackService feedback) : UserPageModel(users)
{
    private const int PageSize = 50;

    public Paged<FeedbackRow> Tracks { get; private set; } = null!;
    public bool Disliked { get; private set; }
    public int DislikedCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(string username, string? show, int p = 1, CancellationToken ct = default)
    {
        if (!await LoadOwnerAsync(username)) return NotFound();
        DislikedCount = await feedback.CountAsync(Owner.Id, TrackFeedback.Hate, ct);
        Disliked = show == "disliked" && DislikedCount > 0;
        Tracks = await feedback.ListAsync(Owner.Id, Disliked ? TrackFeedback.Hate : TrackFeedback.Love, Math.Max(1, p), PageSize, ct);
        return Page();
    }
}
