using SemanticDiff.Core;

namespace SemanticDiff.Rendering;

public sealed record CameraState(double OffsetX, double OffsetY, double Scale)
{
    public const double MinScale = 0.002;
    public const double MaxScale = 4.0;

    public static CameraState Default { get; } = new(32, 32, 1);

    public Point2 ScreenToWorld(Point2 screenPoint) => new((screenPoint.X - OffsetX) / Scale, (screenPoint.Y - OffsetY) / Scale);

    public Point2 WorldToScreen(Point2 worldPoint) => new(worldPoint.X * Scale + OffsetX, worldPoint.Y * Scale + OffsetY);

    public CameraState Pan(double deltaX, double deltaY) => this with { OffsetX = OffsetX + deltaX, OffsetY = OffsetY + deltaY };

    public CameraState ZoomAt(Point2 screenPoint, double zoomFactor)
    {
        var nextScale = Math.Clamp(Scale * zoomFactor, MinScale, MaxScale);
        var worldPoint = ScreenToWorld(screenPoint);
        var nextOffsetX = screenPoint.X - worldPoint.X * nextScale;
        var nextOffsetY = screenPoint.Y - worldPoint.Y * nextScale;

        return new CameraState(nextOffsetX, nextOffsetY, nextScale);
    }

    public static CameraState Fit(Rect2 worldBounds, Size2 viewportSize, double margin)
    {
        if (worldBounds.IsEmpty || viewportSize.Width <= 0 || viewportSize.Height <= 0)
        {
            return Default;
        }

        var availableWidth = Math.Max(1, viewportSize.Width - margin * 2);
        var availableHeight = Math.Max(1, viewportSize.Height - margin * 2);
        var scaleX = availableWidth / worldBounds.Width;
        var scaleY = availableHeight / worldBounds.Height;
        var scale = Math.Clamp(Math.Min(scaleX, scaleY), MinScale, MaxScale);
        var offsetX = (viewportSize.Width - worldBounds.Width * scale) / 2 - worldBounds.X * scale;
        var offsetY = (viewportSize.Height - worldBounds.Height * scale) / 2 - worldBounds.Y * scale;

        return new CameraState(offsetX, offsetY, scale);
    }
}