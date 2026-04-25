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
        foreach (var group in anchors.Where(IsCSharpTypeAnchor).GroupBy(anchor => anchor.DisplayName, StringComparer.Ordinal))
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
                yield return new SemanticEdge($"{SemanticEdgeKind.PartialClass}:{source.Id}->{target.Id}:partial", source.Id, target.Id, SemanticEdgeKind.PartialClass, 0.88, "partial");
            }
        }

        foreach (var group in anchors.Where(anchor => IsCSharpTypeAnchor(anchor) || IsXamlClassAnchor(anchor)).GroupBy(anchor => anchor.DisplayName, StringComparer.Ordinal))
        {
            var csharpAnchors = group.Where(IsCSharpTypeAnchor).OrderBy(anchor => anchor.DocumentId.Value, StringComparer.Ordinal).ToArray();
            var xamlAnchors = group.Where(IsXamlClassAnchor).OrderBy(anchor => anchor.DocumentId.Value, StringComparer.Ordinal).ToArray();
            foreach (var xamlAnchor in xamlAnchors)
            {
                foreach (var csharpAnchor in csharpAnchors.Where(anchor => anchor.DocumentId != xamlAnchor.DocumentId).Take(4))
                {
                    yield return new SemanticEdge($"{SemanticEdgeKind.XamlClass}:{xamlAnchor.Id}->{csharpAnchor.Id}:x:Class", xamlAnchor.Id, csharpAnchor.Id, SemanticEdgeKind.XamlClass, 0.94, "x:Class");
                }
            }
        }

        foreach (var group in anchors.Where(anchor => anchor.Kind == SemanticAnchorKind.Resource).GroupBy(anchor => anchor.DisplayName, StringComparer.Ordinal))
        {
            var definitions = group.Where(IsXamlResourceDefinitionAnchor).OrderBy(anchor => anchor.DocumentId.Value, StringComparer.Ordinal).ToArray();
            if (definitions.Length > 4)
            {
                continue;
            }

            var references = group.Where(IsXamlResourceReferenceAnchor).OrderBy(anchor => anchor.DocumentId.Value, StringComparer.Ordinal).ToArray();
            foreach (var reference in references)
            {
                foreach (var definition in definitions.Where(anchor => anchor.DocumentId != reference.DocumentId))
                {
                    yield return new SemanticEdge($"{SemanticEdgeKind.Resource}:{reference.Id}->{definition.Id}:resource", reference.Id, definition.Id, SemanticEdgeKind.Resource, 0.84, "resource");
                }
            }
        }

        foreach (var binding in anchors.Where(anchor => anchor.Kind == SemanticAnchorKind.Binding).OrderBy(anchor => anchor.DocumentId.Value, StringComparer.Ordinal))
        {
            var bindingName = NormalizeBindingName(binding.DisplayName);
            if (string.IsNullOrWhiteSpace(bindingName))
            {
                continue;
            }

            var matchingMembers = anchors
                .Where(anchor => anchor.Kind == SemanticAnchorKind.Member && anchor.DocumentId != binding.DocumentId && string.Equals(anchor.DisplayName, bindingName, StringComparison.Ordinal))
                .OrderBy(anchor => anchor.DocumentId.Value, StringComparer.Ordinal)
                .Take(5)
                .ToArray();
            if (matchingMembers.Length > 4)
            {
                continue;
            }

            foreach (var member in matchingMembers)
            {
                yield return new SemanticEdge($"{SemanticEdgeKind.Binding}:{binding.Id}->{member.Id}:binding", binding.Id, member.Id, SemanticEdgeKind.Binding, 0.76, "binding");
            }
        }
    }

    private static bool IsCSharpTypeAnchor(SemanticAnchor anchor) =>
        anchor.Kind == SemanticAnchorKind.Type && anchor.Id.Contains(":type:", StringComparison.Ordinal);

    private static bool IsXamlClassAnchor(SemanticAnchor anchor) =>
        anchor.Kind == SemanticAnchorKind.Type && anchor.Id.Contains(":xaml-class:", StringComparison.Ordinal);

    private static bool IsXamlResourceDefinitionAnchor(SemanticAnchor anchor) => anchor.Id.Contains(":xaml-Key:", StringComparison.Ordinal);

    private static bool IsXamlResourceReferenceAnchor(SemanticAnchor anchor) => anchor.Id.Contains(":xaml-Resource:", StringComparison.Ordinal);

    private static string NormalizeBindingName(string displayName)
    {
        var dotIndex = displayName.IndexOf('.', StringComparison.Ordinal);
        return dotIndex >= 0 ? displayName[..dotIndex] : displayName;
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