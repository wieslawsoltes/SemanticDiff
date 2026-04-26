using SemanticDiff.Diff;
using SemanticDiff.Core;
using SemanticDiff.Rendering;
using SkiaSharp;
using System.Collections.Immutable;

namespace SemanticDiff.Tests;

public sealed class DiffSceneRendererTests
{
    [Fact]
    public void Render_UsesDifferentBackgroundsForLightAndDarkThemes()
    {
        var scene = DiffCanvasScene.FromDocuments(SampleDiffDocuments.Create());
        var renderer = new DiffSceneRenderer();

        using var darkSurface = SKSurface.Create(new SKImageInfo(320, 240));
        using var lightSurface = SKSurface.Create(new SKImageInfo(320, 240));

        renderer.Render(darkSurface.Canvas, new SKSize(320, 240), scene, DiffCanvasColorTheme.Dark);
        renderer.Render(lightSurface.Canvas, new SKSize(320, 240), scene, DiffCanvasColorTheme.Light);

        using var darkImage = darkSurface.Snapshot();
        using var lightImage = lightSurface.Snapshot();
        Assert.NotEqual(darkImage.PeekPixels().GetPixelColor(0, 0), lightImage.PeekPixels().GetPixelColor(0, 0));
    }

    [Fact]
    public void Render_AcceptsDocumentsWithInlineDiffSpans()
    {
        var scene = DiffCanvasScene.FromDocuments(InlineDiffAnnotator.Annotate(SampleDiffDocuments.Create()));
        var renderer = new DiffSceneRenderer();

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));

        renderer.Render(surface.Canvas, new SKSize(800, 600), scene, DiffCanvasColorTheme.Dark);

        using var image = surface.Snapshot();
        Assert.NotEqual(SKColors.Transparent, image.PeekPixels().GetPixelColor(40, 40));
    }

    [Fact]
    public void Render_AcceptsDocumentsWithConflictLines()
    {
        var factory = new DiffDocumentFactory();
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("Conflict.cs"), "Conflict.cs", null, DiffFileStatus.Conflicted, "C#", 0, 0);
        var document = factory.CreateFromText(metadata, "<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> branch");
        var scene = DiffCanvasScene.FromDocuments([new DiffConflictAnalyzer().Highlight(document)]);
        var renderer = new DiffSceneRenderer();

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));

        renderer.Render(surface.Canvas, new SKSize(800, 600), scene, DiffCanvasColorTheme.Dark);

        using var image = surface.Snapshot();
        Assert.NotEqual(SKColors.Transparent, image.PeekPixels().GetPixelColor(40, 40));
    }

    [Fact]
    public void Render_DrawsVisibleLineAnnotations()
    {
        var documents = SampleDiffDocuments.Create();
        var document = documents[0];
        var annotation = DiffAnnotation.Line(
            document.Id,
            DiffAnnotationKind.Conflict,
            0,
            1,
            "conflict",
            "Unresolved conflict",
            DiffAnnotationSeverity.Error);
        var visibleScene = DiffCanvasScene.FromDocuments(documents, annotations: [annotation]);
        var hiddenScene = DiffCanvasScene.FromDocuments(
            documents,
            annotations: [annotation],
            annotationVisibility: new DiffAnnotationVisibilityState(ShowReview: false));
        var renderer = new DiffSceneRenderer();

        using var visibleSurface = SKSurface.Create(new SKImageInfo(800, 600));
        using var hiddenSurface = SKSurface.Create(new SKImageInfo(800, 600));

        renderer.Render(visibleSurface.Canvas, new SKSize(800, 600), visibleScene, DiffCanvasColorTheme.Dark);
        renderer.Render(hiddenSurface.Canvas, new SKSize(800, 600), hiddenScene, DiffCanvasColorTheme.Dark);

        using var visibleImage = visibleSurface.Snapshot();
        using var hiddenImage = hiddenSurface.Snapshot();
        var visiblePixels = visibleImage.PeekPixels();
        var hiddenPixels = hiddenImage.PeekPixels();
        var differingPixels = 0;
        for (var y = 64; y < 94; y++)
        {
            for (var x = 552; x < 652; x++)
            {
                if (hiddenPixels.GetPixelColor(x, y) != visiblePixels.GetPixelColor(x, y))
                {
                    differingPixels++;
                }
            }
        }

        Assert.True(differingPixels > 0);
    }

    [Fact]
    public void Render_UsesDistinctIndustryStatusColorsForNodeBadges()
    {
        var added = RenderStatusBadgePixel(DiffFileStatus.Added);
        var modified = RenderStatusBadgePixel(DiffFileStatus.Modified);
        var deleted = RenderStatusBadgePixel(DiffFileStatus.Deleted);
        var renamed = RenderStatusBadgePixel(DiffFileStatus.Renamed);

        Assert.True(added.Green > added.Red);
        Assert.True(modified.Red > modified.Blue && modified.Green > modified.Blue);
        Assert.True(deleted.Red > deleted.Green);
        Assert.True(renamed.Blue > renamed.Green);
        Assert.NotEqual(added, modified);
        Assert.NotEqual(modified, deleted);
        Assert.NotEqual(deleted, renamed);
    }

    [Fact]
    public void Render_CullsNodesOutsideWorldViewport()
    {
        var documents = CreateDocuments(48);
        var layout = documents
            .Select((document, index) => new DiffNodeLayout(
                document.Id,
                index == 0
                    ? new Rect2(0, 0, 620, 420)
                    : new Rect2(20_000 + index * 720, 20_000, 620, 420)))
            .ToImmutableArray();
        var scene = DiffCanvasScene.FromDocuments(documents, layoutResult: new GraphLayoutResult(layout));
        var renderer = new DiffSceneRenderer();

        using var surface = SKSurface.Create(new SKImageInfo(640, 480));

        renderer.Render(surface.Canvas, new SKSize(640, 480), scene, DiffCanvasColorTheme.Dark);

        Assert.Equal(documents.Length, renderer.LastRenderStats.TotalNodeCount);
        Assert.Equal(1, renderer.LastRenderStats.DrawnNodeCount);
    }

    [Fact]
    public void Render_DrawsNodeDetailsWhenZoomedOut()
    {
        var documents = CreateDocuments(80);
        var scene = DiffCanvasScene.FromDocuments(documents);
        scene.FitToGraph(new Size2(320, 240));
        var renderer = new DiffSceneRenderer();

        using var surface = SKSurface.Create(new SKImageInfo(320, 240));

        renderer.Render(surface.Canvas, new SKSize(320, 240), scene, DiffCanvasColorTheme.Dark);

        Assert.True(renderer.LastRenderStats.DrawnNodeCount > 0);
        Assert.Equal(renderer.LastRenderStats.DrawnNodeCount, renderer.LastRenderStats.DetailedNodeCount);
    }

    [Fact]
    public void Render_DrawsGraphGroupRegionsBehindNodes()
    {
        var factory = new DiffDocumentFactory();
        var first = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("src/App/A.cs"), "src/App/A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var second = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("src/App/B.cs"), "src/App/B.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class B { }");
        var layout = new GraphLayoutResult([
            new DiffNodeLayout(first.Id, new Rect2(120, 120, 620, 420)),
            new DiffNodeLayout(second.Id, new Rect2(860, 120, 620, 420))
        ]);
        var groupedScene = DiffCanvasScene.FromDocuments([first, second], layoutResult: layout, groupingMode: GraphGroupingMode.Folder);
        var plainScene = DiffCanvasScene.FromDocuments([first, second], layoutResult: layout, groupingMode: GraphGroupingMode.None);
        var renderer = new DiffSceneRenderer();

        using var groupedSurface = SKSurface.Create(new SKImageInfo(1_600, 720));
        using var plainSurface = SKSurface.Create(new SKImageInfo(1_600, 720));
        renderer.Render(groupedSurface.Canvas, new SKSize(1_600, 720), groupedScene, DiffCanvasColorTheme.Dark);
        renderer.Render(plainSurface.Canvas, new SKSize(1_600, 720), plainScene, DiffCanvasColorTheme.Dark);

        using var groupedImage = groupedSurface.Snapshot();
        using var plainImage = plainSurface.Snapshot();
        var groupedPixels = groupedImage.PeekPixels();
        var plainPixels = plainImage.PeekPixels();
        var differingPixels = 0;
        for (var y = 106; y < 124; y++)
        {
            for (var x = 120; x < 1_500; x += 8)
            {
                if (plainPixels.GetPixelColor(x, y) != groupedPixels.GetPixelColor(x, y))
                {
                    differingPixels++;
                }
            }
        }

        Assert.True(differingPixels > 0);
    }

    [Fact]
    public void Render_CachesSceneEdgeGeometryAcrossFrames()
    {
        var factory = new DiffDocumentFactory();
        var first = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var second = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("B.cs"), "B.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class B { }");
        var third = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("C.cs"), "C.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class C { }");
        var graph = new SemanticGraph(
            [
                new SemanticAnchor("A:type", first.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "A"),
                new SemanticAnchor("B:type", second.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "B"),
                new SemanticAnchor("C:type", third.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "C")
            ],
            [
                new SemanticEdge("A->B", "A:type", "B:type", SemanticEdgeKind.SymbolReference, 0.82, "B"),
                new SemanticEdge("B->C", "B:type", "C:type", SemanticEdgeKind.SymbolReference, 0.82, "C")
            ]);
        var scene = DiffCanvasScene.FromDocuments([first, second, third], graph, groupingMode: GraphGroupingMode.None);
        var renderer = new DiffSceneRenderer();

        using var surface = SKSurface.Create(new SKImageInfo(1_400, 900));
        renderer.Render(surface.Canvas, new SKSize(1_400, 900), scene, DiffCanvasColorTheme.Dark);
        renderer.Render(surface.Canvas, new SKSize(1_400, 900), scene, DiffCanvasColorTheme.Dark);

        Assert.Equal(scene.Edges.Count, renderer.LastRenderStats.CachedEdgePathCount);

        scene.MoveNode(scene.Nodes[1], 42, 0);
        renderer.Render(surface.Canvas, new SKSize(1_400, 900), scene, DiffCanvasColorTheme.Dark);

        Assert.Equal(scene.Edges.Count, renderer.LastRenderStats.CachedEdgePathCount);
    }

    [Fact]
    public void Render_KeepsGraphGroupLabelsScreenStableAcrossZoom()
    {
        var group = new GraphGroup("folder:src/App", GraphGroupingMode.Folder, "src/App", new Rect2(100, 120, 2, 2), 4, 10, 3, 0);
        var normalScene = new DiffCanvasScene([], [], [group]);
        var zoomedScene = new DiffCanvasScene([], [], [group]);
        zoomedScene.ZoomAt(Point2.Zero, 2.0);

        var normalBounds = MeasureGroupLabelBounds(normalScene, group);
        var zoomedBounds = MeasureGroupLabelBounds(zoomedScene, group);

        Assert.InRange(normalBounds.Height, 18, 26);
        Assert.InRange(zoomedBounds.Height, 18, 26);
        Assert.True(Math.Abs(normalBounds.Height - zoomedBounds.Height) <= 2);
    }

    [Fact]
    public void Render_KeepsFontControlButtonsScreenStableAcrossZoom()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("src/App/MainPage.xaml.cs"), "src/App/MainPage.xaml.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class MainPage { }");
        var normalScene = DiffCanvasScene.FromDocuments([document], groupingMode: GraphGroupingMode.None);
        var zoomedScene = DiffCanvasScene.FromDocuments([document], groupingMode: GraphGroupingMode.None);
        zoomedScene.ZoomAt(Point2.Zero, 2.0);

        var normalBounds = MeasureFontButtonBounds(normalScene, normalScene.Nodes[0]);
        var zoomedBounds = MeasureFontButtonBounds(zoomedScene, zoomedScene.Nodes[0]);

        Assert.InRange(normalBounds.Width, 17, 23);
        Assert.InRange(normalBounds.Height, 17, 23);
        Assert.InRange(zoomedBounds.Width, 17, 23);
        Assert.InRange(zoomedBounds.Height, 17, 23);
        Assert.True(Math.Abs(normalBounds.Width - zoomedBounds.Width) <= 2);
        Assert.True(Math.Abs(normalBounds.Height - zoomedBounds.Height) <= 2);
    }

    private static SKColor RenderStatusBadgePixel(DiffFileStatus status)
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId($"{status}.cs"), $"{status}.cs", null, status, "C#", 0, 0),
            "class Sample { }");
        var scene = DiffCanvasScene.FromDocuments([document]);
        var renderer = new DiffSceneRenderer();

        using var surface = SKSurface.Create(new SKImageInfo(240, 160));
        renderer.Render(surface.Canvas, new SKSize(240, 160), scene, DiffCanvasColorTheme.Dark);

        using var image = surface.Snapshot();
        return image.PeekPixels().GetPixelColor(48, 48);
    }

    private static PixelBounds MeasureGroupLabelBounds(DiffCanvasScene scene, GraphGroup group)
    {
        var plainScene = new DiffCanvasScene([], []);
        var renderer = new DiffSceneRenderer();
        using var groupedSurface = SKSurface.Create(new SKImageInfo(800, 520));
        using var plainSurface = SKSurface.Create(new SKImageInfo(800, 520));
        plainScene.ApplyViewState(scene.CaptureViewState());

        renderer.Render(groupedSurface.Canvas, new SKSize(800, 520), scene, DiffCanvasColorTheme.Dark);
        renderer.Render(plainSurface.Canvas, new SKSize(800, 520), plainScene, DiffCanvasColorTheme.Dark);

        using var groupedImage = groupedSurface.Snapshot();
        using var plainImage = plainSurface.Snapshot();
        var labelPoint = scene.Camera.WorldToScreen(new Point2(group.Bounds.Left + 14, group.Bounds.Top + 9));
        return MeasureDifferentPixelBounds(
            plainImage.PeekPixels(),
            groupedImage.PeekPixels(),
            new PixelClip((int)Math.Floor(labelPoint.X), (int)Math.Floor(labelPoint.Y), 320, 64));
    }

    private static PixelBounds MeasureFontButtonBounds(DiffCanvasScene scene, DiffNode node)
    {
        var renderer = new DiffSceneRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(1_600, 900));
        renderer.Render(surface.Canvas, new SKSize(1_600, 900), scene, DiffCanvasColorTheme.Dark);

        using var image = surface.Snapshot();
        var pixels = image.PeekPixels();
        var button = ToScreenRect(scene.Camera, node.GetFontSizeButtonBounds(DiffNodeFontSizeAction.Increase, scene.Camera.Scale));
        return MeasurePixelsExceptColor(
            pixels,
            new PixelClip((int)Math.Floor(button.Left) - 2, (int)Math.Floor(button.Top) - 2, (int)Math.Ceiling(button.Width) + 4, (int)Math.Ceiling(button.Height) + 4),
            new SKColor(20, 28, 39));
    }

    private static SKRect ToScreenRect(CameraState camera, Rect2 rectangle)
    {
        var topLeft = camera.WorldToScreen(new Point2(rectangle.Left, rectangle.Top));
        var bottomRight = camera.WorldToScreen(new Point2(rectangle.Right, rectangle.Bottom));
        return SKRect.Create(
            (float)Math.Min(topLeft.X, bottomRight.X),
            (float)Math.Min(topLeft.Y, bottomRight.Y),
            (float)Math.Abs(bottomRight.X - topLeft.X),
            (float)Math.Abs(bottomRight.Y - topLeft.Y));
    }

    private static PixelBounds MeasureDifferentPixelBounds(SKPixmap baseline, SKPixmap rendered, PixelClip clip)
    {
        return MeasurePixels(clip, baseline.Width, baseline.Height, (x, y) => baseline.GetPixelColor(x, y) != rendered.GetPixelColor(x, y));
    }

    private static PixelBounds MeasurePixelsExceptColor(SKPixmap pixels, PixelClip clip, SKColor excludedColor)
    {
        return MeasurePixels(clip, pixels.Width, pixels.Height, (x, y) => pixels.GetPixelColor(x, y) != excludedColor);
    }

    private static PixelBounds MeasurePixels(PixelClip clip, int imageWidth, int imageHeight, Func<int, int, bool> predicate)
    {
        var left = Math.Clamp(clip.Left, 0, imageWidth - 1);
        var top = Math.Clamp(clip.Top, 0, imageHeight - 1);
        var right = Math.Clamp(clip.Left + clip.Width, 0, imageWidth);
        var bottom = Math.Clamp(clip.Top + clip.Height, 0, imageHeight);
        var minX = imageWidth;
        var minY = imageHeight;
        var maxX = -1;
        var maxY = -1;

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                if (!predicate(x, y))
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return maxX < minX || maxY < minY
            ? PixelBounds.Empty
            : new PixelBounds(maxX - minX + 1, maxY - minY + 1);
    }

    private static ImmutableArray<DiffDocumentSnapshot> CreateDocuments(int count)
    {
        var factory = new DiffDocumentFactory();
        return Enumerable.Range(0, count)
            .Select(index => factory.CreateFromText(
                new DiffDocumentMetadata(new DiffDocumentId($"File{index:000}.cs"), $"File{index:000}.cs", null, DiffFileStatus.Modified, "C#", 0, 0),
                "class Sample { }"))
            .ToImmutableArray();
    }

    private readonly record struct PixelClip(int Left, int Top, int Width, int Height);

    private readonly record struct PixelBounds(int Width, int Height)
    {
        public static PixelBounds Empty { get; } = new(0, 0);
    }
}
