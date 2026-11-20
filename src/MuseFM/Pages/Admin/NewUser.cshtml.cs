using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuseFM.Data.Entities;

namespace MuseFM.Pages.Admin;

/// <summary>/admin/users/new: create an account, also while sign-up is closed.</summary>
public sealed class NewUserModel(UserManager<AppUser> users) : PageModel
{
    [BindProperty]
    public NewUserInput Input { get; set; } = new();

    public List<string> Errors { get; } = [];

    public sealed class NewUserInput
    {
        [Required, StringLength(UserNames.MaxLength, MinimumLength = UserNames.MinLength)]
        [RegularExpression(UserNames.Pattern, ErrorMessage = UserNames.PatternError)]
        public string UserName { get; set; } = "";

        [Required, StringLength(128, MinimumLength = 8, ErrorMessage = "The password must be at least 8 characters long.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";

        [Required, DataType(DataType.Password)]
        [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
        public string ConfirmPassword { get; set; } = "";

        public bool IsPublic { get; set; } = true;
        public string TimeZoneId { get; set; } = "UTC";
        public bool IsAdmin { get; set; }
    }

    public async Task OnGetAsync()
    {
        // New accounts start in the admin's own time zone; most servers are run for people in one place.
        var me = await users.GetUserAsync(User);
        Input.TimeZoneId = me?.TimeZoneId ?? "UTC";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!TimeZones.IsKnown(Input.TimeZoneId)) ModelState.AddModelError("Input.TimeZoneId", "Unknown time zone.");
        if (!ModelState.IsValid) return Page();

        var user = new AppUser { UserName = Input.UserName.Trim(), IsPublic = Input.IsPublic, TimeZoneId = Input.TimeZoneId };
        var result = await users.CreateAsync(user, Input.Password);
        if (!result.Succeeded)
        {
            Errors.AddRange(result.Errors.Select(e => e.Description));
            return Page();
        }

        var message = $"Created {user.UserName}. They can sign in with the password you chose and change it under Settings.";
        var roleError = false;
        if (Input.IsAdmin && !(await users.AddToRoleAsync(user, Roles.Admin)).Succeeded)
        {
            message = $"Created {user.UserName}, but could not make them an admin. Try again below.";
            roleError = true;
        }

        TempData["StatusMessage"] = message;
        TempData["StatusIsError"] = roleError;
        return Redirect($"/admin/users/{user.Id}");
    }
}
