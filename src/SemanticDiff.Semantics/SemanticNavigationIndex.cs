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
    int IncidentEdgeCount,
    bool IsChanged = false)
{
    public bool IsLinked => IncidentEdgeCount > 0;

    public string KindText => Kind switch
    {
        SemanticAnchorKind.File => "File",
        SemanticAnchorKind.XamlRoot => "XAML",
        SemanticAnchorKind.XamlName => "Name",
        _ => Kind.ToString()
    };

    public string SearchText => $"{DisplayName} {Path} {KindText} {(IsChanged ? "changed" : string.Empty)} {(IsLinked ? "linked references" : string.Empty)}";
}

public sealed class SemanticNavigationIndex
{
    public ImmutableArray<SemanticNavigationItem> Build(SemanticGraph graph, ImmutableArray<DiffDocumentSnapshot> documents)
    {
        if (documents.IsDefaultOrEmpty)
        {
            return ImmutableArray<SemanticNavigationItem>.Empty;
        }

        var anchors = graph.Anchors.IsDefault ? ImmutableArray<SemanticAnchor>.Empty : graph.Anchors;
        var edges = graph.Edges.IsDefault ? ImmutableArray<SemanticEdge>.Empty : graph.Edges;
        var pathsByDocumentId = documents.ToDictionary(document => document.Id, document => document.Metadata.Path);
        var edgeCounts = BuildIncidentEdgeCounts(edges);
        var changedLineIndex = new SemanticChangeLineIndexBuilder().Build(documents);
        var builder = ImmutableArray.CreateBuilder<SemanticNavigationItem>();

        foreach (var anchor in anchors)
        {
            if (!IsNavigable(anchor.Kind) || !pathsByDocumentId.TryGetValue(anchor.DocumentId, out var path))
            {
                continue;
            }

            builder.Add(new SemanticNavigationItem(
                anchor.Id,
                anchor.DocumentId,
                path,
                anchor.Kind,
                anchor.DisplayName,
                Math.Max(1, anchor.Range.Line),
                Math.Max(1, anchor.Range.Column),
                edgeCounts.GetValueOrDefault(anchor.Id),
                changedLineIndex.Contains(anchor)));
        }

        var documentsWithNavigableSymbols = builder
            .Select(item => item.DocumentId)
            .ToHashSet();
        var fileAnchorsByDocumentId = anchors
            .Where(anchor => anchor.Kind == SemanticAnchorKind.File && pathsByDocumentId.ContainsKey(anchor.DocumentId))
            .GroupBy(anchor => anchor.DocumentId)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var document in documents)
        {
            if (documentsWithNavigableSymbols.Contains(document.Id))
            {
                continue;
            }

            var path = pathsByDocumentId[document.Id];
            if (fileAnchorsByDocumentId.TryGetValue(document.Id, out var fileAnchor))
            {
                builder.Add(CreateFileNavigationItem(fileAnchor, path, edgeCounts.GetValueOrDefault(fileAnchor.Id), IsChangedDocument(document, changedLineIndex)));
            }
            else
            {
                builder.Add(new SemanticNavigationItem(
                    $"{document.Id.Value}:file:fallback",
                    document.Id,
                    path,
                    SemanticAnchorKind.File,
                    GetFileDisplayName(path),
                    1,
                    1,
                    0,
                    IsChangedDocument(document, changedLineIndex)));
            }
        }

        return builder
            .OrderBy(item => KindSortOrder(item.Kind))
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Line)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static SemanticNavigationItem CreateFileNavigationItem(SemanticAnchor anchor, string path, int incidentEdgeCount, bool isChanged) => new(
        anchor.Id,
        anchor.DocumentId,
        path,
        SemanticAnchorKind.File,
        GetFileDisplayName(path),
        Math.Max(1, anchor.Range.Line),
        Math.Max(1, anchor.Range.Column),
        incidentEdgeCount,
        isChanged);

    private static bool IsChangedDocument(DiffDocumentSnapshot document, SemanticChangedLineIndex changedLineIndex) =>
        changedLineIndex.ChangedLinesByDocumentId.ContainsKey(document.Id) ||
        document.Metadata.Status != DiffFileStatus.Unchanged;

    private static string GetFileDisplayName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex >= 0 && separatorIndex + 1 < normalized.Length
            ? normalized[(separatorIndex + 1)..]
            : normalized;
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
        SemanticAnchorKind.File => 6,
        _ => 9
    };
}

public sealed record SemanticSymbolKindFacet(
    SemanticAnchorKind Kind,
    string KindText,
    int Count,
    int ChangedCount,
    int LinkedCount);

public sealed record SemanticSymbolDocumentFacet(
    DiffDocumentId DocumentId,
    string Path,
    int Count,
    int ChangedCount,
    int LinkedCount);

public sealed record SemanticSymbolInsightSummary(
    int TotalSymbolCount,
    int ChangedSymbolCount,
    int LinkedSymbolCount,
    int DocumentCount,
    ImmutableArray<SemanticSymbolKindFacet> KindFacets,
    ImmutableArray<SemanticSymbolDocumentFacet> DocumentFacets,
    ImmutableArray<SemanticNavigationItem> HotSymbols)
{
    public static SemanticSymbolInsightSummary Empty { get; } = new(
        0,
        0,
        0,
        0,
        ImmutableArray<SemanticSymbolKindFacet>.Empty,
        ImmutableArray<SemanticSymbolDocumentFacet>.Empty,
        ImmutableArray<SemanticNavigationItem>.Empty);
}

public sealed class SemanticSymbolInsightIndex
{
    public SemanticSymbolInsightSummary Build(ImmutableArray<SemanticNavigationItem> symbols)
    {
        if (symbols.IsDefaultOrEmpty)
        {
            return SemanticSymbolInsightSummary.Empty;
        }

        var kindFacets = symbols
            .GroupBy(symbol => symbol.Kind)
            .Select(group => new SemanticSymbolKindFacet(
                group.Key,
                group.First().KindText,
                group.Count(),
                group.Count(symbol => symbol.IsChanged),
                group.Count(symbol => symbol.IsLinked)))
            .OrderByDescending(facet => facet.ChangedCount)
            .ThenByDescending(facet => facet.LinkedCount)
            .ThenBy(facet => KindSortOrder(facet.Kind))
            .ThenBy(facet => facet.KindText, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var documentFacets = symbols
            .GroupBy(symbol => symbol.DocumentId)
            .Select(group => new SemanticSymbolDocumentFacet(
                group.Key,
                group.First().Path,
                group.Count(),
                group.Count(symbol => symbol.IsChanged),
                group.Count(symbol => symbol.IsLinked)))
            .OrderByDescending(facet => facet.ChangedCount)
            .ThenByDescending(facet => facet.LinkedCount)
            .ThenByDescending(facet => facet.Count)
            .ThenBy(facet => facet.Path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var hotSymbols = symbols
            .Where(symbol => symbol.IsChanged || symbol.IsLinked)
            .OrderByDescending(symbol => symbol.IsChanged)
            .ThenByDescending(symbol => symbol.IncidentEdgeCount)
            .ThenBy(symbol => KindSortOrder(symbol.Kind))
            .ThenBy(symbol => symbol.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(symbol => symbol.Line)
            .ThenBy(symbol => symbol.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToImmutableArray();

        return new SemanticSymbolInsightSummary(
            symbols.Length,
            symbols.Count(symbol => symbol.IsChanged),
            symbols.Count(symbol => symbol.IsLinked),
            documentFacets.Length,
            kindFacets,
            documentFacets,
            hotSymbols);
    }

    private static int KindSortOrder(SemanticAnchorKind kind) => kind switch
    {
        SemanticAnchorKind.Type => 0,
        SemanticAnchorKind.Member => 1,
        SemanticAnchorKind.XamlRoot => 2,
        SemanticAnchorKind.XamlName => 3,
        SemanticAnchorKind.Resource => 4,
        SemanticAnchorKind.Binding => 5,
        SemanticAnchorKind.File => 6,
        _ => 9
    };
}
