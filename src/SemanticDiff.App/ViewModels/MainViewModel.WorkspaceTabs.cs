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

        var fallback = WorkspaceTabs.FirstOrDefault(candidate => candidate.Kind == WorkspaceTabKind.Graph)
            ?? WorkspaceTabs.FirstOrDefault()
            ?? WorkspaceTabViewModel.Graph();
        workspaceDocumentManager.Close(tab, fallback, selected => SelectedWorkspaceTab = selected);
    }

    public void SetFileDiffDisplayMode(WorkspaceTabViewModel? tab, FileDiffDisplayMode displayMode)
    {
        tab?.FileDiff?.SetDisplayMode(displayMode);
    }

    public void SetFileDiffScopeMode(WorkspaceTabViewModel? tab, FileDiffScopeMode scopeMode)
    {
        tab?.FileDiff?.SetDiffScopeMode(scopeMode);
    }

    public void SetFileDiffAnnotationVisibility(WorkspaceTabViewModel? tab, bool isEnabled)
    {
        tab?.FileDiff?.SetDiffAnnotationVisibility(isEnabled);
    }

    public void ToggleBlameTimeline(WorkspaceTabViewModel? tab)
    {
        tab?.Blame?.ToggleTimeline();
    }

    public void SetBlameDisplayMode(WorkspaceTabViewModel? tab, BlameDisplayMode displayMode)
    {
        tab?.Blame?.SetDisplayMode(displayMode);
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
            reviewWorkflow.ThreadItems,
            reviewWorkflow.Threads,
            gitReferenceBrowser.ReviewRequestKind);
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
        reviewWorkflow.Restore(
            state.ReviewThreadItems,
            state.ReviewThreads,
            state.ReviewRequest is null
                ? "Select a PR or MR"
                : $"{FormatReviewRequestLabel(state.ReviewRequest)} | {FormatThreadCount(state.ReviewThreadItems.Length)}");
        gitReferenceBrowser.SetReviewRequestKind(state.ReviewRequestKind);

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
        ReviewPanelStatusText = reviewWorkflow.StatusText;
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
        tab.IsLightTheme = IsLightThemeEnabled;
        tab.UseInteractiveLevelOfDetail = UseInteractiveLevelOfDetail;
        workspaceDocumentManager.AddAndSelect(tab, selected => SelectedWorkspaceTab = selected);
    }

    private bool SelectWorkspaceTab(string id) =>
        workspaceDocumentManager.TrySelect(id, tab => SelectedWorkspaceTab = tab);

    private WorkspaceTabViewModel? FindWorkspaceTab(string id) => workspaceDocumentManager.Find(id);

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

    private static bool IsCurrentHeadReference(string? reference) =>
        string.IsNullOrWhiteSpace(reference) || string.Equals(reference.Trim(), "HEAD", StringComparison.Ordinal);

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
}
