using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Git;
using SemanticDiff.Layout;
using SemanticDiff.Rendering;
using SemanticDiff.Semantics;
using SemanticDiff.Semantics.Roslyn;
using SemanticDiff.Semantics.Xaml;

namespace SemanticDiff.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAppStateStore appStateStore;
    private readonly IRepositoryFileWatcherFactory repositoryFileWatcherFactory;
    private readonly IGitReviewService gitReviewService;
    private readonly IGitBlameService gitBlameService;
    private readonly IReadOnlyList<IDiffAnnotationProvider> annotationProviders = [new BuiltInDiffAnnotationProvider()];
    private readonly SynchronizationContext? synchronizationContext;
    private GraphLayoutResult? previousLayout;
    private ImmutableHashSet<DiffDocumentId> pinnedDocumentIds = ImmutableHashSet<DiffDocumentId>.Empty;
    private ImmutableArray<DiffDocumentSnapshot> currentDocuments = [];
    private SemanticGraph currentSemanticGraph = SemanticGraph.Empty;
    private ImmutableArray<ExplorerItemViewModel> allExplorerItems = [];
    private ImmutableHashSet<string> collapsedExplorerNodePaths = ImmutableHashSet<string>.Empty;
    private ImmutableArray<SemanticNavigationItem> allSemanticNavigationItems = [];
    private ImmutableArray<DiffChangeNavigationItem> changeNavigationItems = [];
    private int currentChangeNavigationIndex = -1;
    private string currentStatusPrefix = "sample fallback";
    private string? currentRepositoryPath;
    private SemanticDiffAppState appState = new();
    private CancellationTokenSource? currentOperation;
    private CancellationTokenSource? currentBlameOperation;
    private CancellationTokenSource? pendingAutoReload;
    private IRepositoryFileWatcher? repositoryFileWatcher;
    private ExplorerItemViewModel? selectedExplorerItem;
    private bool currentDocumentsAreRepositoryDocuments;

    public MainViewModel()
        : this(JsonAppStateStore.CreateDefault())
    {
    }

    public MainViewModel(IAppStateStore appStateStore)
        : this(appStateStore, new FileSystemRepositoryFileWatcherFactory())
    {
    }

    public MainViewModel(IAppStateStore appStateStore, IRepositoryFileWatcherFactory repositoryFileWatcherFactory)
        : this(appStateStore, repositoryFileWatcherFactory, new GitReviewService(), new GitBlameService())
    {
    }

    public MainViewModel(IAppStateStore appStateStore, IRepositoryFileWatcherFactory repositoryFileWatcherFactory, IGitReviewService gitReviewService)
        : this(appStateStore, repositoryFileWatcherFactory, gitReviewService, new GitBlameService())
    {
    }

    public MainViewModel(
        IAppStateStore appStateStore,
        IRepositoryFileWatcherFactory repositoryFileWatcherFactory,
        IGitReviewService gitReviewService,
        IGitBlameService gitBlameService)
    {
        this.appStateStore = appStateStore;
        this.repositoryFileWatcherFactory = repositoryFileWatcherFactory;
        this.gitReviewService = gitReviewService;
        this.gitBlameService = gitBlameService;
        synchronizationContext = SynchronizationContext.Current;
        InitializeSampleDocuments(SampleDiffDocuments.Create());
        _ = LoadRepositoryAsync(loadAppState: true, operationMessage: "Loading repository");
    }

    [ObservableProperty]
    private DiffCanvasScene scene = DiffCanvasScene.FromDocuments([]);

    [ObservableProperty]
    private ImmutableArray<ExplorerItemViewModel> explorerItems = [];

    [ObservableProperty]
    private ImmutableArray<FileExplorerNodeViewModel> explorerTreeItems = [];

    [ObservableProperty]
    private FileExplorerNodeViewModel? selectedExplorerTreeNode;

    [ObservableProperty]
    private int selectedRailTabIndex;

    [ObservableProperty]
    private string statusText = "Loading repository diff...";

    [ObservableProperty]
    private string repositoryName = "SemanticDiff";

    [ObservableProperty]
    private string repositoryContextText = "Sample graph";

    [ObservableProperty]
    private string repositoryPathText = "No repository selected";

    [ObservableProperty]
    private double leftPaneWidth = 260;

    [ObservableProperty]
    private string diffScopeText = "Worktree";

    [ObservableProperty]
    private bool isWorktreeScopeSelected = true;

    [ObservableProperty]
    private bool isUnstagedScopeSelected;

    [ObservableProperty]
    private bool isStagedScopeSelected;

    [ObservableProperty]
    private bool isBranchScopeSelected;

    [ObservableProperty]
    private bool isRangeScopeSelected;

    [ObservableProperty]
    private string baseRefText = string.Empty;

    [ObservableProperty]
    private string headRefText = "HEAD";

    [ObservableProperty]
    private string diffContextModeText = "Changed";

    [ObservableProperty]
    private bool isChangedHunksContextSelected = true;

    [ObservableProperty]
    private bool isFullFileDiffContextSelected;

    [ObservableProperty]
    private bool isCurrentFileContextSelected;

    [ObservableProperty]
    private bool isNoiseFilterEnabled;

    [ObservableProperty]
    private string reviewModeText = "Precise";

    [ObservableProperty]
    private bool isContextFoldingEnabled;

    [ObservableProperty]
    private string contextFoldingText = "Full context";

    [ObservableProperty]
    private bool isAutoRefreshEnabled = true;

    [ObservableProperty]
    private bool isLightThemeEnabled;

    [ObservableProperty]
    private string themeToggleText = "Dark";

    [ObservableProperty]
    private string watchStatusText = "Watch ready";

    [ObservableProperty]
    private string maxInitialGitFilesText = "24";

    [ObservableProperty]
    private string documentCountText = "0 files";

    [ObservableProperty]
    private string semanticEdgeCountText = "0 edges";

    [ObservableProperty]
    private bool isSemanticEdgesEnabled = true;

    [ObservableProperty]
    private string semanticEdgesText = "Edges on";

    [ObservableProperty]
    private string semanticAnalysisModeText = "MSBuild";

    [ObservableProperty]
    private bool isSemanticWorkspaceModeSelected = true;

    [ObservableProperty]
    private bool isSemanticFastModeSelected;

    [ObservableProperty]
    private string visualizationButtonText = "Visuals 7/7";

    [ObservableProperty]
    private string visualizationSummaryText = "Visual layers 7/7";

    [ObservableProperty]
    private bool isGitVisualizationEnabled = true;

    [ObservableProperty]
    private bool isSemanticVisualizationEnabled = true;

    [ObservableProperty]
    private bool isDiagnosticVisualizationEnabled = true;

    [ObservableProperty]
    private bool isReviewVisualizationEnabled = true;

    [ObservableProperty]
    private bool isHistoryVisualizationEnabled = true;

    [ObservableProperty]
    private bool isNavigationVisualizationEnabled = true;

    [ObservableProperty]
    private bool isContextVisualizationEnabled = true;

    [ObservableProperty]
    private string fileSearchText = string.Empty;

    [ObservableProperty]
    private string explorerCountText = "0 files";

    [ObservableProperty]
    private string symbolSearchText = string.Empty;

    [ObservableProperty]
    private string symbolCountText = "0 symbols";

    [ObservableProperty]
    private string impactSummaryText = "Impact 0 symbols";

    [ObservableProperty]
    private string reviewSignalText = "Moved 0 | Noise 0";

    [ObservableProperty]
    private string changeNavigationText = "0 changes";

    [ObservableProperty]
    private bool hasNavigableChanges;

    [ObservableProperty]
    private bool hasSelectedRepositoryFile;

    [ObservableProperty]
    private string selectedFileReviewText = "Select a changed file";

    [ObservableProperty]
    private string reviewActionStatusText = "No file selected";

    [ObservableProperty]
    private string blameSummaryText = "Blame unavailable";

    [ObservableProperty]
    private ImmutableArray<SemanticNavigationItemViewModel> semanticItems = [];

    [ObservableProperty]
    private string diagnosticsCountText = "0 diagnostics";

    [ObservableProperty]
    private string latestDiagnosticText = "No diagnostics";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private string progressText = "Ready";

    [ObservableProperty]
    private ImmutableArray<DiagnosticItemViewModel> diagnostics = [];

    public void ReloadRepository() => _ = LoadRepositoryAsync(loadAppState: false, operationMessage: "Loading repository");

    public async Task OpenRepositoryAsync(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        var operation = BeginOperation("Opening repository");
        try
        {
            var cancellationToken = operation.Token;
            var repositoryDiscovery = new GitRepositoryDiscovery();
            var repositoryRoot = await repositoryDiscovery.DiscoverRootAsync(selectedPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(repositoryRoot))
            {
                AddDiagnostic("Warning", $"No Git repository found at {selectedPath}");
                CompleteOperation(operation, "Repository not found");
                return;
            }

            currentRepositoryPath = repositoryRoot;
            appState = appState with
            {
                RepositoryPath = repositoryRoot,
                LayoutNodes = null
            };
            previousLayout = null;
            pinnedDocumentIds = ImmutableHashSet<DiffDocumentId>.Empty;
            ApplyAppStateToPresentation();
            await SaveOptionsAsync(cancellationToken);
            AddDiagnostic("Info", $"Selected repository {repositoryRoot}");
            CompleteOperation(operation, "Repository selected");
            await LoadRepositoryAsync(loadAppState: false, operationMessage: "Loading selected repository");
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(operation, "Open canceled");
        }
        catch (Exception exception)
        {
            CompleteOperation(operation, "Open failed");
            AddDiagnostic("Error", exception.Message);
        }
    }

    public async Task SetDiffScopeAsync(GitDiffScope diffScope)
    {
        if (appState.DiffScope == diffScope)
        {
            return;
        }

        appState = appState with
        {
            DiffScope = diffScope,
            LayoutNodes = null
        };
        previousLayout = null;
        pinnedDocumentIds = ImmutableHashSet<DiffDocumentId>.Empty;
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Diff scope changed to {diffScope}");
        await LoadRepositoryAsync(loadAppState: false, operationMessage: $"Loading {diffScope} diff");
    }

    public async Task ApplyReferenceOptionsAsync()
    {
        var baseRef = NormalizeRef(BaseRefText);
        var headRef = NormalizeRef(HeadRefText);
        if (string.Equals(appState.BaseRef, baseRef, StringComparison.Ordinal) &&
            string.Equals(appState.HeadRef, headRef, StringComparison.Ordinal))
        {
            await SaveOptionsAsync(CancellationToken.None);
            return;
        }

        appState = appState with
        {
            BaseRef = baseRef,
            HeadRef = headRef,
            LayoutNodes = null
        };
        previousLayout = null;
        pinnedDocumentIds = ImmutableHashSet<DiffDocumentId>.Empty;
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Reference range changed to {FormatReferenceText(appState)}");
        RefreshSceneAnnotations();

        if (appState.DiffScope is GitDiffScope.Branch or GitDiffScope.CommitRange or GitDiffScope.Custom)
        {
            await LoadRepositoryAsync(loadAppState: false, operationMessage: "Reloading reference range");
        }
    }

    public async Task SetAutoRefreshAsync(bool isEnabled)
    {
        appState = appState with { WatchRepositoryChanges = isEnabled };
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);

        if (isEnabled && !string.IsNullOrWhiteSpace(currentRepositoryPath) && Directory.Exists(currentRepositoryPath))
        {
            await RestartRepositoryWatcherAsync(currentRepositoryPath, CancellationToken.None);
            AddDiagnostic("Info", "Automatic refresh enabled");
        }
        else
        {
            await StopRepositoryWatcherAsync();
            WatchStatusText = "Watch off";
            AddDiagnostic("Info", "Automatic refresh disabled");
        }

        RefreshSceneAnnotations();
    }

    public async Task SetDiffContextModeAsync(DiffContextMode contextMode)
    {
        if (appState.DiffContextMode == contextMode)
        {
            ApplyAppStateToPresentation();
            return;
        }

        appState = appState with { DiffContextMode = contextMode };
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Diff context changed to {FormatDiffContextMode(contextMode)}");
        await LoadRepositoryAsync(loadAppState: false, operationMessage: "Reloading diff context");
    }

    public async Task SetReviewModeAsync(bool isNoiseFilterEnabled)
    {
        var nextMode = isNoiseFilterEnabled ? DiffReviewMode.IgnoreWhitespace : DiffReviewMode.Precise;
        if (appState.ReviewMode == nextMode)
        {
            ApplyAppStateToPresentation();
            return;
        }

        appState = appState with { ReviewMode = nextMode };
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Review mode changed to {FormatReviewMode(nextMode)}");
        await LoadRepositoryAsync(loadAppState: false, operationMessage: "Reloading review mode");
    }

    public async Task SetContextFoldingAsync(bool isContextFoldingEnabled)
    {
        if (appState.CollapseUnchangedContext == isContextFoldingEnabled)
        {
            ApplyAppStateToPresentation();
            return;
        }

        appState = appState with { CollapseUnchangedContext = isContextFoldingEnabled };
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", isContextFoldingEnabled ? "Collapsed unchanged context" : "Expanded unchanged context");
        await LoadRepositoryAsync(loadAppState: false, operationMessage: "Reloading context folding");
    }

    public async Task SetThemeAsync(bool isLightThemeEnabled)
    {
        var nextTheme = isLightThemeEnabled ? SemanticDiffThemeMode.Light : SemanticDiffThemeMode.Dark;
        if (appState.ThemeMode == nextTheme)
        {
            ApplyAppStateToPresentation();
            return;
        }

        appState = appState with { ThemeMode = nextTheme };
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Theme changed to {nextTheme}");
    }

    public async Task SetSemanticEdgesAsync(bool isEnabled)
    {
        if (appState.ShowSemanticEdges == isEnabled)
        {
            ApplyAppStateToPresentation();
            return;
        }

        CaptureLayoutState(Scene);
        appState = appState with { ShowSemanticEdges = isEnabled };
        ApplyAppStateToPresentation();
        Scene = CreateScene(currentDocuments, currentSemanticGraph, previousLayout);
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", isEnabled ? "Semantic edges shown" : "Semantic edges hidden");
    }

    public async Task SetSemanticAnalysisModeAsync(SemanticAnalysisMode analysisMode)
    {
        if (appState.SemanticAnalysisMode == analysisMode)
        {
            ApplyAppStateToPresentation();
            return;
        }

        appState = appState with { SemanticAnalysisMode = analysisMode, LayoutNodes = null };
        previousLayout = null;
        pinnedDocumentIds = ImmutableHashSet<DiffDocumentId>.Empty;
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Semantic mode changed to {FormatSemanticAnalysisMode(analysisMode)}");
        await LoadRepositoryAsync(loadAppState: false, operationMessage: "Reloading semantic analysis");
    }

    public async Task SetVisualizationLayerAsync(string layer, bool isEnabled)
    {
        var visibility = appState.EffectiveAnnotationVisibility;
        var nextVisibility = layer switch
        {
            "Git" => visibility with { ShowGitStatus = isEnabled },
            "Semantic" => visibility with { ShowSemantic = isEnabled },
            "Diagnostics" => visibility with { ShowDiagnostics = isEnabled },
            "Review" => visibility with { ShowReview = isEnabled },
            "History" => visibility with { ShowHistory = isEnabled },
            "Navigation" => visibility with { ShowNavigation = isEnabled },
            "Context" => visibility with { ShowContext = isEnabled },
            _ => visibility
        };

        if (nextVisibility == visibility)
        {
            ApplyAppStateToPresentation();
            return;
        }

        appState = appState with { AnnotationVisibility = nextVisibility };
        ApplyAppStateToPresentation();
        RefreshSceneAnnotations();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"{layer} visualization {(isEnabled ? "shown" : "hidden")}");
    }

    public async Task ApplyOptionsAsync()
    {
        if (!int.TryParse(MaxInitialGitFilesText, out var maxInitialGitFiles))
        {
            MaxInitialGitFilesText = appState.MaxInitialGitFiles.ToString(System.Globalization.CultureInfo.InvariantCulture);
            AddDiagnostic("Warning", "Max files must be a number");
            return;
        }

        maxInitialGitFiles = Math.Clamp(maxInitialGitFiles, 1, 500);
        MaxInitialGitFilesText = maxInitialGitFiles.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (appState.MaxInitialGitFiles == maxInitialGitFiles)
        {
            await SaveOptionsAsync(CancellationToken.None);
            return;
        }

        appState = appState with { MaxInitialGitFiles = maxInitialGitFiles };
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Max files changed to {maxInitialGitFiles}");
        await LoadRepositoryAsync(loadAppState: false, operationMessage: "Reloading options");
    }

    public async Task SetLeftPaneWidthAsync(double width)
    {
        var normalizedWidth = NormalizeLeftPaneWidth(width);
        LeftPaneWidth = normalizedWidth;
        if (Math.Abs(appState.LeftPaneWidth - normalizedWidth) < 0.5)
        {
            return;
        }

        appState = appState with { LeftPaneWidth = normalizedWidth };
        await SaveOptionsAsync(CancellationToken.None);
    }

    public void ReportInteractionError(string message) => AddDiagnostic("Error", message);

    public async ValueTask DisposeAsync()
    {
        currentOperation?.Cancel();
        currentBlameOperation?.Cancel();
        pendingAutoReload?.Cancel();
        await StopRepositoryWatcherAsync();
        currentOperation?.Dispose();
        currentBlameOperation?.Dispose();
        currentOperation = null;
        currentBlameOperation = null;
    }

    public void CancelCurrentOperation()
    {
        currentOperation?.Cancel();
        AddDiagnostic("Info", "Cancel requested");
    }

    public FocusRequest? FocusExplorerItem(ExplorerItemViewModel? item)
    {
        if (item is null)
        {
            SelectExplorerItem(null);
            return null;
        }

        SelectExplorerItem(item);
        AddDiagnostic("Info", $"Focused {item.Path}");
        return new FocusRequest(item.DocumentId, null);
    }

    public FocusRequest? FocusExplorerNode(FileExplorerNodeViewModel? node)
    {
        if (node is null)
        {
            SelectExplorerItem(null);
            return null;
        }

        if (!node.IsFile)
        {
            ToggleExplorerNode(node);
            return null;
        }

        var item = allExplorerItems.FirstOrDefault(item => string.Equals(item.DocumentId, node.DocumentId, StringComparison.Ordinal));
        if (item is null)
        {
            AddDiagnostic("Warning", $"No document node for {node.Path}");
            return null;
        }

        return FocusExplorerItem(item);
    }

    public void ToggleExplorerNode(FileExplorerNodeViewModel? node)
    {
        if (node is null || !node.HasChildren)
        {
            return;
        }

        collapsedExplorerNodePaths = collapsedExplorerNodePaths.Contains(node.Path)
            ? collapsedExplorerNodePaths.Remove(node.Path)
            : collapsedExplorerNodePaths.Add(node.Path);
        ApplyExplorerFilter();
    }

    public void RevealDocumentInExplorer(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        var item = allExplorerItems.FirstOrDefault(item => string.Equals(item.DocumentId, documentId, StringComparison.Ordinal));
        if (item is null)
        {
            AddDiagnostic("Warning", $"No file tree item for {documentId}");
            return;
        }

        FileSearchText = string.Empty;
        ExpandAncestors(item.Path);
        ApplyExplorerFilter();
        SelectExplorerItem(item);
        SelectedRailTabIndex = 0;
        AddDiagnostic("Info", $"Revealed {item.Path} in the file tree");
    }

    public Task StageSelectedFileAsync() => RunReviewActionAsync(
        "Staging",
        (repositoryPath, path, cancellationToken) => gitReviewService.StageFileAsync(repositoryPath, path, cancellationToken));

    public Task UnstageSelectedFileAsync() => RunReviewActionAsync(
        "Unstaging",
        (repositoryPath, path, cancellationToken) => gitReviewService.UnstageFileAsync(repositoryPath, path, cancellationToken));

    public FocusRequest? FocusSemanticItem(SemanticNavigationItemViewModel? item)
    {
        if (item is null)
        {
            return null;
        }

        AddDiagnostic("Info", $"Focused {item.DisplayName}");
        return new FocusRequest(item.DocumentId, item.Line);
    }

    public FocusRequest? FocusNextChange() => FocusAdjacentChange(1);

    public FocusRequest? FocusPreviousChange() => FocusAdjacentChange(-1);

    public async Task RelayoutAsync(DiffCanvasScene? currentScene)
    {
        if (currentDocuments.IsDefaultOrEmpty)
        {
            return;
        }

        var operation = BeginOperation("Refreshing layout");
        try
        {
            var cancellationToken = operation.Token;
            CaptureLayoutState(currentScene);
            ReportProgress(0.45, "Running layout", cancellationToken);
            var layout = await LayoutDocumentsAsync(currentDocuments, currentSemanticGraph, cancellationToken);
            previousLayout = layout;
            Scene = CreateScene(currentDocuments, currentSemanticGraph, layout);
            StatusText = $"{currentStatusPrefix} | {currentDocuments.Length} nodes | {currentSemanticGraph.Edges.Length} semantic edges | layout refreshed";
            ReportProgress(0.9, "Saving layout state", cancellationToken);
            await SaveStateAsync(cancellationToken);
            AddDiagnostic("Info", "Layout refreshed");
            CompleteOperation(operation, "Layout ready");
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(operation, "Layout canceled");
            StatusText = "Layout canceled";
        }
        catch (Exception exception)
        {
            CompleteOperation(operation, "Layout failed");
            StatusText = $"Layout failed: {exception.Message}";
            AddDiagnostic("Error", exception.Message);
        }
    }

    private async Task LoadRepositoryAsync(bool loadAppState, string operationMessage, DiffCanvasSceneViewState? preservedSceneState = null)
    {
        var operation = BeginOperation(operationMessage);
        try
        {
            var cancellationToken = operation.Token;
            if (loadAppState)
            {
                ReportProgress(0.05, "Loading app state", cancellationToken);
                appState = await appStateStore.LoadAsync(cancellationToken);
            }

            ApplyAppStateToPresentation();
            ReportProgress(0.1, "Discovering repository", cancellationToken);
            var repositoryDiscovery = new GitRepositoryDiscovery();
            var startPath = !string.IsNullOrWhiteSpace(appState.RepositoryPath) && Directory.Exists(appState.RepositoryPath)
                ? appState.RepositoryPath
                : Environment.CurrentDirectory;
            var repositoryRoot = await repositoryDiscovery.DiscoverRootAsync(startPath, cancellationToken);

            if (string.IsNullOrWhiteSpace(repositoryRoot))
            {
                currentRepositoryPath = null;
                await StopRepositoryWatcherAsync();
                StatusText = "No Git repository found | showing sample diff graph";
                AddDiagnostic("Info", "No Git repository found; using sample graph");
                CompleteOperation(operation, "Sample graph ready");
                return;
            }

            currentRepositoryPath = repositoryRoot;
            var documentService = new GitDiffDocumentService();
            var request = new GitDiffRequest(repositoryRoot, appState.DiffScope, NormalizeRef(appState.BaseRef), NormalizeRef(appState.HeadRef));
            ReportProgress(0.25, "Loading Git diff", cancellationToken);
            var snapshot = await documentService.LoadDocumentsAsync(request, Math.Max(1, appState.MaxInitialGitFiles), appState.DiffContextMode, cancellationToken);

            if (snapshot.Documents.Length == 0)
            {
                currentDocumentsAreRepositoryDocuments = false;
                var emptyScopeText = FormatDiffScope(appState.DiffScope);
                UpdateWorkspaceSummary(
                    Path.GetFileName(repositoryRoot),
                    $"{Path.GetFileName(repositoryRoot)} | no {emptyScopeText} changes | {FormatDiffContextMode(appState.DiffContextMode)} | base {snapshot.GitSnapshot.DefaultBranch ?? "unknown"}",
                    0,
                    0);
                await SaveOptionsAsync(cancellationToken);
                await RestartRepositoryWatcherAsync(repositoryRoot, cancellationToken);
                StatusText = $"{Path.GetFileName(repositoryRoot)} | no {emptyScopeText} changes | showing sample diff graph";
                AddDiagnostic("Info", $"Repository has no {emptyScopeText} changes");
                CompleteOperation(operation, "Sample graph ready");
                return;
            }

            ReportProgress(0.45, "Tokenizing documents", cancellationToken);
            var tokenizationProgress = new Progress<(double Value, string Message)>(update =>
                ReportProgress(0.45 + update.Value * 0.2, update.Message, cancellationToken));
            var reviewedDocuments = DiffReviewDocumentTransformer.Apply(snapshot.Documents, appState.ReviewMode);
            reviewedDocuments = new DiffConflictAnalyzer().Highlight(reviewedDocuments);
            if (appState.CollapseUnchangedContext)
            {
                reviewedDocuments = DiffContextFolder.Apply(reviewedDocuments);
            }

            reviewedDocuments = InlineDiffAnnotator.Annotate(reviewedDocuments);
            var tokenizedDocuments = await TokenizeAsync(reviewedDocuments, cancellationToken, tokenizationProgress);
            await SetDocumentsAsync(
                tokenizedDocuments,
                $"{Path.GetFileName(repositoryRoot)} | {snapshot.GitSnapshot.Files.Length} {FormatDiffScope(appState.DiffScope)} changes | {FormatDiffContextMode(appState.DiffContextMode)} | {FormatReviewMode(appState.ReviewMode)} | {FormatReferenceText(request, snapshot.GitSnapshot.DefaultBranch)}",
                repositoryRoot,
                snapshot.GitSnapshot,
                preservedSceneState,
                cancellationToken);
            CompleteOperation(operation, "Repository diff ready");
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(operation, "Load canceled");
            StatusText = "Load canceled | showing current diff graph";
        }
        catch (Exception exception)
        {
            CompleteOperation(operation, "Load failed");
            StatusText = $"Git load failed: {exception.Message} | showing sample diff graph";
            AddDiagnostic("Error", exception.Message);
        }
    }

    private void InitializeSampleDocuments(ImmutableArray<DiffDocumentSnapshot> documents)
    {
        currentDocuments = documents;
        currentSemanticGraph = SemanticGraph.Empty;
        currentStatusPrefix = "sample fallback";
        UpdateChangeNavigation(documents);
        currentDocumentsAreRepositoryDocuments = false;
        SelectExplorerItem(null);
        RestoreLayoutState(documents);
        Scene = CreateScene(documents, SemanticGraph.Empty, previousLayout);
        SetExplorerItems(documents.Select(document => new ExplorerItemViewModel(document.Metadata.Path, document.Metadata.Status, document.Metadata.Language)).ToImmutableArray());
        UpdateSemanticNavigation(SemanticGraph.Empty, documents);
        UpdateImpactSummary(documents, SemanticGraph.Empty);
        UpdateWorkspaceSummary("SemanticDiff", "Sample fallback", documents.Length, 0);
        StatusText = $"sample fallback | {documents.Length} nodes | loading repository diff";
    }

    private async Task SetDocumentsAsync(
        ImmutableArray<DiffDocumentSnapshot> documents,
        string statusPrefix,
        string repositoryPath,
        GitDiffSnapshot? gitSnapshot,
        DiffCanvasSceneViewState? preservedSceneState,
        CancellationToken cancellationToken)
    {
        var preservedSelectedDocumentId = preservedSceneState?.SelectedDocumentId ?? selectedExplorerItem?.DocumentId;
        currentDocuments = documents;
        currentStatusPrefix = statusPrefix;
        UpdateChangeNavigation(documents);
        currentRepositoryPath = string.IsNullOrWhiteSpace(repositoryPath) ? null : repositoryPath;
        currentDocumentsAreRepositoryDocuments = !string.IsNullOrWhiteSpace(repositoryPath);
        SelectExplorerItem(null);
        RestoreLayoutState(documents, preservedSceneState);

        ReportProgress(0.68, $"Analyzing semantics ({FormatSemanticAnalysisMode(appState.SemanticAnalysisMode)})", cancellationToken);
        var semanticGraph = await AnalyzeSemanticsAsync(repositoryPath, gitSnapshot, documents, appState.SemanticAnalysisMode, cancellationToken);
        currentSemanticGraph = semanticGraph;
        ReportProgress(0.82, "Running graph layout", cancellationToken);
        var layout = await LayoutDocumentsAsync(documents, semanticGraph, cancellationToken);
        previousLayout = layout;
        Scene = CreateScene(documents, semanticGraph, layout);
        if (preservedSceneState is not null)
        {
            Scene.ApplyViewState(preservedSceneState);
            CaptureLayoutState(Scene);
        }

        SetExplorerItems(documents.Select(document => new ExplorerItemViewModel(document.Metadata.Path, document.Metadata.Status, document.Metadata.Language)).ToImmutableArray());
        RestoreSelectedExplorerItem(preservedSelectedDocumentId);
        UpdateSemanticNavigation(semanticGraph, documents);
        var impactSummary = UpdateImpactSummary(documents, semanticGraph);
        UpdateWorkspaceSummary(
            string.IsNullOrWhiteSpace(repositoryPath) ? "SemanticDiff" : Path.GetFileName(repositoryPath),
            statusPrefix,
            documents.Length,
            semanticGraph.Edges.Length);
        StatusText = $"{statusPrefix} | {documents.Length} nodes | {semanticGraph.Edges.Length} semantic edges | {FormatImpactStatus(impactSummary)} | layout ready";
        ReportProgress(0.95, "Saving app state", cancellationToken);
        await SaveStateAsync(cancellationToken);
        await RestartRepositoryWatcherAsync(repositoryPath, cancellationToken);
        AddDiagnostic("Info", preservedSceneState is null
            ? $"Loaded {documents.Length} document nodes, {semanticGraph.Edges.Length} semantic edges, {impactSummary.ChangedSymbolCount} changed symbols"
            : $"Smart refresh synced {documents.Length} document nodes without resetting the canvas view");
    }

    private static async Task<SemanticGraph> AnalyzeSemanticsAsync(
        string repositoryPath,
        GitDiffSnapshot? gitSnapshot,
        ImmutableArray<DiffDocumentSnapshot> documents,
        SemanticAnalysisMode analysisMode,
        CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            var providers = new ISemanticProvider[]
            {
                new CSharpSemanticProvider(),
                new XamlSemanticProvider()
            };
            var filter = new SemanticGraphFilter(MinimumConfidence: 0.65);
            var orchestrator = new SemanticOrchestrator(providers, filter);
            return await orchestrator.AnalyzeAsync(new SemanticAnalysisRequest(repositoryPath, gitSnapshot, documents, analysisMode), cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task<GraphLayoutResult> LayoutDocumentsAsync(
        ImmutableArray<DiffDocumentSnapshot> documents,
        SemanticGraph semanticGraph,
        CancellationToken cancellationToken)
    {
        var previousNodes = previousLayout?.Nodes ?? default;
        var pinnedIds = pinnedDocumentIds;
        return Task.Run(async () =>
        {
            var layoutEngine = new MsaglGraphLayoutEngine();
            return await layoutEngine.LayoutAsync(new GraphLayoutRequest(documents, semanticGraph, new Size2(620, 420), previousNodes, pinnedIds), cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static Task<ImmutableArray<DiffDocumentSnapshot>> TokenizeAsync(
        ImmutableArray<DiffDocumentSnapshot> documents,
        CancellationToken cancellationToken,
        IProgress<(double Value, string Message)> progress)
    {
        return Task.Run(
            () => TokenizeCoreAsync(documents, cancellationToken, (value, message) => progress.Report((value, message))),
            cancellationToken);
    }

    private static async Task<ImmutableArray<DiffDocumentSnapshot>> TokenizeCoreAsync(
        ImmutableArray<DiffDocumentSnapshot> documents,
        CancellationToken cancellationToken,
        Action<double, string> progress)
    {
        const int tokenPageSize = 128;
        var tokenizer = new TextMateDocumentTokenizer(tokenPageSize);
        var documentBuilder = ImmutableArray.CreateBuilder<DiffDocumentSnapshot>(documents.Length);

        for (var documentIndex = 0; documentIndex < documents.Length; documentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = documents[documentIndex];
            progress(documents.Length == 0 ? 1 : (double)documentIndex / documents.Length, $"Tokenizing {document.Metadata.Path}");
            var lineBuilder = ImmutableArray.CreateBuilder<DiffLine>(document.LineCount);
            for (var firstLineIndex = 0; firstLineIndex < document.LineCount; firstLineIndex += tokenPageSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tokenizedLines = await tokenizer.TokenizePageAsync(document, firstLineIndex, tokenPageSize, cancellationToken).ConfigureAwait(false);
                lineBuilder.AddRange(tokenizedLines);
            }

            documentBuilder.Add(document with { Lines = lineBuilder.ToImmutable() });
        }

        progress(1, "Tokenization complete");
        return documentBuilder.ToImmutable();
    }

    private void RestoreLayoutState(ImmutableArray<DiffDocumentSnapshot> documents, DiffCanvasSceneViewState? preservedSceneState = null)
    {
        var documentIds = documents.Select(document => document.Id.Value).ToHashSet(StringComparer.Ordinal);
        if (preservedSceneState is not null)
        {
            var preservedNodes = preservedSceneState.Nodes
                .Where(node => documentIds.Contains(node.DocumentId.Value))
                .Select(node => new DiffNodeLayout(node.DocumentId, node.Bounds, node.IsPinned, node.FontSize))
                .ToImmutableArray();
            if (!preservedNodes.IsDefaultOrEmpty)
            {
                previousLayout = new GraphLayoutResult(preservedNodes);
                pinnedDocumentIds = preservedNodes
                    .Where(node => node.IsPinned)
                    .Select(node => node.DocumentId)
                    .ToImmutableHashSet();
                return;
            }
        }

        var restoredNodes = appState.EffectiveLayoutNodes
            .Where(node => documentIds.Contains(node.DocumentId))
            .Select(node => node.ToLayout())
            .ToImmutableArray();
        previousLayout = restoredNodes.IsDefaultOrEmpty ? null : new GraphLayoutResult(restoredNodes);
        pinnedDocumentIds = restoredNodes
            .Where(node => node.IsPinned)
            .Select(node => node.DocumentId)
            .ToImmutableHashSet();
    }

    private void CaptureLayoutState(DiffCanvasScene? currentScene)
    {
        if (currentScene is null)
        {
            return;
        }

        previousLayout = new GraphLayoutResult(currentScene.GetCurrentLayout());
        pinnedDocumentIds = currentScene.GetPinnedDocumentIds();
    }

    private void RestoreSelectedExplorerItem(string? documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        var item = allExplorerItems.FirstOrDefault(item => string.Equals(item.DocumentId, documentId, StringComparison.Ordinal));
        if (item is not null)
        {
            SelectExplorerItem(item);
        }
    }

    private async Task SaveStateAsync(CancellationToken cancellationToken)
    {
        var layoutNodes = Scene.GetCurrentLayout().Select(DiffNodeLayoutState.FromLayout).ToArray();
        appState = appState with
        {
            RepositoryPath = currentRepositoryPath ?? appState.RepositoryPath,
            DiffScope = appState.DiffScope,
            MaxInitialGitFiles = Math.Max(1, appState.MaxInitialGitFiles),
            WatchRepositoryChanges = IsAutoRefreshEnabled,
            AutoReloadDelayMs = Math.Clamp(appState.AutoReloadDelayMs, 250, 10_000),
            ThemeMode = IsLightThemeEnabled ? SemanticDiffThemeMode.Light : SemanticDiffThemeMode.Dark,
            DiffContextMode = appState.DiffContextMode,
            ReviewMode = appState.ReviewMode,
            CollapseUnchangedContext = appState.CollapseUnchangedContext,
            BaseRef = NormalizeRef(appState.BaseRef),
            HeadRef = NormalizeRef(appState.HeadRef),
            ShowSemanticEdges = IsSemanticEdgesEnabled,
            AnnotationVisibility = appState.EffectiveAnnotationVisibility,
            SemanticAnalysisMode = appState.SemanticAnalysisMode,
            LeftPaneWidth = NormalizeLeftPaneWidth(LeftPaneWidth),
            LayoutNodes = currentDocumentsAreRepositoryDocuments ? layoutNodes : appState.LayoutNodes
        };
        await appStateStore.SaveAsync(appState, cancellationToken);
    }

    private async Task SaveOptionsAsync(CancellationToken cancellationToken)
    {
        appState = appState with
        {
            RepositoryPath = currentRepositoryPath ?? appState.RepositoryPath,
            MaxInitialGitFiles = Math.Max(1, appState.MaxInitialGitFiles),
            WatchRepositoryChanges = IsAutoRefreshEnabled,
            AutoReloadDelayMs = Math.Clamp(appState.AutoReloadDelayMs, 250, 10_000),
            ThemeMode = IsLightThemeEnabled ? SemanticDiffThemeMode.Light : SemanticDiffThemeMode.Dark,
            DiffContextMode = appState.DiffContextMode,
            ReviewMode = appState.ReviewMode,
            CollapseUnchangedContext = appState.CollapseUnchangedContext,
            BaseRef = NormalizeRef(appState.BaseRef),
            HeadRef = NormalizeRef(appState.HeadRef),
            ShowSemanticEdges = IsSemanticEdgesEnabled,
            AnnotationVisibility = appState.EffectiveAnnotationVisibility,
            SemanticAnalysisMode = appState.SemanticAnalysisMode,
            LeftPaneWidth = NormalizeLeftPaneWidth(LeftPaneWidth)
        };
        await appStateStore.SaveAsync(appState, cancellationToken);
    }

    private EdgeProjectionOptions CreateEdgeOptions() => appState.ShowSemanticEdges
        ? new EdgeProjectionOptions(MinimumConfidence: 0.65, MaxEdgesPerDocumentPair: 2)
        : new EdgeProjectionOptions(MinimumConfidence: 1, MaxEdgesPerDocumentPair: 1, IncludedEdgeKinds: ImmutableHashSet<SemanticEdgeKind>.Empty);

    private void ApplyAppStateToPresentation()
    {
        DiffScopeText = appState.DiffScope.ToString();
        IsWorktreeScopeSelected = appState.DiffScope == GitDiffScope.Worktree;
        IsUnstagedScopeSelected = appState.DiffScope == GitDiffScope.Unstaged;
        IsStagedScopeSelected = appState.DiffScope == GitDiffScope.Staged;
        IsBranchScopeSelected = appState.DiffScope == GitDiffScope.Branch;
        IsRangeScopeSelected = appState.DiffScope == GitDiffScope.CommitRange || appState.DiffScope == GitDiffScope.Custom;
        BaseRefText = appState.BaseRef ?? string.Empty;
        HeadRefText = appState.HeadRef ?? "HEAD";
        DiffContextModeText = FormatDiffContextMode(appState.DiffContextMode);
        IsChangedHunksContextSelected = appState.DiffContextMode == DiffContextMode.ChangedHunks;
        IsFullFileDiffContextSelected = appState.DiffContextMode == DiffContextMode.FullFileDiff;
        IsCurrentFileContextSelected = appState.DiffContextMode == DiffContextMode.CurrentFile;
        IsNoiseFilterEnabled = appState.ReviewMode == DiffReviewMode.IgnoreWhitespace;
        ReviewModeText = FormatReviewMode(appState.ReviewMode);
        IsContextFoldingEnabled = appState.CollapseUnchangedContext;
        ContextFoldingText = appState.CollapseUnchangedContext ? "Collapsed" : "Full context";
        IsAutoRefreshEnabled = appState.WatchRepositoryChanges;
        IsLightThemeEnabled = appState.ThemeMode == SemanticDiffThemeMode.Light;
        ThemeToggleText = IsLightThemeEnabled ? "Light" : "Dark";
        IsSemanticEdgesEnabled = appState.ShowSemanticEdges;
        SemanticEdgesText = appState.ShowSemanticEdges ? "Edges on" : "Edges off";
        SemanticAnalysisModeText = FormatSemanticAnalysisMode(appState.SemanticAnalysisMode);
        IsSemanticWorkspaceModeSelected = appState.SemanticAnalysisMode == SemanticAnalysisMode.WorkspaceThenSyntax;
        IsSemanticFastModeSelected = appState.SemanticAnalysisMode == SemanticAnalysisMode.FastSyntaxOnly;
        ApplyAnnotationVisibilityToPresentation();
        MaxInitialGitFilesText = Math.Max(1, appState.MaxInitialGitFiles).ToString(System.Globalization.CultureInfo.InvariantCulture);
        LeftPaneWidth = NormalizeLeftPaneWidth(appState.LeftPaneWidth);
        RepositoryPathText = string.IsNullOrWhiteSpace(appState.RepositoryPath) ? "No repository selected" : appState.RepositoryPath;
        if (!IsAutoRefreshEnabled)
        {
            WatchStatusText = "Watch off";
        }
        else if (repositoryFileWatcher is null)
        {
            WatchStatusText = "Watch ready";
        }
    }

    private void UpdateWorkspaceSummary(string repositoryName, string contextText, int documentCount, int edgeCount)
    {
        RepositoryName = string.IsNullOrWhiteSpace(repositoryName) ? "SemanticDiff" : repositoryName;
        RepositoryContextText = contextText;
        DocumentCountText = FormatCount(documentCount, "file", "files");
        SemanticEdgeCountText = FormatCount(edgeCount, "edge", "edges");
    }

    private void ApplyAnnotationVisibilityToPresentation()
    {
        var visibility = appState.EffectiveAnnotationVisibility;
        IsGitVisualizationEnabled = visibility.ShowGitStatus;
        IsSemanticVisualizationEnabled = visibility.ShowSemantic;
        IsDiagnosticVisualizationEnabled = visibility.ShowDiagnostics;
        IsReviewVisualizationEnabled = visibility.ShowReview;
        IsHistoryVisualizationEnabled = visibility.ShowHistory;
        IsNavigationVisualizationEnabled = visibility.ShowNavigation;
        IsContextVisualizationEnabled = visibility.ShowContext;
        VisualizationButtonText = $"Visuals {visibility.EnabledLayerCount}/7";
        VisualizationSummaryText = $"Visual layers {visibility.EnabledLayerCount}/7";
    }

    private void SetExplorerItems(ImmutableArray<ExplorerItemViewModel> items)
    {
        allExplorerItems = items;
        ApplyExplorerFilter();
    }

    partial void OnFileSearchTextChanged(string value) => ApplyExplorerFilter();

    partial void OnIsLightThemeEnabledChanged(bool value) => ApplyExplorerFilter();

    private void ApplyExplorerFilter()
    {
        var query = FileSearchText.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? allExplorerItems
            : allExplorerItems
                .Where(item => item.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray();

        ExplorerItems = filtered;
        var tree = FileExplorerTreeBuilder.Build(filtered.Select(item => new FileExplorerFile(item.Path, item.Status, item.Language)));
        ExplorerTreeItems = FileExplorerNodeViewModel.Flatten(tree, collapsedExplorerNodePaths, !string.IsNullOrWhiteSpace(query), IsLightThemeEnabled);
        UpdateSelectedExplorerTreeNode();
        ExplorerCountText = string.IsNullOrWhiteSpace(query)
            ? FormatCount(allExplorerItems.Length, "file", "files")
            : $"{filtered.Length:N0}/{allExplorerItems.Length:N0} files";
    }

    private void UpdateSelectedExplorerTreeNode()
    {
        SelectedExplorerTreeNode = selectedExplorerItem is null
            ? null
            : ExplorerTreeItems.FirstOrDefault(node => string.Equals(node.DocumentId, selectedExplorerItem.DocumentId, StringComparison.Ordinal));
    }

    private void ExpandAncestors(string path)
    {
        var normalizedPath = path.Replace('\\', '/');
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length <= 1)
        {
            return;
        }

        var folderPath = string.Empty;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            folderPath = string.IsNullOrWhiteSpace(folderPath) ? segments[index] : $"{folderPath}/{segments[index]}";
            collapsedExplorerNodePaths = collapsedExplorerNodePaths.Remove(folderPath);
        }
    }

    private void UpdateSemanticNavigation(SemanticGraph semanticGraph, ImmutableArray<DiffDocumentSnapshot> documents)
    {
        var index = new SemanticNavigationIndex();
        allSemanticNavigationItems = index.Build(semanticGraph, documents);
        ApplySemanticNavigationFilter();
    }

    private void UpdateChangeNavigation(ImmutableArray<DiffDocumentSnapshot> documents)
    {
        var index = new DiffChangeNavigationIndex();
        changeNavigationItems = index.Build(documents);
        currentChangeNavigationIndex = -1;
        HasNavigableChanges = !changeNavigationItems.IsDefaultOrEmpty;
        ChangeNavigationText = changeNavigationItems.IsDefaultOrEmpty ? "0 changes" : $"0/{changeNavigationItems.Length:N0} changes";
    }

    private FocusRequest? FocusAdjacentChange(int direction)
    {
        var nextIndex = DiffChangeNavigationIndex.GetAdjacentIndex(changeNavigationItems, currentChangeNavigationIndex, direction);
        if (nextIndex < 0)
        {
            ChangeNavigationText = "0 changes";
            AddDiagnostic("Info", "No changed lines to navigate");
            return null;
        }

        currentChangeNavigationIndex = nextIndex;
        var item = changeNavigationItems[nextIndex];
        SelectExplorerItem(allExplorerItems.FirstOrDefault(explorerItem => string.Equals(explorerItem.DocumentId, item.DocumentId.Value, StringComparison.Ordinal)));
        ChangeNavigationText = $"{nextIndex + 1:N0}/{changeNavigationItems.Length:N0} {ShortenPath(item.Path)}:{item.DisplayLineNumber}";
        AddDiagnostic("Info", $"Focused change {nextIndex + 1:N0}/{changeNavigationItems.Length:N0} in {item.Path}");
        RefreshSceneAnnotations();
        return new FocusRequest(item.DocumentId.Value, item.DisplayLineNumber);
    }

    private SemanticImpactSummary UpdateImpactSummary(ImmutableArray<DiffDocumentSnapshot> documents, SemanticGraph semanticGraph)
    {
        var analyzer = new SemanticImpactAnalyzer();
        var summary = analyzer.Analyze(documents, semanticGraph);
        var conflictSummary = new DiffConflictAnalyzer().Analyze(documents);
        ImpactSummaryText = $"Impact {FormatCount(summary.ChangedSymbolCount, "symbol", "symbols")} | {FormatCount(summary.ImpactedEdgeCount, "link", "links")}";
        ReviewSignalText = $"Moved {summary.MovedLineCount:N0} | Noise {summary.IgnoredLineCount:N0} | Conflicts {conflictSummary.ConflictRegionCount:N0}";
        return summary;
    }

    private static string FormatImpactStatus(SemanticImpactSummary summary) =>
        $"{summary.ChangedSymbolCount:N0} changed symbols, {summary.MovedLineCount:N0} moved lines, {summary.IgnoredLineCount:N0} noise lines";

    partial void OnSymbolSearchTextChanged(string value) => ApplySemanticNavigationFilter();

    private void ApplySemanticNavigationFilter()
    {
        var query = SymbolSearchText.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? allSemanticNavigationItems
            : allSemanticNavigationItems
                .Where(item => item.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray();

        SemanticItems = filtered
            .Take(80)
            .Select(SemanticNavigationItemViewModel.FromItem)
            .ToImmutableArray();
        SymbolCountText = string.IsNullOrWhiteSpace(query)
            ? FormatCount(allSemanticNavigationItems.Length, "symbol", "symbols")
            : $"{SemanticItems.Length:N0}/{allSemanticNavigationItems.Length:N0} symbols";
    }

    private static string FormatCount(int count, string singular, string plural) => $"{count:N0} {(count == 1 ? singular : plural)}";

    private static string FormatDiffScope(GitDiffScope diffScope) => diffScope switch
    {
        GitDiffScope.Worktree => "worktree",
        GitDiffScope.Unstaged => "unstaged",
        GitDiffScope.Staged => "staged",
        GitDiffScope.Branch => "branch",
        GitDiffScope.Head => "head",
        GitDiffScope.CommitRange => "range",
        GitDiffScope.Custom => "custom",
        _ => diffScope.ToString().ToLowerInvariant()
    };

    private static string FormatDiffContextMode(DiffContextMode contextMode) => contextMode switch
    {
        DiffContextMode.FullFileDiff => "Full diff",
        DiffContextMode.CurrentFile => "Current file",
        _ => "Changed"
    };

    private static string? NormalizeRef(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double NormalizeLeftPaneWidth(double width) => double.IsFinite(width) ? Math.Clamp(width, 220, 520) : 260;

    private static string FormatReferenceText(SemanticDiffAppState state) => FormatReferenceText(
        new GitDiffRequest(string.Empty, state.DiffScope, NormalizeRef(state.BaseRef), NormalizeRef(state.HeadRef)),
        null);

    private static string FormatReferenceText(GitDiffRequest request, string? defaultBranch)
    {
        var baseRef = NormalizeRef(request.BaseRef) ?? defaultBranch ?? (request.Scope == GitDiffScope.Branch ? "default" : "base");
        var headRef = NormalizeRef(request.HeadRef) ?? "HEAD";
        return request.Scope is GitDiffScope.Branch or GitDiffScope.CommitRange or GitDiffScope.Custom
            ? $"range {baseRef}...{headRef}"
            : $"base {defaultBranch ?? "unknown"}";
    }

    private static string FormatReviewMode(DiffReviewMode reviewMode) => reviewMode switch
    {
        DiffReviewMode.IgnoreWhitespace => "Noise filter",
        _ => "Precise"
    };

    private static string FormatSemanticAnalysisMode(SemanticAnalysisMode analysisMode) => analysisMode switch
    {
        SemanticAnalysisMode.FastSyntaxOnly => "Fast syntax",
        _ => "MSBuild"
    };

    private void SelectExplorerItem(ExplorerItemViewModel? item)
    {
        selectedExplorerItem = item;
        UpdateSelectedExplorerTreeNode();
        currentBlameOperation?.Cancel();
        HasSelectedRepositoryFile = item is not null && !string.IsNullOrWhiteSpace(currentRepositoryPath);
        SelectedFileReviewText = item is null ? "Select a changed file" : ShortenPath(item.Path);
        ReviewActionStatusText = HasSelectedRepositoryFile ? "Ready" : "No repository file selected";
        BlameSummaryText = HasSelectedRepositoryFile ? "Loading blame..." : "Blame unavailable";

        if (HasSelectedRepositoryFile && item is not null)
        {
            _ = LoadBlameSummaryAsync(item.Path);
        }

        RefreshSceneAnnotations();
    }

    private async Task LoadBlameSummaryAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return;
        }

        var blameOperation = new CancellationTokenSource();
        var previousOperation = Interlocked.Exchange(ref currentBlameOperation, blameOperation);
        previousOperation?.Cancel();
        previousOperation?.Dispose();

        try
        {
            var blame = await gitBlameService.GetFileBlameAsync(currentRepositoryPath, path, blameOperation.Token).ConfigureAwait(false);
            PostToCapturedContext(() => ApplyBlameSummary(path, blame));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            PostToCapturedContext(() =>
            {
                BlameSummaryText = "Blame unavailable";
                AddDiagnostic("Warning", $"Blame failed: {exception.Message}");
                RefreshSceneAnnotations();
            });
        }
        finally
        {
            if (ReferenceEquals(currentBlameOperation, blameOperation))
            {
                currentBlameOperation = null;
            }

            blameOperation.Dispose();
        }
    }

    private void ApplyBlameSummary(string path, GitFileBlame blame)
    {
        if (selectedExplorerItem is null || !string.Equals(selectedExplorerItem.Path, path, StringComparison.Ordinal))
        {
            return;
        }

        if (blame.Lines.IsDefaultOrEmpty)
        {
            BlameSummaryText = "No blame data";
            RefreshSceneAnnotations();
            return;
        }

        var topAuthors = blame.Lines
            .GroupBy(line => line.Author, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(2)
            .Select(group => $"{group.Key} {group.Count():N0}");
        var latest = blame.Lines
            .Where(line => line.AuthorTime is not null)
            .OrderByDescending(line => line.AuthorTime)
            .FirstOrDefault();
        var latestText = latest is null
            ? "latest unknown"
            : $"latest {latest.Author} {latest.AuthorTime!.Value:yyyy-MM-dd} {ShortCommit(latest.CommitId)}";
        BlameSummaryText = $"Blame {string.Join(", ", topAuthors)} | {latestText}";
        RefreshSceneAnnotations();
    }

    private static string ShortCommit(string commitId) => commitId.Length <= 8 ? commitId : commitId[..8];

    private async Task RunReviewActionAsync(
        string operationName,
        Func<string, string, CancellationToken, Task<GitReviewOperationResult>> action)
    {
        if (selectedExplorerItem is null || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            ReviewActionStatusText = "Select a repository file";
            AddDiagnostic("Warning", "Select a changed file before running a review action");
            return;
        }

        var path = selectedExplorerItem.Path;
        var operation = BeginOperation($"{operationName} {path}");
        try
        {
            var result = await action(currentRepositoryPath, path, operation.Token);
            if (!result.Succeeded)
            {
                ReviewActionStatusText = "Action failed";
                AddDiagnostic("Error", result.Message);
                RefreshSceneAnnotations();
                CompleteOperation(operation, "Review action failed");
                return;
            }

            ReviewActionStatusText = result.Message;
            AddDiagnostic("Info", result.Message);
            RefreshSceneAnnotations();
            CompleteOperation(operation, "Review action complete");
            await LoadRepositoryAsync(loadAppState: false, operationMessage: "Reloading review state");
        }
        catch (OperationCanceledException)
        {
            ReviewActionStatusText = "Action canceled";
            CompleteOperation(operation, "Review action canceled");
        }
        catch (Exception exception)
        {
            ReviewActionStatusText = "Action failed";
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "Review action failed");
        }
    }

    private async Task RestartRepositoryWatcherAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        await StopRepositoryWatcherAsync();
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsAutoRefreshEnabled || string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            WatchStatusText = IsAutoRefreshEnabled ? "Watch unavailable" : "Watch off";
            RefreshSceneAnnotations();
            return;
        }

        try
        {
            repositoryFileWatcher = repositoryFileWatcherFactory.Watch(repositoryPath, new RepositoryFileWatcherOptions());
            repositoryFileWatcher.Changed += OnRepositoryFileChanged;
            WatchStatusText = "Watching";
            RefreshSceneAnnotations();
        }
        catch (Exception exception)
        {
            WatchStatusText = "Watch unavailable";
            AddDiagnostic("Warning", $"File watcher unavailable: {exception.Message}");
            RefreshSceneAnnotations();
        }
    }

    private async ValueTask StopRepositoryWatcherAsync()
    {
        pendingAutoReload?.Cancel();
        if (repositoryFileWatcher is null)
        {
            return;
        }

        repositoryFileWatcher.Changed -= OnRepositoryFileChanged;
        await repositoryFileWatcher.DisposeAsync();
        repositoryFileWatcher = null;
    }

    private void OnRepositoryFileChanged(object? sender, RepositoryFileChangedEventArgs args)
    {
        var relativePath = currentRepositoryPath is null
            ? args.FullPath
            : Path.GetRelativePath(currentRepositoryPath, args.FullPath).Replace('\\', '/');
        PostToCapturedContext(() => WatchStatusText = $"Changed {ShortenPath(relativePath)}");
        PostToCapturedContext(RefreshSceneAnnotations);
        ScheduleRepositoryReload(relativePath);
    }

    private void ScheduleRepositoryReload(string relativePath)
    {
        var reloadTokenSource = new CancellationTokenSource();
        var previousTokenSource = Interlocked.Exchange(ref pendingAutoReload, reloadTokenSource);
        previousTokenSource?.Cancel();
        _ = DebounceRepositoryReloadAsync(reloadTokenSource, relativePath);
    }

    private async Task DebounceRepositoryReloadAsync(CancellationTokenSource reloadTokenSource, string relativePath)
    {
        try
        {
            await Task.Delay(Math.Clamp(appState.AutoReloadDelayMs, 250, 10_000), reloadTokenSource.Token).ConfigureAwait(false);
            await RunOnCapturedContextAsync(async () =>
            {
                if (!IsAutoRefreshEnabled)
                {
                    return;
                }

                AddDiagnostic("Info", $"Auto refresh after {ShortenPath(relativePath)}");
                await LoadRepositoryAsync(loadAppState: false, operationMessage: "Auto refreshing repository", Scene.CaptureViewState());
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(pendingAutoReload, reloadTokenSource))
            {
                pendingAutoReload = null;
            }

            reloadTokenSource.Dispose();
        }
    }

    private void PostToCapturedContext(Action action)
    {
        if (synchronizationContext is null || SynchronizationContext.Current == synchronizationContext)
        {
            action();
            return;
        }

        synchronizationContext.Post(_ => action(), null);
    }

    private Task RunOnCapturedContextAsync(Func<Task> action)
    {
        if (synchronizationContext is null || SynchronizationContext.Current == synchronizationContext)
        {
            return action();
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        synchronizationContext.Post(async _ =>
        {
            try
            {
                await action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }, null);
        return completion.Task;
    }

    private static string ShortenPath(string path) => path.Length <= 72 ? path : $"...{path[^69..]}";

    private DiffCanvasScene CreateScene(ImmutableArray<DiffDocumentSnapshot> documents, SemanticGraph semanticGraph, GraphLayoutResult? layout) =>
        DiffCanvasScene.FromDocuments(
            documents,
            semanticGraph,
            layout,
            CreateEdgeOptions(),
            CreateAnnotations(documents, semanticGraph),
            appState.EffectiveAnnotationVisibility);

    private void RefreshSceneAnnotations()
    {
        if (currentDocuments.IsDefaultOrEmpty)
        {
            return;
        }

        Scene = Scene.WithAnnotations(CreateAnnotations(currentDocuments, currentSemanticGraph), appState.EffectiveAnnotationVisibility);
    }

    private ImmutableArray<DiffAnnotation> CreateAnnotations(ImmutableArray<DiffDocumentSnapshot> documents, SemanticGraph semanticGraph)
    {
        if (documents.IsDefaultOrEmpty)
        {
            return ImmutableArray<DiffAnnotation>.Empty;
        }

        var context = CreateAnnotationContext();
        var request = new DiffAnnotationRequest(documents, semanticGraph, context);
        return annotationProviders
            .SelectMany(provider => provider.CreateAnnotations(request))
            .GroupBy(annotation => annotation.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToImmutableArray();
    }

    private ImmutableDictionary<string, string> CreateAnnotationContext()
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        builder[DiffAnnotationContextKeys.DiffScope] = DiffScopeText;
        builder[DiffAnnotationContextKeys.DiffContextMode] = DiffContextModeText;
        builder[DiffAnnotationContextKeys.ReviewMode] = ReviewModeText;
        builder[DiffAnnotationContextKeys.ReferenceRange] = FormatReferenceText(appState);
        builder[DiffAnnotationContextKeys.WatchStatus] = WatchStatusText;

        if (selectedExplorerItem is not null)
        {
            builder[DiffAnnotationContextKeys.SelectedDocumentId] = selectedExplorerItem.DocumentId;
            builder[DiffAnnotationContextKeys.BlameSummary] = BlameSummaryText;
            builder[DiffAnnotationContextKeys.ReviewActionStatus] = ReviewActionStatusText;
        }

        if (currentChangeNavigationIndex >= 0 && currentChangeNavigationIndex < changeNavigationItems.Length)
        {
            var item = changeNavigationItems[currentChangeNavigationIndex];
            builder[DiffAnnotationContextKeys.CurrentChangeDocumentId] = item.DocumentId.Value;
            builder[DiffAnnotationContextKeys.CurrentChangeLineIndex] = item.LineIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            builder[DiffAnnotationContextKeys.CurrentChangeText] = ChangeNavigationText;
        }

        return builder.ToImmutable();
    }

    private CancellationTokenSource BeginOperation(string message)
    {
        currentOperation?.Cancel();
        var operation = new CancellationTokenSource();
        currentOperation = operation;
        IsBusy = true;
        ProgressValue = 0;
        ProgressText = message;
        AddDiagnostic("Info", message);
        return operation;
    }

    private void CompleteOperation(CancellationTokenSource operation, string message)
    {
        if (ReferenceEquals(currentOperation, operation))
        {
            IsBusy = false;
            ProgressValue = 1;
            ProgressText = message;
            currentOperation = null;
        }

        operation.Dispose();
    }

    private void ReportProgress(double value, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProgressValue = Math.Clamp(value, 0, 1);
        ProgressText = message;
    }

    private void AddDiagnostic(string level, string message)
    {
        var item = new DiagnosticItemViewModel(DateTimeOffset.Now.ToString("HH:mm:ss"), level, message);
        Diagnostics = Diagnostics.Insert(0, item).Take(8).ToImmutableArray();
        DiagnosticsCountText = FormatCount(Diagnostics.Length, "diagnostic", "diagnostics");
        LatestDiagnosticText = item.DisplayText;
    }
}

public sealed record ExplorerItemViewModel(string Path, DiffFileStatus Status, string Language)
{
    public string DocumentId => Path;

    public string DisplayName => $"{StatusText}  {Path}";

    public string SearchText => $"{StatusText} {Path} {FileName} {FolderPath} {Language}";

    public string FileName => System.IO.Path.GetFileName(Path);

    public string FolderPath
    {
        get
        {
            var folderPath = System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? string.Empty;
            return string.IsNullOrWhiteSpace(folderPath) ? "/" : folderPath;
        }
    }

    public string StatusText => Status switch
    {
        DiffFileStatus.Added => "A",
        DiffFileStatus.Deleted => "D",
        DiffFileStatus.Renamed => "R",
        DiffFileStatus.Untracked => "?",
        DiffFileStatus.Conflicted => "!",
        _ => "M"
    };
}

public sealed record SemanticNavigationItemViewModel(
    string AnchorId,
    string DocumentId,
    string Path,
    string KindText,
    string DisplayName,
    int Line,
    int IncidentEdgeCount)
{
    public string LocationText => $"{Path}:{Line}";

    public string EdgeText => IncidentEdgeCount == 1 ? "1 link" : $"{IncidentEdgeCount:N0} links";

    public static SemanticNavigationItemViewModel FromItem(SemanticNavigationItem item) => new(
        item.AnchorId,
        item.DocumentId.Value,
        item.Path,
        item.KindText,
        item.DisplayName,
        item.Line,
        item.IncidentEdgeCount);
}

public sealed record FocusRequest(string DocumentId, int? Line);

public sealed record DiagnosticItemViewModel(string TimeText, string Level, string Message)
{
    public string DisplayText => $"{TimeText}  {Level}  {Message}";
}