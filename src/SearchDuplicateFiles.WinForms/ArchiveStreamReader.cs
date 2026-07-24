using SharpCompress.Archives;
using SharpCompress.Readers;

namespace SearchDuplicateFiles.WinForms;

internal static class ArchiveStreamReader
{
    private const int BufferSize = 1024 * 1024;

    public static ArchiveReaderSession Open(string archivePath)
    {
        var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);

        try
        {
            var options = ReaderOptions.ForExternalStream.WithExtensionHint(Path.GetFileName(archivePath));
            if (archivePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            {
                var archive = ArchiveFactory.OpenArchive(stream, options);
                return new ArchiveReaderSession(archive.ExtractAllEntries(), archive, stream);
            }

            return new ArchiveReaderSession(ReaderFactory.OpenReader(stream, options), stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}

internal sealed class ArchiveReaderSession : IDisposable
{
    private readonly IReadOnlyList<IDisposable> _owners;

    public ArchiveReaderSession(IReader reader, params IDisposable[] owners)
    {
        Reader = reader;
        _owners = owners;
    }

    public IReader Reader { get; }

    public void Dispose()
    {
        Exception? firstException = null;
        TryDispose(Reader, ref firstException);
        foreach (var owner in _owners)
        {
            TryDispose(owner, ref firstException);
        }

        if (firstException is not null)
        {
            throw firstException;
        }
    }

    private static void TryDispose(IDisposable disposable, ref Exception? firstException)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }
    }
}
