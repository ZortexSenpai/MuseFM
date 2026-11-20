using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;

namespace MuseFM.Services;

/// <summary>Someone a user follows, with what they are playing now or played last.</summary>
public sealed record FriendRow(string UserName, string? AvatarFile, bool IsPublic, DateTime CreatedAt, int Scrobbles, ScrobbleRow? Last, NowPlaying? NowPlaying);

public sealed record FeedRow(string UserName, string? AvatarFile, ScrobbleRow Scrobble);

/// <summary>A user in a list, with their uploaded picture (see ProfileImages), if any.</summary>
public sealed record UserRef(string UserName, string? AvatarFile);

/// <summary>A page of the feed; <see cref="Next"/> is the cursor for older scrobbles, null at the end.</summary>
public sealed record FeedPage(IReadOnlyList<FeedRow> Rows, string? Next);

public sealed record SharedArtist(int Id, string Name, int Plays, int OtherPlays);

/// <summary>How similar two users' listening is within a window.</summary>
/// <param name="Score">0 (nothing in common) to 1 (the same artists in the same proportions).</param>
/// <param name="OnlyOther">The other user's top artists that the first never played at all.</param>
/// <param name="OnlyUser">The first user's top artists that the other never played at all.</param>
public sealed record TasteComparison(
    double Score,
    string Level,
    int Artists,
    int OtherArtists,
    int SharedCount,
    IReadOnlyList<SharedArtist> Shared,
    IReadOnlyList<ChartEntry> OnlyOther,
    IReadOnlyList<ChartEntry> OnlyUser);

/// <summary>Following other users, their listening in a feed, and comparing tastes.</summary>
public sealed class SocialService(AppDbContext db, StatsService stats, NowPlayingStore nowPlaying)
{
    private const int SharedCount = 25;
    private const int ExclusiveCount = 10;

    /// <summary>Users the viewer may see: public profiles, their own, or everyone for admins.</summary>
    private IQueryable<AppUser> Visible(IQueryable<AppUser> users, string? viewerId, bool isAdmin) =>
        users.Where(u => u.IsPublic || u.Id == viewerId || isAdmin);

    /// <summary>Follows a public user. Nobody follows themselves.</summary>
    public async Task<bool> FollowAsync(string followerId, AppUser followee, CancellationToken ct = default)
    {
        if (followerId == followee.Id || !followee.IsPublic) return false;
        if (await FollowsAsync(followerId, followee.Id, ct)) return true;
        db.Follows.Add(new Follow { FollowerId = followerId, FolloweeId = followee.Id });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Followed at the same moment from another tab.
        }

        return true;
    }

    /// <returns>False when there was nothing to unfollow.</returns>
    public async Task<bool> UnfollowAsync(string followerId, string followeeId, CancellationToken ct = default) =>
        await db.Follows.Where(f => f.FollowerId == followerId && f.FolloweeId == followeeId).ExecuteDeleteAsync(ct) > 0;

    public Task<bool> FollowsAsync(string followerId, string followeeId, CancellationToken ct = default) =>
        db.Follows.AnyAsync(f => f.FollowerId == followerId && f.FolloweeId == followeeId, ct);

    /// <summary>Followers and followed users of <paramref name="userId"/> that the viewer can see.</summary>
    public async Task<(int Followers, int Following)> CountsAsync(string userId, string? viewerId, bool isAdmin, CancellationToken ct = default) =>
    (
        await Visible(db.Follows.Where(f => f.FolloweeId == userId).Select(f => f.Follower!), viewerId, isAdmin).CountAsync(ct),
        await Visible(db.Follows.Where(f => f.FollowerId == userId).Select(f => f.Followee!), viewerId, isAdmin).CountAsync(ct)
    );

    /// <summary>The users someone follows, now playing first, then by their latest scrobble.</summary>
    public async Task<IReadOnlyList<FriendRow>> FollowingAsync(string userId, string? viewerId, bool isAdmin, CancellationToken ct = default)
    {
        var users = await Visible(db.Follows.Where(f => f.FollowerId == userId).Select(f => f.Followee!), viewerId, isAdmin)
            .Select(u => new { u.Id, u.UserName, u.AvatarFile, u.IsPublic, u.CreatedAt, Scrobbles = db.Scrobbles.Count(s => s.UserId == u.Id) })
            .ToListAsync(ct);
        var rows = new List<FriendRow>(users.Count);
        foreach (var user in users)
        {
            var last = (await stats.ListensAsync(user.Id, 1, null, null, ct)).FirstOrDefault();
            rows.Add(new FriendRow(user.UserName!, user.AvatarFile, user.IsPublic, user.CreatedAt, user.Scrobbles, last, nowPlaying.Get(user.Id)));
        }

        return rows
            .OrderByDescending(r => r.NowPlaying is not null)
            .ThenByDescending(r => r.Last?.ListenedAt ?? 0)
            .ThenBy(r => r.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<UserRef>> FollowersAsync(string userId, string? viewerId, bool isAdmin, CancellationToken ct = default) =>
        await Visible(db.Follows.Where(f => f.FolloweeId == userId).Select(f => f.Follower!), viewerId, isAdmin)
            .OrderBy(u => u.NormalizedUserName)
            .Select(u => new UserRef(u.UserName!, u.AvatarFile))
            .ToListAsync(ct);

    /// <summary>The latest scrobbles of everyone the viewer follows. <paramref name="before"/> is a cursor from a previous page.</summary>
    public async Task<FeedPage> FeedAsync(string viewerId, bool isAdmin, string? before, int count, CancellationToken ct = default)
    {
        var users = await Visible(db.Follows.Where(f => f.FollowerId == viewerId).Select(f => f.Followee!), viewerId, isAdmin)
            .ToDictionaryAsync(u => u.Id, u => new UserRef(u.UserName!, u.AvatarFile), ct);
        if (users.Count == 0) return new FeedPage([], null);

        var rows = await stats.ListensOfAsync(users.Keys.ToList(), ParseCursor(before), count + 1, ct);
        var page = rows.Take(count).Select(r => new FeedRow(users[r.UserId].UserName, users[r.UserId].AvatarFile, r.Row)).ToList();
        var next = rows.Count > count ? $"{page[^1].Scrobble.ListenedAt}_{page[^1].Scrobble.Id}" : null;
        return new FeedPage(page, next);
    }

    /// <summary>Now playing of the users the viewer follows.</summary>
    public async Task<IReadOnlyList<(string UserName, NowPlaying Playing)>> PlayingNowAsync(string viewerId, bool isAdmin, CancellationToken ct = default)
    {
        var users = await Visible(db.Follows.Where(f => f.FollowerId == viewerId).Select(f => f.Followee!), viewerId, isAdmin)
            .Select(u => new { u.Id, u.UserName })
            .ToListAsync(ct);
        return users
            .Select(u => (UserName: u.UserName!, Playing: nowPlaying.Get(u.Id)))
            .Where(x => x.Playing is not null)
            .OrderByDescending(x => x.Playing!.StartedAt)
            .Select(x => (x.UserName, x.Playing!))
            .ToList();
    }

    private static (long, long)? ParseCursor(string? cursor)
    {
        var parts = cursor?.Split('_');
        return parts is { Length: 2 } && long.TryParse(parts[0], out var at) && long.TryParse(parts[1], out var id) ? (at, id) : null;
    }

    /// <summary>
    /// Compares two users' artists within a window: cosine similarity of log-scaled play counts, so a few heavily played
    /// artists don't decide everything and the many artists played once still count.
    /// </summary>
    public async Task<TasteComparison> CompareAsync(string userId, string otherId, StatWindow window, CancellationToken ct = default)
    {
        var mine = await stats.RanksAsync(ChartKind.Artists, userId, window, ct);
        var theirs = await stats.RanksAsync(ChartKind.Artists, otherId, window, ct);

        double dot = 0, mineSquared = 0, theirsSquared = 0;
        foreach (var (id, (_, count)) in mine)
        {
            var x = Math.Log(1 + count);
            mineSquared += x * x;
            if (theirs.TryGetValue(id, out var other)) dot += x * Math.Log(1 + other.Count);
        }

        foreach (var (_, (_, count)) in theirs) theirsSquared += Math.Log(1 + count) * Math.Log(1 + count);
        var score = mineSquared == 0 || theirsSquared == 0 ? 0 : Math.Clamp(dot / Math.Sqrt(mineSquared * theirsSquared), 0, 1);

        // Shared artists that matter to both come first: ordered by the smaller of the two shares of listening.
        double myTotal = Math.Max(1, mine.Values.Sum(v => v.Count)), theirTotal = Math.Max(1, theirs.Values.Sum(v => v.Count));
        var sharedIds = mine.Keys.Where(theirs.ContainsKey).ToList();
        var shared = sharedIds
            .OrderByDescending(id => Math.Min(mine[id].Count / myTotal, theirs[id].Count / theirTotal))
            .ThenBy(id => id)
            .Take(SharedCount)
            .ToList();

        var onlyOther = await NeverPlayedAsync(theirs, userId, ct);
        var onlyUser = await NeverPlayedAsync(mine, otherId, ct);
        var nameIds = shared.Concat(onlyOther.Select(e => e.Id)).Concat(onlyUser.Select(e => e.Id)).ToList();
        var names = await db.Artists.AsNoTracking().Where(a => nameIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        return new TasteComparison(
            score,
            Level(score),
            mine.Count,
            theirs.Count,
            sharedIds.Count,
            shared.Select(id => new SharedArtist(id, names.GetValueOrDefault(id, "?"), mine[id].Count, theirs[id].Count)).ToList(),
            onlyOther.Select(e => e with { Title = names.GetValueOrDefault(e.Id, "?") }).ToList(),
            onlyUser.Select(e => e with { Title = names.GetValueOrDefault(e.Id, "?") }).ToList());
    }

    /// <summary>The top artists of a ranking that <paramref name="userId"/> never played, in any window.</summary>
    private async Task<List<ChartEntry>> NeverPlayedAsync(Dictionary<int, (int Rank, int Count)> ranking, string userId, CancellationToken ct)
    {
        var top = ranking.OrderBy(r => r.Value.Rank).Take(200).Select(r => r.Key).ToList();
        var played = (await db.Scrobbles.AsNoTracking()
                .Where(s => s.UserId == userId)
                .Join(db.TrackArtists.Where(ta => top.Contains(ta.ArtistId)), s => s.TrackId, ta => ta.TrackId, (s, ta) => ta.ArtistId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();
        return top.Where(id => !played.Contains(id)).Take(ExclusiveCount)
            .Select(id => new ChartEntry(ranking[id].Rank, id, "", [], ranking[id].Count)).ToList();
    }

    public static string Level(double score) => score switch
    {
        >= 0.75 => "Super",
        >= 0.5 => "Very high",
        >= 0.35 => "High",
        >= 0.2 => "Medium",
        >= 0.1 => "Low",
        _ => "Very low",
    };
}
