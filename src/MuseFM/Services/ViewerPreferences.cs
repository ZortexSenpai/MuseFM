using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MuseFM.Data.Entities;

namespace MuseFM.Services;

public enum ThemeMode
{
    System,
    Light,
    Dark,
    Newsprint,
    Kraft,
    Midnight,
    Forest,
    Black,
}

/// <summary>
/// How the signed-in viewer wants pages to look: theme, accent, clock, first day of the week, artwork and the
/// default period. They are the viewer's, not the profile owner's: everyone sees any profile their own way.
/// They travel in the auth cookie as one claim, so applying them costs no database lookup.
/// </summary>
public sealed record ViewerPreferences(ThemeMode Theme, string Accent, bool Clock12, DayOfWeek FirstDayOfWeek, bool ShowArtwork, StatRange? DefaultRange)
{
    public const string ClaimType = "musefm:prefs";

    /// <summary>Themes defined in site.css, in display order. System follows the device between Light and Dark.</summary>
    public static readonly IReadOnlyList<(ThemeMode Mode, string Name, string Hint)> Themes =
    [
        (ThemeMode.System, "System", "Follows your device"),
        (ThemeMode.Light, "Light", "Warm paper"),
        (ThemeMode.Dark, "Dark", "Warm charcoal"),
        (ThemeMode.Newsprint, "Newsprint", "Cool gray paper"),
        (ThemeMode.Kraft, "Kraft", "Brown sleeve paper"),
        (ThemeMode.Midnight, "Midnight", "Deep blue"),
        (ThemeMode.Forest, "Forest", "Deep green"),
        (ThemeMode.Black, "Black", "True black"),
    ];

    /// <summary>Accent palettes defined in site.css, in display order.</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> Accents =
        [("violet", "Violet"), ("blue", "Blue"), ("teal", "Teal"), ("green", "Green"), ("rose", "Rose"), ("amber", "Amber")];

    public static readonly ViewerPreferences Default = new(ThemeMode.System, "violet", false, DayOfWeek.Monday, true, null);

    private static readonly AsyncLocal<ViewerPreferences?> CurrentValue = new();

    /// <summary>The current request's viewer preferences (set by middleware); defaults for visitors and outside requests.</summary>
    public static ViewerPreferences Current
    {
        get => CurrentValue.Value ?? Default;
        set => CurrentValue.Value = value;
    }

    /// <summary>Value for &lt;html data-theme&gt;; null follows the operating system.</summary>
    public string? ThemeAttribute => Theme == ThemeMode.System ? null : ThemeId(Theme);

    /// <summary>Value for &lt;meta name="color-scheme"&gt;: which browser defaults (form controls, scrollbars) fit the theme.</summary>
    public string ColorScheme => ColorSchemeOf(Theme);

    /// <summary>The theme's name as stored, sent in the claim and used in site.css.</summary>
    public static string ThemeId(ThemeMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>Themes built on the dark mode in site.css.</summary>
    public static bool IsDark(ThemeMode mode) => mode is ThemeMode.Dark or ThemeMode.Midnight or ThemeMode.Forest or ThemeMode.Black;

    public static string ColorSchemeOf(ThemeMode mode) => mode == ThemeMode.System ? "light dark" : IsDark(mode) ? "dark" : "light";

    public static bool IsAccent(string? id) => Accents.Any(a => a.Id == id);

    /// <summary>Unknown names follow the operating system.</summary>
    public static ThemeMode ParseTheme(string? value)
    {
        var id = value?.ToLowerInvariant();
        return Themes.FirstOrDefault(t => ThemeId(t.Mode) == id).Mode;
    }

    public static ViewerPreferences From(AppUser user) => new(
        ParseTheme(user.Theme),
        IsAccent(user.Accent) ? user.Accent : Default.Accent,
        user.Clock12,
        user.WeekStartsSunday ? DayOfWeek.Sunday : DayOfWeek.Monday,
        !user.HideArtwork,
        string.IsNullOrEmpty(user.DefaultRange) ? null : StatPeriod.Parse(user.DefaultRange));

    public string ToClaim() => string.Join('|',
        ThemeId(Theme), Accent, Clock12 ? "1" : "0", FirstDayOfWeek == DayOfWeek.Sunday ? "sun" : "mon",
        ShowArtwork ? "1" : "0", DefaultRange is { } range ? StatPeriod.Slug(range) : "");

    /// <summary>Reads the preferences claim; anything missing or unknown falls back to the default.</summary>
    public static ViewerPreferences FromClaims(ClaimsPrincipal? user)
    {
        var value = user?.FindFirst(ClaimType)?.Value;
        if (string.IsNullOrEmpty(value)) return Default;
        var parts = value.Split('|');
        string Part(int i) => i < parts.Length ? parts[i] : "";
        return new ViewerPreferences(
            ParseTheme(Part(0)),
            IsAccent(Part(1)) ? Part(1) : Default.Accent,
            Part(2) == "1",
            Part(3) == "sun" ? DayOfWeek.Sunday : DayOfWeek.Monday,
            Part(4) != "0",
            Part(5).Length > 0 ? StatPeriod.Parse(Part(5)) : null);
    }
}

/// <summary>Adds the viewer preferences claim when Identity builds the sign-in cookie.</summary>
public sealed class AppClaimsPrincipalFactory(UserManager<AppUser> users, RoleManager<IdentityRole> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(ViewerPreferences.ClaimType, ViewerPreferences.From(user).ToClaim()));
        return identity;
    }
}
