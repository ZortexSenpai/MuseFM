using System.Globalization;
using System.IO.Compression;
using System.Text;
using MuseFM.Data.Entities;

namespace MuseFM.Services.Import;

/// <summary>One parsed entry of an import: either a scrobble or the reason it could not be read.</summary>
public sealed record ImportRow(ScrobbleInput? Input, string? Error)
{
    public static ImportRow Ok(ScrobbleInput input) => new(input, null);

    public static ImportRow Fail(string error) => new(null, error);
}

/// <summary>
/// The timeframe an import is limited to, in Unix seconds: from <paramref name="From"/> up to, but not including,
/// <paramref name="Before"/>. Open at an end that is null.
/// </summary>
public readonly record struct ImportRange(long? From, long? Before)
{
    private static readonly DateOnly Earliest = new(1970, 1, 2);
    private static readonly DateOnly Latest = new(2999, 12, 31);

    public bool Contains(long listenedAt) =>
        (From is not { } from || listenedAt >= from) && (Before is not { } before || listenedAt < before);

    /// <summary>
    /// Reads the two optional dates (yyyy-MM-dd) of an import form: whole days in the user's time zone, the last one included.
    /// </summary>
    public static bool TryParse(string? firstDay, string? lastDay, TimeZoneInfo zone, out ImportRange range, out string? error)
    {
        range = default;
        error = null;
        if (!TryParseDay(firstDay, out var first) || !TryParseDay(lastDay, out var last))
        {
            error = "Please enter the dates as they are offered by the date picker (year-month-day).";
            return false;
        }

        if (first > last)
        {
            error = "The first day of the timeframe is after its last day.";
            return false;
        }

        var clock = new LocalClock(zone);
        range = new ImportRange(
            first is { } from ? clock.StartOf(from) : null,
            last is { } to ? clock.StartOf(to.AddDays(1)) : null);
        return true;
    }

    private static bool TryParseDay(string? value, out DateOnly? day)
    {
        day = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || parsed < Earliest || parsed > Latest)
            return false;
        day = parsed;
        return true;
    }
}

public static class ImportFormats
{
    public static readonly string[] AllowedExtensions = [".json", ".jsonl", ".csv", ".zip"];

    public static string Describe(ImportKind kind) => kind switch
    {
        ImportKind.MalojaJson => "Maloja export",
        ImportKind.LastFmCsv => "Last.fm CSV",
        ImportKind.LastFmStatsJson => "lastfmstats.com export",
        ImportKind.ListenBrainzJson => "ListenBrainz export",
        ImportKind.LastFmApi => "Last.fm account",
        ImportKind.MalojaApi => "Maloja server",
        ImportKind.SpotifyJson => "Spotify streaming history",
        _ => kind.ToString(),
    };

    /// <summary>Guesses the format from the extension and the first bytes of the file.</summary>
    public static ImportKind? Detect(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".csv":
                return ImportKind.LastFmCsv;
            case ".jsonl":
                return ImportKind.ListenBrainzJson;
            case ".zip":
                return IsSpotifyArchive(path) ? ImportKind.SpotifyJson : ImportKind.ListenBrainzJson;
            case ".json":
                var head = ReadHead(path, 64 * 1024).TrimStart('﻿', ' ', '\t', '\r', '\n');
                if (head.Contains("\"maloja\"")) return ImportKind.MalojaJson;
                if (head.StartsWith('['))
                {
                    if (head.Contains("\"listened_at\"")) return ImportKind.ListenBrainzJson;
                    return SpotifyFormat.LooksLikeHistory(head) ? ImportKind.SpotifyJson : null;
                }

                if (head.Contains("\"listened_at\"") && head.Contains("\"track_metadata\"")) return ImportKind.ListenBrainzJson;
                if (head.Contains("\"scrobbles\"") && head.Contains("\"artists\"")) return ImportKind.MalojaJson;
                if (head.Contains("\"scrobbles\"") && head.Contains("\"username\"")) return ImportKind.LastFmStatsJson;
                return null;
            default:
                return null;
        }
    }

    public static IEnumerable<ImportRow> Read(ImportKind kind, string path) => kind switch
    {
        ImportKind.MalojaJson => MalojaFormat.Read(path),
        ImportKind.LastFmCsv => LastFmCsvFormat.Read(path),
        ImportKind.LastFmStatsJson => LastFmStatsFormat.Read(path),
        ImportKind.ListenBrainzJson => ListenBrainzFormat.Read(path),
        ImportKind.SpotifyJson => SpotifyFormat.Read(path),
        _ => throw new NotSupportedException($"{kind} is not a file format."),
    };

    /// <summary>A .zip is a ListenBrainz export unless the files in it are named like Spotify's.</summary>
    private static bool IsSpotifyArchive(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Any(entry => SpotifyFormat.IsHistoryFile(entry.FullName));
        }
        catch (InvalidDataException)
        {
            // Not a readable .zip: the import says so when it runs.
            return false;
        }
    }

    private static string ReadHead(string path, int bytes)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[bytes];
        var read = stream.ReadAtLeast(buffer, bytes, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
