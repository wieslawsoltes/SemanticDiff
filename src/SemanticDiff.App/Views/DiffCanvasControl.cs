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

namespace SemanticDiff.App.Views;

public sealed class DiffCanvasControl : Grid
{
    private enum ActiveInteraction
    {
        None,
        Pan,
        DragNode,
        DragGroup,
        ResizeNode,
        DragScrollbar
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

    private readonly SKXamlCanvas canvas;
    private readonly DiffSceneRenderer renderer = new();
    private readonly InputSystemCursor handCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    private ActiveInteraction activeInteraction;
    private PanPointerButton activePointerButton;
    private uint? activePointerId;
    private DiffNode? activeNode;
    private GraphGroup? activeGroup;
    private DiffNodeResizeHandle activeResizeHandle;
    private Point2 activeNodePointerOffset;
    private double activeScrollbarThumbOffsetY;
    private Point lastPointerPosition;
    private Size2 lastCanvasSize = Size2.Zero;
    private bool fitSceneWhenSizeAvailable;
    private bool fitPendingRequiresDefaultCamera;
    private bool renderRequested;
    private bool renderingFrameSubscribed;

    public DiffCanvasControl()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ManipulationMode = ManipulationModes.None;
        canvas = new SKXamlCanvas
        {
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
        Unloaded += (_, _) => StopRenderingFrameSubscription();
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

    public event EventHandler<DiffCanvasNodeNavigationRequestedEventArgs>? NodeNavigationRequested;

    public event EventHandler<DiffCanvasNodeDiffTabRequestedEventArgs>? NodeDiffTabRequested;

    public event EventHandler<DiffCanvasNodeBlameTabRequestedEventArgs>? NodeBlameTabRequested;

    public event EventHandler<DiffCanvasAnnotationInteractionRequestedEventArgs>? AnnotationInteractionRequested;

    public bool HasSelectedNode => GetSelectedNode() is not null;

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

        NodeNavigationRequested?.Invoke(this, new DiffCanvasNodeNavigationRequestedEventArgs(node.Document.Id.Value));
        return true;
    }

    public bool OpenSelectedNodeBlameTab()
    {
        var node = GetSelectedNode();
        if (node is null)
        {
            return false;
        }

        NodeBlameTabRequested?.Invoke(this, new DiffCanvasNodeBlameTabRequestedEventArgs(node.Document.Id.Value));
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
            ShouldRenderDetailedDocumentBodies() ? DiffSceneRenderMode.Normal : DiffSceneRenderMode.Interactive);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var pointerPoint = args.GetCurrentPoint(this);

        if (Scene is not null && pointerPoint.Properties.IsRightButtonPressed)
        {
            var rightClickPoint = ToCanvasPoint(pointerPoint.Position);
            if (Scene.TryHitTestAnnotation(rightClickPoint, out var annotationHit) && annotationHit is not null)
            {
                Scene.SelectNode(annotationHit.Node);
                RequestRender();
                ShowAnnotationContextMenu(annotationHit, pointerPoint.Position);
                args.Handled = true;
                return;
            }

            var node = Scene.HitTestNode(rightClickPoint);
            if (node is not null)
            {
                Scene.SelectNode(node);
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
        if (Scene.TryHitTestAnnotation(screenPoint, out var hit) && hit is not null)
        {
            Scene.SelectNode(hit.Node);
            RequestRender();
            AnnotationInteractionRequested?.Invoke(this, new DiffCanvasAnnotationInteractionRequestedEventArgs(hit.Annotation));
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
            var worldPoint = Scene.Camera.ScreenToWorld(screenPoint);
            Scene.SelectNode(titleNode);
            activeNode = titleNode;
            activeNodePointerOffset = new Point2(worldPoint.X - titleNode.Bounds.X, worldPoint.Y - titleNode.Bounds.Y);
            BeginInteraction(args, pointerPoint, ActiveInteraction.DragNode, pointerButton);
            RequestRender();
            return;
        }

        var selectedNode = Scene.HitTestNode(screenPoint);
        if (selectedNode is not null)
        {
            Scene.SelectNode(selectedNode);
            RequestRender();
            args.Handled = true;
            return;
        }

        if (Scene.TryHitTestGroup(screenPoint, out var group) && group is not null)
        {
            Scene.SelectNode(null);
            activeGroup = group;
            BeginInteraction(args, pointerPoint, ActiveInteraction.DragGroup, pointerButton);
            RequestRender();
        }
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
            case ActiveInteraction.DragNode when activeNode is not null:
                Scene.MoveNodeTo(activeNode, currentWorldPoint.X - activeNodePointerOffset.X, currentWorldPoint.Y - activeNodePointerOffset.Y);
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
        var screenPoint = ToCanvasPoint(pointerPoint.Position);
        var wheelDelta = pointerPoint.Properties.MouseWheelDelta;
        Scene.HandleWheel(screenPoint, wheelDelta, IsCameraModifierDown(args));

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
        revealItem.Click += (_, _) => NodeNavigationRequested?.Invoke(this, new DiffCanvasNodeNavigationRequestedEventArgs(node.Document.Id.Value));
        menu.Items.Add(revealItem);

        var openDiffItem = new MenuFlyoutItem { Text = "Open diff tab" };
        openDiffItem.Click += (_, _) => NodeDiffTabRequested?.Invoke(this, new DiffCanvasNodeDiffTabRequestedEventArgs(node.Document.Id.Value, showFullFile: false));
        menu.Items.Add(openDiffItem);

        var openFullFileItem = new MenuFlyoutItem { Text = "Open full file tab" };
        openFullFileItem.Click += (_, _) => NodeDiffTabRequested?.Invoke(this, new DiffCanvasNodeDiffTabRequestedEventArgs(node.Document.Id.Value, showFullFile: true));
        menu.Items.Add(openFullFileItem);

        var openBlameItem = new MenuFlyoutItem { Text = "Open blame tab" };
        openBlameItem.Click += (_, _) => NodeBlameTabRequested?.Invoke(this, new DiffCanvasNodeBlameTabRequestedEventArgs(node.Document.Id.Value));
        menu.Items.Add(openBlameItem);

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
        openItem.Click += (_, _) => AnnotationInteractionRequested?.Invoke(this, new DiffCanvasAnnotationInteractionRequestedEventArgs(hit.Annotation));
        menu.Items.Add(openItem);

        var revealItem = new MenuFlyoutItem { Text = "Reveal file in tree" };
        revealItem.Click += (_, _) => NodeNavigationRequested?.Invoke(this, new DiffCanvasNodeNavigationRequestedEventArgs(hit.Node.Document.Id.Value));
        menu.Items.Add(revealItem);

        menu.ShowAt(this, new FlyoutShowOptions { Position = position });
    }

    private DiffNode? GetSelectedNode() => Scene?.Nodes.FirstOrDefault(node => node.IsSelected);

    private void ShowCanvasContextMenu(Point position)
    {
        var menu = new MenuFlyout();
        var fitGraphItem = new MenuFlyoutItem { Text = "Fit graph" };
        fitGraphItem.Click += (_, _) => FitToScene();
        menu.Items.Add(fitGraphItem);
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
        (pointerButton == PanPointerButton.Primary && IsCameraModifierDown(args));

    private static bool IsCameraModifierDown(PointerRoutedEventArgs args) =>
        (args.KeyModifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Windows)) != 0;

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
        activeInteraction = ActiveInteraction.None;
        activePointerButton = PanPointerButton.None;
        activePointerId = null;
        activeNode = null;
        activeGroup = null;
        activeResizeHandle = DiffNodeResizeHandle.None;
        activeNodePointerOffset = Point2.Zero;
        activeScrollbarThumbOffsetY = 0;
        StopInteractiveRenderLoop();
        ReleasePointerCaptures();
        args.Handled = true;
    }

    private void RequestRender()
    {
        if (activeInteraction == ActiveInteraction.None)
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

    private void OnCompositionTargetRendering(object? sender, object args)
    {
        if (renderRequested)
        {
            renderRequested = false;
            canvas.Invalidate();
        }

        if (activeInteraction == ActiveInteraction.None && !renderRequested)
        {
            StopRenderingFrameSubscription();
        }
    }

    private bool ShouldRenderDetailedDocumentBodies() =>
        activeInteraction is ActiveInteraction.None or ActiveInteraction.DragScrollbar;

    private static PanPointerButton GetPanButton(Microsoft.UI.Input.PointerPoint pointerPoint)
    {
        if (pointerPoint.Properties.IsMiddleButtonPressed)
        {
            return PanPointerButton.Middle;
        }

        return pointerPoint.Properties.IsLeftButtonPressed
            ? PanPointerButton.Primary
            : PanPointerButton.None;
    }

    private static bool IsPointerButtonStillPressed(Microsoft.UI.Input.PointerPoint pointerPoint, PanPointerButton panButton) => panButton switch
    {
        PanPointerButton.Middle => pointerPoint.Properties.IsMiddleButtonPressed,
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

public sealed class DiffCanvasAnnotationInteractionRequestedEventArgs : EventArgs
{
    public DiffCanvasAnnotationInteractionRequestedEventArgs(DiffAnnotation annotation)
    {
        Annotation = annotation;
    }

    public DiffAnnotation Annotation { get; }
}
