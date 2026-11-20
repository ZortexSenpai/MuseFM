namespace MuseFM;

/// <summary>Application settings, bound from the "MuseFM" configuration section (env: MuseFM__Key).</summary>
public sealed class MuseFmOptions
{
    public const string Section = "MuseFM";

    /// <summary>Directory holding the SQLite database, data-protection keys and pending import uploads.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>When false, only the very first account can be created (it becomes the admin).</summary>
    public bool AllowRegistration { get; set; } = true;

    /// <summary>
    /// Last.fm API key used to import a user's history directly from Last.fm and by the Last.fm metadata connector.
    /// A key saved in the admin dashboard takes precedence. The import option is hidden when neither is set.
    /// </summary>
    public string? LastFmApiKey { get; set; }

    /// <summary>
    /// When false, only administrators can import from a Maloja server on a private or local address (which is where a
    /// Maloja usually runs); for everyone else MuseFM only connects to the public internet, so that an account is not a
    /// way into the server's network. Set to true when everyone with an account is trusted.
    /// </summary>
    public bool ImportFromPrivateAddresses { get; set; }

    /// <summary>
    /// The reverse proxies whose X-Forwarded-* headers are believed: IP addresses or networks ("172.18.0.0/16"),
    /// separated by commas. Empty believes these headers from anyone, which is only right when nothing but the proxy
    /// can reach MuseFM.
    /// </summary>
    public string? TrustedProxies { get; set; }

    /// <summary>
    /// How long a "now playing" update that carries no track length is shown. Clients cannot report that playback
    /// stopped, so this is how soon a stopped track disappears; clients that repeat the update while playing
    /// (multi-scrobbler: every 30 seconds) keep it alive. Updates with a track length are shown for that length.
    /// </summary>
    public int NowPlayingTimeoutSeconds { get; set; } = 90;
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string AdminPolicy = "AdminOnly";
}

/// <summary>Rules for usernames, shared by sign-up and the admin's user management. They appear in profile URLs.</summary>
public static class UserNames
{
    public const int MinLength = 2;
    public const int MaxLength = 32;
    public const string Pattern = "^[A-Za-z0-9._-]+$";
    public const string PatternError = "Use only letters, digits, dots, dashes and underscores.";
}

/// <summary>Resolved locations inside the data directory.</summary>
public sealed class DataPaths
{
    public DataPaths(string root)
    {
        Root = Path.GetFullPath(root);
        Database = Path.Combine(Root, "musefm.db");
        Keys = Path.Combine(Root, "keys");
        Imports = Path.Combine(Root, "imports");
        Images = Path.Combine(Root, "images");
        ProfileImages = Path.Combine(Root, "profiles");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Keys);
        Directory.CreateDirectory(Imports);
        Directory.CreateDirectory(Images);
        Directory.CreateDirectory(ProfileImages);
    }

    public string Root { get; }
    public string Database { get; }
    public string Keys { get; }
    public string Imports { get; }

    /// <summary>Cached covers and artist photos from the metadata connectors.</summary>
    public string Images { get; }

    /// <summary>Profile pictures and banners that users uploaded. Kept apart from <see cref="Images"/>, which is a cache.</summary>
    public string ProfileImages { get; }
}
