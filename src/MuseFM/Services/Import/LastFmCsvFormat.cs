using System.Globalization;
using System.Text;

namespace MuseFM.Services.Import;

/// <summary>
/// Last.fm scrobble CSVs as produced by lastfm-to-csv (benjaminbenben.com/lastfm-to-csv):
/// either headerless "artist,album,track,date" (date like "31 Jan 2021 12:34", UTC)
/// or with a header such as "uts,utc_time,artist,artist_mbid,album,album_mbid,track,track_mbid".
/// </summary>
public static class LastFmCsvFormat
{
    private static readonly string[] DateFormats =
    [
        "dd MMM yyyy HH:mm", "d MMM yyyy HH:mm", "dd MMM yyyy, HH:mm", "d MMM yyyy, HH:mm",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ",
    ];

    public static IEnumerable<ImportRow> Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        Dictionary<string, int>? columns = null;
        var first = true;
        foreach (var row in Csv.Parse(reader))
        {
            if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0])) continue;
            if (first)
            {
                first = false;
                columns = TryReadHeader(row);
                if (columns is not null) continue;
            }

            yield return ParseRow(row, columns);
        }
    }

    private static Dictionary<string, int>? TryReadHeader(List<string> row)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < row.Count; i++) map.TryAdd(row[i].Trim(), i);
        var hasArtist = map.ContainsKey("artist") || map.ContainsKey("artist_name");
        var hasTrack = map.ContainsKey("track") || map.ContainsKey("title") || map.ContainsKey("name") || map.ContainsKey("track_name");
        return hasArtist && hasTrack ? map : null;
    }

    private static ImportRow ParseRow(List<string> row, Dictionary<string, int>? columns)
    {
        string? artist, album, track, date;
        string? artistMbid = null, albumMbid = null, trackMbid = null;
        if (columns is null)
        {
            if (row.Count < 4)
                return ImportRow.Fail($"Expected 4 columns (artist, album, track, date) but found {row.Count}.");
            (artist, album, track, date) = (row[0], row[1], row[2], row[3]);
        }
        else
        {
            artist = Get("artist", "artist_name");
            album = Get("album", "album_name", "release_name");
            track = Get("track", "title", "name", "track_name");
            date = Get("uts", "timestamp", "listened_at", "date", "utc_time", "time");
            artistMbid = Get("artist_mbid");
            albumMbid = Get("album_mbid", "release_mbid");
            trackMbid = Get("track_mbid", "recording_mbid", "mbid");
        }

        if (!TryParseTime(date, out var timestamp))
            return ImportRow.Fail($"Could not read the date \"{date}\" for \"{track}\".");

        return ImportRow.Ok(new ScrobbleInput
        {
            Title = track ?? "",
            Artists = [artist ?? ""],
            Album = album,
            ListenedAt = timestamp,
            Origin = "import:lastfm",
            TrackMbid = trackMbid,
            AlbumMbid = albumMbid,
            ArtistMbids = artistMbid is null ? null : [artistMbid],
        });

        string? Get(params string[] names)
        {
            foreach (var name in names)
            {
                if (columns!.TryGetValue(name, out var index) && index < row.Count && !string.IsNullOrWhiteSpace(row[index]))
                    return row[index];
            }

            return null;
        }
    }

    public static bool TryParseTime(string? value, out long timestamp)
    {
        timestamp = 0;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            timestamp = JsonValues.NormalizeUnix(number);
            return timestamp > 0;
        }

        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, styles, out var exact)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, styles, out exact))
        {
            timestamp = new DateTimeOffset(exact, TimeSpan.Zero).ToUnixTimeSeconds();
            return timestamp > 0;
        }

        return false;
    }

    public static async Task WriteAsync(IAsyncEnumerable<IReadOnlyList<ExportScrobble>> batches, Stream output, CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        await foreach (var batch in batches.WithCancellation(ct))
        {
            var text = new StringBuilder();
            foreach (var s in batch)
            {
                var date = DateTimeOffset.FromUnixTimeSeconds(s.ListenedAt).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);
                text.Append(Csv.Escape(MetadataParser.Credit(s.Artists))).Append(',')
                    .Append(Csv.Escape(s.Album)).Append(',')
                    .Append(Csv.Escape(s.Title)).Append(',')
                    .Append(Csv.Escape(date)).Append('\n');
            }

            await writer.WriteAsync(text, ct);
        }

        await writer.FlushAsync(ct);
    }
}

/// <summary>Minimal RFC 4180 CSV reader/writer.</summary>
public static class Csv
{
    public static IEnumerable<List<string>> Parse(TextReader reader)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var hasContent = false;
        int c;
        while ((c = reader.Read()) != -1)
        {
            var ch = (char)c;
            hasContent = true;
            if (inQuotes)
            {
                if (ch != '"') field.Append(ch);
                else if (reader.Peek() == '"')
                {
                    reader.Read();
                    field.Append('"');
                }
                else inQuotes = false;

                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    yield return row;
                    row = [];
                    hasContent = false;
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (hasContent)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
