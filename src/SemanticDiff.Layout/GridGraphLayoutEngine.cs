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
            var layoutEdges = BuildLayoutEdges(request);

            if (layoutEdges.IsDefaultOrEmpty && request.Documents.Length > 1)
            {
                return fallback.LayoutAsync(request with { LayoutMode = GraphLayoutMode.Grid }, cancellationToken);
            }

            foreach (var document in request.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var curve = CurveFactory.CreateRectangle(request.DefaultNodeSize.Width, request.DefaultNodeSize.Height, new Point(0, 0));
                var node = new Node(curve, document.Id.Value);
                geometryGraph.Nodes.Add(node);
                msaglNodes[document.Id.Value] = node;
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

            var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(request.Documents.Length);
            foreach (var document in request.Documents)
            {
                var node = msaglNodes[document.Id.Value];
                var box = node.BoundingBox;
                builder.Add(new DiffNodeLayout(document.Id, new Rect2(box.Left, -box.Top, box.Width, box.Height)));
            }

            var compactedNodes = CompactDisconnectedComponents(request, builder.ToImmutable(), layoutEdges);
            return new ValueTask<GraphLayoutResult>(new GraphLayoutResult(LayoutStabilizer.Apply(request, compactedNodes)));
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

    private static ImmutableArray<DocumentLayoutEdge> BuildLayoutEdges(GraphLayoutRequest request)
    {
        if (request.SemanticGraph.Edges.IsDefaultOrEmpty || request.SemanticGraph.Anchors.IsDefaultOrEmpty)
        {
            return [];
        }

        var anchors = request.SemanticGraph.Anchors.ToDictionary(anchor => anchor.Id, StringComparer.Ordinal);
        var documentIds = request.Documents.Select(document => document.Id).ToHashSet();
        var minimumConfidence = request.Documents.Length >= 150 ? 0.74 : 0.68;
        var candidates = ImmutableArray.CreateBuilder<DocumentLayoutEdge>();

        foreach (var edge in request.SemanticGraph.Edges)
        {
            if (!IsLayoutEdgeKind(edge.Kind) || edge.Confidence < minimumConfidence ||
                !anchors.TryGetValue(edge.SourceAnchorId, out var sourceAnchor) ||
                !anchors.TryGetValue(edge.TargetAnchorId, out var targetAnchor) ||
                sourceAnchor.DocumentId == targetAnchor.DocumentId ||
                !documentIds.Contains(sourceAnchor.DocumentId) ||
                !documentIds.Contains(targetAnchor.DocumentId))
            {
                continue;
            }

            candidates.Add(new DocumentLayoutEdge(
                sourceAnchor.DocumentId,
                targetAnchor.DocumentId,
                edge.Kind,
                edge.Confidence + LayoutKindWeight(edge.Kind)));
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var pairEdges = candidates
            .GroupBy(edge => (edge.SourceDocumentId, edge.TargetDocumentId))
            .Select(group =>
            {
                var strongest = group.OrderByDescending(edge => edge.Score).First();
                var score = strongest.Score + Math.Log2(group.Count() + 1) * 0.08 + group.Select(edge => edge.Kind).Distinct().Count() * 0.04;
                return strongest with { Score = score };
            })
            .ToArray();
        var perSourceLimit = request.Documents.Length >= 150 ? 5 : 10;
        var maxEdges = Math.Max(request.Documents.Length, request.Documents.Length * (request.Documents.Length >= 150 ? 3 : 6));

        return pairEdges
            .GroupBy(edge => edge.SourceDocumentId)
            .SelectMany(group => group
                .OrderByDescending(edge => edge.Score)
                .ThenBy(edge => edge.TargetDocumentId.Value, StringComparer.Ordinal)
                .Take(perSourceLimit))
            .GroupBy(edge => (edge.SourceDocumentId, edge.TargetDocumentId))
            .Select(group => group.OrderByDescending(edge => edge.Score).First())
            .OrderByDescending(edge => edge.Score)
            .ThenBy(edge => edge.SourceDocumentId.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetDocumentId.Value, StringComparer.Ordinal)
            .Take(maxEdges)
            .ToImmutableArray();
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
        if (nodes.Length <= 1)
        {
            return nodes;
        }

        var nodeByDocumentId = nodes.ToDictionary(node => node.DocumentId);
        var adjacency = nodes.ToDictionary(node => node.DocumentId, _ => new List<DiffDocumentId>());
        foreach (var edge in layoutEdges)
        {
            if (adjacency.TryGetValue(edge.SourceDocumentId, out var sourceNeighbors) && adjacency.ContainsKey(edge.TargetDocumentId))
            {
                sourceNeighbors.Add(edge.TargetDocumentId);
                adjacency[edge.TargetDocumentId].Add(edge.SourceDocumentId);
            }
        }

        var components = BuildComponents(nodes, adjacency)
            .Select(component => new LayoutComponent(component, Rect2.Union(component.Select(documentId => nodeByDocumentId[documentId].Bounds))))
            .OrderByDescending(component => component.DocumentIds.Length)
            .ThenBy(component => component.Bounds.Top)
            .ThenBy(component => component.Bounds.Left)
            .ToArray();
        if (components.Length <= 1)
        {
            return nodes;
        }

        var targetRowWidth = Math.Max(
            request.DefaultNodeSize.Width * 4,
            Math.Sqrt(Math.Max(1, nodes.Length)) * (request.DefaultNodeSize.Width + 120));
        var componentGap = request.Documents.Length >= 150 ? 90 : 140;
        var nextBoundsByDocumentId = new Dictionary<DiffDocumentId, Rect2>();
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

        return nodes.Select(node => node with { Bounds = nextBoundsByDocumentId[node.DocumentId] }).ToImmutableArray();
    }

    private static ImmutableArray<ImmutableArray<DiffDocumentId>> BuildComponents(
        ImmutableArray<DiffNodeLayout> nodes,
        IReadOnlyDictionary<DiffDocumentId, List<DiffDocumentId>> adjacency)
    {
        var visited = new HashSet<DiffDocumentId>();
        var components = ImmutableArray.CreateBuilder<ImmutableArray<DiffDocumentId>>();

        foreach (var node in nodes.OrderBy(node => node.DocumentId.Value, StringComparer.Ordinal))
        {
            if (!visited.Add(node.DocumentId))
            {
                continue;
            }

            var component = ImmutableArray.CreateBuilder<DiffDocumentId>();
            var stack = new Stack<DiffDocumentId>();
            stack.Push(node.DocumentId);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                component.Add(current);
                foreach (var neighbor in adjacency[current].OrderBy(id => id.Value, StringComparer.Ordinal))
                {
                    if (visited.Add(neighbor))
                    {
                        stack.Push(neighbor);
                    }
                }
            }

            components.Add(component.ToImmutable());
        }

        return components.ToImmutable();
    }

    private sealed record DocumentLayoutEdge(DiffDocumentId SourceDocumentId, DiffDocumentId TargetDocumentId, SemanticEdgeKind Kind, double Score);

    private sealed record LayoutComponent(ImmutableArray<DiffDocumentId> DocumentIds, Rect2 Bounds);
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