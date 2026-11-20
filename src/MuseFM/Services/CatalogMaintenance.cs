using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;

namespace MuseFM.Services;

/// <summary>
/// Brings an existing catalog up to the current matching rules: re-keys artists, tracks and albums, re-reads titles for
/// featured artists, and merges rows that now count as the same, moving their scrobbles to the surviving row.
/// Runs once per catalog version at startup, in a single transaction.
/// </summary>
public static class CatalogMaintenance
{
    /// <summary>1: exact title + artist-set keys. 2: matching keys and featured-artist-aware credits.</summary>
    public const int Version = 2;

    private const string VersionName = "CatalogVersion";

    public sealed record Merged(int Artists, int Tracks, int Albums);

    private sealed record Link(int OwnerId, int ArtistId, int Position);

    private sealed class Candidate<T>(T item, ArtistCredit credit)
    {
        public T Item { get; } = item;
        public ArtistCredit Credit { get; set; } = credit;
    }

    public static async Task UpgradeAsync(AppDbContext db, DataPaths paths, ILogger logger, CancellationToken ct = default)
    {
        var state = await db.AppState.FirstOrDefaultAsync(s => s.Name == VersionName, ct);
        var version = int.TryParse(state?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 1;
        if (version >= Version) return;

        // Start from a clean slate: rows are rewritten with raw SQL below, so nothing tracked may survive into it.
        db.ChangeTracker.Clear();
        var rekeyed = false;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            if (await db.Artists.AnyAsync(ct))
            {
                logger.LogInformation("Upgrading the music catalog to version {Version}, this can take a moment for large libraries", Version);
                var merged = await RekeyAsync(db, ct);
                logger.LogInformation("Catalog upgraded: merged {Artists} artists, {Tracks} tracks and {Albums} albums",
                    merged.Artists, merged.Tracks, merged.Albums);
                rekeyed = true;
            }

            db.ChangeTracker.Clear();
            state = await db.AppState.FirstOrDefaultAsync(s => s.Name == VersionName, ct);
            if (state is null) db.AppState.Add(state = new AppState { Name = VersionName, Value = "" });
            state.Value = Version.ToString(CultureInfo.InvariantCulture);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        if (rekeyed) await DeleteUnusedImagesAsync(db, paths, logger, ct);
    }

    private static async Task<Merged> RekeyAsync(AppDbContext db, CancellationToken ct)
    {
        // Artist keys are unique: park them on placeholders while rows merge, and set the new keys at the end.
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Artists\" SET \"Key\" = '~' || \"Id\"", ct);

        // Artists: one per matching key; the oldest row keeps its id and spelling.
        var byKey = new Dictionary<string, Artist>();
        var survivorOf = new Dictionary<int, Artist>();
        foreach (var artist in await db.Artists.OrderBy(a => a.Id).ToListAsync(ct))
        {
            var key = TextKey.ArtistKey(artist.Name);
            if (!byKey.TryGetValue(key, out var survivor)) byKey[key] = survivor = artist;
            else survivor.MusicBrainzId ??= artist.MusicBrainzId;
            survivorOf[artist.Id] = survivor;
        }

        Artist ArtistNamed(string name)
        {
            var key = TextKey.ArtistKey(name);
            if (byKey.TryGetValue(key, out var artist)) return artist;
            artist = new Artist { Name = name, Key = "~new-" + Guid.NewGuid().ToString("N") };
            db.Artists.Add(artist);
            return byKey[key] = artist;
        }

        // Tracks: re-read titles with the current featuring patterns, then merge on title key + matching credit.
        var trackLinks = await LinksAsync(db.TrackArtists.Select(x => new Link(x.TrackId, x.ArtistId, x.Position)), ct);
        var trackCredits = new Dictionary<Track, List<Artist>>();
        var trackCandidates = new Dictionary<string, List<Candidate<Track>>>();
        var trackMap = new Dictionary<int, int>();
        foreach (var track in await db.Tracks.OrderBy(t => t.Id).ToListAsync(ct))
        {
            var names = Credited(trackLinks, track.Id, survivorOf).Select(a => a.Name).ToList();
            var (title, parsed) = MetadataParser.Parse(track.Title, names);
            if (title.Length == 0 || parsed.Count == 0) (title, parsed) = (track.Title, names);
            var credited = parsed.Select(ArtistNamed).Distinct().ToList();
            var credit = new ArtistCredit(credited.Select(a => a.Name));
            var key = TextKey.TitleKey(title);
            if (!trackCandidates.TryGetValue(key, out var candidates)) trackCandidates[key] = candidates = [];

            var match = candidates.FirstOrDefault(c => c.Credit.Matches(credit));
            if (match is null)
            {
                track.Title = title;
                track.Key = key;
                trackCredits[track] = credited;
                candidates.Add(new Candidate<Track>(track, credit));
                continue;
            }

            trackMap[track.Id] = match.Item.Id;
            match.Item.LengthSeconds ??= track.LengthSeconds;
            match.Item.MusicBrainzId ??= track.MusicBrainzId;
            var kept = trackCredits[match.Item];
            foreach (var artist in credited.Where(a => !kept.Contains(a)))
            {
                kept.Add(artist);
                match.Credit.Add(artist.Name);
            }
        }

        // Albums: merge on title key (editions folded) + matching credit. When one credit is part of the other the
        // shorter one wins, which undoes albums that were credited to a track's featured artists.
        var albumLinks = await LinksAsync(db.AlbumArtists.Select(x => new Link(x.AlbumId, x.ArtistId, x.Position)), ct);
        var albumCredits = new Dictionary<Album, List<Artist>>();
        var albumCandidates = new Dictionary<string, List<Candidate<Album>>>();
        var albumMap = new Dictionary<int, int>();
        foreach (var album in await db.Albums.OrderBy(a => a.Id).ToListAsync(ct))
        {
            var credited = Credited(albumLinks, album.Id, survivorOf);
            var credit = new ArtistCredit(credited.Select(a => a.Name));
            var key = TextKey.AlbumKey(album.Title);
            if (!albumCandidates.TryGetValue(key, out var candidates)) albumCandidates[key] = candidates = [];

            var match = candidates.FirstOrDefault(c => c.Credit.Matches(credit));
            if (match is null)
            {
                album.Key = key;
                albumCredits[album] = credited;
                candidates.Add(new Candidate<Album>(album, credit));
                continue;
            }

            albumMap[album.Id] = match.Item.Id;
            match.Item.MusicBrainzId ??= album.MusicBrainzId;
            var kept = albumCredits[match.Item];
            if (credited.Count > 0 && credited.Count < kept.Count && credited.All(kept.Contains))
            {
                albumCredits[match.Item] = credited;
                match.Credit = credit;
            }
        }

        // New artists (from re-read titles) need ids before the links are rebuilt.
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"TrackArtists\"", ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"AlbumArtists\"", ct);
        db.TrackArtists.AddRange(trackCredits.SelectMany(c => c.Value.Select((a, i) => new TrackArtist { TrackId = c.Key.Id, ArtistId = a.Id, Position = i })));
        db.AlbumArtists.AddRange(albumCredits.SelectMany(c => c.Value.Select((a, i) => new AlbumArtist { AlbumId = c.Key.Id, ArtistId = a.Id, Position = i })));
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        await RemapAsync(db, "TrackId", trackMap, ct);
        await RemapAsync(db, "AlbumId", albumMap, ct);
        await RemapAsync(db, "ArtistId", survivorOf.Where(s => s.Key != s.Value.Id).ToDictionary(s => s.Key, s => s.Value.Id), ct);
        var mergedTracks = trackMap.Keys.ToList();
        var mergedAlbums = albumMap.Keys.ToList();
        await db.Tracks.Where(t => mergedTracks.Contains(t.Id)).ExecuteDeleteAsync(ct);
        await db.Albums.Where(a => mergedAlbums.Contains(a.Id)).ExecuteDeleteAsync(ct);

        // Artists nothing is credited to any more: merged duplicates, and names that turned out to be "A feat. B".
        var removedArtists = await db.Artists
            .Where(a => !db.TrackArtists.Any(x => x.ArtistId == a.Id) && !db.AlbumArtists.Any(x => x.ArtistId == a.Id))
            .ExecuteDeleteAsync(ct);

        foreach (var artist in await db.Artists.ToListAsync(ct))
            artist.Key = TextKey.ArtistKey(artist.Name);
        foreach (var alias in await db.CatalogAliases.ToListAsync(ct))
        {
            alias.Key = alias.ArtistId is not null ? TextKey.ArtistKey(alias.Name)
                : alias.TrackId is not null ? TextKey.TitleKey(alias.Name)
                : TextKey.AlbumKey(alias.Name);
        }

        await db.SaveChangesAsync(ct);

        return new Merged(removedArtists, trackMap.Count, albumMap.Count);
    }

    private static async Task<Dictionary<int, List<int>>> LinksAsync(IQueryable<Link> links, CancellationToken ct) =>
        (await links.ToListAsync(ct))
        .GroupBy(l => l.OwnerId)
        .ToDictionary(g => g.Key, g => g.OrderBy(l => l.Position).Select(l => l.ArtistId).ToList());

    private static List<Artist> Credited(Dictionary<int, List<int>> links, int ownerId, Dictionary<int, Artist> survivorOf) =>
        links.TryGetValue(ownerId, out var ids) ? ids.Select(id => survivorOf[id]).Distinct().ToList() : [];

    /// <summary>Points scrobbles, feedback, obsessions and aliases of merged tracks, albums or artists at the surviving row.</summary>
    private static async Task RemapAsync(AppDbContext db, string column, Dictionary<int, int> map, CancellationToken ct)
    {
        if (map.Count == 0) return;
        if (column is not ("TrackId" or "AlbumId" or "ArtistId")) throw new ArgumentOutOfRangeException(nameof(column));
        await db.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE IF NOT EXISTS \"IdMap\" (\"From\" INTEGER PRIMARY KEY, \"To\" INTEGER NOT NULL)", ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"IdMap\"", ct);
        foreach (var chunk in map.Chunk(400))
        {
            // Only integers are inlined.
            var insert = "INSERT INTO \"IdMap\" (\"From\", \"To\") VALUES "
                         + string.Join(",", chunk.Select(kv => string.Create(CultureInfo.InvariantCulture, $"({kv.Key},{kv.Value})")));
            await db.Database.ExecuteSqlRawAsync(insert, ct);
        }

        // The column name comes from the fixed set checked above.
        string Remap(string table, string verb = "UPDATE") =>
            $"{verb} \"{table}\" SET \"{column}\" = (SELECT \"To\" FROM \"IdMap\" WHERE \"From\" = \"{table}\".\"{column}\") WHERE \"{column}\" IN (SELECT \"From\" FROM \"IdMap\")";

        if (column != "ArtistId") await db.Database.ExecuteSqlRawAsync(Remap("Scrobbles"), ct);
        // Feedback moves along. Where the user already rated the surviving track, that rating stays and the other is
        // deleted with the merged track.
        if (column == "TrackId")
        {
            await db.Database.ExecuteSqlRawAsync(Remap("TrackFeedback", "UPDATE OR IGNORE"), ct);
            await db.Database.ExecuteSqlRawAsync(Remap("Obsessions"), ct);
        }

        await db.Database.ExecuteSqlRawAsync(Remap("CatalogAliases"), ct);
    }

    /// <summary>Cached artwork of merged artists and albums is no longer referenced.</summary>
    private static async Task DeleteUnusedImagesAsync(AppDbContext db, DataPaths paths, ILogger logger, CancellationToken ct)
    {
        var used = (await db.ArtistMetadata.Where(m => m.ImageFile != null).Select(m => m.ImageFile!)
                .Concat(db.AlbumMetadata.Where(m => m.ImageFile != null).Select(m => m.ImageFile!))
                .ToListAsync(ct))
            .ToHashSet();
        foreach (var file in Directory.EnumerateFiles(paths.Images).Where(f => !used.Contains(Path.GetFileName(f))))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete unused image {Path}", file);
            }
        }
    }
}
