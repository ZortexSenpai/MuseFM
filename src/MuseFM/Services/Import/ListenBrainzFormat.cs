using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using MuseFM.Data.Entities;
using MuseFM.Services.Metadata;

namespace MuseFM.Services.Import;

/// <summary>Recording feedback from a ListenBrainz export: a MusicBrainz recording id, track names, or both.</summary>
public sealed record FeedbackImport(string? RecordingMbid, TrackRef? Track, int Score, long CreatedAt);

/// <summary>ListenBrainz listen objects: used by the submit API, for importing ListenBrainz exports and for exporting.</summary>
public static class ListenBrainzFormat
{
    public const string ListensEntry = "listens.jsonl";
    public const string FeedbackEntry = "feedback.jsonl";

    public static bool TryParse(
        JsonElement listen,
        bool requireTimestamp,
        [NotNullWhen(true)] out ScrobbleInput? input,
        [NotNullWhen(false)] out string? error)
    {
        input = null;
        error = null;
        if (listen.ValueKind != JsonValueKind.Object)
        {
            error = "Each listen must be a JSON object.";
            return false;
        }

        long listenedAt = 0;
        if (listen.TryGetProperty("listened_at", out var timestamp) && timestamp.ValueKind != JsonValueKind.Null)
        {
            if (!JsonValues.TryGetUnixTime(listen, "listened_at", out listenedAt) || listenedAt <= 0)
            {
                error = "Value for key listened_at is invalid.";
                return false;
            }
        }
        else if (requireTimestamp)
        {
            error = "JSON document must contain the key listened_at at the top level.";
            return false;
        }

        if (!listen.TryGetProperty("track_metadata", out var meta) || meta.ValueKind != JsonValueKind.Object)
        {
            error = "JSON document must contain the key track_metadata at the top level.";
            return false;
        }

        var artistName = JsonValues.GetString(meta, "artist_name");
        var trackName = JsonValues.GetString(meta, "track_name");
        if (string.IsNullOrWhiteSpace(artistName))
        {
            error = "JSON document does not contain required track_metadata.artist_name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(trackName))
        {
            error = "JSON document does not contain required track_metadata.track_name.";
            return false;
        }

        var info = meta.TryGetProperty("additional_info", out var additional) ? additional : default;
        // Exports carry ListenBrainz's own matching in mbid_mapping; what the client sent in additional_info wins.
        var mapping = JsonValues.Child(meta, "mbid_mapping");
        var artistMbids = JsonValues.GetStringList(info, "artist_mbids");
        if (artistMbids.Count == 0) artistMbids = JsonValues.GetStringList(mapping, "artist_mbids");
        var artists = JsonValues.GetStringList(info, "artist_names");
        var albumArtists = JsonValues.GetStringList(info, "release_artist_names");
        if (albumArtists.Count == 0) albumArtists = JsonValues.GetStringList(info, "release_artist_name");

        int? length = null;
        if (JsonValues.TryGetLong(info, "duration_ms", out var ms) && ms > 0)
            length = (int)Math.Min(ms / 1000, int.MaxValue);
        else if (JsonValues.TryGetLong(info, "duration", out var seconds) && seconds > 0)
            length = (int)Math.Min(seconds, int.MaxValue);

        input = new ScrobbleInput
        {
            Title = trackName,
            Artists = artists.Count > 0 ? artists : [artistName],
            Album = JsonValues.GetString(meta, "release_name"),
            AlbumArtists = albumArtists,
            ListenedAt = listenedAt,
            LengthSeconds = length,
            Origin = JsonValues.GetString(info, "submission_client")
                     ?? JsonValues.GetString(info, "media_player")
                     ?? JsonValues.GetString(info, "music_service_name"),
            TrackMbid = JsonValues.GetString(info, "recording_mbid") ?? JsonValues.GetString(mapping, "recording_mbid"),
            AlbumMbid = JsonValues.GetString(info, "release_mbid") ?? JsonValues.GetString(mapping, "release_mbid"),
            ArtistMbids = artistMbids,
        };
        return true;
    }

    /// <summary>Reads the recording feedback (loves and hates) of a ListenBrainz export .zip; nothing for other files.</summary>
    public static IEnumerable<FeedbackImport> ReadFeedback(
        string path, long maxEntryBytes = ImportLimits.MaxDocumentBytes, long maxArchiveBytes = ImportLimits.MaxArchiveBytes)
    {
        if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)) yield break;
        using var archive = ZipFile.OpenRead(path);
        long unpacked = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.ToLowerInvariant();
            if (!name.Contains("feedback") || !(name.EndsWith(".jsonl") || name.EndsWith(".json"))) continue;
            using var stream = Unpack(entry, maxEntryBytes, maxArchiveBytes, unpacked);
            using var counted = new Unpacked(stream, bytes => unpacked += bytes);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                FeedbackImport? row;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    row = ParseFeedback(document.RootElement);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (row is not null) yield return row;
            }
        }
    }

    private static FeedbackImport? ParseFeedback(JsonElement item)
    {
        if (!JsonValues.TryGetLong(item, "score", out var score) || score is not (1 or -1)) return null;
        var mbid = MetadataText.Mbid(JsonValues.GetString(item, "recording_mbid"));
        TrackRef? track = null;
        var meta = JsonValues.Child(item, "track_metadata");
        if (JsonValues.GetString(meta, "track_name") is { } title && JsonValues.GetString(meta, "artist_name") is { } artist)
        {
            var names = JsonValues.GetStringList(JsonValues.Child(meta, "additional_info"), "artist_names");
            track = new TrackRef(title, names.Count > 0 ? names : [artist], mbid);
        }

        if (mbid is null && track is null) return null;
        var created = JsonValues.TryGetUnixTime(item, "created", out var at) ? at : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new FeedbackImport(mbid, track, (int)score, created);
    }

    /// <summary>
    /// Writes a .zip in the layout of a ListenBrainz export: listens (with MusicBrainz ids where known) as JSON lines, and
    /// loves and hates as recording feedback with the track's names, so it can be read back without MusicBrainz.
    /// </summary>
    public static async Task WriteAsync(
        IAsyncEnumerable<IReadOnlyList<ExportScrobble>> batches, IReadOnlyList<ExportFeedback> feedback, string userName, Stream output, CancellationToken ct)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var options = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        await using (var stream = archive.CreateEntry(ListensEntry, CompressionLevel.Optimal).Open())
        {
            await foreach (var batch in batches.WithCancellation(ct))
            {
                foreach (var s in batch)
                {
                    await using var writer = new Utf8JsonWriter(stream, options);
                    writer.WriteStartObject();
                    writer.WriteNumber("listened_at", s.ListenedAt);
                    writer.WriteString("user_name", userName);
                    WriteTrackMetadata(writer, s.Title, s.Artists, s.ArtistMbids ?? [], s.TrackMbid, w =>
                    {
                        if (s.Album is not null) w.WriteString("release_name", s.Album);
                    }, w =>
                    {
                        if (s.AlbumArtists.Count > 0) WriteStrings(w, "release_artist_names", s.AlbumArtists);
                        if (s.AlbumMbid is not null) w.WriteString("release_mbid", s.AlbumMbid);
                        if (s.LengthSeconds is { } length) w.WriteNumber("duration_ms", length * 1000L);
                        if (s.Origin is not null) w.WriteString("submission_client", s.Origin);
                    });
                    writer.WriteEndObject();
                    await writer.FlushAsync(ct);
                    stream.WriteByte((byte)'\n');
                }
            }
        }

        await using (var stream = archive.CreateEntry(FeedbackEntry, CompressionLevel.Optimal).Open())
        {
            foreach (var f in feedback)
            {
                await using var writer = new Utf8JsonWriter(stream, options);
                writer.WriteStartObject();
                writer.WriteNumber("created", f.CreatedAt);
                writer.WriteString("user_id", userName);
                writer.WriteNumber("score", f.Score);
                if (f.TrackMbid is not null) writer.WriteString("recording_mbid", f.TrackMbid);
                else writer.WriteNull("recording_mbid");
                WriteTrackMetadata(writer, f.Title, f.Artists, [], null, _ => { }, _ => { });
                writer.WriteEndObject();
                await writer.FlushAsync(ct);
                stream.WriteByte((byte)'\n');
            }
        }
    }

    private static void WriteTrackMetadata(
        Utf8JsonWriter writer, string title, IReadOnlyList<string> artists, IReadOnlyList<string?> artistMbids, string? trackMbid,
        Action<Utf8JsonWriter> release, Action<Utf8JsonWriter> additional)
    {
        writer.WriteStartObject("track_metadata");
        writer.WriteString("artist_name", MetadataParser.Credit(artists));
        writer.WriteString("track_name", title);
        release(writer);
        writer.WriteStartObject("additional_info");
        WriteStrings(writer, "artist_names", artists);
        if (trackMbid is not null) writer.WriteString("recording_mbid", trackMbid);
        // Only a complete list lines up with artist_names.
        if (artistMbids.Count == artists.Count && artistMbids.All(id => id is not null)) WriteStrings(writer, "artist_mbids", artistMbids!);
        additional(writer);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string?> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    /// <summary>Reads a ListenBrainz export: JSON array, JSON lines, an API response, or a .zip of those.</summary>
    /// <param name="maxDocumentBytes">Limit for JSON read in one piece and for each file in a .zip; JSON lines in a file of their own are read line by line.</param>
    public static IEnumerable<ImportRow> Read(
        string path, long maxDocumentBytes = ImportLimits.MaxDocumentBytes, long maxArchiveBytes = ImportLimits.MaxArchiveBytes)
    {
        if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            long unpacked = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.ToLowerInvariant();
                if (name.Contains("feedback") || name.Contains("pinned") || !(name.EndsWith(".jsonl") || name.EndsWith(".json")))
                    continue;
                using var stream = Unpack(entry, maxDocumentBytes, maxArchiveBytes, unpacked);
                using var counted = new Unpacked(stream, bytes => unpacked += bytes);
                foreach (var row in ReadStream(stream, skipNonListens: true, maxDocumentBytes))
                    yield return row;
            }

            yield break;
        }

        using var file = File.OpenRead(path);
        foreach (var row in ReadStream(file, skipNonListens: false, maxDocumentBytes))
            yield return row;
    }

    /// <summary>
    /// Opens a file of a .zip for reading up to a size: a small archive can unpack to gigabytes, and the sizes written
    /// in it are whatever its author wanted them to be.
    /// </summary>
    /// <param name="unpacked">What was unpacked from the archive before this file.</param>
    private static SizeLimitedStream Unpack(ZipArchiveEntry entry, long maxEntryBytes, long maxArchiveBytes, long unpacked) =>
        maxArchiveBytes - unpacked < maxEntryBytes
            ? new SizeLimitedStream(entry.Open(), Math.Max(0, maxArchiveBytes - unpacked),
                $"This archive unpacks to more than {ImportLimits.Megabytes(maxArchiveBytes)}, which is too much to import.")
            : new SizeLimitedStream(entry.Open(), maxEntryBytes,
                $"The archive contains a file of more than {ImportLimits.Megabytes(maxEntryBytes)}, which is too large to import.");

    /// <summary>Reports how much of a file was unpacked once it is done with, also when reading stopped early.</summary>
    private sealed class Unpacked(SizeLimitedStream stream, Action<long> report) : IDisposable
    {
        public void Dispose() => report(stream.BytesRead);
    }

    /// <summary>For JSON that is about to be read into memory as a whole. Files inside a .zip are limited while they are read.</summary>
    private static void EnsureFitsInMemory(Stream stream, long maxBytes)
    {
        if (stream.CanSeek && stream.Length > maxBytes) throw new InvalidDataException(ImportLimits.TooLarge(maxBytes));
    }

    private static IEnumerable<ImportRow> ReadStream(Stream stream, bool skipNonListens, long maxDocumentBytes)
    {
        using var reader = new StreamReader(stream);
        var first = (char)Math.Max(0, PeekNonWhitespace(reader));

        if (first == '[')
        {
            EnsureFitsInMemory(stream, maxDocumentBytes);
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            foreach (var listen in document.RootElement.EnumerateArray())
                yield return Parse(listen);
            yield break;
        }

        // Either JSON lines, or a single object (e.g. a saved API response with payload.listens).
        var lineNumber = 0;
        string? line;
        var buffered = new List<string>();
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // Not JSON lines: fall back to treating the whole stream as one document.
                EnsureFitsInMemory(stream, maxDocumentBytes);
                buffered.Add(line);
                buffered.Add(reader.ReadToEnd());
                break;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("payload", out var payload)
                    && payload.TryGetProperty("listens", out var listens) && listens.ValueKind == JsonValueKind.Array)
                {
                    foreach (var listen in listens.EnumerateArray())
                        yield return Parse(listen);
                }
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("track_metadata", out _))
                {
                    yield return Parse(root);
                }
                else if (!skipNonListens)
                {
                    yield return ImportRow.Fail($"Line {lineNumber} is not a listen.");
                }
            }
        }

        if (buffered.Count > 0)
        {
            using var document = JsonDocument.Parse(string.Concat(buffered));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("payload", out var payload)
                && payload.TryGetProperty("listens", out var listens) && listens.ValueKind == JsonValueKind.Array)
            {
                foreach (var listen in listens.EnumerateArray())
                    yield return Parse(listen);
            }
            else if (!skipNonListens)
            {
                throw new InvalidDataException("This file does not look like a ListenBrainz export.");
            }
        }
    }

    private static ImportRow Parse(JsonElement listen) =>
        TryParse(listen, requireTimestamp: true, out var input, out var error)
            ? ImportRow.Ok(input with { Origin = input.Origin ?? "import:listenbrainz" })
            : ImportRow.Fail(error);

    private static int PeekNonWhitespace(StreamReader reader)
    {
        while (reader.Peek() is var c and >= 0)
        {
            if (!char.IsWhiteSpace((char)c) && c != '﻿') return c;
            reader.Read();
        }

        return -1;
    }
}
