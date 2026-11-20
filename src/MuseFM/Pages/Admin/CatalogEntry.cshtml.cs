using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages.Admin;

/// <summary>/admin/catalog/{kind}/{id}: rename an artist, album or track, set its MusicBrainz id, or merge it into another.</summary>
public sealed class CatalogEntryModel(CatalogEditor editor, StatsService stats, UserManager<AppUser> users) : PageModel
{
    public CatalogEntry Entry { get; private set; } = null!;
    public IReadOnlyList<AliasEntry> Aliases { get; private set; } = [];
    public string Query { get; private set; } = "";
    public IReadOnlyList<CatalogEntry> Candidates { get; private set; } = [];

    /// <summary>The entry on the admin's own profile, when they scrobbled it.</summary>
    public string? ProfileUrl { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public bool StatusIsError { get; set; }

    public async Task<IActionResult> OnGetAsync(string kind, int id, string? q, CancellationToken ct)
    {
        if (CatalogUrls.ParseKind(kind) is not { } parsed || await editor.GetAsync(parsed, id, ct) is not { } entry) return NotFound();
        Entry = entry;
        Aliases = await editor.AliasesAsync(parsed, id, ct);

        // Merge candidates: anything matching the search, by default entries named like this one.
        Query = q?.Trim() is { Length: > 0 } query ? query : entry.Name;
        Candidates = (await editor.SearchAsync(parsed, Query, ct)).Where(c => c.Id != id).ToList();

        if (users.GetUserId(User) is { } me && await stats.SummaryAsync(me, new StatSubject(parsed, id), default, ct) is not null)
            ProfileUrl = $"{Ui.UserUrl(users.GetUserName(User)!)}/{CatalogUrls.Singular(parsed)}/{id}";
        return Page();
    }

    public async Task<IActionResult> OnPostRenameAsync(string kind, int id, string? name, CancellationToken ct) =>
        CatalogUrls.ParseKind(kind) is { } parsed ? Show(parsed, id, await editor.RenameAsync(parsed, id, name ?? "", ct)) : NotFound();

    public async Task<IActionResult> OnPostMbidAsync(string kind, int id, string? mbid, CancellationToken ct) =>
        CatalogUrls.ParseKind(kind) is { } parsed ? Show(parsed, id, await editor.SetMusicBrainzIdAsync(parsed, id, mbid, ct)) : NotFound();

    public async Task<IActionResult> OnPostMergeAsync(string kind, int id, int targetId, CancellationToken ct) =>
        CatalogUrls.ParseKind(kind) is { } parsed ? Show(parsed, id, await editor.MergeAsync(parsed, id, targetId, ct)) : NotFound();

    public async Task<IActionResult> OnPostDeleteAliasAsync(string kind, int id, int aliasId, CancellationToken ct)
    {
        if (CatalogUrls.ParseKind(kind) is not { } parsed) return NotFound();
        var deleted = await editor.DeleteAliasAsync(aliasId, ct);
        return Show(parsed, id, new CatalogEdit(deleted, deleted ? "Alias removed." : "That alias no longer exists."));
    }

    /// <summary>
    /// Shows the outcome on the entry the edit leads to (a merge target). When the edit clashed with another entry, that
    /// one is listed as the merge candidate.
    /// </summary>
    private RedirectResult Show(ChartKind kind, int id, CatalogEdit edit)
    {
        StatusMessage = edit.Message;
        StatusIsError = !edit.Ok;
        if (edit.Ok) return Redirect(CatalogUrls.Entry(kind, edit.Id ?? id));
        return Redirect(CatalogUrls.Entry(kind, id) + (edit.Id is { } other ? $"?q={other}#merge" : ""));
    }
}
