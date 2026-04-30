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

    [Fact]
    public void CanvasViewportScrollbars_ShrinkThumbsWhenZoomingIn()
    {
        var graphBounds = new Rect2(0, 0, 2_000, 1_200);
        var viewport = new Size2(800, 500);
        var normal = CanvasViewportScrollbarCalculator.Calculate(graphBounds, new CameraState(0, 0, 1), viewport);
        var zoomed = CanvasViewportScrollbarCalculator.Calculate(graphBounds, new CameraState(0, 0, 2), viewport);

        Assert.True(normal.Horizontal.IsVisible);
        Assert.True(normal.Vertical.IsVisible);
        Assert.True(zoomed.Horizontal.ThumbBounds.Width < normal.Horizontal.ThumbBounds.Width);
        Assert.True(zoomed.Vertical.ThumbBounds.Height < normal.Vertical.ThumbBounds.Height);
    }

    [Fact]
    public void CanvasViewportScrollbars_MoveThumbsWithCameraOffset()
    {
        var graphBounds = new Rect2(0, 0, 2_000, 1_200);
        var viewport = new Size2(800, 500);
        var start = CanvasViewportScrollbarCalculator.Calculate(graphBounds, new CameraState(0, 0, 1), viewport);
        var panned = CanvasViewportScrollbarCalculator.Calculate(graphBounds, new CameraState(-500, -300, 1), viewport);

        Assert.True(panned.Horizontal.ThumbBounds.Left > start.Horizontal.ThumbBounds.Left);
        Assert.True(panned.Vertical.ThumbBounds.Top > start.Vertical.ThumbBounds.Top);
    }
}
