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

        if (!HasCurrentDiffDocument(node.Path))
        {
            SelectExplorerItem(item);
            AddDiagnostic("Info", $"Selected workspace file {node.Path}");
            return null;
        }

        return FocusExplorerItem(item);
    }

    public string GetExplorerNodePathText(FileExplorerNodeViewModel? node) =>
        node is null ? string.Empty : FormatExplorerNodeLabel(node);

    public string GetExplorerNodeFullPathText(FileExplorerNodeViewModel? node)
    {
        if (node is null || string.IsNullOrWhiteSpace(node.Path))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(node.Path) || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return node.Path;
        }

        return Path.GetFullPath(Path.Combine(currentRepositoryPath, NormalizeRepositoryPath(node.Path)));
    }

    public string GetExplorerNodeChildPathsText(FileExplorerNodeViewModel? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node.IsFile)
        {
            return node.Path;
        }

        var normalizedPath = NormalizeRepositoryPath(node.Path);
        var prefix = string.IsNullOrWhiteSpace(normalizedPath) ? string.Empty : $"{normalizedPath.TrimEnd('/')}/";
        var builder = new System.Text.StringBuilder();
        foreach (var item in allExplorerItems)
        {
            if (!string.IsNullOrWhiteSpace(item.Path) && IsPathUnderFolder(item.Path, normalizedPath, prefix))
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(item.Path);
            }
        }

        return builder.ToString();
    }

    public async Task<string?> LoadExplorerNodeContentTextAsync(FileExplorerNodeViewModel? node, CancellationToken cancellationToken = default)
    {
        if (node is null || !node.IsFile || string.IsNullOrWhiteSpace(node.Path))
        {
            return null;
        }

        var operation = BeginBackgroundOperation($"Copying file content for {node.Path}");
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operation.Token);
        try
        {
            ReportProgress(operation, 0.25, $"Loading {node.Path}");
            var text = await LoadFileContentByPathAsync(node.Path, linkedCancellation.Token);
            CompleteOperation(operation, text is null ? "File content unavailable" : "File content ready");
            if (text is null)
            {
                AddDiagnostic("Warning", $"Could not load contents for {node.Path}");
            }

            return text;
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(operation, "File content copy canceled");
            return null;
        }
        catch (Exception exception)
        {
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "File content copy failed");
            return null;
        }
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

        return string.IsNullOrWhiteSpace(node.DocumentId) || !HasCurrentDiffDocument(node.Path)
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

        return string.IsNullOrWhiteSpace(node.DocumentId) || !HasCurrentDiffDocument(node.Path)
            ? OpenBlameTabByPathAsync(node.Path)
            : OpenBlameTabAsync(node.DocumentId);
    }

    public bool HasActiveGraphSubsetFilter => GetTargetGraphWorkspaceTab()?.HasGraphFileSubsetFilter == true;

    public async Task OpenSubsetDiffWorkspaceTabAsync(FileExplorerNodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        var sourceTab = GetTargetGraphWorkspaceTab();
        var sourceState = GetGraphSubsetSourceState(sourceTab);
        if (sourceState is null)
        {
            AddDiagnostic("Warning", "Open a diff workspace before creating a file subset workspace");
            return;
        }

        if (sourceState.Request is null)
        {
            AddDiagnostic("Warning", "The current diff workspace has no git request to clone");
            return;
        }

        var documents = GetSubsetDocumentsForExplorerNode(node, sourceState.Documents);
        if (documents.IsDefaultOrEmpty)
        {
            AddDiagnostic("Info", $"{FormatExplorerNodeLabel(node)} has no changed files in the active diff workspace");
            return;
        }

        var label = FormatExplorerNodeLabel(node);
        var tabId = $"graph-subset:{sourceTab?.Id ?? "graph"}:{CreateStableSubsetKey(node.Path)}:{documents.Length}";
        if (SelectWorkspaceTab(tabId))
        {
            return;
        }

        var tab = WorkspaceTabViewModel.CreateGraphWorkspace(
            tabId,
            $"Diff {ShortenPath(label)}",
            $"{documents.Length:N0} changed files under {label}",
            sourceState.Request,
            sourceTab?.GraphBranchReferenceName,
            sourceState.ReviewRequest);
        AddWorkspaceTab(tab);

        var operation = BeginTabOperation(tab, $"Creating subset workspace for {label}", logDiagnostic: true);
        try
        {
            ReportProgress(operation, 0.18, "Filtering semantic graph");
            var subsetState = await BuildSubsetGraphWorkspaceStateAsync(sourceState, documents, label, operation.Token);
            tab.GraphState = subsetState;
            tab.StatusText = subsetState.StatusText;
            tab.GraphUnfilteredState = null;
            tab.GraphFileSubsetLabel = label;
            RestoreGraphWorkspaceState(tab);
            CompleteOperation(operation, "Subset workspace ready");
            AddDiagnostic("Info", $"Opened diff workspace for {documents.Length:N0} changed files under {label}");
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(operation, "Subset workspace canceled");
        }
        catch (Exception exception)
        {
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "Subset workspace failed");
        }
    }

    public async Task ApplyCanvasSubsetFilterAsync(FileExplorerNodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        var tab = GetTargetGraphWorkspaceTab();
        if (tab is null)
        {
            AddDiagnostic("Warning", "Open a diff workspace before filtering the canvas");
            return;
        }

        var sourceState = GetGraphSubsetSourceState(tab);
        if (sourceState is null)
        {
            AddDiagnostic("Warning", "The active diff workspace is not ready yet");
            return;
        }

        var documents = GetSubsetDocumentsForExplorerNode(node, sourceState.Documents);
        if (documents.IsDefaultOrEmpty)
        {
            AddDiagnostic("Info", $"{FormatExplorerNodeLabel(node)} has no changed files in the active diff workspace");
            return;
        }

        var label = FormatExplorerNodeLabel(node);
        var operation = BeginTabOperation(tab, $"Filtering canvas to {label}", logDiagnostic: true);
        try
        {
            ReportProgress(operation, 0.2, "Building filtered canvas");
            var subsetState = await BuildSubsetGraphWorkspaceStateAsync(sourceState, documents, label, operation.Token);
            tab.GraphUnfilteredState ??= sourceState;
            tab.GraphFileSubsetLabel = label;
            tab.GraphState = subsetState;
            tab.StatusText = subsetState.StatusText;
            if (!ReferenceEquals(SelectedWorkspaceTab, tab))
            {
                SelectedWorkspaceTab = tab;
            }
            else
            {
                RestoreGraphWorkspaceState(tab);
            }

            OnPropertyChanged(nameof(HasActiveGraphSubsetFilter));
            CompleteOperation(operation, "Canvas filter applied");
            AddDiagnostic("Info", $"Filtered canvas to {documents.Length:N0} changed files under {label}");
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(operation, "Canvas filter canceled");
        }
        catch (Exception exception)
        {
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "Canvas filter failed");
        }
    }

    public void ClearCanvasSubsetFilter()
    {
        var tab = GetTargetGraphWorkspaceTab();
        if (tab?.GraphUnfilteredState is not { } unfilteredState)
        {
            return;
        }

        tab.GraphState = unfilteredState;
        tab.GraphUnfilteredState = null;
        tab.GraphFileSubsetLabel = null;
        tab.StatusText = unfilteredState.StatusText;
        if (!ReferenceEquals(SelectedWorkspaceTab, tab))
        {
            SelectedWorkspaceTab = tab;
        }
        else
        {
            RestoreGraphWorkspaceState(tab);
        }

        OnPropertyChanged(nameof(HasActiveGraphSubsetFilter));
        AddDiagnostic("Info", "Cleared canvas file subset filter");
    }

    public async Task OpenFileDiffTabAsync(string documentId, FileDiffDisplayMode displayMode)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        var sourceDocumentId = ResolveSourceDocumentId(documentId);
        var document = FindCurrentDiffDocumentByIdOrPath(sourceDocumentId);
        if (document is not null)
        {
            await OpenFileDiffTabAsync(document, displayMode);
            return;
        }

        await OpenFileDiffTabByPathAsync(sourceDocumentId, displayMode);
    }

    public async Task OpenBlameTabAsync(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        var sourceDocumentId = ResolveSourceDocumentId(documentId);
        var document = FindCurrentDiffDocumentByIdOrPath(sourceDocumentId);
        if (document is not null)
        {
            await OpenBlameTabAsync(document);
            return;
        }

        await OpenBlameTabByPathAsync(sourceDocumentId);
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
            var operation = BeginBackgroundOperation($"Resolving workspace file {path}");
            try
            {
                ReportProgress(operation, 0.25, $"Loading workspace file {path}");
                document = await CreateWorkspaceFileDocumentAsync(normalizedPath, operation.Token);
                CompleteOperation(operation, document is null ? "Workspace file not found" : "Workspace file resolved");
                if (document is null)
                {
                    AddDiagnostic("Warning", $"No document node for {path}");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                CompleteOperation(operation, "Workspace file load canceled");
                return;
            }
            catch (Exception exception)
            {
                AddDiagnostic("Error", exception.Message);
                CompleteOperation(operation, "Workspace file load failed");
                return;
            }
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
            var operation = BeginBackgroundOperation($"Resolving blame file {path}");
            try
            {
                ReportProgress(operation, 0.35, $"Checking repository file {path}");
                if (!await HasRepositoryFileAsync(normalizedPath, operation.Token))
                {
                    CompleteOperation(operation, "Blame file not found");
                    AddDiagnostic("Warning", $"No document node for {path}");
                    return;
                }

                document = CreateWorkspaceFilePlaceholderDocument(normalizedPath);
                CompleteOperation(operation, "Blame file resolved");
            }
            catch (OperationCanceledException)
            {
                CompleteOperation(operation, "Blame file load canceled");
                return;
            }
            catch (Exception exception)
            {
                AddDiagnostic("Error", exception.Message);
                CompleteOperation(operation, "Blame file load failed");
                return;
            }
        }

        await OpenBlameTabAsync(document);
    }

    private static DiffDocumentSnapshot CreateWorkspaceFilePlaceholderDocument(string path)
    {
        var metadata = new DiffDocumentMetadata(
            new DiffDocumentId(NormalizeRepositoryPath(path)),
            NormalizeRepositoryPath(path),
            null,
            DiffFileStatus.Unchanged,
            LanguageFromPath(path),
            0,
            0);
        return new DiffDocumentFactory().CreateFromText(metadata, string.Empty, DiffLineKind.Context);
    }

    private bool HasCurrentDiffDocument(string path)
    {
        var normalizedPath = NormalizeRepositoryPath(path);
        return currentDocuments.Any(document =>
            string.Equals(NormalizeRepositoryPath(document.Metadata.Path), normalizedPath, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(document.Metadata.OldPath) &&
                string.Equals(NormalizeRepositoryPath(document.Metadata.OldPath), normalizedPath, StringComparison.OrdinalIgnoreCase)));
    }

    private WorkspaceTabViewModel? GetTargetGraphWorkspaceTab()
    {
        if (SelectedWorkspaceTab?.Kind == WorkspaceTabKind.Graph)
        {
            return SelectedWorkspaceTab;
        }

        return WorkspaceTabs.FirstOrDefault(tab => tab.Kind == WorkspaceTabKind.Graph && tab.GraphState is not null) ??
            WorkspaceTabs.FirstOrDefault(tab => tab.Kind == WorkspaceTabKind.Graph);
    }

    private GraphWorkspaceState? GetGraphSubsetSourceState(WorkspaceTabViewModel? tab)
    {
        if (tab is null)
        {
            return null;
        }

        if (ReferenceEquals(SelectedWorkspaceTab, tab))
        {
            CaptureGraphWorkspaceState(tab);
        }

        return tab.GraphUnfilteredState ?? tab.GraphState;
    }

    private static ImmutableArray<DiffDocumentSnapshot> GetSubsetDocumentsForExplorerNode(
        FileExplorerNodeViewModel node,
        ImmutableArray<DiffDocumentSnapshot> sourceDocuments)
    {
        if (sourceDocuments.IsDefaultOrEmpty || string.IsNullOrWhiteSpace(node.Path))
        {
            return [];
        }

        var normalizedPath = NormalizeRepositoryPath(node.Path);
        if (node.IsFile)
        {
            return sourceDocuments
                .Where(document => MatchesDocumentPath(document, normalizedPath))
                .ToImmutableArray();
        }

        var prefix = string.IsNullOrWhiteSpace(normalizedPath) ? string.Empty : $"{normalizedPath.TrimEnd('/')}/";
        return sourceDocuments
            .Where(document => IsDocumentUnderFolder(document, normalizedPath, prefix))
            .ToImmutableArray();
    }

    private static bool MatchesDocumentPath(DiffDocumentSnapshot document, string normalizedPath) =>
        string.Equals(NormalizeRepositoryPath(document.Metadata.Path), normalizedPath, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(document.Metadata.OldPath) &&
            string.Equals(NormalizeRepositoryPath(document.Metadata.OldPath), normalizedPath, StringComparison.OrdinalIgnoreCase)) ||
        string.Equals(document.Id.Value, normalizedPath, StringComparison.OrdinalIgnoreCase);

    private static bool IsDocumentUnderFolder(DiffDocumentSnapshot document, string normalizedFolderPath, string normalizedFolderPrefix) =>
        string.IsNullOrWhiteSpace(normalizedFolderPrefix) ||
        IsPathUnderFolder(document.Metadata.Path, normalizedFolderPath, normalizedFolderPrefix) ||
        (!string.IsNullOrWhiteSpace(document.Metadata.OldPath) &&
            IsPathUnderFolder(document.Metadata.OldPath, normalizedFolderPath, normalizedFolderPrefix));

    private static bool IsPathUnderFolder(string path, string normalizedFolderPath, string normalizedFolderPrefix)
    {
        var normalizedPath = NormalizeRepositoryPath(path);
        return string.Equals(normalizedPath, normalizedFolderPath, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(normalizedFolderPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GraphWorkspaceState> BuildSubsetGraphWorkspaceStateAsync(
        GraphWorkspaceState sourceState,
        ImmutableArray<DiffDocumentSnapshot> documents,
        string label,
        CancellationToken cancellationToken)
    {
        var documentIds = documents.Select(document => document.Id).ToImmutableHashSet();
        var semanticGraph = FilterSemanticGraph(sourceState.SemanticGraph, documentIds);
        var explorerItems = CreateExplorerItems(documents);
        var explorerTreeTask = BuildExplorerTreeAsync(explorerItems, cancellationToken);
        var semanticNavigationTask = BuildSemanticNavigationStateAsync(documents, semanticGraph, cancellationToken);
        var layout = await LayoutDocumentsForWorkspaceTabAsync(
            documents,
            semanticGraph,
            SelectedLayoutModeOption?.Mode ?? GraphLayoutMode.Layered,
            cancellationToken);
        var reviewThreads = FilterReviewThreads(sourceState.ReviewThreads, documents);
        var scene = CreateScene(documents, semanticGraph, layout, reviewThreads);
        scene.SetShowFullFileNodes(sourceState.Scene.ShowFullFileNodes);
        scene.SetNodeEditingEnabled(sourceState.Scene.EnableNodeEditing);
        var semanticNavigationState = await semanticNavigationTask;
        var explorerTreeRoots = await explorerTreeTask;
        var impactSummary = new SemanticImpactAnalyzer().Analyze(documents, semanticGraph);
        var statusText = $"{sourceState.StatusPrefix} | filtered to {label} | {documents.Length:N0} nodes | {semanticGraph.Edges.Length:N0} semantic edges | {FormatImpactStatus(impactSummary)}";

        return sourceState with
        {
            ContextText = $"{sourceState.ContextText} | subset {label}",
            StatusText = statusText,
            Documents = documents,
            ExplorerItems = explorerItems,
            ExplorerTreeRoots = explorerTreeRoots,
            SemanticNavigationItems = semanticNavigationState.Items,
            SymbolInsight = semanticNavigationState.SymbolInsight,
            SemanticDocumentInsights = semanticNavigationState.DocumentInsights,
            SemanticGraph = semanticGraph,
            PreviousLayout = layout,
            PinnedDocumentIds = sourceState.PinnedDocumentIds.Intersect(documentIds),
            Scene = scene,
            SelectedDocumentId = documents.FirstOrDefault()?.Id.Value,
            ReviewThreadItems = FilterReviewThreadItems(sourceState.ReviewThreadItems, documents),
            ReviewThreads = reviewThreads
        };
    }

    private static SemanticGraph FilterSemanticGraph(SemanticGraph graph, ImmutableHashSet<DiffDocumentId> documentIds)
    {
        if (graph.Anchors.IsDefaultOrEmpty || documentIds.IsEmpty)
        {
            return SemanticGraph.Empty;
        }

        var anchors = graph.Anchors
            .Where(anchor => documentIds.Contains(anchor.DocumentId))
            .ToImmutableArray();
        var anchorIds = anchors.Select(anchor => anchor.Id).ToImmutableHashSet(StringComparer.Ordinal);
        var edges = graph.Edges.IsDefaultOrEmpty
            ? ImmutableArray<SemanticEdge>.Empty
            : graph.Edges
                .Where(edge => anchorIds.Contains(edge.SourceAnchorId) && anchorIds.Contains(edge.TargetAnchorId))
                .ToImmutableArray();
        return new SemanticGraph(anchors, edges);
    }

    private static ImmutableArray<ReviewThreadItemViewModel> FilterReviewThreadItems(
        ImmutableArray<ReviewThreadItemViewModel> threads,
        ImmutableArray<DiffDocumentSnapshot> documents)
    {
        if (threads.IsDefaultOrEmpty)
        {
            return [];
        }

        return threads
            .Where(thread => string.IsNullOrWhiteSpace(thread.Path) || ContainsDocumentPath(documents, thread.Path))
            .ToImmutableArray();
    }

    private static ImmutableArray<GitReviewThreadInfo> FilterReviewThreads(
        ImmutableArray<GitReviewThreadInfo> threads,
        ImmutableArray<DiffDocumentSnapshot> documents)
    {
        if (threads.IsDefaultOrEmpty)
        {
            return [];
        }

        return threads
            .Where(thread => string.IsNullOrWhiteSpace(thread.Path) || ContainsDocumentPath(documents, thread.Path))
            .ToImmutableArray();
    }

    private static bool ContainsDocumentPath(ImmutableArray<DiffDocumentSnapshot> documents, string path)
    {
        var normalizedPath = NormalizeRepositoryPath(path);
        return documents.Any(document => MatchesDocumentPath(document, normalizedPath));
    }

    private static string FormatExplorerNodeLabel(FileExplorerNodeViewModel node) =>
        string.IsNullOrWhiteSpace(node.Path)
            ? node.Name
            : node.IsFile ? node.Path : $"{node.Path.TrimEnd('/')}/";

    private static string CreateStableSubsetKey(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var character in value.AsSpan())
            {
                hash ^= character;
                hash *= 16777619;
            }

            return hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private async Task<DiffDocumentSnapshot?> CreateWorkspaceFileDocumentAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return null;
        }

        var normalizedPath = NormalizeRepositoryPath(path);
        var language = LanguageFromPath(normalizedPath);
        var fileChange = new GitFileChange(normalizedPath, null, DiffFileStatus.Unchanged, 0, 0, language);
        string? text = null;
        if (currentGitSnapshot is not null)
        {
            try
            {
                text = await new GitDiffService().GetFileContentAsync(currentGitSnapshot.Request, fileChange, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AddDiagnostic("Warning", $"Git workspace file load failed: {exception.Message}");
            }
        }

        text ??= await TryReadWorkspaceFileTextAsync(normalizedPath, cancellationToken);

        if (text is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(text) && !await HasRepositoryFileAsync(normalizedPath, cancellationToken))
        {
            return null;
        }

        var metadata = new DiffDocumentMetadata(
            new DiffDocumentId(normalizedPath),
            normalizedPath,
            null,
            DiffFileStatus.Unchanged,
            language,
            0,
            0);
        var document = new DiffDocumentFactory().CreateFromText(metadata, text, DiffLineKind.Context);
        return await CreateFullFileDocumentAsync(document, text, appState.EnableTokenization, cancellationToken);
    }

    private async Task<string?> LoadFileContentByPathAsync(string path, CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeRepositoryPath(path);
        var document = currentDocuments.FirstOrDefault(document =>
            string.Equals(NormalizeRepositoryPath(document.Metadata.Path), normalizedPath, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(document.Metadata.OldPath) &&
                string.Equals(NormalizeRepositoryPath(document.Metadata.OldPath), normalizedPath, StringComparison.OrdinalIgnoreCase)));
        if (document is not null)
        {
            return await LoadFullFileTextAsync(document, cancellationToken);
        }

        if (currentGitSnapshot is not null && !string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            try
            {
                var fileChange = new GitFileChange(normalizedPath, null, DiffFileStatus.Unchanged, 0, 0, LanguageFromPath(normalizedPath));
                var text = await new GitDiffService().GetFileContentAsync(currentGitSnapshot.Request, fileChange, cancellationToken);
                if (!string.IsNullOrEmpty(text) || await HasRepositoryFileAsync(normalizedPath, cancellationToken))
                {
                    return text;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AddDiagnostic("Warning", $"Git file content load failed: {exception.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return null;
        }

        var absolutePath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(currentRepositoryPath, normalizedPath);
        return File.Exists(absolutePath)
            ? await File.ReadAllTextAsync(absolutePath, cancellationToken)
            : null;
    }

    private async Task<string?> TryReadWorkspaceFileTextAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return null;
        }

        var absolutePath = Path.IsPathRooted(normalizedPath)
            ? normalizedPath
            : Path.Combine(currentRepositoryPath, normalizedPath);
        return File.Exists(absolutePath)
            ? await File.ReadAllTextAsync(absolutePath, cancellationToken)
            : null;
    }

    private async Task<bool> HasRepositoryFileAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return false;
        }

        var normalizedPath = NormalizeRepositoryPath(path);
        if (File.Exists(Path.Combine(currentRepositoryPath, normalizedPath)))
        {
            return true;
        }

        if (currentGitSnapshot is null)
        {
            return false;
        }

        var result = await new GitCommandRunner()
            .RunAsync(currentRepositoryPath, ["cat-file", "-e", $"{ResolveRepositoryContentRevision()}:{normalizedPath}"], cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded;
    }

    private string ResolveRepositoryContentRevision()
    {
        var headRef = currentGitSnapshot?.Request.HeadRef;
        return string.IsNullOrWhiteSpace(headRef) ? "HEAD" : headRef;
    }

    private static string LanguageFromPath(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".cs" => "C#",
            ".xaml" => "XAML",
            ".axaml" => "AXAML",
            ".xml" => "XML",
            ".json" => "JSON",
            ".md" or ".markdown" => "Markdown",
            ".sln" or ".slnx" => "Solution",
            ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" => "MSBuild",
            "" => "Text",
            _ => extension.TrimStart('.').ToUpperInvariant()
        };
    }

    private async Task OpenFileDiffTabAsync(DiffDocumentSnapshot document, FileDiffDisplayMode displayMode)
    {
        var tabId = $"file:{document.Id.Value}";
        if (FindWorkspaceTab(tabId) is { FileDiff: not null } existingTab)
        {
            existingTab.FileDiff.SetDisplayMode(displayMode);
            existingTab.FileDiff.SetTokenizationEnabled(appState.EnableTokenization);
            SelectedWorkspaceTab = existingTab;
            return;
        }

        var operation = BeginBackgroundOperation($"Opening file tab for {document.Metadata.Path}");
        try
        {
            ReportProgress(operation, 0.18, $"Loading full text for {document.Metadata.Path}");
            var fullText = await LoadFullFileTextAsync(document, operation.Token);
            ReportProgress(operation, 0.46, appState.EnableTokenization ? "Tokenizing file view" : "Preparing plain file view");
            var fullFileDocument = await CreateFullFileDocumentAsync(document, fullText, appState.EnableTokenization, operation.Token);
            ReportProgress(operation, 0.72, "Preparing folding and diff annotations");
            var foldRegions = CreateFoldRegions(fullFileDocument, operation.Token);
            var fileDiff = FileDiffTabViewModel.FromDocument(
                document,
                fullFileDocument,
                fullText,
                foldRegions,
                GetSemanticDocumentInsight(document.Id),
                displayMode,
                currentRepositoryPath,
                CodeCompletionProvider);
            fileDiff.SetTokenizationEnabled(appState.EnableTokenization);
            var tab = WorkspaceTabViewModel.CreateFileDiff(tabId, Path.GetFileName(document.Metadata.Path), document.Metadata.Path, fileDiff);
            AddWorkspaceTab(tab);
            AddDiagnostic("Info", $"Opened file diff tab for {document.Metadata.Path}");
            CompleteOperation(operation, "File tab ready");
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(operation, "File tab load canceled");
        }
        catch (Exception exception)
        {
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "File tab load failed");
        }
    }

    private SemanticDocumentInsight GetSemanticDocumentInsight(DiffDocumentId documentId) =>
        currentSemanticDocumentInsights.TryGetValue(documentId, out var insight)
            ? insight
            : SemanticDocumentInsight.Empty(documentId);

    private void RefreshOpenFileDiffSemanticInsights()
    {
        foreach (var tab in WorkspaceTabs)
        {
            if (tab.FileDiff is { } fileDiff)
            {
                fileDiff.SetSemanticInsight(GetSemanticDocumentInsight(new DiffDocumentId(fileDiff.DocumentId)));
            }
        }
    }

    private void UpdateOpenFileDiffCompletionProviders()
    {
        foreach (var tab in WorkspaceTabs)
        {
            tab.FileDiff?.SetCompletionProvider(CodeCompletionProvider);
        }
    }

    private void UpdateOpenFileDiffTokenizationSettings()
    {
        foreach (var tab in WorkspaceTabs)
        {
            tab.FileDiff?.SetTokenizationEnabled(appState.EnableTokenization);
        }
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
        var operation = BeginTabOperation(tab, $"Loading blame for {path}");
        try
        {
            var blameRevision = GetActiveBlameRevision();
            ReportProgress(operation, 0.18, $"Loading blame for {path}");
            var blameTask = gitBlameService.GetFileBlameAsync(currentRepositoryPath, path, operation.Token, blameRevision);
            var historyTask = gitHistoryService.GetHistoryAsync(
                new GitHistoryRequest(currentRepositoryPath, blameRevision ?? "HEAD", MaxCount: 160, PathFilter: path),
                operation.Token);
            await Task.WhenAll(blameTask, historyTask);
            ReportProgress(operation, 0.86, "Building blame visualization");

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

    public void ExpandExplorerNodeTree(FileExplorerNodeViewModel? node)
    {
        if (node is null || !node.HasChildren)
        {
            return;
        }

        var paths = GetExplorerFolderPathSet(node.Path);
        if (paths.Count == 0)
        {
            return;
        }

        var builder = collapsedExplorerNodePaths.ToBuilder();
        foreach (var path in paths)
        {
            builder.Remove(path);
        }

        collapsedExplorerNodePaths = builder.ToImmutable();
        ApplyExplorerFilter();
    }

    public void CollapseExplorerNodeTree(FileExplorerNodeViewModel? node)
    {
        if (node is null || !node.HasChildren)
        {
            return;
        }

        var paths = GetExplorerFolderPathSet(node.Path);
        if (paths.Count == 0)
        {
            return;
        }

        collapsedExplorerNodePaths = collapsedExplorerNodePaths.Union(paths);
        ApplyExplorerFilter();
    }

    public void ExpandAllExplorerNodes()
    {
        collapsedExplorerNodePaths = CreateExplorerPathSet();
        ApplyExplorerFilter();
    }

    public void CollapseAllExplorerNodes()
    {
        collapsedExplorerNodePaths = GetAllExplorerFolderPathSet();
        ApplyExplorerFilter();
    }

    public void RevealDocumentInExplorer(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        var sourceDocumentId = ResolveSourceDocumentId(documentId);
        var item = FindExplorerItemByDocumentOrPath(allExplorerItems, sourceDocumentId);
        var targetMode = FileExplorerMode;
        if (item is null)
        {
            item = FindExplorerItemByDocumentOrPath(workspaceExplorerItems, sourceDocumentId);
            targetMode = FileExplorerMode.Workspace;
        }

        if (item is null)
        {
            item = FindExplorerItemByDocumentOrPath(diffExplorerItems, sourceDocumentId);
            targetMode = FileExplorerMode.Diff;
        }

        if (item is null)
        {
            AddDiagnostic("Warning", $"No file tree item for {sourceDocumentId}");
            return;
        }

        if (targetMode != FileExplorerMode)
        {
            FileExplorerMode = targetMode;
            IsDiffFileExplorerModeSelected = targetMode == FileExplorerMode.Diff;
            IsWorkspaceFileExplorerModeSelected = targetMode == FileExplorerMode.Workspace;
            UpdateFileExplorerModeLabels();
            SetActiveExplorerItems(
                targetMode == FileExplorerMode.Workspace ? workspaceExplorerItems : diffExplorerItems,
                targetMode == FileExplorerMode.Workspace ? workspaceExplorerTreeRoots : diffExplorerTreeRoots);
        }

        FileSearchText = string.Empty;
        ExpandAncestors(item.Path);
        ApplyExplorerFilter();
        SelectExplorerItem(item);
        SelectedRailTabIndex = 0;
        AddDiagnostic("Info", $"Revealed {item.Path} in the file tree");
    }

    private DiffDocumentSnapshot? FindCurrentDiffDocumentByIdOrPath(string documentIdOrPath)
    {
        var sourceDocumentId = ResolveSourceDocumentId(documentIdOrPath);
        var normalizedPath = NormalizeRepositoryPath(sourceDocumentId);
        return currentDocuments.FirstOrDefault(document =>
            string.Equals(document.Id.Value, sourceDocumentId, StringComparison.Ordinal) ||
            MatchesDocumentPath(document, normalizedPath));
    }

    private static ExplorerItemViewModel? FindExplorerItemByDocumentOrPath(
        ImmutableArray<ExplorerItemViewModel> items,
        string documentIdOrPath)
    {
        if (items.IsDefaultOrEmpty)
        {
            return null;
        }

        var normalizedPath = NormalizeRepositoryPath(documentIdOrPath);
        return items.FirstOrDefault(item =>
            string.Equals(item.DocumentId, documentIdOrPath, StringComparison.Ordinal) ||
            string.Equals(NormalizeRepositoryPath(item.Path), normalizedPath, StringComparison.OrdinalIgnoreCase));
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

        var thread = reviewWorkflow.FindThread(threadId);
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
}
