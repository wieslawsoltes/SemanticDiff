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
using SemanticDiff.Workbench.Review;
using SemanticDiff.Workbench.Query;
using SemanticDiff.Workbench.Symbols;
using SemanticDiff.Workbench.Workspace;
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
    private readonly DiffWorkspaceCache diffViewCache = new(MaxCachedDiffViews);
    private readonly RepositoryDiffLoader repositoryDiffLoader = new();
    private readonly SymbolBrowserModel symbolBrowser = new();
    private readonly DocumentCodeCompletionProvider documentCompletionProvider = new();
    private readonly RoslynCSharpCodeCompletionProvider roslynCompletionProvider = new();
    private readonly MSBuildWorkspaceFileDiscoveryService workspaceFileDiscoveryService = new();
    private readonly QueryCanvasEngine queryCanvasEngine = new();
    private readonly QueryCanvasCompletionProvider queryCanvasCompletionProvider = new();
    private readonly Dictionary<string, CancellationTokenSource> queryCanvasOperations = new(StringComparer.Ordinal);
    private readonly GitReferenceBrowserModel<GitBranchOptionViewModel, GitPullRequestOptionViewModel> gitReferenceBrowser = new(
        branch => branch.SearchText,
        reviewRequest => reviewRequest.SearchText);
    private readonly ReviewWorkflowModel<ReviewThreadItemViewModel> reviewWorkflow = new(item => item.Id, item => item.SearchText);
    private readonly WorkspaceDocumentManager<WorkspaceTabViewModel> workspaceDocumentManager;
    private readonly SynchronizationContext? synchronizationContext;
    private const int MaxCachedDiffViews = 12;
    private const int GitHistoryPageSize = 200;
    private GraphLayoutResult? previousLayout;
    private ImmutableHashSet<DiffDocumentId> pinnedDocumentIds = ImmutableHashSet<DiffDocumentId>.Empty;
    private ImmutableArray<DiffDocumentSnapshot> currentDocuments = [];
    private SemanticGraph currentSemanticGraph = SemanticGraph.Empty;
    private ImmutableArray<ExplorerItemViewModel> allExplorerItems = [];
    private ImmutableArray<ExplorerItemViewModel> diffExplorerItems = [];
    private ImmutableArray<ExplorerItemViewModel> workspaceExplorerItems = [];
    private ImmutableHashSet<string> collapsedExplorerNodePaths = ImmutableHashSet<string>.Empty;
    private ImmutableArray<SemanticNavigationItem> allSemanticNavigationItems => symbolBrowser.AllItems;
    private SemanticSymbolInsightSummary currentSymbolInsight => symbolBrowser.Insight;
    private ImmutableDictionary<DiffDocumentId, SemanticDocumentInsight> currentSemanticDocumentInsights = ImmutableDictionary<DiffDocumentId, SemanticDocumentInsight>.Empty;
    private ImmutableArray<DiffChangeNavigationItem> changeNavigationItems = [];
    private int currentChangeNavigationIndex = -1;
    private string currentStatusPrefix = "sample fallback";
    private string? currentRepositoryPath;
    private GitDiffSnapshot? currentGitSnapshot;
    private SemanticDiffAppState appState = new();
    private CancellationTokenSource? currentOperation;
    private CancellationTokenSource? currentSemanticRefinementOperation;
    private CancellationTokenSource? currentBlameOperation;
    private CancellationTokenSource? currentWorkspaceExplorerOperation;
    private CancellationTokenSource? pendingAutoReload;
    private IRepositoryFileWatcher? repositoryFileWatcher;
    private ExplorerItemViewModel? selectedExplorerItem;
    private bool currentDocumentsAreRepositoryDocuments;
    private bool isUpdatingReferenceSelection;
    private string? workspaceExplorerRepositoryPath;
    private string? workspaceExplorerWorkspacePath;

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
        workspaceDocumentManager = new WorkspaceDocumentManager<WorkspaceTabViewModel>(WorkspaceTabs, tab => tab.Id, tab => tab.IsClosable);
        CodeCompletionProvider = CreateCodeCompletionProvider(appState.CodeCompletionMode);
        WorkspaceTabs.Add(WorkspaceTabViewModel.Graph());
        SelectedWorkspaceTab = WorkspaceTabs[0];
        InitializeSampleDocuments(SampleDiffDocuments.Create());
        _ = LoadRepositoryAsync(loadAppState: true, operationMessage: "Loading repository");
    }

    public ObservableCollection<WorkspaceTabViewModel> WorkspaceTabs { get; } = [];

    [ObservableProperty]
    private DiffCanvasScene scene = DiffCanvasScene.FromDocuments([]);

    [ObservableProperty]
    private bool isFullCodeWorkspaceEnabled;

    [ObservableProperty]
    private bool isNodeEditingWorkspaceEnabled;

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
    private bool useInteractiveLevelOfDetail = true;

    [ObservableProperty]
    private string semanticAnalysisModeText = "MSBuild";

    [ObservableProperty]
    private ICodeCompletionProvider codeCompletionProvider = new DocumentCodeCompletionProvider();

    [ObservableProperty]
    private string codeCompletionModeText = "Language services";

    [ObservableProperty]
    private bool isCompletionLanguageServicesModeSelected = true;

    [ObservableProperty]
    private bool isCompletionDocumentModeSelected;

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
    private FileExplorerMode fileExplorerMode = FileExplorerMode.Diff;

    [ObservableProperty]
    private bool isDiffFileExplorerModeSelected = true;

    [ObservableProperty]
    private bool isWorkspaceFileExplorerModeSelected;

    [ObservableProperty]
    private string fileExplorerTitleText = "Changed Files";

    [ObservableProperty]
    private string fileExplorerSearchPlaceholderText = "Find changed file";

    [ObservableProperty]
    private string fileExplorerModeStatusText = "Diff files";

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


}
