using System.Security.Claims;
using MuseFM.Services.Import;

namespace MuseFM.Api;

/// <summary>File downloads of the signed-in user's full history.</summary>
public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/settings/export").RequireAuthorization();

        // Named so Maloja's importer recognizes it ("maloja_export_*.json").
        group.MapGet("/maloja", (HttpContext context, ExportService export) =>
            Results.Stream(
                stream => export.WriteMalojaAsync(UserId(context), stream, context.RequestAborted),
                "application/json",
                $"maloja_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json"));

        group.MapGet("/lastfm", (HttpContext context, ExportService export) =>
            Results.Stream(
                stream => export.WriteLastFmCsvAsync(UserId(context), stream, context.RequestAborted),
                "text/csv; charset=utf-8",
                $"musefm_lastfm_{context.User.Identity?.Name}_{DateTime.UtcNow:yyyyMMdd}.csv"));

        group.MapGet("/listenbrainz", (HttpContext context, ExportService export) =>
            Results.Stream(
                stream => export.WriteListenBrainzAsync(UserId(context), context.User.Identity?.Name ?? "", stream, context.RequestAborted),
                "application/zip",
                $"musefm_listenbrainz_{context.User.Identity?.Name}_{DateTime.UtcNow:yyyyMMdd}.zip"));

        return app;
    }

    private static string UserId(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Not signed in.");
}
