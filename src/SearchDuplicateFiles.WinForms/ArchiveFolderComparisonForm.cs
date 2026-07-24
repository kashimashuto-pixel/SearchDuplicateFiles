using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SearchDuplicateFiles.WinForms;

public sealed class ArchiveFolderComparisonForm : Form
{
    private readonly TextBox _archivePathTextBox = new();
    private readonly TextBox _folderPathTextBox = new();
    private readonly Button _browseArchiveButton = new();
    private readonly Button _browseFolderButton = new();
    private readonly Button _compareButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _exportButton = new();
    private readonly Button _warningsButton = new();
    private readonly Button _openLocationButton = new();
    private readonly CheckBox _showMatchesCheckBox = new();
    private readonly CheckBox _ignoreTopLevelFolderCheckBox = new();
    private readonly DataGridView _resultsGrid = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Label _statusLabel = new();
    private readonly BindingList<ComparisonRow> _rows = new();
    private readonly ArchiveFolderComparer _comparer = new();

    private CancellationTokenSource? _comparisonCancellation;
    private ArchiveFolderComparisonResult? _lastResult;
    private bool _isComparing;

    public ArchiveFolderComparisonForm()
    {
        Text = "圧縮ファイルと展開済みフォルダーの比較";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 600);
        Size = new Size(1180, 760);

        BuildLayout();
        WireEvents();
        UpdateState();
    }

    private void BuildLayout()
    {
        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12)
        };
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainLayout.Controls.Add(CreateTargetPanel(), 0, 0);
        mainLayout.Controls.Add(CreateResultsGrid(), 0, 1);
        mainLayout.Controls.Add(CreateFooter(), 0, 2);
        Controls.Add(mainLayout);
    }

    private Control CreateTargetPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var archiveLabel = new Label
        {
            Text = "圧縮ファイル",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 7, 12, 0)
        };
        _archivePathTextBox.Dock = DockStyle.Fill;
        _archivePathTextBox.Margin = new Padding(0, 3, 8, 3);
        _browseArchiveButton.Text = "参照...";
        _browseArchiveButton.AutoSize = true;

        var folderLabel = new Label
        {
            Text = "展開済みフォルダー",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 7, 12, 0)
        };
        _folderPathTextBox.Dock = DockStyle.Fill;
        _folderPathTextBox.Margin = new Padding(0, 3, 8, 3);
        _browseFolderButton.Text = "参照...";
        _browseFolderButton.AutoSize = true;

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0)
        };
        _compareButton.Text = "比較開始";
        _compareButton.AutoSize = true;
        _cancelButton.Text = "キャンセル";
        _cancelButton.AutoSize = true;
        _ignoreTopLevelFolderCheckBox.Text = "圧縮内の先頭フォルダー1階層を無視";
        _ignoreTopLevelFolderCheckBox.AutoSize = true;
        _ignoreTopLevelFolderCheckBox.Margin = new Padding(16, 5, 0, 0);
        actions.Controls.Add(_compareButton);
        actions.Controls.Add(_cancelButton);
        actions.Controls.Add(_ignoreTopLevelFolderCheckBox);

        layout.Controls.Add(archiveLabel, 0, 0);
        layout.Controls.Add(_archivePathTextBox, 1, 0);
        layout.Controls.Add(_browseArchiveButton, 2, 0);
        layout.Controls.Add(folderLabel, 0, 1);
        layout.Controls.Add(_folderPathTextBox, 1, 1);
        layout.Controls.Add(_browseFolderButton, 2, 1);
        layout.Controls.Add(actions, 1, 2);
        return layout;
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
        _resultsGrid.MultiSelect = false;
        _resultsGrid.ReadOnly = true;
        _resultsGrid.RowHeadersVisible = false;
        _resultsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _resultsGrid.DataSource = _rows;

        _resultsGrid.Columns.Add(CreateTextColumn(nameof(ComparisonRow.StatusText), "状態", 120, 14));
        _resultsGrid.Columns.Add(CreateTextColumn(nameof(ComparisonRow.RelativePath), "相対パス", 340, 45));
        _resultsGrid.Columns.Add(CreateTextColumn(nameof(ComparisonRow.ArchiveSizeText), "圧縮側サイズ", 120, 14));
        _resultsGrid.Columns.Add(CreateTextColumn(nameof(ComparisonRow.FolderSizeText), "フォルダー側サイズ", 130, 15));
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

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0)
        };
        _showMatchesCheckBox.Text = "一致したファイルも表示";
        _showMatchesCheckBox.AutoSize = true;
        _showMatchesCheckBox.Margin = new Padding(0, 5, 16, 0);
        _exportButton.Text = "CSV保存";
        _exportButton.AutoSize = true;
        _warningsButton.Text = "警告を表示";
        _warningsButton.AutoSize = true;
        _openLocationButton.Text = "場所を開く";
        _openLocationButton.AutoSize = true;
        actions.Controls.Add(_showMatchesCheckBox);
        actions.Controls.Add(_exportButton);
        actions.Controls.Add(_warningsButton);
        actions.Controls.Add(_openLocationButton);

        var statusLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0)
        };
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Height = 18;
        _progressBar.Margin = new Padding(0, 2, 10, 0);
        _statusLabel.Text = "圧縮ファイルと、その展開先フォルダーを選択してください。";
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.AutoEllipsis = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusLayout.Controls.Add(_progressBar, 0, 0);
        statusLayout.Controls.Add(_statusLabel, 1, 0);

        footer.Controls.Add(actions, 0, 0);
        footer.Controls.Add(statusLayout, 0, 1);
        return footer;
    }

    private void WireEvents()
    {
        _browseArchiveButton.Click += BrowseArchiveButton_Click;
        _browseFolderButton.Click += BrowseFolderButton_Click;
        _compareButton.Click += CompareButton_Click;
        _cancelButton.Click += (_, _) => _comparisonCancellation?.Cancel();
        _showMatchesCheckBox.CheckedChanged += (_, _) => LoadVisibleRows();
        _exportButton.Click += ExportButton_Click;
        _warningsButton.Click += WarningsButton_Click;
        _openLocationButton.Click += OpenLocationButton_Click;
        _resultsGrid.SelectionChanged += (_, _) => UpdateState();
        _archivePathTextBox.TextChanged += (_, _) => UpdateState();
        _folderPathTextBox.TextChanged += (_, _) => UpdateState();
        FormClosing += ArchiveFolderComparisonForm_FormClosing;
    }

    private void ArchiveFolderComparisonForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_isComparing)
        {
            return;
        }

        _comparisonCancellation?.Cancel();
        e.Cancel = true;
        _statusLabel.Text = "比較をキャンセルしています...";
    }

    private void BrowseArchiveButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = DuplicateScanner.ArchiveFileDialogFilter,
            Title = "比較する圧縮ファイルを選択"
        };

        if (File.Exists(_archivePathTextBox.Text))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_archivePathTextBox.Text);
            dialog.FileName = Path.GetFileName(_archivePathTextBox.Text);
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _archivePathTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseFolderButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "圧縮ファイルを展開したフォルダーを選択してください",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_folderPathTextBox.Text)
                ? _folderPathTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folderPathTextBox.Text = dialog.SelectedPath;
        }
    }

    private async void CompareButton_Click(object? sender, EventArgs e)
    {
        var archivePath = _archivePathTextBox.Text.Trim();
        var folderPath = _folderPathTextBox.Text.Trim();
        if (!File.Exists(archivePath)
            || !DuplicateScanner.IsSupportedArchivePath(archivePath)
            || !Directory.Exists(folderPath))
        {
            MessageBox.Show(
                this,
                "対応する圧縮ファイルと、存在する展開済みフォルダーを選択してください。",
                "比較対象の確認",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        _comparisonCancellation = new CancellationTokenSource();
        var cancellationToken = _comparisonCancellation.Token;
        var ignoreTopLevelFolder = _ignoreTopLevelFolderCheckBox.Checked;
        _lastResult = null;
        _rows.Clear();
        _isComparing = true;
        _progressBar.Style = ProgressBarStyle.Marquee;
        _statusLabel.Text = "比較を準備中...";
        UpdateState();

        try
        {
            var progress = new Progress<ArchiveFolderComparisonProgress>(UpdateProgress);
            _lastResult = await Task.Run(
                () => _comparer.Compare(
                    archivePath,
                    folderPath,
                    progress,
                    cancellationToken,
                    ignoreTopLevelFolder),
                cancellationToken);
            LoadVisibleRows();
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "比較をキャンセルしました。";
            _progressBar.Value = 0;
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "比較中にエラーが発生しました。";
            MessageBox.Show(this, ex.Message, "比較エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _comparisonCancellation.Dispose();
            _comparisonCancellation = null;
            _isComparing = false;
            _progressBar.Style = ProgressBarStyle.Blocks;
            UpdateState();
        }
    }

    private void UpdateProgress(ArchiveFolderComparisonProgress progress)
    {
        _statusLabel.Text = $"{progress.Stage}: {progress.ItemsProcessed:N0} 件 {ShortenPath(progress.CurrentPath)}";
    }

    private void LoadVisibleRows()
    {
        _rows.Clear();
        if (_lastResult is null)
        {
            UpdateState();
            return;
        }

        var visibleItems = _showMatchesCheckBox.Checked
            ? _lastResult.Items
            : _lastResult.Items.Where(item => item.Status != ArchiveFolderComparisonStatus.Match);
        foreach (var item in visibleItems)
        {
            _rows.Add(new ComparisonRow(item));
        }

        _resultsGrid.ClearSelection();
        _statusLabel.Text = CreateSummary(_lastResult);
        _progressBar.Style = ProgressBarStyle.Blocks;
        _progressBar.Maximum = 1;
        _progressBar.Value = 1;
        UpdateState();
    }

    private void ExportButton_Click(object? sender, EventArgs e)
    {
        if (_lastResult is null)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "csv",
            FileName = $"archive-folder-comparison-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
            Title = "比較結果を保存"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        WriteCsvLine(writer, "Status", "RelativePath", "ArchiveSize", "FolderSize", "ArchiveSHA256", "FolderSHA256");
        foreach (var item in _lastResult.Items)
        {
            WriteCsvLine(
                writer,
                GetStatusText(item.Status),
                item.RelativePath,
                item.ArchiveSize?.ToString() ?? string.Empty,
                item.FolderSize?.ToString() ?? string.Empty,
                item.ArchiveSha256 ?? string.Empty,
                item.FolderSha256 ?? string.Empty);
        }

        _statusLabel.Text = $"CSVを保存しました: {dialog.FileName}";
    }

    private void WarningsButton_Click(object? sender, EventArgs e)
    {
        if (_lastResult is null || _lastResult.Warnings.Count == 0)
        {
            return;
        }

        var warnings = _lastResult.Warnings.Take(40).ToList();
        if (_lastResult.Warnings.Count > warnings.Count)
        {
            warnings.Add($"...ほか {_lastResult.Warnings.Count - warnings.Count:N0} 件");
        }

        MessageBox.Show(this, string.Join(Environment.NewLine, warnings), "比較時の警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void OpenLocationButton_Click(object? sender, EventArgs e)
    {
        if (_lastResult is null
            || _resultsGrid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.DataBoundItem is not ComparisonRow row)
        {
            return;
        }

        if (row.Item.FolderSize is not null)
        {
            var folderFilePath = Path.Combine(_lastResult.FolderPath, row.Item.RelativePath);
            if (File.Exists(folderFilePath))
            {
                Process.Start("explorer.exe", $"/select,\"{folderFilePath}\"");
                return;
            }
        }

        if (File.Exists(_lastResult.ArchivePath))
        {
            Process.Start("explorer.exe", $"/select,\"{_lastResult.ArchivePath}\"");
        }
    }

    private void UpdateState()
    {
        var canCompare = File.Exists(_archivePathTextBox.Text.Trim())
            && DuplicateScanner.IsSupportedArchivePath(_archivePathTextBox.Text.Trim())
            && Directory.Exists(_folderPathTextBox.Text.Trim());

        _archivePathTextBox.Enabled = !_isComparing;
        _folderPathTextBox.Enabled = !_isComparing;
        _browseArchiveButton.Enabled = !_isComparing;
        _browseFolderButton.Enabled = !_isComparing;
        _compareButton.Enabled = !_isComparing && canCompare;
        _cancelButton.Enabled = _isComparing;
        _ignoreTopLevelFolderCheckBox.Enabled = !_isComparing;
        _showMatchesCheckBox.Enabled = !_isComparing && _lastResult is not null;
        _exportButton.Enabled = !_isComparing && _lastResult is not null;
        _warningsButton.Enabled = !_isComparing && _lastResult?.Warnings.Count > 0;
        _openLocationButton.Enabled = !_isComparing && _resultsGrid.SelectedRows.Count > 0;
    }

    private static string CreateSummary(ArchiveFolderComparisonResult result)
    {
        var warningText = result.Warnings.Count == 0 ? string.Empty : $" / 警告 {result.Warnings.Count:N0} 件";
        var pathModeText = result.IgnoredArchiveTopLevelFolder ? " / 先頭階層を除外" : string.Empty;
        return result.IsExactMatch
            ? $"完全一致: {result.MatchCount:N0} ファイル / {result.Elapsed:mm\\:ss}{pathModeText}"
            : $"不一致 {result.DifferenceCount:N0} 件 / 一致 {result.MatchCount:N0} 件 / {result.Elapsed:mm\\:ss}{pathModeText}{warningText}";
    }

    private static string GetStatusText(ArchiveFolderComparisonStatus status)
    {
        return status switch
        {
            ArchiveFolderComparisonStatus.Match => "一致",
            ArchiveFolderComparisonStatus.ArchiveOnly => "圧縮側のみ",
            ArchiveFolderComparisonStatus.FolderOnly => "フォルダー側のみ",
            ArchiveFolderComparisonStatus.SizeMismatch => "サイズ違い",
            ArchiveFolderComparisonStatus.ContentMismatch => "内容違い",
            ArchiveFolderComparisonStatus.Unreadable => "読取不可",
            ArchiveFolderComparisonStatus.DuplicateArchivePath => "圧縮内パス重複",
            ArchiveFolderComparisonStatus.UnsupportedEntry => "比較対象外",
            _ => status.ToString()
        };
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes is null)
        {
            return string.Empty;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes.Value;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes:N0} {units[unitIndex]}" : $"{value:N2} {units[unitIndex]}";
    }

    private static string ShortenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length <= 100)
        {
            return path ?? string.Empty;
        }

        return "..." + path[^100..];
    }

    private static void WriteCsvLine(TextWriter writer, params string[] values)
    {
        writer.WriteLine(string.Join(",", values.Select(EscapeCsv)));
    }

    private static string EscapeCsv(string value)
    {
        return value.IndexOfAny(['"', ',', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private sealed class ComparisonRow
    {
        public ComparisonRow(ArchiveFolderComparisonItem item)
        {
            Item = item;
        }

        public ArchiveFolderComparisonItem Item { get; }

        public string StatusText => GetStatusText(Item.Status);

        public string RelativePath => Item.RelativePath;

        public string ArchiveSizeText => FormatSize(Item.ArchiveSize);

        public string FolderSizeText => FormatSize(Item.FolderSize);
    }
}
