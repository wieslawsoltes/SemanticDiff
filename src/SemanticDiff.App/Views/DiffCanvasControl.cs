using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private ActiveInteraction activeInteraction;
    private PanPointerButton activePointerButton;
    private uint? activePointerId;
    private DiffNode? activeNode;
    private DiffNodeResizeHandle activeResizeHandle;
    private Point2 activeNodePointerOffset;
    private double activeScrollbarThumbOffsetY;
    private Point lastPointerPosition;
    private Size2 lastCanvasSize = Size2.Zero;
    private bool fitSceneWhenSizeAvailable;
    private bool fitPendingRequiresDefaultCamera;

    public DiffCanvasControl()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
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
        DoubleTapped += OnDoubleTapped;
        SizeChanged += (_, _) =>
        {
            if (TryFitPendingScene())
            {
                canvas.Invalidate();
                return;
            }

            canvas.Invalidate();
        };
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

    public void FitToScene()
    {
        fitPendingRequiresDefaultCamera = false;
        fitSceneWhenSizeAvailable = false;
        if (!TryFitSceneNow())
        {
            fitSceneWhenSizeAvailable = Scene is not null && Scene.Nodes.Count > 0;
        }

        canvas.Invalidate();
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
        canvas.Invalidate();
        return true;
    }

    private static void OnSceneChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is DiffCanvasControl control)
        {
            control.RequestInitialFit((DiffCanvasScene?)args.NewValue);
            control.canvas.Invalidate();
        }
    }

    private static void OnThemeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is DiffCanvasControl control)
        {
            control.canvas.Invalidate();
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

        renderer.Render(args.Surface.Canvas, args.Info.Size, Scene, IsLightTheme ? DiffCanvasColorTheme.Light : DiffCanvasColorTheme.Dark);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var pointerPoint = args.GetCurrentPoint(this);

        if (Scene is not null && pointerPoint.Properties.IsRightButtonPressed)
        {
            var node = Scene.HitTestNode(ToCanvasPoint(pointerPoint.Position));
            if (node is not null)
            {
                Scene.SelectNode(node);
                canvas.Invalidate();
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
            BeginInteraction(args, pointerPoint, ActiveInteraction.Pan, pointerButton);
            return;
        }

        if (Scene is null || pointerButton != PanPointerButton.Primary)
        {
            return;
        }

        var screenPoint = ToCanvasPoint(pointerPoint.Position);
        if (Scene.TryHitTestResizeHandle(screenPoint, out var resizeNode, out var resizeHandle) && resizeNode is not null)
        {
            Scene.SelectNode(resizeNode);
            activeNode = resizeNode;
            activeResizeHandle = resizeHandle;
            BeginInteraction(args, pointerPoint, ActiveInteraction.ResizeNode, pointerButton);
            canvas.Invalidate();
            return;
        }

        if (Scene.TryHitTestFontSizeButton(screenPoint, out var fontNode, out var fontAction) && fontNode is not null)
        {
            Scene.SelectNode(fontNode);
            Scene.AdjustNodeFontSize(fontNode, fontAction);
            canvas.Invalidate();
            args.Handled = true;
            return;
        }

        if (Scene.TryHitTestScrollbarThumb(screenPoint, out var scrollNode, out var thumbGrabOffsetY) && scrollNode is not null)
        {
            Scene.SelectNode(scrollNode);
            activeNode = scrollNode;
            activeScrollbarThumbOffsetY = thumbGrabOffsetY;
            BeginInteraction(args, pointerPoint, ActiveInteraction.DragScrollbar, pointerButton);
            canvas.Invalidate();
            return;
        }

        if (Scene.TryHitTestTitleBar(screenPoint, out var titleNode) && titleNode is not null)
        {
            var worldPoint = Scene.Camera.ScreenToWorld(screenPoint);
            Scene.SelectNode(titleNode);
            activeNode = titleNode;
            activeNodePointerOffset = new Point2(worldPoint.X - titleNode.Bounds.X, worldPoint.Y - titleNode.Bounds.Y);
            BeginInteraction(args, pointerPoint, ActiveInteraction.DragNode, pointerButton);
            canvas.Invalidate();
            return;
        }

        var selectedNode = Scene.HitTestNode(screenPoint);
        if (selectedNode is not null)
        {
            Scene.SelectNode(selectedNode);
            canvas.Invalidate();
            args.Handled = true;
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
            var pointerButton = GetPanButton(pointerPoint);
            if (pointerButton != PanPointerButton.None && ShouldPanCanvas(args, pointerButton))
            {
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
            case ActiveInteraction.ResizeNode when activeNode is not null:
                Scene.ResizeNode(activeNode, activeResizeHandle, currentWorldPoint.X - lastWorldPoint.X, currentWorldPoint.Y - lastWorldPoint.Y);
                break;
            case ActiveInteraction.DragScrollbar when activeNode is not null:
                Scene.DragScrollbarThumb(activeNode, currentWorldPoint.Y, activeScrollbarThumbOffsetY);
                break;
        }

        lastPointerPosition = currentPosition;
        canvas.Invalidate();
        args.Handled = true;
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

        canvas.Invalidate();
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

        canvas.Invalidate();
        args.Handled = true;
    }

    private void ShowNodeContextMenu(DiffNode node, Point position)
    {
        var menu = new MenuFlyout();

        var revealItem = new MenuFlyoutItem { Text = "Reveal in file tree" };
        revealItem.Click += (_, _) => NodeNavigationRequested?.Invoke(this, new DiffCanvasNodeNavigationRequestedEventArgs(node.Document.Id.Value));
        menu.Items.Add(revealItem);

        var fitItem = new MenuFlyoutItem { Text = "Focus node" };
        fitItem.Click += (_, _) =>
        {
            Scene?.FitToNode(node, GetCanvasSize());
            canvas.Invalidate();
        };
        menu.Items.Add(fitItem);

        var pinItem = new MenuFlyoutItem { Text = node.IsPinned ? "Unpin node" : "Pin node" };
        pinItem.Click += (_, _) =>
        {
            Scene?.TogglePinned(node);
            canvas.Invalidate();
        };
        menu.Items.Add(pinItem);

        var decreaseFontItem = new MenuFlyoutItem { Text = "Decrease font size" };
        decreaseFontItem.Click += (_, _) =>
        {
            Scene?.AdjustNodeFontSize(node, DiffNodeFontSizeAction.Decrease);
            canvas.Invalidate();
        };
        menu.Items.Add(decreaseFontItem);

        var increaseFontItem = new MenuFlyoutItem { Text = "Increase font size" };
        increaseFontItem.Click += (_, _) =>
        {
            Scene?.AdjustNodeFontSize(node, DiffNodeFontSizeAction.Increase);
            canvas.Invalidate();
        };
        menu.Items.Add(increaseFontItem);

        menu.ShowAt(this, new FlyoutShowOptions { Position = position });
    }

    private void ShowCanvasContextMenu(Point position)
    {
        var menu = new MenuFlyout();
        var fitGraphItem = new MenuFlyoutItem { Text = "Fit graph" };
        fitGraphItem.Click += (_, _) => FitToScene();
        menu.Items.Add(fitGraphItem);
        menu.ShowAt(this, new FlyoutShowOptions { Position = position });
    }

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
        CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void EndInteraction(PointerRoutedEventArgs args)
    {
        activeInteraction = ActiveInteraction.None;
        activePointerButton = PanPointerButton.None;
        activePointerId = null;
        activeNode = null;
        activeResizeHandle = DiffNodeResizeHandle.None;
        activeNodePointerOffset = Point2.Zero;
        activeScrollbarThumbOffsetY = 0;
        ReleasePointerCaptures();
        args.Handled = true;
    }

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