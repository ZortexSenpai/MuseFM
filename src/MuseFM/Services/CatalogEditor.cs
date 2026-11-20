using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services.Metadata;

namespace MuseFM.Services;

/// <summary>An artist, album or track as the catalog editor shows it; counts are over all users.</summary>
public sealed record CatalogEntry(
    ChartKind Kind, int Id, string Name, string Key, IReadOnlyList<ArtistRef> Artists, string? MusicBrainzId, int Scrobbles, int Listeners);

public sealed record AliasEntry(int Id, string Name, IReadOnlyList<string> Credit, DateTime CreatedAt);

/// <summary>The outcome of an edit, for the admin.</summary>
/// <param name="Id">The entry to show next: the merge target, or an existing entry to merge into instead.</param>
public sealed record CatalogEdit(bool Ok, string Message, int? Id = null);

/// <summary>
/// Admin fixes to the shared catalog: renaming, MusicBrainz ids and merging duplicates. Renames and merges keep the old
/// name as an alias, so later scrobbles that still use it land on the right entry. Edits affect every user.
/// </summary>
public sealed class CatalogEditor(AppDbContext db, StatsService stats, DataPaths paths, ILogger<CatalogEditor> logger)
{
    public const int SearchLimit = 50;
    private const int MaxName = 512;

    // ---- reading ----

    /// <summary>Entries whose name contains the query (also ignoring accents, case and punctuation), or with that id.</summary>
    public async Task<IReadOnlyList<CatalogEntry>> SearchAsync(ChartKind kind, string query, CancellationToken ct = default)
    {
        var text = TextKey.Clean(query);
        if (text.Length == 0) return [];
        var pattern = "%" + text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        var id = int.TryParse(text, out var number) ? number : -1;

        List<(int Id, string Name, string Key, string? Mbid)> rows;
        switch (kind)
        {
            case ChartKind.Artists:
            {
                var key = TextKey.ArtistKey(text);
                rows = (await db.Artists.AsNoTracking()
                        .Where(a => a.Id == id || EF.Functions.Like(a.Name, pattern, @"\") || (key.Length > 0 && a.Key.Contains(key)))
                        .OrderBy(a => a.Name.Length).ThenBy(a => a.Id).Take(SearchLimit)
                        .Select(a => new { a.Id, a.Name, a.Key, a.MusicBrainzId }).ToListAsync(ct))
                    .Select(a => (a.Id, a.Name, a.Key, a.MusicBrainzId)).ToList();
                break;
            }
            case ChartKind.Albums:
            {
                var key = TextKey.AlbumKey(text);
                rows = (await db.Albums.AsNoTracking()
                        .Where(a => a.Id == id || EF.Functions.Like(a.Title, pattern, @"\") || (key.Length > 0 && a.Key.Contains(key)))
                        .OrderBy(a => a.Title.Length).ThenBy(a => a.Id).Take(SearchLimit)
                        .Select(a => new { a.Id, a.Title, a.Key, a.MusicBrainzId }).ToListAsync(ct))
                    .Select(a => (a.Id, a.Title, a.Key, a.MusicBrainzId)).ToList();
                break;
            }
            default:
            {
                var key = TextKey.TitleKey(text);
                rows = (await db.Tracks.AsNoTracking()
                        .Where(t => t.Id == id || EF.Functions.Like(t.Title, pattern, @"\") || (key.Length > 0 && t.Key.Contains(key)))
                        .OrderBy(t => t.Title.Length).ThenBy(t => t.Id).Take(SearchLimit)
                        .Select(t => new { t.Id, t.Title, t.Key, t.MusicBrainzId }).ToListAsync(ct))
                    .Select(t => (t.Id, t.Title, t.Key, t.MusicBrainzId)).ToList();
                break;
            }
        }

        var entries = await ToEntriesAsync(kind, rows, ct);
        return entries.OrderByDescending(e => e.Scrobbles).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<CatalogEntry?> GetAsync(ChartKind kind, int id, CancellationToken ct = default)
    {
        (int, string, string, string?)? row = kind switch
        {
            ChartKind.Artists => await db.Artists.AsNoTracking().Where(a => a.Id == id).Select(a => new { a.Id, a.Name, a.Key, a.MusicBrainzId })
                .FirstOrDefaultAsync(ct) is { } a ? (a.Id, a.Name, a.Key, a.MusicBrainzId) : null,
            ChartKind.Albums => await db.Albums.AsNoTracking().Where(a => a.Id == id).Select(a => new { a.Id, a.Title, a.Key, a.MusicBrainzId })
                .FirstOrDefaultAsync(ct) is { } b ? (b.Id, b.Title, b.Key, b.MusicBrainzId) : null,
            _ => await db.Tracks.AsNoTracking().Where(t => t.Id == id).Select(t => new { t.Id, t.Title, t.Key, t.MusicBrainzId })
                .FirstOrDefaultAsync(ct) is { } t ? (t.Id, t.Title, t.Key, t.MusicBrainzId) : null,
        };
        return row is { } found ? (await ToEntriesAsync(kind, [found], ct))[0] : null;
    }

    public async Task<IReadOnlyList<AliasEntry>> AliasesAsync(ChartKind kind, int id, CancellationToken ct = default)
    {
        var rows = await Aliases(kind, id).AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
        return rows.Select(a => new AliasEntry(a.Id, a.Name, a.CreditNames, a.CreatedAt)).ToList();
    }

    private IQueryable<CatalogAlias> Aliases(ChartKind kind, int id) => kind switch
    {
        ChartKind.Artists => db.CatalogAliases.Where(a => a.ArtistId == id),
        ChartKind.Albums => db.CatalogAliases.Where(a => a.AlbumId == id),
        _ => db.CatalogAliases.Where(a => a.TrackId == id),
    };

    private async Task<List<CatalogEntry>> ToEntriesAsync(ChartKind kind, IReadOnlyList<(int Id, string Name, string Key, string? Mbid)> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var counts = kind switch
        {
            ChartKind.Artists => await db.TrackArtists.AsNoTracking().Where(ta => ids.Contains(ta.ArtistId))
                .Join(db.Scrobbles, ta => ta.TrackId, s => s.TrackId, (ta, s) => new { Id = ta.ArtistId, s.UserId })
                .GroupBy(x => x.Id).Select(g => new { g.Key, Scrobbles = g.Count(), Listeners = g.Select(x => x.UserId).Distinct().Count() })
                .ToDictionaryAsync(x => x.Key, x => (x.Scrobbles, x.Listeners), ct),
            ChartKind.Albums => await db.Scrobbles.AsNoTracking().Where(s => s.AlbumId != null && ids.Contains(s.AlbumId.Value))
                .GroupBy(s => s.AlbumId!.Value).Select(g => new { g.Key, Scrobbles = g.Count(), Listeners = g.Select(x => x.UserId).Distinct().Count() })
                .ToDictionaryAsync(x => x.Key, x => (x.Scrobbles, x.Listeners), ct),
            _ => await db.Scrobbles.AsNoTracking().Where(s => ids.Contains(s.TrackId))
                .GroupBy(s => s.TrackId).Select(g => new { g.Key, Scrobbles = g.Count(), Listeners = g.Select(x => x.UserId).Distinct().Count() })
                .ToDictionaryAsync(x => x.Key, x => (x.Scrobbles, x.Listeners), ct),
        };
        var artists = kind switch
        {
            ChartKind.Albums => await stats.AlbumArtistsAsync(ids, ct),
            ChartKind.Tracks => await stats.TrackArtistsAsync(ids, ct),
            _ => [],
        };
        return rows.Select(r =>
        {
            var (scrobbles, listeners) = counts.GetValueOrDefault(r.Id);
            return new CatalogEntry(kind, r.Id, r.Name, r.Key, artists.GetValueOrDefault(r.Id) ?? [], r.Mbid, scrobbles, listeners);
        }).ToList();
    }

    // ---- editing ----

    /// <summary>
    /// Renames an entry. When the new name matches differently, the old one becomes an alias; when it matches another
    /// entry already, nothing changes and that entry is suggested for a merge instead.
    /// </summary>
    public Task<CatalogEdit> RenameAsync(ChartKind kind, int id, string newName, CancellationToken ct = default) =>
        EditAsync(async () =>
        {
            var name = TextKey.Clean(newName);
            if (name.Length == 0) return Fail("The name can't be empty.");
            if (name.Length > MaxName) return Fail($"Names are at most {MaxName} characters.");
            return kind switch
            {
                ChartKind.Artists => await RenameArtistAsync(id, name, ct),
                ChartKind.Albums => await RenameAlbumAsync(id, name, ct),
                _ => await RenameTrackAsync(id, name, ct),
            };
        }, ct);

    private async Task<CatalogEdit> RenameArtistAsync(int id, string name, CancellationToken ct)
    {
        var artist = await db.Artists.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (artist is null) return Fail("This artist no longer exists.");
        var key = TextKey.ArtistKey(name);
        if (key.Length == 0) return Fail("That name has no letters or digits to match on.");

        if (key != artist.Key)
        {
            if (await db.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.Key == key, ct) is { } other)
                return Fail($"“{other.Name}” already exists. Merge into it instead.", other.Id);
            // The new name now leads here directly; an alias for it would be shadowed.
            await db.CatalogAliases.Where(a => a.ArtistId != null && a.Key == key).ExecuteDeleteAsync(ct);
            db.CatalogAliases.Add(new CatalogAlias { Name = artist.Name, Key = artist.Key, ArtistId = id });
            artist.Key = key;
        }

        var old = artist.Name;
        artist.Name = name;
        await db.SaveChangesAsync(ct);
        await RefetchArtistAsync(id, ct);
        return Done($"Renamed “{old}” to “{name}”.", id);
    }

    private async Task<CatalogEdit> RenameTrackAsync(int id, string title, CancellationToken ct)
    {
        var track = await db.Tracks.Include(t => t.Artists).ThenInclude(ta => ta.Artist).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (track is null) return Fail("This track no longer exists.");
        var key = TextKey.TitleKey(title);
        if (key.Length == 0) return Fail("That title has no letters or digits to match on.");

        if (key != track.Key)
        {
            var credit = Credit(track.Artists.OrderBy(a => a.Position).Select(a => a.Artist.Name));
            var clash = (await db.Tracks.AsNoTracking().Include(t => t.Artists).ThenInclude(ta => ta.Artist).Where(t => t.Key == key && t.Id != id).ToListAsync(ct))
                .FirstOrDefault(t => Credit(t.Artists.Select(a => a.Artist.Name)).Matches(credit));
            if (clash is not null) return Fail($"“{clash.Title}” by the same artist already exists. Merge into it instead.", clash.Id);

            await RemoveShadowedAliasesAsync(db.CatalogAliases.Where(a => a.TrackId != null && a.Key == key), credit, ct);
            db.CatalogAliases.Add(new CatalogAlias { Name = track.Title, Key = track.Key, Credit = Lines(track.Artists.OrderBy(a => a.Position).Select(a => a.Artist.Name)), TrackId = id });
            track.Key = key;
        }

        var old = track.Title;
        track.Title = title;
        await db.SaveChangesAsync(ct);
        return Done($"Renamed “{old}” to “{title}”.", id);
    }

    private async Task<CatalogEdit> RenameAlbumAsync(int id, string title, CancellationToken ct)
    {
        var album = await db.Albums.Include(a => a.Artists).ThenInclude(aa => aa.Artist).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (album is null) return Fail("This album no longer exists.");
        var key = TextKey.AlbumKey(title);
        if (key.Length == 0) return Fail("That title has no letters or digits to match on.");

        if (key != album.Key)
        {
            var credit = Credit(album.Artists.OrderBy(a => a.Position).Select(a => a.Artist.Name));
            var clash = (await db.Albums.AsNoTracking().Include(a => a.Artists).ThenInclude(aa => aa.Artist).Where(a => a.Key == key && a.Id != id).ToListAsync(ct))
                .FirstOrDefault(a => Credit(a.Artists.Select(x => x.Artist.Name)).Matches(credit));
            if (clash is not null) return Fail($"“{clash.Title}” by the same artist already exists. Merge into it instead.", clash.Id);

            await RemoveShadowedAliasesAsync(db.CatalogAliases.Where(a => a.AlbumId != null && a.Key == key), credit, ct);
            db.CatalogAliases.Add(new CatalogAlias { Name = album.Title, Key = album.Key, Credit = Lines(album.Artists.OrderBy(a => a.Position).Select(a => a.Artist.Name)), AlbumId = id });
            album.Key = key;
        }

        var old = album.Title;
        album.Title = title;
        await db.SaveChangesAsync(ct);
        await RefetchAlbumAsync(id, ct);
        return Done($"Renamed “{old}” to “{title}”.", id);
    }

    /// <summary>Aliases with this key and a matching credit would never be reached once an entry has that title.</summary>
    private async Task RemoveShadowedAliasesAsync(IQueryable<CatalogAlias> candidates, ArtistCredit credit, CancellationToken ct)
    {
        foreach (var alias in await candidates.ToListAsync(ct))
        {
            if (Credit(alias.CreditNames).Matches(credit)) db.CatalogAliases.Remove(alias);
        }
    }

    /// <summary>Sets or clears (empty value) the MusicBrainz id; covers and photos are then looked up again with it.</summary>
    public Task<CatalogEdit> SetMusicBrainzIdAsync(ChartKind kind, int id, string? value, CancellationToken ct = default) =>
        EditAsync(async () =>
        {
            string? mbid = null;
            if (!string.IsNullOrWhiteSpace(value) && (mbid = MetadataText.Mbid(value.Trim())) is null)
                return Fail("That isn't a MusicBrainz id (a UUID like 8f3471b5-7e6a-48da-86a9-c1c07a0f47ae).");

            var updated = kind switch
            {
                ChartKind.Artists => await db.Artists.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.MusicBrainzId, mbid), ct),
                ChartKind.Albums => await db.Albums.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.MusicBrainzId, mbid), ct),
                _ => await db.Tracks.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.MusicBrainzId, mbid), ct),
            };
            if (updated == 0) return Fail("This entry no longer exists.");
            if (kind == ChartKind.Artists) await RefetchArtistAsync(id, ct);
            if (kind == ChartKind.Albums) await RefetchAlbumAsync(id, ct);
            return Done(mbid is null ? "MusicBrainz id cleared." : "MusicBrainz id saved.", id);
        }, ct);

    public async Task<bool> DeleteAliasAsync(int aliasId, CancellationToken ct = default) =>
        await db.CatalogAliases.Where(a => a.Id == aliasId).ExecuteDeleteAsync(ct) > 0;

    /// <summary>
    /// Merges <paramref name="sourceId"/> into <paramref name="targetId"/>: its scrobbles, loves and aliases move over and
    /// its name becomes an alias. Merging artists also merges the tracks and albums that were apart only because of the
    /// two names.
    /// </summary>
    public Task<CatalogEdit> MergeAsync(ChartKind kind, int sourceId, int targetId, CancellationToken ct = default) =>
        EditAsync(async () =>
        {
            if (sourceId == targetId) return Fail("An entry can't be merged into itself.");
            return kind switch
            {
                ChartKind.Artists => await MergeArtistsAsync(sourceId, targetId, ct),
                ChartKind.Albums => await MergeAlbumsAsync(sourceId, targetId, ct),
                _ => await MergeTracksAsync(sourceId, targetId, ct),
            };
        }, ct);

    private async Task<CatalogEdit> MergeArtistsAsync(int sourceId, int targetId, CancellationToken ct)
    {
        var source = await db.Artists.FirstOrDefaultAsync(a => a.Id == sourceId, ct);
        var target = await db.Artists.FirstOrDefaultAsync(a => a.Id == targetId, ct);
        if (source is null || target is null) return Fail("One of the artists no longer exists.");

        // Credits move to the target, except where it is credited already.
        await db.Database.ExecuteSqlAsync($"""UPDATE OR IGNORE "TrackArtists" SET "ArtistId" = {targetId} WHERE "ArtistId" = {sourceId}""", ct);
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "TrackArtists" WHERE "ArtistId" = {sourceId}""", ct);
        await db.Database.ExecuteSqlAsync($"""UPDATE OR IGNORE "AlbumArtists" SET "ArtistId" = {targetId} WHERE "ArtistId" = {sourceId}""", ct);
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "AlbumArtists" WHERE "ArtistId" = {sourceId}""", ct);
        await db.CatalogAliases.Where(a => a.ArtistId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ArtistId, targetId), ct);
        db.CatalogAliases.Add(new CatalogAlias { Name = source.Name, Key = source.Key, ArtistId = targetId });
        target.MusicBrainzId ??= source.MusicBrainzId;
        var image = await db.ArtistMetadata.Where(m => m.ArtistId == sourceId).Select(m => m.ImageFile).FirstOrDefaultAsync(ct);
        db.Artists.Remove(source);
        await db.SaveChangesAsync(ct);
        DeleteImage(image);

        // Songs and albums that were apart only because of the two names are one now.
        var (tracks, albums) = await DeduplicateAsync(targetId, ct);
        var extra = tracks + albums > 0 ? $" {Plural(tracks, "track")} and {Plural(albums, "album")} that were duplicates were merged too." : "";
        return Done($"Merged “{source.Name}” into “{target.Name}”.{extra}", targetId);
    }

    private async Task<(int Tracks, int Albums)> DeduplicateAsync(int artistId, CancellationToken ct)
    {
        var tracks = await db.Tracks.Include(t => t.Artists).ThenInclude(ta => ta.Artist)
            .Where(t => t.Artists.Any(ta => ta.ArtistId == artistId)).OrderBy(t => t.Id).ToListAsync(ct);
        var mergedTracks = 0;
        foreach (var group in tracks.GroupBy(t => t.Key))
        {
            var kept = new List<(Track Track, ArtistCredit Credit)>();
            foreach (var track in group)
            {
                var credit = Credit(track.Artists.OrderBy(a => a.Position).Select(a => a.Artist.Name));
                var match = kept.FirstOrDefault(k => k.Credit.Matches(credit));
                if (match.Track is null)
                {
                    kept.Add((track, credit));
                    continue;
                }

                // Like the automatic matching: the first row stays and gains any featured artists of the other.
                foreach (var link in track.Artists.Where(l => match.Track.Artists.All(m => m.ArtistId != l.ArtistId)).OrderBy(l => l.Position).ToList())
                    match.Track.Artists.Add(new TrackArtist { ArtistId = link.ArtistId, Position = match.Track.Artists.Count });
                await MoveTrackAsync(track, match.Track, addAlias: false, ct);
                mergedTracks++;
            }
        }

        var albums = await db.Albums.Include(a => a.Artists).ThenInclude(aa => aa.Artist)
            .Where(a => a.Artists.Any(aa => aa.ArtistId == artistId)).OrderBy(a => a.Id).ToListAsync(ct);
        var mergedAlbums = 0;
        foreach (var group in albums.GroupBy(a => a.Key))
        {
            var kept = new List<(Album Album, ArtistCredit Credit)>();
            foreach (var album in group)
            {
                var credit = Credit(album.Artists.OrderBy(a => a.Position).Select(a => a.Artist.Name));
                var match = kept.FirstOrDefault(k => k.Credit.Matches(credit));
                if (match.Album is null)
                {
                    kept.Add((album, credit));
                    continue;
                }

                await MoveAlbumAsync(album, match.Album, addAlias: false, ct);
                mergedAlbums++;
            }
        }

        await db.SaveChangesAsync(ct);
        return (mergedTracks, mergedAlbums);
    }

    private async Task<CatalogEdit> MergeTracksAsync(int sourceId, int targetId, CancellationToken ct)
    {
        var source = await db.Tracks.Include(t => t.Artists).ThenInclude(ta => ta.Artist).FirstOrDefaultAsync(t => t.Id == sourceId, ct);
        var target = await db.Tracks.Include(t => t.Artists).ThenInclude(ta => ta.Artist).FirstOrDefaultAsync(t => t.Id == targetId, ct);
        if (source is null || target is null) return Fail("One of the tracks no longer exists.");

        var artists = source.Artists.Select(a => a.ArtistId).ToList();
        await MoveTrackAsync(source, target, addAlias: true, ct);
        await db.SaveChangesAsync(ct);
        await DeleteUncreditedArtistsAsync(artists, ct);
        return Done($"Merged “{source.Title}” into “{target.Title}”.", targetId);
    }

    private async Task<CatalogEdit> MergeAlbumsAsync(int sourceId, int targetId, CancellationToken ct)
    {
        var source = await db.Albums.Include(a => a.Artists).ThenInclude(aa => aa.Artist).FirstOrDefaultAsync(a => a.Id == sourceId, ct);
        var target = await db.Albums.Include(a => a.Artists).ThenInclude(aa => aa.Artist).FirstOrDefaultAsync(a => a.Id == targetId, ct);
        if (source is null || target is null) return Fail("One of the albums no longer exists.");

        var artists = source.Artists.Select(a => a.ArtistId).ToList();
        await MoveAlbumAsync(source, target, addAlias: true, ct);
        await db.SaveChangesAsync(ct);
        await DeleteUncreditedArtistsAsync(artists, ct);
        return Done($"Merged “{source.Title}” into “{target.Title}”.", targetId);
    }

    /// <summary>Moves scrobbles, feedback, obsessions and aliases of a track to another and removes it (saved by the caller).</summary>
    private async Task MoveTrackAsync(Track source, Track target, bool addAlias, CancellationToken ct)
    {
        await db.Scrobbles.Where(s => s.TrackId == source.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.TrackId, target.Id), ct);
        // Where the user rated both, the target's rating stays and the other goes with the merged track.
        await db.Database.ExecuteSqlAsync($"""UPDATE OR IGNORE "TrackFeedback" SET "TrackId" = {target.Id} WHERE "TrackId" = {source.Id}""", ct);
        await db.Obsessions.Where(o => o.TrackId == source.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.TrackId, target.Id), ct);
        await db.CatalogAliases.Where(a => a.TrackId == source.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.TrackId, target.Id), ct);
        if (addAlias)
            db.CatalogAliases.Add(new CatalogAlias { Name = source.Title, Key = source.Key, Credit = Lines(source.Artists.OrderBy(a => a.Position).Select(a => a.Artist.Name)), TrackId = target.Id });
        target.MusicBrainzId ??= source.MusicBrainzId;
        target.LengthSeconds ??= source.LengthSeconds;
        db.Tracks.Remove(source);
    }

    /// <summary>Moves scrobbles and aliases of an album to another and removes it (saved by the caller).</summary>
    private async Task MoveAlbumAsync(Album source, Album target, bool addAlias, CancellationToken ct)
    {
        await db.Scrobbles.Where(s => s.AlbumId == source.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AlbumId, target.Id), ct);
        await db.CatalogAliases.Where(a => a.AlbumId == source.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.AlbumId, target.Id), ct);
        if (addAlias)
            db.CatalogAliases.Add(new CatalogAlias { Name = source.Title, Key = source.Key, Credit = Lines(source.Artists.OrderBy(a => a.Position).Select(a => a.Artist.Name)), AlbumId = target.Id });
        target.MusicBrainzId ??= source.MusicBrainzId;
        DeleteImage(await db.AlbumMetadata.Where(m => m.AlbumId == source.Id).Select(m => m.ImageFile).FirstOrDefaultAsync(ct));
        db.Albums.Remove(source);
    }

    /// <summary>Artists left without any track or album (after a misattributed track was merged away) are removed.</summary>
    private async Task DeleteUncreditedArtistsAsync(IReadOnlyList<int> artistIds, CancellationToken ct)
    {
        var orphans = await db.Artists
            .Where(a => artistIds.Contains(a.Id) && !db.TrackArtists.Any(x => x.ArtistId == a.Id) && !db.AlbumArtists.Any(x => x.ArtistId == a.Id))
            .Select(a => new { a.Id, Image = db.ArtistMetadata.Where(m => m.ArtistId == a.Id).Select(m => m.ImageFile).FirstOrDefault() })
            .ToListAsync(ct);
        if (orphans.Count == 0) return;
        var ids = orphans.Select(o => o.Id).ToList();
        await db.Artists.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct);
        foreach (var orphan in orphans) DeleteImage(orphan.Image);
    }

    // ---- helpers ----

    /// <summary>Runs an edit in a transaction, serialized with scrobbling so an ingest never sees half a merge.</summary>
    private async Task<CatalogEdit> EditAsync(Func<Task<CatalogEdit>> edit, CancellationToken ct)
    {
        await ScrobbleService.WriteGate.WaitAsync(ct);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await edit();
            if (result.Ok) await transaction.CommitAsync(ct);
            return result;
        }
        finally
        {
            db.ChangeTracker.Clear();
            ScrobbleService.WriteGate.Release();
        }
    }

    /// <summary>Makes the metadata connectors look an artist up again, without a MusicBrainz id found under the old name.</summary>
    private Task RefetchArtistAsync(int id, CancellationToken ct) =>
        db.ArtistMetadata.Where(m => m.ArtistId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.MusicBrainzId, (string?)null).SetProperty(m => m.RefreshAfter, DateTime.UtcNow), ct);

    private Task RefetchAlbumAsync(int id, CancellationToken ct) =>
        db.AlbumMetadata.Where(m => m.AlbumId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.MusicBrainzId, (string?)null).SetProperty(m => m.RefreshAfter, DateTime.UtcNow), ct);

    private void DeleteImage(string? file)
    {
        if (file is null) return;
        try
        {
            File.Delete(Path.Combine(paths.Images, Path.GetFileName(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete cached image {File}", file);
        }
    }

    private static ArtistCredit Credit(IEnumerable<string> names) => new(names);

    private static string Lines(IEnumerable<string> names) => string.Join('\n', names);

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static CatalogEdit Fail(string message, int? id = null) => new(false, message, id);

    private static CatalogEdit Done(string message, int id) => new(true, message, id);
}
