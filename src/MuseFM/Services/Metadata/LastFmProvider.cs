using System.Text.Json;
using MuseFM.Data.Entities;
using MuseFM.Services.Import;

namespace MuseFM.Services.Metadata;

/// <summary>
/// Artist biographies and tags, album covers and track lengths from Last.fm (artist.getInfo, album.getInfo).
/// Last.fm stopped providing artist photos in 2019, so it supplies none. Its tags are whatever listeners typed, so they
/// are only roughly genres; an album's tags and release date are too unreliable to use.
/// </summary>
public sealed class LastFmProvider(LastFmClient lastFm, IHttpClientFactory httpFactory, ConnectorSettings settings) : IMetadataProvider
{
    private const string Source = "Last.fm";

    // The grey star Last.fm returns in place of every artist photo.
    private const string PlaceholderImage = "2a96cbd8b46e442fc41c2b86b821562f";

    // Last.fm asks for no more than 5 requests per second per key; the history import shares the key.
    private static readonly RateGate Gate = new(TimeSpan.FromMilliseconds(250));

    public ConnectorKind Kind => ConnectorKind.LastFm;

    public async Task<string> TestAsync(ConnectorConfig config, CancellationToken ct)
    {
        using var _ = await CallAsync(config, "artist.getInfo", [("artist", "Radiohead")], ct);
        return string.IsNullOrEmpty(config.Secret) ? "The API key from MuseFM__LastFmApiKey works." : "The API key works.";
    }

    public async Task<ArtistLookup?> FindArtistAsync(ConnectorConfig config, ArtistQuery query, CancellationToken ct)
    {
        // Artist photos are only placeholders, so a biography and tags are the only reasons to ask.
        if (!query.NeedBio && !query.NeedGenres) return null;

        using var document = await CallAsync(config, "artist.getInfo", [("artist", query.Name)], ct);
        var artist = JsonValues.Child(document?.RootElement ?? default, "artist");
        if (artist.ValueKind != JsonValueKind.Object) return null;

        var bio = JsonValues.Child(artist, "bio");
        var text = MetadataText.CleanBio(JsonValues.GetString(bio, "content")) ?? MetadataText.CleanBio(JsonValues.GetString(bio, "summary"));
        var url = MetadataText.HttpUrl(JsonValues.GetString(artist, "url"));
        return new ArtistLookup(
            MetadataText.Mbid(JsonValues.GetString(artist, "mbid")),
            text is null ? null : new Biography(text, Source, url),
            null,
            Genres: GenreNames.Clean(Items(JsonValues.Child(artist, "tags"), "tag").Select(t => JsonValues.GetString(t, "name")), titleCase: true));
    }

    public async Task<AlbumLookup?> FindAlbumAsync(ConnectorConfig config, AlbumQuery query, CancellationToken ct)
    {
        foreach (var artist in query.Artists.Take(2))
        {
            string? imageUrl, mbid;
            List<TrackLength> tracks;
            using (var document = await CallAsync(config, "album.getInfo", [("artist", artist), ("album", query.Title)], ct))
            {
                var album = JsonValues.Child(document?.RootElement ?? default, "album");
                if (album.ValueKind != JsonValueKind.Object) continue;
                imageUrl = LargestImage(album);
                mbid = MetadataText.Mbid(JsonValues.GetString(album, "mbid"));
                // Durations are in seconds; Last.fm leaves them empty or zero for tracks it has none for.
                tracks = Items(JsonValues.Child(album, "tracks"), "track")
                    .Select(t => (Title: JsonValues.GetString(t, "name"), Seconds: JsonValues.GetSeconds(t, "duration")))
                    .Where(t => !string.IsNullOrWhiteSpace(t.Title) && t.Seconds is not null)
                    .Select(t => new TrackLength(t.Title!, t.Seconds!.Value))
                    .ToList();
            }

            ImageData? image = null;
            if (query.NeedImage && imageUrl is not null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
                image = await MetadataHttp.GetImageAsync(httpFactory.CreateClient(MetadataHttp.ClientName), request, Source, ct);
            }

            return new AlbumLookup(mbid, image, Tracks: tracks);
        }

        return null;
    }

    /// <summary>The entries of a Last.fm list, which is an array, a single object when there is one entry, or text when empty.</summary>
    private static IReadOnlyList<JsonElement> Items(JsonElement list, string name) => JsonValues.Child(list, name) switch
    {
        { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().ToList(),
        { ValueKind: JsonValueKind.Object } single => [single],
        _ => [],
    };

    /// <summary>The last (largest) entry of Last.fm's image array that is a real picture.</summary>
    private static string? LargestImage(JsonElement entity)
    {
        if (JsonValues.Child(entity, "image") is not { ValueKind: JsonValueKind.Array } images) return null;
        string? best = null;
        foreach (var image in images.EnumerateArray())
        {
            if (MetadataText.HttpUrl(JsonValues.GetString(image, "#text")) is { } url && !url.Contains(PlaceholderImage, StringComparison.Ordinal))
                best = url;
        }

        return best;
    }

    /// <returns>The response, or null when Last.fm doesn't know the artist or album.</returns>
    private async Task<JsonDocument?> CallAsync(ConnectorConfig config, string method, IEnumerable<(string, string)> args, CancellationToken ct)
    {
        var key = settings.LastFmKey(config) ?? throw new MetadataSourceException("Last.fm: no API key is configured.");
        try
        {
            using (await Gate.EnterAsync(ct))
                return await lastFm.CallAsync(key, method, args, ct);
        }
        catch (LastFmException ex) when (ex.Code == 6)
        {
            return null; // "The artist/album you supplied could not be found"
        }
        catch (LastFmException ex)
        {
            throw new MetadataSourceException(ex.Code is 10 or 26 ? "Last.fm: the API key is invalid or suspended." : ex.Message, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new MetadataSourceException("Last.fm could not be reached.", ex);
        }
    }
}
