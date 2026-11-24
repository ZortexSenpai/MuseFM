namespace MuseFM.Data.Entities;

/// <summary>A user-generated key for scrobbling clients. Only the SHA-256 hash is stored.</summary>
public sealed class ApiKey
{
    public int Id { get; set; }
    public required string UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public required string Name { get; set; }
    public required string KeyHash { get; set; }

    /// <summary>First characters of the key, so users can tell keys apart.</summary>
    public required string Prefix { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>A session key handed out by the Last.fm-compatible auth.getMobileSession call.</summary>
public sealed class ScrobblerSession
{
    public int Id { get; set; }
    public required string UserId { get; set; }
    public AppUser User { get; set; } = null!;

    /// <summary>
    /// The API key used to log in; null when the account password was used. Revoking the key ends the session, and so
    /// does changing the password for sessions started with it.
    /// </summary>
    public int? ApiKeyId { get; set; }
    public ApiKey? ApiKey { get; set; }

    public required string KeyHash { get; set; }
    public string? Client { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>Small named values the application keeps about itself, such as the catalog version.</summary>
public sealed class AppState
{
    [System.ComponentModel.DataAnnotations.MaxLength(64)]
    public required string Name { get; set; }

    [System.ComponentModel.DataAnnotations.MaxLength(256)]
    public required string Value { get; set; }
}

public enum ImportKind
{
    MalojaJson,
    LastFmCsv,
    LastFmStatsJson,
    ListenBrainzJson,
    LastFmApi,
    MalojaApi,
    SpotifyJson,
}

public enum ImportStatus
{
    Queued,
    Running,
    Completed,
    Failed,
}

public sealed class ImportJob
{
    public int Id { get; set; }
    public required string UserId { get; set; }
    public AppUser? User { get; set; }
    public ImportKind Kind { get; set; }
    public ImportStatus Status { get; set; } = ImportStatus.Queued;

    /// <summary>Original file name, the Last.fm user name, or the Maloja server address.</summary>
    public string Source { get; set; } = "";

    /// <summary>Uploaded file waiting to be processed; deleted when the job finishes.</summary>
    public string? StoredFile { get; set; }

    /// <summary>Only scrobbles listened to at or after this time (Unix seconds) are imported; everything from the start when null.</summary>
    public long? ListenedFrom { get; set; }

    /// <summary>Only scrobbles listened to before this time (Unix seconds) are imported; everything up to now when null.</summary>
    public long? ListenedBefore { get; set; }

    public int? Total { get; set; }
    public int Processed { get; set; }
    public int Imported { get; set; }
    public int Duplicates { get; set; }
    public int Failed { get; set; }
    public string? Message { get; set; }
    public string? ErrorSample { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>One user following another, to see their listening in the feed and on the friends page.</summary>
public sealed class Follow
{
    public required string FollowerId { get; set; }
    public AppUser? Follower { get; set; }
    public required string FolloweeId { get; set; }
    public AppUser? Followee { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
