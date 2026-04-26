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

    [Fact]
    public void Fit_AllowsVeryLargeGraphsToFitBelowOldZoomFloor()
    {
        var bounds = new Rect2(0, 0, 5_000_000, 2_000_000);
        var viewport = new Size2(1_200, 700);

        var camera = CameraState.Fit(bounds, viewport, 48);
        var topLeft = camera.WorldToScreen(new Point2(bounds.Left, bounds.Top));
        var bottomRight = camera.WorldToScreen(new Point2(bounds.Right, bounds.Bottom));

        Assert.True(camera.Scale < 0.002);
        Assert.True(topLeft.X >= 47);
        Assert.True(topLeft.Y >= 47);
        Assert.True(bottomRight.X <= viewport.Width - 47);
        Assert.True(bottomRight.Y <= viewport.Height - 47);
    }
}