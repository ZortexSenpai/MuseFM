using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MuseFM.Services;

/// <summary>Cleaning of user-supplied names and the matching keys that deduplicate the catalog.</summary>
public static partial class TextKey
{
    public const int MaxLength = 512;

    /// <summary>Latin letters that don't decompose into a base letter plus an accent.</summary>
    private static readonly Dictionary<char, string> Folds = new()
    {
        ['ø'] = "o", ['æ'] = "ae", ['œ'] = "oe", ['ß'] = "ss", ['đ'] = "d", ['ð'] = "d", ['ł'] = "l", ['þ'] = "th", ['ı'] = "i",
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^the\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingThe();

    /// <summary>A trailing "(...)", "[...]" or " - ..." part of a title.</summary>
    [GeneratedRegex(@"(?:\s*[\(\[](?<tag>[^\(\)\[\]]*)[\)\]]|\s+[-–—]\s+(?<tag>[^-–—\(\)\[\]]+))\s*$")]
    private static partial Regex TrailingTag();

    /// <summary>Tags that name a release of the same recording rather than a different one (remix, live, acoustic...).</summary>
    [GeneratedRegex(@"^(?:(?:\d{4}\s+)?(?:digital(?:ly)?\s+)?re-?master(?:ed)?(?:\s+(?:version|edition|mix))?(?:\s+(?:in\s+)?\d{4})?"
                    + @"|(?:radio|single|album|lp|original|extended|clean|explicit|dirty|mono|stereo)\s+(?:edit|version|mix|ver\.?)"
                    + @"|radio\s+edit|single\s+edit|edit|mono|stereo|clean|explicit"
                    + @"|re-?recorded(?:\s+version)?|(?:\d{4}\s+)?re-?recording)$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionTag();

    /// <summary>Tags that name an edition of the same album.</summary>
    [GeneratedRegex(@"^(?:(?:(?:\d+(?:st|nd|rd|th)\s+)?anniversary|(?:super\s+)?deluxe|expanded|special|limited|collector'?s|legacy|platinum|bonus\s+tracks?)"
                    + @"(?:\s+(?:edition|version|reissue))?"
                    + @"|(?:\d{4}\s+)?(?:digital(?:ly)?\s+)?re-?master(?:ed)?(?:\s+(?:version|edition))?(?:\s+\d{4})?"
                    + @"|bonus\s+track\s+version)$", RegexOptions.IgnoreCase)]
    private static partial Regex EditionTag();

    /// <summary>Trims, collapses whitespace, removes control characters and applies Unicode NFC.</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = SafeNormalize(value, NormalizationForm.FormC);
        if (text.Any(char.IsControl))
            text = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length > MaxLength ? text[..MaxLength].TrimEnd() : text;
    }

    /// <summary>Case- and compatibility-insensitive form of an already cleaned name.</summary>
    public static string Key(string cleaned) => SafeNormalize(cleaned, NormalizationForm.FormKC).ToLowerInvariant();

    /// <summary>
    /// Matching key: letters and digits only, lower case, accents on Latin letters removed and "&amp;"/"+" read as "and".
    /// So "AC/DC" = "AC DC", "Guns N' Roses" = "Guns N’ Roses", "Björk" = "Bjork" and "Simon &amp; Garfunkel" = "Simon and Garfunkel".
    /// Marks on other scripts are kept: Japanese dakuten or Devanagari vowel signs change the word.
    /// Names without any letter or digit ("!!!") keep their symbols.
    /// </summary>
    public static string MatchKey(string? value)
    {
        var cleaned = Clean(value);
        if (cleaned.Length == 0) return "";

        var decomposed = SafeNormalize(SafeNormalize(cleaned, NormalizationForm.FormKC).ToLowerInvariant(), NormalizationForm.FormD);
        var key = new StringBuilder(decomposed.Length);
        var afterLatin = false;
        foreach (var rune in decomposed.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                if (!afterLatin) key.Append(rune.ToString());
                continue;
            }

            afterLatin = rune.Value < 0x0250 || rune.Value is >= 0x1E00 and <= 0x1EFF;
            if (rune.IsBmp && Folds.TryGetValue((char)rune.Value, out var folded)) key.Append(folded);
            else if (Rune.IsLetterOrDigit(rune)) key.Append(rune.ToString());
            else if (rune.Value is '&' or '+') key.Append("and");
            // Spaces, punctuation, apostrophes and symbols are left out.
        }

        var result = SafeNormalize(key.ToString(), NormalizationForm.FormC);
        return result.Length > 0 ? result : Key(cleaned);
    }

    /// <summary>Artist matching key; a leading "The" doesn't count ("The Beatles" = "Beatles").</summary>
    public static string ArtistKey(string? name)
    {
        var cleaned = Clean(name);
        var withoutArticle = LeadingThe().Replace(cleaned, "");
        return MatchKey(withoutArticle.Length > 0 ? withoutArticle : cleaned);
    }

    /// <summary>
    /// Track title matching key. Trailing release tags such as " - 2011 Remaster", "(Radio Edit)" or "[Mono]" don't count;
    /// tags that make a different recording ("Live", "Acoustic", "Instrumental", remixes) do. Featured artists are
    /// already taken out of the title by <see cref="MetadataParser"/>.
    /// </summary>
    public static string TitleKey(string? title) => MatchKey(StripTags(Clean(title), VersionTag()));

    /// <summary>Album title matching key; edition tags such as "(Deluxe Edition)" or "[Remastered]" don't count.</summary>
    public static string AlbumKey(string? title) => MatchKey(StripTags(Clean(title), EditionTag()));

    /// <summary>Key for a set of names; order-independent so "A feat. B" and "B feat. A" match.</summary>
    public static string SetKey(IEnumerable<string> keys) => string.Join('\u001e', keys.Order(StringComparer.Ordinal));

    private static string StripTags(string title, Regex tag)
    {
        var text = title;
        for (var i = 0; i < 4; i++)
        {
            var match = TrailingTag().Match(text);
            // Never strip the whole title: an album called "Deluxe" stays "Deluxe".
            if (!match.Success || match.Index == 0 || !tag.IsMatch(match.Groups["tag"].Value.Trim())) break;
            text = text[..match.Index].TrimEnd();
        }

        return text;
    }

    private static string SafeNormalize(string value, NormalizationForm form)
    {
        try
        {
            return value.Normalize(form);
        }
        catch (ArgumentException)
        {
            // Invalid UTF-16 (lone surrogates) - keep the text as it is.
            return value;
        }
    }
}
