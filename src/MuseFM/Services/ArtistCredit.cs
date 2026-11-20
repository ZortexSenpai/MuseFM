using System.Text.RegularExpressions;

namespace MuseFM.Services;

/// <summary>
/// Who a track or album is credited to, for matching. Two credits match when the main (first) artist of either one
/// appears anywhere in the other, so "Saku" by "Bicep" and "Saku" by "Bicep feat. Clara La San" are one track, while
/// "Song" by "A feat. C" and "Song" by "B feat. C" stay two. Combined names also count by their parts:
/// "A &amp; B" matches "A" (used only for matching; the catalog keeps "A &amp; B" as one artist).
/// </summary>
public sealed partial class ArtistCredit
{
    [GeneratedRegex(@"\s*(?:,|&|\+)\s*|\s+(?:and|x|vs\.?|with|/)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Collaboration();

    private readonly HashSet<string> _main;
    private readonly HashSet<string> _all;

    public ArtistCredit(IEnumerable<string> artistNames)
    {
        var names = artistNames.ToList();
        _main = names.Count > 0 ? Keys(names[0]).ToHashSet() : [];
        _all = names.SelectMany(Keys).ToHashSet();
    }

    public bool Matches(ArtistCredit other) => _main.Overlaps(other._all) || other._main.Overlaps(_all);

    /// <summary>Adds a featured artist learned from a later scrobble.</summary>
    public void Add(string artistName) => _all.UnionWith(Keys(artistName));

    private static IEnumerable<string> Keys(string name)
    {
        var key = TextKey.ArtistKey(name);
        if (key.Length > 0) yield return key;
        var parts = Collaboration().Split(name);
        if (parts.Length < 2) yield break;
        foreach (var part in parts)
        {
            var partKey = TextKey.ArtistKey(part);
            if (partKey.Length > 0) yield return partKey;
        }
    }
}
