using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Semantics;

public sealed record SemanticImpactSummary(
    int ChangedSymbolCount,
    int ImpactedEdgeCount,
    int MovedLineCount,
    int IgnoredLineCount)
{
    public static SemanticImpactSummary Empty { get; } = new(0, 0, 0, 0);
}

public sealed class SemanticImpactAnalyzer
{
    public SemanticImpactSummary Analyze(ImmutableArray<DiffDocumentSnapshot> documents, SemanticGraph semanticGraph)
    {
        if (documents.IsDefaultOrEmpty)
        {
            return SemanticImpactSummary.Empty;
        }

        var changedLinesByDocumentId = new Dictionary<DiffDocumentId, HashSet<int>>();
        var movedLineCount = 0;
        var ignoredLineCount = 0;

        foreach (var document in documents)
        {
            var changedLines = new HashSet<int>();
            foreach (var line in document.Lines)
            {
                if (line.Kind == DiffLineKind.Moved)
                {
                    movedLineCount++;
                }
                else if (line.Kind == DiffLineKind.Ignored)
                {
                    ignoredLineCount++;
                    continue;
                }

                if (!IsImpactingLine(line.Kind))
                {
                    continue;
                }

                changedLines.Add(line.Index + 1);
                if (line.OldLineNumber is { } oldLineNumber)
                {
                    changedLines.Add(oldLineNumber);
                }

                if (line.NewLineNumber is { } newLineNumber)
                {
                    changedLines.Add(newLineNumber);
                }
            }

            if (changedLines.Count > 0)
            {
                changedLinesByDocumentId[document.Id] = changedLines;
            }
        }

        if (semanticGraph.Anchors.IsDefaultOrEmpty)
        {
            return new SemanticImpactSummary(0, 0, movedLineCount, ignoredLineCount);
        }

        var changedAnchorIds = semanticGraph.Anchors
            .Where(anchor => IsNavigable(anchor.Kind))
            .Where(anchor => changedLinesByDocumentId.TryGetValue(anchor.DocumentId, out var changedLines) && changedLines.Contains(anchor.Range.Line))
            .Select(anchor => anchor.Id)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var impactedEdgeCount = semanticGraph.Edges.IsDefaultOrEmpty
            ? 0
            : semanticGraph.Edges.Count(edge => changedAnchorIds.Contains(edge.SourceAnchorId) || changedAnchorIds.Contains(edge.TargetAnchorId));

        return new SemanticImpactSummary(changedAnchorIds.Count, impactedEdgeCount, movedLineCount, ignoredLineCount);
    }

    private static bool IsImpactingLine(DiffLineKind kind) => kind is DiffLineKind.Added or DiffLineKind.Deleted or DiffLineKind.Modified or DiffLineKind.Moved or DiffLineKind.Conflict;

    private static bool IsNavigable(SemanticAnchorKind kind) => kind is
        SemanticAnchorKind.Type or
        SemanticAnchorKind.Member or
        SemanticAnchorKind.XamlRoot or
        SemanticAnchorKind.XamlName or
        SemanticAnchorKind.Resource or
        SemanticAnchorKind.Binding;
}