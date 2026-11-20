using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using MuseFM.Data.Entities;
using MuseFM.Services.Metadata;

namespace MuseFM.Services;

public enum ProfileImageKind
{
    Avatar,
    Banner,
}

/// <summary>
/// Profile pictures and banners that users upload. Every upload gets a new random file name, which is also its URL
/// (/media/profile/{file}): the URL never changes its image, so browsers cache it for good, and only pages that show
/// the user reveal it, so private profiles keep their pictures to the people who can see them.
/// </summary>
public sealed partial class ProfileImages(DataPaths paths, UserManager<AppUser> users, ILogger<ProfileImages> logger)
{
    public const int MaxMegabytes = 10;
    public const long MaxBytes = MaxMegabytes * 1024 * 1024;

    /// <summary>The <c>accept</c> attribute for file inputs: the formats MetadataHttp.SniffExtension recognizes.</summary>
    public const string Accept = "image/jpeg,image/png,image/gif,image/webp";

    [GeneratedRegex("^[0-9a-f]{32}\\.(jpg|png|gif|webp)$")]
    private static partial Regex FileName();

    public static string Url(string file) => "/media/profile/" + file;

    public static string? Get(AppUser user, ProfileImageKind kind) => kind == ProfileImageKind.Avatar ? user.AvatarFile : user.BannerFile;

    private static void Set(AppUser user, ProfileImageKind kind, string? file)
    {
        if (kind == ProfileImageKind.Avatar) user.AvatarFile = file;
        else user.BannerFile = file;
    }

    /// <summary>The path of a file named in a URL; null when the name is not one <see cref="SaveAsync"/> could have made.</summary>
    public string? PathOf(string file) => FileName().IsMatch(file) ? Path.Combine(paths.ProfileImages, file) : null;

    /// <summary>Stores an uploaded JPEG, PNG, GIF or WebP image as the user's picture or banner and deletes the one it replaces.</summary>
    /// <returns>An error to show, or null when the image was saved.</returns>
    public async Task<string?> SaveAsync(AppUser user, ProfileImageKind kind, IFormFile? upload, CancellationToken ct = default)
    {
        if (upload is null || upload.Length == 0) return "Choose an image to upload.";
        if (upload.Length > MaxBytes) return $"That image is too large. Upload one of at most {MaxMegabytes} MB.";

        var bytes = new byte[upload.Length];
        await using (var stream = upload.OpenReadStream())
            await stream.ReadExactlyAsync(bytes, ct);
        // Checked by content, not by name or the browser's content type: SVG in particular is refused, as it can carry scripts.
        if (MetadataHttp.SniffExtension(bytes) is not { } extension) return "Upload a JPEG, PNG, GIF or WebP image.";

        var file = Guid.NewGuid().ToString("N") + extension;
        var path = Path.Combine(paths.ProfileImages, file);
        try
        {
            await File.WriteAllBytesAsync(path, bytes, ct);
        }
        catch
        {
            TryDelete(file);
            throw;
        }

        var previous = Get(user, kind);
        Set(user, kind, file);
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            Set(user, kind, previous);
            TryDelete(file);
            return string.Join(" ", result.Errors.Select(e => e.Description));
        }

        TryDelete(previous);
        return null;
    }

    /// <returns>An error to show, or null when the image was removed (or there was none).</returns>
    public async Task<string?> RemoveAsync(AppUser user, ProfileImageKind kind)
    {
        if (Get(user, kind) is not { } previous) return null;
        Set(user, kind, null);
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            Set(user, kind, previous);
            return string.Join(" ", result.Errors.Select(e => e.Description));
        }

        TryDelete(previous);
        return null;
    }

    /// <summary>Deletes the files of an account that has been deleted.</summary>
    public void DeleteFiles(AppUser user)
    {
        TryDelete(user.AvatarFile);
        TryDelete(user.BannerFile);
    }

    private void TryDelete(string? file)
    {
        if (file is null || PathOf(file) is not { } path) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Harmless: /media/profile only serves files a user still has.
            logger.LogWarning(ex, "Could not delete profile image {Path}", path);
        }
    }
}
