using Microsoft.AspNetCore.Mvc.RazorPages;
using MuseFM.Services;

namespace MuseFM.Pages.Admin;

/// <summary>/admin/catalog: find artists, albums and tracks to rename or merge.</summary>
public sealed class CatalogModel(CatalogEditor editor) : PageModel
{
    public ChartKind Kind { get; private set; }
    public string Query { get; private set; } = "";
    public IReadOnlyList<CatalogEntry> Results { get; private set; } = [];

    public async Task OnGetAsync(string? kind, string? q, CancellationToken ct)
    {
        Kind = CatalogUrls.ParseKind(kind) ?? ChartKind.Artists;
        Query = (q ?? "").Trim();
        Results = await editor.SearchAsync(Kind, Query, ct);
    }
}

public static class CatalogUrls
{
    public static ChartKind? ParseKind(string? value) => value?.ToLowerInvariant() switch
    {
        "artist" or "artists" => ChartKind.Artists,
        "album" or "albums" => ChartKind.Albums,
        "track" or "tracks" => ChartKind.Tracks,
        _ => null,
    };

    /// <summary>"artist", "album" or "track".</summary>
    public static string Singular(ChartKind kind) => kind switch
    {
        ChartKind.Artists => "artist",
        ChartKind.Albums => "album",
        _ => "track",
    };

    public static string Entry(ChartKind kind, int id) => $"/admin/catalog/{Singular(kind)}/{id}";

    public static string Search(ChartKind kind, string? query = null) =>
        $"/admin/catalog?kind={kind.ToString().ToLowerInvariant()}" + (string.IsNullOrEmpty(query) ? "" : "&q=" + Uri.EscapeDataString(query));
}
