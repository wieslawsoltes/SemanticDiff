using System.Collections.Immutable;
using System.Text;
using SemanticDiff.Core;
using SemanticDiff.Rendering;
using SemanticDiff.Semantics;

namespace SemanticDiff.Workbench.Symbols;

public enum SymbolGraphViewMode
{
    SymbolsOnly,
    FilesAndSymbols
}

public sealed record SymbolGraphSceneBuildRequest(
    ImmutableArray<SemanticNavigationItem> Items,
    SemanticGraph SourceGraph,
    ImmutableArray<DiffDocumentSnapshot> SourceDocuments,
    GraphLayoutMode LayoutMode,
    GraphGroupingMode GroupingMode,
    SymbolGraphViewMode ViewMode,
    SemanticEdgeKind? EdgeKind = null);

public sealed record SymbolGraphSceneBuildResult(DiffCanvasScene Scene, int FileCount);

public sealed class SymbolGraphSceneBuilder
{
    public SymbolGraphSceneBuildResult Build(SymbolGraphSceneBuildRequest request)
    {
        var items = request.Items.IsDefault ? ImmutableArray<SemanticNavigationItem>.Empty : request.Items;
        return request.ViewMode == SymbolGraphViewMode.FilesAndSymbols
            ? BuildFileSymbolScene(request with { Items = items })
            : BuildSymbolOnlyScene(request with { Items = items });
    }

    private static SymbolGraphSceneBuildResult BuildSymbolOnlyScene(SymbolGraphSceneBuildRequest request)
    {
        var items = request.Items;
        if (items.IsDefaultOrEmpty)
        {
            return new SymbolGraphSceneBuildResult(DiffCanvasScene.FromDocuments([]), 0);
        }

        var sourceDocumentsById = request.SourceDocuments.ToDictionary(document => document.Id.Value, StringComparer.Ordinal);
        var selectedAnchorIds = items.Select(item => item.AnchorId).ToHashSet(StringComparer.Ordinal);
        var documents = ImmutableArray.CreateBuilder<DiffDocumentSnapshot>(items.Length);
        var anchors = ImmutableArray.CreateBuilder<SemanticAnchor>(items.Length);

        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            sourceDocumentsById.TryGetValue(item.DocumentId.Value, out var sourceDocument);
            var documentId = SymbolGraphDocumentIds.Create(item.DocumentId, item.AnchorId);
            documents.Add(CreateSymbolDocument(documentId, item, sourceDocument, index));
            anchors.Add(new SemanticAnchor(item.AnchorId, documentId, new TextRange(0, 1, Math.Max(1, item.Line), 1), item.Kind, item.DisplayName));
        }

        var edges = request.SourceGraph.Edges
            .Where(edge => selectedAnchorIds.Contains(edge.SourceAnchorId) && selectedAnchorIds.Contains(edge.TargetAnchorId))
            .Where(edge => request.EdgeKind is null || edge.Kind == request.EdgeKind)
            .ToImmutableArray();
        var symbolGraph = new SemanticGraph(anchors.ToImmutable(), edges);
        var layout = CreateLayout(documents.ToImmutable(), items, edges, request.LayoutMode);
        var scene = DiffCanvasScene.FromDocuments(
            documents.ToImmutable(),
            symbolGraph,
            layout,
            new EdgeProjectionOptions(MinimumConfidence: 0, MaxEdgesPerDocumentPair: 4),
            groupingMode: request.GroupingMode);
        return new SymbolGraphSceneBuildResult(scene, 0);
    }

    private static SymbolGraphSceneBuildResult BuildFileSymbolScene(SymbolGraphSceneBuildRequest request)
    {
        var items = request.Items;
        if (items.IsDefaultOrEmpty)
        {
            return new SymbolGraphSceneBuildResult(DiffCanvasScene.FromDocuments([]), 0);
        }

        var sourceDocumentsById = request.SourceDocuments.ToDictionary(document => document.Id.Value, StringComparer.Ordinal);
        var selectedAnchorIds = items.Select(item => item.AnchorId).ToHashSet(StringComparer.Ordinal);
        var selectedDocuments = items
            .Select(item => item.DocumentId.Value)
            .Distinct(StringComparer.Ordinal)
            .Select(documentId => sourceDocumentsById.TryGetValue(documentId, out var document) ? document : null)
            .OfType<DiffDocumentSnapshot>()
            .OrderBy(document => document.Metadata.Path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var documents = ImmutableArray.CreateBuilder<DiffDocumentSnapshot>(selectedDocuments.Length + items.Length);
        documents.AddRange(selectedDocuments);

        var anchors = ImmutableArray.CreateBuilder<SemanticAnchor>(selectedDocuments.Length + items.Length);
        var fileAnchorIdsByDocumentId = new Dictionary<DiffDocumentId, string>();
        foreach (var document in selectedDocuments)
        {
            var anchorId = SymbolGraphDocumentIds.CreateFileAnchorId(document.Id);
            fileAnchorIdsByDocumentId[document.Id] = anchorId;
            anchors.Add(new SemanticAnchor(
                anchorId,
                document.Id,
                new TextRange(0, 1, 1, 1),
                SemanticAnchorKind.File,
                Path.GetFileName(document.Metadata.Path)));
        }

        var symbolDocumentIdsByAnchorId = new Dictionary<string, DiffDocumentId>(StringComparer.Ordinal);
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            sourceDocumentsById.TryGetValue(item.DocumentId.Value, out var sourceDocument);
            var documentId = SymbolGraphDocumentIds.Create(item.DocumentId, item.AnchorId);
            symbolDocumentIdsByAnchorId[item.AnchorId] = documentId;
            documents.Add(CreateSymbolDocument(documentId, item, sourceDocument, index));
            anchors.Add(new SemanticAnchor(item.AnchorId, documentId, new TextRange(0, 1, Math.Max(1, item.Line), 1), item.Kind, item.DisplayName));
        }

        var edges = ImmutableArray.CreateBuilder<SemanticEdge>();
        foreach (var item in items)
        {
            if (!fileAnchorIdsByDocumentId.TryGetValue(item.DocumentId, out var fileAnchorId))
            {
                continue;
            }

            edges.Add(new SemanticEdge(
                $"contains:{fileAnchorId}:{item.AnchorId}",
                fileAnchorId,
                item.AnchorId,
                SemanticEdgeKind.Contains,
                1,
                "declares"));
        }

        if (request.EdgeKind != SemanticEdgeKind.Contains)
        {
            edges.AddRange(request.SourceGraph.Edges
                .Where(edge => selectedAnchorIds.Contains(edge.SourceAnchorId) && selectedAnchorIds.Contains(edge.TargetAnchorId))
                .Where(edge => request.EdgeKind is null || edge.Kind == request.EdgeKind));
        }

        var hybridGraph = new SemanticGraph(anchors.ToImmutable(), edges.ToImmutable());
        var layout = CreateFileSymbolLayout(selectedDocuments, items, symbolDocumentIdsByAnchorId, request.LayoutMode);
        var scene = DiffCanvasScene.FromDocuments(
            documents.ToImmutable(),
            hybridGraph,
            layout,
            new EdgeProjectionOptions(MinimumConfidence: 0, MaxEdgesPerDocumentPair: 6),
            groupingMode: request.GroupingMode);
        return new SymbolGraphSceneBuildResult(scene, selectedDocuments.Length);
    }

    private static DiffDocumentSnapshot CreateSymbolDocument(
        DiffDocumentId documentId,
        SemanticNavigationItem item,
        DiffDocumentSnapshot? sourceDocument,
        int index)
    {
        var sourceLine = sourceDocument?.Lines.FirstOrDefault(line => line.NewLineNumber == item.Line || line.OldLineNumber == item.Line);
        var sourceText = sourceLine is null ? string.Empty : sourceLine.Text.Trim();
        var signal = item switch
        {
            { IsChanged: true, IsLinked: true } => "changed + linked",
            { IsChanged: true } => "changed",
            { IsLinked: true } => "linked",
            _ => "symbol"
        };
        var metadata = new DiffDocumentMetadata(
            documentId,
            $"{item.KindText}/{ShortenForPath(item.DisplayName, 56)}",
            item.Path,
            item.IsChanged ? DiffFileStatus.Modified : DiffFileStatus.Unchanged,
            string.IsNullOrWhiteSpace(sourceDocument?.Metadata.Language) ? item.KindText : sourceDocument.Metadata.Language,
            item.IsChanged ? 1 : 0,
            0);
        var lines = ImmutableArray.CreateBuilder<DiffLine>();
        AddLine(lines, 0, DiffLineKind.Metadata, $"symbol {index + 1}: {item.DisplayName}");
        AddLine(lines, 1, DiffLineKind.Metadata, $"kind: {item.KindText}");
        AddLine(lines, 2, DiffLineKind.Context, $"file: {item.Path}:{item.Line}");
        AddLine(lines, 3, item.IsChanged ? DiffLineKind.Modified : DiffLineKind.Context, $"signal: {signal}");
        AddLine(lines, 4, item.IsLinked ? DiffLineKind.Modified : DiffLineKind.Context, $"links: {item.IncidentEdgeCount:N0}");
        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            AddLine(lines, 5, DiffLineKind.Context, sourceText);
        }

        return new DiffDocumentSnapshot(documentId, metadata, lines.ToImmutable());
    }

    private static void AddLine(ImmutableArray<DiffLine>.Builder lines, int index, DiffLineKind kind, string text) =>
        lines.Add(new DiffLine(index, index + 1, index + 1, kind, text, ImmutableArray<TokenSpan>.Empty));

    private static GraphLayoutResult CreateLayout(
        ImmutableArray<DiffDocumentSnapshot> documents,
        ImmutableArray<SemanticNavigationItem> items,
        ImmutableArray<SemanticEdge> edges,
        GraphLayoutMode layoutMode)
    {
        var mode = layoutMode == GraphLayoutMode.Auto
            ? documents.Length > 90 ? GraphLayoutMode.CompactGrid : GraphLayoutMode.Layered
            : layoutMode;
        var nodeWidth = mode == GraphLayoutMode.CompactGrid ? 380 : 460;
        var nodeHeight = mode == GraphLayoutMode.CompactGrid ? 220 : 260;
        var horizontalGap = mode == GraphLayoutMode.CompactGrid ? 34 : 72;
        var verticalGap = mode == GraphLayoutMode.CompactGrid ? 34 : 68;
        var layouts = ImmutableArray.CreateBuilder<DiffNodeLayout>(documents.Length);

        if (mode is GraphLayoutMode.Layered or GraphLayoutMode.StatusLanes)
        {
            var laneKeys = mode == GraphLayoutMode.StatusLanes
                ? items.Select(GetStatusLane).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : items.Select(item => item.KindText).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Array.Sort(laneKeys, StringComparer.OrdinalIgnoreCase);
            var laneIndexes = laneKeys.Select((key, index) => (key, index)).ToDictionary(pair => pair.key, pair => pair.index, StringComparer.OrdinalIgnoreCase);
            var laneCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < documents.Length; index++)
            {
                var lane = mode == GraphLayoutMode.StatusLanes ? GetStatusLane(items[index]) : items[index].KindText;
                var laneIndex = laneIndexes[lane];
                laneCounts.TryGetValue(lane, out var row);
                laneCounts[lane] = row + 1;
                layouts.Add(new DiffNodeLayout(
                    documents[index].Id,
                    new Rect2(laneIndex * (nodeWidth + horizontalGap), row * (nodeHeight + verticalGap), nodeWidth, nodeHeight),
                    FontSize: 12.5));
            }

            return new GraphLayoutResult(layouts.ToImmutable());
        }

        var columns = mode == GraphLayoutMode.Grid
            ? Math.Max(1, (int)Math.Ceiling(Math.Sqrt(documents.Length)))
            : Math.Max(1, (int)Math.Ceiling(Math.Sqrt(documents.Length * 1.4)));
        for (var index = 0; index < documents.Length; index++)
        {
            var row = index / columns;
            var column = index % columns;
            layouts.Add(new DiffNodeLayout(
                documents[index].Id,
                new Rect2(column * (nodeWidth + horizontalGap), row * (nodeHeight + verticalGap), nodeWidth, nodeHeight),
                FontSize: 12.5));
        }

        return new GraphLayoutResult(SpreadHighDegreeNodes(layouts.ToImmutable(), items, edges, nodeWidth, nodeHeight, horizontalGap, verticalGap));
    }

    private static GraphLayoutResult CreateFileSymbolLayout(
        ImmutableArray<DiffDocumentSnapshot> fileDocuments,
        ImmutableArray<SemanticNavigationItem> items,
        IReadOnlyDictionary<string, DiffDocumentId> symbolDocumentIdsByAnchorId,
        GraphLayoutMode layoutMode)
    {
        if (fileDocuments.IsDefaultOrEmpty)
        {
            return new GraphLayoutResult([]);
        }

        var mode = layoutMode == GraphLayoutMode.Auto ? GraphLayoutMode.Layered : layoutMode;
        if (mode is GraphLayoutMode.Grid or GraphLayoutMode.CompactGrid)
        {
            return CreateFileSymbolGridLayout(fileDocuments, items, symbolDocumentIdsByAnchorId, mode);
        }

        const double fileWidth = 620;
        const double fileHeight = 420;
        const double symbolWidth = 380;
        const double symbolHeight = 190;
        const double horizontalGap = 84;
        const double symbolColumnGap = 32;
        const double symbolGap = 26;
        const double sectionGap = 88;
        var layouts = ImmutableArray.CreateBuilder<DiffNodeLayout>(fileDocuments.Length + items.Length);
        var itemsByDocumentId = items
            .GroupBy(item => item.DocumentId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var y = 0.0;

        foreach (var fileDocument in fileDocuments)
        {
            var fileSymbols = itemsByDocumentId.GetValueOrDefault(fileDocument.Id) ?? [];
            var symbolColumns = Math.Max(1, Math.Min(3, (int)Math.Ceiling(Math.Sqrt(Math.Max(1, fileSymbols.Length) / 1.7))));
            var symbolsPerColumn = Math.Max(1, (int)Math.Ceiling(fileSymbols.Length / (double)symbolColumns));
            var symbolBlockHeight = fileSymbols.Length == 0
                ? 0
                : symbolsPerColumn * symbolHeight + Math.Max(0, symbolsPerColumn - 1) * symbolGap;
            var rowHeight = Math.Max(fileHeight, symbolBlockHeight);
            var fileY = y + Math.Max(0, rowHeight - fileHeight) * 0.5;
            layouts.Add(new DiffNodeLayout(fileDocument.Id, new Rect2(0, fileY, fileWidth, fileHeight), FontSize: 12.5));

            for (var index = 0; index < fileSymbols.Length; index++)
            {
                var item = fileSymbols[index];
                if (!symbolDocumentIdsByAnchorId.TryGetValue(item.AnchorId, out var documentId))
                {
                    continue;
                }

                var column = index / symbolsPerColumn;
                var row = index % symbolsPerColumn;
                var x = fileWidth + horizontalGap + column * (symbolWidth + symbolColumnGap);
                var symbolY = y + row * (symbolHeight + symbolGap);
                layouts.Add(new DiffNodeLayout(documentId, new Rect2(x, symbolY, symbolWidth, symbolHeight), FontSize: 12.5));
            }

            y += rowHeight + sectionGap;
        }

        return new GraphLayoutResult(layouts.ToImmutable());
    }

    private static GraphLayoutResult CreateFileSymbolGridLayout(
        ImmutableArray<DiffDocumentSnapshot> fileDocuments,
        ImmutableArray<SemanticNavigationItem> items,
        IReadOnlyDictionary<string, DiffDocumentId> symbolDocumentIdsByAnchorId,
        GraphLayoutMode layoutMode)
    {
        var documents = ImmutableArray.CreateBuilder<DiffDocumentId>(fileDocuments.Length + items.Length);
        documents.AddRange(fileDocuments.Select(document => document.Id));
        foreach (var item in items)
        {
            if (symbolDocumentIdsByAnchorId.TryGetValue(item.AnchorId, out var documentId))
            {
                documents.Add(documentId);
            }
        }

        var nodeWidth = layoutMode == GraphLayoutMode.CompactGrid ? 360 : 460;
        var nodeHeight = layoutMode == GraphLayoutMode.CompactGrid ? 190 : 260;
        var horizontalGap = layoutMode == GraphLayoutMode.CompactGrid ? 34 : 72;
        var verticalGap = layoutMode == GraphLayoutMode.CompactGrid ? 34 : 68;
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(documents.Count * 1.25)));
        var layouts = ImmutableArray.CreateBuilder<DiffNodeLayout>(documents.Count);
        for (var index = 0; index < documents.Count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            layouts.Add(new DiffNodeLayout(
                documents[index],
                new Rect2(column * (nodeWidth + horizontalGap), row * (nodeHeight + verticalGap), nodeWidth, nodeHeight),
                FontSize: 12.5));
        }

        return new GraphLayoutResult(layouts.ToImmutable());
    }

    private static ImmutableArray<DiffNodeLayout> SpreadHighDegreeNodes(
        ImmutableArray<DiffNodeLayout> layouts,
        ImmutableArray<SemanticNavigationItem> items,
        ImmutableArray<SemanticEdge> edges,
        double nodeWidth,
        double nodeHeight,
        double horizontalGap,
        double verticalGap)
    {
        if (layouts.Length < 8 || edges.IsDefaultOrEmpty)
        {
            return layouts;
        }

        var degreeByAnchor = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            degreeByAnchor[edge.SourceAnchorId] = degreeByAnchor.GetValueOrDefault(edge.SourceAnchorId) + 1;
            degreeByAnchor[edge.TargetAnchorId] = degreeByAnchor.GetValueOrDefault(edge.TargetAnchorId) + 1;
        }

        var builder = layouts.ToBuilder();
        var highDegreeIndexes = items
            .Select((item, index) => (index, degree: degreeByAnchor.GetValueOrDefault(item.AnchorId)))
            .Where(pair => pair.degree > 2)
            .OrderByDescending(pair => pair.degree)
            .ThenBy(pair => items[pair.index].DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Min(6, items.Length))
            .Select(pair => pair.index)
            .ToArray();
        for (var index = 0; index < highDegreeIndexes.Length; index++)
        {
            var layoutIndex = highDegreeIndexes[index];
            builder[layoutIndex] = builder[layoutIndex] with
            {
                Bounds = new Rect2(index * (nodeWidth + horizontalGap), -nodeHeight - verticalGap, nodeWidth, nodeHeight)
            };
        }

        return builder.ToImmutable();
    }

    private static string GetStatusLane(SemanticNavigationItem item) => item switch
    {
        { IsChanged: true, IsLinked: true } => "Changed + linked",
        { IsChanged: true } => "Changed",
        { IsLinked: true } => "Linked",
        _ => "Other"
    };

    private static string ShortenForPath(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return string.IsNullOrWhiteSpace(value) ? "symbol" : value;
        }

        var side = Math.Max(4, (maxLength - 3) / 2);
        return $"{value[..side]}...{value[^side..]}";
    }
}

public static class SymbolGraphDocumentIds
{
    private const string Prefix = "symbol:";
    private const string FileAnchorPrefix = "file-anchor:";

    public static DiffDocumentId Create(DiffDocumentId sourceDocumentId, string anchorId) =>
        new($"{Prefix}{Encode(sourceDocumentId.Value)}:{Encode(anchorId)}");

    public static string CreateFileAnchorId(DiffDocumentId sourceDocumentId) =>
        $"{FileAnchorPrefix}{Encode(sourceDocumentId.Value)}";

    public static string? TryGetSourceDocumentId(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId) || !documentId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var payload = documentId[Prefix.Length..];
        var separator = payload.IndexOf(':');
        return separator <= 0 ? null : Decode(payload[..separator]);
    }

    public static string? TryGetAnchorId(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId) || !documentId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var payload = documentId[Prefix.Length..];
        var separator = payload.IndexOf(':');
        return separator <= 0 || separator >= payload.Length - 1 ? null : Decode(payload[(separator + 1)..]);
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Decode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - normalized.Length % 4) % 4;
        normalized = normalized.PadRight(normalized.Length + padding, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
    }
}
