using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Diff;

public sealed class DiffDocumentFactory : IDiffDocumentFactory
{
    public DiffDocumentSnapshot CreateFromText(DiffDocumentMetadata metadata, string text, DiffLineKind lineKind = DiffLineKind.Context)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var builder = ImmutableArray.CreateBuilder<DiffLine>(lines.Length);

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var lineNumber = lineIndex + 1;
            builder.Add(new DiffLine(lineIndex, lineNumber, lineNumber, lineKind, lines[lineIndex], ImmutableArray<TokenSpan>.Empty));
        }

        return new DiffDocumentSnapshot(metadata.Id, metadata, builder.ToImmutable());
    }

    public DiffDocumentSnapshot CreateFromUnifiedDiff(DiffDocumentMetadata metadata, string unifiedDiff)
    {
        var sourceLines = unifiedDiff.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var builder = ImmutableArray.CreateBuilder<DiffLine>(sourceLines.Length);
        var oldLineNumber = 1;
        var newLineNumber = 1;

        foreach (var sourceLine in sourceLines)
        {
            if (sourceLine.StartsWith("@@", StringComparison.Ordinal))
            {
                ParseHunkHeader(sourceLine, ref oldLineNumber, ref newLineNumber);
                builder.Add(new DiffLine(builder.Count, null, null, DiffLineKind.Metadata, sourceLine, ImmutableArray<TokenSpan>.Empty));
                continue;
            }

            if (sourceLine.StartsWith("+++", StringComparison.Ordinal) || sourceLine.StartsWith("---", StringComparison.Ordinal))
            {
                builder.Add(new DiffLine(builder.Count, null, null, DiffLineKind.Metadata, sourceLine, ImmutableArray<TokenSpan>.Empty));
                continue;
            }

            if (sourceLine.StartsWith('+'))
            {
                builder.Add(new DiffLine(builder.Count, null, newLineNumber++, DiffLineKind.Added, sourceLine[1..], ImmutableArray<TokenSpan>.Empty));
                continue;
            }

            if (sourceLine.StartsWith('-'))
            {
                builder.Add(new DiffLine(builder.Count, oldLineNumber++, null, DiffLineKind.Deleted, sourceLine[1..], ImmutableArray<TokenSpan>.Empty));
                continue;
            }

            var text = sourceLine.StartsWith(' ') ? sourceLine[1..] : sourceLine;
            builder.Add(new DiffLine(builder.Count, oldLineNumber++, newLineNumber++, DiffLineKind.Context, text, ImmutableArray<TokenSpan>.Empty));
        }

        return new DiffDocumentSnapshot(metadata.Id, metadata, builder.ToImmutable());
    }

    private static void ParseHunkHeader(string header, ref int oldLineNumber, ref int newLineNumber)
    {
        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return;
        }

        oldLineNumber = ParseStart(parts[1]);
        newLineNumber = ParseStart(parts[2]);

        static int ParseStart(string token)
        {
            var normalized = token.TrimStart('-', '+');
            var commaIndex = normalized.IndexOf(',', StringComparison.Ordinal);
            var startText = commaIndex >= 0 ? normalized[..commaIndex] : normalized;
            return int.TryParse(startText, out var value) ? Math.Max(1, value) : 1;
        }
    }
}