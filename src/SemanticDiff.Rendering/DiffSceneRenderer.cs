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
    private const float TitlePathLeftInset = 40;
    private const float TitleMetadataGap = 8;
    private const float TitlePinnedLabelLeftInset = 188;

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
        DiffSceneRenderMode renderMode = DiffSceneRenderMode.Normal)
    {
        var palette = colorTheme == DiffCanvasColorTheme.Light ? LightPalette : DarkPalette;
        canvas.Clear(palette.Background);
        DrawGrid(canvas, canvasSize, scene.Camera, palette);
        var useInteractiveRendering = renderMode == DiffSceneRenderMode.Interactive;
        var cache = useInteractiveRendering ? null : GetRenderCache(scene);

        var worldViewport = GetWorldViewport(scene.Camera, canvasSize)
            .Inflate(DiffCanvasScene.ScreenStableWorldLength(scene.Camera.Scale, 96));
        var nodeSource = cache?.Nodes ?? scene.Nodes;
        var visibleNodes = nodeSource
            .Where(node => node.Bounds.Intersects(worldViewport))
            .ToArray();
        var visibleGroups = scene.Groups
            .Where(group => group.Bounds.Intersects(worldViewport))
            .ToArray();
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
        if (cache is not null)
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
            drawnEdges = DrawInteractiveEdges(canvas, scene, worldViewport, edgePaint, palette);
        }

        foreach (var node in visibleNodes)
        {
            DrawNode(
                canvas,
                node,
                palette,
                cache?.AnnotationsByDocument.GetValueOrDefault(node.Document.Id) ?? [],
                scene.Camera.Scale,
                scene.HoveredAnnotationId,
                drawDocumentBody: !useInteractiveRendering);
        }

        LastRenderStats = new(
            scene.Nodes.Count,
            visibleNodes.Length,
            useInteractiveRendering ? 0 : visibleNodes.Length,
            scene.Edges.Count,
            drawnEdges,
            cache?.Edges.Length ?? 0);
        canvas.Restore();
        DrawGroupLabels(canvas, scene.Camera, visibleGroups, palette, canvasSize);
        DrawFontControls(canvas, scene.Camera, visibleNodes, palette, canvasSize);
    }

    private RenderSceneCache GetRenderCache(DiffCanvasScene scene)
    {
        if (renderCache is not null && renderCache.Matches(scene))
        {
            return renderCache;
        }

        renderCache?.Dispose();
        renderCache = RenderSceneCache.Create(scene);
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

    private static void DrawGroupLabels(SKCanvas canvas, CameraState camera, IReadOnlyList<GraphGroup> groups, RendererPalette palette, SKSize canvasSize)
    {
        if (groups.Count == 0)
        {
            return;
        }

        using var textStyle = CreateUiTextStyle(14, palette.TextColor, true);
        foreach (var group in groups)
        {
            var color = GroupColor(group, palette);
            var labelPoint = camera.WorldToScreen(new Point2(group.Bounds.Left + 14, group.Bounds.Top + 9));
            var label = group.SummaryText;
            var width = Math.Clamp(textStyle.Font.MeasureText(label, textStyle.Paint) + 26, 94, 340);
            var rect = SKRect.Create((float)labelPoint.X, (float)labelPoint.Y, width, 28);
            if (rect.Right < 0 || rect.Left > canvasSize.Width || rect.Bottom < 0 || rect.Top > canvasSize.Height)
            {
                continue;
            }

            using var chipFill = new SKPaint { Color = palette.Background.WithAlpha(232), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var chipStroke = new SKPaint { Color = color.WithAlpha(210), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true };
            using var accent = new SKPaint { Color = color.WithAlpha(220), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(rect, 5, 5, chipFill);
            canvas.DrawRoundRect(rect, 5, 5, chipStroke);
            canvas.DrawRoundRect(SKRect.Create(rect.Left + 7, rect.Top + 7, 5, 14), 2.5f, 2.5f, accent);
            canvas.DrawText(label, rect.Left + 18, rect.Top + 19, textStyle.Font, textStyle.Paint);
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
        canvas.DrawPath(edge.Path, paint);
    }

    private static int DrawInteractiveEdges(SKCanvas canvas, DiffCanvasScene scene, Rect2 worldViewport, SKPaint paint, RendererPalette palette)
    {
        if (scene.Edges.Count == 0 || scene.Nodes.Count == 0)
        {
            return 0;
        }

        var nodesById = scene.Nodes.ToDictionary(node => node.Document.Id.Value, StringComparer.Ordinal);
        var drawn = 0;
        paint.Color = palette.EdgeColor.WithAlpha(110);
        paint.StrokeWidth = (float)DiffCanvasScene.ScreenStableWorldLength(scene.Camera.Scale, 1.5);

        foreach (var edge in scene.Edges)
        {
            if (!nodesById.TryGetValue(edge.SourceNodeId, out var source) ||
                !nodesById.TryGetValue(edge.TargetNodeId, out var target))
            {
                continue;
            }

            var bounds = GetEdgeBounds(source, target);
            if (!bounds.Inflate(DiffCanvasScene.ScreenStableWorldLength(scene.Camera.Scale, 80)).Intersects(worldViewport))
            {
                continue;
            }

            canvas.DrawLine(
                (float)source.Bounds.Right,
                (float)source.Bounds.Center.Y,
                (float)target.Bounds.Left,
                (float)target.Bounds.Center.Y,
                paint);
            drawn++;
        }

        return drawn;
    }

    private static SKPath CreateEdgePath(DiffNode source, DiffNode target)
    {
        var sourcePoint = new SKPoint((float)source.Bounds.Right, (float)source.Bounds.Center.Y);
        var targetPoint = new SKPoint((float)target.Bounds.Left, (float)target.Bounds.Center.Y);
        var controlOffset = Math.Max(120, Math.Abs(targetPoint.X - sourcePoint.X) * 0.35f);
        var path = new SKPath();
        path.MoveTo(sourcePoint);
        path.CubicTo(sourcePoint.X + controlOffset, sourcePoint.Y, targetPoint.X - controlOffset, targetPoint.Y, targetPoint.X, targetPoint.Y);
        return path;
    }

    private static Rect2 GetEdgeBounds(DiffNode source, DiffNode target)
    {
        var sourceX = source.Bounds.Right;
        var sourceY = source.Bounds.Center.Y;
        var targetX = target.Bounds.Left;
        var targetY = target.Bounds.Center.Y;
        var controlOffset = Math.Max(120, Math.Abs(targetX - sourceX) * 0.35);
        var left = Math.Min(Math.Min(sourceX, targetX), Math.Min(sourceX + controlOffset, targetX - controlOffset));
        var right = Math.Max(Math.Max(sourceX, targetX), Math.Max(sourceX + controlOffset, targetX - controlOffset));
        var top = Math.Min(sourceY, targetY);
        var bottom = Math.Max(sourceY, targetY);
        return new Rect2(left, top, right - left, bottom - top);
    }

    private static void DrawNode(
        SKCanvas canvas,
        DiffNode node,
        RendererPalette palette,
        IReadOnlyList<DiffAnnotation> annotations,
        double cameraScale,
        string? hoveredAnnotationId,
        bool drawDocumentBody)
    {
        var bounds = ToRect(node.Bounds);
        var statusColor = NodeStatusColor(node.Document.Metadata.Status, palette);
        using var backgroundPaint = new SKPaint { Color = palette.NodeBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var borderPaint = new SKPaint { Color = node.IsSelected ? palette.EdgeColor : statusColor.WithAlpha(210), Style = SKPaintStyle.Stroke, StrokeWidth = node.IsSelected ? 3 : 1.8f, IsAntialias = true };

        canvas.DrawRoundRect(bounds, 6, 6, backgroundPaint);
        DrawNodeStatusAccent(canvas, bounds, statusColor);
        canvas.DrawRoundRect(bounds, 6, 6, borderPaint);
        DrawTitle(canvas, node, palette, cameraScale);
        if (drawDocumentBody)
        {
            DrawDocumentBody(canvas, node, palette, annotations, cameraScale, hoveredAnnotationId);
        }
        else
        {
            DrawInteractiveDocumentBody(canvas, node, palette, cameraScale);
        }

        DrawFooter(canvas, node, palette, annotations, hoveredAnnotationId);
        DrawResizeHandles(canvas, node, palette, cameraScale);
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
        var points = new[]
        {
            new Point2(node.Bounds.Left, node.Bounds.Top),
            new Point2(node.Bounds.Center.X, node.Bounds.Top),
            new Point2(node.Bounds.Right, node.Bounds.Top),
            new Point2(node.Bounds.Right, node.Bounds.Center.Y),
            new Point2(node.Bounds.Right, node.Bounds.Bottom),
            new Point2(node.Bounds.Center.X, node.Bounds.Bottom),
            new Point2(node.Bounds.Left, node.Bounds.Bottom),
            new Point2(node.Bounds.Left, node.Bounds.Center.Y)
        };

        using var fillPaint = new SKPaint { Color = palette.NodeBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var strokePaint = new SKPaint { Color = palette.EdgeColor, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(1.2 / Math.Max(cameraScale, 0.01)), IsAntialias = true };
        foreach (var point in points)
        {
            var rect = SKRect.Create((float)(point.X - halfHandle), (float)(point.Y - halfHandle), (float)handleSize, (float)handleSize);
            canvas.DrawRoundRect(rect, (float)(2 / Math.Max(cameraScale, 0.01)), (float)(2 / Math.Max(cameraScale, 0.01)), fillPaint);
            canvas.DrawRoundRect(rect, (float)(2 / Math.Max(cameraScale, 0.01)), (float)(2 / Math.Max(cameraScale, 0.01)), strokePaint);
        }
    }

    private static void DrawTitle(SKCanvas canvas, DiffNode node, RendererPalette palette, double cameraScale)
    {
        var titleRect = SKRect.Create((float)node.Bounds.X, (float)node.Bounds.Y, (float)node.Bounds.Width, (float)DiffNode.TitleHeight);
        var statusColor = NodeStatusColor(node.Document.Metadata.Status, palette);
        using var paint = new SKPaint { Color = palette.TitleBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var textStyle = CreateUiTextStyle(13, palette.TextColor, true);
        using var metaStyle = CreateUiTextStyle(11.5f, palette.MutedTextColor, false);
        using var statusPaint = new SKPaint { Color = statusColor, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var statusTextStyle = CreateUiTextStyle(11, SKColors.White, true);

        canvas.Save();
        canvas.ClipRect(titleRect);
        canvas.DrawRoundRect(titleRect, 6, 6, paint);
        canvas.DrawRect(SKRect.Create(titleRect.Left, titleRect.Bottom - 6, titleRect.Width, 6), paint);
        var statusRect = SKRect.Create(titleRect.Left + 11, titleRect.Top + 7, 21, 18);
        canvas.DrawRoundRect(statusRect, 4, 4, statusPaint);
        canvas.DrawText(StatusText(node.Document.Metadata.Status), statusRect.Left + 6, statusRect.Top + 13, statusTextStyle.Font, statusTextStyle.Paint);
        var pathTextX = titleRect.Left + TitlePathLeftInset;
        var pathTextRight = GetTitlePathRightEdge(node, titleRect, cameraScale);
        var pathMaxWidth = Math.Max(0, pathTextRight - pathTextX);
        var pathText = MiddleEllipsizeText(node.Document.Metadata.Path, pathMaxWidth, textStyle.Font, textStyle.Paint);
        canvas.DrawText(pathText, pathTextX, titleRect.Top + 20, textStyle.Font, textStyle.Paint);
        if (node.IsPinned)
        {
            canvas.DrawText("PIN", titleRect.Right - TitlePinnedLabelLeftInset, titleRect.Top + 20, metaStyle.Font, metaStyle.Paint);
        }

        canvas.DrawText($"{node.Document.LineCount:N0} lines", titleRect.Right - (float)DiffNode.FontControlLineCountInset, titleRect.Top + 20, metaStyle.Font, metaStyle.Paint);
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

    internal static string MiddleEllipsizeText(string text, float maxWidth, SKFont font, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
        {
            return string.Empty;
        }

        if (font.MeasureText(text, paint) <= maxWidth)
        {
            return text;
        }

        if (font.MeasureText(TextEllipsis, paint) > maxWidth)
        {
            return string.Empty;
        }

        var best = TextEllipsis;
        var low = 0;
        var high = text.Length - 1;
        while (low <= high)
        {
            var keepCount = low + (high - low) / 2;
            var prefixCount = keepCount / 2;
            var suffixCount = keepCount - prefixCount;
            var candidate = BuildMiddleEllipsizedText(text, prefixCount, suffixCount);
            if (font.MeasureText(candidate, paint) <= maxWidth)
            {
                best = candidate;
                low = keepCount + 1;
            }
            else
            {
                high = keepCount - 1;
            }
        }

        return best;
    }

    private static string BuildMiddleEllipsizedText(string text, int prefixCount, int suffixCount)
    {
        return string.Concat(
            text.AsSpan(0, prefixCount),
            TextEllipsis,
            text.AsSpan(text.Length - suffixCount, suffixCount));
    }

    private static void DrawFontControls(SKCanvas canvas, CameraState camera, IReadOnlyList<DiffNode> nodes, RendererPalette palette, SKSize canvasSize)
    {
        using var fillPaint = new SKPaint { Color = palette.NodeBackground.WithAlpha(230), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var strokePaint = new SKPaint { Color = palette.NodeBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        using var textStyle = CreateUiTextStyle(11, palette.TextColor, true);

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
        var textWidth = textStyle.Font.MeasureText(text, textStyle.Paint);
        canvas.DrawText(text, rect.Left + (rect.Width - textWidth) / 2, rect.Top + rect.Height * 0.68f, textStyle.Font, textStyle.Paint);
    }

    private static void DrawDocumentBody(SKCanvas canvas, DiffNode node, RendererPalette palette, IReadOnlyList<DiffAnnotation> annotations, double cameraScale, string? hoveredAnnotationId)
    {
        var body = node.BodyBounds;
        var bodyRect = ToRect(body);
        var gutterWidth = 94f;
        var markerWidth = 24f;

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
        using var codeStyles = new CodeTextStyles(palette, (float)node.FontSize);
        using var lineStyle = CreateCodeTextStyle((float)Math.Max(8, node.FontSize - 1), palette.MutedTextColor);

        canvas.DrawRect(SKRect.Create(bodyRect.Left, bodyRect.Top, gutterWidth, bodyRect.Height), gutterPaint);

        var lineHeight = node.LineHeight;
        var firstLineIndex = Math.Max(0, (int)(node.ScrollOffsetY / lineHeight));
        var visibleLineCount = (int)Math.Ceiling(body.Height / lineHeight) + 2;
        var y = body.Y - node.ScrollOffsetY % lineHeight;

        var annotationsByLine = annotations
            .Where(annotation => annotation.Target == DiffAnnotationTarget.Line && annotation.LineIndex is not null)
            .GroupBy(annotation => annotation.LineIndex!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(AnnotationPriority).ToArray());

        foreach (var line in node.Document.GetVisibleLines(firstLineIndex, visibleLineCount))
        {
            var lineRect = SKRect.Create(bodyRect.Left, (float)y, bodyRect.Width, (float)lineHeight);
            var codeX = bodyRect.Left + gutterWidth + markerWidth + 10;
            var lineAnnotations = annotationsByLine.GetValueOrDefault(line.Index) ?? [];
            DrawLineBackground(canvas, line, lineRect, addedPaint, deletedPaint, ignoredPaint, movedPaint, conflictPaint, metadataPaint);
            DrawLineAnnotationBand(canvas, lineAnnotations, lineRect, palette, hoveredAnnotationId);
            DrawLineNumber(canvas, line, bodyRect.Left + 8, (float)(y + lineHeight * 0.73), lineStyle);
            DrawMarker(canvas, line, bodyRect.Left + gutterWidth, (float)y, markerWidth, (float)lineHeight, lineStyle);
            DrawInlineSpans(canvas, line, codeX, (float)y, codeStyles.Default, inlineAddedPaint, inlineDeletedPaint, inlineChangedPaint);
            DrawCode(canvas, line, codeX, (float)(y + lineHeight * 0.73), codeStyles);
            DrawLineAnnotationMarkers(canvas, lineAnnotations, lineRect, palette, lineStyle, hoveredAnnotationId);
            y += lineHeight;
        }

        canvas.Restore();
        DrawScrollbar(canvas, node, palette, cameraScale);
    }

    private static void DrawInteractiveDocumentBody(SKCanvas canvas, DiffNode node, RendererPalette palette, double cameraScale)
    {
        var body = node.BodyBounds;
        if (body.Width <= 0 || body.Height <= 0)
        {
            return;
        }

        var bodyRect = ToRect(body);
        using var gutterPaint = new SKPaint { Color = palette.GutterBackground, Style = SKPaintStyle.Fill };
        using var linePaint = new SKPaint
        {
            Color = palette.MutedTextColor.WithAlpha(70),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)DiffCanvasScene.ScreenStableWorldLength(cameraScale, 1),
            IsAntialias = false
        };

        canvas.Save();
        canvas.ClipRect(bodyRect);
        canvas.DrawRect(SKRect.Create(bodyRect.Left, bodyRect.Top, 94, bodyRect.Height), gutterPaint);

        var lineHeight = Math.Max(10, node.LineHeight);
        var firstY = bodyRect.Top + (float)(lineHeight - node.ScrollOffsetY % lineHeight);
        var maxLines = Math.Min(16, (int)Math.Ceiling(bodyRect.Height / lineHeight));
        for (var index = 0; index < maxLines; index++)
        {
            var y = firstY + (float)(index * lineHeight);
            if (y > bodyRect.Bottom)
            {
                break;
            }

            canvas.DrawLine(bodyRect.Left + 118, y, bodyRect.Right - 18, y, linePaint);
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

    private static void DrawLineNumber(SKCanvas canvas, DiffLine line, float x, float y, TextStyle style)
    {
        var oldText = line.OldLineNumber?.ToString() ?? string.Empty;
        var newText = line.NewLineNumber?.ToString() ?? string.Empty;
        canvas.DrawText(oldText.PadLeft(4), x, y, style.Font, style.Paint);
        canvas.DrawText(newText.PadLeft(4), x + 42, y, style.Font, style.Paint);
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

        var characterWidth = textStyle.Font.MeasureText("M", textStyle.Paint);
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

    private static void DrawCode(SKCanvas canvas, DiffLine line, float x, float y, CodeTextStyles codeStyles)
    {
        if (line.Tokens.IsDefaultOrEmpty)
        {
            canvas.DrawText(line.Text, x, y, codeStyles.Default.Font, codeStyles.Default.Paint);
            return;
        }

        canvas.DrawText(line.Text, x, y, codeStyles.Default.Font, codeStyles.Default.Paint);
        var characterWidth = codeStyles.Default.Font.MeasureText("M", codeStyles.Default.Paint);

        foreach (var token in line.Tokens)
        {
            if (token.StartColumn < 0 || token.StartColumn >= line.Text.Length || token.Length <= 0)
            {
                continue;
            }

            var length = Math.Min(token.Length, line.Text.Length - token.StartColumn);
            var style = codeStyles.GetStyle(token);
            canvas.DrawText(line.Text.Substring(token.StartColumn, length), x + token.StartColumn * characterWidth, y, style.Font, style.Paint);
        }
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

        var primary = annotations.OrderBy(AnnotationPriority).First();
        if (primary.Kind is not (DiffAnnotationKind.Navigation or DiffAnnotationKind.ParserDiagnostic or DiffAnnotationKind.Conflict or DiffAnnotationKind.Impact or DiffAnnotationKind.ReviewComment))
        {
            return;
        }

        var alpha = IsAnnotationHovered(annotations, hoveredAnnotationId) ? (byte)118 : (byte)70;
        using var paint = new SKPaint { Color = AnnotationColor(primary.Kind, palette).WithAlpha(alpha), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRect(SKRect.Create(lineRect.Left, lineRect.Top, lineRect.Width, lineRect.Height), paint);
    }

    private static void DrawLineAnnotationMarkers(SKCanvas canvas, IReadOnlyList<DiffAnnotation> annotations, SKRect lineRect, RendererPalette palette, TextStyle textStyle, string? hoveredAnnotationId)
    {
        if (annotations.Count == 0)
        {
            return;
        }

        var markerX = lineRect.Right - 14;
        var markerY = lineRect.Top + 4;
        foreach (var annotation in annotations.Take(4))
        {
            var isHovered = IsAnnotationHovered(annotation, hoveredAnnotationId);
            var markerSize = isHovered ? 9 : 7;
            var markerOffset = isHovered ? -1 : 0;
            using var paint = new SKPaint { Color = AnnotationColor(annotation.Kind, palette), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(SKRect.Create(markerX + markerOffset, markerY + markerOffset, markerSize, markerSize), 2.5f, 2.5f, paint);
            markerY += 9;
        }

        var labeled = annotations.FirstOrDefault(annotation => annotation.Kind is DiffAnnotationKind.Navigation or DiffAnnotationKind.ParserDiagnostic or DiffAnnotationKind.Conflict or DiffAnnotationKind.Impact or DiffAnnotationKind.ReviewComment);
        if (labeled is null)
        {
            return;
        }

        var isLabeledHovered = IsAnnotationHovered(labeled, hoveredAnnotationId);
        using var chipPaint = new SKPaint { Color = AnnotationColor(labeled.Kind, palette).WithAlpha(isLabeledHovered ? (byte)245 : (byte)210), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var labelStyle = CreateUiTextStyle(9.5f, palette.Background, true);
        var label = ShortAnnotationLabel(labeled.Label);
        var width = Math.Clamp(labelStyle.Font.MeasureText(label, labelStyle.Paint) + 12, 38, 88);
        var chipRect = SKRect.Create(lineRect.Right - width - 22, lineRect.Top + 2, width, 13);
        canvas.DrawRoundRect(chipRect, 3, 3, chipPaint);
        if (isLabeledHovered)
        {
            using var strokePaint = new SKPaint { Color = palette.Background.WithAlpha(230), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
            canvas.DrawRoundRect(chipRect, 3, 3, strokePaint);
        }

        canvas.DrawText(label, chipRect.Left + 6, chipRect.Top + 10, labelStyle.Font, labelStyle.Paint);
    }

    private static void DrawFooter(SKCanvas canvas, DiffNode node, RendererPalette palette, IReadOnlyList<DiffAnnotation> annotations, string? hoveredAnnotationId)
    {
        using var textStyle = CreateUiTextStyle(11, palette.MutedTextColor, false);
        var y = (float)(node.Bounds.Bottom - 7);
        var summary = $"+{node.Document.Metadata.AddedLines}  -{node.Document.Metadata.DeletedLines}  {node.Document.Metadata.Language}";
        canvas.DrawText(summary, (float)node.Bounds.X + 12, y, textStyle.Font, textStyle.Paint);

        var groupedAnnotations = annotations
            .Where(annotation => annotation.Target == DiffAnnotationTarget.Node)
            .GroupBy(annotation => annotation.Kind)
            .Select(group => (Kind: group.Key, Count: group.Count(), Label: group.First().Label, IsHovered: group.Any(annotation => IsAnnotationHovered(annotation, hoveredAnnotationId))))
            .OrderBy(group => AnnotationPriority(group.Kind))
            .Take(4)
            .ToArray();
        if (groupedAnnotations.Length == 0)
        {
            return;
        }

        using var chipTextStyle = CreateUiTextStyle(9.5f, palette.Background, true);
        var right = (float)node.Bounds.Right - 10;
        foreach (var annotation in groupedAnnotations.Reverse())
        {
            var label = annotation.Count > 1 ? $"{ShortAnnotationLabel(annotation.Label)} {annotation.Count}" : ShortAnnotationLabel(annotation.Label);
            var width = Math.Clamp(chipTextStyle.Font.MeasureText(label, chipTextStyle.Paint) + 12, 34, 84);
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

    private static bool IsAnnotationHovered(IEnumerable<DiffAnnotation> annotations, string? hoveredAnnotationId) =>
        !string.IsNullOrWhiteSpace(hoveredAnnotationId) &&
        annotations.Any(annotation => IsAnnotationHovered(annotation, hoveredAnnotationId));

    private static bool IsAnnotationHovered(DiffAnnotation annotation, string? hoveredAnnotationId) =>
        !string.IsNullOrWhiteSpace(hoveredAnnotationId) &&
        string.Equals(annotation.Id, hoveredAnnotationId, StringComparison.Ordinal);

    private static string ShortAnnotationLabel(string label) => label.Length <= 10 ? label : label[..10];

    private static TextStyle CreateUiTextStyle(float size, SKColor color, bool bold) => new(
        "SF Pro Text",
        size,
        color,
        bold ? SKFontStyle.Bold : SKFontStyle.Normal);

    private static TextStyle CreateCodeTextStyle(float size, SKColor color) => new("Menlo", size, color, SKFontStyle.Normal);

    private sealed record RenderGraphEdge(GraphEdge Edge, DiffNode Source, DiffNode Target, Rect2 Bounds, SKPath Path);

    private sealed class RenderSceneCache : IDisposable
    {
        private RenderSceneCache(
            DiffCanvasScene scene,
            int geometryVersion,
            DiffNode[] nodes,
            RenderGraphEdge[] edges,
            Dictionary<DiffDocumentId, DiffAnnotation[]> annotationsByDocument)
        {
            Scene = scene;
            GeometryVersion = geometryVersion;
            Nodes = nodes;
            Edges = edges;
            AnnotationsByDocument = annotationsByDocument;
        }

        private DiffCanvasScene Scene { get; }

        private int GeometryVersion { get; }

        public DiffNode[] Nodes { get; }

        public RenderGraphEdge[] Edges { get; }

        public Dictionary<DiffDocumentId, DiffAnnotation[]> AnnotationsByDocument { get; }

        public static RenderSceneCache Create(DiffCanvasScene scene)
        {
            var nodes = scene.Nodes.ToArray();
            var nodesById = nodes.ToDictionary(node => node.Document.Id.Value, StringComparer.Ordinal);
            var edges = scene.Edges
                .Select(edge => CreateRenderGraphEdge(edge, nodesById))
                .Where(edge => edge is not null)
                .Cast<RenderGraphEdge>()
                .ToArray();
            var annotationsByDocument = scene.Annotations
                .Where(annotation => scene.AnnotationVisibility.IsVisible(annotation.Kind))
                .GroupBy(annotation => annotation.DocumentId)
                .ToDictionary(group => group.Key, group => group.OrderBy(AnnotationPriority).ToArray());

            return new RenderSceneCache(scene, scene.GeometryVersion, nodes, edges, annotationsByDocument);
        }

        public bool Matches(DiffCanvasScene scene) => ReferenceEquals(scene, Scene) && scene.GeometryVersion == GeometryVersion;

        public void Dispose()
        {
            foreach (var edge in Edges)
            {
                edge.Path.Dispose();
            }
        }

        private static RenderGraphEdge? CreateRenderGraphEdge(GraphEdge edge, IReadOnlyDictionary<string, DiffNode> nodesById)
        {
            if (!nodesById.TryGetValue(edge.SourceNodeId, out var source) ||
                !nodesById.TryGetValue(edge.TargetNodeId, out var target))
            {
                return null;
            }

            return new RenderGraphEdge(edge, source, target, GetEdgeBounds(source, target), CreateEdgePath(source, target));
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
        private readonly SKTypeface typeface;

        public TextStyle(string familyName, float size, SKColor color, SKFontStyle fontStyle)
        {
            typeface = SKTypeface.FromFamilyName(familyName, fontStyle);
            Font = new SKFont(typeface, size, 1, 0);
            Paint = new SKPaint { Color = color, IsAntialias = true };
        }

        public SKFont Font { get; }

        public SKPaint Paint { get; }

        public void Dispose()
        {
            Font.Dispose();
            Paint.Dispose();
            typeface.Dispose();
        }
    }
}
