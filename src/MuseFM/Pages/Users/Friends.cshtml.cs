using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages.Users;

/// <summary>/u/{username}/friends: who someone follows (with what they are listening to) and who follows them.</summary>
public sealed class FriendsModel(UserManager<AppUser> users, SocialService social) : UserPageModel(users)
{
    public IReadOnlyList<FriendRow> FollowingList { get; private set; } = [];
    public IReadOnlyList<UserRef> FollowerList { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string username, CancellationToken ct)
    {
        if (!await LoadOwnerAsync(username)) return NotFound();
        FollowingList = await social.FollowingAsync(Owner.Id, ViewerId, ViewerIsAdmin, ct);
        FollowerList = await social.FollowersAsync(Owner.Id, ViewerId, ViewerIsAdmin, ct);
        return Page();
    }

    /// <summary>
    /// Follows or unfollows the profile owner. Only public profiles can be followed; unfollowing works even after the
    /// profile went private.
    /// </summary>
    public async Task<IActionResult> OnPostFollowAsync(string username, bool follow, string? returnUrl, CancellationToken ct)
    {
        if (Users.GetUserId(User) is not { } viewerId) return Challenge();
        var target = await Users.FindByNameAsync(username);
        if (target is null) return NotFound();
        if (follow)
        {
            if (!await LoadOwnerAsync(username)) return NotFound();
            await social.FollowAsync(viewerId, Owner, ct);
        }
        else if (!await social.UnfollowAsync(viewerId, target.Id, ct) && !await LoadOwnerAsync(username))
        {
            // Not followed and not visible: the same answer as for a name nobody has, or it would give private users away.
            return NotFound();
        }

        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage(new { username = target.UserName });
    }
}
