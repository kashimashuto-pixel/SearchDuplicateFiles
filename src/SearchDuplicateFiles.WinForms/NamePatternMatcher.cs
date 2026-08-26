using System.IO.Enumeration;

namespace SearchDuplicateFiles.WinForms;

internal static class NamePatternMatcher
{
    public static IReadOnlyList<string> ParsePatterns(string input)
    {
        return input
            .Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .ToArray();
    }

    public static bool IsMatch(string name, IReadOnlyList<string>? patterns)
    {
        return patterns is { Count: > 0 }
            && patterns.Any(pattern => MatchesName(name, pattern));
    }

    public static bool MatchesName(string name, string pattern)
    {
        return pattern.IndexOfAny(['*', '?']) >= 0
            ? FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)
            : name.Contains(pattern, StringComparison.CurrentCultureIgnoreCase);
    }
}
