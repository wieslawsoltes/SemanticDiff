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
            if (request.GitSnapshot is not null && !HasAnyAnalyzableFile(request.GitSnapshot.Files, provider))
            {
                continue;
            }

            var graph = await provider.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
            anchors.AddRange(graph.Anchors);
            edges.AddRange(graph.Edges);
        }

        var anchorsById = DeduplicateAnchors(anchors);
        var deduplicatedAnchors = anchorsById.Values.ToImmutableArray();
        var edgesById = DeduplicateEdges(edges, InferCrossProviderEdges(deduplicatedAnchors));
        var deduplicatedEdgeBuilder = ImmutableArray.CreateBuilder<SemanticEdge>(edgesById.Count);
        foreach (var edge in edgesById.Values)
        {
            if (IsIncluded(edge, anchorsById, filter))
            {
                deduplicatedEdgeBuilder.Add(edge);
            }
        }

        return new SemanticGraph(deduplicatedAnchors, deduplicatedEdgeBuilder.ToImmutable());
    }

    private static bool HasAnyAnalyzableFile(ImmutableArray<GitFileChange> files, ISemanticProvider provider)
    {
        foreach (var file in files)
        {
            if (provider.CanAnalyze(file))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, SemanticAnchor> DeduplicateAnchors(ImmutableArray<SemanticAnchor>.Builder anchors)
    {
        var anchorsById = new Dictionary<string, SemanticAnchor>(anchors.Count, StringComparer.Ordinal);
        foreach (var anchor in anchors)
        {
            anchorsById.TryAdd(anchor.Id, anchor);
        }

        return anchorsById;
    }

    private static Dictionary<string, SemanticEdge> DeduplicateEdges(
        ImmutableArray<SemanticEdge>.Builder providerEdges,
        IEnumerable<SemanticEdge> inferredEdges)
    {
        var edgesById = new Dictionary<string, SemanticEdge>(providerEdges.Count, StringComparer.Ordinal);
        foreach (var edge in providerEdges)
        {
            AddBestEdge(edgesById, edge);
        }

        foreach (var edge in inferredEdges)
        {
            AddBestEdge(edgesById, edge);
        }

        return edgesById;
    }

    private static void AddBestEdge(Dictionary<string, SemanticEdge> edgesById, SemanticEdge edge)
    {
        if (!edgesById.TryGetValue(edge.Id, out var existing) || edge.Confidence > existing.Confidence)
        {
            edgesById[edge.Id] = edge;
        }
    }

    private static IEnumerable<SemanticEdge> InferCrossProviderEdges(ImmutableArray<SemanticAnchor> anchors)
    {
        var index = SemanticAnchorInferenceIndex.Create(anchors);

        foreach (var edge in InferPathCompanionEdges(index.PrimaryAnchorsByPath))
        {
            yield return edge;
        }

        foreach (var edge in InferRepositoryAreaEdges(index.PrimaryAnchorsByDocument))
        {
            yield return edge;
        }

        foreach (var csharpAnchors in index.CSharpTypesByName.Values)
        {
            if (!HasMultipleDocuments(csharpAnchors))
            {
                continue;
            }

            SortAnchorsByDocument(csharpAnchors);
            for (var anchorIndex = 0; anchorIndex < csharpAnchors.Count - 1; anchorIndex++)
            {
                var source = csharpAnchors[anchorIndex];
                var target = csharpAnchors[anchorIndex + 1];
                yield return new SemanticEdge($"{SemanticEdgeKind.PartialClass}:{source.Id}->{target.Id}:partial", source.Id, target.Id, SemanticEdgeKind.PartialClass, 0.88, "partial");
            }
        }

        foreach (var (name, xamlAnchors) in index.XamlClassesByName)
        {
            if (!index.CSharpTypesByName.TryGetValue(name, out var csharpAnchors))
            {
                continue;
            }

            SortAnchorsByDocument(csharpAnchors);
            SortAnchorsByDocument(xamlAnchors);
            foreach (var xamlAnchor in xamlAnchors)
            {
                var emitted = 0;
                foreach (var csharpAnchor in csharpAnchors)
                {
                    if (csharpAnchor.DocumentId == xamlAnchor.DocumentId)
                    {
                        continue;
                    }

                    yield return new SemanticEdge($"{SemanticEdgeKind.XamlClass}:{xamlAnchor.Id}->{csharpAnchor.Id}:x:Class", xamlAnchor.Id, csharpAnchor.Id, SemanticEdgeKind.XamlClass, 0.94, "x:Class");
                    emitted++;
                    if (emitted >= 4)
                    {
                        break;
                    }
                }
            }
        }

        foreach (var (name, definitions) in index.ResourceDefinitionsByName)
        {
            if (definitions.Count > 4 || !index.ResourceReferencesByName.TryGetValue(name, out var references))
            {
                continue;
            }

            SortAnchorsByDocument(definitions);
            SortAnchorsByDocument(references);
            foreach (var reference in references)
            {
                foreach (var definition in definitions)
                {
                    if (definition.DocumentId == reference.DocumentId)
                    {
                        continue;
                    }

                    yield return new SemanticEdge($"{SemanticEdgeKind.Resource}:{reference.Id}->{definition.Id}:resource", reference.Id, definition.Id, SemanticEdgeKind.Resource, 0.84, "resource");
                }
            }
        }

        SortAnchorsByDocument(index.Bindings);
        foreach (var binding in index.Bindings)
        {
            var bindingName = NormalizeBindingName(binding.DisplayName);
            if (bindingName.Length == 0 || !index.MembersByName.TryGetValue(bindingName, out var members))
            {
                continue;
            }

            SortAnchorsByDocument(members);
            var matchingCount = CountMatchingOtherDocumentAnchors(members, binding.DocumentId, 5);
            if (matchingCount > 4)
            {
                continue;
            }

            var emitted = 0;
            foreach (var member in members)
            {
                if (member.DocumentId == binding.DocumentId)
                {
                    continue;
                }

                yield return new SemanticEdge($"{SemanticEdgeKind.Binding}:{binding.Id}->{member.Id}:binding", binding.Id, member.Id, SemanticEdgeKind.Binding, 0.76, "binding");
                emitted++;
                if (emitted >= matchingCount)
                {
                    break;
                }
            }
        }
    }

    private static IEnumerable<SemanticEdge> InferPathCompanionEdges(IReadOnlyDictionary<string, SemanticAnchor> primaryAnchorsByPath)
    {
        var paths = primaryAnchorsByPath.Keys.ToArray();
        Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var anchor = primaryAnchorsByPath[path];
            if (!TryGetCompanionPath(path, out var companionPath) ||
                !primaryAnchorsByPath.TryGetValue(companionPath, out var companionAnchor) ||
                string.Compare(anchor.DocumentId.Value, companionAnchor.DocumentId.Value, StringComparison.Ordinal) >= 0)
            {
                continue;
            }

            yield return new SemanticEdge(
                $"{SemanticEdgeKind.GeneratedFile}:{anchor.Id}->{companionAnchor.Id}:companion",
                anchor.Id,
                companionAnchor.Id,
                SemanticEdgeKind.GeneratedFile,
                0.86,
                "companion");
        }
    }

    private static IEnumerable<SemanticEdge> InferRepositoryAreaEdges(IReadOnlyDictionary<DiffDocumentId, SemanticAnchor> primaryAnchorsByDocument)
    {
        var anchorsByArea = new Dictionary<string, List<SemanticAnchor>>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in primaryAnchorsByDocument.Values)
        {
            if (!IsRepositoryAreaAnchor(anchor))
            {
                continue;
            }

            AddAnchorByKey(anchorsByArea, GetRepositoryArea(anchor.DocumentId.Value), anchor);
        }

        var areas = anchorsByArea.Keys.ToArray();
        Array.Sort(areas, StringComparer.OrdinalIgnoreCase);
        foreach (var area in areas)
        {
            var ordered = anchorsByArea[area];
            if (ordered.Count < 2)
            {
                continue;
            }

            ordered.Sort(CompareAnchorsByDocumentIdIgnoreCase);
            for (var anchorIndex = 0; anchorIndex < ordered.Count - 1; anchorIndex++)
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

    private static bool TryReplacePrimaryAnchor(Dictionary<string, SemanticAnchor> anchorsByKey, string key, SemanticAnchor anchor)
    {
        if (anchorsByKey.TryGetValue(key, out var existing) && ComparePrimaryAnchors(existing, anchor) <= 0)
        {
            return false;
        }

        anchorsByKey[key] = anchor;
        return true;
    }

    private static bool TryReplacePrimaryAnchor(Dictionary<DiffDocumentId, SemanticAnchor> anchorsByDocument, DiffDocumentId documentId, SemanticAnchor anchor)
    {
        if (anchorsByDocument.TryGetValue(documentId, out var existing) && ComparePrimaryAnchors(existing, anchor) <= 0)
        {
            return false;
        }

        anchorsByDocument[documentId] = anchor;
        return true;
    }

    private static int ComparePrimaryAnchors(SemanticAnchor left, SemanticAnchor right)
    {
        var priorityComparison = PrimaryAnchorPriority(left).CompareTo(PrimaryAnchorPriority(right));
        if (priorityComparison != 0)
        {
            return priorityComparison;
        }

        var lineComparison = left.Range.Line.CompareTo(right.Range.Line);
        return lineComparison != 0
            ? lineComparison
            : string.Compare(left.DisplayName, right.DisplayName, StringComparison.Ordinal);
    }

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
        if (!TryReadPathSegment(path.AsSpan(), 0, out var firstSegment, out var nextIndex))
        {
            return "Repository root";
        }

        if (!TryReadPathSegment(path.AsSpan(), nextIndex, out var secondSegment, out _))
        {
            return "Repository root";
        }

        return IsSourceLikeRoot(firstSegment)
            ? string.Concat(firstSegment, "/", secondSegment)
            : firstSegment.ToString();
    }

    private static bool TryReadPathSegment(ReadOnlySpan<char> path, int startIndex, out ReadOnlySpan<char> segment, out int nextIndex)
    {
        var index = startIndex;
        while (index < path.Length && (path[index] == '/' || path[index] == '\\'))
        {
            index++;
        }

        var segmentStart = index;
        while (index < path.Length && path[index] != '/' && path[index] != '\\')
        {
            index++;
        }

        segment = path[segmentStart..index].Trim();
        nextIndex = index;
        return segment.Length > 0;
    }

    private static bool IsSourceLikeRoot(ReadOnlySpan<char> segment) =>
        segment.Equals("src".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("samples".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("examples".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("tests".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("test".AsSpan(), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) => path.IndexOf('\\') < 0 ? path : path.Replace('\\', '/');

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

    private static void AddAnchorByKey(Dictionary<string, List<SemanticAnchor>> anchorsByKey, string key, SemanticAnchor anchor)
    {
        if (!anchorsByKey.TryGetValue(key, out var anchors))
        {
            anchors = [];
            anchorsByKey[key] = anchors;
        }

        anchors.Add(anchor);
    }

    private static bool HasMultipleDocuments(List<SemanticAnchor> anchors)
    {
        if (anchors.Count < 2)
        {
            return false;
        }

        var firstDocumentId = anchors[0].DocumentId;
        for (var index = 1; index < anchors.Count; index++)
        {
            if (anchors[index].DocumentId != firstDocumentId)
            {
                return true;
            }
        }

        return false;
    }

    private static void SortAnchorsByDocument(List<SemanticAnchor> anchors) => anchors.Sort(CompareAnchorsByDocumentId);

    private static int CompareAnchorsByDocumentId(SemanticAnchor left, SemanticAnchor right)
    {
        var documentComparison = string.Compare(left.DocumentId.Value, right.DocumentId.Value, StringComparison.Ordinal);
        return documentComparison != 0
            ? documentComparison
            : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
    }

    private static int CompareAnchorsByDocumentIdIgnoreCase(SemanticAnchor left, SemanticAnchor right)
    {
        var documentComparison = string.Compare(left.DocumentId.Value, right.DocumentId.Value, StringComparison.OrdinalIgnoreCase);
        return documentComparison != 0
            ? documentComparison
            : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
    }

    private static int CountMatchingOtherDocumentAnchors(List<SemanticAnchor> anchors, DiffDocumentId documentId, int limit)
    {
        var count = 0;
        foreach (var anchor in anchors)
        {
            if (anchor.DocumentId == documentId)
            {
                continue;
            }

            count++;
            if (count >= limit)
            {
                return count;
            }
        }

        return count;
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

    private sealed class SemanticAnchorInferenceIndex
    {
        private SemanticAnchorInferenceIndex()
        {
        }

        public Dictionary<string, SemanticAnchor> PrimaryAnchorsByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<DiffDocumentId, SemanticAnchor> PrimaryAnchorsByDocument { get; } = [];

        public Dictionary<string, List<SemanticAnchor>> CSharpTypesByName { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<SemanticAnchor>> XamlClassesByName { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<SemanticAnchor>> ResourceDefinitionsByName { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<SemanticAnchor>> ResourceReferencesByName { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<SemanticAnchor>> MembersByName { get; } = new(StringComparer.Ordinal);

        public List<SemanticAnchor> Bindings { get; } = [];

        public static SemanticAnchorInferenceIndex Create(ImmutableArray<SemanticAnchor> anchors)
        {
            var index = new SemanticAnchorInferenceIndex();
            foreach (var anchor in anchors)
            {
                TryReplacePrimaryAnchor(index.PrimaryAnchorsByPath, NormalizePath(anchor.DocumentId.Value), anchor);
                TryReplacePrimaryAnchor(index.PrimaryAnchorsByDocument, anchor.DocumentId, anchor);

                if (IsCSharpTypeAnchor(anchor))
                {
                    AddAnchorByKey(index.CSharpTypesByName, anchor.DisplayName, anchor);
                }
                else if (IsXamlClassAnchor(anchor))
                {
                    AddAnchorByKey(index.XamlClassesByName, anchor.DisplayName, anchor);
                }

                if (anchor.Kind == SemanticAnchorKind.Resource)
                {
                    if (IsXamlResourceDefinitionAnchor(anchor))
                    {
                        AddAnchorByKey(index.ResourceDefinitionsByName, anchor.DisplayName, anchor);
                    }
                    else if (IsXamlResourceReferenceAnchor(anchor))
                    {
                        AddAnchorByKey(index.ResourceReferencesByName, anchor.DisplayName, anchor);
                    }
                }
                else if (anchor.Kind == SemanticAnchorKind.Member)
                {
                    AddAnchorByKey(index.MembersByName, anchor.DisplayName, anchor);
                }
                else if (anchor.Kind == SemanticAnchorKind.Binding)
                {
                    index.Bindings.Add(anchor);
                }
            }

            return index;
        }
    }
}
