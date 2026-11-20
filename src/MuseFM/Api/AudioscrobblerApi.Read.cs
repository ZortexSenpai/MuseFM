using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;
using MuseFM.Services.Metadata;

namespace MuseFM.Api;

/// <summary>
/// Last.fm read methods (user.getRecentTracks, user.getTop*, user.getLovedTracks, user.getFriends, track.getInfo), so
/// widgets, "now playing" displays and other Last.fm tools can read from MuseFM. Public users can be read without a
/// session; private users only with their own session key.
/// </summary>
public static partial class AudioscrobblerApi
{
    private const int DefaultLimit = 50;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] ImageSizes = ["small", "medium", "large", "extralarge"];

    /// <summary>
    /// Marks an element whose <see cref="Item"/> children form a JSON array, even when there are none or one
    /// (Last.fm itself sends a lone object then, which trips up clients).
    /// </summary>
    private sealed record ListOf(string Item);

    /// <summary>Where MuseFM is reached (for URLs in responses) and whether it has artwork to link to.</summary>
    private sealed record Site(string Base, bool Artwork)
    {
        public static Site Of(HttpContext http) =>
            new($"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}", http.RequestServices.GetRequiredService<MetadataService>().Enabled);
    }

    private sealed record ReadContext(AppUser User, string? ViewerId, Site Site, HttpContext Http)
    {
        public T Service<T>() where T : notnull => Http.RequestServices.GetRequiredService<T>();

        public string Base => Site.Base;

        public string UserUrl => $"{Base}/u/{Uri.EscapeDataString(User.UserName!)}";
    }

    /// <summary>The user to read (the <c>user</c> parameter, else the session's user), if the caller may see them.</summary>
    private static async Task<(ReadContext? Context, IResult? Error)> ReadUserAsync(Dictionary<string, string> p, bool json, HttpContext http, CancellationToken ct)
    {
        var session = await http.RequestServices.GetRequiredService<ApiKeyService>().ValidateSessionAsync(p.GetValueOrDefault("sk"), ct);
        var name = p.GetValueOrDefault("user") ?? p.GetValueOrDefault("username");
        var user = string.IsNullOrWhiteSpace(name)
            ? session?.User
            : await http.RequestServices.GetRequiredService<UserManager<AppUser>>().FindByNameAsync(name);
        if (user is null || (!user.IsPublic && user.Id != session?.UserId))
            return (null, Failure(json, 6, "User not found", 404));
        return (new ReadContext(user, session?.UserId, Site.Of(http), http), null);
    }

    private static (int Page, int Limit) Paging(Dictionary<string, string> p, int max) =>
    (
        Math.Max(1, Int(p, "page") ?? 1),
        Math.Clamp(Int(p, "limit") ?? DefaultLimit, 1, max)
    );

    private static int? Int(Dictionary<string, string> p, string name) =>
        int.TryParse(p.GetValueOrDefault(name), NumberStyles.Integer, Inv, out var value) ? value : null;

    private static long? Long(Dictionary<string, string> p, string name) =>
        long.TryParse(p.GetValueOrDefault(name), NumberStyles.Integer, Inv, out var value) ? value : null;

    /// <summary>A Last.fm list: paging attributes and the items as an array.</summary>
    private static XElement Page(string name, string item, ReadContext context, int page, int limit, int total, IEnumerable<XElement> items)
    {
        var element = new XElement(name,
            new XAttribute("user", context.User.UserName!),
            new XAttribute("page", page),
            new XAttribute("perPage", limit),
            new XAttribute("totalPages", Math.Max(1, (int)Math.Ceiling(total / (double)limit))),
            new XAttribute("total", total),
            items);
        element.AddAnnotation(new ListOf(item));
        return element;
    }

    /// <summary>Last.fm's four image sizes, all pointing at MuseFM's cached artwork when a metadata connector is active.</summary>
    private static IEnumerable<XElement> Images(XElement owner, Site site, string? media) =>
        Images(owner, media is not null && site.Artwork ? $"{site.Base}/media/{media}" : "");

    /// <summary>Last.fm's four image sizes, all with the same <paramref name="url"/> (empty for none).</summary>
    private static IEnumerable<XElement> Images(XElement owner, string url)
    {
        owner.AddAnnotation(new ListOf("image"));
        return ImageSizes.Select(size => new XElement("image", new XAttribute("size", size), url));
    }

    private static XElement WithImages(XElement element, ReadContext context, string? media) => WithImages(element, context.Site, media);

    private static XElement WithImages(XElement element, Site site, string? media)
    {
        element.Add(Images(element, site, media));
        return element;
    }

    private static XElement Date(long unix) =>
        new("date", new XAttribute("uts", unix), DateTimeOffset.FromUnixTimeSeconds(unix).ToString("d MMM yyyy, HH:mm", Inv));

    private static XElement Streamable() => new("streamable", new XAttribute("fulltrack", 0), 0);

    private static XElement ArtistElement(ReadContext context, IReadOnlyList<ArtistRef> artists, IReadOnlyDictionary<int, string> mbids) =>
        new("artist",
            new XElement("name", MetadataParser.Credit(artists.Select(a => a.Name).ToList())),
            new XElement("mbid", artists.Count > 0 ? mbids.GetValueOrDefault(artists[0].Id) ?? "" : ""),
            new XElement("url", artists.Count > 0 ? $"{context.UserUrl}/artist/{artists[0].Id}" : context.UserUrl));

    private static IResult Reply(bool json, XElement content) => json ? Results.Json(new JsonObject { [content.Name.LocalName] = ToJson(content) }) : Ok(content);

    /// <summary>
    /// Last.fm's JSON for its XML: a text element is a string, or an object with its attributes and "#text"; an element
    /// with children is an object with its attributes under "@attr".
    /// </summary>
    private static JsonNode? ToJson(XElement element)
    {
        var list = element.Annotation<ListOf>();
        if (!element.HasElements && list is null)
        {
            if (!element.HasAttributes) return JsonValue.Create(element.Value);
            var leaf = new JsonObject();
            foreach (var attribute in element.Attributes()) leaf[attribute.Name.LocalName] = attribute.Value;
            leaf["#text"] = element.Value;
            return leaf;
        }

        var node = new JsonObject();
        if (list is not null) node[list.Item] = new JsonArray(element.Elements(list.Item).Select(ToJson).ToArray());
        foreach (var child in element.Elements().Where(c => c.Name.LocalName != list?.Item))
            node[child.Name.LocalName] = ToJson(child);
        if (element.HasAttributes)
            node["@attr"] = new JsonObject(element.Attributes().Select(a => KeyValuePair.Create(a.Name.LocalName, (JsonNode?)JsonValue.Create(a.Value))));
        return node;
    }

    // ---- user.getRecentTracks ----

    private static async Task<IResult> GetRecentTracksAsync(Dictionary<string, string> p, bool json, HttpContext http, CancellationToken ct)
    {
        var (context, error) = await ReadUserAsync(p, json, http, ct);
        if (context is null) return error!;
        var (page, limit) = Paging(p, 200);
        var extended = p.GetValueOrDefault("extended") == "1";
        // "from": scrobbles after this time; "to": before it.
        var from = Long(p, "from");
        var to = Long(p, "to");

        var stats = context.Service<StatsService>();
        var history = await stats.RecentAsync(context.User.Id, page, limit, null, new StatWindow(from + 1, to), ct);
        var artistMbids = await stats.ArtistMbidsAsync(history.Items.SelectMany(r => r.Artists.Take(1).Select(a => a.Id)), ct);
        var loved = extended ? await context.Service<FeedbackService>().LovedAmongAsync(context.User.Id, history.Items.Select(r => r.TrackId), ct) : null;

        var tracks = new List<XElement>();
        if (page == 1 && to is null && context.Service<NowPlayingStore>().Get(context.User.Id) is { } playing)
        {
            var track = new XElement("track", new XAttribute("nowplaying", "true"),
                extended
                    ? new XElement("artist", new XElement("name", MetadataParser.Credit(playing.Artists)), new XElement("mbid", ""), new XElement("url", context.UserUrl))
                    : new XElement("artist", new XAttribute("mbid", ""), MetadataParser.Credit(playing.Artists)),
                new XElement("name", playing.Title),
                Streamable(),
                new XElement("mbid", ""),
                new XElement("album", new XAttribute("mbid", ""), playing.Album ?? ""),
                new XElement("url", context.UserUrl));
            tracks.Add(WithImages(track, context, null));
        }

        foreach (var row in history.Items)
        {
            var main = row.Artists.Count > 0 ? row.Artists[0] : null;
            var mbid = main is null ? "" : artistMbids.GetValueOrDefault(main.Id) ?? "";
            var track = new XElement("track",
                extended
                    ? ArtistElement(context, row.Artists, artistMbids)
                    : new XElement("artist", new XAttribute("mbid", mbid), MetadataParser.Credit(row.Artists.Select(a => a.Name).ToList())),
                new XElement("name", row.Title),
                Streamable(),
                new XElement("mbid", row.TrackMbid ?? ""),
                new XElement("album", new XAttribute("mbid", row.AlbumMbid ?? ""), row.Album ?? ""),
                new XElement("url", $"{context.UserUrl}/track/{row.TrackId}"),
                Date(row.ListenedAt));
            track.Add(Images(track, context.Site, row.AlbumId is { } albumId ? $"album/{albumId}" : $"track/{row.TrackId}"));
            if (loved is not null) track.Add(new XElement("loved", loved.Contains(row.TrackId) ? 1 : 0));
            tracks.Add(track);
        }

        return Reply(json, Page("recenttracks", "track", context, page, limit, history.Total, tracks));
    }

    // ---- user.getTopArtists / getTopAlbums / getTopTracks ----

    /// <summary>Last.fm's periods are rolling: 7day, 1month, 3month, 6month, 12month, or overall.</summary>
    private static StatWindow TopWindow(Dictionary<string, string> p)
    {
        int? days = p.GetValueOrDefault("period")?.ToLowerInvariant() switch
        {
            "7day" => 7,
            "1month" => 30,
            "3month" => 90,
            "6month" => 180,
            "12month" => 365,
            _ => null,
        };
        return days is { } d ? new StatWindow(DateTimeOffset.UtcNow.AddDays(-d).ToUnixTimeSeconds()) : default;
    }

    private static async Task<IResult> GetTopArtistsAsync(Dictionary<string, string> p, bool json, HttpContext http, CancellationToken ct)
    {
        var (context, error) = await ReadUserAsync(p, json, http, ct);
        if (context is null) return error!;
        var (page, limit) = Paging(p, 1000);
        var stats = context.Service<StatsService>();
        var chart = await stats.TopArtistsAsync(context.User.Id, TopWindow(p), page, limit, ct);
        var mbids = await stats.ArtistMbidsAsync(chart.Items.Select(e => e.Id), ct);

        var items = chart.Items.Select(e => WithImages(new XElement("artist", new XAttribute("rank", e.Rank),
            new XElement("name", e.Title),
            new XElement("playcount", e.Count),
            new XElement("mbid", mbids.GetValueOrDefault(e.Id) ?? ""),
            new XElement("url", $"{context.UserUrl}/artist/{e.Id}"),
            new XElement("streamable", 0)), context, $"artist/{e.Id}"));
        return Reply(json, Page("topartists", "artist", context, page, limit, chart.Total, items));
    }

    private static async Task<IResult> GetTopAlbumsAsync(Dictionary<string, string> p, bool json, HttpContext http, CancellationToken ct)
    {
        var (context, error) = await ReadUserAsync(p, json, http, ct);
        if (context is null) return error!;
        var (page, limit) = Paging(p, 1000);
        var stats = context.Service<StatsService>();
        var chart = await stats.TopAlbumsAsync(context.User.Id, TopWindow(p), page, limit, ct: ct);
        var mbids = await stats.AlbumMbidsAsync(chart.Items.Select(e => e.Id), ct);
        var artistMbids = await stats.ArtistMbidsAsync(chart.Items.SelectMany(e => e.Artists.Take(1).Select(a => a.Id)), ct);

        var items = chart.Items.Select(e => WithImages(new XElement("album", new XAttribute("rank", e.Rank),
            new XElement("name", e.Title),
            new XElement("playcount", e.Count),
            new XElement("mbid", mbids.GetValueOrDefault(e.Id) ?? ""),
            new XElement("url", $"{context.UserUrl}/album/{e.Id}"),
            ArtistElement(context, e.Artists, artistMbids)), context, $"album/{e.Id}"));
        return Reply(json, Page("topalbums", "album", context, page, limit, chart.Total, items));
    }

    private static async Task<IResult> GetTopTracksAsync(Dictionary<string, string> p, bool json, HttpContext http, CancellationToken ct)
    {
        var (context, error) = await ReadUserAsync(p, json, http, ct);
        if (context is null) return error!;
        var (page, limit) = Paging(p, 1000);
        var stats = context.Service<StatsService>();
        var chart = await stats.TopTracksAsync(context.User.Id, TopWindow(p), page, limit, ct: ct);
        var details = await stats.TrackDetailsAsync(chart.Items.Select(e => e.Id), ct);
        var artistMbids = await stats.ArtistMbidsAsync(chart.Items.SelectMany(e => e.Artists.Take(1).Select(a => a.Id)), ct);

        var items = chart.Items.Select(e =>
        {
            var (mbid, length) = details.GetValueOrDefault(e.Id);
            return WithImages(new XElement("track", new XAttribute("rank", e.Rank),
                new XElement("name", e.Title),
                new XElement("duration", length ?? 0),
                new XElement("playcount", e.Count),
                new XElement("mbid", mbid ?? ""),
                new XElement("url", $"{context.UserUrl}/track/{e.Id}"),
                Streamable(),
                ArtistElement(context, e.Artists, artistMbids)), context, $"track/{e.Id}");
        });
        return Reply(json, Page("toptracks", "track", context, page, limit, chart.Total, items));
    }

    // ---- user.getLovedTracks ----

    private static async Task<IResult> GetLovedTracksAsync(Dictionary<string, string> p, bool json, HttpContext http, CancellationToken ct)
    {
        var (context, error) = await ReadUserAsync(p, json, http, ct);
        if (context is null) return error!;
        var (page, limit) = Paging(p, 1000);
        var loved = await context.Service<FeedbackService>().ListAsync(context.User.Id, TrackFeedback.Love, page, limit, ct);
        var artistMbids = await context.Service<StatsService>().ArtistMbidsAsync(loved.Items.SelectMany(t => t.Artists.Take(1).Select(a => a.Id)), ct);

        var items = loved.Items.Select(t => WithImages(new XElement("track",
            new XElement("name", t.Title),
            new XElement("mbid", t.MusicBrainzId ?? ""),
            new XElement("url", $"{context.UserUrl}/track/{t.TrackId}"),
            Date(t.CreatedAt),
            ArtistElement(context, t.Artists, artistMbids),
            Streamable()), context, $"track/{t.TrackId}"));
        return Reply(json, Page("lovedtracks", "track", context, page, limit, loved.Total, items));
    }

    // ---- user.getFriends ----

    /// <summary>The users someone follows (MuseFM has no mutual friendships).</summary>
    private static async Task<IResult> GetFriendsAsync(Dictionary<string, string> p, bool json, HttpContext http, CancellationToken ct)
    {
        var (context, error) = await ReadUserAsync(p, json, http, ct);
        if (context is null) return error!;
        var (page, limit) = Paging(p, 1000);
        var following = await context.Service<SocialService>().FollowingAsync(context.User.Id, context.ViewerId, isAdmin: false, ct);
        var items = new List<XElement>();
        foreach (var friend in following.Skip((page - 1) * limit).Take(limit))
        {
            var registered = new DateTimeOffset(DateTime.SpecifyKind(friend.CreatedAt, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var user = new XElement("user",
                new XElement("name", friend.UserName),
                new XElement("realname", ""),
                new XElement("url", $"{context.Base}/u/{Uri.EscapeDataString(friend.UserName)}"),
                new XElement("country", ""),
                new XElement("playcount", friend.Scrobbles),
                new XElement("registered", new XAttribute("unixtime", registered), DateTimeOffset.FromUnixTimeSeconds(registered).ToString("yyyy-MM-dd HH:mm", Inv)),
                new XElement("type", "user"),
                new XElement("subscriber", 0));
            user.Add(Images(user, friend.AvatarFile is { } avatar ? context.Base + ProfileImages.Url(avatar) : ""));
            items.Add(user);
        }

        return Reply(json, Page("friends", "user", context, page, limit, following.Count, items));
    }

    // ---- track.getInfo ----

    /// <summary>
    /// A track by artist and title (or MusicBrainz id). Listeners and play counts cover public profiles (and the caller's
    /// own); with <c>username</c>, that user's play count and love are included.
    /// </summary>
    private static async Task<IResult> GetTrackInfoAsync(Dictionary<string, string> p, bool json, HttpContext http, CancellationToken ct)
    {
        var services = http.RequestServices;
        var session = await services.GetRequiredService<ApiKeyService>().ValidateSessionAsync(p.GetValueOrDefault("sk"), ct);
        int? trackId = null;
        if (MetadataText.Mbid(p.GetValueOrDefault("mbid")) is { } mbid)
            trackId = await services.GetRequiredService<FeedbackService>().TrackByMbidAsync(mbid, ct);
        if (trackId is null && !string.IsNullOrWhiteSpace(p.GetValueOrDefault("track")) && !string.IsNullOrWhiteSpace(p.GetValueOrDefault("artist")))
            trackId = (await services.GetRequiredService<ScrobbleService>().ResolveTracksAsync([new TrackRef(p["track"], [p["artist"]])], create: false, ct))[0];
        if (trackId is not { } id) return Failure(json, 6, "Track not found", 404);

        var stats = services.GetRequiredService<StatsService>();
        var track = (await stats.TrackAsync(id, ct))!;
        var db = services.GetRequiredService<AppDbContext>();
        var caller = session?.UserId;
        var counted = db.Scrobbles.AsNoTracking().Where(s => s.TrackId == id && (s.User!.IsPublic || s.UserId == caller));
        var playcount = await counted.CountAsync(ct);
        var listeners = await counted.Select(s => s.UserId).Distinct().CountAsync(ct);
        var albumId = await db.Scrobbles.AsNoTracking().Where(s => s.TrackId == id && s.AlbumId != null)
            .GroupBy(s => s.AlbumId!.Value).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => (int?)g.Key).FirstOrDefaultAsync(ct);

        var (context, _) = await ReadUserAsync(p, json, http, ct);
        var site = Site.Of(http);
        var @base = site.Base;
        var pageUrl = context is null ? @base : $"{context.UserUrl}/track/{id}";
        var artistMbids = await stats.ArtistMbidsAsync(track.Artists.Take(1).Select(a => a.Id), ct);

        var element = new XElement("track",
            new XElement("name", track.Title),
            new XElement("mbid", track.MusicBrainzId ?? ""),
            new XElement("url", pageUrl),
            new XElement("duration", (track.LengthSeconds ?? 0) * 1000L),
            Streamable(),
            new XElement("listeners", listeners),
            new XElement("playcount", playcount),
            new XElement("artist",
                new XElement("name", MetadataParser.Credit(track.Artists.Select(a => a.Name).ToList())),
                new XElement("mbid", track.Artists.Count > 0 ? artistMbids.GetValueOrDefault(track.Artists[0].Id) ?? "" : ""),
                new XElement("url", context is not null && track.Artists.Count > 0 ? $"{context.UserUrl}/artist/{track.Artists[0].Id}" : @base)));

        if (albumId is { } album && await stats.AlbumAsync(album, ct) is { } info)
        {
            var albumElement = new XElement("album",
                new XElement("artist", MetadataParser.Credit(info.Artists.Select(a => a.Name).ToList())),
                new XElement("title", info.Title),
                new XElement("mbid", info.MusicBrainzId ?? ""),
                new XElement("url", context is null ? @base : $"{context.UserUrl}/album/{album}"));
            element.Add(WithImages(albumElement, site, $"album/{album}"));
        }

        var tags = new XElement("toptags");
        tags.AddAnnotation(new ListOf("tag"));
        element.Add(tags);

        if (context is not null && p.ContainsKey("username"))
        {
            element.Add(new XElement("userplaycount", await db.Scrobbles.CountAsync(s => s.TrackId == id && s.UserId == context.User.Id, ct)));
            element.Add(new XElement("userloved", await services.GetRequiredService<FeedbackService>().ScoreAsync(context.User.Id, id, ct) == TrackFeedback.Love ? 1 : 0));
        }

        return Reply(json, element);
    }
}
