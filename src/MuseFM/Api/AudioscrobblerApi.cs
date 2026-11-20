using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Api;

/// <summary>
/// Last.fm-compatible scrobbling API (Audioscrobbler 2.0, as used by GNU FM / Libre.fm clients).
/// Clients log in with auth.getMobileSession using their username and an API key (or account password)
/// as password; api_key/api_sig are accepted but not checked. Responses are XML unless format=json.
/// </summary>
public static partial class AudioscrobblerApi
{
    public const string BasePath = "/apis/audioscrobbler/2.0";
    private const int MaxBatch = 50;

    public static IEndpointRouteBuilder MapAudioscrobblerApi(this IEndpointRouteBuilder app)
    {
        // Cast to Delegate so the returned IResult is executed (a bare HttpContext handler is treated as a RequestDelegate).
        app.MapMethods(BasePath, [HttpMethods.Get, HttpMethods.Post], (Delegate)HandleAsync).DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> HandleAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        var p = await ReadParametersAsync(context.Request, ct);
        var json = string.Equals(p.GetValueOrDefault("format"), "json", StringComparison.OrdinalIgnoreCase);

        return (p.GetValueOrDefault("method") ?? "").Trim().ToLowerInvariant() switch
        {
            "auth.getmobilesession" => await GetMobileSessionAsync(p, json, context, ct),
            "track.scrobble" => await ScrobbleAsync(p, json, context.RequestServices, ct),
            "track.updatenowplaying" => await UpdateNowPlayingAsync(p, json, context.RequestServices, ct),
            "user.getinfo" => await GetUserInfoAsync(p, json, context, ct),
            "track.love" => await LoveAsync(p, json, context.RequestServices, TrackFeedback.Love, ct),
            "track.unlove" => await LoveAsync(p, json, context.RequestServices, 0, ct),
            "track.getinfo" => await GetTrackInfoAsync(p, json, context, ct),
            "user.getrecenttracks" => await GetRecentTracksAsync(p, json, context, ct),
            "user.gettopartists" => await GetTopArtistsAsync(p, json, context, ct),
            "user.gettopalbums" => await GetTopAlbumsAsync(p, json, context, ct),
            "user.gettoptracks" => await GetTopTracksAsync(p, json, context, ct),
            "user.getlovedtracks" => await GetLovedTracksAsync(p, json, context, ct),
            "user.getfriends" => await GetFriendsAsync(p, json, context, ct),
            "" => Failure(json, 6, "Invalid parameters - no method given"),
            _ => Failure(json, 3, "Invalid Method - No method with that name in this package"),
        };
    }

    private static async Task<Dictionary<string, string>> ReadParametersAsync(HttpRequest request, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Query) parameters[key] = value.ToString();
        if (!request.HasFormContentType) return parameters;

        try
        {
            var form = await request.ReadFormAsync(ct);
            foreach (var (key, value) in form) parameters[key] = value.ToString();
        }
        catch (InvalidDataException)
        {
            // Malformed form body: carry on with the query string only.
        }

        return parameters;
    }

    // ---- auth.getMobileSession ----

    private static async Task<IResult> GetMobileSessionAsync(Dictionary<string, string> p, bool json, HttpContext context, CancellationToken ct)
    {
        var username = p.GetValueOrDefault("username");
        var password = p.GetValueOrDefault("password");
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return Failure(json, 6, "Invalid parameters - username and password are required");

        var services = context.RequestServices;
        var keys = services.GetRequiredService<ApiKeyService>();
        AppUser? user = null;
        int? apiKeyId = null;

        var key = await keys.ValidateAsync(password, ct);
        if (key is not null)
        {
            user = key.User;
            apiKeyId = key.Id;
        }
        else
        {
            var users = services.GetRequiredService<UserManager<AppUser>>();
            var candidate = await users.FindByNameAsync(username);
            if (candidate is not null)
            {
                var signIn = services.GetRequiredService<SignInManager<AppUser>>();
                var result = await signIn.CheckPasswordSignInAsync(candidate, password, lockoutOnFailure: true);
                if (result.Succeeded) user = candidate;
            }
        }

        if (user is null)
            return Failure(json, 4, "Authentication Failed - You do not have permissions to access the service", 403);

        var client = context.Request.Headers.UserAgent.ToString();
        var sessionKey = await keys.CreateSessionAsync(user.Id, apiKeyId, string.IsNullOrWhiteSpace(client) ? "Last.fm API client" : client, ct);

        if (json)
        {
            return Results.Json(new JsonObject
            {
                ["session"] = new JsonObject { ["name"] = user.UserName, ["key"] = sessionKey, ["subscriber"] = 0 },
            });
        }

        return Ok(new XElement("session",
            new XElement("name", user.UserName),
            new XElement("key", sessionKey),
            new XElement("subscriber", 0)));
    }

    // ---- track.scrobble ----

    private sealed record ScrobbleEntry(string? Artist, string? Track, string? Album, string? AlbumArtist, string? Timestamp, string? Duration, string? Mbid = null);

    [GeneratedRegex(@"^(?<name>[A-Za-z]+)\[(?<index>\d{1,3})\]$")]
    private static partial Regex IndexedParameter();

    private static async Task<IResult> ScrobbleAsync(Dictionary<string, string> p, bool json, IServiceProvider services, CancellationToken ct)
    {
        var session = await services.GetRequiredService<ApiKeyService>().ValidateSessionAsync(p.GetValueOrDefault("sk"), ct);
        if (session is null) return InvalidSession(json);

        var entries = ReadEntries(p);
        if (entries.Count == 0) return Failure(json, 6, "Invalid parameters - artist, track and timestamp are required");
        if (entries.Count > MaxBatch) return Failure(json, 6, $"Invalid parameters - at most {MaxBatch} scrobbles per request");

        var inputs = entries.Select(e => ToInput(e, session.Client)).ToList();
        var outcomes = await services.GetRequiredService<ScrobbleService>().IngestAsync(session.UserId, inputs, ct);

        // Duplicates are reported as accepted so client queues are cleared instead of retrying forever.
        var codes = entries.Select((e, i) => outcomes[i] == ScrobbleOutcome.Invalid ? IgnoredCode(e) : 0).ToList();
        var accepted = codes.Count(c => c == 0);
        var ignored = codes.Count - accepted;

        if (json)
        {
            var items = entries.Select((e, i) => ScrobbleJson(e, codes[i])).ToList();
            // Last.fm returns an object rather than an array for a single scrobble.
            JsonNode scrobble = items.Count == 1 ? items[0] : new JsonArray(items.ToArray<JsonNode?>());
            return Results.Json(new JsonObject
            {
                ["scrobbles"] = new JsonObject
                {
                    ["scrobble"] = scrobble,
                    ["@attr"] = new JsonObject { ["accepted"] = accepted, ["ignored"] = ignored },
                },
            });
        }

        return Ok(new XElement("scrobbles",
            new XAttribute("accepted", accepted),
            new XAttribute("ignored", ignored),
            entries.Select((e, i) => new XElement("scrobble", EntryXml(e, codes[i]), new XElement("timestamp", e.Timestamp ?? "")))));
    }

    private static List<ScrobbleEntry> ReadEntries(Dictionary<string, string> p)
    {
        var groups = new SortedDictionary<int, Dictionary<string, string>>();
        foreach (var (key, value) in p)
        {
            var match = IndexedParameter().Match(key);
            if (!match.Success) continue;
            var index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
            if (!groups.TryGetValue(index, out var group))
                groups[index] = group = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            group[match.Groups["name"].Value] = value;
        }

        if (groups.Count == 0 && (p.ContainsKey("artist") || p.ContainsKey("track")))
            groups[0] = p;

        return groups.Values.Select(g => new ScrobbleEntry(
            g.GetValueOrDefault("artist"),
            g.GetValueOrDefault("track"),
            g.GetValueOrDefault("album"),
            g.GetValueOrDefault("albumArtist"),
            g.GetValueOrDefault("timestamp"),
            g.GetValueOrDefault("duration"),
            g.GetValueOrDefault("mbid"))).ToList();
    }

    private static ScrobbleInput ToInput(ScrobbleEntry e, string? client) => new()
    {
        Title = e.Track ?? "",
        Artists = [e.Artist ?? ""],
        Album = e.Album,
        AlbumArtists = string.IsNullOrWhiteSpace(e.AlbumArtist) ? null : new[] { e.AlbumArtist },
        ListenedAt = long.TryParse(e.Timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts) ? ts : 0,
        LengthSeconds = int.TryParse(e.Duration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null,
        Origin = client ?? "audioscrobbler",
        TrackMbid = e.Mbid,
    };

    private static int IgnoredCode(ScrobbleEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.Artist)) return 1;
        if (string.IsNullOrWhiteSpace(e.Track)) return 2;
        if (!long.TryParse(e.Timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts) || ts <= 0) return 3;
        return ts > DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() ? 4 : 1;
    }

    private static string IgnoredMessage(int code) => code switch
    {
        0 => "",
        1 => "Artist was ignored",
        2 => "Track was ignored",
        3 => "Timestamp was too old",
        4 => "Timestamp was too new",
        _ => "Scrobble was ignored",
    };

    // ---- track.updateNowPlaying ----

    private static async Task<IResult> UpdateNowPlayingAsync(Dictionary<string, string> p, bool json, IServiceProvider services, CancellationToken ct)
    {
        var session = await services.GetRequiredService<ApiKeyService>().ValidateSessionAsync(p.GetValueOrDefault("sk"), ct);
        if (session is null) return InvalidSession(json);

        var entry = ReadEntries(p).FirstOrDefault();
        if (entry is null || string.IsNullOrWhiteSpace(entry.Artist) || string.IsNullOrWhiteSpace(entry.Track))
            return Failure(json, 6, "Invalid parameters - artist and track are required");

        services.GetRequiredService<NowPlayingStore>().Set(session.UserId, ToInput(entry, session.Client));

        if (json)
        {
            var node = ScrobbleJson(entry, 0);
            node.Remove("timestamp");
            return Results.Json(new JsonObject { ["nowplaying"] = node });
        }

        return Ok(new XElement("nowplaying", EntryXml(entry, 0)));
    }

    // ---- track.love / track.unlove ----

    /// <summary>Loves (score 1) or unloves (0) a track by name. Like Last.fm, a track that was never scrobbled is added.</summary>
    private static async Task<IResult> LoveAsync(Dictionary<string, string> p, bool json, IServiceProvider services, int score, CancellationToken ct)
    {
        var session = await services.GetRequiredService<ApiKeyService>().ValidateSessionAsync(p.GetValueOrDefault("sk"), ct);
        if (session is null) return InvalidSession(json);

        var artist = p.GetValueOrDefault("artist");
        var track = p.GetValueOrDefault("track");
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(track))
            return Failure(json, 6, "Invalid parameters - artist and track are required");

        var ids = await services.GetRequiredService<ScrobbleService>()
            .ResolveTracksAsync([new TrackRef(track, [artist], p.GetValueOrDefault("mbid"))], create: score != 0, ct);
        if (ids[0] is { } trackId)
            await services.GetRequiredService<FeedbackService>().SetAsync(session.UserId, trackId, score, ct: ct);
        return json ? Results.Json(new JsonObject()) : Xml(new XElement("lfm", new XAttribute("status", "ok")));
    }

    // ---- user.getInfo ----

    private static async Task<IResult> GetUserInfoAsync(Dictionary<string, string> p, bool json, HttpContext context, CancellationToken ct)
    {
        var services = context.RequestServices;
        var session = await services.GetRequiredService<ApiKeyService>().ValidateSessionAsync(p.GetValueOrDefault("sk"), ct);
        var name = p.GetValueOrDefault("user");
        var target = string.IsNullOrWhiteSpace(name)
            ? session?.User
            : await services.GetRequiredService<UserManager<AppUser>>().FindByNameAsync(name);
        if (target is null || (!target.IsPublic && target.Id != session?.UserId))
            return Failure(json, 6, "User not found", 404);

        var db = services.GetRequiredService<AppDbContext>();
        var playcount = await db.Scrobbles.CountAsync(s => s.UserId == target.Id, ct);
        var registered = new DateTimeOffset(DateTime.SpecifyKind(target.CreatedAt, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var request = context.Request;
        var url = $"{request.Scheme}://{request.Host}{request.PathBase}/u/{Uri.EscapeDataString(target.UserName!)}";
        var inv = CultureInfo.InvariantCulture;

        if (json)
        {
            return Results.Json(new JsonObject
            {
                ["user"] = new JsonObject
                {
                    ["name"] = target.UserName,
                    ["realname"] = "",
                    ["url"] = url,
                    ["country"] = "",
                    ["playcount"] = playcount.ToString(inv),
                    ["registered"] = new JsonObject { ["unixtime"] = registered.ToString(inv), ["#text"] = registered },
                    ["type"] = "user",
                    ["subscriber"] = "0",
                    ["image"] = new JsonArray(),
                },
            });
        }

        return Ok(new XElement("user",
            new XElement("name", target.UserName),
            new XElement("realname", ""),
            new XElement("url", url),
            new XElement("playcount", playcount),
            new XElement("registered", new XAttribute("unixtime", registered), registered),
            new XElement("type", "user"),
            new XElement("subscriber", 0)));
    }

    // ---- response helpers ----

    private static IEnumerable<XElement> EntryXml(ScrobbleEntry e, int code) =>
    [
        Corrected("track", e.Track),
        Corrected("artist", e.Artist),
        Corrected("album", e.Album),
        Corrected("albumArtist", e.AlbumArtist),
        new XElement("ignoredMessage", new XAttribute("code", code), IgnoredMessage(code)),
    ];

    private static XElement Corrected(string name, string? value) => new(name, new XAttribute("corrected", 0), value ?? "");

    private static JsonObject ScrobbleJson(ScrobbleEntry e, int code) => new()
    {
        ["track"] = CorrectedJson(e.Track),
        ["artist"] = CorrectedJson(e.Artist),
        ["album"] = CorrectedJson(e.Album),
        ["albumArtist"] = CorrectedJson(e.AlbumArtist),
        ["timestamp"] = e.Timestamp ?? "",
        ["ignoredMessage"] = new JsonObject { ["code"] = code.ToString(CultureInfo.InvariantCulture), ["#text"] = IgnoredMessage(code) },
    };

    private static JsonObject CorrectedJson(string? value) => new() { ["corrected"] = "0", ["#text"] = value ?? "" };

    private static IResult InvalidSession(bool json) => Failure(json, 9, "Invalid session key - Please re-authenticate", 403);

    private static IResult Failure(bool json, int code, string message, int status = 400)
    {
        if (json) return Results.Json(new { error = code, message }, statusCode: status);
        return Xml(new XElement("lfm", new XAttribute("status", "failed"),
            new XElement("error", new XAttribute("code", code), message)), status);
    }

    private static IResult Ok(XElement content) => Xml(new XElement("lfm", new XAttribute("status", "ok"), content));

    private static IResult Xml(XElement root, int status = 200) =>
        Results.Text(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + root.ToString(SaveOptions.DisableFormatting),
            "text/xml",
            Encoding.UTF8,
            status);
}
