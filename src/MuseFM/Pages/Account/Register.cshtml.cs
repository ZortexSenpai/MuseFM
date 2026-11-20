using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MuseFM.Data.Entities;

namespace MuseFM.Pages.Account;

public sealed class RegisterModel(SignInManager<AppUser> signIn, UserManager<AppUser> users, IOptions<MuseFmOptions> options) : PageModel
{
    [BindProperty]
    public RegisterInput Input { get; set; } = new();

    public bool RegistrationOpen { get; private set; }
    public bool IsFirstUser { get; private set; }

    public sealed class RegisterInput
    {
        [Required, StringLength(UserNames.MaxLength, MinimumLength = UserNames.MinLength)]
        [RegularExpression(UserNames.Pattern, ErrorMessage = UserNames.PatternError)]
        public string Username { get; set; } = "";

        [Required, StringLength(128, MinimumLength = 8, ErrorMessage = "The password must be at least 8 characters long.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";

        [Required, DataType(DataType.Password)]
        [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
        public string ConfirmPassword { get; set; } = "";
    }

    private async Task LoadStateAsync()
    {
        IsFirstUser = !await users.Users.AnyAsync();
        RegistrationOpen = IsFirstUser || options.Value.AllowRegistration;
    }

    public async Task OnGetAsync() => await LoadStateAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadStateAsync();
        if (!RegistrationOpen) return Page();
        if (!ModelState.IsValid) return Page();

        var user = new AppUser { UserName = Input.Username.Trim() };
        var result = await users.CreateAsync(user, Input.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }

        if (IsFirstUser) await users.AddToRoleAsync(user, Roles.Admin);

        await signIn.SignInAsync(user, isPersistent: true);
        return LocalRedirect(Ui.UserUrl(user.UserName));
    }
}
