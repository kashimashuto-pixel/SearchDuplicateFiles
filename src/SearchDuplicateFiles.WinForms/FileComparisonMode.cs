namespace SearchDuplicateFiles.WinForms;

public enum FileComparisonMode
{
    Content,
    FileNameAndSize,
    FileName
}

internal static class FileComparisonModeExtensions
{
    public static string ToDisplayText(this FileComparisonMode mode)
    {
        return mode switch
        {
            FileComparisonMode.Content => "内容（SHA-256）",
            FileComparisonMode.FileNameAndSize => "ファイル名＋サイズ",
            FileComparisonMode.FileName => "ファイル名のみ",
            _ => mode.ToString()
        };
    }
}
