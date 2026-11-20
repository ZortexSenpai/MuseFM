using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MuseFM.Services.Import;

public sealed class MalojaException(string message, Exception? inner = null) : Exception(message, inner);

/// <param name="Month">The month the scrobbles are from, in the Maloja server's time zone.</param>
/// <param name="Total">Scrobbles to import in all, as far as known so far.</param>
public sealed record MalojaPage(DateOnly Month, int Total, IReadOnlyList<ImportRow> Rows);

/// <summary>
/// Reads a complete scrobble history from a Maloja server through its read API, which needs no key.
/// The history is fetched one calendar month at a time, newest first: Maloja's own paging re-reads all earlier pages
/// for every page, which gets slow on large libraries, while a month is a cheap range query.
/// </summary>
public sealed class MalojaClient(HttpClient http)
{
    public const int MaxUrlLength = 200;

    /// <summary>The HTTP client for addresses typed by any user: it only connects to the public internet (see <see cref="PublicInternet"/>).</summary>
    public const string PublicClientName = "maloja-import";

    /// <summary>The HTTP client that also reaches the server's own network, where a Maloja usually runs: for administrators.</summary>
    public const string AnyAddressClientName = "maloja-import-any-address";

    private const string Api = "apis/mlj_1/";

    // Maloja computes months in its own time zone, so a month's scrobbles can lie up to 14 hours outside the UTC month.
    private static readonly TimeSpan ZoneSlack = TimeSpan.FromHours(14);

    // Safety stop for the walk back in time; real histories end long before.
    private static readonly DateOnly Oldest = new(1970, 1, 1);

    /// <summary>
    /// Checks and tidies a server address typed by a user: adds http:// when no scheme is given and drops a pasted API path.
    /// </summary>
    public static bool TryNormalizeUrl(string? value, out string url, out string? error)
    {
        url = "";
        var text = value?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = "Please enter the address of your Maloja server.";
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.Host.Length == 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            error = "Enter the address of your Maloja server, for example http://192.168.1.10:42010 or https://maloja.example.com.";
            return false;
        }

        if (uri.UserInfo.Length > 0)
        {
            error = "Leave the user name and password out of the address.";
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/apis/mlj_1", StringComparison.OrdinalIgnoreCase)) path = path[..^"/apis/mlj_1".Length];
        url = uri.GetLeftPart(UriPartial.Authority) + path;
        if (url.Length > MaxUrlLength)
        {
            error = "That address is too long.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Pages through the history month by month, newest first, until everything Maloja counts has been read.</summary>
    public async IAsyncEnumerable<MalojaPage> GetScrobblesAsync(string baseUrl, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using (var info = await GetAsync(baseUrl, "serverinfo", ct))
        {
            if (info.RootElement.ValueKind != JsonValueKind.Object || !info.RootElement.TryGetProperty("version", out _))
                throw new MalojaException(NotMaloja(baseUrl));
        }

        var total = await CountAsync(baseUrl, "", ct);
        var seen = 0;
        // A server ahead of UTC may already be in the next month.
        var month = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1);
        while (month >= Oldest)
        {
            var rows = await GetMonthAsync(baseUrl, month, ct);
            seen += rows.Count;
            total = Math.Max(total, seen);
            yield return new MalojaPage(month, total, rows);

            if (seen >= total)
            {
                // Everything counted at the start has arrived, unless new scrobbles came in meanwhile: check that nothing older is left.
                var older = await CountAsync(baseUrl, "?to=" + Month(month.AddMonths(-1)), ct);
                if (older == 0) yield break;
                total = seen + older;
            }

            month = month.AddMonths(-1);
        }
    }

    private async Task<int> CountAsync(string baseUrl, string query, CancellationToken ct)
    {
        using var document = await GetAsync(baseUrl, "numscrobbles" + query, ct);
        if (!JsonValues.TryGetLong(document.RootElement, "amount", out var amount) || amount < 0)
            throw new MalojaException("Maloja sent an unexpected answer when asked for the number of scrobbles.");
        return (int)Math.Min(amount, int.MaxValue);
    }

    private async Task<List<ImportRow>> GetMonthAsync(string baseUrl, DateOnly month, CancellationToken ct)
    {
        using var document = await GetAsync(baseUrl, "scrobbles?in=" + Month(month), ct);
        if (!document.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new MalojaException("Maloja sent an unexpected answer when asked for scrobbles.");

        var from = new DateTimeOffset(month.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) - ZoneSlack;
        var to = new DateTimeOffset(month.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) + ZoneSlack;
        var rows = new List<ImportRow>(list.GetArrayLength());
        foreach (var scrobble in list.EnumerateArray())
        {
            var row = MalojaFormat.ParseScrobble(scrobble);
            // Old versions ignore the time range and send everything; walking back month by month would then never end.
            if (row.Input is { } input && (input.ListenedAt < from.ToUnixTimeSeconds() || input.ListenedAt >= to.ToUnixTimeSeconds()))
                throw new MalojaException("This Maloja server does not support time ranges. Update Maloja, or upload a Maloja export file instead.");
            rows.Add(row);
        }

        return rows;
    }

    private async Task<JsonDocument> GetAsync(string baseUrl, string pathAndQuery, CancellationToken stopping)
    {
        // The client's own timeout ends with the response headers; this one also covers a body that never ends.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        deadline.CancelAfter(http.Timeout);
        var ct = deadline.Token;
        try
        {
            using var response = await http.GetAsync(new Uri($"{baseUrl}/{Api}{pathAndQuery}"), HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) throw new MalojaException(NotMaloja(baseUrl));
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                throw new MalojaException("Maloja is still starting up (its database is not ready). Try again in a minute.");
            if (!response.IsSuccessStatusCode)
                throw new MalojaException(string.Create(CultureInfo.InvariantCulture, $"Maloja returned HTTP {(int)response.StatusCode}."));

            await using var stream = new SizeLimitedStream(await response.Content.ReadAsStreamAsync(ct), ImportLimits.MaxMalojaResponseBytes, "");
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (InvalidDataException ex)
        {
            throw new MalojaException(
                $"Maloja sent more than {ImportLimits.Megabytes(ImportLimits.MaxMalojaResponseBytes)} in one answer. Upload a Maloja export file instead.", ex);
        }
        catch (JsonException ex)
        {
            throw new MalojaException(NotMaloja(baseUrl), ex);
        }
        catch (Exception ex) when (PublicInternet.Blocked(ex))
        {
            throw new MalojaException(
                $"{baseUrl} is not an address on the public internet. Only administrators can import from a server on a private or local network.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Without the socket error: together with the HTTP errors it would tell which ports of a host are open.
            throw new MalojaException($"The Maloja server at {baseUrl} could not be reached. Check the address and that the server is running.", ex);
        }
        catch (OperationCanceledException ex) when (!stopping.IsCancellationRequested)
        {
            throw new MalojaException($"The Maloja server at {baseUrl} did not answer in time.", ex);
        }
    }

    private static string Month(DateOnly month) => month.ToString("yyyy'/'MM", CultureInfo.InvariantCulture);

    private static string NotMaloja(string baseUrl) =>
        $"No Maloja server found at {baseUrl}. Check the address; Maloja listens on port 42010 unless configured otherwise.";
}
