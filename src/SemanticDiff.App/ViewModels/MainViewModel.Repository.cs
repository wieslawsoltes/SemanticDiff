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
                pendingWorkspaceSessionState = loadedState.WorkspaceSession;
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
                InvalidateWorkspaceExplorerCache();
                ClearReferenceOptions("Refs unavailable");
                await StopRepositoryWatcherAsync();
                EnsureCurrentRepositoryRequest(requestId, cancellationToken);
                InitializeSampleDocuments(SampleDiffDocuments.Create());
                StatusText = "No Git repository found | showing sample diff graph";
                await RestorePendingWorkspaceSessionAsync(cancellationToken);
                AddDiagnostic("Info", "No Git repository found; using sample graph");
                CompleteOperation(operation, "Sample graph ready");
                return;
            }

            var isNewRepositoryRoot = !string.Equals(currentRepositoryPath, repositoryRoot, StringComparison.Ordinal);
            currentRepositoryPath = repositoryRoot;
            if (isNewRepositoryRoot)
            {
                InvalidateWorkspaceExplorerCache();
            }

            appState = appState with { RepositoryPath = repositoryRoot };
            ApplyAppStateToPresentation();
            if (isNewRepositoryRoot || BranchOptions.IsDefaultOrEmpty)
            {
                ClearReferenceOptions("Loading branches");
            }

            _ = RefreshRepositoryReferencesAsync(repositoryRoot, requestId, cancellationToken);
            var request = new GitDiffRequest(repositoryRoot, appState.DiffScope, NormalizeRef(appState.BaseRef), NormalizeRef(appState.HeadRef));

            if (TryApplyCachedDiffView(repositoryRoot, request, requestId, cancellationToken))
            {
                await SaveOptionsAsync(cancellationToken);
                EnsureCurrentRepositoryRequest(requestId, cancellationToken);
                await RestartRepositoryWatcherAsync(repositoryRoot, cancellationToken);
                EnsureCurrentRepositoryRequest(requestId, cancellationToken);
                await RestorePendingWorkspaceSessionAsync(cancellationToken);
                EnsureCurrentRepositoryRequest(requestId, cancellationToken);
                CompleteOperation(operation, "Cached diff ready");
                return;
            }

            ReportProgress(0.25, "Loading Git diff", cancellationToken);
            var loadedDiff = await repositoryDiffLoader.LoadAsync(
                new RepositoryDiffLoadRequest(
                    repositoryRoot,
                    appState.DiffScope,
                    appState.BaseRef,
                    appState.HeadRef,
                    appState.DiffContextMode,
                    appState.ReviewMode,
                    appState.CollapseUnchangedContext),
                cancellationToken);
            EnsureCurrentRepositoryRequest(requestId, cancellationToken);

            if (loadedDiff.Documents.Length == 0)
            {
                var emptyScopeText = FormatDiffScope(appState.DiffScope);
                ResetRepositoryPresentation(
                    Path.GetFileName(repositoryRoot),
                    $"{Path.GetFileName(repositoryRoot)} | no {emptyScopeText} changes | {FormatDiffContextMode(appState.DiffContextMode)} | base {loadedDiff.GitSnapshot.DefaultBranch ?? "unknown"}",
                    $"{Path.GetFileName(repositoryRoot)} | no {emptyScopeText} changes",
                    isRepository: true);
                await SaveOptionsAsync(cancellationToken);
                EnsureCurrentRepositoryRequest(requestId, cancellationToken);
                await RestartRepositoryWatcherAsync(repositoryRoot, cancellationToken);
                EnsureCurrentRepositoryRequest(requestId, cancellationToken);
                await RestorePendingWorkspaceSessionAsync(cancellationToken);
                EnsureCurrentRepositoryRequest(requestId, cancellationToken);
                AddDiagnostic("Info", $"Repository has no {emptyScopeText} changes");
                CompleteOperation(operation, "Repository has no changes");
                return;
            }

            ReportProgress(0.45, "Preparing document graph", cancellationToken);
            EnsureCurrentRepositoryRequest(requestId, cancellationToken);
            await SetDocumentsAsync(
                loadedDiff.Documents,
                $"{Path.GetFileName(repositoryRoot)} | {loadedDiff.GitSnapshot.Files.Length} {FormatDiffScope(appState.DiffScope)} changes | {FormatDiffContextMode(appState.DiffContextMode)} | {FormatReviewMode(appState.ReviewMode)} | {FormatReferenceText(request, loadedDiff.GitSnapshot.DefaultBranch)}",
                repositoryRoot,
                loadedDiff.GitSnapshot,
                preservedSceneState,
                requestId,
                cancellationToken);
            EnsureCurrentRepositoryRequest(requestId, cancellationToken);
            await RestorePendingWorkspaceSessionAsync(cancellationToken);
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
        SetExplorerItems(CreateExplorerItems(documents));
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
        var explorerItems = CreateExplorerItems(documents);
        var explorerTreeTask = BuildExplorerTreeAsync(explorerItems, cancellationToken);

        ReportProgress(0.52, "Running initial layout", cancellationToken);
        var initialLayout = await LayoutDocumentsAsync(documents, SemanticGraph.Empty, cancellationToken);
        EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);
        var explorerTreeRoots = await explorerTreeTask;
        EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);

        currentSemanticGraph = SemanticGraph.Empty;
        previousLayout = initialLayout;
        Scene = CreateScene(documents, SemanticGraph.Empty, initialLayout);
        if (preservedSceneState is not null)
        {
            Scene.ApplyViewState(preservedSceneState);
            CaptureLayoutState(Scene);
        }

        SetExplorerItems(explorerItems, explorerTreeRoots);
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
        SetExplorerItems(explorerItems, explorerTreeRoots);
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

        SetExplorerItems(explorerItems, explorerTreeRoots);
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

            ReportProgress(refinementOperation, 0.22, "Analyzing MSBuild workspace semantics");
            var semanticGraph = await AnalyzeSemanticsAsync(repositoryPath, gitSnapshot, documents, SemanticAnalysisMode.WorkspaceThenSyntax, cancellationToken);
            EnsureCurrentRepositoryRequest(repositoryRequestId, cancellationToken);
            var viewState = Scene.CaptureViewState();
            currentSemanticGraph = semanticGraph;
            ReportProgress(refinementOperation, 0.72, "Laying out refined semantic graph");
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
            CompleteOperation(refinementOperation, "MSBuild semantics ready");
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(refinementOperation, "MSBuild semantic refinement canceled");
        }
        catch (Exception exception)
        {
            if (IsCurrentRepositoryRequest(repositoryRequestId))
            {
                AddDiagnostic("Warning", $"MSBuild semantic refinement failed: {exception.Message}");
            }

            CompleteOperation(refinementOperation, "MSBuild semantic refinement failed");
        }
        finally
        {
            if (ReferenceEquals(currentSemanticRefinementOperation, refinementOperation))
            {
                currentSemanticRefinementOperation = null;
            }
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
        var workspaceSession = CaptureWorkspaceSessionForSave();
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
            UseInteractiveLevelOfDetail = UseInteractiveLevelOfDetail,
            CodeCompletionMode = appState.CodeCompletionMode,
            LeftPaneWidth = NormalizeLeftPaneWidth(LeftPaneWidth),
            LayoutNodes = currentDocumentsAreRepositoryDocuments ? layoutNodes : appState.LayoutNodes,
            WorkspaceSession = workspaceSession
        };
        await appStateStore.SaveAsync(appState, cancellationToken);
    }

    private async Task SaveOptionsAsync(CancellationToken cancellationToken)
    {
        var workspaceSession = CaptureWorkspaceSessionForSave();
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
            UseInteractiveLevelOfDetail = UseInteractiveLevelOfDetail,
            CodeCompletionMode = appState.CodeCompletionMode,
            LeftPaneWidth = NormalizeLeftPaneWidth(LeftPaneWidth),
            WorkspaceSession = workspaceSession
        };
        await appStateStore.SaveAsync(appState, cancellationToken);
    }

    private void CacheCurrentDiffView()
    {
        if (!currentDocumentsAreRepositoryDocuments || currentGitSnapshot is null || currentDocuments.IsDefaultOrEmpty || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return;
        }

        if (!DiffWorkspaceCache.IsCacheable(currentGitSnapshot.Request))
        {
            return;
        }

        CaptureLayoutState(Scene);
        diffViewCache.Store(
            currentRepositoryPath,
            currentGitSnapshot.Request,
            CreateDiffViewCacheOptions(),
            currentDocuments,
            currentSemanticGraph,
            Scene,
            previousLayout,
            pinnedDocumentIds,
            currentGitSnapshot,
            currentStatusPrefix,
            selectedExplorerItem?.DocumentId);
        UpdateDiffViewCacheText();
    }

    private bool TryApplyCachedDiffView(string repositoryPath, GitDiffRequest request, long repositoryRequestId, CancellationToken cancellationToken)
    {
        if (!diffViewCache.TryGet(repositoryPath, request, CreateDiffViewCacheOptions(), out var entry))
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
        Scene = entry.Scene.WithAnnotations(entry.Scene.Annotations, appState.EffectiveAnnotationVisibility);
        SetExplorerItems(CreateExplorerItems(entry.Documents));
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

    private DiffWorkspaceCacheKeyOptions CreateDiffViewCacheOptions() => new(
        appState.DiffContextMode,
        appState.ReviewMode,
        appState.CollapseUnchangedContext,
        appState.SemanticAnalysisMode,
        appState.LayoutMode,
        appState.GroupingMode,
        appState.ShowSemanticEdges);

    private void UpdateDiffViewCacheText()
    {
        DiffViewCacheText = diffViewCache.StatusText;
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
        UseInteractiveLevelOfDetail = appState.UseInteractiveLevelOfDetail;
        SemanticAnalysisModeText = FormatSemanticAnalysisMode(appState.SemanticAnalysisMode);
        CodeCompletionProvider = CreateCodeCompletionProvider(appState.CodeCompletionMode);
        CodeCompletionModeText = FormatCodeCompletionMode(appState.CodeCompletionMode);
        IsCompletionLanguageServicesModeSelected = appState.CodeCompletionMode == CodeCompletionMode.LanguageServicesThenDocument;
        IsCompletionDocumentModeSelected = appState.CodeCompletionMode == CodeCompletionMode.DocumentOnly;
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

        ApplyReferenceSelectionsToPresentation();
    }

    private void ApplyReferenceSelectionsToPresentation()
    {
        isUpdatingReferenceSelection = true;
        try
        {
            SelectedPullRequestOption = appState.SelectedPullRequestNumber is null
                ? null
                : gitReferenceBrowser.AllReviewRequests.FirstOrDefault(option => option.Number == appState.SelectedPullRequestNumber.Value);
            SelectedBranchOption = appState.DiffScope == GitDiffScope.Branch && appState.SelectedPullRequestNumber is null
                ? gitReferenceBrowser.AllBranches.FirstOrDefault(option => string.Equals(option.ReferenceName, appState.SelectedBranchRef ?? appState.HeadRef, StringComparison.Ordinal))
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

    private static ImmutableArray<ExplorerItemViewModel> CreateExplorerItems(ImmutableArray<DiffDocumentSnapshot> documents) =>
        documents.IsDefaultOrEmpty
            ? []
            : documents.Select(document => new ExplorerItemViewModel(document.Metadata.Path, document.Metadata.Status, document.Metadata.Language)).ToImmutableArray();

    private static Task<ImmutableArray<FileExplorerNode>> BuildExplorerTreeAsync(ImmutableArray<ExplorerItemViewModel> items, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return BuildExplorerTree(items);
        }, cancellationToken);

    private void SetExplorerItems(ImmutableArray<ExplorerItemViewModel> items, ImmutableArray<FileExplorerNode> treeRoots = default)
    {
        diffExplorerItems = items.IsDefault ? ImmutableArray<ExplorerItemViewModel>.Empty : items;
        diffExplorerTreeRoots = treeRoots.IsDefault ? BuildExplorerTree(diffExplorerItems) : treeRoots;
        UpdateFileExplorerModeLabels();
        if (FileExplorerMode == FileExplorerMode.Workspace)
        {
            if (IsWorkspaceExplorerCacheValid())
            {
                SetActiveExplorerItems(workspaceExplorerItems, workspaceExplorerTreeRoots);
            }
            else
            {
                _ = LoadWorkspaceExplorerAsync(CancellationToken.None);
            }

            return;
        }

        SetActiveExplorerItems(diffExplorerItems, diffExplorerTreeRoots);
    }
}
