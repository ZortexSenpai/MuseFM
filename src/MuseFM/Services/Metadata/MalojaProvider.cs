using System.Net;
using System.Text.Json;
using MuseFM.Data.Entities;

namespace MuseFM.Services.Metadata;

/// <summary>
/// Artist images and album covers from a Maloja server: pictures uploaded there and the ones Maloja found through its own
/// sources. Maloja has no biographies, and its read API needs no key.
/// </summary>
/// <remarks>
/// Maloja only exposes its internal ids, which its image URLs need, through search results, and searching is also what
/// makes it start looking for missing pictures. While it looks, an image request is held for up to five seconds and
/// then answered with 202; when it has nothing, it redirects to a placeholder.
/// </remarks>
public sealed class MalojaProvider(IHttpClientFactory httpFactory) : IMetadataProvider
{
    private const string Source = "Maloja";
    private const string SearchLimit = "50";
    private const int ImageAttempts = 3;
    private const int MaxRedirects = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    // The admin's own server, but a small Python app that holds image requests while it looks: two at a time.
    private static readonly RateGate Gate = new(TimeSpan.Zero, concurrency: 2);

    public ConnectorKind Kind => ConnectorKind.Maloja;

    public async Task<string> TestAsync(ConnectorConfig config, CancellationToken ct)
    {
        using var info = await GetJsonAsync(config, "apis/mlj_1/serverinfo", ct);
        var version = info is { RootElement.ValueKind: JsonValueKind.Object } ? JsonValues.GetString(info.RootElement, "versionstring") : null;
        if (version is null) throw new MetadataSourceException("Maloja: no Maloja server found at this URL.");
        return $"Connected to Maloja {version}.";
    }

    public async Task<ArtistLookup?> FindArtistAsync(ConnectorConfig config, ArtistQuery query, CancellationToken ct)
    {
        if (!query.NeedImage) return null;

        string? link;
        using (var results = await SearchAsync(config, query.Name, ct))
        {
            var artist = Items(results, "artists").FirstOrDefault(a => NameMatch.Same(JsonValues.GetString(a, "artist"), query.Name));
            link = JsonValues.GetString(artist, "image");
        }

        if (Resolve(config, link) is not { } uri) return null;
        var (image, pending) = await GetImageAsync(uri, ct);
        return new ArtistLookup(null, null, image, pending);
    }

    public async Task<AlbumLookup?> FindAlbumAsync(ConnectorConfig config, AlbumQuery query, CancellationToken ct)
    {
        string? link;
        using (var results = await SearchAsync(config, query.Title, ct))
        {
            var hit = Items(results, "albums").FirstOrDefault(a =>
            {
                var album = JsonValues.Child(a, "album");
                return NameMatch.SameTitle(JsonValues.GetString(album, "albumtitle"), query.Title) && ArtistsMatch(album, query.Artists);
            });
            link = JsonValues.GetString(hit, "image");
        }

        if (Resolve(config, link) is not { } uri) return null;
        var (image, pending) = await GetImageAsync(uri, ct);
        return new AlbumLookup(null, image, pending);
    }

    private static bool ArtistsMatch(JsonElement album, IReadOnlyList<string> artists)
    {
        if (artists.Count == 0) return true;
        var credits = JsonValues.GetStringList(album, "artists").Append(JsonValues.GetString(album, "displayArtist")).ToList();
        return artists.Any(artist => credits.Any(credit => NameMatch.Credits(credit, artist)));
    }

    /// <summary>Fetches an image, asking again a few times while Maloja is still looking for it.</summary>
    private async Task<(ImageData? Image, bool Pending)> GetImageAsync(Uri uri, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (image, pending) = await FollowAsync(uri, ct);
            if (!pending || attempt == ImageAttempts) return (image, pending);
            await Task.Delay(RetryDelay, ct);
        }
    }

    /// <summary>
    /// Requests an image and follows Maloja's redirects to wherever the picture is (its own files or a third-party URL),
    /// stopping at placeholders. Redirects are followed here too because the HTTP handler may not (https → http).
    /// </summary>
    private async Task<(ImageData? Image, bool Pending)> FollowAsync(Uri uri, CancellationToken ct)
    {
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (IsPlaceholder(uri)) return (null, false);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var slot = await Gate.EnterAsync(ct);
            using var response = await MetadataHttp.SendAsync(Http, request, Source, ct);
            // The handler may already have followed redirects: judge the picture by where it came from.
            if (IsPlaceholder(response.RequestMessage?.RequestUri)) return (null, false);
            if (response.StatusCode == HttpStatusCode.Accepted) return (null, true);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(response.RequestMessage?.RequestUri ?? uri, location);
                if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps) return (null, false);
                uri = next;
                continue;
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.BadRequest) return (null, false);
            MetadataHttp.EnsureSuccess(response, Source);
            return (await MetadataHttp.ReadImageAsync(response, Source, ct), false);
        }

        return (null, false);
    }

    // Maloja's stand-ins for missing pictures: its bundled SVGs, or generated art when "fancy placeholder art" is on.
    private static bool IsPlaceholder(Uri? uri) =>
        uri is not null
        && (uri.Host.Equals("generative-placeholders.glitch.me", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/static/svg/placeholder", StringComparison.OrdinalIgnoreCase));

    private Task<JsonDocument?> SearchAsync(ConnectorConfig config, string text, CancellationToken ct) =>
        GetJsonAsync(config, $"apis/mlj_1/search?query={Uri.EscapeDataString(text)}&max={SearchLimit}", ct);

    private async Task<JsonDocument?> GetJsonAsync(ConnectorConfig config, string pathAndQuery, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{config.BaseUrl!.TrimEnd('/')}/{pathAndQuery}"));
        using (await Gate.EnterAsync(ct))
            return await MetadataHttp.GetJsonAsync(Http, request, Source, ct);
    }

    /// <summary>An image link from a search result as an absolute URL. Maloja's own links start at its root.</summary>
    private static Uri? Resolve(ConnectorConfig config, string? link)
    {
        if (string.IsNullOrEmpty(link)) return null;
        if (Uri.TryCreate(link, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return absolute;
        return link.StartsWith('/') && !link.StartsWith("//", StringComparison.Ordinal)
               && Uri.TryCreate(config.BaseUrl!.TrimEnd('/') + link, UriKind.Absolute, out var local)
            ? local
            : null;
    }

    private static IReadOnlyList<JsonElement> Items(JsonDocument? document, string kind) =>
        document is not null && JsonValues.Child(document.RootElement, kind) is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().ToList()
            : [];

    private HttpClient Http => httpFactory.CreateClient(MetadataHttp.ClientName);
}
