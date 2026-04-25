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
        var mode = request.LayoutMode == GraphLayoutMode.Auto ? GraphLayoutMode.Grid : request.LayoutMode;
        var nodes = mode switch
        {
            GraphLayoutMode.CompactGrid => LayoutGrid(request, cancellationToken, 48, 44, columnsBias: 1.35),
            GraphLayoutMode.StatusLanes => LayoutStatusLanes(request, cancellationToken),
            _ => LayoutGrid(request, cancellationToken, 96, 96, columnsBias: 1)
        };

        return new ValueTask<GraphLayoutResult>(new GraphLayoutResult(LayoutStabilizer.Apply(request, nodes)));
    }

    private static ImmutableArray<DiffNodeLayout> LayoutGrid(
        GraphLayoutRequest request,
        CancellationToken cancellationToken,
        double horizontalGap,
        double verticalGap,
        double columnsBias)
    {
        var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(request.Documents.Length);
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(request.Documents.Length * columnsBias)));
        var horizontalSpacing = request.DefaultNodeSize.Width + horizontalGap;
        var verticalSpacing = request.DefaultNodeSize.Height + verticalGap;

        for (var documentIndex = 0; documentIndex < request.Documents.Length; documentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var column = documentIndex % columns;
            var row = documentIndex / columns;
            var bounds = new Rect2(column * horizontalSpacing, row * verticalSpacing, request.DefaultNodeSize.Width, request.DefaultNodeSize.Height);
            builder.Add(new DiffNodeLayout(request.Documents[documentIndex].Id, bounds));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<DiffNodeLayout> LayoutStatusLanes(GraphLayoutRequest request, CancellationToken cancellationToken)
    {
        var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(request.Documents.Length);
        var laneLeft = 0.0;
        var horizontalSpacing = request.DefaultNodeSize.Width + 58;
        var verticalSpacing = request.DefaultNodeSize.Height + 52;

        foreach (var group in request.Documents.GroupBy(document => document.Metadata.Status).OrderBy(group => StatusLaneOrder(group.Key)))
        {
            var documents = group.OrderBy(document => document.Metadata.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            var laneColumns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(documents.Length * 0.9)));

            for (var documentIndex = 0; documentIndex < documents.Length; documentIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var column = documentIndex % laneColumns;
                var row = documentIndex / laneColumns;
                var bounds = new Rect2(
                    laneLeft + column * horizontalSpacing,
                    row * verticalSpacing,
                    request.DefaultNodeSize.Width,
                    request.DefaultNodeSize.Height);
                builder.Add(new DiffNodeLayout(documents[documentIndex].Id, bounds));
            }

            laneLeft += laneColumns * horizontalSpacing + 120;
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

        try
        {
            var geometryGraph = new GeometryGraph();
            var msaglNodes = new Dictionary<string, Node>(StringComparer.Ordinal);

            foreach (var document in request.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var curve = CurveFactory.CreateRectangle(request.DefaultNodeSize.Width, request.DefaultNodeSize.Height, new Point(0, 0));
                var node = new Node(curve, document.Id.Value);
                geometryGraph.Nodes.Add(node);
                msaglNodes[document.Id.Value] = node;
            }

            var anchors = request.SemanticGraph.Anchors.ToDictionary(anchor => anchor.Id, StringComparer.Ordinal);
            foreach (var edge in request.SemanticGraph.Edges)
            {
                if (!anchors.TryGetValue(edge.SourceAnchorId, out var sourceAnchor) ||
                    !anchors.TryGetValue(edge.TargetAnchorId, out var targetAnchor) ||
                    !msaglNodes.TryGetValue(sourceAnchor.DocumentId.Value, out var sourceNode) ||
                    !msaglNodes.TryGetValue(targetAnchor.DocumentId.Value, out var targetNode) ||
                    ReferenceEquals(sourceNode, targetNode))
                {
                    continue;
                }

                geometryGraph.Edges.Add(new Edge(sourceNode, targetNode));
            }

            var settings = new SugiyamaLayoutSettings
            {
                NodeSeparation = 80,
                LayerSeparation = 120
            };

            var layout = new LayeredLayout(geometryGraph, settings);
            layout.Run();
            geometryGraph.UpdateBoundingBox();

            var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(request.Documents.Length);
            foreach (var document in request.Documents)
            {
                var node = msaglNodes[document.Id.Value];
                var box = node.BoundingBox;
                builder.Add(new DiffNodeLayout(document.Id, new Rect2(box.Left, -box.Top, box.Width, box.Height)));
            }

            return new ValueTask<GraphLayoutResult>(new GraphLayoutResult(LayoutStabilizer.Apply(request, builder.ToImmutable())));
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
}

internal static class LayoutStabilizer
{
    public static ImmutableArray<DiffNodeLayout> Apply(GraphLayoutRequest request, ImmutableArray<DiffNodeLayout> computedNodes)
    {
        if (computedNodes.IsDefaultOrEmpty || request.PreviousNodes.IsDefaultOrEmpty)
        {
            return MarkPinned(request, computedNodes);
        }

        var previousByDocumentId = request.PreviousNodes.ToDictionary(node => node.DocumentId, node => node);
        var pinnedDocumentIds = request.PinnedDocumentIds ?? ImmutableHashSet<DiffDocumentId>.Empty;
        var anchorDelta = GetAnchorDelta(computedNodes, previousByDocumentId, pinnedDocumentIds);
        var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(computedNodes.Length);

        foreach (var computedNode in computedNodes)
        {
            if (pinnedDocumentIds.Contains(computedNode.DocumentId) && previousByDocumentId.TryGetValue(computedNode.DocumentId, out var previousNode))
            {
                builder.Add(previousNode with { IsPinned = true });
                continue;
            }

            var stabilizedBounds = computedNode.Bounds.Translate(anchorDelta.X, anchorDelta.Y);
            builder.Add(computedNode with { Bounds = stabilizedBounds, IsPinned = false });
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<DiffNodeLayout> MarkPinned(GraphLayoutRequest request, ImmutableArray<DiffNodeLayout> nodes)
    {
        var pinnedDocumentIds = request.PinnedDocumentIds ?? ImmutableHashSet<DiffDocumentId>.Empty;
        if (pinnedDocumentIds.Count == 0)
        {
            return nodes;
        }

        return nodes.Select(node => node with { IsPinned = pinnedDocumentIds.Contains(node.DocumentId) }).ToImmutableArray();
    }

    private static Point2 GetAnchorDelta(
        ImmutableArray<DiffNodeLayout> computedNodes,
        IReadOnlyDictionary<DiffDocumentId, DiffNodeLayout> previousByDocumentId,
        ImmutableHashSet<DiffDocumentId> pinnedDocumentIds)
    {
        var anchor = computedNodes.FirstOrDefault(node => !pinnedDocumentIds.Contains(node.DocumentId) && previousByDocumentId.ContainsKey(node.DocumentId));
        if (anchor is null)
        {
            return Point2.Zero;
        }

        var previous = previousByDocumentId[anchor.DocumentId];
        return new Point2(previous.Bounds.Center.X - anchor.Bounds.Center.X, previous.Bounds.Center.Y - anchor.Bounds.Center.Y);
    }
}