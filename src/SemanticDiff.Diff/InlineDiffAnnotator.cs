using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Diff;

public static class InlineDiffAnnotator
{
    private const int MaxSegmentCountForLcs = 512;

    public static ImmutableArray<DiffDocumentSnapshot> Annotate(ImmutableArray<DiffDocumentSnapshot> documents)
    {
        if (documents.IsDefaultOrEmpty)
        {
            return documents.IsDefault ? ImmutableArray<DiffDocumentSnapshot>.Empty : documents;
        }

        var builder = ImmutableArray.CreateBuilder<DiffDocumentSnapshot>(documents.Length);
        foreach (var document in documents)
        {
            builder.Add(Annotate(document));
        }

        return builder.ToImmutable();
    }

    public static DiffDocumentSnapshot Annotate(DiffDocumentSnapshot document)
    {
        if (document.Lines.IsDefaultOrEmpty)
        {
            return document;
        }

        var lines = document.Lines.ToArray();
        var changed = false;

        for (var lineIndex = 0; lineIndex < lines.Length;)
        {
            if (lines[lineIndex].Kind != DiffLineKind.Deleted)
            {
                lineIndex++;
                continue;
            }

            var deletedStart = lineIndex;
            while (lineIndex < lines.Length && lines[lineIndex].Kind == DiffLineKind.Deleted)
            {
                lineIndex++;
            }

            var addedStart = lineIndex;
            while (lineIndex < lines.Length && lines[lineIndex].Kind == DiffLineKind.Added)
            {
                lineIndex++;
            }

            var deletedCount = addedStart - deletedStart;
            var addedCount = lineIndex - addedStart;
            var pairCount = Math.Min(deletedCount, addedCount);
            if (pairCount == 0)
            {
                continue;
            }

            for (var pairIndex = 0; pairIndex < pairCount; pairIndex++)
            {
                var deletedLineIndex = deletedStart + pairIndex;
                var addedLineIndex = addedStart + pairIndex;
                var (deletedSpans, addedSpans) = CreateInlineSpans(lines[deletedLineIndex].Text, lines[addedLineIndex].Text);
                if (!deletedSpans.IsDefaultOrEmpty)
                {
                    lines[deletedLineIndex] = lines[deletedLineIndex] with { InlineSpans = deletedSpans };
                    changed = true;
                }

                if (!addedSpans.IsDefaultOrEmpty)
                {
                    lines[addedLineIndex] = lines[addedLineIndex] with { InlineSpans = addedSpans };
                    changed = true;
                }
            }
        }

        return changed ? document with { Lines = lines.ToImmutableArray() } : document;
    }

    private static (ImmutableArray<DiffInlineSpan> DeletedSpans, ImmutableArray<DiffInlineSpan> AddedSpans) CreateInlineSpans(string deletedText, string addedText)
    {
        if (string.Equals(deletedText, addedText, StringComparison.Ordinal))
        {
            return (ImmutableArray<DiffInlineSpan>.Empty, ImmutableArray<DiffInlineSpan>.Empty);
        }

        var deletedSegments = TextSegment.Split(deletedText);
        var addedSegments = TextSegment.Split(addedText);
        if (deletedSegments.Length == 0 || addedSegments.Length == 0 ||
            deletedSegments.Length > MaxSegmentCountForLcs || addedSegments.Length > MaxSegmentCountForLcs)
        {
            return CreateFallbackSpans(deletedText, addedText);
        }

        var deletedMatched = new bool[deletedSegments.Length];
        var addedMatched = new bool[addedSegments.Length];
        MarkMatchedSegments(deletedSegments, addedSegments, deletedMatched, addedMatched);

        return (
            BuildSpans(deletedSegments, deletedMatched, DiffInlineKind.Deleted),
            BuildSpans(addedSegments, addedMatched, DiffInlineKind.Inserted));
    }

    private static void MarkMatchedSegments(TextSegment[] deletedSegments, TextSegment[] addedSegments, bool[] deletedMatched, bool[] addedMatched)
    {
        var table = new int[deletedSegments.Length + 1, addedSegments.Length + 1];
        for (var deletedIndex = deletedSegments.Length - 1; deletedIndex >= 0; deletedIndex--)
        {
            for (var addedIndex = addedSegments.Length - 1; addedIndex >= 0; addedIndex--)
            {
                table[deletedIndex, addedIndex] = string.Equals(deletedSegments[deletedIndex].Text, addedSegments[addedIndex].Text, StringComparison.Ordinal)
                    ? table[deletedIndex + 1, addedIndex + 1] + 1
                    : Math.Max(table[deletedIndex + 1, addedIndex], table[deletedIndex, addedIndex + 1]);
            }
        }

        for (int deletedIndex = 0, addedIndex = 0; deletedIndex < deletedSegments.Length && addedIndex < addedSegments.Length;)
        {
            if (string.Equals(deletedSegments[deletedIndex].Text, addedSegments[addedIndex].Text, StringComparison.Ordinal))
            {
                deletedMatched[deletedIndex] = true;
                addedMatched[addedIndex] = true;
                deletedIndex++;
                addedIndex++;
            }
            else if (table[deletedIndex + 1, addedIndex] >= table[deletedIndex, addedIndex + 1])
            {
                deletedIndex++;
            }
            else
            {
                addedIndex++;
            }
        }
    }

    private static ImmutableArray<DiffInlineSpan> BuildSpans(TextSegment[] segments, bool[] matched, DiffInlineKind kind)
    {
        var builder = ImmutableArray.CreateBuilder<DiffInlineSpan>();
        var runStart = -1;
        var runEnd = -1;

        for (var index = 0; index < segments.Length; index++)
        {
            if (!matched[index])
            {
                if (runStart < 0)
                {
                    runStart = segments[index].StartColumn;
                }

                runEnd = segments[index].StartColumn + segments[index].Length;
                continue;
            }

            AddRun(builder, runStart, runEnd, kind);
            runStart = -1;
            runEnd = -1;
        }

        AddRun(builder, runStart, runEnd, kind);
        return builder.ToImmutable();
    }

    private static void AddRun(ImmutableArray<DiffInlineSpan>.Builder builder, int startColumn, int endColumn, DiffInlineKind kind)
    {
        if (startColumn >= 0 && endColumn > startColumn)
        {
            builder.Add(new DiffInlineSpan(startColumn, endColumn - startColumn, kind));
        }
    }

    private static (ImmutableArray<DiffInlineSpan> DeletedSpans, ImmutableArray<DiffInlineSpan> AddedSpans) CreateFallbackSpans(string deletedText, string addedText)
    {
        var prefixLength = 0;
        while (prefixLength < deletedText.Length && prefixLength < addedText.Length && deletedText[prefixLength] == addedText[prefixLength])
        {
            prefixLength++;
        }

        var deletedSuffix = deletedText.Length - 1;
        var addedSuffix = addedText.Length - 1;
        while (deletedSuffix >= prefixLength && addedSuffix >= prefixLength && deletedText[deletedSuffix] == addedText[addedSuffix])
        {
            deletedSuffix--;
            addedSuffix--;
        }

        var deletedLength = deletedSuffix - prefixLength + 1;
        var addedLength = addedSuffix - prefixLength + 1;
        return (
            deletedLength > 0 ? [new DiffInlineSpan(prefixLength, deletedLength, DiffInlineKind.Deleted)] : ImmutableArray<DiffInlineSpan>.Empty,
            addedLength > 0 ? [new DiffInlineSpan(prefixLength, addedLength, DiffInlineKind.Inserted)] : ImmutableArray<DiffInlineSpan>.Empty);
    }

    private readonly record struct TextSegment(int StartColumn, int Length, string Text)
    {
        public static TextSegment[] Split(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return [];
            }

            var segments = new List<TextSegment>();
            for (var column = 0; column < text.Length;)
            {
                var startColumn = column;
                var segmentKind = GetSegmentKind(text[column]);
                column++;

                while (column < text.Length && GetSegmentKind(text[column]) == segmentKind && segmentKind != SegmentKind.Symbol)
                {
                    column++;
                }

                segments.Add(new TextSegment(startColumn, column - startColumn, text[startColumn..column]));
            }

            return segments.ToArray();
        }

        private static SegmentKind GetSegmentKind(char character)
        {
            if (char.IsWhiteSpace(character))
            {
                return SegmentKind.Whitespace;
            }

            return char.IsLetterOrDigit(character) || character == '_'
                ? SegmentKind.Word
                : SegmentKind.Symbol;
        }
    }

    private enum SegmentKind
    {
        Word,
        Whitespace,
        Symbol
    }
}