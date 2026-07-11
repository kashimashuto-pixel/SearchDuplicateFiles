using System.Diagnostics;
using System.IO.Enumeration;
using System.Security.Cryptography;

namespace SearchDuplicateFiles.WinForms;

public sealed record ScanOptions(
    IReadOnlyList<string> RootPaths,
    bool IncludeSubdirectories,
    bool IncludeHiddenAndSystemFiles,
    bool OnlyShowAcrossDifferentRootFolders,
    long MinimumSizeBytes,
    IReadOnlyList<string> FileNamePatterns,
    IReadOnlyList<string> FolderNamePatterns);

public sealed record DuplicateFile(
    string FullPath,
    string RootPath,
    long Size,
    DateTime LastWriteTimeUtc,
    string Sha256);

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
        if (rootPaths.Length == 0)
        {
            throw new ArgumentException("At least one root path is required.", nameof(options));
        }

        var stopwatch = Stopwatch.StartNew();
        var progressStopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var filesBySize = new Dictionary<long, List<FileCandidate>>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filesSeen = 0;
        var folderNamePatterns = NormalizePatterns(options.FolderNamePatterns).ToArray();

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
            foreach (var pattern in NormalizePatterns(options.FileNamePatterns))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnumerateFiles(rootPath, options, pattern, folderNamePatterns, seenPaths, filesBySize, warnings, ReportProgress, ref filesSeen, cancellationToken);
            }
        }

        var candidateFiles = filesBySize
            .Where(pair => pair.Value.Count > 1)
            .SelectMany(pair => pair.Value)
            .OrderBy(file => file.Size)
            .ThenBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ReportProgress(new ScanProgress(ScanStage.Hashing, filesSeen, candidateFiles.Count, 0, 0, null), force: true);

        var hashedFiles = new Dictionary<string, List<DuplicateFile>>(StringComparer.OrdinalIgnoreCase);
        var filesHashed = 0;

        foreach (var candidate in candidateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(new ScanProgress(
                ScanStage.Hashing,
                filesSeen,
                candidateFiles.Count,
                filesHashed,
                0,
                candidate.FullPath));

            try
            {
                var currentInfo = new FileInfo(candidate.FullPath);
                if (!currentInfo.Exists)
                {
                    warnings.Add($"スキップ: ファイルが削除されています: {candidate.FullPath}");
                    continue;
                }

                if (currentInfo.Length != candidate.Size)
                {
                    warnings.Add($"スキップ: スキャン中にサイズが変わりました: {candidate.FullPath}");
                    continue;
                }

                var hash = await ComputeSha256Async(candidate.FullPath, cancellationToken).ConfigureAwait(false);
                var duplicateFile = new DuplicateFile(
                    candidate.FullPath,
                    candidate.RootPath,
                    currentInfo.Length,
                    currentInfo.LastWriteTimeUtc,
                    hash);

                var key = $"{duplicateFile.Size}:{duplicateFile.Sha256}";
                if (!hashedFiles.TryGetValue(key, out var list))
                {
                    list = new List<DuplicateFile>();
                    hashedFiles[key] = list;
                }

                list.Add(duplicateFile);
                filesHashed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsExpectedFileException(ex))
            {
                warnings.Add($"スキップ: 読み取りできませんでした: {candidate.FullPath} ({ex.Message})");
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
        string pattern,
        IReadOnlyList<string> folderNamePatterns,
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
            files = Directory.EnumerateFiles(rootPath, pattern, CreateEnumerationOptions(options));
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            warnings.Add($"列挙できませんでした: {rootPath} / {pattern} ({ex.Message})");
            return;
        }

        try
        {
            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!MatchesFolderName(path, folderNamePatterns))
                {
                    continue;
                }

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

                if (!filesBySize.TryGetValue(fileInfo.Length, out var list))
                {
                    list = new List<FileCandidate>();
                    filesBySize[fileInfo.Length] = list;
                }

                list.Add(new FileCandidate(fileInfo.FullName, rootPath, fileInfo.Length));
            }
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            warnings.Add($"列挙中にエラーが発生しました: {rootPath} / {pattern} ({ex.Message})");
        }
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

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

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

    private static IEnumerable<string> NormalizePatterns(IReadOnlyList<string> patterns)
    {
        var normalized = patterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => pattern.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length == 0 ? new[] { "*" } : normalized;
    }

    private static bool MatchesFolderName(string filePath, IReadOnlyList<string> patterns)
    {
        var directoryPath = Path.GetDirectoryName(filePath);
        var folderName = string.IsNullOrEmpty(directoryPath)
            ? string.Empty
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));

        return patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, folderName, ignoreCase: true));
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
            or NotSupportedException
            or ArgumentException
            or PathTooLongException;
    }

    private sealed record FileCandidate(string FullPath, string RootPath, long Size);
}
