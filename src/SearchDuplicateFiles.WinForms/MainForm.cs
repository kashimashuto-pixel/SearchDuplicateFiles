using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace SearchDuplicateFiles.WinForms;

public sealed class MainForm : Form
{
    private readonly TextBox _folderTextBox = new();
    private readonly TextBox _patternTextBox = new();
    private readonly NumericUpDown _minimumSizeBox = new();
    private readonly CheckBox _recursiveCheckBox = new();
    private readonly CheckBox _includeHiddenCheckBox = new();
    private readonly Button _browseButton = new();
    private readonly Button _scanButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _exportButton = new();
    private readonly Button _openFileButton = new();
    private readonly Button _openFolderButton = new();
    private readonly Button _recycleButton = new();
    private readonly Button _warningsButton = new();
    private readonly DataGridView _resultsGrid = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Label _statusLabel = new();
    private readonly ToolTip _toolTip = new();
    private readonly DuplicateScanner _scanner = new();
    private readonly BindingList<DuplicateFileRow> _rows = new();

    private CancellationTokenSource? _scanCancellation;
    private IReadOnlyList<string> _lastWarnings = Array.Empty<string>();
    private bool _isScanning;

    public MainForm()
    {
        Text = "Search Duplicate Files";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 620);
        Size = new Size(1180, 760);

        BuildLayout();
        WireEvents();
        UpdateScanState(false);
        UpdateActionButtons();
    }

    private void BuildLayout()
    {
        SuspendLayout();

        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12)
        };
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        mainLayout.Controls.Add(CreateFolderRow(), 0, 0);
        mainLayout.Controls.Add(CreateOptionsRow(), 0, 1);
        mainLayout.Controls.Add(CreateResultsGrid(), 0, 2);
        mainLayout.Controls.Add(CreateFooter(), 0, 3);

        Controls.Add(mainLayout);
        ResumeLayout();
    }

    private Control CreateFolderRow()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            AutoSize = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = "対象フォルダー",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 0)
        };

        _folderTextBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _folderTextBox.Margin = new Padding(0, 0, 8, 0);

        _browseButton.Text = "参照...";
        _browseButton.AutoSize = true;

        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(_folderTextBox, 1, 0);
        layout.Controls.Add(_browseButton, 2, 0);
        return layout;
    }

    private Control CreateOptionsRow()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 10, 0, 10)
        };

        _recursiveCheckBox.Text = "サブフォルダーも検索";
        _recursiveCheckBox.Checked = true;
        _recursiveCheckBox.AutoSize = true;
        _recursiveCheckBox.Margin = new Padding(0, 6, 16, 0);

        _includeHiddenCheckBox.Text = "隠し/システムを含める";
        _includeHiddenCheckBox.AutoSize = true;
        _includeHiddenCheckBox.Margin = new Padding(0, 6, 16, 0);

        var patternLabel = new Label
        {
            Text = "パターン",
            AutoSize = true,
            Margin = new Padding(0, 7, 6, 0)
        };

        _patternTextBox.Text = "*";
        _patternTextBox.Width = 140;
        _patternTextBox.Margin = new Padding(0, 3, 16, 0);
        _toolTip.SetToolTip(_patternTextBox, "*.jpg;*.png のように ; 区切りで指定できます。");

        var minimumSizeLabel = new Label
        {
            Text = "最小サイズ(KB)",
            AutoSize = true,
            Margin = new Padding(0, 7, 6, 0)
        };

        _minimumSizeBox.Minimum = 0;
        _minimumSizeBox.Maximum = 1_073_741_824;
        _minimumSizeBox.Width = 96;
        _minimumSizeBox.ThousandsSeparator = true;
        _minimumSizeBox.Margin = new Padding(0, 3, 16, 0);

        _scanButton.Text = "スキャン開始";
        _scanButton.AutoSize = true;
        _scanButton.Margin = new Padding(0, 0, 8, 0);

        _cancelButton.Text = "キャンセル";
        _cancelButton.AutoSize = true;

        panel.Controls.Add(_recursiveCheckBox);
        panel.Controls.Add(_includeHiddenCheckBox);
        panel.Controls.Add(patternLabel);
        panel.Controls.Add(_patternTextBox);
        panel.Controls.Add(minimumSizeLabel);
        panel.Controls.Add(_minimumSizeBox);
        panel.Controls.Add(_scanButton);
        panel.Controls.Add(_cancelButton);

        return panel;
    }

    private Control CreateResultsGrid()
    {
        _resultsGrid.Dock = DockStyle.Fill;
        _resultsGrid.AllowUserToAddRows = false;
        _resultsGrid.AllowUserToDeleteRows = false;
        _resultsGrid.AutoGenerateColumns = false;
        _resultsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _resultsGrid.BackgroundColor = SystemColors.Window;
        _resultsGrid.BorderStyle = BorderStyle.FixedSingle;
        _resultsGrid.MultiSelect = true;
        _resultsGrid.ReadOnly = true;
        _resultsGrid.RowHeadersVisible = false;
        _resultsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _resultsGrid.DataSource = _rows;

        _resultsGrid.Columns.Add(CreateTextColumn(nameof(DuplicateFileRow.GroupNumber), "グループ", 72, 8));
        _resultsGrid.Columns.Add(CreateTextColumn(nameof(DuplicateFileRow.FileName), "ファイル名", 180, 22));
        _resultsGrid.Columns.Add(CreateTextColumn(nameof(DuplicateFileRow.Folder), "フォルダー", 260, 34));
        _resultsGrid.Columns.Add(CreateTextColumn(nameof(DuplicateFileRow.SizeText), "サイズ", 100, 10));
        _resultsGrid.Columns.Add(CreateTextColumn(nameof(DuplicateFileRow.LastWriteLocalText), "更新日時", 140, 14));
        _resultsGrid.Columns.Add(CreateTextColumn(nameof(DuplicateFileRow.Sha256), "SHA-256", 220, 18));

        return _resultsGrid;
    }

    private static DataGridViewTextBoxColumn CreateTextColumn(string propertyName, string headerText, int width, int fillWeight)
    {
        return new DataGridViewTextBoxColumn
        {
            DataPropertyName = propertyName,
            HeaderText = headerText,
            MinimumWidth = width,
            FillWeight = fillWeight,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
    }

    private Control CreateFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0)
        };
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true
        };

        _exportButton.Text = "CSV保存";
        _openFileButton.Text = "ファイルを開く";
        _openFolderButton.Text = "フォルダーを開く";
        _recycleButton.Text = "選択ファイルをごみ箱へ";
        _warningsButton.Text = "警告を表示";

        foreach (var button in new[] { _exportButton, _openFileButton, _openFolderButton, _recycleButton, _warningsButton })
        {
            button.AutoSize = true;
            button.Margin = new Padding(0, 0, 8, 6);
            actions.Controls.Add(button);
        }

        var statusRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true
        };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Height = 18;
        _progressBar.Margin = new Padding(0, 2, 10, 0);

        _statusLabel.Text = "対象フォルダーを選択してください。";
        _statusLabel.AutoEllipsis = true;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

        statusRow.Controls.Add(_progressBar, 0, 0);
        statusRow.Controls.Add(_statusLabel, 1, 0);

        footer.Controls.Add(actions, 0, 0);
        footer.Controls.Add(statusRow, 0, 1);
        return footer;
    }

    private void WireEvents()
    {
        _browseButton.Click += BrowseButton_Click;
        _scanButton.Click += ScanButton_Click;
        _cancelButton.Click += CancelButton_Click;
        _exportButton.Click += ExportButton_Click;
        _openFileButton.Click += OpenFileButton_Click;
        _openFolderButton.Click += OpenFolderButton_Click;
        _recycleButton.Click += RecycleButton_Click;
        _warningsButton.Click += WarningsButton_Click;
        _resultsGrid.SelectionChanged += (_, _) => UpdateActionButtons();
    }

    private void BrowseButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "重複ファイルを検索するフォルダーを選択してください",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_folderTextBox.Text) ? _folderTextBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folderTextBox.Text = dialog.SelectedPath;
        }
    }

    private async void ScanButton_Click(object? sender, EventArgs e)
    {
        var rootPath = _folderTextBox.Text.Trim();
        if (!Directory.Exists(rootPath))
        {
            MessageBox.Show(this, "存在するフォルダーを指定してください。", "フォルダー確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var options = new ScanOptions(
            rootPath,
            _recursiveCheckBox.Checked,
            _includeHiddenCheckBox.Checked,
            Decimal.ToInt64(_minimumSizeBox.Value) * 1024L,
            ParsePatterns(_patternTextBox.Text));

        _scanCancellation = new CancellationTokenSource();
        _lastWarnings = Array.Empty<string>();
        _rows.Clear();
        UpdateScanState(true);

        try
        {
            var progress = new Progress<ScanProgress>(UpdateProgress);
            var result = await _scanner.ScanAsync(options, progress, _scanCancellation.Token);
            LoadResult(result);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "スキャンをキャンセルしました。";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "スキャン中にエラーが発生しました。";
            MessageBox.Show(this, ex.Message, "スキャンエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            UpdateScanState(false);
        }
    }

    private void CancelButton_Click(object? sender, EventArgs e)
    {
        _scanCancellation?.Cancel();
    }

    private void ExportButton_Click(object? sender, EventArgs e)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "csv",
            FileName = $"duplicate-files-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
            Title = "重複ファイル一覧を保存"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        WriteCsvLine(writer, "Group", "FileName", "Folder", "SizeBytes", "LastWriteLocal", "SHA256", "FullPath");

        foreach (var row in _rows)
        {
            WriteCsvLine(
                writer,
                row.GroupNumber.ToString(),
                row.FileName,
                row.Folder,
                row.File.Size.ToString(),
                row.LastWriteLocalText,
                row.Sha256,
                row.File.FullPath);
        }

        _statusLabel.Text = $"CSVを保存しました: {dialog.FileName}";
    }

    private void OpenFileButton_Click(object? sender, EventArgs e)
    {
        var row = GetSelectedRows().FirstOrDefault();
        if (row is null)
        {
            return;
        }

        if (!File.Exists(row.File.FullPath))
        {
            MessageBox.Show(this, "ファイルが見つかりません。", "ファイル確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(row.File.FullPath) { UseShellExecute = true });
    }

    private void OpenFolderButton_Click(object? sender, EventArgs e)
    {
        var row = GetSelectedRows().FirstOrDefault();
        if (row is null)
        {
            return;
        }

        if (File.Exists(row.File.FullPath))
        {
            Process.Start("explorer.exe", $"/select,\"{row.File.FullPath}\"");
            return;
        }

        var folder = row.Folder;
        if (Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
    }

    private void RecycleButton_Click(object? sender, EventArgs e)
    {
        var selectedRows = GetSelectedRows().ToArray();
        if (selectedRows.Length == 0)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"選択中の {selectedRows.Length:N0} 件をごみ箱へ移動します。",
            "ごみ箱へ移動",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        var removedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var row in selectedRows)
        {
            try
            {
                if (File.Exists(row.File.FullPath))
                {
                    FileSystem.DeleteFile(row.File.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }

                removedPaths.Add(row.File.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                errors.Add($"{row.File.FullPath} ({ex.Message})");
            }
        }

        if (removedPaths.Count > 0)
        {
            var remainingRows = RebuildRowsAfterRemoval(removedPaths);
            _rows.Clear();
            foreach (var row in remainingRows)
            {
                _rows.Add(row);
            }

            _statusLabel.Text = $"{removedPaths.Count:N0} 件をごみ箱へ移動しました。";
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors.Take(20)), "移動できなかったファイル", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        UpdateActionButtons();
    }

    private void WarningsButton_Click(object? sender, EventArgs e)
    {
        if (_lastWarnings.Count == 0)
        {
            return;
        }

        var lines = _lastWarnings.Take(40).ToList();
        if (_lastWarnings.Count > lines.Count)
        {
            lines.Add($"...ほか {_lastWarnings.Count - lines.Count:N0} 件");
        }

        MessageBox.Show(this, string.Join(Environment.NewLine, lines), "スキャン警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void LoadResult(DuplicateScanResult result)
    {
        _lastWarnings = result.Warnings;
        _rows.Clear();

        foreach (var group in result.Groups)
        {
            foreach (var file in group.Files)
            {
                _rows.Add(new DuplicateFileRow(group.Number, file));
            }
        }

        _statusLabel.Text = CreateSummaryText(result);
        UpdateActionButtons();
    }

    private void UpdateProgress(ScanProgress progress)
    {
        if (progress.Stage == ScanStage.Hashing && progress.CandidateFiles > 0)
        {
            _progressBar.Style = ProgressBarStyle.Blocks;
            _progressBar.Maximum = Math.Max(1, progress.CandidateFiles);
            _progressBar.Value = Math.Min(_progressBar.Maximum, progress.FilesHashed);
        }
        else if (_isScanning)
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
        }

        _statusLabel.Text = progress.Stage switch
        {
            ScanStage.Enumerating => $"列挙中: {progress.FilesSeen:N0} 件 {ShortenPath(progress.CurrentPath)}",
            ScanStage.Hashing => $"内容確認中: {progress.FilesHashed:N0}/{progress.CandidateFiles:N0} 件 {ShortenPath(progress.CurrentPath)}",
            ScanStage.Finished => $"完了: {progress.DuplicateGroups:N0} グループ",
            _ => _statusLabel.Text
        };
    }

    private void UpdateScanState(bool isScanning)
    {
        _isScanning = isScanning;
        Cursor = isScanning ? Cursors.WaitCursor : Cursors.Default;

        _folderTextBox.Enabled = !isScanning;
        _browseButton.Enabled = !isScanning;
        _recursiveCheckBox.Enabled = !isScanning;
        _includeHiddenCheckBox.Enabled = !isScanning;
        _patternTextBox.Enabled = !isScanning;
        _minimumSizeBox.Enabled = !isScanning;
        _scanButton.Enabled = !isScanning;
        _cancelButton.Enabled = isScanning;

        if (isScanning)
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
        }
        else
        {
            _progressBar.Style = ProgressBarStyle.Blocks;
            _progressBar.Value = 0;
        }

        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        var hasRows = _rows.Count > 0;
        var hasSelection = _resultsGrid.SelectedRows.Count > 0;

        _exportButton.Enabled = !_isScanning && hasRows;
        _openFileButton.Enabled = !_isScanning && hasSelection;
        _openFolderButton.Enabled = !_isScanning && hasSelection;
        _recycleButton.Enabled = !_isScanning && hasSelection;
        _warningsButton.Enabled = !_isScanning && _lastWarnings.Count > 0;
    }

    private IEnumerable<DuplicateFileRow> GetSelectedRows()
    {
        return _resultsGrid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(row => row.DataBoundItem as DuplicateFileRow)
            .Where(row => row is not null)
            .Cast<DuplicateFileRow>()
            .OrderBy(row => row.GroupNumber)
            .ThenBy(row => row.File.FullPath, StringComparer.OrdinalIgnoreCase);
    }

    private List<DuplicateFileRow> RebuildRowsAfterRemoval(HashSet<string> removedPaths)
    {
        return _rows
            .Where(row => !removedPaths.Contains(row.File.FullPath))
            .GroupBy(row => (row.File.Size, row.File.Sha256))
            .Where(group => group.Count() > 1)
            .OrderByDescending(group => (group.Count() - 1) * group.Key.Size)
            .ThenByDescending(group => group.Key.Size)
            .SelectMany((group, index) => group
                .OrderBy(row => row.File.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(row => new DuplicateFileRow(index + 1, row.File)))
            .ToList();
    }

    private static IReadOnlyList<string> ParsePatterns(string input)
    {
        var patterns = input
            .Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .ToArray();

        return patterns.Length == 0 ? new[] { "*" } : patterns;
    }

    private static string CreateSummaryText(DuplicateScanResult result)
    {
        var warningText = result.Warnings.Count == 0 ? string.Empty : $" / 警告 {result.Warnings.Count:N0} 件";

        if (result.Groups.Count == 0)
        {
            return $"完了: 重複は見つかりませんでした。確認 {result.TotalFilesSeen:N0} 件 / ハッシュ {result.FilesHashed:N0} 件 / {result.Elapsed:mm\\:ss}{warningText}";
        }

        return $"完了: {result.Groups.Count:N0} グループ / 重複候補 {result.DuplicateFileCount:N0} 件 / 削減可能 {FormatSize(result.ReclaimableBytes)} / {result.Elapsed:mm\\:ss}{warningText}";
    }

    private static string ShortenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        const int maxLength = 110;
        return path.Length <= maxLength ? path : "..." + path[^maxLength..];
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        var size = (double)bytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes:N0} {units[unitIndex]}" : $"{size:N2} {units[unitIndex]}";
    }

    private static void WriteCsvLine(TextWriter writer, params string[] values)
    {
        writer.WriteLine(string.Join(",", values.Select(EscapeCsv)));
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\r') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    private sealed class DuplicateFileRow
    {
        public DuplicateFileRow(int groupNumber, DuplicateFile file)
        {
            GroupNumber = groupNumber;
            File = file;
        }

        public int GroupNumber { get; }

        public DuplicateFile File { get; }

        public string FileName => Path.GetFileName(File.FullPath);

        public string Folder => Path.GetDirectoryName(File.FullPath) ?? string.Empty;

        public string SizeText => FormatSize(File.Size);

        public string LastWriteLocalText => File.LastWriteTimeUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");

        public string Sha256 => File.Sha256;
    }
}
