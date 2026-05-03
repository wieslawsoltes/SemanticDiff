using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Rendering;
using SemanticDiff.Semantics;
using SemanticDiff.Workbench.Blame;
using SemanticDiff.Workbench.FileDiff;
using SemanticDiff.Workbench.History;
using SemanticDiff.Workbench.Query;
using SemanticDiff.Workbench.Symbols;
using Windows.Foundation;
using Windows.UI;

namespace SemanticDiff.App.ViewModels;

public enum WorkspaceTabKind
{
    Graph,
    GitHistory,
    FileDiff,
    Blame,
    SymbolGraph,
    EditorCanvas,
    QueryCanvas,
    PatchCompare
}

public enum FileDiffDisplayMode
{
    DiffOnly,
    FullFile
}

public enum FileDiffScopeMode
{
    Changes,
    FullFileDiff
}

public enum BlameDisplayMode
{
    CommitTimeline,
    ChangeGraph
}

public sealed partial class WorkspaceTabViewModel : ObservableObject
{
    private WorkspaceTabViewModel(
        string id,
        WorkspaceTabKind kind,
        string header,
        string detailText,
        string iconGlyph,
        bool isClosable)
    {
        Id = id;
        Kind = kind;
        Header = header;
        DetailText = detailText;
        IconGlyph = iconGlyph;
        IsClosable = isClosable;
    }

    public string Id { get; }

    public WorkspaceTabKind Kind { get; }

    public string Header { get; }

    public string DetailText { get; }

    public string IconGlyph { get; }

    public bool IsClosable { get; }

    public Visibility IconVisibility => Kind is WorkspaceTabKind.Graph or WorkspaceTabKind.GitHistory or WorkspaceTabKind.Blame or WorkspaceTabKind.SymbolGraph or WorkspaceTabKind.EditorCanvas or WorkspaceTabKind.QueryCanvas or WorkspaceTabKind.PatchCompare
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CloseButtonVisibility => IsClosable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GraphVisibility => Kind == WorkspaceTabKind.Graph ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HistoryVisibility => Kind == WorkspaceTabKind.GitHistory ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FileDiffVisibility => Kind == WorkspaceTabKind.FileDiff ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BlameVisibility => Kind == WorkspaceTabKind.Blame ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SymbolGraphVisibility => Kind == WorkspaceTabKind.SymbolGraph ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EditorCanvasVisibility => Kind == WorkspaceTabKind.EditorCanvas ? Visibility.Visible : Visibility.Collapsed;

    public Visibility QueryCanvasVisibility => Kind == WorkspaceTabKind.QueryCanvas ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PatchCompareVisibility => Kind == WorkspaceTabKind.PatchCompare ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;

    public string LoadingProgressText => IsLoadingIndeterminate ? StatusText : $"{LoadingProgress:P0} {StatusText}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingVisibility))]
    private bool isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingProgressText))]
    private string statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingProgressText))]
    private double loadingProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingProgressText))]
    private bool isLoadingIndeterminate = true;

    [ObservableProperty]
    private GitHistoryTimelineViewModel? history;

    [ObservableProperty]
    private FileDiffTabViewModel? fileDiff;

    [ObservableProperty]
    private BlameTabViewModel? blame;

    [ObservableProperty]
    private SymbolGraphTabViewModel? symbolGraph;

    [ObservableProperty]
    private EditorCanvasTabViewModel? editorCanvas;

    [ObservableProperty]
    private QueryCanvasTabViewModel? queryCanvas;

    [ObservableProperty]
    private PatchCompareTabViewModel? patchCompare;

    [ObservableProperty]
    private GitDiffRequest? graphRequest;

    [ObservableProperty]
    private string? graphBranchReferenceName;

    [ObservableProperty]
    private GitPullRequestInfo? graphReviewRequest;

    [ObservableProperty]
    private GraphWorkspaceState? graphState;

    public GraphWorkspaceState? GraphUnfilteredState { get; set; }

    public string? GraphFileSubsetLabel { get; set; }

    public bool HasGraphFileSubsetFilter => GraphUnfilteredState is not null;

    [ObservableProperty]
    private bool isLightTheme;

    [ObservableProperty]
    private bool useInteractiveLevelOfDetail = true;

    public static WorkspaceTabViewModel Graph() => new(
        "graph",
        WorkspaceTabKind.Graph,
        "Diff Graph",
        "Semantic node canvas",
        "\uECA5",
        isClosable: false);

    public static WorkspaceTabViewModel CreateGraphWorkspace(
        string id,
        string header,
        string detailText,
        GitDiffRequest request,
        string? branchReferenceName,
        GitPullRequestInfo? reviewRequest) => new(
            id,
            WorkspaceTabKind.Graph,
            header,
            detailText,
            "\uECA5",
            isClosable: true)
        {
            GraphRequest = request,
            GraphBranchReferenceName = branchReferenceName,
            GraphReviewRequest = reviewRequest,
            StatusText = "Loading workspace"
        };

    public static WorkspaceTabViewModel CreateHistory(string id, string header, string detailText) => new(
        id,
        WorkspaceTabKind.GitHistory,
        header,
        detailText,
        "\uE81C",
        isClosable: true);

    public static WorkspaceTabViewModel CreateFileDiff(string id, string header, string detailText, FileDiffTabViewModel fileDiff) => new(
        id,
        WorkspaceTabKind.FileDiff,
        header,
        detailText,
        "\uE8A5",
        isClosable: true)
    {
        FileDiff = fileDiff,
        StatusText = fileDiff.StatusText
    };

    public static WorkspaceTabViewModel CreateBlame(string id, string header, string detailText, BlameTabViewModel blame) => new(
        id,
        WorkspaceTabKind.Blame,
        header,
        detailText,
        "\uE946",
        isClosable: true)
    {
        Blame = blame,
        StatusText = blame.StatusText
    };

    public static WorkspaceTabViewModel CreateSymbolGraph(string id, string header, string detailText, SymbolGraphTabViewModel symbolGraph) => new(
        id,
        WorkspaceTabKind.SymbolGraph,
        header,
        detailText,
        "\uE8D2",
        isClosable: true)
    {
        SymbolGraph = symbolGraph,
        StatusText = symbolGraph.StatusText
    };

    public static WorkspaceTabViewModel CreateEditorCanvas(string id, string header, string detailText, EditorCanvasTabViewModel editorCanvas) => new(
        id,
        WorkspaceTabKind.EditorCanvas,
        header,
        detailText,
        "\uE70F",
        isClosable: true)
    {
        EditorCanvas = editorCanvas,
        StatusText = editorCanvas.StatusText
    };

    public static WorkspaceTabViewModel CreateQueryCanvas(string id, string header, string detailText, QueryCanvasTabViewModel queryCanvas) => new(
        id,
        WorkspaceTabKind.QueryCanvas,
        header,
        detailText,
        "\uE946",
        isClosable: true)
    {
        QueryCanvas = queryCanvas,
        StatusText = queryCanvas.StatusText
    };

    public static WorkspaceTabViewModel CreatePatchCompare(string id, string header, string detailText, PatchCompareTabViewModel patchCompare) => new(
        id,
        WorkspaceTabKind.PatchCompare,
        header,
        detailText,
        "\uE8EF",
        isClosable: true)
    {
        PatchCompare = patchCompare,
        StatusText = patchCompare.StatusText
    };
}

public sealed partial class PatchCompareTabViewModel : ObservableObject
{
    private bool suppressRangeChangeNotifications;

    public PatchCompareTabViewModel(string title, string description)
    {
        Title = title;
        Description = description;
        Reset();
    }

    public string Title { get; }

    public string Description { get; }

    public Visibility EmptyVisibility => Snapshot is null && !HasError ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ContentVisibility => Snapshot is not null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ErrorVisibility => HasError ? Visibility.Visible : Visibility.Collapsed;

    public string RangeHintText => "Use any Git range accepted by git log/range-diff, for example upstream/main..feature, tagA..tagB, sha1..sha2, or chrome/m119..119.";

    public Visibility WizardRefsVisibility => FilteredWizardRefs.IsDefaultOrEmpty ? Visibility.Collapsed : Visibility.Visible;

    public Visibility WizardEmptyVisibility => FilteredWizardRefs.IsDefaultOrEmpty ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WizardRepositoryVisibility => string.IsNullOrWhiteSpace(ComparisonRepositoryPath) ? Visibility.Collapsed : Visibility.Visible;

    [ObservableProperty]
    private string oldRangeText = string.Empty;

    [ObservableProperty]
    private string newRangeText = string.Empty;

    [ObservableProperty]
    private string wizardRepositoryText = string.Empty;

    [ObservableProperty]
    private string wizardFilterText = string.Empty;

    [ObservableProperty]
    private string wizardStatusText = "Enter a local path, remote Git URL, or use the current repository to discover branches and tags.";

    [ObservableProperty]
    private string wizardReferenceCountText = "No refs loaded";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WizardRepositoryVisibility))]
    private string? comparisonRepositoryPath;

    [ObservableProperty]
    private GitPatchSeriesDiscoverySnapshot? wizardSnapshot;

    [ObservableProperty]
    private ImmutableArray<PatchCompareWizardRefViewModel> wizardRefs = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WizardRefsVisibility))]
    [NotifyPropertyChangedFor(nameof(WizardEmptyVisibility))]
    private ImmutableArray<PatchCompareWizardRefViewModel> filteredWizardRefs = [];

    [ObservableProperty]
    private PatchCompareWizardRefViewModel? selectedOldBaseRef;

    [ObservableProperty]
    private PatchCompareWizardRefViewModel? selectedOldHeadRef;

    [ObservableProperty]
    private PatchCompareWizardRefViewModel? selectedNewBaseRef;

    [ObservableProperty]
    private PatchCompareWizardRefViewModel? selectedNewHeadRef;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    [NotifyPropertyChangedFor(nameof(ContentVisibility))]
    private GitPatchSeriesComparisonSnapshot? snapshot;

    [ObservableProperty]
    private ImmutableArray<PatchCompareItemViewModel> items = [];

    [ObservableProperty]
    private string statusText = "Patch comparison ready";

    [ObservableProperty]
    private string summaryText = "Compare any two Git patch series with git range-diff.";

    [ObservableProperty]
    private string oldSeriesText = "Old patch series: enter any Git range";

    [ObservableProperty]
    private string newSeriesText = "New patch series: enter any Git range";

    [ObservableProperty]
    private string rawRangeDiff = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    [NotifyPropertyChangedFor(nameof(ErrorVisibility))]
    private bool hasError;

    public GitPatchSeriesDiscoveryRequest CreateDiscoveryRequest(string? fallbackRepositoryPath)
    {
        var sourceText = WizardRepositoryText.Trim();
        var sourceKind = SemanticDiff.Git.GitPatchSeriesDiscoveryService.InferSourceKind(sourceText, fallbackRepositoryPath);
        return new GitPatchSeriesDiscoveryRequest(
            sourceKind,
            sourceText,
            fallbackRepositoryPath);
    }

    public void UseCurrentRepository(string? repositoryPath)
    {
        WizardRepositoryText = string.Empty;
        ComparisonRepositoryPath = repositoryPath;
        WizardStatusText = string.IsNullOrWhiteSpace(repositoryPath)
            ? "Open a repository before using the current repository source."
            : $"Current repository selected: {repositoryPath}";
    }

    public void SetWizardRunning(string sourceText)
    {
        WizardStatusText = string.IsNullOrWhiteSpace(sourceText)
            ? "Inspecting current repository refs..."
            : $"Inspecting {sourceText}...";
        WizardReferenceCountText = "Loading refs...";
        ComparisonRepositoryPath = null;
        WizardSnapshot = null;
        WizardRefs = [];
        FilteredWizardRefs = [];
        ClearWizardSelection();
    }

    public void SetWizardSnapshot(GitPatchSeriesDiscoverySnapshot snapshot)
    {
        WizardSnapshot = snapshot;
        ComparisonRepositoryPath = snapshot.RepositoryPath;
        WizardStatusText = snapshot.StatusMessage;
        WizardReferenceCountText = $"{snapshot.RefCount:N0} refs discovered";
        WizardRefs = snapshot.Refs.Select(PatchCompareWizardRefViewModel.FromRef).ToImmutableArray();
        ApplyWizardFilter();
        SelectDefaultWizardRefs();
    }

    public void SetWizardError(string message)
    {
        WizardSnapshot = null;
        WizardRefs = [];
        FilteredWizardRefs = [];
        ClearWizardSelection();
        ComparisonRepositoryPath = null;
        WizardReferenceCountText = "No refs loaded";
        WizardStatusText = message;
    }

    public void ApplyWizardSelection()
    {
        if (SelectedOldBaseRef is null || SelectedOldHeadRef is null || SelectedNewBaseRef is null || SelectedNewHeadRef is null)
        {
            throw new ArgumentException("Select old base/head and new base/head refs before applying the visual patch comparison.");
        }

        suppressRangeChangeNotifications = true;
        try
        {
            OldRangeText = $"{SelectedOldBaseRef.RangeName}..{SelectedOldHeadRef.RangeName}";
            NewRangeText = $"{SelectedNewBaseRef.RangeName}..{SelectedNewHeadRef.RangeName}";
            Snapshot = null;
            HasError = false;
            RawRangeDiff = string.Empty;
            Items = [];
            StatusText = "Patch comparison ready";
            SummaryText = "Visual refs applied. Run comparison to refresh results.";
            OldSeriesText = CreateInputSeriesText("Old patch series", OldRangeText);
            NewSeriesText = CreateInputSeriesText("New patch series", NewRangeText);
        }
        finally
        {
            suppressRangeChangeNotifications = false;
        }
    }

    public void Reset()
    {
        suppressRangeChangeNotifications = true;
        try
        {
            OldRangeText = string.Empty;
            NewRangeText = string.Empty;
            WizardRepositoryText = string.Empty;
            WizardFilterText = string.Empty;
            WizardStatusText = "Enter a local path, remote Git URL, or use the current repository to discover branches and tags.";
            WizardReferenceCountText = "No refs loaded";
            ComparisonRepositoryPath = null;
            WizardSnapshot = null;
            WizardRefs = [];
            FilteredWizardRefs = [];
            ClearWizardSelection();
            Snapshot = null;
            HasError = false;
            StatusText = "Patch comparison ready";
            SummaryText = "Compare any two Git patch series with git range-diff.";
            OldSeriesText = "Old patch series: enter any Git range";
            NewSeriesText = "New patch series: enter any Git range";
            RawRangeDiff = string.Empty;
            Items = [];
        }
        finally
        {
            suppressRangeChangeNotifications = false;
        }
    }

    public GitPatchSeriesComparisonRequest CreateRequest(string repositoryPath)
    {
        var oldRange = OldRangeText.Trim();
        var newRange = NewRangeText.Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(oldRange, nameof(OldRangeText));
        ArgumentException.ThrowIfNullOrWhiteSpace(newRange, nameof(NewRangeText));
        var comparisonRepositoryPath = string.IsNullOrWhiteSpace(ComparisonRepositoryPath)
            ? repositoryPath
            : ComparisonRepositoryPath.Trim();
        return new GitPatchSeriesComparisonRequest(comparisonRepositoryPath, oldRange, newRange);
    }

    public void SetRunning()
    {
        Snapshot = null;
        HasError = false;
        RawRangeDiff = string.Empty;
        Items = [];
        StatusText = "Running git range-diff...";
        SummaryText = $"Comparing {OldRangeText} with {NewRangeText}";
    }

    public void SetResult(GitPatchSeriesComparisonSnapshot snapshot)
    {
        var hasError = snapshot.StatusMessage.StartsWith("Patch comparison failed:", StringComparison.Ordinal);
        Snapshot = snapshot;
        HasError = hasError;
        StatusText = snapshot.StatusMessage;
        SummaryText = hasError
            ? snapshot.StatusMessage
            : $"{snapshot.UnchangedCount:N0} unchanged | {snapshot.ModifiedCount:N0} modified | {snapshot.RemovedCount:N0} old-only | {snapshot.AddedCount:N0} new-only";
        OldSeriesText = $"{snapshot.OldSeries.RangeText}: {snapshot.OldSeries.CommitCount:N0} commits, {snapshot.OldSeries.FileCount:N0} files";
        NewSeriesText = $"{snapshot.NewSeries.RangeText}: {snapshot.NewSeries.CommitCount:N0} commits, {snapshot.NewSeries.FileCount:N0} files";
        RawRangeDiff = snapshot.RawRangeDiff;
        Items = snapshot.Items.Select(PatchCompareItemViewModel.FromItem).ToImmutableArray();
    }

    public void SetError(string message)
    {
        Snapshot = null;
        HasError = true;
        StatusText = "Patch comparison failed";
        SummaryText = message;
        RawRangeDiff = string.Empty;
        Items = [];
    }

    partial void OnOldRangeTextChanged(string value) => MarkInputsChanged();

    partial void OnNewRangeTextChanged(string value) => MarkInputsChanged();

    partial void OnWizardFilterTextChanged(string value) => ApplyWizardFilter();

    public void ApplyWizardFilter()
    {
        var refs = WizardRefs;
        if (refs.IsDefaultOrEmpty)
        {
            FilteredWizardRefs = [];
            return;
        }

        var filter = WizardFilterText.Trim();
        FilteredWizardRefs = string.IsNullOrWhiteSpace(filter)
            ? refs
            : refs
                .Where(item => item.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray();
    }

    private void MarkInputsChanged()
    {
        if (suppressRangeChangeNotifications)
        {
            return;
        }

        Snapshot = null;
        HasError = false;
        RawRangeDiff = string.Empty;
        Items = [];
        StatusText = "Patch comparison ready";
        SummaryText = "Inputs changed. Run comparison to refresh results.";
        OldSeriesText = CreateInputSeriesText("Old patch series", OldRangeText);
        NewSeriesText = CreateInputSeriesText("New patch series", NewRangeText);
    }

    private static string CreateInputSeriesText(string label, string rangeText)
    {
        var trimmed = rangeText.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? $"{label}: enter any Git range"
            : $"{label}: {trimmed}";
    }

    private void SelectDefaultWizardRefs()
    {
        if (FilteredWizardRefs.IsDefaultOrEmpty)
        {
            ClearWizardSelection();
            return;
        }

        var defaultRef = FilteredWizardRefs.FirstOrDefault(item => item.IsCurrent) ??
                         FilteredWizardRefs.FirstOrDefault(item => item.IsDefault) ??
                         FilteredWizardRefs[0];
        SelectedOldBaseRef ??= defaultRef;
        SelectedNewBaseRef ??= defaultRef;
        SelectedOldHeadRef ??= FilteredWizardRefs.FirstOrDefault(item => !ReferenceEquals(item, defaultRef)) ?? defaultRef;
        SelectedNewHeadRef ??= SelectedOldHeadRef;
    }

    private void ClearWizardSelection()
    {
        SelectedOldBaseRef = null;
        SelectedOldHeadRef = null;
        SelectedNewBaseRef = null;
        SelectedNewHeadRef = null;
    }
}

public sealed record PatchCompareWizardRefViewModel(
    string DisplayName,
    string DetailText,
    string RangeName,
    string SearchText,
    string KindText,
    string Sha,
    bool IsDefault,
    bool IsCurrent)
{
    public string BadgeText => IsCurrent ? "current" : IsDefault ? "default" : KindText;

    public string SelectionText => $"{DisplayName}  {RangeName}";

    public static PatchCompareWizardRefViewModel FromRef(GitPatchSeriesRefInfo item)
    {
        var timeText = item.CommitTime is null ? "unknown time" : item.CommitTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var kindText = item.Kind switch
        {
            GitPatchSeriesRefKind.Branch => "branch",
            GitPatchSeriesRefKind.RemoteBranch => "remote",
            GitPatchSeriesRefKind.Tag => "tag",
            GitPatchSeriesRefKind.PullRequest => "PR",
            GitPatchSeriesRefKind.MergeRequest => "MR",
            _ => "ref"
        };
        var subject = string.IsNullOrWhiteSpace(item.Subject) ? "No commit subject" : item.Subject;
        var detail = $"{kindText} | {item.RangeName} | {item.Sha} | {timeText} | {subject}";
        return new PatchCompareWizardRefViewModel(
            item.DisplayName,
            detail,
            item.RangeName,
            item.SearchText,
            kindText,
            item.Sha,
            item.IsDefault,
            item.IsCurrent);
    }
}

public sealed record PatchCompareItemViewModel(
    string StatusText,
    string StatusDetail,
    string OldIndexText,
    string OldCommitText,
    string NewIndexText,
    string NewCommitText,
    string Subject,
    string DetailText)
{
    public static PatchCompareItemViewModel FromItem(GitPatchSeriesComparisonItem item)
    {
        var (status, detail) = item.Kind switch
        {
            GitPatchSeriesComparisonKind.Unchanged => ("=", "Present unchanged"),
            GitPatchSeriesComparisonKind.Modified => ("!", "Present with changes"),
            GitPatchSeriesComparisonKind.Removed => ("<", "Old-only patch"),
            GitPatchSeriesComparisonKind.Added => (">", "New-only patch"),
            _ => ("?", "Unclassified")
        };

        return new PatchCompareItemViewModel(
            status,
            detail,
            item.OldIndex?.ToString() ?? "-",
            item.OldCommit ?? "-------",
            item.NewIndex?.ToString() ?? "-",
            item.NewCommit ?? "-------",
            item.Subject,
            item.DetailText);
    }
}

public sealed partial class QueryCanvasTabViewModel : ObservableObject
{
    public static string DefaultQuery => QueryCanvasSampleCatalog.Default.Query;

    private readonly DiffDocumentFactory documentFactory = new();

    public QueryCanvasTabViewModel(string title, string description, ICodeCompletionProvider completionProvider)
    {
        Title = title;
        Description = description;
        CompletionProvider = completionProvider;
        ScopeOptions =
        [
            new QueryCanvasScopeOption(QueryCanvasScope.Diff, "Current diff", "Query files and symbols from the active diff workspace"),
            new QueryCanvasScopeOption(QueryCanvasScope.Workspace, "MSBuild workspace", "Query files from the loaded MSBuild workspace and current semantic symbols")
        ];
        SampleOptions = QueryCanvasSampleCatalog.All;
        selectedScopeOption = ScopeOptions[0];
        selectedSampleOption = QueryCanvasSampleCatalog.Default;
        queryText = selectedSampleOption.Query;
        queryLines = CreateQueryLines(queryText);
        queryRefreshKey = Guid.NewGuid().ToString("N");
    }

    public event EventHandler? QueryChanged;

    public string Title { get; }

    public string Description { get; }

    public ImmutableArray<QueryCanvasScopeOption> ScopeOptions { get; }

    public ImmutableArray<QueryCanvasSample> SampleOptions { get; }

    public ICodeCompletionProvider CompletionProvider { get; }

    public QueryCanvasScope Scope => SelectedScopeOption?.Scope ?? QueryCanvasScope.Diff;

    public Visibility ContentVisibility => Scene.Nodes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyVisibility => Scene.Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ErrorVisibility => HasError ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private QueryCanvasScopeOption? selectedScopeOption;

    [ObservableProperty]
    private QueryCanvasSample? selectedSampleOption;

    [ObservableProperty]
    private string queryText;

    [ObservableProperty]
    private ImmutableArray<DiffLine> queryLines;

    [ObservableProperty]
    private string queryRefreshKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContentVisibility))]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    private DiffCanvasScene scene = DiffCanvasScene.FromDocuments([]);

    [ObservableProperty]
    private string statusText = "Query canvas ready";

    [ObservableProperty]
    private string resultText = "Write a LINQ query over Files, WorkspaceFiles, Symbols, ChangedSymbols, or LinkedSymbols.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorVisibility))]
    private bool hasError;

    partial void OnSelectedScopeOptionChanged(QueryCanvasScopeOption? value) => RaiseQueryChanged();

    partial void OnSelectedSampleOptionChanged(QueryCanvasSample? value)
    {
        if (value is null)
        {
            return;
        }

        var preferredScope = ScopeOptions.FirstOrDefault(option => option.Scope == value.PreferredScope);
        if (preferredScope is not null && SelectedScopeOption != preferredScope)
        {
            SelectedScopeOption = preferredScope;
        }

        if (!string.Equals(QueryText, value.Query, StringComparison.Ordinal))
        {
            QueryText = value.Query;
        }
    }

    partial void OnQueryTextChanged(string value)
    {
        QueryLines = CreateQueryLines(value);
        QueryRefreshKey = Guid.NewGuid().ToString("N");
        RaiseQueryChanged();
    }

    public void SetResult(QueryCanvasExecutionResult result)
    {
        Scene = result.Scene;
        StatusText = result.StatusText;
        ResultText = result.DetailText;
        HasError = result.HasError;
    }

    public void SetExecuting()
    {
        StatusText = "Running query...";
        HasError = false;
    }

    private void RaiseQueryChanged() => QueryChanged?.Invoke(this, EventArgs.Empty);

    private ImmutableArray<DiffLine> CreateQueryLines(string text)
    {
        var metadata = new DiffDocumentMetadata(
            new DiffDocumentId("query://canvas.csx"),
            "query.csx",
            null,
            DiffFileStatus.Modified,
            "C#",
            0,
            0);
        return documentFactory.CreateFromText(metadata, text ?? string.Empty, DiffLineKind.Context).Lines;
    }
}

public sealed record EditorCanvasDocument(
    DiffDocumentSnapshot Document,
    string FullText,
    ImmutableArray<CodeFoldRegion> FoldRegions);

public sealed partial class EditorCanvasTabViewModel : ObservableObject
{
    public EditorCanvasTabViewModel(string title, string description, DiffCanvasScene scene)
    {
        Title = title;
        Description = description;
        this.scene = scene;
        UpdateStatus();
    }

    public string Title { get; }

    public string Description { get; }

    public ImmutableArray<EditorCanvasDocument> Documents { get; private set; } = [];

    public Visibility EmptyVisibility => Documents.IsDefaultOrEmpty ? Visibility.Visible : Visibility.Collapsed;

    public string SummaryText => Documents.IsDefaultOrEmpty
        ? "Empty editor canvas | drag files here to create editable code nodes"
        : $"{Documents.Length:N0} editable file nodes | drag more files here to add nodes";

    [ObservableProperty]
    private DiffCanvasScene scene;

    [ObservableProperty]
    private string statusText = string.Empty;

    public bool ContainsDocument(string documentId) =>
        Documents.Any(document => string.Equals(document.Document.Id.Value, documentId, StringComparison.OrdinalIgnoreCase));

    public void SetDocuments(ImmutableArray<EditorCanvasDocument> documents, DiffCanvasScene nextScene)
    {
        Documents = documents.IsDefault ? [] : documents;
        Scene = nextScene;
        UpdateStatus();
        OnPropertyChanged(nameof(Documents));
        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(SummaryText));
    }

    private void UpdateStatus()
    {
        StatusText = Documents.IsDefaultOrEmpty
            ? "Editor canvas ready for file drops"
            : $"{Documents.Length:N0} editable files in canvas";
    }
}

public sealed partial class SymbolGraphTabViewModel : ObservableObject
{
    private const int MaxRenderedSymbols = 240;
    private const string AllKey = "All";
    private const string ChangedKey = "Changed";
    private const string LinkedKey = "Linked";
    private const string UnlinkedKey = "Unlinked";

    private readonly ImmutableArray<SemanticNavigationItem> sourceItems;
    private readonly SemanticGraph sourceGraph;
    private readonly ImmutableArray<DiffDocumentSnapshot> sourceDocuments;
    private readonly string? focusAnchorId;
    private readonly SynchronizationContext? synchronizationContext;
    private bool isRefreshing;
    private long refreshVersion;
    private CancellationTokenSource? refreshOperation;

    private readonly record struct SymbolGraphSelection(
        SymbolGraphFilterOptionViewModel Scope,
        SymbolGraphFilterOptionViewModel Kind,
        SymbolGraphFilterOptionViewModel Document,
        SymbolGraphEdgeKindOptionViewModel EdgeKind,
        LayoutModeOptionViewModel Layout,
        GroupingModeOptionViewModel Grouping,
        SymbolGraphViewModeOptionViewModel ViewMode);

    public SymbolGraphTabViewModel(
        string title,
        string description,
        ImmutableArray<SemanticNavigationItem> sourceItems,
        SemanticGraph sourceGraph,
        ImmutableArray<DiffDocumentSnapshot> sourceDocuments,
        string initialSearchText = "",
        string initialScopeKey = AllKey,
        string initialKindKey = AllKey,
        string initialDocumentId = AllKey,
        string initialEdgeKindKey = AllKey,
        GraphLayoutMode initialLayoutMode = GraphLayoutMode.Layered,
        GraphGroupingMode initialGroupingMode = GraphGroupingMode.Semantic,
        SymbolGraphViewMode initialViewMode = SymbolGraphViewMode.SymbolsOnly,
        string? focusAnchorId = null)
    {
        Title = title;
        Description = description;
        synchronizationContext = SynchronizationContext.Current;
        this.sourceItems = sourceItems.IsDefault ? [] : sourceItems;
        this.sourceGraph = sourceGraph;
        this.sourceDocuments = sourceDocuments.IsDefault ? [] : sourceDocuments;
        this.focusAnchorId = focusAnchorId;

        ScopeOptions =
        [
            new SymbolGraphFilterOptionViewModel(AllKey, "All", "All symbols"),
            new SymbolGraphFilterOptionViewModel(ChangedKey, "Changed", "Symbols touched by the diff"),
            new SymbolGraphFilterOptionViewModel(LinkedKey, "Linked", "Symbols with semantic edges"),
            new SymbolGraphFilterOptionViewModel(UnlinkedKey, "Unlinked", "Symbols without semantic edges")
        ];
        KindOptions = CreateKindOptions(this.sourceItems);
        DocumentOptions = CreateDocumentOptions(this.sourceItems);
        EdgeKindOptions = CreateEdgeKindOptions(this.sourceGraph);
        LayoutOptions = LayoutModeOptionViewModel.All;
        GroupingOptions = GroupingModeOptionViewModel.All;
        ViewModeOptions = SymbolGraphViewModeOptionViewModel.All;

        searchText = initialSearchText;
        selectedScopeOption = ScopeOptions.FirstOrDefault(option => string.Equals(option.Key, initialScopeKey, StringComparison.OrdinalIgnoreCase)) ?? ScopeOptions[0];
        selectedKindOption = KindOptions.FirstOrDefault(option => string.Equals(option.Key, initialKindKey, StringComparison.OrdinalIgnoreCase)) ?? KindOptions[0];
        selectedDocumentOption = DocumentOptions.FirstOrDefault(option => string.Equals(option.Key, initialDocumentId, StringComparison.Ordinal)) ?? DocumentOptions[0];
        selectedEdgeKindOption = EdgeKindOptions.FirstOrDefault(option => string.Equals(option.Key, initialEdgeKindKey, StringComparison.OrdinalIgnoreCase)) ?? EdgeKindOptions[0];
        selectedLayoutOption = LayoutOptions.FirstOrDefault(option => option.Mode == initialLayoutMode) ?? LayoutOptions[1];
        selectedGroupingOption = GroupingOptions.FirstOrDefault(option => option.Mode == initialGroupingMode) ?? GroupingOptions[2];
        selectedViewModeOption = ViewModeOptions.FirstOrDefault(option => option.Mode == initialViewMode) ?? ViewModeOptions[0];

        RefreshScene();
    }

    public string Title { get; }

    public string Description { get; }

    public string? FocusAnchorId => focusAnchorId;

    public ImmutableArray<SymbolGraphFilterOptionViewModel> ScopeOptions { get; }

    public ImmutableArray<SymbolGraphFilterOptionViewModel> KindOptions { get; }

    public ImmutableArray<SymbolGraphFilterOptionViewModel> DocumentOptions { get; }

    public ImmutableArray<SymbolGraphEdgeKindOptionViewModel> EdgeKindOptions { get; }

    public ImmutableArray<LayoutModeOptionViewModel> LayoutOptions { get; }

    public ImmutableArray<GroupingModeOptionViewModel> GroupingOptions { get; }

    public ImmutableArray<SymbolGraphViewModeOptionViewModel> ViewModeOptions { get; }

    public Visibility EmptyVisibility => !IsLoading && RenderedSymbolCount == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ContentVisibility => RenderedSymbolCount == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private SymbolGraphFilterOptionViewModel selectedScopeOption = new(AllKey, "All", "All symbols");

    [ObservableProperty]
    private SymbolGraphFilterOptionViewModel selectedKindOption = new(AllKey, "All kinds", "All symbol kinds");

    [ObservableProperty]
    private SymbolGraphFilterOptionViewModel selectedDocumentOption = new(AllKey, "All files", "All files");

    [ObservableProperty]
    private SymbolGraphEdgeKindOptionViewModel selectedEdgeKindOption = new(null, AllKey, "All edges", "All semantic edges");

    [ObservableProperty]
    private LayoutModeOptionViewModel selectedLayoutOption = LayoutModeOptionViewModel.All[1];

    [ObservableProperty]
    private GroupingModeOptionViewModel selectedGroupingOption = GroupingModeOptionViewModel.All[2];

    [ObservableProperty]
    private SymbolGraphViewModeOptionViewModel selectedViewModeOption = SymbolGraphViewModeOptionViewModel.All[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    [NotifyPropertyChangedFor(nameof(ContentVisibility))]
    private int renderedSymbolCount;

    [ObservableProperty]
    private int renderedFileCount;

    [ObservableProperty]
    private int filteredSymbolCount;

    [ObservableProperty]
    private int renderedEdgeCount;

    [ObservableProperty]
    private string summaryText = "No symbols";

    [ObservableProperty]
    private string filterStatusText = "Showing all symbols";

    [ObservableProperty]
    private string statusText = "Symbol graph ready";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    [NotifyPropertyChangedFor(nameof(LoadingVisibility))]
    private bool isLoading;

    [ObservableProperty]
    private DiffCanvasScene scene = DiffCanvasScene.FromDocuments([]);

    partial void OnSearchTextChanged(string value) => RefreshScene();

    partial void OnSelectedScopeOptionChanged(SymbolGraphFilterOptionViewModel value) => RefreshScene();

    partial void OnSelectedKindOptionChanged(SymbolGraphFilterOptionViewModel value) => RefreshScene();

    partial void OnSelectedDocumentOptionChanged(SymbolGraphFilterOptionViewModel value) => RefreshScene();

    partial void OnSelectedEdgeKindOptionChanged(SymbolGraphEdgeKindOptionViewModel value) => RefreshScene();

    partial void OnSelectedLayoutOptionChanged(LayoutModeOptionViewModel value) => RefreshScene();

    partial void OnSelectedGroupingOptionChanged(GroupingModeOptionViewModel value) => RefreshScene();

    partial void OnSelectedViewModeOptionChanged(SymbolGraphViewModeOptionViewModel value) => RefreshScene();

    public void Relayout() => RefreshScene();

    public void CancelRefresh()
    {
        Interlocked.Increment(ref refreshVersion);
        var operation = Interlocked.Exchange(ref refreshOperation, null);
        operation?.Cancel();
        IsLoading = false;
    }

    public void SetLayoutMode(GraphLayoutMode layoutMode)
    {
        var option = LayoutOptions.FirstOrDefault(candidate => candidate.Mode == layoutMode) ?? LayoutOptions[1];
        if (SelectedLayoutOption == option)
        {
            RefreshScene();
            return;
        }

        SelectedLayoutOption = option;
    }

    public void SetGroupingMode(GraphGroupingMode groupingMode)
    {
        var option = GroupingOptions.FirstOrDefault(candidate => candidate.Mode == groupingMode) ?? GroupingOptions[2];
        if (SelectedGroupingOption == option)
        {
            RefreshScene();
            return;
        }

        SelectedGroupingOption = option;
    }

    public void ResetFilters()
    {
        SearchText = string.Empty;
        SelectedScopeOption = ScopeOptions[0];
        SelectedKindOption = KindOptions[0];
        SelectedDocumentOption = DocumentOptions[0];
        SelectedEdgeKindOption = EdgeKindOptions[0];
        RefreshScene();
    }

    private void RefreshScene()
    {
        if (isRefreshing)
        {
            return;
        }

        isRefreshing = true;
        SymbolGraphSelection selection;
        string query;
        try
        {
            selection = NormalizeSelectionOptions();
            query = (SearchText ?? string.Empty).Trim();
        }
        finally
        {
            isRefreshing = false;
        }

        var version = Interlocked.Increment(ref refreshVersion);
        var operation = new CancellationTokenSource();
        var previousOperation = refreshOperation;
        refreshOperation = operation;
        previousOperation?.Cancel();

        IsLoading = true;
        StatusText = "Building symbol graph...";
        _ = RefreshSceneAsync(selection, query, version, operation);
    }

    private async Task RefreshSceneAsync(
        SymbolGraphSelection selection,
        string query,
        long version,
        CancellationTokenSource operation)
    {
        try
        {
            var result = await Task.Run(
                    () => BuildSceneResult(selection, query, operation.Token),
                    operation.Token)
                .ConfigureAwait(false);
            RunOnCapturedContext(() => ApplySceneResult(result, version, operation));
        }
        catch (OperationCanceledException)
        {
            operation.Dispose();
        }
        catch (Exception exception)
        {
            RunOnCapturedContext(() =>
            {
                if (version != refreshVersion || !ReferenceEquals(refreshOperation, operation))
                {
                    operation.Dispose();
                    return;
                }

                IsLoading = false;
                StatusText = $"Symbol graph unavailable: {exception.Message}";
                refreshOperation = null;
                operation.Dispose();
            });
        }
    }

    private SymbolGraphRefreshResult BuildSceneResult(
        SymbolGraphSelection selection,
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var edgeKindAnchorIds = CreateEdgeKindAnchorSet(selection.EdgeKind.Kind);
        var filtered = sourceItems
            .Where(item => MatchesScope(item, selection.Scope.Key))
            .Where(item => MatchesOption(item.KindText, selection.Kind.Key, ignoreCase: true))
            .Where(item => MatchesOption(item.DocumentId.Value, selection.Document.Key, ignoreCase: false))
            .Where(item => edgeKindAnchorIds is null || edgeKindAnchorIds.Contains(item.AnchorId))
            .Where(item => string.IsNullOrWhiteSpace(query) || item.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();
        cancellationToken.ThrowIfCancellationRequested();

        var selected = filtered
            .OrderByDescending(item => string.Equals(item.AnchorId, focusAnchorId, StringComparison.Ordinal))
            .ThenByDescending(item => item.IsChanged)
            .ThenByDescending(item => item.IsLinked)
            .ThenByDescending(item => item.IncidentEdgeCount)
            .ThenBy(item => item.KindText, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Line)
            .Take(MaxRenderedSymbols)
            .ToImmutableArray();
        cancellationToken.ThrowIfCancellationRequested();

        var sceneResult = new SymbolGraphSceneBuilder().Build(new SymbolGraphSceneBuildRequest(
            selected,
            sourceGraph,
            sourceDocuments,
            selection.Layout.Mode,
            selection.Grouping.Mode,
            selection.ViewMode.Mode,
            selection.EdgeKind.Kind));
        var renderedEdgeCount = sceneResult.Scene.Edges.Count;
        var summary = BuildSummary(filtered.Length, selected.Length, sceneResult.FileCount, renderedEdgeCount, selection.ViewMode.Mode);
        return new SymbolGraphRefreshResult(
            sceneResult.Scene,
            filtered.Length,
            selected.Length,
            sceneResult.FileCount,
            renderedEdgeCount,
            summary,
            BuildFilterStatus(filtered.Length, selected.Length, query, selection),
            $"{summary} | {selection.Layout.DisplayName} | {selection.Grouping.DisplayName}");
    }

    private void ApplySceneResult(SymbolGraphRefreshResult result, long version, CancellationTokenSource operation)
    {
        if (version != refreshVersion || !ReferenceEquals(refreshOperation, operation))
        {
            operation.Dispose();
            return;
        }

        FilteredSymbolCount = result.FilteredSymbolCount;
        Scene = result.Scene;
        RenderedSymbolCount = result.RenderedSymbolCount;
        RenderedFileCount = result.RenderedFileCount;
        RenderedEdgeCount = result.RenderedEdgeCount;
        SummaryText = result.SummaryText;
        FilterStatusText = result.FilterStatusText;
        StatusText = result.StatusText;
        IsLoading = false;
        refreshOperation = null;
        operation.Dispose();
    }

    private void RunOnCapturedContext(Action action)
    {
        if (synchronizationContext is not null && SynchronizationContext.Current != synchronizationContext)
        {
            synchronizationContext.Post(_ => action(), null);
            return;
        }

        action();
    }

    private SymbolGraphSelection NormalizeSelectionOptions()
    {
        // Uno ComboBox can transiently push null SelectedItem values while rebinding ItemsSource.
        var scope = NormalizeSelectedOption(SelectedScopeOption, ScopeOptions, 0, value => SelectedScopeOption = value, nameof(SelectedScopeOption));
        var kind = NormalizeSelectedOption(SelectedKindOption, KindOptions, 0, value => SelectedKindOption = value, nameof(SelectedKindOption));
        var document = NormalizeSelectedOption(SelectedDocumentOption, DocumentOptions, 0, value => SelectedDocumentOption = value, nameof(SelectedDocumentOption));
        var edgeKind = NormalizeSelectedOption(SelectedEdgeKindOption, EdgeKindOptions, 0, value => SelectedEdgeKindOption = value, nameof(SelectedEdgeKindOption));
        var layout = NormalizeSelectedOption(SelectedLayoutOption, LayoutOptions, 1, value => SelectedLayoutOption = value, nameof(SelectedLayoutOption));
        var grouping = NormalizeSelectedOption(SelectedGroupingOption, GroupingOptions, 2, value => SelectedGroupingOption = value, nameof(SelectedGroupingOption));
        var viewMode = NormalizeSelectedOption(SelectedViewModeOption, ViewModeOptions, 0, value => SelectedViewModeOption = value, nameof(SelectedViewModeOption));
        return new SymbolGraphSelection(scope, kind, document, edgeKind, layout, grouping, viewMode);
    }

    private static T NormalizeSelectedOption<T>(T? selectedOption, ImmutableArray<T> options, int preferredIndex, Action<T> setSelectedOption, string propertyName)
        where T : class
    {
        if (selectedOption is not null)
        {
            return selectedOption;
        }

        if (options.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException($"Symbol graph option list for {propertyName} is empty.");
        }

        var fallbackIndex = preferredIndex >= 0 && preferredIndex < options.Length ? preferredIndex : 0;
        var fallback = options[fallbackIndex];
        setSelectedOption(fallback);
        return fallback;
    }

    private static ImmutableArray<SymbolGraphFilterOptionViewModel> CreateKindOptions(ImmutableArray<SemanticNavigationItem> items) =>
    [
        new SymbolGraphFilterOptionViewModel(AllKey, "All kinds", "All symbol kinds"),
        .. items
            .GroupBy(item => item.KindText, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SymbolGraphFilterOptionViewModel(group.Key, group.Key, $"{group.Count():N0} symbols"))
    ];

    private static ImmutableArray<SymbolGraphFilterOptionViewModel> CreateDocumentOptions(ImmutableArray<SemanticNavigationItem> items) =>
    [
        new SymbolGraphFilterOptionViewModel(AllKey, "All files", "All files"),
        .. items
            .GroupBy(item => item.DocumentId.Value, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var fileName = System.IO.Path.GetFileName(first.Path);
                return new SymbolGraphFilterOptionViewModel(group.Key, string.IsNullOrWhiteSpace(fileName) ? first.Path : fileName, $"{group.Count():N0} symbols | {first.Path}");
            })
            .OrderByDescending(option => ExtractLeadingCount(option.DetailText))
            .ThenBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
    ];

    private static ImmutableArray<SymbolGraphEdgeKindOptionViewModel> CreateEdgeKindOptions(SemanticGraph graph) =>
    [
        new SymbolGraphEdgeKindOptionViewModel(null, AllKey, "All edges", "All semantic edges"),
        new SymbolGraphEdgeKindOptionViewModel(SemanticEdgeKind.Contains, SemanticEdgeKind.Contains.ToString(), "Contains", "File to symbol declarations"),
        .. graph.Edges
            .Where(edge => edge.Kind != SemanticEdgeKind.Contains)
            .GroupBy(edge => edge.Kind)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new SymbolGraphEdgeKindOptionViewModel(group.Key, group.Key.ToString(), group.Key.ToString(), $"{group.Count():N0} edges"))
    ];

    private static bool MatchesScope(SemanticNavigationItem item, string scopeKey) => scopeKey switch
    {
        ChangedKey => item.IsChanged,
        LinkedKey => item.IsLinked,
        UnlinkedKey => !item.IsLinked,
        _ => true
    };

    private static bool MatchesOption(string value, string key, bool ignoreCase)
    {
        if (string.Equals(key, AllKey, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(value, key, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private HashSet<string>? CreateEdgeKindAnchorSet(SemanticEdgeKind? edgeKind)
    {
        if (edgeKind is null)
        {
            return null;
        }

        if (edgeKind == SemanticEdgeKind.Contains)
        {
            return sourceItems.Select(item => item.AnchorId).ToHashSet(StringComparer.Ordinal);
        }

        var anchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in sourceGraph.Edges.Where(edge => edge.Kind == edgeKind))
        {
            anchors.Add(edge.SourceAnchorId);
            anchors.Add(edge.TargetAnchorId);
        }

        return anchors;
    }

    private static string BuildSummary(int filteredCount, int renderedCount, int renderedFileCount, int edgeCount, SymbolGraphViewMode viewMode)
    {
        var cappedText = filteredCount > renderedCount ? $"showing first {renderedCount:N0} of " : string.Empty;
        var fileText = viewMode == SymbolGraphViewMode.FilesAndSymbols ? $" | {renderedFileCount:N0} files" : string.Empty;
        return $"{cappedText}{filteredCount:N0} symbols{fileText} | {edgeCount:N0} links";
    }

    private static string BuildFilterStatus(int filteredCount, int renderedCount, string query, SymbolGraphSelection selection)
    {
        var parts = new List<string>();
        if (!string.Equals(selection.Scope.Key, AllKey, StringComparison.Ordinal))
        {
            parts.Add(selection.Scope.DisplayName);
        }

        if (!string.Equals(selection.Kind.Key, AllKey, StringComparison.Ordinal))
        {
            parts.Add(selection.Kind.DisplayName);
        }

        if (!string.Equals(selection.Document.Key, AllKey, StringComparison.Ordinal))
        {
            parts.Add(selection.Document.DisplayName);
        }

        if (!string.Equals(selection.EdgeKind.Key, AllKey, StringComparison.Ordinal))
        {
            parts.Add(selection.EdgeKind.DisplayName);
        }

        if (selection.ViewMode.Mode == SymbolGraphViewMode.FilesAndSymbols)
        {
            parts.Add("files + symbols");
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            parts.Add($"search \"{query}\"");
        }

        var filterText = parts.Count == 0 ? "Showing all symbols" : $"Filtered by {string.Join(", ", parts)}";
        return filteredCount > renderedCount
            ? $"{filterText}; rendering first {renderedCount:N0} for graph readability"
            : filterText;
    }

    private static int ExtractLeadingCount(string text)
    {
        var digits = new string(text.TakeWhile(character => char.IsDigit(character) || character == ',' || character == '.').ToArray());
        return int.TryParse(digits.Replace(",", string.Empty).Replace(".", string.Empty), out var count) ? count : 0;
    }

    private sealed record SymbolGraphRefreshResult(
        DiffCanvasScene Scene,
        int FilteredSymbolCount,
        int RenderedSymbolCount,
        int RenderedFileCount,
        int RenderedEdgeCount,
        string SummaryText,
        string FilterStatusText,
        string StatusText);
}

public sealed partial record SymbolGraphFilterOptionViewModel(string Key, string DisplayName, string DetailText)
{
    public override string ToString() => DisplayName;
}

public sealed partial record SymbolGraphEdgeKindOptionViewModel(SemanticEdgeKind? Kind, string Key, string DisplayName, string DetailText)
{
    public override string ToString() => DisplayName;
}

public sealed partial record SymbolGraphViewModeOptionViewModel(SymbolGraphViewMode Mode, string DisplayName, string DetailText)
{
    public static ImmutableArray<SymbolGraphViewModeOptionViewModel> All { get; } =
    [
        new(SymbolGraphViewMode.SymbolsOnly, "Symbols", "Only semantic symbols"),
        new(SymbolGraphViewMode.FilesAndSymbols, "Files + symbols", "Real file diff nodes connected to symbol nodes")
    ];

    public override string ToString() => DisplayName;
}

public sealed record GraphWorkspaceState(
    string? RepositoryPath,
    GitDiffRequest? Request,
    GitPullRequestInfo? ReviewRequest,
    string RepositoryName,
    string ContextText,
    string StatusText,
    string StatusPrefix,
    bool DocumentsAreRepositoryDocuments,
    ImmutableArray<DiffDocumentSnapshot> Documents,
    ImmutableArray<ExplorerItemViewModel> ExplorerItems,
    ImmutableArray<FileExplorerNode> ExplorerTreeRoots,
    ImmutableArray<SemanticNavigationItem> SemanticNavigationItems,
    SemanticSymbolInsightSummary SymbolInsight,
    ImmutableDictionary<DiffDocumentId, SemanticDocumentInsight> SemanticDocumentInsights,
    SemanticGraph SemanticGraph,
    GitDiffSnapshot? GitSnapshot,
    GraphLayoutResult? PreviousLayout,
    ImmutableHashSet<DiffDocumentId> PinnedDocumentIds,
    DiffCanvasScene Scene,
    string? SelectedDocumentId,
    ImmutableArray<ReviewThreadItemViewModel> ReviewThreadItems,
    ImmutableArray<GitReviewThreadInfo> ReviewThreads,
    GitReviewRequestKind ReviewRequestKind);

public sealed partial class BlameTabViewModel : ObservableObject
{
    public BlameTabViewModel(
        string path,
        string language,
        string summaryText,
        string statusText,
        ImmutableArray<BlameCommitNodeViewModel> commitNodes,
        ImmutableArray<BlameTimelineItemViewModel> timelineItems,
        DiffCanvasScene? changeGraphScene = null,
        string changeGraphSummaryText = "")
    {
        Path = path;
        Language = language;
        SummaryText = summaryText;
        StatusText = statusText;
        CommitNodes = commitNodes;
        TimelineItems = timelineItems;
        ChangeGraphScene = changeGraphScene ?? DiffCanvasScene.FromDocuments([]);
        ChangeGraphSummaryText = string.IsNullOrWhiteSpace(changeGraphSummaryText)
            ? "Change graph unavailable"
            : changeGraphSummaryText;
    }

    public string Path { get; }

    public string Language { get; }

    public string SummaryText { get; }

    public string StatusText { get; }

    public ImmutableArray<BlameCommitNodeViewModel> CommitNodes { get; }

    public ImmutableArray<BlameTimelineItemViewModel> TimelineItems { get; }

    public DiffCanvasScene ChangeGraphScene { get; }

    public string ChangeGraphSummaryText { get; }

    public Visibility EmptyVisibility => CommitNodes.IsDefaultOrEmpty ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ContentVisibility => CommitNodes.IsDefaultOrEmpty ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CommitTimelineVisibility => ContentVisibility == Visibility.Visible && DisplayMode == BlameDisplayMode.CommitTimeline
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ChangeGraphVisibility => ContentVisibility == Visibility.Visible && DisplayMode == BlameDisplayMode.ChangeGraph
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CollapsedTimelineVisibility => IsTimelineExpanded ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ExpandedTimelineVisibility => IsTimelineExpanded ? Visibility.Visible : Visibility.Collapsed;

    public string TimelineToggleText => IsTimelineExpanded ? "Shrink timeline" : "Expand timeline";

    public bool IsCommitTimelineMode => DisplayMode == BlameDisplayMode.CommitTimeline;

    public bool IsChangeGraphMode => DisplayMode == BlameDisplayMode.ChangeGraph;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CollapsedTimelineVisibility))]
    [NotifyPropertyChangedFor(nameof(ExpandedTimelineVisibility))]
    [NotifyPropertyChangedFor(nameof(TimelineToggleText))]
    private bool isTimelineExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitTimelineVisibility))]
    [NotifyPropertyChangedFor(nameof(ChangeGraphVisibility))]
    [NotifyPropertyChangedFor(nameof(IsCommitTimelineMode))]
    [NotifyPropertyChangedFor(nameof(IsChangeGraphMode))]
    private BlameDisplayMode displayMode = BlameDisplayMode.CommitTimeline;

    public void ToggleTimeline() => IsTimelineExpanded = !IsTimelineExpanded;

    public void SetDisplayMode(BlameDisplayMode mode) => DisplayMode = mode;

    public static BlameTabViewModel Loading(string path, string language) => new(
        path,
        language,
        "Loading blame and file history",
        $"{path} | loading blame",
        [],
        []);

    public static BlameTabViewModel FromBlame(string path, string language, GitFileBlame blame, ImmutableArray<GitCommitInfo> history)
    {
        if (blame.Lines.IsDefaultOrEmpty)
        {
            return new BlameTabViewModel(
                path,
                language,
                "No blame data available",
                $"{path} | blame unavailable",
                [],
                BuildTimelineItems(history, ImmutableDictionary<string, int>.Empty));
        }

        var totalLines = blame.Lines.Length;
        var lineCountsByCommit = blame.Lines
            .GroupBy(line => line.CommitId, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var groupsByCommit = blame.Lines
            .GroupBy(line => line.CommitId, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.ToImmutableArray(), StringComparer.Ordinal);
        var nodes = groupsByCommit
            .Select((pair, index) => BlameCommitNodeViewModel.FromLines(pair.Key, pair.Value, totalLines, index))
            .OrderByDescending(node => node.AuthorTime)
            .ThenBy(node => node.Author, StringComparer.Ordinal)
            .ToImmutableArray();
        var latest = nodes.FirstOrDefault();
        var authors = nodes
            .GroupBy(node => node.Author, StringComparer.Ordinal)
            .OrderByDescending(group => group.Sum(node => node.LineCount))
            .Take(2)
            .Select(group => $"{group.Key} {group.Sum(node => node.LineCount):N0}");
        var summary = $"{totalLines:N0} blamed lines | {nodes.Length:N0} commits | {string.Join(", ", authors)}";
        if (latest is not null)
        {
            summary += $" | latest {latest.Author} {latest.TimeText} {latest.ShortId}";
        }

        var timelineItems = BuildTimelineItems(history, lineCountsByCommit);
        var changeGraph = new BlameChangeGraphBuilder().Build(new BlameChangeGraphBuildRequest(
            path,
            language,
            timelineItems
                .Select(item => new BlameChangeGraphCommit(item.CommitId, item.ShortId, item.Subject, item.Author, item.TimeText, item.BlamedLineCount))
                .ToImmutableArray(),
            groupsByCommit));
        return new BlameTabViewModel(
            path,
            language,
            summary,
            $"{path} | {summary}",
            nodes,
            timelineItems,
            changeGraph.Scene,
            changeGraph.SummaryText);
    }

    private static ImmutableArray<BlameTimelineItemViewModel> BuildTimelineItems(
        ImmutableArray<GitCommitInfo> history,
        ImmutableDictionary<string, int> lineCountsByCommit)
    {
        if (history.IsDefaultOrEmpty && lineCountsByCommit.Count == 0)
        {
            return [];
        }

        var fromHistory = history
            .Select((commit, index) => BlameTimelineItemViewModel.FromCommit(commit, lineCountsByCommit.GetValueOrDefault(commit.Id), index))
            .ToImmutableArray();
        if (!fromHistory.IsDefaultOrEmpty)
        {
            return fromHistory;
        }

        return lineCountsByCommit
            .OrderByDescending(pair => pair.Value)
            .Select((pair, index) => BlameTimelineItemViewModel.FromBlameOnly(pair.Key, pair.Value, index))
            .ToImmutableArray();
    }
}

public sealed record BlameCommitNodeViewModel(
    string CommitId,
    string ShortId,
    string Author,
    DateTimeOffset? AuthorTime,
    string TimeText,
    string Summary,
    string LineRangeText,
    int LineCount,
    string LineCountText,
    string CoverageText,
    double CoverageWidth,
    SolidColorBrush AccentBrush,
    SolidColorBrush SoftBrush)
{
    public static BlameCommitNodeViewModel FromLines(string commitId, ImmutableArray<GitBlameLine> lines, int totalLines, int index)
    {
        var first = lines[0];
        var lineCount = lines.Length;
        var coverage = totalLines <= 0 ? 0 : (double)lineCount / totalLines;
        var accent = BlameInsightBrushes.GetBrush(index);
        return new BlameCommitNodeViewModel(
            commitId,
            Shorten(commitId),
            first.Author,
            first.AuthorTime,
            FormatTimestamp(first.AuthorTime),
            string.IsNullOrWhiteSpace(first.Summary) ? "No commit summary" : first.Summary,
            FormatLineRanges(lines.Select(line => line.LineNumber).Order().ToArray()),
            lineCount,
            lineCount == 1 ? "1 line" : $"{lineCount:N0} lines",
            $"{coverage:P0} of file",
            Math.Clamp(28 + coverage * 260, 28, 288),
            accent,
            BlameInsightBrushes.GetSoftBrush(accent));
    }

    private static string FormatLineRanges(IReadOnlyList<int> lineNumbers)
    {
        if (lineNumbers.Count == 0)
        {
            return "lines unknown";
        }

        var ranges = new List<string>();
        var start = lineNumbers[0];
        var previous = start;
        for (var index = 1; index < lineNumbers.Count; index++)
        {
            var line = lineNumbers[index];
            if (line == previous + 1)
            {
                previous = line;
                continue;
            }

            ranges.Add(start == previous ? start.ToString() : $"{start}-{previous}");
            start = previous = line;
        }

        ranges.Add(start == previous ? start.ToString() : $"{start}-{previous}");
        return $"lines {string.Join(", ", ranges.Take(6))}{(ranges.Count > 6 ? ", ..." : string.Empty)}";
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp is null
            ? "unknown date"
            : timestamp.Value.LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.CurrentCulture);

    private static string Shorten(string commitId) => commitId.Length <= 10 ? commitId : commitId[..10];
}

public sealed record BlameTimelineItemViewModel(
    string CommitId,
    string ShortId,
    string Subject,
    string Author,
    string TimeText,
    int BlamedLineCount,
    string BlamedLineText,
    double MarkerHeight,
    SolidColorBrush AccentBrush,
    SolidColorBrush SoftBrush)
{
    public Visibility BlamedLineVisibility => BlamedLineCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static BlameTimelineItemViewModel FromCommit(GitCommitInfo commit, int blamedLineCount, int index)
    {
        var accent = BlameInsightBrushes.GetBrush(index);
        return new BlameTimelineItemViewModel(
            commit.Id,
            commit.ShortId,
            string.IsNullOrWhiteSpace(commit.Subject) ? "No commit subject" : commit.Subject,
            string.IsNullOrWhiteSpace(commit.Author) ? "unknown" : commit.Author,
            FormatTimestamp(commit.AuthorTime),
            blamedLineCount,
            blamedLineCount == 1 ? "1 blamed line" : $"{blamedLineCount:N0} blamed lines",
            blamedLineCount == 0 ? 24 : Math.Clamp(32 + blamedLineCount * 1.8, 32, 96),
            accent,
            BlameInsightBrushes.GetSoftBrush(accent));
    }

    public static BlameTimelineItemViewModel FromBlameOnly(string commitId, int blamedLineCount, int index)
    {
        var accent = BlameInsightBrushes.GetBrush(index);
        return new BlameTimelineItemViewModel(
            commitId,
            commitId.Length <= 10 ? commitId : commitId[..10],
            "Commit not loaded in file history",
            "unknown",
            "unknown date",
            blamedLineCount,
            blamedLineCount == 1 ? "1 blamed line" : $"{blamedLineCount:N0} blamed lines",
            Math.Clamp(32 + blamedLineCount * 1.8, 32, 96),
            accent,
            BlameInsightBrushes.GetSoftBrush(accent));
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp is null
            ? "unknown date"
            : timestamp.Value.LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.CurrentCulture);
}

internal static class BlameInsightBrushes
{
    private static readonly Color[] Colors =
    [
        Color.FromArgb(255, 0, 122, 204),
        Color.FromArgb(255, 26, 127, 55),
        Color.FromArgb(255, 154, 103, 0),
        Color.FromArgb(255, 168, 85, 247),
        Color.FromArgb(255, 20, 184, 166),
        Color.FromArgb(255, 236, 72, 153),
        Color.FromArgb(255, 100, 116, 139)
    ];

    public static SolidColorBrush GetBrush(int index) => new(Colors[Math.Abs(index) % Colors.Length]);

    public static SolidColorBrush GetSoftBrush(SolidColorBrush brush)
    {
        var color = brush.Color;
        return new SolidColorBrush(Color.FromArgb(34, color.R, color.G, color.B));
    }
}

public sealed partial class GitHistoryTimelineViewModel : ObservableObject
{
    private readonly HashSet<string> seenCommitIds = new(StringComparer.Ordinal);
    private readonly GitHistoryLaneLayout graphLayout = new();
    private int nextSkip;

    public GitHistoryTimelineViewModel(
        string title,
        string referenceText,
        string rangeText,
        GitHistoryRequest request)
    {
        Title = title;
        ReferenceText = referenceText;
        RangeText = rangeText;
        Request = request with { Skip = 0 };
        nextSkip = Math.Max(0, request.Skip);
    }

    public string Title { get; }

    public string ReferenceText { get; }

    public string RangeText { get; }

    public GitHistoryRequest Request { get; }

    public ObservableCollection<GitHistoryItemViewModel> Commits { get; } = [];

    public int LoadedCount => Commits.Count;

    public string CountText => HasMore
        ? $"{LoadedCount:N0}+ commits"
        : LoadedCount == 1
            ? "1 commit"
            : $"{LoadedCount:N0} commits";

    public string FooterText => IsLoadingMore
        ? "Loading more commits..."
        : HasMore
            ? "Scroll to load more commits"
            : "End of history";

    public Visibility FooterVisibility => IsLoadingMore || HasMore ? Visibility.Visible : Visibility.Collapsed;

    public GitHistoryRequest NextPageRequest => Request with { Skip = nextSkip };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    [NotifyPropertyChangedFor(nameof(FooterVisibility))]
    private bool hasMore = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    [NotifyPropertyChangedFor(nameof(FooterVisibility))]
    private bool isLoadingMore;

    public void AppendSnapshot(GitHistorySnapshot snapshot)
    {
        foreach (var commit in snapshot.Commits)
        {
            if (seenCommitIds.Add(commit.Id))
            {
                Commits.Add(GitHistoryItemViewModel.FromCommit(commit, graphLayout.CreateRow(commit)));
            }
        }

        nextSkip = Math.Max(nextSkip, snapshot.Request.Skip + snapshot.Commits.Length);
        HasMore = snapshot.HasMore;
        OnPropertyChanged(nameof(LoadedCount));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(FooterVisibility));
    }

    public static GitHistoryTimelineViewModel Create(string title, GitHistoryRequest request)
    {
        var range = string.IsNullOrWhiteSpace(request.BaseRef)
            ? request.HeadRef
            : $"{request.BaseRef}..{request.HeadRef}";
        return new GitHistoryTimelineViewModel(title, request.HeadRef, range, request);
    }
}

public sealed record GitHistoryItemViewModel(
    string CommitId,
    string ShortId,
    string Subject,
    string Author,
    string AuthorTimeText,
    string Decorations,
    ImmutableArray<GitHistoryRefBadgeViewModel> RefBadges,
    string ParentText,
    double GraphWidth,
    double RowHeight,
    ImmutableArray<GitHistoryGraphPathViewModel> GraphPaths,
    double DotLeft,
    double DotTop,
    double DotSize,
    SolidColorBrush DotBrush,
    SolidColorBrush DotStroke,
    double RowMinHeight,
    string MergeText)
{
    public Visibility MergeVisibility => string.IsNullOrWhiteSpace(MergeText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DecorationsVisibility => RefBadges.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string MetaText => $"{Author} | {AuthorTimeText} | {ParentText}";

    public static GitHistoryItemViewModel FromCommit(GitCommitInfo commit, GitHistoryLaneRow graph)
    {
        var parentText = commit.ParentIds.Length == 0
            ? "root commit"
            : commit.ParentIds.Length == 1
                ? $"parent {Shorten(commit.ParentIds[0])}"
                : $"{commit.ParentIds.Length:N0} parents";
        return new GitHistoryItemViewModel(
            commit.Id,
            commit.ShortId,
            commit.Subject,
            string.IsNullOrWhiteSpace(commit.Author) ? "unknown" : commit.Author,
            FormatTimestamp(commit.AuthorTime),
            commit.Decorations,
            GitHistoryRefBadgeViewModel.FromDecorations(commit.Decorations),
            parentText,
            graph.Width,
            graph.Height,
            graph.Paths.Select(GitHistoryGraphPathViewModel.FromPath).ToImmutableArray(),
            graph.DotLeft,
            graph.DotTop,
            graph.DotSize,
            GitHistoryBrushes.GetLaneBrush(graph.DotColorIndex),
            GitHistoryBrushes.DotStrokeBrush,
            graph.Height,
            commit.IsMerge ? "merge" : string.Empty);
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp is null
            ? "unknown date"
            : timestamp.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    private static string Shorten(string commitId) => commitId.Length <= 12 ? commitId : commitId[..12];
}

public sealed record GitHistoryGraphPathViewModel(Geometry Data, SolidColorBrush Stroke, double StrokeThickness, double Opacity)
{
    public static GitHistoryGraphPathViewModel FromPath(GitHistoryLanePath path) =>
        new(CreatePath(path), GitHistoryBrushes.GetLaneBrush(path.ColorIndex), path.StrokeThickness, path.Opacity);

    private static PathGeometry CreatePath(GitHistoryLanePath path)
    {
        var points = path.Points.IsDefaultOrEmpty
            ? ImmutableArray.Create(new Point2(0, 0), new Point2(0, 0))
            : path.Points;
        var figure = new PathFigure
        {
            StartPoint = ToPoint(points[0]),
            IsClosed = false,
            IsFilled = false
        };
        if (path.IsCurve && points.Length >= 4)
        {
            figure.Segments.Add(new BezierSegment
            {
                Point1 = ToPoint(points[1]),
                Point2 = ToPoint(points[2]),
                Point3 = ToPoint(points[3])
            });
        }
        else
        {
            figure.Segments.Add(new LineSegment { Point = ToPoint(points[^1]) });
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Point ToPoint(Point2 point) => new(point.X, point.Y);
}

public sealed record GitHistoryRefBadgeViewModel(string Text, SolidColorBrush Foreground, SolidColorBrush Background, SolidColorBrush Border)
{
    public static ImmutableArray<GitHistoryRefBadgeViewModel> FromDecorations(string decorations)
    {
        if (string.IsNullOrWhiteSpace(decorations))
        {
            return [];
        }

        return decorations
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(12)
            .Select(Create)
            .ToImmutableArray();
    }

    private static GitHistoryRefBadgeViewModel Create(string text)
    {
        var foreground = text.StartsWith("tag: ", StringComparison.OrdinalIgnoreCase)
            ? GitHistoryBrushes.TagBrush
            : text.Contains("HEAD", StringComparison.OrdinalIgnoreCase)
                ? GitHistoryBrushes.HeadBrush
                : GitHistoryBrushes.BranchBrush;
        return new GitHistoryRefBadgeViewModel(text, foreground, GitHistoryBrushes.GetSoftBrush(foreground), foreground);
    }
}

internal static class GitHistoryBrushes
{
    private static readonly Color[] LaneColors =
    [
        Color.FromArgb(255, 0, 122, 204),
        Color.FromArgb(255, 249, 115, 22),
        Color.FromArgb(255, 34, 197, 94),
        Color.FromArgb(255, 168, 85, 247),
        Color.FromArgb(255, 236, 72, 153),
        Color.FromArgb(255, 20, 184, 166),
        Color.FromArgb(255, 234, 179, 8),
        Color.FromArgb(255, 100, 116, 139)
    ];

    public static SolidColorBrush Transparent { get; } = new(Color.FromArgb(0, 0, 0, 0));

    public static SolidColorBrush DotStrokeBrush { get; } = new(Color.FromArgb(255, 255, 255, 255));

    public static SolidColorBrush HeadBrush { get; } = new(Color.FromArgb(255, 0, 122, 204));

    public static SolidColorBrush BranchBrush { get; } = new(Color.FromArgb(255, 26, 127, 55));

    public static SolidColorBrush TagBrush { get; } = new(Color.FromArgb(255, 154, 103, 0));

    public static SolidColorBrush GetLaneBrush(int lane) => new(LaneColors[Math.Abs(lane) % LaneColors.Length]);

    public static SolidColorBrush GetSoftBrush(SolidColorBrush brush)
    {
        var color = brush.Color;
        return new SolidColorBrush(Color.FromArgb(32, color.R, color.G, color.B));
    }
}

public sealed partial class FileDiffTabViewModel : ObservableObject
{
    public FileDiffTabViewModel(
        string documentId,
        string path,
        string language,
        DiffFileStatus status,
        ImmutableArray<DiffLine> diffOnlyLines,
        ImmutableArray<DiffLine> fullDiffLines,
        ImmutableArray<FileDiffLineViewModel> diffLines,
        string fullText,
        ImmutableArray<DiffLine> fullFileLines,
        ImmutableArray<DiffLine> annotatedFullFileLines,
        ImmutableArray<CodeFoldRegion> foldRegions,
        SemanticDocumentInsight semanticInsight,
        FileDiffDisplayMode displayMode,
        string? repositoryPath = null,
        ICodeCompletionProvider? completionProvider = null)
    {
        DocumentId = documentId;
        Path = path;
        Language = language;
        Status = status;
        DiffOnlyLines = diffOnlyLines;
        FullDiffLines = fullDiffLines;
        DiffLines = diffLines;
        FullFileLines = fullFileLines;
        AnnotatedFullFileLines = annotatedFullFileLines;
        PlainDiffOnlyLines = StripTokenSpans(diffOnlyLines);
        PlainFullDiffLines = StripTokenSpans(fullDiffLines);
        PlainFullFileLines = StripTokenSpans(fullFileLines);
        PlainAnnotatedFullFileLines = StripTokenSpans(annotatedFullFileLines);
        FoldRegions = foldRegions;
        this.semanticInsight = semanticInsight;
        this.fullText = fullText;
        this.displayMode = displayMode;
        RepositoryPath = repositoryPath ?? string.Empty;
        CompletionProvider = completionProvider;
        IsTokenizationEnabled = true;
    }

    public string DocumentId { get; }

    public string Path { get; }

    public string Language { get; }

    public string RepositoryPath { get; }

    public ICodeCompletionProvider? CompletionProvider { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentDiffOnlyLines))]
    [NotifyPropertyChangedFor(nameof(CurrentFullFileLines))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(FullFileHeader))]
    [NotifyPropertyChangedFor(nameof(DiffOnlyHeader))]
    [NotifyPropertyChangedFor(nameof(RefreshKey))]
    private bool isTokenizationEnabled = true;

    public string LineCommentPrefix => GetLineCommentTokens(Language, Path).Prefix;

    public string LineCommentSuffix => GetLineCommentTokens(Language, Path).Suffix;

    public DiffFileStatus Status { get; }

    public ImmutableArray<DiffLine> DiffOnlyLines { get; }

    public ImmutableArray<DiffLine> FullDiffLines { get; }

    public ImmutableArray<FileDiffLineViewModel> DiffLines { get; }

    public ImmutableArray<DiffLine> FullFileLines { get; }

    public ImmutableArray<DiffLine> AnnotatedFullFileLines { get; }

    private ImmutableArray<DiffLine> PlainDiffOnlyLines { get; }

    private ImmutableArray<DiffLine> PlainFullDiffLines { get; }

    private ImmutableArray<DiffLine> PlainFullFileLines { get; }

    private ImmutableArray<DiffLine> PlainAnnotatedFullFileLines { get; }

    public ImmutableArray<CodeFoldRegion> FoldRegions { get; }

    public ImmutableArray<DiffLine> CurrentDiffOnlyLines => DiffScopeMode == FileDiffScopeMode.FullFileDiff
        ? GetDisplayLines(FullDiffLines, PlainFullDiffLines)
        : GetDisplayLines(DiffOnlyLines, PlainDiffOnlyLines);

    public ImmutableArray<DiffLine> CurrentFullFileLines => IsDiffAnnotationEnabled
        ? GetDisplayLines(AnnotatedFullFileLines, PlainAnnotatedFullFileLines)
        : GetDisplayLines(FullFileLines, PlainFullFileLines);

    public ImmutableArray<SemanticLineInsight> SemanticLineInsights => SemanticInsight.Lines;

    public bool HasSemanticInsights => SemanticInsight.HasInsights;

    public string SemanticSummaryText => SemanticInsight.SummaryText;

    public string SummaryText => HasSemanticInsights
        ? $"{Status} | {Language} | {CurrentDiffOnlyLines.Length:N0} diff lines | {SemanticSummaryText}"
        : $"{Status} | {Language} | {CurrentDiffOnlyLines.Length:N0} diff lines";

    public string StatusText => $"{Path} | {SummaryText}";

    public Visibility DiffOnlyVisibility => DisplayMode == FileDiffDisplayMode.DiffOnly ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FullFileVisibility => DisplayMode == FileDiffDisplayMode.FullFile ? Visibility.Visible : Visibility.Collapsed;

    public bool IsDiffOnlyMode => DisplayMode == FileDiffDisplayMode.DiffOnly;

    public bool IsFullFileMode => DisplayMode == FileDiffDisplayMode.FullFile;

    public bool IsChangesDiffScopeMode => DiffScopeMode == FileDiffScopeMode.Changes;

    public bool IsFullFileDiffScopeMode => DiffScopeMode == FileDiffScopeMode.FullFileDiff;

    public string FullFileHeader => string.IsNullOrWhiteSpace(FullText)
        ? "Full file content unavailable"
        : $"{Path} | {DisplayFullFileLineCount:N0} lines | {FoldRegions.Length:N0} fold regions | diff annotations {(IsDiffAnnotationEnabled ? "on" : "off")} | editing {(IsEditingEnabled ? "on" : "off")} | {SemanticSummaryText}";

    public string DiffOnlyHeader => DiffScopeMode == FileDiffScopeMode.FullFileDiff
        ? $"{Path} | full file diff | {CurrentDiffOnlyLines.Length:N0} lines | {SemanticSummaryText}"
        : $"{Path} | changed hunks | {CurrentDiffOnlyLines.Length:N0} lines | {SemanticSummaryText}";

    public string RefreshKey => $"{DisplayMode}:{DiffScopeMode}:{IsDiffAnnotationEnabled}:{IsTokenizationEnabled}:{SemanticLineInsights.Length}";

    public ImmutableArray<double> CodeFontSizeOptions { get; } = [10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28];

    public string CodeFontSizeText => $"{CodeFontSize:0} px";

    public bool CanDecreaseCodeFontSize => CodeFontSize > CodeFontSizeOptions[0];

    public bool CanIncreaseCodeFontSize => CodeFontSize < CodeFontSizeOptions[^1];

    private int DisplayFullFileLineCount =>
        string.IsNullOrEmpty(FullText) ? FullFileLines.Length : CountLines(FullText);

    private static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 1;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private ImmutableArray<DiffLine> GetDisplayLines(ImmutableArray<DiffLine> tokenizedLines, ImmutableArray<DiffLine> plainLines) =>
        IsTokenizationEnabled ? tokenizedLines : plainLines;

    private static ImmutableArray<DiffLine> StripTokenSpans(ImmutableArray<DiffLine> lines)
    {
        if (lines.IsDefaultOrEmpty)
        {
            return lines;
        }

        var builder = ImmutableArray.CreateBuilder<DiffLine>(lines.Length);
        foreach (var line in lines)
        {
            builder.Add(line.Tokens.IsDefaultOrEmpty ? line : line with { Tokens = ImmutableArray<TokenSpan>.Empty });
        }

        return builder.ToImmutable();
    }

    private static (string Prefix, string Suffix) GetLineCommentTokens(string language, string path)
    {
        var normalizedLanguage = (language ?? string.Empty).Trim().ToLowerInvariant();
        var extension = System.IO.Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
        return normalizedLanguage switch
        {
            "xml" or "xaml" or "html" or "svg" or "markdown" when extension is ".xaml" or ".xml" or ".html" or ".htm" or ".svg" or ".md" or ".markdown" => ("<!-- ", " -->"),
            "css" or "scss" or "sass" or "less" => ("/* ", " */"),
            "python" or "py" or "ruby" or "rb" or "shell" or "bash" or "sh" or "yaml" or "yml" or "toml" or "powershell" => ("# ", string.Empty),
            "sql" or "lua" or "haskell" => ("-- ", string.Empty),
            _ => extension switch
            {
                ".xaml" or ".xml" or ".html" or ".htm" or ".svg" or ".md" or ".markdown" => ("<!-- ", " -->"),
                ".css" or ".scss" or ".sass" or ".less" => ("/* ", " */"),
                ".py" or ".rb" or ".sh" or ".bash" or ".zsh" or ".yaml" or ".yml" or ".toml" or ".ps1" => ("# ", string.Empty),
                ".sql" or ".lua" or ".hs" => ("-- ", string.Empty),
                _ => ("// ", string.Empty)
            }
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffOnlyVisibility))]
    [NotifyPropertyChangedFor(nameof(FullFileVisibility))]
    [NotifyPropertyChangedFor(nameof(IsDiffOnlyMode))]
    [NotifyPropertyChangedFor(nameof(IsFullFileMode))]
    [NotifyPropertyChangedFor(nameof(RefreshKey))]
    private FileDiffDisplayMode displayMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentDiffOnlyLines))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(DiffOnlyHeader))]
    [NotifyPropertyChangedFor(nameof(IsChangesDiffScopeMode))]
    [NotifyPropertyChangedFor(nameof(IsFullFileDiffScopeMode))]
    [NotifyPropertyChangedFor(nameof(RefreshKey))]
    private FileDiffScopeMode diffScopeMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentFullFileLines))]
    [NotifyPropertyChangedFor(nameof(FullFileHeader))]
    [NotifyPropertyChangedFor(nameof(RefreshKey))]
    private bool isDiffAnnotationEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullFileHeader))]
    private bool isEditingEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullFileHeader))]
    private string fullText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SemanticLineInsights))]
    [NotifyPropertyChangedFor(nameof(HasSemanticInsights))]
    [NotifyPropertyChangedFor(nameof(SemanticSummaryText))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(FullFileHeader))]
    [NotifyPropertyChangedFor(nameof(DiffOnlyHeader))]
    [NotifyPropertyChangedFor(nameof(RefreshKey))]
    private SemanticDocumentInsight semanticInsight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeFontSizeText))]
    [NotifyPropertyChangedFor(nameof(CanDecreaseCodeFontSize))]
    [NotifyPropertyChangedFor(nameof(CanIncreaseCodeFontSize))]
    private double codeFontSize = 15;

    public void SetDisplayMode(FileDiffDisplayMode mode) => DisplayMode = mode;

    public void SetDiffScopeMode(FileDiffScopeMode mode) => DiffScopeMode = mode;

    public void SetDiffAnnotationVisibility(bool isEnabled) => IsDiffAnnotationEnabled = isEnabled;

    public void SetEditingEnabled(bool isEnabled) => IsEditingEnabled = isEnabled;

    public void SetSemanticInsight(SemanticDocumentInsight insight) => SemanticInsight = insight;

    public void SetCompletionProvider(ICodeCompletionProvider completionProvider)
    {
        CompletionProvider = completionProvider;
        OnPropertyChanged(nameof(CompletionProvider));
    }

    public void SetTokenizationEnabled(bool isEnabled) => IsTokenizationEnabled = isEnabled;

    public SemanticLineInsight? FindSemanticLineInsight(int? lineNumber)
    {
        if (lineNumber is null || SemanticLineInsights.IsDefaultOrEmpty)
        {
            return null;
        }

        return SemanticLineInsights.FirstOrDefault(insight => insight.LineNumber == lineNumber.Value);
    }

    public void IncreaseCodeFontSize() => SetCodeFontSize(CodeFontSize + 1);

    public void DecreaseCodeFontSize() => SetCodeFontSize(CodeFontSize - 1);

    public void SetCodeFontSize(double value)
    {
        if (CodeFontSizeOptions.Length == 0)
        {
            CodeFontSize = value;
            return;
        }

        var minimum = CodeFontSizeOptions[0];
        var maximum = CodeFontSizeOptions[^1];
        CodeFontSize = Math.Clamp(value, minimum, maximum);
    }

    public static FileDiffTabViewModel FromDocument(
        DiffDocumentSnapshot document,
        DiffDocumentSnapshot fullFileDocument,
        string fullText,
        ImmutableArray<CodeFoldRegion> foldRegions,
        SemanticDocumentInsight semanticInsight,
        FileDiffDisplayMode displayMode,
        string? repositoryPath = null,
        ICodeCompletionProvider? completionProvider = null)
    {
        var view = new FileDiffDocumentBuilder().Build(document, fullFileDocument, fullText, foldRegions);
        return new FileDiffTabViewModel(
            view.DiffDocument.Id.Value,
            view.DiffDocument.Metadata.Path,
            view.DiffDocument.Metadata.Language,
            view.DiffDocument.Metadata.Status,
            view.ChangedHunkLines,
            view.FullDiffLines,
            view.ChangedHunkLines.Select(FileDiffLineViewModel.FromLine).ToImmutableArray(),
            view.FullText,
            view.FullFileDocument.Lines,
            view.AnnotatedFullFileLines,
            view.FoldRegions,
            semanticInsight,
            displayMode,
            repositoryPath,
            completionProvider);
    }
}

public sealed record FileDiffLineViewModel(
    string OldLineNumberText,
    string NewLineNumberText,
    string Marker,
    string Text,
    string KindText)
{
    public static FileDiffLineViewModel FromLine(DiffLine line) => new(
        line.OldLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        line.NewLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        MarkerFor(line.Kind),
        line.Text,
        line.Kind.ToString());

    private static string MarkerFor(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Deleted => "-",
        DiffLineKind.Ignored => "~",
        DiffLineKind.Moved => ">",
        DiffLineKind.Conflict => "!",
        DiffLineKind.Metadata => "@",
        DiffLineKind.Imaginary => "...",
        _ => string.Empty
    };
}
