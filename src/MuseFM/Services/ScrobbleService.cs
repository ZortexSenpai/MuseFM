using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services.Metadata;

namespace MuseFM.Services;

public enum ScrobbleOutcome
{
    Accepted,
    Duplicate,
    Invalid,
}

/// <summary>A scrobble as received from an API or an import file, before normalization.</summary>
public sealed record ScrobbleInput
{
    public required string Title { get; init; }
    public required IReadOnlyList<string> Artists { get; init; }
    public string? Album { get; init; }
    public IReadOnlyList<string>? AlbumArtists { get; init; }
    public required long ListenedAt { get; init; }
    public int? LengthSeconds { get; init; }
    public int? DurationSeconds { get; init; }
    public string? Origin { get; init; }

    /// <summary>MusicBrainz recording id.</summary>
    public string? TrackMbid { get; init; }

    /// <summary>MusicBrainz release id.</summary>
    public string? AlbumMbid { get; init; }

    /// <summary>MusicBrainz artist ids, in the order of <see cref="Artists"/>.</summary>
    public IReadOnlyList<string>? ArtistMbids { get; init; }
}

/// <summary>What the catalog knows of a track that was not scrobbled (yet); null for what it has never seen.</summary>
/// <param name="ArtistIds">One per artist, in the order <see cref="MetadataParser.Parse"/> gives them.</param>
public sealed record CatalogMatch(int? TrackId, IReadOnlyList<int?> ArtistIds, int? AlbumId);

/// <summary>A track to find in, or add to, the catalog without scrobbling it (loved tracks).</summary>
public sealed record TrackRef(string Title, IReadOnlyList<string> Artists, string? MusicBrainzId = null);

/// <summary>
/// The single write path for scrobbles. Normalizes metadata, resolves or creates artists/tracks/albums
/// and skips scrobbles whose timestamp the user already has.
/// </summary>
public sealed class ScrobbleService(AppDbContext db, ScrobbleMetadataFetcher metadataFetcher)
{
    /// <summary>
    /// Serializes catalog writes. SQLite has a single writer anyway; this avoids unique-key races when creating catalog
    /// rows, and keeps catalog edits from removing rows an ingest is about to use.
    /// </summary>
    internal static readonly SemaphoreSlim WriteGate = new(1, 1);

    private const int ChunkSize = 500;
    private const int MaxSeconds = 24 * 60 * 60;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromDays(1);

    public async Task<ScrobbleOutcome> IngestAsync(string userId, ScrobbleInput input, CancellationToken ct = default) =>
        (await IngestAsync(userId, [input], ct))[0];

    /// <param name="fetchMetadata">
    /// Look up covers, photos and biographies of artists and albums that were never looked up, in the background.
    /// Off for history imports, which would queue thousands of lookups; those are left to the admin's "Fetch missing".
    /// </param>
    /// <returns>One outcome per input, in input order.</returns>
    public async Task<IReadOnlyList<ScrobbleOutcome>> IngestAsync(
        string userId, IReadOnlyList<ScrobbleInput> inputs, CancellationToken ct = default, bool fetchMetadata = true)
    {
        var outcomes = new ScrobbleOutcome[inputs.Count];
        var latestAllowed = DateTimeOffset.UtcNow.Add(MaxFutureSkew).ToUnixTimeSeconds();
        var prepared = new List<Prepared>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            var p = Prepare(i, inputs[i], latestAllowed);
            if (p is null) outcomes[i] = ScrobbleOutcome.Invalid;
            else prepared.Add(p);
        }

        if (prepared.Count == 0) return outcomes;

        await WriteGate.WaitAsync(ct);
        try
        {
            foreach (var chunk in prepared.Chunk(ChunkSize))
                await IngestChunkAsync(userId, chunk, outcomes, fetchMetadata, ct);
        }
        finally
        {
            WriteGate.Release();
        }

        return outcomes;
    }

    /// <summary>
    /// Finds tracks the way scrobbles are matched, for feedback that names a track rather than scrobbling it.
    /// </summary>
    /// <param name="create">Add tracks (and artists) the catalog doesn't have yet.</param>
    /// <returns>One track id per reference, in order; null when it is unknown (and not created) or has no title or artist.</returns>
    public async Task<IReadOnlyList<int?>> ResolveTracksAsync(IReadOnlyList<TrackRef> refs, bool create, CancellationToken ct = default)
    {
        var specs = refs.Select(r => PrepareTrack(r.Title, r.Artists, null, r.MusicBrainzId)).ToList();
        var valid = specs.Select((spec, i) => (spec, i)).Where(x => x.spec is not null).ToList();
        var ids = new int?[refs.Count];
        if (valid.Count == 0) return ids;

        await WriteGate.WaitAsync(ct);
        try
        {
            var artists = await ArtistsAsync(valid.SelectMany(x => x.spec!.Artists), create, ct);
            var tracks = await TracksAsync(valid.Select(x => (x.i, x.spec!)).ToList(), artists, create, ct);
            ForgetUnusedArtists();
            if (create) await db.SaveChangesAsync(ct);
            foreach (var (index, track) in tracks) ids[index] = track.Id;
            return ids;
        }
        finally
        {
            db.ChangeTracker.Clear();
            WriteGate.Release();
        }
    }

    /// <summary>
    /// Finds a track, its artists and its album the way a scrobble of it would be matched, without adding anything: for
    /// what is playing now. Null without a title or an artist.
    /// </summary>
    public async Task<CatalogMatch?> FindAsync(ScrobbleInput input, CancellationToken ct = default)
    {
        if (Normalize(0, input) is not { } p) return null;
        var artists = await ArtistsAsync(p.Track.Artists.Concat(p.AlbumArtists), create: false, ct);
        var tracks = await TracksAsync([(p.Index, p.Track)], artists, create: false, ct);
        var albums = await AlbumsAsync([p], artists, create: false, ct);
        return new CatalogMatch(
            tracks.GetValueOrDefault(p.Index)?.Id,
            p.Track.Artists.Select(n => artists.TryGetValue(n.Key, out var artist) ? artist.Id : (int?)null).ToList(),
            albums.GetValueOrDefault(p.Index)?.Id);
    }

    private sealed record Name(string Display, string Key, string? Mbid = null);

    /// <param name="TitleKey">Matching key of the title; tracks with this key are candidates.</param>
    private sealed record TrackSpec(string Title, string TitleKey, IReadOnlyList<Name> Artists, string? Mbid);

    /// <param name="AlbumKey">Matching key of the album title.</param>
    private sealed record Prepared(
        int Index,
        TrackSpec Track,
        string? Album,
        string? AlbumKey,
        IReadOnlyList<Name> AlbumArtists,
        string? AlbumMbid,
        long ListenedAt,
        int? LengthSeconds,
        int? DurationSeconds,
        string? Origin);

    private static Prepared? Prepare(int index, ScrobbleInput input, long latestAllowed) =>
        input.ListenedAt <= 0 || input.ListenedAt > latestAllowed ? null : Normalize(index, input);

    /// <summary>The input's names cleaned up, with their matching keys; null without a title or an artist.</summary>
    private static Prepared? Normalize(int index, ScrobbleInput input)
    {
        if (PrepareTrack(input.Title, input.Artists, input.ArtistMbids, input.TrackMbid) is not { } track) return null;

        string? album = TextKey.Clean(input.Album) is { Length: > 0 } a ? a : null;
        string? albumKey = null;
        IReadOnlyList<Name> albumArtists = [];
        if (album is not null)
        {
            albumArtists = Distinct(input.AlbumArtists ?? []);
            // Without an album artist the album belongs to the main artist, not to whoever is featured on this track.
            if (albumArtists.Count == 0) albumArtists = [track.Artists[0]];
            albumKey = TextKey.AlbumKey(album);
        }

        var origin = TextKey.Clean(input.Origin);
        return new Prepared(
            index, track, album, albumKey, albumArtists, album is null ? null : MetadataText.Mbid(input.AlbumMbid), input.ListenedAt,
            Seconds(input.LengthSeconds), Seconds(input.DurationSeconds),
            origin.Length == 0 ? null : origin[..Math.Min(origin.Length, 128)]);
    }

    private static TrackSpec? PrepareTrack(string title, IReadOnlyList<string> artists, IReadOnlyList<string>? artistMbids, string? trackMbid)
    {
        var (cleanTitle, names) = MetadataParser.Parse(title, artists);
        if (cleanTitle.Length == 0 || names.Count == 0) return null;
        var mbids = ArtistMbids(artists, names, artistMbids);
        return new TrackSpec(
            cleanTitle, TextKey.TitleKey(cleanTitle),
            names.Select((name, i) => new Name(name, TextKey.ArtistKey(name), mbids[i])).ToList(),
            MetadataText.Mbid(trackMbid));
    }

    /// <summary>
    /// Pairs artist MBIDs with the parsed artist names, only where that is unambiguous: one id per name sent, with the
    /// name unchanged by parsing; or a single id, which belongs to the main artist.
    /// </summary>
    private static string?[] ArtistMbids(IReadOnlyList<string> sent, IReadOnlyList<string> parsed, IReadOnlyList<string>? mbids)
    {
        var result = new string?[parsed.Count];
        var ids = (mbids ?? []).Select(MetadataText.Mbid).ToList();
        if (ids.Count == 0 || ids.Contains(null)) return result;
        if (ids.Count == sent.Count)
        {
            for (var i = 0; i < Math.Min(sent.Count, parsed.Count); i++)
                if (TextKey.ArtistKey(TextKey.Clean(sent[i])) == TextKey.ArtistKey(parsed[i])) result[i] = ids[i];
        }

        if (ids.Count == 1) result[0] ??= ids[0];
        return result;
    }

    private static Name ToName(string cleaned) => new(cleaned, TextKey.ArtistKey(cleaned));

    /// <summary>The catalog's spelling of an artist, which differs from the scrobbled one when that is an old name (an alias).</summary>
    private static string CatalogName(Name name, Dictionary<string, Artist> artists) =>
        artists.TryGetValue(name.Key, out var artist) ? artist.Name : name.Display;

    private static IEnumerable<string> Lines(string? text) => (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static List<Name> Distinct(IEnumerable<string> names) =>
        names.Select(TextKey.Clean).Where(n => n.Length > 0).Select(ToName).DistinctBy(n => n.Key).ToList();

    private static int? Seconds(int? value) => value is > 0 and <= MaxSeconds ? value : null;

    private async Task IngestChunkAsync(string userId, Prepared[] chunk, ScrobbleOutcome[] outcomes, bool fetchMetadata, CancellationToken ct)
    {
        var times = chunk.Select(p => p.ListenedAt).Distinct().ToList();
        var taken = (await db.Scrobbles
                .Where(s => s.UserId == userId && times.Contains(s.ListenedAt))
                .Select(s => s.ListenedAt)
                .ToListAsync(ct))
            .ToHashSet();

        var toInsert = new List<Prepared>();
        foreach (var p in chunk)
        {
            if (taken.Add(p.ListenedAt))
            {
                outcomes[p.Index] = ScrobbleOutcome.Accepted;
                toInsert.Add(p);
            }
            else
            {
                outcomes[p.Index] = ScrobbleOutcome.Duplicate;
            }
        }

        if (toInsert.Count == 0) return;

        var artists = await ArtistsAsync(toInsert.SelectMany(p => p.Track.Artists.Concat(p.AlbumArtists)), create: true, ct);
        var trackFor = await TracksAsync(toInsert.Select(p => (p.Index, p.Track)).ToList(), artists, create: true, ct);
        foreach (var p in toInsert)
            trackFor[p.Index].LengthSeconds ??= p.LengthSeconds;
        var albumFor = await AlbumsAsync(toInsert, artists, create: true, ct);
        ForgetUnusedArtists();

        foreach (var p in toInsert)
        {
            db.Scrobbles.Add(new Scrobble
            {
                UserId = userId,
                Track = trackFor[p.Index],
                Album = albumFor.GetValueOrDefault(p.Index),
                ListenedAt = p.ListenedAt,
                DurationSeconds = p.DurationSeconds,
                Origin = p.Origin,
            });
        }

        await db.SaveChangesAsync(ct);
        if (fetchMetadata)
        {
            metadataFetcher.Enqueue(
                toInsert.SelectMany(p => trackFor[p.Index].Artists).Select(a => a.ArtistId)
                    .Concat(albumFor.Values.SelectMany(a => a.Artists).Select(a => a.ArtistId)),
                albumFor.Values.Select(a => a.Id));
        }

        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// New artists end up unused when their name only led to an alias (a track or album an admin moved elsewhere): don't
    /// add them.
    /// </summary>
    private void ForgetUnusedArtists()
    {
        var credited = db.ChangeTracker.Entries<TrackArtist>().Select(e => e.Entity.Artist)
            .Concat(db.ChangeTracker.Entries<AlbumArtist>().Select(e => e.Entity.Artist))
            .ToHashSet();
        foreach (var entry in db.ChangeTracker.Entries<Artist>().Where(e => e.State == EntityState.Added).ToList())
        {
            if (!credited.Contains(entry.Entity)) entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Catalog artists by matching key, also through the old names admins renamed or merged away. Missing ones are added
    /// when <paramref name="create"/> is set.
    /// </summary>
    private async Task<Dictionary<string, Artist>> ArtistsAsync(IEnumerable<Name> names, bool create, CancellationToken ct)
    {
        var all = names.ToList();
        var keys = all.Select(a => a.Key).Distinct().ToList();
        var artists = await db.Artists.Where(a => keys.Contains(a.Key)).ToDictionaryAsync(a => a.Key, ct);
        var unknown = keys.Where(k => !artists.ContainsKey(k)).ToList();
        if (unknown.Count > 0)
        {
            foreach (var alias in await db.CatalogAliases.Where(a => a.ArtistId != null && unknown.Contains(a.Key)).Select(a => new { a.Key, a.Artist }).ToListAsync(ct))
                artists.TryAdd(alias.Key, alias.Artist!);
        }

        foreach (var name in all)
        {
            if (!artists.TryGetValue(name.Key, out var artist))
            {
                if (!create) continue;
                artists[name.Key] = artist = new Artist { Name = name.Display, Key = name.Key };
                db.Artists.Add(artist);
            }

            if (create) artist.MusicBrainzId ??= name.Mbid;
        }

        return artists;
    }

    /// <summary>
    /// Tracks: same title key and a matching credit. With <paramref name="create"/>, unknown tracks are added and a newly
    /// seen featured artist joins the track's credits; without it, unknown tracks are left out of the result.
    /// Credits compare by the catalog's names, so an artist's old name (an alias) matches their tracks.
    /// </summary>
    private async Task<Dictionary<int, Track>> TracksAsync(
        IReadOnlyList<(int Index, TrackSpec Spec)> items, Dictionary<string, Artist> artists, bool create, CancellationToken ct)
    {
        var titleKeys = items.Select(x => x.Spec.TitleKey).Distinct().ToList();
        // Old titles admins renamed or merged away come first: they are explicit decisions.
        var aliases = await db.CatalogAliases.AsNoTracking()
            .Where(a => a.TrackId != null && titleKeys.Contains(a.Key))
            .OrderBy(a => a.Id)
            .Select(a => new { a.Key, a.Credit, TrackId = a.TrackId!.Value })
            .ToListAsync(ct);
        var aliasIds = aliases.Select(a => a.TrackId).ToList();
        var knownTracks = await db.Tracks
            .Include(t => t.Artists).ThenInclude(ta => ta.Artist)
            .Where(t => titleKeys.Contains(t.Key) || aliasIds.Contains(t.Id))
            .OrderBy(t => t.Id)
            .ToListAsync(ct);
        var byId = knownTracks.ToDictionary(t => t.Id);
        var trackCandidates = aliases
            .Where(a => byId.ContainsKey(a.TrackId))
            .Select(a => (a.Key, Track: byId[a.TrackId], Credit: new ArtistCredit(Lines(a.Credit)), Alias: true))
            .Concat(knownTracks.Where(t => titleKeys.Contains(t.Key))
                .Select(t => (t.Key, Track: t, Credit: new ArtistCredit(t.Artists.OrderBy(a => a.Position).Select(a => a.Artist.Name)), Alias: false)))
            .GroupBy(c => c.Key)
            .ToDictionary(g => g.Key, g => g.Select(c => (c.Track, c.Credit, c.Alias)).ToList());

        var trackFor = new Dictionary<int, Track>();
        foreach (var (index, spec) in items)
        {
            var credit = new ArtistCredit(spec.Artists.Select(n => CatalogName(n, artists)));
            if (!trackCandidates.TryGetValue(spec.TitleKey, out var candidates))
                trackCandidates[spec.TitleKey] = candidates = [];
            var match = candidates.FirstOrDefault(c => c.Credit.Matches(credit));
            if (match.Track is { } track)
            {
                if (create) track.MusicBrainzId ??= spec.Mbid;
                // An alias maps exactly this credit to the track; its artists don't join the track's credits.
                if (create && !match.Alias)
                {
                    foreach (var name in spec.Artists)
                    {
                        // Compared by entity: an old name can stand for an artist the track already credits.
                        var artist = artists[name.Key];
                        if (track.Artists.Any(ta => ta.Artist == artist)) continue;
                        track.Artists.Add(new TrackArtist { Artist = artist, Position = track.Artists.Count });
                        match.Credit.Add(artist.Name);
                    }
                }
            }
            else if (create)
            {
                track = new Track { Title = spec.Title, Key = spec.TitleKey, MusicBrainzId = spec.Mbid };
                foreach (var artist in spec.Artists.Select(n => artists[n.Key]).Distinct())
                    track.Artists.Add(new TrackArtist { Artist = artist, Position = track.Artists.Count });
                candidates.Add((track, credit, false));
                db.Tracks.Add(track);
            }
            else
            {
                continue;
            }

            trackFor[index] = track;
        }

        return trackFor;
    }

    /// <summary>
    /// Albums: same title key (editions folded) and a matching album-artist credit. The first credit stays. With
    /// <paramref name="create"/>, unknown albums are added; without it they are left out of the result.
    /// </summary>
    private async Task<Dictionary<int, Album>> AlbumsAsync(
        IReadOnlyList<Prepared> items, Dictionary<string, Artist> artists, bool create, CancellationToken ct)
    {
        var albumKeys = items.Where(p => p.AlbumKey is not null).Select(p => p.AlbumKey!).Distinct().ToList();
        if (albumKeys.Count == 0) return [];
        var aliases = await db.CatalogAliases.AsNoTracking()
            .Where(a => a.AlbumId != null && albumKeys.Contains(a.Key))
            .OrderBy(a => a.Id)
            .Select(a => new { a.Key, a.Credit, AlbumId = a.AlbumId!.Value })
            .ToListAsync(ct);
        var aliasIds = aliases.Select(a => a.AlbumId).ToList();
        var knownAlbums = await db.Albums
            .Include(a => a.Artists).ThenInclude(aa => aa.Artist)
            .Where(a => albumKeys.Contains(a.Key) || aliasIds.Contains(a.Id))
            .OrderBy(a => a.Id)
            .ToListAsync(ct);
        var byId = knownAlbums.ToDictionary(a => a.Id);
        var albumCandidates = aliases
            .Where(a => byId.ContainsKey(a.AlbumId))
            .Select(a => (a.Key, Album: byId[a.AlbumId], Credit: new ArtistCredit(Lines(a.Credit))))
            .Concat(knownAlbums.Where(a => albumKeys.Contains(a.Key))
                .Select(a => (a.Key, Album: a, Credit: new ArtistCredit(a.Artists.OrderBy(x => x.Position).Select(x => x.Artist.Name)))))
            .GroupBy(c => c.Key)
            .ToDictionary(g => g.Key, g => g.Select(c => (c.Album, c.Credit)).ToList());

        var albumFor = new Dictionary<int, Album>();
        foreach (var p in items.Where(p => p.AlbumKey is not null))
        {
            var credit = new ArtistCredit(p.AlbumArtists.Select(n => CatalogName(n, artists)));
            if (!albumCandidates.TryGetValue(p.AlbumKey!, out var candidates))
                albumCandidates[p.AlbumKey!] = candidates = [];
            var album = candidates.FirstOrDefault(c => c.Credit.Matches(credit)).Album;
            if (album is null)
            {
                if (!create) continue;
                album = new Album { Title = p.Album!, Key = p.AlbumKey! };
                foreach (var artist in p.AlbumArtists.Select(n => artists[n.Key]).Distinct())
                    album.Artists.Add(new AlbumArtist { Artist = artist, Position = album.Artists.Count });
                candidates.Add((album, credit));
                db.Albums.Add(album);
            }

            if (create) album.MusicBrainzId ??= p.AlbumMbid;
            albumFor[p.Index] = album;
        }

        return albumFor;
    }
}
