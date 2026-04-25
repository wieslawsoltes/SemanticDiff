using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Rendering;

namespace SemanticDiff.Tests;

public sealed class DiffCanvasSceneWheelTests
{
    [Fact]
    public void HandleWheel_ScrollsNodeWhenCursorIsOverNodeBody()
    {
        var scene = CreateScrollableScene();
        var node = scene.Nodes[0];
        var screenPoint = scene.Camera.WorldToScreen(new Point2(node.BodyBounds.X + 24, node.BodyBounds.Y + 24));
        var initialScale = scene.Camera.Scale;

        scene.HandleWheel(screenPoint, -120, zoomCanvas: false);

        Assert.Equal(initialScale, scene.Camera.Scale);
        Assert.True(node.ScrollOffsetY > 0);
    }

    [Fact]
    public void HandleWheel_ZoomsWhenCameraModifierIsActiveOverNodeBody()
    {
        var scene = CreateScrollableScene();
        var node = scene.Nodes[0];
        var screenPoint = scene.Camera.WorldToScreen(new Point2(node.BodyBounds.X + 24, node.BodyBounds.Y + 24));
        var initialScale = scene.Camera.Scale;

        scene.HandleWheel(screenPoint, 120, zoomCanvas: true);

        Assert.True(scene.Camera.Scale > initialScale);
        Assert.Equal(0, node.ScrollOffsetY);
    }

    [Fact]
    public void HandleWheel_DoesNothingWithoutModifierWhenPointerIsOutsideNodeBody()
    {
        var scene = CreateScrollableScene();
        var initialScale = scene.Camera.Scale;

        scene.HandleWheel(new Point2(10_000, 10_000), 120, zoomCanvas: false);

        Assert.Equal(initialScale, scene.Camera.Scale);
    }

    private static DiffCanvasScene CreateScrollableScene()
    {
        var factory = new DiffDocumentFactory();
        var text = string.Join('\n', Enumerable.Range(1, 80).Select(line => $"line {line}"));
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("Scrollable.cs"), "Scrollable.cs", null, DiffFileStatus.Modified, "C#", 0, 0),
            text);
        return DiffCanvasScene.FromDocuments([document]);
    }
}
