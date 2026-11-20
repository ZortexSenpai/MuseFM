using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages.Settings;

public sealed class IndexModel(UserManager<AppUser> users, SignInManager<AppUser> signIn, ProfileImages images, ApiKeyService keys) : PageModel
{
    public AppUser Me { get; private set; } = null!;
    public ProfileInput Profile { get; set; } = new();
    public PasswordInput Password { get; set; } = new();
    public List<string> PasswordErrors { get; } = [];
    public string? DeleteError { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public bool StatusIsError { get; set; }

    public IEnumerable<SelectListItem> TimeZoneItems => TimeZones.SelectItems;

    public sealed class ProfileInput
    {
        public bool IsPublic { get; set; }

        [Required]
        public string TimeZoneId { get; set; } = "UTC";
    }

    public sealed class PasswordInput
    {
        [Required, DataType(DataType.Password)]
        public string CurrentPassword { get; set; } = "";

        [Required, DataType(DataType.Password)]
        public string NewPassword { get; set; } = "";

        [Required, DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = "";
    }

    private async Task<AppUser> CurrentUserAsync()
    {
        Me = await users.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        return Me;
    }

    public async Task OnGetAsync()
    {
        var user = await CurrentUserAsync();
        Profile = new ProfileInput { IsPublic = user.IsPublic, TimeZoneId = user.TimeZoneId };
    }

    public async Task<IActionResult> OnPostImageAsync(ProfileImageKind kind, IFormFile? file, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind)) return BadRequest();
        var error = await images.SaveAsync(await CurrentUserAsync(), kind, file, ct);
        return Done(error ?? (kind == ProfileImageKind.Avatar ? "Profile picture saved." : "Banner saved."), error is not null);
    }

    public async Task<IActionResult> OnPostRemoveImageAsync(ProfileImageKind kind)
    {
        if (!Enum.IsDefined(kind)) return BadRequest();
        var error = await images.RemoveAsync(await CurrentUserAsync(), kind);
        return Done(error ?? (kind == ProfileImageKind.Avatar ? "Profile picture removed." : "Banner removed."), error is not null);
    }

    private RedirectToPageResult Done(string message, bool error)
    {
        StatusMessage = message;
        StatusIsError = error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostProfileAsync([Bind(Prefix = "Profile")] ProfileInput input)
    {
        var user = await CurrentUserAsync();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(input.TimeZoneId, out _))
        {
            ModelState.AddModelError("Profile.TimeZoneId", "Unknown time zone.");
            Profile = input;
            return Page();
        }

        user.IsPublic = input.IsPublic;
        user.TimeZoneId = input.TimeZoneId;
        await users.UpdateAsync(user);
        return Done("Profile saved.", false);
    }

    public async Task<IActionResult> OnPostPasswordAsync([Bind(Prefix = "Password")] PasswordInput input)
    {
        var user = await CurrentUserAsync();
        Profile = new ProfileInput { IsPublic = user.IsPublic, TimeZoneId = user.TimeZoneId };

        if (string.IsNullOrEmpty(input.CurrentPassword) || string.IsNullOrEmpty(input.NewPassword))
            PasswordErrors.Add("Please fill in all password fields.");
        else if (input.NewPassword != input.ConfirmPassword)
            PasswordErrors.Add("The new passwords do not match.");
        if (PasswordErrors.Count > 0) return Page();

        var result = await users.ChangePasswordAsync(user, input.CurrentPassword, input.NewPassword);
        if (!result.Succeeded)
        {
            PasswordErrors.AddRange(result.Errors.Select(e => e.Description));
            return Page();
        }

        await keys.RevokePasswordSessionsAsync(user.Id);
        await signIn.RefreshSignInAsync(user);
        return Done("Password changed.", false);
    }

    public async Task<IActionResult> OnPostDeleteAsync(string? confirmPassword)
    {
        var user = await CurrentUserAsync();
        Profile = new ProfileInput { IsPublic = user.IsPublic, TimeZoneId = user.TimeZoneId };

        if (string.IsNullOrEmpty(confirmPassword) || !await users.CheckPasswordAsync(user, confirmPassword))
        {
            DeleteError = "Incorrect password.";
            return Page();
        }

        if (await users.IsInRoleAsync(user, Roles.Admin)
            && (await users.GetUsersInRoleAsync(Roles.Admin)).Count == 1
            && users.Users.Count() > 1)
        {
            DeleteError = "You are the only administrator. Make another user an admin first.";
            return Page();
        }

        var result = await users.DeleteAsync(user);
        if (!result.Succeeded)
        {
            DeleteError = string.Join(" ", result.Errors.Select(e => e.Description));
            return Page();
        }

        images.DeleteFiles(user);
        await signIn.SignOutAsync();
        return Redirect("/");
    }
}
