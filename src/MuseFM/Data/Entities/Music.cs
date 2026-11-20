using System.ComponentModel.DataAnnotations;

namespace MuseFM.Data.Entities;

// The music catalog is shared between users; statistics are always computed per user from scrobbles.
// Matching (see TextKey, ArtistCredit and ScrobbleService): artists by a unique key, tracks and albums by a title key
// plus an artist credit that matches on the main artist, so featured artists don't split a song.

public sealed class Artist
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Unique matching key (TextKey.ArtistKey): "Björk" and "Bjork" are one artist.</summary>
    public required string Key { get; set; }

    /// <summary>MusicBrainz artist id, as first sent by a client or found in an import.</summary>
    [MaxLength(36)]
    public string? MusicBrainzId { get; set; }

    public List<TrackArtist> Tracks { get; set; } = [];
    public List<AlbumArtist> Albums { get; set; } = [];
}

/// <summary>
/// A recording, album-independent. Identified by its title key and its artists: a scrobble belongs to an existing track
/// with the same key when the main artist of either side is credited on the other.
/// </summary>
public sealed class Track
{
    public int Id { get; set; }
    public required string Title { get; set; }

    /// <summary>Matching key of the title (TextKey.TitleKey); several tracks can share it.</summary>
    public required string Key { get; set; }

    public int? LengthSeconds { get; set; }

    /// <summary>MusicBrainz recording id, as first sent by a client or found in an import. Not used for matching.</summary>
    [MaxLength(36)]
    public string? MusicBrainzId { get; set; }

    public List<TrackArtist> Artists { get; set; } = [];
}

public sealed class TrackArtist
{
    public int TrackId { get; set; }
    public Track Track { get; set; } = null!;
    public int ArtistId { get; set; }
    public Artist Artist { get; set; } = null!;
    public int Position { get; set; }
}

/// <summary>An album, identified like a track: its title key (editions folded) and its album artists.</summary>
public sealed class Album
{
    public int Id { get; set; }
    public required string Title { get; set; }

    /// <summary>Matching key of the title (TextKey.AlbumKey); several albums can share it.</summary>
    public required string Key { get; set; }

    /// <summary>MusicBrainz release id of the first edition seen (editions are folded into one album).</summary>
    [MaxLength(36)]
    public string? MusicBrainzId { get; set; }

    public List<AlbumArtist> Artists { get; set; } = [];
}

public sealed class AlbumArtist
{
    public int AlbumId { get; set; }
    public Album Album { get; set; } = null!;
    public int ArtistId { get; set; }
    public Artist Artist { get; set; } = null!;
    public int Position { get; set; }
}

public sealed class Scrobble
{
    public long Id { get; set; }
    public required string UserId { get; set; }
    public AppUser? User { get; set; }
    public int TrackId { get; set; }
    public Track Track { get; set; } = null!;
    public int? AlbumId { get; set; }
    public Album? Album { get; set; }

    /// <summary>Unix timestamp (seconds). Unique per user, which is also how duplicates are detected.</summary>
    public long ListenedAt { get; set; }

    /// <summary>How long the user actually listened, if the client reported it.</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>Which client or import produced this scrobble.</summary>
    public string? Origin { get; set; }
}

/// <summary>
/// A track a user is obsessed with, shown on their profile from <see cref="StartedAt"/> until <see cref="EndsAt"/>.
/// Picking another or removing it ends it; past obsessions are kept.
/// </summary>
public sealed class Obsession
{
    public int Id { get; set; }
    public required string UserId { get; set; }
    public AppUser? User { get; set; }
    public int TrackId { get; set; }
    public Track Track { get; set; } = null!;
    public DateTime StartedAt { get; set; }

    /// <summary>When it stops being shown; null to show it until the user picks another or removes it.</summary>
    public DateTime? EndsAt { get; set; }
}

/// <summary>A user's love (or, from ListenBrainz, hate) of a track. One per user and track.</summary>
public sealed class TrackFeedback
{
    public const int Love = 1;
    public const int Hate = -1;

    public required string UserId { get; set; }
    public AppUser? User { get; set; }
    public int TrackId { get; set; }
    public Track Track { get; set; } = null!;

    /// <summary><see cref="Love"/> or <see cref="Hate"/>.</summary>
    public int Score { get; set; }

    /// <summary>Unix timestamp (seconds).</summary>
    public long CreatedAt { get; set; }
}

/// <summary>
/// An old name of an artist, album or track, kept when an admin renames or merges it: later scrobbles that still use
/// the old name land on the entry it became. Exactly one of <see cref="ArtistId"/>, <see cref="AlbumId"/> and
/// <see cref="TrackId"/> is set.
/// </summary>
public sealed class CatalogAlias
{
    public int Id { get; set; }

    /// <summary>The old name or title, as it was spelled.</summary>
    [MaxLength(512)]
    public required string Name { get; set; }

    /// <summary>Matching key of <see cref="Name"/> (artist, title or album key); recomputed when the matching rules change.</summary>
    [MaxLength(512)]
    public required string Key { get; set; }

    /// <summary>Albums and tracks: the old credited artists, one per line, so only that artist's song or album is redirected.</summary>
    public string? Credit { get; set; }

    public int? ArtistId { get; set; }
    public Artist? Artist { get; set; }
    public int? AlbumId { get; set; }
    public Album? Album { get; set; }
    public int? TrackId { get; set; }
    public Track? Track { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public IReadOnlyList<string> CreditNames => (Credit ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
