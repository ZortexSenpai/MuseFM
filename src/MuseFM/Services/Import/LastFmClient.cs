using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MuseFM.Services.Metadata;

namespace MuseFM.Services.Import;

public sealed class LastFmException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

public sealed record LastFmPage(int Page, int TotalPages, int Total, IReadOnlyList<ImportRow> Rows);

/// <summary>A loved track from user.getLovedTracks.</summary>
public sealed record LastFmLove(TrackRef Track, long LovedAt);

/// <summary>Calls the public Last.fm API: a user's scrobble history (user.getRecentTracks) and metadata lookups.</summary>
public sealed class LastFmClient(HttpClient http, ConnectorSettings settings)
{
    private const int PageSize = 200;
    private const int MaxAttempts = 4;
    private static readonly int[] RetryableErrors = [8, 11, 16, 29];

    public bool IsConfigured => settings.LastFmApiKey is not null;

    /// <summary>Pages through the history, newest first.</summary>
    public async IAsyncEnumerable<LastFmPage> GetRecentTracksAsync(string user, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var apiKey = settings.LastFmApiKey
                     ?? throw new InvalidOperationException("No Last.fm API key is configured (Admin → Metadata or MuseFM__LastFmApiKey).");

        var page = 1;
        int totalPages;
        do
        {
            var args = new[] { ("user", user), ("limit", PageSize.ToString(CultureInfo.InvariantCulture)), ("page", page.ToString(CultureInfo.InvariantCulture)) };
            using var document = await CallAsync(apiKey, "user.getrecenttracks", args, ct);
            if (!document.RootElement.TryGetProperty("recenttracks", out var recent))
                throw new LastFmException(0, "Unexpected response from Last.fm.");

            var attributes = recent.TryGetProperty("@attr", out var attr) ? attr : default;
            totalPages = ReadInt(attributes, "totalPages");
            var total = ReadInt(attributes, "total");

            var rows = new List<ImportRow>();
            if (recent.TryGetProperty("track", out var tracks))
            {
                // Last.fm returns a single object instead of an array when there is exactly one track.
                var items = tracks.ValueKind == JsonValueKind.Array ? tracks.EnumerateArray().ToList() : [tracks];
                foreach (var track in items)
                {
                    if (ParseTrack(track) is { } row) rows.Add(row);
                }
            }

            yield return new LastFmPage(page, totalPages, total, rows);
            page++;
            if (page <= totalPages) await Task.Delay(250, ct); // stay well below Last.fm's rate limit
        }
        while (page <= totalPages);
    }

    /// <summary>All of a user's loved tracks, newest first.</summary>
    public async Task<IReadOnlyList<LastFmLove>> GetLovedTracksAsync(string user, CancellationToken ct = default)
    {
        var apiKey = settings.LastFmApiKey
                     ?? throw new InvalidOperationException("No Last.fm API key is configured (Admin → Metadata or MuseFM__LastFmApiKey).");

        var loves = new List<LastFmLove>();
        for (var (page, totalPages) = (1, 1); page <= totalPages; page++)
        {
            if (page > 1) await Task.Delay(250, ct);
            var args = new[] { ("user", user), ("limit", "1000"), ("page", page.ToString(CultureInfo.InvariantCulture)) };
            using var document = await CallAsync(apiKey, "user.getlovedtracks", args, ct);
            if (!document.RootElement.TryGetProperty("lovedtracks", out var loved)) break;
            totalPages = ReadInt(loved.TryGetProperty("@attr", out var attr) ? attr : default, "totalPages");
            if (!loved.TryGetProperty("track", out var tracks)) break;
            foreach (var track in tracks.ValueKind == JsonValueKind.Array ? tracks.EnumerateArray().ToList() : [tracks])
            {
                var title = JsonValues.GetString(track, "name");
                var artist = Text(track, "artist");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) continue;
                var at = JsonValues.TryGetLong(JsonValues.Child(track, "date"), "uts", out var uts) ? uts : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                loves.Add(new LastFmLove(new TrackRef(title, [artist], JsonValues.GetString(track, "mbid")), at));
            }
        }

        return loves;
    }

    /// <summary>Calls an API method, retrying on rate limits and server errors. Throws <see cref="LastFmException"/> for API errors.</summary>
    public Task<JsonDocument> CallAsync(string apiKey, string method, IEnumerable<(string Name, string Value)> args, CancellationToken ct)
    {
        var url = new StringBuilder("?method=").Append(method)
            .Append("&api_key=").Append(Uri.EscapeDataString(apiKey))
            .Append("&format=json");
        foreach (var (name, value) in args) url.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
        return GetJsonAsync(url.ToString(), ct);
    }

    private static ImportRow? ParseTrack(JsonElement track)
    {
        if (track.ValueKind != JsonValueKind.Object) return null;
        // The currently playing track has no date - it is not a scrobble yet.
        if (!track.TryGetProperty("date", out var date)) return null;
        if (!JsonValues.TryGetLong(date, "uts", out var uts) || uts <= 0)
            return ImportRow.Fail("Track without a valid timestamp.");

        var artistMbid = JsonValues.GetString(JsonValues.Child(track, "artist"), "mbid");
        return ImportRow.Ok(new ScrobbleInput
        {
            Title = JsonValues.GetString(track, "name") ?? "",
            Artists = [Text(track, "artist") ?? ""],
            Album = Text(track, "album"),
            ListenedAt = uts,
            Origin = "import:lastfm",
            TrackMbid = JsonValues.GetString(track, "mbid"),
            AlbumMbid = JsonValues.GetString(JsonValues.Child(track, "album"), "mbid"),
            ArtistMbids = string.IsNullOrEmpty(artistMbid) ? null : [artistMbid],
        });
    }

    private static string? Text(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => JsonValues.GetString(value, "#text") ?? JsonValues.GetString(value, "name"),
            _ => null,
        };
    }

    private static int ReadInt(JsonElement element, string property) =>
        JsonValues.TryGetLong(element, property, out var value) ? (int)Math.Clamp(value, 0, int.MaxValue) : 0;

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var response = await http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
            }

            if (document is not null && document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error))
            {
                var code = JsonValues.TryGetLong(error, out var c) ? (int)c : 0;
                var message = JsonValues.GetString(document.RootElement, "message") ?? "Unknown Last.fm error.";
                document.Dispose();
                if (RetryableErrors.Contains(code) && attempt < MaxAttempts)
                {
                    await Task.Delay(Backoff(attempt), ct);
                    continue;
                }

                throw new LastFmException(code, $"Last.fm: {message}");
            }

            if (response.IsSuccessStatusCode && document is not null) return document;
            document?.Dispose();

            if (attempt < MaxAttempts && ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests))
            {
                await Task.Delay(Backoff(attempt), ct);
                continue;
            }

            throw new LastFmException(0, string.Create(CultureInfo.InvariantCulture, $"Last.fm returned HTTP {(int)response.StatusCode}."));
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));
}
