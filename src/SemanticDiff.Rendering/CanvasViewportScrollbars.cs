using SemanticDiff.Core;

namespace SemanticDiff.Rendering;

public enum CanvasViewportScrollbarOrientation
{
    Horizontal,
    Vertical
}

public readonly record struct CanvasViewportScrollbarMetrics(
    CanvasViewportScrollbarOrientation Orientation,
    Rect2 TrackBounds,
    Rect2 ThumbBounds,
    double ContentStart,
    double ContentSize,
    double ViewportSize,
    double ScrollOffset,
    double MaxScrollOffset)
{
    public bool IsVisible =>
        !TrackBounds.IsEmpty &&
        !ThumbBounds.IsEmpty &&
        ContentSize > 0 &&
        ViewportSize > 0 &&
        MaxScrollOffset > 0;
}

public readonly record struct CanvasViewportScrollbarSet(
    CanvasViewportScrollbarMetrics Horizontal,
    CanvasViewportScrollbarMetrics Vertical)
{
    public bool HasHorizontal => Horizontal.IsVisible;

    public bool HasVertical => Vertical.IsVisible;
}

public static class CanvasViewportScrollbarCalculator
{
    public const double Thickness = 12;
    public const double Margin = 8;
    public const double Gap = 4;
    public const double MinimumThumbLength = 38;

    private const double MinimumWorldMargin = 160;
    private const double ViewportMarginFactor = 0.08;

    public static CanvasViewportScrollbarSet Calculate(Rect2 graphBounds, CameraState camera, Size2 viewportSize)
    {
        if (graphBounds.IsEmpty || viewportSize.Width <= 0 || viewportSize.Height <= 0 || camera.Scale <= 0)
        {
            return default;
        }

        var worldViewport = GetWorldViewport(camera, viewportSize);
        var worldMargin = Math.Max(MinimumWorldMargin, Math.Min(worldViewport.Width, worldViewport.Height) * ViewportMarginFactor);
        var contentBounds = graphBounds.Inflate(worldMargin);

        var hasHorizontal = contentBounds.Width > worldViewport.Width + 0.5;
        var hasVertical = contentBounds.Height > worldViewport.Height + 0.5;

        var horizontalTrack = hasHorizontal
            ? GetHorizontalTrack(viewportSize, hasVertical)
            : Rect2.Empty;
        var verticalTrack = hasVertical
            ? GetVerticalTrack(viewportSize, hasHorizontal)
            : Rect2.Empty;

        return new CanvasViewportScrollbarSet(
            hasHorizontal ? CreateHorizontal(horizontalTrack, contentBounds, worldViewport) : default,
            hasVertical ? CreateVertical(verticalTrack, contentBounds, worldViewport) : default);
    }

    public static Rect2 GetWorldViewport(CameraState camera, Size2 viewportSize)
    {
        var left = -camera.OffsetX / camera.Scale;
        var top = -camera.OffsetY / camera.Scale;
        return new Rect2(left, top, viewportSize.Width / camera.Scale, viewportSize.Height / camera.Scale);
    }

    private static CanvasViewportScrollbarMetrics CreateHorizontal(Rect2 track, Rect2 contentBounds, Rect2 worldViewport)
    {
        if (track.IsEmpty)
        {
            return default;
        }

        var maxScrollOffset = Math.Max(0, contentBounds.Width - worldViewport.Width);
        if (maxScrollOffset <= 0)
        {
            return default;
        }

        var viewportRatio = Math.Clamp(worldViewport.Width / contentBounds.Width, 0, 1);
        var thumbWidth = Math.Clamp(track.Width * viewportRatio, MinimumThumbLength, track.Width);
        var travel = Math.Max(0, track.Width - thumbWidth);
        if (travel <= 0)
        {
            return default;
        }

        var scrollOffset = Math.Clamp(worldViewport.Left - contentBounds.Left, 0, maxScrollOffset);
        var ratio = maxScrollOffset <= 0 ? 0 : scrollOffset / maxScrollOffset;
        var thumbLeft = track.Left + travel * ratio;
        var thumb = new Rect2(thumbLeft, track.Top, thumbWidth, track.Height);

        return new CanvasViewportScrollbarMetrics(
            CanvasViewportScrollbarOrientation.Horizontal,
            track,
            thumb,
            contentBounds.Left,
            contentBounds.Width,
            worldViewport.Width,
            scrollOffset,
            maxScrollOffset);
    }

    private static CanvasViewportScrollbarMetrics CreateVertical(Rect2 track, Rect2 contentBounds, Rect2 worldViewport)
    {
        if (track.IsEmpty)
        {
            return default;
        }

        var maxScrollOffset = Math.Max(0, contentBounds.Height - worldViewport.Height);
        if (maxScrollOffset <= 0)
        {
            return default;
        }

        var viewportRatio = Math.Clamp(worldViewport.Height / contentBounds.Height, 0, 1);
        var thumbHeight = Math.Clamp(track.Height * viewportRatio, MinimumThumbLength, track.Height);
        var travel = Math.Max(0, track.Height - thumbHeight);
        if (travel <= 0)
        {
            return default;
        }

        var scrollOffset = Math.Clamp(worldViewport.Top - contentBounds.Top, 0, maxScrollOffset);
        var ratio = maxScrollOffset <= 0 ? 0 : scrollOffset / maxScrollOffset;
        var thumbTop = track.Top + travel * ratio;
        var thumb = new Rect2(track.Left, thumbTop, track.Width, thumbHeight);

        return new CanvasViewportScrollbarMetrics(
            CanvasViewportScrollbarOrientation.Vertical,
            track,
            thumb,
            contentBounds.Top,
            contentBounds.Height,
            worldViewport.Height,
            scrollOffset,
            maxScrollOffset);
    }

    private static Rect2 GetHorizontalTrack(Size2 viewportSize, bool hasVertical)
    {
        var rightInset = hasVertical ? Thickness + Gap : 0;
        var width = Math.Max(0, viewportSize.Width - Margin * 2 - rightInset);
        if (width <= MinimumThumbLength)
        {
            return Rect2.Empty;
        }

        return new Rect2(Margin, viewportSize.Height - Margin - Thickness, width, Thickness);
    }

    private static Rect2 GetVerticalTrack(Size2 viewportSize, bool hasHorizontal)
    {
        var bottomInset = hasHorizontal ? Thickness + Gap : 0;
        var height = Math.Max(0, viewportSize.Height - Margin * 2 - bottomInset);
        if (height <= MinimumThumbLength)
        {
            return Rect2.Empty;
        }

        return new Rect2(viewportSize.Width - Margin - Thickness, Margin, Thickness, height);
    }
}
