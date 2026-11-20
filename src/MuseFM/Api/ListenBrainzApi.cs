using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;
using MuseFM.Data.Entities;
using MuseFM.Services;
using MuseFM.Services.Import;
using MuseFM.Services.Metadata;

namespace MuseFM.Api;

/// <summary>
/// ListenBrainz-compatible API (https://listenbrainz.readthedocs.io/en/latest/users/api/core.html).
/// Clients use "{host}/apis/listenbrainz" as the API root and an API key as the token.
/// </summary>
public static class ListenBrainzApi
{
    public const string BasePath = "/apis/listenbrainz";
    private const int DefaultCount = 25;
    private const int MaxCount = 1000;

    /// <summary>
    /// Listen responses leave out values we don't have, like ListenBrainz does, rather than sending null: clients such as
    /// multi-scrobbler destructure fields with JS defaults (<c>artist_mbids = []</c>), which only apply to missing keys.
    /// </summary>
    private static readonly JsonSerializerOptions ListenJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapListenBrainzApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(BasePath + "/1").DisableAntiforgery();
        api.MapGet("/validate-token", ValidateTokenAsync);
        api.MapPost("/submit-listens", SubmitListensAsync);
        api.MapGet("/user/{username}/listens", GetListensAsync);
        api.MapGet("/user/{username}/listen-count", GetListenCountAsync);
        api.MapGet("/user/{username}/playing-now", GetPlayingNowAsync);
        api.MapPost("/feedback/recording-feedback", SubmitFeedbackAsync);
        api.MapGet("/feedback/user/{username}/get-feedback", GetFeedbackAsync);
        return app;
    }

    private static IResult Error(int code, string message) => Results.Json(new { code, error = message }, statusCode: code);

    /// <summary>Reads "Authorization: Token &lt;key&gt;" (or Bearer).</summary>
    internal static string? ReadToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString().Trim();
        if (header.Length == 0) return null;
        var parts = header.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && (parts[0].Equals("Token", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase)))
            return parts[1];
        return null;
    }

    private static async Task<IResult> ValidateTokenAsync(HttpRequest request, ApiKeyService keys, CancellationToken ct)
    {
        var token = request.Query["token"].FirstOrDefault() ?? ReadToken(request);
        if (string.IsNullOrWhiteSpace(token)) return Error(400, "You need to provide an Authorization token.");

        var key = await keys.ValidateAsync(token, ct);
        return key is null
            ? Results.Json(new { code = 200, message = "Token invalid.", valid = false })
            : Results.Json(new { code = 200, message = "Token valid.", valid = true, user_name = key.User.UserName });
    }

    private static async Task<IResult> SubmitListensAsync(
        HttpRequest request, ApiKeyService keys, ScrobbleService scrobbles, NowPlayingStore nowPlaying, CancellationToken ct)
    {
        var key = await keys.ValidateAsync(ReadToken(request), ct);
        if (key is null) return Error(401, "Invalid authorization token.");

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return Error(400, "Cannot parse JSON document.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("listen_type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Array)
                return Error(400, "Invalid JSON document submitted: listen_type and payload are required.");

            var listenType = typeElement.GetString();
            var count = payload.GetArrayLength();
            if (listenType is not ("single" or "playing_now" or "import"))
                return Error(400, "Invalid listen_type.");
            if (listenType is "single" or "playing_now" && count != 1)
                return Error(400, "JSON document should contain exactly one listen for this listen_type.");
            if (count == 0)
                return Error(400, "JSON document should contain at least one listen.");

            if (listenType == "playing_now")
            {
                if (!ListenBrainzFormat.TryParse(payload[0], requireTimestamp: false, out var playing, out var error))
                    return Error(400, error);
                nowPlaying.Set(key.UserId, playing);
                return Results.Json(new { status = "ok" });
            }

            var inputs = new List<ScrobbleInput>(count);
            foreach (var listen in payload.EnumerateArray())
            {
                if (!ListenBrainzFormat.TryParse(listen, requireTimestamp: true, out var input, out var error))
                    return Error(400, error);
                inputs.Add(input with { Origin = input.Origin ?? key.Name });
            }

            var outcomes = await scrobbles.IngestAsync(key.UserId, inputs, ct);
            if (listenType == "single" && outcomes[0] == ScrobbleOutcome.Invalid)
                return Error(400, "Listen could not be stored: missing metadata or an invalid timestamp.");
            return Results.Json(new { status = "ok" });
        }
    }

    private static async Task<IResult> GetListensAsync(
        string username, HttpRequest request, UserManager<AppUser> users, ApiKeyService keys, StatsService stats,
        int? count, long? max_ts, long? min_ts, CancellationToken ct)
    {
        var user = await FindVisibleUserAsync(username, request, users, keys, ct);
        if (user is null) return Error(404, $"Cannot find user: {username}");
        // Both together ask for a time range, newest first; multi-scrobbler checks for an existing scrobble this way.
        if (max_ts is not null && min_ts is not null && min_ts >= max_ts)
            return Error(400, "min_ts should be less than max_ts");

        var rows = await stats.ListensAsync(user.Id, Math.Clamp(count ?? DefaultCount, 1, MaxCount), max_ts, min_ts, ct);
        var latest = await stats.LastScrobbleAsync(user.Id, ct);
        var artistMbids = await stats.ArtistMbidsAsync(rows.SelectMany(r => r.Artists.Select(a => a.Id)), ct);
        return Results.Json(new
        {
            payload = new
            {
                count = rows.Count,
                latest_listen_ts = latest,
                user_id = user.UserName,
                listens = rows.Select(r => new
                {
                    listened_at = r.ListenedAt,
                    inserted_at = r.ListenedAt,
                    user_name = user.UserName,
                    track_metadata = TrackMetadata(r.Title, r.Artists.Select(a => a.Name).ToList(), r.Album, r.LengthSeconds, r.Origin,
                        r.TrackMbid, r.AlbumMbid, ArtistMbids(r.Artists, artistMbids)),
                }),
            },
        }, ListenJson);
    }

    private static async Task<IResult> GetListenCountAsync(
        string username, HttpRequest request, UserManager<AppUser> users, ApiKeyService keys, StatsService stats, CancellationToken ct)
    {
        var user = await FindVisibleUserAsync(username, request, users, keys, ct);
        if (user is null) return Error(404, $"Cannot find user: {username}");
        return Results.Json(new { payload = new { count = await stats.CountAsync(user.Id, ct: ct) } });
    }

    private static async Task<IResult> GetPlayingNowAsync(
        string username, HttpRequest request, UserManager<AppUser> users, ApiKeyService keys, NowPlayingStore nowPlaying, CancellationToken ct)
    {
        var user = await FindVisibleUserAsync(username, request, users, keys, ct);
        if (user is null) return Error(404, $"Cannot find user: {username}");

        var playing = nowPlaying.Get(user.Id);
        object[] listens = playing is null
            ? []
            :
            [
                new
                {
                    playing_now = true,
                    track_metadata = TrackMetadata(playing.Title, playing.Artists, playing.Album, playing.LengthSeconds, playing.Origin),
                },
            ];
        return Results.Json(new
        {
            payload = new { count = listens.Length, playing_now = true, user_id = user.UserName, listens },
        }, ListenJson);
    }

    /// <summary>Artist MBIDs in credit order, or null unless every artist has one (so the list lines up with artist_names).</summary>
    private static IReadOnlyList<string>? ArtistMbids(IReadOnlyList<ArtistRef> artists, IReadOnlyDictionary<int, string> mbids)
    {
        var list = artists.Select(a => mbids.GetValueOrDefault(a.Id)).OfType<string>().ToList();
        return list.Count > 0 && list.Count == artists.Count ? list : null;
    }

    private static object TrackMetadata(
        string title, IReadOnlyList<string> artists, string? album, int? lengthSeconds, string? origin,
        string? recordingMbid = null, string? releaseMbid = null, IReadOnlyList<string>? artistMbids = null) => new
    {
        artist_name = MetadataParser.Credit(artists),
        track_name = title,
        release_name = album,
        additional_info = new
        {
            artist_names = artists,
            duration_ms = lengthSeconds * 1000,
            submission_client = origin,
            recording_mbid = recordingMbid,
            release_mbid = releaseMbid,
            artist_mbids = artistMbids,
        },
    };

    // ---- feedback ----

    /// <summary>
    /// Love (1), hate (-1) or clear (0) a recording. ListenBrainz clients name it by MusicBrainz id; feedback for a
    /// recording that was never scrobbled with that id (or only by MessyBrainz id) is accepted but can't be stored.
    /// </summary>
    private static async Task<IResult> SubmitFeedbackAsync(
        HttpRequest request, ApiKeyService keys, FeedbackService feedback, ILoggerFactory loggers, CancellationToken ct)
    {
        var key = await keys.ValidateAsync(ReadToken(request), ct);
        if (key is null) return Error(401, "Invalid authorization token.");

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return Error(400, "Cannot parse JSON document.");
        }

        using (document)
        {
            var root = document.RootElement;
            var mbidText = JsonValues.GetString(root, "recording_mbid");
            var msid = JsonValues.GetString(root, "recording_msid");
            if (!JsonValues.TryGetLong(root, "score", out var score) || score is < -1 or > 1)
                return Error(400, "Score must be -1, 0 or 1.");
            if (string.IsNullOrWhiteSpace(mbidText) && string.IsNullOrWhiteSpace(msid))
                return Error(400, "JSON document must contain either recording_msid or recording_mbid, and score top level keys");
            var mbid = MetadataText.Mbid(mbidText);
            if (mbidText is not null && mbid is null) return Error(400, "recording_mbid is not a valid UUID.");

            if (mbid is not null && await feedback.TrackByMbidAsync(mbid, ct) is { } trackId)
                await feedback.SetAsync(key.UserId, trackId, (int)score, ct: ct);
            else
                loggers.CreateLogger("MuseFM.ListenBrainz").LogInformation("Feedback for unknown recording {Mbid} {Msid} was not stored", mbid, msid);
            return Results.Json(new { status = "ok" });
        }
    }

    private static async Task<IResult> GetFeedbackAsync(
        string username, HttpRequest request, UserManager<AppUser> users, ApiKeyService keys, FeedbackService feedback,
        int? score, int? count, int? offset, string? metadata, CancellationToken ct)
    {
        var user = await FindVisibleUserAsync(username, request, users, keys, ct);
        if (user is null) return Error(404, $"Cannot find user: {username}");
        if (score is not (null or 1 or -1)) return Error(400, "Score must be -1 or 1.");

        var skip = Math.Max(0, offset ?? 0);
        var (items, total) = await feedback.SliceAsync(user.Id, score ?? 0, skip, Math.Clamp(count ?? 100, 1, MaxCount), ct);
        var withMetadata = string.Equals(metadata, "true", StringComparison.OrdinalIgnoreCase);
        return Results.Json(new
        {
            feedback = items.Select(f => new
            {
                created = f.CreatedAt,
                recording_mbid = f.MusicBrainzId,
                recording_msid = (string?)null,
                score = f.Score,
                user_id = user.UserName,
                track_metadata = withMetadata ? TrackMetadata(f.Title, f.Artists.Select(a => a.Name).ToList(), null, null, null, f.MusicBrainzId) : null,
            }),
            count = items.Count,
            offset = skip,
            total_count = total,
        });
    }

    private static async Task<AppUser?> FindVisibleUserAsync(
        string username, HttpRequest request, UserManager<AppUser> users, ApiKeyService keys, CancellationToken ct)
    {
        var user = await users.FindByNameAsync(username);
        if (user is null) return null;
        if (user.IsPublic) return user;
        var key = await keys.ValidateAsync(ReadToken(request), ct);
        return key?.UserId == user.Id ? user : null;
    }
}
