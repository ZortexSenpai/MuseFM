using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MuseFM.Services.Metadata;

/// <summary>HTTP plumbing shared by the metadata providers.</summary>
internal static class MetadataHttp
{
    public const string ClientName = "metadata";
    public const string UserAgent = "MuseFM/1.0 (self-hosted music statistics)";
    private const int MaxImageBytes = 15 * 1024 * 1024;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>GETs a JSON document. Returns null for 400 and 404, which these APIs use for "no match".</summary>
    public static async Task<JsonDocument?> GetJsonAsync(HttpClient http, HttpRequestMessage request, string source, CancellationToken ct)
    {
        using var response = await SendAsync(http, request, source, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return null;
        EnsureSuccess(response, source);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new MetadataSourceException($"{source} did not answer with JSON. Check the URL.", ex);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, ct))
        {
            throw Unreachable(source, ex);
        }
    }

    /// <summary>
    /// Downloads an image. Returns null when there is none: 404, a body that is not JPEG/PNG/GIF/WebP,
    /// more than 15 MB, or a response rejected by <paramref name="accept"/>.
    /// </summary>
    public static async Task<ImageData?> GetImageAsync(
        HttpClient http, HttpRequestMessage request, string source, CancellationToken ct, Func<HttpResponseMessage, bool>? accept = null)
    {
        using var response = await SendAsync(http, request, source, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.BadRequest) return null;
        EnsureSuccess(response, source);
        if (accept is not null && !accept(response)) return null;
        return await ReadImageAsync(response, source, ct);
    }

    /// <summary>Reads the body of a successful response as an image; null when it is not JPEG/PNG/GIF/WebP or over 15 MB.</summary>
    public static async Task<ImageData?> ReadImageAsync(HttpResponseMessage response, string source, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxImageBytes) return null;

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxImageBytes) return null;
                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.ToArray();
            return SniffExtension(bytes) is { } extension ? new ImageData(bytes, extension, source) : null;
        }
        catch (Exception ex) when (IsNetworkFailure(ex, ct))
        {
            throw Unreachable(source, ex);
        }
    }

    /// <summary>File extension for JPEG, PNG, GIF and WebP data. Anything else (notably SVG, which can carry scripts) is refused.</summary>
    public static string? SniffExtension(ReadOnlySpan<byte> b)
    {
        if (b.Length < 12) return null;
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b[..8].SequenceEqual(PngSignature)) return ".png";
        if (b[..4].SequenceEqual("GIF8"u8)) return ".gif";
        if (b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return ".webp";
        return null;
    }

    public static string ContentType(string extension) => extension switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    /// <summary>Sends a request, reading only the headers. Network failures and timeouts become <see cref="MetadataSourceException"/>.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string source, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, ct))
        {
            throw Unreachable(source, ex);
        }
    }

    public static void EnsureSuccess(HttpResponseMessage response, string source)
    {
        if (response.IsSuccessStatusCode) return;
        var code = (int)response.StatusCode;
        throw new MetadataSourceException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{source} rejected the credentials (HTTP {code}).",
            HttpStatusCode.TooManyRequests => $"{source} is rate limiting requests (HTTP 429).",
            _ => $"{source} returned HTTP {code}.",
        });
    }

    // HttpClient reports its own timeout as a cancellation that the caller did not request.
    private static bool IsNetworkFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private static MetadataSourceException Unreachable(string source, Exception ex) =>
        ex is OperationCanceledException
            ? new MetadataSourceException($"{source} did not answer in time.", ex)
            : new MetadataSourceException($"{source} could not be reached: {ex.Message}", ex);
}

/// <summary>Cleaning of texts and identifiers received from metadata sources.</summary>
internal static partial class MetadataText
{
    private const int MaxBioLength = 12_000;

    /// <summary>
    /// Turns a biography (Last.fm/Navidrome markup, HTML or plain text) into plain paragraphs separated by blank lines.
    /// Drops Last.fm's "Read more" link and licence footer; the page shows its own source line instead.
    /// </summary>
    /// <param name="html">False for plain text, which is taken literally.</param>
    public static string? CleanBio(string? value, bool html = true)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value;
        if (html)
        {
            text = ReadMoreLink().Replace(text, "");
            text = LicenceNote().Replace(text, "");
            text = BlockBreak().Replace(text, "\n");
            text = Tag().Replace(text, "");
            text = WebUtility.HtmlDecode(text);
        }

        var result = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var paragraph = Whitespace().Replace(line, " ").Trim();
            if (paragraph.Length == 0) continue;
            if (result.Length + paragraph.Length > MaxBioLength) break;
            if (result.Length > 0) result.Append("\n\n");
            result.Append(paragraph);
        }

        return result.Length == 0 ? null : result.ToString();
    }

    public static IReadOnlyList<string> Paragraphs(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A MusicBrainz id in canonical form, or null when the value is not a GUID.</summary>
    public static string? Mbid(string? value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString("D", CultureInfo.InvariantCulture) : null;

    /// <summary>The value if it is an absolute http(s) URL that fits the database column, else null.</summary>
    public static string? HttpUrl(string? value) =>
        value is { Length: <= 512 } && Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri
            : null;

    [GeneratedRegex(@"<a\b[^>]*>\s*Read more on Last\.fm\s*</a>.*", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ReadMoreLink();

    [GeneratedRegex(@"User-contributed text is available under the Creative Commons By-SA License;?\s*additional terms may apply\.?", RegexOptions.IgnoreCase)]
    private static partial Regex LicenceNote();

    [GeneratedRegex(@"<\s*(?:br|/?p|/div|/li|/h\d)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Lenient comparison of artist and album names coming from different sources.</summary>
internal static class NameMatch
{
    /// <summary>Equal ignoring case, accents, punctuation, spacing, a leading "The" and "&amp;" vs "and".</summary>
    public static bool Same(string? a, string? b)
    {
        var x = Compact(a);
        var y = Compact(b);
        if (x.Length > 0 || y.Length > 0) return x == y;
        // Names without any letters or digits (e.g. "!!!"): fall back to the catalog key.
        var key = TextKey.Key(TextKey.Clean(a));
        return key.Length > 0 && key == TextKey.Key(TextKey.Clean(b));
    }

    /// <summary>Like <see cref="Same"/>, but also ignores trailing qualifiers such as "(Deluxe Edition)" or "[Remastered]".</summary>
    public static bool SameTitle(string? a, string? b) => Same(a, b) || Same(StripQualifiers(a), StripQualifiers(b));

    /// <summary>True when an artist credit such as "A &amp; B" or "A feat. C" names <paramref name="artist"/> as whole words.</summary>
    public static bool Credits(string? credit, string? artist)
    {
        var words = Words(artist);
        if (words.Length == 0) return Same(credit, artist);
        return $" {Words(credit)} ".Contains($" {words} ", StringComparison.Ordinal);
    }

    private static string Compact(string? value)
    {
        var words = Words(value);
        if (words.StartsWith("the ", StringComparison.Ordinal)) words = words[4..];
        return words.Replace(" ", "", StringComparison.Ordinal);
    }

    private static string Words(string? value)
    {
        var cleaned = TextKey.Clean(value);
        if (cleaned.Length == 0) return "";
        string decomposed;
        try
        {
            decomposed = cleaned.Normalize(NormalizationForm.FormKD);
        }
        catch (ArgumentException)
        {
            decomposed = cleaned;
        }

        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (c is '&' or '+') sb.Append(" and ");
            else sb.Append(' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? StripQualifiers(string? title)
    {
        if (title is null) return null;
        var text = title.Trim();
        while (text.Length > 0 && text[^1] is ')' or ']')
        {
            var open = text.LastIndexOf(text[^1] == ')' ? '(' : '[');
            if (open <= 0) break;
            text = text[..open].TrimEnd();
        }

        return text;
    }
}
