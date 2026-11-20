using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;

namespace MuseFM.Pages;

public sealed class SearchModel(AppDbContext db, UserManager<AppUser> users) : PageModel
{
    public const int Limit = 50;

    public sealed record UserResult(string UserName, string? AvatarFile, bool IsPublic, DateTime CreatedAt, int Scrobbles, long? LastScrobble);

    public string Query { get; private set; } = "";
    public List<UserResult> Results { get; private set; } = [];

    public async Task OnGetAsync(string? q, CancellationToken ct)
    {
        Query = (q ?? "").Trim();
        var me = users.GetUserId(User);
        var isAdmin = User.IsInRole(Roles.Admin);

        var query = db.Users.AsNoTracking().Where(u => u.IsPublic || u.Id == me || isAdmin);
        if (Query.Length > 0)
        {
            var normalized = Query.ToUpperInvariant();
            query = query.Where(u => u.NormalizedUserName!.Contains(normalized));
        }

        Results = await query
            .OrderBy(u => u.NormalizedUserName)
            .Take(Limit)
            .Select(u => new UserResult(
                u.UserName!,
                u.AvatarFile,
                u.IsPublic,
                u.CreatedAt,
                db.Scrobbles.Count(s => s.UserId == u.Id),
                db.Scrobbles.Where(s => s.UserId == u.Id).Max(s => (long?)s.ListenedAt)))
            .ToListAsync(ct);
    }
}
