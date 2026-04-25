using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Rendering;

public sealed class DiffNode
{
    public const double MinWidth = 320;
    public const double MinHeight = 180;
    public const double TitleHeight = 32;
    public const double FooterHeight = 22;
    public const double DefaultFontSize = 12.5;
    public const double MinFontSize = 9;
    public const double MaxFontSize = 22;
    public const double FontSizeStep = 1;
    public const double FontControlButtonScreenSize = 20;
    public const double FontControlGapScreenSize = 5;
    public const double FontControlRightInsetScreenSize = 98;
    public const double FontControlLineCountInset = 100;
    public const double FontControlLineCountGapScreenSize = 8;
    public const double FontControlMinimumNodeScreenWidth = 220;
    public const double FontControlMinimumTitleScreenHeight = 22;

    public DiffNode(DiffDocumentSnapshot document, Rect2 bounds, bool isPinned = false, double fontSize = DefaultFontSize)
    {
        Document = document;
        Bounds = bounds;
        IsPinned = isPinned;
        FontSize = NormalizeFontSize(fontSize);
    }

    public DiffDocumentSnapshot Document { get; }

    public Rect2 Bounds { get; set; }

    public double ScrollOffsetY { get; private set; }

    public bool IsSelected { get; set; }

    public bool IsPinned { get; set; }

    public double FontSize { get; private set; }

    public double LineHeight => Math.Round(FontSize + 4.5, 2);

    public double MaxScrollOffset => Math.Max(0, Document.LineCount * LineHeight - BodyBounds.Height);

    public Rect2 TitleBounds => new(Bounds.X, Bounds.Y, Bounds.Width, TitleHeight);

    public Rect2 BodyBounds => new(Bounds.X, Bounds.Y + TitleHeight, Bounds.Width, Math.Max(0, Bounds.Height - TitleHeight - FooterHeight));

    public void ScrollBy(double deltaY)
    {
        ScrollOffsetY = ClampScrollOffset(ScrollOffsetY + deltaY);
    }

    public void ClampScrollOffset()
    {
        ScrollOffsetY = ClampScrollOffset(ScrollOffsetY);
    }

    public void RestoreScrollOffset(double scrollOffsetY)
    {
        ScrollOffsetY = ClampScrollOffset(scrollOffsetY);
    }

    private double ClampScrollOffset(double scrollOffsetY)
    {
        return Math.Clamp(scrollOffsetY, 0, MaxScrollOffset);
    }

    public void SetScrollOffset(double scrollOffsetY)
    {
        ScrollOffsetY = ClampScrollOffset(scrollOffsetY);
    }

    public void IncreaseFontSize() => SetFontSize(FontSize + FontSizeStep);

    public void DecreaseFontSize() => SetFontSize(FontSize - FontSizeStep);

    public void SetFontSize(double fontSize)
    {
        FontSize = NormalizeFontSize(fontSize);
        ClampScrollOffset();
    }

    public Rect2 GetScrollbarThumbBounds(double cameraScale)
    {
        var body = BodyBounds;
        var contentHeight = Math.Max(1, Document.LineCount * LineHeight);
        if (contentHeight <= body.Height)
        {
            return Rect2.Empty;
        }

        var trackInset = DiffCanvasScene.ScreenStableWorldLength(cameraScale, 3);
        var thumbWidth = DiffCanvasScene.ScreenStableWorldLength(cameraScale, 6);
        var minThumbHeight = DiffCanvasScene.ScreenStableWorldLength(cameraScale, 24);
        var thumbHeight = Math.Max(minThumbHeight, body.Height * body.Height / contentHeight);
        thumbHeight = Math.Min(body.Height, thumbHeight);
        var trackHeight = Math.Max(0, body.Height - thumbHeight);
        var thumbTop = body.Top;
        if (trackHeight > 0)
        {
            thumbTop += ScrollOffsetY / MaxScrollOffset * trackHeight;
        }

        return new Rect2(body.Right - thumbWidth - trackInset, thumbTop, thumbWidth, thumbHeight);
    }

    public Rect2 GetFontSizeButtonBounds(DiffNodeFontSizeAction action, double cameraScale)
    {
        if (!CanShowFontSizeButtons(cameraScale))
        {
            return Rect2.Empty;
        }

        var buttonSize = DiffCanvasScene.ScreenStableWorldLength(cameraScale, FontControlButtonScreenSize);
        var gap = DiffCanvasScene.ScreenStableWorldLength(cameraScale, FontControlGapScreenSize);
        var rightInset = Math.Max(
            DiffCanvasScene.ScreenStableWorldLength(cameraScale, FontControlRightInsetScreenSize),
            FontControlLineCountInset + DiffCanvasScene.ScreenStableWorldLength(cameraScale, FontControlLineCountGapScreenSize));
        var plusRight = Bounds.Right - rightInset;
        var buttonLeft = action == DiffNodeFontSizeAction.Increase
            ? plusRight - buttonSize
            : plusRight - buttonSize * 2 - gap;
        var top = Bounds.Top + (TitleHeight - buttonSize) / 2;
        return new Rect2(buttonLeft, top, buttonSize, buttonSize);
    }

    public bool CanShowFontSizeButtons(double cameraScale)
    {
        var scale = Math.Max(cameraScale, 0.01);
        return Bounds.Width * scale >= FontControlMinimumNodeScreenWidth &&
            TitleHeight * scale >= FontControlMinimumTitleScreenHeight;
    }

    private static double NormalizeFontSize(double fontSize) => double.IsFinite(fontSize)
        ? Math.Clamp(fontSize, MinFontSize, MaxFontSize)
        : DefaultFontSize;

    public void ScrollToLine(int lineNumber)
    {
        var targetLineIndex = Math.Clamp(lineNumber - 1, 0, Math.Max(0, Document.LineCount - 1));
        for (var index = 0; index < Document.Lines.Length; index++)
        {
            var line = Document.Lines[index];
            if (line.OldLineNumber == lineNumber || line.NewLineNumber == lineNumber)
            {
                targetLineIndex = index;
                break;
            }
        }

        var bodyHeight = BodyBounds.Height;
        ScrollOffsetY = Math.Clamp(targetLineIndex * LineHeight - bodyHeight * 0.35, 0, MaxScrollOffset);
    }
}

public enum DiffNodeFontSizeAction
{
    Decrease,
    Increase
}

public enum DiffNodeResizeHandle
{
    None,
    Left,
    Top,
    Right,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public sealed record DiffNodeViewState(
    DiffDocumentId DocumentId,
    Rect2 Bounds,
    double ScrollOffsetY,
    bool IsSelected,
    bool IsPinned,
    double FontSize);

public sealed record DiffCanvasSceneViewState(CameraState Camera, ImmutableArray<DiffNodeViewState> Nodes)
{
    public string? SelectedDocumentId => Nodes.FirstOrDefault(node => node.IsSelected)?.DocumentId.Value;
}

public sealed record GraphEdge(string SourceNodeId, string TargetNodeId, SemanticEdgeKind Kind, double Confidence, string? Label, int BundleCount = 1);

public sealed record GraphGroup(
    string Id,
    GraphGroupingMode Mode,
    string Label,
    Rect2 Bounds,
    int DocumentCount,
    int AddedLines,
    int DeletedLines,
    int ColorIndex)
{
    public string SummaryText => DocumentCount == 1 ? Label : $"{Label} ({DocumentCount:N0})";
}

public sealed record EdgeProjectionOptions(
    double MinimumConfidence = 0.65,
    int MaxEdgesPerDocumentPair = 1,
    bool BundleParallelEdges = true,
    ImmutableHashSet<SemanticEdgeKind>? IncludedEdgeKinds = null);

public sealed class DiffCanvasScene
{
    public const double ResizeHandleScreenSize = 10;

    private readonly List<DiffNode> nodes;
    private readonly List<GraphEdge> edges;
    private readonly ImmutableArray<GraphGroup> groups;
    private readonly ImmutableArray<DiffAnnotation> annotations;

    public DiffCanvasScene(
        IEnumerable<DiffNode> nodes,
        IEnumerable<GraphEdge> edges,
        ImmutableArray<GraphGroup> groups = default,
        ImmutableArray<DiffAnnotation> annotations = default,
        DiffAnnotationVisibilityState? annotationVisibility = null)
    {
        this.nodes = nodes.ToList();
        this.edges = edges.ToList();
        this.groups = groups.IsDefault ? ImmutableArray<GraphGroup>.Empty : groups;
        this.annotations = annotations.IsDefault ? ImmutableArray<DiffAnnotation>.Empty : annotations;
        AnnotationVisibility = annotationVisibility ?? DiffAnnotationVisibilityState.Default;
    }

    public IReadOnlyList<DiffNode> Nodes => nodes;

    public IReadOnlyList<GraphEdge> Edges => edges;

    public ImmutableArray<GraphGroup> Groups => groups;

    public ImmutableArray<DiffAnnotation> Annotations => annotations;

    public DiffAnnotationVisibilityState AnnotationVisibility { get; }

    public CameraState Camera { get; private set; } = CameraState.Default;

    public Rect2 GraphBounds => Rect2.Union(nodes.Select(node => node.Bounds).Concat(groups.Select(group => group.Bounds)));

    public static double ScreenStableWorldLength(double cameraScale, double screenPixels) => screenPixels / Math.Max(cameraScale, 0.01);

    public void Pan(double deltaX, double deltaY) => Camera = Camera.Pan(deltaX, deltaY);

    public void ZoomAt(Point2 screenPoint, double zoomFactor) => Camera = Camera.ZoomAt(screenPoint, zoomFactor);

    public void HandleWheel(Point2 screenPoint, double wheelDelta, bool zoomCanvas)
    {
        if (!zoomCanvas && TryScrollNodeAt(screenPoint, -wheelDelta * 0.6))
        {
            return;
        }

        if (!zoomCanvas)
        {
            return;
        }

        var zoomFactor = wheelDelta > 0 ? 1.12 : 0.89;
        ZoomAt(screenPoint, zoomFactor);
    }

    public void FitToGraph(Size2 viewportSize) => Camera = CameraState.Fit(GraphBounds, viewportSize, 48);

    public void FitToNode(DiffNode node, Size2 viewportSize) => Camera = CameraState.Fit(node.Bounds, viewportSize, 64);

    public DiffNode? HitTestNode(Point2 screenPoint)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);

        for (var nodeIndex = nodes.Count - 1; nodeIndex >= 0; nodeIndex--)
        {
            if (nodes[nodeIndex].Bounds.Contains(worldPoint))
            {
                return nodes[nodeIndex];
            }
        }

        return null;
    }

    public bool TryHitTestResizeHandle(Point2 screenPoint, out DiffNode? node, out DiffNodeResizeHandle handle)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);
        var handleSize = ScreenStableWorldLength(Camera.Scale, ResizeHandleScreenSize);

        for (var nodeIndex = nodes.Count - 1; nodeIndex >= 0; nodeIndex--)
        {
            var candidate = nodes[nodeIndex];
            if (!candidate.Bounds.Inflate(handleSize).Contains(worldPoint))
            {
                continue;
            }

            handle = GetResizeHandle(candidate.Bounds, worldPoint, handleSize);
            if (handle != DiffNodeResizeHandle.None)
            {
                node = candidate;
                return true;
            }
        }

        node = null;
        handle = DiffNodeResizeHandle.None;
        return false;
    }

    public bool TryHitTestTitleBar(Point2 screenPoint, out DiffNode? node)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);
        for (var nodeIndex = nodes.Count - 1; nodeIndex >= 0; nodeIndex--)
        {
            var candidate = nodes[nodeIndex];
            if (candidate.TitleBounds.Contains(worldPoint))
            {
                node = candidate;
                return true;
            }
        }

        node = null;
        return false;
    }

    public bool TryHitTestScrollbarThumb(Point2 screenPoint, out DiffNode? node, out double thumbGrabOffsetY)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);
        var hitPadding = ScreenStableWorldLength(Camera.Scale, 3);
        for (var nodeIndex = nodes.Count - 1; nodeIndex >= 0; nodeIndex--)
        {
            var candidate = nodes[nodeIndex];
            var thumb = candidate.GetScrollbarThumbBounds(Camera.Scale);
            if (!thumb.IsEmpty && thumb.Inflate(hitPadding).Contains(worldPoint))
            {
                node = candidate;
                thumbGrabOffsetY = worldPoint.Y - thumb.Top;
                return true;
            }
        }

        node = null;
        thumbGrabOffsetY = 0;
        return false;
    }

    public bool TryHitTestFontSizeButton(Point2 screenPoint, out DiffNode? node, out DiffNodeFontSizeAction action)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);
        for (var nodeIndex = nodes.Count - 1; nodeIndex >= 0; nodeIndex--)
        {
            var candidate = nodes[nodeIndex];
            if (candidate.GetFontSizeButtonBounds(DiffNodeFontSizeAction.Decrease, Camera.Scale).Contains(worldPoint))
            {
                node = candidate;
                action = DiffNodeFontSizeAction.Decrease;
                return true;
            }

            if (candidate.GetFontSizeButtonBounds(DiffNodeFontSizeAction.Increase, Camera.Scale).Contains(worldPoint))
            {
                node = candidate;
                action = DiffNodeFontSizeAction.Increase;
                return true;
            }
        }

        node = null;
        action = DiffNodeFontSizeAction.Decrease;
        return false;
    }

    public void SelectNode(DiffNode? selectedNode)
    {
        foreach (var node in nodes)
        {
            node.IsSelected = ReferenceEquals(node, selectedNode);
        }
    }

    public void MoveNode(DiffNode node, double deltaX, double deltaY)
    {
        node.Bounds = node.Bounds.Translate(deltaX, deltaY);
        node.IsPinned = true;
    }

    public void MoveNodeTo(DiffNode node, double x, double y)
    {
        node.Bounds = new Rect2(x, y, node.Bounds.Width, node.Bounds.Height);
        node.IsPinned = true;
    }

    public void ResizeNode(DiffNode node, DiffNodeResizeHandle handle, double deltaX, double deltaY)
    {
        if (handle == DiffNodeResizeHandle.None)
        {
            return;
        }

        var left = node.Bounds.Left;
        var top = node.Bounds.Top;
        var right = node.Bounds.Right;
        var bottom = node.Bounds.Bottom;

        if (handle is DiffNodeResizeHandle.Left or DiffNodeResizeHandle.TopLeft or DiffNodeResizeHandle.BottomLeft)
        {
            left += deltaX;
        }

        if (handle is DiffNodeResizeHandle.Right or DiffNodeResizeHandle.TopRight or DiffNodeResizeHandle.BottomRight)
        {
            right += deltaX;
        }

        if (handle is DiffNodeResizeHandle.Top or DiffNodeResizeHandle.TopLeft or DiffNodeResizeHandle.TopRight)
        {
            top += deltaY;
        }

        if (handle is DiffNodeResizeHandle.Bottom or DiffNodeResizeHandle.BottomLeft or DiffNodeResizeHandle.BottomRight)
        {
            bottom += deltaY;
        }

        if (right - left < DiffNode.MinWidth)
        {
            if (handle is DiffNodeResizeHandle.Left or DiffNodeResizeHandle.TopLeft or DiffNodeResizeHandle.BottomLeft)
            {
                left = right - DiffNode.MinWidth;
            }
            else
            {
                right = left + DiffNode.MinWidth;
            }
        }

        if (bottom - top < DiffNode.MinHeight)
        {
            if (handle is DiffNodeResizeHandle.Top or DiffNodeResizeHandle.TopLeft or DiffNodeResizeHandle.TopRight)
            {
                top = bottom - DiffNode.MinHeight;
            }
            else
            {
                bottom = top + DiffNode.MinHeight;
            }
        }

        node.Bounds = new Rect2(left, top, right - left, bottom - top);
        node.IsPinned = true;
        node.ClampScrollOffset();
    }

    public bool TryScrollNodeAt(Point2 screenPoint, double deltaY)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);
        var node = HitTestNode(screenPoint);

        if (node is null || !node.BodyBounds.Contains(worldPoint))
        {
            return false;
        }

        node.ScrollBy(deltaY);
        return true;
    }

    public void DragScrollbarThumb(DiffNode node, double worldY, double thumbGrabOffsetY)
    {
        var body = node.BodyBounds;
        var thumb = node.GetScrollbarThumbBounds(Camera.Scale);
        if (thumb.IsEmpty || node.MaxScrollOffset <= 0)
        {
            return;
        }

        var trackHeight = Math.Max(0, body.Height - thumb.Height);
        if (trackHeight <= 0)
        {
            node.SetScrollOffset(0);
            return;
        }

        var thumbTop = Math.Clamp(worldY - thumbGrabOffsetY, body.Top, body.Bottom - thumb.Height);
        var scrollRatio = (thumbTop - body.Top) / trackHeight;
        node.SetScrollOffset(node.MaxScrollOffset * scrollRatio);
    }

    public void AdjustNodeFontSize(DiffNode node, DiffNodeFontSizeAction action)
    {
        if (action == DiffNodeFontSizeAction.Increase)
        {
            node.IncreaseFontSize();
        }
        else
        {
            node.DecreaseFontSize();
        }

        node.IsPinned = true;
    }

    public void TogglePinned(DiffNode node) => node.IsPinned = !node.IsPinned;

    public DiffCanvasSceneViewState CaptureViewState() => new(
        Camera,
        nodes
            .Select(node => new DiffNodeViewState(node.Document.Id, node.Bounds, node.ScrollOffsetY, node.IsSelected, node.IsPinned, node.FontSize))
            .ToImmutableArray());

    public void ApplyViewState(DiffCanvasSceneViewState viewState)
    {
        Camera = viewState.Camera;
        var nodesByDocumentId = viewState.Nodes.ToDictionary(node => node.DocumentId);
        foreach (var node in nodes)
        {
            if (!nodesByDocumentId.TryGetValue(node.Document.Id, out var nodeState))
            {
                node.IsSelected = false;
                continue;
            }

            node.Bounds = NormalizeBounds(nodeState.Bounds);
            node.IsSelected = nodeState.IsSelected;
            node.IsPinned = nodeState.IsPinned;
            node.SetFontSize(nodeState.FontSize);
            node.RestoreScrollOffset(nodeState.ScrollOffsetY);
        }
    }

    private static DiffNodeResizeHandle GetResizeHandle(Rect2 bounds, Point2 worldPoint, double handleSize)
    {
        var nearLeft = Math.Abs(worldPoint.X - bounds.Left) <= handleSize && worldPoint.Y >= bounds.Top - handleSize && worldPoint.Y <= bounds.Bottom + handleSize;
        var nearRight = Math.Abs(worldPoint.X - bounds.Right) <= handleSize && worldPoint.Y >= bounds.Top - handleSize && worldPoint.Y <= bounds.Bottom + handleSize;
        var nearTop = Math.Abs(worldPoint.Y - bounds.Top) <= handleSize && worldPoint.X >= bounds.Left - handleSize && worldPoint.X <= bounds.Right + handleSize;
        var nearBottom = Math.Abs(worldPoint.Y - bounds.Bottom) <= handleSize && worldPoint.X >= bounds.Left - handleSize && worldPoint.X <= bounds.Right + handleSize;

        return (nearLeft, nearTop, nearRight, nearBottom) switch
        {
            (true, true, _, _) => DiffNodeResizeHandle.TopLeft,
            (_, true, true, _) => DiffNodeResizeHandle.TopRight,
            (true, _, _, true) => DiffNodeResizeHandle.BottomLeft,
            (_, _, true, true) => DiffNodeResizeHandle.BottomRight,
            (true, _, _, _) => DiffNodeResizeHandle.Left,
            (_, true, _, _) => DiffNodeResizeHandle.Top,
            (_, _, true, _) => DiffNodeResizeHandle.Right,
            (_, _, _, true) => DiffNodeResizeHandle.Bottom,
            _ => DiffNodeResizeHandle.None
        };
    }

    private static Rect2 NormalizeBounds(Rect2 bounds) => new(
        bounds.X,
        bounds.Y,
        Math.Max(DiffNode.MinWidth, bounds.Width),
        Math.Max(DiffNode.MinHeight, bounds.Height));

    public ImmutableArray<DiffNodeLayout> GetCurrentLayout() => nodes
        .Select(node => new DiffNodeLayout(node.Document.Id, node.Bounds, node.IsPinned, node.FontSize))
        .ToImmutableArray();

    public ImmutableHashSet<DiffDocumentId> GetPinnedDocumentIds() => nodes
        .Where(node => node.IsPinned)
        .Select(node => node.Document.Id)
        .ToImmutableHashSet();

    public DiffCanvasScene WithAnnotations(ImmutableArray<DiffAnnotation> nextAnnotations, DiffAnnotationVisibilityState annotationVisibility)
    {
        var nextScene = new DiffCanvasScene(nodes, edges, groups, nextAnnotations, annotationVisibility)
        {
            Camera = Camera
        };
        return nextScene;
    }

    public static DiffCanvasScene FromDocuments(
        ImmutableArray<DiffDocumentSnapshot> documents,
        SemanticGraph? semanticGraph = null,
        GraphLayoutResult? layoutResult = null,
        EdgeProjectionOptions? edgeOptions = null,
        ImmutableArray<DiffAnnotation> annotations = default,
        DiffAnnotationVisibilityState? annotationVisibility = null,
        GraphGroupingMode groupingMode = GraphGroupingMode.Folder)
    {
        var nodeWidth = 620.0;
        var nodeHeight = 420.0;
        var layoutByDocumentId = layoutResult?.Nodes.ToDictionary(node => node.DocumentId, node => node);
        var nodes = documents.Select((document, index) =>
        {
            if (layoutByDocumentId is not null && layoutByDocumentId.TryGetValue(document.Id, out var layoutNode))
            {
                var bounds = layoutNode.Bounds;
                return new DiffNode(document, bounds.Width > 0 && bounds.Height > 0 ? bounds : bounds with { Width = nodeWidth, Height = nodeHeight }, layoutNode.IsPinned, layoutNode.FontSize);
            }

            var column = index % Math.Max(1, (int)Math.Ceiling(Math.Sqrt(documents.Length)));
            var row = index / Math.Max(1, (int)Math.Ceiling(Math.Sqrt(documents.Length)));
            return new DiffNode(document, new Rect2(column * 700, row * 500, nodeWidth, nodeHeight));
        }).ToArray();

        var documentIds = documents.Select(document => document.Id.Value).ToHashSet(StringComparer.Ordinal);
        var anchorsById = semanticGraph?.Anchors.ToDictionary(anchor => anchor.Id, StringComparer.Ordinal) ?? [];
        var options = edgeOptions ?? new EdgeProjectionOptions();
        var projectedEdges = semanticGraph?.Edges
            .Where(edge => IsIncluded(edge, options))
            .Select(edge => TryCreateGraphEdge(edge, anchorsById, documentIds))
            .Where(edge => edge is not null)
            .Cast<GraphEdge>() ?? [];
        var edges = BundleEdges(projectedEdges, options).ToArray();
        var groups = BuildGroups(groupingMode, nodes, semanticGraph);

        return new DiffCanvasScene(nodes, edges, groups, annotations, annotationVisibility);
    }

    private static ImmutableArray<GraphGroup> BuildGroups(GraphGroupingMode groupingMode, IReadOnlyList<DiffNode> nodes, SemanticGraph? semanticGraph)
    {
        if (groupingMode == GraphGroupingMode.None || nodes.Count == 0)
        {
            return [];
        }

        var anchorsByDocumentId = semanticGraph?.Anchors
            .GroupBy(anchor => anchor.DocumentId)
            .ToDictionary(group => group.Key, group => group.ToArray()) ?? [];
        var groups = nodes
            .Select(node => (Node: node, Key: CreateGroupKey(groupingMode, node.Document, anchorsByDocumentId.GetValueOrDefault(node.Document.Id) ?? [])))
            .Where(item => !string.IsNullOrWhiteSpace(item.Key.Id))
            .GroupBy(item => item.Key)
            .Where(group => group.Count() >= 2)
            .Select(group => CreateGraphGroup(groupingMode, group.Key, group.Select(item => item.Node).ToArray()))
            .OrderByDescending(group => group.DocumentCount)
            .ThenBy(group => group.Label, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return groups;
    }

    private static GraphGroup CreateGraphGroup(GraphGroupingMode mode, GraphGroupKey key, IReadOnlyList<DiffNode> nodes)
    {
        var bounds = ExpandGroupBounds(Rect2.Union(nodes.Select(node => node.Bounds)));
        return new GraphGroup(
            $"{mode}:{key.Id}",
            mode,
            key.Label,
            bounds,
            nodes.Count,
            nodes.Sum(node => node.Document.Metadata.AddedLines),
            nodes.Sum(node => node.Document.Metadata.DeletedLines),
            key.ColorIndex);
    }

    private static Rect2 ExpandGroupBounds(Rect2 bounds) => bounds.IsEmpty
        ? Rect2.Empty
        : new Rect2(bounds.Left - 34, bounds.Top - 48, bounds.Width + 68, bounds.Height + 82);

    private static GraphGroupKey CreateGroupKey(GraphGroupingMode groupingMode, DiffDocumentSnapshot document, IReadOnlyList<SemanticAnchor> anchors) => groupingMode switch
    {
        GraphGroupingMode.Folder => CreateFolderGroupKey(document.Metadata.Path),
        GraphGroupingMode.Semantic => CreateSemanticGroupKey(document, anchors),
        GraphGroupingMode.Language => CreateStableGroupKey($"language:{NormalizeGroupLabel(document.Metadata.Language, "Other")}", NormalizeGroupLabel(document.Metadata.Language, "Other")),
        GraphGroupingMode.Status => CreateStableGroupKey($"status:{document.Metadata.Status}", FormatStatusGroup(document.Metadata.Status)),
        _ => GraphGroupKey.Empty
    };

    private static GraphGroupKey CreateFolderGroupKey(string path)
    {
        var normalizedPath = path.Replace('\\', '/');
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var label = segments.Length switch
        {
            0 => "Repository root",
            1 => "Repository root",
            >= 2 when IsSourceRoot(segments[0]) => $"{segments[0]}/{segments[1]}",
            _ => segments[0]
        };

        return CreateStableGroupKey($"folder:{label}", label);
    }

    private static GraphGroupKey CreateSemanticGroupKey(DiffDocumentSnapshot document, IReadOnlyList<SemanticAnchor> anchors)
    {
        var path = document.Metadata.Path.Replace('\\', '/');
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);

        if (IsProjectLikeFile(extension, fileName))
        {
            return CreateStableGroupKey("semantic:projects", "Projects");
        }

        if (path.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            return CreateStableGroupKey("semantic:tests", "Tests");
        }

        if (anchors.Any(anchor => anchor.Kind is SemanticAnchorKind.XamlRoot or SemanticAnchorKind.XamlName) || document.Metadata.Language.Contains("XAML", StringComparison.OrdinalIgnoreCase))
        {
            return CreateStableGroupKey("semantic:xaml", "UI/XAML");
        }

        if (anchors.Any(anchor => anchor.Kind == SemanticAnchorKind.Resource))
        {
            return CreateStableGroupKey("semantic:resources", "Resources");
        }

        if (anchors.Any(anchor => anchor.Kind is SemanticAnchorKind.Type or SemanticAnchorKind.Member or SemanticAnchorKind.Namespace) || string.Equals(document.Metadata.Language, "C#", StringComparison.OrdinalIgnoreCase))
        {
            return CreateStableGroupKey("semantic:csharp", "C# symbols");
        }

        if (extension is ".md" or ".txt" or ".rst")
        {
            return CreateStableGroupKey("semantic:docs", "Docs");
        }

        if (extension is ".json" or ".xml" or ".config" or ".props" or ".targets" or ".yml" or ".yaml")
        {
            return CreateStableGroupKey("semantic:config", "Config");
        }

        var label = NormalizeGroupLabel(document.Metadata.Language, "Other");
        return CreateStableGroupKey($"semantic:{label}", label);
    }

    private static bool IsProjectLikeFile(string extension, string fileName) =>
        extension is ".csproj" or ".sln" or ".slnx" or ".fsproj" or ".vbproj" ||
        string.Equals(fileName, "Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, "Directory.Build.targets", StringComparison.OrdinalIgnoreCase);

    private static bool IsSourceRoot(string segment) =>
        segment.Equals("src", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("test", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("samples", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("examples", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeGroupLabel(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static GraphGroupKey CreateStableGroupKey(string id, string label) => new(id, label, StableColorIndex(id));

    private static int StableColorIndex(string value)
    {
        var hash = 17;
        foreach (var character in value)
        {
            hash = unchecked(hash * 31 + character);
        }

        return (hash & int.MaxValue) % 8;
    }

    private static string FormatStatusGroup(DiffFileStatus status) => status switch
    {
        DiffFileStatus.Added => "Added",
        DiffFileStatus.Deleted => "Deleted",
        DiffFileStatus.Renamed => "Renamed",
        DiffFileStatus.Copied => "Copied",
        DiffFileStatus.Untracked => "Untracked",
        DiffFileStatus.Conflicted => "Conflicted",
        DiffFileStatus.Modified => "Modified",
        _ => "Unchanged"
    };

    private static bool IsIncluded(SemanticEdge edge, EdgeProjectionOptions options)
    {
        if (edge.Confidence < options.MinimumConfidence)
        {
            return false;
        }

        return options.IncludedEdgeKinds is null || options.IncludedEdgeKinds.Contains(edge.Kind);
    }

    private static IEnumerable<GraphEdge> BundleEdges(IEnumerable<GraphEdge> edges, EdgeProjectionOptions options)
    {
        if (!options.BundleParallelEdges)
        {
            return edges
                .OrderByDescending(edge => edge.Confidence)
                .Take(Math.Max(1, options.MaxEdgesPerDocumentPair));
        }

        return edges
            .GroupBy(edge => (edge.SourceNodeId, edge.TargetNodeId))
            .SelectMany(group => group
                .GroupBy(edge => edge.Kind)
                .Select(kindGroup => CreateBundledEdge(kindGroup))
                .OrderByDescending(edge => edge.Confidence)
                .Take(Math.Max(1, options.MaxEdgesPerDocumentPair)));
    }

    private static GraphEdge CreateBundledEdge(IEnumerable<GraphEdge> edges)
    {
        var orderedEdges = edges.OrderByDescending(edge => edge.Confidence).ToArray();
        var strongest = orderedEdges[0];
        var bundleCount = orderedEdges.Sum(edge => Math.Max(1, edge.BundleCount));
        var label = bundleCount > 1 ? $"{bundleCount} semantic links" : strongest.Label;
        return strongest with { Label = label, BundleCount = bundleCount };
    }

    private static GraphEdge? TryCreateGraphEdge(
        SemanticEdge semanticEdge,
        IReadOnlyDictionary<string, SemanticAnchor> anchorsById,
        HashSet<string> documentIds)
    {
        if (!anchorsById.TryGetValue(semanticEdge.SourceAnchorId, out var sourceAnchor) ||
            !anchorsById.TryGetValue(semanticEdge.TargetAnchorId, out var targetAnchor) ||
            sourceAnchor.DocumentId == targetAnchor.DocumentId ||
            !documentIds.Contains(sourceAnchor.DocumentId.Value) ||
            !documentIds.Contains(targetAnchor.DocumentId.Value))
        {
            return null;
        }

        return new GraphEdge(sourceAnchor.DocumentId.Value, targetAnchor.DocumentId.Value, semanticEdge.Kind, semanticEdge.Confidence, semanticEdge.Label);
    }

    private sealed record GraphGroupKey(string Id, string Label, int ColorIndex)
    {
        public static GraphGroupKey Empty { get; } = new(string.Empty, string.Empty, 0);
    }
}