using System.Buffers;
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

        DiffLine[]? changedLines = null;

        for (var lineIndex = 0; lineIndex < document.Lines.Length;)
        {
            if (document.Lines[lineIndex].Kind != DiffLineKind.Deleted)
            {
                lineIndex++;
                continue;
            }

            var deletedStart = lineIndex;
            while (lineIndex < document.Lines.Length && document.Lines[lineIndex].Kind == DiffLineKind.Deleted)
            {
                lineIndex++;
            }

            var addedStart = lineIndex;
            while (lineIndex < document.Lines.Length && document.Lines[lineIndex].Kind == DiffLineKind.Added)
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
                var deletedLine = document.Lines[deletedLineIndex];
                var addedLine = document.Lines[addedLineIndex];
                var (deletedSpans, addedSpans) = CreateInlineSpans(deletedLine.Text, addedLine.Text);
                if (!deletedSpans.IsDefaultOrEmpty)
                {
                    changedLines ??= document.Lines.ToArray();
                    changedLines[deletedLineIndex] = deletedLine with { InlineSpans = deletedSpans };
                }

                if (!addedSpans.IsDefaultOrEmpty)
                {
                    changedLines ??= document.Lines.ToArray();
                    changedLines[addedLineIndex] = addedLine with { InlineSpans = addedSpans };
                }
            }
        }

        return changedLines is null ? document : document with { Lines = changedLines.ToImmutableArray() };
    }

    private static (ImmutableArray<DiffInlineSpan> DeletedSpans, ImmutableArray<DiffInlineSpan> AddedSpans) CreateInlineSpans(string deletedText, string addedText)
    {
        if (string.Equals(deletedText, addedText, StringComparison.Ordinal))
        {
            return (ImmutableArray<DiffInlineSpan>.Empty, ImmutableArray<DiffInlineSpan>.Empty);
        }

        var deletedSegments = TextSegment.Split(deletedText, MaxSegmentCountForLcs);
        var addedSegments = TextSegment.Split(addedText, MaxSegmentCountForLcs);
        if (deletedSegments is null || addedSegments is null || deletedSegments.Length == 0 || addedSegments.Length == 0)
        {
            return CreateFallbackSpans(deletedText, addedText);
        }

        var deletedMatched = ArrayPool<bool>.Shared.Rent(deletedSegments.Length);
        var addedMatched = ArrayPool<bool>.Shared.Rent(addedSegments.Length);
        try
        {
            Array.Clear(deletedMatched, 0, deletedSegments.Length);
            Array.Clear(addedMatched, 0, addedSegments.Length);
            MarkMatchedSegments(deletedSegments, addedSegments, deletedMatched, addedMatched);

            return (
                BuildSpans(deletedSegments, deletedMatched, DiffInlineKind.Deleted),
                BuildSpans(addedSegments, addedMatched, DiffInlineKind.Inserted));
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(deletedMatched);
            ArrayPool<bool>.Shared.Return(addedMatched);
        }
    }

    private static void MarkMatchedSegments(TextSegment[] deletedSegments, TextSegment[] addedSegments, bool[] deletedMatched, bool[] addedMatched)
    {
        var width = addedSegments.Length + 1;
        var tableLength = (deletedSegments.Length + 1) * width;
        var table = ArrayPool<int>.Shared.Rent(tableLength);
        try
        {
            Array.Clear(table, deletedSegments.Length * width, width);
            for (var deletedIndex = 0; deletedIndex < deletedSegments.Length; deletedIndex++)
            {
                table[deletedIndex * width + addedSegments.Length] = 0;
            }

            for (var deletedIndex = deletedSegments.Length - 1; deletedIndex >= 0; deletedIndex--)
            {
                var rowOffset = deletedIndex * width;
                var nextRowOffset = rowOffset + width;
                for (var addedIndex = addedSegments.Length - 1; addedIndex >= 0; addedIndex--)
                {
                    table[rowOffset + addedIndex] = deletedSegments[deletedIndex].TextEquals(addedSegments[addedIndex])
                        ? table[nextRowOffset + addedIndex + 1] + 1
                        : Math.Max(table[nextRowOffset + addedIndex], table[rowOffset + addedIndex + 1]);
                }
            }

            for (int deletedIndex = 0, addedIndex = 0; deletedIndex < deletedSegments.Length && addedIndex < addedSegments.Length;)
            {
                if (deletedSegments[deletedIndex].TextEquals(addedSegments[addedIndex]))
                {
                    deletedMatched[deletedIndex] = true;
                    addedMatched[addedIndex] = true;
                    deletedIndex++;
                    addedIndex++;
                }
                else if (table[(deletedIndex + 1) * width + addedIndex] >= table[deletedIndex * width + addedIndex + 1])
                {
                    deletedIndex++;
                }
                else
                {
                    addedIndex++;
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(table);
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

    private readonly record struct TextSegment(string Source, int StartColumn, int Length)
    {
        public bool TextEquals(TextSegment other) =>
            Length == other.Length &&
            Source.AsSpan(StartColumn, Length).SequenceEqual(other.Source.AsSpan(other.StartColumn, other.Length));

        public static TextSegment[]? Split(string text, int maxSegmentCount)
        {
            if (string.IsNullOrEmpty(text))
            {
                return [];
            }

            var segmentCount = CountSegments(text, maxSegmentCount);
            if (segmentCount < 0)
            {
                return null;
            }

            var segments = new TextSegment[segmentCount];
            var segmentIndex = 0;
            for (var column = 0; column < text.Length;)
            {
                var startColumn = column;
                var segmentKind = GetSegmentKind(text[column]);
                column++;

                while (column < text.Length && GetSegmentKind(text[column]) == segmentKind && segmentKind != SegmentKind.Symbol)
                {
                    column++;
                }

                segments[segmentIndex++] = new TextSegment(text, startColumn, column - startColumn);
            }

            return segments;
        }

        private static int CountSegments(string text, int maxSegmentCount)
        {
            var segmentCount = 0;
            for (var column = 0; column < text.Length;)
            {
                var segmentKind = GetSegmentKind(text[column]);
                column++;

                while (column < text.Length && GetSegmentKind(text[column]) == segmentKind && segmentKind != SegmentKind.Symbol)
                {
                    column++;
                }

                segmentCount++;
                if (segmentCount > maxSegmentCount)
                {
                    return -1;
                }
            }

            return segmentCount;
        }

        private static SegmentKind GetSegmentKind(char character)
        {
            if (character <= 127)
            {
                if (character == ' ' || (uint)(character - '\t') <= '\r' - '\t')
                {
                    return SegmentKind.Whitespace;
                }

                return ((uint)(character - 'A') <= 'Z' - 'A') ||
                    ((uint)(character - 'a') <= 'z' - 'a') ||
                    ((uint)(character - '0') <= '9' - '0') ||
                    character == '_'
                    ? SegmentKind.Word
                    : SegmentKind.Symbol;
            }

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
