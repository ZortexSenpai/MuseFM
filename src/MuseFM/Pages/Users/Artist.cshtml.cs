using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MuseFM.Data.Entities;
using MuseFM.Services;
using MuseFM.Services.Metadata;

namespace MuseFM.Pages.Users;

public sealed class ArtistModel(UserManager<AppUser> users, StatsService stats, MetadataService metadata) : UserPageModel(users)
{
    private const int TopCount = 25;

    // On the first visit the page waits this long for a biography; a slower lookup finishes in the background.
    private static readonly TimeSpan MetadataWait = TimeSpan.FromSeconds(4);

    public int ArtistId { get; private set; }
    public string Name { get; private set; } = "";
    public ListenSummary Summary { get; private set; } = null!;
    public StatPeriod Period { get; private set; } = null!;
    public int FirstYear { get; private set; }
    public IReadOnlyList<ActivityBucket> Activity { get; private set; } = [];
    public IReadOnlyList<ChartEntry> TopTracks { get; private set; } = [];
    public IReadOnlyList<ChartEntry> TopAlbums { get; private set; } = [];
    public bool ShowPhoto { get; private set; }
    public ArtistMetadata? Info { get; private set; }
    public IReadOnlyList<string> Bio { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string username, int id, string? range, CancellationToken ct)
    {
        if (!await LoadOwnerAsync(username)) return NotFound();

        // Default to all time here: an artist page is about the whole relationship with that artist.
        Period = ResolvePeriod(range, StatRange.All);
        var subject = StatSubject.Artist(id);
        var summary = await stats.SummaryAsync(Owner.Id, subject, Period.Window, ct);
        if (summary is null || await stats.ArtistNameAsync(id, ct) is not { } name) return NotFound();

        ArtistId = id;
        Name = name;
        Summary = summary;
        FirstYear = await stats.FirstYearAsync(Owner.Id, Zone, ct);
        // Runs alongside the statistics queries; MetadataService uses its own DbContext.
        // The biography is text: it shows even when the viewer turned artwork off.
        ShowPhoto = metadata.Enabled && ViewerPreferences.Current.ShowArtwork;
        var info = metadata.Enabled ? metadata.GetArtistAsync(id, MetadataWait, ct) : Task.FromResult<ArtistMetadata?>(null);

        Activity = await stats.ActivityAsync(Owner.Id, Period, subject, ct);
        TopTracks = (await stats.TopTracksAsync(Owner.Id, Period.Window, 1, TopCount, subject, ct)).Items;
        TopAlbums = (await stats.TopAlbumsAsync(Owner.Id, Period.Window, 1, TopCount, subject, ct)).Items;

        Info = await info;
        Bio = MetadataText.Paragraphs(Info?.Bio);
        return Page();
    }
}
