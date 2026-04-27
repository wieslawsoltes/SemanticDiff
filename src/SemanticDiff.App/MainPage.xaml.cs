using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using SemanticDiff.Core;
using SemanticDiff.Rendering;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
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
        RegisterKeyboardAccelerators();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        DiffCanvas.NodeNavigationRequested += OnDiffCanvasNodeNavigationRequested;
        DiffCanvas.NodeDiffTabRequested += OnDiffCanvasNodeDiffTabRequested;
        DiffCanvas.NodeBlameTabRequested += OnDiffCanvasNodeBlameTabRequested;
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
        RegisterAccelerator(VirtualKey.L, control, RelayoutAndFitAsync);
        RegisterAccelerator(VirtualKey.Number0, control, () => RunSyncCommand(DiffCanvas.FitToScene));
        RegisterAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, () => RunSyncCommand(ViewModel.CancelCurrentOperation), () => ViewModel.IsBusy, includePlatformCommandVariant: false);
        RegisterAccelerator(VirtualKey.F7, VirtualKeyModifiers.None, () => RunSyncCommand(() => FocusCanvas(ViewModel.FocusPreviousChange())), () => ViewModel.HasNavigableChanges, includePlatformCommandVariant: false);
        RegisterAccelerator(VirtualKey.F8, VirtualKeyModifiers.None, () => RunSyncCommand(() => FocusCanvas(ViewModel.FocusNextChange())), () => ViewModel.HasNavigableChanges, includePlatformCommandVariant: false);

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

        RegisterAccelerator(VirtualKey.R, controlShift, () => RunCanvasNodeCommand(DiffCanvas.RevealSelectedNode, "Select a node before revealing it"));
        RegisterAccelerator(VirtualKey.B, controlShift, () => RunCanvasNodeCommand(DiffCanvas.OpenSelectedNodeBlameTab, "Select a node before opening blame"));
        RegisterAccelerator(VirtualKey.N, controlShift, () => RunCanvasNodeCommand(DiffCanvas.FocusSelectedNode, "Select a node before focusing it"));
        RegisterAccelerator(VirtualKey.P, controlAlt, () => RunCanvasNodeCommand(DiffCanvas.ToggleSelectedNodePin, "Select a node before pinning it"));
        RegisterAccelerator(VirtualKey.Add, control, () => RunCanvasNodeCommand(() => DiffCanvas.AdjustSelectedNodeFontSize(DiffNodeFontSizeAction.Increase), "Select a node before changing font size"));
        RegisterAccelerator(VirtualKey.Subtract, control, () => RunCanvasNodeCommand(() => DiffCanvas.AdjustSelectedNodeFontSize(DiffNodeFontSizeAction.Decrease), "Select a node before changing font size"));
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
        DiffCanvas.NodeNavigationRequested -= OnDiffCanvasNodeNavigationRequested;
        DiffCanvas.NodeDiffTabRequested -= OnDiffCanvasNodeDiffTabRequested;
        DiffCanvas.NodeBlameTabRequested -= OnDiffCanvasNodeBlameTabRequested;
        DiffCanvas.AnnotationInteractionRequested -= OnDiffCanvasAnnotationInteractionRequested;
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
        else if (args.PropertyName == nameof(ViewModel.Scene))
        {
            DiffCanvas.Scene = ViewModel.Scene;
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

    private async Task RelayoutAndFitAsync()
    {
        await ViewModel.RelayoutAsync(DiffCanvas.Scene);
        DiffCanvas.Scene = ViewModel.Scene;
        DiffCanvas.FitToScene();
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
        await ViewModel.SetLayoutModeAsync(layoutMode, DiffCanvas.Scene);
        DiffCanvas.Scene = ViewModel.Scene;
        DiffCanvas.FitToScene();
    }

    private async Task SetGroupingModeFromAcceleratorAsync(GraphGroupingMode groupingMode)
    {
        await ViewModel.SetGroupingModeAsync(groupingMode);
        DiffCanvas.Scene = ViewModel.Scene;
    }

    private Task RunCanvasNodeCommand(Func<bool> command, string missingSelectionMessage)
    {
        if (!command())
        {
            ViewModel.ReportInteractionError(missingSelectionMessage);
        }

        return Task.CompletedTask;
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
        await OpenRepositoryFromPickerAsync();
    }

    private async void OnLayoutClicked(object sender, RoutedEventArgs args)
    {
        await RelayoutAndFitAsync();
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

    private async void OnLayoutModeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: ViewModels.LayoutModeOptionViewModel option })
        {
            return;
        }

        await ViewModel.SetLayoutModeAsync(option.Mode, DiffCanvas.Scene);
        DiffCanvas.Scene = ViewModel.Scene;
        DiffCanvas.FitToScene();
    }

    private async void OnGroupingModeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: ViewModels.GroupingModeOptionViewModel option })
        {
            return;
        }

        await ViewModel.SetGroupingModeAsync(option.Mode);
        DiffCanvas.Scene = ViewModel.Scene;
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

    private void OnBlameTimelineToggleClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.WorkspaceTabViewModel tab })
        {
            ViewModel.ToggleBlameTimeline(tab);
        }
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

        var package = new DataPackage();
        package.SetText(item.CommitId);
        Clipboard.SetContent(package);
        ViewModel.ReportGitHistoryCommitHashCopied(item);
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

    private void OnExplorerTreeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ListView { SelectedItem: ViewModels.FileExplorerNodeViewModel item })
        {
            FocusCanvas(ViewModel.FocusExplorerNode(item));
        }
    }

    private void OnExplorerTreeKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not ListView { SelectedItem: ViewModels.FileExplorerNodeViewModel item })
        {
            return;
        }

        if (args.Key == VirtualKey.Enter)
        {
            FocusCanvas(ViewModel.FocusExplorerNode(item));
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

        if (item.IsFile)
        {
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

        menu.ShowAt(element, new FlyoutShowOptions { Position = args.GetPosition(element) });
        args.Handled = true;
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

    private async void OnExplorerNodeOpenBlameTabClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: ViewModels.FileExplorerNodeViewModel item })
        {
            await ViewModel.OpenBlameTabAsync(item);
        }
    }

    private async void OnGitReferenceTreeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
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

    private void OnGitReferenceTreeItemRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: ViewModels.GitReferenceTreeItemViewModel item } element)
        {
            return;
        }

        var menu = new MenuFlyout();
        if (item.IsSelectableReference)
        {
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

    private void OnDiffCanvasNodeNavigationRequested(object? sender, Views.DiffCanvasNodeNavigationRequestedEventArgs args)
    {
        ViewModel.RevealDocumentInExplorer(args.DocumentId);
    }

    private async void OnDiffCanvasNodeDiffTabRequested(object? sender, Views.DiffCanvasNodeDiffTabRequestedEventArgs args)
    {
        await ViewModel.OpenFileDiffTabAsync(
            args.DocumentId,
            args.ShowFullFile ? ViewModels.FileDiffDisplayMode.FullFile : ViewModels.FileDiffDisplayMode.DiffOnly);
    }

    private async void OnDiffCanvasNodeBlameTabRequested(object? sender, Views.DiffCanvasNodeBlameTabRequestedEventArgs args)
    {
        await ViewModel.OpenBlameTabAsync(args.DocumentId);
    }

    private async void OnDiffCanvasAnnotationInteractionRequested(object? sender, Views.DiffCanvasAnnotationInteractionRequestedEventArgs args)
    {
        if (args.Annotation.Kind == DiffAnnotationKind.HistoryBlame)
        {
            await ViewModel.OpenBlameTabAsync(args.Annotation.DocumentId.Value);
            return;
        }

        FocusCanvas(ViewModel.FocusAnnotation(args.Annotation));
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
        ViewModel.SelectGraphWorkspaceTab();
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
