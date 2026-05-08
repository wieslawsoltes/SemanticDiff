using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SemanticDiff.Controls.Uno;
using SemanticDiff.Core;
using SemanticDiff.Layout;
using SemanticDiff.Rendering;
using SemanticDiff.Rendering.Export;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace SemanticDiff.App;

public sealed partial class MainPage : Page
{
    private enum WorkspaceGraphExportFormat
    {
        Svg,
        Png,
        Pdf
    }

    private const double MinimumLeftPaneWidth = 220;
    private const double MinimumCanvasWidth = 480;
    private const double MinimumSettingsFlyoutWidth = 320;
    private const double SettingsFlyoutWidthRatio = 0.38;
    private const double SettingsFlyoutHorizontalMargin = 48;
    private const double SettingsFlyoutVerticalMargin = 96;
    private const double DefaultSymbolFacetsHeight = 220;
    private const double MinimumSymbolFacetsHeight = 140;
    private const double MaximumSymbolFacetsHeight = 420;
    private const string EditorCanvasDragPathPrefix = "semanticdiff-file:";
    private const string EditorCanvasDragFolderPathPrefix = "semanticdiff-folder:";

    private bool isPaneSplitterDragging;
    private bool isPaneSplitterPointerOver;
    private double paneSplitterStartX;
    private double paneSplitterStartWidth;
    private bool isSymbolFacetSplitterDragging;
    private bool isSymbolFacetSplitterPointerOver;
    private bool isWorkspaceSettingsFlyoutOpen;
    private double symbolFacetSplitterStartY;
    private double symbolFacetSplitterStartHeight;
    private ScrollViewer? workspaceTabsScrollViewer;
    private bool suppressNextGitReferenceSelection;
    private readonly IDiffSceneExporter workspaceGraphExporter = new DiffSceneExportService();

    public ViewModels.MainViewModel ViewModel { get; } = new();

    public MainPage()
    {
        this.InitializeComponent();
        RegisterKeyboardAccelerators();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.WorkspaceTabs.CollectionChanged += OnWorkspaceTabsCollectionChanged;
        DiffCanvas.NodeNavigationRequested += OnDiffCanvasNodeNavigationRequested;
        DiffCanvas.NodeDiffTabRequested += OnDiffCanvasNodeDiffTabRequested;
        DiffCanvas.NodeBlameTabRequested += OnDiffCanvasNodeBlameTabRequested;
        DiffCanvas.NodeSymbolGraphRequested += OnDiffCanvasNodeSymbolGraphRequested;
        DiffCanvas.NodeFullFileViewRequested += OnDiffCanvasNodeFullFileViewRequested;
        DiffCanvas.NodeFullFileViewResetRequested += OnDiffCanvasNodeFullFileViewResetRequested;
        DiffCanvas.NodeEditingRequested += OnDiffCanvasNodeEditingRequested;
        DiffCanvas.NodeEditingResetRequested += OnDiffCanvasNodeEditingResetRequested;
        DiffCanvas.AnnotationInteractionRequested += OnDiffCanvasAnnotationInteractionRequested;
        ApplyRequestedTheme();
        ApplyLeftPaneWidth(ViewModel.LeftPaneWidth);
        RootShellGrid.SizeChanged += OnRootShellGridSizeChanged;
        Unloaded += OnUnloaded;
    }

    private void RegisterKeyboardAccelerators()
    {
        const VirtualKeyModifiers control = VirtualKeyModifiers.Control;
        const VirtualKeyModifiers controlShift = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift;
        const VirtualKeyModifiers controlAlt = VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu;

        RegisterAccelerator(VirtualKey.O, control, OpenRepositoryFromPickerAsync);
        RegisterAccelerator(VirtualKey.R, control, () => RunSyncCommand(ViewModel.ReloadRepository));
        RegisterAccelerator(VirtualKey.L, control, RelayoutAndFitActiveWorkspaceAsync);
        RegisterAccelerator(VirtualKey.Number0, control, () => RunSyncCommand(FitActiveWorkspaceCanvas));
        RegisterAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, () => RunSyncCommand(ViewModel.CancelCurrentOperation), () => ViewModel.IsBusy, includePlatformCommandVariant: false);
        RegisterAccelerator(VirtualKey.F7, VirtualKeyModifiers.None, () => RunSyncCommand(() => FocusActiveCanvas(ViewModel.FocusPreviousChange())), () => ViewModel.HasNavigableChanges, includePlatformCommandVariant: false);
        RegisterAccelerator(VirtualKey.F8, VirtualKeyModifiers.None, () => RunSyncCommand(() => FocusActiveCanvas(ViewModel.FocusNextChange())), () => ViewModel.HasNavigableChanges, includePlatformCommandVariant: false);

        RegisterAccelerator(VirtualKey.Number1, VirtualKeyModifiers.Menu, () => SelectRailTabAsync(0), includePlatformCommandVariant: false);
        RegisterAccelerator(VirtualKey.Number2, VirtualKeyModifiers.Menu, () => SelectRailTabAsync(1), includePlatformCommandVariant: false);
        RegisterAccelerator(VirtualKey.Number3, VirtualKeyModifiers.Menu, () => SelectRailTabAsync(2), includePlatformCommandVariant: false);
        RegisterAccelerator(VirtualKey.Number4, VirtualKeyModifiers.Menu, () => SelectRailTabAsync(3), includePlatformCommandVariant: false);
        RegisterAccelerator(VirtualKey.Number5, VirtualKeyModifiers.Menu, () => SelectRailTabAsync(4), includePlatformCommandVariant: false);
        RegisterAccelerator(VirtualKey.F, control, () => FocusTextBoxAsync(FileSearchBox));
        RegisterAccelerator(VirtualKey.F, controlShift, () => FocusTextBoxAsync(SymbolSearchBox));
        RegisterAccelerator(VirtualKey.B, control, FocusGitReferenceSearchAsync);
        RegisterAccelerator(VirtualKey.P, controlShift, FocusGitReferenceSearchAsync);
        RegisterAccelerator(VirtualKey.Y, controlShift, FocusReviewSearchAsync);
        RegisterAccelerator(VirtualKey.Enter, control, ViewModel.ApplyReferenceOptionsAsync);

        RegisterAccelerator(VirtualKey.Number1, controlAlt, () => ViewModel.SetDiffScopeAsync(GitDiffScope.Worktree));
        RegisterAccelerator(VirtualKey.Number2, controlAlt, () => ViewModel.SetDiffScopeAsync(GitDiffScope.Unstaged));
        RegisterAccelerator(VirtualKey.Number3, controlAlt, () => ViewModel.SetDiffScopeAsync(GitDiffScope.Staged));
        RegisterAccelerator(VirtualKey.Number4, controlAlt, () => ViewModel.SetDiffScopeAsync(GitDiffScope.Branch));
        RegisterAccelerator(VirtualKey.Number5, controlAlt, () => ViewModel.SetDiffScopeAsync(GitDiffScope.CommitRange));
        RegisterAccelerator(VirtualKey.Number6, controlAlt, () => ViewModel.SetDiffContextModeAsync(DiffContextMode.ChangedHunks));
        RegisterAccelerator(VirtualKey.Number7, controlAlt, () => ViewModel.SetDiffContextModeAsync(DiffContextMode.FullFileDiff));
        RegisterAccelerator(VirtualKey.Number8, controlAlt, () => ViewModel.SetDiffContextModeAsync(DiffContextMode.CurrentFile));

        RegisterAccelerator(VirtualKey.A, controlAlt, () => ViewModel.SetAutoRefreshAsync(!ViewModel.IsAutoRefreshEnabled));
        RegisterAccelerator(VirtualKey.M, controlAlt, () => ViewModel.SetSemanticAnalysisModeAsync(SemanticAnalysisMode.WorkspaceThenSyntax));
        RegisterAccelerator(VirtualKey.F, controlAlt, () => ViewModel.SetSemanticAnalysisModeAsync(SemanticAnalysisMode.FastSyntaxOnly));
        RegisterAccelerator(VirtualKey.N, controlAlt, () => ViewModel.SetReviewModeAsync(!ViewModel.IsNoiseFilterEnabled));
        RegisterAccelerator(VirtualKey.C, controlAlt, () => ViewModel.SetContextFoldingAsync(!ViewModel.IsContextFoldingEnabled));
        RegisterAccelerator(VirtualKey.T, controlAlt, ToggleThemeAsync);
        RegisterAccelerator(VirtualKey.E, controlAlt, () => ViewModel.SetSemanticEdgesAsync(!ViewModel.IsSemanticEdgesEnabled));

        RegisterAccelerator(VirtualKey.G, controlAlt, () => ToggleVisualizationLayerAsync("Git", ViewModel.IsGitVisualizationEnabled));
        RegisterAccelerator(VirtualKey.S, controlAlt, () => ToggleVisualizationLayerAsync("Semantic", ViewModel.IsSemanticVisualizationEnabled));
        RegisterAccelerator(VirtualKey.D, controlAlt, () => ToggleVisualizationLayerAsync("Diagnostics", ViewModel.IsDiagnosticVisualizationEnabled));
        RegisterAccelerator(VirtualKey.R, controlAlt, () => ToggleVisualizationLayerAsync("Review", ViewModel.IsReviewVisualizationEnabled));
        RegisterAccelerator(VirtualKey.Q, controlAlt, () => ToggleVisualizationLayerAsync("ReviewComments", ViewModel.IsReviewCommentVisualizationEnabled));
        RegisterAccelerator(VirtualKey.H, controlAlt, () => ToggleVisualizationLayerAsync("History", ViewModel.IsHistoryVisualizationEnabled));
        RegisterAccelerator(VirtualKey.V, controlAlt, () => ToggleVisualizationLayerAsync("Navigation", ViewModel.IsNavigationVisualizationEnabled));
        RegisterAccelerator(VirtualKey.X, controlAlt, () => ToggleVisualizationLayerAsync("Context", ViewModel.IsContextVisualizationEnabled));

        RegisterAccelerator(VirtualKey.S, controlShift, ViewModel.StageSelectedFileAsync, () => ViewModel.HasSelectedRepositoryFile);
        RegisterAccelerator(VirtualKey.U, controlShift, ViewModel.UnstageSelectedFileAsync, () => ViewModel.HasSelectedRepositoryFile);

        RegisterAccelerator(VirtualKey.Number1, controlShift, () => SetLayoutModeFromAcceleratorAsync(GraphLayoutMode.Auto));
        RegisterAccelerator(VirtualKey.Number2, controlShift, () => SetLayoutModeFromAcceleratorAsync(GraphLayoutMode.Layered));
        RegisterAccelerator(VirtualKey.Number3, controlShift, () => SetLayoutModeFromAcceleratorAsync(GraphLayoutMode.Grid));
        RegisterAccelerator(VirtualKey.Number4, controlShift, () => SetLayoutModeFromAcceleratorAsync(GraphLayoutMode.CompactGrid));
        RegisterAccelerator(VirtualKey.Number5, controlShift, () => SetLayoutModeFromAcceleratorAsync(GraphLayoutMode.StatusLanes));
        RegisterAccelerator(VirtualKey.Number6, controlShift, () => SetGroupingModeFromAcceleratorAsync(GraphGroupingMode.None));
        RegisterAccelerator(VirtualKey.Number7, controlShift, () => SetGroupingModeFromAcceleratorAsync(GraphGroupingMode.Folder));
        RegisterAccelerator(VirtualKey.Number8, controlShift, () => SetGroupingModeFromAcceleratorAsync(GraphGroupingMode.Semantic));
        RegisterAccelerator(VirtualKey.Number9, controlShift, () => SetGroupingModeFromAcceleratorAsync(GraphGroupingMode.Language));
        RegisterAccelerator(VirtualKey.Number0, controlShift, () => SetGroupingModeFromAcceleratorAsync(GraphGroupingMode.Status));

        RegisterAccelerator(VirtualKey.R, controlShift, () => RunCanvasNodeCommand(canvas => canvas.RevealSelectedNode(), "Select a node before revealing it"));
        RegisterAccelerator(VirtualKey.B, controlShift, () => RunCanvasNodeCommand(canvas => canvas.OpenSelectedNodeBlameTab(), "Select a node before opening blame"));
        RegisterAccelerator(VirtualKey.N, controlShift, () => RunCanvasNodeCommand(canvas => canvas.FocusSelectedNode(), "Select a node before focusing it"));
        RegisterAccelerator(VirtualKey.P, controlAlt, () => RunCanvasNodeCommand(canvas => canvas.ToggleSelectedNodePin(), "Select a node before pinning it"));
        RegisterAccelerator(VirtualKey.Add, control, () => RunCanvasNodeCommand(canvas => canvas.AdjustSelectedNodeFontSize(DiffNodeFontSizeAction.Increase), "Select a node before changing font size"));
        RegisterAccelerator(VirtualKey.Subtract, control, () => RunCanvasNodeCommand(canvas => canvas.AdjustSelectedNodeFontSize(DiffNodeFontSizeAction.Decrease), "Select a node before changing font size"));
    }

    private void RegisterAccelerator(
        VirtualKey key,
        VirtualKeyModifiers modifiers,
        Func<Task> command,
        Func<bool>? canExecute = null,
        bool includePlatformCommandVariant = true)
    {
        AddKeyboardAccelerator(key, modifiers, command, canExecute);
        if (includePlatformCommandVariant && modifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            var commandModifiers = modifiers & ~VirtualKeyModifiers.Control;
            commandModifiers |= VirtualKeyModifiers.Windows;
            AddKeyboardAccelerator(key, commandModifiers, command, canExecute);
        }
    }

    private void AddKeyboardAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Func<Task> command, Func<bool>? canExecute)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers
        };
        accelerator.Invoked += async (_, args) =>
        {
            if (canExecute is not null && !canExecute())
            {
                return;
            }

            args.Handled = true;
            try
            {
                await command();
            }
            catch (Exception exception)
            {
                ViewModel.ReportInteractionError(exception.Message);
            }
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private static Task RunSyncCommand(Action command)
    {
        command();
        return Task.CompletedTask;
    }

    private async void OnUnloaded(object sender, RoutedEventArgs args)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.WorkspaceTabs.CollectionChanged -= OnWorkspaceTabsCollectionChanged;
        DiffCanvas.NodeNavigationRequested -= OnDiffCanvasNodeNavigationRequested;
        DiffCanvas.NodeDiffTabRequested -= OnDiffCanvasNodeDiffTabRequested;
        DiffCanvas.NodeBlameTabRequested -= OnDiffCanvasNodeBlameTabRequested;
        DiffCanvas.NodeSymbolGraphRequested -= OnDiffCanvasNodeSymbolGraphRequested;
        DiffCanvas.NodeFullFileViewRequested -= OnDiffCanvasNodeFullFileViewRequested;
        DiffCanvas.NodeFullFileViewResetRequested -= OnDiffCanvasNodeFullFileViewResetRequested;
        DiffCanvas.NodeEditingRequested -= OnDiffCanvasNodeEditingRequested;
        DiffCanvas.NodeEditingResetRequested -= OnDiffCanvasNodeEditingResetRequested;
        DiffCanvas.AnnotationInteractionRequested -= OnDiffCanvasAnnotationInteractionRequested;
        RootShellGrid.SizeChanged -= OnRootShellGridSizeChanged;
        if (workspaceTabsScrollViewer is not null)
        {
            workspaceTabsScrollViewer.ViewChanged -= OnWorkspaceTabsScrollViewerViewChanged;
        }

        await ViewModel.DisposeAsync();
    }

    private void OnWorkspaceTabListLoaded(object sender, RoutedEventArgs args)
    {
        EnsureWorkspaceTabsScrollViewer();
        QueueWorkspaceTabsScrollStateUpdate(scrollSelectedIntoView: true);
    }

    private void OnWorkspaceTabListSizeChanged(object sender, SizeChangedEventArgs args)
    {
        QueueWorkspaceTabsScrollStateUpdate();
    }

    private void OnWorkspaceTabListSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        QueueWorkspaceTabsScrollStateUpdate(scrollSelectedIntoView: true);
    }

    private void OnWorkspaceTabListPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        var scrollViewer = EnsureWorkspaceTabsScrollViewer();
        if (scrollViewer is null || scrollViewer.ScrollableWidth <= 0.5)
        {
            return;
        }

        var delta = args.GetCurrentPoint(WorkspaceTabList).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var nextOffset = Math.Clamp(scrollViewer.HorizontalOffset - delta, 0, scrollViewer.ScrollableWidth);
        if (Math.Abs(nextOffset - scrollViewer.HorizontalOffset) <= 0.5)
        {
            return;
        }

        scrollViewer.ChangeView(nextOffset, null, null, disableAnimation: true);
        UpdateWorkspaceTabsScrollButtons();
        args.Handled = true;
    }

    private void OnWorkspaceTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        QueueWorkspaceTabsScrollStateUpdate(scrollSelectedIntoView: true);
    }

    private void OnWorkspaceTabsScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        UpdateWorkspaceTabsScrollButtons();
    }

    private void OnWorkspaceTabsScrollLeftClicked(object sender, RoutedEventArgs args)
    {
        ScrollWorkspaceTabs(-1);
    }

    private void OnWorkspaceTabsScrollRightClicked(object sender, RoutedEventArgs args)
    {
        ScrollWorkspaceTabs(1);
    }

    private void ScrollWorkspaceTabs(int direction)
    {
        var scrollViewer = EnsureWorkspaceTabsScrollViewer();
        if (scrollViewer is null)
        {
            return;
        }

        var page = Math.Max(160, scrollViewer.ViewportWidth * 0.72);
        var nextOffset = Math.Clamp(scrollViewer.HorizontalOffset + direction * page, 0, scrollViewer.ScrollableWidth);
        scrollViewer.ChangeView(nextOffset, null, null, disableAnimation: false);
        QueueWorkspaceTabsScrollStateUpdate();
    }

    private void QueueWorkspaceTabsScrollStateUpdate(bool scrollSelectedIntoView = false)
    {
        _ = DispatcherQueue?.TryEnqueue(() =>
        {
            if (scrollSelectedIntoView && WorkspaceTabList.SelectedItem is { } selectedItem)
            {
                WorkspaceTabList.ScrollIntoView(selectedItem);
            }

            EnsureWorkspaceTabsScrollViewer();
            UpdateWorkspaceTabsScrollButtons();
        });
    }

    private ScrollViewer? EnsureWorkspaceTabsScrollViewer()
    {
        if (workspaceTabsScrollViewer is not null)
        {
            return workspaceTabsScrollViewer;
        }

        var scrollViewer = FindDescendant<ScrollViewer>(WorkspaceTabList);
        if (scrollViewer is null)
        {
            return null;
        }

        workspaceTabsScrollViewer = scrollViewer;
        workspaceTabsScrollViewer.ViewChanged += OnWorkspaceTabsScrollViewerViewChanged;
        return workspaceTabsScrollViewer;
    }

    private void UpdateWorkspaceTabsScrollButtons()
    {
        var scrollViewer = EnsureWorkspaceTabsScrollViewer();
        var hasOverflow = scrollViewer is not null && scrollViewer.ScrollableWidth > 0.5;
        var visibility = hasOverflow ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceTabsScrollLeftButton.Visibility = visibility;
        WorkspaceTabsScrollRightButton.Visibility = visibility;
        WorkspaceTabsScrollLeftButton.IsEnabled = hasOverflow && scrollViewer!.HorizontalOffset > 0.5;
        WorkspaceTabsScrollRightButton.IsEnabled = hasOverflow && scrollViewer!.HorizontalOffset < scrollViewer.ScrollableWidth - 0.5;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : class
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindVisibleDescendant<T>(DependencyObject root)
        where T : FrameworkElement
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement { Visibility: not Visibility.Visible })
            {
                continue;
            }

            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisibleDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ViewModel.IsLightThemeEnabled))
        {
            ApplyRequestedTheme();
        }
        else if (args.PropertyName == nameof(ViewModel.LeftPaneWidth))
        {
            ApplyLeftPaneWidth(ViewModel.LeftPaneWidth);
        }
        else if (args.PropertyName == nameof(ViewModel.Scene))
        {
            DiffCanvas.Scene = ViewModel.Scene;
        }
        else if (args.PropertyName == nameof(ViewModel.UseInteractiveLevelOfDetail))
        {
            DiffCanvas.UseInteractiveLevelOfDetail = ViewModel.UseInteractiveLevelOfDetail;
        }
    }

    private void ApplyRequestedTheme()
    {
        RequestedTheme = ViewModel.IsLightThemeEnabled ? ElementTheme.Light : ElementTheme.Dark;
    }

    private void OnRootShellGridSizeChanged(object sender, SizeChangedEventArgs args)
    {
        ApplyLeftPaneWidth(LeftPaneColumn.ActualWidth > 0 ? LeftPaneColumn.ActualWidth : ViewModel.LeftPaneWidth);
        UpdateWorkspaceSettingsFlyoutSize();
    }

    private void OnPaneSplitterPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var pointerPoint = args.GetCurrentPoint(RootShellGrid);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            return;
        }

        isPaneSplitterDragging = true;
        SetPaneSplitterCursor(isResizeCursorEnabled: true);
        paneSplitterStartX = pointerPoint.Position.X;
        paneSplitterStartWidth = LeftPaneColumn.ActualWidth > 0 ? LeftPaneColumn.ActualWidth : ViewModel.LeftPaneWidth;
        PaneSplitter.CapturePointer(args.Pointer);
        PaneSplitterLine.Width = 2;
        args.Handled = true;
    }

    private void OnPaneSplitterPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!isPaneSplitterDragging)
        {
            return;
        }

        var pointerPoint = args.GetCurrentPoint(RootShellGrid);
        var nextWidth = paneSplitterStartWidth + pointerPoint.Position.X - paneSplitterStartX;
        ApplyLeftPaneWidth(nextWidth);
        args.Handled = true;
    }

    private async void OnPaneSplitterPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!isPaneSplitterDragging)
        {
            return;
        }

        isPaneSplitterDragging = false;
        PaneSplitter.ReleasePointerCaptures();
        SetPaneSplitterCursor(isPaneSplitterPointerOver);
        PaneSplitterLine.Width = 1;
        await ViewModel.SetLeftPaneWidthAsync(LeftPaneColumn.ActualWidth);
        args.Handled = true;
    }

    private void OnPaneSplitterPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        isPaneSplitterPointerOver = true;
        SetPaneSplitterCursor(isResizeCursorEnabled: true);
        PaneSplitterLine.Width = 2;
    }

    private void OnPaneSplitterPointerExited(object sender, PointerRoutedEventArgs args)
    {
        isPaneSplitterPointerOver = false;
        if (!isPaneSplitterDragging)
        {
            SetPaneSplitterCursor(isResizeCursorEnabled: false);
            PaneSplitterLine.Width = 1;
        }
    }

    private void SetPaneSplitterCursor(bool isResizeCursorEnabled)
    {
        if (isResizeCursorEnabled)
        {
            PaneSplitter.UseHorizontalResizeCursor();
        }
        else
        {
            PaneSplitter.ClearCursor();
        }
    }

    private void OnSymbolFacetSplitterPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var pointerPoint = args.GetCurrentPoint(SymbolPanelGrid);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            return;
        }

        isSymbolFacetSplitterDragging = true;
        SetSymbolFacetSplitterCursor(isResizeCursorEnabled: true);
        symbolFacetSplitterStartY = pointerPoint.Position.Y;
        symbolFacetSplitterStartHeight = SymbolFacetsRow.ActualHeight > 0
            ? SymbolFacetsRow.ActualHeight
            : DefaultSymbolFacetsHeight;
        SymbolFacetSplitter.CapturePointer(args.Pointer);
        SymbolFacetSplitterLine.Height = 2;
        args.Handled = true;
    }

    private void OnSymbolFacetSplitterPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!isSymbolFacetSplitterDragging)
        {
            return;
        }

        var pointerPoint = args.GetCurrentPoint(SymbolPanelGrid);
        var nextHeight = symbolFacetSplitterStartHeight + pointerPoint.Position.Y - symbolFacetSplitterStartY;
        ApplySymbolFacetsHeight(nextHeight);
        args.Handled = true;
    }

    private void OnSymbolFacetSplitterPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!isSymbolFacetSplitterDragging)
        {
            return;
        }

        isSymbolFacetSplitterDragging = false;
        SymbolFacetSplitter.ReleasePointerCaptures();
        SetSymbolFacetSplitterCursor(isSymbolFacetSplitterPointerOver);
        SymbolFacetSplitterLine.Height = 1;
        args.Handled = true;
    }

    private void OnSymbolFacetSplitterPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        isSymbolFacetSplitterPointerOver = true;
        SetSymbolFacetSplitterCursor(isResizeCursorEnabled: true);
        SymbolFacetSplitterLine.Height = 2;
    }

    private void OnSymbolFacetSplitterPointerExited(object sender, PointerRoutedEventArgs args)
    {
        isSymbolFacetSplitterPointerOver = false;
        if (!isSymbolFacetSplitterDragging)
        {
            SetSymbolFacetSplitterCursor(isResizeCursorEnabled: false);
            SymbolFacetSplitterLine.Height = 1;
        }
    }

    private void SetSymbolFacetSplitterCursor(bool isResizeCursorEnabled)
    {
        if (isResizeCursorEnabled)
        {
            SymbolFacetSplitter.UseVerticalResizeCursor();
        }
        else
        {
            SymbolFacetSplitter.ClearCursor();
        }
    }

    private void ApplySymbolFacetsHeight(double requestedHeight)
    {
        SymbolFacetsRow.Height = new GridLength(ClampSymbolFacetsHeight(requestedHeight));
    }

    private double ClampSymbolFacetsHeight(double requestedHeight)
    {
        var maximumFromPanel = SymbolPanelGrid.ActualHeight > 0
            ? SymbolPanelGrid.ActualHeight - 180
            : MaximumSymbolFacetsHeight;
        var maximumHeight = Math.Max(MinimumSymbolFacetsHeight, Math.Min(MaximumSymbolFacetsHeight, maximumFromPanel));
        return Math.Clamp(double.IsFinite(requestedHeight) ? requestedHeight : DefaultSymbolFacetsHeight, MinimumSymbolFacetsHeight, maximumHeight);
    }

    private void ApplyLeftPaneWidth(double requestedWidth)
    {
        var width = ClampLeftPaneWidth(requestedWidth);
        LeftPaneColumn.Width = new GridLength(width);
    }

    private double ClampLeftPaneWidth(double requestedWidth)
    {
        var shellWidth = RootShellGrid.ActualWidth;
        var splitterWidth = PaneSplitterColumn.ActualWidth > 0 ? PaneSplitterColumn.ActualWidth : PaneSplitterColumn.Width.Value;
        var maximumWidth = shellWidth > 0
            ? Math.Max(MinimumLeftPaneWidth, shellWidth - splitterWidth - MinimumCanvasWidth)
            : Math.Max(MinimumLeftPaneWidth, ViewModel.LeftPaneWidth);
        return Math.Clamp(double.IsFinite(requestedWidth) ? requestedWidth : ViewModel.LeftPaneWidth, MinimumLeftPaneWidth, maximumWidth);
    }

    private void OnWorkspaceSettingsFlyoutOpened(object sender, object args)
    {
        isWorkspaceSettingsFlyoutOpen = true;
        UpdateWorkspaceSettingsFlyoutSize();
    }

    private void OnWorkspaceSettingsFlyoutClosed(object sender, object args)
    {
        isWorkspaceSettingsFlyoutOpen = false;
    }

    private void UpdateWorkspaceSettingsFlyoutSize()
    {
        if (!isWorkspaceSettingsFlyoutOpen)
        {
            return;
        }

        var shellWidth = RootShellGrid.ActualWidth;
        var availableWidth = shellWidth > 0
            ? Math.Max(MinimumSettingsFlyoutWidth, shellWidth - SettingsFlyoutHorizontalMargin)
            : MinimumSettingsFlyoutWidth;
        var preferredWidth = Math.Max(MinimumSettingsFlyoutWidth, availableWidth * SettingsFlyoutWidthRatio);
        WorkspaceSettingsFlyoutContent.Width = Math.Min(preferredWidth, availableWidth);

        var shellHeight = RootShellGrid.ActualHeight;
        if (shellHeight > 0)
        {
            WorkspaceSettingsFlyoutContent.MaxHeight = Math.Max(280, shellHeight - SettingsFlyoutVerticalMargin);
        }
    }

    private async Task OpenRepositoryFromPickerAsync()
    {
        try
        {
            var folderPicker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };
            folderPicker.FileTypeFilter.Add("*");
            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder is not null)
            {
                await ViewModel.OpenRepositoryAsync(folder.Path);
            }
        }
        catch (Exception exception)
        {
            ViewModel.ReportInteractionError(exception.Message);
        }
    }

    private async Task OpenDirectoryWorkspaceFromPickerAsync()
    {
        try
        {
            var folderPicker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };
            folderPicker.FileTypeFilter.Add("*");
            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder is not null)
            {
                await ViewModel.OpenDirectoryWorkspaceAsync(folder.Path);
            }
        }
        catch (Exception exception)
        {
            ViewModel.ReportInteractionError(exception.Message);
        }
    }

    private async Task ExportWorkspaceGraphAsync(WorkspaceGraphExportFormat format)
    {
        try
        {
            var scene = ViewModel.Scene;
            if (scene is null || scene.Nodes.Count == 0 || scene.GraphBounds.IsEmpty)
            {
                ViewModel.ReportInteractionError("No workspace graph to export");
                return;
            }

            var picker = CreateWorkspaceGraphSavePicker(format);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await using var stream = File.Open(file.Path, FileMode.Create, FileAccess.Write, FileShare.None);
            workspaceGraphExporter.Export(scene, stream, new DiffSceneExportOptions(ToDiffSceneExportFormat(format), ViewModel.IsLightThemeEnabled));
            await stream.FlushAsync();
            ViewModel.ReportInteractionInfo($"Exported workspace graph to {file.Path}");
        }
        catch (Exception exception)
        {
            ViewModel.ReportInteractionError($"Graph export failed: {exception.Message}");
        }
    }

    private FileSavePicker CreateWorkspaceGraphSavePicker(WorkspaceGraphExportFormat format)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = BuildWorkspaceGraphExportFileName(format)
        };

        switch (format)
        {
            case WorkspaceGraphExportFormat.Svg:
                picker.FileTypeChoices.Add("SVG image", [".svg"]);
                break;
            case WorkspaceGraphExportFormat.Png:
                picker.FileTypeChoices.Add("PNG image", [".png"]);
                break;
            case WorkspaceGraphExportFormat.Pdf:
                picker.FileTypeChoices.Add("PDF document", [".pdf"]);
                break;
        }

        return picker;
    }

    private string BuildWorkspaceGraphExportFileName(WorkspaceGraphExportFormat format)
    {
        var repositoryName = string.IsNullOrWhiteSpace(ViewModel.RepositoryName)
            ? "workspace"
            : ViewModel.RepositoryName.Trim();
        var safeName = new string(repositoryName
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character)
            .ToArray());
        return $"{safeName}-diff-graph.{FileExtension(format)}";
    }

    private static string FileExtension(WorkspaceGraphExportFormat format) => format switch
    {
        WorkspaceGraphExportFormat.Svg => "svg",
        WorkspaceGraphExportFormat.Png => "png",
        WorkspaceGraphExportFormat.Pdf => "pdf",
        _ => "png"
    };

    private static DiffSceneExportFormat ToDiffSceneExportFormat(WorkspaceGraphExportFormat format)
    {
        return format switch
        {
            WorkspaceGraphExportFormat.Svg => DiffSceneExportFormat.Svg,
            WorkspaceGraphExportFormat.Png => DiffSceneExportFormat.Png,
            WorkspaceGraphExportFormat.Pdf => DiffSceneExportFormat.Pdf,
            _ => DiffSceneExportFormat.Png
        };
    }

    private async Task RelayoutAndFitActiveWorkspaceAsync()
    {
        await ApplyLayoutModeToActiveWorkspaceAsync(layoutMode: null, fitAfterLayout: true);
    }

    private DiffCanvasControl? GetActiveWorkspaceCanvas()
    {
        if (ViewModel.SelectedWorkspaceTab?.Kind == ViewModels.WorkspaceTabKind.Graph)
        {
            return DiffCanvas;
        }

        return FindVisibleDescendant<DiffCanvasControl>(WorkspaceTabContentHost);
    }

    private void FitActiveWorkspaceCanvas()
    {
        if (GetActiveWorkspaceCanvas() is { } canvas)
        {
            canvas.FitToScene();
            return;
        }

        ViewModel.ReportInteractionError("No active canvas is available to fit");
    }

    private async Task ApplyLayoutModeToActiveWorkspaceAsync(GraphLayoutMode? layoutMode, bool fitAfterLayout)
    {
        var mode = layoutMode ?? ViewModel.SelectedLayoutModeOption?.Mode ?? GraphLayoutMode.Layered;
        var activeTab = ViewModel.SelectedWorkspaceTab;
        if (activeTab?.Kind == ViewModels.WorkspaceTabKind.Graph)
        {
            if (layoutMode is null)
            {
                await ViewModel.RelayoutAsync(DiffCanvas.Scene);
            }
            else
            {
                await ViewModel.SetLayoutModeAsync(mode, DiffCanvas.Scene);
            }

            DiffCanvas.Scene = ViewModel.Scene;
            if (fitAfterLayout)
            {
                DiffCanvas.FitToScene();
            }

            return;
        }

        if (activeTab?.SymbolGraph is { } symbolGraph)
        {
            if (layoutMode is null)
            {
                symbolGraph.Relayout();
            }
            else
            {
                symbolGraph.SetLayoutMode(mode);
            }

            if (GetActiveWorkspaceCanvas() is { } symbolCanvas)
            {
                symbolCanvas.Scene = symbolGraph.Scene;
                if (fitAfterLayout)
                {
                    symbolCanvas.FitToScene();
                }
            }

            return;
        }

        if (GetActiveWorkspaceCanvas() is not { } canvas)
        {
            ViewModel.ReportInteractionError("No active canvas is available to lay out");
            return;
        }

        if (canvas.Scene is null || canvas.Scene.Nodes.Count == 0)
        {
            ViewModel.ReportInteractionError("The active canvas has no nodes to lay out");
            return;
        }

        await RelayoutCanvasSceneAsync(canvas, mode);
        if (fitAfterLayout)
        {
            canvas.FitToScene();
        }
    }

    private async Task ApplyGroupingModeToActiveWorkspaceAsync(GraphGroupingMode groupingMode)
    {
        var activeTab = ViewModel.SelectedWorkspaceTab;
        if (activeTab?.Kind == ViewModels.WorkspaceTabKind.Graph)
        {
            await ViewModel.SetGroupingModeAsync(groupingMode);
            DiffCanvas.Scene = ViewModel.Scene;
            return;
        }

        if (activeTab?.SymbolGraph is { } symbolGraph)
        {
            symbolGraph.SetGroupingMode(groupingMode);
            if (GetActiveWorkspaceCanvas() is { } symbolCanvas)
            {
                symbolCanvas.Scene = symbolGraph.Scene;
            }

            return;
        }

        ViewModel.ReportInteractionError("Grouping is only available for diff and symbol graph workspaces");
    }

    private static async Task RelayoutCanvasSceneAsync(DiffCanvasControl canvas, GraphLayoutMode layoutMode)
    {
        if (canvas.Scene is not { } scene)
        {
            return;
        }

        var documents = scene.Nodes
            .Select(node => node.DiffDocument)
            .ToImmutableArray();
        if (documents.IsDefaultOrEmpty)
        {
            return;
        }

        var layoutEngine = new MsaglGraphLayoutEngine();
        var layout = await layoutEngine.LayoutAsync(
            new GraphLayoutRequest(
                documents,
                SemanticGraph.Empty,
                new Size2(620, 420),
                scene.GetCurrentLayout(),
                ImmutableHashSet<DiffDocumentId>.Empty,
                layoutMode),
            CancellationToken.None);
        scene.ApplyLayout(layout);
        canvas.InvalidateScene();
    }

    private Task SelectRailTabAsync(int selectedIndex)
    {
        ViewModel.SelectedRailTabIndex = selectedIndex;
        return Task.CompletedTask;
    }

    private static Task FocusTextBoxAsync(TextBox textBox)
    {
        textBox.Focus(FocusState.Programmatic);
        textBox.SelectAll();
        return Task.CompletedTask;
    }

    private Task FocusGitReferenceSearchAsync()
    {
        ViewModel.SelectedRailTabIndex = 1;
        GitReferenceSearchBox.Focus(FocusState.Programmatic);
        GitReferenceSearchBox.SelectAll();
        return Task.CompletedTask;
    }

    private Task FocusReviewSearchAsync()
    {
        ViewModel.SelectedRailTabIndex = 2;
        ReviewSearchBox.Focus(FocusState.Programmatic);
        ReviewSearchBox.SelectAll();
        return Task.CompletedTask;
    }

    private async Task ToggleThemeAsync()
    {
        await ViewModel.SetThemeAsync(!ViewModel.IsLightThemeEnabled);
        ApplyRequestedTheme();
    }

    private Task ToggleVisualizationLayerAsync(string layer, bool isCurrentlyEnabled) =>
        ViewModel.SetVisualizationLayerAsync(layer, !isCurrentlyEnabled);

    private async Task SetLayoutModeFromAcceleratorAsync(GraphLayoutMode layoutMode)
    {
        await ApplyLayoutModeToActiveWorkspaceAsync(layoutMode, fitAfterLayout: true);
    }

    private async Task SetGroupingModeFromAcceleratorAsync(GraphGroupingMode groupingMode)
    {
        await ApplyGroupingModeToActiveWorkspaceAsync(groupingMode);
    }

    private Task RunCanvasNodeCommand(Func<DiffCanvasControl, bool> command, string missingSelectionMessage)
    {
        var canvas = GetActiveWorkspaceCanvas();
        if (canvas is null)
        {
            ViewModel.ReportInteractionError("No active canvas is available for this command");
            return Task.CompletedTask;
        }

        if (!command(canvas))
        {
            ViewModel.ReportInteractionError(missingSelectionMessage);
        }

        return Task.CompletedTask;
    }

    private void OnFitClicked(object sender, RoutedEventArgs args)
    {
        FitActiveWorkspaceCanvas();
    }

    private void OnReloadClicked(object sender, RoutedEventArgs args)
    {
        ViewModel.ReloadRepository();
    }

    private void OnPreviousChangeClicked(object sender, RoutedEventArgs args)
    {
        FocusActiveCanvas(ViewModel.FocusPreviousChange());
    }

    private void OnNextChangeClicked(object sender, RoutedEventArgs args)
    {
        FocusActiveCanvas(ViewModel.FocusNextChange());
    }

    private async void OnOpenRepositoryClicked(object sender, RoutedEventArgs args)
    {
        await OpenRepositoryFromPickerAsync();
    }

    private async void OnOpenDirectoryWorkspaceClicked(object sender, RoutedEventArgs args)
    {
        await OpenDirectoryWorkspaceFromPickerAsync();
    }

    private async void OnLayoutClicked(object sender, RoutedEventArgs args)
    {
        await RelayoutAndFitActiveWorkspaceAsync();
    }

    private async void OnExportWorkspaceGraphClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string tag } ||
            !Enum.TryParse<WorkspaceGraphExportFormat>(tag, ignoreCase: true, out var format))
        {
            ViewModel.ReportInteractionError("Unknown graph export format");
            return;
        }

        await ExportWorkspaceGraphAsync(format);
    }

    private async void OnFullCodeNodesClicked(object sender, RoutedEventArgs args)
    {
        var enabled = sender is ToggleButton { IsChecked: true };
        await ViewModel.SetFullCodeWorkspaceAsync(enabled);
        DiffCanvas.InvalidateScene();
    }

    private async void OnNodeEditingWorkspaceClicked(object sender, RoutedEventArgs args)
    {
        var enabled = sender is ToggleButton { IsChecked: true };
        await ViewModel.SetNodeEditingWorkspaceAsync(enabled);
        DiffCanvas.InvalidateScene();
    }

    private void OnNewEditorCanvasClicked(object sender, RoutedEventArgs args)
    {
        ViewModel.OpenEditorCanvasTab();
    }

    private void OnNewQueryCanvasClicked(object sender, RoutedEventArgs args)
    {
        ViewModel.OpenQueryCanvasTab();
    }

    private void OnNewPatchCompareClicked(object sender, RoutedEventArgs args)
    {
        ViewModel.OpenPatchCompareTab();
    }

    private async void OnDiscoverPatchCompareSourcesClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.DiscoverPatchCompareSourcesAsync(ViewModel.SelectedWorkspaceTab);
    }

    private void OnPatchCompareUseCurrentRepositoryClicked(object sender, RoutedEventArgs args)
    {
        ViewModel.UseCurrentRepositoryForPatchCompare(ViewModel.SelectedWorkspaceTab);
    }

    private void OnApplyPatchCompareWizardSelectionClicked(object sender, RoutedEventArgs args)
    {
        ViewModel.ApplyPatchCompareWizardSelection(ViewModel.SelectedWorkspaceTab);
    }

    private async void OnRunPatchCompareClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.RunPatchCompareAsync(ViewModel.SelectedWorkspaceTab);
    }

    private void OnResetPatchCompareClicked(object sender, RoutedEventArgs args)
    {
        ViewModel.ResetPatchCompare(ViewModel.SelectedWorkspaceTab);
    }

    private void OnRunQueryCanvasClicked(object sender, RoutedEventArgs args)
    {
        ViewModel.RunSelectedQueryCanvas();
    }

    private void OnResetQueryCanvasClicked(object sender, RoutedEventArgs args)
    {
        ViewModel.ResetSelectedQueryCanvas();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs args)
    {
        ViewModel.CancelCurrentOperation();
    }

    private async void OnWorktreeScopeClicked(object sender, RoutedEventArgs args)
    {
        KeepSelectedToggleChecked(sender);
        await ViewModel.SetDiffScopeAsync(GitDiffScope.Worktree);
    }

    private async void OnUnstagedScopeClicked(object sender, RoutedEventArgs args)
    {
        KeepSelectedToggleChecked(sender);
        await ViewModel.SetDiffScopeAsync(GitDiffScope.Unstaged);
    }

    private async void OnStagedScopeClicked(object sender, RoutedEventArgs args)
    {
        KeepSelectedToggleChecked(sender);
        await ViewModel.SetDiffScopeAsync(GitDiffScope.Staged);
    }

    private async void OnBranchScopeClicked(object sender, RoutedEventArgs args)
    {
        KeepSelectedToggleChecked(sender);
        await ViewModel.SetDiffScopeAsync(GitDiffScope.Branch);
    }

    private async void OnRangeScopeClicked(object sender, RoutedEventArgs args)
    {
        KeepSelectedToggleChecked(sender);
        await ViewModel.SetDiffScopeAsync(GitDiffScope.CommitRange);
    }

    private async void OnApplyRefsClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.ApplyReferenceOptionsAsync();
    }

    private async void OnAutoRefreshClicked(object sender, RoutedEventArgs args)
    {
        var isEnabled = sender is ToggleButton { IsChecked: true };
        await ViewModel.SetAutoRefreshAsync(isEnabled);
    }

    private async void OnChangedHunksContextClicked(object sender, RoutedEventArgs args)
    {
        KeepSelectedToggleChecked(sender);
        await ViewModel.SetDiffContextModeAsync(DiffContextMode.ChangedHunks);
    }

    private async void OnFullFileDiffContextClicked(object sender, RoutedEventArgs args)
    {
        KeepSelectedToggleChecked(sender);
        await ViewModel.SetDiffContextModeAsync(DiffContextMode.FullFileDiff);
    }

    private async void OnCurrentFileContextClicked(object sender, RoutedEventArgs args)
    {
        KeepSelectedToggleChecked(sender);
        await ViewModel.SetDiffContextModeAsync(DiffContextMode.CurrentFile);
    }

    private async void OnThemeClicked(object sender, RoutedEventArgs args)
    {
        var isLightThemeEnabled = sender is ToggleButton { IsChecked: true };
        await ViewModel.SetThemeAsync(isLightThemeEnabled);
        ApplyRequestedTheme();
    }

    private async void OnSemanticEdgesClicked(object sender, RoutedEventArgs args)
    {
        var isEnabled = sender is ToggleButton { IsChecked: true };
        await ViewModel.SetSemanticEdgesAsync(isEnabled);
    }

    private async void OnInteractiveLevelOfDetailClicked(object sender, RoutedEventArgs args)
    {
        var isEnabled = sender is ToggleButton { IsChecked: true };
        await ViewModel.SetInteractiveLevelOfDetailAsync(isEnabled);
    }

    private async void OnSemanticWorkspaceModeClicked(object sender, RoutedEventArgs args)
    {
        KeepSelectedToggleChecked(sender);
        await ViewModel.SetSemanticAnalysisModeAsync(SemanticAnalysisMode.WorkspaceThenSyntax);
    }

    private async void OnSemanticFastModeClicked(object sender, RoutedEventArgs args)
    {
        KeepSelectedToggleChecked(sender);
        await ViewModel.SetSemanticAnalysisModeAsync(SemanticAnalysisMode.FastSyntaxOnly);
    }

    private async void OnCompletionLanguageServicesModeClicked(object sender, RoutedEventArgs args)
    {
        KeepSelectedToggleChecked(sender);
        await ViewModel.SetCodeCompletionModeAsync(CodeCompletionMode.LanguageServicesThenDocument);
    }

    private async void OnCompletionDocumentModeClicked(object sender, RoutedEventArgs args)
    {
        KeepSelectedToggleChecked(sender);
        await ViewModel.SetCodeCompletionModeAsync(CodeCompletionMode.DocumentOnly);
    }

    private async void OnTokenizationClicked(object sender, RoutedEventArgs args)
    {
        var isEnabled = sender is ToggleButton { IsChecked: true };
        await ViewModel.SetTokenizationAsync(isEnabled);
    }

    private async void OnFileTypeFilterClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileTypeFilterOptionViewModel option })
        {
            await ViewModel.ToggleFileTypeFilterAsync(option);
        }
    }

    private async void OnFileTypeFilterAllClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.IncludeAllFileTypesAsync();
    }

    private async void OnFileTypeFilterNoneClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.IncludeNoFileTypesAsync();
    }

    private async void OnLayoutModeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: ViewModels.LayoutModeOptionViewModel option })
        {
            return;
        }

        await ApplyLayoutModeToActiveWorkspaceAsync(option.Mode, fitAfterLayout: true);
    }

    private async void OnGroupingModeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: ViewModels.GroupingModeOptionViewModel option })
        {
            return;
        }

        await ApplyGroupingModeToActiveWorkspaceAsync(option.Mode);
    }

    private async void OnReviewRequestStateSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: ViewModels.ReviewRequestStateOptionViewModel option })
        {
            return;
        }

        await ViewModel.SetReviewRequestStateAsync(option.State);
    }

    private async void OnVisualizationToggleClicked(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleButton { Tag: string layer } toggleButton)
        {
            await ViewModel.SetVisualizationLayerAsync(layer, toggleButton.IsChecked == true);
        }
    }

    private async void OnNoiseFilterClicked(object sender, RoutedEventArgs args)
    {
        var isNoiseFilterEnabled = sender is ToggleButton { IsChecked: true };
        await ViewModel.SetReviewModeAsync(isNoiseFilterEnabled);
    }

    private async void OnContextFoldingClicked(object sender, RoutedEventArgs args)
    {
        var isContextFoldingEnabled = sender is ToggleButton { IsChecked: true };
        await ViewModel.SetContextFoldingAsync(isContextFoldingEnabled);
    }

    private void OnWorkspaceTabCloseClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.WorkspaceTabViewModel tab })
        {
            ViewModel.CloseWorkspaceTab(tab);
        }
    }

    private void OnWorkspaceTabHeaderRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: ViewModels.WorkspaceTabViewModel tab } element)
        {
            return;
        }

        ViewModel.SelectedWorkspaceTab = tab;

        var menu = new MenuFlyout();
        var copyTitleItem = new MenuFlyoutItem { Text = "Copy tab title" };
        copyTitleItem.Click += (_, _) => CopyTextToClipboard(tab.Header, "tab title");
        menu.Items.Add(copyTitleItem);

        if (!string.IsNullOrWhiteSpace(tab.DetailText))
        {
            var copyDetailsItem = new MenuFlyoutItem { Text = "Copy tab details" };
            copyDetailsItem.Click += (_, _) => CopyTextToClipboard(tab.DetailText, "tab details");
            menu.Items.Add(copyDetailsItem);
        }

        if (tab.FileDiff is { } fileDiff)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            AddFileDiffClipboardMenuItems(menu, fileDiff);
        }

        menu.ShowAt(element, new FlyoutShowOptions { Position = args.GetPosition(element) });
        args.Handled = true;
    }

    private void OnFileDiffModeClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ViewModels.WorkspaceTabViewModel tab, Tag: string mode })
        {
            return;
        }

        ViewModel.SetFileDiffDisplayMode(
            tab,
            string.Equals(mode, "FullFile", StringComparison.Ordinal)
                ? ViewModels.FileDiffDisplayMode.FullFile
                : ViewModels.FileDiffDisplayMode.DiffOnly);
    }

    private void OnFileDiffScopeModeClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ViewModels.WorkspaceTabViewModel tab, Tag: string mode })
        {
            return;
        }

        ViewModel.SetFileDiffScopeMode(
            tab,
            string.Equals(mode, "FullFileDiff", StringComparison.Ordinal)
                ? ViewModels.FileDiffScopeMode.FullFileDiff
                : ViewModels.FileDiffScopeMode.Changes);
    }

    private void OnFileDiffAnnotationToggleClicked(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleButton { DataContext: ViewModels.WorkspaceTabViewModel tab } toggle)
        {
            ViewModel.SetFileDiffAnnotationVisibility(tab, toggle.IsChecked == true);
        }
    }

    private void OnFileDiffEditToggleClicked(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleButton { DataContext: ViewModels.WorkspaceTabViewModel { FileDiff: { } fileDiff } } toggle)
        {
            fileDiff.SetEditingEnabled(toggle.IsChecked == true);
        }
    }

    private async void OnFileDiffOpenBlameClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.WorkspaceTabViewModel { FileDiff: { } fileDiff } })
        {
            await ViewModel.OpenBlameTabAsync(fileDiff.DocumentId);
        }
    }

    private void OnFileDiffDecreaseFontSizeClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: ViewModels.WorkspaceTabViewModel { FileDiff: { } fileDiff } })
        {
            fileDiff.DecreaseCodeFontSize();
        }
    }

    private void OnFileDiffIncreaseFontSizeClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: ViewModels.WorkspaceTabViewModel { FileDiff: { } fileDiff } })
        {
            fileDiff.IncreaseCodeFontSize();
        }
    }

    private void OnFileDiffFontSizeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox { SelectedItem: double size, DataContext: ViewModels.WorkspaceTabViewModel { FileDiff: { } fileDiff } })
        {
            fileDiff.SetCodeFontSize(size);
        }
    }

    private void OnFileViewerLineContextRequested(object? sender, CodeFileLineContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ViewModels.WorkspaceTabViewModel { FileDiff: { } fileDiff } } element)
        {
            return;
        }

        var documentId = fileDiff.DocumentId;
        var symbol = ViewModel.FindSemanticItemForLineContext(documentId, args.LineNumber, args.SymbolText);
        var menu = new MenuFlyout();

        if (sender is CodeFileViewerControl viewer)
        {
            AddFileEditorContextMenuItems(menu, viewer, fileDiff);
        }

        var focusLineItem = new MenuFlyoutItem { Text = $"Focus line {args.LineNumber} in graph" };
        focusLineItem.Click += (_, _) => FocusCanvas(new ViewModels.FocusRequest(documentId, args.LineNumber));
        menu.Items.Add(focusLineItem);

        var revealFileItem = new MenuFlyoutItem { Text = "Reveal file in Files" };
        revealFileItem.Click += (_, _) => ViewModel.RevealDocumentInExplorer(documentId);
        menu.Items.Add(revealFileItem);

        var mapFileItem = new MenuFlyoutItem { Text = "Open file semantic map" };
        mapFileItem.Click += (_, _) => ViewModel.OpenSemanticMapForDocumentId(documentId);
        menu.Items.Add(mapFileItem);

        var graphFileItem = new MenuFlyoutItem { Text = "Open file symbol graph" };
        graphFileItem.Click += (_, _) => ViewModel.OpenSymbolGraphForDocumentId(documentId);
        menu.Items.Add(graphFileItem);

        if (fileDiff.FindSemanticLineInsight(args.LineNumber) is { } insight)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem
            {
                Text = $"Semantic: {TrimMenuText(insight.Detail)}",
                IsEnabled = false
            });
        }

        if (symbol is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            var symbolLabel = TrimMenuText(symbol.DisplayName);
            var focusSymbolItem = new MenuFlyoutItem { Text = $"Focus symbol {symbolLabel}" };
            focusSymbolItem.Click += (_, _) => FocusCanvas(ViewModel.FocusSemanticItem(symbol));
            menu.Items.Add(focusSymbolItem);

            var mapSymbolItem = new MenuFlyoutItem { Text = $"Open map for {symbolLabel}" };
            mapSymbolItem.Click += (_, _) => ViewModel.OpenSemanticMapForSymbol(symbol);
            menu.Items.Add(mapSymbolItem);

            var graphSymbolItem = new MenuFlyoutItem { Text = $"Open symbol graph for {symbolLabel}" };
            graphSymbolItem.Click += (_, _) => ViewModel.OpenSymbolGraphForSymbol(symbol);
            menu.Items.Add(graphSymbolItem);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var diffTabItem = new MenuFlyoutItem { Text = "Open diff tab" };
        diffTabItem.Click += async (_, _) => await ViewModel.OpenFileDiffTabAsync(documentId, ViewModels.FileDiffDisplayMode.DiffOnly);
        menu.Items.Add(diffTabItem);

        var fullFileItem = new MenuFlyoutItem { Text = "Open full file tab" };
        fullFileItem.Click += async (_, _) => await ViewModel.OpenFileDiffTabAsync(documentId, ViewModels.FileDiffDisplayMode.FullFile);
        menu.Items.Add(fullFileItem);

        var blameItem = new MenuFlyoutItem { Text = "Open blame tab" };
        blameItem.Click += async (_, _) => await ViewModel.OpenBlameTabAsync(documentId);
        menu.Items.Add(blameItem);

        var copyLineItem = new MenuFlyoutItem { Text = "Copy line text" };
        copyLineItem.Click += (_, _) => CopyTextToClipboard(args.Line.Text, $"line {args.LineNumber}");
        menu.Items.Add(copyLineItem);

        menu.ShowAt(element, new FlyoutShowOptions { Position = args.Position });
    }

    private static void AddFileEditorContextMenuItems(
        MenuFlyout menu,
        CodeFileViewerControl viewer,
        ViewModels.FileDiffTabViewModel fileDiff)
    {
        var canEdit = fileDiff.IsFullFileMode && fileDiff.IsEditingEnabled;
        menu.Items.Add(CreateMenuItem("Copy selection", () => viewer.CopySelection()));
        menu.Items.Add(CreateMenuItem("Select all", () => viewer.SelectAll()));

        if (fileDiff.IsFullFileMode)
        {
            menu.Items.Add(CreateMenuItem("Cut selection", () => viewer.CutSelection(), canEdit));
            menu.Items.Add(CreateMenuItem("Paste", () => viewer.PasteFromClipboard(), canEdit));
            menu.Items.Add(CreateMenuItem("Undo edit (Ctrl+Z)", () => viewer.UndoEdit(), canEdit));
            menu.Items.Add(CreateMenuItem("Redo edit (Ctrl+Y)", () => viewer.RedoEdit(), canEdit));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(CreateMenuItem("Toggle line comment (Ctrl+/)", () => viewer.ToggleLineComment(), canEdit));
            menu.Items.Add(CreateMenuItem("Move line or selection up (Alt+Up)", () => viewer.MoveSelectionUp(), canEdit));
            menu.Items.Add(CreateMenuItem("Move line or selection down (Alt+Down)", () => viewer.MoveSelectionDown(), canEdit));
            menu.Items.Add(CreateMenuItem("Copy line or selection up (Alt+Shift+Up)", () => viewer.CopySelectionUp(), canEdit));
            menu.Items.Add(CreateMenuItem("Copy line or selection down (Alt+Shift+Down)", () => viewer.CopySelectionDown(), canEdit));
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem CreateMenuItem(string text, Action action, bool isEnabled = true)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                IsEnabled = isEnabled
            };
            if (isEnabled)
            {
                item.Click += (_, _) =>
                {
                    viewer.Focus(FocusState.Programmatic);
                    action();
                };
            }

            return item;
        }
    }

    private void OnBlameTimelineToggleClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.WorkspaceTabViewModel tab })
        {
            ViewModel.ToggleBlameTimeline(tab);
        }
    }

    private void OnBlameDisplayModeClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ViewModels.WorkspaceTabViewModel tab, Tag: string mode })
        {
            return;
        }

        ViewModel.SetBlameDisplayMode(
            tab,
            string.Equals(mode, "ChangeGraph", StringComparison.Ordinal)
                ? ViewModels.BlameDisplayMode.ChangeGraph
                : ViewModels.BlameDisplayMode.CommitTimeline);
    }

    private async void OnGitHistoryContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue ||
            args.Item is not ViewModels.GitHistoryItemViewModel item ||
            sender.DataContext is not ViewModels.WorkspaceTabViewModel tab)
        {
            return;
        }

        await ViewModel.LoadMoreGitHistoryAsync(tab, item);
    }

    private void OnGitHistoryItemRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: ViewModels.GitHistoryItemViewModel item } element)
        {
            return;
        }

        var menu = new MenuFlyout();
        var copyHashItem = new MenuFlyoutItem
        {
            Text = "Copy commit hash",
            Tag = item
        };
        copyHashItem.Click += OnGitHistoryCopyCommitHashClicked;
        menu.Items.Add(copyHashItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var startRangeItem = new MenuFlyoutItem
        {
            Text = "Set as range start",
            Tag = item
        };
        startRangeItem.Click += OnGitHistorySetRangeStartClicked;
        menu.Items.Add(startRangeItem);

        var endRangeItem = new MenuFlyoutItem
        {
            Text = "Set as range end",
            Tag = item
        };
        endRangeItem.Click += OnGitHistorySetRangeEndClicked;
        menu.Items.Add(endRangeItem);

        menu.ShowAt(element, new FlyoutShowOptions { Position = args.GetPosition(element) });
        args.Handled = true;
    }

    private void OnGitHistoryCopyCommitHashClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: ViewModels.GitHistoryItemViewModel item })
        {
            return;
        }

        CopyTextToClipboard(item.CommitId, $"commit hash {item.ShortId}");
    }

    private void OnGitHistorySetRangeStartClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.GitHistoryItemViewModel item })
        {
            ViewModel.SetComparisonRangeStart(item);
        }
    }

    private void OnGitHistorySetRangeEndClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.GitHistoryItemViewModel item })
        {
            ViewModel.SetComparisonRangeEnd(item);
        }
    }

    private async void OnStageSelectedFileClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.StageSelectedFileAsync();
    }

    private async void OnUnstageSelectedFileClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.UnstageSelectedFileAsync();
    }

    private async void OnReviewReloadClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.RefreshReviewDiscussionAsync();
    }

    private void OnReviewOpenThreadClicked(object sender, RoutedEventArgs args)
    {
        FocusCanvas(ViewModel.FocusReviewThread(ViewModel.SelectedReviewThreadItem));
    }

    private async void OnReviewAddCommentClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.AddReviewCommentAsync();
    }

    private async void OnReviewReplyClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.ReplyToSelectedReviewThreadAsync();
    }

    private async void OnReviewResolveClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.ToggleSelectedReviewThreadResolvedAsync();
    }

    private void OnReviewThreadKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter && sender is ListView { SelectedItem: ViewModels.ReviewThreadItemViewModel item })
        {
            FocusCanvas(ViewModel.FocusReviewThread(item));
            args.Handled = true;
        }
    }

    private void OnReviewThreadDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (sender is ListView { SelectedItem: ViewModels.ReviewThreadItemViewModel item })
        {
            FocusCanvas(ViewModel.FocusReviewThread(item));
            args.Handled = true;
        }
    }

    private async void OnFileExplorerModeClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: string mode })
        {
            await ViewModel.SetFileExplorerModeAsync(
                string.Equals(mode, "Workspace", StringComparison.OrdinalIgnoreCase)
                    ? ViewModels.FileExplorerMode.Workspace
                    : ViewModels.FileExplorerMode.Diff);
        }
    }

    private async void OnExplorerTreeItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is ViewModels.FileExplorerNodeViewModel item)
        {
            await ActivateExplorerNodeAsync(item);
        }
    }

    private async void OnExplorerTreeKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not ListView { SelectedItem: ViewModels.FileExplorerNodeViewModel item })
        {
            return;
        }

        if (args.Key == VirtualKey.Enter)
        {
            await ActivateExplorerNodeAsync(item);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Space && item.HasChildren)
        {
            ViewModel.ToggleExplorerNode(item);
            args.Handled = true;
        }
    }

    private void OnExplorerNodeDisclosureClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item })
        {
            ViewModel.ToggleExplorerNode(item);
        }
    }

    private async void OnExplorerNodeContextNavigateClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item })
        {
            await ActivateExplorerNodeAsync(item);
        }
    }

    private async Task ActivateExplorerNodeAsync(ViewModels.FileExplorerNodeViewModel item)
    {
        try
        {
            var focusRequest = ViewModel.FocusExplorerNode(item);
            if (focusRequest is not null)
            {
                FocusCanvas(focusRequest);
                return;
            }

            if (item.IsFile && ViewModel.FileExplorerMode == ViewModels.FileExplorerMode.Workspace)
            {
                await ViewModel.OpenFileDiffTabAsync(item, ViewModels.FileDiffDisplayMode.FullFile);
            }
        }
        catch (Exception exception)
        {
            ViewModel.ReportInteractionError(exception.Message);
        }
    }

    private void OnExplorerTreeNodeRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item } element)
        {
            return;
        }

        var menu = new MenuFlyout();
        var primaryItem = new MenuFlyoutItem
        {
            Text = item.IsFile && ViewModel.FileExplorerMode == ViewModels.FileExplorerMode.Workspace
                ? "Open full file"
                : item.ContextMenuPrimaryText,
            Tag = item
        };
        primaryItem.Click += OnExplorerNodeContextNavigateClicked;
        menu.Items.Add(primaryItem);
        AddExplorerClipboardMenuItems(menu, item);

        menu.Items.Add(new MenuFlyoutSeparator());
        if (item.HasChildren)
        {
            var expandTreeItem = new MenuFlyoutItem
            {
                Text = "Expand selected tree",
                Tag = item
            };
            expandTreeItem.Click += OnExplorerNodeExpandTreeClicked;
            menu.Items.Add(expandTreeItem);

            var collapseTreeItem = new MenuFlyoutItem
            {
                Text = "Collapse selected tree",
                Tag = item
            };
            collapseTreeItem.Click += OnExplorerNodeCollapseTreeClicked;
            menu.Items.Add(collapseTreeItem);
        }

        var expandAllItem = new MenuFlyoutItem
        {
            Text = "Expand entire tree"
        };
        expandAllItem.Click += OnExplorerTreeExpandAllClicked;
        menu.Items.Add(expandAllItem);

        var collapseAllItem = new MenuFlyoutItem
        {
            Text = "Collapse entire tree"
        };
        collapseAllItem.Click += OnExplorerTreeCollapseAllClicked;
        menu.Items.Add(collapseAllItem);

        menu.Items.Add(new MenuFlyoutSeparator());
        var openSubsetWorkspaceItem = new MenuFlyoutItem
        {
            Text = item.IsFile ? "Open file diff workspace" : "Open folder diff workspace",
            Tag = item
        };
        openSubsetWorkspaceItem.Click += OnExplorerNodeOpenSubsetWorkspaceClicked;
        menu.Items.Add(openSubsetWorkspaceItem);

        var filterCanvasItem = new MenuFlyoutItem
        {
            Text = item.IsFile ? "Filter canvas to this file" : "Filter canvas to this folder",
            Tag = item
        };
        filterCanvasItem.Click += OnExplorerNodeFilterCanvasClicked;
        menu.Items.Add(filterCanvasItem);

        if (ViewModel.HasActiveGraphSubsetFilter)
        {
            var clearCanvasFilterItem = new MenuFlyoutItem
            {
                Text = "Clear canvas file filter"
            };
            clearCanvasFilterItem.Click += OnExplorerNodeClearCanvasFilterClicked;
            menu.Items.Add(clearCanvasFilterItem);
        }

        if (item.IsFile)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var openDiffItem = new MenuFlyoutItem
            {
                Text = "Open diff tab",
                Tag = item
            };
            openDiffItem.Click += OnExplorerNodeOpenDiffTabClicked;
            menu.Items.Add(openDiffItem);

            var openFullFileItem = new MenuFlyoutItem
            {
                Text = "Open full file tab",
                Tag = item
            };
            openFullFileItem.Click += OnExplorerNodeOpenFullFileTabClicked;
            menu.Items.Add(openFullFileItem);

            var openBlameItem = new MenuFlyoutItem
            {
                Text = "Open blame tab",
                Tag = item
            };
            openBlameItem.Click += OnExplorerNodeOpenBlameTabClicked;
            menu.Items.Add(openBlameItem);

        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var addToEditorCanvasItem = new MenuFlyoutItem
        {
            Text = item.IsFile ? "Add to editor canvas" : "Add folder recursively to editor canvas",
            Tag = item
        };
        addToEditorCanvasItem.Click += OnExplorerNodeAddToEditorCanvasClicked;
        menu.Items.Add(addToEditorCanvasItem);

        var addToNewEditorCanvasItem = new MenuFlyoutItem
        {
            Text = item.IsFile ? "Add to new editor canvas" : "Add folder recursively to new editor canvas",
            Tag = item
        };
        addToNewEditorCanvasItem.Click += OnExplorerNodeAddToNewEditorCanvasClicked;
        menu.Items.Add(addToNewEditorCanvasItem);

        var openSymbolsItem = new MenuFlyoutItem
        {
            Text = item.IsFile ? "Open semantic map" : "Open folder semantic map",
            Tag = item
        };
        openSymbolsItem.Click += OnExplorerNodeOpenSymbolsGraphClicked;
        menu.Items.Add(openSymbolsItem);

        menu.ShowAt(element, new FlyoutShowOptions { Position = args.GetPosition(element) });
        args.Handled = true;
    }

    private void OnExplorerNodeExpandTreeClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item })
        {
            ViewModel.ExpandExplorerNodeTree(item);
        }
    }

    private void OnExplorerNodeCollapseTreeClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item })
        {
            ViewModel.CollapseExplorerNodeTree(item);
        }
    }

    private void OnExplorerTreeExpandAllClicked(object sender, RoutedEventArgs args) =>
        ViewModel.ExpandAllExplorerNodes();

    private void OnExplorerTreeCollapseAllClicked(object sender, RoutedEventArgs args) =>
        ViewModel.CollapseAllExplorerNodes();

    private async void OnExplorerNodeOpenSubsetWorkspaceClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item })
        {
            await ViewModel.OpenSubsetDiffWorkspaceTabAsync(item);
        }
    }

    private async void OnExplorerNodeFilterCanvasClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item })
        {
            await ViewModel.ApplyCanvasSubsetFilterAsync(item);
        }
    }

    private void OnExplorerNodeClearCanvasFilterClicked(object sender, RoutedEventArgs args) =>
        ViewModel.ClearCanvasSubsetFilter();

    private void OnExplorerTreeNodeDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item } ||
            string.IsNullOrWhiteSpace(item.Path))
        {
            return;
        }

        args.AllowedOperations = DataPackageOperation.Copy;
        args.Data.RequestedOperation = DataPackageOperation.Copy;
        args.Data.Properties.Title = Path.GetFileName(item.Path);
        args.Data.Properties.Description = item.Path;
        args.Data.SetText((item.IsFile ? EditorCanvasDragPathPrefix : EditorCanvasDragFolderPathPrefix) + item.Path);
    }

    private async void OnExplorerNodeOpenDiffTabClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item })
        {
            await ViewModel.OpenFileDiffTabAsync(item, ViewModels.FileDiffDisplayMode.DiffOnly);
        }
    }

    private async void OnExplorerNodeOpenFullFileTabClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item })
        {
            await ViewModel.OpenFileDiffTabAsync(item, ViewModels.FileDiffDisplayMode.FullFile);
        }
    }

    private async void OnExplorerNodeAddToEditorCanvasClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item })
        {
            await ViewModel.AddExplorerNodeToEditorCanvasAsync(item);
        }
    }

    private async void OnExplorerNodeAddToNewEditorCanvasClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item })
        {
            await ViewModel.AddExplorerNodeToNewEditorCanvasAsync(item);
        }
    }

    private async void OnExplorerNodeOpenBlameTabClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item })
        {
            await ViewModel.OpenBlameTabAsync(item);
        }
    }

    private void OnExplorerNodeOpenSymbolsGraphClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item })
        {
            ViewModel.OpenSemanticMapForExplorerNode(item);
        }
    }

    private async void OnGitReferenceTreeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ViewModel.IsUpdatingGitReferenceTree)
        {
            return;
        }

        if (suppressNextGitReferenceSelection)
        {
            suppressNextGitReferenceSelection = false;
            return;
        }

        if (sender is ListView { SelectedItem: ViewModels.GitReferenceTreeItemViewModel item })
        {
            await ViewModel.SelectGitReferenceNodeAsync(item);
        }
    }

    private async void OnGitReferenceTreeKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not ListView { SelectedItem: ViewModels.GitReferenceTreeItemViewModel item })
        {
            return;
        }

        if (args.Key == VirtualKey.Enter)
        {
            await ViewModel.SelectGitReferenceNodeAsync(item);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Space && item.HasChildren)
        {
            ViewModel.ToggleGitReferenceNode(item);
            args.Handled = true;
        }
    }

    private void OnGitReferenceNodeDisclosureClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.GitReferenceTreeItemViewModel item })
        {
            ViewModel.ToggleGitReferenceNode(item);
        }
    }

    private void OnGitReferenceTreeItemPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        var point = args.GetCurrentPoint(element);
        if (point.Properties.IsRightButtonPressed)
        {
            suppressNextGitReferenceSelection = true;
        }
    }

    private void OnGitReferenceTreeItemRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: ViewModels.GitReferenceTreeItemViewModel item } element)
        {
            return;
        }

        var menu = new MenuFlyout();
        AddGitReferenceClipboardMenuItems(menu, item);
        if (item.IsSelectableReference)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var workspaceItem = new MenuFlyoutItem
            {
                Text = item.PullRequest is { } reviewRequest
                    ? $"Open {reviewRequest.KindText} workspace tab"
                    : "Open branch workspace tab",
                Tag = item
            };
            workspaceItem.Click += OnGitReferenceOpenWorkspaceClicked;
            menu.Items.Add(workspaceItem);

            var historyItem = new MenuFlyoutItem
            {
                Text = "Open history tab",
                Tag = item
            };
            historyItem.Click += OnGitReferenceOpenHistoryClicked;
            menu.Items.Add(historyItem);
        }

        if (item.HasChildren)
        {
            var toggleItem = new MenuFlyoutItem
            {
                Text = item.IsExpanded ? "Collapse" : "Expand",
                Tag = item
            };
            toggleItem.Click += (_, _) => ViewModel.ToggleGitReferenceNode(item);
            menu.Items.Add(toggleItem);
        }

        if (menu.Items.Count == 0)
        {
            return;
        }

        menu.ShowAt(element, new FlyoutShowOptions { Position = args.GetPosition(element) });
        _ = DispatcherQueue.TryEnqueue(() => suppressNextGitReferenceSelection = false);
        args.Handled = true;
    }

    private async void OnGitReferenceOpenHistoryClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.GitReferenceTreeItemViewModel item })
        {
            await ViewModel.OpenGitHistoryTabAsync(item);
        }
    }

    private async void OnGitReferenceOpenWorkspaceClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.GitReferenceTreeItemViewModel item })
        {
            await ViewModel.OpenGitWorkspaceTabAsync(item);
        }
    }

    private void OnDiffCanvasNodeNavigationRequested(object? sender, DiffCanvasNodeNavigationRequestedEventArgs args)
    {
        ViewModel.RevealDocumentInExplorer(args.DocumentId);
    }

    private async void OnDiffCanvasNodeDiffTabRequested(object? sender, DiffCanvasNodeDiffTabRequestedEventArgs args)
    {
        await ViewModel.OpenFileDiffTabAsync(
            args.DocumentId,
            args.ShowFullFile ? ViewModels.FileDiffDisplayMode.FullFile : ViewModels.FileDiffDisplayMode.DiffOnly);
    }

    private async void OnDiffCanvasNodeBlameTabRequested(object? sender, DiffCanvasNodeBlameTabRequestedEventArgs args)
    {
        await ViewModel.OpenBlameTabAsync(args.DocumentId);
    }

    private void OnDiffCanvasNodeSymbolGraphRequested(object? sender, DiffCanvasNodeSymbolGraphRequestedEventArgs args)
    {
        ViewModel.OpenSemanticMapForDocumentId(args.DocumentId);
    }

    private async void OnDiffCanvasNodeFullFileViewRequested(object? sender, DiffCanvasNodeFullFileViewRequestedEventArgs args)
    {
        var canvas = sender as DiffCanvasControl;
        await ViewModel.ToggleNodeFullFileViewAsync(args.DocumentId, canvas?.Scene);
        (canvas ?? DiffCanvas).InvalidateScene();
    }

    private void OnDiffCanvasNodeFullFileViewResetRequested(object? sender, DiffCanvasNodeFullFileViewResetRequestedEventArgs args)
    {
        var canvas = sender as DiffCanvasControl;
        ViewModel.ClearNodeFullFileViewOverride(args.DocumentId, canvas?.Scene);
        (canvas ?? DiffCanvas).InvalidateScene();
    }

    private async void OnDiffCanvasNodeEditingRequested(object? sender, DiffCanvasNodeEditingRequestedEventArgs args)
    {
        var canvas = sender as DiffCanvasControl;
        await ViewModel.ToggleNodeEditingAsync(args.DocumentId, canvas?.Scene);
        (canvas ?? DiffCanvas).InvalidateScene();
    }

    private void OnDiffCanvasNodeEditingResetRequested(object? sender, DiffCanvasNodeEditingResetRequestedEventArgs args)
    {
        var canvas = sender as DiffCanvasControl;
        ViewModel.ClearNodeEditingOverride(args.DocumentId, canvas?.Scene);
        (canvas ?? DiffCanvas).InvalidateScene();
    }

    private async void OnDiffCanvasAnnotationInteractionRequested(object? sender, DiffCanvasAnnotationInteractionRequestedEventArgs args)
    {
        if (args.Annotation.Kind == DiffAnnotationKind.HistoryBlame)
        {
            await ViewModel.OpenBlameTabAsync(args.Annotation.DocumentId.Value);
            return;
        }

        FocusCanvas(ViewModel.FocusAnnotation(args.Annotation));
    }

    private void OnEditorCanvasDragOver(object sender, DragEventArgs args)
    {
        if (args.DataView.Contains(StandardDataFormats.StorageItems) ||
            args.DataView.Contains(StandardDataFormats.Text))
        {
            args.AcceptedOperation = DataPackageOperation.Copy;
            args.DragUIOverride.Caption = "Add files or folders to editor canvas";
            args.DragUIOverride.IsCaptionVisible = true;
            args.Handled = true;
        }
    }

    private async void OnEditorCanvasDrop(object sender, DragEventArgs args)
    {
        if (!args.DataView.Contains(StandardDataFormats.StorageItems) &&
            !args.DataView.Contains(StandardDataFormats.Text))
        {
            return;
        }

        args.Handled = true;
        var deferral = args.GetDeferral();
        try
        {
            var paths = new List<string>();
            var shouldAutoLayoutByFolder = false;
            if (args.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await args.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    shouldAutoLayoutByFolder |= await AddEditorCanvasStorageItemPathsAsync(item, paths);
                }
            }

            if (args.DataView.Contains(StandardDataFormats.Text))
            {
                shouldAutoLayoutByFolder |= AddEditorCanvasDragPaths(await args.DataView.GetTextAsync(), paths);
            }

            if (paths.Count == 0)
            {
                ViewModel.ReportInteractionError("Drop one or more files or folders onto the editor canvas");
                return;
            }

            var tab = sender is FrameworkElement { DataContext: ViewModels.WorkspaceTabViewModel workspaceTab }
                ? workspaceTab
                : ViewModel.SelectedWorkspaceTab;
            Point2? dropPoint = null;
            if (sender is DiffCanvasControl canvas && tab?.EditorCanvas?.Scene is { } scene)
            {
                var position = args.GetPosition(canvas);
                dropPoint = scene.Camera.ScreenToWorld(new Point2(position.X, position.Y));
            }

            await ViewModel.AddFilesToEditorCanvasAsync(
                paths,
                tab,
                shouldAutoLayoutByFolder ? null : dropPoint,
                CancellationToken.None,
                autoLayoutByFolder: shouldAutoLayoutByFolder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ViewModel.ReportInteractionError(exception.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnEditorCanvasNodeNavigationRequested(object? sender, DiffCanvasNodeNavigationRequestedEventArgs args)
    {
        ViewModel.RevealDocumentInExplorer(args.DocumentId);
    }

    private static async Task<bool> AddEditorCanvasStorageItemPathsAsync(IStorageItem item, List<string> paths)
    {
        switch (item)
        {
            case StorageFile file when !string.IsNullOrWhiteSpace(file.Path):
                paths.Add(file.Path);
                return false;

            case StorageFolder folder:
                if (!string.IsNullOrWhiteSpace(folder.Path))
                {
                    paths.Add(folder.Path);
                }
                else
                {
                    await AddEditorCanvasStorageFolderPathsAsync(folder, paths);
                }

                return true;
        }

        return false;
    }

    private static async Task AddEditorCanvasStorageFolderPathsAsync(StorageFolder folder, List<string> paths)
    {
        var children = await folder.GetItemsAsync();
        foreach (var child in children)
        {
            await AddEditorCanvasStorageItemPathsAsync(child, paths);
        }
    }

    private static bool AddEditorCanvasDragPaths(string text, List<string> paths)
    {
        var containsFolder = false;
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(EditorCanvasDragFolderPathPrefix, StringComparison.Ordinal))
            {
                AddEditorCanvasDragPath(line[EditorCanvasDragFolderPathPrefix.Length..], paths);
                containsFolder = true;
            }
            else if (line.StartsWith(EditorCanvasDragPathPrefix, StringComparison.Ordinal))
            {
                AddEditorCanvasDragPath(line[EditorCanvasDragPathPrefix.Length..], paths);
            }
        }

        return containsFolder;
    }

    private static void AddEditorCanvasDragPath(string path, List<string> paths)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            paths.Add(path);
        }
    }

    private void OnSemanticSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ListView { SelectedItem: ViewModels.SemanticNavigationItemViewModel item })
        {
            FocusCanvas(ViewModel.FocusSemanticItem(item));
        }
    }

    private void OnSymbolScopeFilterClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.SymbolScopeFilterViewModel filter })
        {
            ViewModel.SetSymbolScopeFilter(filter);
        }
    }

    private void OnSymbolKindFacetClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.SemanticSymbolKindFacetViewModel facet })
        {
            ViewModel.SetSymbolKindFilter(facet);
        }
    }

    private void OnSymbolDocumentFacetClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.SemanticSymbolDocumentFacetViewModel facet })
        {
            FocusCanvas(ViewModel.SetSymbolDocumentFilter(facet));
        }
    }

    private void OnOpenFilteredSymbolGraphClicked(object sender, RoutedEventArgs args)
    {
        ViewModel.OpenSymbolGraphFromCurrentFilters();
    }

    private void OnOpenFilteredSemanticMapClicked(object sender, RoutedEventArgs args)
    {
        ViewModel.OpenSemanticMapFromCurrentFilters();
    }

    private void OnSemanticItemOpenSymbolGraphClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.SemanticNavigationItemViewModel item })
        {
            ViewModel.OpenSymbolGraphForSymbol(item);
        }
    }

    private void OnSemanticItemOpenSemanticMapClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.SemanticNavigationItemViewModel item })
        {
            ViewModel.OpenSemanticMapForSymbol(item);
        }
    }

    private void OnSymbolDocumentFacetOpenGraphClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.SemanticSymbolDocumentFacetViewModel facet })
        {
            ViewModel.OpenSymbolGraphForDocumentFacet(facet);
        }
    }

    private void OnSymbolDocumentFacetOpenMapClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.SemanticSymbolDocumentFacetViewModel facet })
        {
            ViewModel.OpenSemanticMapForDocumentFacet(facet);
        }
    }

    private void OnSymbolGraphResetFiltersClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: ViewModels.WorkspaceTabViewModel { SymbolGraph: { } symbolGraph } })
        {
            symbolGraph.ResetFilters();
        }
    }

    private void OnSymbolClearFiltersClicked(object sender, RoutedEventArgs args) => ViewModel.ClearSymbolFilters();

    private void OnSemanticItemsKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter && sender is ListView { SelectedItem: ViewModels.SemanticNavigationItemViewModel item })
        {
            FocusCanvas(ViewModel.FocusSemanticItem(item));
            args.Handled = true;
        }
    }

    private void FocusCanvas(ViewModels.FocusRequest? focusRequest)
    {
        ViewModel.SelectGraphWorkspaceTab(focusRequest?.DocumentId);
        if (focusRequest is not null && !DiffCanvas.FocusDocument(focusRequest.DocumentId, focusRequest.Line))
        {
            ViewModel.ReportInteractionError($"Could not focus {focusRequest.DocumentId}");
        }
    }

    private void FocusActiveCanvas(ViewModels.FocusRequest? focusRequest)
    {
        if (focusRequest is null)
        {
            return;
        }

        var canvas = GetActiveWorkspaceCanvas();
        if (canvas is null)
        {
            ViewModel.ReportInteractionError("No active canvas is available for navigation");
            return;
        }

        if (!canvas.FocusDocument(focusRequest.DocumentId, focusRequest.Line))
        {
            ViewModel.ReportInteractionError($"Could not focus {focusRequest.DocumentId} in the active workspace");
        }
    }

    private void AddFileDiffClipboardMenuItems(MenuFlyout menu, ViewModels.FileDiffTabViewModel fileDiff)
    {
        var copyPathItem = new MenuFlyoutItem { Text = "Copy file path" };
        copyPathItem.Click += (_, _) => CopyTextToClipboard(fileDiff.Path, "file path");
        menu.Items.Add(copyPathItem);

        var copyFullPathItem = new MenuFlyoutItem { Text = "Copy full file path" };
        copyFullPathItem.Click += (_, _) => CopyTextToClipboard(BuildFileDiffFullPath(fileDiff), "full file path");
        menu.Items.Add(copyFullPathItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var copyDisplayedContentsItem = new MenuFlyoutItem { Text = "Copy displayed contents" };
        copyDisplayedContentsItem.Click += (_, _) => CopyTextToClipboard(BuildFileDiffDisplayedText(fileDiff), "displayed file contents");
        menu.Items.Add(copyDisplayedContentsItem);

        var copyFullFileContentsItem = new MenuFlyoutItem { Text = "Copy full file contents" };
        copyFullFileContentsItem.Click += (_, _) => CopyTextToClipboard(BuildFileDiffFullFileText(fileDiff), "full file contents");
        menu.Items.Add(copyFullFileContentsItem);

        var copyDiffContentsItem = new MenuFlyoutItem { Text = "Copy diff contents" };
        copyDiffContentsItem.Click += (_, _) => CopyTextToClipboard(BuildLinesText(fileDiff.CurrentDiffOnlyLines), "diff contents");
        menu.Items.Add(copyDiffContentsItem);
    }

    private void AddExplorerClipboardMenuItems(MenuFlyout menu, ViewModels.FileExplorerNodeViewModel item)
    {
        var copyPathItem = new MenuFlyoutItem { Text = item.IsFile ? "Copy file path" : "Copy folder path" };
        copyPathItem.Click += (_, _) => CopyTextToClipboard(ViewModel.GetExplorerNodePathText(item), item.IsFile ? "file path" : "folder path");
        menu.Items.Add(copyPathItem);

        var copyFullPathItem = new MenuFlyoutItem { Text = item.IsFile ? "Copy full file path" : "Copy full folder path" };
        copyFullPathItem.Click += (_, _) => CopyTextToClipboard(ViewModel.GetExplorerNodeFullPathText(item), item.IsFile ? "full file path" : "full folder path");
        menu.Items.Add(copyFullPathItem);

        if (item.IsFile)
        {
            var copyContentItem = new MenuFlyoutItem { Text = "Copy file contents" };
            copyContentItem.Click += async (_, _) =>
            {
                var text = await ViewModel.LoadExplorerNodeContentTextAsync(item);
                CopyTextToClipboard(text, "file contents");
            };
            menu.Items.Add(copyContentItem);
        }
        else
        {
            var copyDescendantPathsItem = new MenuFlyoutItem { Text = "Copy descendant file paths" };
            copyDescendantPathsItem.Click += (_, _) => CopyTextToClipboard(ViewModel.GetExplorerNodeChildPathsText(item), "descendant file paths");
            menu.Items.Add(copyDescendantPathsItem);
        }
    }

    private void AddGitReferenceClipboardMenuItems(MenuFlyout menu, ViewModels.GitReferenceTreeItemViewModel item)
    {
        var copyPathItem = new MenuFlyoutItem { Text = item.IsSelectableReference ? "Copy reference path" : "Copy item path" };
        copyPathItem.Click += (_, _) => CopyTextToClipboard(BuildGitReferencePathText(item), "git reference path");
        menu.Items.Add(copyPathItem);

        var copyDetailsItem = new MenuFlyoutItem { Text = "Copy reference details" };
        copyDetailsItem.Click += (_, _) => CopyTextToClipboard(BuildGitReferenceDetailsText(item), "git reference details");
        menu.Items.Add(copyDetailsItem);
    }

    private void CopyTextToClipboard(string? text, string description)
    {
        if (string.IsNullOrEmpty(text))
        {
            ViewModel.ReportInteractionInfo($"Nothing to copy for {description}");
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            ViewModel.ReportInteractionInfo($"Copied {description}");
        }
        catch (Exception exception)
        {
            ViewModel.ReportInteractionError($"Clipboard copy failed: {exception.Message}");
        }
    }

    private static string BuildFileDiffDisplayedText(ViewModels.FileDiffTabViewModel fileDiff) =>
        fileDiff.IsFullFileMode
            ? BuildFileDiffFullFileText(fileDiff)
            : BuildLinesText(fileDiff.CurrentDiffOnlyLines);

    private static string BuildFileDiffFullFileText(ViewModels.FileDiffTabViewModel fileDiff) =>
        !string.IsNullOrEmpty(fileDiff.FullText)
            ? fileDiff.FullText
            : BuildLinesText(fileDiff.CurrentFullFileLines);

    private static string BuildLinesText(ImmutableArray<DiffLine> lines)
    {
        if (lines.IsDefaultOrEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(capacity: Math.Min(lines.Length * 96, 1024 * 1024));
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                builder.AppendLine();
            }

            builder.Append(lines[i].Text);
        }

        return builder.ToString();
    }

    private static string BuildFileDiffFullPath(ViewModels.FileDiffTabViewModel fileDiff)
    {
        if (Path.IsPathRooted(fileDiff.Path) || string.IsNullOrWhiteSpace(fileDiff.RepositoryPath))
        {
            return fileDiff.Path;
        }

        return Path.GetFullPath(Path.Combine(fileDiff.RepositoryPath, fileDiff.Path.Replace('\\', '/')));
    }

    private static string BuildGitReferencePathText(ViewModels.GitReferenceTreeItemViewModel item)
    {
        if (item.Branch is { } branch)
        {
            return branch.ReferenceName;
        }

        if (item.PullRequest is { } reviewRequest)
        {
            var headReference = string.IsNullOrWhiteSpace(reviewRequest.RemoteName)
                ? reviewRequest.HeadRefName
                : $"{reviewRequest.RemoteName}/{reviewRequest.HeadRefName}";
            return $"{reviewRequest.BaseReferenceName}...{headReference}";
        }

        const string remotePrefix = "remote:";
        return item.Id.StartsWith(remotePrefix, StringComparison.Ordinal)
            ? item.Id[remotePrefix.Length..]
            : item.DisplayName;
    }

    private static string BuildGitReferenceDetailsText(ViewModels.GitReferenceTreeItemViewModel item)
    {
        var builder = new StringBuilder();
        builder.AppendLine(item.DisplayName);

        if (!string.IsNullOrWhiteSpace(item.DetailText))
        {
            builder.AppendLine(item.DetailText);
        }

        if (item.Branch is { } branch)
        {
            builder.Append("reference: ").AppendLine(branch.ReferenceName);
            builder.Append("remote: ").AppendLine(string.IsNullOrWhiteSpace(branch.RemoteName) ? "local" : branch.RemoteName);
            builder.Append("kind: ").AppendLine(branch.IsRemote ? "remote branch" : "local branch");
            builder.Append("current: ").AppendLine(branch.IsCurrent ? "yes" : "no");
            builder.Append("default: ").AppendLine(branch.IsDefault ? "yes" : "no");
        }
        else if (item.PullRequest is { } reviewRequest)
        {
            builder.Append("kind: ").AppendLine(reviewRequest.KindText);
            builder.Append("number: ").AppendLine(reviewRequest.NumberText);
            builder.Append("title: ").AppendLine(reviewRequest.Title);
            builder.Append("state: ").AppendLine(reviewRequest.State.ToString());
            builder.Append("base: ").AppendLine(reviewRequest.BaseReferenceName);
            builder.Append("head: ").Append(reviewRequest.HeadRepository).Append(':').AppendLine(reviewRequest.HeadRefName);
            builder.Append("remote: ").AppendLine(string.IsNullOrWhiteSpace(reviewRequest.RemoteName) ? "origin" : reviewRequest.RemoteName);
        }
        else
        {
            builder.Append("item: ").AppendLine(item.Id);
            builder.Append("kind: ").AppendLine(item.Kind.ToString());
            builder.Append("children: ").AppendLine(item.Count.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString().TrimEnd();
    }

    private static string TrimMenuText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "symbol";
        }

        return value.Length <= 42 ? value : $"{value[..18]}...{value[^18..]}";
    }

    private static void KeepSelectedToggleChecked(object sender)
    {
        if (sender is ToggleButton toggleButton)
        {
            toggleButton.IsChecked = true;
        }
    }
}
