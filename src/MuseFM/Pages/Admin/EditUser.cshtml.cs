using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages.Admin;

/// <summary>
/// /admin/users/{id}: change someone's username, visibility, time zone and role, set a new password, remove their picture or
/// banner, or delete the account.
/// Admins can't demote or delete themselves, so the server always keeps an admin.
/// </summary>
public sealed class EditUserModel(
    UserManager<AppUser> users, SignInManager<AppUser> signIn, AppDbContext db, ProfileImages images, ApiKeyService keys) : PageModel
{
    public AppUser Target { get; private set; } = null!;

    /// <summary>The username as stored, even while the form shows a rename that failed.</summary>
    public string Name { get; private set; } = "";
    public bool IsSelf { get; private set; }
    public bool TargetIsAdmin { get; private set; }
    public int Scrobbles { get; private set; }
    public long? LastScrobble { get; private set; }
    public int ApiKeys { get; private set; }

    public AccountInput Account { get; set; } = new();
    public List<string> AccountErrors { get; } = [];
    public List<string> PasswordErrors { get; } = [];
    public string? DeleteError { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public bool StatusIsError { get; set; }

    public sealed class AccountInput
    {
        [Required, StringLength(UserNames.MaxLength, MinimumLength = UserNames.MinLength)]
        [RegularExpression(UserNames.Pattern, ErrorMessage = UserNames.PatternError)]
        public string UserName { get; set; } = "";

        public bool IsPublic { get; set; }
        public string TimeZoneId { get; set; } = "UTC";
        public bool IsAdmin { get; set; }
    }

    public sealed class PasswordInput
    {
        public string NewPassword { get; set; } = "";
        public string ConfirmPassword { get; set; } = "";
    }

    public async Task<IActionResult> OnGetAsync(string id)
    {
        if (!await LoadAsync(id)) return NotFound();
        Account = new AccountInput { UserName = Target.UserName!, IsPublic = Target.IsPublic, TimeZoneId = Target.TimeZoneId, IsAdmin = TargetIsAdmin };
        return Page();
    }

    public async Task<IActionResult> OnPostAccountAsync(string id, [Bind(Prefix = "Account")] AccountInput input)
    {
        if (!await LoadAsync(id)) return NotFound();
        Account = input;
        if (!TimeZones.IsKnown(input.TimeZoneId)) ModelState.AddModelError("Account.TimeZoneId", "Unknown time zone.");
        if (!ModelState.IsValid) return Page();

        Target.IsPublic = input.IsPublic;
        Target.TimeZoneId = input.TimeZoneId;
        var name = input.UserName.Trim();
        var renamed = name != Target.UserName;
        // Renaming also saves the other fields; it changes the security stamp, which signs the user out elsewhere.
        var result = renamed ? await users.SetUserNameAsync(Target, name) : await users.UpdateAsync(Target);
        if (!result.Succeeded)
        {
            AccountErrors.AddRange(result.Errors.Select(e => e.Description));
            return Page();
        }

        var roleChanged = !IsSelf && input.IsAdmin != TargetIsAdmin;
        if (roleChanged)
        {
            result = input.IsAdmin ? await users.AddToRoleAsync(Target, Roles.Admin) : await users.RemoveFromRoleAsync(Target, Roles.Admin);
            // The role is part of the sign-in cookie: a new security stamp ends the sign-ins that still carry the old one.
            if (result.Succeeded) result = await users.UpdateSecurityStampAsync(Target);
            if (!result.Succeeded)
            {
                AccountErrors.AddRange(result.Errors.Select(e => e.Description));
                return Page();
            }
        }

        // Keep the admin signed in under their new name.
        if (IsSelf) await signIn.RefreshSignInAsync(Target);
        StatusMessage = renamed
            ? $"Saved. The profile is now at {Ui.UserUrl(Target.UserName!)}{(IsSelf ? "" : $"; {Target.UserName} needs to sign in again with the new name")}."
            : roleChanged ? $"Saved. {Target.UserName} needs to sign in again."
            : "Saved.";
        return RedirectToPage(new { id });
    }

    /// <summary>Sets a new password without the old one, for users who forgot theirs. Admins change their own in Settings.</summary>
    public async Task<IActionResult> OnPostPasswordAsync(string id, [Bind(Prefix = "Password")] PasswordInput input)
    {
        if (!await LoadAsync(id)) return NotFound();
        ResetAccountInput();
        if (IsSelf) return Message("Change your own password under Settings, with your current one.", error: true);

        if (string.IsNullOrEmpty(input.NewPassword)) PasswordErrors.Add("Enter the new password.");
        else if (input.NewPassword != input.ConfirmPassword) PasswordErrors.Add("The passwords do not match.");
        if (PasswordErrors.Count > 0) return Page();

        var token = await users.GeneratePasswordResetTokenAsync(Target);
        var result = await users.ResetPasswordAsync(Target, token, input.NewPassword);
        if (!result.Succeeded)
        {
            PasswordErrors.AddRange(result.Errors.Select(e => e.Description));
            return Page();
        }

        await keys.RevokePasswordSessionsAsync(Target.Id);
        return Message($"Password changed. {Name} is signed out everywhere, also in apps that logged in with the old password; scrobblers keep working with their API keys.");
    }

    /// <summary>Removes a picture or banner that doesn't belong on the server; the user can upload another one.</summary>
    public async Task<IActionResult> OnPostRemoveImageAsync(string id, ProfileImageKind kind)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!Enum.IsDefined(kind)) return BadRequest();
        var error = await images.RemoveAsync(Target, kind);
        return error is not null
            ? Message(error, error: true)
            : Message(kind == ProfileImageKind.Avatar ? "Profile picture removed." : "Banner removed.");
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id, string? confirmName)
    {
        if (!await LoadAsync(id)) return NotFound();
        ResetAccountInput();
        if (IsSelf) return Message("You can't delete your own account here. Use Settings, after making someone else an admin.", error: true);
        if (!string.Equals(confirmName?.Trim(), Target.UserName, StringComparison.Ordinal))
        {
            DeleteError = $"Type {Target.UserName} to confirm.";
            return Page();
        }

        var result = await users.DeleteAsync(Target);
        if (!result.Succeeded)
        {
            DeleteError = string.Join(" ", result.Errors.Select(e => e.Description));
            return Page();
        }

        images.DeleteFiles(Target);
        TempData["StatusMessage"] = $"Deleted {Target.UserName} and all of their data.";
        TempData["StatusIsError"] = false;
        return Redirect("/admin");
    }

    private async Task<bool> LoadAsync(string id)
    {
        if (await users.FindByIdAsync(id) is not { } user) return false;
        Target = user;
        Name = user.UserName!;
        IsSelf = users.GetUserId(User) == user.Id;
        TargetIsAdmin = await users.IsInRoleAsync(user, Roles.Admin);
        Scrobbles = await db.Scrobbles.CountAsync(s => s.UserId == user.Id);
        LastScrobble = await db.Scrobbles.Where(s => s.UserId == user.Id).MaxAsync(s => (long?)s.ListenedAt);
        ApiKeys = await db.ApiKeys.CountAsync(k => k.UserId == user.Id);
        return true;
    }

    private void ResetAccountInput() =>
        Account = new AccountInput { UserName = Target.UserName!, IsPublic = Target.IsPublic, TimeZoneId = Target.TimeZoneId, IsAdmin = TargetIsAdmin };

    private RedirectToPageResult Message(string text, bool error = false)
    {
        StatusMessage = text;
        StatusIsError = error;
        return RedirectToPage(new { id = Target.Id });
    }
}
