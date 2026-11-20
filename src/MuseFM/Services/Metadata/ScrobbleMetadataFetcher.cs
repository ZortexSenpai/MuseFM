using System.Threading.Channels;

namespace MuseFM.Services.Metadata;

/// <summary>
/// Looks up the artists and albums of new scrobbles that were never looked up, so their artwork is there before
/// someone opens a page. One at a time, which leaves <see cref="MetadataService"/>'s other lookups to pages being
/// viewed. The queue is not kept across a restart: what is left is looked up when it is shown.
/// </summary>
public sealed class ScrobbleMetadataFetcher(MetadataService metadata, ILogger<ScrobbleMetadataFetcher> logger) : BackgroundService
{
    private readonly Channel<(MediaKind Kind, int Id)> _queue =
        Channel.CreateUnbounded<(MediaKind, int)>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(IEnumerable<int> artistIds, IEnumerable<int> albumIds)
    {
        if (!metadata.Enabled) return;
        foreach (var id in artistIds.Distinct()) _queue.Writer.TryWrite((MediaKind.Artist, id));
        foreach (var id in albumIds.Distinct()) _queue.Writer.TryWrite((MediaKind.Album, id));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (kind, id) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await metadata.FetchNewAsync(kind, id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Looking up metadata for scrobbled {Kind} {Id} failed", kind, id);
            }
        }
    }
}
