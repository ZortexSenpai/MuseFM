using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using MuseFM.Services;

namespace MuseFM.Pages;

/// <summary>Formatting helpers for Razor views.</summary>
public static class Ui
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record Change(string Text, string Css, string? Title = null);

    public static string Number(int value) => value.ToString("N0", Inv);

    public static string Percent(int value, int max) =>
        max <= 0 ? "0%" : Math.Clamp(value * 100.0 / max, 0, 100).ToString("0.##", Inv) + "%";

    public static DateTime Local(long unix, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UnixEpoch.AddSeconds(unix), zone);

    public static DateTime Local(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    /// <summary>The viewer's clock: "21:30" or "9:30 PM".</summary>
    private static string Clock => ViewerPreferences.Current.Clock12 ? "h:mm tt" : "HH:mm";

    public static string FullTime(long unix, TimeZoneInfo zone) => Local(unix, zone).ToString("ddd, d MMM yyyy " + Clock, Inv);

    public static string Date(long unix, TimeZoneInfo zone) => Local(unix, zone).ToString("d MMM yyyy", Inv);

    public static string Date(DateTime utc, TimeZoneInfo zone) => Local(utc, zone).ToString("d MMM yyyy", Inv);

    public static string DateAndTime(DateTime? utc, TimeZoneInfo zone) =>
        utc is null ? "—" : Local(utc.Value, zone).ToString("d MMM yyyy, " + Clock, Inv);

    public static string Relative(long unix, TimeZoneInfo zone)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var diff = now - unix;
        if (diff < 0) return FullTime(unix, zone);
        if (diff < 60) return "just now";
        if (diff < 3600) return $"{diff / 60} min ago";
        if (diff < 86400) return $"{diff / 3600} h ago";
        var local = Local(unix, zone);
        return local.Year == Local(now, zone).Year ? local.ToString("d MMM, " + Clock, Inv) : local.ToString("d MMM yyyy", Inv);
    }

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(Inv) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", Inv) + " KB",
        < 1024 * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.#", Inv) + " MB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", Inv) + " GB",
    };

    /// <summary>Whole percent of a total, rounded down so an incomplete share never shows as 100%.</summary>
    public static string Share(int part, int total) =>
        total <= 0 ? "" : Math.Floor(part * 100.0 / total).ToString("0", Inv) + "%";

    /// <summary>Rough duration such as "40 s", "12 min" or "2 h 5 min".</summary>
    public static string Elapsed(TimeSpan span) =>
        span.TotalSeconds < 60 ? $"{Math.Max(1, (int)span.TotalSeconds)} s"
        : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min"
        : $"{(int)span.TotalHours} h {span.Minutes} min";

    /// <summary>Listening time: "42 min", "12 h 5 min", and whole hours from 100 h ("1,320 h").</summary>
    public static string Hours(long seconds) => seconds switch
    {
        < 3600 => $"{Math.Max(seconds > 0 ? 1 : 0, seconds / 60)} min",
        < 360_000 => $"{seconds / 3600} h {seconds % 3600 / 60} min",
        _ => (seconds / 3600).ToString("N0", Inv) + " h",
    };

    /// <summary>A long stretch of days in words: "45 days", "7 months", "2.5 years".</summary>
    public static string Stretch(int days) => days switch
    {
        < 60 => days == 1 ? "1 day" : $"{days} days",
        < 730 => $"{days / 30} months",
        _ => (days / 365.25).ToString("0.#", Inv) + " years",
    };

    /// <summary>Axis numbers: in full below 10,000, then compact (12K, 1.2M).</summary>
    public static string Compact(int value) => value switch
    {
        < 10_000 => value.ToString("N0", Inv),
        < 1_000_000 => (value / 1000.0).ToString("0.#", Inv) + "K",
        _ => (value / 1_000_000.0).ToString("0.#", Inv) + "M",
    };

    /// <summary>An axis top and evenly spaced round ticks (steps of 1, 2 or 5 × 10ⁿ) covering <paramref name="max"/>.</summary>
    public static (int Top, IReadOnlyList<int> Ticks) NiceScale(int max, int targetTicks = 4)
    {
        if (max <= 0) return (1, [0, 1]);
        var raw = max / (double)targetTicks;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = Math.Max(1, (int)Math.Ceiling(new[] { 1, 2, 5, 10 }.Select(f => f * magnitude).First(s => s >= raw)));
        var top = (int)Math.Ceiling(max / (double)step) * step;
        return (top, Enumerable.Range(0, top / step + 1).Select(i => i * step).ToList());
    }

    /// <summary>
    /// Tooltip payload for charts.js: a title and rows of (value, name, series key). Rendered with textContent
    /// on the client, so names from the catalog are never interpreted as HTML.
    /// </summary>
    public static string Tip(string title, IEnumerable<(string Value, string Name, string? Key)> rows) =>
        JsonSerializer.Serialize(new { t = title, r = rows.Select(r => new[] { r.Value, r.Name, r.Key }) });

    public static string Tip(string title, string value, string name, string? key = null) => Tip(title, [(value, name, key)]);

    /// <summary>Change from <paramref name="previous"/> to <paramref name="current"/> in percent, with an arrow.</summary>
    public static Change Delta(int current, int previous)
    {
        if (previous == 0) return current == 0 ? new("±0", "flat") : new("▲ from 0", "up");
        var percent = (int)Math.Round((current - previous) * 100.0 / previous, MidpointRounding.AwayFromZero);
        return percent switch
        {
            0 => new("±0%", "flat"),
            > 0 => new($"▲ {percent.ToString("N0", Inv)}%", "up"),
            _ => new($"▼ {(-percent).ToString("N0", Inv)}%", "down"),
        };
    }

    /// <summary>Chart movement since the previous period: ▲3, ▼1, – or new.</summary>
    public static Change Movement(int rank, int? previousRank) => previousRank switch
    {
        null => new("new", "new", "Not played in the previous period"),
        { } was when was == rank => new("–", "flat", "Same position as before"),
        { } was when was > rank => new($"▲{was - rank}", "up", $"Up from #{was}"),
        { } was => new($"▼{rank - was}", "down", $"Down from #{was}"),
    };

    /// <summary>Heatmap level 0-4 (0 = none), square-root scaled so a few outliers don't wash out everything else.</summary>
    public static int HeatLevel(int value, int max) =>
        value <= 0 || max <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(4 * Math.Sqrt(value / (double)max)), 1, 4);

    public static string Day(DateOnly day) => day.ToString("ddd, d MMM yyyy", Inv);

    public static string ShortDay(DateOnly day) => day.ToString("d MMM", Inv);

    /// <summary>First character (grapheme) of a name, for placeholders.</summary>
    public static string Initial(string name) =>
        string.IsNullOrEmpty(name) ? "?" : new StringInfo(name).SubstringByTextElements(0, 1).ToUpperInvariant();

    /// <summary>An hour of the day: "21:00" or "9 PM".</summary>
    public static string Hour(int hour) =>
        ViewerPreferences.Current.Clock12 ? $"{(hour % 12 == 0 ? 12 : hour % 12)} {(hour % 24 < 12 ? "AM" : "PM")}" : $"{hour % 24:00}:00";

    /// <summary>One hour of the day as a span: "21:00–22:00" or "9 PM–10 PM".</summary>
    public static string HourSpan(int hour) => $"{Hour(hour)}–{Hour(hour + 1)}";

    /// <summary>Short axis label for an hour: "21" or "9p".</summary>
    public static string HourTick(int hour) =>
        ViewerPreferences.Current.Clock12 ? $"{(hour % 12 == 0 ? 12 : hour % 12)}{(hour < 12 ? "a" : "p")}" : hour.ToString("00", Inv);

    public static string Duration(int? seconds) =>
        seconds is { } s ? TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss", Inv) : "";

    public static string UserUrl(string userName) => "/u/" + Uri.EscapeDataString(userName);

    /// <summary>
    /// One of the six "pop" colours of site.css (tone-1 to tone-6) for a name: always the same one, so a person
    /// keeps their colour wherever they appear.
    /// </summary>
    public static int Tone(string name)
    {
        var sum = 0;
        foreach (var c in name.ToLowerInvariant()) sum += c;
        return sum % 6 + 1;
    }

    /// <summary>A user's uploaded picture, or the first letter of their name on their colour.</summary>
    /// <param name="large">The size of the profile header; otherwise the small one used in lists.</param>
    public static IHtmlContent Avatar(string userName, string? file, bool large = false)
    {
        var size = large ? "" : " small";
        var html = new HtmlContentBuilder();
        if (file is null)
        {
            html.AppendHtml($"<span class=\"avatar{size} tone-{Tone(userName)}\" aria-hidden=\"true\">");
            html.Append(Initial(userName));
        }
        else
        {
            // Lazy in lists; the header's picture is at the top of the page and needed right away.
            html.AppendHtml($"<span class=\"avatar{size} has-image\" aria-hidden=\"true\"><img src=\"{HtmlEncoder.Default.Encode(ProfileImages.Url(file))}\" alt=\"\"{(large ? "" : " loading=\"lazy\"")} decoding=\"async\">");
        }

        html.AppendHtml("</span>");
        return html;
    }

    public static IHtmlContent ArtistLinks(string userName, IEnumerable<ArtistRef> artists) =>
        ArtistLinks(userName, artists.Select(a => new PlayingArtist(a.Name, a.Id)));

    /// <summary>Artists of a track that is playing: those the user has no page for yet are plain text.</summary>
    public static IHtmlContent ArtistLinks(string userName, IEnumerable<PlayingArtist> artists)
    {
        var html = new HtmlContentBuilder();
        var first = true;
        foreach (var artist in artists)
        {
            if (!first) html.AppendHtml(", ");
            first = false;
            if (artist.Id is null)
            {
                html.Append(artist.Name);
                continue;
            }

            html.AppendHtml($"<a href=\"{HtmlEncoder.Default.Encode(UserUrl(userName))}/artist/{artist.Id}\">");
            html.Append(artist.Name);
            html.AppendHtml("</a>");
        }

        return html;
    }
}

public static class TimeZones
{
    public static TimeZoneInfo FindOrUtc(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;

    public static IReadOnlyList<TimeZoneInfo> All { get; } = TimeZoneInfo.GetSystemTimeZones()
        .Where(z => z.Id.Contains('/') || z.Id == "UTC")
        .OrderBy(z => z.Id, StringComparer.Ordinal)
        .ToList();

    /// <summary>Options for a time zone select: "Europe/Zurich (UTC+01:00)".</summary>
    public static IEnumerable<SelectListItem> SelectItems => All.Select(z => new SelectListItem(
        $"{z.Id} (UTC{(z.BaseUtcOffset < TimeSpan.Zero ? "-" : "+")}{z.BaseUtcOffset:hh\\:mm})", z.Id));

    public static bool IsKnown(string? id) => !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);
}
