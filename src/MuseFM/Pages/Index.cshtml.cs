using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MuseFM.Data.Entities;

namespace MuseFM.Pages;

public sealed class IndexModel(UserManager<AppUser> users, IOptions<MuseFmOptions> options) : PageModel
{
    public bool RegistrationOpen { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true && users.GetUserName(User) is { } name)
            return Redirect(Ui.UserUrl(name));

        RegistrationOpen = await Registration.IsOpenAsync(users, options.Value);
        return Page();
    }
}

public static class Registration
{
    /// <summary>Registration is always open while there are no users, so the first admin can sign up.</summary>
    public static async Task<bool> IsOpenAsync(UserManager<AppUser> users, MuseFmOptions options) =>
        options.AllowRegistration || !await users.Users.AnyAsync();
}
