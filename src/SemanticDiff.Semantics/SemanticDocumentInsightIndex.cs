using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Semantics;

public sealed class SemanticDocumentInsightIndex
{
    public ImmutableArray<SemanticDocumentInsight> Build(SemanticGraph graph, ImmutableArray<DiffDocumentSnapshot> documents)
    {
        if (documents.IsDefaultOrEmpty)
        {
            return ImmutableArray<SemanticDocumentInsight>.Empty;
        }

        var anchors = graph.Anchors.IsDefault ? ImmutableArray<SemanticAnchor>.Empty : graph.Anchors;
        var edges = graph.Edges.IsDefault ? ImmutableArray<SemanticEdge>.Empty : graph.Edges;
        var documentsById = documents.ToDictionary(document => document.Id);
        var anchorsById = anchors
            .GroupBy(anchor => anchor.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var edgeCounts = BuildIncidentEdgeCounts(edges);
        var incidentEdges = BuildIncidentEdges(edges);
        var changedLineIndex = new SemanticChangeLineIndexBuilder().Build(documents);
        var changedAnchorIds = anchors
            .Where(anchor => IsImpactAnchor(anchor.Kind))
            .Where(changedLineIndex.Contains)
            .Select(anchor => anchor.Id)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var impactedEdgeIds = edges
            .Where(edge => changedAnchorIds.Contains(edge.SourceAnchorId) || changedAnchorIds.Contains(edge.TargetAnchorId))
            .Select(edge => edge.Id)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var anchorsByDocument = anchors
            .Where(anchor => documentsById.ContainsKey(anchor.DocumentId))
            .Where(anchor => IsLineInsightAnchor(anchor.Kind) || IsDiagnosticAnchor(anchor))
            .GroupBy(anchor => anchor.DocumentId)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray());

        var builder = ImmutableArray.CreateBuilder<SemanticDocumentInsight>(documents.Length);
        foreach (var document in documents)
        {
            anchorsByDocument.TryGetValue(document.Id, out var documentAnchors);
            if (documentAnchors.IsDefault)
            {
                documentAnchors = ImmutableArray<SemanticAnchor>.Empty;
            }

            var documentAnchorIds = documentAnchors
                .Select(anchor => anchor.Id)
                .ToHashSet(StringComparer.Ordinal);
            var changedAnchorCount = documentAnchors.Count(anchor => changedAnchorIds.Contains(anchor.Id));
            var linkedAnchorCount = documentAnchors.Count(anchor => edgeCounts.GetValueOrDefault(anchor.Id) > 0);
            var impactedEdgeCount = edges.Count(edge =>
                impactedEdgeIds.Contains(edge.Id) &&
                (documentAnchorIds.Contains(edge.SourceAnchorId) || documentAnchorIds.Contains(edge.TargetAnchorId)));
            var lines = BuildLineInsights(documentAnchors, edgeCounts, incidentEdges, anchorsById, changedAnchorIds, impactedEdgeIds);

            builder.Add(new SemanticDocumentInsight(
                document.Id,
                documentAnchors.Length,
                changedAnchorCount,
                linkedAnchorCount,
                impactedEdgeCount,
                lines));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<SemanticLineInsight> BuildLineInsights(
        ImmutableArray<SemanticAnchor> anchors,
        IReadOnlyDictionary<string, int> edgeCounts,
        IReadOnlyDictionary<string, ImmutableArray<SemanticEdge>> incidentEdges,
        IReadOnlyDictionary<string, SemanticAnchor> anchorsById,
        ImmutableHashSet<string> changedAnchorIds,
        ImmutableHashSet<string> impactedEdgeIds)
    {
        if (anchors.IsDefaultOrEmpty)
        {
            return ImmutableArray<SemanticLineInsight>.Empty;
        }

        return anchors
            .GroupBy(anchor => Math.Max(1, anchor.Range.Line))
            .OrderBy(group => group.Key)
            .Select(group => CreateLineInsight(group.Key, group.ToImmutableArray(), edgeCounts, incidentEdges, anchorsById, changedAnchorIds, impactedEdgeIds))
            .ToImmutableArray();
    }

    private static SemanticLineInsight CreateLineInsight(
        int lineNumber,
        ImmutableArray<SemanticAnchor> anchors,
        IReadOnlyDictionary<string, int> edgeCounts,
        IReadOnlyDictionary<string, ImmutableArray<SemanticEdge>> incidentEdges,
        IReadOnlyDictionary<string, SemanticAnchor> anchorsById,
        ImmutableHashSet<string> changedAnchorIds,
        ImmutableHashSet<string> impactedEdgeIds)
    {
        var dominant = anchors
            .OrderBy(anchor => KindSortOrder(anchor.Kind))
            .ThenBy(anchor => anchor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .First();
        var isDiagnostic = anchors.Any(IsDiagnosticAnchor);
        var isChanged = anchors.Any(anchor => changedAnchorIds.Contains(anchor.Id));
        var linkCount = anchors.Sum(anchor => edgeCounts.GetValueOrDefault(anchor.Id));
        var impactedEdges = anchors
            .SelectMany(anchor => incidentEdges.TryGetValue(anchor.Id, out var edges) ? edges : ImmutableArray<SemanticEdge>.Empty)
            .Where(edge => impactedEdgeIds.Contains(edge.Id))
            .DistinctBy(edge => edge.Id)
            .ToImmutableArray();
        var isImpacted = isChanged || impactedEdges.Length > 0;
        var label = isDiagnostic
            ? "parse"
            : isChanged
                ? "impact"
                : anchors.Length > 1
                    ? $"{anchors.Length:N0} sem"
                    : SemanticLabel(dominant.Kind);
        var detail = FormatDetail(anchors, dominant, linkCount, isChanged, impactedEdges, anchorsById);

        return new SemanticLineInsight(
            lineNumber,
            label,
            detail,
            isDiagnostic ? SemanticAnchorKind.Unknown : dominant.Kind,
            anchors.Length,
            linkCount,
            isChanged,
            isImpacted);
    }

    private static string FormatDetail(
        ImmutableArray<SemanticAnchor> anchors,
        SemanticAnchor dominant,
        int linkCount,
        bool isChanged,
        ImmutableArray<SemanticEdge> impactedEdges,
        IReadOnlyDictionary<string, SemanticAnchor> anchorsById)
    {
        if (anchors.Any(IsDiagnosticAnchor))
        {
            return string.Join(" | ", anchors.Where(IsDiagnosticAnchor).Select(anchor => anchor.DisplayName).Distinct(StringComparer.Ordinal).Take(3));
        }

        var names = anchors
            .Select(anchor => $"{SemanticLabel(anchor.Kind)} {anchor.DisplayName}")
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        var nameText = names.Length == 0 ? dominant.DisplayName : string.Join(", ", names);
        var parts = new List<string> { isChanged ? $"Changed semantic context: {nameText}" : $"Semantic context: {nameText}" };

        if (anchors.Length > names.Length)
        {
            parts.Add($"+{anchors.Length - names.Length:N0} more anchors");
        }

        if (linkCount > 0)
        {
            parts.Add(linkCount == 1 ? "1 semantic link" : $"{linkCount:N0} semantic links");
        }

        if (!impactedEdges.IsDefaultOrEmpty)
        {
            var edgeText = impactedEdges
                .Take(3)
                .Select(edge => FormatEdge(edge, anchorsById))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (edgeText.Length > 0)
            {
                parts.Add($"Impacted by {string.Join(", ", edgeText)}");
            }
        }

        return string.Join(" | ", parts);
    }

    private static string FormatEdge(SemanticEdge edge, IReadOnlyDictionary<string, SemanticAnchor> anchorsById)
    {
        var source = anchorsById.TryGetValue(edge.SourceAnchorId, out var sourceAnchor) ? sourceAnchor.DisplayName : edge.SourceAnchorId;
        var target = anchorsById.TryGetValue(edge.TargetAnchorId, out var targetAnchor) ? targetAnchor.DisplayName : edge.TargetAnchorId;
        var label = string.IsNullOrWhiteSpace(edge.Label) ? edge.Kind.ToString() : edge.Label;
        return $"{source} -> {target} ({label})";
    }

    private static Dictionary<string, int> BuildIncidentEdgeCounts(ImmutableArray<SemanticEdge> edges)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            counts[edge.SourceAnchorId] = counts.GetValueOrDefault(edge.SourceAnchorId) + 1;
            counts[edge.TargetAnchorId] = counts.GetValueOrDefault(edge.TargetAnchorId) + 1;
        }

        return counts;
    }

    private static Dictionary<string, ImmutableArray<SemanticEdge>> BuildIncidentEdges(ImmutableArray<SemanticEdge> edges)
    {
        var builders = new Dictionary<string, ImmutableArray<SemanticEdge>.Builder>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            Add(edge.SourceAnchorId, edge);
            Add(edge.TargetAnchorId, edge);
        }

        return builders.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToImmutable(),
            StringComparer.Ordinal);

        void Add(string anchorId, SemanticEdge edge)
        {
            if (!builders.TryGetValue(anchorId, out var builder))
            {
                builder = ImmutableArray.CreateBuilder<SemanticEdge>();
                builders[anchorId] = builder;
            }

            builder.Add(edge);
        }
    }

    private static bool IsLineInsightAnchor(SemanticAnchorKind kind) => kind is
        SemanticAnchorKind.Namespace or
        SemanticAnchorKind.Type or
        SemanticAnchorKind.Member or
        SemanticAnchorKind.XamlRoot or
        SemanticAnchorKind.XamlName or
        SemanticAnchorKind.Resource or
        SemanticAnchorKind.Binding;

    private static bool IsImpactAnchor(SemanticAnchorKind kind) => kind is
        SemanticAnchorKind.Type or
        SemanticAnchorKind.Member or
        SemanticAnchorKind.XamlRoot or
        SemanticAnchorKind.XamlName or
        SemanticAnchorKind.Resource or
        SemanticAnchorKind.Binding;

    private static bool IsDiagnosticAnchor(SemanticAnchor anchor) =>
        anchor.Kind == SemanticAnchorKind.Unknown ||
        anchor.DisplayName.StartsWith("XML parse error:", StringComparison.Ordinal);

    private static int KindSortOrder(SemanticAnchorKind kind) => kind switch
    {
        SemanticAnchorKind.Type => 0,
        SemanticAnchorKind.Member => 1,
        SemanticAnchorKind.XamlRoot => 2,
        SemanticAnchorKind.XamlName => 3,
        SemanticAnchorKind.Resource => 4,
        SemanticAnchorKind.Binding => 5,
        SemanticAnchorKind.Namespace => 6,
        SemanticAnchorKind.File => 7,
        SemanticAnchorKind.Project => 8,
        SemanticAnchorKind.Unknown => 9,
        _ => 10
    };

    private static string SemanticLabel(SemanticAnchorKind kind) => kind switch
    {
        SemanticAnchorKind.XamlRoot => "xaml",
        SemanticAnchorKind.XamlName => "name",
        SemanticAnchorKind.Resource => "res",
        SemanticAnchorKind.Binding => "bind",
        SemanticAnchorKind.Member => "member",
        SemanticAnchorKind.Type => "type",
        SemanticAnchorKind.Namespace => "ns",
        SemanticAnchorKind.Project => "proj",
        SemanticAnchorKind.File => "file",
        _ => "sem"
    };
}
