using Microsoft.EntityFrameworkCore;
using MuseFM.Services.Metadata;

namespace MuseFM.Services;

public sealed record GenreCount(string Name, int Plays);

/// <param name="Decade">Its first year: 1990 for the nineties.</param>
public sealed record DecadeCount(int Decade, int Plays);

/// <summary>
/// The genres and release years of what a user played within a range, as far as the metadata connectors knew them.
/// </summary>
/// <param name="Genres">The most played genres. A scrobble with several genres counts for each of them.</param>
/// <param name="GenreCount">All genres played.</param>
/// <param name="WithGenre">Scrobbles with at least one genre, out of <paramref name="Scrobbles"/>.</param>
/// <param name="Decades">Every decade from the oldest to the newest one played.</param>
/// <param name="WithYear">Scrobbles of albums with a known release year.</param>
/// <param name="MedianYear">Half of those scrobbles are of albums from this year or later.</param>
public sealed record MusicMix(
    int Scrobbles, IReadOnlyList<GenreCount> Genres, int GenreCount, int WithGenre, IReadOnlyList<DecadeCount> Decades, int WithYear, int? MedianYear);

public sealed partial class InsightsService
{
    /// <summary>Genres an artist without any of their own takes over from their albums.</summary>
    private const int InheritedGenres = 3;

    /// <summary>
    /// Genres and release decades of the scrobbles in the window. A scrobble has the genres of its album; without those,
    /// the genres of the first of its artists that has any, be it their own or the most common ones of their albums.
    /// </summary>
    public async Task<MusicMix> MusicMixAsync(string userId, StatWindow window, int count, CancellationToken ct = default)
    {
        var query = db.Scrobbles.AsNoTracking().Where(s => s.UserId == userId);
        if (window.From is { } from) query = query.Where(s => s.ListenedAt >= from);
        if (window.To is { } to) query = query.Where(s => s.ListenedAt < to);
        var plays = await query
            .GroupBy(s => new { s.AlbumId, s.TrackId })
            .Select(g => new { g.Key.AlbumId, g.Key.TrackId, Count = g.Count() })
            .ToListAsync(ct);
        var scrobbles = plays.Sum(p => p.Count);

        // The catalog is loaded whole and combined here: these are small rows, and SQLite plans such joins badly.
        var albums = await db.AlbumMetadata.AsNoTracking()
            .Where(m => m.Genres != null || m.ReleaseYear != null)
            .Select(m => new { m.AlbumId, m.Genres, m.ReleaseYear })
            .ToDictionaryAsync(m => m.AlbumId, ct);
        var ownGenres = await db.ArtistMetadata.AsNoTracking()
            .Where(m => m.Genres != null)
            .Select(m => new { m.ArtistId, m.Genres })
            .ToDictionaryAsync(m => m.ArtistId, m => GenreNames.Split(m.Genres), ct);
        if (scrobbles == 0 || (albums.Count == 0 && ownGenres.Count == 0)) return new MusicMix(scrobbles, [], 0, 0, [], 0, null);

        var albumGenres = albums.Where(a => a.Value.Genres is not null).ToDictionary(a => a.Key, a => GenreNames.Split(a.Value.Genres));
        var needArtists = plays.Any(p => p.AlbumId is not { } albumId || !albumGenres.ContainsKey(albumId));
        var trackArtists = needArtists
            ? (await db.TrackArtists.AsNoTracking().OrderBy(ta => ta.Position).Select(ta => new { ta.TrackId, ta.ArtistId }).ToListAsync(ct))
                .ToLookup(ta => ta.TrackId, ta => ta.ArtistId)
            : null;
        var artistAlbums = needArtists
            ? (await db.AlbumArtists.AsNoTracking().Select(aa => new { aa.ArtistId, aa.AlbumId }).ToListAsync(ct)).ToLookup(aa => aa.ArtistId, aa => aa.AlbumId)
            : null;

        var inherited = new Dictionary<int, IReadOnlyList<string>>();
        IReadOnlyList<string> GenresOfArtist(int artistId)
        {
            if (ownGenres.TryGetValue(artistId, out var own)) return own;
            if (inherited.TryGetValue(artistId, out var cached)) return cached;
            var common = artistAlbums![artistId]
                .SelectMany(albumId => albumGenres.GetValueOrDefault(albumId) ?? [])
                .GroupBy(GenreNames.Key)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(InheritedGenres)
                .Select(g => g.First())
                .ToList();
            return inherited[artistId] = common;
        }

        // Spellings of a genre are merged by key and shown the way most plays spell it.
        var genres = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var years = new SortedDictionary<int, int>();
        var withGenre = 0;
        var withYear = 0;
        foreach (var play in plays)
        {
            var album = play.AlbumId is { } id ? albums.GetValueOrDefault(id) : null;
            if (album?.ReleaseYear is { } year)
            {
                years[year] = years.GetValueOrDefault(year) + play.Count;
                withYear += play.Count;
            }

            var names = play.AlbumId is { } albumId && albumGenres.TryGetValue(albumId, out var ofAlbum)
                ? ofAlbum
                : trackArtists![play.TrackId].Select(GenresOfArtist).FirstOrDefault(g => g.Count > 0) ?? [];
            if (names.Count == 0) continue;
            withGenre += play.Count;
            foreach (var name in names)
            {
                var key = GenreNames.Key(name);
                if (!genres.TryGetValue(key, out var spellings)) genres[key] = spellings = new Dictionary<string, int>(StringComparer.Ordinal);
                spellings[name] = spellings.GetValueOrDefault(name) + play.Count;
            }
        }

        var top = genres.Values
            .Select(s => new GenreCount(s.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key, s.Values.Sum()))
            .OrderByDescending(g => g.Plays).ThenBy(g => g.Name, StringComparer.Ordinal)
            .Take(count)
            .ToList();

        var decades = new List<DecadeCount>();
        int? median = null;
        if (years.Count > 0)
        {
            for (var decade = years.Keys.First() / 10 * 10; decade <= years.Keys.Last(); decade += 10)
                decades.Add(new DecadeCount(decade, years.Where(y => y.Key >= decade && y.Key < decade + 10).Sum(y => y.Value)));

            // From the newest year back, until half of the plays are covered.
            var seen = 0;
            foreach (var (year, yearPlays) in years.Reverse())
            {
                seen += yearPlays;
                median = year;
                if (seen * 2 >= withYear) break;
            }
        }

        return new MusicMix(scrobbles, top, genres.Count, withGenre, decades, withYear, median);
    }
}
