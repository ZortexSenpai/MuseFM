using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages.Users;

/// <summary>/u/{username}/taste?with=other: how much two users' listening has in common.</summary>
public sealed class TasteModel(UserManager<AppUser> users, SocialService social, StatsService stats) : UserPageModel(users)
{
    public StatPeriod Period { get; private set; } = null!;
    public int FirstYear { get; private set; }

    /// <summary>The user compared with; null until one is chosen.</summary>
    public string? Other { get; private set; }

    public string? Problem { get; private set; }
    public TasteComparison? Result { get; private set; }

    public async Task<IActionResult> OnGetAsync(string username, string? with, string? range, CancellationToken ct)
    {
        if (!await LoadOwnerAsync(username)) return NotFound();
        // Taste shows over time: default to the past 12 months.
        Period = ResolvePeriod(range, StatRange.Year);
        FirstYear = await stats.FirstYearAsync(Owner.Id, Zone, ct);

        var name = with?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            if (ViewerId is null || IsOwner) return Page();
            name = User.Identity!.Name!;
        }

        var other = await Users.FindByNameAsync(name);
        if (other is null || (!other.IsPublic && other.Id != ViewerId && !ViewerIsAdmin))
        {
            Problem = $"There is no user called “{name}”.";
            return Page();
        }

        if (other.Id == Owner.Id)
        {
            Problem = "Pick someone else to compare with.";
            return Page();
        }

        Other = other.UserName;
        Result = await social.CompareAsync(Owner.Id, other.Id, Period.Window, ct);
        return Page();
    }
}
