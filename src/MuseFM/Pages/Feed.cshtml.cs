using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages;

/// <summary>/feed: what the people you follow are listening to.</summary>
[Authorize]
public sealed class FeedModel(UserManager<AppUser> users, SocialService social) : PageModel
{
    private const int PageSize = 50;

    public FeedPage Feed { get; private set; } = new([], null);
    public IReadOnlyList<(string UserName, NowPlaying Playing)> PlayingNow { get; private set; } = [];
    public bool Older { get; private set; }
    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Utc;

    public async Task<IActionResult> OnGetAsync(string? before, CancellationToken ct)
    {
        // The account can be gone while its cookie is still valid.
        if (await users.GetUserAsync(User) is not { } me) return Challenge();
        var isAdmin = User.IsInRole(Roles.Admin);
        Zone = TimeZones.FindOrUtc(me.TimeZoneId);
        Older = before is not null;
        Feed = await social.FeedAsync(me.Id, isAdmin, before, PageSize, ct);
        if (!Older) PlayingNow = await social.PlayingNowAsync(me.Id, isAdmin, ct);
        return Page();
    }
}
