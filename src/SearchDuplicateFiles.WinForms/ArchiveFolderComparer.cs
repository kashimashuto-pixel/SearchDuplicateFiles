using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using SharpCompress.Common;

namespace SearchDuplicateFiles.WinForms;

public enum ArchiveFolderComparisonStatus
{
    Match,
    ArchiveOnly,
    FolderOnly,
    SizeMismatch,
    ContentMismatch,
    Unreadable,
    DuplicateArchivePath,
    UnsupportedEntry
}

public sealed record ArchiveFolderComparisonItem(
    string RelativePath,
    ArchiveFolderComparisonStatus Status,
    long? ArchiveSize,
    long? FolderSize,
    string? ArchiveSha256 = null,
    string? FolderSha256 = null);

public sealed record ArchiveFolderComparisonResult(
    string ArchivePath,
    string FolderPath,
    bool IgnoredArchiveTopLevelFolder,
    IReadOnlyList<ArchiveFolderComparisonItem> Items,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed,
    FileComparisonMode ComparisonMode = FileComparisonMode.Content)
{
    public int MatchCount => Items.Count(item => item.Status == ArchiveFolderComparisonStatus.Match);

    public int DifferenceCount => Items.Count(item => item.Status != ArchiveFolderComparisonStatus.Match);

    public bool IsExactMatch => DifferenceCount == 0 && Warnings.Count == 0;
}

public sealed record ArchiveFolderComparisonProgress(
    string Stage,
    int ItemsProcessed,
    string? CurrentPath);

public sealed class ArchiveFolderComparer
{
    private const int BufferSize = 1024 * 1024;
    private const int ProgressIntervalMilliseconds = 120;
    private const int MaximumArchiveEntries = 250_000;
    private const long MaximumArchiveEntrySizeBytes = 64L * 1024 * 1024 * 1024;
    private const long MaximumArchiveTotalSizeBytes = 512L * 1024 * 1024 * 1024;

    public ArchiveFolderComparisonResult Compare(
        string archivePath,
        string folderPath,
        IProgress<ArchiveFolderComparisonProgress>? progress,
        CancellationToken cancellationToken,
        bool ignoreArchiveTopLevelFolder = false,
        FileComparisonMode comparisonMode = FileComparisonMode.Content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        archivePath = Path.GetFullPath(archivePath);
        folderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("圧縮ファイルが見つかりません。", archivePath);
        }

        if (!DuplicateScanner.IsSupportedArchivePath(archivePath))
        {
            throw new NotSupportedException("対応していない圧縮形式です。");
        }

        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"フォルダーが見つかりません: {folderPath}");
        }

        var stopwatch = Stopwatch.StartNew();
        var progressStopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var items = new List<ArchiveFolderComparisonItem>();

        void ReportProgress(ArchiveFolderComparisonProgress comparisonProgress, bool force = false)
        {
            if (progress is null)
            {
                return;
            }

            if (!force && progressStopwatch.ElapsedMilliseconds < ProgressIntervalMilliseconds)
            {
                return;
            }

            progress.Report(comparisonProgress);
            progressStopwatch.Restart();
        }

        ReportProgress(new ArchiveFolderComparisonProgress("フォルダーの列挙を準備中", 0, folderPath), force: true);
        var folderFiles = EnumerateFolder(folderPath, warnings, ReportProgress, cancellationToken);
        ReportProgress(new ArchiveFolderComparisonProgress("圧縮ファイルを解析中", 0, archivePath), force: true);
        var archivePathsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedFolderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemsProcessed = 0;

        try
        {
            var entryCount = 0;
            long totalSize = 0;

            bool CompareEntry(IEntry entry, Func<Stream>? openEntryStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entryCount++;
                if (entryCount > MaximumArchiveEntries)
                {
                    warnings.Add($"エントリ数が安全上限 {MaximumArchiveEntries:N0} 件を超えたため、比較を中断しました。");
                    items.Add(new ArchiveFolderComparisonItem(
                        "（安全上限を超えた残りのエントリ）",
                        ArchiveFolderComparisonStatus.Unreadable,
                        null,
                        null));
                    return false;
                }

                if (entry.IsDirectory)
                {
                    return true;
                }

                itemsProcessed++;
                var originalEntryPath = entry.Key ?? $"(名称なしエントリ {entryCount:N0})";
                ReportProgress(new ArchiveFolderComparisonProgress("圧縮ファイルを比較中", itemsProcessed, originalEntryPath));

                if (!string.IsNullOrWhiteSpace(entry.LinkTarget))
                {
                    items.Add(new ArchiveFolderComparisonItem(
                        NormalizeDisplayPath(originalEntryPath),
                        ArchiveFolderComparisonStatus.UnsupportedEntry,
                        entry.Size >= 0 ? entry.Size : null,
                        null));
                    return true;
                }

                if (!TryNormalizeRelativePath(originalEntryPath, out var relativePath))
                {
                    warnings.Add($"安全でない内部パスのため比較できません: {originalEntryPath}");
                    items.Add(new ArchiveFolderComparisonItem(
                        NormalizeDisplayPath(originalEntryPath),
                        ArchiveFolderComparisonStatus.Unreadable,
                        entry.Size >= 0 ? entry.Size : null,
                        null));
                    return true;
                }

                if (ignoreArchiveTopLevelFolder)
                {
                    relativePath = RemoveTopLevelFolder(relativePath);
                }

                if (!archivePathsSeen.Add(relativePath))
                {
                    items.Add(new ArchiveFolderComparisonItem(
                        relativePath,
                        ArchiveFolderComparisonStatus.DuplicateArchivePath,
                        entry.Size >= 0 ? entry.Size : null,
                        folderFiles.TryGetValue(relativePath, out var duplicateFolderFile) ? duplicateFolderFile.Size : null));
                    return true;
                }

                if (comparisonMode == FileComparisonMode.Content && entry.IsEncrypted)
                {
                    matchedFolderPaths.Add(relativePath);
                    items.Add(new ArchiveFolderComparisonItem(
                        relativePath,
                        ArchiveFolderComparisonStatus.Unreadable,
                        entry.Size >= 0 ? entry.Size : null,
                        folderFiles.TryGetValue(relativePath, out var encryptedFolderFile) ? encryptedFolderFile.Size : null));
                    warnings.Add($"暗号化されたエントリの内容は比較できません: {relativePath}");
                    return true;
                }

                if (comparisonMode != FileComparisonMode.FileName && entry.Size < 0)
                {
                    matchedFolderPaths.Add(relativePath);
                    items.Add(new ArchiveFolderComparisonItem(
                        relativePath,
                        ArchiveFolderComparisonStatus.Unreadable,
                        null,
                        folderFiles.TryGetValue(relativePath, out var unknownSizeFolderFile) ? unknownSizeFolderFile.Size : null));
                    warnings.Add($"展開後サイズが不明なため比較できません: {relativePath}");
                    return true;
                }

                if (comparisonMode == FileComparisonMode.Content)
                {
                    if (entry.Size > MaximumArchiveEntrySizeBytes)
                    {
                        matchedFolderPaths.Add(relativePath);
                        items.Add(new ArchiveFolderComparisonItem(
                            relativePath,
                            ArchiveFolderComparisonStatus.Unreadable,
                            entry.Size,
                            folderFiles.TryGetValue(relativePath, out var oversizedFolderFile) ? oversizedFolderFile.Size : null));
                        warnings.Add($"展開後サイズが安全上限を超えています: {relativePath}");
                        return false;
                    }

                    if (totalSize > MaximumArchiveTotalSizeBytes - entry.Size)
                    {
                        warnings.Add("圧縮ファイルの展開後合計サイズが安全上限を超えたため、比較を中断しました。");
                        items.Add(new ArchiveFolderComparisonItem(
                            "（安全上限を超えた残りのエントリ）",
                            ArchiveFolderComparisonStatus.Unreadable,
                            null,
                            null));
                        return false;
                    }

                    totalSize += entry.Size;
                }

                if (!folderFiles.TryGetValue(relativePath, out var folderFile))
                {
                    items.Add(new ArchiveFolderComparisonItem(
                        relativePath,
                        ArchiveFolderComparisonStatus.ArchiveOnly,
                        entry.Size >= 0 ? entry.Size : null,
                        null));
                    return true;
                }

                matchedFolderPaths.Add(relativePath);
                if (comparisonMode == FileComparisonMode.FileName)
                {
                    items.Add(new ArchiveFolderComparisonItem(
                        relativePath,
                        ArchiveFolderComparisonStatus.Match,
                        entry.Size >= 0 ? entry.Size : null,
                        folderFile.Size));
                    return true;
                }

                if (entry.Size != folderFile.Size)
                {
                    items.Add(new ArchiveFolderComparisonItem(
                        relativePath,
                        ArchiveFolderComparisonStatus.SizeMismatch,
                        entry.Size,
                        folderFile.Size));
                    return true;
                }

                if (comparisonMode == FileComparisonMode.FileNameAndSize)
                {
                    items.Add(new ArchiveFolderComparisonItem(
                        relativePath,
                        ArchiveFolderComparisonStatus.Match,
                        entry.Size,
                        folderFile.Size));
                    return true;
                }

                try
                {
                    var currentFolderInfo = new FileInfo(folderFile.FullPath);
                    if (!currentFolderInfo.Exists || currentFolderInfo.Length != folderFile.Size)
                    {
                        throw new IOException("比較中にフォルダー側のファイルが変更されました。");
                    }

                    using var entryStream = openEntryStream?.Invoke()
                        ?? throw new InvalidOperationException("圧縮ファイルの内容を読み取るストリームがありません。");
                    var archiveHash = ComputeSha256(entryStream, entry.Size, cancellationToken);
                    using var folderStream = new FileStream(
                        folderFile.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        BufferSize,
                        FileOptions.SequentialScan);
                    var folderHash = ComputeSha256(folderStream, folderFile.Size, cancellationToken);
                    var status = string.Equals(archiveHash, folderHash, StringComparison.Ordinal)
                        ? ArchiveFolderComparisonStatus.Match
                        : ArchiveFolderComparisonStatus.ContentMismatch;

                    items.Add(new ArchiveFolderComparisonItem(
                        relativePath,
                        status,
                        entry.Size,
                        folderFile.Size,
                        archiveHash,
                        folderHash));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsExpectedException(ex))
                {
                    warnings.Add($"読み取りできませんでした: {relativePath} ({ex.Message})");
                    items.Add(new ArchiveFolderComparisonItem(
                        relativePath,
                        ArchiveFolderComparisonStatus.Unreadable,
                        entry.Size,
                        folderFile.Size));
                }

                return true;
            }

            if (comparisonMode != FileComparisonMode.Content
                && archivePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            {
                using var session = ArchiveStreamReader.OpenMetadata(archivePath);
                var entries = session.Archive.Entries.ToArray();
                ReportProgress(new ArchiveFolderComparisonProgress("圧縮ファイルを比較中", 0, archivePath), force: true);
                foreach (var entry in entries)
                {
                    if (!CompareEntry(entry, null))
                    {
                        break;
                    }
                }
            }
            else
            {
                using var session = ArchiveStreamReader.Open(archivePath);
                ReportProgress(new ArchiveFolderComparisonProgress("圧縮ファイルを比較中", 0, archivePath), force: true);
                while (session.Reader.MoveToNextEntry())
                {
                    if (!CompareEntry(session.Reader.Entry, () => session.Reader.OpenEntryStream()))
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedException(ex))
        {
            warnings.Add($"圧縮ファイルを最後まで読み取りできませんでした: {ex.Message}");
            items.Add(new ArchiveFolderComparisonItem(
                "（圧縮ファイルの読取エラー）",
                ArchiveFolderComparisonStatus.Unreadable,
                null,
                null));
        }

        foreach (var folderFile in folderFiles.Values
            .Where(file => !matchedFolderPaths.Contains(file.RelativePath))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new ArchiveFolderComparisonItem(
                folderFile.RelativePath,
                ArchiveFolderComparisonStatus.FolderOnly,
                null,
                folderFile.Size));
        }

        ReportProgress(new ArchiveFolderComparisonProgress("比較結果を整理中", itemsProcessed, null), force: true);
        stopwatch.Stop();
        return new ArchiveFolderComparisonResult(
            archivePath,
            folderPath,
            ignoreArchiveTopLevelFolder,
            items.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Status)
                .ToArray(),
            warnings,
            stopwatch.Elapsed,
            comparisonMode);
    }

    private static Dictionary<string, FolderFile> EnumerateFolder(
        string folderPath,
        List<string> warnings,
        Action<ArchiveFolderComparisonProgress, bool> reportProgress,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FolderFile>(StringComparer.OrdinalIgnoreCase);
        var filesProcessed = 0;

        try
        {
            foreach (var path in Directory.EnumerateFiles(folderPath, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                filesProcessed++;
                reportProgress(new ArchiveFolderComparisonProgress("フォルダーを列挙中", filesProcessed, path), false);

                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        continue;
                    }

                    var relativePath = NormalizeDisplayPath(Path.GetRelativePath(folderPath, info.FullName));
                    result[relativePath] = new FolderFile(relativePath, info.FullName, info.Length);
                }
                catch (Exception ex) when (IsExpectedException(ex))
                {
                    warnings.Add($"フォルダー側の情報を取得できませんでした: {path} ({ex.Message})");
                }
            }
        }
        catch (Exception ex) when (IsExpectedException(ex))
        {
            warnings.Add($"フォルダーを最後まで列挙できませんでした: {ex.Message}");
        }

        return result;
    }

    private static bool TryNormalizeRelativePath(string path, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || (normalized.Length >= 2 && normalized[1] == ':'))
        {
            return false;
        }

        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !string.Equals(segment, ".", StringComparison.Ordinal))
            .ToArray();
        if (segments.Length == 0 || segments.Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            return false;
        }

        relativePath = string.Join('\\', segments);
        return true;
    }

    private static string NormalizeDisplayPath(string path)
    {
        return path.Replace('/', '\\');
    }

    private static string RemoveTopLevelFolder(string relativePath)
    {
        var separatorIndex = relativePath.IndexOf('\\');
        return separatorIndex > 0 && separatorIndex < relativePath.Length - 1
            ? relativePath[(separatorIndex + 1)..]
            : relativePath;
    }

    private static string ComputeSha256(Stream stream, long expectedSize, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long totalBytesRead = 0;
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                totalBytesRead += bytesRead;
                if (totalBytesRead > expectedSize)
                {
                    throw new InvalidDataException("実データが記録されたサイズを超えています。");
                }

                hash.AppendData(buffer, 0, bytesRead);
            }

            if (totalBytesRead != expectedSize)
            {
                throw new InvalidDataException("実データと記録されたサイズが一致しません。");
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsExpectedException(Exception ex)
    {
        return ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or SharpCompressException
            or NotSupportedException
            or ArgumentException
            or PathTooLongException;
    }

    private sealed record FolderFile(string RelativePath, string FullPath, long Size);
}
