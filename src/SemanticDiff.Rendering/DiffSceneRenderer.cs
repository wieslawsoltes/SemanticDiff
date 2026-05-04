using System.Collections.Immutable;
using SemanticDiff.Core;
using SkiaSharp;

namespace SemanticDiff.Rendering;

public enum DiffCanvasColorTheme
{
    Dark,
    Light
}

public enum DiffSceneRenderMode
{
    Normal,
    Interactive
}

public sealed record DiffSceneRenderStats(
    int TotalNodeCount,
    int DrawnNodeCount,
    int DetailedNodeCount,
    int TotalEdgeCount,
    int DrawnEdgeCount,
    int CachedEdgePathCount)
{
    public static DiffSceneRenderStats Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

public sealed class DiffSceneRenderer
{
    private const string TextEllipsis = "...";
    private const string TabReplacement = "    ";
    private const float TitlePathLeftInset = 40;
    private const float TitleMetadataGap = 8;
    private const float TitlePinnedLabelLeftInset = 188;
    private const double DetailedBodyMinimumFontPixels = 6.5;
    private const double DetailedBodyMinimumNodeWidthPixels = 360;
    private const double DetailedBodyMinimumBodyHeightPixels = 110;
    private const int DetailedBodyVisibleNodeBudget = 140;
    private const int OverviewMinimumBucketCount = 8;
    private const int OverviewMaximumBucketCount = 160;

    private RenderSceneCache? renderCache;

    private static readonly RendererPalette DarkPalette = new(
        Background: new SKColor(11, 15, 20),
        GridLine: new SKColor(25, 32, 42, 150),
        NodeBackground: new SKColor(16, 23, 32),
        NodeBorder: new SKColor(44, 55, 69),
        TitleBackground: new SKColor(20, 28, 39),
        GutterBackground: new SKColor(13, 19, 26),
        TextColor: new SKColor(222, 229, 238),
        MutedTextColor: new SKColor(132, 145, 160),
        AddedBackground: new SKColor(17, 54, 42),
        DeletedBackground: new SKColor(65, 31, 37),
        IgnoredBackground: new SKColor(33, 39, 48),
        MovedBackground: new SKColor(51, 43, 21),
        ConflictBackground: new SKColor(92, 50, 26),
        InlineAddedBackground: new SKColor(69, 143, 96, 130),
        InlineDeletedBackground: new SKColor(164, 77, 88, 130),
        InlineChangedBackground: new SKColor(207, 159, 63, 120),
        MetadataBackground: new SKColor(28, 38, 50),
        EdgeColor: new SKColor(88, 166, 214),
        ScrollbarThumb: new SKColor(128, 138, 153, 180),
        Keyword: new SKColor(123, 185, 245),
        Type: new SKColor(224, 192, 122),
        String: new SKColor(141, 207, 139),
        Comment: new SKColor(112, 122, 138),
        Number: new SKColor(198, 160, 246),
        Function: new SKColor(137, 221, 255),
        Property: new SKColor(127, 203, 202),
        Tag: new SKColor(245, 180, 97),
        Invalid: new SKColor(255, 107, 129),
        NodeAdded: new SKColor(46, 160, 67),
        NodeModified: new SKColor(210, 153, 34),
        NodeDeleted: new SKColor(248, 81, 73),
        NodeRenamed: new SKColor(163, 113, 247),
        NodeUntracked: new SKColor(63, 185, 80),
        NodeConflict: new SKColor(249, 115, 22));

    private static readonly RendererPalette LightPalette = new(
        Background: new SKColor(247, 249, 252),
        GridLine: new SKColor(208, 218, 230, 160),
        NodeBackground: new SKColor(255, 255, 255),
        NodeBorder: new SKColor(184, 197, 212),
        TitleBackground: new SKColor(235, 241, 247),
        GutterBackground: new SKColor(243, 246, 250),
        TextColor: new SKColor(20, 32, 51),
        MutedTextColor: new SKColor(104, 118, 135),
        AddedBackground: new SKColor(219, 244, 231),
        DeletedBackground: new SKColor(252, 226, 229),
        IgnoredBackground: new SKColor(232, 237, 244),
        MovedBackground: new SKColor(252, 240, 203),
        ConflictBackground: new SKColor(255, 229, 196),
        InlineAddedBackground: new SKColor(95, 176, 122, 120),
        InlineDeletedBackground: new SKColor(218, 101, 112, 120),
        InlineChangedBackground: new SKColor(218, 164, 48, 105),
        MetadataBackground: new SKColor(229, 236, 245),
        EdgeColor: new SKColor(22, 107, 154),
        ScrollbarThumb: new SKColor(99, 115, 132, 170),
        Keyword: new SKColor(14, 91, 146),
        Type: new SKColor(143, 92, 24),
        String: new SKColor(28, 116, 75),
        Comment: new SKColor(116, 130, 146),
        Number: new SKColor(112, 76, 164),
        Function: new SKColor(20, 110, 145),
        Property: new SKColor(23, 124, 126),
        Tag: new SKColor(162, 86, 20),
        Invalid: new SKColor(190, 44, 64),
        NodeAdded: new SKColor(26, 127, 55),
        NodeModified: new SKColor(154, 103, 0),
        NodeDeleted: new SKColor(207, 34, 46),
        NodeRenamed: new SKColor(130, 80, 223),
        NodeUntracked: new SKColor(26, 127, 55),
        NodeConflict: new SKColor(188, 76, 0));

    public DiffSceneRenderStats LastRenderStats { get; private set; } = DiffSceneRenderStats.Empty;

    public void Render(
        SKCanvas canvas,
        SKSize canvasSize,
        DiffCanvasScene scene,
        DiffCanvasColorTheme colorTheme = DiffCanvasColorTheme.Dark,
        DiffSceneRenderMode renderMode = DiffSceneRenderMode.Normal,
        bool useLevelOfDetail = true)
    {
        var palette = colorTheme == DiffCanvasColorTheme.Light ? LightPalette : DarkPalette;
        canvas.Clear(palette.Background);
        DrawGrid(canvas, canvasSize, scene.Camera, palette);
        var useInteractiveRendering = useLevelOfDetail && renderMode == DiffSceneRenderMode.Interactive;
        var cache = GetRenderCache(scene);
        using var textResources = new RenderTextResources(palette);

        var worldViewport = GetWorldViewport(scene.Camera, canvasSize)
            .Inflate(DiffCanvasScene.ScreenStableWorldLength(scene.Camera.Scale, 96));
        var nodeSource = cache.Nodes;
        var visibleNodes = new List<DiffNode>(Math.Min(nodeSource.Length, DetailedBodyVisibleNodeBudget * 2));
        foreach (var node in nodeSource)
        {
            if (node.Bounds.Intersects(worldViewport))
            {
                visibleNodes.Add(node);
            }
        }

        var visibleGroups = new List<GraphGroup>();
        foreach (var group in scene.Groups)
        {
            if (group.Bounds.Intersects(worldViewport))
            {
                visibleGroups.Add(group);
            }
        }
        var drawnEdges = 0;
        canvas.Save();
        canvas.Translate((float)scene.Camera.OffsetX, (float)scene.Camera.OffsetY);
        canvas.Scale((float)scene.Camera.Scale);

        foreach (var group in visibleGroups)
        {
            DrawGroupRegion(canvas, group, palette, scene.Camera.Scale);
        }

        using var edgePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
        if (!useInteractiveRendering)
        {
            foreach (var edge in cache.Edges)
            {
                if (!edge.Bounds.Inflate(DiffCanvasScene.ScreenStableWorldLength(scene.Camera.Scale, 80)).Intersects(worldViewport))
                {
                    continue;
                }

                DrawEdge(canvas, edge, edgePaint, palette);
                drawnEdges++;
            }
        }
        else
        {
            drawnEdges = DrawInteractiveEdges(canvas, cache.Edges, scene.Camera.Scale, worldViewport, edgePaint, palette);
        }

        var detailedNodeCount = 0;
        foreach (var node in visibleNodes)
        {
            var documentCache = cache.Documents.GetValueOrDefault(node.Document.Id) ?? DocumentRenderCache.Create(node.Document, []);
            var bodyDetail = ResolveBodyDetail(node, scene.Camera.Scale, visibleNodes.Count, useInteractiveRendering, useLevelOfDetail);
            if (bodyDetail == NodeBodyDetail.Full)
            {
                detailedNodeCount++;
            }

            DrawNode(
                canvas,
                node,
                palette,
                documentCache,
                textResources,
                scene.Camera.Scale,
                scene.HoveredAnnotationId,
                bodyDetail);
        }

        LastRenderStats = new(
            scene.Nodes.Count,
            visibleNodes.Count,
            detailedNodeCount,
            scene.Edges.Count,
            drawnEdges,
            useInteractiveRendering ? 0 : cache.Edges.Length);
        canvas.Restore();
        DrawGroupLabels(canvas, scene.Camera, visibleGroups, palette, textResources, canvasSize);
        DrawFontControls(canvas, scene.Camera, visibleNodes, palette, textResources, canvasSize);
    }

    private RenderSceneCache GetRenderCache(DiffCanvasScene scene)
    {
        if (renderCache is not null && renderCache.Matches(scene))
        {
            return renderCache;
        }

        var previousCache = renderCache;
        renderCache = RenderSceneCache.Create(scene, previousCache);
        previousCache?.Dispose();
        return renderCache;
    }

    private static void DrawGroupRegion(SKCanvas canvas, GraphGroup group, RendererPalette palette, double cameraScale)
    {
        var color = GroupColor(group, palette);
        var rect = ToRect(group.Bounds);
        var radius = (float)DiffCanvasScene.ScreenStableWorldLength(cameraScale, 10);
        using var fillPaint = new SKPaint { Color = color.WithAlpha(24), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var strokePaint = new SKPaint
        {
            Color = color.WithAlpha(145),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)DiffCanvasScene.ScreenStableWorldLength(cameraScale, 1.4),
            IsAntialias = true
        };

        canvas.DrawRoundRect(rect, radius, radius, fillPaint);
        canvas.DrawRoundRect(rect, radius, radius, strokePaint);
    }

    private static void DrawGroupLabels(SKCanvas canvas, CameraState camera, IReadOnlyList<GraphGroup> groups, RendererPalette palette, RenderTextResources textResources, SKSize canvasSize)
    {
        if (groups.Count == 0)
        {
            return;
        }

        var textStyle = textResources.GetUiTextStyle(18, palette.TextColor, true);
        foreach (var group in groups)
        {
            var color = GroupColor(group, palette);
            var labelPoint = camera.WorldToScreen(new Point2(group.Bounds.Left + 14, group.Bounds.Top + 9));
            var label = group.SummaryText;
            var width = Math.Clamp(textStyle.MeasureText(label) + 30, 104, 420);
            var rect = SKRect.Create((float)labelPoint.X, (float)labelPoint.Y, width, 32);
            if (rect.Right < 0 || rect.Left > canvasSize.Width || rect.Bottom < 0 || rect.Top > canvasSize.Height)
            {
                continue;
            }

            using var chipFill = new SKPaint { Color = palette.Background.WithAlpha(232), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var chipStroke = new SKPaint { Color = color.WithAlpha(210), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true };
            using var accent = new SKPaint { Color = color.WithAlpha(220), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(rect, 5, 5, chipFill);
            canvas.DrawRoundRect(rect, 5, 5, chipStroke);
            canvas.DrawRoundRect(SKRect.Create(rect.Left + 7, rect.Top + 7, 5, 18), 2.5f, 2.5f, accent);
            canvas.DrawText(label, rect.Left + 18, rect.Top + 23, textStyle.Font, textStyle.Paint);
        }
    }

    private static Rect2 GetWorldViewport(CameraState camera, SKSize canvasSize)
    {
        if (canvasSize.Width <= 0 || canvasSize.Height <= 0)
        {
            return Rect2.Empty;
        }

        var topLeft = camera.ScreenToWorld(Point2.Zero);
        var bottomRight = camera.ScreenToWorld(new Point2(canvasSize.Width, canvasSize.Height));
        var left = Math.Min(topLeft.X, bottomRight.X);
        var top = Math.Min(topLeft.Y, bottomRight.Y);
        return new Rect2(left, top, Math.Abs(bottomRight.X - topLeft.X), Math.Abs(bottomRight.Y - topLeft.Y));
    }

    private static void DrawGrid(SKCanvas canvas, SKSize canvasSize, CameraState camera, RendererPalette palette)
    {
        using var paint = new SKPaint { Color = palette.GridLine, StrokeWidth = 1, IsAntialias = false };
        var spacing = Math.Max(32, 96 * camera.Scale);
        var startX = camera.OffsetX % spacing;
        var startY = camera.OffsetY % spacing;

        for (var screenX = startX; screenX < canvasSize.Width; screenX += spacing)
        {
            canvas.DrawLine((float)screenX, 0, (float)screenX, canvasSize.Height, paint);
        }

        for (var screenY = startY; screenY < canvasSize.Height; screenY += spacing)
        {
            canvas.DrawLine(0, (float)screenY, canvasSize.Width, (float)screenY, paint);
        }
    }

    private static void DrawEdge(SKCanvas canvas, RenderGraphEdge edge, SKPaint paint, RendererPalette palette)
    {
        paint.Color = EdgeColorForKind(edge.Edge.Kind, palette).WithAlpha((byte)Math.Clamp(edge.Edge.Confidence * 220, 64, 220));
        paint.StrokeWidth = (float)Math.Clamp(1.5 + Math.Log2(Math.Max(1, edge.Edge.BundleCount)), 2, 5);
        using var path = edge.Curve.CreatePath();
        canvas.DrawPath(path, paint);
    }

    private static int DrawInteractiveEdges(
        SKCanvas canvas,
        ReadOnlySpan<RenderGraphEdge> edges,
        double cameraScale,
        Rect2 worldViewport,
        SKPaint paint,
        RendererPalette palette)
    {
        if (edges.Length == 0)
        {
            return 0;
        }

        var drawn = 0;
        paint.Color = palette.EdgeColor.WithAlpha(110);
        paint.StrokeWidth = (float)DiffCanvasScene.ScreenStableWorldLength(cameraScale, 1.5);

        foreach (var edge in edges)
        {
            if (!edge.Bounds.Inflate(DiffCanvasScene.ScreenStableWorldLength(cameraScale, 80)).Intersects(worldViewport))
            {
                continue;
            }

            canvas.DrawLine(
                (float)edge.Source.Bounds.Right,
                (float)edge.Source.Bounds.Center.Y,
                (float)edge.Target.Bounds.Left,
                (float)edge.Target.Bounds.Center.Y,
                paint);
            drawn++;
        }

        return drawn;
    }

    private static EdgeCurve CreateEdgeCurve(DiffNode source, DiffNode target)
    {
        var sourcePoint = new SKPoint((float)source.Bounds.Right, (float)source.Bounds.Center.Y);
        var targetPoint = new SKPoint((float)target.Bounds.Left, (float)target.Bounds.Center.Y);
        var controlOffset = Math.Max(120, Math.Abs(targetPoint.X - sourcePoint.X) * 0.35f);
        return new EdgeCurve(
            sourcePoint,
            new SKPoint(sourcePoint.X + controlOffset, sourcePoint.Y),
            new SKPoint(targetPoint.X - controlOffset, targetPoint.Y),
            targetPoint);
    }

    private readonly record struct EdgeCurve(SKPoint Start, SKPoint Control1, SKPoint Control2, SKPoint End)
    {
        public SKPath CreatePath()
        {
            var path = new SKPath();
            path.MoveTo(Start);
            path.CubicTo(Control1, Control2, End);
            return path;
        }
    }

    private static Rect2 GetEdgeBounds(DiffNode source, DiffNode target)
    {
        return GetEdgeBounds(CreateEdgeCurve(source, target));
    }

    private static Rect2 GetEdgeBounds(EdgeCurve curve)
    {
        var left = Math.Min(Math.Min(curve.Start.X, curve.End.X), Math.Min(curve.Control1.X, curve.Control2.X));
        var right = Math.Max(Math.Max(curve.Start.X, curve.End.X), Math.Max(curve.Control1.X, curve.Control2.X));
        var top = Math.Min(curve.Start.Y, curve.End.Y);
        var bottom = Math.Max(curve.Start.Y, curve.End.Y);
        return new Rect2(left, top, right - left, bottom - top);
    }

    private static void DrawNode(
        SKCanvas canvas,
        DiffNode node,
        RendererPalette palette,
        DocumentRenderCache documentCache,
        RenderTextResources textResources,
        double cameraScale,
        string? hoveredAnnotationId,
        NodeBodyDetail bodyDetail)
    {
        var bounds = ToRect(node.Bounds);
        var statusColor = NodeStatusColor(node.DiffDocument.Metadata.Status, palette);
        using var backgroundPaint = new SKPaint { Color = palette.NodeBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var borderPaint = new SKPaint { Color = node.IsSelected ? palette.EdgeColor : statusColor.WithAlpha(210), Style = SKPaintStyle.Stroke, StrokeWidth = node.IsSelected ? 3 : 1.8f, IsAntialias = true };

        canvas.DrawRoundRect(bounds, 6, 6, backgroundPaint);
        DrawNodeStatusAccent(canvas, bounds, statusColor);
        canvas.DrawRoundRect(bounds, 6, 6, borderPaint);
        DrawTitle(canvas, node, palette, textResources, cameraScale);
        if (bodyDetail == NodeBodyDetail.Full)
        {
            DrawDocumentBody(canvas, node, palette, documentCache, textResources, cameraScale, hoveredAnnotationId);
        }
        else
        {
            DrawOverviewDocumentBody(canvas, node, palette, documentCache, cameraScale);
        }

        DrawFooter(canvas, node, palette, documentCache.NodeAnnotations, textResources, hoveredAnnotationId);
        DrawResizeHandles(canvas, node, palette, cameraScale);
    }

    private static NodeBodyDetail ResolveBodyDetail(DiffNode node, double cameraScale, int visibleNodeCount, bool useInteractiveRendering, bool useLevelOfDetail)
    {
        if (!useLevelOfDetail)
        {
            return NodeBodyDetail.Full;
        }

        if (useInteractiveRendering)
        {
            return NodeBodyDetail.Overview;
        }

        var nodeWidthPixels = node.Bounds.Width * cameraScale;
        var bodyHeightPixels = node.BodyBounds.Height * cameraScale;
        var fontPixels = node.FontSize * cameraScale;
        var readable = nodeWidthPixels >= DetailedBodyMinimumNodeWidthPixels &&
            bodyHeightPixels >= DetailedBodyMinimumBodyHeightPixels &&
            fontPixels >= DetailedBodyMinimumFontPixels;

        if (!readable)
        {
            return NodeBodyDetail.Overview;
        }

        if (visibleNodeCount > DetailedBodyVisibleNodeBudget && !node.IsSelected && !node.IsPinned)
        {
            return NodeBodyDetail.Overview;
        }

        return NodeBodyDetail.Full;
    }

    private static void DrawNodeStatusAccent(SKCanvas canvas, SKRect bounds, SKColor statusColor)
    {
        using var paint = new SKPaint { Color = statusColor, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(SKRect.Create(bounds.Left + 1, bounds.Top + 5, 5, bounds.Height - 10), 2.5f, 2.5f, paint);
    }

    private static void DrawResizeHandles(SKCanvas canvas, DiffNode node, RendererPalette palette, double cameraScale)
    {
        if (!node.IsSelected)
        {
            return;
        }

        var handleSize = DiffCanvasScene.ScreenStableWorldLength(cameraScale, DiffCanvasScene.ResizeHandleScreenSize);
        var halfHandle = handleSize / 2;
        var cornerRadius = (float)(2 / Math.Max(cameraScale, 0.01));
        using var fillPaint = new SKPaint { Color = palette.NodeBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var strokePaint = new SKPaint { Color = palette.EdgeColor, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(1.2 / Math.Max(cameraScale, 0.01)), IsAntialias = true };
        DrawResizeHandle(canvas, node.Bounds.Left, node.Bounds.Top, halfHandle, handleSize, cornerRadius, fillPaint, strokePaint);
        DrawResizeHandle(canvas, node.Bounds.Center.X, node.Bounds.Top, halfHandle, handleSize, cornerRadius, fillPaint, strokePaint);
        DrawResizeHandle(canvas, node.Bounds.Right, node.Bounds.Top, halfHandle, handleSize, cornerRadius, fillPaint, strokePaint);
        DrawResizeHandle(canvas, node.Bounds.Right, node.Bounds.Center.Y, halfHandle, handleSize, cornerRadius, fillPaint, strokePaint);
        DrawResizeHandle(canvas, node.Bounds.Right, node.Bounds.Bottom, halfHandle, handleSize, cornerRadius, fillPaint, strokePaint);
        DrawResizeHandle(canvas, node.Bounds.Center.X, node.Bounds.Bottom, halfHandle, handleSize, cornerRadius, fillPaint, strokePaint);
        DrawResizeHandle(canvas, node.Bounds.Left, node.Bounds.Bottom, halfHandle, handleSize, cornerRadius, fillPaint, strokePaint);
        DrawResizeHandle(canvas, node.Bounds.Left, node.Bounds.Center.Y, halfHandle, handleSize, cornerRadius, fillPaint, strokePaint);
    }

    private static void DrawResizeHandle(
        SKCanvas canvas,
        double x,
        double y,
        double halfHandle,
        double handleSize,
        float cornerRadius,
        SKPaint fillPaint,
        SKPaint strokePaint)
    {
        var rect = SKRect.Create((float)(x - halfHandle), (float)(y - halfHandle), (float)handleSize, (float)handleSize);
        canvas.DrawRoundRect(rect, cornerRadius, cornerRadius, fillPaint);
        canvas.DrawRoundRect(rect, cornerRadius, cornerRadius, strokePaint);
    }

    private static void DrawTitle(SKCanvas canvas, DiffNode node, RendererPalette palette, RenderTextResources textResources, double cameraScale)
    {
        var titleRect = SKRect.Create((float)node.Bounds.X, (float)node.Bounds.Y, (float)node.Bounds.Width, (float)DiffNode.TitleHeight);
        var metadata = node.DiffDocument.Metadata;
        var statusColor = NodeStatusColor(metadata.Status, palette);
        using var paint = new SKPaint { Color = palette.TitleBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        var textStyle = textResources.GetUiTextStyle(17, palette.TextColor, true);
        var metaStyle = textResources.GetUiTextStyle(11.5f, palette.MutedTextColor, false);
        using var statusPaint = new SKPaint { Color = statusColor, Style = SKPaintStyle.Fill, IsAntialias = true };
        var statusTextStyle = textResources.GetUiTextStyle(11, SKColors.White, true);

        canvas.Save();
        canvas.ClipRect(titleRect);
        canvas.DrawRoundRect(titleRect, 6, 6, paint);
        canvas.DrawRect(SKRect.Create(titleRect.Left, titleRect.Bottom - 6, titleRect.Width, 6), paint);
        var statusRect = SKRect.Create(titleRect.Left + 11, titleRect.Top + 7, 21, 18);
        canvas.DrawRoundRect(statusRect, 4, 4, statusPaint);
        canvas.DrawText(StatusText(metadata.Status), statusRect.Left + 6, statusRect.Top + 13, statusTextStyle.Font, statusTextStyle.Paint);
        var pathTextX = titleRect.Left + TitlePathLeftInset;
        var pathTextRight = GetTitlePathRightEdge(node, titleRect, cameraScale);
        var pathMaxWidth = Math.Max(0, pathTextRight - pathTextX);
        var pathText = textStyle.MiddleEllipsize(metadata.Path, pathMaxWidth);
        canvas.DrawText(pathText, pathTextX, titleRect.Top + 23, textStyle.Font, textStyle.Paint);
        if (node.IsPinned)
        {
            canvas.DrawText("PIN", titleRect.Right - TitlePinnedLabelLeftInset, titleRect.Top + 20, metaStyle.Font, metaStyle.Paint);
        }

        canvas.DrawText(node.LineCountText, titleRect.Right - (float)DiffNode.FontControlLineCountInset, titleRect.Top + 20, metaStyle.Font, metaStyle.Paint);
        canvas.Restore();
    }

    private static float GetTitlePathRightEdge(DiffNode node, SKRect titleRect, double cameraScale)
    {
        var metadataLeft = titleRect.Right - (float)DiffNode.FontControlLineCountInset;
        if (node.IsPinned)
        {
            metadataLeft = Math.Min(metadataLeft, titleRect.Right - TitlePinnedLabelLeftInset);
        }

        if (node.CanShowFontSizeButtons(cameraScale))
        {
            var decreaseButton = node.GetFontSizeButtonBounds(DiffNodeFontSizeAction.Decrease, cameraScale);
            if (!decreaseButton.IsEmpty)
            {
                metadataLeft = Math.Min(metadataLeft, (float)decreaseButton.Left);
            }
        }

        return Math.Max(titleRect.Left + TitlePathLeftInset, metadataLeft - TitleMetadataGap);
    }

    private static void DrawFontControls(SKCanvas canvas, CameraState camera, IReadOnlyList<DiffNode> nodes, RendererPalette palette, RenderTextResources textResources, SKSize canvasSize)
    {
        using var fillPaint = new SKPaint { Color = palette.NodeBackground.WithAlpha(230), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var strokePaint = new SKPaint { Color = palette.NodeBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        var textStyle = textResources.GetUiTextStyle(11, palette.TextColor, true);

        foreach (var node in nodes)
        {
            if (!node.CanShowFontSizeButtons(camera.Scale))
            {
                continue;
            }

            DrawFontButton(canvas, ToScreenRect(camera, node.GetFontSizeButtonBounds(DiffNodeFontSizeAction.Decrease, camera.Scale)), "-", fillPaint, strokePaint, textStyle, canvasSize);
            DrawFontButton(canvas, ToScreenRect(camera, node.GetFontSizeButtonBounds(DiffNodeFontSizeAction.Increase, camera.Scale)), "+", fillPaint, strokePaint, textStyle, canvasSize);
        }
    }

    private static void DrawFontButton(SKCanvas canvas, SKRect rect, string text, SKPaint fillPaint, SKPaint strokePaint, TextStyle textStyle, SKSize canvasSize)
    {
        if (rect.IsEmpty || rect.Right < 0 || rect.Left > canvasSize.Width || rect.Bottom < 0 || rect.Top > canvasSize.Height)
        {
            return;
        }

        const float radius = 4.5f;
        canvas.DrawRoundRect(rect, radius, radius, fillPaint);
        canvas.DrawRoundRect(rect, radius, radius, strokePaint);
        var textWidth = textStyle.MeasureText(text);
        canvas.DrawText(text, rect.Left + (rect.Width - textWidth) / 2, rect.Top + rect.Height * 0.68f, textStyle.Font, textStyle.Paint);
    }

    private static void DrawDocumentBody(SKCanvas canvas, DiffNode node, RendererPalette palette, DocumentRenderCache documentCache, RenderTextResources textResources, double cameraScale, string? hoveredAnnotationId)
    {
        var body = node.BodyBounds;
        var bodyRect = ToRect(body);
        var gutterWidth = (float)(node.IsShowingFullFile ? DiffNode.FullFileGutterWidth : DiffNode.DiffGutterWidth);
        var markerWidth = (float)(node.IsShowingFullFile ? 0 : DiffNode.MarkerWidth);

        canvas.Save();
        canvas.ClipRect(bodyRect);

        using var gutterPaint = new SKPaint { Color = palette.GutterBackground, Style = SKPaintStyle.Fill };
        using var addedPaint = new SKPaint { Color = palette.AddedBackground, Style = SKPaintStyle.Fill };
        using var deletedPaint = new SKPaint { Color = palette.DeletedBackground, Style = SKPaintStyle.Fill };
        using var ignoredPaint = new SKPaint { Color = palette.IgnoredBackground, Style = SKPaintStyle.Fill };
        using var movedPaint = new SKPaint { Color = palette.MovedBackground, Style = SKPaintStyle.Fill };
        using var conflictPaint = new SKPaint { Color = palette.ConflictBackground, Style = SKPaintStyle.Fill };
        using var inlineAddedPaint = new SKPaint { Color = palette.InlineAddedBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var inlineDeletedPaint = new SKPaint { Color = palette.InlineDeletedBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var inlineChangedPaint = new SKPaint { Color = palette.InlineChangedBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var metadataPaint = new SKPaint { Color = palette.MetadataBackground, Style = SKPaintStyle.Fill };
        using var foldGuidePaint = new SKPaint { Color = palette.MutedTextColor.WithAlpha(90), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = false };
        using var foldMarkerFillPaint = new SKPaint { Color = palette.GutterBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var foldMarkerStrokePaint = new SKPaint { Color = palette.MutedTextColor.WithAlpha(190), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true };
        var codeStyles = textResources.GetCodeTextStyles((float)node.FontSize);
        var lineStyle = textResources.GetCodeTextStyle((float)Math.Max(8, node.FontSize - 1), palette.MutedTextColor);

        canvas.DrawRect(SKRect.Create(bodyRect.Left, bodyRect.Top, gutterWidth, bodyRect.Height), gutterPaint);

        var lineHeight = node.LineHeight;
        var firstLineIndex = Math.Max(0, (int)(node.ScrollOffsetY / lineHeight));
        var visibleLineCount = (int)Math.Ceiling(body.Height / lineHeight) + 2;
        var y = body.Y - node.ScrollOffsetY % lineHeight;

        foreach (var visibleLine in node.GetVisibleRows(firstLineIndex, visibleLineCount))
        {
            var line = visibleLine.Line;
            var lineRect = SKRect.Create(bodyRect.Left, (float)y, bodyRect.Width, (float)lineHeight);
            var codeX = bodyRect.Left + gutterWidth + markerWidth + (float)DiffNode.CodeLeftPadding;
            var lineAnnotations = documentCache.GetLineAnnotations(line.Index);
            var lineLayout = documentCache.GetLineLayout(line.Index);
            var lineNumberText = documentCache.GetLineNumberText(line.Index);
            DrawLineBackground(canvas, line, lineRect, addedPaint, deletedPaint, ignoredPaint, movedPaint, conflictPaint, metadataPaint);
            DrawLineAnnotationBand(canvas, lineAnnotations, lineRect, palette, hoveredAnnotationId);
            if (node.IsShowingFullFile)
            {
                DrawFoldGuide(canvas, node, visibleLine, bodyRect.Left, codeX, (float)y, (float)lineHeight, lineStyle, codeStyles.Default, foldGuidePaint, foldMarkerFillPaint, foldMarkerStrokePaint);
                DrawFullLineNumber(canvas, lineNumberText.Full, bodyRect.Left + gutterWidth - 10, (float)(y + lineHeight * 0.73), lineStyle);
            }
            else
            {
                DrawLineNumber(canvas, lineNumberText, bodyRect.Left + 8, (float)(y + lineHeight * 0.73), lineStyle);
                DrawMarker(canvas, line, bodyRect.Left + gutterWidth, (float)y, markerWidth, (float)lineHeight, lineStyle);
            }

            DrawInlineSpans(canvas, line, codeX, (float)y, codeStyles.Default, inlineAddedPaint, inlineDeletedPaint, inlineChangedPaint);
            DrawCode(canvas, line, lineLayout, codeX, (float)(y + lineHeight * 0.73), codeStyles);
            DrawEditorCaret(canvas, node, visibleLine, codeX, (float)y, (float)lineHeight, codeStyles.Default, palette);
            DrawLineAnnotationMarkers(canvas, lineAnnotations, lineRect, palette, lineStyle, textResources, hoveredAnnotationId);
            y += lineHeight;
        }

        canvas.Restore();
        DrawScrollbar(canvas, node, palette, cameraScale);
    }

    private static void DrawOverviewDocumentBody(SKCanvas canvas, DiffNode node, RendererPalette palette, DocumentRenderCache documentCache, double cameraScale)
    {
        var body = node.BodyBounds;
        if (body.Width <= 0 || body.Height <= 0)
        {
            return;
        }

        var bodyRect = ToRect(body);
        using var gutterPaint = new SKPaint { Color = palette.GutterBackground, Style = SKPaintStyle.Fill };
        using var bucketPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };
        using var annotationPaint = new SKPaint { Color = palette.ReviewAction.WithAlpha(155), Style = SKPaintStyle.Fill, IsAntialias = false };

        canvas.Save();
        canvas.ClipRect(bodyRect);
        canvas.DrawRect(SKRect.Create(bodyRect.Left, bodyRect.Top, 94, bodyRect.Height), gutterPaint);

        var bucketCount = Math.Clamp(
            (int)Math.Ceiling(bodyRect.Height * Math.Max(cameraScale, 0.01)),
            OverviewMinimumBucketCount,
            OverviewMaximumBucketCount);
        var buckets = documentCache.GetOverviewBuckets(bucketCount);
        var bucketHeight = bodyRect.Height / Math.Max(1, buckets.Length);
        for (var index = 0; index < buckets.Length; index++)
        {
            var bucket = buckets[index];
            var y = bodyRect.Top + index * bucketHeight;
            if (bucket.Kind != DiffLineKind.Context)
            {
                bucketPaint.Color = OverviewColor(bucket.Kind, palette);
                canvas.DrawRect(SKRect.Create(bodyRect.Left + 94, y, Math.Max(1, bodyRect.Width - 94), Math.Max(1, bucketHeight)), bucketPaint);
                bucketPaint.Color = LineAccentColor(bucket.Kind, palette).WithAlpha(220);
                canvas.DrawRect(SKRect.Create(bodyRect.Left + 94, y, 5, Math.Max(1, bucketHeight)), bucketPaint);
            }
            else if (bucket.HasText)
            {
                bucketPaint.Color = palette.MutedTextColor.WithAlpha(42);
                canvas.DrawRect(SKRect.Create(bodyRect.Left + 112, y + bucketHeight * 0.35f, Math.Max(18, bodyRect.Width * 0.58f), Math.Max(1, bucketHeight * 0.2f)), bucketPaint);
            }

            if (bucket.HasAnnotation)
            {
                canvas.DrawRect(SKRect.Create(bodyRect.Right - 11, y, 6, Math.Max(1, bucketHeight)), annotationPaint);
            }
        }

        canvas.Restore();
        DrawScrollbar(canvas, node, palette, cameraScale);
    }

    private static void DrawLineBackground(SKCanvas canvas, DiffLine line, SKRect lineRect, SKPaint addedPaint, SKPaint deletedPaint, SKPaint ignoredPaint, SKPaint movedPaint, SKPaint conflictPaint, SKPaint metadataPaint)
    {
        switch (line.Kind)
        {
            case DiffLineKind.Added:
                canvas.DrawRect(lineRect, addedPaint);
                break;
            case DiffLineKind.Deleted:
                canvas.DrawRect(lineRect, deletedPaint);
                break;
            case DiffLineKind.Ignored:
                canvas.DrawRect(lineRect, ignoredPaint);
                break;
            case DiffLineKind.Moved:
                canvas.DrawRect(lineRect, movedPaint);
                break;
            case DiffLineKind.Conflict:
                canvas.DrawRect(lineRect, conflictPaint);
                break;
            case DiffLineKind.Metadata:
            case DiffLineKind.Imaginary:
                canvas.DrawRect(lineRect, metadataPaint);
                break;
        }
    }

    private static void DrawLineNumber(SKCanvas canvas, LineNumberRenderText lineNumberText, float x, float y, TextStyle style)
    {
        canvas.DrawText(lineNumberText.Old, x, y, style.Font, style.Paint);
        canvas.DrawText(lineNumberText.New, x + 42, y, style.Font, style.Paint);
    }

    private static void DrawFullLineNumber(SKCanvas canvas, string text, float right, float y, TextStyle style)
    {
        var width = style.MeasureText(text);
        canvas.DrawText(text, right - width, y, style.Font, style.Paint);
    }

    private static void DrawFoldGuide(
        SKCanvas canvas,
        DiffNode node,
        DiffNodeVisibleLine visibleLine,
        float gutterLeft,
        float codeX,
        float y,
        float lineHeight,
        TextStyle lineStyle,
        TextStyle codeStyle,
        SKPaint guidePaint,
        SKPaint markerFill,
        SKPaint markerStroke)
    {
        var guideCount = Math.Min(5, visibleLine.ActiveFoldRegions.Length);
        for (var index = 0; index < guideCount; index++)
        {
            var region = visibleLine.ActiveFoldRegions[index];
            var x = GetFoldGuideX(node, region, codeX, codeStyle);
            canvas.DrawLine(x, y, x, y + lineHeight, guidePaint);
        }

        if (visibleLine.FoldRegion is null)
        {
            return;
        }

        var centerX = gutterLeft + 23;
        var centerY = y + lineHeight * 0.5f;
        var rect = SKRect.Create(centerX - 5, centerY - 5, 10, 10);
        canvas.DrawRoundRect(rect, 2, 2, markerFill);
        canvas.DrawRoundRect(rect, 2, 2, markerStroke);
        canvas.DrawLine(rect.Left + 2, centerY, rect.Right - 2, centerY, markerStroke);
        if (visibleLine.IsFoldCollapsed)
        {
            canvas.DrawLine(centerX, rect.Top + 2, centerX, rect.Bottom - 2, markerStroke);
            var label = $"... {visibleLine.FoldRegion.CollapsedLineCount:N0}";
            canvas.DrawText(label, codeX + 96, y + lineHeight * 0.73f, lineStyle.Font, lineStyle.Paint);
        }
    }

    private static float GetFoldGuideX(DiffNode node, CodeFoldRegion region, float codeX, TextStyle codeStyle)
    {
        if (region.GuideVisualColumn is { } guideColumn)
        {
            return codeX + Math.Min(160, Math.Max(0, guideColumn)) * codeStyle.MonospaceAdvance + codeStyle.MonospaceAdvance * 0.5f;
        }

        if (region.StartLineIndex < 0 || region.StartLineIndex >= node.Document.LineCount)
        {
            return codeX;
        }

        var text = node.Document.Lines[region.StartLineIndex].Text;
        var indentColumns = CountIndentColumns(text);
        if (!text.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            indentColumns += 4;
        }

        return codeX + Math.Min(80, indentColumns) * codeStyle.MonospaceAdvance;
    }

    private static int CountIndentColumns(string text)
    {
        var columns = 0;
        foreach (var character in text)
        {
            if (character == ' ')
            {
                columns++;
            }
            else if (character == '\t')
            {
                columns += 4;
            }
            else
            {
                break;
            }
        }

        return columns;
    }

    private static void DrawEditorCaret(
        SKCanvas canvas,
        DiffNode node,
        DiffNodeVisibleLine visibleLine,
        float codeX,
        float y,
        float lineHeight,
        TextStyle textStyle,
        RendererPalette palette)
    {
        if (!node.IsEditorFocused || !node.IsEditingActive || visibleLine.LineIndex != node.CaretLineIndex)
        {
            return;
        }

        var x = codeX + node.CaretColumn * textStyle.MonospaceAdvance;
        using var caretPaint = new SKPaint
        {
            Color = palette.EdgeColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.6f,
            IsAntialias = false
        };
        canvas.DrawLine(x, y + 2, x, y + lineHeight - 2, caretPaint);
    }

    private static void DrawMarker(SKCanvas canvas, DiffLine line, float x, float y, float width, float lineHeight, TextStyle style)
    {
        var marker = line.Kind switch
        {
            DiffLineKind.Added => "+",
            DiffLineKind.Deleted => "-",
            DiffLineKind.Ignored => "~",
            DiffLineKind.Moved => ">",
            DiffLineKind.Conflict => "!",
            DiffLineKind.Metadata => "@",
            DiffLineKind.Imaginary => "...",
            _ => string.Empty
        };

        if (marker.Length > 0)
        {
            canvas.DrawText(marker, x + width / 2 - 3, y + lineHeight * 0.73f, style.Font, style.Paint);
        }
    }

    private static void DrawInlineSpans(
        SKCanvas canvas,
        DiffLine line,
        float codeX,
        float lineY,
        TextStyle textStyle,
        SKPaint inlineAddedPaint,
        SKPaint inlineDeletedPaint,
        SKPaint inlineChangedPaint)
    {
        if (line.InlineSpans.IsDefaultOrEmpty || line.Text.Length == 0)
        {
            return;
        }

        var characterWidth = textStyle.MonospaceAdvance;
        foreach (var span in line.InlineSpans)
        {
            if (span.StartColumn < 0 || span.StartColumn >= line.Text.Length || span.Length <= 0)
            {
                continue;
            }

            var length = Math.Min(span.Length, line.Text.Length - span.StartColumn);
            var paint = span.Kind switch
            {
                DiffInlineKind.Inserted => inlineAddedPaint,
                DiffInlineKind.Deleted => inlineDeletedPaint,
                _ => inlineChangedPaint
            };
            var highlightRect = SKRect.Create(codeX + span.StartColumn * characterWidth, lineY + 2, Math.Max(4, length * characterWidth), Math.Max(5, textStyle.Font.Size + 2));
            canvas.DrawRoundRect(highlightRect, 2, 2, paint);
        }
    }

    private static void DrawCode(SKCanvas canvas, DiffLine line, DiffLineRenderLayout lineLayout, float x, float y, CodeTextStyles codeStyles)
    {
        if (lineLayout.Runs.Length == 0)
        {
            canvas.DrawText(line.Text, x, y, codeStyles.Default.Font, codeStyles.Default.Paint);
            return;
        }

        var characterWidth = codeStyles.Default.MonospaceAdvance;
        foreach (var run in lineLayout.Runs)
        {
            var style = run.Token is null ? codeStyles.Default : codeStyles.GetStyle(run.Token);
            DrawCodeTextRange(canvas, run.Text, run.StartColumn, x, y, characterWidth, style);
        }
    }

    private static void DrawCodeTextRange(SKCanvas canvas, string text, int start, float x, float y, float characterWidth, TextStyle style)
    {
        if (text.Length == 0 || start < 0)
        {
            return;
        }

        canvas.DrawText(text, x + start * characterWidth, y, style.Font, style.Paint);
    }

    private static void DrawScrollbar(SKCanvas canvas, DiffNode node, RendererPalette palette, double cameraScale)
    {
        var thumb = node.GetScrollbarThumbBounds(cameraScale);
        if (thumb.IsEmpty)
        {
            return;
        }

        using var paint = new SKPaint { Color = palette.ScrollbarThumb, Style = SKPaintStyle.Fill, IsAntialias = true };
        var thumbRect = ToRect(thumb);
        var radius = (float)Math.Max(1, thumb.Width / 2);
        canvas.DrawRoundRect(thumbRect, radius, radius, paint);
    }

    private static void DrawLineAnnotationBand(SKCanvas canvas, IReadOnlyList<DiffAnnotation> annotations, SKRect lineRect, RendererPalette palette, string? hoveredAnnotationId)
    {
        if (annotations.Count == 0)
        {
            return;
        }

        var primary = GetPrimaryAnnotation(annotations);
        if (primary.Kind is not (DiffAnnotationKind.Navigation or DiffAnnotationKind.ParserDiagnostic or DiffAnnotationKind.Conflict or DiffAnnotationKind.Impact or DiffAnnotationKind.ReviewComment))
        {
            return;
        }

        var alpha = IsAnnotationHovered(annotations, hoveredAnnotationId) ? (byte)118 : (byte)70;
        using var paint = new SKPaint { Color = AnnotationColor(primary.Kind, palette).WithAlpha(alpha), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRect(SKRect.Create(lineRect.Left, lineRect.Top, lineRect.Width, lineRect.Height), paint);
    }

    private static void DrawLineAnnotationMarkers(SKCanvas canvas, IReadOnlyList<DiffAnnotation> annotations, SKRect lineRect, RendererPalette palette, TextStyle textStyle, RenderTextResources textResources, string? hoveredAnnotationId)
    {
        if (annotations.Count == 0)
        {
            return;
        }

        var markerX = lineRect.Right - 14;
        var markerY = lineRect.Top + 4;
        var markerCount = Math.Min(4, annotations.Count);
        for (var index = 0; index < markerCount; index++)
        {
            var annotation = annotations[index];
            var isHovered = IsAnnotationHovered(annotation, hoveredAnnotationId);
            var markerSize = isHovered ? 9 : 7;
            var markerOffset = isHovered ? -1 : 0;
            using var paint = new SKPaint { Color = AnnotationColor(annotation.Kind, palette), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(SKRect.Create(markerX + markerOffset, markerY + markerOffset, markerSize, markerSize), 2.5f, 2.5f, paint);
            markerY += 9;
        }

        DiffAnnotation? labeled = null;
        foreach (var annotation in annotations)
        {
            if (annotation.Kind is DiffAnnotationKind.Navigation or DiffAnnotationKind.ParserDiagnostic or DiffAnnotationKind.Conflict or DiffAnnotationKind.Impact or DiffAnnotationKind.ReviewComment)
            {
                labeled = annotation;
                break;
            }
        }

        if (labeled is null)
        {
            return;
        }

        var isLabeledHovered = IsAnnotationHovered(labeled, hoveredAnnotationId);
        using var chipPaint = new SKPaint { Color = AnnotationColor(labeled.Kind, palette).WithAlpha(isLabeledHovered ? (byte)245 : (byte)210), Style = SKPaintStyle.Fill, IsAntialias = true };
        var labelStyle = textResources.GetUiTextStyle(9.5f, palette.Background, true);
        var label = ShortAnnotationLabel(labeled.Label);
        var width = Math.Clamp(labelStyle.MeasureText(label) + 12, 38, 88);
        var chipRect = SKRect.Create(lineRect.Right - width - 22, lineRect.Top + 2, width, 13);
        canvas.DrawRoundRect(chipRect, 3, 3, chipPaint);
        if (isLabeledHovered)
        {
            using var strokePaint = new SKPaint { Color = palette.Background.WithAlpha(230), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
            canvas.DrawRoundRect(chipRect, 3, 3, strokePaint);
        }

        canvas.DrawText(label, chipRect.Left + 6, chipRect.Top + 10, labelStyle.Font, labelStyle.Paint);
    }

    private static void DrawFooter(SKCanvas canvas, DiffNode node, RendererPalette palette, IReadOnlyList<DiffAnnotation> annotations, RenderTextResources textResources, string? hoveredAnnotationId)
    {
        var textStyle = textResources.GetUiTextStyle(11, palette.MutedTextColor, false);
        var y = (float)(node.Bounds.Bottom - 7);
        var metadata = node.DiffDocument.Metadata;
        var summary = $"+{metadata.AddedLines}  -{metadata.DeletedLines}  {metadata.Language}";
        canvas.DrawText(summary, (float)node.Bounds.X + 12, y, textStyle.Font, textStyle.Paint);
        if (node.IsShowingFullFile || node.IsEditingActive)
        {
            var mode = node.IsEditingActive ? "full edit" : "full";
            canvas.DrawText(mode, (float)node.Bounds.X + 12 + textStyle.MeasureText(summary) + 16, y, textStyle.Font, textStyle.Paint);
        }

        Span<FooterAnnotationGroup> groupedAnnotations = stackalloc FooterAnnotationGroup[4];
        var groupedAnnotationCount = BuildFooterAnnotationGroups(annotations, hoveredAnnotationId, groupedAnnotations);
        if (groupedAnnotationCount == 0)
        {
            return;
        }

        var chipTextStyle = textResources.GetUiTextStyle(9.5f, palette.Background, true);
        var right = (float)node.Bounds.Right - 10;
        for (var index = groupedAnnotationCount - 1; index >= 0; index--)
        {
            var annotation = groupedAnnotations[index];
            var annotationLabel = annotations[annotation.LabelAnnotationIndex].Label;
            var label = annotation.Count > 1 ? $"{ShortAnnotationLabel(annotationLabel)} {annotation.Count}" : ShortAnnotationLabel(annotationLabel);
            var width = Math.Clamp(chipTextStyle.MeasureText(label) + 12, 34, 84);
            right -= width;
            using var chipPaint = new SKPaint { Color = AnnotationColor(annotation.Kind, palette).WithAlpha(annotation.IsHovered ? (byte)245 : (byte)215), Style = SKPaintStyle.Fill, IsAntialias = true };
            var chipRect = SKRect.Create(right, (float)node.Bounds.Bottom - 19, width, 14);
            canvas.DrawRoundRect(chipRect, 3, 3, chipPaint);
            if (annotation.IsHovered)
            {
                using var strokePaint = new SKPaint { Color = palette.Background.WithAlpha(230), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
                canvas.DrawRoundRect(chipRect, 3, 3, strokePaint);
            }

            canvas.DrawText(label, chipRect.Left + 6, chipRect.Top + 10.5f, chipTextStyle.Font, chipTextStyle.Paint);
            right -= 5;
        }
    }

    private static DiffAnnotation GetPrimaryAnnotation(IReadOnlyList<DiffAnnotation> annotations)
    {
        var primary = annotations[0];
        var primaryPriority = AnnotationPriority(primary);
        for (var index = 1; index < annotations.Count; index++)
        {
            var candidate = annotations[index];
            var candidatePriority = AnnotationPriority(candidate);
            if (candidatePriority < primaryPriority)
            {
                primary = candidate;
                primaryPriority = candidatePriority;
            }
        }

        return primary;
    }

    private static int BuildFooterAnnotationGroups(IReadOnlyList<DiffAnnotation> annotations, string? hoveredAnnotationId, Span<FooterAnnotationGroup> groups)
    {
        var count = 0;
        for (var annotationIndex = 0; annotationIndex < annotations.Count; annotationIndex++)
        {
            var annotation = annotations[annotationIndex];
            if (annotation.Target != DiffAnnotationTarget.Node)
            {
                continue;
            }

            var groupIndex = -1;
            for (var index = 0; index < count; index++)
            {
                if (groups[index].Kind == annotation.Kind)
                {
                    groupIndex = index;
                    break;
                }
            }

            if (groupIndex >= 0)
            {
                var group = groups[groupIndex];
                group.Count++;
                group.IsHovered |= IsAnnotationHovered(annotation, hoveredAnnotationId);
                groups[groupIndex] = group;
                continue;
            }

            var candidate = new FooterAnnotationGroup(
                annotation.Kind,
                1,
                annotationIndex,
                IsAnnotationHovered(annotation, hoveredAnnotationId));
            if (count < groups.Length)
            {
                groups[count++] = candidate;
                continue;
            }

            var candidatePriority = AnnotationPriority(candidate.Kind);
            var worstIndex = 0;
            var worstPriority = AnnotationPriority(groups[0].Kind);
            for (var index = 1; index < groups.Length; index++)
            {
                var priority = AnnotationPriority(groups[index].Kind);
                if (priority > worstPriority)
                {
                    worstPriority = priority;
                    worstIndex = index;
                }
            }

            if (candidatePriority < worstPriority)
            {
                groups[worstIndex] = candidate;
            }
        }

        SortFooterAnnotationGroups(groups[..count]);
        return count;
    }

    private static void SortFooterAnnotationGroups(Span<FooterAnnotationGroup> groups)
    {
        for (var index = 1; index < groups.Length; index++)
        {
            var current = groups[index];
            var insertIndex = index - 1;
            while (insertIndex >= 0 && AnnotationPriority(groups[insertIndex].Kind) > AnnotationPriority(current.Kind))
            {
                groups[insertIndex + 1] = groups[insertIndex];
                insertIndex--;
            }

            groups[insertIndex + 1] = current;
        }
    }

    private record struct FooterAnnotationGroup(DiffAnnotationKind Kind, int Count, int LabelAnnotationIndex, bool IsHovered);

    private static SKRect ToRect(Rect2 rectangle) => SKRect.Create((float)rectangle.X, (float)rectangle.Y, (float)rectangle.Width, (float)rectangle.Height);

    private static SKRect ToScreenRect(CameraState camera, Rect2 rectangle)
    {
        if (rectangle.IsEmpty)
        {
            return SKRect.Empty;
        }

        var topLeft = camera.WorldToScreen(new Point2(rectangle.Left, rectangle.Top));
        var bottomRight = camera.WorldToScreen(new Point2(rectangle.Right, rectangle.Bottom));
        var left = Math.Min(topLeft.X, bottomRight.X);
        var top = Math.Min(topLeft.Y, bottomRight.Y);
        return SKRect.Create((float)left, (float)top, (float)Math.Abs(bottomRight.X - topLeft.X), (float)Math.Abs(bottomRight.Y - topLeft.Y));
    }

    private static string StatusText(DiffFileStatus status) => status switch
    {
        DiffFileStatus.Added => "A",
        DiffFileStatus.Deleted => "D",
        DiffFileStatus.Renamed => "R",
        DiffFileStatus.Copied => "C",
        DiffFileStatus.Untracked => "?",
        DiffFileStatus.Conflicted => "!",
        _ => "M"
    };

    private static SKColor NodeStatusColor(DiffFileStatus status, RendererPalette palette) => status switch
    {
        DiffFileStatus.Added => palette.NodeAdded,
        DiffFileStatus.Untracked => palette.NodeUntracked,
        DiffFileStatus.Deleted => palette.NodeDeleted,
        DiffFileStatus.Renamed or DiffFileStatus.Copied => palette.NodeRenamed,
        DiffFileStatus.Conflicted => palette.NodeConflict,
        DiffFileStatus.Modified => palette.NodeModified,
        _ => palette.NodeBorder
    };

    private static SKColor EdgeColorForKind(SemanticEdgeKind kind, RendererPalette palette) => kind switch
    {
        SemanticEdgeKind.XamlClass => palette.Keyword,
        SemanticEdgeKind.TypeInheritance => palette.Type,
        SemanticEdgeKind.Binding => palette.Binding,
        SemanticEdgeKind.Resource => palette.Resource,
        SemanticEdgeKind.PartialClass => palette.PartialClass,
        _ => palette.EdgeColor
    };

    private static SKColor GroupColor(GraphGroup group, RendererPalette palette)
    {
        if (group.Mode == GraphGroupingMode.Status)
        {
            return group.Label switch
            {
                "Added" or "Untracked" => palette.NodeAdded,
                "Deleted" => palette.NodeDeleted,
                "Renamed" or "Copied" => palette.NodeRenamed,
                "Conflicted" => palette.NodeConflict,
                "Modified" => palette.NodeModified,
                _ => palette.EdgeColor
            };
        }

        var colors = new[]
        {
            palette.EdgeColor,
            palette.Type,
            palette.Property,
            palette.String,
            palette.NodeModified,
            palette.NodeRenamed,
            palette.NodeAdded,
            palette.Warning
        };
        return colors[Math.Clamp(group.ColorIndex, 0, colors.Length - 1)];
    }

    private static SKColor AnnotationColor(DiffAnnotationKind kind, RendererPalette palette) => kind switch
    {
        DiffAnnotationKind.GitStatus => palette.EdgeColor,
        DiffAnnotationKind.ReferenceRange => palette.Type,
        DiffAnnotationKind.Syntax => palette.Comment,
        DiffAnnotationKind.SemanticAnchor => palette.Property,
        DiffAnnotationKind.ParserDiagnostic => palette.Warning,
        DiffAnnotationKind.ReviewNoise => palette.Comment,
        DiffAnnotationKind.MovedCode => palette.MovedAccent,
        DiffAnnotationKind.InlineChange => palette.InlineChangedBackground,
        DiffAnnotationKind.Impact => palette.Impact,
        DiffAnnotationKind.Conflict => palette.Invalid,
        DiffAnnotationKind.ContextFold => palette.MetadataAccent,
        DiffAnnotationKind.Navigation => palette.Navigation,
        DiffAnnotationKind.HistoryBlame => palette.History,
        DiffAnnotationKind.ReviewAction => palette.ReviewAction,
        DiffAnnotationKind.ReviewComment => palette.ReviewAction,
        DiffAnnotationKind.RepositoryWatch => palette.Positive,
        _ => palette.EdgeColor
    };

    private static SKColor LineAccentColor(DiffLineKind kind, RendererPalette palette) => kind switch
    {
        DiffLineKind.Added => palette.NodeAdded,
        DiffLineKind.Deleted => palette.NodeDeleted,
        DiffLineKind.Modified => palette.NodeModified,
        DiffLineKind.Moved => palette.MovedAccent,
        DiffLineKind.Conflict => palette.Invalid,
        DiffLineKind.Metadata => palette.MetadataAccent,
        DiffLineKind.Ignored => palette.MutedTextColor,
        DiffLineKind.Imaginary => palette.MetadataAccent,
        _ => palette.MutedTextColor
    };

    private static SKColor OverviewColor(DiffLineKind kind, RendererPalette palette) => kind switch
    {
        DiffLineKind.Added => palette.AddedBackground.WithAlpha(180),
        DiffLineKind.Deleted => palette.DeletedBackground.WithAlpha(180),
        DiffLineKind.Modified => palette.MetadataBackground.WithAlpha(130),
        DiffLineKind.Moved => palette.MovedBackground.WithAlpha(180),
        DiffLineKind.Conflict => palette.ConflictBackground.WithAlpha(190),
        DiffLineKind.Metadata => palette.MetadataBackground.WithAlpha(120),
        DiffLineKind.Ignored => palette.IgnoredBackground.WithAlpha(150),
        DiffLineKind.Imaginary => palette.MetadataBackground.WithAlpha(120),
        _ => SKColors.Transparent
    };

    private static int LineKindPriority(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Conflict => 0,
        DiffLineKind.Added or DiffLineKind.Deleted or DiffLineKind.Modified or DiffLineKind.Moved => 1,
        DiffLineKind.Metadata => 2,
        DiffLineKind.Ignored => 3,
        DiffLineKind.Imaginary => 4,
        _ => 10
    };

    private static int AnnotationPriority(DiffAnnotation annotation) => AnnotationPriority(annotation.Kind);

    private static int AnnotationPriority(DiffAnnotationKind kind) => kind switch
    {
        DiffAnnotationKind.Conflict => 0,
        DiffAnnotationKind.ParserDiagnostic => 1,
        DiffAnnotationKind.Navigation => 2,
        DiffAnnotationKind.Impact => 3,
        DiffAnnotationKind.ReviewComment => 4,
        DiffAnnotationKind.MovedCode => 5,
        DiffAnnotationKind.ReviewNoise => 6,
        DiffAnnotationKind.SemanticAnchor => 7,
        DiffAnnotationKind.HistoryBlame => 8,
        DiffAnnotationKind.GitStatus => 9,
        _ => 9
    };

    private static bool IsAnnotationHovered(IEnumerable<DiffAnnotation> annotations, string? hoveredAnnotationId)
    {
        if (string.IsNullOrWhiteSpace(hoveredAnnotationId))
        {
            return false;
        }

        foreach (var annotation in annotations)
        {
            if (IsAnnotationHovered(annotation, hoveredAnnotationId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAnnotationHovered(DiffAnnotation annotation, string? hoveredAnnotationId) =>
        !string.IsNullOrWhiteSpace(hoveredAnnotationId) &&
        string.Equals(annotation.Id, hoveredAnnotationId, StringComparison.Ordinal);

    private static string ShortAnnotationLabel(string label) => label.Length <= 10 ? label : label[..10];

    private static TextStyle CreateUiTextStyle(float size, SKColor color, bool bold) => new(
        "SF Pro Text",
        size,
        color,
        bold);

    private static TextStyle CreateCodeTextStyle(float size, SKColor color) => new("Menlo", size, color, bold: false);

    private enum NodeBodyDetail
    {
        Full,
        Overview
    }

    private sealed record RenderGraphEdge(GraphEdge Edge, DiffNode Source, DiffNode Target, Rect2 Bounds, EdgeCurve Curve);

    private readonly record struct DiffTextRun(int StartColumn, string Text, TokenSpan? Token);

    private sealed record DiffLineRenderLayout(DiffTextRun[] Runs)
    {
        public static DiffLineRenderLayout Empty { get; } = new([]);

        public static DiffLineRenderLayout Create(DiffLine line)
        {
            if (string.IsNullOrEmpty(line.Text) || line.Tokens.IsDefaultOrEmpty)
            {
                return Empty;
            }

            var tokenSource = line.Tokens;
            if (!AreTokensOrdered(tokenSource))
            {
                tokenSource = tokenSource.Sort(static (left, right) => left.StartColumn.CompareTo(right.StartColumn));
            }

            var runs = new List<DiffTextRun>();
            var cursor = 0;
            foreach (var token in tokenSource)
            {
                if (token.StartColumn < 0 || token.StartColumn >= line.Text.Length || token.Length <= 0)
                {
                    continue;
                }

                var start = Math.Clamp(token.StartColumn, 0, line.Text.Length);
                var availableLength = line.Text.Length - start;
                var end = start + Math.Min(token.Length, availableLength);
                if (end <= cursor)
                {
                    continue;
                }

                start = Math.Max(start, cursor);
                if (start > cursor)
                {
                    runs.Add(new DiffTextRun(cursor, CreateRunText(line.Text, cursor, start - cursor), null));
                }

                if (end > start)
                {
                    runs.Add(new DiffTextRun(start, CreateRunText(line.Text, start, end - start), token));
                    cursor = Math.Max(cursor, end);
                }
            }

            if (cursor < line.Text.Length)
            {
                runs.Add(new DiffTextRun(cursor, CreateRunText(line.Text, cursor, line.Text.Length - cursor), null));
            }

            return runs.Count == 0 ? Empty : new DiffLineRenderLayout(runs.ToArray());
        }

        private static string CreateRunText(string text, int start, int length)
        {
            var safeLength = Math.Min(length, text.Length - start);
            if (safeLength <= 0)
            {
                return string.Empty;
            }

            var value = text.Substring(start, safeLength);
            return value.IndexOf('\t') >= 0
                ? value.Replace("\t", TabReplacement, StringComparison.Ordinal)
                : value;
        }

        private static bool AreTokensOrdered(ImmutableArray<TokenSpan> tokens)
        {
            var previousStart = -1;
            foreach (var token in tokens)
            {
                if (token.StartColumn < previousStart)
                {
                    return false;
                }

                previousStart = token.StartColumn;
            }

            return true;
        }
    }

    private readonly record struct DocumentOverviewBucket(DiffLineKind Kind, bool HasText, bool HasAnnotation);

    private readonly record struct LineNumberRenderText(string Old, string New, string Full)
    {
        public static LineNumberRenderText Empty { get; } = new("    ", "    ", string.Empty);
    }

    private sealed class DocumentRenderCache
    {
        private readonly DiffDocumentSnapshot document;
        private readonly DiffAnnotation[] annotations;
        private readonly DiffLineRenderLayout[] lineLayouts;
        private readonly LineNumberRenderText[] lineNumberTexts;
        private readonly Dictionary<int, DiffAnnotation[]> lineAnnotationsByIndex;
        private readonly Dictionary<int, DocumentOverviewBucket[]> overviewBucketsByCount = [];

        private DocumentRenderCache(
            DiffDocumentSnapshot document,
            DiffAnnotation[] annotations,
            DiffAnnotation[] nodeAnnotations,
            Dictionary<int, DiffAnnotation[]> lineAnnotationsByIndex,
            DiffLineRenderLayout[] lineLayouts,
            LineNumberRenderText[] lineNumberTexts)
        {
            this.document = document;
            this.annotations = annotations;
            NodeAnnotations = nodeAnnotations;
            this.lineAnnotationsByIndex = lineAnnotationsByIndex;
            this.lineLayouts = lineLayouts;
            this.lineNumberTexts = lineNumberTexts;
        }

        public DiffAnnotation[] NodeAnnotations { get; }

        public static DocumentRenderCache Create(DiffDocumentSnapshot document, IReadOnlyList<DiffAnnotation> annotations, bool annotationsAreSorted = false)
        {
            var sortedAnnotations = annotationsAreSorted
                ? CopyAnnotations(annotations)
                : CopyAndSortAnnotations(annotations);
            var nodeAnnotations = new List<DiffAnnotation>();
            var lineAnnotationLists = new Dictionary<int, List<DiffAnnotation>>();
            for (var index = 0; index < sortedAnnotations.Length; index++)
            {
                var annotation = sortedAnnotations[index];
                if (annotation.Target == DiffAnnotationTarget.Node)
                {
                    nodeAnnotations.Add(annotation);
                    continue;
                }

                if (annotation.Target != DiffAnnotationTarget.Line || annotation.LineIndex is null)
                {
                    continue;
                }

                var lineIndex = annotation.LineIndex.Value;
                if (!lineAnnotationLists.TryGetValue(lineIndex, out var lineAnnotations))
                {
                    lineAnnotations = [];
                    lineAnnotationLists[lineIndex] = lineAnnotations;
                }

                lineAnnotations.Add(annotation);
            }

            var lineAnnotationsByIndex = new Dictionary<int, DiffAnnotation[]>(lineAnnotationLists.Count);
            foreach (var (lineIndex, lineAnnotations) in lineAnnotationLists)
            {
                lineAnnotationsByIndex[lineIndex] = lineAnnotations.ToArray();
            }

            var lineLayouts = new DiffLineRenderLayout[document.Lines.Length];
            var lineNumberTexts = new LineNumberRenderText[document.Lines.Length];
            for (var index = 0; index < document.Lines.Length; index++)
            {
                var line = document.Lines[index];
                lineLayouts[index] = DiffLineRenderLayout.Create(line);
                lineNumberTexts[index] = CreateLineNumberText(line);
            }

            return new DocumentRenderCache(document, sortedAnnotations, nodeAnnotations.ToArray(), lineAnnotationsByIndex, lineLayouts, lineNumberTexts);
        }

        private static LineNumberRenderText CreateLineNumberText(DiffLine line)
        {
            var oldText = line.OldLineNumber?.ToString().PadLeft(4) ?? "    ";
            var newText = line.NewLineNumber?.ToString().PadLeft(4) ?? "    ";
            var fullText = (line.NewLineNumber ?? line.OldLineNumber ?? line.Index + 1).ToString();
            return new LineNumberRenderText(oldText, newText, fullText);
        }

        public bool Matches(DiffDocumentSnapshot nextDocument, IReadOnlyList<DiffAnnotation> nextAnnotations)
        {
            if (!ReferenceEquals(document, nextDocument) || annotations.Length != nextAnnotations.Count)
            {
                return false;
            }

            for (var index = 0; index < annotations.Length; index++)
            {
                if (!annotations[index].Equals(nextAnnotations[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static DiffAnnotation[] CopyAnnotations(IReadOnlyList<DiffAnnotation> source)
        {
            if (source.Count == 0)
            {
                return Array.Empty<DiffAnnotation>();
            }

            var result = new DiffAnnotation[source.Count];
            for (var index = 0; index < source.Count; index++)
            {
                result[index] = source[index];
            }

            return result;
        }

        private static DiffAnnotation[] CopyAndSortAnnotations(IReadOnlyList<DiffAnnotation> source)
        {
            if (source.Count == 0)
            {
                return Array.Empty<DiffAnnotation>();
            }

            var result = new DiffAnnotation[source.Count];
            for (var index = 0; index < source.Count; index++)
            {
                result[index] = source[index];
            }

            Array.Sort(result, static (left, right) =>
            {
                var priority = AnnotationPriority(left).CompareTo(AnnotationPriority(right));
                return priority != 0
                    ? priority
                    : string.CompareOrdinal(left.Id, right.Id);
            });
            return result;
        }

        public DiffAnnotation[] GetLineAnnotations(int lineIndex) =>
            lineAnnotationsByIndex.GetValueOrDefault(lineIndex) ?? [];

        public DiffLineRenderLayout GetLineLayout(int lineIndex) =>
            lineIndex >= 0 && lineIndex < lineLayouts.Length ? lineLayouts[lineIndex] : DiffLineRenderLayout.Empty;

        public LineNumberRenderText GetLineNumberText(int lineIndex) =>
            lineIndex >= 0 && lineIndex < lineNumberTexts.Length ? lineNumberTexts[lineIndex] : LineNumberRenderText.Empty;

        public DocumentOverviewBucket[] GetOverviewBuckets(int bucketCount)
        {
            bucketCount = Math.Clamp(bucketCount, OverviewMinimumBucketCount, OverviewMaximumBucketCount);
            if (overviewBucketsByCount.TryGetValue(bucketCount, out var buckets))
            {
                return buckets;
            }

            buckets = BuildOverviewBuckets(bucketCount);
            overviewBucketsByCount[bucketCount] = buckets;
            return buckets;
        }

        private DocumentOverviewBucket[] BuildOverviewBuckets(int bucketCount)
        {
            if (document.Lines.Length == 0)
            {
                return Array.Empty<DocumentOverviewBucket>();
            }

            var buckets = new DocumentOverviewBucket[bucketCount];
            var rowsPerBucket = document.Lines.Length / (double)bucketCount;
            for (var bucket = 0; bucket < bucketCount; bucket++)
            {
                var start = Math.Clamp((int)Math.Floor(bucket * rowsPerBucket), 0, document.Lines.Length - 1);
                var end = Math.Clamp((int)Math.Ceiling((bucket + 1) * rowsPerBucket), start + 1, document.Lines.Length);
                var dominantKind = DiffLineKind.Context;
                var hasText = false;
                var hasAnnotation = false;

                for (var index = start; index < end; index++)
                {
                    var line = document.Lines[index];
                    hasText |= !string.IsNullOrWhiteSpace(line.Text);
                    hasAnnotation |= lineAnnotationsByIndex.ContainsKey(line.Index);
                    if (LineKindPriority(line.Kind) < LineKindPriority(dominantKind))
                    {
                        dominantKind = line.Kind;
                    }
                }

                buckets[bucket] = new DocumentOverviewBucket(dominantKind, hasText, hasAnnotation);
            }

            return buckets;
        }
    }

    private sealed class RenderSceneCache : IDisposable
    {
        private RenderSceneCache(
            DiffCanvasScene scene,
            int geometryVersion,
            DiffNode[] nodes,
            RenderGraphEdge[] edges,
            Dictionary<DiffDocumentId, DocumentRenderCache> documents)
        {
            Scene = scene;
            GeometryVersion = geometryVersion;
            Nodes = nodes;
            Edges = edges;
            Documents = documents;
        }

        private DiffCanvasScene Scene { get; }

        private int GeometryVersion { get; }

        public DiffNode[] Nodes { get; }

        public RenderGraphEdge[] Edges { get; }

        public Dictionary<DiffDocumentId, DocumentRenderCache> Documents { get; }

        public static RenderSceneCache Create(DiffCanvasScene scene, RenderSceneCache? previous)
        {
            var nodes = scene.Nodes.ToArray();
            var nodesById = new Dictionary<string, DiffNode>(nodes.Length, StringComparer.Ordinal);
            for (var index = 0; index < nodes.Length; index++)
            {
                var node = nodes[index];
                nodesById[node.Document.Id.Value] = node;
            }

            var edges = new List<RenderGraphEdge>(scene.Edges.Count);
            foreach (var graphEdge in scene.Edges)
            {
                var edge = CreateRenderGraphEdge(graphEdge, nodesById);
                if (edge is not null)
                {
                    edges.Add(edge);
                }
            }

            var annotationsByDocument = new Dictionary<DiffDocumentId, List<DiffAnnotation>>();
            foreach (var annotation in scene.Annotations)
            {
                if (!scene.AnnotationVisibility.IsVisible(annotation.Kind))
                {
                    continue;
                }

                if (!annotationsByDocument.TryGetValue(annotation.DocumentId, out var annotations))
                {
                    annotations = [];
                    annotationsByDocument[annotation.DocumentId] = annotations;
                }

                annotations.Add(annotation);
            }

            foreach (var annotations in annotationsByDocument.Values)
            {
                annotations.Sort(static (left, right) =>
                {
                    var priority = AnnotationPriority(left).CompareTo(AnnotationPriority(right));
                    return priority != 0
                        ? priority
                        : string.CompareOrdinal(left.Id, right.Id);
                });
            }

            var documents = new Dictionary<DiffDocumentId, DocumentRenderCache>(nodes.Length);
            for (var index = 0; index < nodes.Length; index++)
            {
                var document = nodes[index].Document;
                if (documents.ContainsKey(document.Id))
                {
                    continue;
                }

                IReadOnlyList<DiffAnnotation> annotations = annotationsByDocument.TryGetValue(document.Id, out var documentAnnotations)
                    ? documentAnnotations
                    : EmptyAnnotations;
                if (previous?.Documents.TryGetValue(document.Id, out var previousDocumentCache) == true &&
                    previousDocumentCache.Matches(document, annotations))
                {
                    documents[document.Id] = previousDocumentCache;
                }
                else
                {
                    documents[document.Id] = DocumentRenderCache.Create(document, annotations, annotationsAreSorted: true);
                }
            }

            return new RenderSceneCache(scene, scene.GeometryVersion, nodes, edges.ToArray(), documents);
        }

        private static readonly DiffAnnotation[] EmptyAnnotations = [];

        public bool Matches(DiffCanvasScene scene) => ReferenceEquals(scene, Scene) && scene.GeometryVersion == GeometryVersion;

        public void Dispose()
        {
        }

        private static RenderGraphEdge? CreateRenderGraphEdge(GraphEdge edge, IReadOnlyDictionary<string, DiffNode> nodesById)
        {
            if (!nodesById.TryGetValue(edge.SourceNodeId, out var source) ||
                !nodesById.TryGetValue(edge.TargetNodeId, out var target))
            {
                return null;
            }

            var curve = CreateEdgeCurve(source, target);
            return new RenderGraphEdge(edge, source, target, GetEdgeBounds(curve), curve);
        }
    }

    private sealed record RendererPalette(
        SKColor Background,
        SKColor GridLine,
        SKColor NodeBackground,
        SKColor NodeBorder,
        SKColor TitleBackground,
        SKColor GutterBackground,
        SKColor TextColor,
        SKColor MutedTextColor,
        SKColor AddedBackground,
        SKColor DeletedBackground,
        SKColor IgnoredBackground,
        SKColor MovedBackground,
        SKColor ConflictBackground,
        SKColor InlineAddedBackground,
        SKColor InlineDeletedBackground,
        SKColor InlineChangedBackground,
        SKColor MetadataBackground,
        SKColor EdgeColor,
        SKColor ScrollbarThumb,
        SKColor Keyword,
        SKColor Type,
        SKColor String,
        SKColor Comment,
        SKColor Number,
        SKColor Function,
        SKColor Property,
        SKColor Tag,
        SKColor Invalid,
        SKColor NodeAdded,
        SKColor NodeModified,
        SKColor NodeDeleted,
        SKColor NodeRenamed,
        SKColor NodeUntracked,
        SKColor NodeConflict)
    {
        public SKColor Binding => Property;

        public SKColor Resource => String;

        public SKColor PartialClass => Number;

        public SKColor Positive => String;

        public SKColor Warning => Tag;

        public SKColor Impact => Number;

        public SKColor Navigation => EdgeColor;

        public SKColor History => Type;

        public SKColor ReviewAction => Property;

        public SKColor MovedAccent => new((byte)Math.Min(255, MovedBackground.Red + 85), (byte)Math.Min(255, MovedBackground.Green + 65), (byte)Math.Min(255, MovedBackground.Blue + 25));

        public SKColor MetadataAccent => new((byte)Math.Min(255, MetadataBackground.Red + 55), (byte)Math.Min(255, MetadataBackground.Green + 55), (byte)Math.Min(255, MetadataBackground.Blue + 55));
    }

    private sealed class RenderTextResources : IDisposable
    {
        private readonly RendererPalette palette;
        private readonly Dictionary<UiTextStyleKey, TextStyle> uiStyles = [];
        private readonly Dictionary<CodeTextStyleKey, TextStyle> codeStyles = [];
        private readonly Dictionary<float, CodeTextStyles> codeStyleSets = [];

        public RenderTextResources(RendererPalette palette)
        {
            this.palette = palette;
        }

        public TextStyle GetUiTextStyle(float size, SKColor color, bool bold)
        {
            var key = new UiTextStyleKey(size, color, bold);
            if (!uiStyles.TryGetValue(key, out var style))
            {
                style = CreateUiTextStyle(size, color, bold);
                uiStyles[key] = style;
            }

            return style;
        }

        public TextStyle GetCodeTextStyle(float size, SKColor color)
        {
            var key = new CodeTextStyleKey(size, color);
            if (!codeStyles.TryGetValue(key, out var style))
            {
                style = CreateCodeTextStyle(size, color);
                codeStyles[key] = style;
            }

            return style;
        }

        public CodeTextStyles GetCodeTextStyles(float size)
        {
            if (!codeStyleSets.TryGetValue(size, out var styles))
            {
                styles = new CodeTextStyles(palette, size);
                codeStyleSets[size] = styles;
            }

            return styles;
        }

        public void Dispose()
        {
            foreach (var styles in codeStyleSets.Values)
            {
                styles.Dispose();
            }

            foreach (var style in codeStyles.Values)
            {
                style.Dispose();
            }

            foreach (var style in uiStyles.Values)
            {
                style.Dispose();
            }
        }

        private readonly record struct UiTextStyleKey(float Size, SKColor Color, bool Bold);

        private readonly record struct CodeTextStyleKey(float Size, SKColor Color);
    }

    private sealed class CodeTextStyles : IDisposable
    {
        public CodeTextStyles(RendererPalette palette, float size)
        {
            Default = CreateCodeTextStyle(size, palette.TextColor);
            Keyword = CreateCodeTextStyle(size, palette.Keyword);
            Type = CreateCodeTextStyle(size, palette.Type);
            String = CreateCodeTextStyle(size, palette.String);
            Comment = CreateCodeTextStyle(size, palette.Comment);
            Number = CreateCodeTextStyle(size, palette.Number);
            Function = CreateCodeTextStyle(size, palette.Function);
            Property = CreateCodeTextStyle(size, palette.Property);
            Tag = CreateCodeTextStyle(size, palette.Tag);
            Variable = CreateCodeTextStyle(size, palette.TextColor);
            Parameter = CreateCodeTextStyle(size, palette.Property);
            Operator = CreateCodeTextStyle(size, palette.Keyword);
            Punctuation = CreateCodeTextStyle(size, palette.MutedTextColor);
            Macro = CreateCodeTextStyle(size, palette.Tag);
            Regexp = CreateCodeTextStyle(size, palette.String);
            Invalid = CreateCodeTextStyle(size, palette.Invalid);
        }

        public TextStyle Default { get; }

        private TextStyle Keyword { get; }

        private TextStyle Type { get; }

        private TextStyle String { get; }

        private TextStyle Comment { get; }

        private TextStyle Number { get; }

        private TextStyle Function { get; }

        private TextStyle Property { get; }

        private TextStyle Tag { get; }

        private TextStyle Variable { get; }

        private TextStyle Parameter { get; }

        private TextStyle Operator { get; }

        private TextStyle Punctuation { get; }

        private TextStyle Macro { get; }

        private TextStyle Regexp { get; }

        private TextStyle Invalid { get; }

        public TextStyle GetStyle(TokenSpan token)
        {
            var styleId = string.IsNullOrWhiteSpace(token.StyleId) || token.StyleId == "text"
                ? StyleFromTokenType(token.TokenType)
                : token.StyleId;

            return styleId switch
            {
                "keyword" => Keyword,
                "type" or "namespace" or "class" or "interface" or "enum" or "struct" => Type,
                "string" => String,
                "comment" => Comment,
                "number" => Number,
                "function" or "method" => Function,
                "property" => Property,
                "parameter" => Parameter,
                "variable" => Variable,
                "tag" => Tag,
                "operator" => Operator,
                "punctuation" => Punctuation,
                "macro" => Macro,
                "regexp" => Regexp,
                "invalid" => Invalid,
                _ => Default
            };
        }

        private static string StyleFromTokenType(string tokenType) => tokenType switch
        {
            "namespace" or "class" or "enum" or "interface" or "struct" or "typeParameter" or "type" => "type",
            "function" or "method" => "function",
            "parameter" => "parameter",
            "property" or "enumMember" or "event" => "property",
            "decorator" or "macro" or "label" => "tag",
            "comment" => "comment",
            "string" => "string",
            "regexp" => "regexp",
            "keyword" => "keyword",
            "number" => "number",
            "operator" => "operator",
            "variable" => "variable",
            "invalid" => "invalid",
            _ => "text"
        };

        public void Dispose()
        {
            Default.Dispose();
            Keyword.Dispose();
            Type.Dispose();
            String.Dispose();
            Comment.Dispose();
            Number.Dispose();
            Function.Dispose();
            Property.Dispose();
            Tag.Dispose();
            Variable.Dispose();
            Parameter.Dispose();
            Operator.Dispose();
            Punctuation.Dispose();
            Macro.Dispose();
            Regexp.Dispose();
            Invalid.Dispose();
        }
    }

    private sealed class TextStyle : IDisposable
    {
        private readonly TextMetricsCache metrics = TextMetricsCache.Shared;

        public TextStyle(string familyName, float size, SKColor color, bool bold)
        {
            Descriptor = new TextFontDescriptor(familyName, size, bold);
            var typeface = metrics.GetTypeface(Descriptor);
            Font = new SKFont(typeface, size, 1, 0);
            Paint = new SKPaint { Color = color, IsAntialias = true };
            MonospaceAdvance = metrics.MeasureMonospaceAdvance(Descriptor);
        }

        public TextFontDescriptor Descriptor { get; }

        public SKFont Font { get; }

        public SKPaint Paint { get; }

        public float MonospaceAdvance { get; }

        public float MeasureText(string text) => metrics.MeasureNaturalWidth(text, Descriptor);

        public string MiddleEllipsize(string text, float maxWidth) => metrics.MiddleEllipsize(text, maxWidth, Descriptor);

        public void Dispose()
        {
            Font.Dispose();
            Paint.Dispose();
        }
    }
}
