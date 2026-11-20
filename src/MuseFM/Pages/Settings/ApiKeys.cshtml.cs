using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages.Settings;

public sealed class ApiKeysModel(UserManager<AppUser> users, ApiKeyService keys) : PageModel
{
    public List<ApiKey> Keys { get; private set; } = [];
    public List<ScrobblerSession> Sessions { get; private set; } = [];
    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Utc;
    public string UserName { get; private set; } = "";
    public string BaseUrl => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

    // The raw key is shown exactly once, in the response to the create request. It deliberately does not go
    // through TempData: that would put the secret in a cookie, and TempData turns UUID-shaped strings into Guids.
    public string? NewToken { get; private set; }

    public string? NewTokenName { get; private set; }

    public Task OnGetAsync(CancellationToken ct) => LoadAsync(ct);

    public async Task<IActionResult> OnPostCreateAsync(string? name, CancellationToken ct)
    {
        var (key, token) = await keys.CreateAsync(users.GetUserId(User)!, name, ct);
        NewToken = token;
        NewTokenName = key.Name;
        await LoadAsync(ct);
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var user = await users.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        UserName = user.UserName!;
        Zone = TimeZones.FindOrUtc(user.TimeZoneId);
        Keys = await keys.ListAsync(user.Id, ct);
        Sessions = await keys.ListSessionsAsync(user.Id, ct);
    }

    public async Task<IActionResult> OnPostRevokeAsync(int id, CancellationToken ct)
    {
        await keys.RevokeAsync(users.GetUserId(User)!, id, ct);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeSessionAsync(int id, CancellationToken ct)
    {
        await keys.RevokeSessionAsync(users.GetUserId(User)!, id, ct);
        return RedirectToPage();
    }
}
