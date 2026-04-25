using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Diff;

public sealed record DiffConflictRegion(
    DiffDocumentId DocumentId,
    string Path,
    int StartLineIndex,
    int EndLineIndex,
    int DisplayLineNumber,
    string MarkerText);

public sealed record DiffConflictSummary(int ConflictedFileCount, int ConflictRegionCount)
{
    public static DiffConflictSummary Empty { get; } = new(0, 0);
}

public sealed class DiffConflictAnalyzer
{
    public ImmutableArray<DiffDocumentSnapshot> Highlight(ImmutableArray<DiffDocumentSnapshot> documents)
    {
        if (documents.IsDefaultOrEmpty)
        {
            return documents.IsDefault ? ImmutableArray<DiffDocumentSnapshot>.Empty : documents;
        }

        var builder = ImmutableArray.CreateBuilder<DiffDocumentSnapshot>(documents.Length);
        foreach (var document in documents)
        {
            builder.Add(Highlight(document));
        }

        return builder.ToImmutable();
    }

    public DiffDocumentSnapshot Highlight(DiffDocumentSnapshot document)
    {
        var regions = FindRegions(document);
        if (regions.IsDefaultOrEmpty)
        {
            return document;
        }

        var conflictLineIndexes = regions
            .SelectMany(region => Enumerable.Range(region.StartLineIndex, region.EndLineIndex - region.StartLineIndex + 1))
            .ToHashSet();
        var builder = ImmutableArray.CreateBuilder<DiffLine>(document.Lines.Length);
        foreach (var line in document.Lines)
        {
            builder.Add(conflictLineIndexes.Contains(line.Index) ? line with { Kind = DiffLineKind.Conflict } : line);
        }

        return document with { Lines = builder.ToImmutable() };
    }

    public DiffConflictSummary Analyze(ImmutableArray<DiffDocumentSnapshot> documents)
    {
        if (documents.IsDefaultOrEmpty)
        {
            return DiffConflictSummary.Empty;
        }

        var conflictedFiles = 0;
        var conflictRegions = 0;
        foreach (var document in documents)
        {
            var regions = FindRegions(document);
            conflictRegions += regions.Length;
            if (regions.Length > 0 || document.Metadata.Status == DiffFileStatus.Conflicted)
            {
                conflictedFiles++;
            }
        }

        return new DiffConflictSummary(conflictedFiles, conflictRegions);
    }

    public ImmutableArray<DiffConflictRegion> FindRegions(DiffDocumentSnapshot document)
    {
        if (document.Lines.IsDefaultOrEmpty)
        {
            return ImmutableArray<DiffConflictRegion>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<DiffConflictRegion>();
        var startLineIndex = -1;
        var markerText = string.Empty;

        for (var lineIndex = 0; lineIndex < document.Lines.Length; lineIndex++)
        {
            var line = document.Lines[lineIndex];
            if (IsConflictStart(line.Text))
            {
                if (startLineIndex >= 0)
                {
                    AddRegion(builder, document, startLineIndex, lineIndex - 1, markerText);
                }

                startLineIndex = lineIndex;
                markerText = line.Text.Trim();
                continue;
            }

            if (startLineIndex >= 0 && IsConflictEnd(line.Text))
            {
                AddRegion(builder, document, startLineIndex, lineIndex, markerText);
                startLineIndex = -1;
                markerText = string.Empty;
            }
        }

        if (startLineIndex >= 0)
        {
            AddRegion(builder, document, startLineIndex, document.Lines.Length - 1, markerText);
        }

        return builder.ToImmutable();
    }

    private static void AddRegion(
        ImmutableArray<DiffConflictRegion>.Builder builder,
        DiffDocumentSnapshot document,
        int startLineIndex,
        int endLineIndex,
        string markerText)
    {
        if (startLineIndex < 0 || endLineIndex < startLineIndex || startLineIndex >= document.Lines.Length)
        {
            return;
        }

        var line = document.Lines[startLineIndex];
        builder.Add(new DiffConflictRegion(
            document.Id,
            document.Metadata.Path,
            startLineIndex,
            Math.Min(endLineIndex, document.Lines.Length - 1),
            line.NewLineNumber ?? line.OldLineNumber ?? startLineIndex + 1,
            markerText));
    }

    private static bool IsConflictStart(string text) => text.TrimStart().StartsWith("<<<<<<<", StringComparison.Ordinal);

    private static bool IsConflictEnd(string text) => text.TrimStart().StartsWith(">>>>>>>", StringComparison.Ordinal);
}