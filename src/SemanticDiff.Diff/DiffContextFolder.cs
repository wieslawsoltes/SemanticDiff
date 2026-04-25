using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Diff;

public static class DiffContextFolder
{
    public const int DefaultVisibleContextLines = 3;
    public const int DefaultMinimumFoldLineCount = 10;

    public static ImmutableArray<DiffDocumentSnapshot> Apply(
        ImmutableArray<DiffDocumentSnapshot> documents,
        int visibleContextLines = DefaultVisibleContextLines,
        int minimumFoldLineCount = DefaultMinimumFoldLineCount)
    {
        if (documents.IsDefaultOrEmpty)
        {
            return documents.IsDefault ? ImmutableArray<DiffDocumentSnapshot>.Empty : documents;
        }

        var builder = ImmutableArray.CreateBuilder<DiffDocumentSnapshot>(documents.Length);
        foreach (var document in documents)
        {
            builder.Add(Apply(document, visibleContextLines, minimumFoldLineCount));
        }

        return builder.ToImmutable();
    }

    public static DiffDocumentSnapshot Apply(
        DiffDocumentSnapshot document,
        int visibleContextLines = DefaultVisibleContextLines,
        int minimumFoldLineCount = DefaultMinimumFoldLineCount)
    {
        if (document.Lines.IsDefaultOrEmpty || !document.Lines.Any(IsReviewRelevantLine))
        {
            return document;
        }

        visibleContextLines = Math.Max(0, visibleContextLines);
        minimumFoldLineCount = Math.Max(1, minimumFoldLineCount);
        var foldedLines = ImmutableArray.CreateBuilder<DiffLine>(document.Lines.Length);

        for (var lineIndex = 0; lineIndex < document.Lines.Length;)
        {
            if (document.Lines[lineIndex].Kind != DiffLineKind.Context)
            {
                AddWithNextIndex(foldedLines, document.Lines[lineIndex]);
                lineIndex++;
                continue;
            }

            var runStart = lineIndex;
            while (lineIndex < document.Lines.Length && document.Lines[lineIndex].Kind == DiffLineKind.Context)
            {
                lineIndex++;
            }

            AddContextRun(document, runStart, lineIndex, visibleContextLines, minimumFoldLineCount, foldedLines);
        }

        return foldedLines.Count == document.Lines.Length
            ? document
            : document with { Lines = foldedLines.ToImmutable() };
    }

    private static void AddContextRun(
        DiffDocumentSnapshot document,
        int runStart,
        int runEnd,
        int visibleContextLines,
        int minimumFoldLineCount,
        ImmutableArray<DiffLine>.Builder foldedLines)
    {
        var runLength = runEnd - runStart;
        var edgeContextLineCount = Math.Min(visibleContextLines, runLength / 2);
        var collapsedLineCount = runLength - edgeContextLineCount * 2;

        if (collapsedLineCount < minimumFoldLineCount)
        {
            for (var index = runStart; index < runEnd; index++)
            {
                AddWithNextIndex(foldedLines, document.Lines[index]);
            }

            return;
        }

        for (var index = runStart; index < runStart + edgeContextLineCount; index++)
        {
            AddWithNextIndex(foldedLines, document.Lines[index]);
        }

        foldedLines.Add(new DiffLine(
            foldedLines.Count,
            null,
            null,
            DiffLineKind.Imaginary,
            $"... {collapsedLineCount:N0} unchanged lines collapsed ...",
            ImmutableArray<TokenSpan>.Empty));

        for (var index = runEnd - edgeContextLineCount; index < runEnd; index++)
        {
            AddWithNextIndex(foldedLines, document.Lines[index]);
        }
    }

    private static void AddWithNextIndex(ImmutableArray<DiffLine>.Builder builder, DiffLine line) =>
        builder.Add(line with { Index = builder.Count });

    private static bool IsReviewRelevantLine(DiffLine line) => line.Kind is
        DiffLineKind.Added or
        DiffLineKind.Deleted or
        DiffLineKind.Modified or
        DiffLineKind.Ignored or
        DiffLineKind.Moved or
        DiffLineKind.Conflict;
}