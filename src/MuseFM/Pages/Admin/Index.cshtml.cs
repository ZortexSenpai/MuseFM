using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services.Metadata;

namespace MuseFM.Pages.Admin;

/// <summary>/admin: the users of this server. Accounts are created and edited on their own pages (AdminUser, NewUser).</summary>
public sealed class IndexModel(AppDbContext db, UserManager<AppUser> users, IOptions<MuseFmOptions> options, ConnectorSettings connectors) : PageModel
{
    public sealed record Row(string Id, string UserName, DateTime CreatedAt, bool IsPublic, bool IsAdmin, bool IsSelf, int Scrobbles, long? LastScrobble);

    public List<Row> Rows { get; private set; } = [];
    public bool RegistrationEnabled => options.Value.AllowRegistration;
    public bool LastFmImportEnabled => connectors.LastFmApiKey is not null;

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public bool StatusIsError { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var me = users.GetUserId(User);
        var admins = (await users.GetUsersInRoleAsync(Roles.Admin)).Select(u => u.Id).ToHashSet();
        var rows = await db.Users.AsNoTracking()
            .OrderBy(u => u.NormalizedUserName)
            .Select(u => new
            {
                u.Id,
                u.UserName,
                u.CreatedAt,
                u.IsPublic,
                Scrobbles = db.Scrobbles.Count(s => s.UserId == u.Id),
                Last = db.Scrobbles.Where(s => s.UserId == u.Id).Max(s => (long?)s.ListenedAt),
            })
            .ToListAsync(ct);
        Rows = rows.Select(u => new Row(u.Id, u.UserName!, u.CreatedAt, u.IsPublic, admins.Contains(u.Id), u.Id == me, u.Scrobbles, u.Last)).ToList();
    }
}
