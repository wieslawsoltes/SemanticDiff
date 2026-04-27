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

        var changedLineIndex = new SemanticChangeLineIndexBuilder().Build(documents);

        if (semanticGraph.Anchors.IsDefaultOrEmpty)
        {
            return new SemanticImpactSummary(0, 0, changedLineIndex.MovedLineCount, changedLineIndex.IgnoredLineCount);
        }

        var changedAnchorIds = semanticGraph.Anchors
            .Where(anchor => IsNavigable(anchor.Kind))
            .Where(changedLineIndex.Contains)
            .Select(anchor => anchor.Id)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var impactedEdgeCount = semanticGraph.Edges.IsDefaultOrEmpty
            ? 0
            : semanticGraph.Edges.Count(edge => changedAnchorIds.Contains(edge.SourceAnchorId) || changedAnchorIds.Contains(edge.TargetAnchorId));

        return new SemanticImpactSummary(changedAnchorIds.Count, impactedEdgeCount, changedLineIndex.MovedLineCount, changedLineIndex.IgnoredLineCount);
    }

    private static bool IsNavigable(SemanticAnchorKind kind) => kind is
        SemanticAnchorKind.Type or
        SemanticAnchorKind.Member or
        SemanticAnchorKind.XamlRoot or
        SemanticAnchorKind.XamlName or
        SemanticAnchorKind.Resource or
        SemanticAnchorKind.Binding;
}
