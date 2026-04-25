using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using SemanticDiff.Core;
using Windows.Storage.Pickers;
using System.ComponentModel;

namespace SemanticDiff.App;

public sealed partial class MainPage : Page
{
    private const double MinimumLeftPaneWidth = 220;
    private const double MaximumLeftPaneWidth = 520;
    private const double MinimumCanvasWidth = 480;

    private bool isPaneSplitterDragging;
    private bool isPaneSplitterPointerOver;
    private double paneSplitterStartX;
    private double paneSplitterStartWidth;

    public ViewModels.MainViewModel ViewModel { get; } = new();

    public MainPage()
    {
        this.InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        DiffCanvas.NodeNavigationRequested += OnDiffCanvasNodeNavigationRequested;
        ApplyRequestedTheme();
        ApplyLeftPaneWidth(ViewModel.LeftPaneWidth);
        RootShellGrid.SizeChanged += OnRootShellGridSizeChanged;
        Unloaded += OnUnloaded;
    }

    private async void OnUnloaded(object sender, RoutedEventArgs args)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        DiffCanvas.NodeNavigationRequested -= OnDiffCanvasNodeNavigationRequested;
        RootShellGrid.SizeChanged -= OnRootShellGridSizeChanged;
        await ViewModel.DisposeAsync();
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
    }

    private void ApplyRequestedTheme()
    {
        RequestedTheme = ViewModel.IsLightThemeEnabled ? ElementTheme.Light : ElementTheme.Dark;
    }

    private void OnRootShellGridSizeChanged(object sender, SizeChangedEventArgs args)
    {
        ApplyLeftPaneWidth(LeftPaneColumn.ActualWidth > 0 ? LeftPaneColumn.ActualWidth : ViewModel.LeftPaneWidth);
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

    private void ApplyLeftPaneWidth(double requestedWidth)
    {
        var width = ClampLeftPaneWidth(requestedWidth);
        LeftPaneColumn.Width = new GridLength(width);
    }

    private double ClampLeftPaneWidth(double requestedWidth)
    {
        var shellWidth = RootShellGrid.ActualWidth;
        var splitterWidth = PaneSplitterColumn.ActualWidth > 0 ? PaneSplitterColumn.ActualWidth : PaneSplitterColumn.Width.Value;
        var maximumFromCanvas = shellWidth > 0 ? shellWidth - splitterWidth - MinimumCanvasWidth : MaximumLeftPaneWidth;
        var maximumWidth = Math.Max(MinimumLeftPaneWidth, Math.Min(MaximumLeftPaneWidth, maximumFromCanvas));
        return Math.Clamp(double.IsFinite(requestedWidth) ? requestedWidth : ViewModel.LeftPaneWidth, MinimumLeftPaneWidth, maximumWidth);
    }

    private void OnFitClicked(object sender, RoutedEventArgs args)
    {
        DiffCanvas.FitToScene();
    }

    private void OnReloadClicked(object sender, RoutedEventArgs args)
    {
        ViewModel.ReloadRepository();
    }

    private void OnPreviousChangeClicked(object sender, RoutedEventArgs args)
    {
        FocusCanvas(ViewModel.FocusPreviousChange());
    }

    private void OnNextChangeClicked(object sender, RoutedEventArgs args)
    {
        FocusCanvas(ViewModel.FocusNextChange());
    }

    private async void OnOpenRepositoryClicked(object sender, RoutedEventArgs args)
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

    private async void OnLayoutClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.RelayoutAsync(DiffCanvas.Scene);
        DiffCanvas.Scene = ViewModel.Scene;
        DiffCanvas.FitToScene();
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

    private async void OnStageSelectedFileClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.StageSelectedFileAsync();
    }

    private async void OnUnstageSelectedFileClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.UnstageSelectedFileAsync();
    }

    private void OnExplorerTreeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ListView { SelectedItem: ViewModels.FileExplorerNodeViewModel item })
        {
            FocusCanvas(ViewModel.FocusExplorerNode(item));
        }
    }

    private void OnExplorerNodeDisclosureClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item })
        {
            ViewModel.ToggleExplorerNode(item);
        }
    }

    private void OnExplorerNodeContextNavigateClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item })
        {
            FocusCanvas(ViewModel.FocusExplorerNode(item));
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
            Text = item.ContextMenuPrimaryText,
            Tag = item
        };
        primaryItem.Click += OnExplorerNodeContextNavigateClicked;
        menu.Items.Add(primaryItem);

        menu.ShowAt(element, new FlyoutShowOptions { Position = args.GetPosition(element) });
        args.Handled = true;
    }

    private void OnDiffCanvasNodeNavigationRequested(object? sender, Views.DiffCanvasNodeNavigationRequestedEventArgs args)
    {
        ViewModel.RevealDocumentInExplorer(args.DocumentId);
    }

    private void OnSemanticSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ListView { SelectedItem: ViewModels.SemanticNavigationItemViewModel item })
        {
            FocusCanvas(ViewModel.FocusSemanticItem(item));
        }
    }

    private void FocusCanvas(ViewModels.FocusRequest? focusRequest)
    {
        if (focusRequest is not null && !DiffCanvas.FocusDocument(focusRequest.DocumentId, focusRequest.Line))
        {
            ViewModel.ReportInteractionError($"Could not focus {focusRequest.DocumentId}");
        }
    }

    private static void KeepSelectedToggleChecked(object sender)
    {
        if (sender is ToggleButton toggleButton)
        {
            toggleButton.IsChecked = true;
        }
    }
}
