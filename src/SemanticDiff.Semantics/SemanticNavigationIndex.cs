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

    public string KindText { get; } = GetKindText(Kind);

    public string SearchText { get; } = $"{DisplayName} {Path} {GetKindText(Kind)} {(IsChanged ? "changed" : string.Empty)} {(IncidentEdgeCount > 0 ? "linked references" : string.Empty)}";

    private static string GetKindText(SemanticAnchorKind kind) => kind switch
    {
        SemanticAnchorKind.File => "File",
        SemanticAnchorKind.XamlRoot => "XAML",
        SemanticAnchorKind.XamlName => "Name",
        _ => kind.ToString()
    };
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
        var documentsWithNavigableSymbols = new HashSet<DiffDocumentId>();
        var fileAnchorsByDocumentId = new Dictionary<DiffDocumentId, SemanticAnchor>();

        foreach (var anchor in anchors)
        {
            if (anchor.Kind == SemanticAnchorKind.File && pathsByDocumentId.ContainsKey(anchor.DocumentId))
            {
                fileAnchorsByDocumentId.TryAdd(anchor.DocumentId, anchor);
            }

            if (!IsNavigable(anchor.Kind) || !pathsByDocumentId.TryGetValue(anchor.DocumentId, out var path))
            {
                continue;
            }

            documentsWithNavigableSymbols.Add(anchor.DocumentId);
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

        var items = builder.ToArray();
        Array.Sort(items, CompareNavigationItems);
        return items.ToImmutableArray();
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

    private static int CompareNavigationItems(SemanticNavigationItem left, SemanticNavigationItem right)
    {
        var kindComparison = KindSortOrder(left.Kind).CompareTo(KindSortOrder(right.Kind));
        if (kindComparison != 0)
        {
            return kindComparison;
        }

        var pathComparison = string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        if (pathComparison != 0)
        {
            return pathComparison;
        }

        var lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0
            ? lineComparison
            : string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
    }
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

        var kindFacetsByKind = new Dictionary<SemanticAnchorKind, KindFacetAccumulator>();
        var documentFacetsById = new Dictionary<DiffDocumentId, DocumentFacetAccumulator>();
        var hotSymbolCandidates = new List<SemanticNavigationItem>();
        var changedCount = 0;
        var linkedCount = 0;

        foreach (var symbol in symbols)
        {
            if (!kindFacetsByKind.TryGetValue(symbol.Kind, out var kindFacet))
            {
                kindFacet = new KindFacetAccumulator(symbol.Kind, symbol.KindText);
                kindFacetsByKind[symbol.Kind] = kindFacet;
            }

            kindFacet.Add(symbol);

            if (!documentFacetsById.TryGetValue(symbol.DocumentId, out var documentFacet))
            {
                documentFacet = new DocumentFacetAccumulator(symbol.DocumentId, symbol.Path);
                documentFacetsById[symbol.DocumentId] = documentFacet;
            }

            documentFacet.Add(symbol);

            if (symbol.IsChanged)
            {
                changedCount++;
            }

            if (symbol.IsLinked)
            {
                linkedCount++;
            }

            if (symbol.IsChanged || symbol.IsLinked)
            {
                hotSymbolCandidates.Add(symbol);
            }
        }

        var kindFacetArray = new SemanticSymbolKindFacet[kindFacetsByKind.Count];
        var kindFacetIndex = 0;
        foreach (var accumulator in kindFacetsByKind.Values)
        {
            kindFacetArray[kindFacetIndex++] = accumulator.ToFacet();
        }

        Array.Sort(kindFacetArray, CompareKindFacets);

        var documentFacetArray = new SemanticSymbolDocumentFacet[documentFacetsById.Count];
        var documentFacetIndex = 0;
        foreach (var accumulator in documentFacetsById.Values)
        {
            documentFacetArray[documentFacetIndex++] = accumulator.ToFacet();
        }

        Array.Sort(documentFacetArray, CompareDocumentFacets);
        hotSymbolCandidates.Sort(CompareHotSymbols);
        var hotSymbolCount = Math.Min(12, hotSymbolCandidates.Count);
        var hotSymbolBuilder = ImmutableArray.CreateBuilder<SemanticNavigationItem>(hotSymbolCount);
        for (var index = 0; index < hotSymbolCount; index++)
        {
            hotSymbolBuilder.Add(hotSymbolCandidates[index]);
        }

        return new SemanticSymbolInsightSummary(
            symbols.Length,
            changedCount,
            linkedCount,
            documentFacetArray.Length,
            kindFacetArray.ToImmutableArray(),
            documentFacetArray.ToImmutableArray(),
            hotSymbolBuilder.ToImmutable());
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

    private static int CompareKindFacets(SemanticSymbolKindFacet left, SemanticSymbolKindFacet right)
    {
        var changedComparison = right.ChangedCount.CompareTo(left.ChangedCount);
        if (changedComparison != 0)
        {
            return changedComparison;
        }

        var linkedComparison = right.LinkedCount.CompareTo(left.LinkedCount);
        if (linkedComparison != 0)
        {
            return linkedComparison;
        }

        var kindComparison = KindSortOrder(left.Kind).CompareTo(KindSortOrder(right.Kind));
        return kindComparison != 0
            ? kindComparison
            : string.Compare(left.KindText, right.KindText, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareDocumentFacets(SemanticSymbolDocumentFacet left, SemanticSymbolDocumentFacet right)
    {
        var changedComparison = right.ChangedCount.CompareTo(left.ChangedCount);
        if (changedComparison != 0)
        {
            return changedComparison;
        }

        var linkedComparison = right.LinkedCount.CompareTo(left.LinkedCount);
        if (linkedComparison != 0)
        {
            return linkedComparison;
        }

        var countComparison = right.Count.CompareTo(left.Count);
        return countComparison != 0
            ? countComparison
            : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareHotSymbols(SemanticNavigationItem left, SemanticNavigationItem right)
    {
        var changedComparison = right.IsChanged.CompareTo(left.IsChanged);
        if (changedComparison != 0)
        {
            return changedComparison;
        }

        var incidentComparison = right.IncidentEdgeCount.CompareTo(left.IncidentEdgeCount);
        if (incidentComparison != 0)
        {
            return incidentComparison;
        }

        var kindComparison = KindSortOrder(left.Kind).CompareTo(KindSortOrder(right.Kind));
        if (kindComparison != 0)
        {
            return kindComparison;
        }

        var pathComparison = string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        if (pathComparison != 0)
        {
            return pathComparison;
        }

        var lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0
            ? lineComparison
            : string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class KindFacetAccumulator(SemanticAnchorKind kind, string kindText)
    {
        private int count;
        private int changedCount;
        private int linkedCount;

        public void Add(SemanticNavigationItem symbol)
        {
            count++;
            if (symbol.IsChanged)
            {
                changedCount++;
            }

            if (symbol.IsLinked)
            {
                linkedCount++;
            }
        }

        public SemanticSymbolKindFacet ToFacet() => new(kind, kindText, count, changedCount, linkedCount);
    }

    private sealed class DocumentFacetAccumulator(DiffDocumentId documentId, string path)
    {
        private int count;
        private int changedCount;
        private int linkedCount;

        public void Add(SemanticNavigationItem symbol)
        {
            count++;
            if (symbol.IsChanged)
            {
                changedCount++;
            }

            if (symbol.IsLinked)
            {
                linkedCount++;
            }
        }

        public SemanticSymbolDocumentFacet ToFacet() => new(documentId, path, count, changedCount, linkedCount);
    }
}
