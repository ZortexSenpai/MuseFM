using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MuseFM.Data.Entities;

namespace MuseFM.Pages.Account;

public sealed class LoginModel(SignInManager<AppUser> signIn, UserManager<AppUser> users, IOptions<MuseFmOptions> options) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public bool RegistrationOpen { get; private set; }

    public sealed class LoginInput
    {
        [Required]
        public string Username { get; set; } = "";

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = "";

        public bool RememberMe { get; set; } = true;
    }

    public async Task OnGetAsync() => RegistrationOpen = await Registration.IsOpenAsync(users, options.Value);

    public async Task<IActionResult> OnPostAsync()
    {
        RegistrationOpen = await Registration.IsOpenAsync(users, options.Value);
        if (!ModelState.IsValid) return Page();

        var result = await signIn.PasswordSignInAsync(Input.Username.Trim(), Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            if (Url.IsLocalUrl(ReturnUrl)) return LocalRedirect(ReturnUrl);
            var user = await users.FindByNameAsync(Input.Username.Trim());
            return LocalRedirect(Ui.UserUrl(user!.UserName!));
        }

        ModelState.AddModelError(string.Empty, result.IsLockedOut
            ? "Too many failed attempts. Please try again in a few minutes."
            : "Invalid username or password.");
        return Page();
    }
}
