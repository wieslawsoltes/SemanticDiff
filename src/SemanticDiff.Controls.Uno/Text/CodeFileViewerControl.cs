using System.Collections.Immutable;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pretext.Uno.Controls;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Rendering;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace SemanticDiff.Controls.Uno;

public sealed class CodeFileViewerControl : Grid
{
    private const float TopPadding = 8;
    private const float BottomPadding = 8;
    private const float LeftPadding = 8;
    private const float FoldGutterWidth = 30;
    private const float FoldMarkerSize = 13;
    private const int FoldGuideMaxDepth = 4;
    private const float TextPadding = 10;
    private const float ScrollbarWidth = 8;
    private const float ScrollbarMargin = 4;
    private const float HorizontalScrollbarHeight = 8;
    private const float HorizontalScrollbarMinThumbWidth = 36;
    private const int HorizontalWheelScrollColumns = 12;
    private const string TabReplacement = "    ";
    private const float MinimapWidth = 112;
    private const float MinimapMargin = 8;
    private const float MinimapPadding = 4;
    private const float MinimapChangeLaneWidth = 4;
    private const float MinimapViewportMinHeight = 28;
    private const float MinimapMinimumHostWidth = 560;
    private const float MinimapMinimumHostHeight = 180;
    private const int MinimapMinimumRows = 24;
    private const int MinimapDetailedRowLimit = 1200;
    private const int MinimapMaxTokenSegmentsPerRow = 18;
    private const int MinimapMaxVisualColumns = 220;
    private const double DefaultCodeFontSize = 15;
    private const double MinimumCodeFontSize = 10;
    private const double MaximumCodeFontSize = 28;
    private const double DefaultViewportWidth = 960;
    private const double DefaultViewportHeight = 520;
    private const double SelectionAutoScrollMargin = 34;
    private const double SelectionAutoScrollMinimumStep = 2;

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

    public static readonly DependencyProperty ShowSemanticInsightsProperty = DependencyProperty.Register(
        nameof(ShowSemanticInsights),
        typeof(bool),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(true, OnDiffAnnotationVisibilityChanged));

    public static readonly DependencyProperty SemanticLineInsightsProperty = DependencyProperty.Register(
        nameof(SemanticLineInsights),
        typeof(object),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(null, OnSemanticLineInsightsChanged));

    public static readonly DependencyProperty RefreshKeyProperty = DependencyProperty.Register(
        nameof(RefreshKey),
        typeof(object),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty IsEditableProperty = DependencyProperty.Register(
        nameof(IsEditable),
        typeof(bool),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(false, OnEditableStateChanged));

    public static readonly DependencyProperty IsTokenizationEnabledProperty = DependencyProperty.Register(
        nameof(IsTokenizationEnabled),
        typeof(bool),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(true, OnTokenizationConfigurationChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(null, OnEditableTextChanged));

    public static readonly DependencyProperty LineCommentPrefixProperty = DependencyProperty.Register(
        nameof(LineCommentPrefix),
        typeof(string),
        typeof(CodeFileViewerControl),
        new PropertyMetadata("// "));

    public static readonly DependencyProperty LineCommentSuffixProperty = DependencyProperty.Register(
        nameof(LineCommentSuffix),
        typeof(string),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsCompletionEnabledProperty = DependencyProperty.Register(
        nameof(IsCompletionEnabled),
        typeof(bool),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(true, OnCompletionConfigurationChanged));

    public static readonly DependencyProperty CompletionProviderProperty = DependencyProperty.Register(
        nameof(CompletionProvider),
        typeof(ICodeCompletionProvider),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(null, OnCompletionConfigurationChanged));

    public static readonly DependencyProperty CompletionLanguageProperty = DependencyProperty.Register(
        nameof(CompletionLanguage),
        typeof(string),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(string.Empty, OnCompletionConfigurationChanged));

    public static readonly DependencyProperty CompletionPathProperty = DependencyProperty.Register(
        nameof(CompletionPath),
        typeof(string),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(string.Empty, OnCompletionConfigurationChanged));

    public static readonly DependencyProperty CompletionRepositoryPathProperty = DependencyProperty.Register(
        nameof(CompletionRepositoryPath),
        typeof(string),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(string.Empty, OnCompletionConfigurationChanged));

    public static readonly DependencyProperty CompletionTriggerLengthProperty = DependencyProperty.Register(
        nameof(CompletionTriggerLength),
        typeof(int),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(1));

    public static readonly DependencyProperty CompletionMaxItemCountProperty = DependencyProperty.Register(
        nameof(CompletionMaxItemCount),
        typeof(int),
        typeof(CodeFileViewerControl),
        new PropertyMetadata(100));

    private static readonly SKTypeface RegularTypeface = SKTypeface.FromFamilyName("Cascadia Mono") ?? SKTypeface.Default;
    private static readonly SKTypeface BoldTypeface = SKTypeface.FromFamilyName("Cascadia Mono", SKFontStyle.Bold) ?? SKTypeface.Default;
    private static readonly TextMetricsCache TextMetrics = TextMetricsCache.Shared;

    private readonly SKXamlCanvas canvas;
    private readonly Border completionPresenter;
    private readonly ListView completionList;
    private readonly TextBlock completionStatusText;
    private readonly CodeTextEditorDocument editorDocument = new();
    private readonly AdaptiveDocumentTokenizer editableTokenizer = new();
    private readonly PlainTextDocumentTokenizer editableFallbackTokenizer = new();
    private readonly HashSet<int> collapsedFoldStarts = [];
    private ImmutableArray<VisibleCodeRow> visibleRows = [];
    private ImmutableArray<DiffLine> editableLines = [];
    private Dictionary<int, CodeFoldRegion> foldRegionsByStart = [];
    private Size2 lastCanvasSize = Size2.Zero;
    private double scrollOffsetX;
    private double scrollOffsetY;
    private uint? activePointerId;
    private double horizontalScrollbarGrabOffsetX;
    private double scrollbarGrabOffsetY;
    private int? hoveredFoldStartLine;
    private int? hoveredSemanticLineNumber;
    private CodeTextPosition caretPosition;
    private CodeTextPosition? selectionAnchor;
    private CodeTextPosition? selectionActive;
    private bool visibleRowsDirty = true;
    private bool editorInitialized;
    private bool isUpdatingEditableText;
    private bool isDraggingScrollbar;
    private bool isDraggingHorizontalScrollbar;
    private bool isDraggingMinimap;
    private bool isSelectingText;
    private int editableLinesVersion = -1;
    private IReadOnlyDictionary<int, SemanticLineInsight> semanticLineInsightsByLine = new Dictionary<int, SemanticLineInsight>();
    private double minimapGrabOffsetY;
    private Point2 selectionAutoScrollPoint;
    private DispatcherTimer? selectionAutoScrollTimer;
    private UiRenderScheduler? deferredRenderScheduler;
    private ICodeCompletionProvider? fallbackCompletionProvider;
    private CodeCompletionSession? completionSession;
    private int completionRequestVersion;
    private bool isCommittingCompletion;

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
        (completionPresenter, completionList, completionStatusText) = CreateCompletionPresenter();
        completionList.ItemClick += OnCompletionItemClick;
        completionPresenter.PointerPressed += (_, args) => args.Handled = true;
        completionPresenter.PointerReleased += (_, args) => args.Handled = true;
        completionPresenter.PointerWheelChanged += (_, args) => args.Handled = true;
        Children.Add(completionPresenter);

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerReleased;
        PointerCaptureLost += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        PointerExited += OnPointerExited;
        RightTapped += OnRightTapped;
        KeyDown += OnKeyDown;
        CharacterReceived += OnCharacterReceived;
        GotFocus += (_, _) => RequestRender();
        LostFocus += (_, _) => RequestRender();
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

    public bool ShowSemanticInsights
    {
        get => (bool)GetValue(ShowSemanticInsightsProperty);
        set => SetValue(ShowSemanticInsightsProperty, value);
    }

    public object? SemanticLineInsights
    {
        get => GetValue(SemanticLineInsightsProperty);
        set => SetValue(SemanticLineInsightsProperty, value);
    }

    public object? RefreshKey
    {
        get => GetValue(RefreshKeyProperty);
        set => SetValue(RefreshKeyProperty, value);
    }

    public bool IsEditable
    {
        get => (bool)GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    public bool IsTokenizationEnabled
    {
        get => (bool)GetValue(IsTokenizationEnabledProperty);
        set => SetValue(IsTokenizationEnabledProperty, value);
    }

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string LineCommentPrefix
    {
        get => (string)GetValue(LineCommentPrefixProperty);
        set => SetValue(LineCommentPrefixProperty, value);
    }

    public string LineCommentSuffix
    {
        get => (string)GetValue(LineCommentSuffixProperty);
        set => SetValue(LineCommentSuffixProperty, value);
    }

    public bool IsCompletionEnabled
    {
        get => (bool)GetValue(IsCompletionEnabledProperty);
        set => SetValue(IsCompletionEnabledProperty, value);
    }

    public ICodeCompletionProvider? CompletionProvider
    {
        get => (ICodeCompletionProvider?)GetValue(CompletionProviderProperty);
        set => SetValue(CompletionProviderProperty, value);
    }

    public string CompletionLanguage
    {
        get => (string)GetValue(CompletionLanguageProperty);
        set => SetValue(CompletionLanguageProperty, value);
    }

    public string CompletionPath
    {
        get => (string)GetValue(CompletionPathProperty);
        set => SetValue(CompletionPathProperty, value);
    }

    public string CompletionRepositoryPath
    {
        get => (string)GetValue(CompletionRepositoryPathProperty);
        set => SetValue(CompletionRepositoryPathProperty, value);
    }

    public int CompletionTriggerLength
    {
        get => (int)GetValue(CompletionTriggerLengthProperty);
        set => SetValue(CompletionTriggerLengthProperty, value);
    }

    public int CompletionMaxItemCount
    {
        get => (int)GetValue(CompletionMaxItemCountProperty);
        set => SetValue(CompletionMaxItemCountProperty, value);
    }

    public event EventHandler<CodeFileLineContextRequestedEventArgs>? LineContextRequested;

    public event EventHandler<CodeFileTextEditedEventArgs>? TextEdited;

    public void CopySelection() => CopySelectionToClipboard();

    public void CutSelection() => CutSelectionToClipboard();

    public void PasteFromClipboard() => PasteTextFromClipboard();

    public void SelectAll() => SelectAllText();

    public void UndoEdit() => UndoTextEdit();

    public void RedoEdit() => RedoTextEdit();

    public void ToggleLineComment() => ToggleSelectedLineComment();

    public void MoveSelectionUp() => MoveSelectedLines(-1);

    public void MoveSelectionDown() => MoveSelectedLines(1);

    public void CopySelectionUp() => CopySelectedLines(-1);

    public void CopySelectionDown() => CopySelectedLines(1);

    private float EffectiveFontSize => (float)Math.Clamp(CodeFontSize, MinimumCodeFontSize, MaximumCodeFontSize);

    private float LineHeight => MathF.Ceiling(EffectiveFontSize + 9);

    private float BaselineOffset => MathF.Round(EffectiveFontSize + (LineHeight - EffectiveFontSize) * 0.5f);

    private TextFontDescriptor RegularFontDescriptor => new("Cascadia Mono", EffectiveFontSize);

    private TextFontDescriptor BoldFontDescriptor => new("Cascadia Mono", EffectiveFontSize, Bold: true);

    private float CodeCharacterWidth => TextMetrics.MeasureMonospaceAdvance(RegularFontDescriptor);

    private bool CanEditText => IsEditable && !IsDiffMode;

    private bool ShouldUseTextDocument => !IsDiffMode && Text is not null;

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

    private static (Border Presenter, ListView List, TextBlock Status) CreateCompletionPresenter()
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            MaxHeight = 220,
            MinWidth = 360,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var status = new TextBlock
        {
            Margin = new Thickness(10, 6, 10, 8),
            FontSize = 11,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 100, 116, 139)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(list, 0);
        Grid.SetRow(status, 1);
        panel.Children.Add(list);
        panel.Children.Add(status);

        var presenter = new Border
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Width = 420,
            MaxHeight = 280,
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(248, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 148, 163, 184)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = panel
        };
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(presenter, 50);
        return (presenter, list, status);
    }

    private static void OnContentChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CodeFileViewerControl control)
        {
            control.CloseCompletion();
            control.visibleRowsDirty = true;
            control.hoveredFoldStartLine = null;
            control.hoveredSemanticLineNumber = null;
            control.isDraggingScrollbar = false;
            control.isDraggingHorizontalScrollbar = false;
            control.isDraggingMinimap = false;
            control.activePointerId = null;
            control.StopSelectionAutoScroll();
            control.editorInitialized = false;
            control.editableLinesVersion = -1;
            control.ClearSelection();
            control.TrimCollapsedFoldState();
            control.ClampScrollOffset();
            control.ReleasePointerCaptures();
            ToolTipService.SetToolTip(control, null);
            control.RequestDeferredRender();
        }
    }

    private static void OnFontSizeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CodeFileViewerControl control)
        {
            control.ClampScrollOffset();
            control.UpdateCompletionPresenterPosition();
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

    private static void OnSemanticLineInsightsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CodeFileViewerControl control)
        {
            control.semanticLineInsightsByLine = BuildSemanticInsightMap(args.NewValue);
            control.hoveredSemanticLineNumber = null;
            ToolTipService.SetToolTip(control, null);
            control.RequestDeferredRender();
        }
    }

    private static void OnEditableStateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CodeFileViewerControl control)
        {
            control.CloseCompletion();
            control.editorInitialized = false;
            control.editableLinesVersion = -1;
            control.visibleRowsDirty = true;
            control.ClearSelection();
            control.isDraggingHorizontalScrollbar = false;
            control.ClampScrollOffset();
            control.RequestDeferredRender();
        }
    }

    private static void OnEditableTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CodeFileViewerControl control && !control.isUpdatingEditableText)
        {
            control.CloseCompletion();
            control.editorInitialized = false;
            control.editableLinesVersion = -1;
            control.visibleRowsDirty = true;
            control.isDraggingHorizontalScrollbar = false;
            control.ClampScrollOffset();
            control.RequestDeferredRender();
        }
    }

    private static void OnTokenizationConfigurationChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CodeFileViewerControl control)
        {
            control.editableLinesVersion = -1;
            control.editableLines = [];
            control.RequestDeferredRender();
        }
    }

    private static void OnCompletionConfigurationChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CodeFileViewerControl control)
        {
            control.CloseCompletion();
        }
    }

    private static IReadOnlyDictionary<int, SemanticLineInsight> BuildSemanticInsightMap(object? source)
    {
        var insights = source switch
        {
            ImmutableArray<SemanticLineInsight> immutable when !immutable.IsDefaultOrEmpty => immutable,
            IReadOnlyList<SemanticLineInsight> list => list.ToImmutableArray(),
            IEnumerable<SemanticLineInsight> enumerable => enumerable.ToImmutableArray(),
            _ => ImmutableArray<SemanticLineInsight>.Empty
        };

        if (insights.IsDefaultOrEmpty)
        {
            return new Dictionary<int, SemanticLineInsight>();
        }

        return insights
            .GroupBy(insight => insight.LineNumber)
            .ToDictionary(group => group.Key, group => MergeSemanticInsights(group.ToImmutableArray()));
    }

    private static SemanticLineInsight MergeSemanticInsights(ImmutableArray<SemanticLineInsight> insights)
    {
        if (insights.Length == 1)
        {
            return insights[0];
        }

        var dominant = insights
            .OrderByDescending(insight => insight.IsChanged)
            .ThenByDescending(insight => insight.IsImpacted)
            .ThenByDescending(insight => insight.LinkCount)
            .ThenBy(insight => insight.KindText, StringComparer.OrdinalIgnoreCase)
            .First();
        return new SemanticLineInsight(
            dominant.LineNumber,
            $"{insights.Length:N0} sem",
            string.Join(" | ", insights.Select(insight => insight.Detail).Distinct(StringComparer.Ordinal).Take(3)),
            dominant.Kind,
            insights.Sum(insight => insight.AnchorCount),
            insights.Sum(insight => insight.LinkCount),
            insights.Any(insight => insight.IsChanged),
            insights.Any(insight => insight.IsImpacted));
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs args)
    {
        var rasterScale = GetRasterScale(args.Info.Width, args.Info.Height);
        lastCanvasSize = new Size2(args.Info.Width / rasterScale, args.Info.Height / rasterScale);
        EnsureVisibleRows();

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
        using var tokenPaints = new TokenPaintCache();
        var charWidth = CodeCharacterWidth;
        var lines = GetLines();
        var gutterWidth = CalculateGutterWidth(charWidth, lines);
        var contentRight = GetContentRight(width, height);
        var hasHorizontalScrollbar = ShouldShowHorizontalScrollbar(width, height, gutterWidth, lines);
        var viewportHeight = GetVerticalViewportHeight(height, hasHorizontalScrollbar);
        ClampScrollOffset();
        DrawGutter(canvasSurface, palette, gutterWidth, height, IsDiffMode);

        var firstRow = Math.Max(0, (int)Math.Floor((scrollOffsetY - TopPadding) / LineHeight));
        var lastRow = Math.Min(visibleRows.Length - 1, (int)Math.Ceiling((scrollOffsetY + viewportHeight) / LineHeight));
        if (visibleRows.Length == 0 || firstRow > lastRow)
        {
            DrawEmptyState(canvasSurface, width, height, mutedPaint, font, RegularFontDescriptor);
            canvasSurface.Restore();
            return;
        }

        var textClip = SKRect.Create(gutterWidth, 0, Math.Max(0, contentRight - gutterWidth), (float)viewportHeight);
        var fixedTextLeft = gutterWidth + TextPadding;
        var scrolledTextLeft = fixedTextLeft - (float)scrollOffsetX;
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
            DrawLineBackground(canvasSurface, palette, gutterWidth, contentRight, y, LineHeight, annotationKind, row.CollapsedRegion is not null);
            if (IsDiffMode)
            {
                DrawDiffGutter(canvasSurface, palette, line, ShowDiffAnnotations, gutterWidth, y, LineHeight, BaselineOffset, lineNumberPaint, font, RegularFontDescriptor);
            }
            else
            {
                DrawFoldGutterGuide(canvasSurface, palette, row, y, LineHeight);
                DrawLineNumber(canvasSurface, line.Index + 1, gutterWidth, y, BaselineOffset, lineNumberPaint, font, RegularFontDescriptor);
                DrawFoldMarker(canvasSurface, palette, row, y, LineHeight);
            }

            using (new SKAutoCanvasRestore(canvasSurface, doSave: true))
            {
                canvasSurface.ClipRect(textClip);
                if (!IsDiffMode)
                {
                    DrawFoldGuide(canvasSurface, palette, lines, row, scrolledTextLeft, contentRight, y, LineHeight, charWidth);
                }

                DrawSelection(canvasSurface, palette, row, line, scrolledTextLeft, y, charWidth, contentRight);
                DrawCodeLine(canvasSurface, lines[row.LineIndex], row.CollapsedRegion, scrolledTextLeft, contentRight, y, BaselineOffset, LineHeight, charWidth, font, boldFont, defaultPaint, foldPaint, tokenPaints, BoldFontDescriptor, palette);
                DrawCaret(canvasSurface, palette, row, line, scrolledTextLeft, y, charWidth, contentRight);
            }

            if (TryGetSemanticInsight(line, out var semanticInsight))
            {
                DrawSemanticInsight(canvasSurface, palette, semanticInsight, gutterWidth, contentRight, y, LineHeight, BaselineOffset, font, BoldFontDescriptor);
            }
        }

        if (ShouldShowMinimap(width, height))
        {
            DrawMinimap(canvasSurface, palette, lines, width, height, viewportHeight);
        }
        else
        {
            DrawScrollbar(canvasSurface, palette, width, viewportHeight);
        }

        if (hasHorizontalScrollbar)
        {
            DrawHorizontalScrollbar(canvasSurface, palette, width, height, gutterWidth, contentRight, lines);
        }

        canvasSurface.Restore();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var pointerPoint = args.GetCurrentPoint(this);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            return;
        }

        CloseCompletion();
        StopSelectionAutoScroll();
        var point = ToCanvasPoint(pointerPoint.Position);
        if (TryHitTestMinimap(point, out var minimapBounds, out var minimapViewport))
        {
            isDraggingMinimap = true;
            activePointerId = args.Pointer.PointerId;
            minimapGrabOffsetY = minimapViewport.Contains((float)point.X, (float)point.Y)
                ? point.Y - minimapViewport.Top
                : minimapViewport.Height * 0.5;
            if (!minimapViewport.Contains((float)point.X, (float)point.Y))
            {
                DragMinimap(point.Y, minimapBounds);
            }

            CapturePointer(args.Pointer);
            args.Handled = true;
            return;
        }

        if (TryHitTestScrollbar(point, out var thumb))
        {
            isDraggingScrollbar = true;
            activePointerId = args.Pointer.PointerId;
            scrollbarGrabOffsetY = point.Y - thumb.Top;
            CapturePointer(args.Pointer);
            args.Handled = true;
            return;
        }

        if (TryHitTestHorizontalScrollbar(point, out var horizontalThumb, out var hitHorizontalThumb))
        {
            isDraggingHorizontalScrollbar = true;
            activePointerId = args.Pointer.PointerId;
            horizontalScrollbarGrabOffsetX = hitHorizontalThumb
                ? point.X - horizontalThumb.Left
                : horizontalThumb.Width * 0.5;
            if (!hitHorizontalThumb)
            {
                DragHorizontalScrollbar(point.X);
            }

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
            ToolTipService.SetToolTip(this, CreateFoldTooltip(region));
            RequestRender();
            args.Handled = true;
            return;
        }

        if (TryHitTestText(point, out var position))
        {
            Focus(FocusState.Pointer);
            isSelectingText = true;
            activePointerId = args.Pointer.PointerId;
            caretPosition = position;
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

        if (isDraggingHorizontalScrollbar && activePointerId == args.Pointer.PointerId)
        {
            DragHorizontalScrollbar(point.X);
            args.Handled = true;
            return;
        }

        if (isDraggingMinimap && activePointerId == args.Pointer.PointerId)
        {
            DragMinimap(point.Y);
            args.Handled = true;
            return;
        }

        if (isSelectingText && activePointerId == args.Pointer.PointerId)
        {
            UpdateSelectionDrag(point);
            args.Handled = true;
            return;
        }

        var hasFoldHover = TryHitTestFold(point, out var region);
        var nextHoveredFold = hasFoldHover ? region.StartLineIndex : (int?)null;
        var nextHoveredSemanticLine = TryHitTestSemanticMarker(point, out var semanticInsight) && semanticInsight is not null
            ? semanticInsight.LineNumber
            : (int?)null;
        var hoverChanged = false;
        if (hoveredFoldStartLine != nextHoveredFold)
        {
            hoveredFoldStartLine = nextHoveredFold;
            hoverChanged = true;
            RequestRender();
        }

        if (hoveredSemanticLineNumber != nextHoveredSemanticLine)
        {
            hoveredSemanticLineNumber = nextHoveredSemanticLine;
            hoverChanged = true;
            RequestRender();
        }

        if (hoverChanged)
        {
            ToolTipService.SetToolTip(this, semanticInsight is not null
                ? semanticInsight.Detail
                : hasFoldHover ? CreateFoldTooltip(region) : null);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (activePointerId == args.Pointer.PointerId)
        {
            isDraggingScrollbar = false;
            isDraggingHorizontalScrollbar = false;
            isDraggingMinimap = false;
            isSelectingText = false;
            activePointerId = null;
            StopSelectionAutoScroll();
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
            UpdateCompletionPresenterPosition();
            args.Handled = true;
            return;
        }

        CloseCompletion();
        if (IsHorizontalScrollModifierDown(args) && GetMaxHorizontalScrollOffset() > 0)
        {
            scrollOffsetX -= delta / 120.0 * CodeCharacterWidth * HorizontalWheelScrollColumns;
        }
        else
        {
            scrollOffsetY -= delta / 120.0 * LineHeight * 3;
        }

        ClampScrollOffset();
        RequestRender();
        args.Handled = true;
    }

    private void UpdateSelectionDrag(Point2 point)
    {
        selectionAutoScrollPoint = point;
        var scrolled = TryAutoScrollSelection(point);
        if (TryGetSelectionDragTextPosition(point, out var position))
        {
            caretPosition = position;
            selectionActive = position;
            RequestRender();
        }
        else if (scrolled)
        {
            RequestRender();
        }

        UpdateSelectionAutoScroll(point);
    }

    private void UpdateSelectionAutoScroll(Point2 point)
    {
        if (!isSelectingText ||
            activePointerId is null ||
            (GetSelectionAutoScrollStepX(point) == 0 && GetSelectionAutoScrollStepY(point) == 0))
        {
            StopSelectionAutoScroll();
            return;
        }

        selectionAutoScrollTimer ??= CreateSelectionAutoScrollTimer();
        if (!selectionAutoScrollTimer.IsEnabled)
        {
            selectionAutoScrollTimer.Start();
        }
    }

    private DispatcherTimer CreateSelectionAutoScrollTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        timer.Tick += OnSelectionAutoScrollTick;
        return timer;
    }

    private void OnSelectionAutoScrollTick(object? sender, object e)
    {
        if (!isSelectingText || activePointerId is null)
        {
            StopSelectionAutoScroll();
            return;
        }

        if (TryAutoScrollSelection(selectionAutoScrollPoint))
        {
            if (TryGetSelectionDragTextPosition(selectionAutoScrollPoint, out var position))
            {
                caretPosition = position;
                selectionActive = position;
            }

            RequestRender();
            return;
        }

        StopSelectionAutoScroll();
    }

    private void StopSelectionAutoScroll()
    {
        selectionAutoScrollTimer?.Stop();
    }

    private bool TryAutoScrollSelection(Point2 point)
    {
        var stepX = GetSelectionAutoScrollStepX(point);
        var stepY = GetSelectionAutoScrollStepY(point);
        if (stepX == 0 && stepY == 0)
        {
            return false;
        }

        var beforeX = scrollOffsetX;
        var beforeY = scrollOffsetY;
        scrollOffsetX += stepX;
        scrollOffsetY += stepY;
        ClampScrollOffset();
        return Math.Abs(scrollOffsetX - beforeX) > 0.01 ||
            Math.Abs(scrollOffsetY - beforeY) > 0.01;
    }

    private double GetSelectionAutoScrollStepY(Point2 point)
    {
        var size = GetCanvasSize();
        if (size.Height <= 0 || GetContentHeight() <= GetVerticalViewportHeight())
        {
            return 0;
        }

        var viewportTop = TopPadding;
        var viewportBottom = Math.Max(viewportTop, GetVerticalViewportHeight() - BottomPadding);
        var maxStep = Math.Max(SelectionAutoScrollMinimumStep, LineHeight * 0.65);
        if (point.Y < viewportTop + SelectionAutoScrollMargin)
        {
            var distance = viewportTop + SelectionAutoScrollMargin - point.Y;
            return -Math.Clamp(distance / SelectionAutoScrollMargin * maxStep, SelectionAutoScrollMinimumStep, maxStep);
        }

        if (point.Y > viewportBottom - SelectionAutoScrollMargin)
        {
            var distance = point.Y - (viewportBottom - SelectionAutoScrollMargin);
            return Math.Clamp(distance / SelectionAutoScrollMargin * maxStep, SelectionAutoScrollMinimumStep, maxStep);
        }

        return 0;
    }

    private double GetSelectionAutoScrollStepX(Point2 point)
    {
        var maxOffset = GetMaxHorizontalScrollOffset();
        if (maxOffset <= 0)
        {
            return 0;
        }

        var lines = GetLines();
        var charWidth = CodeCharacterWidth;
        var gutterWidth = CalculateGutterWidth(charWidth, lines);
        var size = GetCanvasSize();
        var contentRight = GetContentRight((float)size.Width, (float)size.Height);
        var viewportLeft = gutterWidth + TextPadding;
        var viewportRight = Math.Max(viewportLeft, contentRight);
        var maxStep = Math.Max(SelectionAutoScrollMinimumStep, charWidth * 1.5);
        if (point.X < viewportLeft + SelectionAutoScrollMargin)
        {
            var distance = viewportLeft + SelectionAutoScrollMargin - point.X;
            return -Math.Clamp(distance / SelectionAutoScrollMargin * maxStep, SelectionAutoScrollMinimumStep, maxStep);
        }

        if (point.X > viewportRight - SelectionAutoScrollMargin)
        {
            var distance = point.X - (viewportRight - SelectionAutoScrollMargin);
            return Math.Clamp(distance / SelectionAutoScrollMargin * maxStep, SelectionAutoScrollMinimumStep, maxStep);
        }

        return 0;
    }

    private static bool IsFontZoomModifierDown(PointerRoutedEventArgs args) =>
        (args.KeyModifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Windows)) != 0;

    private static bool IsHorizontalScrollModifierDown(PointerRoutedEventArgs args) =>
        (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0;

    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (hoveredFoldStartLine is not null || hoveredSemanticLineNumber is not null)
        {
            hoveredFoldStartLine = null;
            hoveredSemanticLineNumber = null;
            ToolTipService.SetToolTip(this, null);
            RequestRender();
        }
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        var position = args.GetPosition(this);
        var point = ToCanvasPoint(position);
        if (IsPointInRightOverlay(point))
        {
            return;
        }

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

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var isCommand = IsCommandModifierDown();
        var isShift = IsKeyDown(VirtualKey.Shift) || IsKeyDown(VirtualKey.LeftShift) || IsKeyDown(VirtualKey.RightShift);
        var isMenu = IsMenuModifierDown();

        if (CanEditText && HandleCompletionKey(args.Key))
        {
            args.Handled = true;
            return;
        }

        if (isCommand && HandleCommandKey(args.Key, isShift))
        {
            args.Handled = true;
            return;
        }

        if (!CanEditText)
        {
            return;
        }

        if (isMenu && HandleMenuKey(args.Key, isShift))
        {
            args.Handled = true;
            return;
        }

        if (HandleNavigationKey(args.Key, isShift, isCommand))
        {
            CloseCompletion();
            args.Handled = true;
            return;
        }

        if (!isCommand && !isMenu && TryGetPrintableKeyText(args.Key, isShift, out var text))
        {
            InsertTextInput(text);
            args.Handled = true;
            return;
        }

        switch (args.Key)
        {
            case VirtualKey.Back:
                if (isCommand)
                {
                    BackspaceWordText();
                }
                else
                {
                    BackspaceText();
                }

                RefreshCompletionAfterEdit();
                args.Handled = true;
                break;
            case VirtualKey.Delete:
                if (isCommand)
                {
                    DeleteWordText();
                }
                else
                {
                    DeleteText();
                }

                RefreshCompletionAfterEdit();
                args.Handled = true;
                break;
            case VirtualKey.Enter:
                CloseCompletion();
                ReplaceSelectionWithText("\n");
                args.Handled = true;
                break;
            case VirtualKey.Tab:
                CloseCompletion();
                if (isShift)
                {
                    OutdentSelectedLines();
                }
                else
                {
                    IndentSelectionOrInsertTab();
                }

                args.Handled = true;
                break;
        }
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (args.Handled)
        {
            return;
        }

        if (CanEditText && IsCommandModifierDown() && args.Character == '/')
        {
            ToggleSelectedLineComment();
            args.Handled = true;
            return;
        }

        if (!CanEditText || IsCommandModifierDown())
        {
            return;
        }

        var character = args.Character;
        if (character is '\0' or '\b' or '\t' or '\r' or '\n' || char.IsControl(character))
        {
            return;
        }

        InsertTextInput(character.ToString());
        args.Handled = true;
    }

    private void InsertTextInput(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (text.Length == 1)
        {
            var character = text[0];
            if (TryHandlePairCharacter(character))
            {
                RefreshCompletionAfterCharacter(character);
                return;
            }

            ReplaceSelectionWithText(text);
            RefreshCompletionAfterCharacter(character);
            return;
        }

        ReplaceSelectionWithText(text);
        RefreshCompletionAfterEdit();
    }

    private static bool TryGetPrintableKeyText(VirtualKey key, bool isShift, out string text)
    {
        text = string.Empty;

        if (key >= VirtualKey.A && key <= VirtualKey.Z)
        {
            var offset = (int)key - (int)VirtualKey.A;
            text = ((char)((isShift ? 'A' : 'a') + offset)).ToString();
            return true;
        }

        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
        {
            var digit = (int)key - (int)VirtualKey.Number0;
            const string shiftedDigits = ")!@#$%^&*(";
            text = isShift ? shiftedDigits[digit].ToString() : ((char)('0' + digit)).ToString();
            return true;
        }

        if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9)
        {
            var digit = (int)key - (int)VirtualKey.NumberPad0;
            text = ((char)('0' + digit)).ToString();
            return true;
        }

        text = key switch
        {
            VirtualKey.Space => " ",
            VirtualKey.Decimal => ".",
            VirtualKey.Add => "+",
            VirtualKey.Subtract => "-",
            VirtualKey.Multiply => "*",
            VirtualKey.Divide => "/",
            _ => string.Empty
        };

        if (text.Length > 0)
        {
            return true;
        }

        text = TryGetOemPrintableKeyText((int)key, isShift);
        return text.Length > 0;
    }

    private static string TryGetOemPrintableKeyText(int keyCode, bool isShift) =>
        keyCode switch
        {
            186 => isShift ? ":" : ";",
            187 => isShift ? "+" : "=",
            188 => isShift ? "<" : ",",
            189 => isShift ? "_" : "-",
            190 => isShift ? ">" : ".",
            191 => isShift ? "?" : "/",
            192 => isShift ? "~" : "`",
            219 => isShift ? "{" : "[",
            220 => isShift ? "|" : "\\",
            221 => isShift ? "}" : "]",
            222 => isShift ? "\"" : "'",
            _ => string.Empty
        };

    private bool HandleCommandKey(VirtualKey key, bool isShift)
    {
        switch (key)
        {
            case VirtualKey.Space when CanEditText:
                _ = OpenCompletionAsync(isExplicit: true);
                return true;
            case VirtualKey.A:
                SelectAllText();
                return true;
            case VirtualKey.C:
                CopySelectionToClipboard();
                return true;
            case VirtualKey.X when CanEditText:
                CutSelectionToClipboard();
                return true;
            case VirtualKey.V when CanEditText:
                PasteTextFromClipboard();
                return true;
            case VirtualKey.Z when CanEditText:
                if (isShift)
                {
                    RedoTextEdit();
                }
                else
                {
                    UndoTextEdit();
                }

                return true;
            case VirtualKey.Y when CanEditText:
                RedoTextEdit();
                return true;
            case VirtualKey.D when CanEditText:
                DuplicateSelectedLines();
                return true;
            case VirtualKey.L when CanEditText:
                SelectCurrentLine();
                return true;
            case VirtualKey.K when CanEditText && isShift:
                DeleteSelectedLines();
                return true;
            case VirtualKey.Divide when CanEditText:
                ToggleSelectedLineComment();
                CloseCompletion();
                return true;
        }

        return false;
    }

    private bool HandleCompletionKey(VirtualKey key)
    {
        if (completionSession is null)
        {
            return false;
        }

        switch (key)
        {
            case VirtualKey.Escape:
                CloseCompletion();
                return true;
            case VirtualKey.Up:
                MoveCompletionSelection(-1);
                return true;
            case VirtualKey.Down:
                MoveCompletionSelection(1);
                return true;
            case VirtualKey.PageUp:
                MoveCompletionSelection(-8);
                return true;
            case VirtualKey.PageDown:
                MoveCompletionSelection(8);
                return true;
            case VirtualKey.Enter:
            case VirtualKey.Tab:
                CommitSelectedCompletion();
                return true;
        }

        return false;
    }

    private void RefreshCompletionAfterCharacter(char character)
    {
        if (isCommittingCompletion)
        {
            return;
        }

        if (completionSession is not null || IsCompletionTriggerCharacter(character))
        {
            _ = OpenCompletionAsync(isExplicit: false);
        }
        else
        {
            CloseCompletion();
        }
    }

    private void RefreshCompletionAfterEdit()
    {
        if (isCommittingCompletion)
        {
            return;
        }

        _ = OpenCompletionAsync(isExplicit: false);
    }

    private async Task OpenCompletionAsync(bool isExplicit)
    {
        if (!IsCompletionEnabled || !CanEditText)
        {
            CloseCompletion();
            return;
        }

        var version = ++completionRequestVersion;
        try
        {
            var provider = GetCompletionProvider();
            var request = CreateCompletionRequest(isExplicit);
            var result = await provider.GetCompletionsAsync(request).ConfigureAwait(true);
            if (version != completionRequestVersion)
            {
                return;
            }

            if (!ShouldShowCompletion(result, isExplicit))
            {
                CloseCompletion();
                return;
            }

            completionSession = new CodeCompletionSession(result);
            completionList.ItemsSource = result.Items.ToArray();
            completionList.SelectedIndex = 0;
            completionStatusText.Text = $"{result.Items.Length:N0} suggestions | Enter/Tab to accept | Esc to close";
            UpdateCompletionPresenterPosition();
            completionPresenter.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            if (version == completionRequestVersion)
            {
                CloseCompletion();
            }
        }
        catch
        {
            if (version == completionRequestVersion)
            {
                CloseCompletion();
            }
        }
    }

    private CodeCompletionRequest CreateCompletionRequest(bool isExplicit)
    {
        EnsureEditorDocument();
        var path = string.IsNullOrWhiteSpace(CompletionPath) ? "untitled" : CompletionPath;
        var language = string.IsNullOrWhiteSpace(CompletionLanguage)
            ? System.IO.Path.GetExtension(path).TrimStart('.')
            : CompletionLanguage;
        var metadata = new DiffDocumentMetadata(
            new DiffDocumentId(path),
            path,
            null,
            DiffFileStatus.Modified,
            language,
            0,
            0);
        var lines = GetLines()
            .Select((line, index) => line with { Index = index, NewLineNumber = index + 1 })
            .ToImmutableArray();
        var document = new DiffDocumentSnapshot(metadata.Id, metadata, lines);
        return new CodeCompletionRequest(
            document,
            caretPosition.RowIndex,
            caretPosition.Column,
            isExplicit,
            CompletionMaxItemCount,
            string.IsNullOrWhiteSpace(CompletionRepositoryPath) ? null : CompletionRepositoryPath);
    }

    private ICodeCompletionProvider GetCompletionProvider() =>
        CompletionProvider ?? (fallbackCompletionProvider ??= new DocumentCodeCompletionProvider());

    private bool ShouldShowCompletion(CodeCompletionResult result, bool isExplicit)
    {
        if (result.Items.IsDefaultOrEmpty)
        {
            return false;
        }

        if (isExplicit)
        {
            return true;
        }

        if (result.FilterText.Length >= Math.Max(1, CompletionTriggerLength))
        {
            return true;
        }

        return IsContextCompletionPosition(result);
    }

    private bool IsContextCompletionPosition(CodeCompletionResult result)
    {
        EnsureEditorDocument();
        var line = editorDocument.GetLine(caretPosition.RowIndex);
        var column = Math.Clamp(caretPosition.Column, 0, line.Length);
        if (column <= 0)
        {
            return false;
        }

        var previous = line[column - 1];
        if (previous == '.')
        {
            return result.Items.Any(item => item.Kind is CodeCompletionItemKind.Member
                or CodeCompletionItemKind.Function
                or CodeCompletionItemKind.Property
                or CodeCompletionItemKind.Symbol);
        }

        return IsXmlCompletionLanguage()
            && previous is '<' or ' ' or ':' or '/'
            && IsInsideOpenXmlTag(line, column);
    }

    private bool IsXmlCompletionLanguage()
    {
        var language = (CompletionLanguage ?? string.Empty).Trim().ToLowerInvariant();
        var extension = System.IO.Path.GetExtension(CompletionPath ?? string.Empty).ToLowerInvariant();
        return language is "xml" or "xaml" or "axaml" or "html" or "svg" or "props" or "targets" or "resx"
            || extension is ".xml" or ".xaml" or ".axaml" or ".html" or ".htm" or ".svg" or ".props"
                or ".targets" or ".resx" or ".config";
    }

    private static bool IsInsideOpenXmlTag(string line, int column)
    {
        var probe = Math.Clamp(column - 1, 0, Math.Max(0, line.Length - 1));
        var tagStart = line.LastIndexOf('<', probe);
        return tagStart >= 0
            && line.LastIndexOf('>', probe) < tagStart
            && !IsInsideQuotedXmlAttribute(line, tagStart, column);
    }

    private static bool IsInsideQuotedXmlAttribute(string line, int start, int column)
    {
        var quote = '\0';
        for (var index = start; index < column; index++)
        {
            var character = line[index];
            if (quote == '\0')
            {
                if (character is '"' or '\'')
                {
                    quote = character;
                }
            }
            else if (character == quote)
            {
                quote = '\0';
            }
        }

        return quote != '\0';
    }

    private static bool IsCompletionTriggerCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '@' or '$' or '.' or '<' or ':' or '/' or ' ';

    private void MoveCompletionSelection(int delta)
    {
        if (completionSession is null || completionList.Items.Count == 0)
        {
            return;
        }

        var next = Math.Clamp(completionList.SelectedIndex + delta, 0, completionList.Items.Count - 1);
        completionList.SelectedIndex = next;
        completionList.ScrollIntoView(completionList.Items[next]);
    }

    private void CommitSelectedCompletion()
    {
        if (completionSession is null)
        {
            return;
        }

        var item = completionList.SelectedItem as CodeCompletionItem ??
            completionSession.Result.Items.FirstOrDefault();
        if (item is not null)
        {
            CommitCompletionItem(item);
        }
    }

    private void CommitCompletionItem(CodeCompletionItem item)
    {
        if (completionSession is null)
        {
            return;
        }

        EnsureEditorDocument();
        var line = editorDocument.GetLine(caretPosition.RowIndex);
        var startColumn = Math.Clamp(completionSession.Result.ReplacementStartColumn, 0, line.Length);
        var endColumn = Math.Clamp(
            completionSession.Result.ReplacementStartColumn + completionSession.Result.ReplacementLength,
            startColumn,
            line.Length);
        isCommittingCompletion = true;
        try
        {
            ReplaceExplicitRange(
                new CodeTextPosition(caretPosition.RowIndex, startColumn),
                new CodeTextPosition(caretPosition.RowIndex, endColumn),
                item.InsertionText);
        }
        finally
        {
            isCommittingCompletion = false;
            CloseCompletion();
        }
    }

    private void CloseCompletion()
    {
        completionRequestVersion++;
        completionSession = null;
        completionList.ItemsSource = null;
        completionPresenter.Visibility = Visibility.Collapsed;
    }

    private void OnCompletionItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is CodeCompletionItem item)
        {
            CommitCompletionItem(item);
        }
    }

    private void UpdateCompletionPresenterPosition()
    {
        if (completionPresenter.Visibility != Visibility.Visible || completionSession is null)
        {
            return;
        }

        var canvasPoint = GetCaretCompletionCanvasPoint();
        var logicalPoint = ToLogicalPoint(canvasPoint);
        var width = Math.Min(420, Math.Max(260, ActualWidth - 24));
        var height = 280d;
        var x = Math.Clamp(logicalPoint.X, 8, Math.Max(8, ActualWidth - width - 8));
        var y = logicalPoint.Y + 4;
        if (y + height > ActualHeight && logicalPoint.Y - height - LineHeight > 8)
        {
            y = logicalPoint.Y - height - LineHeight;
        }

        completionPresenter.Width = width;
        completionPresenter.Margin = new Thickness(x, Math.Clamp(y, 8, Math.Max(8, ActualHeight - 40)), 0, 0);
    }

    private Point2 GetCaretCompletionCanvasPoint()
    {
        EnsureVisibleRows();
        var lines = GetLines();
        if (lines.Count == 0)
        {
            return new Point2(LeftPadding, TopPadding + LineHeight);
        }

        var lineIndex = Math.Clamp(caretPosition.RowIndex, 0, lines.Count - 1);
        var line = lines[lineIndex];
        var visibleRowIndex = GetVisibleRowIndexForLine(lineIndex);
        var charWidth = CodeCharacterWidth;
        var gutterWidth = CalculateGutterWidth(charWidth, lines);
        var column = Math.Clamp(caretPosition.Column, 0, line.Text.Length);
        var visualColumn = CodeTextLayout.GetVisualColumn(line.Text, column);
        var x = gutterWidth + TextPadding + visualColumn * charWidth - (float)scrollOffsetX;
        var y = TopPadding + visibleRowIndex * LineHeight - (float)scrollOffsetY + LineHeight;
        return new Point2(x, y);
    }

    private Point ToLogicalPoint(Point2 point)
    {
        var canvasSize = GetCanvasSize();
        var scaleX = canvasSize.Width > 0 ? ActualWidth / canvasSize.Width : 1;
        var scaleY = canvasSize.Height > 0 ? ActualHeight / canvasSize.Height : 1;
        return new Point(point.X * scaleX, point.Y * scaleY);
    }

    private bool HandleMenuKey(VirtualKey key, bool isShift)
    {
        switch (key)
        {
            case VirtualKey.Up when CanEditText:
                if (isShift)
                {
                    CopySelectedLines(-1);
                }
                else
                {
                    MoveSelectedLines(-1);
                }

                return true;
            case VirtualKey.Down when CanEditText:
                if (isShift)
                {
                    CopySelectedLines(1);
                }
                else
                {
                    MoveSelectedLines(1);
                }

                return true;
        }

        return false;
    }

    private bool HandleNavigationKey(VirtualKey key, bool extendSelection, bool documentModifier)
    {
        EnsureVisibleRows();
        var pageRows = Math.Max(1, (int)Math.Floor(GetVerticalViewportHeight() / Math.Max(1, LineHeight)) - 1);
        var next = caretPosition;
        switch (key)
        {
            case VirtualKey.Left:
                next = documentModifier ? MoveCaretWord(-1) : MoveCaretByCharacter(-1);
                break;
            case VirtualKey.Right:
                next = documentModifier ? MoveCaretWord(1) : MoveCaretByCharacter(1);
                break;
            case VirtualKey.Up:
                next = MoveCaretByLine(-1);
                break;
            case VirtualKey.Down:
                next = MoveCaretByLine(1);
                break;
            case VirtualKey.Home:
                next = documentModifier ? new CodeTextPosition(0, 0) : MoveCaretToSmartLineStart();
                break;
            case VirtualKey.End:
                next = documentModifier ? GetDocumentEndPosition() : MoveCaretToLineEdge(start: false);
                break;
            case VirtualKey.PageUp:
                next = MoveCaretByLine(-pageRows);
                break;
            case VirtualKey.PageDown:
                next = MoveCaretByLine(pageRows);
                break;
            case VirtualKey.Escape:
                SetCaret(caretPosition, extendSelection: false);
                return true;
            default:
                return false;
        }

        SetCaret(next, extendSelection);
        return true;
    }

    private static bool IsCommandModifierDown() =>
        IsKeyDown(VirtualKey.Control) ||
        IsKeyDown(VirtualKey.LeftControl) ||
        IsKeyDown(VirtualKey.RightControl) ||
        IsKeyDown(VirtualKey.LeftWindows) ||
        IsKeyDown(VirtualKey.RightWindows);

    private static bool IsMenuModifierDown() =>
        IsKeyDown(VirtualKey.Menu) ||
        IsKeyDown(VirtualKey.LeftMenu) ||
        IsKeyDown(VirtualKey.RightMenu);

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    private void ReplaceSelectionWithText(string text)
    {
        if (!CanEditText)
        {
            return;
        }

        EnsureEditorDocument();
        var (start, end) = GetEditableSelectionOrCaretRange();
        if (IsNoOpReplacement(start, end, text))
        {
            return;
        }

        editorDocument.CaptureUndoState(caretPosition, selectionAnchor, selectionActive);
        if (!editorDocument.Replace(start, end, text, out var newCaret))
        {
            return;
        }

        CompleteTextEdit(newCaret);
    }

    private bool IsNoOpReplacement(CodeTextPosition start, CodeTextPosition end, string text)
    {
        if (CodeTextEditorDocument.Compare(start, end) == 0)
        {
            return text.Length == 0;
        }

        return string.Equals(editorDocument.GetText(start, end), text, StringComparison.Ordinal);
    }

    private void BackspaceText()
    {
        EnsureEditorDocument();
        if (HasNonEmptySelection())
        {
            ReplaceSelectionWithText(string.Empty);
            return;
        }

        var start = MoveCaretByCharacter(-1);
        if (CodeTextEditorDocument.Compare(start, caretPosition) == 0)
        {
            return;
        }

        editorDocument.CaptureUndoState(caretPosition, selectionAnchor, selectionActive);
        if (editorDocument.Replace(start, caretPosition, string.Empty, out var newCaret))
        {
            CompleteTextEdit(newCaret);
        }
    }

    private void DeleteText()
    {
        EnsureEditorDocument();
        if (HasNonEmptySelection())
        {
            ReplaceSelectionWithText(string.Empty);
            return;
        }

        var end = MoveCaretByCharacter(1);
        if (CodeTextEditorDocument.Compare(end, caretPosition) == 0)
        {
            return;
        }

        editorDocument.CaptureUndoState(caretPosition, selectionAnchor, selectionActive);
        if (editorDocument.Replace(caretPosition, end, string.Empty, out var newCaret))
        {
            CompleteTextEdit(newCaret);
        }
    }

    private void BackspaceWordText()
    {
        EnsureEditorDocument();
        if (HasNonEmptySelection())
        {
            ReplaceSelectionWithText(string.Empty);
            return;
        }

        ReplaceExplicitRange(MoveCaretWord(-1), caretPosition, string.Empty);
    }

    private void DeleteWordText()
    {
        EnsureEditorDocument();
        if (HasNonEmptySelection())
        {
            ReplaceSelectionWithText(string.Empty);
            return;
        }

        ReplaceExplicitRange(caretPosition, MoveCaretWord(1), string.Empty);
    }

    private void ReplaceExplicitRange(CodeTextPosition start, CodeTextPosition end, string text)
    {
        if (CodeTextEditorDocument.Compare(start, end) == 0 && text.Length == 0)
        {
            return;
        }

        editorDocument.CaptureUndoState(caretPosition, selectionAnchor, selectionActive);
        if (editorDocument.Replace(start, end, text, out var newCaret))
        {
            CompleteTextEdit(newCaret);
        }
    }

    private void UndoTextEdit()
    {
        EnsureEditorDocument();
        if (editorDocument.Undo(ref caretPosition, ref selectionAnchor, ref selectionActive))
        {
            CompleteTextEdit(caretPosition, preserveSelection: true);
        }
    }

    private void RedoTextEdit()
    {
        EnsureEditorDocument();
        if (editorDocument.Redo(ref caretPosition, ref selectionAnchor, ref selectionActive))
        {
            CompleteTextEdit(caretPosition, preserveSelection: true);
        }
    }

    private void CompleteTextEdit(CodeTextPosition newCaret, bool preserveSelection = false)
    {
        caretPosition = editorDocument.ClampPosition(newCaret);
        if (!preserveSelection)
        {
            selectionAnchor = caretPosition;
            selectionActive = caretPosition;
        }

        collapsedFoldStarts.Clear();
        visibleRowsDirty = true;
        editableLinesVersion = -1;
        ClampScrollOffset();
        EnsureCaretVisible();
        UpdateEditableTextProperty();
        TextEdited?.Invoke(this, new CodeFileTextEditedEventArgs(editorDocument.Text, editorDocument.CanUndo, editorDocument.CanRedo));
        RequestDeferredRender();
    }

    private (CodeTextPosition Start, CodeTextPosition End) GetEditableSelectionOrCaretRange()
    {
        if (TryGetSelectionRange(out var start, out var end) &&
            CodeTextEditorDocument.Compare(start, end) != 0)
        {
            return (editorDocument.ClampPosition(start), editorDocument.ClampPosition(end));
        }

        var caret = editorDocument.ClampPosition(caretPosition);
        return (caret, caret);
    }

    private bool HasNonEmptySelection() =>
        TryGetSelectionRange(out var start, out var end) &&
        CodeTextEditorDocument.Compare(start, end) != 0;

    private void IndentSelectionOrInsertTab()
    {
        if (!HasNonEmptySelection())
        {
            ReplaceSelectionWithText(new string(' ', CodeTextLayout.TabSize));
            return;
        }

        IndentSelectedLines();
    }

    private void IndentSelectedLines()
    {
        EnsureEditorDocument();
        var range = GetSelectedLineRange();
        var replacement = GetEditorLines(range.FirstLine, range.LastLine)
            .Select(line => new string(' ', CodeTextLayout.TabSize) + line)
            .ToArray();
        var caret = ShiftLineRangeColumn(caretPosition, range.FirstLine, range.LastLine, CodeTextLayout.TabSize);
        var anchor = selectionAnchor is { } selectedAnchor
            ? ShiftLineRangeColumn(selectedAnchor, range.FirstLine, range.LastLine, CodeTextLayout.TabSize)
            : caret;
        var active = selectionActive is { } selectedActive
            ? ShiftLineRangeColumn(selectedActive, range.FirstLine, range.LastLine, CodeTextLayout.TabSize)
            : caret;

        ReplaceLineRange(range.FirstLine, range.LastLine, replacement, caret, anchor, active);
    }

    private void OutdentSelectedLines()
    {
        EnsureEditorDocument();
        var range = GetSelectedLineRange();
        var sourceLines = GetEditorLines(range.FirstLine, range.LastLine);
        var removedByLine = new int[sourceLines.Count];
        var replacement = new string[sourceLines.Count];
        var changed = false;
        for (var index = 0; index < sourceLines.Count; index++)
        {
            var (line, removed) = RemoveIndent(sourceLines[index]);
            replacement[index] = line;
            removedByLine[index] = removed;
            changed |= removed > 0;
        }

        if (!changed)
        {
            return;
        }

        var caret = ShiftOutdentedPosition(caretPosition, range.FirstLine, range.LastLine, removedByLine);
        var anchor = selectionAnchor is { } selectedAnchor
            ? ShiftOutdentedPosition(selectedAnchor, range.FirstLine, range.LastLine, removedByLine)
            : caret;
        var active = selectionActive is { } selectedActive
            ? ShiftOutdentedPosition(selectedActive, range.FirstLine, range.LastLine, removedByLine)
            : caret;

        ReplaceLineRange(range.FirstLine, range.LastLine, replacement, caret, anchor, active);
    }

    private void DuplicateSelectedLines()
    {
        EnsureEditorDocument();
        var hadSelection = HasNonEmptySelection();
        var range = GetSelectedLineRange();
        var sourceLines = GetEditorLines(range.FirstLine, range.LastLine);
        var replacement = sourceLines.Concat(sourceLines).ToArray();
        var duplicatedLineCount = sourceLines.Count;
        var duplicateStart = range.FirstLine + duplicatedLineCount;
        var newCaret = new CodeTextPosition(
            Math.Clamp(caretPosition.RowIndex + duplicatedLineCount, duplicateStart, duplicateStart + duplicatedLineCount - 1),
            caretPosition.Column);
        var anchor = newCaret;
        var active = newCaret;
        if (hadSelection)
        {
            anchor = new CodeTextPosition(duplicateStart, 0);
            var lastDuplicateLine = duplicateStart + duplicatedLineCount - 1;
            active = new CodeTextPosition(lastDuplicateLine, sourceLines[^1].Length);
            newCaret = active;
        }

        ReplaceLineRange(range.FirstLine, range.LastLine, replacement, newCaret, anchor, active);
    }

    private void MoveSelectedLines(int direction)
    {
        if (!CanEditText || direction == 0)
        {
            return;
        }

        EnsureEditorDocument();
        var range = GetSelectedLineRange();
        var sourceLines = GetEditorLines(range.FirstLine, range.LastLine);
        if (direction < 0)
        {
            if (range.FirstLine <= 0)
            {
                return;
            }

            var previousLine = editorDocument.GetLine(range.FirstLine - 1);
            var replacement = sourceLines.Append(previousLine).ToArray();
            var caret = ShiftMovedBlockPosition(caretPosition, range.FirstLine, range.LastLine, -1);
            var anchor = selectionAnchor is { } selectedAnchor
                ? ShiftMovedBlockPosition(selectedAnchor, range.FirstLine, range.LastLine, -1)
                : caret;
            var active = selectionActive is { } selectedActive
                ? ShiftMovedBlockPosition(selectedActive, range.FirstLine, range.LastLine, -1)
                : caret;

            ReplaceLineRange(range.FirstLine - 1, range.LastLine, replacement, caret, anchor, active);
            return;
        }

        if (range.LastLine >= editorDocument.LineCount - 1)
        {
            return;
        }

        var nextLine = editorDocument.GetLine(range.LastLine + 1);
        var movedReplacement = new[] { nextLine }.Concat(sourceLines).ToArray();
        var movedCaret = ShiftMovedBlockPosition(caretPosition, range.FirstLine, range.LastLine, 1);
        var movedAnchor = selectionAnchor is { } anchorPosition
            ? ShiftMovedBlockPosition(anchorPosition, range.FirstLine, range.LastLine, 1)
            : movedCaret;
        var movedActive = selectionActive is { } activePosition
            ? ShiftMovedBlockPosition(activePosition, range.FirstLine, range.LastLine, 1)
            : movedCaret;

        ReplaceLineRange(range.FirstLine, range.LastLine + 1, movedReplacement, movedCaret, movedAnchor, movedActive);
    }

    private void CopySelectedLines(int direction)
    {
        if (!CanEditText || direction == 0)
        {
            return;
        }

        EnsureEditorDocument();
        var hadSelection = HasNonEmptySelection();
        var range = GetSelectedLineRange();
        var sourceLines = GetEditorLines(range.FirstLine, range.LastLine);
        var sourceOffset = Math.Clamp(caretPosition.RowIndex - range.FirstLine, 0, Math.Max(0, sourceLines.Count - 1));
        var copiedLine = sourceLines[sourceOffset];
        if (direction < 0)
        {
            var boundaryLine = editorDocument.GetLine(range.FirstLine);
            var replacement = sourceLines.Append(boundaryLine).ToArray();
            var copyStart = range.FirstLine;
            var caret = new CodeTextPosition(copyStart + sourceOffset, Math.Clamp(caretPosition.Column, 0, copiedLine.Length));
            var (anchor, active) = CreateCopiedLineSelection(copyStart, sourceLines, hadSelection, caret, editorDocument.LineCount + sourceLines.Count);
            ReplaceLineRange(range.FirstLine, range.FirstLine, replacement, caret, anchor, active);
            return;
        }

        var lowerBoundaryLine = editorDocument.GetLine(range.LastLine);
        var lowerReplacement = new[] { lowerBoundaryLine }.Concat(sourceLines).ToArray();
        var lowerCopyStart = range.LastLine + 1;
        var lowerCaret = new CodeTextPosition(lowerCopyStart + sourceOffset, Math.Clamp(caretPosition.Column, 0, copiedLine.Length));
        var (lowerAnchor, lowerActive) = CreateCopiedLineSelection(lowerCopyStart, sourceLines, hadSelection, lowerCaret, editorDocument.LineCount + sourceLines.Count);
        ReplaceLineRange(range.LastLine, range.LastLine, lowerReplacement, lowerCaret, lowerAnchor, lowerActive);
    }

    private void ToggleSelectedLineComment()
    {
        if (!CanEditText)
        {
            return;
        }

        EnsureEditorDocument();
        var prefix = string.IsNullOrEmpty(LineCommentPrefix) ? "// " : LineCommentPrefix;
        var suffix = LineCommentSuffix ?? string.Empty;
        var range = GetSelectedLineRange();
        var sourceLines = GetEditorLines(range.FirstLine, range.LastLine);
        var meaningfulLines = sourceLines
            .Select((line, index) => (line, index))
            .Where(item => !string.IsNullOrWhiteSpace(item.line))
            .ToArray();

        if (meaningfulLines.Length == 0)
        {
            return;
        }

        var shouldUncomment = meaningfulLines.All(item => IsLineCommented(item.line, prefix, suffix));
        var replacement = sourceLines
            .Select(line => string.IsNullOrWhiteSpace(line)
                ? line
                : shouldUncomment
                    ? UncommentLine(line, prefix, suffix)
                    : CommentLine(line, prefix, suffix))
            .ToArray();

        if (sourceLines.SequenceEqual(replacement, StringComparer.Ordinal))
        {
            return;
        }

        var caret = ClampPositionToReplacement(caretPosition, range.FirstLine, replacement);
        var anchor = selectionAnchor is { } selectedAnchor
            ? ClampPositionToReplacement(selectedAnchor, range.FirstLine, replacement)
            : caret;
        var active = selectionActive is { } selectedActive
            ? ClampPositionToReplacement(selectedActive, range.FirstLine, replacement)
            : caret;

        ReplaceLineRange(range.FirstLine, range.LastLine, replacement, caret, anchor, active);
    }

    private bool TryHandlePairCharacter(char character)
    {
        if (!TryGetClosingPair(character, out var closing))
        {
            return false;
        }

        EnsureEditorDocument();
        if (HasNonEmptySelection())
        {
            SurroundSelection(character, closing);
            return true;
        }

        editorDocument.CaptureUndoState(caretPosition, selectionAnchor, selectionActive);
        if (!editorDocument.Replace(caretPosition, caretPosition, string.Concat(character, closing), out var newCaret))
        {
            return true;
        }

        CompleteTextEdit(new CodeTextPosition(newCaret.RowIndex, Math.Max(0, newCaret.Column - 1)));
        return true;
    }

    private void SurroundSelection(char opening, char closing)
    {
        if (!TryGetSelectionRange(out var start, out var end) ||
            CodeTextEditorDocument.Compare(start, end) == 0)
        {
            return;
        }

        EnsureEditorDocument();
        start = editorDocument.ClampPosition(start);
        end = editorDocument.ClampPosition(end);
        var selectedText = editorDocument.GetText(start, end);
        var startOffset = GetOffset(start);
        editorDocument.CaptureUndoState(caretPosition, selectionAnchor, selectionActive);
        if (!editorDocument.Replace(start, end, string.Concat(opening, selectedText, closing), out _))
        {
            return;
        }

        var anchor = GetPosition(startOffset + 1);
        var active = GetPosition(startOffset + 1 + selectedText.Length);
        caretPosition = active;
        selectionAnchor = anchor;
        selectionActive = active;
        CompleteTextEdit(active, preserveSelection: true);
    }

    private void DeleteSelectedLines()
    {
        EnsureEditorDocument();
        var range = GetSelectedLineRange();
        var deletedCount = range.LastLine - range.FirstLine + 1;
        var remainingLineCount = Math.Max(1, editorDocument.LineCount - deletedCount);
        var nextLine = Math.Clamp(range.FirstLine, 0, remainingLineCount - 1);
        var nextCaret = new CodeTextPosition(nextLine, 0);

        ReplaceLineRange(range.FirstLine, range.LastLine, [], nextCaret, nextCaret, nextCaret);
    }

    private void SelectCurrentLine()
    {
        EnsureEditorDocument();
        var row = Math.Clamp(caretPosition.RowIndex, 0, Math.Max(0, editorDocument.LineCount - 1));
        selectionAnchor = new CodeTextPosition(row, 0);
        selectionActive = row < editorDocument.LineCount - 1
            ? new CodeTextPosition(row + 1, 0)
            : new CodeTextPosition(row, editorDocument.GetLine(row).Length);
        caretPosition = selectionActive.Value;
        EnsureCaretVisible();
        RequestRender();
    }

    private (int FirstLine, int LastLine) GetSelectedLineRange()
    {
        EnsureEditorDocument();
        if (!TryGetSelectionRange(out var start, out var end) ||
            CodeTextEditorDocument.Compare(start, end) == 0)
        {
            var row = Math.Clamp(caretPosition.RowIndex, 0, Math.Max(0, editorDocument.LineCount - 1));
            return (row, row);
        }

        var first = Math.Clamp(start.RowIndex, 0, Math.Max(0, editorDocument.LineCount - 1));
        var last = Math.Clamp(end.RowIndex, first, Math.Max(0, editorDocument.LineCount - 1));
        if (end.Column == 0 && last > first)
        {
            last--;
        }

        return (first, last);
    }

    private List<string> GetEditorLines(int firstLine, int lastLine)
    {
        var lines = new List<string>(Math.Max(0, lastLine - firstLine + 1));
        for (var lineIndex = firstLine; lineIndex <= lastLine; lineIndex++)
        {
            lines.Add(editorDocument.GetLine(lineIndex));
        }

        return lines;
    }

    private void ReplaceLineRange(
        int firstLine,
        int lastLine,
        IReadOnlyList<string> replacementLines,
        CodeTextPosition caret,
        CodeTextPosition anchor,
        CodeTextPosition active)
    {
        var currentLines = GetEditorLines(firstLine, lastLine);
        if (currentLines.SequenceEqual(replacementLines, StringComparer.Ordinal))
        {
            return;
        }

        editorDocument.CaptureUndoState(caretPosition, selectionAnchor, selectionActive);
        if (!editorDocument.ReplaceLines(firstLine, lastLine, replacementLines, out _))
        {
            return;
        }

        caretPosition = caret;
        selectionAnchor = anchor;
        selectionActive = active;
        CompleteTextEdit(caret, preserveSelection: true);
    }

    private static (string Line, int Removed) RemoveIndent(string line)
    {
        if (line.Length == 0)
        {
            return (line, 0);
        }

        if (line[0] == '\t')
        {
            return (line[1..], 1);
        }

        var spaces = 0;
        while (spaces < Math.Min(CodeTextLayout.TabSize, line.Length) && line[spaces] == ' ')
        {
            spaces++;
        }

        return spaces == 0 ? (line, 0) : (line[spaces..], spaces);
    }

    private static CodeTextPosition ShiftLineRangeColumn(CodeTextPosition position, int firstLine, int lastLine, int delta)
    {
        if (position.RowIndex < firstLine || position.RowIndex > lastLine)
        {
            return position;
        }

        return new CodeTextPosition(position.RowIndex, Math.Max(0, position.Column + delta));
    }

    private static CodeTextPosition ShiftOutdentedPosition(CodeTextPosition position, int firstLine, int lastLine, IReadOnlyList<int> removedByLine)
    {
        if (position.RowIndex < firstLine || position.RowIndex > lastLine)
        {
            return position;
        }

        var index = position.RowIndex - firstLine;
        var removed = index >= 0 && index < removedByLine.Count ? removedByLine[index] : 0;
        return new CodeTextPosition(position.RowIndex, Math.Max(0, position.Column - Math.Min(position.Column, removed)));
    }

    private static CodeTextPosition ShiftMovedBlockPosition(CodeTextPosition position, int firstLine, int lastLine, int direction)
    {
        if (position.RowIndex >= firstLine && position.RowIndex <= lastLine)
        {
            return new CodeTextPosition(position.RowIndex + direction, position.Column);
        }

        if (position.RowIndex == lastLine + 1 && position.Column == 0)
        {
            return new CodeTextPosition(position.RowIndex + direction, 0);
        }

        return position;
    }

    private static (CodeTextPosition Anchor, CodeTextPosition Active) CreateCopiedLineSelection(
        int copyStart,
        IReadOnlyList<string> sourceLines,
        bool hadSelection,
        CodeTextPosition caret,
        int newLineCount)
    {
        if (!hadSelection || sourceLines.Count == 0)
        {
            return (caret, caret);
        }

        var copyLast = copyStart + sourceLines.Count - 1;
        var anchor = new CodeTextPosition(copyStart, 0);
        var active = copyLast + 1 < newLineCount
            ? new CodeTextPosition(copyLast + 1, 0)
            : new CodeTextPosition(copyLast, sourceLines[^1].Length);
        return (anchor, active);
    }

    private static CodeTextPosition ClampPositionToReplacement(
        CodeTextPosition position,
        int firstLine,
        IReadOnlyList<string> replacementLines)
    {
        if (position.RowIndex < firstLine || position.RowIndex >= firstLine + replacementLines.Count)
        {
            return position;
        }

        var replacementLine = replacementLines[position.RowIndex - firstLine];
        return new CodeTextPosition(position.RowIndex, Math.Clamp(position.Column, 0, replacementLine.Length));
    }

    private static bool IsLineCommented(string line, string prefix, string suffix)
    {
        var indent = GetIndentLength(line);
        if (!line.AsSpan(indent).StartsWith(prefix.AsSpan(), StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrEmpty(suffix))
        {
            return true;
        }

        var suffixEnd = line.Length;
        while (suffixEnd > indent && char.IsWhiteSpace(line[suffixEnd - 1]))
        {
            suffixEnd--;
        }

        return suffixEnd >= suffix.Length &&
            line.AsSpan(suffixEnd - suffix.Length, suffix.Length).SequenceEqual(suffix.AsSpan());
    }

    private static string CommentLine(string line, string prefix, string suffix)
    {
        var indent = GetIndentLength(line);
        var commented = line[..indent] + prefix + line[indent..];
        return string.IsNullOrEmpty(suffix) ? commented : commented + suffix;
    }

    private static string UncommentLine(string line, string prefix, string suffix)
    {
        var indent = GetIndentLength(line);
        var start = line.AsSpan(indent).StartsWith(prefix.AsSpan(), StringComparison.Ordinal)
            ? line.Remove(indent, prefix.Length)
            : line;

        if (string.IsNullOrEmpty(suffix))
        {
            return start;
        }

        var suffixEnd = start.Length;
        while (suffixEnd > 0 && char.IsWhiteSpace(start[suffixEnd - 1]))
        {
            suffixEnd--;
        }

        return suffixEnd >= suffix.Length &&
            start.AsSpan(suffixEnd - suffix.Length, suffix.Length).SequenceEqual(suffix.AsSpan())
            ? start.Remove(suffixEnd - suffix.Length, suffix.Length)
            : start;
    }

    private static int GetIndentLength(string line)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }

        return index;
    }

    private static bool TryGetClosingPair(char opening, out char closing)
    {
        closing = opening switch
        {
            '(' => ')',
            '[' => ']',
            '{' => '}',
            '"' => '"',
            '\'' => '\'',
            '`' => '`',
            _ => '\0'
        };
        return closing != '\0';
    }

    private void SelectAllText()
    {
        EnsureEditorDocument();
        caretPosition = GetDocumentEndPosition();
        selectionAnchor = new CodeTextPosition(0, 0);
        selectionActive = caretPosition;
        EnsureCaretVisible();
        RequestRender();
    }

    private void CopySelectionToClipboard()
    {
        var text = GetSelectedText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch
        {
            // Clipboard access can fail when the host platform denies the request.
        }
    }

    private void CutSelectionToClipboard()
    {
        if (!CanEditText || !HasNonEmptySelection())
        {
            return;
        }

        CopySelectionToClipboard();
        ReplaceSelectionWithText(string.Empty);
    }

    private async void PasteTextFromClipboard()
    {
        if (!CanEditText)
        {
            return;
        }

        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                ReplaceSelectionWithText(await content.GetTextAsync());
            }
        }
        catch
        {
            // Clipboard access can fail when the host platform denies the request.
        }
    }

    private string GetSelectedText()
    {
        if (!TryGetSelectionRange(out var start, out var end) ||
            CodeTextEditorDocument.Compare(start, end) == 0)
        {
            return string.Empty;
        }

        if (CanEditText)
        {
            EnsureEditorDocument();
            return editorDocument.GetText(start, end);
        }

        return GetSelectedTextFromLines(start, end);
    }

    private string GetSelectedTextFromLines(CodeTextPosition start, CodeTextPosition end)
    {
        var lines = GetLines();
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        var startLineIndex = Math.Clamp(start.RowIndex, 0, lines.Count - 1);
        var endLineIndex = Math.Clamp(end.RowIndex, startLineIndex, lines.Count - 1);
        for (var lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
        {
            var text = lines[lineIndex].Text;
            var startColumn = lineIndex == start.RowIndex ? Math.Clamp(start.Column, 0, text.Length) : 0;
            var endColumn = lineIndex == end.RowIndex ? Math.Clamp(end.Column, startColumn, text.Length) : text.Length;
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(text[startColumn..endColumn]);
        }

        return builder.ToString();
    }

    private void SetCaret(CodeTextPosition position, bool extendSelection)
    {
        EnsureEditorDocument();
        var next = editorDocument.ClampPosition(position);
        if (extendSelection)
        {
            selectionAnchor ??= caretPosition;
            selectionActive = next;
        }
        else
        {
            selectionAnchor = next;
            selectionActive = next;
        }

        caretPosition = next;
        EnsureCaretVisible();
        RequestRender();
    }

    private CodeTextPosition MoveCaretByCharacter(int direction)
    {
        EnsureEditorDocument();
        var position = editorDocument.ClampPosition(caretPosition);
        if (direction < 0)
        {
            if (position.Column > 0)
            {
                return new CodeTextPosition(position.RowIndex, position.Column - 1);
            }

            if (position.RowIndex > 0)
            {
                var previousRow = position.RowIndex - 1;
                return new CodeTextPosition(previousRow, editorDocument.GetLine(previousRow).Length);
            }
        }
        else if (direction > 0)
        {
            var line = editorDocument.GetLine(position.RowIndex);
            if (position.Column < line.Length)
            {
                return new CodeTextPosition(position.RowIndex, position.Column + 1);
            }

            if (position.RowIndex < editorDocument.LineCount - 1)
            {
                return new CodeTextPosition(position.RowIndex + 1, 0);
            }
        }

        return position;
    }

    private CodeTextPosition MoveCaretWord(int direction)
    {
        EnsureEditorDocument();
        var position = editorDocument.ClampPosition(caretPosition);
        if (direction == 0)
        {
            return position;
        }

        var text = editorDocument.Text;
        var offset = GetOffset(position);
        if (direction < 0)
        {
            offset = Math.Max(0, offset - 1);
            while (offset > 0 && char.IsWhiteSpace(text[offset]))
            {
                offset--;
            }

            while (offset > 0 && IsWordCharacter(text[offset - 1]))
            {
                offset--;
            }
        }
        else
        {
            while (offset < text.Length && IsWordCharacter(text[offset]))
            {
                offset++;
            }

            while (offset < text.Length && char.IsWhiteSpace(text[offset]))
            {
                offset++;
            }
        }

        return GetPosition(offset);
    }

    private CodeTextPosition MoveCaretByLine(int deltaRows)
    {
        EnsureEditorDocument();
        var position = editorDocument.ClampPosition(caretPosition);
        EnsureVisibleRows();
        if (visibleRows.Length > 0)
        {
            var visibleRow = GetVisibleRowIndexForLine(position.RowIndex);
            var targetVisibleRow = Math.Clamp(visibleRow + deltaRows, 0, visibleRows.Length - 1);
            var targetLine = Math.Clamp(visibleRows[targetVisibleRow].LineIndex, 0, Math.Max(0, editorDocument.LineCount - 1));
            var targetColumn = Math.Clamp(position.Column, 0, editorDocument.GetLine(targetLine).Length);
            return new CodeTextPosition(targetLine, targetColumn);
        }

        var line = Math.Clamp(position.RowIndex + deltaRows, 0, Math.Max(0, editorDocument.LineCount - 1));
        var column = Math.Clamp(position.Column, 0, editorDocument.GetLine(line).Length);
        return new CodeTextPosition(line, column);
    }

    private CodeTextPosition MoveCaretToLineEdge(bool start)
    {
        EnsureEditorDocument();
        var position = editorDocument.ClampPosition(caretPosition);
        return start
            ? new CodeTextPosition(position.RowIndex, 0)
            : new CodeTextPosition(position.RowIndex, editorDocument.GetLine(position.RowIndex).Length);
    }

    private CodeTextPosition MoveCaretToSmartLineStart()
    {
        EnsureEditorDocument();
        var position = editorDocument.ClampPosition(caretPosition);
        var line = editorDocument.GetLine(position.RowIndex);
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < line.Length && char.IsWhiteSpace(line[firstNonWhitespace]))
        {
            firstNonWhitespace++;
        }

        var targetColumn = position.Column == firstNonWhitespace ? 0 : firstNonWhitespace;
        return new CodeTextPosition(position.RowIndex, targetColumn);
    }

    private CodeTextPosition GetDocumentEndPosition()
    {
        EnsureEditorDocument();
        var row = Math.Max(0, editorDocument.LineCount - 1);
        return new CodeTextPosition(row, editorDocument.GetLine(row).Length);
    }

    private int GetOffset(CodeTextPosition position)
    {
        position = editorDocument.ClampPosition(position);
        var offset = 0;
        for (var row = 0; row < position.RowIndex; row++)
        {
            offset += editorDocument.GetLine(row).Length + 1;
        }

        return offset + position.Column;
    }

    private CodeTextPosition GetPosition(int offset)
    {
        offset = Math.Clamp(offset, 0, editorDocument.Text.Length);
        var remaining = offset;
        for (var row = 0; row < editorDocument.LineCount; row++)
        {
            var lineLength = editorDocument.GetLine(row).Length;
            if (remaining <= lineLength)
            {
                return new CodeTextPosition(row, remaining);
            }

            remaining -= lineLength + 1;
        }

        return GetDocumentEndPosition();
    }

    private static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '_';

    private void EnsureCaretVisible()
    {
        EnsureVisibleRows();
        var visibleRowIndex = visibleRows.Length > 0 ? GetVisibleRowIndexForLine(caretPosition.RowIndex) : caretPosition.RowIndex;
        var viewportHeight = GetVerticalViewportHeight();
        var caretTop = TopPadding + visibleRowIndex * LineHeight;
        var caretBottom = caretTop + LineHeight;
        if (caretTop < scrollOffsetY + TopPadding)
        {
            scrollOffsetY = Math.Max(0, caretTop - TopPadding);
        }
        else if (caretBottom > scrollOffsetY + viewportHeight - BottomPadding)
        {
            scrollOffsetY = Math.Max(0, caretBottom - viewportHeight + BottomPadding);
        }

        var lines = GetLines();
        if (caretPosition.RowIndex >= 0 && caretPosition.RowIndex < lines.Count)
        {
            var size = GetCanvasSize();
            var charWidth = CodeCharacterWidth;
            var gutterWidth = CalculateGutterWidth(charWidth, lines);
            var contentRight = GetContentRight((float)size.Width, (float)size.Height);
            var viewportWidth = GetTextViewportWidth(gutterWidth, contentRight);
            var line = lines[caretPosition.RowIndex];
            var column = Math.Clamp(caretPosition.Column, 0, line.Text.Length);
            var caretX = CodeTextLayout.GetVisualColumn(line.Text, column) * charWidth;
            if (caretX < scrollOffsetX + charWidth)
            {
                scrollOffsetX = Math.Max(0, caretX - charWidth);
            }
            else if (caretX > scrollOffsetX + viewportWidth - charWidth)
            {
                scrollOffsetX = Math.Max(0, caretX - viewportWidth + charWidth * 2);
            }
        }

        ClampScrollOffset();
    }

    private void UpdateEditableTextProperty()
    {
        isUpdatingEditableText = true;
        try
        {
            SetValue(TextProperty, editorDocument.Text);
        }
        finally
        {
            isUpdatingEditableText = false;
        }
    }

    private void DragScrollbar(double pointerY)
    {
        var viewportHeight = GetVerticalViewportHeight();
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

    private void DragHorizontalScrollbar(double pointerX)
    {
        var size = GetCanvasSize();
        var lines = GetLines();
        var charWidth = CodeCharacterWidth;
        var gutterWidth = CalculateGutterWidth(charWidth, lines);
        var contentRight = GetContentRight((float)size.Width, (float)size.Height);
        var viewportWidth = GetTextViewportWidth(gutterWidth, contentRight);
        var contentWidth = GetTextContentWidth(lines, charWidth);
        if (contentWidth <= viewportWidth)
        {
            scrollOffsetX = 0;
            return;
        }

        var track = GetHorizontalScrollbarTrack(size.Width, size.Height, gutterWidth, contentRight);
        var thumbWidth = GetHorizontalScrollbarThumbWidth(track.Width, viewportWidth, contentWidth);
        var trackScrollable = Math.Max(1, track.Width - thumbWidth);
        var thumbLeft = Math.Clamp(pointerX - horizontalScrollbarGrabOffsetX, track.Left, track.Right - thumbWidth);
        scrollOffsetX = (thumbLeft - track.Left) / trackScrollable * (contentWidth - viewportWidth);
        ClampScrollOffset();
        RequestRender();
    }

    private void DragMinimap(double pointerY)
    {
        var size = GetCanvasSize();
        var bounds = GetMinimapBounds((float)size.Width, (float)size.Height);
        DragMinimap(pointerY, bounds);
    }

    private void DragMinimap(double pointerY, SKRect minimapBounds)
    {
        EnsureVisibleRows();
        var viewportHeight = GetVerticalViewportHeight();
        var contentHeight = GetContentHeight();
        if (visibleRows.Length == 0 || contentHeight <= viewportHeight)
        {
            scrollOffsetY = 0;
            RequestRender();
            return;
        }

        var inner = GetMinimapInnerBounds(minimapBounds);
        var viewport = GetMinimapViewport(minimapBounds, viewportHeight);
        var availableTop = inner.Top;
        var availableBottom = Math.Max(availableTop, inner.Bottom - viewport.Height);
        var thumbTop = Math.Clamp(pointerY - minimapGrabOffsetY, availableTop, availableBottom);
        var ratio = (thumbTop - inner.Top) / Math.Max(1, inner.Height - viewport.Height);
        scrollOffsetY = ratio * Math.Max(0, contentHeight - viewportHeight);
        ClampScrollOffset();
        RequestRender();
    }

    private static void DrawGutter(SKCanvas canvasSurface, CodeFileViewerPalette palette, float gutterWidth, float height, bool isDiffMode)
    {
        using var gutterPaint = new SKPaint { Color = palette.GutterBackground, Style = SKPaintStyle.Fill };
        using var borderPaint = new SKPaint { Color = palette.Border, StrokeWidth = 1, Style = SKPaintStyle.Stroke };
        canvasSurface.DrawRect(SKRect.Create(0, 0, gutterWidth, height), gutterPaint);
        if (!isDiffMode)
        {
            using var foldDividerPaint = new SKPaint { Color = WithAlpha(palette.Border, 120), StrokeWidth = 1, Style = SKPaintStyle.Stroke };
            canvasSurface.DrawLine(LeftPadding + FoldGutterWidth - 0.5f, 0, LeftPadding + FoldGutterWidth - 0.5f, height, foldDividerPaint);
        }

        canvasSurface.DrawLine(gutterWidth - 0.5f, 0, gutterWidth - 0.5f, height, borderPaint);
    }

    private static void DrawLineBackground(SKCanvas canvasSurface, CodeFileViewerPalette palette, float gutterWidth, float contentRight, float y, float lineHeight, DiffLineKind kind, bool isCollapsed)
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
        canvasSurface.DrawRect(SKRect.Create(gutterWidth, y, Math.Max(0, contentRight - gutterWidth), lineHeight), paint);
    }

    private void DrawSelection(SKCanvas canvasSurface, CodeFileViewerPalette palette, VisibleCodeRow row, DiffLine line, float textX, float y, float charWidth, float contentRight)
    {
        var lineIndex = row.LineIndex;
        if (!TryGetSelectionRange(out var start, out var end) ||
            lineIndex < start.RowIndex ||
            lineIndex > end.RowIndex)
        {
            return;
        }

        var startColumn = lineIndex == start.RowIndex ? start.Column : 0;
        var endColumn = lineIndex == end.RowIndex ? end.Column : line.Text.Length;
        startColumn = Math.Clamp(startColumn, 0, line.Text.Length);
        endColumn = Math.Clamp(endColumn, 0, line.Text.Length);

        var startVisualColumn = CodeTextLayout.GetVisualColumn(line.Text, startColumn);
        var endVisualColumn = CodeTextLayout.GetVisualColumn(line.Text, endColumn);
        var selectionLeft = textX + startVisualColumn * charWidth;
        var selectionRight = textX + Math.Max(startVisualColumn, endVisualColumn) * charWidth;
        var maxRight = Math.Max(selectionLeft + 1, contentRight);

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

    private void DrawCaret(SKCanvas canvasSurface, CodeFileViewerPalette palette, VisibleCodeRow row, DiffLine line, float textX, float y, float charWidth, float contentRight)
    {
        if (!CanEditText || FocusState == FocusState.Unfocused || row.LineIndex != caretPosition.RowIndex)
        {
            return;
        }

        var column = Math.Clamp(caretPosition.Column, 0, line.Text.Length);
        var visualColumn = CodeTextLayout.GetVisualColumn(line.Text, column);
        var caretX = Math.Clamp(textX + visualColumn * charWidth, textX, Math.Max(textX, contentRight - 2));
        using var caretPaint = new SKPaint
        {
            Color = palette.Accent,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = false
        };
        canvasSurface.DrawLine(caretX, y + 3, caretX, y + Math.Max(4, LineHeight - 3), caretPaint);
    }

    private static void DrawLineNumber(SKCanvas canvasSurface, int lineNumber, float gutterWidth, float y, float baselineOffset, SKPaint paint, SKFont font, TextFontDescriptor fontDescriptor)
    {
        var text = lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var width = TextMetrics.MeasureNaturalWidth(text, fontDescriptor);
        canvasSurface.DrawText(text, gutterWidth - 10 - width, y + baselineOffset, font, paint);
    }

    private static void DrawDiffGutter(
        SKCanvas canvasSurface,
        CodeFileViewerPalette palette,
        DiffLine line,
        bool showDiffAnnotations,
        float gutterWidth,
        float y,
        float lineHeight,
        float baselineOffset,
        SKPaint lineNumberPaint,
        SKFont font,
        TextFontDescriptor fontDescriptor)
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

            DrawRightAlignedText(canvasSurface, oldText, oldX, y + baselineOffset, font, lineNumberPaint, fontDescriptor);
            DrawRightAlignedText(canvasSurface, newText, newX, y + baselineOffset, font, lineNumberPaint, fontDescriptor);
            if (!string.IsNullOrEmpty(marker))
            {
                canvasSurface.DrawText(marker, markerX, y + baselineOffset, font, markerPaint);
            }

            using var lanePaint = new SKPaint { Color = CodeTextStyleMap.LineAccentColor(annotationKind, palette), Style = SKPaintStyle.Fill, IsAntialias = true };
            if (showDiffAnnotations && line.Kind is DiffLineKind.Added or DiffLineKind.Deleted or DiffLineKind.Modified or DiffLineKind.Moved or DiffLineKind.Conflict or DiffLineKind.Imaginary)
            {
                var laneWidth = line.Kind == DiffLineKind.Imaginary ? 2 : 3;
                canvasSurface.DrawRoundRect(SKRect.Create(gutterWidth - laneWidth - 1, y + 2, laneWidth, lineHeight - 4), laneWidth * 0.5f, laneWidth * 0.5f, lanePaint);
            }
        }
        finally
        {
            markerPaint.Dispose();
        }
    }

    private static void DrawRightAlignedText(SKCanvas canvasSurface, string text, float right, float baseline, SKFont font, SKPaint paint, TextFontDescriptor fontDescriptor)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        canvasSurface.DrawText(text, right - TextMetrics.MeasureNaturalWidth(text, fontDescriptor), baseline, font, paint);
    }

    private void DrawSemanticInsight(
        SKCanvas canvasSurface,
        CodeFileViewerPalette palette,
        SemanticLineInsight insight,
        float gutterWidth,
        float contentRight,
        float y,
        float lineHeight,
        float baselineOffset,
        SKFont font,
        TextFontDescriptor fontDescriptor)
    {
        var isHovered = hoveredSemanticLineNumber == insight.LineNumber;
        var accent = SemanticInsightColor(insight, palette);
        var markerSize = isHovered ? 9 : 7;
        var markerX = gutterWidth - markerSize - 8;
        var markerY = y + (lineHeight - markerSize) * 0.5f;
        using var markerPaint = new SKPaint { Color = accent, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var haloPaint = new SKPaint { Color = WithAlpha(accent, isHovered ? (byte)56 : (byte)28), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvasSurface.DrawCircle(markerX + markerSize * 0.5f, markerY + markerSize * 0.5f, markerSize * 0.9f, haloPaint);
        canvasSurface.DrawRoundRect(SKRect.Create(markerX, markerY, markerSize, markerSize), markerSize * 0.5f, markerSize * 0.5f, markerPaint);

        var label = insight.Label.Length <= 10 ? insight.Label : insight.Label[..10];
        var labelWidth = TextMetrics.MeasureNaturalWidth(label, fontDescriptor) + 10;
        if (contentRight - gutterWidth < labelWidth + 160)
        {
            return;
        }

        var chipRight = contentRight - 14;
        var chipRect = SKRect.Create(chipRight - labelWidth, y + 3, labelWidth, Math.Max(1, lineHeight - 6));
        using var chipPaint = new SKPaint { Color = WithAlpha(accent, isHovered ? (byte)52 : (byte)34), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var chipBorderPaint = new SKPaint { Color = WithAlpha(accent, isHovered ? (byte)210 : (byte)150), Style = SKPaintStyle.Stroke, StrokeWidth = isHovered ? 1.5f : 1, IsAntialias = true };
        using var textPaint = CreateTextPaint(accent);
        canvasSurface.DrawRoundRect(chipRect, 4, 4, chipPaint);
        canvasSurface.DrawRoundRect(chipRect, 4, 4, chipBorderPaint);
        canvasSurface.DrawText(label, chipRect.Left + 5, y + baselineOffset, font, textPaint);
    }

    private void DrawFoldGuide(
        SKCanvas canvasSurface,
        CodeFileViewerPalette palette,
        IReadOnlyList<DiffLine> lines,
        VisibleCodeRow row,
        float textLeft,
        float contentRight,
        float y,
        float lineHeight,
        float charWidth)
    {
        if (row.ActiveRegions.IsDefaultOrEmpty)
        {
            return;
        }

        var firstRegionIndex = Math.Max(0, row.ActiveRegions.Length - FoldGuideMaxDepth);
        for (var index = firstRegionIndex; index < row.ActiveRegions.Length; index++)
        {
            var region = row.ActiveRegions[index];
            var laneX = GetFoldGuideX(lines, region, textLeft, contentRight, charWidth);
            var isHighlighted = hoveredFoldStartLine == region.StartLineIndex;
            var top = row.LineIndex == region.StartLineIndex ? y + lineHeight : y;
            var bottom = row.LineIndex == region.EndLineIndex ? y + lineHeight * 0.5f : y + lineHeight;
            if (bottom <= top)
            {
                continue;
            }

            using var guidePaint = new SKPaint
            {
                Color = isHighlighted ? WithAlpha(palette.Accent, 190) : WithAlpha(palette.FoldMarker, 90),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = isHighlighted ? 1.5f : 1,
                IsAntialias = true
            };
            canvasSurface.DrawLine(laneX, top, laneX, bottom, guidePaint);
        }
    }

    private static float GetFoldGuideX(
        IReadOnlyList<DiffLine> lines,
        CodeFoldRegion region,
        float textLeft,
        float contentRight,
        float charWidth)
    {
        var guide = GetFoldGuidePosition(lines, region);
        var x = guide.CenterOnColumn
            ? textLeft + guide.VisualColumn * charWidth + charWidth * 0.5f
            : GetIndentGuideX(textLeft, guide.VisualColumn, charWidth);
        return Math.Clamp(x, textLeft + 3, Math.Max(textLeft + 3, contentRight - 10));
    }

    private static float GetIndentGuideX(float textLeft, int visualColumn, float charWidth) =>
        visualColumn <= 0
            ? textLeft + 3
            : textLeft + visualColumn * charWidth - charWidth * 0.5f;

    private static FoldGuidePosition GetFoldGuidePosition(IReadOnlyList<DiffLine> lines, CodeFoldRegion region)
    {
        if (region.GuideVisualColumn is { } guideColumn)
        {
            return new FoldGuidePosition(guideColumn, CenterOnColumn: true);
        }

        return new FoldGuidePosition(GetFoldGuideVisualColumn(lines, region), CenterOnColumn: false);
    }

    private static int GetFoldGuideVisualColumn(IReadOnlyList<DiffLine> lines, CodeFoldRegion region)
    {
        var startIndent = GetLeadingWhitespaceVisualColumn(lines, region.StartLineIndex);
        var firstBodyLine = Math.Min(lines.Count - 1, region.EndLineIndex - 1);
        for (var lineIndex = region.StartLineIndex + 1; lineIndex <= firstBodyLine; lineIndex++)
        {
            if (lineIndex < 0 || lineIndex >= lines.Count || string.IsNullOrWhiteSpace(lines[lineIndex].Text))
            {
                continue;
            }

            var candidate = GetLeadingWhitespaceVisualColumn(lines, lineIndex);
            if (candidate > startIndent)
            {
                return candidate;
            }
        }

        return startIndent;
    }

    private static int GetLeadingWhitespaceVisualColumn(IReadOnlyList<DiffLine> lines, int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= lines.Count)
        {
            return 0;
        }

        var text = lines[lineIndex].Text;
        var column = 0;
        foreach (var character in text)
        {
            if (character == ' ')
            {
                column++;
            }
            else if (character == '\t')
            {
                column += CodeTextLayout.TabSize;
            }
            else
            {
                break;
            }
        }

        return column;
    }

    private readonly record struct FoldGuidePosition(int VisualColumn, bool CenterOnColumn);

    private static void DrawFoldGutterGuide(SKCanvas canvasSurface, CodeFileViewerPalette palette, VisibleCodeRow row, float y, float lineHeight)
    {
        if (row.ActiveRegions.IsDefaultOrEmpty)
        {
            return;
        }

        var rect = GetFoldMarkerRect(y, lineHeight);
        var hasStart = row.StartRegion is not null;
        var hasOuterContinuation = row.ActiveRegions.Any(region => region.StartLineIndex < row.LineIndex);
        var hasEnd = row.ActiveRegions.Any(region => region.EndLineIndex == row.LineIndex);
        var top = hasStart && !hasOuterContinuation ? rect.MidY : y;
        var bottom = hasEnd ? y + lineHeight * 0.5f : y + lineHeight;
        if (bottom <= top)
        {
            return;
        }

        using var guidePaint = new SKPaint
        {
            Color = WithAlpha(palette.FoldMarker, hasStart ? (byte)120 : (byte)82),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };
        canvasSurface.DrawLine(rect.MidX, top, rect.MidX, bottom, guidePaint);
    }

    private void DrawFoldMarker(SKCanvas canvasSurface, CodeFileViewerPalette palette, VisibleCodeRow row, float y, float lineHeight)
    {
        if (row.StartRegion is not { } region)
        {
            return;
        }

        var isCollapsed = row.CollapsedRegion is not null || collapsedFoldStarts.Contains(region.StartLineIndex);
        var isHovered = hoveredFoldStartLine == region.StartLineIndex;
        var rect = GetFoldMarkerRect(y, lineHeight);
        using var fillPaint = new SKPaint
        {
            Color = isHovered ? palette.FoldHoverBackground : palette.GutterBackground,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var strokePaint = new SKPaint
        {
            Color = isHovered ? palette.Accent : palette.FoldMarker,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = isHovered ? 1.35f : 1,
            IsAntialias = true
        };
        canvasSurface.DrawRoundRect(rect, 2, 2, fillPaint);
        canvasSurface.DrawRoundRect(rect, 2, 2, strokePaint);
        canvasSurface.DrawLine(rect.Left + 3, rect.MidY, rect.Right - 3, rect.MidY, strokePaint);
        if (isCollapsed)
        {
            canvasSurface.DrawLine(rect.MidX, rect.Top + 3, rect.MidX, rect.Bottom - 3, strokePaint);
        }

        if (isHovered && row.InnermostActiveRegion is not null)
        {
            var connectorX = Math.Min(rect.Right + 4, LeftPadding + FoldGutterWidth - 3);
            using var connectorPaint = new SKPaint
            {
                Color = WithAlpha(palette.Accent, 160),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true
            };
            canvasSurface.DrawLine(rect.Right, rect.MidY, connectorX, rect.MidY, connectorPaint);
        }
    }

    private static SKRect GetFoldMarkerRect(float y, float lineHeight) =>
        SKRect.Create(LeftPadding + 2, y + (lineHeight - FoldMarkerSize) * 0.5f, FoldMarkerSize, FoldMarkerSize);

    private void DrawCodeLine(
        SKCanvas canvasSurface,
        DiffLine line,
        CodeFoldRegion? collapsedRegion,
        float x,
        float contentRight,
        float y,
        float baselineOffset,
        float lineHeight,
        float charWidth,
        SKFont font,
        SKFont boldFont,
        SKPaint defaultPaint,
        SKPaint foldPaint,
        TokenPaintCache tokenPaints,
        TextFontDescriptor boldFontDescriptor,
        CodeFileViewerPalette palette)
    {
        if (line.Kind == DiffLineKind.Imaginary)
        {
            DrawCollapsedContextLine(canvasSurface, line, x, contentRight, y, baselineOffset, lineHeight, boldFont, foldPaint, boldFontDescriptor, palette);
            return;
        }

        DrawTokenizedText(canvasSurface, line.Text, line.Tokens, x, y + baselineOffset, charWidth, font, boldFont, defaultPaint, palette, tokenPaints);
        if (collapsedRegion is null)
        {
            return;
        }

        var visualLength = CodeTextLayout.GetVisualColumn(line.Text, line.Text.Length);
        var chipX = x + visualLength * charWidth + 8;
        DrawCollapsedFoldAdorner(canvasSurface, collapsedRegion, chipX, contentRight, y, baselineOffset, lineHeight, boldFont, foldPaint, boldFontDescriptor, palette);
    }

    private static void DrawCollapsedFoldAdorner(
        SKCanvas canvasSurface,
        CodeFoldRegion collapsedRegion,
        float x,
        float contentRight,
        float y,
        float baselineOffset,
        float lineHeight,
        SKFont boldFont,
        SKPaint foldPaint,
        TextFontDescriptor boldFontDescriptor,
        CodeFileViewerPalette palette)
    {
        if (x >= contentRight - 20)
        {
            return;
        }

        var placeholder = BuildCollapsedFoldPlaceholder(collapsedRegion);
        using var chipPaint = new SKPaint { Color = palette.FoldChipBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var chipBorderPaint = new SKPaint { Color = WithAlpha(palette.FoldMarker, 140), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        var chipWidth = Math.Min(Math.Max(142, TextMetrics.MeasureNaturalWidth(placeholder, boldFontDescriptor) + 14), Math.Max(44, contentRight - x - 8));
        var chipRect = SKRect.Create(x, y + 2, chipWidth, lineHeight - 4);
        canvasSurface.DrawRoundRect(chipRect, 4, 4, chipPaint);
        canvasSurface.DrawRoundRect(chipRect, 4, 4, chipBorderPaint);
        canvasSurface.DrawText(placeholder, chipRect.Left + 7, y + baselineOffset, boldFont, foldPaint);
    }

    private static void DrawCollapsedContextLine(
        SKCanvas canvasSurface,
        DiffLine line,
        float x,
        float contentRight,
        float y,
        float baselineOffset,
        float lineHeight,
        SKFont boldFont,
        SKPaint foldPaint,
        TextFontDescriptor boldFontDescriptor,
        CodeFileViewerPalette palette)
    {
        var label = NormalizeCollapsedContextText(line.Text);
        var labelWidth = TextMetrics.MeasureNaturalWidth(label, boldFontDescriptor) + 18;
        var availableWidth = Math.Max(80, contentRight - x - 8);
        var chipWidth = Math.Min(Math.Max(150, labelWidth), availableWidth);
        var chipX = x + Math.Max(0, (availableWidth - chipWidth) * 0.5f);
        var centerY = y + lineHeight * 0.5f;
        using var linePaint = new SKPaint { Color = WithAlpha(palette.FoldMarker, 100), StrokeWidth = 1, IsAntialias = true };
        using var chipPaint = new SKPaint { Color = palette.FoldChipBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var chipBorderPaint = new SKPaint { Color = WithAlpha(palette.FoldMarker, 135), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        var chipRect = SKRect.Create(chipX, y + 2, chipWidth, lineHeight - 4);
        canvasSurface.DrawLine(x, centerY, chipRect.Left - 8, centerY, linePaint);
        canvasSurface.DrawLine(chipRect.Right + 8, centerY, contentRight - 8, centerY, linePaint);
        canvasSurface.DrawRoundRect(chipRect, 5, 5, chipPaint);
        canvasSurface.DrawRoundRect(chipRect, 5, 5, chipBorderPaint);
        canvasSurface.DrawText(label, chipRect.Left + 9, y + baselineOffset, boldFont, foldPaint);
    }

    private static string BuildCollapsedFoldPlaceholder(CodeFoldRegion collapsedRegion)
    {
        var title = string.IsNullOrWhiteSpace(collapsedRegion.Title)
            ? "fold"
            : TrimMiddle(collapsedRegion.Title.Trim(), 44);
        return $"... {collapsedRegion.CollapsedLineCount:N0} lines hidden: {title}";
    }

    private static string NormalizeCollapsedContextText(string text)
    {
        var value = string.IsNullOrWhiteSpace(text)
            ? "unchanged context"
            : text.Trim().Trim('.').Trim();
        value = value.Replace("collapsed", "hidden", StringComparison.OrdinalIgnoreCase);
        return value.StartsWith("...", StringComparison.Ordinal)
            ? $"{value} ..."
            : $"... {value} ...";
    }

    private static string TrimMiddle(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var prefixLength = Math.Max(1, maxLength / 2 - 2);
        var suffixLength = Math.Max(1, maxLength - prefixLength - 3);
        return $"{value[..prefixLength]}...{value[^suffixLength..]}";
    }

    private string CreateFoldTooltip(CodeFoldRegion region)
    {
        var action = collapsedFoldStarts.Contains(region.StartLineIndex) ? "Expand" : "Collapse";
        var title = string.IsNullOrWhiteSpace(region.Title) ? "folding region" : region.Title.Trim();
        return $"{action} {title} ({region.CollapsedLineCount:N0} hidden lines)";
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
        CodeFileViewerPalette palette,
        TokenPaintCache tokenPaints)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var cursor = 0;
        if (tokens.IsDefault)
        {
            DrawTextRange(canvasSurface, text, 0, text.Length, x, baseline, charWidth, font, defaultPaint);
            return;
        }

        var tokenSource = tokens;
        if (!AreTokensOrdered(tokenSource))
        {
            tokenSource = tokenSource.Sort(static (left, right) => left.StartColumn.CompareTo(right.StartColumn));
        }

        foreach (var token in tokenSource)
        {
            var start = Math.Clamp(token.StartColumn, 0, text.Length);
            var end = Math.Clamp(token.StartColumn + token.Length, start, text.Length);
            if (start > cursor)
            {
                DrawTextRange(canvasSurface, text, cursor, start - cursor, x, baseline, charWidth, font, defaultPaint);
            }

            if (end > start)
            {
                var tokenPaint = tokenPaints.Get(CodeTextStyleMap.TokenColor(token, palette));
                DrawTextRange(canvasSurface, text, start, end - start, x, baseline, charWidth, CodeTextStyleMap.IsBoldToken(token) ? boldFont : font, tokenPaint);
            }

            cursor = Math.Max(cursor, end);
        }

        if (cursor < text.Length)
        {
            DrawTextRange(canvasSurface, text, cursor, text.Length - cursor, x, baseline, charWidth, font, defaultPaint);
        }
    }

    private static bool AreTokensOrdered(ImmutableArray<TokenSpan> tokens)
    {
        var previousStart = -1;
        foreach (var token in tokens)
        {
            if (token.StartColumn < previousStart)
            {
                return false;
            }

            previousStart = token.StartColumn;
        }

        return true;
    }

    private static void DrawTextRange(SKCanvas canvasSurface, string text, int start, int length, float x, float baseline, float charWidth, SKFont font, SKPaint paint)
    {
        if (length <= 0 || start < 0 || start >= text.Length)
        {
            return;
        }

        var safeLength = Math.Min(length, text.Length - start);
        var visualColumn = CodeTextLayout.GetVisualColumn(text, start);
        var value = text.Substring(start, safeLength);
        if (value.IndexOf('\t') >= 0)
        {
            value = value.Replace("\t", TabReplacement, StringComparison.Ordinal);
        }

        canvasSurface.DrawText(value, x + visualColumn * charWidth, baseline, font, paint);
    }

    private static void DrawEmptyState(SKCanvas canvasSurface, float width, float height, SKPaint paint, SKFont font, TextFontDescriptor fontDescriptor)
    {
        const string text = "Full file content unavailable";
        var textWidth = TextMetrics.MeasureNaturalWidth(text, fontDescriptor);
        canvasSurface.DrawText(text, Math.Max(16, (width - textWidth) / 2), Math.Max(32, height / 2), font, paint);
    }

    private void DrawScrollbar(SKCanvas canvasSurface, CodeFileViewerPalette palette, float width, double viewportHeight)
    {
        var contentHeight = GetContentHeight();
        if (contentHeight <= viewportHeight)
        {
            return;
        }

        var track = GetScrollbarTrack(width, viewportHeight);
        var thumb = GetScrollbarThumb(track, viewportHeight, contentHeight);
        using var trackPaint = new SKPaint { Color = palette.ScrollbarTrack, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var thumbPaint = new SKPaint { Color = isDraggingScrollbar ? palette.ScrollbarThumbActive : palette.ScrollbarThumb, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvasSurface.DrawRoundRect(track, ScrollbarWidth / 2, ScrollbarWidth / 2, trackPaint);
        canvasSurface.DrawRoundRect(thumb, ScrollbarWidth / 2, ScrollbarWidth / 2, thumbPaint);
    }

    private void DrawHorizontalScrollbar(
        SKCanvas canvasSurface,
        CodeFileViewerPalette palette,
        float width,
        float height,
        float gutterWidth,
        float contentRight,
        IReadOnlyList<DiffLine> lines)
    {
        var charWidth = CodeCharacterWidth;
        var viewportWidth = GetTextViewportWidth(gutterWidth, contentRight);
        var contentWidth = GetTextContentWidth(lines, charWidth);
        if (contentWidth <= viewportWidth)
        {
            return;
        }

        var track = GetHorizontalScrollbarTrack(width, height, gutterWidth, contentRight);
        if (track.Width <= 0 || track.Height <= 0)
        {
            return;
        }

        var thumb = GetHorizontalScrollbarThumb(track, viewportWidth, contentWidth);
        using var trackPaint = new SKPaint { Color = palette.ScrollbarTrack, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var thumbPaint = new SKPaint { Color = isDraggingHorizontalScrollbar ? palette.ScrollbarThumbActive : palette.ScrollbarThumb, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvasSurface.DrawRoundRect(track, HorizontalScrollbarHeight / 2, HorizontalScrollbarHeight / 2, trackPaint);
        canvasSurface.DrawRoundRect(thumb, HorizontalScrollbarHeight / 2, HorizontalScrollbarHeight / 2, thumbPaint);
    }

    private void DrawMinimap(SKCanvas canvasSurface, CodeFileViewerPalette palette, IReadOnlyList<DiffLine> lines, float width, float height, double viewportHeight)
    {
        EnsureVisibleRows();
        if (visibleRows.Length == 0)
        {
            return;
        }

        var bounds = GetMinimapBounds(width, height);
        var inner = GetMinimapInnerBounds(bounds);
        if (inner.Width <= 0 || inner.Height <= 0)
        {
            return;
        }

        using var backgroundPaint = new SKPaint { Color = palette.MinimapBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var borderPaint = new SKPaint { Color = palette.MinimapBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvasSurface.DrawRoundRect(bounds, 4, 4, backgroundPaint);
        canvasSurface.DrawRoundRect(bounds, 4, 4, borderPaint);

        using (new SKAutoCanvasRestore(canvasSurface, doSave: true))
        {
            canvasSurface.ClipRect(inner);
            using var minimapPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };
            // Detailed rows preserve token colors for normal files; aggregation keeps very large files O(viewport pixels).
            if (visibleRows.Length <= MinimapDetailedRowLimit)
            {
                DrawDetailedMinimapRows(canvasSurface, palette, lines, inner, minimapPaint);
            }
            else
            {
                DrawAggregatedMinimapRows(canvasSurface, palette, lines, inner, minimapPaint);
            }

            DrawMinimapSemanticInsights(canvasSurface, palette, lines, inner, minimapPaint);
            DrawMinimapSelection(canvasSurface, palette, inner, minimapPaint);
        }

        var viewport = GetMinimapViewport(bounds, viewportHeight);
        if (!viewport.IsEmpty)
        {
            using var viewportPaint = new SKPaint { Color = palette.MinimapViewport, Style = SKPaintStyle.Fill, IsAntialias = true };
            using var viewportBorderPaint = new SKPaint
            {
                Color = isDraggingMinimap ? palette.Accent : palette.MinimapViewportBorder,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = isDraggingMinimap ? 1.5f : 1,
                IsAntialias = true
            };
            canvasSurface.DrawRoundRect(viewport, 3, 3, viewportPaint);
            canvasSurface.DrawRoundRect(viewport, 3, 3, viewportBorderPaint);
        }
    }

    private void DrawDetailedMinimapRows(
        SKCanvas canvasSurface,
        CodeFileViewerPalette palette,
        IReadOnlyList<DiffLine> lines,
        SKRect inner,
        SKPaint paint)
    {
        var rowScale = inner.Height / Math.Max(1, visibleRows.Length);
        var rowHeight = GetMinimapRowPaintHeight(rowScale);
        for (var rowIndex = 0; rowIndex < visibleRows.Length; rowIndex++)
        {
            var row = visibleRows[rowIndex];
            if (row.LineIndex < 0 || row.LineIndex >= lines.Count)
            {
                continue;
            }

            var y = inner.Top + rowIndex * rowScale;
            if (y > inner.Bottom)
            {
                break;
            }

            DrawMinimapLine(canvasSurface, palette, lines[row.LineIndex], row.CollapsedRegion, inner, y, rowHeight, paint);
        }
    }

    private void DrawAggregatedMinimapRows(
        SKCanvas canvasSurface,
        CodeFileViewerPalette palette,
        IReadOnlyList<DiffLine> lines,
        SKRect inner,
        SKPaint paint)
    {
        var bucketCount = Math.Max(1, (int)Math.Ceiling(inner.Height));
        var rowsPerBucket = visibleRows.Length / (double)bucketCount;
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var start = Math.Clamp((int)Math.Floor(bucket * rowsPerBucket), 0, visibleRows.Length - 1);
            var end = Math.Clamp((int)Math.Ceiling((bucket + 1) * rowsPerBucket), start + 1, visibleRows.Length);
            var dominantKind = DiffLineKind.Context;
            var hasText = false;
            var isCollapsed = false;

            for (var index = start; index < end; index++)
            {
                var row = visibleRows[index];
                if (row.LineIndex < 0 || row.LineIndex >= lines.Count)
                {
                    continue;
                }

                var line = lines[row.LineIndex];
                hasText |= !string.IsNullOrWhiteSpace(line.Text);
                isCollapsed |= row.CollapsedRegion is not null;
                if (GetMinimapKindPriority(line.Kind) < GetMinimapKindPriority(dominantKind))
                {
                    dominantKind = line.Kind;
                }
            }

            var y = inner.Top + bucket;
            var kind = ShowDiffAnnotations ? dominantKind : DiffLineKind.Context;
            if (kind != DiffLineKind.Context)
            {
                paint.Color = WithAlpha(CodeTextStyleMap.LineAccentColor(kind, palette), 190);
                canvasSurface.DrawRect(SKRect.Create(inner.Left, y, MinimapChangeLaneWidth, 1), paint);
                paint.Color = GetMinimapLineBackground(kind, palette);
                canvasSurface.DrawRect(SKRect.Create(inner.Left + MinimapChangeLaneWidth + 2, y, inner.Width - MinimapChangeLaneWidth - 2, 1), paint);
            }
            else if (hasText)
            {
                paint.Color = isCollapsed ? WithAlpha(palette.FoldText, 120) : palette.MinimapMutedToken;
                canvasSurface.DrawRect(SKRect.Create(inner.Left + MinimapChangeLaneWidth + 4, y, inner.Width * 0.62f, 1), paint);
            }
        }
    }

    private static int GetMinimapKindPriority(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Conflict => 0,
        DiffLineKind.Added or DiffLineKind.Deleted or DiffLineKind.Modified or DiffLineKind.Moved => 1,
        DiffLineKind.Metadata => 2,
        DiffLineKind.Ignored => 3,
        DiffLineKind.Imaginary => 4,
        _ => 10
    };

    private void DrawMinimapLine(
        SKCanvas canvasSurface,
        CodeFileViewerPalette palette,
        DiffLine line,
        CodeFoldRegion? collapsedRegion,
        SKRect inner,
        float y,
        float rowHeight,
        SKPaint paint)
    {
        var kind = ShowDiffAnnotations ? line.Kind : DiffLineKind.Context;
        if (kind != DiffLineKind.Context)
        {
            paint.Color = GetMinimapLineBackground(kind, palette);
            canvasSurface.DrawRect(SKRect.Create(inner.Left, y, inner.Width, rowHeight), paint);
            paint.Color = WithAlpha(CodeTextStyleMap.LineAccentColor(kind, palette), 220);
            canvasSurface.DrawRect(SKRect.Create(inner.Left, y, MinimapChangeLaneWidth, rowHeight), paint);
        }

        if (collapsedRegion is not null)
        {
            paint.Color = WithAlpha(palette.FoldText, 145);
            canvasSurface.DrawRect(SKRect.Create(inner.Right - 5, y, 3, Math.Max(1, rowHeight)), paint);
        }

        if (string.IsNullOrWhiteSpace(line.Text))
        {
            return;
        }

        var textLeft = inner.Left + MinimapChangeLaneWidth + 5;
        var textRight = inner.Right - 2;
        var textWidth = Math.Max(0, textRight - textLeft);
        if (textWidth <= 0)
        {
            return;
        }

        var columnScale = textWidth / MinimapMaxVisualColumns;
        var hasTabs = line.Text.AsSpan().IndexOf('\t') >= 0;
        var tokenHeight = rowHeight > 3
            ? Math.Max(2, rowHeight * 0.42f)
            : Math.Max(1, Math.Min(rowHeight, 2));
        if (line.Tokens.IsDefaultOrEmpty)
        {
            var visualColumns = Math.Min(MinimapMaxVisualColumns, GetMinimapVisualColumn(line.Text, line.Text.Length, hasTabs));
            paint.Color = palette.MinimapToken;
            canvasSurface.DrawRect(SKRect.Create(textLeft, y, Math.Max(1, visualColumns * columnScale), tokenHeight), paint);
            return;
        }

        var drawnSegments = 0;
        foreach (var token in line.Tokens)
        {
            if (drawnSegments >= MinimapMaxTokenSegmentsPerRow)
            {
                break;
            }

            var startColumn = Math.Clamp(token.StartColumn, 0, line.Text.Length);
            var endColumn = Math.Clamp(token.StartColumn + token.Length, startColumn, line.Text.Length);
            if (endColumn <= startColumn)
            {
                continue;
            }

            var startVisual = Math.Min(MinimapMaxVisualColumns, GetMinimapVisualColumn(line.Text, startColumn, hasTabs));
            var endVisual = Math.Min(MinimapMaxVisualColumns, GetMinimapVisualColumn(line.Text, endColumn, hasTabs));
            var rectLeft = textLeft + startVisual * columnScale;
            var rectWidth = Math.Max(1, (endVisual - startVisual) * columnScale);
            if (rectLeft >= textRight)
            {
                break;
            }

            paint.Color = WithAlpha(CodeTextStyleMap.TokenColor(token, palette), 170);
            canvasSurface.DrawRect(SKRect.Create(rectLeft, y, Math.Min(rectWidth, textRight - rectLeft), tokenHeight), paint);
            drawnSegments++;
        }

        if (drawnSegments == 0)
        {
            paint.Color = palette.MinimapToken;
            canvasSurface.DrawRect(SKRect.Create(textLeft, y, Math.Min(textWidth, 24), tokenHeight), paint);
        }
    }

    private static int GetMinimapVisualColumn(string text, int column, bool hasTabs) =>
        hasTabs ? CodeTextLayout.GetVisualColumn(text, column) : Math.Clamp(column, 0, text.Length);

    private static float GetMinimapRowPaintHeight(float rowScale)
    {
        if (rowScale >= 3)
        {
            return rowScale;
        }

        return Math.Clamp(rowScale, 1, 3);
    }

    private void DrawMinimapSemanticInsights(
        SKCanvas canvasSurface,
        CodeFileViewerPalette palette,
        IReadOnlyList<DiffLine> lines,
        SKRect inner,
        SKPaint paint)
    {
        if (!ShowSemanticInsights || semanticLineInsightsByLine.Count == 0 || visibleRows.Length == 0)
        {
            return;
        }

        var rowScale = inner.Height / Math.Max(1, visibleRows.Length);
        var rowHeight = GetMinimapRowPaintHeight(rowScale);
        const float markerWidth = 3;
        for (var rowIndex = 0; rowIndex < visibleRows.Length; rowIndex++)
        {
            var row = visibleRows[rowIndex];
            if (row.LineIndex < 0 || row.LineIndex >= lines.Count)
            {
                continue;
            }

            if (!TryGetSemanticInsight(lines[row.LineIndex], out var insight))
            {
                continue;
            }

            var y = inner.Top + rowIndex * rowScale;
            paint.Color = WithAlpha(SemanticInsightColor(insight, palette), 220);
            canvasSurface.DrawRect(SKRect.Create(inner.Right - markerWidth, y, markerWidth, rowHeight), paint);
        }
    }

    private void DrawMinimapSelection(SKCanvas canvasSurface, CodeFileViewerPalette palette, SKRect inner, SKPaint paint)
    {
        if (!TryGetSelectionRange(out var start, out var end) ||
            CodeTextEditorDocument.Compare(start, end) == 0 ||
            visibleRows.Length == 0)
        {
            return;
        }

        var startVisibleRow = GetVisibleRowIndexForLine(start.RowIndex);
        var endVisibleRow = GetVisibleRowIndexForLine(end.RowIndex);
        var startRatio = Math.Clamp(startVisibleRow / (float)visibleRows.Length, 0, 1);
        var endRatio = Math.Clamp((endVisibleRow + 1) / (float)visibleRows.Length, 0, 1);
        var selectionTop = inner.Top + inner.Height * startRatio;
        var selectionBottom = inner.Top + inner.Height * Math.Max(startRatio, endRatio);
        paint.Color = palette.MinimapSelection;
        canvasSurface.DrawRect(SKRect.Create(inner.Left, selectionTop, inner.Width, Math.Max(2, selectionBottom - selectionTop)), paint);
    }

    private static SKColor GetMinimapLineBackground(DiffLineKind kind, CodeFileViewerPalette palette) => kind switch
    {
        DiffLineKind.Added => WithAlpha(palette.AddedAccent, 36),
        DiffLineKind.Deleted => WithAlpha(palette.DeletedAccent, 36),
        DiffLineKind.Modified => WithAlpha(palette.ModifiedAccent, 36),
        DiffLineKind.Moved => WithAlpha(palette.MovedAccent, 36),
        DiffLineKind.Conflict => WithAlpha(palette.ConflictAccent, 48),
        DiffLineKind.Metadata => WithAlpha(palette.MetadataAccent, 28),
        DiffLineKind.Imaginary => WithAlpha(palette.FoldText, 28),
        _ => SKColors.Transparent
    };

    private static SKColor SemanticInsightColor(SemanticLineInsight insight, CodeFileViewerPalette palette)
    {
        if (insight.Kind == SemanticAnchorKind.Unknown)
        {
            return palette.Invalid;
        }

        if (insight.IsChanged)
        {
            return palette.ModifiedAccent;
        }

        if (insight.IsImpacted)
        {
            return palette.Accent;
        }

        return insight.Kind switch
        {
            SemanticAnchorKind.Type or SemanticAnchorKind.XamlRoot => palette.Type,
            SemanticAnchorKind.Member => palette.Function,
            SemanticAnchorKind.Binding or SemanticAnchorKind.Resource => palette.Property,
            SemanticAnchorKind.XamlName => palette.Tag,
            SemanticAnchorKind.Namespace or SemanticAnchorKind.Project => palette.Keyword,
            _ => palette.Accent
        };
    }

    private bool TryHitTestSemanticMarker(Point2 point, out SemanticLineInsight? insight)
    {
        EnsureVisibleRows();
        insight = null;
        if (!ShowSemanticInsights || semanticLineInsightsByLine.Count == 0)
        {
            return false;
        }

        var lines = GetLines();
        if (visibleRows.Length == 0 || lines.Count == 0)
        {
            return false;
        }

        var charWidth = CodeCharacterWidth;
        var gutterWidth = CalculateGutterWidth(charWidth, lines);
        var rowIndex = (int)Math.Floor((scrollOffsetY + point.Y - TopPadding) / LineHeight);
        if (rowIndex < 0 || rowIndex >= visibleRows.Length)
        {
            return false;
        }

        var row = visibleRows[rowIndex];
        if (row.LineIndex < 0 || row.LineIndex >= lines.Count)
        {
            return false;
        }

        if (point.X < gutterWidth - 24 || point.X > gutterWidth - 2)
        {
            return false;
        }

        if (TryGetSemanticInsight(lines[row.LineIndex], out var foundInsight))
        {
            insight = foundInsight;
            return true;
        }

        return false;
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
        if (row.StartRegion is not { } foundRegion)
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
        var size = GetCanvasSize();
        if (ShouldShowMinimap((float)size.Width, (float)size.Height))
        {
            thumb = SKRect.Empty;
            return false;
        }

        var viewportHeight = GetVerticalViewportHeight();
        var contentHeight = GetContentHeight();
        if (contentHeight <= viewportHeight)
        {
            thumb = SKRect.Empty;
            return false;
        }

        var track = GetScrollbarTrack(size.Width, viewportHeight);
        thumb = GetScrollbarThumb(track, viewportHeight, contentHeight);
        return thumb.Contains((float)point.X, (float)point.Y);
    }

    private bool TryHitTestHorizontalScrollbar(Point2 point, out SKRect thumb)
    {
        return TryHitTestHorizontalScrollbar(point, out thumb, out _);
    }

    private bool TryHitTestHorizontalScrollbar(Point2 point, out SKRect thumb, out bool hitThumb)
    {
        var size = GetCanvasSize();
        var lines = GetLines();
        var charWidth = CodeCharacterWidth;
        var gutterWidth = CalculateGutterWidth(charWidth, lines);
        var contentRight = GetContentRight((float)size.Width, (float)size.Height);
        var viewportWidth = GetTextViewportWidth(gutterWidth, contentRight);
        var contentWidth = GetTextContentWidth(lines, charWidth);
        if (contentWidth <= viewportWidth)
        {
            thumb = SKRect.Empty;
            hitThumb = false;
            return false;
        }

        var track = GetHorizontalScrollbarTrack(size.Width, size.Height, gutterWidth, contentRight);
        if (track.Width <= 0 || track.Height <= 0)
        {
            thumb = SKRect.Empty;
            hitThumb = false;
            return false;
        }

        thumb = GetHorizontalScrollbarThumb(track, viewportWidth, contentWidth);
        hitThumb = thumb.Contains((float)point.X, (float)point.Y);
        return track.Contains((float)point.X, (float)point.Y);
    }

    private bool TryHitTestMinimap(Point2 point, out SKRect bounds, out SKRect viewport)
    {
        var size = GetCanvasSize();
        if (!ShouldShowMinimap((float)size.Width, (float)size.Height))
        {
            bounds = SKRect.Empty;
            viewport = SKRect.Empty;
            return false;
        }

        bounds = GetMinimapBounds((float)size.Width, (float)size.Height);
        viewport = GetMinimapViewport(bounds, GetVerticalViewportHeight());
        return bounds.Contains((float)point.X, (float)point.Y);
    }

    private bool IsPointInRightOverlay(Point2 point)
    {
        var size = GetCanvasSize();
        var width = (float)size.Width;
        var height = (float)size.Height;
        if (ShouldShowMinimap(width, height))
        {
            return GetMinimapBounds(width, height).Contains((float)point.X, (float)point.Y);
        }

        if (TryHitTestHorizontalScrollbar(point, out _))
        {
            return true;
        }

        return GetContentHeight() > GetVerticalViewportHeight() &&
            point.X >= GetScrollbarTrack(size.Width, GetVerticalViewportHeight()).Left - ScrollbarMargin;
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

        var size = GetCanvasSize();
        var contentRight = GetContentRight((float)size.Width, (float)size.Height);
        if (point.X >= contentRight)
        {
            return false;
        }

        var charWidth = CodeCharacterWidth;
        var gutterWidth = CalculateGutterWidth(charWidth, lines);
        var rowIndex = (int)Math.Floor((scrollOffsetY + point.Y - TopPadding) / LineHeight);
        rowIndex = Math.Clamp(rowIndex, 0, visibleRows.Length - 1);

        var row = visibleRows[rowIndex];
        if (row.LineIndex < 0 || row.LineIndex >= lines.Count)
        {
            return false;
        }

        var line = lines[row.LineIndex];
        var column = CodeTextLayout.GetSourceColumnFromVisualOffset(line.Text, (float)(point.X - gutterWidth - TextPadding + scrollOffsetX), charWidth);
        position = new CodeTextPosition(row.LineIndex, column);
        return true;
    }

    private bool TryGetSelectionDragTextPosition(Point2 point, out CodeTextPosition position)
    {
        EnsureVisibleRows();
        position = default;
        var lines = GetLines();
        if (visibleRows.Length == 0 || lines.Count == 0)
        {
            return false;
        }

        var size = GetCanvasSize();
        var charWidth = CodeCharacterWidth;
        var gutterWidth = CalculateGutterWidth(charWidth, lines);
        var contentRight = GetContentRight((float)size.Width, (float)size.Height);
        var viewportTop = TopPadding;
        var viewportBottom = Math.Max(viewportTop, GetVerticalViewportHeight() - BottomPadding);
        var clampedY = Math.Clamp(point.Y, viewportTop, viewportBottom);
        var rowIndex = (int)Math.Floor((scrollOffsetY + clampedY - TopPadding) / LineHeight);
        rowIndex = Math.Clamp(rowIndex, 0, visibleRows.Length - 1);

        var row = visibleRows[rowIndex];
        if (row.LineIndex < 0 || row.LineIndex >= lines.Count)
        {
            return false;
        }

        var line = lines[row.LineIndex];
        var column = point.X >= contentRight
            ? line.Text.Length
            : CodeTextLayout.GetSourceColumnFromVisualOffset(line.Text, (float)(point.X - gutterWidth - TextPadding + scrollOffsetX), charWidth);
        position = new CodeTextPosition(row.LineIndex, column);
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

        var size = GetCanvasSize();
        var contentRight = GetContentRight((float)size.Width, (float)size.Height);
        if (point.X >= contentRight)
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

        var charWidth = CodeCharacterWidth;
        var gutterWidth = CalculateGutterWidth(charWidth, lines);
        line = lines[row.LineIndex];
        var textOffset = (float)(point.X - gutterWidth - TextPadding + scrollOffsetX);
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

    private int GetVisibleRowIndexForLine(int lineIndex)
    {
        EnsureVisibleRows();
        if (visibleRows.Length == 0)
        {
            return 0;
        }

        var nearest = 0;
        for (var rowIndex = 0; rowIndex < visibleRows.Length; rowIndex++)
        {
            var row = visibleRows[rowIndex];
            if (row.LineIndex == lineIndex)
            {
                return rowIndex;
            }

            if (row.LineIndex <= lineIndex)
            {
                nearest = rowIndex;
            }

            if (row.CollapsedRegion is { } collapsed &&
                lineIndex >= collapsed.StartLineIndex &&
                lineIndex <= collapsed.EndLineIndex)
            {
                return rowIndex;
            }
        }

        return nearest;
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

    private IReadOnlyList<DiffLine> GetLines()
    {
        if (!ShouldUseTextDocument)
        {
            return GetBoundLines();
        }

        EnsureEditorDocument();
        if (editableLinesVersion == editorDocument.Version && !editableLines.IsDefault)
        {
            return editableLines;
        }

        var originalLines = GetBoundLines();
        var builder = ImmutableArray.CreateBuilder<DiffLine>(editorDocument.LineCount);
        for (var index = 0; index < editorDocument.LineCount; index++)
        {
            var text = editorDocument.GetLine(index);
            if (index < originalLines.Count && string.Equals(originalLines[index].Text, text, StringComparison.Ordinal))
            {
                builder.Add(originalLines[index] with { Index = index });
            }
            else
            {
                builder.Add(new DiffLine(index, null, index + 1, DiffLineKind.Context, text, ImmutableArray<TokenSpan>.Empty));
            }
        }

        editableLines = builder.ToImmutable();
        editableLines = TokenizeEditableLines(editableLines);
        editableLinesVersion = editorDocument.Version;
        return editableLines;
    }

    private ImmutableArray<DiffLine> TokenizeEditableLines(ImmutableArray<DiffLine> lines)
    {
        if (lines.IsDefaultOrEmpty)
        {
            return lines;
        }

        if (!IsTokenizationEnabled)
        {
            return lines;
        }

        var document = CreateEditableDocument(lines);
        try
        {
            editableTokenizer.ClearCache();
            return editableTokenizer
                .TokenizePageAsync(document, 0, document.LineCount, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            return editableFallbackTokenizer
                .TokenizePageAsync(document, 0, document.LineCount, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
    }

    private DiffDocumentSnapshot CreateEditableDocument(ImmutableArray<DiffLine> lines)
    {
        var path = string.IsNullOrWhiteSpace(CompletionPath) ? "editable" : CompletionPath;
        var language = string.IsNullOrWhiteSpace(CompletionLanguage)
            ? System.IO.Path.GetExtension(path).TrimStart('.')
            : CompletionLanguage;
        var metadata = new DiffDocumentMetadata(
            new DiffDocumentId(path),
            path,
            null,
            DiffFileStatus.Modified,
            language,
            0,
            0);
        return new DiffDocumentSnapshot(metadata.Id, metadata, lines);
    }

    private IReadOnlyList<DiffLine> GetBoundLines() => Lines switch
    {
        IReadOnlyList<DiffLine> list => list,
        IEnumerable<DiffLine> enumerable => enumerable.ToArray(),
        _ => Array.Empty<DiffLine>()
    };

    private void EnsureEditorDocument()
    {
        if (editorInitialized)
        {
            return;
        }

        editorDocument.SetText(Text ?? string.Join('\n', GetBoundLines().Select(line => line.Text)));
        caretPosition = editorDocument.ClampPosition(caretPosition);
        selectionAnchor = selectionAnchor is { } anchor ? editorDocument.ClampPosition(anchor) : caretPosition;
        selectionActive = selectionActive is { } active ? editorDocument.ClampPosition(active) : caretPosition;
        editableLinesVersion = -1;
        visibleRowsDirty = true;
        editorInitialized = true;
    }

    private bool TryGetSemanticInsight(DiffLine line, out SemanticLineInsight insight)
    {
        insight = default!;
        if (!ShowSemanticInsights ||
            semanticLineInsightsByLine.Count == 0 ||
            !semanticLineInsightsByLine.TryGetValue(GetSemanticLineNumber(line), out var foundInsight))
        {
            return false;
        }

        insight = foundInsight;
        return true;
    }

    private static int GetSemanticLineNumber(DiffLine line) =>
        line.NewLineNumber ?? line.OldLineNumber ?? line.Index + 1;

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
        var maxLineNumber = 1;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            maxLineNumber = Math.Max(maxLineNumber, Math.Max(line.OldLineNumber ?? 0, line.NewLineNumber ?? line.Index + 1));
        }

        var digits = Math.Max(3, CountDecimalDigits(maxLineNumber));
        return IsDiffMode
            ? LeftPadding + digits * charWidth * 2 + 62
            : LeftPadding + FoldGutterWidth + digits * charWidth + 18;
    }

    private static int CountDecimalDigits(int value)
    {
        var digits = 1;
        var remaining = value < 0 ? (uint)-(long)value : (uint)value;
        while (remaining >= 10)
        {
            remaining /= 10;
            digits++;
        }

        return digits;
    }

    private double GetContentHeight()
    {
        EnsureVisibleRows();
        return TopPadding + visibleRows.Length * LineHeight + BottomPadding;
    }

    private void ClampScrollOffset()
    {
        var maxOffsetY = Math.Max(0, GetContentHeight() - GetVerticalViewportHeight());
        scrollOffsetY = Math.Clamp(scrollOffsetY, 0, maxOffsetY);
        scrollOffsetX = Math.Clamp(scrollOffsetX, 0, GetMaxHorizontalScrollOffset());
    }

    private double GetMaxHorizontalScrollOffset()
    {
        var size = GetCanvasSize();
        var lines = GetLines();
        var charWidth = CodeCharacterWidth;
        var gutterWidth = CalculateGutterWidth(charWidth, lines);
        var contentRight = GetContentRight((float)size.Width, (float)size.Height);
        var viewportWidth = GetTextViewportWidth(gutterWidth, contentRight);
        var contentWidth = GetTextContentWidth(lines, charWidth);
        return Math.Max(0, contentWidth - viewportWidth);
    }

    private double GetVerticalViewportHeight()
    {
        var size = GetCanvasSize();
        var lines = GetLines();
        var charWidth = CodeCharacterWidth;
        var gutterWidth = CalculateGutterWidth(charWidth, lines);
        var hasHorizontalScrollbar = ShouldShowHorizontalScrollbar((float)size.Width, (float)size.Height, gutterWidth, lines);
        return GetVerticalViewportHeight(size.Height, hasHorizontalScrollbar);
    }

    private static double GetVerticalViewportHeight(double height, bool hasHorizontalScrollbar) =>
        Math.Max(1, height - (hasHorizontalScrollbar ? HorizontalScrollbarHeight + ScrollbarMargin * 2 : 0));

    private bool ShouldShowHorizontalScrollbar(float width, float height, float gutterWidth, IReadOnlyList<DiffLine> lines)
    {
        var viewportWidth = GetTextViewportWidth(gutterWidth, GetContentRight(width, height));
        return GetTextContentWidth(lines, CodeCharacterWidth) > viewportWidth + 0.5;
    }

    private static double GetTextViewportWidth(float gutterWidth, float contentRight) =>
        Math.Max(0, contentRight - gutterWidth - TextPadding);

    private static double GetTextContentWidth(IReadOnlyList<DiffLine> lines, float charWidth)
    {
        if (lines.Count == 0)
        {
            return 0;
        }

        var maxVisualColumn = 0;
        foreach (var line in lines)
        {
            maxVisualColumn = Math.Max(maxVisualColumn, CodeTextLayout.GetVisualColumn(line.Text, line.Text.Length));
        }

        return maxVisualColumn * charWidth + TextPadding;
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

    private static float GetScrollbarThumbHeight(float trackHeight, double viewportHeight, double contentHeight)
    {
        if (trackHeight <= 0)
        {
            return 0;
        }

        var minHeight = Math.Min(32, trackHeight);
        return (float)Math.Clamp(viewportHeight / Math.Max(1, contentHeight) * trackHeight, minHeight, trackHeight);
    }

    private static SKRect GetHorizontalScrollbarTrack(double width, double height, float gutterWidth, float contentRight)
    {
        var left = gutterWidth + ScrollbarMargin;
        var right = Math.Max(left, contentRight - ScrollbarMargin);
        var top = Math.Max(0, (float)(height - HorizontalScrollbarHeight - ScrollbarMargin));
        return SKRect.Create(left, top, right - left, HorizontalScrollbarHeight);
    }

    private SKRect GetHorizontalScrollbarThumb(SKRect track, double viewportWidth, double contentWidth)
    {
        var thumbWidth = GetHorizontalScrollbarThumbWidth(track.Width, viewportWidth, contentWidth);
        var scrollable = Math.Max(1, contentWidth - viewportWidth);
        var trackScrollable = Math.Max(1, track.Width - thumbWidth);
        var left = track.Left + (float)(scrollOffsetX / scrollable * trackScrollable);
        return SKRect.Create(left, track.Top, thumbWidth, track.Height);
    }

    private static float GetHorizontalScrollbarThumbWidth(float trackWidth, double viewportWidth, double contentWidth)
    {
        if (trackWidth <= 0)
        {
            return 0;
        }

        var minWidth = Math.Min(HorizontalScrollbarMinThumbWidth, trackWidth);
        return (float)Math.Clamp(viewportWidth / Math.Max(1, contentWidth) * trackWidth, minWidth, trackWidth);
    }

    private bool ShouldShowMinimap(float width, float height)
    {
        EnsureVisibleRows();
        return width >= MinimapMinimumHostWidth &&
            height >= MinimapMinimumHostHeight &&
            visibleRows.Length >= MinimapMinimumRows;
    }

    private float GetContentRight(float width, float height) =>
        ShouldShowMinimap(width, height)
            ? Math.Max(0, width - MinimapWidth - MinimapMargin * 2)
            : Math.Max(0, width - ScrollbarWidth - ScrollbarMargin);

    private static SKRect GetMinimapBounds(float width, float height) =>
        SKRect.Create(
            Math.Max(0, width - MinimapWidth - MinimapMargin),
            MinimapMargin,
            MinimapWidth,
            Math.Max(0, height - MinimapMargin * 2));

    private static SKRect GetMinimapInnerBounds(SKRect bounds) =>
        SKRect.Create(
            bounds.Left + MinimapPadding,
            bounds.Top + MinimapPadding,
            Math.Max(0, bounds.Width - MinimapPadding * 2),
            Math.Max(0, bounds.Height - MinimapPadding * 2));

    private SKRect GetMinimapViewport(SKRect minimapBounds, double viewportHeight)
    {
        EnsureVisibleRows();
        var inner = GetMinimapInnerBounds(minimapBounds);
        if (inner.Width <= 0 || inner.Height <= 0 || visibleRows.Length == 0)
        {
            return SKRect.Empty;
        }

        var contentHeight = GetContentHeight();
        if (contentHeight <= viewportHeight)
        {
            return inner;
        }

        var scrollable = Math.Max(1, contentHeight - viewportHeight);
        var viewportRatio = Math.Clamp(viewportHeight / Math.Max(1, contentHeight), 0, 1);
        var thumbHeight = (float)Math.Clamp(inner.Height * viewportRatio, MinimapViewportMinHeight, inner.Height);
        var thumbTop = inner.Top + (float)(scrollOffsetY / scrollable * Math.Max(1, inner.Height - thumbHeight));
        thumbTop = Math.Clamp(thumbTop, inner.Top, Math.Max(inner.Top, inner.Bottom - thumbHeight));
        return SKRect.Create(inner.Left, thumbTop, inner.Width, thumbHeight);
    }

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
        GetDeferredRenderScheduler()?.Schedule();
    }

    private UiRenderScheduler? GetDeferredRenderScheduler()
    {
        if (deferredRenderScheduler is not null)
        {
            return deferredRenderScheduler;
        }

        if (DispatcherQueue is null)
        {
            return null;
        }

        deferredRenderScheduler = new UiRenderScheduler(DispatcherQueue, RequestRender);
        return deferredRenderScheduler;
    }

    private static SKPaint CreateTextPaint(SKColor color) => new()
    {
        Color = color,
        IsAntialias = true
    };

    private sealed class TokenPaintCache : IDisposable
    {
        private readonly Dictionary<SKColor, SKPaint> paints = [];

        public SKPaint Get(SKColor color)
        {
            if (!paints.TryGetValue(color, out var paint))
            {
                paint = CreateTextPaint(color);
                paints[color] = paint;
            }

            return paint;
        }

        public void Dispose()
        {
            foreach (var paint in paints.Values)
            {
                paint.Dispose();
            }
        }
    }

    private static SKColor WithAlpha(SKColor color, byte alpha) =>
        new(color.Red, color.Green, color.Blue, alpha);

    private sealed record CodeCompletionSession(CodeCompletionResult Result);
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

public sealed class CodeFileTextEditedEventArgs : EventArgs
{
    public CodeFileTextEditedEventArgs(string text, bool canUndo, bool canRedo)
    {
        Text = text;
        CanUndo = canUndo;
        CanRedo = canRedo;
    }

    public string Text { get; }

    public bool CanUndo { get; }

    public bool CanRedo { get; }
}
