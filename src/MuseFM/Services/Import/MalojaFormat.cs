using System.Text.Encodings.Web;
using System.Text.Json;

namespace MuseFM.Services.Import;

/// <summary>
/// Maloja's JSON export (maloja_export_*.json); the read API (<see cref="MalojaClient"/>) sends scrobbles in the same shape:
/// { "maloja": {...}, "scrobbles": [ { "time", "track": { "artists", "title", "album": { "albumtitle", "artists" }, "length" }, "duration", "origin" } ] }
/// </summary>
public static class MalojaFormat
{
    public static IEnumerable<ImportRow> Read(string path)
    {
        using var stream = ImportLimits.OpenDocument(path);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("scrobbles", out var scrobbles)
            || scrobbles.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("This file is not a Maloja export (no \"scrobbles\" list found).");

        foreach (var scrobble in scrobbles.EnumerateArray())
            yield return ParseScrobble(scrobble);
    }

    internal static ImportRow ParseScrobble(JsonElement scrobble)
    {
        if (scrobble.ValueKind != JsonValueKind.Object) return ImportRow.Fail("Entry is not an object.");
        if (!JsonValues.TryGetUnixTime(scrobble, "time", out var time)) return ImportRow.Fail("Entry has no \"time\".");
        if (!scrobble.TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Object)
            return ImportRow.Fail($"Scrobble at {time} has no track.");

        var title = JsonValues.GetString(track, "title");
        var artists = JsonValues.GetStringList(track, "artists");
        if (string.IsNullOrWhiteSpace(title) || artists.Count == 0)
            return ImportRow.Fail($"Scrobble at {time} has no title or artists.");

        string? album = null;
        IReadOnlyList<string> albumArtists = [];
        if (track.TryGetProperty("album", out var albumElement) && albumElement.ValueKind == JsonValueKind.Object)
        {
            album = JsonValues.GetString(albumElement, "albumtitle")
                    ?? JsonValues.GetString(albumElement, "name")
                    ?? JsonValues.GetString(albumElement, "title");
            albumArtists = JsonValues.GetStringList(albumElement, "artists");
        }

        return ImportRow.Ok(new ScrobbleInput
        {
            Title = title,
            Artists = artists,
            Album = album,
            AlbumArtists = albumArtists,
            ListenedAt = time,
            LengthSeconds = JsonValues.GetSeconds(track, "length"),
            DurationSeconds = JsonValues.GetSeconds(scrobble, "duration"),
            Origin = JsonValues.GetString(scrobble, "origin") ?? "import:maloja",
        });
    }

    public static async Task WriteAsync(IAsyncEnumerable<IReadOnlyList<ExportScrobble>> batches, Stream output, CancellationToken ct)
    {
        await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        writer.WriteStartObject();
        writer.WriteStartObject("maloja");
        writer.WriteNumber("export_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        writer.WriteString("exported_by", "MuseFM");
        writer.WriteEndObject();
        writer.WriteStartArray("scrobbles");

        await foreach (var batch in batches.WithCancellation(ct))
        {
            foreach (var s in batch)
            {
                writer.WriteStartObject();
                writer.WriteNumber("time", s.ListenedAt);
                writer.WriteStartObject("track");
                WriteStrings(writer, "artists", s.Artists);
                writer.WriteString("title", s.Title);
                if (s.Album is null)
                {
                    writer.WriteNull("album");
                }
                else
                {
                    writer.WriteStartObject("album");
                    writer.WriteString("albumtitle", s.Album);
                    WriteStrings(writer, "artists", s.AlbumArtists);
                    writer.WriteEndObject();
                }

                WriteNumberOrNull(writer, "length", s.LengthSeconds);
                writer.WriteEndObject();
                WriteNumberOrNull(writer, "duration", s.DurationSeconds);
                writer.WriteString("origin", s.Origin ?? "client:musefm");
                writer.WriteEndObject();
            }

            await writer.FlushAsync(ct);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(ct);
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static void WriteNumberOrNull(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } v) writer.WriteNumber(name, v);
        else writer.WriteNull(name);
    }
}
