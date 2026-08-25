using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using SharpCompress.Common;

namespace SearchDuplicateFiles.WinForms;

public sealed record ScanOptions(
    IReadOnlyList<string> RootPaths,
    IReadOnlyList<string> ArchivePaths,
    bool IncludeSubdirectories,
    bool IncludeHiddenAndSystemFiles,
    bool OnlyShowAcrossDifferentRootFolders,
    long MinimumSizeBytes,
    FileComparisonMode ComparisonMode = FileComparisonMode.Content);

public sealed record DuplicateFile(
    string FullPath,
    string RootPath,
    long Size,
    DateTime LastWriteTimeUtc,
    string Sha256,
    string? ArchivePath = null,
    string? ArchiveEntryPath = null)
{
    public bool IsArchiveEntry => ArchivePath is not null;
}

public sealed record DuplicateFileGroup(
    int Number,
    long Size,
    string Sha256,
    IReadOnlyList<DuplicateFile> Files);

public sealed record DuplicateScanResult(
    IReadOnlyList<DuplicateFileGroup> Groups,
    int TotalFilesSeen,
    int CandidateFiles,
    int FilesHashed,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed,
    FileComparisonMode ComparisonMode = FileComparisonMode.Content)
{
    public int DuplicateFileCount => Groups.Sum(group => group.Files.Count);

    public int ExtraDuplicateFileCount => Groups.Sum(group => group.Files.Count - 1);

    public long ReclaimableBytes => ComparisonMode == FileComparisonMode.Content
        ? Groups.Sum(group => (group.Files.Count - 1) * group.Size)
        : 0;
}

public enum ScanStage
{
    Enumerating,
    Comparing,
    Hashing,
    Finished
}

public sealed record ScanProgress(
    ScanStage Stage,
    int FilesSeen,
    int CandidateFiles,
    int FilesHashed,
    int DuplicateGroups,
    string? CurrentPath);

public sealed class DuplicateScanner
{
    public const string ArchiveFileDialogFilter = "対応する圧縮ファイル|*.zip;*.7z;*.tar;*.tar.gz;*.tgz;*.tar.bz2;*.tbz2;*.tbz;*.tar.xz;*.txz;*.tar.lz;*.tar.lzip;*.tlz;*.tar.zst;*.tar.zstd;*.tzst|すべてのファイル (*.*)|*.*";

    private const int BufferSize = 1024 * 1024;
    private const int ProgressIntervalMilliseconds = 120;
    private const int MaximumArchiveEntries = 250_000;
    private const long MaximumArchiveEntrySizeBytes = 64L * 1024 * 1024 * 1024;
    private const long MaximumArchiveTotalSizeBytes = 512L * 1024 * 1024 * 1024;

    private static readonly string[] SupportedArchiveSuffixes =
    [
        ".tar.bz2",
        ".tar.lzip",
        ".tar.lz",
        ".tar.xz",
        ".tar.zst",
        ".tar.zstd",
        ".tar.gz",
        ".tbz2",
        ".tzst",
        ".tgz",
        ".tbz",
        ".txz",
        ".tlz",
        ".zip",
        ".7z",
        ".tar"
    ];

    public static bool IsSupportedArchivePath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && SupportedArchiveSuffixes.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<DuplicateScanResult> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var rootPaths = NormalizeRootPaths(options.RootPaths).ToArray();
        var archivePaths = NormalizeArchivePaths(options.ArchivePaths).ToArray();
        if (rootPaths.Length == 0 && archivePaths.Length == 0)
        {
            throw new ArgumentException("At least one folder or supported archive is required.", nameof(options));
        }

        var stopwatch = Stopwatch.StartNew();
        var progressStopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var filesBySize = new Dictionary<long, List<FileCandidate>>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filesSeen = 0;

        void ReportProgress(ScanProgress scanProgress, bool force = false)
        {
            if (progress is null)
            {
                return;
            }

            if (!force && progressStopwatch.ElapsedMilliseconds < ProgressIntervalMilliseconds)
            {
                return;
            }

            progress.Report(scanProgress);
            progressStopwatch.Restart();
        }

        foreach (var rootPath in rootPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnumerateFiles(rootPath, options, seenPaths, filesBySize, warnings, ReportProgress, ref filesSeen, cancellationToken);
        }

        foreach (var archivePath in archivePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnumerateArchive(archivePath, options, filesBySize, warnings, ReportProgress, ref filesSeen, cancellationToken);
        }

        if (options.ComparisonMode != FileComparisonMode.Content)
        {
            ReportProgress(new ScanProgress(ScanStage.Comparing, filesSeen, filesSeen, 0, 0, null), force: true);
            var matchingFileSets = filesBySize.Values
                .SelectMany(files => files)
                .Select(CreateUnhashedDuplicateFile)
                .GroupBy(
                    file => CreateComparisonKey(file, options.ComparisonMode),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderBy(file => file.RootPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray())
                .Where(files => IsDuplicateGroup(files, options.OnlyShowAcrossDifferentRootFolders))
                .OrderBy(files => GetComparisonFileName(files[0]), StringComparer.OrdinalIgnoreCase)
                .ThenBy(files => files[0].Size)
                .ToArray();

            cancellationToken.ThrowIfCancellationRequested();
            var candidateFileCount = matchingFileSets.Sum(files => files.Length);
            var nameBasedGroups = matchingFileSets
                .Select((files, index) => new DuplicateFileGroup(
                    index + 1,
                    files[0].Size,
                    string.Empty,
                    files))
                .ToArray();

            stopwatch.Stop();
            ReportProgress(new ScanProgress(
                ScanStage.Finished,
                filesSeen,
                candidateFileCount,
                0,
                nameBasedGroups.Length,
                null),
                force: true);

            return new DuplicateScanResult(
                nameBasedGroups,
                filesSeen,
                candidateFileCount,
                0,
                warnings,
                stopwatch.Elapsed,
                options.ComparisonMode);
        }

        var candidateFiles = filesBySize
            .Where(pair => pair.Value.Count > 1)
            .SelectMany(pair => pair.Value)
            .OrderBy(file => file.Size)
            .ThenBy(file => file.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ReportProgress(new ScanProgress(ScanStage.Hashing, filesSeen, candidateFiles.Count, 0, 0, null), force: true);

        var hashedFiles = new Dictionary<string, List<DuplicateFile>>(StringComparer.OrdinalIgnoreCase);
        var filesHashed = 0;

        void AddHashedFile(DuplicateFile duplicateFile)
        {
            var key = $"{duplicateFile.Size}:{duplicateFile.Sha256}";
            if (!hashedFiles.TryGetValue(key, out var list))
            {
                list = new List<DuplicateFile>();
                hashedFiles[key] = list;
            }

            list.Add(duplicateFile);
            filesHashed++;
        }

        foreach (var candidate in candidateFiles.Where(candidate => !candidate.IsArchiveEntry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(new ScanProgress(
                ScanStage.Hashing,
                filesSeen,
                candidateFiles.Count,
                filesHashed,
                0,
                candidate.DisplayPath));

            try
            {
                var duplicateFile = await HashFileAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (duplicateFile is null)
                {
                    warnings.Add($"スキップ: スキャン中に見つからないかサイズが変わりました: {candidate.DisplayPath}");
                    continue;
                }

                AddHashedFile(duplicateFile);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsExpectedFileException(ex))
            {
                warnings.Add($"スキップ: 読み取りできませんでした: {candidate.DisplayPath} ({ex.Message})");
            }
        }

        foreach (var archiveGroup in candidateFiles
            .Where(candidate => candidate.IsArchiveEntry)
            .GroupBy(candidate => candidate.ArchivePath!, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                HashArchiveCandidates(
                    archiveGroup.Key,
                    archiveGroup.ToArray(),
                    warnings,
                    candidate => ReportProgress(new ScanProgress(
                        ScanStage.Hashing,
                        filesSeen,
                        candidateFiles.Count,
                        filesHashed,
                        0,
                        candidate.DisplayPath)),
                    AddHashedFile,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsExpectedFileException(ex))
            {
                warnings.Add($"圧縮ファイルの内容を確認できませんでした: {archiveGroup.Key} ({ex.Message})");
            }
        }

        var groups = hashedFiles.Values
            .Where(files => IsDuplicateGroup(files, options.OnlyShowAcrossDifferentRootFolders))
            .OrderByDescending(files => (files.Count - 1) * files[0].Size)
            .ThenByDescending(files => files[0].Size)
            .Select((files, index) => new DuplicateFileGroup(
                index + 1,
                files[0].Size,
                files[0].Sha256,
                files.OrderBy(file => file.RootPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .ToArray();

        stopwatch.Stop();
        ReportProgress(new ScanProgress(ScanStage.Finished, filesSeen, candidateFiles.Count, filesHashed, groups.Length, null), force: true);

        return new DuplicateScanResult(
            groups,
            filesSeen,
            candidateFiles.Count,
            filesHashed,
            warnings,
            stopwatch.Elapsed,
            options.ComparisonMode);
    }

    private static void EnumerateFiles(
        string rootPath,
        ScanOptions options,
        HashSet<string> seenPaths,
        Dictionary<long, List<FileCandidate>> filesBySize,
        List<string> warnings,
        Action<ScanProgress, bool> reportProgress,
        ref int filesSeen,
        CancellationToken cancellationToken)
    {
        IEnumerable<string> files;

        try
        {
            files = Directory.EnumerateFiles(rootPath, "*", CreateEnumerationOptions(options));
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            warnings.Add($"列挙できませんでした: {rootPath} ({ex.Message})");
            return;
        }

        try
        {
            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!seenPaths.Add(path))
                {
                    continue;
                }

                filesSeen++;
                reportProgress(new ScanProgress(ScanStage.Enumerating, filesSeen, 0, 0, 0, path), false);

                FileInfo fileInfo;
                try
                {
                    fileInfo = new FileInfo(path);
                    if (!fileInfo.Exists || fileInfo.Length < options.MinimumSizeBytes)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (IsExpectedFileException(ex))
                {
                    warnings.Add($"スキップ: 情報を取得できませんでした: {path} ({ex.Message})");
                    continue;
                }

                AddCandidate(filesBySize, new FileCandidate(
                    fileInfo.FullName,
                    rootPath,
                    fileInfo.Length,
                    fileInfo.LastWriteTimeUtc));
            }
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            warnings.Add($"列挙中にエラーが発生しました: {rootPath} ({ex.Message})");
        }
    }

    private static void EnumerateArchive(
        string archivePath,
        ScanOptions options,
        Dictionary<long, List<FileCandidate>> filesBySize,
        List<string> warnings,
        Action<ScanProgress, bool> reportProgress,
        ref int filesSeen,
        CancellationToken cancellationToken)
    {
        try
        {
            var entryIndex = -1;
            var entryCount = 0;
            var archiveFilesSeen = filesSeen;
            long totalSize = 0;

            bool AddArchiveEntry(IEntry entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entryIndex++;
                entryCount++;

                if (entryCount > MaximumArchiveEntries)
                {
                    warnings.Add($"圧縮ファイルのエントリ数が上限 {MaximumArchiveEntries:N0} 件を超えたため、残りをスキップしました: {archivePath}");
                    return false;
                }

                if (entry.IsDirectory || !string.IsNullOrWhiteSpace(entry.LinkTarget))
                {
                    return true;
                }

                var entryPath = NormalizeArchiveEntryPath(entry.Key, entryIndex);
                archiveFilesSeen++;
                var displayPath = CreateArchiveDisplayPath(archivePath, entryPath);
                reportProgress(new ScanProgress(ScanStage.Enumerating, archiveFilesSeen, 0, 0, 0, displayPath), false);

                if (options.ComparisonMode == FileComparisonMode.Content && entry.IsEncrypted)
                {
                    warnings.Add($"スキップ: 暗号化されたエントリには対応していません: {displayPath}");
                    return true;
                }

                if (entry.Size < 0)
                {
                    warnings.Add($"スキップ: サイズを取得できませんでした: {displayPath}");
                    return true;
                }

                if (options.ComparisonMode == FileComparisonMode.Content
                    && entry.Size > MaximumArchiveEntrySizeBytes)
                {
                    warnings.Add($"展開後サイズが安全上限 {FormatSize(MaximumArchiveEntrySizeBytes)} を超えたため、この圧縮ファイルの残りをスキップしました: {displayPath}");
                    return false;
                }

                if (options.ComparisonMode == FileComparisonMode.Content
                    && totalSize > MaximumArchiveTotalSizeBytes - entry.Size)
                {
                    warnings.Add($"圧縮ファイルの展開後合計サイズが安全上限 {FormatSize(MaximumArchiveTotalSizeBytes)} を超えたため、残りをスキップしました: {archivePath}");
                    return false;
                }

                if (options.ComparisonMode == FileComparisonMode.Content)
                {
                    totalSize += entry.Size;
                }

                if (entry.Size < options.MinimumSizeBytes || ShouldSkipArchiveEntry(entry, archivePath, options))
                {
                    return true;
                }

                AddCandidate(filesBySize, new FileCandidate(
                    displayPath,
                    archivePath,
                    entry.Size,
                    (entry.LastModifiedTime ?? File.GetLastWriteTimeUtc(archivePath)).ToUniversalTime(),
                    archivePath,
                    entryPath,
                    entryIndex));

                return true;
            }

            reportProgress(new ScanProgress(ScanStage.Enumerating, archiveFilesSeen, 0, 0, 0, archivePath), true);
            if (options.ComparisonMode != FileComparisonMode.Content
                && archivePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            {
                using var session = ArchiveStreamReader.OpenMetadata(archivePath);
                foreach (var entry in session.Archive.Entries)
                {
                    if (!AddArchiveEntry(entry))
                    {
                        break;
                    }
                }
            }
            else
            {
                using var session = ArchiveStreamReader.Open(archivePath);
                while (session.Reader.MoveToNextEntry())
                {
                    if (!AddArchiveEntry(session.Reader.Entry))
                    {
                        break;
                    }
                }
            }

            filesSeen = archiveFilesSeen;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            warnings.Add($"圧縮ファイルを読み取りできませんでした: {archivePath} ({ex.Message})");
        }
    }

    private static void HashArchiveCandidates(
        string archivePath,
        IReadOnlyList<FileCandidate> candidates,
        List<string> warnings,
        Action<FileCandidate> reportCandidate,
        Action<DuplicateFile> addHashedFile,
        CancellationToken cancellationToken)
    {
        var candidatesByIndex = candidates.ToDictionary(candidate => candidate.ArchiveEntryIndex);
        var remainingIndices = candidatesByIndex.Keys.ToHashSet();

        using var session = ArchiveStreamReader.Open(archivePath);
        var entryIndex = -1;

        while (remainingIndices.Count > 0 && session.Reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryIndex++;

            if (!candidatesByIndex.TryGetValue(entryIndex, out var candidate))
            {
                continue;
            }

            remainingIndices.Remove(entryIndex);
            reportCandidate(candidate);

            var entry = session.Reader.Entry;
            var entryPath = NormalizeArchiveEntryPath(entry.Key, entryIndex);
            if (entry.IsDirectory
                || entry.IsEncrypted
                || entry.Size != candidate.Size
                || !string.Equals(entryPath, candidate.ArchiveEntryPath, StringComparison.Ordinal))
            {
                warnings.Add($"スキップ: スキャン中に圧縮ファイルの内容が変わりました: {candidate.DisplayPath}");
                continue;
            }

            using var stream = session.Reader.OpenEntryStream();
            var hash = ComputeSha256(stream, candidate.Size, cancellationToken);
            addHashedFile(new DuplicateFile(
                candidate.DisplayPath,
                candidate.RootPath,
                candidate.Size,
                candidate.LastWriteTimeUtc,
                hash,
                candidate.ArchivePath,
                candidate.ArchiveEntryPath));
        }

        foreach (var missingIndex in remainingIndices)
        {
            warnings.Add($"スキップ: 圧縮ファイル内のエントリが見つかりませんでした: {candidatesByIndex[missingIndex].DisplayPath}");
        }
    }

    private static bool ShouldSkipArchiveEntry(IEntry entry, string archivePath, ScanOptions options)
    {
        if (options.IncludeHiddenAndSystemFiles)
        {
            return false;
        }

        var pathSegments = (entry.Key ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Any(segment => segment.StartsWith(".", StringComparison.Ordinal) && segment.Length > 1))
        {
            return true;
        }

        if (!archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && !archivePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var attributes = (FileAttributes)(entry.Attrib ?? 0);
        return (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
    }

    private static void AddCandidate(Dictionary<long, List<FileCandidate>> filesBySize, FileCandidate candidate)
    {
        if (!filesBySize.TryGetValue(candidate.Size, out var list))
        {
            list = new List<FileCandidate>();
            filesBySize[candidate.Size] = list;
        }

        list.Add(candidate);
    }

    private static EnumerationOptions CreateEnumerationOptions(ScanOptions options)
    {
        var attributesToSkip = FileAttributes.ReparsePoint;
        if (!options.IncludeHiddenAndSystemFiles)
        {
            attributesToSkip |= FileAttributes.Hidden | FileAttributes.System;
        }

        return new EnumerationOptions
        {
            RecurseSubdirectories = options.IncludeSubdirectories,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = attributesToSkip,
            MatchCasing = MatchCasing.CaseInsensitive
        };
    }

    private static async Task<DuplicateFile?> HashFileAsync(FileCandidate candidate, CancellationToken cancellationToken)
    {
        var currentInfo = new FileInfo(candidate.FullPath);
        if (!currentInfo.Exists || currentInfo.Length != candidate.Size)
        {
            return null;
        }

        await using var stream = new FileStream(
            candidate.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new DuplicateFile(
            candidate.FullPath,
            candidate.RootPath,
            currentInfo.Length,
            currentInfo.LastWriteTimeUtc,
            Convert.ToHexString(hash));
    }

    private static DuplicateFile CreateUnhashedDuplicateFile(FileCandidate candidate)
    {
        return new DuplicateFile(
            candidate.FullPath,
            candidate.RootPath,
            candidate.Size,
            candidate.LastWriteTimeUtc,
            string.Empty,
            candidate.ArchivePath,
            candidate.ArchiveEntryPath);
    }

    private static string CreateComparisonKey(DuplicateFile file, FileComparisonMode comparisonMode)
    {
        var fileName = GetComparisonFileName(file);
        return comparisonMode switch
        {
            FileComparisonMode.FileName => fileName,
            FileComparisonMode.FileNameAndSize => $"{fileName}\0{file.Size}",
            _ => throw new ArgumentOutOfRangeException(nameof(comparisonMode))
        };
    }

    private static string GetComparisonFileName(DuplicateFile file)
    {
        var path = file.ArchiveEntryPath ?? file.FullPath;
        return Path.GetFileName(path.Replace('/', '\\'));
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
                    throw new InvalidDataException("エントリの実データが記録されたサイズを超えています。");
                }

                hash.AppendData(buffer, 0, bytesRead);
            }

            if (totalBytesRead != expectedSize)
            {
                throw new InvalidDataException("エントリの実データと記録されたサイズが一致しません。");
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IEnumerable<string> NormalizeRootPaths(IReadOnlyList<string> rootPaths)
    {
        return rootPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> NormalizeArchivePaths(IReadOnlyList<string> archivePaths)
    {
        return archivePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path.Trim()))
            .Where(path => File.Exists(path) && IsSupportedArchivePath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsDuplicateGroup(IReadOnlyList<DuplicateFile> files, bool onlyShowAcrossDifferentRootFolders)
    {
        if (files.Count <= 1)
        {
            return false;
        }

        return !onlyShowAcrossDifferentRootFolders
            || files.Select(file => file.RootPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
    }

    private static bool IsExpectedFileException(Exception ex)
    {
        return ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or SharpCompressException
            or NotSupportedException
            or ArgumentException
            or PathTooLongException;
    }

    private static string NormalizeArchiveEntryPath(string? entryPath, int entryIndex)
    {
        return string.IsNullOrWhiteSpace(entryPath)
            ? $"(名称なしエントリ {entryIndex + 1:N0})"
            : entryPath.Replace('/', '\\');
    }

    private static string CreateArchiveDisplayPath(string archivePath, string entryPath)
    {
        return $"{archivePath} :: {entryPath}";
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes:N0} {units[unitIndex]}" : $"{value:N0} {units[unitIndex]}";
    }

    private sealed record FileCandidate(
        string FullPath,
        string RootPath,
        long Size,
        DateTime LastWriteTimeUtc,
        string? ArchivePath = null,
        string? ArchiveEntryPath = null,
        int ArchiveEntryIndex = -1)
    {
        public bool IsArchiveEntry => ArchivePath is not null;

        public string DisplayPath => FullPath;
    }

}
