using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Semantics;

public sealed class SemanticOrchestrator
{
    private readonly IReadOnlyList<ISemanticProvider> providers;
    private readonly SemanticGraphFilter filter;

    public SemanticOrchestrator(IEnumerable<ISemanticProvider> providers, SemanticGraphFilter? filter = null)
    {
        this.providers = providers.ToArray();
        this.filter = filter ?? new SemanticGraphFilter();
    }

    public async ValueTask<SemanticGraph> AnalyzeAsync(SemanticAnalysisRequest request, CancellationToken cancellationToken)
    {
        var anchors = ImmutableArray.CreateBuilder<SemanticAnchor>();
        var edges = ImmutableArray.CreateBuilder<SemanticEdge>();

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.GitSnapshot is not null && !request.GitSnapshot.Files.Any(provider.CanAnalyze))
            {
                continue;
            }

            var graph = await provider.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
            anchors.AddRange(graph.Anchors);
            edges.AddRange(graph.Edges);
        }

        var deduplicatedAnchors = anchors
            .GroupBy(anchor => anchor.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToImmutableArray();
        var inferredEdges = InferCrossProviderEdges(deduplicatedAnchors);
        var deduplicatedEdges = edges
            .Concat(inferredEdges)
            .GroupBy(edge => edge.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(edge => edge.Confidence).First())
            .Where(edge => IsIncluded(edge, deduplicatedAnchors, filter))
            .ToImmutableArray();

        return new SemanticGraph(deduplicatedAnchors, deduplicatedEdges);
    }

    private static IEnumerable<SemanticEdge> InferCrossProviderEdges(ImmutableArray<SemanticAnchor> anchors)
    {
        foreach (var group in anchors.Where(anchor => anchor.Kind == SemanticAnchorKind.Type).GroupBy(anchor => anchor.DisplayName, StringComparer.Ordinal))
        {
            var distinctDocuments = group.Select(anchor => anchor.DocumentId).Distinct().ToArray();
            if (distinctDocuments.Length < 2)
            {
                continue;
            }

            var ordered = group.OrderBy(anchor => anchor.DocumentId.Value, StringComparer.Ordinal).ToArray();
            for (var anchorIndex = 0; anchorIndex < ordered.Length - 1; anchorIndex++)
            {
                var source = ordered[anchorIndex];
                var target = ordered[anchorIndex + 1];
                var edgeKind = IsXamlDocument(source.DocumentId) || IsXamlDocument(target.DocumentId)
                    ? SemanticEdgeKind.XamlClass
                    : SemanticEdgeKind.PartialClass;
                var label = edgeKind == SemanticEdgeKind.XamlClass ? "x:Class" : "partial";
                yield return new SemanticEdge($"{edgeKind}:{source.Id}->{target.Id}:{label}", source.Id, target.Id, edgeKind, 0.9, label);
            }
        }
    }

    private static bool IsIncluded(SemanticEdge edge, ImmutableArray<SemanticAnchor> anchors, SemanticGraphFilter filter)
    {
        if (edge.Confidence < filter.MinimumConfidence)
        {
            return false;
        }

        if (filter.IncludedEdgeKinds is not null && !filter.IncludedEdgeKinds.Contains(edge.Kind))
        {
            return false;
        }

        if (filter.FocusDocuments is null || filter.FocusDocuments.Count == 0)
        {
            return true;
        }

        var source = anchors.FirstOrDefault(anchor => anchor.Id == edge.SourceAnchorId);
        var target = anchors.FirstOrDefault(anchor => anchor.Id == edge.TargetAnchorId);
        if (source is null || target is null)
        {
            return false;
        }

        return filter.FocusDocuments.Contains(source.DocumentId) || filter.FocusDocuments.Contains(target.DocumentId);
    }

    private static bool IsXamlDocument(DiffDocumentId documentId) =>
        documentId.Value.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
        documentId.Value.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ||
        documentId.Value.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
}