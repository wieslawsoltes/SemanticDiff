using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Git;
using SemanticDiff.Layout;
using SemanticDiff.Rendering;
using SemanticDiff.Semantics;
using SemanticDiff.Semantics.Roslyn;
using SemanticDiff.Semantics.Xaml;
using Windows.UI;

namespace SemanticDiff.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAppStateStore appStateStore;
    private readonly IRepositoryFileWatcherFactory repositoryFileWatcherFactory;
    private readonly IGitReviewService gitReviewService;
    private readonly IGitReviewDiscussionService gitReviewDiscussionService;
    private readonly IGitBlameService gitBlameService;
    private readonly IGitReferenceDiscoveryService gitReferenceDiscoveryService;
    private readonly IGitHistoryService gitHistoryService;
    private readonly IReadOnlyList<IDiffAnnotationProvider> annotationProviders = [new BuiltInDiffAnnotationProvider()];
    private readonly LatestRequestGate repositoryLoadRequests = new();
    private readonly Dictionary<string, DiffViewCacheEntry> diffViewCache = new(StringComparer.Ordinal);
    private readonly Queue<string> diffViewCacheOrder = new();
    private readonly SynchronizationContext? synchronizationContext;
    private const int MaxCachedDiffViews = 12;
    private const int GitHistoryPageSize = 200;
    private GraphLayoutResult? previousLayout;
    private ImmutableHashSet<DiffDocumentId> pinnedDocumentIds = ImmutableHashSet<DiffDocumentId>.Empty;
    private ImmutableArray<DiffDocumentSnapshot> currentDocuments = [];
    private SemanticGraph currentSemanticGraph = SemanticGraph.Empty;
    private ImmutableArray<ExplorerItemViewModel> allExplorerItems = [];
    private ImmutableHashSet<string> collapsedExplorerNodePaths = ImmutableHashSet<string>.Empty;
    private ImmutableHashSet<string> collapsedGitReferenceNodeIds = ImmutableHashSet<string>.Empty;
    private ImmutableArray<SemanticNavigationItem> allSemanticNavigationItems = [];
    private SemanticSymbolInsightSummary currentSymbolInsight = SemanticSymbolInsightSummary.Empty;
    private string selectedSymbolScopeFilter = SymbolScopeFilterViewModel.AllKey;
    private string selectedSymbolKindFilter = SymbolFilterAll;
    private string selectedSymbolDocumentFilter = SymbolFilterAll;
    private ImmutableArray<DiffChangeNavigationItem> changeNavigationItems = [];
    private int currentChangeNavigationIndex = -1;
    private string currentStatusPrefix = "sample fallback";
    private string? currentRepositoryPath;
    private GitDiffSnapshot? currentGitSnapshot;
    private ImmutableArray<GitBranchOptionViewModel> allBranchOptions = [];
    private ImmutableArray<GitPullRequestOptionViewModel> allPullRequestOptions = [];
    private ImmutableArray<ReviewThreadItemViewModel> allReviewThreadItems = [];
    private ImmutableArray<GitReviewThreadInfo> currentReviewThreads = [];
    private GitReviewRequestKind currentReviewRequestKind = GitReviewRequestKind.PullRequest;
    private SemanticDiffAppState appState = new();
    private CancellationTokenSource? currentOperation;
    private CancellationTokenSource? currentSemanticRefinementOperation;
    private CancellationTokenSource? currentBlameOperation;
    private CancellationTokenSource? pendingAutoReload;
    private IRepositoryFileWatcher? repositoryFileWatcher;
    private ExplorerItemViewModel? selectedExplorerItem;
    private bool currentDocumentsAreRepositoryDocuments;
    private bool isUpdatingReferenceSelection;
    private const string SymbolFilterAll = "All";

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
        : this(appStateStore, repositoryFileWatcherFactory, gitReviewService, new GitReviewDiscussionService(), gitBlameService, new GitReferenceDiscoveryService(), new GitHistoryService())
    {
    }

    public MainViewModel(
        IAppStateStore appStateStore,
        IRepositoryFileWatcherFactory repositoryFileWatcherFactory,
        IGitReviewService gitReviewService,
        IGitReviewDiscussionService gitReviewDiscussionService,
        IGitBlameService gitBlameService)
        : this(appStateStore, repositoryFileWatcherFactory, gitReviewService, gitReviewDiscussionService, gitBlameService, new GitReferenceDiscoveryService(), new GitHistoryService())
    {
    }

    public MainViewModel(
        IAppStateStore appStateStore,
        IRepositoryFileWatcherFactory repositoryFileWatcherFactory,
        IGitReviewService gitReviewService,
        IGitReviewDiscussionService gitReviewDiscussionService,
        IGitBlameService gitBlameService,
        IGitReferenceDiscoveryService gitReferenceDiscoveryService,
        IGitHistoryService gitHistoryService)
    {
        this.appStateStore = appStateStore;
        this.repositoryFileWatcherFactory = repositoryFileWatcherFactory;
        this.gitReviewService = gitReviewService;
        this.gitReviewDiscussionService = gitReviewDiscussionService;
        this.gitBlameService = gitBlameService;
        this.gitReferenceDiscoveryService = gitReferenceDiscoveryService;
        this.gitHistoryService = gitHistoryService;
        synchronizationContext = SynchronizationContext.Current;
        WorkspaceTabs.Add(WorkspaceTabViewModel.Graph());
        SelectedWorkspaceTab = WorkspaceTabs[0];
        InitializeSampleDocuments(SampleDiffDocuments.Create());
        _ = LoadRepositoryAsync(loadAppState: true, operationMessage: "Loading repository");
    }

    public ObservableCollection<WorkspaceTabViewModel> WorkspaceTabs { get; } = [];

    [ObservableProperty]
    private DiffCanvasScene scene = DiffCanvasScene.FromDocuments([]);

    [ObservableProperty]
    private WorkspaceTabViewModel? selectedWorkspaceTab;

    [ObservableProperty]
    private Visibility graphWorkspaceVisibility = Visibility.Visible;

    [ObservableProperty]
    private Visibility auxiliaryWorkspaceVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private ImmutableArray<ExplorerItemViewModel> explorerItems = [];

    [ObservableProperty]
    private ImmutableArray<FileExplorerNodeViewModel> explorerTreeItems = [];

    [ObservableProperty]
    private FileExplorerNodeViewModel? selectedExplorerTreeNode;

    [ObservableProperty]
    private int selectedRailTabIndex;

    [ObservableProperty]
    private string gitReferenceSearchText = string.Empty;

    [ObservableProperty]
    private ImmutableArray<GitReferenceTreeItemViewModel> gitReferenceTreeItems = [];

    [ObservableProperty]
    private GitReferenceTreeItemViewModel? selectedGitReferenceTreeItem;

    [ObservableProperty]
    private string gitReferenceCountText = "0 refs";

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
    private ImmutableArray<GitBranchOptionViewModel> branchOptions = [];

    [ObservableProperty]
    private GitBranchOptionViewModel? selectedBranchOption;

    [ObservableProperty]
    private bool hasBranchOptions;

    [ObservableProperty]
    private ImmutableArray<GitPullRequestOptionViewModel> pullRequestOptions = [];

    [ObservableProperty]
    private GitPullRequestOptionViewModel? selectedPullRequestOption;

    [ObservableProperty]
    private bool hasPullRequestOptions;

    public ImmutableArray<ReviewRequestStateOptionViewModel> ReviewRequestStateOptions { get; } = ReviewRequestStateOptionViewModel.All;

    [ObservableProperty]
    private ReviewRequestStateOptionViewModel selectedReviewRequestStateOption = ReviewRequestStateOptionViewModel.All[0];

    [ObservableProperty]
    private string reviewRequestStateText = "Open";

    [ObservableProperty]
    private Visibility pullRequestSelectorVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string referenceSelectorStatusText = "Refs loading";

    [ObservableProperty]
    private string diffViewCacheText = "Cache empty";

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
    private string documentCountText = "0 files";

    [ObservableProperty]
    private string semanticEdgeCountText = "0 edges";

    [ObservableProperty]
    private bool isSemanticEdgesEnabled = true;

    [ObservableProperty]
    private string semanticEdgesText = "Edges on";

    [ObservableProperty]
    private string semanticAnalysisModeText = "MSBuild";

    public ImmutableArray<LayoutModeOptionViewModel> LayoutModeOptions { get; } = LayoutModeOptionViewModel.All;

    [ObservableProperty]
    private LayoutModeOptionViewModel selectedLayoutModeOption = LayoutModeOptionViewModel.All[1];

    [ObservableProperty]
    private string layoutModeText = "Layered";

    public ImmutableArray<GroupingModeOptionViewModel> GroupingModeOptions { get; } = GroupingModeOptionViewModel.All;

    [ObservableProperty]
    private GroupingModeOptionViewModel selectedGroupingModeOption = GroupingModeOptionViewModel.All[1];

    [ObservableProperty]
    private string groupingModeText = "Folders";

    [ObservableProperty]
    private bool isSemanticWorkspaceModeSelected = true;

    [ObservableProperty]
    private bool isSemanticFastModeSelected;

    [ObservableProperty]
    private string visualizationButtonText = "Visuals 8/8";

    [ObservableProperty]
    private string visualizationSummaryText = "Visual layers 8/8";

    [ObservableProperty]
    private bool isGitVisualizationEnabled = true;

    [ObservableProperty]
    private bool isSemanticVisualizationEnabled = true;

    [ObservableProperty]
    private bool isDiagnosticVisualizationEnabled = true;

    [ObservableProperty]
    private bool isReviewVisualizationEnabled = true;

    [ObservableProperty]
    private bool isReviewCommentVisualizationEnabled = true;

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
    private string symbolInsightSummaryText = "No symbol insight";

    [ObservableProperty]
    private string symbolFilterStatusText = "All symbols";

    [ObservableProperty]
    private ImmutableArray<SymbolScopeFilterViewModel> symbolScopeFilters = [];

    [ObservableProperty]
    private ImmutableArray<SemanticSymbolKindFacetViewModel> symbolKindFacets = [];

    [ObservableProperty]
    private ImmutableArray<SemanticSymbolDocumentFacetViewModel> symbolDocumentFacets = [];

    [ObservableProperty]
    private ImmutableArray<SemanticNavigationItemViewModel> hotSemanticItems = [];

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
    private string reviewSearchText = string.Empty;

    [ObservableProperty]
    private ImmutableArray<ReviewThreadItemViewModel> reviewThreadItems = [];

    [ObservableProperty]
    private ReviewThreadItemViewModel? selectedReviewThreadItem;

    [ObservableProperty]
    private ImmutableArray<ReviewCommentItemViewModel> selectedReviewComments = [];

    [ObservableProperty]
    private string reviewThreadCountText = "0 threads";

    [ObservableProperty]
    private string reviewPanelStatusText = "Select a PR or MR";

    [ObservableProperty]
    private string reviewCommentText = string.Empty;

    [ObservableProperty]
    private bool hasSelectedReviewThread;

    [ObservableProperty]
    private bool canNavigateToSelectedReviewThread;

    [ObservableProperty]
    private bool canReplyToSelectedReviewThread;

    [ObservableProperty]
    private bool canResolveSelectedReviewThread;

    [ObservableProperty]
    private string reviewResolveButtonText = "Resolve";

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

        var repositoryRequestId = repositoryLoadRequests.BeginRequest();
        var operation = BeginOperation("Opening repository");
        try
        {
            var cancellationToken = operation.Token;
            var repositoryDiscovery = new GitRepositoryDiscovery();
            var repositoryRoot = await repositoryDiscovery.DiscoverRootAsync(selectedPath, cancellationToken);
            EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);
            if (string.IsNullOrWhiteSpace(repositoryRoot))
            {
                currentRepositoryPath = null;
                appState = appState with
                {
                    RepositoryPath = null,
                    LayoutNodes = null
                };
                ApplyAppStateToPresentation();
                ResetRepositoryPresentation(
                    "SemanticDiff",
                    "No Git repository found",
                    $"No Git repository found at {selectedPath}",
                    isRepository: false);
                await StopRepositoryWatcherAsync();
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
            ResetRepositoryPresentation(
                Path.GetFileName(repositoryRoot),
                $"{Path.GetFileName(repositoryRoot)} | loading selected repository",
                $"{Path.GetFileName(repositoryRoot)} | loading selected repository",
                isRepository: true);
            await SaveOptionsAsync(cancellationToken);
            EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);
            AddDiagnostic("Info", $"Selected repository {repositoryRoot}");
            CompleteOperation(operation, "Repository selected");
            await LoadRepositoryAsync(loadAppState: false, operationMessage: "Loading selected repository", repositoryRequestId: repositoryRequestId);
        }
        catch (OperationCanceledException)
        {
            var shouldReport = IsCurrentRepositoryRequest(repositoryRequestId) && IsCurrentOperation(operation);
            CompleteOperation(operation, "Open canceled");
            if (shouldReport)
            {
                StatusText = "Open canceled | showing current diff graph";
            }
        }
        catch (Exception exception)
        {
            var shouldReport = IsCurrentRepositoryRequest(repositoryRequestId) && IsCurrentOperation(operation);
            CompleteOperation(operation, "Open failed");
            if (shouldReport)
            {
                AddDiagnostic("Error", exception.Message);
            }
        }
    }

    public async Task SetDiffScopeAsync(GitDiffScope diffScope)
    {
        if (appState.DiffScope == diffScope)
        {
            return;
        }

        CacheCurrentDiffView();
        appState = appState with
        {
            DiffScope = diffScope,
            SelectedBranchRef = diffScope == GitDiffScope.Branch ? appState.SelectedBranchRef : null,
            SelectedPullRequestNumber = null,
            LayoutNodes = null
        };
        previousLayout = null;
        pinnedDocumentIds = ImmutableHashSet<DiffDocumentId>.Empty;
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Diff scope changed to {diffScope}");
        ClearReviewDiscussion("Select a PR or MR");
        await LoadRepositoryAsync(loadAppState: false, operationMessage: $"Loading {diffScope} diff");
    }

    public async Task ApplyReferenceOptionsAsync()
    {
        var baseRef = NormalizeRef(BaseRefText);
        var headRef = NormalizeRef(HeadRefText);
        var nextScope = IsRangeScopeSelected ? GitDiffScope.CommitRange : appState.DiffScope;
        if (string.Equals(appState.BaseRef, baseRef, StringComparison.Ordinal) &&
            string.Equals(appState.HeadRef, headRef, StringComparison.Ordinal) &&
            appState.DiffScope == nextScope)
        {
            await SaveOptionsAsync(CancellationToken.None);
            return;
        }

        CacheCurrentDiffView();
        appState = appState with
        {
            DiffScope = nextScope,
            BaseRef = baseRef,
            HeadRef = headRef,
            SelectedBranchRef = null,
            SelectedPullRequestNumber = null,
            LayoutNodes = null
        };
        previousLayout = null;
        pinnedDocumentIds = ImmutableHashSet<DiffDocumentId>.Empty;
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Reference range changed to {FormatReferenceText(appState)}");
        ClearReviewDiscussion("Select a PR or MR");
        RefreshSceneAnnotations();

        if (appState.DiffScope is GitDiffScope.Branch or GitDiffScope.CommitRange or GitDiffScope.Custom)
        {
            await LoadRepositoryAsync(loadAppState: false, operationMessage: "Reloading reference range");
        }
    }

    public async Task SelectBranchAsync(GitBranchOptionViewModel? option)
    {
        if (option is null || isUpdatingReferenceSelection)
        {
            return;
        }

        if (appState.DiffScope == GitDiffScope.Branch &&
            appState.SelectedPullRequestNumber is null &&
            string.Equals(appState.HeadRef, option.ReferenceName, StringComparison.Ordinal) &&
            string.Equals(appState.SelectedBranchRef, option.ReferenceName, StringComparison.Ordinal))
        {
            return;
        }

        CacheCurrentDiffView();
        appState = appState with
        {
            DiffScope = GitDiffScope.Branch,
            BaseRef = null,
            HeadRef = option.ReferenceName,
            SelectedBranchRef = option.ReferenceName,
            SelectedPullRequestNumber = null,
            LayoutNodes = null
        };
        previousLayout = null;
        pinnedDocumentIds = ImmutableHashSet<DiffDocumentId>.Empty;
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Branch view changed to {option.ReferenceName}");
        ClearReviewDiscussion("Select a PR or MR");
        await LoadRepositoryAsync(loadAppState: false, operationMessage: $"Loading branch {option.ReferenceName}");
    }

    public async Task SelectPullRequestAsync(GitPullRequestOptionViewModel? option)
    {
        if (option is null || isUpdatingReferenceSelection || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return;
        }

        if (appState.DiffScope == GitDiffScope.Branch && appState.SelectedPullRequestNumber == option.Number)
        {
            return;
        }

        CacheCurrentDiffView();
        var operation = BeginOperation($"Preparing {option.KindText} {option.NumberText}");
        try
        {
            var headRef = await gitReferenceDiscoveryService.EnsurePullRequestHeadAsync(currentRepositoryPath, option.ToPullRequestInfo(), operation.Token);
            if (string.IsNullOrWhiteSpace(headRef))
            {
                CompleteOperation(operation, $"{option.KindText} unavailable");
                AddDiagnostic("Warning", $"Unable to fetch {option.KindText} {option.NumberText}");
                ApplyReferenceSelectionsToPresentation();
                return;
            }

            appState = appState with
            {
                DiffScope = GitDiffScope.Branch,
                BaseRef = option.BaseReferenceName,
                HeadRef = headRef,
                SelectedBranchRef = null,
                SelectedPullRequestNumber = option.Number,
                LayoutNodes = null
            };
            previousLayout = null;
            pinnedDocumentIds = ImmutableHashSet<DiffDocumentId>.Empty;
            ApplyAppStateToPresentation();
            await SaveOptionsAsync(operation.Token);
            AddDiagnostic("Info", $"{option.KindText} view changed to {option.NumberText}");
            CompleteOperation(operation, $"{option.KindText} ready");
            await LoadRepositoryAsync(loadAppState: false, operationMessage: $"Loading {option.KindText} {option.NumberText}");
            await RefreshReviewDiscussionAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(operation, $"{option.KindText} selection canceled");
        }
        catch (Exception exception)
        {
            CompleteOperation(operation, $"{option.KindText} selection failed");
            AddDiagnostic("Error", exception.Message);
            ApplyReferenceSelectionsToPresentation();
        }
    }

    private async Task RefreshRepositoryReferencesAsync(string repositoryPath, long repositoryRequestId, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsCurrentRepositoryRequest(repositoryRequestId))
            {
                return;
            }

            ReferenceSelectorStatusText = $"Loading {FormatReviewRequestState(appState.ReviewRequestState)} review requests";
            var snapshot = await gitReferenceDiscoveryService.GetReferencesAsync(repositoryPath, cancellationToken, appState.ReviewRequestState);
            if (!IsCurrentRepositoryRequest(repositoryRequestId))
            {
                return;
            }

            allBranchOptions = snapshot.Branches.Select(GitBranchOptionViewModel.FromBranch).ToImmutableArray();
            allPullRequestOptions = snapshot.PullRequests.Select(GitPullRequestOptionViewModel.FromPullRequest).ToImmutableArray();
            currentReviewRequestKind = snapshot.ReviewRequestKind;
            ApplyReferenceOptionFilters();
            PullRequestSelectorVisibility = snapshot.SupportsReviewRequests ? Visibility.Visible : Visibility.Collapsed;
            ReferenceSelectorStatusText = snapshot.StatusMessage;
            ApplyReferenceSelectionsToPresentation();
            if (SelectedPullRequestOption is null)
            {
                ClearReviewDiscussion(snapshot.SupportsReviewRequests ? "Select a PR or MR" : "Review workflow unavailable");
            }
            else
            {
                _ = RefreshReviewDiscussionAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentRepositoryRequest(repositoryRequestId))
            {
                ReferenceSelectorStatusText = "Refs unavailable";
                AddDiagnostic("Warning", $"Reference discovery failed: {exception.Message}");
            }
        }
    }

    private void ClearReferenceOptions(string status)
    {
        allBranchOptions = [];
        allPullRequestOptions = [];
        currentReviewRequestKind = GitReviewRequestKind.PullRequest;
        GitReferenceSearchText = string.Empty;
        BranchOptions = [];
        PullRequestOptions = [];
        GitReferenceTreeItems = [];
        SelectedGitReferenceTreeItem = null;
        GitReferenceCountText = "0 refs";
        SelectedBranchOption = null;
        SelectedPullRequestOption = null;
        HasBranchOptions = false;
        HasPullRequestOptions = false;
        PullRequestSelectorVisibility = Visibility.Collapsed;
        ReferenceSelectorStatusText = status;
        ClearReviewDiscussion("Select a PR or MR");
    }

    partial void OnGitReferenceSearchTextChanged(string value) => ApplyReferenceOptionFilters();

    partial void OnReviewSearchTextChanged(string value) => ApplyReviewThreadFilter();

    partial void OnSelectedWorkspaceTabChanged(WorkspaceTabViewModel? value)
    {
        var isGraph = value?.Kind == WorkspaceTabKind.Graph;
        GraphWorkspaceVisibility = isGraph ? Visibility.Visible : Visibility.Collapsed;
        AuxiliaryWorkspaceVisibility = isGraph ? Visibility.Collapsed : Visibility.Visible;
        if (isGraph && value is not null)
        {
            RestoreGraphWorkspaceState(value);
        }
    }

    partial void OnSelectedWorkspaceTabChanging(WorkspaceTabViewModel? oldValue, WorkspaceTabViewModel? newValue)
    {
        if (!ReferenceEquals(oldValue, newValue))
        {
            CaptureGraphWorkspaceState(oldValue);
        }
    }

    partial void OnSelectedReviewThreadItemChanged(ReviewThreadItemViewModel? value)
    {
        SelectedReviewComments = value?.Comments ?? [];
        HasSelectedReviewThread = value is not null;
        CanNavigateToSelectedReviewThread = !string.IsNullOrWhiteSpace(value?.Path);
        CanReplyToSelectedReviewThread = value?.CanReply == true;
        CanResolveSelectedReviewThread = value?.CanResolve == true;
        ReviewResolveButtonText = value?.IsResolved == true ? "Reopen" : "Resolve";
    }

    private void ApplyReferenceOptionFilters()
    {
        BranchOptions = FilterReferenceOptions(allBranchOptions, GitReferenceSearchText, option => option.SearchText);
        PullRequestOptions = FilterReferenceOptions(allPullRequestOptions, GitReferenceSearchText, option => option.SearchText);
        HasBranchOptions = !allBranchOptions.IsDefaultOrEmpty;
        HasPullRequestOptions = !allPullRequestOptions.IsDefaultOrEmpty;
        BuildGitReferenceTree();
        ApplyReferenceSelectionsToPresentation();
    }

    private void ClearReviewDiscussion(string status)
    {
        allReviewThreadItems = [];
        currentReviewThreads = [];
        ReviewSearchText = string.Empty;
        ReviewThreadItems = [];
        SelectedReviewThreadItem = null;
        SelectedReviewComments = [];
        ReviewCommentText = string.Empty;
        ReviewThreadCountText = "0 threads";
        ReviewPanelStatusText = status;
        HasSelectedReviewThread = false;
        CanNavigateToSelectedReviewThread = false;
        CanReplyToSelectedReviewThread = false;
        CanResolveSelectedReviewThread = false;
        ReviewResolveButtonText = "Resolve";
        RefreshSceneAnnotations();
    }

    private void ApplyReviewThreadFilter()
    {
        var query = ReviewSearchText.Trim();
        ReviewThreadItems = string.IsNullOrWhiteSpace(query)
            ? allReviewThreadItems
            : allReviewThreadItems.Where(item => item.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
        ReviewThreadCountText = string.IsNullOrWhiteSpace(query)
            ? FormatThreadCount(allReviewThreadItems.Length)
            : $"{ReviewThreadItems.Length:N0}/{allReviewThreadItems.Length:N0} threads";

        if (SelectedReviewThreadItem is not null && ReviewThreadItems.Any(item => string.Equals(item.Id, SelectedReviewThreadItem.Id, StringComparison.Ordinal)))
        {
            return;
        }

        SelectedReviewThreadItem = ReviewThreadItems.FirstOrDefault();
    }

    public void ToggleGitReferenceNode(GitReferenceTreeItemViewModel? node)
    {
        if (node is null || !node.HasChildren)
        {
            return;
        }

        collapsedGitReferenceNodeIds = collapsedGitReferenceNodeIds.Contains(node.Id)
            ? collapsedGitReferenceNodeIds.Remove(node.Id)
            : collapsedGitReferenceNodeIds.Add(node.Id);
        BuildGitReferenceTree();
        ApplyReferenceSelectionsToPresentation();
    }

    public async Task SelectGitReferenceNodeAsync(GitReferenceTreeItemViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        if (node.Branch is not null)
        {
            await SelectBranchAsync(node.Branch);
            return;
        }

        if (node.PullRequest is not null)
        {
            await SelectPullRequestAsync(node.PullRequest);
            return;
        }

        ToggleGitReferenceNode(node);
    }

    public async Task OpenGitHistoryTabAsync(GitReferenceTreeItemViewModel? node)
    {
        if (node?.Branch is null && node?.PullRequest is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            AddDiagnostic("Warning", "Open a repository before loading history");
            return;
        }

        if (node.Branch is not null)
        {
            await OpenBranchHistoryTabAsync(node.Branch);
            return;
        }

        if (node.PullRequest is not null)
        {
            await OpenPullRequestHistoryTabAsync(node.PullRequest);
        }
    }

    public async Task OpenGitWorkspaceTabAsync(GitReferenceTreeItemViewModel? node)
    {
        if (node?.Branch is null && node?.PullRequest is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            AddDiagnostic("Warning", "Open a repository before opening a workspace tab");
            return;
        }

        if (node.Branch is not null)
        {
            await OpenBranchWorkspaceTabAsync(node.Branch);
            return;
        }

        if (node.PullRequest is not null)
        {
            await OpenPullRequestWorkspaceTabAsync(node.PullRequest);
        }
    }

    private async Task OpenBranchWorkspaceTabAsync(GitBranchOptionViewModel branch)
    {
        var tabId = $"workspace:branch:{branch.ReferenceName}";
        if (SelectWorkspaceTab(tabId))
        {
            return;
        }

        var request = new GitDiffRequest(currentRepositoryPath!, GitDiffScope.Branch, HeadRef: branch.ReferenceName);
        var tab = WorkspaceTabViewModel.CreateGraphWorkspace(
            tabId,
            branch.ShortBranchName,
            branch.ReferenceName,
            request,
            branch.ReferenceName,
            null);
        AddWorkspaceTab(tab);
        tab.IsLoading = true;
        tab.StatusText = $"Loading workspace for {branch.ReferenceName}";
        try
        {
            await SelectBranchAsync(branch);
            CaptureGraphWorkspaceState(tab);
            tab.StatusText = StatusText;
            AddDiagnostic("Info", $"Opened workspace tab for {branch.ReferenceName}");
        }
        finally
        {
            tab.IsLoading = false;
        }
    }

    private async Task OpenPullRequestWorkspaceTabAsync(GitPullRequestOptionViewModel pullRequest)
    {
        var tabId = $"workspace:{pullRequest.KindText.ToLowerInvariant()}:{pullRequest.RemoteName}:{pullRequest.Number}";
        if (SelectWorkspaceTab(tabId))
        {
            return;
        }

        var reviewRequest = pullRequest.ToPullRequestInfo();
        var request = new GitDiffRequest(currentRepositoryPath!, GitDiffScope.Branch, pullRequest.BaseReferenceName, pullRequest.HeadRefName);
        var tab = WorkspaceTabViewModel.CreateGraphWorkspace(
            tabId,
            $"{pullRequest.KindText} {pullRequest.NumberText}",
            pullRequest.Title,
            request,
            null,
            reviewRequest);
        AddWorkspaceTab(tab);
        tab.IsLoading = true;
        tab.StatusText = $"Loading workspace for {pullRequest.KindText} {pullRequest.NumberText}";
        try
        {
            await SelectPullRequestAsync(pullRequest);
            CaptureGraphWorkspaceState(tab);
            tab.StatusText = StatusText;
            AddDiagnostic("Info", $"Opened workspace tab for {pullRequest.KindText} {pullRequest.NumberText}");
        }
        finally
        {
            tab.IsLoading = false;
        }
    }

    private async Task OpenBranchHistoryTabAsync(GitBranchOptionViewModel branch)
    {
        var tabId = $"history:branch:{branch.ReferenceName}";
        if (SelectWorkspaceTab(tabId))
        {
            return;
        }

        var tab = WorkspaceTabViewModel.CreateHistory(tabId, $"History {branch.ShortBranchName}", branch.ReferenceName);
        tab.History = GitHistoryTimelineViewModel.Create(
            branch.ReferenceName,
            new GitHistoryRequest(currentRepositoryPath!, branch.ReferenceName, MaxCount: GitHistoryPageSize));
        AddWorkspaceTab(tab);
        var operation = BeginOperation($"Loading history for {branch.ReferenceName}");
        try
        {
            await LoadGitHistoryPageAsync(tab, operation.Token);
            AddDiagnostic("Info", $"Loaded history for {branch.ReferenceName}");
            CompleteOperation(operation, "History ready");
        }
        catch (OperationCanceledException)
        {
            tab.StatusText = "History load canceled";
            CompleteOperation(operation, "History canceled");
        }
        catch (Exception exception)
        {
            tab.StatusText = "History unavailable";
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "History failed");
        }
        finally
        {
            tab.IsLoading = false;
        }
    }

    private async Task OpenPullRequestHistoryTabAsync(GitPullRequestOptionViewModel pullRequest)
    {
        var tabId = $"history:{pullRequest.KindText.ToLowerInvariant()}:{pullRequest.Number}";
        if (SelectWorkspaceTab(tabId))
        {
            return;
        }

        var tab = WorkspaceTabViewModel.CreateHistory(tabId, $"History {pullRequest.NumberText}", pullRequest.Title);
        AddWorkspaceTab(tab);
        var operation = BeginOperation($"Loading history for {pullRequest.KindText} {pullRequest.NumberText}");
        try
        {
            tab.IsLoading = true;
            tab.StatusText = $"Preparing {pullRequest.KindText} head";
            var headRef = await gitReferenceDiscoveryService.EnsurePullRequestHeadAsync(currentRepositoryPath!, pullRequest.ToPullRequestInfo(), operation.Token);
            if (string.IsNullOrWhiteSpace(headRef))
            {
                tab.StatusText = $"{pullRequest.KindText} history unavailable";
                CompleteOperation(operation, "History unavailable");
                return;
            }

            tab.History = GitHistoryTimelineViewModel.Create(
                $"{pullRequest.NumberText} {pullRequest.Title}",
                new GitHistoryRequest(currentRepositoryPath!, headRef, pullRequest.BaseReferenceName, GitHistoryPageSize));
            await LoadGitHistoryPageAsync(tab, operation.Token);
            AddDiagnostic("Info", $"Loaded history for {pullRequest.KindText} {pullRequest.NumberText}");
            CompleteOperation(operation, "History ready");
        }
        catch (OperationCanceledException)
        {
            tab.StatusText = "History load canceled";
            CompleteOperation(operation, "History canceled");
        }
        catch (Exception exception)
        {
            tab.StatusText = "History unavailable";
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "History failed");
        }
        finally
        {
            tab.IsLoading = false;
        }
    }

    public async Task LoadMoreGitHistoryAsync(WorkspaceTabViewModel? tab, GitHistoryItemViewModel? realizedItem)
    {
        var history = tab?.History;
        if (tab is null || history is null || history.IsLoadingMore || !history.HasMore)
        {
            return;
        }

        if (realizedItem is not null)
        {
            var realizedIndex = history.Commits.IndexOf(realizedItem);
            var loadThreshold = Math.Max(0, history.Commits.Count - 32);
            if (realizedIndex >= 0 && realizedIndex < loadThreshold)
            {
                return;
            }
        }

        var operation = BeginOperation($"Loading more history for {history.ReferenceText}");
        try
        {
            await LoadGitHistoryPageAsync(tab, operation.Token);
            CompleteOperation(operation, "History page loaded");
        }
        catch (OperationCanceledException)
        {
            tab.StatusText = "History load canceled";
            CompleteOperation(operation, "History canceled");
        }
        catch (Exception exception)
        {
            tab.StatusText = "History page unavailable";
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "History failed");
        }
    }

    private async Task LoadGitHistoryPageAsync(WorkspaceTabViewModel tab, CancellationToken cancellationToken)
    {
        var history = tab.History;
        if (history is null || history.IsLoadingMore || !history.HasMore)
        {
            return;
        }

        history.IsLoadingMore = true;
        tab.IsLoading = history.LoadedCount == 0;
        tab.StatusText = history.LoadedCount == 0 ? "Loading Git history" : $"Loading more commits from {history.ReferenceText}";
        try
        {
            var snapshot = await gitHistoryService.GetHistoryAsync(history.NextPageRequest, cancellationToken);
            history.AppendSnapshot(snapshot);
            tab.StatusText = history.CountText;
        }
        finally
        {
            history.IsLoadingMore = false;
            tab.IsLoading = false;
        }
    }

    private static ImmutableArray<TOption> FilterReferenceOptions<TOption>(
        ImmutableArray<TOption> options,
        string query,
        Func<TOption, string> getSearchText)
    {
        var trimmedQuery = query.Trim();
        return string.IsNullOrWhiteSpace(trimmedQuery)
            ? options
            : options.Where(option => getSearchText(option).Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
    }

    private void BuildGitReferenceTree()
    {
        var query = GitReferenceSearchText.Trim();
        var forceExpanded = !string.IsNullOrWhiteSpace(query);
        var builder = ImmutableArray.CreateBuilder<GitReferenceTreeItemViewModel>();
        var localBranches = BranchOptions
            .Where(branch => !branch.IsRemote)
            .ToImmutableArray();
        var remoteBranchGroups = BranchOptions
            .Where(branch => branch.IsRemote)
            .GroupBy(branch => string.IsNullOrWhiteSpace(branch.RemoteName) ? "origin" : branch.RemoteName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var remoteBranchCount = remoteBranchGroups.Sum(group => group.Count());

        AddGroup(
            builder,
            GitReferenceTreeItemViewModel.Group("git:branches", "Branches", "Local branches", 0, localBranches.Length, IsGitGroupExpanded("git:branches", forceExpanded)),
            () =>
            {
                foreach (var branch in localBranches.OrderByDescending(branch => branch.IsCurrent).ThenByDescending(branch => branch.IsDefault).ThenBy(branch => branch.ReferenceName, StringComparer.OrdinalIgnoreCase))
                {
                    builder.Add(GitReferenceTreeItemViewModel.BranchItem(branch, 1, IsSelectedBranch(branch)));
                }
            });

        AddGroup(
            builder,
            GitReferenceTreeItemViewModel.Group("git:remotes", "Remotes", "Remote branches", 0, remoteBranchCount, IsGitGroupExpanded("git:remotes", forceExpanded)),
            () =>
            {
                foreach (var remoteGroup in remoteBranchGroups)
                {
                    var remoteId = $"remote:{remoteGroup.Key}";
                    var remoteBranches = remoteGroup.OrderByDescending(branch => branch.IsDefault).ThenBy(branch => branch.ShortBranchName, StringComparer.OrdinalIgnoreCase).ToArray();
                    AddGroup(
                        builder,
                        GitReferenceTreeItemViewModel.Remote(remoteGroup.Key, 1, remoteBranches.Length, IsGitGroupExpanded(remoteId, forceExpanded)),
                        () =>
                        {
                            foreach (var branch in remoteBranches)
                            {
                                builder.Add(GitReferenceTreeItemViewModel.BranchItem(branch, 2, IsSelectedBranch(branch)));
                            }
                        });
                }
            });

        AddGroup(
            builder,
            GitReferenceTreeItemViewModel.Group("git:pull-requests", GetReviewRequestGroupTitle(), GetReviewRequestGroupDetail(), 0, PullRequestOptions.Length, IsGitGroupExpanded("git:pull-requests", forceExpanded)),
            () =>
            {
                foreach (var pullRequest in PullRequestOptions)
                {
                    builder.Add(GitReferenceTreeItemViewModel.PullRequestItem(pullRequest, 1, IsSelectedPullRequest(pullRequest)));
                }
            });

        GitReferenceTreeItems = builder.ToImmutable();
        var reviewRequestLabel = GetReviewRequestCountLabel(allPullRequestOptions.Length);
        GitReferenceCountText = string.IsNullOrWhiteSpace(query)
            ? $"{allBranchOptions.Length:N0} branches | {allPullRequestOptions.Length:N0} {reviewRequestLabel}"
            : $"{BranchOptions.Length:N0}/{allBranchOptions.Length:N0} branches | {PullRequestOptions.Length:N0}/{allPullRequestOptions.Length:N0} {reviewRequestLabel}";
    }

    private string GetReviewRequestGroupTitle() =>
        currentReviewRequestKind == GitReviewRequestKind.MergeRequest ? "Merge Requests" : "Pull Requests";

    private string GetReviewRequestGroupDetail() =>
        currentReviewRequestKind == GitReviewRequestKind.MergeRequest
            ? $"{FormatReviewRequestState(appState.ReviewRequestState)} GitLab merge requests"
            : $"{FormatReviewRequestState(appState.ReviewRequestState)} GitHub pull requests";

    private string GetReviewRequestCountLabel(int count)
    {
        var singular = currentReviewRequestKind == GitReviewRequestKind.MergeRequest ? "MR" : "PR";
        return count == 1 ? singular : $"{singular}s";
    }

    private void AddGroup(
        ImmutableArray<GitReferenceTreeItemViewModel>.Builder builder,
        GitReferenceTreeItemViewModel group,
        Action addChildren)
    {
        builder.Add(group);
        if (group is { HasChildren: true, IsExpanded: true })
        {
            addChildren();
        }
    }

    private bool IsGitGroupExpanded(string id, bool forceExpanded) =>
        forceExpanded || !collapsedGitReferenceNodeIds.Contains(id);

    private bool IsSelectedBranch(GitBranchOptionViewModel branch) =>
        appState.DiffScope == GitDiffScope.Branch &&
        appState.SelectedPullRequestNumber is null &&
        string.Equals(branch.ReferenceName, appState.SelectedBranchRef ?? appState.HeadRef, StringComparison.Ordinal);

    private bool IsSelectedPullRequest(GitPullRequestOptionViewModel pullRequest) =>
        appState.DiffScope == GitDiffScope.Branch &&
        appState.SelectedPullRequestNumber == pullRequest.Number;

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

        CacheCurrentDiffView();
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

        CacheCurrentDiffView();
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

        CacheCurrentDiffView();
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

        CacheCurrentDiffView();
        appState = appState with { SemanticAnalysisMode = analysisMode, LayoutNodes = null };
        previousLayout = null;
        pinnedDocumentIds = ImmutableHashSet<DiffDocumentId>.Empty;
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Semantic mode changed to {FormatSemanticAnalysisMode(analysisMode)}");
        await LoadRepositoryAsync(loadAppState: false, operationMessage: "Reloading semantic analysis");
    }

    public async Task SetLayoutModeAsync(GraphLayoutMode layoutMode, DiffCanvasScene? currentScene)
    {
        if (appState.LayoutMode == layoutMode)
        {
            ApplyAppStateToPresentation();
            return;
        }

        appState = appState with { LayoutMode = layoutMode, LayoutNodes = null };
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Layout mode changed to {FormatLayoutMode(layoutMode)}");
        await RelayoutAsync(currentScene);
    }

    public async Task SetGroupingModeAsync(GraphGroupingMode groupingMode)
    {
        if (appState.GroupingMode == groupingMode)
        {
            ApplyAppStateToPresentation();
            return;
        }

        var viewState = Scene.CaptureViewState();
        CaptureLayoutState(Scene);
        appState = appState with { GroupingMode = groupingMode };
        ApplyAppStateToPresentation();
        var nextScene = CreateScene(currentDocuments, currentSemanticGraph, previousLayout);
        nextScene.ApplyViewState(viewState);
        Scene = nextScene;
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Grouping changed to {FormatGroupingMode(groupingMode)}");
    }

    public async Task SetReviewRequestStateAsync(GitReviewRequestState reviewRequestState)
    {
        if (appState.ReviewRequestState == reviewRequestState)
        {
            ApplyAppStateToPresentation();
            return;
        }

        appState = appState with { ReviewRequestState = reviewRequestState };
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Review request list changed to {FormatReviewRequestState(reviewRequestState)}");

        if (!string.IsNullOrWhiteSpace(currentRepositoryPath) && Directory.Exists(currentRepositoryPath))
        {
            var requestId = repositoryLoadRequests.BeginRequest();
            await RefreshRepositoryReferencesAsync(currentRepositoryPath, requestId, CancellationToken.None);
        }
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
            "ReviewComments" => visibility with { ShowReviewComments = isEnabled },
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
        currentSemanticRefinementOperation?.Cancel();
        currentBlameOperation?.Cancel();
        pendingAutoReload?.Cancel();
        await StopRepositoryWatcherAsync();
        currentOperation?.Dispose();
        currentSemanticRefinementOperation?.Dispose();
        currentBlameOperation?.Dispose();
        currentOperation = null;
        currentSemanticRefinementOperation = null;
        currentBlameOperation = null;
    }

    public void CancelCurrentOperation()
    {
        currentOperation?.Cancel();
        currentSemanticRefinementOperation?.Cancel();
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

    public Task OpenFileDiffTabAsync(FileExplorerNodeViewModel? node, FileDiffDisplayMode displayMode)
    {
        if (node is null)
        {
            return Task.CompletedTask;
        }

        if (!node.IsFile)
        {
            ToggleExplorerNode(node);
            return Task.CompletedTask;
        }

        return string.IsNullOrWhiteSpace(node.DocumentId)
            ? OpenFileDiffTabByPathAsync(node.Path, displayMode)
            : OpenFileDiffTabAsync(node.DocumentId, displayMode);
    }

    public Task OpenBlameTabAsync(FileExplorerNodeViewModel? node)
    {
        if (node is null)
        {
            return Task.CompletedTask;
        }

        if (!node.IsFile)
        {
            ToggleExplorerNode(node);
            return Task.CompletedTask;
        }

        return string.IsNullOrWhiteSpace(node.DocumentId)
            ? OpenBlameTabByPathAsync(node.Path)
            : OpenBlameTabAsync(node.DocumentId);
    }

    public async Task OpenFileDiffTabAsync(string documentId, FileDiffDisplayMode displayMode)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        var document = currentDocuments.FirstOrDefault(document => string.Equals(document.Id.Value, documentId, StringComparison.Ordinal));
        if (document is null)
        {
            AddDiagnostic("Warning", $"No document node for {documentId}");
            return;
        }

        await OpenFileDiffTabAsync(document, displayMode);
    }

    public async Task OpenBlameTabAsync(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        var document = currentDocuments.FirstOrDefault(document => string.Equals(document.Id.Value, documentId, StringComparison.Ordinal));
        if (document is null)
        {
            AddDiagnostic("Warning", $"No document node for {documentId}");
            return;
        }

        await OpenBlameTabAsync(document);
    }

    private async Task OpenFileDiffTabByPathAsync(string path, FileDiffDisplayMode displayMode)
    {
        var normalizedPath = NormalizeRepositoryPath(path);
        var document = currentDocuments.FirstOrDefault(document =>
            string.Equals(NormalizeRepositoryPath(document.Metadata.Path), normalizedPath, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(document.Metadata.OldPath) &&
                string.Equals(NormalizeRepositoryPath(document.Metadata.OldPath), normalizedPath, StringComparison.OrdinalIgnoreCase)));
        if (document is null)
        {
            AddDiagnostic("Warning", $"No document node for {path}");
            return;
        }

        await OpenFileDiffTabAsync(document, displayMode);
    }

    private async Task OpenBlameTabByPathAsync(string path)
    {
        var normalizedPath = NormalizeRepositoryPath(path);
        var document = currentDocuments.FirstOrDefault(document =>
            string.Equals(NormalizeRepositoryPath(document.Metadata.Path), normalizedPath, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(document.Metadata.OldPath) &&
                string.Equals(NormalizeRepositoryPath(document.Metadata.OldPath), normalizedPath, StringComparison.OrdinalIgnoreCase)));
        if (document is null)
        {
            AddDiagnostic("Warning", $"No document node for {path}");
            return;
        }

        await OpenBlameTabAsync(document);
    }

    private async Task OpenFileDiffTabAsync(DiffDocumentSnapshot document, FileDiffDisplayMode displayMode)
    {
        var tabId = $"file:{document.Id.Value}";
        if (FindWorkspaceTab(tabId) is { FileDiff: not null } existingTab)
        {
            existingTab.FileDiff.SetDisplayMode(displayMode);
            SelectedWorkspaceTab = existingTab;
            return;
        }

        var fullText = await LoadFullFileTextAsync(document, CancellationToken.None);
        var fullFileDocument = await CreateTokenizedFullFileDocumentAsync(document, fullText, CancellationToken.None);
        var foldRegions = new CodeFoldingService().CreateFoldRegions(fullFileDocument);
        var fileDiff = FileDiffTabViewModel.FromDocument(document, fullFileDocument, fullText, foldRegions, displayMode);
        var tab = WorkspaceTabViewModel.CreateFileDiff(tabId, Path.GetFileName(document.Metadata.Path), document.Metadata.Path, fileDiff);
        AddWorkspaceTab(tab);
        AddDiagnostic("Info", $"Opened file diff tab for {document.Metadata.Path}");
    }

    private async Task OpenBlameTabAsync(DiffDocumentSnapshot document)
    {
        if (string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            AddDiagnostic("Warning", "Open a repository before loading blame");
            return;
        }

        var path = document.Metadata.Path;
        var tabId = $"blame:{path}";
        if (SelectWorkspaceTab(tabId))
        {
            return;
        }

        var blameView = BlameTabViewModel.Loading(path, document.Metadata.Language);
        var tab = WorkspaceTabViewModel.CreateBlame(tabId, $"Blame {Path.GetFileName(path)}", path, blameView);
        AddWorkspaceTab(tab);
        tab.IsLoading = true;
        tab.StatusText = $"Loading blame for {path}";
        var operation = BeginOperation($"Loading blame for {path}");
        try
        {
            var blameRevision = GetActiveBlameRevision();
            var blameTask = gitBlameService.GetFileBlameAsync(currentRepositoryPath, path, operation.Token, blameRevision);
            var historyTask = gitHistoryService.GetHistoryAsync(
                new GitHistoryRequest(currentRepositoryPath, blameRevision ?? "HEAD", MaxCount: 160, PathFilter: path),
                operation.Token);
            await Task.WhenAll(blameTask, historyTask);

            tab.Blame = BlameTabViewModel.FromBlame(path, document.Metadata.Language, await blameTask, (await historyTask).Commits);
            tab.StatusText = tab.Blame.StatusText;
            AddDiagnostic("Info", $"Opened blame tab for {path}");
            CompleteOperation(operation, "Blame ready");
        }
        catch (OperationCanceledException)
        {
            tab.StatusText = "Blame load canceled";
            CompleteOperation(operation, "Blame canceled");
        }
        catch (Exception exception)
        {
            tab.StatusText = "Blame unavailable";
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "Blame failed");
        }
        finally
        {
            tab.IsLoading = false;
        }
    }

    private string? GetActiveBlameRevision()
    {
        var headRef = NormalizeRef(currentGitSnapshot?.Request.HeadRef);
        return IsCurrentHeadReference(headRef) ? null : headRef;
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

    public FocusRequest? FocusReviewThread(ReviewThreadItemViewModel? thread)
    {
        if (thread is null)
        {
            ReviewPanelStatusText = "Select a review thread";
            return null;
        }

        if (string.IsNullOrWhiteSpace(thread.Path))
        {
            ReviewPanelStatusText = "Thread has no linked changed file";
            AddDiagnostic("Warning", ReviewPanelStatusText);
            return null;
        }

        var normalizedThreadPath = NormalizeRepositoryPath(thread.Path);
        var document = currentDocuments.FirstOrDefault(document =>
            string.Equals(NormalizeRepositoryPath(document.Metadata.Path), normalizedThreadPath, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(document.Metadata.OldPath) &&
                string.Equals(NormalizeRepositoryPath(document.Metadata.OldPath), normalizedThreadPath, StringComparison.OrdinalIgnoreCase)));
        if (document is null)
        {
            ReviewPanelStatusText = $"No changed node for {thread.Path}";
            AddDiagnostic("Warning", ReviewPanelStatusText);
            return null;
        }

        var explorerItem = allExplorerItems.FirstOrDefault(item => string.Equals(item.DocumentId, document.Id.Value, StringComparison.Ordinal));
        if (explorerItem is not null)
        {
            SelectExplorerItem(explorerItem);
        }

        var location = thread.Line is int lineNumber ? $"{thread.Path}:{lineNumber}" : thread.Path;
        ReviewPanelStatusText = $"Focused {location}";
        AddDiagnostic("Info", ReviewPanelStatusText);
        return new FocusRequest(document.Id.Value, thread.Line);
    }

    public FocusRequest? FocusAnnotation(DiffAnnotation annotation)
    {
        if (annotation.ActionKind == DiffAnnotationActionKind.ReviewThread)
        {
            return FocusReviewAnnotation(annotation);
        }

        var document = currentDocuments.FirstOrDefault(document => document.Id == annotation.DocumentId);
        if (document is null)
        {
            AddDiagnostic("Warning", $"No document node for annotation {annotation.Label}");
            return null;
        }

        var explorerItem = allExplorerItems.FirstOrDefault(item => string.Equals(item.DocumentId, document.Id.Value, StringComparison.Ordinal));
        if (explorerItem is not null)
        {
            SelectExplorerItem(explorerItem);
        }

        var line = annotation.DisplayLineNumber;
        var location = line is int lineNumber ? $"{document.Metadata.Path}:{lineNumber}" : document.Metadata.Path;
        AddDiagnostic("Info", $"Focused {annotation.Kind} annotation in {location}");
        return new FocusRequest(document.Id.Value, line);
    }

    private FocusRequest? FocusReviewAnnotation(DiffAnnotation annotation)
    {
        var threadId = annotation.ActionTargetId;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            ReviewPanelStatusText = "Review annotation has no linked thread";
            AddDiagnostic("Warning", ReviewPanelStatusText);
            return FocusAnnotationDocument(annotation);
        }

        var thread = allReviewThreadItems.FirstOrDefault(item => string.Equals(item.Id, threadId, StringComparison.Ordinal));
        if (thread is null)
        {
            ReviewPanelStatusText = "Review thread is not loaded";
            AddDiagnostic("Warning", ReviewPanelStatusText);
            return FocusAnnotationDocument(annotation);
        }

        if (!ReviewThreadItems.Any(item => string.Equals(item.Id, thread.Id, StringComparison.Ordinal)))
        {
            ReviewSearchText = string.Empty;
            ApplyReviewThreadFilter();
        }

        SelectedRailTabIndex = 2;
        SelectedReviewThreadItem = ReviewThreadItems.FirstOrDefault(item => string.Equals(item.Id, thread.Id, StringComparison.Ordinal)) ?? thread;
        return FocusReviewThread(SelectedReviewThreadItem);
    }

    private FocusRequest? FocusAnnotationDocument(DiffAnnotation annotation)
    {
        var document = currentDocuments.FirstOrDefault(document => document.Id == annotation.DocumentId);
        return document is null ? null : new FocusRequest(document.Id.Value, annotation.DisplayLineNumber);
    }

    public Task StageSelectedFileAsync() => RunReviewActionAsync(
        "Staging",
        (repositoryPath, path, cancellationToken) => gitReviewService.StageFileAsync(repositoryPath, path, cancellationToken));

    public Task UnstageSelectedFileAsync() => RunReviewActionAsync(
        "Unstaging",
        (repositoryPath, path, cancellationToken) => gitReviewService.UnstageFileAsync(repositoryPath, path, cancellationToken));

    public Task RefreshReviewDiscussionAsync() => RefreshReviewDiscussionAsync(CancellationToken.None);

    public async Task RefreshReviewDiscussionAsync(CancellationToken cancellationToken)
    {
        var option = SelectedPullRequestOption;
        if (option is null || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            ClearReviewDiscussion("Select a PR or MR");
            return;
        }

        try
        {
            ReviewPanelStatusText = $"Loading {option.KindText} review";
            currentReviewThreads = [];
            RefreshSceneAnnotations();
            var snapshot = await gitReviewDiscussionService.GetDiscussionAsync(currentRepositoryPath, option.ToPullRequestInfo(), cancellationToken);
            currentReviewThreads = snapshot.Threads;
            allReviewThreadItems = snapshot.Threads.Select(ReviewThreadItemViewModel.FromThread).ToImmutableArray();
            ApplyReviewThreadFilter();
            RefreshSceneAnnotations();
            ReviewPanelStatusText = snapshot.StatusMessage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReviewPanelStatusText = "Review load canceled";
        }
        catch (Exception exception)
        {
            ReviewPanelStatusText = "Review unavailable";
            AddDiagnostic("Warning", $"Review discussion failed: {exception.Message}");
        }
    }

    public async Task AddReviewCommentAsync()
    {
        var option = SelectedPullRequestOption;
        if (option is null || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            ReviewPanelStatusText = "Select a PR or MR";
            return;
        }

        var body = ReviewCommentText.Trim();
        if (string.IsNullOrWhiteSpace(body))
        {
            ReviewPanelStatusText = "Comment is empty";
            return;
        }

        var operation = BeginOperation($"Adding {option.KindText} comment");
        try
        {
            var result = await gitReviewDiscussionService.AddCommentAsync(currentRepositoryPath, option.ToPullRequestInfo(), body, operation.Token);
            ReviewPanelStatusText = result.Message;
            AddDiagnostic(result.Succeeded ? "Info" : "Warning", result.Message);
            CompleteOperation(operation, result.Succeeded ? "Comment added" : "Comment failed");
            if (result.Succeeded)
            {
                ReviewCommentText = string.Empty;
                await RefreshReviewDiscussionAsync(operation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            ReviewPanelStatusText = "Comment canceled";
            CompleteOperation(operation, "Comment canceled");
        }
        catch (Exception exception)
        {
            ReviewPanelStatusText = "Comment failed";
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "Comment failed");
        }
    }

    public async Task ReplyToSelectedReviewThreadAsync()
    {
        var option = SelectedPullRequestOption;
        var thread = SelectedReviewThreadItem;
        if (option is null || thread is null || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            ReviewPanelStatusText = "Select a review thread";
            return;
        }

        var body = ReviewCommentText.Trim();
        if (string.IsNullOrWhiteSpace(body))
        {
            ReviewPanelStatusText = "Reply is empty";
            return;
        }

        var operation = BeginOperation($"Replying to {option.KindText} thread");
        try
        {
            var result = await gitReviewDiscussionService.ReplyToThreadAsync(currentRepositoryPath, option.ToPullRequestInfo(), thread.Id, body, operation.Token);
            ReviewPanelStatusText = result.Message;
            AddDiagnostic(result.Succeeded ? "Info" : "Warning", result.Message);
            CompleteOperation(operation, result.Succeeded ? "Reply added" : "Reply failed");
            if (result.Succeeded)
            {
                ReviewCommentText = string.Empty;
                await RefreshReviewDiscussionAsync(operation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            ReviewPanelStatusText = "Reply canceled";
            CompleteOperation(operation, "Reply canceled");
        }
        catch (Exception exception)
        {
            ReviewPanelStatusText = "Reply failed";
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "Reply failed");
        }
    }

    public async Task ToggleSelectedReviewThreadResolvedAsync()
    {
        var option = SelectedPullRequestOption;
        var thread = SelectedReviewThreadItem;
        if (option is null || thread is null || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            ReviewPanelStatusText = "Select a review thread";
            return;
        }

        var shouldResolve = !thread.IsResolved;
        var operation = BeginOperation(shouldResolve ? "Resolving review thread" : "Reopening review thread");
        try
        {
            var result = await gitReviewDiscussionService.SetThreadResolvedAsync(currentRepositoryPath, option.ToPullRequestInfo(), thread.Id, shouldResolve, operation.Token);
            ReviewPanelStatusText = result.Message;
            AddDiagnostic(result.Succeeded ? "Info" : "Warning", result.Message);
            CompleteOperation(operation, result.Succeeded ? "Thread updated" : "Thread update failed");
            if (result.Succeeded)
            {
                await RefreshReviewDiscussionAsync(operation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            ReviewPanelStatusText = "Thread update canceled";
            CompleteOperation(operation, "Thread update canceled");
        }
        catch (Exception exception)
        {
            ReviewPanelStatusText = "Thread update failed";
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "Thread update failed");
        }
    }

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

    private async Task LoadRepositoryAsync(
        bool loadAppState,
        string operationMessage,
        DiffCanvasSceneViewState? preservedSceneState = null,
        long repositoryRequestId = 0)
    {
        var requestId = repositoryRequestId == 0 ? repositoryLoadRequests.BeginRequest() : repositoryRequestId;
        if (!IsCurrentRepositoryRequest(requestId))
        {
            return;
        }

        var operation = BeginOperation(operationMessage);
        try
        {
            var cancellationToken = operation.Token;
            EnsureCurrentRepositoryRequest(requestId, cancellationToken);
            if (loadAppState)
            {
                ReportProgress(0.05, "Loading app state", cancellationToken);
                var loadedState = await appStateStore.LoadAsync(cancellationToken);
                EnsureCurrentRepositoryRequest(requestId, cancellationToken);
                appState = loadedState;
            }

            EnsureCurrentRepositoryRequest(requestId, cancellationToken);
            ApplyAppStateToPresentation();
            ReportProgress(0.1, "Discovering repository", cancellationToken);
            var repositoryDiscovery = new GitRepositoryDiscovery();
            var startPath = !string.IsNullOrWhiteSpace(appState.RepositoryPath) && Directory.Exists(appState.RepositoryPath)
                ? appState.RepositoryPath
                : Environment.CurrentDirectory;
            var repositoryRoot = await repositoryDiscovery.DiscoverRootAsync(startPath, cancellationToken);
            EnsureCurrentRepositoryRequest(requestId, cancellationToken);

            if (string.IsNullOrWhiteSpace(repositoryRoot))
            {
                currentRepositoryPath = null;
                currentGitSnapshot = null;
                ClearReferenceOptions("Refs unavailable");
                await StopRepositoryWatcherAsync();
                EnsureCurrentRepositoryRequest(requestId, cancellationToken);
                InitializeSampleDocuments(SampleDiffDocuments.Create());
                StatusText = "No Git repository found | showing sample diff graph";
                AddDiagnostic("Info", "No Git repository found; using sample graph");
                CompleteOperation(operation, "Sample graph ready");
                return;
            }

            var isNewRepositoryRoot = !string.Equals(currentRepositoryPath, repositoryRoot, StringComparison.Ordinal);
            currentRepositoryPath = repositoryRoot;
            appState = appState with { RepositoryPath = repositoryRoot };
            ApplyAppStateToPresentation();
            if (isNewRepositoryRoot || BranchOptions.IsDefaultOrEmpty)
            {
                ClearReferenceOptions("Loading branches");
            }

            _ = RefreshRepositoryReferencesAsync(repositoryRoot, requestId, CancellationToken.None);
            var documentService = new GitDiffDocumentService();
            var request = new GitDiffRequest(repositoryRoot, appState.DiffScope, NormalizeRef(appState.BaseRef), NormalizeRef(appState.HeadRef));

            if (TryApplyCachedDiffView(repositoryRoot, request, requestId, cancellationToken))
            {
                await SaveOptionsAsync(cancellationToken);
                EnsureCurrentRepositoryRequest(requestId, cancellationToken);
                await RestartRepositoryWatcherAsync(repositoryRoot, cancellationToken);
                EnsureCurrentRepositoryRequest(requestId, cancellationToken);
                CompleteOperation(operation, "Cached diff ready");
                return;
            }

            ReportProgress(0.25, "Loading Git diff", cancellationToken);
            var snapshot = await documentService.LoadDocumentsAsync(request, appState.DiffContextMode, cancellationToken);
            EnsureCurrentRepositoryRequest(requestId, cancellationToken);

            if (snapshot.Documents.Length == 0)
            {
                var emptyScopeText = FormatDiffScope(appState.DiffScope);
                ResetRepositoryPresentation(
                    Path.GetFileName(repositoryRoot),
                    $"{Path.GetFileName(repositoryRoot)} | no {emptyScopeText} changes | {FormatDiffContextMode(appState.DiffContextMode)} | base {snapshot.GitSnapshot.DefaultBranch ?? "unknown"}",
                    $"{Path.GetFileName(repositoryRoot)} | no {emptyScopeText} changes",
                    isRepository: true);
                await SaveOptionsAsync(cancellationToken);
                EnsureCurrentRepositoryRequest(requestId, cancellationToken);
                await RestartRepositoryWatcherAsync(repositoryRoot, cancellationToken);
                EnsureCurrentRepositoryRequest(requestId, cancellationToken);
                AddDiagnostic("Info", $"Repository has no {emptyScopeText} changes");
                CompleteOperation(operation, "Repository has no changes");
                return;
            }

            ReportProgress(0.45, "Preparing document graph", cancellationToken);
            var reviewedDocuments = DiffReviewDocumentTransformer.Apply(snapshot.Documents, appState.ReviewMode);
            reviewedDocuments = new DiffConflictAnalyzer().Highlight(reviewedDocuments);
            if (appState.CollapseUnchangedContext)
            {
                reviewedDocuments = DiffContextFolder.Apply(reviewedDocuments);
            }

            reviewedDocuments = InlineDiffAnnotator.Annotate(reviewedDocuments);
            EnsureCurrentRepositoryRequest(requestId, cancellationToken);
            await SetDocumentsAsync(
                reviewedDocuments,
                $"{Path.GetFileName(repositoryRoot)} | {snapshot.GitSnapshot.Files.Length} {FormatDiffScope(appState.DiffScope)} changes | {FormatDiffContextMode(appState.DiffContextMode)} | {FormatReviewMode(appState.ReviewMode)} | {FormatReferenceText(request, snapshot.GitSnapshot.DefaultBranch)}",
                repositoryRoot,
                snapshot.GitSnapshot,
                preservedSceneState,
                requestId,
                cancellationToken);
            EnsureCurrentRepositoryRequest(requestId, cancellationToken);
            CompleteOperation(operation, "Repository diff ready");
        }
        catch (OperationCanceledException)
        {
            var shouldReport = IsCurrentRepositoryRequest(requestId) && IsCurrentOperation(operation);
            CompleteOperation(operation, "Load canceled");
            if (shouldReport)
            {
                StatusText = "Load canceled | showing current diff graph";
            }
        }
        catch (Exception exception)
        {
            var shouldReport = IsCurrentRepositoryRequest(requestId) && IsCurrentOperation(operation);
            CompleteOperation(operation, "Load failed");
            if (shouldReport)
            {
                StatusText = $"Git load failed: {exception.Message} | showing sample diff graph";
                AddDiagnostic("Error", exception.Message);
            }
        }
    }

    private void InitializeSampleDocuments(ImmutableArray<DiffDocumentSnapshot> documents)
    {
        currentDocuments = documents;
        currentSemanticGraph = SemanticGraph.Empty;
        currentGitSnapshot = null;
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

    private void ResetRepositoryPresentation(string repositoryName, string contextText, string statusText, bool isRepository)
    {
        currentDocuments = [];
        currentSemanticGraph = SemanticGraph.Empty;
        currentGitSnapshot = null;
        currentStatusPrefix = contextText;
        currentDocumentsAreRepositoryDocuments = isRepository;
        previousLayout = null;
        pinnedDocumentIds = ImmutableHashSet<DiffDocumentId>.Empty;
        SelectExplorerItem(null);
        UpdateChangeNavigation(currentDocuments);
        SetExplorerItems([]);
        UpdateSemanticNavigation(SemanticGraph.Empty, currentDocuments);
        UpdateImpactSummary(currentDocuments, SemanticGraph.Empty);
        UpdateWorkspaceSummary(repositoryName, contextText, 0, 0);
        Scene = CreateScene(currentDocuments, SemanticGraph.Empty, null);
        StatusText = statusText;
    }

    private async Task SetDocumentsAsync(
        ImmutableArray<DiffDocumentSnapshot> documents,
        string statusPrefix,
        string repositoryPath,
        GitDiffSnapshot? gitSnapshot,
        DiffCanvasSceneViewState? preservedSceneState,
        long repositoryRequestId,
        CancellationToken cancellationToken)
    {
        EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);
        var preservedSelectedDocumentId = preservedSceneState?.SelectedDocumentId ?? selectedExplorerItem?.DocumentId;
        currentDocuments = documents;
        currentGitSnapshot = gitSnapshot;
        currentStatusPrefix = statusPrefix;
        UpdateChangeNavigation(documents);
        currentRepositoryPath = string.IsNullOrWhiteSpace(repositoryPath) ? null : repositoryPath;
        currentDocumentsAreRepositoryDocuments = !string.IsNullOrWhiteSpace(repositoryPath);
        SelectExplorerItem(null);
        RestoreLayoutState(documents, preservedSceneState);

        ReportProgress(0.52, "Running initial layout", cancellationToken);
        var initialLayout = await LayoutDocumentsAsync(documents, SemanticGraph.Empty, cancellationToken);
        EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);

        currentSemanticGraph = SemanticGraph.Empty;
        previousLayout = initialLayout;
        Scene = CreateScene(documents, SemanticGraph.Empty, initialLayout);
        if (preservedSceneState is not null)
        {
            Scene.ApplyViewState(preservedSceneState);
            CaptureLayoutState(Scene);
        }

        SetExplorerItems(documents.Select(document => new ExplorerItemViewModel(document.Metadata.Path, document.Metadata.Status, document.Metadata.Language)).ToImmutableArray());
        RestoreSelectedExplorerItem(preservedSelectedDocumentId);
        UpdateSemanticNavigation(SemanticGraph.Empty, documents);
        UpdateImpactSummary(documents, SemanticGraph.Empty);
        UpdateWorkspaceSummary(
            string.IsNullOrWhiteSpace(repositoryPath) ? "SemanticDiff" : Path.GetFileName(repositoryPath),
            statusPrefix,
            documents.Length,
            0);
        StatusText = $"{statusPrefix} | {documents.Length} nodes | document graph ready | tokenizing";

        ReportProgress(0.6, "Tokenizing documents", cancellationToken);
        var tokenizationProgress = new Progress<(double Value, string Message)>(update =>
            ReportProgress(0.6 + update.Value * 0.14, update.Message, cancellationToken));
        var tokenizedDocuments = await TokenizeAsync(documents, cancellationToken, tokenizationProgress);
        EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);

        var tokenizedViewState = Scene.CaptureViewState();
        currentDocuments = tokenizedDocuments;
        UpdateChangeNavigation(tokenizedDocuments);
        var tokenizedScene = CreateScene(tokenizedDocuments, SemanticGraph.Empty, previousLayout);
        tokenizedScene.ApplyViewState(tokenizedViewState);
        Scene = tokenizedScene;
        CaptureLayoutState(Scene);
        SetExplorerItems(tokenizedDocuments.Select(document => new ExplorerItemViewModel(document.Metadata.Path, document.Metadata.Status, document.Metadata.Language)).ToImmutableArray());
        RestoreSelectedExplorerItem(preservedSelectedDocumentId);
        var initialSemanticAnalysisMode = GetInitialSemanticAnalysisMode(appState.SemanticAnalysisMode);
        StatusText = $"{statusPrefix} | {tokenizedDocuments.Length} nodes | syntax coloring ready | analyzing semantics";

        ReportProgress(0.76, $"Analyzing semantics ({FormatSemanticAnalysisMode(initialSemanticAnalysisMode)})", cancellationToken);
        var semanticGraph = await AnalyzeSemanticsAsync(repositoryPath, gitSnapshot, tokenizedDocuments, initialSemanticAnalysisMode, cancellationToken);
        EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);
        currentSemanticGraph = semanticGraph;
        ReportProgress(0.88, "Running semantic layout", cancellationToken);
        var layout = await LayoutDocumentsAsync(tokenizedDocuments, semanticGraph, cancellationToken);
        EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);
        var finalViewState = preservedSceneState is not null ? Scene.CaptureViewState() : null;
        previousLayout = layout;
        Scene = CreateScene(tokenizedDocuments, semanticGraph, layout);
        if (finalViewState is not null)
        {
            Scene.ApplyViewState(finalViewState);
            CaptureLayoutState(Scene);
        }

        SetExplorerItems(tokenizedDocuments.Select(document => new ExplorerItemViewModel(document.Metadata.Path, document.Metadata.Status, document.Metadata.Language)).ToImmutableArray());
        RestoreSelectedExplorerItem(preservedSelectedDocumentId);
        UpdateSemanticNavigation(semanticGraph, tokenizedDocuments);
        var impactSummary = UpdateImpactSummary(tokenizedDocuments, semanticGraph);
        UpdateWorkspaceSummary(
            string.IsNullOrWhiteSpace(repositoryPath) ? "SemanticDiff" : Path.GetFileName(repositoryPath),
            statusPrefix,
            tokenizedDocuments.Length,
            semanticGraph.Edges.Length);
        StatusText = $"{statusPrefix} | {tokenizedDocuments.Length} nodes | {semanticGraph.Edges.Length} semantic edges | {FormatImpactStatus(impactSummary)} | layout ready";
        ReportProgress(0.95, "Saving app state", cancellationToken);
        await SaveStateAsync(cancellationToken);
        EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);
        await RestartRepositoryWatcherAsync(repositoryPath, cancellationToken);
        EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);
        AddDiagnostic("Info", preservedSceneState is null
            ? $"Loaded {tokenizedDocuments.Length} document nodes, {semanticGraph.Edges.Length} semantic edges, {impactSummary.ChangedSymbolCount} changed symbols"
            : $"Smart refresh synced {tokenizedDocuments.Length} document nodes without resetting the canvas view");
        CacheCurrentDiffView();

        if (appState.SemanticAnalysisMode == SemanticAnalysisMode.WorkspaceThenSyntax)
        {
            _ = RefineWorkspaceSemanticsAsync(tokenizedDocuments, statusPrefix, repositoryPath, gitSnapshot, repositoryRequestId);
        }
    }

    private async Task RefineWorkspaceSemanticsAsync(
        ImmutableArray<DiffDocumentSnapshot> documents,
        string statusPrefix,
        string repositoryPath,
        GitDiffSnapshot? gitSnapshot,
        long repositoryRequestId)
    {
        var refinementOperation = BeginSemanticRefinementOperation();
        try
        {
            var cancellationToken = refinementOperation.Token;
            EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);
            AddDiagnostic("Info", "Refining semantic graph with MSBuild");
            StatusText = $"{statusPrefix} | {documents.Length} nodes | refining MSBuild semantics";

            var semanticGraph = await AnalyzeSemanticsAsync(repositoryPath, gitSnapshot, documents, SemanticAnalysisMode.WorkspaceThenSyntax, cancellationToken);
            EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);
            var viewState = Scene.CaptureViewState();
            currentSemanticGraph = semanticGraph;
            var layout = await LayoutDocumentsAsync(documents, semanticGraph, cancellationToken);
            EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);

            previousLayout = layout;
            var nextScene = CreateScene(documents, semanticGraph, layout);
            nextScene.ApplyViewState(viewState);
            Scene = nextScene;
            CaptureLayoutState(Scene);
            UpdateSemanticNavigation(semanticGraph, documents);
            var impactSummary = UpdateImpactSummary(documents, semanticGraph);
            UpdateWorkspaceSummary(
                string.IsNullOrWhiteSpace(repositoryPath) ? "SemanticDiff" : Path.GetFileName(repositoryPath),
                statusPrefix,
                documents.Length,
                semanticGraph.Edges.Length);
            StatusText = $"{statusPrefix} | {documents.Length} nodes | {semanticGraph.Edges.Length} semantic edges | {FormatImpactStatus(impactSummary)} | MSBuild semantics ready";
            await SaveStateAsync(cancellationToken);
            CacheCurrentDiffView();
            AddDiagnostic("Info", $"MSBuild semantic refinement produced {semanticGraph.Edges.Length} semantic edges");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentRepositoryRequest(repositoryRequestId))
            {
                AddDiagnostic("Warning", $"MSBuild semantic refinement failed: {exception.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(currentSemanticRefinementOperation, refinementOperation))
            {
                currentSemanticRefinementOperation = null;
            }

            refinementOperation.Dispose();
        }
    }

    private static SemanticAnalysisMode GetInitialSemanticAnalysisMode(SemanticAnalysisMode analysisMode) =>
        analysisMode == SemanticAnalysisMode.WorkspaceThenSyntax ? SemanticAnalysisMode.FastSyntaxOnly : analysisMode;

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
            return await layoutEngine.LayoutAsync(new GraphLayoutRequest(documents, semanticGraph, new Size2(620, 420), previousNodes, pinnedIds, appState.LayoutMode), cancellationToken).ConfigureAwait(false);
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
            LayoutMode = appState.LayoutMode,
            GroupingMode = appState.GroupingMode,
            ReviewRequestState = appState.ReviewRequestState,
            SelectedBranchRef = appState.SelectedBranchRef,
            SelectedPullRequestNumber = appState.SelectedPullRequestNumber,
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
            LayoutMode = appState.LayoutMode,
            GroupingMode = appState.GroupingMode,
            ReviewRequestState = appState.ReviewRequestState,
            SelectedBranchRef = appState.SelectedBranchRef,
            SelectedPullRequestNumber = appState.SelectedPullRequestNumber,
            LeftPaneWidth = NormalizeLeftPaneWidth(LeftPaneWidth)
        };
        await appStateStore.SaveAsync(appState, cancellationToken);
    }

    private void CacheCurrentDiffView()
    {
        if (!currentDocumentsAreRepositoryDocuments || currentGitSnapshot is null || currentDocuments.IsDefaultOrEmpty || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return;
        }

        if (!IsCacheableDiffView(currentGitSnapshot.Request))
        {
            return;
        }

        CaptureLayoutState(Scene);
        var key = CreateDiffViewCacheKey(currentRepositoryPath, currentGitSnapshot.Request);
        var selectedDocumentId = selectedExplorerItem?.DocumentId;
        diffViewCache[key] = new DiffViewCacheEntry(
            key,
            currentRepositoryPath,
            currentDocuments,
            currentSemanticGraph,
            Scene,
            previousLayout,
            pinnedDocumentIds,
            currentGitSnapshot,
            currentStatusPrefix,
            selectedDocumentId,
            DateTimeOffset.UtcNow);
        if (!diffViewCacheOrder.Contains(key, StringComparer.Ordinal))
        {
            diffViewCacheOrder.Enqueue(key);
        }

        while (diffViewCacheOrder.Count > MaxCachedDiffViews)
        {
            var staleKey = diffViewCacheOrder.Dequeue();
            if (!string.Equals(staleKey, key, StringComparison.Ordinal))
            {
                diffViewCache.Remove(staleKey);
            }
        }

        UpdateDiffViewCacheText();
    }

    private bool TryApplyCachedDiffView(string repositoryPath, GitDiffRequest request, long repositoryRequestId, CancellationToken cancellationToken)
    {
        if (!IsCacheableDiffView(request))
        {
            return false;
        }

        var key = CreateDiffViewCacheKey(repositoryPath, request);
        if (!diffViewCache.TryGetValue(key, out var entry))
        {
            return false;
        }

        EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);
        currentDocuments = entry.Documents;
        currentSemanticGraph = entry.SemanticGraph;
        currentGitSnapshot = entry.GitSnapshot;
        currentStatusPrefix = entry.StatusPrefix;
        currentRepositoryPath = entry.RepositoryPath;
        currentDocumentsAreRepositoryDocuments = true;
        previousLayout = entry.PreviousLayout;
        pinnedDocumentIds = entry.PinnedDocumentIds;
        Scene = entry.Scene.WithAnnotations(CreateAnnotations(entry.Documents, entry.SemanticGraph), appState.EffectiveAnnotationVisibility);
        SetExplorerItems(entry.Documents.Select(document => new ExplorerItemViewModel(document.Metadata.Path, document.Metadata.Status, document.Metadata.Language)).ToImmutableArray());
        RestoreSelectedExplorerItem(entry.SelectedDocumentId);
        UpdateChangeNavigation(entry.Documents);
        UpdateSemanticNavigation(entry.SemanticGraph, entry.Documents);
        var impactSummary = UpdateImpactSummary(entry.Documents, entry.SemanticGraph);
        UpdateWorkspaceSummary(Path.GetFileName(repositoryPath), entry.StatusPrefix, entry.Documents.Length, entry.SemanticGraph.Edges.Length);
        StatusText = $"{entry.StatusPrefix} | {entry.Documents.Length} nodes | {entry.SemanticGraph.Edges.Length} semantic edges | {FormatImpactStatus(impactSummary)} | cached view ready";
        AddDiagnostic("Info", $"Restored cached semantic diff view for {FormatReferenceText(appState)}");
        UpdateDiffViewCacheText();
        return true;
    }

    private string CreateDiffViewCacheKey(string repositoryPath, GitDiffRequest request)
    {
        var normalizedRepositoryPath = Path.GetFullPath(repositoryPath);
        return string.Join('\u001f',
            normalizedRepositoryPath,
            request.Scope.ToString(),
            NormalizeRef(request.BaseRef) ?? string.Empty,
            NormalizeRef(request.HeadRef) ?? string.Empty,
            appState.DiffContextMode.ToString(),
            appState.ReviewMode.ToString(),
            appState.CollapseUnchangedContext ? "fold" : "full",
            appState.SemanticAnalysisMode.ToString(),
            appState.LayoutMode.ToString(),
            appState.GroupingMode.ToString(),
            appState.ShowSemanticEdges ? "edges" : "no-edges");
    }

    private void UpdateDiffViewCacheText()
    {
        DiffViewCacheText = diffViewCache.Count == 0 ? "Cache empty" : $"{diffViewCache.Count:N0} cached views";
    }

    private static bool IsCacheableDiffView(GitDiffRequest request) =>
        request.Scope == GitDiffScope.Branch && !IsCurrentHeadReference(request.HeadRef);

    private static bool IsCurrentHeadReference(string? reference) =>
        string.IsNullOrWhiteSpace(reference) || string.Equals(reference.Trim(), "HEAD", StringComparison.Ordinal);

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
        LayoutModeText = FormatLayoutMode(appState.LayoutMode);
        SelectedLayoutModeOption = LayoutModeOptions.FirstOrDefault(option => option.Mode == appState.LayoutMode) ?? LayoutModeOptions[1];
        GroupingModeText = FormatGroupingMode(appState.GroupingMode);
        SelectedGroupingModeOption = GroupingModeOptions.FirstOrDefault(option => option.Mode == appState.GroupingMode) ?? GroupingModeOptions[1];
        ReviewRequestStateText = FormatReviewRequestState(appState.ReviewRequestState);
        SelectedReviewRequestStateOption = ReviewRequestStateOptions.FirstOrDefault(option => option.State == appState.ReviewRequestState) ?? ReviewRequestStateOptions[0];
        IsSemanticWorkspaceModeSelected = appState.SemanticAnalysisMode == SemanticAnalysisMode.WorkspaceThenSyntax;
        IsSemanticFastModeSelected = appState.SemanticAnalysisMode == SemanticAnalysisMode.FastSyntaxOnly;
        ApplyAnnotationVisibilityToPresentation();
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

        BuildGitReferenceTree();
        ApplyReferenceSelectionsToPresentation();
    }

    private void ApplyReferenceSelectionsToPresentation()
    {
        isUpdatingReferenceSelection = true;
        try
        {
            SelectedPullRequestOption = appState.SelectedPullRequestNumber is null
                ? null
                : allPullRequestOptions.FirstOrDefault(option => option.Number == appState.SelectedPullRequestNumber.Value);
            SelectedBranchOption = appState.DiffScope == GitDiffScope.Branch && appState.SelectedPullRequestNumber is null
                ? allBranchOptions.FirstOrDefault(option => string.Equals(option.ReferenceName, appState.SelectedBranchRef ?? appState.HeadRef, StringComparison.Ordinal))
                : null;
            SelectedGitReferenceTreeItem = GitReferenceTreeItems.FirstOrDefault(item =>
                (item.Branch is not null && SelectedBranchOption is not null && string.Equals(item.Branch.ReferenceName, SelectedBranchOption.ReferenceName, StringComparison.Ordinal)) ||
                (item.PullRequest is not null && SelectedPullRequestOption is not null && item.PullRequest.Number == SelectedPullRequestOption.Number));
        }
        finally
        {
            isUpdatingReferenceSelection = false;
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
        IsReviewCommentVisualizationEnabled = visibility.ShowReviewComments;
        IsHistoryVisualizationEnabled = visibility.ShowHistory;
        IsNavigationVisualizationEnabled = visibility.ShowNavigation;
        IsContextVisualizationEnabled = visibility.ShowContext;
        VisualizationButtonText = $"Visuals {visibility.EnabledLayerCount}/8";
        VisualizationSummaryText = $"Visual layers {visibility.EnabledLayerCount}/8";
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
        ExplorerTreeItems = FileExplorerNodeViewModel.Flatten(
            tree,
            collapsedExplorerNodePaths,
            !string.IsNullOrWhiteSpace(query),
            currentRepositoryPath,
            IsLightThemeEnabled);
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
        currentSymbolInsight = new SemanticSymbolInsightIndex().Build(allSemanticNavigationItems);
        PruneSymbolFilters();
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
        ImpactSummaryText = $"Impact {FormatCount(summary.ChangedSymbolCount, "symbol", "symbols")} | {FormatCount(summary.ImpactedEdgeCount, "link", "links")} | {FormatCount(currentSymbolInsight.DocumentCount, "symbol file", "symbol files")}";
        ReviewSignalText = $"Moved {summary.MovedLineCount:N0} | Noise {summary.IgnoredLineCount:N0} | Conflicts {conflictSummary.ConflictRegionCount:N0}";
        return summary;
    }

    private static string FormatImpactStatus(SemanticImpactSummary summary) =>
        $"{summary.ChangedSymbolCount:N0} changed symbols, {summary.MovedLineCount:N0} moved lines, {summary.IgnoredLineCount:N0} noise lines";

    partial void OnSymbolSearchTextChanged(string value) => ApplySemanticNavigationFilter();

    public void SetSymbolScopeFilter(SymbolScopeFilterViewModel? filter)
    {
        selectedSymbolScopeFilter = string.IsNullOrWhiteSpace(filter?.FilterKey)
            ? SymbolScopeFilterViewModel.AllKey
            : filter.FilterKey;
        ApplySemanticNavigationFilter();
    }

    public void SetSymbolKindFilter(SemanticSymbolKindFacetViewModel? facet)
    {
        var nextFilter = string.IsNullOrWhiteSpace(facet?.KindKey)
            ? SymbolFilterAll
            : facet.KindKey;
        selectedSymbolKindFilter = string.Equals(selectedSymbolKindFilter, nextFilter, StringComparison.OrdinalIgnoreCase)
            ? SymbolFilterAll
            : nextFilter;
        ApplySemanticNavigationFilter();
    }

    public FocusRequest? SetSymbolDocumentFilter(SemanticSymbolDocumentFacetViewModel? facet)
    {
        var nextFilter = string.IsNullOrWhiteSpace(facet?.DocumentId)
            ? SymbolFilterAll
            : facet.DocumentId;
        selectedSymbolDocumentFilter = string.Equals(selectedSymbolDocumentFilter, nextFilter, StringComparison.Ordinal)
            ? SymbolFilterAll
            : nextFilter;
        ApplySemanticNavigationFilter();
        return FocusFirstSemanticResult();
    }

    public void ClearSymbolFilters()
    {
        SymbolSearchText = string.Empty;
        selectedSymbolScopeFilter = SymbolScopeFilterViewModel.AllKey;
        selectedSymbolKindFilter = SymbolFilterAll;
        selectedSymbolDocumentFilter = SymbolFilterAll;
        ApplySemanticNavigationFilter();
    }

    private void ApplySemanticNavigationFilter()
    {
        var query = SymbolSearchText.Trim();
        var filtered = allSemanticNavigationItems
            .Where(MatchesSelectedSymbolScope)
            .Where(MatchesSelectedSymbolKind)
            .Where(MatchesSelectedSymbolDocument)
            .Where(item => string.IsNullOrWhiteSpace(query) || item.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();

        SemanticItems = filtered
            .Select(SemanticNavigationItemViewModel.FromItem)
            .ToImmutableArray();
        SymbolCountText = !HasActiveSymbolFilters(query)
            ? FormatCount(allSemanticNavigationItems.Length, "symbol", "symbols")
            : $"{filtered.Length:N0}/{allSemanticNavigationItems.Length:N0} symbols";
        SymbolFilterStatusText = BuildSymbolFilterStatus(filtered.Length, query);
        RefreshSymbolInsightViewModels();
    }

    private bool MatchesSelectedSymbolScope(SemanticNavigationItem item) => selectedSymbolScopeFilter switch
    {
        SymbolScopeFilterViewModel.ChangedKey => item.IsChanged,
        SymbolScopeFilterViewModel.LinkedKey => item.IsLinked,
        _ => true
    };

    private bool MatchesSelectedSymbolKind(SemanticNavigationItem item) =>
        string.Equals(selectedSymbolKindFilter, SymbolFilterAll, StringComparison.Ordinal) ||
        string.Equals(item.KindText, selectedSymbolKindFilter, StringComparison.OrdinalIgnoreCase);

    private bool MatchesSelectedSymbolDocument(SemanticNavigationItem item) =>
        string.Equals(selectedSymbolDocumentFilter, SymbolFilterAll, StringComparison.Ordinal) ||
        string.Equals(item.DocumentId.Value, selectedSymbolDocumentFilter, StringComparison.Ordinal);

    private FocusRequest? FocusFirstSemanticResult()
    {
        var first = SemanticItems.FirstOrDefault();
        return first is null ? null : FocusSemanticItem(first);
    }

    private void PruneSymbolFilters()
    {
        if (!string.Equals(selectedSymbolKindFilter, SymbolFilterAll, StringComparison.Ordinal) &&
            !allSemanticNavigationItems.Any(item => string.Equals(item.KindText, selectedSymbolKindFilter, StringComparison.OrdinalIgnoreCase)))
        {
            selectedSymbolKindFilter = SymbolFilterAll;
        }

        if (!string.Equals(selectedSymbolDocumentFilter, SymbolFilterAll, StringComparison.Ordinal) &&
            !allSemanticNavigationItems.Any(item => string.Equals(item.DocumentId.Value, selectedSymbolDocumentFilter, StringComparison.Ordinal)))
        {
            selectedSymbolDocumentFilter = SymbolFilterAll;
        }
    }

    private void RefreshSymbolInsightViewModels()
    {
        SymbolInsightSummaryText = currentSymbolInsight.TotalSymbolCount == 0
            ? "No semantic symbols found for this diff"
            : $"{currentSymbolInsight.TotalSymbolCount:N0} symbols across {currentSymbolInsight.DocumentCount:N0} files | {currentSymbolInsight.ChangedSymbolCount:N0} changed | {currentSymbolInsight.LinkedSymbolCount:N0} linked";
        SymbolScopeFilters =
        [
            new SymbolScopeFilterViewModel(SymbolScopeFilterViewModel.AllKey, "All", currentSymbolInsight.TotalSymbolCount, string.Equals(selectedSymbolScopeFilter, SymbolScopeFilterViewModel.AllKey, StringComparison.Ordinal)),
            new SymbolScopeFilterViewModel(SymbolScopeFilterViewModel.ChangedKey, "Changed", currentSymbolInsight.ChangedSymbolCount, string.Equals(selectedSymbolScopeFilter, SymbolScopeFilterViewModel.ChangedKey, StringComparison.Ordinal)),
            new SymbolScopeFilterViewModel(SymbolScopeFilterViewModel.LinkedKey, "Linked", currentSymbolInsight.LinkedSymbolCount, string.Equals(selectedSymbolScopeFilter, SymbolScopeFilterViewModel.LinkedKey, StringComparison.Ordinal))
        ];
        SymbolKindFacets = currentSymbolInsight.KindFacets
            .Select(facet => SemanticSymbolKindFacetViewModel.FromFacet(
                facet,
                string.Equals(selectedSymbolKindFilter, facet.KindText, StringComparison.OrdinalIgnoreCase)))
            .ToImmutableArray();
        SymbolDocumentFacets = currentSymbolInsight.DocumentFacets
            .Select(facet => SemanticSymbolDocumentFacetViewModel.FromFacet(
                facet,
                string.Equals(selectedSymbolDocumentFilter, facet.DocumentId.Value, StringComparison.Ordinal)))
            .ToImmutableArray();
        HotSemanticItems = currentSymbolInsight.HotSymbols
            .Take(4)
            .Select(SemanticNavigationItemViewModel.FromItem)
            .ToImmutableArray();
    }

    private bool HasActiveSymbolFilters(string query) =>
        !string.IsNullOrWhiteSpace(query) ||
        !string.Equals(selectedSymbolScopeFilter, SymbolScopeFilterViewModel.AllKey, StringComparison.Ordinal) ||
        !string.Equals(selectedSymbolKindFilter, SymbolFilterAll, StringComparison.Ordinal) ||
        !string.Equals(selectedSymbolDocumentFilter, SymbolFilterAll, StringComparison.Ordinal);

    private string BuildSymbolFilterStatus(int filteredCount, string query)
    {
        var parts = new List<string>();
        if (!string.Equals(selectedSymbolScopeFilter, SymbolScopeFilterViewModel.AllKey, StringComparison.Ordinal))
        {
            parts.Add(selectedSymbolScopeFilter);
        }

        if (!string.Equals(selectedSymbolKindFilter, SymbolFilterAll, StringComparison.Ordinal))
        {
            parts.Add(selectedSymbolKindFilter);
        }

        if (!string.Equals(selectedSymbolDocumentFilter, SymbolFilterAll, StringComparison.Ordinal))
        {
            var documentFacet = currentSymbolInsight.DocumentFacets.FirstOrDefault(facet => string.Equals(facet.DocumentId.Value, selectedSymbolDocumentFilter, StringComparison.Ordinal));
            parts.Add(string.IsNullOrWhiteSpace(documentFacet?.Path) ? selectedSymbolDocumentFilter : ShortenPath(documentFacet.Path));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            parts.Add($"search \"{query}\"");
        }

        return parts.Count == 0
            ? "Showing all semantic symbols"
            : $"Showing {filteredCount:N0} symbols filtered by {string.Join(", ", parts)}";
    }

    private static string FormatCount(int count, string singular, string plural) => $"{count:N0} {(count == 1 ? singular : plural)}";

    private static string FormatThreadCount(int count) => FormatCount(count, "thread", "threads");

    public void SelectGraphWorkspaceTab()
    {
        SelectedWorkspaceTab = WorkspaceTabs.FirstOrDefault(tab => tab.Kind == WorkspaceTabKind.Graph) ?? WorkspaceTabs.FirstOrDefault();
    }

    public void CloseWorkspaceTab(WorkspaceTabViewModel? tab)
    {
        if (tab is null || !tab.IsClosable)
        {
            return;
        }

        var selectedIndex = WorkspaceTabs.IndexOf(tab);
        WorkspaceTabs.Remove(tab);
        if (ReferenceEquals(SelectedWorkspaceTab, tab))
        {
            SelectedWorkspaceTab = WorkspaceTabs.Count == 0
                ? null
                : WorkspaceTabs[Math.Clamp(selectedIndex - 1, 0, WorkspaceTabs.Count - 1)];
        }
    }

    public void SetFileDiffDisplayMode(WorkspaceTabViewModel? tab, FileDiffDisplayMode displayMode)
    {
        tab?.FileDiff?.SetDisplayMode(displayMode);
    }

    public void ToggleBlameTimeline(WorkspaceTabViewModel? tab)
    {
        tab?.Blame?.ToggleTimeline();
    }

    public void ReportGitHistoryCommitHashCopied(GitHistoryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        AddDiagnostic("Info", $"Copied commit hash {item.ShortId}");
    }

    public void SetComparisonRangeStart(GitHistoryItemViewModel? item) => SetComparisonRangeEndpoint(item, isStart: true);

    public void SetComparisonRangeEnd(GitHistoryItemViewModel? item) => SetComparisonRangeEndpoint(item, isStart: false);

    private void SetComparisonRangeEndpoint(GitHistoryItemViewModel? item, bool isStart)
    {
        if (item is null)
        {
            return;
        }

        if (isStart)
        {
            BaseRefText = item.CommitId;
        }
        else
        {
            HeadRefText = item.CommitId;
        }

        DiffScopeText = GitDiffScope.CommitRange.ToString();
        IsWorktreeScopeSelected = false;
        IsUnstagedScopeSelected = false;
        IsStagedScopeSelected = false;
        IsRangeScopeSelected = true;
        IsBranchScopeSelected = false;
        AddDiagnostic("Info", $"Set range {(isStart ? "start" : "end")} to {item.ShortId}; apply refs to compare");
    }

    private void CaptureGraphWorkspaceState(WorkspaceTabViewModel? tab)
    {
        if (tab?.Kind != WorkspaceTabKind.Graph)
        {
            return;
        }

        CaptureLayoutState(Scene);
        if (currentGitSnapshot?.Request is { } activeRequest)
        {
            tab.GraphRequest = activeRequest;
        }

        tab.GraphBranchReferenceName = SelectedBranchOption?.ReferenceName ?? tab.GraphBranchReferenceName;
        tab.GraphReviewRequest = SelectedPullRequestOption?.ToPullRequestInfo() ?? tab.GraphReviewRequest;
        tab.StatusText = StatusText;
        tab.GraphState = new GraphWorkspaceState(
            currentRepositoryPath,
            tab.GraphRequest ?? currentGitSnapshot?.Request,
            tab.GraphReviewRequest,
            RepositoryName,
            RepositoryContextText,
            StatusText,
            currentStatusPrefix,
            currentDocumentsAreRepositoryDocuments,
            currentDocuments,
            currentSemanticGraph,
            currentGitSnapshot,
            previousLayout,
            pinnedDocumentIds,
            Scene,
            selectedExplorerItem?.DocumentId,
            allReviewThreadItems,
            currentReviewThreads,
            currentReviewRequestKind);
    }

    private void RestoreGraphWorkspaceState(WorkspaceTabViewModel tab)
    {
        if (tab.GraphState is not { } state)
        {
            return;
        }

        currentRepositoryPath = state.RepositoryPath;
        currentGitSnapshot = state.GitSnapshot;
        currentStatusPrefix = state.StatusPrefix;
        currentDocumentsAreRepositoryDocuments = state.DocumentsAreRepositoryDocuments;
        currentDocuments = state.Documents;
        currentSemanticGraph = state.SemanticGraph;
        previousLayout = state.PreviousLayout;
        pinnedDocumentIds = state.PinnedDocumentIds;
        allReviewThreadItems = state.ReviewThreadItems;
        currentReviewThreads = state.ReviewThreads;
        currentReviewRequestKind = state.ReviewRequestKind;

        ApplyGraphWorkspaceReferenceState(tab, state);
        Scene = state.Scene.WithAnnotations(CreateAnnotations(state.Documents, state.SemanticGraph), appState.EffectiveAnnotationVisibility);
        UpdateChangeNavigation(state.Documents);
        SetExplorerItems(state.Documents.Select(document => new ExplorerItemViewModel(document.Metadata.Path, document.Metadata.Status, document.Metadata.Language)).ToImmutableArray());
        RestoreSelectedExplorerItem(state.SelectedDocumentId);
        UpdateSemanticNavigation(state.SemanticGraph, state.Documents);
        var impactSummary = UpdateImpactSummary(state.Documents, state.SemanticGraph);
        UpdateWorkspaceSummary(state.RepositoryName, state.ContextText, state.Documents.Length, state.SemanticGraph.Edges.Length);
        StatusText = string.IsNullOrWhiteSpace(state.StatusText)
            ? $"{state.StatusPrefix} | {state.Documents.Length} nodes | {state.SemanticGraph.Edges.Length} semantic edges | {FormatImpactStatus(impactSummary)}"
            : state.StatusText;
        ApplyReviewThreadFilter();
        ReviewPanelStatusText = state.ReviewRequest is null
            ? "Select a PR or MR"
            : $"{FormatReviewRequestLabel(state.ReviewRequest)} | {FormatThreadCount(allReviewThreadItems.Length)}";
        UpdateDiffViewCacheText();
    }

    private void ApplyGraphWorkspaceReferenceState(WorkspaceTabViewModel tab, GraphWorkspaceState state)
    {
        var request = tab.GraphRequest ?? state.Request;
        if (request is null)
        {
            return;
        }

        var reviewRequest = tab.GraphReviewRequest ?? state.ReviewRequest;
        appState = appState with
        {
            DiffScope = request.Scope,
            BaseRef = NormalizeRef(request.BaseRef),
            HeadRef = NormalizeRef(request.HeadRef),
            SelectedBranchRef = reviewRequest is null ? tab.GraphBranchReferenceName : null,
            SelectedPullRequestNumber = reviewRequest?.Number
        };
        ApplyAppStateToPresentation();
    }

    private void AddWorkspaceTab(WorkspaceTabViewModel tab)
    {
        WorkspaceTabs.Add(tab);
        SelectedWorkspaceTab = tab;
    }

    private bool SelectWorkspaceTab(string id)
    {
        var tab = FindWorkspaceTab(id);
        if (tab is null)
        {
            return false;
        }

        SelectedWorkspaceTab = tab;
        return true;
    }

    private WorkspaceTabViewModel? FindWorkspaceTab(string id) =>
        WorkspaceTabs.FirstOrDefault(tab => string.Equals(tab.Id, id, StringComparison.Ordinal));

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
        return request.Scope switch
        {
            GitDiffScope.Branch when string.Equals(headRef, "HEAD", StringComparison.Ordinal) => $"range {baseRef}...HEAD + worktree",
            GitDiffScope.Branch => $"range {baseRef}...{headRef}",
            GitDiffScope.CommitRange or GitDiffScope.Custom => $"range {baseRef}..{headRef}",
            _ => $"base {defaultBranch ?? "unknown"}"
        };
    }

    private static string FormatReviewMode(DiffReviewMode reviewMode) => reviewMode switch
    {
        DiffReviewMode.IgnoreWhitespace => "Noise filter",
        _ => "Precise"
    };

    private static string FormatReviewRequestState(GitReviewRequestState state) => state switch
    {
        GitReviewRequestState.Closed => "Closed",
        GitReviewRequestState.Merged => "Merged",
        GitReviewRequestState.All => "All",
        _ => "Open"
    };

    private static string FormatReviewRequestLabel(GitPullRequestInfo request) =>
        request.Kind == GitReviewRequestKind.MergeRequest
            ? $"MR !{request.Number}"
            : $"PR #{request.Number}";

    private static string FormatSemanticAnalysisMode(SemanticAnalysisMode analysisMode) => analysisMode switch
    {
        SemanticAnalysisMode.FastSyntaxOnly => "Fast syntax",
        _ => "MSBuild"
    };

    private static string FormatLayoutMode(GraphLayoutMode layoutMode) => layoutMode switch
    {
        GraphLayoutMode.Layered => "Layered",
        GraphLayoutMode.Grid => "Grid",
        GraphLayoutMode.CompactGrid => "Compact grid",
        GraphLayoutMode.StatusLanes => "Status lanes",
        _ => "Auto"
    };

    private static string FormatGroupingMode(GraphGroupingMode groupingMode) => groupingMode switch
    {
        GraphGroupingMode.None => "None",
        GraphGroupingMode.Semantic => "Semantic",
        GraphGroupingMode.Language => "Language",
        GraphGroupingMode.Status => "Status",
        _ => "Folders"
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
            var blame = await gitBlameService.GetFileBlameAsync(currentRepositoryPath, path, blameOperation.Token, GetActiveBlameRevision()).ConfigureAwait(false);
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
        var watcher = repositoryFileWatcher;
        if (watcher is null)
        {
            return;
        }

        watcher.Changed -= OnRepositoryFileChanged;
        await watcher.DisposeAsync();
        if (ReferenceEquals(repositoryFileWatcher, watcher))
        {
            repositoryFileWatcher = null;
        }
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

    private static string NormalizeRepositoryPath(string path) => path.Replace('\\', '/').Trim('/');

    private async Task<string> LoadFullFileTextAsync(DiffDocumentSnapshot document, CancellationToken cancellationToken)
    {
        if (currentGitSnapshot is null || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return document.ToSourceText();
        }

        var fileChange = currentGitSnapshot.Files.FirstOrDefault(file =>
            string.Equals(NormalizeRepositoryPath(file.Path), NormalizeRepositoryPath(document.Metadata.Path), StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(file.OldPath) &&
                string.Equals(NormalizeRepositoryPath(file.OldPath), NormalizeRepositoryPath(document.Metadata.Path), StringComparison.OrdinalIgnoreCase)));
        if (fileChange is null)
        {
            return document.ToSourceText();
        }

        try
        {
            var content = await new GitDiffService().GetFileContentAsync(currentGitSnapshot.Request, fileChange, cancellationToken);
            return string.IsNullOrEmpty(content) ? document.ToSourceText() : content;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AddDiagnostic("Warning", $"Full file load failed: {exception.Message}");
            return document.ToSourceText();
        }
    }

    private static async Task<DiffDocumentSnapshot> CreateTokenizedFullFileDocumentAsync(
        DiffDocumentSnapshot sourceDocument,
        string fullText,
        CancellationToken cancellationToken)
    {
        const int tokenPageSize = 128;
        var metadata = sourceDocument.Metadata with
        {
            AddedLines = 0,
            DeletedLines = 0
        };
        var document = new DiffDocumentFactory().CreateFromText(metadata, fullText, DiffLineKind.Context);
        var tokenizer = new TextMateDocumentTokenizer(tokenPageSize);
        var lineBuilder = ImmutableArray.CreateBuilder<DiffLine>(document.LineCount);

        for (var firstLineIndex = 0; firstLineIndex < document.LineCount; firstLineIndex += tokenPageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokenizedLines = await tokenizer.TokenizePageAsync(document, firstLineIndex, tokenPageSize, cancellationToken).ConfigureAwait(false);
            lineBuilder.AddRange(tokenizedLines);
        }

        return document with { Lines = lineBuilder.ToImmutable() };
    }

    private DiffCanvasScene CreateScene(ImmutableArray<DiffDocumentSnapshot> documents, SemanticGraph semanticGraph, GraphLayoutResult? layout) =>
        DiffCanvasScene.FromDocuments(
            documents,
            semanticGraph,
            layout,
            CreateEdgeOptions(),
            CreateAnnotations(documents, semanticGraph),
            appState.EffectiveAnnotationVisibility,
            appState.GroupingMode);

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
        var request = new DiffAnnotationRequest(documents, semanticGraph, context, currentReviewThreads);
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
        currentSemanticRefinementOperation?.Cancel();
        var operation = new CancellationTokenSource();
        currentOperation = operation;
        IsBusy = true;
        ProgressValue = 0;
        ProgressText = message;
        AddDiagnostic("Info", message);
        return operation;
    }

    private CancellationTokenSource BeginSemanticRefinementOperation()
    {
        var operation = new CancellationTokenSource();
        var previousOperation = Interlocked.Exchange(ref currentSemanticRefinementOperation, operation);
        previousOperation?.Cancel();
        return operation;
    }

    private bool IsCurrentOperation(CancellationTokenSource operation) => ReferenceEquals(currentOperation, operation);

    private bool IsCurrentRepositoryRequest(long repositoryRequestId) => repositoryLoadRequests.IsCurrent(repositoryRequestId);

    private void EnsureCurrentRepositoryRequest(long repositoryRequestId, CancellationToken cancellationToken) =>
        repositoryLoadRequests.ThrowIfStale(repositoryRequestId, cancellationToken);

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

public sealed record LayoutModeOptionViewModel(GraphLayoutMode Mode, string DisplayName)
{
    public static ImmutableArray<LayoutModeOptionViewModel> All { get; } =
    [
        new(GraphLayoutMode.Auto, "Auto"),
        new(GraphLayoutMode.Layered, "Layered"),
        new(GraphLayoutMode.Grid, "Grid"),
        new(GraphLayoutMode.CompactGrid, "Compact grid"),
        new(GraphLayoutMode.StatusLanes, "Status lanes")
    ];

    public override string ToString() => DisplayName;
}

public sealed record GroupingModeOptionViewModel(GraphGroupingMode Mode, string DisplayName)
{
    public static ImmutableArray<GroupingModeOptionViewModel> All { get; } =
    [
        new(GraphGroupingMode.None, "None"),
        new(GraphGroupingMode.Folder, "Folders"),
        new(GraphGroupingMode.Semantic, "Semantic"),
        new(GraphGroupingMode.Language, "Language"),
        new(GraphGroupingMode.Status, "Status")
    ];

    public override string ToString() => DisplayName;
}

public sealed record ReviewRequestStateOptionViewModel(GitReviewRequestState State, string DisplayName, string Description)
{
    public static ImmutableArray<ReviewRequestStateOptionViewModel> All { get; } =
    [
        new(GitReviewRequestState.Open, "Open", "Open PRs/MRs"),
        new(GitReviewRequestState.Closed, "Closed", "Closed without merge"),
        new(GitReviewRequestState.Merged, "Merged", "Merged review requests"),
        new(GitReviewRequestState.All, "All", "Open, closed, and merged")
    ];

    public override string ToString() => DisplayName;
}

public sealed record SemanticNavigationItemViewModel(
    string AnchorId,
    string DocumentId,
    string Path,
    string KindText,
    string DisplayName,
    int Line,
    int IncidentEdgeCount,
    bool IsChanged,
    bool IsLinked)
{
    public string LocationText => $"{Path}:{Line}";

    public string EdgeText => IncidentEdgeCount == 1 ? "1 link" : $"{IncidentEdgeCount:N0} links";

    public string SignalText => (IsChanged, IsLinked) switch
    {
        (true, true) => "changed + linked",
        (true, false) => "changed",
        (false, true) => "linked",
        _ => string.Empty
    };

    public Visibility SignalVisibility => string.IsNullOrWhiteSpace(SignalText) ? Visibility.Collapsed : Visibility.Visible;

    public static SemanticNavigationItemViewModel FromItem(SemanticNavigationItem item) => new(
        item.AnchorId,
        item.DocumentId.Value,
        item.Path,
        item.KindText,
        item.DisplayName,
        item.Line,
        item.IncidentEdgeCount,
        item.IsChanged,
        item.IsLinked);
}

public sealed record SymbolScopeFilterViewModel(string FilterKey, string DisplayName, int Count, bool IsSelected)
{
    public const string AllKey = "All";
    public const string ChangedKey = "Changed";
    public const string LinkedKey = "Linked";

    public string DisplayText => $"{DisplayName} {Count:N0}";

    public SolidColorBrush Background => IsSelected ? SymbolInsightBrushes.SelectedBackground : SymbolInsightBrushes.Transparent;

    public SolidColorBrush Border => IsSelected ? SymbolInsightBrushes.Accent : SymbolInsightBrushes.SubtleBorder;

    public SolidColorBrush Foreground => IsSelected ? SymbolInsightBrushes.SelectedForeground : SymbolInsightBrushes.Secondary;
}

public sealed record SemanticSymbolKindFacetViewModel(
    string KindKey,
    string KindText,
    int Count,
    int ChangedCount,
    int LinkedCount,
    bool IsSelected)
{
    public string DisplayText => $"{KindText} {Count:N0}";

    public string DetailText => $"{ChangedCount:N0} changed | {LinkedCount:N0} linked";

    public SolidColorBrush Background => IsSelected ? SymbolInsightBrushes.SelectedBackground : SymbolInsightBrushes.Transparent;

    public SolidColorBrush Border => IsSelected ? SymbolInsightBrushes.Accent : SymbolInsightBrushes.SubtleBorder;

    public SolidColorBrush Foreground => IsSelected ? SymbolInsightBrushes.SelectedForeground : SymbolInsightBrushes.Primary;

    public static SemanticSymbolKindFacetViewModel FromFacet(SemanticSymbolKindFacet facet, bool isSelected) => new(
        facet.KindText,
        facet.KindText,
        facet.Count,
        facet.ChangedCount,
        facet.LinkedCount,
        isSelected);
}

public sealed record SemanticSymbolDocumentFacetViewModel(
    string DocumentId,
    string Path,
    string FileName,
    int Count,
    int ChangedCount,
    int LinkedCount,
    bool IsSelected)
{
    public string DetailText => $"{Count:N0} symbols | {ChangedCount:N0} changed | {LinkedCount:N0} linked";

    public SolidColorBrush Background => IsSelected ? SymbolInsightBrushes.SelectedBackground : SymbolInsightBrushes.Transparent;

    public SolidColorBrush Border => IsSelected ? SymbolInsightBrushes.Accent : SymbolInsightBrushes.SubtleBorder;

    public SolidColorBrush Foreground => IsSelected ? SymbolInsightBrushes.SelectedForeground : SymbolInsightBrushes.Primary;

    public static SemanticSymbolDocumentFacetViewModel FromFacet(SemanticSymbolDocumentFacet facet, bool isSelected)
    {
        var fileName = System.IO.Path.GetFileName(facet.Path);
        return new SemanticSymbolDocumentFacetViewModel(
            facet.DocumentId.Value,
            facet.Path,
            string.IsNullOrWhiteSpace(fileName) ? facet.Path : fileName,
            facet.Count,
            facet.ChangedCount,
            facet.LinkedCount,
            isSelected);
    }
}

internal static class SymbolInsightBrushes
{
    public static SolidColorBrush Transparent { get; } = new(Color.FromArgb(0, 0, 0, 0));

    public static SolidColorBrush SelectedBackground { get; } = new(Color.FromArgb(38, 0, 122, 204));

    public static SolidColorBrush Accent { get; } = new(Color.FromArgb(255, 0, 122, 204));

    public static SolidColorBrush SubtleBorder { get; } = new(Color.FromArgb(255, 154, 166, 180));

    public static SolidColorBrush SelectedForeground { get; } = new(Color.FromArgb(255, 0, 92, 150));

    public static SolidColorBrush Primary { get; } = new(Color.FromArgb(255, 20, 32, 51));

    public static SolidColorBrush Secondary { get; } = new(Color.FromArgb(255, 82, 97, 114));
}

public sealed record FocusRequest(string DocumentId, int? Line);

public sealed record DiagnosticItemViewModel(string TimeText, string Level, string Message)
{
    public string DisplayText => $"{TimeText}  {Level}  {Message}";
}
