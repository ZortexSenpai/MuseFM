using System.IO.Compression;
using System.Text.Json;

namespace MuseFM.Services.Import;

/// <summary>
/// Spotify's streaming history, as the .zip Spotify sends or as one of the .json files in it. Both of its exports are read:
/// the extended streaming history (Streaming_History_Audio_*.json, formerly endsong_*.json), which is the complete history:
/// [ { "ts": "2023-05-01T12:34:56Z", "ms_played", "master_metadata_track_name", "master_metadata_album_artist_name", "master_metadata_album_album_name", ... } ]
/// and the account data (StreamingHistory_music_*.json, formerly StreamingHistory*.json), which covers the last year:
/// [ { "endTime": "2023-05-01 12:34", "artistName", "trackName", "msPlayed" } ]
/// Spotify lists everything that was started, so plays of under 30 seconds - what Spotify itself does not count as a
/// stream - are left out, and so are podcast episodes, audiobooks and videos without a track name.
/// </summary>
public static class SpotifyFormat
{
    /// <summary>Spotify counts a stream after 30 seconds.</summary>
    public const int MinPlayedMs = 30_000;

    private static readonly string[] HistoryFilePrefixes = ["Streaming_History", "StreamingHistory", "endsong"];

    /// <summary>Whether a file in a .zip is, by its name, one of the streaming history files of a Spotify export.</summary>
    public static bool IsHistoryFile(string entryName)
    {
        var name = entryName[(entryName.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
               && HistoryFilePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether the start of a .json file looks like a streaming history of either export.</summary>
    public static bool LooksLikeHistory(string head) =>
        head.Contains("\"master_metadata_track_name\"") || (head.Contains("\"endTime\"") && head.Contains("\"msPlayed\""));

    /// <param name="maxDocumentBytes">Limit for a .json file, on its own or in a .zip: each is read in one piece.</param>
    public static IEnumerable<ImportRow> Read(
        string path, long maxDocumentBytes = ImportLimits.MaxDocumentBytes, long maxArchiveBytes = ImportLimits.MaxArchiveBytes)
    {
        if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var file = ImportLimits.OpenDocument(path, maxDocumentBytes);
            using var document = JsonDocument.Parse(file);
            foreach (var row in ReadDocument(document)) yield return row;
            yield break;
        }

        using var archive = ZipFile.OpenRead(path);
        long unpacked = 0;
        var found = false;
        foreach (var entry in archive.Entries)
        {
            if (!IsHistoryFile(entry.FullName)) continue;
            found = true;
            JsonDocument document;
            using (var stream = ImportLimits.OpenEntry(entry, maxDocumentBytes, maxArchiveBytes, unpacked))
            {
                document = JsonDocument.Parse(stream);
                unpacked += stream.BytesRead;
            }

            using (document)
            {
                foreach (var row in ReadDocument(document)) yield return row;
            }
        }

        if (!found)
            throw new InvalidDataException("This archive has no Spotify streaming history in it (files named Streaming_History_….json or StreamingHistory….json).");
    }

    private static IEnumerable<ImportRow> ReadDocument(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("This file is not a Spotify streaming history (it is not a list of plays).");

        foreach (var play in document.RootElement.EnumerateArray())
        {
            if (Parse(play) is { } row) yield return row;
        }
    }

    /// <summary>One play; null for what is left out: short plays and everything that is not a track.</summary>
    internal static ImportRow? Parse(JsonElement play)
    {
        if (play.ValueKind != JsonValueKind.Object) return ImportRow.Fail("Entry is not an object.");

        var extended = play.TryGetProperty("ts", out _);
        var title = JsonValues.GetString(play, extended ? "master_metadata_track_name" : "trackName");
        var artist = JsonValues.GetString(play, extended ? "master_metadata_album_artist_name" : "artistName");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return null;
        // What the account data calls a track Spotify no longer knows.
        if (title == "Unknown Track" && artist == "Unknown Artist") return null;

        if (!JsonValues.TryGetLong(play, extended ? "ms_played" : "msPlayed", out var playedMs))
            return ImportRow.Fail($"\"{title}\" does not say how long it was played.");
        if (playedMs < MinPlayedMs) return null;

        if (!JsonValues.TryGetUnixTime(play, extended ? "ts" : "endTime", out var endedAt))
            return ImportRow.Fail($"Could not read the date of \"{title}\".");

        var playedSeconds = playedMs / 1000;
        return ImportRow.Ok(new ScrobbleInput
        {
            Title = title,
            Artists = [artist],
            Album = extended ? JsonValues.GetString(play, "master_metadata_album_album_name") : null,
            // Spotify notes when a play ended; a scrobble is dated to when it began.
            ListenedAt = endedAt - playedSeconds,
            DurationSeconds = (int)Math.Min(playedSeconds, int.MaxValue),
            Origin = "import:spotify",
        });
    }
}
