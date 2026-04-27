using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SemanticDiff.Core;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using Windows.Foundation;

namespace SemanticDiff.App.Views;

public sealed class CodeFileViewerControl : Grid
{
    private const float TopPadding = 8;
    private const float BottomPadding = 8;
    private const float LeftPadding = 8;
    private const float FoldGutterWidth = 22;
    private const float TextPadding = 10;
    private const float ScrollbarWidth = 8;
    private const float ScrollbarMargin = 4;
    private const float FontSize = 12;
    private const float LineHeight = 18;
    private const int TabSize = 4;

    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines),
        typeof(object),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty FoldRegionsProperty = DependencyProperty.Register(
        nameof(FoldRegions),
        typeof(object),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty IsDiffModeProperty = DependencyProperty.Register(
        nameof(IsDiffMode),
        typeof(bool),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(false, OnContentChanged));

    public static readonly DependencyProperty RefreshKeyProperty = DependencyProperty.Register(
        nameof(RefreshKey),
        typeof(object),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(null, OnContentChanged));

    private readonly SKXamlCanvas canvas;
    private readonly HashSet<int> collapsedFoldStarts = [];
    private readonly SKTypeface typeface = SKTypeface.FromFamilyName("Cascadia Mono") ?? SKTypeface.Default;
    private readonly SKTypeface boldTypeface = SKTypeface.FromFamilyName("Cascadia Mono", SKFontStyle.Bold) ?? SKTypeface.Default;
    private ImmutableArray<VisibleCodeRow> visibleRows = [];
    private Dictionary<int, CodeFoldRegion> foldRegionsByStart = [];
    private Size2 lastCanvasSize = Size2.Zero;
    private double scrollOffsetY;
    private uint? activePointerId;
    private double scrollbarGrabOffsetY;
    private int? hoveredFoldStartLine;
    private bool visibleRowsDirty = true;
    private bool isDraggingScrollbar;

    public CodeFileViewerControl()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        canvas = new SKXamlCanvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        canvas.PaintSurface += OnPaintSurface;
        Children.Add(canvas);

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerReleased;
        PointerCaptureLost += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        PointerExited += OnPointerExited;
        Loaded += (_, _) => RequestDeferredRender();
        SizeChanged += (_, _) =>
        {
            ClampScrollOffset();
            RequestRender();
        };
        ActualThemeChanged += (_, _) => RequestRender();
        Unloaded += (_, _) =>
        {
            typeface.Dispose();
            if (!ReferenceEquals(typeface, boldTypeface))
            {
                boldTypeface.Dispose();
            }
        };
    }

    public object? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public object? FoldRegions
    {
        get => GetValue(FoldRegionsProperty);
        set => SetValue(FoldRegionsProperty, value);
    }

    public bool IsDiffMode
    {
        get => (bool)GetValue(IsDiffModeProperty);
        set => SetValue(IsDiffModeProperty, value);
    }

    public object? RefreshKey
    {
        get => GetValue(RefreshKeyProperty);
        set => SetValue(RefreshKeyProperty, value);
    }

    private static void OnContentChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CodeFileViewerControl control)
        {
            control.visibleRowsDirty = true;
            control.hoveredFoldStartLine = null;
            control.TrimCollapsedFoldState();
            control.ClampScrollOffset();
            control.RequestDeferredRender();
        }
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs args)
    {
        lastCanvasSize = new Size2(args.Info.Width, args.Info.Height);
        EnsureVisibleRows();
        ClampScrollOffset();

        var palette = ViewerPalette.Create(ActualTheme == ElementTheme.Light);
        var canvasSurface = args.Surface.Canvas;
        canvasSurface.Clear(palette.Background);

        var width = args.Info.Width;
        var height = args.Info.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        using var font = new SKFont(typeface, FontSize);
        using var boldFont = new SKFont(boldTypeface, FontSize);
        using var defaultPaint = CreateTextPaint(palette.Text);
        using var mutedPaint = CreateTextPaint(palette.MutedText);
        using var lineNumberPaint = CreateTextPaint(palette.LineNumber);
        using var foldPaint = CreateTextPaint(palette.FoldText);
        var charWidth = Math.Max(1, font.MeasureText("M", defaultPaint));
        var lines = GetLines();
        var gutterWidth = CalculateGutterWidth(charWidth, lines);
        DrawGutter(canvasSurface, palette, gutterWidth, height);

        var firstRow = Math.Max(0, (int)Math.Floor((scrollOffsetY - TopPadding) / LineHeight));
        var lastRow = Math.Min(visibleRows.Length - 1, (int)Math.Ceiling((scrollOffsetY + height) / LineHeight));
        if (visibleRows.Length == 0 || firstRow > lastRow)
        {
            DrawEmptyState(canvasSurface, width, height, mutedPaint, font);
            return;
        }

        using (new SKAutoCanvasRestore(canvasSurface, doSave: true))
        {
            canvasSurface.ClipRect(SKRect.Create(gutterWidth, 0, Math.Max(0, width - gutterWidth - ScrollbarWidth - ScrollbarMargin), height));

            for (var rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
            {
                var row = visibleRows[rowIndex];
                if (row.LineIndex < 0 || row.LineIndex >= lines.Count)
                {
                    continue;
                }

                var y = TopPadding + rowIndex * LineHeight - (float)scrollOffsetY;
                var line = lines[row.LineIndex];
                DrawLineBackground(canvasSurface, palette, gutterWidth, width, y, line.Kind, row.CollapsedRegion is not null);
                if (IsDiffMode)
                {
                    DrawDiffGutter(canvasSurface, palette, line, gutterWidth, y, lineNumberPaint, font);
                }
                else
                {
                    DrawLineNumber(canvasSurface, line.Index + 1, gutterWidth, y, lineNumberPaint, font);
                    DrawFoldMarker(canvasSurface, palette, row.LineIndex, row.CollapsedRegion, y);
                }

                DrawCodeLine(canvasSurface, lines[row.LineIndex], row.CollapsedRegion, gutterWidth + TextPadding, y, charWidth, font, boldFont, defaultPaint, foldPaint, palette);
            }
        }

        DrawScrollbar(canvasSurface, palette, width, height);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var pointerPoint = args.GetCurrentPoint(this);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = ToCanvasPoint(pointerPoint.Position);
        if (TryHitTestScrollbar(point, out var thumb))
        {
            isDraggingScrollbar = true;
            activePointerId = args.Pointer.PointerId;
            scrollbarGrabOffsetY = point.Y - thumb.Top;
            CapturePointer(args.Pointer);
            args.Handled = true;
            return;
        }

        if (TryHitTestFold(point, out var region))
        {
            if (!collapsedFoldStarts.Add(region.StartLineIndex))
            {
                collapsedFoldStarts.Remove(region.StartLineIndex);
            }

            visibleRowsDirty = true;
            ClampScrollOffset();
            RequestRender();
            args.Handled = true;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        var point = ToCanvasPoint(args.GetCurrentPoint(this).Position);
        if (isDraggingScrollbar && activePointerId == args.Pointer.PointerId)
        {
            DragScrollbar(point.Y);
            args.Handled = true;
            return;
        }

        var nextHoveredFold = TryHitTestFold(point, out var region) ? region.StartLineIndex : (int?)null;
        if (hoveredFoldStartLine != nextHoveredFold)
        {
            hoveredFoldStartLine = nextHoveredFold;
            RequestRender();
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (activePointerId == args.Pointer.PointerId)
        {
            isDraggingScrollbar = false;
            activePointerId = null;
            ReleasePointerCaptures();
            args.Handled = true;
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        var delta = args.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        scrollOffsetY -= delta / 120.0 * LineHeight * 3;
        ClampScrollOffset();
        RequestRender();
        args.Handled = true;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (hoveredFoldStartLine is not null)
        {
            hoveredFoldStartLine = null;
            RequestRender();
        }
    }

    private void DragScrollbar(double pointerY)
    {
        var viewportHeight = GetCanvasSize().Height;
        var contentHeight = GetContentHeight();
        if (contentHeight <= viewportHeight)
        {
            scrollOffsetY = 0;
            return;
        }

        var track = GetScrollbarTrack(GetCanvasSize().Width, viewportHeight);
        var thumbHeight = GetScrollbarThumbHeight(track.Height, viewportHeight, contentHeight);
        var trackScrollable = Math.Max(1, track.Height - thumbHeight);
        var thumbTop = Math.Clamp(pointerY - scrollbarGrabOffsetY, track.Top, track.Bottom - thumbHeight);
        scrollOffsetY = (thumbTop - track.Top) / trackScrollable * (contentHeight - viewportHeight);
        ClampScrollOffset();
        RequestRender();
    }

    private void DrawGutter(SKCanvas canvasSurface, ViewerPalette palette, float gutterWidth, int height)
    {
        using var gutterPaint = new SKPaint { Color = palette.GutterBackground, Style = SKPaintStyle.Fill };
        using var borderPaint = new SKPaint { Color = palette.Border, StrokeWidth = 1, Style = SKPaintStyle.Stroke };
        canvasSurface.DrawRect(SKRect.Create(0, 0, gutterWidth, height), gutterPaint);
        canvasSurface.DrawLine(gutterWidth - 0.5f, 0, gutterWidth - 0.5f, height, borderPaint);
    }

    private static void DrawLineBackground(SKCanvas canvasSurface, ViewerPalette palette, float gutterWidth, int width, float y, DiffLineKind kind, bool isCollapsed)
    {
        var color = kind switch
        {
            DiffLineKind.Added => palette.AddedBackground,
            DiffLineKind.Deleted => palette.DeletedBackground,
            DiffLineKind.Modified => palette.ModifiedBackground,
            DiffLineKind.Moved => palette.MovedBackground,
            DiffLineKind.Conflict => palette.ConflictBackground,
            DiffLineKind.Metadata => palette.MetadataBackground,
            DiffLineKind.Imaginary => palette.FoldBackground,
            _ => isCollapsed ? palette.FoldBackground : default
        };

        if (color == default)
        {
            return;
        }

        using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvasSurface.DrawRect(SKRect.Create(gutterWidth, y, Math.Max(0, width - gutterWidth - ScrollbarWidth - ScrollbarMargin), LineHeight), paint);
    }

    private static void DrawLineNumber(SKCanvas canvasSurface, int lineNumber, float gutterWidth, float y, SKPaint paint, SKFont font)
    {
        var text = lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var width = font.MeasureText(text, paint);
        canvasSurface.DrawText(text, gutterWidth - 10 - width, y + 13, font, paint);
    }

    private static void DrawDiffGutter(SKCanvas canvasSurface, ViewerPalette palette, DiffLine line, float gutterWidth, float y, SKPaint lineNumberPaint, SKFont font)
    {
        var markerPaint = CreateTextPaint(LineAccentColor(line.Kind, palette));
        try
        {
            var oldText = line.OldLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            var newText = line.NewLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            var marker = MarkerFor(line.Kind);
            var oldX = LeftPadding + 34;
            var newX = oldX + 44;
            var markerX = newX + 34;

            DrawRightAlignedText(canvasSurface, oldText, oldX, y + 13, font, lineNumberPaint);
            DrawRightAlignedText(canvasSurface, newText, newX, y + 13, font, lineNumberPaint);
            if (!string.IsNullOrEmpty(marker))
            {
                canvasSurface.DrawText(marker, markerX, y + 13, font, markerPaint);
            }

            using var lanePaint = new SKPaint { Color = LineAccentColor(line.Kind, palette), Style = SKPaintStyle.Fill, IsAntialias = true };
            if (line.Kind is DiffLineKind.Added or DiffLineKind.Deleted or DiffLineKind.Modified or DiffLineKind.Moved or DiffLineKind.Conflict)
            {
                canvasSurface.DrawRoundRect(SKRect.Create(gutterWidth - 4, y + 2, 3, LineHeight - 4), 1.5f, 1.5f, lanePaint);
            }
        }
        finally
        {
            markerPaint.Dispose();
        }
    }

    private static void DrawRightAlignedText(SKCanvas canvasSurface, string text, float right, float baseline, SKFont font, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        canvasSurface.DrawText(text, right - font.MeasureText(text, paint), baseline, font, paint);
    }

    private void DrawFoldMarker(SKCanvas canvasSurface, ViewerPalette palette, int lineIndex, CodeFoldRegion? collapsedRegion, float y)
    {
        if (!foldRegionsByStart.TryGetValue(lineIndex, out var region))
        {
            return;
        }

        var isCollapsed = collapsedRegion is not null || collapsedFoldStarts.Contains(region.StartLineIndex);
        var isHovered = hoveredFoldStartLine == region.StartLineIndex;
        var rect = SKRect.Create(LeftPadding + 2, y + 3, 11, 11);
        using var fillPaint = new SKPaint { Color = isHovered ? palette.FoldHoverBackground : palette.GutterBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var strokePaint = new SKPaint { Color = isHovered ? palette.Accent : palette.FoldMarker, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvasSurface.DrawRoundRect(rect, 2, 2, fillPaint);
        canvasSurface.DrawRoundRect(rect, 2, 2, strokePaint);
        canvasSurface.DrawLine(rect.Left + 3, rect.MidY, rect.Right - 3, rect.MidY, strokePaint);
        if (isCollapsed)
        {
            canvasSurface.DrawLine(rect.MidX, rect.Top + 3, rect.MidX, rect.Bottom - 3, strokePaint);
        }
    }

    private void DrawCodeLine(
        SKCanvas canvasSurface,
        DiffLine line,
        CodeFoldRegion? collapsedRegion,
        float x,
        float y,
        float charWidth,
        SKFont font,
        SKFont boldFont,
        SKPaint defaultPaint,
        SKPaint foldPaint,
        ViewerPalette palette)
    {
        DrawTokenizedText(canvasSurface, line.Text, line.Tokens, x, y + 13, charWidth, font, boldFont, defaultPaint, palette);
        if (collapsedRegion is null)
        {
            return;
        }

        var visualLength = GetVisualColumn(line.Text, line.Text.Length);
        var placeholder = $"  ... {collapsedRegion.CollapsedLineCount:N0} folded lines ...";
        using var chipPaint = new SKPaint { Color = palette.FoldChipBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        var chipX = x + visualLength * charWidth + 8;
        var chipWidth = Math.Max(140, boldFont.MeasureText(placeholder, foldPaint) + 12);
        var chipRect = SKRect.Create(chipX, y + 2, chipWidth, LineHeight - 4);
        canvasSurface.DrawRoundRect(chipRect, 4, 4, chipPaint);
        canvasSurface.DrawText(placeholder, chipRect.Left + 6, y + 13, boldFont, foldPaint);
    }

    private static void DrawTokenizedText(
        SKCanvas canvasSurface,
        string text,
        ImmutableArray<TokenSpan> tokens,
        float x,
        float baseline,
        float charWidth,
        SKFont font,
        SKFont boldFont,
        SKPaint defaultPaint,
        ViewerPalette palette)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var cursor = 0;
        foreach (var token in tokens.IsDefault
            ? Enumerable.Empty<TokenSpan>()
            : tokens.OrderBy(token => token.StartColumn))
        {
            var start = Math.Clamp(token.StartColumn, 0, text.Length);
            var end = Math.Clamp(token.StartColumn + token.Length, start, text.Length);
            if (start > cursor)
            {
                DrawTextRange(canvasSurface, text, cursor, start - cursor, x, baseline, charWidth, font, defaultPaint);
            }

            if (end > start)
            {
                using var tokenPaint = CreateTextPaint(TokenColor(token, palette));
                DrawTextRange(canvasSurface, text, start, end - start, x, baseline, charWidth, IsBoldToken(token) ? boldFont : font, tokenPaint);
            }

            cursor = Math.Max(cursor, end);
        }

        if (cursor < text.Length)
        {
            DrawTextRange(canvasSurface, text, cursor, text.Length - cursor, x, baseline, charWidth, font, defaultPaint);
        }
    }

    private static void DrawTextRange(SKCanvas canvasSurface, string text, int start, int length, float x, float baseline, float charWidth, SKFont font, SKPaint paint)
    {
        if (length <= 0)
        {
            return;
        }

        var visualColumn = GetVisualColumn(text, start);
        var value = text.Substring(start, length).Replace("\t", new string(' ', TabSize), StringComparison.Ordinal);
        canvasSurface.DrawText(value, x + visualColumn * charWidth, baseline, font, paint);
    }

    private static void DrawEmptyState(SKCanvas canvasSurface, int width, int height, SKPaint paint, SKFont font)
    {
        const string text = "Full file content unavailable";
        var textWidth = font.MeasureText(text, paint);
        canvasSurface.DrawText(text, Math.Max(16, (width - textWidth) / 2), Math.Max(32, height / 2), font, paint);
    }

    private void DrawScrollbar(SKCanvas canvasSurface, ViewerPalette palette, int width, int height)
    {
        var contentHeight = GetContentHeight();
        if (contentHeight <= height)
        {
            return;
        }

        var track = GetScrollbarTrack(width, height);
        var thumb = GetScrollbarThumb(track, height, contentHeight);
        using var trackPaint = new SKPaint { Color = palette.ScrollbarTrack, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var thumbPaint = new SKPaint { Color = isDraggingScrollbar ? palette.ScrollbarThumbActive : palette.ScrollbarThumb, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvasSurface.DrawRoundRect(track, ScrollbarWidth / 2, ScrollbarWidth / 2, trackPaint);
        canvasSurface.DrawRoundRect(thumb, ScrollbarWidth / 2, ScrollbarWidth / 2, thumbPaint);
    }

    private bool TryHitTestFold(Point2 point, out CodeFoldRegion region)
    {
        EnsureVisibleRows();
        region = default!;
        var rowIndex = (int)Math.Floor((scrollOffsetY + point.Y - TopPadding) / LineHeight);
        if (rowIndex < 0 || rowIndex >= visibleRows.Length)
        {
            return false;
        }

        var row = visibleRows[rowIndex];
        if (!foldRegionsByStart.TryGetValue(row.LineIndex, out var foundRegion))
        {
            return false;
        }

        region = foundRegion;
        var rowTop = TopPadding + rowIndex * LineHeight - scrollOffsetY;
        return point.X >= LeftPadding &&
            point.X <= LeftPadding + FoldGutterWidth &&
            point.Y >= rowTop &&
            point.Y <= rowTop + LineHeight;
    }

    private bool TryHitTestScrollbar(Point2 point, out SKRect thumb)
    {
        var viewportHeight = GetCanvasSize().Height;
        var contentHeight = GetContentHeight();
        if (contentHeight <= viewportHeight)
        {
            thumb = SKRect.Empty;
            return false;
        }

        var track = GetScrollbarTrack(GetCanvasSize().Width, viewportHeight);
        thumb = GetScrollbarThumb(track, viewportHeight, contentHeight);
        return thumb.Contains((float)point.X, (float)point.Y);
    }

    private void EnsureVisibleRows()
    {
        if (!visibleRowsDirty)
        {
            return;
        }

        var lines = GetLines();
        var regions = GetFoldRegions()
            .Where(region => region.StartLineIndex >= 0 && region.EndLineIndex > region.StartLineIndex && region.StartLineIndex < lines.Count)
            .GroupBy(region => region.StartLineIndex)
            .Select(group => group.OrderByDescending(region => region.EndLineIndex).First())
            .ToArray();
        foldRegionsByStart = regions.ToDictionary(region => region.StartLineIndex);
        collapsedFoldStarts.RemoveWhere(start => !foldRegionsByStart.ContainsKey(start));

        var rows = ImmutableArray.CreateBuilder<VisibleCodeRow>(lines.Count);
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            if (foldRegionsByStart.TryGetValue(lineIndex, out var region) && collapsedFoldStarts.Contains(lineIndex))
            {
                rows.Add(new VisibleCodeRow(lineIndex, region));
                lineIndex = Math.Min(lines.Count - 1, region.EndLineIndex);
            }
            else
            {
                rows.Add(new VisibleCodeRow(lineIndex, null));
            }
        }

        visibleRows = rows.ToImmutable();
        visibleRowsDirty = false;
    }

    private void TrimCollapsedFoldState()
    {
        var starts = GetFoldRegions().Select(region => region.StartLineIndex).ToHashSet();
        collapsedFoldStarts.RemoveWhere(start => !starts.Contains(start));
    }

    private IReadOnlyList<DiffLine> GetLines() => Lines switch
    {
        IReadOnlyList<DiffLine> list => list,
        IEnumerable<DiffLine> enumerable => enumerable.ToArray(),
        _ => Array.Empty<DiffLine>()
    };

    private IReadOnlyList<CodeFoldRegion> GetFoldRegions() => IsDiffMode
        ? Array.Empty<CodeFoldRegion>()
        : FoldRegions switch
        {
            IReadOnlyList<CodeFoldRegion> list => list,
            IEnumerable<CodeFoldRegion> enumerable => enumerable.ToArray(),
            _ => Array.Empty<CodeFoldRegion>()
        };

    private float CalculateGutterWidth(float charWidth, IReadOnlyList<DiffLine> lines)
    {
        var maxLineNumber = lines.Count == 0
            ? 1
            : lines.Max(line => Math.Max(line.OldLineNumber ?? 0, line.NewLineNumber ?? line.Index + 1));
        var digits = Math.Max(3, maxLineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        return IsDiffMode
            ? LeftPadding + digits * charWidth * 2 + 62
            : LeftPadding + FoldGutterWidth + digits * charWidth + 18;
    }

    private double GetContentHeight()
    {
        EnsureVisibleRows();
        return TopPadding + visibleRows.Length * LineHeight + BottomPadding;
    }

    private void ClampScrollOffset()
    {
        var maxOffset = Math.Max(0, GetContentHeight() - GetCanvasSize().Height);
        scrollOffsetY = Math.Clamp(scrollOffsetY, 0, maxOffset);
    }

    private SKRect GetScrollbarTrack(double width, double height) =>
        SKRect.Create((float)(width - ScrollbarWidth - ScrollbarMargin), ScrollbarMargin, ScrollbarWidth, (float)Math.Max(0, height - ScrollbarMargin * 2));

    private SKRect GetScrollbarThumb(SKRect track, double viewportHeight, double contentHeight)
    {
        var thumbHeight = GetScrollbarThumbHeight(track.Height, viewportHeight, contentHeight);
        var scrollable = Math.Max(1, contentHeight - viewportHeight);
        var trackScrollable = Math.Max(1, track.Height - thumbHeight);
        var top = track.Top + (float)(scrollOffsetY / scrollable * trackScrollable);
        return SKRect.Create(track.Left, top, track.Width, thumbHeight);
    }

    private static float GetScrollbarThumbHeight(float trackHeight, double viewportHeight, double contentHeight) =>
        (float)Math.Clamp(viewportHeight / Math.Max(1, contentHeight) * trackHeight, 32, Math.Max(32, trackHeight));

    private Point2 ToCanvasPoint(Point point)
    {
        var canvasSize = GetCanvasSize();
        var scaleX = ActualWidth > 0 ? canvasSize.Width / ActualWidth : 1;
        var scaleY = ActualHeight > 0 ? canvasSize.Height / ActualHeight : 1;
        return new Point2(point.X * scaleX, point.Y * scaleY);
    }

    private Size2 GetCanvasSize()
    {
        if (lastCanvasSize.Width > 0 && lastCanvasSize.Height > 0)
        {
            return lastCanvasSize;
        }

        return new Size2(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
    }

    private void RequestRender() => canvas.Invalidate();

    private void RequestDeferredRender()
    {
        RequestRender();
        _ = DispatcherQueue?.TryEnqueue(RequestRender);
    }

    private static int GetVisualColumn(string text, int column)
    {
        var visualColumn = 0;
        var count = Math.Clamp(column, 0, text.Length);
        for (var index = 0; index < count; index++)
        {
            visualColumn += text[index] == '\t' ? TabSize : 1;
        }

        return visualColumn;
    }

    private static SKPaint CreateTextPaint(SKColor color) => new()
    {
        Color = color,
        IsAntialias = true
    };

    private static bool IsBoldToken(TokenSpan token) =>
        token.TokenType is "keyword" or "class" or "interface" or "enum" or "struct" or "function" or "method" ||
        (!token.Modifiers.IsDefaultOrEmpty &&
            token.Modifiers.Any(modifier => string.Equals(modifier, "declaration", StringComparison.OrdinalIgnoreCase)));

    private static SKColor TokenColor(TokenSpan token, ViewerPalette palette)
    {
        var style = string.IsNullOrWhiteSpace(token.StyleId) || token.StyleId == "text"
            ? token.TokenType
            : token.StyleId;

        return style switch
        {
            "keyword" or "operator" => palette.Keyword,
            "type" or "namespace" or "class" or "interface" or "enum" or "struct" or "typeParameter" => palette.Type,
            "string" or "regexp" => palette.String,
            "comment" => palette.Comment,
            "number" => palette.Number,
            "function" or "method" => palette.Function,
            "property" or "enumMember" or "event" => palette.Property,
            "parameter" => palette.Parameter,
            "variable" => palette.Text,
            "tag" or "decorator" or "macro" => palette.Tag,
            "punctuation" => palette.Punctuation,
            "invalid" => palette.Invalid,
            _ => palette.Text
        };
    }

    private static string MarkerFor(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Deleted => "-",
        DiffLineKind.Modified => "*",
        DiffLineKind.Ignored => "~",
        DiffLineKind.Moved => ">",
        DiffLineKind.Conflict => "!",
        DiffLineKind.Metadata => "@",
        DiffLineKind.Imaginary => "...",
        _ => string.Empty
    };

    private static SKColor LineAccentColor(DiffLineKind kind, ViewerPalette palette) => kind switch
    {
        DiffLineKind.Added => palette.AddedAccent,
        DiffLineKind.Deleted => palette.DeletedAccent,
        DiffLineKind.Modified => palette.ModifiedAccent,
        DiffLineKind.Moved => palette.MovedAccent,
        DiffLineKind.Conflict => palette.ConflictAccent,
        DiffLineKind.Metadata => palette.MetadataAccent,
        DiffLineKind.Imaginary => palette.FoldText,
        _ => palette.LineNumber
    };

    private readonly record struct VisibleCodeRow(int LineIndex, CodeFoldRegion? CollapsedRegion);

    private sealed record ViewerPalette(
        SKColor Background,
        SKColor GutterBackground,
        SKColor Border,
        SKColor Text,
        SKColor MutedText,
        SKColor LineNumber,
        SKColor FoldText,
        SKColor FoldMarker,
        SKColor FoldBackground,
        SKColor FoldHoverBackground,
        SKColor FoldChipBackground,
        SKColor AddedBackground,
        SKColor DeletedBackground,
        SKColor ModifiedBackground,
        SKColor MovedBackground,
        SKColor ConflictBackground,
        SKColor MetadataBackground,
        SKColor AddedAccent,
        SKColor DeletedAccent,
        SKColor ModifiedAccent,
        SKColor MovedAccent,
        SKColor ConflictAccent,
        SKColor MetadataAccent,
        SKColor Accent,
        SKColor Keyword,
        SKColor Type,
        SKColor String,
        SKColor Comment,
        SKColor Number,
        SKColor Function,
        SKColor Property,
        SKColor Parameter,
        SKColor Tag,
        SKColor Punctuation,
        SKColor Invalid,
        SKColor ScrollbarTrack,
        SKColor ScrollbarThumb,
        SKColor ScrollbarThumbActive)
    {
        public static ViewerPalette Create(bool isLight) => isLight
            ? new ViewerPalette(
                new SKColor(247, 249, 252),
                new SKColor(238, 242, 246),
                new SKColor(201, 211, 224),
                new SKColor(20, 32, 51),
                new SKColor(82, 97, 114),
                new SKColor(122, 135, 150),
                new SKColor(22, 107, 154),
                new SKColor(122, 135, 150),
                new SKColor(224, 239, 248),
                new SKColor(210, 232, 244),
                new SKColor(221, 239, 248),
                new SKColor(226, 246, 233),
                new SKColor(255, 235, 235),
                new SKColor(255, 248, 219),
                new SKColor(232, 241, 255),
                new SKColor(255, 226, 226),
                new SKColor(235, 242, 250),
                new SKColor(26, 127, 55),
                new SKColor(203, 36, 49),
                new SKColor(154, 103, 0),
                new SKColor(0, 92, 197),
                new SKColor(203, 36, 49),
                new SKColor(22, 107, 154),
                new SKColor(22, 107, 154),
                new SKColor(0, 92, 197),
                new SKColor(111, 66, 193),
                new SKColor(3, 106, 56),
                new SKColor(106, 115, 125),
                new SKColor(0, 92, 197),
                new SKColor(111, 66, 193),
                new SKColor(149, 56, 0),
                new SKColor(149, 56, 0),
                new SKColor(17, 99, 154),
                new SKColor(82, 97, 114),
                new SKColor(203, 36, 49),
                new SKColor(229, 235, 242),
                new SKColor(150, 164, 181),
                new SKColor(100, 116, 135))
            : new ViewerPalette(
                new SKColor(12, 17, 24),
                new SKColor(15, 21, 29),
                new SKColor(38, 49, 64),
                new SKColor(230, 237, 245),
                new SKColor(153, 166, 182),
                new SKColor(111, 123, 139),
                new SKColor(88, 166, 214),
                new SKColor(111, 123, 139),
                new SKColor(24, 49, 66),
                new SKColor(31, 63, 84),
                new SKColor(24, 49, 66),
                new SKColor(12, 45, 28),
                new SKColor(55, 22, 25),
                new SKColor(51, 43, 18),
                new SKColor(18, 39, 64),
                new SKColor(75, 26, 31),
                new SKColor(21, 32, 46),
                new SKColor(86, 211, 100),
                new SKColor(255, 123, 114),
                new SKColor(234, 179, 8),
                new SKColor(121, 192, 255),
                new SKColor(255, 123, 114),
                new SKColor(88, 166, 214),
                new SKColor(88, 166, 214),
                new SKColor(121, 192, 255),
                new SKColor(210, 168, 255),
                new SKColor(165, 214, 255),
                new SKColor(139, 148, 158),
                new SKColor(121, 192, 255),
                new SKColor(210, 168, 255),
                new SKColor(255, 166, 87),
                new SKColor(255, 166, 87),
                new SKColor(126, 231, 135),
                new SKColor(153, 166, 182),
                new SKColor(255, 123, 114),
                new SKColor(22, 30, 40),
                new SKColor(82, 96, 114),
                new SKColor(122, 140, 162));
    }
}
