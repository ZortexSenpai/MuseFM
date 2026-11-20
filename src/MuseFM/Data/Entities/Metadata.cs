using System.ComponentModel.DataAnnotations;

namespace MuseFM.Data.Entities;

/// <summary>External services that can supply covers, artist photos and biographies.</summary>
public enum ConnectorKind
{
    Navidrome,
    LastFm,
    ListenBrainz,
    Maloja,
}

/// <summary>Server-wide settings of one metadata connector, edited in the admin dashboard.</summary>
public sealed class MetadataConnector
{
    public ConnectorKind Kind { get; set; }
    public bool Enabled { get; set; }

    /// <summary>Connectors are asked in ascending order; for each field the first answer wins.</summary>
    public int Position { get; set; }

    [MaxLength(512)]
    public string? BaseUrl { get; set; }

    [MaxLength(256)]
    public string? Username { get; set; }

    /// <summary>Password, API key or token, encrypted with ASP.NET Core data protection.</summary>
    public string? ProtectedSecret { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Cached metadata for an artist. A row is also written when nothing was found,
/// so the connectors are not asked again before <see cref="RefreshAfter"/>.
/// </summary>
public sealed class ArtistMetadata
{
    public int ArtistId { get; set; }
    public Artist Artist { get; set; } = null!;

    /// <summary>File name inside the images directory.</summary>
    [MaxLength(128)]
    public string? ImageFile { get; set; }

    [MaxLength(64)]
    public string? ImageSource { get; set; }

    /// <summary>Plain text; paragraphs are separated by blank lines.</summary>
    public string? Bio { get; set; }

    [MaxLength(64)]
    public string? BioSource { get; set; }

    [MaxLength(512)]
    public string? BioUrl { get; set; }

    [MaxLength(36)]
    public string? MusicBrainzId { get; set; }

    /// <summary>Genres, one per line, the most fitting first (see GenreNames); null when no connector knew any.</summary>
    public string? Genres { get; set; }

    /// <summary>
    /// Which generation of lookups filled this row (MetadataService.Revision). Rows of an older one lack what later
    /// versions look up, and count as missing until they are looked up again.
    /// </summary>
    public int Revision { get; set; }

    public DateTime UpdatedAt { get; set; }
    public DateTime RefreshAfter { get; set; }
}

/// <summary>Cached metadata for an album; see <see cref="ArtistMetadata"/>.</summary>
public sealed class AlbumMetadata
{
    public int AlbumId { get; set; }
    public Album Album { get; set; } = null!;

    [MaxLength(128)]
    public string? ImageFile { get; set; }

    [MaxLength(64)]
    public string? ImageSource { get; set; }

    /// <summary>MusicBrainz release id.</summary>
    [MaxLength(36)]
    public string? MusicBrainzId { get; set; }

    /// <summary>The year the album first came out, where a connector knew it.</summary>
    public int? ReleaseYear { get; set; }

    /// <summary>See <see cref="ArtistMetadata.Genres"/>.</summary>
    public string? Genres { get; set; }

    /// <summary>See <see cref="ArtistMetadata.Revision"/>.</summary>
    public int Revision { get; set; }

    public DateTime UpdatedAt { get; set; }
    public DateTime RefreshAfter { get; set; }
}
