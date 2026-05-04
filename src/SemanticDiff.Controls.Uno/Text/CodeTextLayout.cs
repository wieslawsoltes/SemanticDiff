using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Controls.Uno;

internal readonly record struct VisibleCodeRow(
    int LineIndex,
    CodeFoldRegion? CollapsedRegion,
    CodeFoldRegion? StartRegion,
    ImmutableArray<CodeFoldRegion> ActiveRegions)
{
    public CodeFoldRegion? InnermostActiveRegion => ActiveRegions.IsDefaultOrEmpty ? null : ActiveRegions[^1];

    public int FoldDepth => ActiveRegions.IsDefaultOrEmpty ? 0 : ActiveRegions.Length;
}

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
        var regionsByStart = CreateRegionsByStart(lines.Count, foldRegions);
        var orderedRegions = regionsByStart.Values.ToArray();
        Array.Sort(orderedRegions, static (left, right) =>
        {
            var startComparison = left.StartLineIndex.CompareTo(right.StartLineIndex);
            return startComparison != 0 ? startComparison : right.EndLineIndex.CompareTo(left.EndLineIndex);
        });

        var activeRegions = new List<CodeFoldRegion>();
        var nextRegionIndex = 0;
        var rows = ImmutableArray.CreateBuilder<VisibleCodeRow>(lines.Count);
        var activeVersion = 0;
        var activeSnapshotVersion = -1;
        var activeRegionSnapshot = ImmutableArray<CodeFoldRegion>.Empty;
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            if (RemoveExpiredRegions(activeRegions, lineIndex))
            {
                activeVersion++;
            }

            while (nextRegionIndex < orderedRegions.Length && orderedRegions[nextRegionIndex].StartLineIndex < lineIndex)
            {
                nextRegionIndex++;
            }

            while (nextRegionIndex < orderedRegions.Length && orderedRegions[nextRegionIndex].StartLineIndex == lineIndex)
            {
                var candidate = orderedRegions[nextRegionIndex];
                if (candidate.EndLineIndex >= lineIndex)
                {
                    activeRegions.Add(candidate);
                    activeVersion++;
                }

                nextRegionIndex++;
            }

            var startRegion = regionsByStart.GetValueOrDefault(lineIndex);
            if (activeSnapshotVersion != activeVersion)
            {
                activeRegionSnapshot = activeRegions.Count == 0
                    ? ImmutableArray<CodeFoldRegion>.Empty
                    : activeRegions.ToImmutableArray();
                activeSnapshotVersion = activeVersion;
            }

            if (regionsByStart.TryGetValue(lineIndex, out var region) && collapsedFoldStarts.Contains(lineIndex))
            {
                rows.Add(new VisibleCodeRow(lineIndex, region, startRegion, activeRegionSnapshot));
                lineIndex = Math.Min(lines.Count - 1, region.EndLineIndex);
            }
            else
            {
                rows.Add(new VisibleCodeRow(lineIndex, null, startRegion, activeRegionSnapshot));
            }
        }

        return new CodeTextLayoutSnapshot(rows.ToImmutable(), regionsByStart);
    }

    private static Dictionary<int, CodeFoldRegion> CreateRegionsByStart(int lineCount, IEnumerable<CodeFoldRegion> foldRegions)
    {
        var regionsByStart = new Dictionary<int, CodeFoldRegion>();
        foreach (var region in foldRegions)
        {
            if (region.StartLineIndex < 0 || region.EndLineIndex <= region.StartLineIndex || region.StartLineIndex >= lineCount)
            {
                continue;
            }

            if (!regionsByStart.TryGetValue(region.StartLineIndex, out var existing) ||
                region.EndLineIndex > existing.EndLineIndex)
            {
                regionsByStart[region.StartLineIndex] = region;
            }
        }

        return regionsByStart;
    }

    private static bool RemoveExpiredRegions(List<CodeFoldRegion> activeRegions, int lineIndex)
    {
        var removed = false;
        for (var index = activeRegions.Count - 1; index >= 0; index--)
        {
            if (activeRegions[index].EndLineIndex >= lineIndex)
            {
                continue;
            }

            activeRegions.RemoveAt(index);
            removed = true;
        }

        return removed;
    }

    public static int GetVisualColumn(string text, int column)
    {
        var count = Math.Clamp(column, 0, text.Length);
        if (count == 0)
        {
            return 0;
        }

        if (text.AsSpan(0, count).IndexOf('\t') < 0)
        {
            return count;
        }

        var visualColumn = 0;
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
            TokenSpan? token = null;
            var tokenStart = -1;
            for (var index = 0; index < line.Tokens.Length; index++)
            {
                var candidate = line.Tokens[index];
                if (column >= candidate.StartColumn &&
                    column < candidate.StartColumn + candidate.Length &&
                    candidate.StartColumn >= tokenStart)
                {
                    token = candidate;
                    tokenStart = candidate.StartColumn;
                }
            }

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
