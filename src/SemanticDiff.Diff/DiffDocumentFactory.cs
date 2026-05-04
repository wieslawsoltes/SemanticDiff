using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Diff;

public sealed class DiffDocumentFactory : IDiffDocumentFactory
{
    public DiffDocumentSnapshot CreateFromText(DiffDocumentMetadata metadata, string text, DiffLineKind lineKind = DiffLineKind.Context)
    {
        var builder = ImmutableArray.CreateBuilder<DiffLine>(EstimateLineCount(text));
        var lineIndex = 0;
        var lineStart = 0;

        while (TryReadLine(text, ref lineStart, out var line))
        {
            var lineNumber = lineIndex + 1;
            builder.Add(new DiffLine(lineIndex, lineNumber, lineNumber, lineKind, line.ToString(), ImmutableArray<TokenSpan>.Empty));
            lineIndex++;
        }

        return new DiffDocumentSnapshot(metadata.Id, metadata, builder.ToImmutable());
    }

    public DiffDocumentSnapshot CreateFromUnifiedDiff(DiffDocumentMetadata metadata, string unifiedDiff)
    {
        var builder = ImmutableArray.CreateBuilder<DiffLine>(EstimateLineCount(unifiedDiff));
        var oldLineNumber = 1;
        var newLineNumber = 1;
        var lineStart = 0;

        while (TryReadLine(unifiedDiff, ref lineStart, out var sourceText))
        {
            if (sourceText.StartsWith("@@".AsSpan(), StringComparison.Ordinal))
            {
                ParseHunkHeader(sourceText, ref oldLineNumber, ref newLineNumber);
                builder.Add(new DiffLine(builder.Count, null, null, DiffLineKind.Metadata, sourceText.ToString(), ImmutableArray<TokenSpan>.Empty));
                continue;
            }

            if (IsFileHeaderLine(sourceText))
            {
                builder.Add(new DiffLine(builder.Count, null, null, DiffLineKind.Metadata, sourceText.ToString(), ImmutableArray<TokenSpan>.Empty));
                continue;
            }

            if (sourceText.StartsWith("\\ ".AsSpan(), StringComparison.Ordinal))
            {
                builder.Add(new DiffLine(builder.Count, null, null, DiffLineKind.Metadata, sourceText.ToString(), ImmutableArray<TokenSpan>.Empty));
                continue;
            }

            if (sourceText.Length > 0 && sourceText[0] == '+')
            {
                builder.Add(new DiffLine(builder.Count, null, newLineNumber++, DiffLineKind.Added, sourceText[1..].ToString(), ImmutableArray<TokenSpan>.Empty));
                continue;
            }

            if (sourceText.Length > 0 && sourceText[0] == '-')
            {
                builder.Add(new DiffLine(builder.Count, oldLineNumber++, null, DiffLineKind.Deleted, sourceText[1..].ToString(), ImmutableArray<TokenSpan>.Empty));
                continue;
            }

            var text = sourceText.Length > 0 && sourceText[0] == ' ' ? sourceText[1..].ToString() : sourceText.ToString();
            builder.Add(new DiffLine(builder.Count, oldLineNumber++, newLineNumber++, DiffLineKind.Context, text, ImmutableArray<TokenSpan>.Empty));
        }

        return new DiffDocumentSnapshot(metadata.Id, metadata, builder.ToImmutable());
    }

    private static int EstimateLineCount(string text)
    {
        if (text.Length == 0)
        {
            return 1;
        }

        if (text.AsSpan().IndexOf('\r') < 0)
        {
            return 1 + text.AsSpan().Count('\n');
        }

        var count = 1;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                count++;
            }
            else if (text[index] == '\r')
            {
                count++;
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }
            }
        }

        return count;
    }

    private static bool TryReadLine(string text, ref int lineStart, out ReadOnlySpan<char> line)
    {
        if (text.Length == 0)
        {
            if (lineStart == 0)
            {
                lineStart = 1;
                line = ReadOnlySpan<char>.Empty;
                return true;
            }

            line = ReadOnlySpan<char>.Empty;
            return false;
        }

        if (lineStart == text.Length && text[^1] is '\r' or '\n')
        {
            lineStart++;
            line = ReadOnlySpan<char>.Empty;
            return true;
        }

        if (lineStart >= text.Length)
        {
            line = ReadOnlySpan<char>.Empty;
            return false;
        }

        var newlineOffset = text.AsSpan(lineStart).IndexOfAny('\r', '\n');
        if (newlineOffset < 0)
        {
            line = text.AsSpan(lineStart);
            lineStart = text.Length;
            return true;
        }

        var lineEnd = lineStart + newlineOffset;
        line = text.AsSpan(lineStart, lineEnd - lineStart);
        lineStart = lineEnd + 1;
        if (text[lineEnd] == '\r' && lineStart < text.Length && text[lineStart] == '\n')
        {
            lineStart++;
        }

        return true;
    }

    private static void ParseHunkHeader(ReadOnlySpan<char> header, ref int oldLineNumber, ref int newLineNumber)
    {
        if (!TryReadNextPart(header, 0, out _, out var nextIndex) ||
            !TryReadNextPart(header, nextIndex, out var oldPart, out nextIndex) ||
            !TryReadNextPart(header, nextIndex, out var newPart, out _))
        {
            return;
        }

        oldLineNumber = ParseStart(oldPart);
        newLineNumber = ParseStart(newPart);

        static int ParseStart(ReadOnlySpan<char> token)
        {
            var normalized = token;
            while (normalized.Length > 0 && (normalized[0] == '-' || normalized[0] == '+'))
            {
                normalized = normalized[1..];
            }

            var commaIndex = normalized.IndexOf(',');
            var startText = commaIndex >= 0 ? normalized[..commaIndex] : normalized;
            return int.TryParse(startText, out var value) ? Math.Max(1, value) : 1;
        }
    }

    private static bool IsFileHeaderLine(ReadOnlySpan<char> line) =>
        line.Length > 3 &&
        (line.StartsWith("+++".AsSpan(), StringComparison.Ordinal) ||
            line.StartsWith("---".AsSpan(), StringComparison.Ordinal)) &&
        char.IsWhiteSpace(line[3]);

    private static bool TryReadNextPart(ReadOnlySpan<char> text, int startIndex, out ReadOnlySpan<char> part, out int nextIndex)
    {
        var index = startIndex;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        var partStart = index;
        while (index < text.Length && !char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        part = text[partStart..index];
        nextIndex = index;
        return part.Length > 0;
    }
}
