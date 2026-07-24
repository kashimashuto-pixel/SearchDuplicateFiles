using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace SearchDuplicateFiles.WinForms;

public sealed record ScanOptions(
    IReadOnlyList<string> RootPaths,
    IReadOnlyList<string> ArchivePaths,
    bool IncludeSubdirectories,
    bool IncludeHiddenAndSystemFiles,
    bool OnlyShowAcrossDifferentRootFolders,
    long MinimumSizeBytes);

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
    TimeSpan Elapsed)
{
    public int DuplicateFileCount => Groups.Sum(group => group.Files.Count);

    public int ExtraDuplicateFileCount => Groups.Sum(group => group.Files.Count - 1);

    public long ReclaimableBytes => Groups.Sum(group => (group.Files.Count - 1) * group.Size);
}

public enum ScanStage
{
    Enumerating,
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
    private const int BufferSize = 1024 * 1024;
    private const int ProgressIntervalMilliseconds = 120;

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
            throw new ArgumentException("At least one folder or ZIP archive is required.", nameof(options));
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

        var candidateFiles = filesBySize
            .Where(pair => pair.Value.Count > 1)
            .SelectMany(pair => pair.Value)
            .OrderBy(file => file.Size)
            .ThenBy(file => file.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ReportProgress(new ScanProgress(ScanStage.Hashing, filesSeen, candidateFiles.Count, 0, 0, null), force: true);

        var hashedFiles = new Dictionary<string, List<DuplicateFile>>(StringComparer.OrdinalIgnoreCase);
        var openArchives = new Dictionary<string, ZipArchive>(StringComparer.OrdinalIgnoreCase);
        var filesHashed = 0;

        try
        {
            foreach (var candidate in candidateFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(new ScanProgress(
                    ScanStage.Hashing,
                    filesSeen,
                    candidateFiles.Count,
                    filesHashed,
                    0,
                    candidate.DisplayPath));

                DuplicateFile? duplicateFile;
                try
                {
                    duplicateFile = candidate.IsArchiveEntry
                        ? await HashArchiveEntryAsync(candidate, openArchives, cancellationToken).ConfigureAwait(false)
                        : await HashFileAsync(candidate, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsExpectedFileException(ex))
                {
                    warnings.Add($"スキップ: 読み取りできませんでした: {candidate.DisplayPath} ({ex.Message})");
                    continue;
                }

                if (duplicateFile is null)
                {
                    warnings.Add($"スキップ: スキャン中に見つからないかサイズが変わりました: {candidate.DisplayPath}");
                    continue;
                }

                var key = $"{duplicateFile.Size}:{duplicateFile.Sha256}";
                if (!hashedFiles.TryGetValue(key, out var list))
                {
                    list = new List<DuplicateFile>();
                    hashedFiles[key] = list;
                }

                list.Add(duplicateFile);
                filesHashed++;
            }
        }
        finally
        {
            foreach (var archive in openArchives.Values)
            {
                archive.Dispose();
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

        return new DuplicateScanResult(groups, filesSeen, candidateFiles.Count, filesHashed, warnings, stopwatch.Elapsed);
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
            using var archive = ZipFile.OpenRead(archivePath);
            for (var index = 0; index < archive.Entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.Entries[index];

                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                filesSeen++;
                var displayPath = CreateArchiveDisplayPath(archivePath, entry.FullName);
                reportProgress(new ScanProgress(ScanStage.Enumerating, filesSeen, 0, 0, 0, displayPath), false);

                if (entry.Length < options.MinimumSizeBytes || ShouldSkipArchiveEntry(entry, options))
                {
                    continue;
                }

                AddCandidate(filesBySize, new FileCandidate(
                    displayPath,
                    archivePath,
                    entry.Length,
                    entry.LastWriteTime.UtcDateTime,
                    archivePath,
                    entry.FullName,
                    index));
            }
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            warnings.Add($"ZIPを読み取りできませんでした: {archivePath} ({ex.Message})");
        }
    }

    private static bool ShouldSkipArchiveEntry(ZipArchiveEntry entry, ScanOptions options)
    {
        if (options.IncludeHiddenAndSystemFiles)
        {
            return false;
        }

        var attributes = (FileAttributes)(entry.ExternalAttributes & 0xffff);
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

        var hash = await ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
        return new DuplicateFile(
            candidate.FullPath,
            candidate.RootPath,
            currentInfo.Length,
            currentInfo.LastWriteTimeUtc,
            hash);
    }

    private static async Task<DuplicateFile?> HashArchiveEntryAsync(
        FileCandidate candidate,
        Dictionary<string, ZipArchive> openArchives,
        CancellationToken cancellationToken)
    {
        if (!openArchives.TryGetValue(candidate.ArchivePath!, out var archive))
        {
            archive = ZipFile.OpenRead(candidate.ArchivePath!);
            openArchives[candidate.ArchivePath!] = archive;
        }

        if (candidate.ArchiveEntryIndex < 0 || candidate.ArchiveEntryIndex >= archive.Entries.Count)
        {
            return null;
        }

        var entry = archive.Entries[candidate.ArchiveEntryIndex];
        if (entry.Length != candidate.Size
            || !string.Equals(entry.FullName, candidate.ArchiveEntryPath, StringComparison.Ordinal))
        {
            return null;
        }

        await using var stream = entry.Open();
        var hash = await ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
        return new DuplicateFile(
            candidate.DisplayPath,
            candidate.RootPath,
            entry.Length,
            entry.LastWriteTime.UtcDateTime,
            hash,
            candidate.ArchivePath,
            candidate.ArchiveEntryPath);
    }

    private static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
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
            .Where(path => File.Exists(path) && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
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
            or InvalidDataException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException
            or ArgumentException
            or PathTooLongException;
    }

    private static string CreateArchiveDisplayPath(string archivePath, string entryPath)
    {
        return $"{archivePath} :: {entryPath.Replace('/', '\\')}";
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
