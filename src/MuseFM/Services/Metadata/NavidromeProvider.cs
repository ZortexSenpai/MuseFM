using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MuseFM.Data.Entities;

namespace MuseFM.Services.Metadata;

/// <summary>
/// Album artwork, artist images and biographies from a Navidrome server (Subsonic API). Covers come from the
/// library itself, like an album's year, genres and track lengths, which are what its files are tagged with;
/// artist images and biographies come from Navidrome's agents (Last.fm, Spotify, Deezer...).
/// </summary>
public sealed class NavidromeProvider(IHttpClientFactory httpFactory) : IMetadataProvider
{
    private const string Source = "Navidrome";
    private const string ApiVersion = "1.16.1";
    private const string ImageSize = "600";

    // It's the admin's own server: allow some parallelism, no pacing.
    private static readonly RateGate Gate = new(TimeSpan.Zero, concurrency: 4);

    public ConnectorKind Kind => ConnectorKind.Navidrome;

    public async Task<string> TestAsync(ConnectorConfig config, CancellationToken ct)
    {
        using var document = await CallAsync(config, "ping", [], ct);
        var response = Body(document);
        var type = JsonValues.GetString(response, "type");
        var version = JsonValues.GetString(response, "serverVersion");
        var server = string.IsNullOrEmpty(type) ? "the server" : char.ToUpperInvariant(type[0]) + type[1..];
        return string.IsNullOrEmpty(version) ? $"Connected to {server}." : $"Connected to {server} {version}.";
    }

    public async Task<ArtistLookup?> FindArtistAsync(ConnectorConfig config, ArtistQuery query, CancellationToken ct)
    {
        // Navidrome has genres for albums only.
        if (!query.NeedImage && !query.NeedBio) return null;

        string? id, coverArt, mbid;
        using (var search = await CallAsync(config, "search3", [("query", query.Name), ("artistCount", "20"), ("albumCount", "0"), ("songCount", "0")], ct))
        {
            var artist = Results(search, "artist").FirstOrDefault(a => NameMatch.Same(JsonValues.GetString(a, "name"), query.Name));
            id = JsonValues.GetString(artist, "id");
            if (string.IsNullOrEmpty(id)) return null;
            coverArt = JsonValues.GetString(artist, "coverArt");
            mbid = MetadataText.Mbid(JsonValues.GetString(artist, "musicBrainzId"));
        }

        using var info = await CallAsync(config, "getArtistInfo2", [("id", id), ("count", "0")], ct);
        var details = JsonValues.Child(Body(info), "artistInfo2");
        mbid ??= MetadataText.Mbid(JsonValues.GetString(details, "musicBrainzId"));

        Biography? bio = null;
        if (query.NeedBio && MetadataText.CleanBio(JsonValues.GetString(details, "biography")) is { } text)
            bio = new Biography(text, Source, MetadataText.HttpUrl(JsonValues.GetString(details, "lastFmUrl")));

        // Navidrome leaves the image URLs out when it has no picture of the artist.
        ImageData? image = null;
        var hasImage = new[] { "largeImageUrl", "mediumImageUrl", "smallImageUrl" }
            .Any(p => !string.IsNullOrEmpty(JsonValues.GetString(details, p)));
        if (query.NeedImage && hasImage)
            image = await GetCoverArtAsync(config, string.IsNullOrEmpty(coverArt) ? id : coverArt, ct);

        return new ArtistLookup(mbid, bio, image);
    }

    public async Task<AlbumLookup?> FindAlbumAsync(ConnectorConfig config, AlbumQuery query, CancellationToken ct)
    {
        string? id, coverArt, mbid;
        int? year;
        IReadOnlyList<string> genres;
        using (var search = await CallAsync(config, "search3", [("query", query.Title), ("artistCount", "0"), ("albumCount", "50"), ("songCount", "0")], ct))
        {
            var album = Results(search, "album")
                .Where(a => NameMatch.SameTitle(JsonValues.GetString(a, "name") ?? JsonValues.GetString(a, "title"), query.Title))
                .FirstOrDefault(a => ArtistsMatch(a, query.Artists));
            if (album.ValueKind != JsonValueKind.Object) return null;
            id = JsonValues.GetString(album, "id");
            coverArt = JsonValues.GetString(album, "coverArt");
            mbid = MetadataText.Mbid(JsonValues.GetString(album, "musicBrainzId"));
            // A reissue's own year is the fallback for when the original release is not tagged.
            year = Year(JsonValues.Child(album, "originalReleaseDate")) ?? Year(album);
            genres = Genres(album);
        }

        var tracks = string.IsNullOrEmpty(id) ? [] : await TracksAsync(config, id, ct);
        // An empty coverArt means Navidrome knows the album has no artwork.
        var image = !query.NeedImage || string.IsNullOrEmpty(coverArt) ? null : await GetCoverArtAsync(config, coverArt, ct);
        return new AlbumLookup(mbid, image, Year: year, Genres: genres, Tracks: tracks);
    }

    private static int? Year(JsonElement element) =>
        JsonValues.TryGetLong(element, "year", out var year) && year is > 0 and < 10_000 ? (int)year : null;

    /// <summary>OpenSubsonic's list of genres, or the single genre of the original Subsonic API.</summary>
    private static IReadOnlyList<string> Genres(JsonElement album)
    {
        var names = JsonValues.Child(album, "genres") is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Select(g => JsonValues.GetString(g, "name")).ToList()
            : [];
        return GenreNames.Clean(names.Count > 0 ? names : [JsonValues.GetString(album, "genre")]);
    }

    private async Task<IReadOnlyList<TrackLength>> TracksAsync(ConnectorConfig config, string albumId, CancellationToken ct)
    {
        using var document = await CallAsync(config, "getAlbum", [("id", albumId)], ct);
        var songs = JsonValues.Child(JsonValues.Child(Body(document), "album"), "song") switch
        {
            { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().ToList(),
            { ValueKind: JsonValueKind.Object } single => [single],
            _ => [],
        };
        return songs
            .Select(s => (Title: JsonValues.GetString(s, "title"), Seconds: JsonValues.GetSeconds(s, "duration")))
            .Where(s => !string.IsNullOrWhiteSpace(s.Title) && s.Seconds is not null)
            .Select(s => new TrackLength(s.Title!, s.Seconds!.Value))
            .ToList();
    }

    private static bool ArtistsMatch(JsonElement album, IReadOnlyList<string> artists)
    {
        var credits = new List<string?> { JsonValues.GetString(album, "artist"), JsonValues.GetString(album, "displayArtist") };
        if (JsonValues.Child(album, "artists") is { ValueKind: JsonValueKind.Array } list)
            credits.AddRange(list.EnumerateArray().Select(a => JsonValues.GetString(a, "name")));
        return artists.Any(artist => credits.Any(credit => NameMatch.Credits(credit, artist)));
    }

    private async Task<ImageData?> GetCoverArtAsync(ConnectorConfig config, string id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(config, "getCoverArt", [("id", id), ("size", ImageSize)]));
        using (await Gate.EnterAsync(ct))
            return await MetadataHttp.GetImageAsync(Http, request, Source, ct, accept: IsRealArtwork);
    }

    // For albums and artists without artwork Navidrome serves a generic placeholder with HTTP 200. Placeholders are
    // sent with "Cache-Control: no-store" and no ETag; real artwork always carries an ETag.
    private static bool IsRealArtwork(HttpResponseMessage response) =>
        response.Headers.ETag is not null || response.Headers.CacheControl?.NoStore != true;

    private async Task<JsonDocument> CallAsync(ConnectorConfig config, string method, IEnumerable<(string Name, string Value)> args, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(config, method, args));
        JsonDocument? document;
        using (await Gate.EnterAsync(ct))
            document = await MetadataHttp.GetJsonAsync(Http, request, Source, ct);
        if (document is null) throw new MetadataSourceException("Navidrome: no Subsonic API found at this URL.");

        var response = Body(document);
        if (JsonValues.GetString(response, "status") == "ok") return document;

        var error = JsonValues.Child(response, "error");
        var code = JsonValues.TryGetLong(error, "code", out var c) ? c : 0;
        var message = JsonValues.GetString(error, "message")?.TrimEnd('.');
        document.Dispose();
        throw new MetadataSourceException(code is 40 or 41 or 44
            ? "Navidrome: wrong username or password."
            : $"Navidrome: {message ?? "unexpected response"}{(code > 0 ? $" (error {code})" : "")}.");
    }

    private static Uri Url(ConnectorConfig config, string method, IEnumerable<(string Name, string Value)> args)
    {
        // Token authentication: t = md5(password + salt) with a fresh salt per request, so the password is never sent.
        var salt = RandomNumberGenerator.GetHexString(16, lowercase: true);
#pragma warning disable CA5351 // MD5 is what the Subsonic API specifies.
        var token = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(config.Secret + salt)));
#pragma warning restore CA5351
        var query = new StringBuilder()
            .Append("u=").Append(Uri.EscapeDataString(config.Username ?? ""))
            .Append("&t=").Append(token)
            .Append("&s=").Append(salt)
            .Append("&v=").Append(ApiVersion)
            .Append("&c=MuseFM&f=json");
        foreach (var (name, value) in args) query.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
        return new Uri($"{config.BaseUrl!.TrimEnd('/')}/rest/{method}?{query}");
    }

    private static JsonElement Body(JsonDocument document) => JsonValues.Child(document.RootElement, "subsonic-response");

    private static IReadOnlyList<JsonElement> Results(JsonDocument document, string kind) =>
        JsonValues.Child(JsonValues.Child(Body(document), "searchResult3"), kind) switch
        {
            { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().ToList(),
            { ValueKind: JsonValueKind.Object } single => [single],
            _ => [],
        };

    private HttpClient Http => httpFactory.CreateClient(MetadataHttp.ClientName);
}
