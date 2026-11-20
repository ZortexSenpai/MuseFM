using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using MuseFM.Data;
using MuseFM.Services;
using MuseFM.Services.Metadata;

namespace MuseFM.Api;

/// <summary>
/// Cached artist photos and album covers, which are part of the shared catalog and so public, and the pictures and
/// banners users uploaded.
/// </summary>
public static class MediaEndpoints
{
    // How long a request waits for an image that is being looked up right now. Kept short so a page full of new artwork
    // doesn't tie up the browser's connections; media.js asks again for images that are still pending.
    private static readonly TimeSpan FetchWait = TimeSpan.FromSeconds(5);

    // Transparent placeholders that let the themed background behind the <img> show through. Their width is the only
    // thing the page can read from an image response: 1×1 means "no image", 2×1 means "still looking, ask again".
    private static readonly byte[] NoImage = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
    private static readonly byte[] PendingImage = Convert.FromBase64String("R0lGODlhAgABAIEAAAAAAP///wAAAAAAACH5BAEAAAAALAAAAAACAAEAAAgFAAEACAgAOw==");

    public static void MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/media/artist/{id:int}", (int id, HttpContext http, MetadataService metadata, CancellationToken ct) =>
            ServeAsync(MediaKind.Artist, id, http, metadata, ct));
        app.MapGet("/media/album/{id:int}", (int id, HttpContext http, MetadataService metadata, CancellationToken ct) =>
            ServeAsync(MediaKind.Album, id, http, metadata, ct));
        app.MapGet("/media/track/{id:int}", (int id, HttpContext http, MetadataService metadata, CancellationToken ct) =>
            ServeAsync(MediaKind.Track, id, http, metadata, ct));
        app.MapGet("/media/profile/{file}", ServeProfileImageAsync);
    }

    private static async Task<IResult> ServeProfileImageAsync(string file, HttpContext http, ProfileImages images, AppDbContext db, CancellationToken ct)
    {
        // Only images someone has now: a replaced or removed one stops working even if its file could not be deleted.
        if (images.PathOf(file) is not { } path || !File.Exists(path)
            || !await db.Users.AnyAsync(u => u.AvatarFile == file || u.BannerFile == file, ct))
            return Results.NotFound();

        http.Response.Headers.XContentTypeOptions = "nosniff";
        // Every upload gets a new name, so a name always stands for the same image.
        http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return Results.File(path, MetadataHttp.ContentType(Path.GetExtension(path)),
            entityTag: new EntityTagHeaderValue($"\"{Path.GetFileNameWithoutExtension(path)}\""));
    }

    private static async Task<IResult> ServeAsync(MediaKind kind, int id, HttpContext http, MetadataService metadata, CancellationToken ct)
    {
        var (image, pending) = await metadata.GetImageAsync(kind, id, FetchWait, ct);
        http.Response.Headers.XContentTypeOptions = "nosniff";
        if (image is null)
        {
            // Never cached: a later page view (or media.js, while pending) should get the real image once it exists.
            http.Response.Headers.CacheControl = pending ? "no-store" : "no-cache";
            return Results.File(pending ? PendingImage : NoImage, "image/gif");
        }

        http.Response.Headers.CacheControl = "public, max-age=86400";
        return Results.File(image.Path, image.ContentType, lastModified: image.LastModified, entityTag: new EntityTagHeaderValue(image.ETag));
    }
}
