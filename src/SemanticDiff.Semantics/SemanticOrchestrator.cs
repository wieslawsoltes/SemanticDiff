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
        var anchorsById = deduplicatedAnchors.ToDictionary(anchor => anchor.Id, StringComparer.Ordinal);
        var inferredEdges = InferCrossProviderEdges(deduplicatedAnchors);
        var deduplicatedEdges = edges
            .Concat(inferredEdges)
            .GroupBy(edge => edge.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(edge => edge.Confidence).First())
            .Where(edge => IsIncluded(edge, anchorsById, filter))
            .ToImmutableArray();

        return new SemanticGraph(deduplicatedAnchors, deduplicatedEdges);
    }

    private static IEnumerable<SemanticEdge> InferCrossProviderEdges(ImmutableArray<SemanticAnchor> anchors)
    {
        foreach (var edge in InferPathCompanionEdges(anchors))
        {
            yield return edge;
        }

        foreach (var edge in InferRepositoryAreaEdges(anchors))
        {
            yield return edge;
        }

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

        var membersByName = anchors
            .Where(anchor => anchor.Kind == SemanticAnchorKind.Member)
            .GroupBy(anchor => anchor.DisplayName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(anchor => anchor.DocumentId.Value, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        foreach (var binding in anchors.Where(anchor => anchor.Kind == SemanticAnchorKind.Binding).OrderBy(anchor => anchor.DocumentId.Value, StringComparer.Ordinal))
        {
            var bindingName = NormalizeBindingName(binding.DisplayName);
            if (string.IsNullOrWhiteSpace(bindingName) || !membersByName.TryGetValue(bindingName, out var members))
            {
                continue;
            }

            var matchingMembers = members
                .Where(anchor => anchor.DocumentId != binding.DocumentId)
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

    private static IEnumerable<SemanticEdge> InferPathCompanionEdges(ImmutableArray<SemanticAnchor> anchors)
    {
        var primaryAnchorsByPath = anchors
            .GroupBy(anchor => NormalizePath(anchor.DocumentId.Value), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => SelectPrimaryAnchor(group), StringComparer.OrdinalIgnoreCase);

        foreach (var item in primaryAnchorsByPath.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryGetCompanionPath(item.Key, out var companionPath) ||
                !primaryAnchorsByPath.TryGetValue(companionPath, out var companionAnchor) ||
                string.Compare(item.Value.DocumentId.Value, companionAnchor.DocumentId.Value, StringComparison.Ordinal) >= 0)
            {
                continue;
            }

            yield return new SemanticEdge(
                $"{SemanticEdgeKind.GeneratedFile}:{item.Value.Id}->{companionAnchor.Id}:companion",
                item.Value.Id,
                companionAnchor.Id,
                SemanticEdgeKind.GeneratedFile,
                0.86,
                "companion");
        }
    }

    private static IEnumerable<SemanticEdge> InferRepositoryAreaEdges(ImmutableArray<SemanticAnchor> anchors)
    {
        var primaryAnchorsByDocument = anchors
            .GroupBy(anchor => anchor.DocumentId)
            .Select(group => SelectPrimaryAnchor(group))
            .Where(anchor => IsRepositoryAreaAnchor(anchor))
            .ToArray();

        foreach (var group in primaryAnchorsByDocument
            .GroupBy(anchor => GetRepositoryArea(anchor.DocumentId.Value), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= 2)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(anchor => anchor.DocumentId.Value, StringComparer.OrdinalIgnoreCase).ToArray();
            for (var anchorIndex = 0; anchorIndex < ordered.Length - 1; anchorIndex++)
            {
                var source = ordered[anchorIndex];
                var target = ordered[anchorIndex + 1];
                yield return new SemanticEdge(
                    $"{SemanticEdgeKind.ProjectReference}:{source.Id}->{target.Id}:area",
                    source.Id,
                    target.Id,
                    SemanticEdgeKind.ProjectReference,
                    0.78,
                    "area");
            }
        }
    }

    private static SemanticAnchor SelectPrimaryAnchor(IEnumerable<SemanticAnchor> anchors) => anchors
        .OrderBy(PrimaryAnchorPriority)
        .ThenBy(anchor => anchor.Range.Line)
        .ThenBy(anchor => anchor.DisplayName, StringComparer.Ordinal)
        .First();

    private static int PrimaryAnchorPriority(SemanticAnchor anchor) => anchor.Kind switch
    {
        SemanticAnchorKind.Type => 0,
        SemanticAnchorKind.XamlRoot => 1,
        SemanticAnchorKind.Project => 2,
        SemanticAnchorKind.Namespace => 3,
        SemanticAnchorKind.Member => 4,
        SemanticAnchorKind.Resource => 5,
        SemanticAnchorKind.Binding => 6,
        _ => 7
    };

    private static bool IsRepositoryAreaAnchor(SemanticAnchor anchor) => anchor.Kind is
        SemanticAnchorKind.Type or
        SemanticAnchorKind.XamlRoot or
        SemanticAnchorKind.Project or
        SemanticAnchorKind.Namespace or
        SemanticAnchorKind.Member;

    private static bool TryGetCompanionPath(string path, out string companionPath)
    {
        if (path.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".axaml.cs", StringComparison.OrdinalIgnoreCase))
        {
            companionPath = path[..^3];
            return true;
        }

        if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
        {
            companionPath = path + ".cs";
            return true;
        }

        companionPath = string.Empty;
        return false;
    }

    private static string GetRepositoryArea(string path)
    {
        var normalizedPath = NormalizePath(path);
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length >= 2 && IsSourceLikeRoot(segments[0]))
        {
            return $"{segments[0]}/{segments[1]}";
        }

        return segments.Length == 0 || segments.Length == 1 ? "Repository root" : segments[0];
    }

    private static bool IsSourceLikeRoot(string segment) =>
        segment.Equals("src", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("samples", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("examples", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("test", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) => path.Replace('\\', '/');

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

    private static bool IsIncluded(SemanticEdge edge, IReadOnlyDictionary<string, SemanticAnchor> anchorsById, SemanticGraphFilter filter)
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

        if (!anchorsById.TryGetValue(edge.SourceAnchorId, out var source) ||
            !anchorsById.TryGetValue(edge.TargetAnchorId, out var target))
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