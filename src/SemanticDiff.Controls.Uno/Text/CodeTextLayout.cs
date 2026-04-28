using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Controls.Uno;

internal readonly record struct VisibleCodeRow(int LineIndex, CodeFoldRegion? CollapsedRegion);

internal readonly record struct CodeTextPosition(int RowIndex, int Column);

internal sealed record CodeTextLayoutSnapshot(
    ImmutableArray<VisibleCodeRow> VisibleRows,
    Dictionary<int, CodeFoldRegion> FoldRegionsByStart);

internal static class CodeTextLayout
{
    public const int TabSize = 4;

    public static CodeTextLayoutSnapshot BuildVisibleRows(
        IReadOnlyList<DiffLine> lines,
        IEnumerable<CodeFoldRegion> foldRegions,
        ISet<int> collapsedFoldStarts)
    {
        var regionsByStart = foldRegions
            .Where(region => region.StartLineIndex >= 0 && region.EndLineIndex > region.StartLineIndex && region.StartLineIndex < lines.Count)
            .GroupBy(region => region.StartLineIndex)
            .Select(group => group.OrderByDescending(region => region.EndLineIndex).First())
            .ToDictionary(region => region.StartLineIndex);

        var rows = ImmutableArray.CreateBuilder<VisibleCodeRow>(lines.Count);
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            if (regionsByStart.TryGetValue(lineIndex, out var region) && collapsedFoldStarts.Contains(lineIndex))
            {
                rows.Add(new VisibleCodeRow(lineIndex, region));
                lineIndex = Math.Min(lines.Count - 1, region.EndLineIndex);
            }
            else
            {
                rows.Add(new VisibleCodeRow(lineIndex, null));
            }
        }

        return new CodeTextLayoutSnapshot(rows.ToImmutable(), regionsByStart);
    }

    public static int GetVisualColumn(string text, int column)
    {
        var visualColumn = 0;
        var count = Math.Clamp(column, 0, text.Length);
        for (var index = 0; index < count; index++)
        {
            visualColumn += text[index] == '\t' ? TabSize : 1;
        }

        return visualColumn;
    }

    public static int GetSourceColumnFromVisualOffset(string text, float visualOffset, float charWidth)
    {
        if (string.IsNullOrEmpty(text) || visualOffset <= 0)
        {
            return 0;
        }

        var targetVisualColumn = visualOffset / Math.Max(1, charWidth);
        var visualColumn = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var nextVisualColumn = visualColumn + (text[index] == '\t' ? TabSize : 1);
            if (targetVisualColumn < nextVisualColumn)
            {
                var midpoint = visualColumn + (nextVisualColumn - visualColumn) / 2.0;
                return targetVisualColumn < midpoint ? index : index + 1;
            }

            visualColumn = nextVisualColumn;
        }

        return text.Length;
    }

    public static string GetSymbolTextAtColumn(DiffLine line, int column)
    {
        var text = line.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (!line.Tokens.IsDefaultOrEmpty)
        {
            var token = line.Tokens
                .OrderBy(token => token.StartColumn)
                .LastOrDefault(token => column >= token.StartColumn && column < token.StartColumn + token.Length);
            if (token is { Length: > 0 })
            {
                var start = Math.Clamp(token.StartColumn, 0, text.Length);
                var end = Math.Clamp(token.StartColumn + token.Length, start, text.Length);
                var tokenText = TrimSymbolText(text[start..end]);
                if (!string.IsNullOrWhiteSpace(tokenText))
                {
                    return tokenText;
                }
            }
        }

        var boundedColumn = Math.Clamp(column, 0, text.Length);
        var startIndex = boundedColumn;
        while (startIndex > 0 && IsSymbolTextCharacter(text[startIndex - 1]))
        {
            startIndex--;
        }

        var endIndex = boundedColumn;
        while (endIndex < text.Length && IsSymbolTextCharacter(text[endIndex]))
        {
            endIndex++;
        }

        return startIndex >= endIndex ? string.Empty : TrimSymbolText(text[startIndex..endIndex]);
    }

    private static bool IsSymbolTextCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '.' or ':' or '<' or '>';

    private static string TrimSymbolText(string value) =>
        value.Trim().Trim('.', ':', ';', ',', '(', ')', '[', ']', '{', '}', '<', '>', '"', '\'', '/', '\\');
}
