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
using SemanticDiff.Workbench.Symbols;
using SemanticDiff.Workbench.Workspace;
using Windows.UI;

namespace SemanticDiff.App.ViewModels;

public sealed partial class MainViewModel
{
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

            var isNewRepositoryRoot = !string.Equals(currentRepositoryPath, repositoryRoot, StringComparison.Ordinal);
            currentRepositoryPath = repositoryRoot;
            if (isNewRepositoryRoot)
            {
                InvalidateWorkspaceExplorerCache();
            }

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
        var operation = BeginBackgroundOperation(
            $"Loading {FormatReviewRequestState(appState.ReviewRequestState)} branches and review requests",
            cancellationToken,
            drivesGlobalProgress: false);
        try
        {
            if (!IsCurrentRepositoryRequest(repositoryRequestId))
            {
                CompleteOperation(operation, "Reference load skipped");
                return;
            }

            ReferenceSelectorStatusText = $"Loading {FormatReviewRequestState(appState.ReviewRequestState)} review requests";
            ReportProgress(operation, 0.15, "Discovering Git remotes and branches");
            var snapshot = await gitReferenceDiscoveryService.GetReferencesAsync(repositoryPath, operation.Token, appState.ReviewRequestState);
            if (!IsCurrentRepositoryRequest(repositoryRequestId))
            {
                CompleteOperation(operation, "Reference load skipped");
                return;
            }

            ReportProgress(operation, 0.82, "Building Git reference tree");
            gitReferenceBrowser.SetReferences(
                snapshot.Branches.Select(GitBranchOptionViewModel.FromBranch).ToImmutableArray(),
                snapshot.PullRequests.Select(GitPullRequestOptionViewModel.FromPullRequest).ToImmutableArray(),
                snapshot.ReviewRequestKind,
                snapshot.SupportsReviewRequests);
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

            CompleteOperation(operation, snapshot.StatusMessage);
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(operation, "Reference load canceled");
        }
        catch (Exception exception)
        {
            if (IsCurrentRepositoryRequest(repositoryRequestId))
            {
                ReferenceSelectorStatusText = "Refs unavailable";
                AddDiagnostic("Warning", $"Reference discovery failed: {exception.Message}");
            }

            CompleteOperation(operation, "Reference load failed");
        }
    }

    private void ClearReferenceOptions(string status)
    {
        gitReferenceBrowser.Clear();
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
        var referenceView = gitReferenceBrowser.Apply(GitReferenceSearchText, FormatReviewRequestState(appState.ReviewRequestState));
        BranchOptions = referenceView.Branches;
        PullRequestOptions = referenceView.ReviewRequests;
        HasBranchOptions = gitReferenceBrowser.HasBranches;
        HasPullRequestOptions = gitReferenceBrowser.HasReviewRequests;
        BuildGitReferenceTree(referenceView);
        ApplyReferenceSelectionsToPresentation();
    }

    private void ClearReviewDiscussion(string status)
    {
        reviewWorkflow.Clear(status);
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
        var result = reviewWorkflow.ApplyFilter(ReviewSearchText, SelectedReviewThreadItem);
        ReviewThreadItems = result.Items;
        ReviewThreadCountText = result.CountText;
        SelectedReviewThreadItem = result.SelectedItem;
    }

    public void ToggleGitReferenceNode(GitReferenceTreeItemViewModel? node)
    {
        if (node is null || !node.HasChildren)
        {
            return;
        }

        gitReferenceBrowser.ToggleNode(node.Id);
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
        var tabOperation = BeginTabOperation(
            tab,
            $"Loading workspace for {branch.ReferenceName}",
            drivesGlobalProgress: false);
        try
        {
            var state = await LoadGraphWorkspaceTabStateAsync(request, branch.ReferenceName, null, tabOperation);
            tab.GraphState = state;
            tab.StatusText = state.StatusText;
            if (ReferenceEquals(SelectedWorkspaceTab, tab))
            {
                RestoreGraphWorkspaceState(tab);
            }

            AddDiagnostic("Info", $"Opened workspace tab for {branch.ReferenceName}");
            CompleteOperation(tabOperation, "Workspace ready");
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(tabOperation, "Workspace load canceled");
            throw;
        }
        catch
        {
            CompleteOperation(tabOperation, "Workspace load failed");
            throw;
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
        var tabOperation = BeginTabOperation(
            tab,
            $"Loading workspace for {pullRequest.KindText} {pullRequest.NumberText}",
            drivesGlobalProgress: false);
        try
        {
            ReportProgress(tabOperation, 0.08, $"Preparing {pullRequest.KindText} head");
            var headRef = await gitReferenceDiscoveryService.EnsurePullRequestHeadAsync(currentRepositoryPath!, reviewRequest, tabOperation.Token);
            if (string.IsNullOrWhiteSpace(headRef))
            {
                tab.StatusText = $"{pullRequest.KindText} workspace unavailable";
                AddDiagnostic("Warning", $"Unable to fetch {pullRequest.KindText} {pullRequest.NumberText}");
                CompleteOperation(tabOperation, "Workspace unavailable");
                return;
            }

            var loadRequest = request with { HeadRef = headRef };
            tab.GraphRequest = loadRequest;
            var state = await LoadGraphWorkspaceTabStateAsync(
                loadRequest,
                $"{pullRequest.KindText} {pullRequest.NumberText}",
                reviewRequest with { HeadRefName = headRef },
                tabOperation);
            tab.GraphState = state;
            tab.StatusText = state.StatusText;
            if (ReferenceEquals(SelectedWorkspaceTab, tab))
            {
                RestoreGraphWorkspaceState(tab);
            }

            AddDiagnostic("Info", $"Opened workspace tab for {pullRequest.KindText} {pullRequest.NumberText}");
            CompleteOperation(tabOperation, "Workspace ready");
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(tabOperation, "Workspace load canceled");
            throw;
        }
        catch
        {
            CompleteOperation(tabOperation, "Workspace load failed");
            throw;
        }
    }

    private async Task<GraphWorkspaceState> LoadGraphWorkspaceTabStateAsync(
        GitDiffRequest request,
        string statusLabel,
        GitPullRequestInfo? reviewRequest,
        CancellationTokenSource operation)
    {
        ReportProgress(operation, 0.14, $"Loading Git diff for {statusLabel}");
        var diffContextMode = appState.DiffContextMode;
        var reviewMode = appState.ReviewMode;
        var collapseUnchangedContext = appState.CollapseUnchangedContext;
        var layoutMode = appState.LayoutMode;
        var loadedDiff = await Task.Run(async () => await repositoryDiffLoader.LoadAsync(
            new RepositoryDiffLoadRequest(
                request.RepositoryPath,
                request.Scope,
                request.BaseRef,
                request.HeadRef,
                diffContextMode,
                reviewMode,
                collapseUnchangedContext),
            operation.Token).ConfigureAwait(false), operation.Token);

        var repositoryName = Path.GetFileName(request.RepositoryPath);
        var statusPrefix = $"{repositoryName} | {loadedDiff.GitSnapshot.Files.Length:N0} {FormatDiffScope(request.Scope)} changes | {FormatDiffContextMode(diffContextMode)} | {FormatReviewMode(reviewMode)} | {FormatReferenceText(loadedDiff.Request, loadedDiff.GitSnapshot.DefaultBranch)}";

        if (loadedDiff.Documents.IsDefaultOrEmpty)
        {
            var emptyScene = CreateScene([], SemanticGraph.Empty, null);
            return new GraphWorkspaceState(
                request.RepositoryPath,
                loadedDiff.Request,
                reviewRequest,
                repositoryName,
                statusPrefix,
                $"{statusPrefix} | no changes",
                statusPrefix,
                true,
                [],
                SemanticGraph.Empty,
                loadedDiff.GitSnapshot,
                null,
                ImmutableHashSet<DiffDocumentId>.Empty,
                emptyScene,
                null,
                [],
                [],
                reviewRequest?.Kind ?? gitReferenceBrowser.ReviewRequestKind);
        }

        ReportProgress(operation, 0.34, "Tokenizing workspace tab");
        var tokenizationProgress = new Progress<(double Value, string Message)>(update =>
            ReportProgress(operation, 0.34 + update.Value * 0.24, update.Message));
        var tokenizedDocuments = await TokenizeAsync(loadedDiff.Documents, operation.Token, tokenizationProgress);

        var initialSemanticAnalysisMode = GetInitialSemanticAnalysisMode(appState.SemanticAnalysisMode);
        ReportProgress(operation, 0.62, $"Analyzing semantics ({FormatSemanticAnalysisMode(initialSemanticAnalysisMode)})");
        var semanticGraph = await AnalyzeSemanticsAsync(
            request.RepositoryPath,
            loadedDiff.GitSnapshot,
            tokenizedDocuments,
            initialSemanticAnalysisMode,
            operation.Token);

        ReportProgress(operation, 0.86, "Running semantic graph layout");
        var layout = await LayoutDocumentsForWorkspaceTabAsync(
            tokenizedDocuments,
            semanticGraph,
            layoutMode,
            operation.Token);
        var scene = CreateScene(tokenizedDocuments, semanticGraph, layout);
        var impactSummary = new SemanticImpactAnalyzer().Analyze(tokenizedDocuments, semanticGraph);
        var statusText = $"{statusPrefix} | {tokenizedDocuments.Length:N0} nodes | {semanticGraph.Edges.Length:N0} semantic edges | {FormatImpactStatus(impactSummary)} | workspace ready";

        return new GraphWorkspaceState(
            request.RepositoryPath,
            loadedDiff.Request,
            reviewRequest,
            repositoryName,
            statusPrefix,
            statusText,
            statusPrefix,
            true,
            tokenizedDocuments,
            semanticGraph,
            loadedDiff.GitSnapshot,
            layout,
            ImmutableHashSet<DiffDocumentId>.Empty,
            scene,
            null,
            [],
            [],
            reviewRequest?.Kind ?? gitReferenceBrowser.ReviewRequestKind);
    }

    private static Task<GraphLayoutResult> LayoutDocumentsForWorkspaceTabAsync(
        ImmutableArray<DiffDocumentSnapshot> documents,
        SemanticGraph semanticGraph,
        GraphLayoutMode layoutMode,
        CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var layoutEngine = new MsaglGraphLayoutEngine();
            return await layoutEngine.LayoutAsync(
                new GraphLayoutRequest(
                    documents,
                    semanticGraph,
                    new Size2(620, 420),
                    default,
                    ImmutableHashSet<DiffDocumentId>.Empty,
                    layoutMode),
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

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
        var operation = BeginTabOperation(tab, $"Loading history for {branch.ReferenceName}");
        try
        {
            await LoadGitHistoryPageAsync(tab, operation.Token, operation);
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
        var operation = BeginTabOperation(tab, $"Loading history for {pullRequest.KindText} {pullRequest.NumberText}");
        try
        {
            tab.IsLoading = true;
            tab.StatusText = $"Preparing {pullRequest.KindText} head";
            ReportProgress(operation, 0.12, $"Preparing {pullRequest.KindText} head");
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
            await LoadGitHistoryPageAsync(tab, operation.Token, operation);
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

        var operation = BeginTabOperation(tab, $"Loading more history for {history.ReferenceText}");
        try
        {
            await LoadGitHistoryPageAsync(tab, operation.Token, operation);
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

    private async Task LoadGitHistoryPageAsync(
        WorkspaceTabViewModel tab,
        CancellationToken cancellationToken,
        CancellationTokenSource? operation = null)
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
            if (operation is not null)
            {
                ReportProgress(operation, history.LoadedCount == 0 ? 0.25 : 0.45, tab.StatusText);
            }

            var snapshot = await gitHistoryService.GetHistoryAsync(history.NextPageRequest, cancellationToken);
            if (operation is not null)
            {
                ReportProgress(operation, 0.86, "Appending Git history page");
            }

            history.AppendSnapshot(snapshot);
            tab.StatusText = history.CountText;
        }
        finally
        {
            history.IsLoadingMore = false;
            tab.IsLoading = false;
        }
    }

    private void BuildGitReferenceTree()
    {
        BuildGitReferenceTree(gitReferenceBrowser.Apply(GitReferenceSearchText, FormatReviewRequestState(appState.ReviewRequestState)));
    }

    private void BuildGitReferenceTree(GitReferenceBrowserView<GitBranchOptionViewModel, GitPullRequestOptionViewModel> referenceView)
    {
        var forceExpanded = referenceView.ForceExpanded;
        var builder = ImmutableArray.CreateBuilder<GitReferenceTreeItemViewModel>();
        var localBranches = referenceView.Branches
            .Where(branch => !branch.IsRemote)
            .ToImmutableArray();
        var remoteBranchGroups = referenceView.Branches
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
            GitReferenceTreeItemViewModel.Group("git:pull-requests", referenceView.ReviewRequestGroupTitle, referenceView.ReviewRequestGroupDetail, 0, referenceView.ReviewRequests.Length, IsGitGroupExpanded("git:pull-requests", forceExpanded)),
            () =>
            {
                foreach (var pullRequest in referenceView.ReviewRequests)
                {
                    builder.Add(GitReferenceTreeItemViewModel.PullRequestItem(pullRequest, 1, IsSelectedPullRequest(pullRequest)));
                }
            });

        IsUpdatingGitReferenceTree = true;
        try
        {
            GitReferenceTreeItems = builder.ToImmutable();
            GitReferenceCountText = referenceView.CountText;
        }
        finally
        {
            IsUpdatingGitReferenceTree = false;
        }
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
        gitReferenceBrowser.IsExpanded(id, forceExpanded);

    private bool IsSelectedBranch(GitBranchOptionViewModel branch) =>
        appState.DiffScope == GitDiffScope.Branch &&
        appState.SelectedPullRequestNumber is null &&
        string.Equals(branch.ReferenceName, appState.SelectedBranchRef ?? appState.HeadRef, StringComparison.Ordinal);

    private bool IsSelectedPullRequest(GitPullRequestOptionViewModel pullRequest) =>
        appState.DiffScope == GitDiffScope.Branch &&
        appState.SelectedPullRequestNumber == pullRequest.Number;
}
