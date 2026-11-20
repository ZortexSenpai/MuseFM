using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Pages;

/// <param name="Notes">A short remark per entry id, shown after its title ("after 8 months").</param>
public sealed record TopListModel(string UserName, ChartKind Kind, IReadOnlyList<ChartEntry> Entries, IReadOnlyDictionary<int, string>? Notes = null);

/// <summary>A data series. Key is its CSS color role: "current" (accent) or "previous" (de-emphasis gray).</summary>
/// <param name="Note">Shown after the name in the legend, e.g. the series total.</param>
public sealed record ChartSeries(string Name, string Key, string? Note = null);

/// <summary>One position on the x axis; Values holds one number per series.</summary>
/// <param name="Label">Axis label; empty to leave the position unlabelled.</param>
/// <param name="Title">Full name for the tooltip and table view.</param>
public sealed record ChartGroup(string Label, string Title, IReadOnlyList<int?> Values);

/// <param name="Caption">Accessible description of the chart.</param>
/// <param name="Dimension">Heading of the first column in the table view ("Day", "Hour").</param>
/// <param name="Unit">Name of the value in tooltips of single-series charts.</param>
public sealed record ChartModel(string Caption, string Dimension, IReadOnlyList<ChartSeries> Series, IReadOnlyList<ChartGroup> Groups, string Unit = "scrobbles");

/// <summary>A grid of counts: rows × columns (e.g. weekdays × hours).</summary>
public sealed record HeatmapModel(string Caption, IReadOnlyList<string> Rows, IReadOnlyList<string> Columns, IReadOnlyList<string> ColumnTitles, int[,] Values);

/// <summary>Catalog entries for the admin; with <paramref name="MergeFrom"/>, each row offers to merge that entry into it.</summary>
public sealed record CatalogTableModel(IReadOnlyList<CatalogEntry> Entries, int? MergeFrom, string? SourceName);

/// <summary>The period picker: the presets, calendar years and months since <paramref name="FirstYear"/>, and any span of days.</summary>
/// <param name="Path">The page's URL without a query string.</param>
/// <param name="Query">Other query parameters that the links must keep.</param>
public sealed record RangeTabsModel(StatPeriod Current, string Path, int FirstYear, TimeZoneInfo Zone, IReadOnlyList<KeyValuePair<string, string>>? Query = null)
{
    public string Url(string slug) =>
        Path + "?" + string.Concat((Query ?? []).Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}&"))
        + "range=" + Uri.EscapeDataString(slug);

    public string Url(StatPeriod period) => Url(period.Slug());
}

public sealed record PagerModel(int Page, int TotalPages, Func<int, string> UrlFor);

/// <param name="Followers">Followers the viewer can see; likewise <paramref name="Following"/>.</param>
/// <param name="Follows">Whether the signed-in viewer follows this user; null for visitors and on one's own profile.</param>
/// <param name="AvatarFile">The uploaded picture and banner (see ProfileImages), if any; likewise <paramref name="BannerFile"/>.</param>
public sealed record UserHeaderModel(
    string UserName, bool IsPublic, DateTime MemberSince, TimeZoneInfo Zone, string Active, int Followers = 0, int Following = 0, bool? Follows = null,
    string? AvatarFile = null, string? BannerFile = null);

/// <param name="Loved">Tracks the profile owner loves, marked with a heart.</param>
/// <param name="CanLove">The viewer is the owner and can love and unlove tracks from the table.</param>
public sealed record ScrobbleTableModel(
    string UserName, TimeZoneInfo Zone, IReadOnlyList<ScrobbleRow> Rows, bool CanDelete, int Page, IReadOnlySet<int>? Loved = null, bool CanLove = false);

/// <summary>A heart that loves or unloves a track; a plain mark when the viewer can't change it.</summary>
public sealed record LoveButtonModel(string UserName, int TrackId, bool Loved, bool CanLove, string ReturnUrl);

/// <summary>Base for pages under /u/{username}: resolves the profile owner and enforces privacy.</summary>
public abstract class UserPageModel(UserManager<AppUser> users) : PageModel
{
    protected UserManager<AppUser> Users { get; } = users;
    public AppUser Owner { get; private set; } = null!;
    public string UserName => Owner.UserName!;
    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Utc;
    public bool IsOwner { get; private set; }

    /// <summary>The signed-in viewer, or null.</summary>
    public string? ViewerId { get; private set; }

    public bool ViewerIsAdmin => User.IsInRole(Roles.Admin);
    public int Followers { get; private set; }
    public int Following { get; private set; }

    /// <summary>Whether the viewer follows the owner; null for visitors and the owner.</summary>
    public bool? ViewerFollows { get; private set; }

    protected async Task<bool> LoadOwnerAsync(string username)
    {
        var owner = await Users.FindByNameAsync(username);
        if (owner is null) return false;
        ViewerId = Users.GetUserId(User);
        IsOwner = ViewerId == owner.Id;
        if (!owner.IsPublic && !IsOwner && !ViewerIsAdmin) return false;
        Owner = owner;
        Zone = TimeZones.FindOrUtc(owner.TimeZoneId);

        var social = HttpContext.RequestServices.GetRequiredService<SocialService>();
        (Followers, Following) = await social.CountsAsync(owner.Id, ViewerId, ViewerIsAdmin);
        if (ViewerId is not null && !IsOwner) ViewerFollows = await social.FollowsAsync(ViewerId, owner.Id);
        return true;
    }

    public UserHeaderModel Header(string active) =>
        new(UserName, Owner.IsPublic, Owner.CreatedAt, Zone, active, Followers, Following, ViewerFollows, Owner.AvatarFile, Owner.BannerFile);

    /// <summary>The period in the URL (<c>?range=</c>, or <c>?from=&amp;to=</c> from the date form), else <paramref name="fallback"/>.</summary>
    protected StatPeriod ResolvePeriod(string? range, StatRange fallback) =>
        StatPeriod.Resolve(range, fallback, Zone, Request.Query["from"], Request.Query["to"]);

    public string ProfileUrl => Ui.UserUrl(UserName);
}
