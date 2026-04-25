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

    private static ImmutableArray<DiffDocumentSnapshot> CreateDocuments(int count)
    {
        var factory = new DiffDocumentFactory();
        return Enumerable.Range(0, count)
            .Select(index => factory.CreateFromText(
                new DiffDocumentMetadata(new DiffDocumentId($"File{index:000}.cs"), $"File{index:000}.cs", null, DiffFileStatus.Modified, "C#", 0, 0),
                "class Sample { }"))
            .ToImmutableArray();
    }
}
