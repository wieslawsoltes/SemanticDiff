using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Workbench.FileDiff;

public sealed record FileDiffDocumentView(
    DiffDocumentSnapshot DiffDocument,
    DiffDocumentSnapshot FullFileDocument,
    ImmutableArray<DiffLine> ChangedHunkLines,
    ImmutableArray<DiffLine> FullDiffLines,
    ImmutableArray<DiffLine> AnnotatedFullFileLines,
    ImmutableArray<CodeFoldRegion> FoldRegions,
    string FullText);

public sealed class FileDiffDocumentBuilder
{
    public FileDiffDocumentView Build(
        DiffDocumentSnapshot diffDocument,
        DiffDocumentSnapshot fullFileDocument,
        string fullText,
        ImmutableArray<CodeFoldRegion> foldRegions)
    {
        return new FileDiffDocumentView(
            diffDocument,
            fullFileDocument,
            diffDocument.Lines,
            CreateFullDiffLines(diffDocument, fullFileDocument),
            CreateAnnotatedFullFileLines(diffDocument, fullFileDocument),
            foldRegions.IsDefault ? ImmutableArray<CodeFoldRegion>.Empty : foldRegions,
            fullText);
    }

    private static ImmutableArray<DiffLine> CreateAnnotatedFullFileLines(DiffDocumentSnapshot diffDocument, DiffDocumentSnapshot fullFileDocument)
    {
        if (fullFileDocument.Lines.IsDefaultOrEmpty)
        {
            return [];
        }

        var diffLineByNewNumber = diffDocument.Lines
            .Where(line => line.NewLineNumber is > 0 && IsVisibleFullFileAnnotationKind(line.Kind))
            .GroupBy(line => line.NewLineNumber!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(AnnotationPriority).First());

        return fullFileDocument.Lines
            .Select((line, index) =>
            {
                var lineNumber = line.NewLineNumber ?? index + 1;
                return diffLineByNewNumber.TryGetValue(lineNumber, out var diffLine)
                    ? ReindexLine(line with
                    {
                        Kind = diffLine.Kind,
                        InlineSpans = diffLine.InlineSpans
                    }, index)
                    : ReindexLine(line with { Kind = DiffLineKind.Context }, index);
            })
            .ToImmutableArray();
    }

    private static ImmutableArray<DiffLine> CreateFullDiffLines(DiffDocumentSnapshot diffDocument, DiffDocumentSnapshot fullFileDocument)
    {
        if (fullFileDocument.Lines.IsDefaultOrEmpty)
        {
            return diffDocument.Lines;
        }

        var fullLinesByNumber = fullFileDocument.Lines
            .Select((line, index) => (LineNumber: line.NewLineNumber ?? index + 1, Line: line))
            .ToDictionary(pair => pair.LineNumber, pair => pair.Line);
        var builder = ImmutableArray.CreateBuilder<DiffLine>();
        var nextFullLineNumber = 1;

        foreach (var diffLine in diffDocument.Lines)
        {
            if (diffLine.Kind == DiffLineKind.Imaginary)
            {
                continue;
            }

            if (diffLine.Kind == DiffLineKind.Metadata)
            {
                builder.Add(ReindexLine(diffLine, builder.Count));
                continue;
            }

            if (diffLine.NewLineNumber is { } newLineNumber)
            {
                AddFullContextLines(builder, fullLinesByNumber, nextFullLineNumber, newLineNumber - 1);
                if (fullLinesByNumber.TryGetValue(newLineNumber, out var fullLine))
                {
                    builder.Add(ReindexLine(fullLine with
                    {
                        Kind = IsVisibleFullFileAnnotationKind(diffLine.Kind) ? diffLine.Kind : DiffLineKind.Context,
                        OldLineNumber = diffLine.OldLineNumber,
                        NewLineNumber = diffLine.NewLineNumber,
                        InlineSpans = diffLine.InlineSpans
                    }, builder.Count));
                }
                else
                {
                    builder.Add(ReindexLine(diffLine, builder.Count));
                }

                nextFullLineNumber = Math.Max(nextFullLineNumber, newLineNumber + 1);
                continue;
            }

            if (diffLine.Kind == DiffLineKind.Deleted)
            {
                builder.Add(ReindexLine(diffLine, builder.Count));
            }
        }

        AddFullContextLines(builder, fullLinesByNumber, nextFullLineNumber, fullLinesByNumber.Count);
        return builder.ToImmutable();
    }

    private static void AddFullContextLines(ImmutableArray<DiffLine>.Builder builder, IReadOnlyDictionary<int, DiffLine> linesByNumber, int firstLineNumber, int lastLineNumber)
    {
        for (var lineNumber = Math.Max(1, firstLineNumber); lineNumber <= lastLineNumber; lineNumber++)
        {
            if (linesByNumber.TryGetValue(lineNumber, out var line))
            {
                builder.Add(ReindexLine(line with
                {
                    OldLineNumber = lineNumber,
                    NewLineNumber = lineNumber,
                    Kind = DiffLineKind.Context
                }, builder.Count));
            }
        }
    }

    private static DiffLine ReindexLine(DiffLine line, int index) => line with { Index = index };

    private static bool IsVisibleFullFileAnnotationKind(DiffLineKind kind) =>
        kind is DiffLineKind.Added or DiffLineKind.Modified or DiffLineKind.Moved or DiffLineKind.Conflict or DiffLineKind.Ignored;

    private static int AnnotationPriority(DiffLine line) => line.Kind switch
    {
        DiffLineKind.Conflict => 0,
        DiffLineKind.Modified => 1,
        DiffLineKind.Moved => 2,
        DiffLineKind.Added => 3,
        DiffLineKind.Ignored => 4,
        _ => 9
    };
}
