namespace MuseFM.Services;

/// <summary>An artist of a track that is playing; <paramref name="Id"/> is null when the listener has no page for them.</summary>
public sealed record PlayingArtist(string Name, int? Id);

/// <summary>
/// What a user is playing, with where it leads in their statistics. Their pages only exist for what they have
/// scrobbled, so <paramref name="TrackId"/>, <paramref name="AlbumId"/> and the artists' ids are null for anything
/// they hear for the first time, until its scrobble arrives.
/// </summary>
/// <param name="CoverUrl">Artwork for the album, or for the track's usual album; null when the catalog knows neither.</param>
public sealed record PlayingTrack(
    string UserName, NowPlaying Playing, int? TrackId, IReadOnlyList<PlayingArtist> Artists, int? AlbumId, string? CoverUrl);

/// <summary>Looks up what is playing (see <see cref="NowPlayingStore"/>) in the catalog and the listener's history.</summary>
public sealed class NowPlayingService(NowPlayingStore store, ScrobbleService scrobbles, StatsService stats)
{
    public async Task<PlayingTrack?> GetAsync(string userId, string userName, CancellationToken ct = default)
    {
        if (store.Get(userId) is not { } playing) return null;
        var match = await scrobbles.FindAsync(playing.Input, ct);

        async Task<int?> Listened(int? id, Func<int, StatSubject> subject) =>
            id is { } known && await stats.HasListenedAsync(userId, subject(known), ct) ? known : null;

        var artists = new List<PlayingArtist>(playing.Artists.Count);
        for (var i = 0; i < playing.Artists.Count; i++)
            artists.Add(new PlayingArtist(playing.Artists[i], await Listened(match?.ArtistIds.ElementAtOrDefault(i), StatSubject.Artist)));

        // Artwork belongs to the shared catalog, so it shows even when this listener is new to the album.
        var cover = match switch
        {
            { AlbumId: { } albumId } => $"/media/album/{albumId}",
            { TrackId: { } trackId } => $"/media/track/{trackId}",
            _ => null,
        };
        return new PlayingTrack(
            userName, playing, await Listened(match?.TrackId, StatSubject.Track), artists, await Listened(match?.AlbumId, StatSubject.Album), cover);
    }
}
