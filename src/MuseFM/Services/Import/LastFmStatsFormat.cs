using System.Text.Json;

namespace MuseFM.Services.Import;

/// <summary>
/// lastfmstats.com export (lastfmstats-USER.json):
/// { "username": "...", "scrobbles": [ { "track", "artist", "album", "albumId", "date": milliseconds } ] }
/// </summary>
public static class LastFmStatsFormat
{
    public static IEnumerable<ImportRow> Read(string path)
    {
        using var stream = ImportLimits.OpenDocument(path);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("scrobbles", out var scrobbles)
            || scrobbles.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("This file is not a lastfmstats.com export (no \"scrobbles\" list found).");

        foreach (var scrobble in scrobbles.EnumerateArray())
        {
            if (!JsonValues.TryGetUnixTime(scrobble, "date", out var time))
            {
                yield return ImportRow.Fail("Entry has no \"date\".");
                continue;
            }

            yield return ImportRow.Ok(new ScrobbleInput
            {
                Title = JsonValues.GetString(scrobble, "track") ?? "",
                Artists = [JsonValues.GetString(scrobble, "artist") ?? ""],
                Album = JsonValues.GetString(scrobble, "album"),
                // Last.fm's album id is the MusicBrainz release id when it has one; anything else is ignored.
                AlbumMbid = JsonValues.GetString(scrobble, "albumId"),
                ListenedAt = time,
                Origin = "import:lastfm",
            });
        }
    }
}
