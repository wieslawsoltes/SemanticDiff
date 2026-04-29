using SemanticDiff.Core;
using SkiaSharp;

namespace SemanticDiff.Rendering.Export;

public interface IDiffSceneExporter
{
    void Export(DiffCanvasScene scene, Stream stream, DiffSceneExportOptions options);
}

public enum DiffSceneExportFormat
{
    Svg,
    Png,
    Pdf
}

public sealed record DiffSceneExportOptions(
    DiffSceneExportFormat Format,
    bool IsLightTheme,
    int Margin = 96,
    int MaximumPngPixels = 72_000_000);

public sealed class DiffSceneExportService : IDiffSceneExporter
{
    private const int MinimumWidth = 1200;
    private const int MinimumHeight = 800;
    private const int MaximumVectorEdge = 20000;
    private const int MaximumPngEdge = 12000;

    public void Export(DiffCanvasScene scene, Stream stream, DiffSceneExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(stream);

        var exportScene = CloneScene(scene);
        var exportSize = GetExportSize(exportScene.GraphBounds, options);
        exportScene.FitToGraph(new Size2(exportSize.Width, exportSize.Height));

        switch (options.Format)
        {
            case DiffSceneExportFormat.Svg:
                ExportSvg(exportScene, stream, exportSize, options.IsLightTheme);
                break;
            case DiffSceneExportFormat.Png:
                ExportPng(exportScene, stream, exportSize, options.IsLightTheme);
                break;
            case DiffSceneExportFormat.Pdf:
                ExportPdf(exportScene, stream, exportSize, options.IsLightTheme);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Format, "Unsupported scene export format.");
        }
    }

    private static void ExportSvg(DiffCanvasScene scene, Stream stream, SKSizeI exportSize, bool isLightTheme)
    {
        using var canvas = SKSvgCanvas.Create(SKRect.Create(exportSize.Width, exportSize.Height), stream);
        Render(canvas, scene, exportSize, isLightTheme);
        canvas.Flush();
    }

    private static void ExportPng(DiffCanvasScene scene, Stream stream, SKSizeI exportSize, bool isLightTheme)
    {
        var imageInfo = new SKImageInfo(exportSize.Width, exportSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(imageInfo);
        Render(surface.Canvas, scene, exportSize, isLightTheme);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        data.SaveTo(stream);
    }

    private static void ExportPdf(DiffCanvasScene scene, Stream stream, SKSizeI exportSize, bool isLightTheme)
    {
        using var document = SKDocument.CreatePdf(stream);
        var canvas = document.BeginPage(exportSize.Width, exportSize.Height);
        Render(canvas, scene, exportSize, isLightTheme);
        document.EndPage();
        document.Close();
    }

    private static void Render(SKCanvas canvas, DiffCanvasScene scene, SKSizeI exportSize, bool isLightTheme)
    {
        var renderer = new DiffSceneRenderer();
        renderer.Render(
            canvas,
            new SKSize(exportSize.Width, exportSize.Height),
            scene,
            isLightTheme ? DiffCanvasColorTheme.Light : DiffCanvasColorTheme.Dark,
            DiffSceneRenderMode.Normal);
    }

    private static DiffCanvasScene CloneScene(DiffCanvasScene sourceScene)
    {
        var nodes = sourceScene.Nodes
            .Select(node =>
            {
                var clone = new DiffNode(node.DiffDocument, node.Bounds, node.IsPinned, node.FontSize)
                {
                    IsSelected = node.IsSelected
                };
                if (node.FullFileDocument is not null && node.FullText is not null)
                {
                    clone.SetFullFileDocument(node.FullFileDocument, node.FoldRegions, node.FullText);
                    clone.ApplyWorkspaceMode(sourceScene.ShowFullFileNodes, sourceScene.EnableNodeEditing);
                }

                clone.RestoreScrollOffset(node.ScrollOffsetY);
                return clone;
            })
            .ToArray();

        var scene = new DiffCanvasScene(
            nodes,
            sourceScene.Edges.ToArray(),
            sourceScene.Groups,
            sourceScene.Annotations,
            sourceScene.AnnotationVisibility);
        scene.SetShowFullFileNodes(sourceScene.ShowFullFileNodes);
        scene.SetNodeEditingEnabled(sourceScene.EnableNodeEditing);
        return scene;
    }

    private static SKSizeI GetExportSize(Rect2 graphBounds, DiffSceneExportOptions options)
    {
        var margin = Math.Max(0, options.Margin);
        var desiredWidth = Math.Max(MinimumWidth, graphBounds.Width + margin * 2);
        var desiredHeight = Math.Max(MinimumHeight, graphBounds.Height + margin * 2);
        var maximumEdge = options.Format == DiffSceneExportFormat.Png ? MaximumPngEdge : MaximumVectorEdge;
        var scale = Math.Min(1, maximumEdge / Math.Max(desiredWidth, desiredHeight));
        var maximumPngPixels = Math.Max(1, options.MaximumPngPixels);

        if (options.Format == DiffSceneExportFormat.Png && desiredWidth * desiredHeight * scale * scale > maximumPngPixels)
        {
            scale = Math.Sqrt(maximumPngPixels / (desiredWidth * desiredHeight));
        }

        return new SKSizeI(
            Math.Max(1, (int)Math.Ceiling(desiredWidth * scale)),
            Math.Max(1, (int)Math.Ceiling(desiredHeight * scale)));
    }
}
