using System.IO.Compression;

namespace MuseFM.Services.Import;

/// <summary>
/// How much an import may ask of the server. An upload can be 1 GiB, but what counts is what it turns into: a .zip of a
/// few megabytes can unpack to many gigabytes, and JSON that is read in one piece takes several times its size in memory.
/// </summary>
public static class ImportLimits
{
    /// <summary>JSON that is held in memory as a whole: an export that is one document, or one file inside a .zip.</summary>
    public const long MaxDocumentBytes = 256L << 20;

    /// <summary>Everything unpacked from one .zip.</summary>
    public const long MaxArchiveBytes = 2L << 30;

    /// <summary>One answer from a Maloja server, which is a month of scrobbles at most.</summary>
    public const long MaxMalojaResponseBytes = 64L << 20;

    /// <summary>Imports one user can have waiting or running; every waiting upload is a file on disk.</summary>
    public const int MaxPendingJobs = 3;

    public static string Megabytes(long bytes) => $"{bytes >> 20:N0} MB";

    /// <summary>Opens an export that is read as one JSON document, unless it is too large for that.</summary>
    public static FileStream OpenDocument(string path, long maxBytes = MaxDocumentBytes)
    {
        var stream = File.OpenRead(path);
        if (stream.Length <= maxBytes) return stream;
        stream.Dispose();
        throw new InvalidDataException(TooLarge(maxBytes));
    }

    /// <summary>
    /// Opens a file of a .zip for reading up to a size: a small archive can unpack to gigabytes, and the sizes written
    /// in it are whatever its author wanted them to be.
    /// </summary>
    /// <param name="unpacked">What was unpacked from the archive before this file.</param>
    public static SizeLimitedStream OpenEntry(ZipArchiveEntry entry, long maxEntryBytes, long maxArchiveBytes, long unpacked) =>
        maxArchiveBytes - unpacked < maxEntryBytes
            ? new SizeLimitedStream(entry.Open(), Math.Max(0, maxArchiveBytes - unpacked),
                $"This archive unpacks to more than {Megabytes(maxArchiveBytes)}, which is too much to import.")
            : new SizeLimitedStream(entry.Open(), maxEntryBytes,
                $"The archive contains a file of more than {Megabytes(maxEntryBytes)}, which is too large to import.");

    public static string TooLarge(long maxBytes) =>
        $"This file is too large to import in one piece (more than {Megabytes(maxBytes)}). Split it into several files, or import a ListenBrainz export as the .zip it came in.";
}

/// <summary>Reads a stream up to a size and fails with <see cref="InvalidDataException"/> beyond it, whatever the source claimed its size to be.</summary>
public sealed class SizeLimitedStream(Stream inner, long maxBytes, string tooLarge) : Stream
{
    /// <summary>Bytes read so far.</summary>
    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Count(int read)
    {
        BytesRead += read;
        return BytesRead > maxBytes ? throw new InvalidDataException(tooLarge) : read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
