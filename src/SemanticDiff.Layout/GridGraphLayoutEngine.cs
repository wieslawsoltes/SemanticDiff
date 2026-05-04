using System.Buffers;
using System.Collections.Immutable;
using Microsoft.Msagl.Core.Geometry;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Layout.Layered;
using SemanticDiff.Core;

namespace SemanticDiff.Layout;

public sealed class GridGraphLayoutEngine : IGraphLayoutEngine
{
    public string Id => "deterministic-grid";

    public ValueTask<GraphLayoutResult> LayoutAsync(GraphLayoutRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mode = request.LayoutMode == GraphLayoutMode.Auto ? GraphLayoutMode.Grid : request.LayoutMode;
        var prepared = GraphLayoutCacheStore.Shared.Prepare(request, mode, cancellationToken);
        var arrangedNodes = GraphLayoutCacheStore.Shared.GetOrAddArranged(prepared.Fingerprint, () => mode switch
        {
            GraphLayoutMode.CompactGrid => LayoutGrid(prepared, cancellationToken, 48, 44, columnsBias: 1.35),
            GraphLayoutMode.StatusLanes => LayoutStatusLanes(prepared, cancellationToken),
            _ => LayoutGrid(prepared, cancellationToken, 96, 96, columnsBias: 1)
        });

        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<GraphLayoutResult>(new GraphLayoutResult(LayoutStabilizer.Apply(request, arrangedNodes)));
    }

    private static ImmutableArray<DiffNodeLayout> LayoutGrid(
        PreparedGraphLayout prepared,
        CancellationToken cancellationToken,
        double horizontalGap,
        double verticalGap,
        double columnsBias)
    {
        var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(prepared.Nodes.Length);
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(prepared.Nodes.Length * columnsBias)));
        GraphLayoutGridPlacement.AddMeasuredGrid(prepared.Nodes, columns, 0, 0, horizontalGap, verticalGap, cancellationToken, builder);
        return builder.ToImmutable();
    }

    private static ImmutableArray<DiffNodeLayout> LayoutStatusLanes(PreparedGraphLayout prepared, CancellationToken cancellationToken)
    {
        var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(prepared.Nodes.Length);
        var lanes = new List<MeasuredGraphNode>?[8];
        foreach (var node in prepared.Nodes)
        {
            var laneIndex = StatusLaneOrder(node.Status);
            (lanes[laneIndex] ??= []).Add(node);
        }

        var laneLeft = 0.0;
        for (var laneIndex = 0; laneIndex < lanes.Length; laneIndex++)
        {
            var lane = lanes[laneIndex];
            if (lane is null || lane.Count == 0)
            {
                continue;
            }

            lane.Sort(GraphLayoutGridPlacement.CompareMeasuredNodesByPath);
            var laneColumns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(lane.Count * 0.9)));
            var extent = GraphLayoutGridPlacement.AddMeasuredGrid(lane, laneColumns, laneLeft, 0, 58, 52, cancellationToken, builder);
            laneLeft += extent.Width + 120;
        }

        return builder.ToImmutable();
    }

    private static int StatusLaneOrder(DiffFileStatus status) => status switch
    {
        DiffFileStatus.Conflicted => 0,
        DiffFileStatus.Added => 1,
        DiffFileStatus.Untracked => 2,
        DiffFileStatus.Modified => 3,
        DiffFileStatus.Renamed => 4,
        DiffFileStatus.Copied => 5,
        DiffFileStatus.Deleted => 6,
        _ => 7
    };
}

internal static class GraphLayoutGridPlacement
{
    public static Size2 AddMeasuredGrid(
        IReadOnlyList<MeasuredGraphNode> nodes,
        int columns,
        double originX,
        double originY,
        double horizontalGap,
        double verticalGap,
        CancellationToken cancellationToken,
        ImmutableArray<DiffNodeLayout>.Builder builder)
    {
        if (nodes.Count == 0)
        {
            return Size2.Zero;
        }

        columns = Math.Max(1, columns);
        var rows = Math.Max(1, (int)Math.Ceiling((double)nodes.Count / columns));
        var columnWidths = ArrayPool<double>.Shared.Rent(columns);
        var rowHeights = ArrayPool<double>.Shared.Rent(rows);
        var columnOffsets = ArrayPool<double>.Shared.Rent(columns);
        var rowOffsets = ArrayPool<double>.Shared.Rent(rows);

        try
        {
            Array.Clear(columnWidths, 0, columns);
            Array.Clear(rowHeights, 0, rows);

            for (var index = 0; index < nodes.Count; index++)
            {
                var column = index % columns;
                var row = index / columns;
                var size = nodes[index].Size;
                columnWidths[column] = Math.Max(columnWidths[column], size.Width);
                rowHeights[row] = Math.Max(rowHeights[row], size.Height);
            }

            var x = 0d;
            for (var column = 0; column < columns; column++)
            {
                columnOffsets[column] = x;
                x += columnWidths[column] + horizontalGap;
            }

            var y = 0d;
            for (var row = 0; row < rows; row++)
            {
                rowOffsets[row] = y;
                y += rowHeights[row] + verticalGap;
            }

            for (var index = 0; index < nodes.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var column = index % columns;
                var row = index / columns;
                var node = nodes[index];
                builder.Add(new DiffNodeLayout(
                    node.DocumentId,
                    new Rect2(
                        originX + columnOffsets[column],
                        originY + rowOffsets[row],
                        node.Size.Width,
                        node.Size.Height)));
            }

            return new Size2(columnOffsets[columns - 1] + columnWidths[columns - 1], rowOffsets[rows - 1] + rowHeights[rows - 1]);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(columnWidths);
            ArrayPool<double>.Shared.Return(rowHeights);
            ArrayPool<double>.Shared.Return(columnOffsets);
            ArrayPool<double>.Shared.Return(rowOffsets);
        }
    }

    public static GridMetrics MeasureGrid(IReadOnlyList<MeasuredGraphNode> nodes, int columns, double horizontalGap, double verticalGap)
    {
        if (nodes.Count == 0)
        {
            return new GridMetrics(1, [], [], 0, 0);
        }

        columns = Math.Max(1, columns);
        var rows = Math.Max(1, (int)Math.Ceiling((double)nodes.Count / columns));
        var columnWidths = ArrayPool<double>.Shared.Rent(columns);
        var rowHeights = ArrayPool<double>.Shared.Rent(rows);
        var columnOffsets = new double[columns];
        var rowOffsets = new double[rows];
        try
        {
            Array.Clear(columnWidths, 0, columns);
            Array.Clear(rowHeights, 0, rows);

            for (var index = 0; index < nodes.Count; index++)
            {
                var column = index % columns;
                var row = index / columns;
                var size = nodes[index].Size;
                columnWidths[column] = Math.Max(columnWidths[column], size.Width);
                rowHeights[row] = Math.Max(rowHeights[row], size.Height);
            }

            var x = 0d;
            for (var column = 0; column < columns; column++)
            {
                columnOffsets[column] = x;
                x += columnWidths[column] + horizontalGap;
            }

            var y = 0d;
            for (var row = 0; row < rows; row++)
            {
                rowOffsets[row] = y;
                y += rowHeights[row] + verticalGap;
            }

            return new GridMetrics(
                columns,
                columnOffsets,
                rowOffsets,
                columnOffsets[columns - 1] + columnWidths[columns - 1],
                rowOffsets[rows - 1] + rowHeights[rows - 1]);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(columnWidths);
            ArrayPool<double>.Shared.Return(rowHeights);
        }
    }

    public static int CompareMeasuredNodesByPath(MeasuredGraphNode left, MeasuredGraphNode right) =>
        string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);

    internal readonly record struct GridMetrics(
        int Columns,
        double[] ColumnOffsets,
        double[] RowOffsets,
        double Width,
        double Height);
}

public sealed class MsaglGraphLayoutEngine : IGraphLayoutEngine
{
    private readonly GridGraphLayoutEngine fallback = new();

    public string Id => "msagl-layered";

    public ValueTask<GraphLayoutResult> LayoutAsync(GraphLayoutRequest request, CancellationToken cancellationToken)
    {
        var layoutMode = ResolveLayoutMode(request);
        if (layoutMode != GraphLayoutMode.Layered)
        {
            return fallback.LayoutAsync(request with { LayoutMode = layoutMode }, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var prepared = GraphLayoutCacheStore.Shared.Prepare(request, layoutMode, cancellationToken);
        try
        {
            var arrangedNodes = GraphLayoutCacheStore.Shared.GetOrAddArranged(
                prepared.Fingerprint,
                () => ArrangeLayered(request, prepared, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GraphLayoutResult>(new GraphLayoutResult(LayoutStabilizer.Apply(request, arrangedNodes)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return fallback.LayoutAsync(request, cancellationToken);
        }
    }

    private static ImmutableArray<DiffNodeLayout> ArrangeLayered(
        GraphLayoutRequest request,
        PreparedGraphLayout prepared,
        CancellationToken cancellationToken)
    {
        var layoutEdges = BuildLayoutEdges(request);
        if (ShouldUseClusteredLayeredLayout(request))
        {
            return LayoutSemanticClusters(request, prepared, layoutEdges, cancellationToken);
        }

        if (layoutEdges.IsDefaultOrEmpty && prepared.Nodes.Length > 1)
        {
            var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(prepared.Nodes.Length);
            var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(prepared.Nodes.Length)));
            GraphLayoutGridPlacement.AddMeasuredGrid(prepared.Nodes, columns, 0, 0, 96, 96, cancellationToken, builder);
            return builder.ToImmutable();
        }

        try
        {
            var geometryGraph = new GeometryGraph();
            var msaglNodes = new Dictionary<string, Node>(StringComparer.Ordinal);

            foreach (var measuredNode in prepared.Nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = measuredNode.Size;
                var documentId = measuredNode.DocumentId.Value;
                var curve = CurveFactory.CreateRectangle(size.Width, size.Height, new Point(0, 0));
                var node = new Node(curve, documentId);
                geometryGraph.Nodes.Add(node);
                msaglNodes[documentId] = node;
            }

            foreach (var edge in layoutEdges)
            {
                if (!msaglNodes.TryGetValue(edge.SourceDocumentId.Value, out var sourceNode) ||
                    !msaglNodes.TryGetValue(edge.TargetDocumentId.Value, out var targetNode) ||
                    ReferenceEquals(sourceNode, targetNode))
                {
                    continue;
                }

                geometryGraph.Edges.Add(new Edge(sourceNode, targetNode));
            }

            var settings = new SugiyamaLayoutSettings
            {
                NodeSeparation = request.Documents.Length >= 150 ? 36 : 80,
                LayerSeparation = request.Documents.Length >= 150 ? 80 : 120
            };

            var layout = new LayeredLayout(geometryGraph, settings);
            layout.Run();
            geometryGraph.UpdateBoundingBox();

            var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(prepared.Nodes.Length);
            foreach (var measuredNode in prepared.Nodes)
            {
                var node = msaglNodes[measuredNode.DocumentId.Value];
                var box = node.BoundingBox;
                builder.Add(new DiffNodeLayout(measuredNode.DocumentId, new Rect2(box.Left, -box.Top, box.Width, box.Height)));
            }

            return CompactDisconnectedComponents(request, builder.ToImmutable(), layoutEdges);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(prepared.Nodes.Length);
            var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(prepared.Nodes.Length)));
            GraphLayoutGridPlacement.AddMeasuredGrid(prepared.Nodes, columns, 0, 0, 96, 96, cancellationToken, builder);
            return builder.ToImmutable();
        }
    }

    private static GraphLayoutMode ResolveLayoutMode(GraphLayoutRequest request)
    {
        if (request.LayoutMode != GraphLayoutMode.Auto)
        {
            return request.LayoutMode;
        }

        if (request.Documents.Length >= 150)
        {
            return GraphLayoutMode.CompactGrid;
        }

        return request.SemanticGraph.Edges.IsDefaultOrEmpty ? GraphLayoutMode.Grid : GraphLayoutMode.Layered;
    }

    private static bool ShouldUseClusteredLayeredLayout(GraphLayoutRequest request) => request.Documents.Length >= 180;

    private static ImmutableArray<DiffNodeLayout> LayoutSemanticClusters(
        GraphLayoutRequest request,
        PreparedGraphLayout prepared,
        ImmutableArray<DocumentLayoutEdge> layoutEdges,
        CancellationToken cancellationToken)
    {
        const double nodeGapX = 36;
        const double nodeGapY = 34;
        const double clusterGap = 104;

        var centrality = BuildDocumentCentrality(layoutEdges);
        var clusters = new Dictionary<LargeLayoutClusterKey, List<MeasuredGraphNode>>();
        foreach (var node in prepared.Nodes)
        {
            var key = CreateClusterKey(node.Path);
            if (!clusters.TryGetValue(key, out var clusterNodes))
            {
                clusterNodes = [];
                clusters[key] = clusterNodes;
            }

            clusterNodes.Add(node);
        }

        var clusterPlans = new List<LargeLayoutClusterPlan>(clusters.Count);
        foreach (var cluster in clusters)
        {
            clusterPlans.Add(CreateClusterPlan(cluster.Key, cluster.Value, centrality, nodeGapX, nodeGapY));
        }

        clusterPlans.Sort(CompareClusterPlans);
        var targetRowWidth = Math.Max(
            request.DefaultNodeSize.Width * 8,
            Math.Sqrt(Math.Max(1, request.Documents.Length)) * (request.DefaultNodeSize.Width + nodeGapX) * 1.55);
        var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(request.Documents.Length);
        var cursorX = 0.0;
        var cursorY = 0.0;
        var rowHeight = 0.0;

        foreach (var plan in clusterPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cursorX > 0 && cursorX + plan.Width > targetRowWidth)
            {
                cursorX = 0;
                cursorY += rowHeight + clusterGap;
                rowHeight = 0;
            }

            AddClusterGrid(plan, cursorX, cursorY, cancellationToken, builder);

            cursorX += plan.Width + clusterGap;
            rowHeight = Math.Max(rowHeight, plan.Height);
        }

        return builder.ToImmutable();
    }

    private static int CompareClusterPlans(LargeLayoutClusterPlan left, LargeLayoutClusterPlan right)
    {
        var orderComparison = left.Key.Order.CompareTo(right.Key.Order);
        if (orderComparison != 0)
        {
            return orderComparison;
        }

        var sizeComparison = right.Nodes.Length.CompareTo(left.Nodes.Length);
        return sizeComparison != 0
            ? sizeComparison
            : string.Compare(left.Key.Label, right.Key.Label, StringComparison.OrdinalIgnoreCase);
    }

    private static LargeLayoutClusterPlan CreateClusterPlan(
        LargeLayoutClusterKey key,
        List<MeasuredGraphNode> nodes,
        IReadOnlyDictionary<DiffDocumentId, double> centrality,
        double nodeGapX,
        double nodeGapY)
    {
        var orderedNodes = nodes.ToArray();
        Array.Sort(orderedNodes, (left, right) =>
        {
            var centralityComparison = centrality.GetValueOrDefault(right.DocumentId).CompareTo(centrality.GetValueOrDefault(left.DocumentId));
            if (centralityComparison != 0)
            {
                return centralityComparison;
            }

            var statusComparison = LargeLayoutStatusOrder(left.Status).CompareTo(LargeLayoutStatusOrder(right.Status));
            return statusComparison != 0
                ? statusComparison
                : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        });
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(orderedNodes.Length * 1.35)));
        var metrics = GraphLayoutGridPlacement.MeasureGrid(orderedNodes, columns, nodeGapX, nodeGapY);
        return new LargeLayoutClusterPlan(key, orderedNodes, metrics);
    }

    private static void AddClusterGrid(
        LargeLayoutClusterPlan plan,
        double originX,
        double originY,
        CancellationToken cancellationToken,
        ImmutableArray<DiffNodeLayout>.Builder builder)
    {
        for (var documentIndex = 0; documentIndex < plan.Nodes.Length; documentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = plan.Nodes[documentIndex];
            var column = documentIndex % plan.Metrics.Columns;
            var row = documentIndex / plan.Metrics.Columns;
            builder.Add(new DiffNodeLayout(
                node.DocumentId,
                new Rect2(
                    originX + plan.Metrics.ColumnOffsets[column],
                    originY + plan.Metrics.RowOffsets[row],
                    node.Size.Width,
                    node.Size.Height)));
        }
    }

    private static Dictionary<DiffDocumentId, double> BuildDocumentCentrality(ImmutableArray<DocumentLayoutEdge> layoutEdges)
    {
        var centrality = new Dictionary<DiffDocumentId, double>();
        foreach (var edge in layoutEdges)
        {
            centrality[edge.SourceDocumentId] = centrality.GetValueOrDefault(edge.SourceDocumentId) + edge.Score;
            centrality[edge.TargetDocumentId] = centrality.GetValueOrDefault(edge.TargetDocumentId) + edge.Score;
        }

        return centrality;
    }

    private static LargeLayoutClusterKey CreateClusterKey(string documentPath)
    {
        var path = documentPath.AsSpan();
        var segmentIndex = 0;
        if (!TryReadPathSegment(path, ref segmentIndex, out var first))
        {
            return new LargeLayoutClusterKey("root", "Repository root", 4);
        }

        if (!TryReadPathSegment(path, ref segmentIndex, out var second))
        {
            return new LargeLayoutClusterKey("root", "Repository root", 4);
        }

        if (IsSourceLikeRoot(first))
        {
            var label = string.Concat(first, "/", second);
            return new LargeLayoutClusterKey(label, label, SourceLikeRootOrder(first));
        }

        var firstSegment = first.ToString();
        return new LargeLayoutClusterKey(firstSegment, firstSegment, SourceLikeRootOrder(first));
    }

    private static bool TryReadPathSegment(ReadOnlySpan<char> path, ref int index, out ReadOnlySpan<char> segment)
    {
        while (index < path.Length && IsPathSeparator(path[index]))
        {
            index++;
        }

        var start = index;
        while (index < path.Length && !IsPathSeparator(path[index]))
        {
            index++;
        }

        if (index == start)
        {
            segment = default;
            return false;
        }

        segment = path[start..index];
        return true;
    }

    private static bool IsPathSeparator(char character) => character is '/' or '\\';

    private static bool IsSourceLikeRoot(ReadOnlySpan<char> segment) =>
        segment.Equals("src", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("samples", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("examples", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("test", StringComparison.OrdinalIgnoreCase);

    private static int SourceLikeRootOrder(ReadOnlySpan<char> segment)
    {
        if (segment.Equals("src", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (segment.Equals("samples", StringComparison.OrdinalIgnoreCase) || segment.Equals("examples", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (segment.Equals("tests", StringComparison.OrdinalIgnoreCase) || segment.Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    private static int LargeLayoutStatusOrder(DiffFileStatus status) => status switch
    {
        DiffFileStatus.Conflicted => 0,
        DiffFileStatus.Added => 1,
        DiffFileStatus.Untracked => 2,
        DiffFileStatus.Renamed => 3,
        DiffFileStatus.Copied => 4,
        DiffFileStatus.Modified => 5,
        DiffFileStatus.Deleted => 6,
        _ => 7
    };

    private static ImmutableArray<DocumentLayoutEdge> BuildLayoutEdges(GraphLayoutRequest request)
    {
        if (request.SemanticGraph.Edges.IsDefaultOrEmpty || request.SemanticGraph.Anchors.IsDefaultOrEmpty)
        {
            return [];
        }

        var documentIds = new HashSet<DiffDocumentId>(request.Documents.Length);
        foreach (var document in request.Documents)
        {
            documentIds.Add(document.Id);
        }

        var anchorCapacity = GetBoundedCapacity(request.SemanticGraph.Anchors.Length, request.Documents.Length, 32);
        var anchors = new Dictionary<string, SemanticAnchor>(anchorCapacity, StringComparer.Ordinal);
        foreach (var anchor in request.SemanticGraph.Anchors)
        {
            if (documentIds.Contains(anchor.DocumentId))
            {
                anchors.TryAdd(anchor.Id, anchor);
            }
        }

        if (anchors.Count < 2)
        {
            return [];
        }

        var minimumConfidence = request.Documents.Length >= 150 ? 0.74 : 0.68;
        var pairCapacity = GetBoundedCapacity(request.SemanticGraph.Edges.Length, request.Documents.Length, 8);
        var pairAccumulators = new Dictionary<(DiffDocumentId Source, DiffDocumentId Target), DocumentLayoutEdgeAccumulator>(pairCapacity);

        foreach (var edge in request.SemanticGraph.Edges)
        {
            if (!IsLayoutEdgeKind(edge.Kind) || edge.Confidence < minimumConfidence ||
                !anchors.TryGetValue(edge.SourceAnchorId, out var sourceAnchor) ||
                !anchors.TryGetValue(edge.TargetAnchorId, out var targetAnchor) ||
                sourceAnchor.DocumentId == targetAnchor.DocumentId)
            {
                continue;
            }

            var layoutEdge = new DocumentLayoutEdge(
                sourceAnchor.DocumentId,
                targetAnchor.DocumentId,
                edge.Kind,
                edge.Confidence + LayoutKindWeight(edge.Kind));
            var pairKey = (layoutEdge.SourceDocumentId, layoutEdge.TargetDocumentId);
            if (!pairAccumulators.TryGetValue(pairKey, out var accumulator))
            {
                accumulator = new DocumentLayoutEdgeAccumulator(layoutEdge);
                pairAccumulators[pairKey] = accumulator;
            }
            else
            {
                accumulator.Add(layoutEdge);
            }
        }

        if (pairAccumulators.Count == 0)
        {
            return [];
        }

        var perSourceLimit = request.Documents.Length >= 150 ? 5 : 10;
        var maxEdges = GetBoundedMaxEdges(request.Documents.Length, request.Documents.Length >= 150 ? 3 : 6);
        var edgesBySource = new Dictionary<DiffDocumentId, List<DocumentLayoutEdge>>(Math.Min(pairAccumulators.Count, request.Documents.Length));
        foreach (var accumulator in pairAccumulators.Values)
        {
            var edge = accumulator.ToEdge();
            if (!edgesBySource.TryGetValue(edge.SourceDocumentId, out var sourceEdges))
            {
                sourceEdges = [];
                edgesBySource[edge.SourceDocumentId] = sourceEdges;
            }

            sourceEdges.Add(edge);
        }

        var selectedEdges = new List<DocumentLayoutEdge>(Math.Min(pairAccumulators.Count, maxEdges));
        foreach (var sourceEdges in edgesBySource.Values)
        {
            sourceEdges.Sort(CompareLayoutEdgesBySource);
            var takeCount = Math.Min(perSourceLimit, sourceEdges.Count);
            for (var index = 0; index < takeCount; index++)
            {
                selectedEdges.Add(sourceEdges[index]);
            }
        }

        selectedEdges.Sort(CompareLayoutEdges);
        var resultCount = Math.Min(maxEdges, selectedEdges.Count);
        var builder = ImmutableArray.CreateBuilder<DocumentLayoutEdge>(resultCount);
        for (var index = 0; index < resultCount; index++)
        {
            builder.Add(selectedEdges[index]);
        }

        return builder.ToImmutable();
    }

    private static int GetBoundedCapacity(int availableCount, int itemCount, int multiplier)
    {
        if (availableCount <= 0)
        {
            return 0;
        }

        var scaledCount = itemCount > int.MaxValue / multiplier
            ? int.MaxValue
            : itemCount * multiplier;
        var capacityHint = Math.Max(16, scaledCount);
        return Math.Min(availableCount, capacityHint);
    }

    private static int GetBoundedMaxEdges(int documentCount, int multiplier)
    {
        if (documentCount <= 0)
        {
            return 0;
        }

        var scaledCount = documentCount > int.MaxValue / multiplier
            ? int.MaxValue
            : documentCount * multiplier;
        return Math.Max(documentCount, scaledCount);
    }

    private static int CompareLayoutEdgesBySource(DocumentLayoutEdge left, DocumentLayoutEdge right)
    {
        var scoreComparison = right.Score.CompareTo(left.Score);
        return scoreComparison != 0
            ? scoreComparison
            : string.Compare(left.TargetDocumentId.Value, right.TargetDocumentId.Value, StringComparison.Ordinal);
    }

    private static int CompareLayoutEdges(DocumentLayoutEdge left, DocumentLayoutEdge right)
    {
        var scoreComparison = right.Score.CompareTo(left.Score);
        if (scoreComparison != 0)
        {
            return scoreComparison;
        }

        var sourceComparison = string.Compare(left.SourceDocumentId.Value, right.SourceDocumentId.Value, StringComparison.Ordinal);
        return sourceComparison != 0
            ? sourceComparison
            : string.Compare(left.TargetDocumentId.Value, right.TargetDocumentId.Value, StringComparison.Ordinal);
    }

    private static bool IsLayoutEdgeKind(SemanticEdgeKind kind) => kind is not SemanticEdgeKind.Contains;

    private static double LayoutKindWeight(SemanticEdgeKind kind) => kind switch
    {
        SemanticEdgeKind.XamlClass => 0.55,
        SemanticEdgeKind.PartialClass => 0.5,
        SemanticEdgeKind.TypeInheritance => 0.42,
        SemanticEdgeKind.ProjectReference => 0.36,
        SemanticEdgeKind.GeneratedFile => 0.34,
        SemanticEdgeKind.Resource => 0.28,
        SemanticEdgeKind.Binding => 0.24,
        SemanticEdgeKind.RenameOrMove => 0.22,
        SemanticEdgeKind.SymbolReference => 0.16,
        _ => 0
    };

    private static ImmutableArray<DiffNodeLayout> CompactDisconnectedComponents(
        GraphLayoutRequest request,
        ImmutableArray<DiffNodeLayout> nodes,
        ImmutableArray<DocumentLayoutEdge> layoutEdges)
    {
        if (nodes.Length <= 1 || layoutEdges.IsDefaultOrEmpty)
        {
            return nodes;
        }

        var nodeByDocumentId = new Dictionary<DiffDocumentId, DiffNodeLayout>(nodes.Length);
        var adjacency = new Dictionary<DiffDocumentId, List<DiffDocumentId>>(nodes.Length);
        foreach (var node in nodes)
        {
            nodeByDocumentId[node.DocumentId] = node;
            adjacency[node.DocumentId] = [];
        }

        foreach (var edge in layoutEdges)
        {
            if (adjacency.TryGetValue(edge.SourceDocumentId, out var sourceNeighbors) && adjacency.ContainsKey(edge.TargetDocumentId))
            {
                sourceNeighbors.Add(edge.TargetDocumentId);
                adjacency[edge.TargetDocumentId].Add(edge.SourceDocumentId);
            }
        }

        foreach (var neighbors in adjacency.Values)
        {
            if (neighbors.Count > 1)
            {
                neighbors.Sort(CompareDocumentIds);
            }
        }

        var components = BuildComponents(nodes, adjacency, nodeByDocumentId);
        if (components.Count <= 1)
        {
            return nodes;
        }

        components.Sort(CompareLayoutComponents);
        var targetRowWidth = Math.Max(
            request.DefaultNodeSize.Width * 4,
            Math.Sqrt(Math.Max(1, nodes.Length)) * (request.DefaultNodeSize.Width + 120));
        var componentGap = request.Documents.Length >= 150 ? 90 : 140;
        var nextBoundsByDocumentId = new Dictionary<DiffDocumentId, Rect2>(nodes.Length);
        var cursorX = 0.0;
        var cursorY = 0.0;
        var rowHeight = 0.0;

        foreach (var component in components)
        {
            if (cursorX > 0 && cursorX + component.Bounds.Width > targetRowWidth)
            {
                cursorX = 0;
                cursorY += rowHeight + componentGap;
                rowHeight = 0;
            }

            var deltaX = cursorX - component.Bounds.Left;
            var deltaY = cursorY - component.Bounds.Top;
            foreach (var documentId in component.DocumentIds)
            {
                nextBoundsByDocumentId[documentId] = nodeByDocumentId[documentId].Bounds.Translate(deltaX, deltaY);
            }

            cursorX += component.Bounds.Width + componentGap;
            rowHeight = Math.Max(rowHeight, component.Bounds.Height);
        }

        var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(nodes.Length);
        foreach (var node in nodes)
        {
            builder.Add(nextBoundsByDocumentId.TryGetValue(node.DocumentId, out var bounds)
                ? node with { Bounds = bounds }
                : node);
        }

        return builder.ToImmutable();
    }

    private static List<LayoutComponent> BuildComponents(
        ImmutableArray<DiffNodeLayout> nodes,
        IReadOnlyDictionary<DiffDocumentId, List<DiffDocumentId>> adjacency,
        IReadOnlyDictionary<DiffDocumentId, DiffNodeLayout> nodeByDocumentId)
    {
        var visited = new HashSet<DiffDocumentId>(nodes.Length);
        var components = new List<LayoutComponent>();
        var orderedNodes = nodes.ToArray();
        Array.Sort(orderedNodes, CompareNodeLayoutsByDocumentId);

        foreach (var node in orderedNodes)
        {
            if (!visited.Add(node.DocumentId))
            {
                continue;
            }

            var component = new List<DiffDocumentId>();
            var stack = new Stack<DiffDocumentId>();
            stack.Push(node.DocumentId);
            var bounds = Rect2.Empty;
            var hasBounds = false;
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                component.Add(current);
                if (nodeByDocumentId.TryGetValue(current, out var currentNode) && !currentNode.Bounds.IsEmpty)
                {
                    if (!hasBounds)
                    {
                        bounds = currentNode.Bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        bounds = Union(bounds, currentNode.Bounds);
                    }
                }

                foreach (var neighbor in adjacency[current])
                {
                    if (visited.Add(neighbor))
                    {
                        stack.Push(neighbor);
                    }
                }
            }

            components.Add(new LayoutComponent(component.ToArray(), hasBounds ? bounds : Rect2.Empty));
        }

        return components;
    }

    private static Rect2 Union(Rect2 left, Rect2 right)
    {
        var unionLeft = Math.Min(left.Left, right.Left);
        var unionTop = Math.Min(left.Top, right.Top);
        var unionRight = Math.Max(left.Right, right.Right);
        var unionBottom = Math.Max(left.Bottom, right.Bottom);
        return new Rect2(unionLeft, unionTop, unionRight - unionLeft, unionBottom - unionTop);
    }

    private static int CompareNodeLayoutsByDocumentId(DiffNodeLayout left, DiffNodeLayout right) =>
        string.Compare(left.DocumentId.Value, right.DocumentId.Value, StringComparison.Ordinal);

    private static int CompareDocumentIds(DiffDocumentId left, DiffDocumentId right) =>
        string.Compare(left.Value, right.Value, StringComparison.Ordinal);

    private static int CompareLayoutComponents(LayoutComponent left, LayoutComponent right)
    {
        var sizeComparison = right.DocumentIds.Length.CompareTo(left.DocumentIds.Length);
        if (sizeComparison != 0)
        {
            return sizeComparison;
        }

        var topComparison = left.Bounds.Top.CompareTo(right.Bounds.Top);
        return topComparison != 0
            ? topComparison
            : left.Bounds.Left.CompareTo(right.Bounds.Left);
    }

    private readonly record struct DocumentLayoutEdge(DiffDocumentId SourceDocumentId, DiffDocumentId TargetDocumentId, SemanticEdgeKind Kind, double Score);

    private sealed class DocumentLayoutEdgeAccumulator
    {
        private DocumentLayoutEdge strongestEdge;
        private int count;
        private HashSet<SemanticEdgeKind>? kinds;

        public DocumentLayoutEdgeAccumulator(DocumentLayoutEdge edge)
        {
            strongestEdge = edge;
            count = 1;
            kinds = null;
        }

        public void Add(DocumentLayoutEdge edge)
        {
            count++;
            var previousStrongestKind = strongestEdge.Kind;
            if (edge.Score > strongestEdge.Score)
            {
                strongestEdge = edge;
            }

            if (kinds is null)
            {
                if (edge.Kind == previousStrongestKind)
                {
                    return;
                }

                kinds = [previousStrongestKind];
            }

            kinds.Add(edge.Kind);
        }

        public DocumentLayoutEdge ToEdge()
        {
            var distinctKindCount = kinds?.Count ?? 1;
            var score = strongestEdge.Score + Math.Log2(count + 1) * 0.08 + distinctKindCount * 0.04;
            return strongestEdge with { Score = score };
        }
    }

    private readonly record struct LargeLayoutClusterKey(string Id, string Label, int Order);

    private sealed record LargeLayoutClusterPlan(
        LargeLayoutClusterKey Key,
        MeasuredGraphNode[] Nodes,
        GraphLayoutGridPlacement.GridMetrics Metrics)
    {
        public double Width => Metrics.Width;

        public double Height => Metrics.Height;
    }

    private readonly record struct LayoutComponent(DiffDocumentId[] DocumentIds, Rect2 Bounds);
}

internal static class LayoutStabilizer
{
    public static ImmutableArray<DiffNodeLayout> Apply(GraphLayoutRequest request, ImmutableArray<DiffNodeLayout> computedNodes)
    {
        if (computedNodes.IsDefaultOrEmpty)
        {
            return computedNodes;
        }

        var pinnedDocumentIds = request.PinnedDocumentIds ?? ImmutableHashSet<DiffDocumentId>.Empty;
        if (request.PreviousNodes.IsDefaultOrEmpty)
        {
            return pinnedDocumentIds.Count == 0
                ? computedNodes
                : MarkPinned(computedNodes, pinnedDocumentIds);
        }

        var previousByDocumentId = new Dictionary<DiffDocumentId, DiffNodeLayout>(request.PreviousNodes.Length);
        foreach (var previousNode in request.PreviousNodes)
        {
            previousByDocumentId[previousNode.DocumentId] = previousNode;
        }

        var anchorDelta = GetAnchorDelta(computedNodes, previousByDocumentId, pinnedDocumentIds);
        var hasAnchorDelta = anchorDelta.X != 0 || anchorDelta.Y != 0;
        if (!hasAnchorDelta && pinnedDocumentIds.Count == 0)
        {
            return computedNodes;
        }

        var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(computedNodes.Length);

        foreach (var computedNode in computedNodes)
        {
            if (pinnedDocumentIds.Contains(computedNode.DocumentId) && previousByDocumentId.TryGetValue(computedNode.DocumentId, out var previousNode))
            {
                builder.Add(previousNode with { IsPinned = true });
                continue;
            }

            builder.Add(hasAnchorDelta
                ? computedNode with { Bounds = computedNode.Bounds.Translate(anchorDelta.X, anchorDelta.Y), IsPinned = false }
                : computedNode with { IsPinned = false });
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<DiffNodeLayout> MarkPinned(
        ImmutableArray<DiffNodeLayout> nodes,
        ImmutableHashSet<DiffDocumentId> pinnedDocumentIds)
    {
        var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(nodes.Length);
        foreach (var node in nodes)
        {
            var isPinned = pinnedDocumentIds.Contains(node.DocumentId);
            builder.Add(node.IsPinned == isPinned ? node : node with { IsPinned = isPinned });
        }

        return builder.ToImmutable();
    }

    private static Point2 GetAnchorDelta(
        ImmutableArray<DiffNodeLayout> computedNodes,
        IReadOnlyDictionary<DiffDocumentId, DiffNodeLayout> previousByDocumentId,
        ImmutableHashSet<DiffDocumentId> pinnedDocumentIds)
    {
        foreach (var anchor in computedNodes)
        {
            if (pinnedDocumentIds.Contains(anchor.DocumentId) || !previousByDocumentId.TryGetValue(anchor.DocumentId, out var previous))
            {
                continue;
            }

            return new Point2(previous.Bounds.Center.X - anchor.Bounds.Center.X, previous.Bounds.Center.Y - anchor.Bounds.Center.Y);
        }

        return Point2.Zero;
    }
}
