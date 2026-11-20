using System.Globalization;
using System.Text;

namespace MuseFM.Services.Metadata;

/// <summary>
/// Genres as the metadata sources name them: file tags from a music library ("Trip-Hop"), MusicBrainz genres ("trip hop")
/// and Last.fm's free-form tags ("trip-hop", but also "seen live"). Stored one per line; spellings are only merged, by
/// <see cref="Key"/>, when statistics are computed.
/// </summary>
public static class GenreNames
{
    public const int MaxPerItem = 5;
    private const int MaxLength = 48;

    // Common tags that say nothing about the music.
    private static readonly HashSet<string> NotGenres = new(StringComparer.Ordinal)
    {
        "seen live", "favorites", "favourites", "favorite", "favourite", "favorite artists", "favourite artists", "my favorites",
        "awesome", "beautiful", "love", "loved", "good", "cool", "amazing", "best", "albums i own", "under 2000 listeners",
        "all", "other", "others", "misc", "unknown", "music", "spotify", "check out", "to listen",
    };

    /// <summary>
    /// Usable genre names out of what a source returned, in its order and without repeats: at most
    /// <see cref="MaxPerItem"/>, leaving out years, decades and tags that are not genres.
    /// </summary>
    /// <param name="titleCase">For sources that write everything in lower case: "trip hop" becomes "Trip Hop".</param>
    public static IReadOnlyList<string> Clean(IEnumerable<string?> names, bool titleCase = false)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in names)
        {
            var name = string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var key = Key(name);
            if (key.Length == 0 || name.Length > MaxLength || NotGenres.Contains(key) || IsYearOrDecade(key) || !seen.Add(key)) continue;
            result.Add(titleCase && name == name.ToLowerInvariant() ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name) : name);
            if (result.Count == MaxPerItem) break;
        }

        return result;
    }

    /// <summary>What makes two spellings the same genre: case, and "-", "_" or "/" versus a space ("Hip-Hop", "hip hop").</summary>
    public static string Key(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (c is '-' or '_' or '/' || char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string? Join(IReadOnlyList<string>? genres) => genres is { Count: > 0 } ? string.Join('\n', genres) : null;

    public static IReadOnlyList<string> Split(string? stored) =>
        string.IsNullOrEmpty(stored) ? [] : stored.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // "1998", "80s", "1980s", "00s".
    private static bool IsYearOrDecade(string key)
    {
        var digits = key.EndsWith('s') ? key[..^1] : key;
        return digits.Length is 2 or 4 && digits.All(char.IsAsciiDigit);
    }
}
