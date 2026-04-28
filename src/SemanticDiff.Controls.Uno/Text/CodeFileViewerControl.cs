using System.Collections.Immutable;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SemanticDiff.Core;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using Windows.Foundation;
using Windows.System;

namespace SemanticDiff.Controls.Uno;

public sealed class CodeFileViewerControl : Grid
{
    private const float TopPadding = 8;
    private const float BottomPadding = 8;
    private const float LeftPadding = 8;
    private const float FoldGutterWidth = 22;
    private const float TextPadding = 10;
    private const float ScrollbarWidth = 8;
    private const float ScrollbarMargin = 4;
    private const double DefaultCodeFontSize = 15;
    private const double MinimumCodeFontSize = 10;
    private const double MaximumCodeFontSize = 28;
    private const double DefaultViewportWidth = 960;
    private const double DefaultViewportHeight = 520;

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

    public static readonly DependencyProperty CodeFontSizeProperty = DependencyProperty.Register(
        nameof(CodeFontSize),
        typeof(double),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(DefaultCodeFontSize, OnFontSizeChanged));

    public static readonly DependencyProperty ShowDiffAnnotationsProperty = DependencyProperty.Register(
        nameof(ShowDiffAnnotations),
        typeof(bool),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(true, OnDiffAnnotationVisibilityChanged));

    public static readonly DependencyProperty RefreshKeyProperty = DependencyProperty.Register(
        nameof(RefreshKey),
        typeof(object),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(null, OnContentChanged));

    private static readonly SKTypeface RegularTypeface = SKTypeface.FromFamilyName("Cascadia Mono") ?? SKTypeface.Default;
    private static readonly SKTypeface BoldTypeface = SKTypeface.FromFamilyName("Cascadia Mono", SKFontStyle.Bold) ?? SKTypeface.Default;

    private readonly SKXamlCanvas canvas;
    private readonly HashSet<int> collapsedFoldStarts = [];
    private ImmutableArray<VisibleCodeRow> visibleRows = [];
    private Dictionary<int, CodeFoldRegion> foldRegionsByStart = [];
    private Size2 lastCanvasSize = Size2.Zero;
    private double scrollOffsetY;
    private uint? activePointerId;
    private double scrollbarGrabOffsetY;
    private int? hoveredFoldStartLine;
    private CodeTextPosition? selectionAnchor;
    private CodeTextPosition? selectionActive;
    private bool visibleRowsDirty = true;
    private bool isDraggingScrollbar;
    private bool isSelectingText;

    public CodeFileViewerControl()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        IsTabStop = true;
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
        RightTapped += OnRightTapped;
        Loaded += (_, _) => RequestDeferredRender();
        SizeChanged += (_, _) =>
        {
            ClampScrollOffset();
            RequestRender();
        };
        ActualThemeChanged += (_, _) => RequestRender();
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

    public double CodeFontSize
    {
        get => (double)GetValue(CodeFontSizeProperty);
        set => SetValue(CodeFontSizeProperty, value);
    }

    public bool ShowDiffAnnotations
    {
        get => (bool)GetValue(ShowDiffAnnotationsProperty);
        set => SetValue(ShowDiffAnnotationsProperty, value);
    }

    public object? RefreshKey
    {
        get => GetValue(RefreshKeyProperty);
        set => SetValue(RefreshKeyProperty, value);
    }

    public event EventHandler<CodeFileLineContextRequestedEventArgs>? LineContextRequested;

    private float EffectiveFontSize => (float)Math.Clamp(CodeFontSize, MinimumCodeFontSize, MaximumCodeFontSize);

    private float LineHeight => MathF.Ceiling(EffectiveFontSize + 9);

    private float BaselineOffset => MathF.Round(EffectiveFontSize + (LineHeight - EffectiveFontSize) * 0.5f);

    protected override Size MeasureOverride(Size availableSize)
    {
        var measured = base.MeasureOverride(availableSize);
        return new Size(
            ResolveMeasuredLength(measured.Width, availableSize.Width, DefaultViewportWidth),
            ResolveMeasuredLength(measured.Height, availableSize.Height, DefaultViewportHeight));
    }

    private static double ResolveMeasuredLength(double measured, double available, double fallback)
    {
        if (double.IsFinite(measured) && measured > 0)
        {
            return measured;
        }

        if (double.IsFinite(available) && available > 0)
        {
            return available;
        }

        return fallback;
    }

    private static void OnContentChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CodeFileViewerControl control)
        {
            control.visibleRowsDirty = true;
            control.hoveredFoldStartLine = null;
            control.ClearSelection();
            control.TrimCollapsedFoldState();
            control.ClampScrollOffset();
            control.RequestDeferredRender();
        }
    }

    private static void OnFontSizeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CodeFileViewerControl control)
        {
            control.ClampScrollOffset();
            control.RequestDeferredRender();
        }
    }

    private static void OnDiffAnnotationVisibilityChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CodeFileViewerControl control)
        {
            control.RequestDeferredRender();
        }
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs args)
    {
        var rasterScale = GetRasterScale(args.Info.Width, args.Info.Height);
        lastCanvasSize = new Size2(args.Info.Width / rasterScale, args.Info.Height / rasterScale);
        EnsureVisibleRows();
        ClampScrollOffset();

        var palette = CodeFileViewerPalette.Create(ActualTheme == ElementTheme.Light);
        var canvasSurface = args.Surface.Canvas;
        canvasSurface.Clear(palette.Background);

        var width = (float)lastCanvasSize.Width;
        var height = (float)lastCanvasSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        canvasSurface.Save();
        canvasSurface.Scale((float)rasterScale);

        using var font = new SKFont(RegularTypeface, EffectiveFontSize);
        using var boldFont = new SKFont(BoldTypeface, EffectiveFontSize);
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
            canvasSurface.Restore();
            return;
        }

        var textClip = SKRect.Create(gutterWidth, 0, Math.Max(0, width - gutterWidth - ScrollbarWidth - ScrollbarMargin), height);
        for (var rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
        {
            var row = visibleRows[rowIndex];
            if (row.LineIndex < 0 || row.LineIndex >= lines.Count)
            {
                continue;
            }

            var y = TopPadding + rowIndex * LineHeight - (float)scrollOffsetY;
            var line = lines[row.LineIndex];
            var annotationKind = ShowDiffAnnotations ? line.Kind : DiffLineKind.Context;
            DrawLineBackground(canvasSurface, palette, gutterWidth, width, y, LineHeight, annotationKind, row.CollapsedRegion is not null);
            if (IsDiffMode)
            {
                DrawDiffGutter(canvasSurface, palette, line, ShowDiffAnnotations, gutterWidth, y, LineHeight, BaselineOffset, lineNumberPaint, font);
            }
            else
            {
                DrawLineNumber(canvasSurface, line.Index + 1, gutterWidth, y, BaselineOffset, lineNumberPaint, font);
                DrawFoldMarker(canvasSurface, palette, row.LineIndex, row.CollapsedRegion, y, LineHeight);
            }

            using (new SKAutoCanvasRestore(canvasSurface, doSave: true))
            {
                canvasSurface.ClipRect(textClip);
                DrawSelection(canvasSurface, palette, rowIndex, line, gutterWidth + TextPadding, y, charWidth, width);
                DrawCodeLine(canvasSurface, lines[row.LineIndex], row.CollapsedRegion, gutterWidth + TextPadding, y, BaselineOffset, LineHeight, charWidth, font, boldFont, defaultPaint, foldPaint, palette);
            }
        }

        DrawScrollbar(canvasSurface, palette, width, height);
        canvasSurface.Restore();
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
            return;
        }

        if (TryHitTestText(point, out var position))
        {
            Focus(FocusState.Pointer);
            isSelectingText = true;
            activePointerId = args.Pointer.PointerId;
            selectionAnchor = position;
            selectionActive = position;
            CapturePointer(args.Pointer);
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

        if (isSelectingText && activePointerId == args.Pointer.PointerId)
        {
            if (TryHitTestText(point, out var position))
            {
                selectionActive = position;
                RequestRender();
            }

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
            isSelectingText = false;
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

        if (IsFontZoomModifierDown(args))
        {
            CodeFontSize = Math.Clamp(
                CodeFontSize + Math.Sign(delta),
                MinimumCodeFontSize,
                MaximumCodeFontSize);
            args.Handled = true;
            return;
        }

        scrollOffsetY -= delta / 120.0 * LineHeight * 3;
        ClampScrollOffset();
        RequestRender();
        args.Handled = true;
    }

    private static bool IsFontZoomModifierDown(PointerRoutedEventArgs args) =>
        (args.KeyModifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Windows)) != 0;

    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (hoveredFoldStartLine is not null)
        {
            hoveredFoldStartLine = null;
            RequestRender();
        }
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        var position = args.GetPosition(this);
        var point = ToCanvasPoint(position);
        if (!TryHitTestLine(point, out var line, out var rowIndex, out var column))
        {
            return;
        }

        var lineNumber = line.NewLineNumber ?? line.OldLineNumber ?? line.Index + 1;
        var symbolText = column >= 0 ? CodeTextLayout.GetSymbolTextAtColumn(line, column) : string.Empty;
        LineContextRequested?.Invoke(
            this,
            new CodeFileLineContextRequestedEventArgs(
                line,
                rowIndex,
                lineNumber,
                column,
                symbolText,
                position,
                IsDiffMode));
        args.Handled = true;
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

    private void DrawGutter(SKCanvas canvasSurface, CodeFileViewerPalette palette, float gutterWidth, float height)
    {
        using var gutterPaint = new SKPaint { Color = palette.GutterBackground, Style = SKPaintStyle.Fill };
        using var borderPaint = new SKPaint { Color = palette.Border, StrokeWidth = 1, Style = SKPaintStyle.Stroke };
        canvasSurface.DrawRect(SKRect.Create(0, 0, gutterWidth, height), gutterPaint);
        canvasSurface.DrawLine(gutterWidth - 0.5f, 0, gutterWidth - 0.5f, height, borderPaint);
    }

    private static void DrawLineBackground(SKCanvas canvasSurface, CodeFileViewerPalette palette, float gutterWidth, float width, float y, float lineHeight, DiffLineKind kind, bool isCollapsed)
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
        canvasSurface.DrawRect(SKRect.Create(gutterWidth, y, Math.Max(0, width - gutterWidth - ScrollbarWidth - ScrollbarMargin), lineHeight), paint);
    }

    private void DrawSelection(SKCanvas canvasSurface, CodeFileViewerPalette palette, int rowIndex, DiffLine line, float textX, float y, float charWidth, float width)
    {
        if (!TryGetSelectionRange(out var start, out var end) ||
            rowIndex < start.RowIndex ||
            rowIndex > end.RowIndex)
        {
            return;
        }

        var startColumn = rowIndex == start.RowIndex ? start.Column : 0;
        var endColumn = rowIndex == end.RowIndex ? end.Column : line.Text.Length;
        startColumn = Math.Clamp(startColumn, 0, line.Text.Length);
        endColumn = Math.Clamp(endColumn, 0, line.Text.Length);

        var startVisualColumn = CodeTextLayout.GetVisualColumn(line.Text, startColumn);
        var endVisualColumn = CodeTextLayout.GetVisualColumn(line.Text, endColumn);
        var selectionLeft = textX + startVisualColumn * charWidth;
        var selectionRight = textX + Math.Max(startVisualColumn, endVisualColumn) * charWidth;
        var maxRight = Math.Max(selectionLeft + 1, width - ScrollbarWidth - ScrollbarMargin);

        using var selectionPaint = new SKPaint { Color = palette.SelectionBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        if (selectionRight <= selectionLeft)
        {
            if (!isSelectingText)
            {
                return;
            }

            canvasSurface.DrawRoundRect(SKRect.Create(selectionLeft, y + 3, 2, Math.Max(1, LineHeight - 6)), 1, 1, selectionPaint);
            return;
        }

        canvasSurface.DrawRect(SKRect.Create(selectionLeft, y + 2, Math.Min(selectionRight, maxRight) - selectionLeft, Math.Max(1, LineHeight - 4)), selectionPaint);
    }

    private static void DrawLineNumber(SKCanvas canvasSurface, int lineNumber, float gutterWidth, float y, float baselineOffset, SKPaint paint, SKFont font)
    {
        var text = lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var width = font.MeasureText(text, paint);
        canvasSurface.DrawText(text, gutterWidth - 10 - width, y + baselineOffset, font, paint);
    }

    private static void DrawDiffGutter(SKCanvas canvasSurface, CodeFileViewerPalette palette, DiffLine line, bool showDiffAnnotations, float gutterWidth, float y, float lineHeight, float baselineOffset, SKPaint lineNumberPaint, SKFont font)
    {
        var annotationKind = showDiffAnnotations ? line.Kind : DiffLineKind.Context;
        var markerPaint = CreateTextPaint(CodeTextStyleMap.LineAccentColor(annotationKind, palette));
        try
        {
            var oldText = line.OldLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            var newText = line.NewLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            var marker = showDiffAnnotations ? CodeTextStyleMap.MarkerFor(line.Kind) : string.Empty;
            var oldX = LeftPadding + 34;
            var newX = oldX + 44;
            var markerX = newX + 34;

            DrawRightAlignedText(canvasSurface, oldText, oldX, y + baselineOffset, font, lineNumberPaint);
            DrawRightAlignedText(canvasSurface, newText, newX, y + baselineOffset, font, lineNumberPaint);
            if (!string.IsNullOrEmpty(marker))
            {
                canvasSurface.DrawText(marker, markerX, y + baselineOffset, font, markerPaint);
            }

            using var lanePaint = new SKPaint { Color = CodeTextStyleMap.LineAccentColor(annotationKind, palette), Style = SKPaintStyle.Fill, IsAntialias = true };
            if (showDiffAnnotations && line.Kind is DiffLineKind.Added or DiffLineKind.Deleted or DiffLineKind.Modified or DiffLineKind.Moved or DiffLineKind.Conflict)
            {
                canvasSurface.DrawRoundRect(SKRect.Create(gutterWidth - 4, y + 2, 3, lineHeight - 4), 1.5f, 1.5f, lanePaint);
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

    private void DrawFoldMarker(SKCanvas canvasSurface, CodeFileViewerPalette palette, int lineIndex, CodeFoldRegion? collapsedRegion, float y, float lineHeight)
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
        float baselineOffset,
        float lineHeight,
        float charWidth,
        SKFont font,
        SKFont boldFont,
        SKPaint defaultPaint,
        SKPaint foldPaint,
        CodeFileViewerPalette palette)
    {
        DrawTokenizedText(canvasSurface, line.Text, line.Tokens, x, y + baselineOffset, charWidth, font, boldFont, defaultPaint, palette);
        if (collapsedRegion is null)
        {
            return;
        }

        var visualLength = CodeTextLayout.GetVisualColumn(line.Text, line.Text.Length);
        var placeholder = $"  ... {collapsedRegion.CollapsedLineCount:N0} folded lines ...";
        using var chipPaint = new SKPaint { Color = palette.FoldChipBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        var chipX = x + visualLength * charWidth + 8;
        var chipWidth = Math.Max(140, boldFont.MeasureText(placeholder, foldPaint) + 12);
        var chipRect = SKRect.Create(chipX, y + 2, chipWidth, lineHeight - 4);
        canvasSurface.DrawRoundRect(chipRect, 4, 4, chipPaint);
        canvasSurface.DrawText(placeholder, chipRect.Left + 6, y + baselineOffset, boldFont, foldPaint);
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
        CodeFileViewerPalette palette)
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
                using var tokenPaint = CreateTextPaint(CodeTextStyleMap.TokenColor(token, palette));
                DrawTextRange(canvasSurface, text, start, end - start, x, baseline, charWidth, CodeTextStyleMap.IsBoldToken(token) ? boldFont : font, tokenPaint);
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

        var visualColumn = CodeTextLayout.GetVisualColumn(text, start);
        var value = text.Substring(start, length).Replace("\t", new string(' ', CodeTextLayout.TabSize), StringComparison.Ordinal);
        canvasSurface.DrawText(value, x + visualColumn * charWidth, baseline, font, paint);
    }

    private static void DrawEmptyState(SKCanvas canvasSurface, float width, float height, SKPaint paint, SKFont font)
    {
        const string text = "Full file content unavailable";
        var textWidth = font.MeasureText(text, paint);
        canvasSurface.DrawText(text, Math.Max(16, (width - textWidth) / 2), Math.Max(32, height / 2), font, paint);
    }

    private void DrawScrollbar(SKCanvas canvasSurface, CodeFileViewerPalette palette, float width, float height)
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

    private bool TryHitTestText(Point2 point, out CodeTextPosition position)
    {
        EnsureVisibleRows();
        position = default;
        var lines = GetLines();
        if (visibleRows.Length == 0 || lines.Count == 0)
        {
            return false;
        }

        using var font = new SKFont(RegularTypeface, EffectiveFontSize);
        using var paint = CreateTextPaint(SKColors.Black);
        var charWidth = Math.Max(1, font.MeasureText("M", paint));
        var gutterWidth = CalculateGutterWidth(charWidth, lines);
        var rowIndex = (int)Math.Floor((scrollOffsetY + point.Y - TopPadding) / LineHeight);
        rowIndex = Math.Clamp(rowIndex, 0, visibleRows.Length - 1);

        var row = visibleRows[rowIndex];
        if (row.LineIndex < 0 || row.LineIndex >= lines.Count)
        {
            return false;
        }

        var line = lines[row.LineIndex];
        var column = CodeTextLayout.GetSourceColumnFromVisualOffset(line.Text, (float)(point.X - gutterWidth - TextPadding), charWidth);
        position = new CodeTextPosition(rowIndex, column);
        return true;
    }

    private bool TryHitTestLine(Point2 point, out DiffLine line, out int rowIndex, out int column)
    {
        EnsureVisibleRows();
        line = default!;
        rowIndex = -1;
        column = 0;
        var lines = GetLines();
        if (visibleRows.Length == 0 || lines.Count == 0)
        {
            return false;
        }

        rowIndex = (int)Math.Floor((scrollOffsetY + point.Y - TopPadding) / LineHeight);
        if (rowIndex < 0 || rowIndex >= visibleRows.Length)
        {
            return false;
        }

        var row = visibleRows[rowIndex];
        if (row.LineIndex < 0 || row.LineIndex >= lines.Count)
        {
            return false;
        }

        using var font = new SKFont(RegularTypeface, EffectiveFontSize);
        using var paint = CreateTextPaint(SKColors.Black);
        var charWidth = Math.Max(1, font.MeasureText("M", paint));
        var gutterWidth = CalculateGutterWidth(charWidth, lines);
        line = lines[row.LineIndex];
        var textOffset = (float)(point.X - gutterWidth - TextPadding);
        var lineTextWidth = CodeTextLayout.GetVisualColumn(line.Text, line.Text.Length) * charWidth;
        column = textOffset >= 0 && textOffset <= lineTextWidth
            ? CodeTextLayout.GetSourceColumnFromVisualOffset(line.Text, textOffset, charWidth)
            : -1;
        return true;
    }

    private bool TryGetSelectionRange(out CodeTextPosition start, out CodeTextPosition end)
    {
        start = default;
        end = default;
        if (selectionAnchor is not { } anchor || selectionActive is not { } active)
        {
            return false;
        }

        if (anchor.RowIndex < active.RowIndex ||
            (anchor.RowIndex == active.RowIndex && anchor.Column <= active.Column))
        {
            start = anchor;
            end = active;
        }
        else
        {
            start = active;
            end = anchor;
        }

        return true;
    }

    private void EnsureVisibleRows()
    {
        if (!visibleRowsDirty)
        {
            return;
        }

        var layout = CodeTextLayout.BuildVisibleRows(GetLines(), GetFoldRegions(), collapsedFoldStarts);
        foldRegionsByStart = layout.FoldRegionsByStart;
        collapsedFoldStarts.RemoveWhere(start => !foldRegionsByStart.ContainsKey(start));
        visibleRows = layout.VisibleRows;
        visibleRowsDirty = false;
    }

    private void TrimCollapsedFoldState()
    {
        var starts = GetFoldRegions().Select(region => region.StartLineIndex).ToHashSet();
        collapsedFoldStarts.RemoveWhere(start => !starts.Contains(start));
    }

    private void ClearSelection()
    {
        selectionAnchor = null;
        selectionActive = null;
        isSelectingText = false;
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

    private double GetRasterScale(int pixelWidth, int pixelHeight)
    {
        var scaleX = ActualWidth > 0 ? pixelWidth / ActualWidth : 1;
        var scaleY = ActualHeight > 0 ? pixelHeight / ActualHeight : scaleX;
        var scale = Math.Min(scaleX, scaleY);
        return double.IsFinite(scale) && scale > 0 ? Math.Max(1, scale) : 1;
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

    private static SKPaint CreateTextPaint(SKColor color) => new()
    {
        Color = color,
        IsAntialias = true
    };
}

public sealed class CodeFileLineContextRequestedEventArgs : EventArgs
{
    public CodeFileLineContextRequestedEventArgs(
        DiffLine line,
        int rowIndex,
        int lineNumber,
        int column,
        string symbolText,
        Point position,
        bool isDiffMode)
    {
        Line = line;
        RowIndex = rowIndex;
        LineNumber = lineNumber;
        Column = column;
        SymbolText = symbolText;
        Position = position;
        IsDiffMode = isDiffMode;
    }

    public DiffLine Line { get; }

    public int RowIndex { get; }

    public int LineNumber { get; }

    public int Column { get; }

    public string SymbolText { get; }

    public Point Position { get; }

    public bool IsDiffMode { get; }
}
