using System.Windows.Input;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using SemanticDiff.Core;
using SemanticDiff.Rendering;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace SemanticDiff.Controls.Uno;

public sealed class DiffCanvasControl : Grid
{
    private const double DefaultViewportWidth = 960;
    private const double DefaultViewportHeight = 600;
    private const double SelectionMarqueeMinimumScreenSize = 4;
    private const double WheelCanvasPanFactor = 0.6;
    private static readonly TimeSpan WheelZoomSettleDelay = TimeSpan.FromMilliseconds(140);

    private enum ActiveInteraction
    {
        None,
        Pan,
        DragSelection,
        DragGroup,
        ResizeNode,
        DragScrollbar,
        DragViewportHorizontalScrollbar,
        DragViewportVerticalScrollbar,
        MarqueeSelection
    }

    private enum PanPointerButton
    {
        None,
        Primary,
        Middle
    }

    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene),
        typeof(DiffCanvasScene),
        typeof(DiffCanvasControl),
        new PropertyMetadata(null, OnSceneChanged));

    public static readonly DependencyProperty IsLightThemeProperty = DependencyProperty.Register(
        nameof(IsLightTheme),
        typeof(bool),
        typeof(DiffCanvasControl),
        new PropertyMetadata(false, OnThemeChanged));

    public static readonly DependencyProperty UseInteractiveLevelOfDetailProperty = DependencyProperty.Register(
        nameof(UseInteractiveLevelOfDetail),
        typeof(bool),
        typeof(DiffCanvasControl),
        new PropertyMetadata(true, OnRenderOptionChanged));

    private readonly SKXamlCanvas canvas;
    private readonly DiffSceneRenderer renderer = new();
    private readonly InputSystemCursor handCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    private ActiveInteraction activeInteraction;
    private PanPointerButton activePointerButton;
    private uint? activePointerId;
    private DiffNode? activeNode;
    private GraphGroup? activeGroup;
    private DiffNodeResizeHandle activeResizeHandle;
    private double activeScrollbarThumbOffsetY;
    private double activeViewportScrollbarThumbOffset;
    private Point2 marqueeStartCanvasPoint;
    private Point2 marqueeCurrentCanvasPoint;
    private DiffNodeSelectionMode marqueeSelectionMode = DiffNodeSelectionMode.Replace;
    private Point lastPointerPosition;
    private Size2 lastCanvasSize = Size2.Zero;
    private bool fitSceneWhenSizeAvailable;
    private bool fitPendingRequiresDefaultCamera;
    private bool renderRequested;
    private bool renderingFrameSubscribed;
    private bool wheelZoomActive;
    private DispatcherQueueTimer? wheelZoomSettleTimer;

    public DiffCanvasControl()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ManipulationMode = ManipulationModes.None;
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
        KeyDown += OnKeyDown;
        CharacterReceived += OnCharacterReceived;
        DoubleTapped += OnDoubleTapped;
        SizeChanged += (_, _) =>
        {
            if (TryFitPendingScene())
            {
                RequestRender();
                return;
            }

            RequestRender();
        };
        Unloaded += (_, _) => StopAllRenderScheduling();
    }

    public DiffCanvasScene? Scene
    {
        get => (DiffCanvasScene?)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public bool IsLightTheme
    {
        get => (bool)GetValue(IsLightThemeProperty);
        set => SetValue(IsLightThemeProperty, value);
    }

    public bool UseInteractiveLevelOfDetail
    {
        get => (bool)GetValue(UseInteractiveLevelOfDetailProperty);
        set => SetValue(UseInteractiveLevelOfDetailProperty, value);
    }

    public ICommand? RevealNodeCommand { get; set; }

    public ICommand? OpenDiffCommand { get; set; }

    public ICommand? OpenFullFileCommand { get; set; }

    public ICommand? OpenBlameCommand { get; set; }

    public ICommand? OpenSymbolGraphCommand { get; set; }

    public ICommand? AnnotationCommand { get; set; }

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

    public event EventHandler<DiffCanvasNodeNavigationRequestedEventArgs>? NodeNavigationRequested;

    public event EventHandler<DiffCanvasNodeDiffTabRequestedEventArgs>? NodeDiffTabRequested;

    public event EventHandler<DiffCanvasNodeBlameTabRequestedEventArgs>? NodeBlameTabRequested;

    public event EventHandler<DiffCanvasNodeSymbolGraphRequestedEventArgs>? NodeSymbolGraphRequested;

    public event EventHandler<DiffCanvasNodeFullFileViewRequestedEventArgs>? NodeFullFileViewRequested;

    public event EventHandler<DiffCanvasNodeFullFileViewResetRequestedEventArgs>? NodeFullFileViewResetRequested;

    public event EventHandler<DiffCanvasNodeEditingRequestedEventArgs>? NodeEditingRequested;

    public event EventHandler<DiffCanvasNodeEditingResetRequestedEventArgs>? NodeEditingResetRequested;

    public event EventHandler<DiffCanvasAnnotationInteractionRequestedEventArgs>? AnnotationInteractionRequested;

    public bool HasSelectedNode => GetSelectedNode() is not null;

    public void InvalidateScene()
    {
        RequestRender();
    }

    public void FitToScene()
    {
        fitPendingRequiresDefaultCamera = false;
        fitSceneWhenSizeAvailable = false;
        if (!TryFitSceneNow())
        {
            fitSceneWhenSizeAvailable = Scene is not null && Scene.Nodes.Count > 0;
        }

        RequestRender();
    }

    public bool FocusDocument(string documentId, int? lineNumber = null)
    {
        if (Scene is null || string.IsNullOrWhiteSpace(documentId))
        {
            return false;
        }

        var node = Scene.Nodes.FirstOrDefault(node => string.Equals(node.Document.Id.Value, documentId, StringComparison.Ordinal));
        if (node is null)
        {
            return false;
        }

        foreach (var sceneNode in Scene.Nodes)
        {
            sceneNode.IsSelected = false;
        }

        node.IsSelected = true;
        if (lineNumber is > 0)
        {
            node.ScrollToLine(lineNumber.Value);
        }

        Scene.FitToNode(node, GetCanvasSize());
        RequestRender();
        return true;
    }

    public bool RevealSelectedNode()
    {
        var node = GetSelectedNode();
        if (node is null)
        {
            return false;
        }

        RequestRevealNode(node);
        return true;
    }

    public bool OpenSelectedNodeBlameTab()
    {
        var node = GetSelectedNode();
        if (node is null)
        {
            return false;
        }

        RequestBlameTab(node);
        return true;
    }

    public bool OpenSelectedNodeSymbolGraphTab()
    {
        var node = GetSelectedNode();
        if (node is null)
        {
            return false;
        }

        RequestSymbolGraphTab(node);
        return true;
    }

    public bool FocusSelectedNode()
    {
        var node = GetSelectedNode();
        if (node is null || Scene is null)
        {
            return false;
        }

        Scene.FitToNode(node, GetCanvasSize());
        RequestRender();
        return true;
    }

    public bool ToggleSelectedNodePin()
    {
        var node = GetSelectedNode();
        if (node is null || Scene is null)
        {
            return false;
        }

        Scene.TogglePinned(node);
        RequestRender();
        return true;
    }

    public bool AdjustSelectedNodeFontSize(DiffNodeFontSizeAction action)
    {
        var node = GetSelectedNode();
        if (node is null || Scene is null)
        {
            return false;
        }

        Scene.AdjustNodeFontSize(node, action);
        RequestRender();
        return true;
    }

    private static void OnSceneChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is DiffCanvasControl control)
        {
            control.RequestInitialFit((DiffCanvasScene?)args.NewValue);
            control.RequestRender();
        }
    }

    private static void OnThemeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is DiffCanvasControl control)
        {
            control.RequestRender();
        }
    }

    private static void OnRenderOptionChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is DiffCanvasControl control)
        {
            control.RequestRender();
        }
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs args)
    {
        lastCanvasSize = new Size2(args.Info.Width, args.Info.Height);
        TryFitPendingScene();
        if (Scene is null)
        {
            args.Surface.Canvas.Clear(IsLightTheme ? new SKColor(247, 249, 252) : new SKColor(11, 15, 20));
            return;
        }

        renderer.Render(
            args.Surface.Canvas,
            args.Info.Size,
            Scene,
            IsLightTheme ? DiffCanvasColorTheme.Light : DiffCanvasColorTheme.Dark,
            ShouldRenderDetailedDocumentBodies() ? DiffSceneRenderMode.Normal : DiffSceneRenderMode.Interactive,
            UseInteractiveLevelOfDetail);
        DrawViewportScrollbars(args.Surface.Canvas);
        DrawSelectionMarquee(args.Surface.Canvas);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var pointerPoint = args.GetCurrentPoint(this);

        if (Scene is not null && pointerPoint.Properties.IsRightButtonPressed)
        {
            var rightClickPoint = ToCanvasPoint(pointerPoint.Position);
            if (Scene.TryHitTestAnnotation(rightClickPoint, out var annotationHit) && annotationHit is not null)
            {
                if (!annotationHit.Node.IsSelected)
                {
                    Scene.SelectNode(annotationHit.Node);
                }

                RequestRender();
                ShowAnnotationContextMenu(annotationHit, pointerPoint.Position);
                args.Handled = true;
                return;
            }

            var node = Scene.HitTestNode(rightClickPoint);
            if (node is not null)
            {
                if (!node.IsSelected)
                {
                    Scene.SelectNode(node);
                }

                RequestRender();
                ShowNodeContextMenu(node, pointerPoint.Position);
                args.Handled = true;
                return;
            }

            ShowCanvasContextMenu(pointerPoint.Position);
            args.Handled = true;
            return;
        }

        var pointerButton = GetPanButton(pointerPoint);
        if (pointerButton == PanPointerButton.None)
        {
            return;
        }

        if (ShouldPanCanvas(args, pointerButton))
        {
            ClearAnnotationHover();
            BeginInteraction(args, pointerPoint, ActiveInteraction.Pan, pointerButton);
            return;
        }

        if (Scene is null || pointerButton != PanPointerButton.Primary)
        {
            return;
        }

        var screenPoint = ToCanvasPoint(pointerPoint.Position);
        if (TryBeginViewportScrollbarInteraction(args, pointerPoint, screenPoint, pointerButton))
        {
            return;
        }

        var selectionMode = GetSelectionMode(args);
        if (Scene.TryHitTestAnnotation(screenPoint, out var hit) && hit is not null)
        {
            ApplyNodeClickSelection(hit.Node, selectionMode);
            RequestRender();
            RequestAnnotationInteraction(hit.Annotation);
            args.Handled = true;
            return;
        }

        if (Scene.TryHitTestResizeHandle(screenPoint, out var resizeNode, out var resizeHandle) && resizeNode is not null)
        {
            Scene.SelectNode(resizeNode);
            activeNode = resizeNode;
            activeResizeHandle = resizeHandle;
            BeginInteraction(args, pointerPoint, ActiveInteraction.ResizeNode, pointerButton);
            RequestRender();
            return;
        }

        if (Scene.TryHitTestFontSizeButton(screenPoint, out var fontNode, out var fontAction) && fontNode is not null)
        {
            Scene.SelectNode(fontNode);
            Scene.AdjustNodeFontSize(fontNode, fontAction);
            RequestRender();
            args.Handled = true;
            return;
        }

        if (Scene.ToggleFoldAt(screenPoint))
        {
            Focus(FocusState.Programmatic);
            RequestRender();
            args.Handled = true;
            return;
        }

        if (Scene.TryHitTestScrollbarThumb(screenPoint, out var scrollNode, out var thumbGrabOffsetY) && scrollNode is not null)
        {
            Scene.SelectNode(scrollNode);
            activeNode = scrollNode;
            activeScrollbarThumbOffsetY = thumbGrabOffsetY;
            BeginInteraction(args, pointerPoint, ActiveInteraction.DragScrollbar, pointerButton);
            RequestRender();
            return;
        }

        if (Scene.TryHitTestTitleBar(screenPoint, out var titleNode) && titleNode is not null)
        {
            PrepareNodeDragSelection(titleNode, selectionMode);
            BeginInteraction(args, pointerPoint, ActiveInteraction.DragSelection, pointerButton);
            RequestRender();
            return;
        }

        if (Scene.TryFocusEditorAt(screenPoint))
        {
            var editorNode = Scene.HitTestNode(screenPoint);
            if (editorNode is not null)
            {
                Scene.SelectNode(editorNode);
            }

            Focus(FocusState.Programmatic);
            RequestRender();
            args.Handled = true;
            return;
        }

        var selectedNode = Scene.HitTestNode(screenPoint);
        if (selectedNode is not null)
        {
            if (selectionMode == DiffNodeSelectionMode.Toggle && selectedNode.IsSelected)
            {
                ApplyNodeClickSelection(selectedNode, selectionMode);
                RequestRender();
                args.Handled = true;
                return;
            }

            PrepareNodeDragSelection(selectedNode, selectionMode);
            BeginInteraction(args, pointerPoint, ActiveInteraction.DragSelection, pointerButton);
            RequestRender();
            return;
        }

        if (Scene.TryHitTestGroup(screenPoint, out var group) &&
            group is not null)
        {
            Scene.SelectGroupNodes(group, selectionMode);
            activeGroup = group;
            BeginInteraction(args, pointerPoint, ActiveInteraction.DragGroup, pointerButton);
            RequestRender();
            return;
        }

        BeginMarqueeSelection(args, pointerPoint, screenPoint, selectionMode);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (Scene is null)
        {
            return;
        }

        var pointerPoint = args.GetCurrentPoint(this);
        if (activeInteraction == ActiveInteraction.None)
        {
            UpdateAnnotationHover(pointerPoint.Position);
            var pointerButton = GetPanButton(pointerPoint);
            if (pointerButton != PanPointerButton.None && ShouldPanCanvas(args, pointerButton))
            {
                ClearAnnotationHover();
                BeginInteraction(args, pointerPoint, ActiveInteraction.Pan, pointerButton);
            }

            return;
        }

        if (activePointerId != args.Pointer.PointerId)
        {
            return;
        }

        if (!IsPointerButtonStillPressed(pointerPoint, activePointerButton))
        {
            EndInteraction(args);
            return;
        }

        var currentPosition = pointerPoint.Position;
        var currentCanvasPoint = ToCanvasPoint(currentPosition);
        var lastCanvasPoint = ToCanvasPoint(lastPointerPosition);
        var currentWorldPoint = Scene.Camera.ScreenToWorld(currentCanvasPoint);
        var lastWorldPoint = Scene.Camera.ScreenToWorld(lastCanvasPoint);

        switch (activeInteraction)
        {
            case ActiveInteraction.Pan:
                Scene.Pan(currentCanvasPoint.X - lastCanvasPoint.X, currentCanvasPoint.Y - lastCanvasPoint.Y);
                break;
            case ActiveInteraction.DragSelection:
                Scene.MoveSelectedNodes(currentWorldPoint.X - lastWorldPoint.X, currentWorldPoint.Y - lastWorldPoint.Y);
                break;
            case ActiveInteraction.DragGroup when activeGroup is not null:
                activeGroup = Scene.MoveGroup(activeGroup, currentWorldPoint.X - lastWorldPoint.X, currentWorldPoint.Y - lastWorldPoint.Y);
                break;
            case ActiveInteraction.ResizeNode when activeNode is not null:
                Scene.ResizeNode(activeNode, activeResizeHandle, currentWorldPoint.X - lastWorldPoint.X, currentWorldPoint.Y - lastWorldPoint.Y);
                break;
            case ActiveInteraction.DragScrollbar when activeNode is not null:
                Scene.DragScrollbarThumb(activeNode, currentWorldPoint.Y, activeScrollbarThumbOffsetY);
                break;
            case ActiveInteraction.DragViewportHorizontalScrollbar:
                DragViewportScrollbar(CanvasViewportScrollbarOrientation.Horizontal, currentCanvasPoint.X);
                break;
            case ActiveInteraction.DragViewportVerticalScrollbar:
                DragViewportScrollbar(CanvasViewportScrollbarOrientation.Vertical, currentCanvasPoint.Y);
                break;
            case ActiveInteraction.MarqueeSelection:
                marqueeCurrentCanvasPoint = currentCanvasPoint;
                break;
        }

        lastPointerPosition = currentPosition;
        RequestRender();
        args.Handled = true;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        ClearAnnotationHover();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (activePointerId == args.Pointer.PointerId)
        {
            EndInteraction(args);
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (Scene is null)
        {
            return;
        }

        var pointerPoint = args.GetCurrentPoint(this);
        var wheelDelta = pointerPoint.Properties.MouseWheelDelta;
        if (wheelDelta == 0)
        {
            return;
        }

        var screenPoint = ToCanvasPoint(pointerPoint.Position);
        var zoomCanvas = IsCameraModifierDown(args);
        if (zoomCanvas)
        {
            BeginWheelZoomInteraction();
            ClearAnnotationHover();
        }

        if (zoomCanvas)
        {
            Scene.HandleWheel(screenPoint, wheelDelta, zoomCanvas: true);
        }
        else if (IsHorizontalWheel(pointerPoint, args))
        {
            ClearAnnotationHover();
            Scene.Pan(wheelDelta * WheelCanvasPanFactor, 0);
        }
        else
        {
            Scene.HandleWheel(screenPoint, wheelDelta, zoomCanvas: false, panCanvasWhenUnhandled: true);
        }

        RequestRender();
        args.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (Scene is null)
        {
            return;
        }

        var isCommand = IsCommandModifierDown();
        var isShift = IsShiftModifierDown();
        if (TryHandleSelectionShortcut(args.Key, isCommand, isShift))
        {
            RequestRender();
            args.Handled = true;
            return;
        }

        var handled = args.Key switch
        {
            VirtualKey.Left when isCommand => Scene.MoveFocusedEditorCaretWord(-1),
            VirtualKey.Right when isCommand => Scene.MoveFocusedEditorCaretWord(1),
            VirtualKey.Left => Scene.MoveFocusedEditorCaret(0, -1),
            VirtualKey.Right => Scene.MoveFocusedEditorCaret(0, 1),
            VirtualKey.Up => Scene.MoveFocusedEditorCaret(-1, 0),
            VirtualKey.Down => Scene.MoveFocusedEditorCaret(1, 0),
            VirtualKey.Home when isCommand => Scene.MoveFocusedEditorCaretToDocumentStart(),
            VirtualKey.Home => Scene.MoveFocusedEditorCaretToSmartLineStart(),
            VirtualKey.End when isCommand => Scene.MoveFocusedEditorCaretToDocumentEnd(),
            VirtualKey.End => Scene.SetFocusedEditorCaretColumn(int.MaxValue),
            VirtualKey.PageUp => Scene.MoveFocusedEditorCaret(-10, 0),
            VirtualKey.PageDown => Scene.MoveFocusedEditorCaret(10, 0),
            VirtualKey.Back when isCommand => Scene.BackspaceWordInFocusedEditor(),
            VirtualKey.Back => Scene.BackspaceInFocusedEditor(),
            VirtualKey.Delete when isCommand => Scene.DeleteWordInFocusedEditor(),
            VirtualKey.Delete => Scene.DeleteInFocusedEditor(),
            VirtualKey.Enter => Scene.InsertNewLineInFocusedEditor(),
            VirtualKey.Tab when isShift => Scene.OutdentFocusedEditorLine(),
            VirtualKey.Tab => Scene.InsertTextInFocusedEditor("    "),
            VirtualKey.D when isCommand => Scene.DuplicateFocusedEditorLine(),
            VirtualKey.K when isCommand && isShift => Scene.DeleteFocusedEditorLine(),
            _ => false
        };

        if (!handled)
        {
            return;
        }

        RequestRender();
        args.Handled = true;
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (Scene is null || IsCommandModifierDown())
        {
            return;
        }

        var character = args.Character;
        if (character is '\0' or '\b' or '\t' or '\r' or '\n' || char.IsControl(character))
        {
            return;
        }

        if (!Scene.InsertTextInFocusedEditor(character.ToString()))
        {
            return;
        }

        RequestRender();
        args.Handled = true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (Scene is null)
        {
            return;
        }

        var position = args.GetPosition(this);
        var screenPoint = ToCanvasPoint(position);
        var node = Scene.HitTestNode(screenPoint);

        if (node is null)
        {
            Scene.FitToGraph(GetCanvasSize());
        }
        else
        {
            Scene.FitToNode(node, GetCanvasSize());
        }

        RequestRender();
        args.Handled = true;
    }

    private void ShowNodeContextMenu(DiffNode node, Point position)
    {
        var menu = new MenuFlyout();

        var revealItem = new MenuFlyoutItem { Text = "Reveal in file tree" };
        revealItem.Click += (_, _) => RequestRevealNode(node);
        menu.Items.Add(revealItem);

        var openDiffItem = new MenuFlyoutItem { Text = "Open diff tab" };
        openDiffItem.Click += (_, _) => RequestDiffTab(node, showFullFile: false);
        menu.Items.Add(openDiffItem);

        var openFullFileItem = new MenuFlyoutItem { Text = "Open full file tab" };
        openFullFileItem.Click += (_, _) => RequestDiffTab(node, showFullFile: true);
        menu.Items.Add(openFullFileItem);

        var toggleFullInNodeItem = new MenuFlyoutItem
        {
            Text = node.IsShowingFullFile ? "Show diff in node" : "Show full code in node"
        };
        toggleFullInNodeItem.Click += (_, _) => RequestNodeFullFileView(node);
        menu.Items.Add(toggleFullInNodeItem);
        if (node.FullFileViewOverride is not null)
        {
            var resetFullInNodeItem = new MenuFlyoutItem { Text = "Use workspace code mode" };
            resetFullInNodeItem.Click += (_, _) => RequestNodeFullFileViewReset(node);
            menu.Items.Add(resetFullInNodeItem);
        }

        var toggleEditingItem = new MenuFlyoutItem
        {
            Text = node.IsEditingActive ? "Disable node editing" : "Enable node editing"
        };
        toggleEditingItem.Click += (_, _) => RequestNodeEditing(node);
        menu.Items.Add(toggleEditingItem);
        if (node.EditingOverride is not null)
        {
            var resetEditingItem = new MenuFlyoutItem { Text = "Use workspace edit mode" };
            resetEditingItem.Click += (_, _) => RequestNodeEditingReset(node);
            menu.Items.Add(resetEditingItem);
        }

        var openBlameItem = new MenuFlyoutItem { Text = "Open blame tab" };
        openBlameItem.Click += (_, _) => RequestBlameTab(node);
        menu.Items.Add(openBlameItem);

        var openSymbolsItem = new MenuFlyoutItem { Text = "Open semantic map" };
        openSymbolsItem.Click += (_, _) => RequestSymbolGraphTab(node);
        menu.Items.Add(openSymbolsItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var selectConnectedItem = new MenuFlyoutItem { Text = "Select connected nodes" };
        selectConnectedItem.Click += (_, _) =>
        {
            Scene?.SelectConnectedNodes(node, DiffNodeSelectionMode.Replace);
            RequestRender();
        };
        menu.Items.Add(selectConnectedItem);

        var addConnectedItem = new MenuFlyoutItem { Text = "Add connected nodes to selection" };
        addConnectedItem.Click += (_, _) =>
        {
            Scene?.SelectConnectedNodes(node, DiffNodeSelectionMode.Add);
            RequestRender();
        };
        menu.Items.Add(addConnectedItem);

        if (Scene?.FindGroupForNode(node) is { } group)
        {
            var selectGroupItem = new MenuFlyoutItem { Text = $"Select group: {group.SummaryText}" };
            selectGroupItem.Click += (_, _) =>
            {
                Scene?.SelectGroupNodes(group, DiffNodeSelectionMode.Replace);
                RequestRender();
            };
            menu.Items.Add(selectGroupItem);
        }

        var clearSelectionItem = new MenuFlyoutItem { Text = "Clear selection" };
        clearSelectionItem.Click += (_, _) =>
        {
            Scene?.ClearSelection();
            RequestRender();
        };
        menu.Items.Add(clearSelectionItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var fitItem = new MenuFlyoutItem { Text = "Focus node" };
        fitItem.Click += (_, _) =>
        {
            Scene?.FitToNode(node, GetCanvasSize());
            RequestRender();
        };
        menu.Items.Add(fitItem);

        var pinItem = new MenuFlyoutItem { Text = node.IsPinned ? "Unpin node" : "Pin node" };
        pinItem.Click += (_, _) =>
        {
            Scene?.TogglePinned(node);
            RequestRender();
        };
        menu.Items.Add(pinItem);

        var decreaseFontItem = new MenuFlyoutItem { Text = "Decrease font size" };
        decreaseFontItem.Click += (_, _) =>
        {
            Scene?.AdjustNodeFontSize(node, DiffNodeFontSizeAction.Decrease);
            RequestRender();
        };
        menu.Items.Add(decreaseFontItem);

        var increaseFontItem = new MenuFlyoutItem { Text = "Increase font size" };
        increaseFontItem.Click += (_, _) =>
        {
            Scene?.AdjustNodeFontSize(node, DiffNodeFontSizeAction.Increase);
            RequestRender();
        };
        menu.Items.Add(increaseFontItem);

        menu.ShowAt(this, new FlyoutShowOptions { Position = position });
    }

    private void ShowAnnotationContextMenu(DiffAnnotationHit hit, Point position)
    {
        var menu = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = ActionText(hit.Annotation) };
        openItem.Click += (_, _) => RequestAnnotationInteraction(hit.Annotation);
        menu.Items.Add(openItem);

        var revealItem = new MenuFlyoutItem { Text = "Reveal file in tree" };
        revealItem.Click += (_, _) => RequestRevealNode(hit.Node);
        menu.Items.Add(revealItem);

        menu.ShowAt(this, new FlyoutShowOptions { Position = position });
    }

    private void ApplyNodeClickSelection(DiffNode node, DiffNodeSelectionMode selectionMode)
    {
        if (Scene is null)
        {
            return;
        }

        if (selectionMode == DiffNodeSelectionMode.Add)
        {
            Scene.AddNodeToSelection(node);
        }
        else if (selectionMode == DiffNodeSelectionMode.Toggle)
        {
            Scene.ToggleNodeSelection(node);
        }
        else
        {
            Scene.SelectNode(node);
        }
    }

    private void PrepareNodeDragSelection(DiffNode node, DiffNodeSelectionMode selectionMode)
    {
        if (Scene is null)
        {
            return;
        }

        if (selectionMode == DiffNodeSelectionMode.Replace)
        {
            if (!node.IsSelected)
            {
                Scene.SelectNode(node);
            }

            return;
        }

        if (!node.IsSelected)
        {
            Scene.AddNodeToSelection(node);
        }
    }

    private void BeginMarqueeSelection(
        PointerRoutedEventArgs args,
        Microsoft.UI.Input.PointerPoint pointerPoint,
        Point2 screenPoint,
        DiffNodeSelectionMode selectionMode)
    {
        ClearAnnotationHover();
        marqueeStartCanvasPoint = screenPoint;
        marqueeCurrentCanvasPoint = screenPoint;
        marqueeSelectionMode = selectionMode;
        BeginInteraction(args, pointerPoint, ActiveInteraction.MarqueeSelection, PanPointerButton.Primary);
        RequestRender();
    }

    private void CompleteMarqueeSelection(PointerRoutedEventArgs args)
    {
        if (Scene is null)
        {
            return;
        }

        var pointerPoint = args.GetCurrentPoint(this);
        marqueeCurrentCanvasPoint = ToCanvasPoint(pointerPoint.Position);
        var screenBounds = GetMarqueeScreenBounds();
        if (!IsMarqueeLargeEnough(screenBounds))
        {
            if (marqueeSelectionMode == DiffNodeSelectionMode.Replace)
            {
                Scene.ClearSelection();
            }

            return;
        }

        var worldBounds = GetMarqueeWorldBounds(screenBounds);
        Scene.SelectNodesInRect(worldBounds, marqueeSelectionMode);
    }

    private void DrawSelectionMarquee(SKCanvas skCanvas)
    {
        if (activeInteraction != ActiveInteraction.MarqueeSelection)
        {
            return;
        }

        var bounds = GetMarqueeScreenBounds();
        if (!IsMarqueeLargeEnough(bounds))
        {
            return;
        }

        var rect = new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom);
        using var fill = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = IsLightTheme ? new SKColor(0, 120, 212, 36) : new SKColor(79, 156, 249, 42)
        };
        using var dash = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0);
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = IsLightTheme ? new SKColor(0, 120, 212, 210) : new SKColor(79, 156, 249, 230),
            PathEffect = dash
        };
        skCanvas.DrawRect(rect, fill);
        skCanvas.DrawRect(rect, stroke);
    }

    private void DrawViewportScrollbars(SKCanvas skCanvas)
    {
        if (Scene is null)
        {
            return;
        }

        var scrollbars = Scene.GetViewportScrollbars(GetCanvasSize());
        DrawViewportScrollbar(skCanvas, scrollbars.Horizontal);
        DrawViewportScrollbar(skCanvas, scrollbars.Vertical);
    }

    private void DrawViewportScrollbar(SKCanvas skCanvas, CanvasViewportScrollbarMetrics metrics)
    {
        if (!metrics.IsVisible)
        {
            return;
        }

        var isActive = activeInteraction switch
        {
            ActiveInteraction.DragViewportHorizontalScrollbar => metrics.Orientation == CanvasViewportScrollbarOrientation.Horizontal,
            ActiveInteraction.DragViewportVerticalScrollbar => metrics.Orientation == CanvasViewportScrollbarOrientation.Vertical,
            _ => false
        };

        var trackColor = IsLightTheme
            ? new SKColor(74, 104, 140, 38)
            : new SKColor(148, 163, 184, 42);
        var thumbColor = isActive
            ? (IsLightTheme ? new SKColor(0, 120, 212, 230) : new SKColor(96, 165, 250, 235))
            : (IsLightTheme ? new SKColor(83, 105, 130, 168) : new SKColor(148, 163, 184, 168));

        using var trackPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = trackColor
        };
        using var thumbPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = thumbColor
        };

        DrawRoundRect(skCanvas, metrics.TrackBounds, CanvasViewportScrollbarCalculator.Thickness / 2, trackPaint);
        DrawRoundRect(skCanvas, metrics.ThumbBounds, CanvasViewportScrollbarCalculator.Thickness / 2, thumbPaint);
    }

    private static void DrawRoundRect(SKCanvas skCanvas, Rect2 bounds, double radius, SKPaint paint)
    {
        var rect = new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom);
        skCanvas.DrawRoundRect(rect, (float)radius, (float)radius, paint);
    }

    private bool TryBeginViewportScrollbarInteraction(
        PointerRoutedEventArgs args,
        Microsoft.UI.Input.PointerPoint pointerPoint,
        Point2 screenPoint,
        PanPointerButton pointerButton)
    {
        if (Scene is null || pointerButton != PanPointerButton.Primary)
        {
            return false;
        }

        if (TryHitTestViewportScrollbarThumb(screenPoint, out var thumbMetrics, out var thumbGrabOffset))
        {
            activeViewportScrollbarThumbOffset = thumbGrabOffset;
            BeginInteraction(args, pointerPoint, GetViewportScrollbarInteraction(thumbMetrics.Orientation), pointerButton);
            RequestRender();
            return true;
        }

        if (!TryHitTestViewportScrollbarTrack(screenPoint, out var trackMetrics, out var coordinate))
        {
            return false;
        }

        PageViewportScrollbar(trackMetrics, coordinate);
        var nextMetrics = GetViewportScrollbar(trackMetrics.Orientation);
        var thumbLength = GetViewportScrollbarThumbLength(nextMetrics);
        activeViewportScrollbarThumbOffset = thumbLength * 0.5;
        BeginInteraction(args, pointerPoint, GetViewportScrollbarInteraction(trackMetrics.Orientation), pointerButton);
        RequestRender();
        return true;
    }

    private bool TryHitTestViewportScrollbarThumb(
        Point2 screenPoint,
        out CanvasViewportScrollbarMetrics metrics,
        out double thumbGrabOffset)
    {
        metrics = default;
        thumbGrabOffset = 0;

        if (Scene is null)
        {
            return false;
        }

        var scrollbars = Scene.GetViewportScrollbars(GetCanvasSize());
        if (scrollbars.Vertical.IsVisible && scrollbars.Vertical.ThumbBounds.Contains(screenPoint))
        {
            metrics = scrollbars.Vertical;
            thumbGrabOffset = screenPoint.Y - metrics.ThumbBounds.Top;
            return true;
        }

        if (scrollbars.Horizontal.IsVisible && scrollbars.Horizontal.ThumbBounds.Contains(screenPoint))
        {
            metrics = scrollbars.Horizontal;
            thumbGrabOffset = screenPoint.X - metrics.ThumbBounds.Left;
            return true;
        }

        return false;
    }

    private bool TryHitTestViewportScrollbarTrack(
        Point2 screenPoint,
        out CanvasViewportScrollbarMetrics metrics,
        out double coordinate)
    {
        metrics = default;
        coordinate = 0;

        if (Scene is null)
        {
            return false;
        }

        var scrollbars = Scene.GetViewportScrollbars(GetCanvasSize());
        if (scrollbars.Vertical.IsVisible && scrollbars.Vertical.TrackBounds.Contains(screenPoint))
        {
            metrics = scrollbars.Vertical;
            coordinate = screenPoint.Y;
            return true;
        }

        if (scrollbars.Horizontal.IsVisible && scrollbars.Horizontal.TrackBounds.Contains(screenPoint))
        {
            metrics = scrollbars.Horizontal;
            coordinate = screenPoint.X;
            return true;
        }

        return false;
    }

    private void PageViewportScrollbar(CanvasViewportScrollbarMetrics metrics, double coordinate)
    {
        var thumbStart = GetViewportScrollbarThumbStart(metrics);
        var thumbEnd = thumbStart + GetViewportScrollbarThumbLength(metrics);
        var direction = coordinate < thumbStart ? -1 : coordinate > thumbEnd ? 1 : 0;
        if (direction == 0)
        {
            return;
        }

        ScrollViewportToOffset(metrics, metrics.ScrollOffset + direction * metrics.ViewportSize * 0.85);
    }

    private void DragViewportScrollbar(CanvasViewportScrollbarOrientation orientation, double pointerCoordinate)
    {
        if (Scene is null)
        {
            return;
        }

        var metrics = GetViewportScrollbar(orientation);
        if (!metrics.IsVisible)
        {
            return;
        }

        var trackStart = GetViewportScrollbarTrackStart(metrics);
        var trackLength = GetViewportScrollbarTrackLength(metrics);
        var thumbLength = GetViewportScrollbarThumbLength(metrics);
        var travel = Math.Max(0, trackLength - thumbLength);
        if (travel <= 0)
        {
            ScrollViewportToOffset(metrics, 0);
            return;
        }

        var thumbStart = Math.Clamp(
            pointerCoordinate - activeViewportScrollbarThumbOffset,
            trackStart,
            trackStart + travel);
        var ratio = (thumbStart - trackStart) / travel;
        ScrollViewportToOffset(metrics, metrics.MaxScrollOffset * ratio);
    }

    private CanvasViewportScrollbarMetrics GetViewportScrollbar(CanvasViewportScrollbarOrientation orientation)
    {
        if (Scene is null)
        {
            return default;
        }

        var scrollbars = Scene.GetViewportScrollbars(GetCanvasSize());
        return orientation == CanvasViewportScrollbarOrientation.Horizontal
            ? scrollbars.Horizontal
            : scrollbars.Vertical;
    }

    private void ScrollViewportToOffset(CanvasViewportScrollbarMetrics metrics, double scrollOffset)
    {
        if (Scene is null || !metrics.IsVisible)
        {
            return;
        }

        var target = metrics.ContentStart + Math.Clamp(scrollOffset, 0, metrics.MaxScrollOffset);
        if (metrics.Orientation == CanvasViewportScrollbarOrientation.Horizontal)
        {
            Scene.SetViewportWorldOrigin(target, null);
        }
        else
        {
            Scene.SetViewportWorldOrigin(null, target);
        }
    }

    private static ActiveInteraction GetViewportScrollbarInteraction(CanvasViewportScrollbarOrientation orientation) =>
        orientation == CanvasViewportScrollbarOrientation.Horizontal
            ? ActiveInteraction.DragViewportHorizontalScrollbar
            : ActiveInteraction.DragViewportVerticalScrollbar;

    private static double GetViewportScrollbarTrackStart(CanvasViewportScrollbarMetrics metrics) =>
        metrics.Orientation == CanvasViewportScrollbarOrientation.Horizontal
            ? metrics.TrackBounds.Left
            : metrics.TrackBounds.Top;

    private static double GetViewportScrollbarTrackLength(CanvasViewportScrollbarMetrics metrics) =>
        metrics.Orientation == CanvasViewportScrollbarOrientation.Horizontal
            ? metrics.TrackBounds.Width
            : metrics.TrackBounds.Height;

    private static double GetViewportScrollbarThumbStart(CanvasViewportScrollbarMetrics metrics) =>
        metrics.Orientation == CanvasViewportScrollbarOrientation.Horizontal
            ? metrics.ThumbBounds.Left
            : metrics.ThumbBounds.Top;

    private static double GetViewportScrollbarThumbLength(CanvasViewportScrollbarMetrics metrics) =>
        metrics.Orientation == CanvasViewportScrollbarOrientation.Horizontal
            ? metrics.ThumbBounds.Width
            : metrics.ThumbBounds.Height;

    private Rect2 GetMarqueeScreenBounds() => CreateRect(marqueeStartCanvasPoint, marqueeCurrentCanvasPoint);

    private Rect2 GetMarqueeWorldBounds(Rect2 screenBounds)
    {
        if (Scene is null)
        {
            return Rect2.Empty;
        }

        var topLeft = Scene.Camera.ScreenToWorld(new Point2(screenBounds.Left, screenBounds.Top));
        var bottomRight = Scene.Camera.ScreenToWorld(new Point2(screenBounds.Right, screenBounds.Bottom));
        return CreateRect(topLeft, bottomRight);
    }

    private static Rect2 CreateRect(Point2 first, Point2 second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        var right = Math.Max(first.X, second.X);
        var bottom = Math.Max(first.Y, second.Y);
        return new Rect2(left, top, right - left, bottom - top);
    }

    private static bool IsMarqueeLargeEnough(Rect2 bounds) =>
        Math.Max(bounds.Width, bounds.Height) >= SelectionMarqueeMinimumScreenSize;

    private DiffNode? GetSelectedNode() => Scene?.Nodes.FirstOrDefault(node => node.IsSelected);

    private void RequestRevealNode(DiffNode node)
    {
        var documentId = node.Document.Id.Value;
        if (TryExecuteCommand(RevealNodeCommand, documentId))
        {
            return;
        }

        NodeNavigationRequested?.Invoke(this, new DiffCanvasNodeNavigationRequestedEventArgs(documentId));
    }

    private void RequestDiffTab(DiffNode node, bool showFullFile)
    {
        var request = new DiffCanvasNodeDiffTabRequestedEventArgs(node.Document.Id.Value, showFullFile);
        var command = showFullFile ? OpenFullFileCommand : OpenDiffCommand;
        if (TryExecuteCommand(command, request))
        {
            return;
        }

        NodeDiffTabRequested?.Invoke(this, request);
    }

    private void RequestBlameTab(DiffNode node)
    {
        var documentId = node.Document.Id.Value;
        if (TryExecuteCommand(OpenBlameCommand, documentId))
        {
            return;
        }

        NodeBlameTabRequested?.Invoke(this, new DiffCanvasNodeBlameTabRequestedEventArgs(documentId));
    }

    private void RequestSymbolGraphTab(DiffNode node)
    {
        var documentId = node.Document.Id.Value;
        if (TryExecuteCommand(OpenSymbolGraphCommand, documentId))
        {
            return;
        }

        NodeSymbolGraphRequested?.Invoke(this, new DiffCanvasNodeSymbolGraphRequestedEventArgs(documentId));
    }

    private void RequestNodeFullFileView(DiffNode node)
    {
        NodeFullFileViewRequested?.Invoke(this, new DiffCanvasNodeFullFileViewRequestedEventArgs(node.DiffDocument.Id.Value));
    }

    private void RequestNodeFullFileViewReset(DiffNode node)
    {
        NodeFullFileViewResetRequested?.Invoke(this, new DiffCanvasNodeFullFileViewResetRequestedEventArgs(node.DiffDocument.Id.Value));
    }

    private void RequestNodeEditing(DiffNode node)
    {
        NodeEditingRequested?.Invoke(this, new DiffCanvasNodeEditingRequestedEventArgs(node.DiffDocument.Id.Value));
    }

    private void RequestNodeEditingReset(DiffNode node)
    {
        NodeEditingResetRequested?.Invoke(this, new DiffCanvasNodeEditingResetRequestedEventArgs(node.DiffDocument.Id.Value));
    }

    private void RequestAnnotationInteraction(DiffAnnotation annotation)
    {
        if (TryExecuteCommand(AnnotationCommand, annotation))
        {
            return;
        }

        AnnotationInteractionRequested?.Invoke(this, new DiffCanvasAnnotationInteractionRequestedEventArgs(annotation));
    }

    private static bool TryExecuteCommand(ICommand? command, object parameter)
    {
        if (command is null || !command.CanExecute(parameter))
        {
            return false;
        }

        command.Execute(parameter);
        return true;
    }

    private void ShowCanvasContextMenu(Point position)
    {
        var menu = new MenuFlyout();
        var fitGraphItem = new MenuFlyoutItem { Text = "Fit graph" };
        fitGraphItem.Click += (_, _) => FitToScene();
        menu.Items.Add(fitGraphItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var selectAllItem = new MenuFlyoutItem { Text = "Select all nodes" };
        selectAllItem.Click += (_, _) =>
        {
            Scene?.SelectAllNodes();
            RequestRender();
        };
        menu.Items.Add(selectAllItem);

        var invertSelectionItem = new MenuFlyoutItem { Text = "Invert selection" };
        invertSelectionItem.Click += (_, _) =>
        {
            Scene?.InvertNodeSelection();
            RequestRender();
        };
        menu.Items.Add(invertSelectionItem);

        var clearSelectionItem = new MenuFlyoutItem { Text = "Clear selection" };
        clearSelectionItem.Click += (_, _) =>
        {
            Scene?.ClearSelection();
            RequestRender();
        };
        menu.Items.Add(clearSelectionItem);

        menu.ShowAt(this, new FlyoutShowOptions { Position = position });
    }

    private void UpdateAnnotationHover(Point position)
    {
        if (Scene is null)
        {
            return;
        }

        var hit = Scene.TryHitTestAnnotation(ToCanvasPoint(position), out var annotationHit)
            ? annotationHit
            : null;
        if (Scene.SetHoveredAnnotation(hit?.Annotation))
        {
            RequestRender();
        }

        if (hit is null)
        {
            ProtectedCursor = null;
            ToolTipService.SetToolTip(this, null);
            return;
        }

        ProtectedCursor = handCursor;
        ToolTipService.SetToolTip(this, BuildAnnotationToolTip(hit.Annotation));
    }

    private void ClearAnnotationHover()
    {
        if (Scene?.SetHoveredAnnotation(null) == true)
        {
            RequestRender();
        }

        ProtectedCursor = null;
        ToolTipService.SetToolTip(this, null);
    }

    private static string BuildAnnotationToolTip(DiffAnnotation annotation)
    {
        var line = annotation.DisplayLineNumber is int lineNumber ? $" line {lineNumber}" : string.Empty;
        return $"{annotation.Label}{line}\n{annotation.Detail}\n{ActionText(annotation)}";
    }

    private static string ActionText(DiffAnnotation annotation) => annotation.ActionKind switch
    {
        _ when annotation.Kind == DiffAnnotationKind.HistoryBlame => "Open blame tab",
        DiffAnnotationActionKind.ReviewThread => "Open review thread",
        DiffAnnotationActionKind.ChangeNavigation => "Open change target",
        DiffAnnotationActionKind.FocusLine => "Focus annotated line",
        DiffAnnotationActionKind.FocusDocument => "Focus annotated file",
        _ => "Open annotation"
    };

    private Point2 ToCanvasPoint(Point point)
    {
        var scaleX = ActualWidth > 0 ? GetCanvasSize().Width / ActualWidth : 1;
        var scaleY = ActualHeight > 0 ? GetCanvasSize().Height / ActualHeight : 1;
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

    private void RequestInitialFit(DiffCanvasScene? scene)
    {
        fitSceneWhenSizeAvailable = scene is not null && scene.Nodes.Count > 0 && scene.Camera == CameraState.Default;
        fitPendingRequiresDefaultCamera = fitSceneWhenSizeAvailable;
        TryFitPendingScene();
    }

    private bool TryFitPendingScene()
    {
        if (!fitSceneWhenSizeAvailable)
        {
            return false;
        }

        if (fitPendingRequiresDefaultCamera && Scene?.Camera != CameraState.Default)
        {
            fitSceneWhenSizeAvailable = false;
            fitPendingRequiresDefaultCamera = false;
            return false;
        }

        if (!TryFitSceneNow())
        {
            return false;
        }

        fitSceneWhenSizeAvailable = false;
        fitPendingRequiresDefaultCamera = false;
        return true;
    }

    private bool TryFitSceneNow()
    {
        if (Scene is null || Scene.Nodes.Count == 0)
        {
            return false;
        }

        var canvasSize = GetCanvasSize();
        if (canvasSize.Width <= 1 || canvasSize.Height <= 1)
        {
            return false;
        }

        Scene.FitToGraph(canvasSize);
        return true;
    }

    private static bool ShouldPanCanvas(PointerRoutedEventArgs args, PanPointerButton pointerButton) =>
        pointerButton == PanPointerButton.Middle ||
        (pointerButton == PanPointerButton.Primary && IsPanModifierDown(args));

    private static bool IsPanModifierDown(PointerRoutedEventArgs args) =>
        (args.KeyModifiers & VirtualKeyModifiers.Menu) != 0;

    private static bool IsCameraModifierDown(PointerRoutedEventArgs args) =>
        (args.KeyModifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Windows)) != 0;

    private static bool IsHorizontalWheel(Microsoft.UI.Input.PointerPoint pointerPoint, PointerRoutedEventArgs args) =>
        pointerPoint.Properties.IsHorizontalMouseWheel ||
        (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0;

    private static bool IsCommandModifierDown() =>
        IsKeyDown(VirtualKey.Control) ||
        IsKeyDown(VirtualKey.LeftControl) ||
        IsKeyDown(VirtualKey.RightControl) ||
        IsKeyDown(VirtualKey.LeftWindows) ||
        IsKeyDown(VirtualKey.RightWindows);

    private static bool IsShiftModifierDown() =>
        IsKeyDown(VirtualKey.Shift) ||
        IsKeyDown(VirtualKey.LeftShift) ||
        IsKeyDown(VirtualKey.RightShift);

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    private static DiffNodeSelectionMode GetSelectionMode(PointerRoutedEventArgs args)
    {
        if ((args.KeyModifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Windows)) != 0)
        {
            return DiffNodeSelectionMode.Toggle;
        }

        if ((args.KeyModifiers & VirtualKeyModifiers.Shift) != 0)
        {
            return DiffNodeSelectionMode.Add;
        }

        return DiffNodeSelectionMode.Replace;
    }

    private bool TryHandleSelectionShortcut(VirtualKey key, bool isCommand, bool isShift)
    {
        if (Scene is null)
        {
            return false;
        }

        if (key == VirtualKey.Escape)
        {
            Scene.ClearSelectionAndEditorFocus();
            return true;
        }

        if (Scene.HasFocusedEditor)
        {
            return false;
        }

        if (key == VirtualKey.A && isCommand && isShift)
        {
            Scene.ClearSelection();
            return true;
        }

        if (key == VirtualKey.A && isCommand)
        {
            Scene.SelectAllNodes();
            return true;
        }

        if (key == VirtualKey.I && isCommand)
        {
            Scene.InvertNodeSelection();
            return true;
        }

        if (isCommand)
        {
            return false;
        }

        if (Scene.SelectedNodeCount == 0)
        {
            return false;
        }

        var nudge = isShift ? 50 : 10;
        return key switch
        {
            VirtualKey.Left => Scene.MoveSelectedNodes(-nudge, 0),
            VirtualKey.Right => Scene.MoveSelectedNodes(nudge, 0),
            VirtualKey.Up => Scene.MoveSelectedNodes(0, -nudge),
            VirtualKey.Down => Scene.MoveSelectedNodes(0, nudge),
            _ => false
        };
    }

    private void BeginInteraction(PointerRoutedEventArgs args, Microsoft.UI.Input.PointerPoint pointerPoint, ActiveInteraction interaction, PanPointerButton pointerButton)
    {
        activeInteraction = interaction;
        activePointerButton = pointerButton;
        activePointerId = args.Pointer.PointerId;
        lastPointerPosition = pointerPoint.Position;
        StartInteractiveRenderLoop();
        CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void EndInteraction(PointerRoutedEventArgs args)
    {
        if (activeInteraction == ActiveInteraction.MarqueeSelection)
        {
            CompleteMarqueeSelection(args);
        }

        activeInteraction = ActiveInteraction.None;
        activePointerButton = PanPointerButton.None;
        activePointerId = null;
        activeNode = null;
        activeGroup = null;
        activeResizeHandle = DiffNodeResizeHandle.None;
        activeScrollbarThumbOffsetY = 0;
        activeViewportScrollbarThumbOffset = 0;
        marqueeStartCanvasPoint = Point2.Zero;
        marqueeCurrentCanvasPoint = Point2.Zero;
        marqueeSelectionMode = DiffNodeSelectionMode.Replace;
        StopInteractiveRenderLoop();
        ReleasePointerCaptures();
        args.Handled = true;
    }

    private void RequestRender()
    {
        if (activeInteraction == ActiveInteraction.None && !wheelZoomActive)
        {
            renderRequested = false;
            canvas.Invalidate();
            return;
        }

        if (renderRequested)
        {
            return;
        }

        renderRequested = true;
        StartInteractiveRenderLoop();
    }

    private void StartInteractiveRenderLoop()
    {
        if (!renderingFrameSubscribed)
        {
            CompositionTarget.Rendering += OnCompositionTargetRendering;
            renderingFrameSubscribed = true;
        }
    }

    private void StopInteractiveRenderLoop()
    {
        StopRenderingFrameSubscription();
        renderRequested = false;
        canvas.Invalidate();
    }

    private void StopRenderingFrameSubscription()
    {
        if (!renderingFrameSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= OnCompositionTargetRendering;
        renderingFrameSubscribed = false;
    }

    private void StopAllRenderScheduling()
    {
        wheelZoomSettleTimer?.Stop();
        wheelZoomActive = false;
        renderRequested = false;
        StopRenderingFrameSubscription();
    }

    private void OnCompositionTargetRendering(object? sender, object args)
    {
        if (renderRequested)
        {
            renderRequested = false;
            canvas.Invalidate();
        }

        if (activeInteraction == ActiveInteraction.None && !wheelZoomActive && !renderRequested)
        {
            StopRenderingFrameSubscription();
        }
    }

    private void BeginWheelZoomInteraction()
    {
        var timer = GetWheelZoomSettleTimer();
        if (timer is null)
        {
            return;
        }

        wheelZoomActive = true;
        StartInteractiveRenderLoop();
        timer.Stop();
        timer.Start();
    }

    private DispatcherQueueTimer? GetWheelZoomSettleTimer()
    {
        if (wheelZoomSettleTimer is not null)
        {
            return wheelZoomSettleTimer;
        }

        if (DispatcherQueue is null)
        {
            return null;
        }

        wheelZoomSettleTimer = DispatcherQueue.CreateTimer();
        wheelZoomSettleTimer.Interval = WheelZoomSettleDelay;
        wheelZoomSettleTimer.IsRepeating = false;
        wheelZoomSettleTimer.Tick += (_, _) => EndWheelZoomInteraction();
        return wheelZoomSettleTimer;
    }

    private void EndWheelZoomInteraction()
    {
        wheelZoomSettleTimer?.Stop();
        if (!wheelZoomActive)
        {
            return;
        }

        wheelZoomActive = false;
        renderRequested = false;
        if (activeInteraction == ActiveInteraction.None)
        {
            StopRenderingFrameSubscription();
            canvas.Invalidate();
        }
    }

    private bool ShouldRenderDetailedDocumentBodies() =>
        !UseInteractiveLevelOfDetail ||
        (!wheelZoomActive &&
         activeInteraction is
             ActiveInteraction.None or
             ActiveInteraction.DragScrollbar or
             ActiveInteraction.DragViewportHorizontalScrollbar or
             ActiveInteraction.DragViewportVerticalScrollbar);

    private static PanPointerButton GetPanButton(Microsoft.UI.Input.PointerPoint pointerPoint)
    {
        var properties = pointerPoint.Properties;
        if (properties.IsMiddleButtonPressed ||
            properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
        {
            return PanPointerButton.Middle;
        }

        return properties.IsLeftButtonPressed ||
               properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed
            ? PanPointerButton.Primary
            : PanPointerButton.None;
    }

    private static bool IsPointerButtonStillPressed(Microsoft.UI.Input.PointerPoint pointerPoint, PanPointerButton panButton) => panButton switch
    {
        PanPointerButton.Middle => pointerPoint.Properties.IsMiddleButtonPressed ||
                                   pointerPoint.Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonReleased,
        PanPointerButton.Primary => pointerPoint.Properties.IsLeftButtonPressed,
        _ => false
    };
}

public sealed class DiffCanvasNodeNavigationRequestedEventArgs : EventArgs
{
    public DiffCanvasNodeNavigationRequestedEventArgs(string documentId)
    {
        DocumentId = documentId;
    }

    public string DocumentId { get; }
}

public sealed class DiffCanvasNodeDiffTabRequestedEventArgs : EventArgs
{
    public DiffCanvasNodeDiffTabRequestedEventArgs(string documentId, bool showFullFile)
    {
        DocumentId = documentId;
        ShowFullFile = showFullFile;
    }

    public string DocumentId { get; }

    public bool ShowFullFile { get; }
}

public sealed class DiffCanvasNodeBlameTabRequestedEventArgs : EventArgs
{
    public DiffCanvasNodeBlameTabRequestedEventArgs(string documentId)
    {
        DocumentId = documentId;
    }

    public string DocumentId { get; }
}

public sealed class DiffCanvasNodeSymbolGraphRequestedEventArgs : EventArgs
{
    public DiffCanvasNodeSymbolGraphRequestedEventArgs(string documentId)
    {
        DocumentId = documentId;
    }

    public string DocumentId { get; }
}

public sealed class DiffCanvasNodeFullFileViewRequestedEventArgs : EventArgs
{
    public DiffCanvasNodeFullFileViewRequestedEventArgs(string documentId)
    {
        DocumentId = documentId;
    }

    public string DocumentId { get; }
}

public sealed class DiffCanvasNodeFullFileViewResetRequestedEventArgs : EventArgs
{
    public DiffCanvasNodeFullFileViewResetRequestedEventArgs(string documentId)
    {
        DocumentId = documentId;
    }

    public string DocumentId { get; }
}

public sealed class DiffCanvasNodeEditingRequestedEventArgs : EventArgs
{
    public DiffCanvasNodeEditingRequestedEventArgs(string documentId)
    {
        DocumentId = documentId;
    }

    public string DocumentId { get; }
}

public sealed class DiffCanvasNodeEditingResetRequestedEventArgs : EventArgs
{
    public DiffCanvasNodeEditingResetRequestedEventArgs(string documentId)
    {
        DocumentId = documentId;
    }

    public string DocumentId { get; }
}

public sealed class DiffCanvasAnnotationInteractionRequestedEventArgs : EventArgs
{
    public DiffCanvasAnnotationInteractionRequestedEventArgs(DiffAnnotation annotation)
    {
        Annotation = annotation;
    }

    public DiffAnnotation Annotation { get; }
}
