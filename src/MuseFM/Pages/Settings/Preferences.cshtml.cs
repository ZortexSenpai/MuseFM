using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages.Settings;

/// <summary>/settings/preferences: how pages look for this user (theme, accent, clock, week, artwork, default period).</summary>
public sealed class PreferencesModel(UserManager<AppUser> users, SignInManager<AppUser> signIn) : PageModel
{
    public sealed class PreferencesInput
    {
        public string Theme { get; set; } = "system";
        public string Accent { get; set; } = "violet";
        public bool Clock12 { get; set; }
        public bool WeekStartsSunday { get; set; }
        public bool ShowArtwork { get; set; } = true;

        /// <summary>Empty for the built-in default (30 days).</summary>
        public string DefaultRange { get; set; } = "";
    }

    public static readonly IReadOnlyList<(string Value, string Label)> Ranges =
        [("", "30 days"), ("week", "7 days"), ("year", "12 months"), ("all", "All time")];

    [BindProperty]
    public PreferencesInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();

        var prefs = ViewerPreferences.From(user);
        Input = new PreferencesInput
        {
            Theme = ViewerPreferences.ThemeId(prefs.Theme),
            Accent = prefs.Accent,
            Clock12 = prefs.Clock12,
            WeekStartsSunday = prefs.FirstDayOfWeek == DayOfWeek.Sunday,
            ShowArtwork = prefs.ShowArtwork,
            DefaultRange = prefs.DefaultRange is { } range ? StatPeriod.Slug(range) : "",
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();

        user.Theme = ViewerPreferences.ThemeId(ViewerPreferences.ParseTheme(Input.Theme));
        user.Accent = ViewerPreferences.IsAccent(Input.Accent) ? Input.Accent : ViewerPreferences.Default.Accent;
        user.Clock12 = Input.Clock12;
        user.WeekStartsSunday = Input.WeekStartsSunday;
        user.HideArtwork = !Input.ShowArtwork;
        user.DefaultRange = Input.DefaultRange is "week" or "year" or "all" ? Input.DefaultRange : null;
        await users.UpdateAsync(user);

        // The preferences travel in the sign-in cookie: issue a new one so they apply right away.
        await signIn.RefreshSignInAsync(user);
        StatusMessage = "Preferences saved.";
        return RedirectToPage();
    }
}
