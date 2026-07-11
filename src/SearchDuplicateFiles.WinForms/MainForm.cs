using System.ComponentModel;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace SearchDuplicateFiles.WinForms;

public sealed class MainForm : Form
{
    private const string AppTitle = "Search Duplicate Files";

    private static readonly Color[] GroupBackColors =
    {
        Color.FromArgb(255, 249, 219),
        Color.FromArgb(226, 244, 255),
        Color.FromArgb(226, 247, 232),
        Color.FromArgb(255, 232, 238),
        Color.FromArgb(239, 234, 255),
        Color.FromArgb(224, 247, 244),
        Color.FromArgb(255, 238, 218),
        Color.FromArgb(239, 244, 248)
    };

    private readonly ListBox _folderListBox = new();
    private readonly TextBox _fileNamePatternTextBox = new();
    private readonly TextBox _folderNamePatternTextBox = new();
    private readonly NumericUpDown _minimumSizeBox = new();
    private readonly CheckBox _recursiveCheckBox = new();
    private readonly CheckBox _includeHiddenCheckBox = new();
    private readonly CheckBox _onlyAcrossFoldersCheckBox = new();
    private readonly Button _addFolderButton = new();
    private readonly Button _removeFolderButton = new();
    private readonly Button _clearFoldersButton = new();
    private readonly Button _scanButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _applyFilterButton = new();
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
    private bool _lastOnlyAcrossFolders;
    private int _lastScannedFolderCount;
    private DuplicateScanResult? _lastScanResult;
    private string? _sortPropertyName;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    public MainForm()
    {
        Text = AppTitle;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 680);
        Size = new Size(1260, 800);

        BuildLayout();
        WireEvents();
        UpdateScanState(false);
        UpdateActionButtons();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        TaskbarProgress.Clear(this);
        base.OnFormClosed(e);
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
            RowCount = 1,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = "対象フォルダー",
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            Margin = new Padding(0, 6, 12, 0)
        };

        _folderListBox.Dock = DockStyle.Fill;
        _folderListBox.Height = 96;
        _folderListBox.HorizontalScrollbar = true;
        _folderListBox.IntegralHeight = false;
        _folderListBox.SelectionMode = SelectionMode.MultiExtended;
        _folderListBox.Margin = new Padding(0, 0, 10, 0);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0)
        };

        _addFolderButton.Text = "追加...";
        _removeFolderButton.Text = "削除";
        _clearFoldersButton.Text = "クリア";

        foreach (var button in new[] { _addFolderButton, _removeFolderButton, _clearFoldersButton })
        {
            button.Width = 90;
            button.Margin = new Padding(0, 0, 0, 6);
            buttons.Controls.Add(button);
        }

        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(_folderListBox, 1, 0);
        layout.Controls.Add(buttons, 2, 0);
        return layout;
    }

    private Control CreateOptionsRow()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 10)
        };

        _recursiveCheckBox.Text = "サブフォルダーも検索";
        _recursiveCheckBox.Checked = true;
        _recursiveCheckBox.AutoSize = true;
        _recursiveCheckBox.Margin = new Padding(0, 6, 16, 0);

        _includeHiddenCheckBox.Text = "隠し/システムを含める";
        _includeHiddenCheckBox.AutoSize = true;
        _includeHiddenCheckBox.Margin = new Padding(0, 6, 16, 0);

        _onlyAcrossFoldersCheckBox.Text = "対象フォルダー間の重複だけ表示";
        _onlyAcrossFoldersCheckBox.AutoSize = true;
        _onlyAcrossFoldersCheckBox.Margin = new Padding(0, 6, 16, 0);
        _toolTip.SetToolTip(_onlyAcrossFoldersCheckBox, "2つ以上の対象フォルダーにまたがる重複だけを表示します。");

        var fileNamePatternLabel = new Label
        {
            Text = "ファイル名",
            AutoSize = true,
            Margin = new Padding(0, 7, 6, 0)
        };

        _fileNamePatternTextBox.Text = "*";
        _fileNamePatternTextBox.Width = 130;
        _fileNamePatternTextBox.Margin = new Padding(0, 3, 16, 0);
        _toolTip.SetToolTip(_fileNamePatternTextBox, "文字だけなら部分一致、*.jpg のように * と ? も使用できます。複数条件は ; 区切りです。");

        var folderNamePatternLabel = new Label
        {
            Text = "フォルダー名",
            AutoSize = true,
            Margin = new Padding(0, 7, 6, 0)
        };

        _folderNamePatternTextBox.Text = "*";
        _folderNamePatternTextBox.Width = 130;
        _folderNamePatternTextBox.Margin = new Padding(0, 3, 16, 0);
        _toolTip.SetToolTip(_folderNamePatternTextBox, "ファイルの親フォルダー名を部分一致またはワイルドカードで指定できます。複数条件は ; 区切りです。");

        _applyFilterButton.Text = "フィルター適用";
        _applyFilterButton.AutoSize = true;
        _applyFilterButton.Margin = new Padding(0, 0, 16, 0);

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
        panel.Controls.Add(_onlyAcrossFoldersCheckBox);
        panel.Controls.Add(fileNamePatternLabel);
        panel.Controls.Add(_fileNamePatternTextBox);
        panel.Controls.Add(folderNamePatternLabel);
        panel.Controls.Add(_folderNamePatternTextBox);
        panel.Controls.Add(_applyFilterButton);
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

        _resultsGrid.Columns.Add(CreateTextColumn(nameof(DuplicateFileRow.GroupNumber), "グループ", 72, 7));
        _resultsGrid.Columns.Add(CreateTextColumn(nameof(DuplicateFileRow.TargetFolder), "対象", 120, 12));
        _resultsGrid.Columns.Add(CreateTextColumn(nameof(DuplicateFileRow.FileName), "ファイル名", 180, 20));
        _resultsGrid.Columns.Add(CreateTextColumn(nameof(DuplicateFileRow.Folder), "フォルダー", 260, 32));
        _resultsGrid.Columns.Add(CreateTextColumn(nameof(DuplicateFileRow.SizeText), "サイズ", 100, 9));
        _resultsGrid.Columns.Add(CreateTextColumn(nameof(DuplicateFileRow.LastWriteLocalText), "更新日時", 140, 12));
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
            SortMode = DataGridViewColumnSortMode.Programmatic
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
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Height = 18;
        _progressBar.Margin = new Padding(0, 2, 10, 0);

        _statusLabel.Text = "対象フォルダーを追加してください。";
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
        _addFolderButton.Click += AddFolderButton_Click;
        _removeFolderButton.Click += RemoveFolderButton_Click;
        _clearFoldersButton.Click += ClearFoldersButton_Click;
        _scanButton.Click += ScanButton_Click;
        _cancelButton.Click += CancelButton_Click;
        _applyFilterButton.Click += (_, _) => ApplyFiltersToLastResult();
        _exportButton.Click += ExportButton_Click;
        _openFileButton.Click += OpenFileButton_Click;
        _openFolderButton.Click += OpenFolderButton_Click;
        _recycleButton.Click += RecycleButton_Click;
        _warningsButton.Click += WarningsButton_Click;
        _folderListBox.SelectedIndexChanged += (_, _) => UpdateActionButtons();
        _resultsGrid.SelectionChanged += (_, _) => UpdateActionButtons();
        _resultsGrid.RowPrePaint += ResultsGrid_RowPrePaint;
        _resultsGrid.ColumnHeaderMouseClick += ResultsGrid_ColumnHeaderMouseClick;
    }

    private void AddFolderButton_Click(object? sender, EventArgs e)
    {
        var selectedPath = _folderListBox.SelectedItem as string
            ?? _folderListBox.Items.Cast<string>().LastOrDefault()
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        using var dialog = new FolderBrowserDialog
        {
            Description = "重複ファイルを検索するフォルダーを選択してください",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(selectedPath) ? selectedPath : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AddFolder(dialog.SelectedPath);
        }
    }

    private void RemoveFolderButton_Click(object? sender, EventArgs e)
    {
        var selectedFolders = _folderListBox.SelectedItems.Cast<string>().ToArray();
        foreach (var folder in selectedFolders)
        {
            _folderListBox.Items.Remove(folder);
        }

        UpdateActionButtons();
    }

    private void ClearFoldersButton_Click(object? sender, EventArgs e)
    {
        _folderListBox.Items.Clear();
        UpdateActionButtons();
    }

    private async void ScanButton_Click(object? sender, EventArgs e)
    {
        var rootPaths = GetTargetFolders();
        if (rootPaths.Count == 0)
        {
            MessageBox.Show(this, "対象フォルダーを1つ以上追加してください。", "フォルダー確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var missingFolders = rootPaths.Where(path => !Directory.Exists(path)).ToArray();
        if (missingFolders.Length > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, missingFolders), "存在しないフォルダー", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _lastOnlyAcrossFolders = _onlyAcrossFoldersCheckBox.Checked;
        _lastScannedFolderCount = rootPaths.Count;

        var options = new ScanOptions(
            rootPaths,
            _recursiveCheckBox.Checked,
            _includeHiddenCheckBox.Checked,
            _lastOnlyAcrossFolders,
            Decimal.ToInt64(_minimumSizeBox.Value) * 1024L);

        _scanCancellation = new CancellationTokenSource();
        var cancellationToken = _scanCancellation.Token;
        _lastWarnings = Array.Empty<string>();
        _lastScanResult = null;
        _rows.Clear();
        _statusLabel.Text = "スキャン準備中...";
        Text = $"{AppTitle} - スキャン準備中";
        UpdateScanState(true);

        try
        {
            var progress = new Progress<ScanProgress>(UpdateProgress);
            var result = await Task.Run(() => _scanner.ScanAsync(options, progress, cancellationToken), cancellationToken);
            LoadResult(result);
        }
        catch (OperationCanceledException)
        {
            _progressBar.Value = 0;
            _statusLabel.Text = "スキャンをキャンセルしました。";
            Text = $"{AppTitle} - キャンセル";
            TaskbarProgress.SetPaused(this);
        }
        catch (Exception ex)
        {
            _progressBar.Value = 0;
            _statusLabel.Text = "スキャン中にエラーが発生しました。";
            Text = $"{AppTitle} - エラー";
            TaskbarProgress.SetError(this);
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
        WriteCsvLine(writer, "Group", "TargetFolder", "FileName", "Folder", "SizeBytes", "LastWriteLocal", "SHA256", "RootPath", "FullPath");

        foreach (var row in _rows)
        {
            WriteCsvLine(
                writer,
                row.GroupNumber.ToString(),
                row.TargetFolder,
                row.FileName,
                row.Folder,
                row.File.Size.ToString(),
                row.LastWriteLocalText,
                row.Sha256,
                row.File.RootPath,
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
            RemoveFilesFromLastResult(removedPaths);
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

    private void ResultsGrid_RowPrePaint(object? sender, DataGridViewRowPrePaintEventArgs e)
    {
        if (e.RowIndex < 0 || _resultsGrid.Rows[e.RowIndex].DataBoundItem is not DuplicateFileRow row)
        {
            return;
        }

        var style = _resultsGrid.Rows[e.RowIndex].DefaultCellStyle;
        style.BackColor = GetGroupBackColor(row.GroupNumber);
        style.SelectionBackColor = Color.FromArgb(54, 92, 130);
        style.SelectionForeColor = Color.White;
    }

    private void ResultsGrid_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex < 0 || _resultsGrid.Columns[e.ColumnIndex] is not DataGridViewColumn column)
        {
            return;
        }

        var propertyName = column.DataPropertyName;
        _sortDirection = string.Equals(_sortPropertyName, propertyName, StringComparison.Ordinal)
            ? (_sortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending)
            : ListSortDirection.Ascending;
        _sortPropertyName = propertyName;

        ApplyResultSort();

        foreach (DataGridViewColumn gridColumn in _resultsGrid.Columns)
        {
            gridColumn.HeaderCell.SortGlyphDirection = gridColumn == column
                ? (_sortDirection == ListSortDirection.Ascending ? SortOrder.Ascending : SortOrder.Descending)
                : SortOrder.None;
        }
    }

    private void ApplyResultSort()
    {
        IEnumerable<DuplicateFileRow> sortedRows = _sortPropertyName switch
        {
            nameof(DuplicateFileRow.GroupNumber) => _rows.OrderBy(row => row.GroupNumber),
            nameof(DuplicateFileRow.TargetFolder) => _rows.OrderBy(row => row.TargetFolder, StringComparer.CurrentCultureIgnoreCase),
            nameof(DuplicateFileRow.FileName) => _rows.OrderBy(row => row.FileName, StringComparer.CurrentCultureIgnoreCase),
            nameof(DuplicateFileRow.Folder) => _rows.OrderBy(row => row.Folder, StringComparer.CurrentCultureIgnoreCase),
            nameof(DuplicateFileRow.SizeText) => _rows.OrderBy(row => row.File.Size),
            nameof(DuplicateFileRow.LastWriteLocalText) => _rows.OrderBy(row => row.File.LastWriteTimeUtc),
            nameof(DuplicateFileRow.Sha256) => _rows.OrderBy(row => row.Sha256, StringComparer.OrdinalIgnoreCase),
            _ => _rows.OrderBy(row => row.GroupNumber).ThenBy(row => row.File.FullPath, StringComparer.OrdinalIgnoreCase)
        };

        var snapshot = (_sortDirection == ListSortDirection.Descending ? sortedRows.Reverse() : sortedRows).ToArray();
        _rows.RaiseListChangedEvents = false;
        try
        {
            _rows.Clear();
            foreach (var row in snapshot)
            {
                _rows.Add(row);
            }
        }
        finally
        {
            _rows.RaiseListChangedEvents = true;
            _rows.ResetBindings();
        }

        _resultsGrid.ClearSelection();
    }

    private void AddFolder(string folderPath)
    {
        var normalized = NormalizeFolderPath(folderPath);
        if (_folderListBox.Items.Cast<string>().Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _folderListBox.Items.Add(normalized);
        _folderListBox.SelectedItem = normalized;
        UpdateActionButtons();
    }

    private void LoadResult(DuplicateScanResult result)
    {
        _lastScanResult = result;
        _lastWarnings = result.Warnings;
        ApplyFiltersToLastResult();
        Text = $"{AppTitle} - 完了";
    }

    private void ApplyFiltersToLastResult()
    {
        if (_lastScanResult is null)
        {
            return;
        }

        var fileNamePatterns = ParsePatterns(_fileNamePatternTextBox.Text);
        var folderNamePatterns = ParsePatterns(_folderNamePatternTextBox.Text);
        var visibleGroups = _lastScanResult.Groups
            .Where(group => GroupMatchesPatterns(group, fileNamePatterns, folderNamePatterns))
            .ToArray();

        _rows.Clear();

        foreach (var group in visibleGroups)
        {
            foreach (var file in group.Files)
            {
                _rows.Add(new DuplicateFileRow(group.Number, file));
            }
        }

        if (_sortPropertyName is not null)
        {
            ApplyResultSort();
        }

        _resultsGrid.ClearSelection();
        _resultsGrid.Refresh();
        _statusLabel.Text = CreateSummaryText(_lastScanResult, visibleGroups);
        UpdateActionButtons();
    }

    private void UpdateProgress(ScanProgress progress)
    {
        if (progress.Stage == ScanStage.Hashing)
        {
            _progressBar.Style = ProgressBarStyle.Blocks;
            _progressBar.Maximum = Math.Max(1, progress.CandidateFiles);
            _progressBar.Value = Math.Min(_progressBar.Maximum, progress.FilesHashed);
            TaskbarProgress.SetNormal(this, progress.FilesHashed, progress.CandidateFiles);
        }
        else if (progress.Stage == ScanStage.Finished)
        {
            _progressBar.Style = ProgressBarStyle.Blocks;
            _progressBar.Maximum = Math.Max(1, progress.CandidateFiles);
            _progressBar.Value = _progressBar.Maximum;
            TaskbarProgress.SetNormal(this, _progressBar.Value, _progressBar.Maximum);
        }
        else if (_isScanning)
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
            TaskbarProgress.SetIndeterminate(this);
        }

        _statusLabel.Text = progress.Stage switch
        {
            ScanStage.Enumerating => $"列挙中: {progress.FilesSeen:N0} 件 {ShortenPath(progress.CurrentPath)}",
            ScanStage.Hashing => $"内容確認中: {progress.FilesHashed:N0}/{progress.CandidateFiles:N0} 件 {ShortenPath(progress.CurrentPath)}",
            ScanStage.Finished => $"完了: {progress.DuplicateGroups:N0} グループ",
            _ => _statusLabel.Text
        };

        Text = progress.Stage switch
        {
            ScanStage.Enumerating => $"{AppTitle} - 列挙中 ({progress.FilesSeen:N0} 件)",
            ScanStage.Hashing => $"{AppTitle} - 内容確認中 ({progress.FilesHashed:N0}/{progress.CandidateFiles:N0})",
            ScanStage.Finished => $"{AppTitle} - 完了",
            _ => Text
        };
    }

    private void UpdateScanState(bool isScanning)
    {
        _isScanning = isScanning;
        Cursor = isScanning ? Cursors.WaitCursor : Cursors.Default;

        _folderListBox.Enabled = !isScanning;
        _recursiveCheckBox.Enabled = !isScanning;
        _includeHiddenCheckBox.Enabled = !isScanning;
        _onlyAcrossFoldersCheckBox.Enabled = !isScanning;
        _fileNamePatternTextBox.Enabled = !isScanning;
        _folderNamePatternTextBox.Enabled = !isScanning;
        _applyFilterButton.Enabled = !isScanning && _lastScanResult is not null;
        _minimumSizeBox.Enabled = !isScanning;
        _cancelButton.Enabled = isScanning;

        if (isScanning)
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
            TaskbarProgress.SetIndeterminate(this);
        }
        else if (_progressBar.Style == ProgressBarStyle.Marquee)
        {
            _progressBar.Style = ProgressBarStyle.Blocks;
            _progressBar.Value = 0;
        }

        if (!isScanning)
        {
            TaskbarProgress.Clear(this);
        }

        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        var hasFolders = _folderListBox.Items.Count > 0;
        var canCompareFolders = _folderListBox.Items.Count > 1;
        var hasFolderSelection = _folderListBox.SelectedItems.Count > 0;
        var hasRows = _rows.Count > 0;
        var hasResultSelection = _resultsGrid.SelectedRows.Count > 0;

        if (!_isScanning && !canCompareFolders)
        {
            _onlyAcrossFoldersCheckBox.Checked = false;
        }

        _addFolderButton.Enabled = !_isScanning;
        _removeFolderButton.Enabled = !_isScanning && hasFolderSelection;
        _clearFoldersButton.Enabled = !_isScanning && hasFolders;
        _scanButton.Enabled = !_isScanning && hasFolders;
        _applyFilterButton.Enabled = !_isScanning && _lastScanResult is not null;
        _onlyAcrossFoldersCheckBox.Enabled = !_isScanning && canCompareFolders;
        _exportButton.Enabled = !_isScanning && hasRows;
        _openFileButton.Enabled = !_isScanning && hasResultSelection;
        _openFolderButton.Enabled = !_isScanning && hasResultSelection;
        _recycleButton.Enabled = !_isScanning && hasResultSelection;
        _warningsButton.Enabled = !_isScanning && _lastWarnings.Count > 0;
    }

    private IReadOnlyList<string> GetTargetFolders()
    {
        return _folderListBox.Items
            .Cast<string>()
            .Select(NormalizeFolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private void RemoveFilesFromLastResult(HashSet<string> removedPaths)
    {
        if (_lastScanResult is null)
        {
            return;
        }

        var groups = _lastScanResult.Groups
            .Select(group => group.Files.Where(file => !removedPaths.Contains(file.FullPath)).ToArray())
            .Where(files => IsVisibleDuplicateGroup(files, _lastOnlyAcrossFolders))
            .Select((files, index) => new DuplicateFileGroup(index + 1, files[0].Size, files[0].Sha256, files))
            .ToArray();

        _lastScanResult = _lastScanResult with { Groups = groups };
        ApplyFiltersToLastResult();
    }

    private string CreateSummaryText(DuplicateScanResult result, IReadOnlyList<DuplicateFileGroup> visibleGroups)
    {
        var warningText = result.Warnings.Count == 0 ? string.Empty : $" / 警告 {result.Warnings.Count:N0} 件";
        var comparisonText = _lastOnlyAcrossFolders ? " / フォルダー間のみ" : string.Empty;
        var targetText = $"対象 {_lastScannedFolderCount:N0} フォルダー{comparisonText}";
        var duplicateFileCount = visibleGroups.Sum(group => group.Files.Count);
        var reclaimableBytes = visibleGroups.Sum(group => (group.Files.Count - 1) * group.Size);
        var filterText = visibleGroups.Count == result.Groups.Count ? string.Empty : $" / 絞り込み {visibleGroups.Count:N0}/{result.Groups.Count:N0} グループ";

        if (visibleGroups.Count == 0)
        {
            return $"完了: 条件に一致する重複はありません。{targetText}{filterText} / 確認 {result.TotalFilesSeen:N0} 件 / ハッシュ {result.FilesHashed:N0} 件 / {result.Elapsed:mm\\:ss}{warningText}";
        }

        return $"完了: {visibleGroups.Count:N0} グループ / 重複候補 {duplicateFileCount:N0} 件 / 削減可能 {FormatSize(reclaimableBytes)}{filterText} / {targetText} / {result.Elapsed:mm\\:ss}{warningText}";
    }

    private static IReadOnlyList<string> ParsePatterns(string input)
    {
        var patterns = input
            .Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .ToArray();

        return patterns.Length == 0 ? new[] { "*" } : patterns;
    }

    private static bool GroupMatchesPatterns(
        DuplicateFileGroup group,
        IReadOnlyList<string> fileNamePatterns,
        IReadOnlyList<string> folderNamePatterns)
    {
        var matchesFileName = fileNamePatterns.Any(pattern => group.Files.Any(file => MatchesName(Path.GetFileName(file.FullPath), pattern)));
        var matchesFolderName = folderNamePatterns.Any(pattern => group.Files.Any(file =>
        {
            var directory = Path.GetDirectoryName(file.FullPath) ?? string.Empty;
            var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
            return MatchesName(folderName, pattern);
        }));

        return matchesFileName && matchesFolderName;
    }

    private static bool MatchesName(string name, string pattern)
    {
        return pattern.IndexOfAny(['*', '?']) >= 0
            ? FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)
            : name.Contains(pattern, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string NormalizeFolderPath(string folderPath)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath.Trim()));
    }

    private static bool IsVisibleDuplicateGroup(IEnumerable<DuplicateFile> files, bool onlyShowAcrossDifferentRootFolders)
    {
        var fileList = files.ToArray();
        if (fileList.Length <= 1)
        {
            return false;
        }

        return !onlyShowAcrossDifferentRootFolders
            || fileList.Select(file => file.RootPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
    }

    private static Color GetGroupBackColor(int groupNumber)
    {
        return GroupBackColors[(groupNumber - 1) % GroupBackColors.Length];
    }

    private static string GetFolderDisplayName(string folder)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(folder);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? folder : name;
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

        public string TargetFolder => GetFolderDisplayName(File.RootPath);

        public string FileName => Path.GetFileName(File.FullPath);

        public string Folder => Path.GetDirectoryName(File.FullPath) ?? string.Empty;

        public string SizeText => FormatSize(File.Size);

        public string LastWriteLocalText => File.LastWriteTimeUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");

        public string Sha256 => File.Sha256;
    }
}
