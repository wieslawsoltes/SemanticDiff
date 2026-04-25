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

        for (var lineIndex = 0; lineIndex < lines.Length;)
        {
            if (!IsChangedLine(lines[lineIndex]))
            {
                lineIndex++;
                continue;
            }

            var runStart = lineIndex;
            while (lineIndex < lines.Length && IsChangedLine(lines[lineIndex]))
            {
                if (string.IsNullOrWhiteSpace(lines[lineIndex].Text))
                {
                    ignored.Add(lineIndex);
                }

                lineIndex++;
            }

            var run = Enumerable.Range(runStart, lineIndex - runStart).ToArray();
            var deleted = run.Where(index => lines[index].Kind == DiffLineKind.Deleted).ToArray();
            var added = run.Where(index => lines[index].Kind == DiffLineKind.Added).ToArray();
            if (deleted.Length == 0 || added.Length == 0)
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

            var changedIndexes = Enumerable.Range(hunkStart, lineIndex - hunkStart)
                .Where(index => IsChangedLine(lines[index]))
                .ToArray();
            if (changedIndexes.Length == 0 || changedIndexes.Any(index => !IsImportLine(lines[index].Text)))
            {
                continue;
            }

            var deleted = changedIndexes.Where(index => lines[index].Kind == DiffLineKind.Deleted).ToArray();
            var added = changedIndexes.Where(index => lines[index].Kind == DiffLineKind.Added).ToArray();
            if (deleted.Length > 0 && added.Length > 0 && HasSameImportSet(lines, deleted, added))
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
        var deletedByText = CreateChangedLineLookup(lines, DiffLineKind.Deleted);
        var addedByText = CreateChangedLineLookup(lines, DiffLineKind.Added);
        var moved = ImmutableHashSet.CreateBuilder<int>();

        foreach (var (normalizedText, deletedIndexes) in deletedByText)
        {
            if (!addedByText.TryGetValue(normalizedText, out var addedIndexes))
            {
                continue;
            }

            foreach (var index in deletedIndexes)
            {
                moved.Add(index);
            }

            foreach (var index in addedIndexes)
            {
                moved.Add(index);
            }
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

            var normalizedText = NormalizeCode(line.Text);
            if (normalizedText.Length < 4 || !normalizedText.Any(char.IsLetterOrDigit))
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

    private static bool HasSameFormattingInsensitiveText(ImmutableArray<DiffLine> lines, int[] deleted, int[] added)
    {
        var deletedText = NormalizeCode(deleted.Select(index => lines[index].Text));
        if (deletedText.Length == 0)
        {
            return false;
        }

        return string.Equals(deletedText, NormalizeCode(added.Select(index => lines[index].Text)), StringComparison.Ordinal);
    }

    private static bool HasSameImportSet(ImmutableArray<DiffLine> lines, int[] deleted, int[] added)
    {
        if (!deleted.All(index => IsImportLine(lines[index].Text)) || !added.All(index => IsImportLine(lines[index].Text)))
        {
            return false;
        }

        var deletedImports = deleted.Select(index => NormalizeImportLine(lines[index].Text)).OrderBy(import => import, StringComparer.Ordinal).ToArray();
        var addedImports = added.Select(index => NormalizeImportLine(lines[index].Text)).OrderBy(import => import, StringComparer.Ordinal).ToArray();
        return deletedImports.SequenceEqual(addedImports, StringComparer.Ordinal);
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

    private static string NormalizeCode(IEnumerable<string> lines) => NormalizeCode(string.Concat(lines));

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