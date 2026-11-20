using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using MuseFM.Data.Entities;

namespace MuseFM.Services.Metadata;

/// <summary>
/// Metadata from the open MusicBrainz ecosystem, matched through ListenBrainz' MusicBrainz mapping:
/// album covers from the Cover Art Archive, release years and genres (the tags MusicBrainz lists as genres), and, via the
/// artist's Wikidata item, the introduction of their English Wikipedia article as biography and their Wikimedia Commons
/// picture as photo.
/// </summary>
public sealed class ListenBrainzProvider(IHttpClientFactory httpFactory) : IMetadataProvider
{
    private const string Source = "ListenBrainz";
    private const string ApiBase = "https://api.listenbrainz.org/1/";

    // ListenBrainz asks for at most one request per second.
    private static readonly RateGate ApiGate = new(TimeSpan.FromSeconds(1));
    private static readonly RateGate WikimediaGate = new(TimeSpan.FromMilliseconds(100), concurrency: 2);
    private static readonly RateGate CoverArtGate = new(TimeSpan.Zero, concurrency: 2);

    public ConnectorKind Kind => ConnectorKind.ListenBrainz;

    public async Task<string> TestAsync(ConnectorConfig config, CancellationToken ct)
    {
        using var document = await GetApiAsync(config, "validate-token", ct);
        var root = document?.RootElement ?? default;
        if (JsonValues.Child(root, "valid").ValueKind != JsonValueKind.True)
            throw new MetadataSourceException("ListenBrainz: the token is not valid.");
        var user = JsonValues.GetString(root, "user_name");
        return user is null ? "The token is valid." : $"The token belongs to ListenBrainz user {user}.";
    }

    public async Task<ArtistLookup?> FindArtistAsync(ConnectorConfig config, ArtistQuery query, CancellationToken ct)
    {
        var mbid = query.MusicBrainzId;
        string? wikidata = null;
        IReadOnlyList<string> genres = [];
        if (mbid is null)
        {
            // ListenBrainz can only map recordings, so identify the artist through one of their tracks.
            foreach (var track in query.Tracks.Take(2))
            {
                var candidates = await LookupArtistsAsync(config, track, ct);
                if (candidates.Count == 0) continue;
                (mbid, wikidata, genres) = await PickArtistAsync(config, candidates, query.Name, ct);
                if (mbid is not null) break;
            }

            if (mbid is null) return null;
        }
        else
        {
            (_, wikidata, genres) = await PickArtistAsync(config, [mbid], null, ct);
        }

        var (bio, image) = wikidata is null || (!query.NeedBio && !query.NeedImage) ? (null, null) : await FromWikidataAsync(wikidata, query, ct);
        return new ArtistLookup(mbid, bio, image, Genres: genres);
    }

    public async Task<AlbumLookup?> FindAlbumAsync(ConnectorConfig config, AlbumQuery query, CancellationToken ct)
    {
        var known = query.MusicBrainzId;
        var cover = known is not null && query.NeedImage ? await CoverAsync($"release/{known}/front-500", ct) : null;

        // The year and genres only come with a lookup through one of the album's tracks, also when the release is known.
        foreach (var track in query.Tracks.Take(2))
        {
            string release, path;
            int? year;
            IReadOnlyList<string> genres;
            var lookup = $"metadata/lookup/?artist_name={Esc(track.Artist)}&recording_name={Esc(track.Title)}"
                         + $"&release_name={Esc(query.Title)}&metadata=true&inc=release%20tag";
            using (var document = await GetApiAsync(config, lookup, ct))
            {
                var root = document?.RootElement ?? default;
                // The recording may also be on singles and compilations: only trust a match on this album's title.
                if (MetadataText.Mbid(JsonValues.GetString(root, "release_mbid")) is not { } matched
                    || !NameMatch.SameTitle(JsonValues.GetString(root, "release_name"), query.Title))
                    continue;
                release = matched;
                path = CoverArtPath(root) ?? $"release/{release}/front-500";
                var metadata = JsonValues.Child(root, "metadata");
                year = JsonValues.TryGetLong(JsonValues.Child(metadata, "release"), "year", out var y) && y is > 0 and < 10_000 ? (int)y : null;
                genres = Genres(JsonValues.Child(JsonValues.Child(metadata, "tag"), "release_group"));
            }

            if (cover is null && query.NeedImage) cover = await CoverAsync(path, ct);
            return new AlbumLookup(known ?? release, cover, Year: year, Genres: genres);
        }

        return known is not null && cover is not null ? new AlbumLookup(known, cover) : null;
    }

    /// <summary>The tags MusicBrainz lists as genres (they carry a genre id), the most used first.</summary>
    private static IReadOnlyList<string> Genres(JsonElement tags)
    {
        if (tags.ValueKind != JsonValueKind.Array) return [];
        var genres = tags.EnumerateArray()
            .Where(t => MetadataText.Mbid(JsonValues.GetString(t, "genre_mbid")) is not null)
            .OrderByDescending(t => JsonValues.TryGetLong(t, "count", out var count) ? count : 0)
            .Select(t => JsonValues.GetString(t, "tag"));
        return GenreNames.Clean(genres, titleCase: true);
    }

    private async Task<IReadOnlyList<string>> LookupArtistsAsync(ConnectorConfig config, SampleTrack track, CancellationToken ct)
    {
        using var document = await GetApiAsync(config, $"metadata/lookup/?artist_name={Esc(track.Artist)}&recording_name={Esc(track.Title)}", ct);
        return JsonValues.GetStringList(document?.RootElement ?? default, "artist_mbids")
            .Select(MetadataText.Mbid).OfType<string>().Distinct().ToList();
    }

    /// <summary>The artist named <paramref name="name"/> among <paramref name="mbids"/> (the first when null), with its Wikidata link and genres.</summary>
    private async Task<(string? Mbid, string? Wikidata, IReadOnlyList<string> Genres)> PickArtistAsync(
        ConnectorConfig config, IReadOnlyList<string> mbids, string? name, CancellationToken ct)
    {
        using var document = await GetApiAsync(config, $"metadata/artist/?artist_mbids={string.Join(',', mbids)}&inc=artist%20tag", ct);
        var root = document?.RootElement ?? default;
        var artists = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToList(),
            JsonValueKind.Object => root.EnumerateObject().Select(p => p.Value).ToList(),
            _ => [],
        };
        foreach (var artist in artists)
        {
            var id = MetadataText.Mbid(JsonValues.GetString(artist, "artist_mbid") ?? JsonValues.GetString(artist, "mbid"));
            if (id is null || (name is not null && !NameMatch.Same(JsonValues.GetString(artist, "name"), name))) continue;
            return (id, JsonValues.GetString(JsonValues.Child(artist, "rels"), "wikidata"), Genres(JsonValues.Child(JsonValues.Child(artist, "tag"), "artist")));
        }

        return (null, null, []);
    }

    /// <summary>The Wikipedia introduction and Commons picture linked from an artist's Wikidata item.</summary>
    private async Task<(Biography? Bio, ImageData? Image)> FromWikidataAsync(string wikidataUrl, ArtistQuery query, CancellationToken ct)
    {
        var id = wikidataUrl.TrimEnd('/').Split('/')[^1];
        if (id.Length < 2 || id[0] != 'Q' || !id[1..].All(char.IsAsciiDigit)) return (null, null);

        string? article, picture;
        var url = $"https://www.wikidata.org/w/api.php?action=wbgetentities&ids={id}&props=claims%7Csitelinks&sitefilter=enwiki&format=json";
        using (var document = await GetWikimediaJsonAsync(url, "Wikidata", ct))
        {
            // A merged item comes back under its new id, so take whichever entity is there.
            var entities = JsonValues.Child(document?.RootElement ?? default, "entities");
            var entity = entities.ValueKind == JsonValueKind.Object ? entities.EnumerateObject().Select(p => p.Value).FirstOrDefault() : default;
            article = JsonValues.GetString(JsonValues.Child(JsonValues.Child(entity, "sitelinks"), "enwiki"), "title");
            var images = JsonValues.Child(JsonValues.Child(entity, "claims"), "P18"); // P18: image
            var first = images.ValueKind == JsonValueKind.Array ? images.EnumerateArray().FirstOrDefault() : default;
            picture = JsonValues.GetString(JsonValues.Child(JsonValues.Child(first, "mainsnak"), "datavalue"), "value");
        }

        Biography? bio = null;
        if (query.NeedBio && !string.IsNullOrWhiteSpace(article))
        {
            var extract = "https://en.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&prop=extracts%7Cinfo&inprop=url"
                          + $"&exintro=1&explaintext=1&redirects=1&titles={Esc(article)}";
            using var document = await GetWikimediaJsonAsync(extract, "Wikipedia", ct);
            var pages = JsonValues.Child(JsonValues.Child(document?.RootElement ?? default, "query"), "pages");
            var page = pages.ValueKind == JsonValueKind.Array ? pages.EnumerateArray().FirstOrDefault() : default;
            if (MetadataText.CleanBio(JsonValues.GetString(page, "extract"), html: false) is { } text)
                bio = new Biography(text, "Wikipedia", MetadataText.HttpUrl(JsonValues.GetString(page, "fullurl")));
        }

        ImageData? image = null;
        if (query.NeedImage && !string.IsNullOrWhiteSpace(picture))
        {
            var file = $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(picture.Replace(' ', '_'))}?width=600";
            using var request = new HttpRequestMessage(HttpMethod.Get, file);
            using (await WikimediaGate.EnterAsync(ct))
                image = await MetadataHttp.GetImageAsync(Http, request, "Wikimedia Commons", ct);
        }

        return (bio, image);
    }

    private async Task<JsonDocument?> GetWikimediaJsonAsync(string url, string source, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using (await WikimediaGate.EnterAsync(ct))
            return await MetadataHttp.GetJsonAsync(Http, request, source, ct);
    }

    /// <summary>
    /// ListenBrainz names the release whose front cover represents the album (possibly another edition) as
    /// caa_release_mbid + caa_id. Where this sits in a lookup response is not documented, so check both places.
    /// </summary>
    private static string? CoverArtPath(JsonElement root)
    {
        foreach (var release in new[] { JsonValues.Child(JsonValues.Child(root, "metadata"), "release"), JsonValues.Child(root, "release") })
        {
            var caaRelease = MetadataText.Mbid(JsonValues.GetString(release, "caa_release_mbid"));
            if (caaRelease is not null
                && long.TryParse(JsonValues.GetString(release, "caa_id"), NumberStyles.None, CultureInfo.InvariantCulture, out var caaId) && caaId > 0)
                return $"release/{caaRelease}/{caaId}-500.jpg";
        }

        return null;
    }

    private async Task<ImageData?> CoverAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://coverartarchive.org/" + path);
        using (await CoverArtGate.EnterAsync(ct))
            return await MetadataHttp.GetImageAsync(Http, request, "Cover Art Archive", ct);
    }

    private async Task<JsonDocument?> GetApiAsync(ConnectorConfig config, string pathAndQuery, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + pathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", config.Secret);
        using (await ApiGate.EnterAsync(ct))
            return await MetadataHttp.GetJsonAsync(Http, request, Source, ct);
    }

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private HttpClient Http => httpFactory.CreateClient(MetadataHttp.ClientName);
}
