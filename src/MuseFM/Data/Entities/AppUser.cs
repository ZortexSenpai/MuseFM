using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace MuseFM.Data.Entities;

public sealed class AppUser : IdentityUser
{
    /// <summary>Public profiles show up in user search and can be viewed by anyone.</summary>
    public bool IsPublic { get; set; } = true;

    /// <summary>IANA time zone used for charts and timestamps on the profile.</summary>
    [MaxLength(64)]
    public string TimeZoneId { get; set; } = "UTC";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Uploaded profile picture, a file in DataPaths.ProfileImages (see ProfileImages); null shows the initial.</summary>
    [MaxLength(64)]
    public string? AvatarFile { get; set; }

    /// <summary>Uploaded banner shown across the top of the profile, or null for none.</summary>
    [MaxLength(64)]
    public string? BannerFile { get; set; }

    // Display preferences for pages this user views (see ViewerPreferences). Booleans default to false so that
    // new columns need no database default.

    /// <summary>"system", "light" or "dark".</summary>
    [MaxLength(16)]
    public string Theme { get; set; } = "system";

    /// <summary>Accent palette, one of ViewerPreferences.Accents.</summary>
    [MaxLength(16)]
    public string Accent { get; set; } = "violet";

    public bool Clock12 { get; set; }
    public bool WeekStartsSunday { get; set; }
    public bool HideArtwork { get; set; }

    /// <summary>Range slug ("week", "month", "year", "all") opened by default; null for 30 days.</summary>
    [MaxLength(8)]
    public string? DefaultRange { get; set; }
}
