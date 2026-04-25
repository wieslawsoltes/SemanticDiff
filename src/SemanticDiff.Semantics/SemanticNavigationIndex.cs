using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Semantics;

public sealed record SemanticNavigationItem(
    string AnchorId,
    DiffDocumentId DocumentId,
    string Path,
    SemanticAnchorKind Kind,
    string DisplayName,
    int Line,
    int Column,
    int IncidentEdgeCount)
{
    public string KindText => Kind switch
    {
        SemanticAnchorKind.XamlRoot => "XAML",
        SemanticAnchorKind.XamlName => "Name",
        _ => Kind.ToString()
    };

    public string SearchText => $"{DisplayName} {Path} {KindText}";
}

public sealed class SemanticNavigationIndex
{
    public ImmutableArray<SemanticNavigationItem> Build(SemanticGraph graph, ImmutableArray<DiffDocumentSnapshot> documents)
    {
        if (graph.Anchors.IsDefaultOrEmpty || documents.IsDefaultOrEmpty)
        {
            return ImmutableArray<SemanticNavigationItem>.Empty;
        }

        var pathsByDocumentId = documents.ToDictionary(document => document.Id, document => document.Metadata.Path);
        var edgeCounts = BuildIncidentEdgeCounts(graph.Edges);

        return graph.Anchors
            .Where(anchor => IsNavigable(anchor.Kind) && pathsByDocumentId.ContainsKey(anchor.DocumentId))
            .Select(anchor => new SemanticNavigationItem(
                anchor.Id,
                anchor.DocumentId,
                pathsByDocumentId[anchor.DocumentId],
                anchor.Kind,
                anchor.DisplayName,
                Math.Max(1, anchor.Range.Line),
                Math.Max(1, anchor.Range.Column),
                edgeCounts.GetValueOrDefault(anchor.Id)))
            .OrderBy(item => KindSortOrder(item.Kind))
            .ThenBy(item => pathsByDocumentId[item.DocumentId], StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Line)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
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

    private static bool IsNavigable(SemanticAnchorKind kind) => kind is
        SemanticAnchorKind.Type or
        SemanticAnchorKind.Member or
        SemanticAnchorKind.XamlRoot or
        SemanticAnchorKind.XamlName or
        SemanticAnchorKind.Resource or
        SemanticAnchorKind.Binding;

    private static int KindSortOrder(SemanticAnchorKind kind) => kind switch
    {
        SemanticAnchorKind.Type => 0,
        SemanticAnchorKind.Member => 1,
        SemanticAnchorKind.XamlRoot => 2,
        SemanticAnchorKind.XamlName => 3,
        SemanticAnchorKind.Resource => 4,
        SemanticAnchorKind.Binding => 5,
        _ => 9
    };
}