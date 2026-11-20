using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages.Users;

/// <summary>/u/{username}/artists, /albums and /tracks. Literal routes like /scrobbles take precedence.</summary>
public sealed class ChartsModel(UserManager<AppUser> users, StatsService stats) : UserPageModel(users)
{
    private const int PageSize = 50;

    public ChartKind Kind { get; private set; }
    public StatPeriod Period { get; private set; } = null!;
    public int FirstYear { get; private set; }
    public Paged<ChartEntry> Chart { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(string username, string kind, string? range, int p = 1, CancellationToken ct = default)
    {
        ChartKind? parsed = kind.ToLowerInvariant() switch
        {
            "artists" => ChartKind.Artists,
            "albums" => ChartKind.Albums,
            "tracks" => ChartKind.Tracks,
            _ => null,
        };
        if (parsed is null || !await LoadOwnerAsync(username)) return NotFound();

        Kind = parsed.Value;
        Period = ResolvePeriod(range, ViewerPreferences.Current.DefaultRange ?? StatRange.Month);
        FirstYear = await stats.FirstYearAsync(Owner.Id, Zone, ct);
        var page = Math.Max(1, p);
        Chart = Kind switch
        {
            ChartKind.Artists => await stats.TopArtistsAsync(Owner.Id, Period.Window, page, PageSize, ct),
            ChartKind.Albums => await stats.TopAlbumsAsync(Owner.Id, Period.Window, page, PageSize, ct: ct),
            _ => await stats.TopTracksAsync(Owner.Id, Period.Window, page, PageSize, ct: ct),
        };
        return Page();
    }
}
