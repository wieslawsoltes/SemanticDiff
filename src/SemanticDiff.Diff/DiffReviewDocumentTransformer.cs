using System.Collections.Immutable;
using System.Text;
using SemanticDiff.Core;

namespace SemanticDiff.Diff;

public static class DiffReviewDocumentTransformer
{
    public static ImmutableArray<DiffDocumentSnapshot> Apply(ImmutableArray<DiffDocumentSnapshot> documents, DiffReviewMode reviewMode)
    {
        if (documents.IsDefaultOrEmpty)
        {
            return documents.IsDefault ? ImmutableArray<DiffDocumentSnapshot>.Empty : documents;
        }

        var builder = ImmutableArray.CreateBuilder<DiffDocumentSnapshot>(documents.Length);
        foreach (var document in documents)
        {
            builder.Add(Apply(document, reviewMode));
        }

        return builder.ToImmutable();
    }

    public static DiffDocumentSnapshot Apply(DiffDocumentSnapshot document, DiffReviewMode reviewMode)
    {
        if (document.Lines.IsDefaultOrEmpty)
        {
            return document;
        }

        var movedLineIndexes = FindMovedLineIndexes(document.Lines);
        var ignoredLineIndexes = reviewMode == DiffReviewMode.IgnoreWhitespace
            ? FindIgnoredLineIndexes(document.Lines)
            : ImmutableHashSet<int>.Empty;

        if (movedLineIndexes.Count == 0 && ignoredLineIndexes.Count == 0)
        {
            return document;
        }

        var lineBuilder = ImmutableArray.CreateBuilder<DiffLine>(document.Lines.Length);
        for (var lineIndex = 0; lineIndex < document.Lines.Length; lineIndex++)
        {
            var line = document.Lines[lineIndex];
            if (ignoredLineIndexes.Contains(lineIndex))
            {
                lineBuilder.Add(line with { Kind = DiffLineKind.Ignored });
            }
            else if (movedLineIndexes.Contains(lineIndex))
            {
                lineBuilder.Add(line with { Kind = DiffLineKind.Moved });
            }
            else
            {
                lineBuilder.Add(line);
            }
        }

        return document with { Lines = lineBuilder.ToImmutable() };
    }

    private static ImmutableHashSet<int> FindIgnoredLineIndexes(ImmutableArray<DiffLine> lines)
    {
        var ignored = ImmutableHashSet.CreateBuilder<int>();
        var run = new List<int>();
        var deleted = new List<int>();
        var added = new List<int>();

        for (var lineIndex = 0; lineIndex < lines.Length;)
        {
            if (!IsChangedLine(lines[lineIndex]))
            {
                lineIndex++;
                continue;
            }

            var runStart = lineIndex;
            run.Clear();
            deleted.Clear();
            added.Clear();
            while (lineIndex < lines.Length && IsChangedLine(lines[lineIndex]))
            {
                run.Add(lineIndex);
                if (lines[lineIndex].Kind == DiffLineKind.Deleted)
                {
                    deleted.Add(lineIndex);
                }
                else
                {
                    added.Add(lineIndex);
                }

                if (string.IsNullOrWhiteSpace(lines[lineIndex].Text))
                {
                    ignored.Add(lineIndex);
                }

                lineIndex++;
            }

            if (lineIndex == runStart || deleted.Count == 0 || added.Count == 0)
            {
                continue;
            }

            if (HasSameFormattingInsensitiveText(lines, deleted, added) || HasSameImportSet(lines, deleted, added))
            {
                foreach (var index in run)
                {
                    ignored.Add(index);
                }
            }
        }

        AddImportOrderOnlyHunkIndexes(lines, ignored);

        return ignored.ToImmutable();
    }

    private static void AddImportOrderOnlyHunkIndexes(ImmutableArray<DiffLine> lines, ISet<int> ignored)
    {
        var changedIndexes = new List<int>();
        var deleted = new List<int>();
        var added = new List<int>();

        for (var lineIndex = 0; lineIndex < lines.Length;)
        {
            if (!IsHunkHeader(lines[lineIndex]))
            {
                lineIndex++;
                continue;
            }

            var hunkStart = ++lineIndex;
            while (lineIndex < lines.Length && !IsHunkHeader(lines[lineIndex]))
            {
                lineIndex++;
            }

            changedIndexes.Clear();
            deleted.Clear();
            added.Clear();
            var isImportOnlyHunk = true;
            for (var index = hunkStart; index < lineIndex; index++)
            {
                if (!IsChangedLine(lines[index]))
                {
                    continue;
                }

                if (!IsImportLine(lines[index].Text))
                {
                    isImportOnlyHunk = false;
                    break;
                }

                changedIndexes.Add(index);
                if (lines[index].Kind == DiffLineKind.Deleted)
                {
                    deleted.Add(index);
                }
                else
                {
                    added.Add(index);
                }
            }

            if (!isImportOnlyHunk || changedIndexes.Count == 0)
            {
                continue;
            }

            if (deleted.Count > 0 && added.Count > 0 && HasSameImportSet(lines, deleted, added))
            {
                foreach (var index in changedIndexes)
                {
                    ignored.Add(index);
                }
            }
        }
    }

    private static ImmutableHashSet<int> FindMovedLineIndexes(ImmutableArray<DiffLine> lines)
    {
        var deletedCount = 0;
        var addedCount = 0;
        foreach (var line in lines)
        {
            if (line.Kind == DiffLineKind.Deleted)
            {
                deletedCount++;
            }
            else if (line.Kind == DiffLineKind.Added)
            {
                addedCount++;
            }
        }

        if (deletedCount == 0 || addedCount == 0)
        {
            return ImmutableHashSet<int>.Empty;
        }

        return deletedCount <= addedCount
            ? FindMovedLineIndexes(lines, DiffLineKind.Deleted, DiffLineKind.Added)
            : FindMovedLineIndexes(lines, DiffLineKind.Added, DiffLineKind.Deleted);
    }

    private static ImmutableHashSet<int> FindMovedLineIndexes(
        ImmutableArray<DiffLine> lines,
        DiffLineKind lookupKind,
        DiffLineKind probeKind)
    {
        var lookupByText = CreateChangedLineLookup(lines, lookupKind);
        if (lookupByText.Count == 0)
        {
            return ImmutableHashSet<int>.Empty;
        }

        var moved = ImmutableHashSet.CreateBuilder<int>();

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.Kind != probeKind || !TryNormalizeMovedLine(line.Text, out var normalizedText))
            {
                continue;
            }

            if (!lookupByText.TryGetValue(normalizedText, out var matchingIndexes))
            {
                continue;
            }

            foreach (var matchingIndex in matchingIndexes)
            {
                moved.Add(matchingIndex);
            }

            moved.Add(lineIndex);
        }

        return moved.ToImmutable();
    }

    private static Dictionary<string, List<int>> CreateChangedLineLookup(ImmutableArray<DiffLine> lines, DiffLineKind kind)
    {
        var lookup = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.Kind != kind)
            {
                continue;
            }

            if (!TryNormalizeMovedLine(line.Text, out var normalizedText))
            {
                continue;
            }

            if (!lookup.TryGetValue(normalizedText, out var indexes))
            {
                indexes = [];
                lookup[normalizedText] = indexes;
            }

            indexes.Add(lineIndex);
        }

        return lookup;
    }

    private static bool TryNormalizeMovedLine(string text, out string normalizedText)
    {
        normalizedText = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var normalizedLength = 0;
        var hasLetterOrDigit = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            normalizedLength++;
            hasLetterOrDigit |= char.IsLetterOrDigit(character);
        }

        if (normalizedLength < 4 || !hasLetterOrDigit)
        {
            return false;
        }

        normalizedText = string.Create(normalizedLength, text, static (destination, source) =>
        {
            var offset = 0;
            foreach (var character in source)
            {
                if (!char.IsWhiteSpace(character))
                {
                    destination[offset++] = character;
                }
            }
        });
        return true;
    }

    private static bool HasSameFormattingInsensitiveText(ImmutableArray<DiffLine> lines, IReadOnlyList<int> deleted, IReadOnlyList<int> added)
    {
        var deletedText = NormalizeCode(lines, deleted);
        if (deletedText.Length == 0)
        {
            return false;
        }

        return string.Equals(deletedText, NormalizeCode(lines, added), StringComparison.Ordinal);
    }

    private static bool HasSameImportSet(ImmutableArray<DiffLine> lines, IReadOnlyList<int> deleted, IReadOnlyList<int> added)
    {
        var imports = new Dictionary<string, int>(deleted.Count, StringComparer.Ordinal);
        foreach (var index in deleted)
        {
            if (!IsImportLine(lines[index].Text))
            {
                return false;
            }

            var import = NormalizeImportLine(lines[index].Text);
            imports.TryGetValue(import, out var count);
            imports[import] = count + 1;
        }

        foreach (var index in added)
        {
            if (!IsImportLine(lines[index].Text))
            {
                return false;
            }

            var import = NormalizeImportLine(lines[index].Text);
            if (!imports.TryGetValue(import, out var count))
            {
                return false;
            }

            if (count == 1)
            {
                imports.Remove(import);
            }
            else
            {
                imports[import] = count - 1;
            }
        }

        return imports.Count == 0;
    }

    private static bool IsChangedLine(DiffLine line) => line.Kind is DiffLineKind.Added or DiffLineKind.Deleted;

    private static bool IsHunkHeader(DiffLine line) => line.Kind == DiffLineKind.Metadata && line.Text.StartsWith("@@", StringComparison.Ordinal);

    private static bool IsImportLine(string text)
    {
        var trimmed = text.Trim();
        return trimmed.StartsWith("using ", StringComparison.Ordinal) ||
            trimmed.StartsWith("global using ", StringComparison.Ordinal) ||
            trimmed.StartsWith("xmlns", StringComparison.Ordinal);
    }

    private static string NormalizeImportLine(string text) => NormalizeCode(text.Trim().TrimEnd(';', ','));

    private static string NormalizeCode(ImmutableArray<DiffLine> lines, IReadOnlyList<int> indexes)
    {
        var normalizedLength = 0;
        foreach (var index in indexes)
        {
            foreach (var character in lines[index].Text)
            {
                if (!char.IsWhiteSpace(character))
                {
                    normalizedLength++;
                }
            }
        }

        if (normalizedLength == 0)
        {
            return string.Empty;
        }

        return string.Create(normalizedLength, (lines, indexes), static (destination, state) =>
        {
            var offset = 0;
            foreach (var index in state.indexes)
            {
                foreach (var character in state.lines[index].Text)
                {
                    if (!char.IsWhiteSpace(character))
                    {
                        destination[offset++] = character;
                    }
                }
            }
        });
    }

    private static string NormalizeCode(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
