using System.Text.RegularExpressions;

namespace MuseFM.Services;

/// <summary>
/// Splits featured artists out of artist and title fields, e.g. artist "A feat. B &amp; C", title "Song (feat. B)",
/// "Song (with B)" or "Song feat. B" all end up as title "Song" with A and B (and C) as artists.
/// Deliberately conservative: "Simon &amp; Garfunkel" on its own is left alone, and a bare "ft" without a dot only
/// counts inside brackets, so a title like "Six Ft Deep" survives.
/// </summary>
public static partial class MetadataParser
{
    [GeneratedRegex(@"\s+(?:feat\.?|ft\.|featuring)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex FeatSeparator();

    /// <summary>"(feat. B)", "[ft B]", "(featuring B)" and Spotify's "(with B)".</summary>
    [GeneratedRegex(@"\s*[\(\[]\s*(?:feat\.?|ft\.?|featuring|with)\s+(?<artists>[^\)\]]+?)\s*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketFeat();

    /// <summary>"Song feat. B", up to a following " - ...", "(" or "[" part.</summary>
    [GeneratedRegex(@"\s+(?:feat\.?|ft\.|featuring)\s+(?<artists>.+?)(?=\s+[-–—]\s|\s*[\(\[]|\s*$)", RegexOptions.IgnoreCase)]
    private static partial Regex InlineFeat();

    [GeneratedRegex(@"\s*,\s*|\s+&\s+")]
    private static partial Regex ListSeparator();

    public static (string Title, List<string> Artists) Parse(string? title, IEnumerable<string?> artistFields)
    {
        var artists = new List<string>();
        foreach (var field in artistFields)
        {
            var cleaned = TextKey.Clean(field);
            var bracket = BracketFeat().Match(cleaned);
            if (bracket.Success && bracket.Index > 0)
            {
                cleaned = TextKey.Clean(cleaned.Remove(bracket.Index, bracket.Length));
                artists.Add(cleaned);
                artists.AddRange(ListSeparator().Split(bracket.Groups["artists"].Value));
                continue;
            }

            if (cleaned.Length == 0) continue;
            var parts = FeatSeparator().Split(cleaned);
            artists.Add(parts[0]);
            foreach (var featured in parts.Skip(1))
                artists.AddRange(ListSeparator().Split(featured));
        }

        var cleanTitle = TextKey.Clean(title);
        foreach (var pattern in new[] { BracketFeat(), InlineFeat() })
        {
            var match = pattern.Match(cleanTitle);
            if (!match.Success) continue;
            var stripped = TextKey.Clean(cleanTitle.Remove(match.Index, match.Length)).TrimEnd(' ', '-', '–', '—', ':', ',');
            if (stripped.Length == 0) continue;
            cleanTitle = stripped;
            artists.AddRange(ListSeparator().Split(match.Groups["artists"].Value));
        }

        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var artist in artists)
        {
            var cleaned = TextKey.Clean(artist);
            if (cleaned.Length > 0 && seen.Add(TextKey.ArtistKey(cleaned)))
                result.Add(cleaned);
        }

        return (cleanTitle, result);
    }

    /// <summary>Single-string artist credit for formats that only have one artist field.</summary>
    public static string Credit(IReadOnlyList<string> artists) => artists.Count switch
    {
        0 => "",
        1 => artists[0],
        _ => $"{artists[0]} feat. {string.Join(", ", artists.Skip(1))}",
    };
}
