using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages.Users;

public sealed class ScrobblesModel(UserManager<AppUser> users, StatsService stats, FeedbackService feedback, AppDbContext db) : UserPageModel(users)
{
    private const int PageSize = 50;

    public Paged<ScrobbleRow> History { get; private set; } = null!;
    public IReadOnlySet<int> Loved { get; private set; } = new HashSet<int>();

    // "p" rather than "page": "page" is a reserved route value in Razor Pages.
    public async Task<IActionResult> OnGetAsync(string username, int p = 1, CancellationToken ct = default)
    {
        if (!await LoadOwnerAsync(username)) return NotFound();
        History = await stats.RecentAsync(Owner.Id, Math.Max(1, p), PageSize, ct: ct);
        Loved = await feedback.LovedAmongAsync(Owner.Id, History.Items.Select(r => r.TrackId), ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string username, long id, int p = 1, CancellationToken ct = default)
    {
        if (!await LoadOwnerAsync(username) || !IsOwner) return NotFound();
        await db.Scrobbles.Where(s => s.Id == id && s.UserId == Owner.Id).ExecuteDeleteAsync(ct);
        return RedirectToPage(new { username = UserName, p });
    }
}
