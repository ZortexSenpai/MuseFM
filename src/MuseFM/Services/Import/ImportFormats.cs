using System.Text;
using MuseFM.Data.Entities;

namespace MuseFM.Services.Import;

/// <summary>One parsed entry of an import: either a scrobble or the reason it could not be read.</summary>
public sealed record ImportRow(ScrobbleInput? Input, string? Error)
{
    public static ImportRow Ok(ScrobbleInput input) => new(input, null);

    public static ImportRow Fail(string error) => new(null, error);
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
            case ".zip":
                return ImportKind.ListenBrainzJson;
            case ".json":
                var head = ReadHead(path, 64 * 1024).TrimStart('﻿', ' ', '\t', '\r', '\n');
                if (head.Contains("\"maloja\"")) return ImportKind.MalojaJson;
                if (head.StartsWith('[')) return head.Contains("\"listened_at\"") ? ImportKind.ListenBrainzJson : null;
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
        _ => throw new NotSupportedException($"{kind} is not a file format."),
    };

    private static string ReadHead(string path, int bytes)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[bytes];
        var read = stream.ReadAtLeast(buffer, bytes, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
