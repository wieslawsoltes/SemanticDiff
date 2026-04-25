using SemanticDiff.Core;
using SemanticDiff.Rendering;

namespace SemanticDiff.Tests;

public sealed class ViewportControllerTests
{
    [Fact]
    public void ZoomAt_KeepsWorldPointUnderCursorStable()
    {
        var camera = new CameraState(20, 40, 1);
        var screenPoint = new Point2(200, 160);
        var before = camera.ScreenToWorld(screenPoint);

        var zoomed = camera.ZoomAt(screenPoint, 1.5);
        var after = zoomed.ScreenToWorld(screenPoint);

        Assert.Equal(before.X, after.X, precision: 6);
        Assert.Equal(before.Y, after.Y, precision: 6);
    }
}