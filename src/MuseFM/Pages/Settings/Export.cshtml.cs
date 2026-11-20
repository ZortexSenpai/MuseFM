using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages.Settings;

/// <summary>The downloads themselves are served by ExportEndpoints.</summary>
public sealed class ExportModel(UserManager<AppUser> users, StatsService stats) : PageModel
{
    public int ScrobbleCount { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) =>
        ScrobbleCount = await stats.CountAsync(users.GetUserId(User)!, ct: ct);
}
