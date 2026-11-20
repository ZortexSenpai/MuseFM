using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;
using MuseFM.Services.Import;
using MuseFM.Services.Metadata;

namespace MuseFM.Pages.Settings;

[RequestSizeLimit(ImportModel.MaxUploadBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = ImportModel.MaxUploadBytes)]
public sealed class ImportModel(
    UserManager<AppUser> users,
    AppDbContext db,
    ImportQueue queue,
    DataPaths paths,
    ConnectorSettings connectors) : PageModel
{
    public const long MaxUploadBytes = 1L << 30; // 1 GiB

    public List<ImportJob> Jobs { get; private set; } = [];
    public bool HasActiveJobs { get; private set; }
    public bool LastFmEnabled => connectors.LastFmApiKey is not null;
    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Utc;

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public bool StatusIsError { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var user = await users.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        Zone = TimeZones.FindOrUtc(user.TimeZoneId);
        Jobs = await db.ImportJobs.AsNoTracking()
            .Where(j => j.UserId == user.Id)
            .OrderByDescending(j => j.CreatedAt)
            .Take(20)
            .ToListAsync(ct);
        HasActiveJobs = Jobs.Any(j => j.Status is ImportStatus.Queued or ImportStatus.Running);
    }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? file, string? format, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return Fail("Please choose a file to upload.");
        if (await QueueIsFullAsync(ct)) return Fail(QueueFull);

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!ImportFormats.AllowedExtensions.Contains(extension))
            return Fail("Unsupported file type. Upload a .json, .jsonl, .csv or .zip file.");

        var stored = Path.Combine(paths.Imports, $"{Guid.NewGuid():N}{extension}");
        await using (var target = System.IO.File.Create(stored))
            await file.CopyToAsync(target, ct);

        ImportKind? kind = Enum.TryParse<ImportKind>(format, out var chosen) && Enum.IsDefined(chosen) && chosen is not (ImportKind.LastFmApi or ImportKind.MalojaApi)
            ? chosen
            : ImportFormats.Detect(stored);
        if (kind is null)
        {
            System.IO.File.Delete(stored);
            return Fail("Could not recognize the file format. Please select it manually.");
        }

        var name = Path.GetFileName(file.FileName);
        await EnqueueAsync(new ImportJob
        {
            UserId = users.GetUserId(User)!,
            Kind = kind.Value,
            Source = name.Length > 200 ? name[..200] : name,
            StoredFile = stored,
        }, ct);
        StatusMessage = $"Import of {name} ({ImportFormats.Describe(kind.Value)}) started.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostLastFmAsync(string? lastFmUser, CancellationToken ct)
    {
        if (!LastFmEnabled) return Fail("Last.fm import is not configured on this server.");
        var name = TextKey.Clean(lastFmUser);
        if (name.Length is 0 or > 64) return Fail("Please enter your Last.fm username.");
        if (await QueueIsFullAsync(ct)) return Fail(QueueFull);

        await EnqueueAsync(new ImportJob { UserId = users.GetUserId(User)!, Kind = ImportKind.LastFmApi, Source = name }, ct);
        StatusMessage = $"Import from Last.fm user {name} started.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMalojaAsync(string? malojaUrl, CancellationToken ct)
    {
        if (!MalojaClient.TryNormalizeUrl(malojaUrl, out var url, out var error)) return Fail(error!);
        if (await QueueIsFullAsync(ct)) return Fail(QueueFull);

        await EnqueueAsync(new ImportJob { UserId = users.GetUserId(User)!, Kind = ImportKind.MalojaApi, Source = url }, ct);
        StatusMessage = $"Import from the Maloja server at {url} started.";
        return RedirectToPage();
    }

    private static readonly string QueueFull =
        $"You already have {ImportLimits.MaxPendingJobs} imports waiting or running. Start the next one when one of them is done.";

    /// <summary>Imports run one at a time for the whole server, and every waiting upload is a file on its disk.</summary>
    private async Task<bool> QueueIsFullAsync(CancellationToken ct)
    {
        var me = users.GetUserId(User);
        return await db.ImportJobs.CountAsync(j => j.UserId == me && (j.Status == ImportStatus.Queued || j.Status == ImportStatus.Running), ct)
               >= ImportLimits.MaxPendingJobs;
    }

    private async Task EnqueueAsync(ImportJob job, CancellationToken ct)
    {
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync(ct);
        queue.Enqueue(job.Id);
        StatusIsError = false;
    }

    private RedirectToPageResult Fail(string message)
    {
        StatusMessage = message;
        StatusIsError = true;
        return RedirectToPage();
    }
}
