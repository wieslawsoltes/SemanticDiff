using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Rendering;
using SemanticDiff.Workbench.Query;
using SemanticDiff.Workbench.Symbols;

namespace SemanticDiff.App.ViewModels;

public sealed partial class MainViewModel
{
    private const int RestoredHistoryPageLimit = 5;

    private CancellationTokenSource? workspaceSessionSaveTokenSource;

    public WorkspaceSessionState CaptureWorkspaceSession()
    {
        CaptureGraphWorkspaceState(SelectedWorkspaceTab);
        var tabs = WorkspaceTabs
            .Select(CaptureWorkspaceTabState)
            .OfType<WorkspaceTabState>()
            .ToArray();

        return new WorkspaceSessionState(
            Version: 1,
            RepositoryPath: currentRepositoryPath ?? appState.RepositoryPath,
            SelectedTabId: SelectedWorkspaceTab?.Id,
            SelectedExplorerDocumentId: selectedExplorerItem?.DocumentId,
            FileSearchText: FileSearchText,
            FileExplorerMode: FileExplorerMode == FileExplorerMode.Workspace
                ? WorkspaceSessionFileExplorerMode.Workspace
                : WorkspaceSessionFileExplorerMode.Diff,
            GitReferenceSearchText: GitReferenceSearchText,
            ReviewSearchText: ReviewSearchText,
            SymbolSearchText: SymbolSearchText,
            Tabs: tabs);
    }

    public async Task RestoreWorkspaceSessionAsync(WorkspaceSessionState state, CancellationToken cancellationToken)
    {
        if (state.EffectiveTabs.Length == 0)
        {
            return;
        }

        isRestoringWorkspaceSession = true;
        try
        {
            await ApplyWorkspaceSessionPanelStateAsync(state, cancellationToken);
            var graphState = state.EffectiveTabs.FirstOrDefault(tab => tab.Kind == WorkspaceSessionTabKind.Graph && string.Equals(tab.Id, "graph", StringComparison.Ordinal));
            if (graphState is not null)
            {
                ApplyCanvasState(Scene, graphState.Canvas);
                IsFullCodeWorkspaceEnabled = Scene.ShowFullFileNodes;
                IsNodeEditingWorkspaceEnabled = Scene.EnableNodeEditing;
                CaptureLayoutState(Scene);
            }

            foreach (var tabState in state.EffectiveTabs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (tabState.Kind == WorkspaceSessionTabKind.Graph && string.Equals(tabState.Id, "graph", StringComparison.Ordinal))
                {
                    continue;
                }

                if (FindWorkspaceTab(tabState.Id) is not null)
                {
                    continue;
                }

                await RestoreWorkspaceTabAsync(tabState, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(state.SelectedTabId))
            {
                SelectWorkspaceTab(state.SelectedTabId);
            }

            RestoreSelectedExplorerItem(state.SelectedExplorerDocumentId);
            AddDiagnostic("Info", $"Restored {state.EffectiveTabs.Length:N0} workspace tab{(state.EffectiveTabs.Length == 1 ? string.Empty : "s")}");
        }
        finally
        {
            isRestoringWorkspaceSession = false;
        }
    }

    private async Task RestorePendingWorkspaceSessionAsync(CancellationToken cancellationToken)
    {
        var state = pendingWorkspaceSessionState;
        if (state is null || state.EffectiveTabs.Length == 0)
        {
            pendingWorkspaceSessionState = null;
            return;
        }

        pendingWorkspaceSessionState = null;
        var operation = BeginBackgroundOperation("Restoring workspace session");
        try
        {
            await RestoreWorkspaceSessionAsync(state, operation.Token);
            CompleteOperation(operation, "Workspace session restored");
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(operation, "Workspace session restore canceled");
        }
        catch (Exception exception)
        {
            AddDiagnostic("Warning", $"Workspace session restore failed: {exception.Message}");
            CompleteOperation(operation, "Workspace session restore failed");
        }
    }

    private WorkspaceTabState? CaptureWorkspaceTabState(WorkspaceTabViewModel tab)
    {
        return tab.Kind switch
        {
            WorkspaceTabKind.Graph => CaptureGraphTabState(tab),
            WorkspaceTabKind.GitHistory => CaptureHistoryTabState(tab),
            WorkspaceTabKind.FileDiff => CaptureFileDiffTabState(tab),
            WorkspaceTabKind.Blame => CaptureBlameTabState(tab),
            WorkspaceTabKind.SymbolGraph => CaptureSymbolGraphTabState(tab),
            WorkspaceTabKind.EditorCanvas => CaptureEditorCanvasTabState(tab),
            WorkspaceTabKind.QueryCanvas => CaptureQueryCanvasTabState(tab),
            WorkspaceTabKind.PatchCompare => CapturePatchCompareTabState(tab),
            _ => null
        };
    }

    private WorkspaceTabState CaptureGraphTabState(WorkspaceTabViewModel tab)
    {
        var scene = ReferenceEquals(tab, SelectedWorkspaceTab) ? Scene : tab.GraphState?.Scene;
        return new WorkspaceTabState(
            tab.Id,
            WorkspaceSessionTabKind.Graph,
            tab.Header,
            tab.DetailText,
            tab.IsClosable,
            tab.StatusText,
            CaptureCanvasState(scene),
            tab.GraphRequest ?? tab.GraphState?.Request,
            tab.GraphBranchReferenceName,
            tab.GraphReviewRequest ?? tab.GraphState?.ReviewRequest);
    }

    private WorkspaceTabState? CaptureHistoryTabState(WorkspaceTabViewModel tab)
    {
        if (tab.History is not { } history)
        {
            return null;
        }

        return new WorkspaceTabState(
            tab.Id,
            WorkspaceSessionTabKind.GitHistory,
            tab.Header,
            tab.DetailText,
            tab.IsClosable,
            tab.StatusText,
            History: new GitHistoryTabState(history.Request, history.LoadedCount));
    }

    private WorkspaceTabState? CaptureFileDiffTabState(WorkspaceTabViewModel tab)
    {
        if (tab.FileDiff is not { } fileDiff)
        {
            return null;
        }

        return new WorkspaceTabState(
            tab.Id,
            WorkspaceSessionTabKind.FileDiff,
            tab.Header,
            tab.DetailText,
            tab.IsClosable,
            tab.StatusText,
            FileDiff: new FileDiffTabState(
                fileDiff.Path,
                fileDiff.DocumentId,
                ToState(fileDiff.DisplayMode),
                ToState(fileDiff.DiffScopeMode),
                fileDiff.IsDiffAnnotationEnabled,
                fileDiff.IsEditingEnabled,
                fileDiff.CodeFontSize,
                fileDiff.IsEditingEnabled ? fileDiff.FullText : null));
    }

    private WorkspaceTabState? CaptureBlameTabState(WorkspaceTabViewModel tab)
    {
        if (tab.Blame is not { } blame)
        {
            return null;
        }

        return new WorkspaceTabState(
            tab.Id,
            WorkspaceSessionTabKind.Blame,
            tab.Header,
            tab.DetailText,
            tab.IsClosable,
            tab.StatusText,
            CaptureCanvasState(blame.ChangeGraphScene),
            Blame: new BlameTabState(
                blame.Path,
                blame.Language,
                blame.DisplayMode == BlameDisplayMode.ChangeGraph ? BlameDisplayState.ChangeGraph : BlameDisplayState.Timeline,
                blame.IsTimelineExpanded));
    }

    private WorkspaceTabState? CaptureSymbolGraphTabState(WorkspaceTabViewModel tab)
    {
        if (tab.SymbolGraph is not { } symbolGraph)
        {
            return null;
        }

        return new WorkspaceTabState(
            tab.Id,
            WorkspaceSessionTabKind.SymbolGraph,
            tab.Header,
            tab.DetailText,
            tab.IsClosable,
            tab.StatusText,
            CaptureCanvasState(symbolGraph.Scene),
            SymbolGraph: new SymbolGraphTabState(
                symbolGraph.SearchText,
                symbolGraph.SelectedScopeOption.Key,
                symbolGraph.SelectedKindOption.Key,
                symbolGraph.SelectedDocumentOption.Key,
                symbolGraph.SelectedEdgeKindOption.Key,
                symbolGraph.SelectedLayoutOption.Mode,
                symbolGraph.SelectedGroupingOption.Mode,
                symbolGraph.SelectedViewModeOption.Mode == SymbolGraphViewMode.FilesAndSymbols
                    ? SymbolGraphDisplayState.FilesAndSymbols
                    : SymbolGraphDisplayState.SymbolsOnly,
                symbolGraph.FocusAnchorId));
    }

    private WorkspaceTabState? CaptureEditorCanvasTabState(WorkspaceTabViewModel tab)
    {
        if (tab.EditorCanvas is not { } editorCanvas)
        {
            return null;
        }

        var textByDocumentId = editorCanvas.Scene.Nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.FullText))
            .ToDictionary(node => node.DiffDocument.Id.Value, node => node.FullText, StringComparer.OrdinalIgnoreCase);
        var documents = editorCanvas.Documents
            .Select(document => new EditorCanvasDocumentState(
                document.Document.Metadata.Path,
                textByDocumentId.TryGetValue(document.Document.Id.Value, out var fullText) ? fullText : document.FullText))
            .ToArray();

        return new WorkspaceTabState(
            tab.Id,
            WorkspaceSessionTabKind.EditorCanvas,
            tab.Header,
            tab.DetailText,
            tab.IsClosable,
            tab.StatusText,
            CaptureCanvasState(editorCanvas.Scene, includeFullText: true),
            EditorCanvas: new EditorCanvasTabState(documents));
    }

    private WorkspaceTabState? CaptureQueryCanvasTabState(WorkspaceTabViewModel tab)
    {
        if (tab.QueryCanvas is not { } queryCanvas)
        {
            return null;
        }

        return new WorkspaceTabState(
            tab.Id,
            WorkspaceSessionTabKind.QueryCanvas,
            tab.Header,
            tab.DetailText,
            tab.IsClosable,
            tab.StatusText,
            CaptureCanvasState(queryCanvas.Scene),
            QueryCanvas: new QueryCanvasTabState(
                queryCanvas.QueryText,
                queryCanvas.Scope.ToString(),
                queryCanvas.SelectedSampleOption?.DisplayName));
    }

    private WorkspaceTabState? CapturePatchCompareTabState(WorkspaceTabViewModel tab)
    {
        if (tab.PatchCompare is not { } patchCompare)
        {
            return null;
        }

        return new WorkspaceTabState(
            tab.Id,
            WorkspaceSessionTabKind.PatchCompare,
            tab.Header,
            tab.DetailText,
            tab.IsClosable,
            tab.StatusText,
            PatchCompare: new PatchCompareTabState(
                patchCompare.OldRangeText,
                patchCompare.NewRangeText,
                patchCompare.WizardRepositoryText,
                patchCompare.WizardFilterText,
                patchCompare.ComparisonRepositoryPath));
    }

    private async Task RestoreWorkspaceTabAsync(WorkspaceTabState tabState, CancellationToken cancellationToken)
    {
        switch (tabState.Kind)
        {
            case WorkspaceSessionTabKind.Graph:
                await RestoreGraphWorkspaceTabAsync(tabState, cancellationToken);
                break;
            case WorkspaceSessionTabKind.GitHistory:
                await RestoreGitHistoryTabAsync(tabState, cancellationToken);
                break;
            case WorkspaceSessionTabKind.FileDiff:
                await RestoreFileDiffTabAsync(tabState, cancellationToken);
                break;
            case WorkspaceSessionTabKind.Blame:
                await RestoreBlameTabAsync(tabState, cancellationToken);
                break;
            case WorkspaceSessionTabKind.SymbolGraph:
                RestoreSymbolGraphTab(tabState);
                break;
            case WorkspaceSessionTabKind.EditorCanvas:
                await RestoreEditorCanvasTabAsync(tabState, cancellationToken);
                break;
            case WorkspaceSessionTabKind.QueryCanvas:
                RestoreQueryCanvasTab(tabState);
                break;
            case WorkspaceSessionTabKind.PatchCompare:
                RestorePatchCompareTab(tabState);
                break;
        }
    }

    private async Task RestoreGraphWorkspaceTabAsync(WorkspaceTabState tabState, CancellationToken cancellationToken)
    {
        if (tabState.GraphRequest is not { } request)
        {
            return;
        }

        var tab = WorkspaceTabViewModel.CreateGraphWorkspace(
            tabState.Id,
            tabState.Header,
            tabState.DetailText,
            request,
            tabState.GraphBranchReferenceName,
            tabState.GraphReviewRequest);
        AddWorkspaceTab(tab);
        var operation = BeginTabOperation(tab, $"Restoring {tab.Header}", drivesGlobalProgress: false);
        try
        {
            var state = await LoadGraphWorkspaceTabStateAsync(request, tab.Header, tabState.GraphReviewRequest, operation);
            ApplyCanvasState(state.Scene, tabState.Canvas);
            tab.GraphState = state with { Scene = state.Scene };
            tab.StatusText = string.IsNullOrWhiteSpace(tabState.StatusText) ? state.StatusText : tabState.StatusText;
            CompleteOperation(operation, "Workspace restored");
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(operation, "Workspace restore canceled");
        }
        catch (Exception exception)
        {
            tab.StatusText = "Workspace restore failed";
            AddDiagnostic("Warning", $"Could not restore {tab.Header}: {exception.Message}");
            CompleteOperation(operation, "Workspace restore failed");
        }
    }

    private async Task RestoreGitHistoryTabAsync(WorkspaceTabState tabState, CancellationToken cancellationToken)
    {
        if (tabState.History?.Request is not { } request)
        {
            return;
        }

        var tab = WorkspaceTabViewModel.CreateHistory(tabState.Id, tabState.Header, tabState.DetailText);
        tab.History = GitHistoryTimelineViewModel.Create(tabState.Header, request);
        AddWorkspaceTab(tab);
        var operation = BeginTabOperation(tab, $"Restoring history {tabState.Header}", drivesGlobalProgress: false);
        try
        {
            var targetCount = Math.Max(GitHistoryPageSize, tabState.History.LoadedCount);
            var pageCount = 0;
            while (tab.History is { HasMore: true } && tab.History.LoadedCount < targetCount && pageCount < RestoredHistoryPageLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await LoadGitHistoryPageAsync(tab, operation.Token, operation);
                pageCount++;
            }

            tab.StatusText = tab.History?.CountText ?? tabState.StatusText ?? "History restored";
            CompleteOperation(operation, "History restored");
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(operation, "History restore canceled");
        }
        catch (Exception exception)
        {
            tab.StatusText = "History restore failed";
            AddDiagnostic("Warning", $"Could not restore history {tabState.Header}: {exception.Message}");
            CompleteOperation(operation, "History restore failed");
        }
    }

    private async Task RestoreFileDiffTabAsync(WorkspaceTabState tabState, CancellationToken cancellationToken)
    {
        if (tabState.FileDiff is not { } fileDiff || string.IsNullOrWhiteSpace(fileDiff.Path))
        {
            return;
        }

        await OpenFileDiffTabByPathAsync(fileDiff.Path, FromState(fileDiff.DisplayMode));
        var tab = WorkspaceTabs.FirstOrDefault(tab => tab.FileDiff is { } existing && string.Equals(NormalizeRepositoryPath(existing.Path), NormalizeRepositoryPath(fileDiff.Path), StringComparison.OrdinalIgnoreCase));
        if (tab?.FileDiff is null)
        {
            return;
        }

        tab.FileDiff.SetDisplayMode(FromState(fileDiff.DisplayMode));
        tab.FileDiff.SetDiffScopeMode(FromState(fileDiff.ScopeMode));
        tab.FileDiff.SetDiffAnnotationVisibility(fileDiff.IsDiffAnnotationEnabled);
        tab.FileDiff.SetEditingEnabled(fileDiff.IsEditingEnabled);
        tab.FileDiff.SetCodeFontSize(fileDiff.CodeFontSize);
        if (!string.IsNullOrEmpty(fileDiff.FullText))
        {
            tab.FileDiff.FullText = fileDiff.FullText;
        }
    }

    private async Task RestoreBlameTabAsync(WorkspaceTabState tabState, CancellationToken cancellationToken)
    {
        if (tabState.Blame is not { } blame || string.IsNullOrWhiteSpace(blame.Path))
        {
            return;
        }

        await OpenBlameTabByPathAsync(blame.Path);
        var tab = WorkspaceTabs.FirstOrDefault(tab => tab.Blame is { } existing && string.Equals(NormalizeRepositoryPath(existing.Path), NormalizeRepositoryPath(blame.Path), StringComparison.OrdinalIgnoreCase));
        if (tab?.Blame is null)
        {
            return;
        }

        tab.Blame.SetDisplayMode(blame.DisplayMode == BlameDisplayState.ChangeGraph ? BlameDisplayMode.ChangeGraph : BlameDisplayMode.CommitTimeline);
        if (tab.Blame.IsTimelineExpanded != blame.IsTimelineExpanded)
        {
            tab.Blame.ToggleTimeline();
        }

        ApplyCanvasState(tab.Blame.ChangeGraphScene, tabState.Canvas);
    }

    private void RestoreSymbolGraphTab(WorkspaceTabState tabState)
    {
        if (tabState.SymbolGraph is not { } symbolGraphState || allSemanticNavigationItems.IsDefaultOrEmpty)
        {
            return;
        }

        var viewMode = symbolGraphState.ViewMode == SymbolGraphDisplayState.FilesAndSymbols
            ? SymbolGraphViewMode.FilesAndSymbols
            : SymbolGraphViewMode.SymbolsOnly;
        var symbolGraph = new SymbolGraphTabViewModel(
            tabState.Header,
            tabState.DetailText,
            allSemanticNavigationItems,
            currentSemanticGraph,
            currentDocuments,
            symbolGraphState.SearchText,
            symbolGraphState.ScopeKey,
            symbolGraphState.KindKey,
            symbolGraphState.DocumentKey,
            symbolGraphState.EdgeKindKey,
            symbolGraphState.LayoutMode,
            symbolGraphState.GroupingMode,
            viewMode,
            symbolGraphState.FocusAnchorId);
        ApplyCanvasState(symbolGraph.Scene, tabState.Canvas);
        AddWorkspaceTab(WorkspaceTabViewModel.CreateSymbolGraph(tabState.Id, tabState.Header, tabState.DetailText, symbolGraph));
    }

    private async Task RestoreEditorCanvasTabAsync(WorkspaceTabState tabState, CancellationToken cancellationToken)
    {
        var documents = ImmutableArray.CreateBuilder<EditorCanvasDocument>();
        if (tabState.EditorCanvas is { } editorCanvasState)
        {
            foreach (var documentState in editorCanvasState.EffectiveDocuments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = await CreateEditorCanvasDocumentFromStateAsync(documentState, cancellationToken);
                if (document is not null)
                {
                    documents.Add(document);
                }
            }
        }

        var scene = CreateEditorCanvasScene(documents.ToImmutable(), null);
        ApplyCanvasState(scene, tabState.Canvas);
        var editorCanvas = new EditorCanvasTabViewModel(
            tabState.Header,
            string.IsNullOrWhiteSpace(tabState.DetailText) ? "Editable file graph" : tabState.DetailText,
            scene);
        editorCanvas.SetDocuments(documents.ToImmutable(), scene);
        AddWorkspaceTab(WorkspaceTabViewModel.CreateEditorCanvas(tabState.Id, tabState.Header, tabState.DetailText, editorCanvas));
    }

    private void RestoreQueryCanvasTab(WorkspaceTabState tabState)
    {
        var queryCanvas = new QueryCanvasTabViewModel(tabState.Header, tabState.DetailText, queryCanvasCompletionProvider);
        if (tabState.QueryCanvas is { } state)
        {
            if (Enum.TryParse<QueryCanvasScope>(state.Scope, ignoreCase: true, out var scope))
            {
                var scopeOption = queryCanvas.ScopeOptions.FirstOrDefault(option => option.Scope == scope);
                if (scopeOption is not null)
                {
                    queryCanvas.SelectedScopeOption = scopeOption;
                }
            }

            var sample = string.IsNullOrWhiteSpace(state.SampleName)
                ? null
                : queryCanvas.SampleOptions.FirstOrDefault(option => string.Equals(option.DisplayName, state.SampleName, StringComparison.OrdinalIgnoreCase));
            if (sample is not null)
            {
                queryCanvas.SelectedSampleOption = sample;
            }

            queryCanvas.QueryText = state.QueryText;
        }

        ApplyCanvasState(queryCanvas.Scene, tabState.Canvas);
        var tab = WorkspaceTabViewModel.CreateQueryCanvas(tabState.Id, tabState.Header, tabState.DetailText, queryCanvas);
        queryCanvas.QueryChanged += (_, _) =>
        {
            ScheduleQueryCanvasExecution(tab, TimeSpan.FromMilliseconds(260));
            RequestWorkspaceSessionSave();
        };
        AddWorkspaceTab(tab);
        ScheduleQueryCanvasExecution(tab, TimeSpan.Zero);
    }

    private void RestorePatchCompareTab(WorkspaceTabState tabState)
    {
        var patchCompare = new PatchCompareTabViewModel(tabState.Header, tabState.DetailText);
        if (tabState.PatchCompare is { } state)
        {
            patchCompare.OldRangeText = state.OldRangeText;
            patchCompare.NewRangeText = state.NewRangeText;
            patchCompare.WizardRepositoryText = state.WizardRepositoryText;
            patchCompare.WizardFilterText = state.WizardFilterText;
            patchCompare.ComparisonRepositoryPath = state.ComparisonRepositoryPath;
        }

        AddWorkspaceTab(WorkspaceTabViewModel.CreatePatchCompare(tabState.Id, tabState.Header, tabState.DetailText, patchCompare));
    }

    private async Task<EditorCanvasDocument?> CreateEditorCanvasDocumentFromStateAsync(EditorCanvasDocumentState state, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.Path))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(state.FullText))
        {
            var displayPath = NormalizeRepositoryPath(state.Path);
            var metadata = new DiffDocumentMetadata(
                new DiffDocumentId(displayPath),
                displayPath,
                null,
                DiffFileStatus.Unchanged,
                LanguageFromPath(displayPath),
                0,
                0);
            var sourceDocument = new DiffDocumentFactory().CreateFromText(metadata, state.FullText, DiffLineKind.Context);
            return await CreateEditorCanvasDocumentAsync(sourceDocument, state.FullText, cancellationToken);
        }

        return await CreateEditorCanvasDocumentAsync(state.Path, cancellationToken);
    }

    private async Task ApplyWorkspaceSessionPanelStateAsync(WorkspaceSessionState state, CancellationToken cancellationToken)
    {
        FileSearchText = state.FileSearchText ?? string.Empty;
        GitReferenceSearchText = state.GitReferenceSearchText ?? string.Empty;
        ReviewSearchText = state.ReviewSearchText ?? string.Empty;
        SymbolSearchText = state.SymbolSearchText ?? string.Empty;
        if (state.FileExplorerMode == WorkspaceSessionFileExplorerMode.Workspace && currentDocumentsAreRepositoryDocuments)
        {
            await SetFileExplorerModeAsync(FileExplorerMode.Workspace);
        }
        else
        {
            await SetFileExplorerModeAsync(FileExplorerMode.Diff);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static WorkspaceCanvasState? CaptureCanvasState(DiffCanvasScene? scene, bool includeFullText = false)
    {
        if (scene is null)
        {
            return null;
        }

        return new WorkspaceCanvasState(
            scene.Camera.OffsetX,
            scene.Camera.OffsetY,
            scene.Camera.Scale,
            scene.ShowFullFileNodes,
            scene.EnableNodeEditing,
            scene.Nodes.Select(node => new WorkspaceNodeState(
                node.DiffDocument.Id.Value,
                node.Bounds.X,
                node.Bounds.Y,
                node.Bounds.Width,
                node.Bounds.Height,
                node.ScrollOffsetY,
                node.IsSelected,
                node.IsPinned,
                node.FontSize,
                node.FullFileViewOverride,
                node.EditingOverride,
                node.CaretLineIndex,
                node.CaretColumn,
                includeFullText ? node.FullText : null)).ToArray());
    }

    private static void ApplyCanvasState(DiffCanvasScene scene, WorkspaceCanvasState? state)
    {
        if (state is null)
        {
            return;
        }

        scene.SetShowFullFileNodes(state.ShowFullFileNodes);
        scene.SetNodeEditingEnabled(state.EnableNodeEditing);
        var viewState = new DiffCanvasSceneViewState(
            new CameraState(state.CameraOffsetX, state.CameraOffsetY, state.CameraScale),
            state.EffectiveNodes
                .Select(node => new DiffNodeViewState(
                    new DiffDocumentId(node.DocumentId),
                    new Rect2(node.X, node.Y, node.Width, node.Height),
                    node.ScrollOffsetY,
                    node.IsSelected,
                    node.IsPinned,
                    node.FontSize,
                    node.FullFileViewOverride,
                    node.EditingOverride,
                    node.CaretLineIndex,
                    node.CaretColumn))
                .ToImmutableArray());
        scene.ApplyViewState(viewState);
    }

    private void RequestWorkspaceSessionSave()
    {
        if (isRestoringWorkspaceSession)
        {
            return;
        }

        workspaceSessionSaveTokenSource?.Cancel();
        workspaceSessionSaveTokenSource = new CancellationTokenSource();
        _ = SaveWorkspaceSessionSoonAsync(workspaceSessionSaveTokenSource);
    }

    private async Task SaveWorkspaceSessionSoonAsync(CancellationTokenSource tokenSource)
    {
        try
        {
            await Task.Delay(400, tokenSource.Token);
            if (!ReferenceEquals(workspaceSessionSaveTokenSource, tokenSource))
            {
                return;
            }

            await SaveOptionsAsync(tokenSource.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AddDiagnostic("Warning", $"Workspace session save failed: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(workspaceSessionSaveTokenSource, tokenSource))
            {
                workspaceSessionSaveTokenSource = null;
            }

            tokenSource.Dispose();
        }
    }

    private async Task FlushWorkspaceSessionSaveAsync()
    {
        var pendingSave = workspaceSessionSaveTokenSource;
        workspaceSessionSaveTokenSource = null;
        pendingSave?.Cancel();
        pendingSave?.Dispose();

        if (isRestoringWorkspaceSession)
        {
            return;
        }

        try
        {
            await SaveOptionsAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            AddDiagnostic("Warning", $"Workspace session save failed: {exception.Message}");
        }
    }

    private WorkspaceSessionState? CaptureWorkspaceSessionForSave()
    {
        if (pendingWorkspaceSessionState is not null)
        {
            return pendingWorkspaceSessionState;
        }

        return isRestoringWorkspaceSession
            ? appState.WorkspaceSession
            : CaptureWorkspaceSession();
    }

    private static FileDiffDisplayState ToState(FileDiffDisplayMode mode) => mode == FileDiffDisplayMode.FullFile
        ? FileDiffDisplayState.FullFile
        : FileDiffDisplayState.DiffOnly;

    private static FileDiffDisplayMode FromState(FileDiffDisplayState mode) => mode == FileDiffDisplayState.FullFile
        ? FileDiffDisplayMode.FullFile
        : FileDiffDisplayMode.DiffOnly;

    private static FileDiffScopeState ToState(FileDiffScopeMode mode) => mode == FileDiffScopeMode.FullFileDiff
        ? FileDiffScopeState.FullFileDiff
        : FileDiffScopeState.Changes;

    private static FileDiffScopeMode FromState(FileDiffScopeState mode) => mode == FileDiffScopeState.FullFileDiff
        ? FileDiffScopeMode.FullFileDiff
        : FileDiffScopeMode.Changes;
}
