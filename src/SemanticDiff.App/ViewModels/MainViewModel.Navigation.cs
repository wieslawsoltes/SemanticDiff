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
    partial void OnFileSearchTextChanged(string value) => ApplyExplorerFilter();

    partial void OnIsLightThemeEnabledChanged(bool value)
    {
        foreach (var tab in WorkspaceTabs)
        {
            tab.IsLightTheme = value;
        }

        ApplyExplorerFilter();
    }

    partial void OnUseInteractiveLevelOfDetailChanged(bool value)
    {
        foreach (var tab in WorkspaceTabs)
        {
            tab.UseInteractiveLevelOfDetail = value;
        }
    }

    private void ApplyExplorerFilter()
    {
        var query = FileSearchText.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? allExplorerItems
            : allExplorerItems
                .Where(item => item.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray();
        var tree = string.IsNullOrWhiteSpace(query)
            ? activeExplorerTreeRoots
            : BuildExplorerTree(filtered);

        IsUpdatingExplorerTree = true;
        try
        {
            ExplorerItems = filtered;
            ExplorerTreeItems = FileExplorerNodeViewModel.Flatten(
                tree,
                collapsedExplorerNodePaths,
                !string.IsNullOrWhiteSpace(query),
                currentRepositoryPath,
                IsLightThemeEnabled,
                FileExplorerMode == FileExplorerMode.Diff);
            UpdateSelectedExplorerTreeNode();
            ExplorerCountText = string.IsNullOrWhiteSpace(query)
                ? FormatCount(allExplorerItems.Length, "file", "files")
                : $"{filtered.Length:N0}/{allExplorerItems.Length:N0} files";
            UpdateFileExplorerModeStatus(filtered.Length);
        }
        finally
        {
            IsUpdatingExplorerTree = false;
        }
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

    public async Task SetFileExplorerModeAsync(FileExplorerMode mode)
    {
        if (FileExplorerMode == mode && (mode == FileExplorerMode.Diff || IsWorkspaceExplorerCacheValid()))
        {
            return;
        }

        FileExplorerMode = mode;
        IsDiffFileExplorerModeSelected = mode == FileExplorerMode.Diff;
        IsWorkspaceFileExplorerModeSelected = mode == FileExplorerMode.Workspace;
        UpdateFileExplorerModeLabels();

        if (mode == FileExplorerMode.Diff)
        {
            SetActiveExplorerItems(diffExplorerItems, diffExplorerTreeRoots);
            return;
        }

        if (IsWorkspaceExplorerCacheValid())
        {
            SetActiveExplorerItems(workspaceExplorerItems, workspaceExplorerTreeRoots);
            return;
        }

        await LoadWorkspaceExplorerAsync(CancellationToken.None);
    }

    private Task LoadWorkspaceExplorerAsync(CancellationToken cancellationToken)
    {
        var repositoryPath = currentRepositoryPath;
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            return LoadWorkspaceExplorerCoreAsync(cancellationToken);
        }

        lock (workspaceExplorerLoadGate)
        {
            if (currentWorkspaceExplorerLoadTask is { IsCompleted: false } existingTask &&
                string.Equals(currentWorkspaceExplorerLoadRepositoryPath, repositoryPath, StringComparison.Ordinal))
            {
                return cancellationToken.CanBeCanceled
                    ? existingTask.WaitAsync(cancellationToken)
                    : existingTask;
            }

            var loadTask = LoadWorkspaceExplorerCoreAsync(cancellationToken);
            currentWorkspaceExplorerLoadRepositoryPath = repositoryPath;
            currentWorkspaceExplorerLoadTask = loadTask;
            _ = loadTask.ContinueWith(_ =>
            {
                lock (workspaceExplorerLoadGate)
                {
                    if (ReferenceEquals(currentWorkspaceExplorerLoadTask, loadTask))
                    {
                        currentWorkspaceExplorerLoadTask = null;
                        currentWorkspaceExplorerLoadRepositoryPath = null;
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return loadTask;
        }
    }

    private async Task LoadWorkspaceExplorerCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            await RunOnCapturedContextAsync(() =>
            {
                workspaceExplorerItems = [];
                workspaceExplorerRepositoryPath = null;
                workspaceExplorerWorkspacePath = null;
                workspaceExplorerCacheLoaded = false;
                if (FileExplorerMode == FileExplorerMode.Workspace)
                {
                    SetActiveExplorerItems(workspaceExplorerItems, workspaceExplorerTreeRoots);
                }

                FileExplorerModeStatusText = "Open a repository to load workspace files";
                return Task.CompletedTask;
            });
            return;
        }

        var repositoryPath = currentRepositoryPath;
        CancellationTokenSource? operation = null;
        await RunOnCapturedContextAsync(() =>
        {
            operation = BeginBackgroundOperation(
                "Loading workspace files",
                cancellationToken,
                drivesGlobalProgress: true);
            var previousOperation = Interlocked.Exchange(ref currentWorkspaceExplorerOperation, operation);
            previousOperation?.Cancel();
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        try
        {
            await RunOnCapturedContextAsync(() =>
            {
                if (FileExplorerMode == FileExplorerMode.Workspace)
                {
                    SetActiveExplorerItems([], []);
                }

                FileExplorerModeStatusText = "Loading workspace files...";
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            ReportProgress(operation!, 0.15, "Discovering MSBuild workspace files");
            var result = await workspaceFileDiscoveryService.LoadFilesAsync(repositoryPath, operation!.Token).ConfigureAwait(false);
            var items = result.Files
                .Select(file => new ExplorerItemViewModel(file.Path, file.Status, file.Language))
                .ToImmutableArray();
            var tree = BuildExplorerTree(items);
            ReportProgress(operation!, 0.82, "Building workspace file tree");

            await RunOnCapturedContextAsync(() =>
            {
                if (!CanReportOperation(operation!) ||
                    !string.Equals(currentRepositoryPath, repositoryPath, StringComparison.Ordinal))
                {
                    CompleteOperation(operation, "Workspace file load skipped");
                    return Task.CompletedTask;
                }

                workspaceExplorerItems = items;
                workspaceExplorerTreeRoots = tree;
                workspaceExplorerRepositoryPath = repositoryPath;
                workspaceExplorerWorkspacePath = result.WorkspacePath;
                workspaceExplorerCacheLoaded = true;
                if (FileExplorerMode == FileExplorerMode.Workspace)
                {
                    SetActiveExplorerItems(workspaceExplorerItems, workspaceExplorerTreeRoots);
                }

                var workspaceName = string.IsNullOrWhiteSpace(result.WorkspacePath)
                    ? "workspace"
                    : Path.GetFileName(result.WorkspacePath);
                if (workspaceExplorerItems.IsDefaultOrEmpty)
                {
                    FileExplorerModeStatusText = "No workspace files found";
                }
                else if (FileExplorerMode != FileExplorerMode.Workspace)
                {
                    FileExplorerModeStatusText = $"{workspaceName} | {workspaceExplorerItems.Length:N0} files";
                }

                CompleteOperation(operation, FileExplorerModeStatusText);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(operation!, "Workspace file load canceled");
        }
        catch (Exception exception)
        {
            await RunOnCapturedContextAsync(() =>
            {
                if (!string.Equals(currentRepositoryPath, repositoryPath, StringComparison.Ordinal))
                {
                    CompleteOperation(operation!, "Workspace file load skipped");
                    return Task.CompletedTask;
                }

                workspaceExplorerItems = [];
                workspaceExplorerTreeRoots = [];
                workspaceExplorerRepositoryPath = repositoryPath;
                workspaceExplorerWorkspacePath = null;
                workspaceExplorerCacheLoaded = true;
                if (FileExplorerMode == FileExplorerMode.Workspace)
                {
                    SetActiveExplorerItems(workspaceExplorerItems, workspaceExplorerTreeRoots);
                }

                FileExplorerModeStatusText = $"Workspace files unavailable: {exception.Message}";
                AddDiagnostic("Warning", FileExplorerModeStatusText);
                CompleteOperation(operation!, "Workspace files unavailable");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(currentWorkspaceExplorerOperation, operation))
            {
                currentWorkspaceExplorerOperation = null;
            }
        }
    }

    private void SetActiveExplorerItems(
        ImmutableArray<ExplorerItemViewModel> items,
        ImmutableArray<FileExplorerNode> prebuiltTree = default)
    {
        allExplorerItems = items.IsDefault ? ImmutableArray<ExplorerItemViewModel>.Empty : items;
        activeExplorerTreeRoots = prebuiltTree.IsDefault ? BuildExplorerTree(allExplorerItems) : prebuiltTree;
        collapsedExplorerNodePaths = CreateDefaultCollapsedExplorerNodePaths(activeExplorerTreeRoots);
        ApplyExplorerFilter();
    }

    private static ImmutableArray<FileExplorerNode> BuildExplorerTree(ImmutableArray<ExplorerItemViewModel> items) =>
        items.IsDefaultOrEmpty
            ? []
            : FileExplorerTreeBuilder.Build(items.Select(item => new FileExplorerFile(item.Path, item.Status, item.Language)));

    private ImmutableHashSet<string> GetExplorerFolderPathSet(string path)
    {
        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        var node = FindExplorerNode(activeExplorerTreeRoots, path);
        if (node is { Kind: FileExplorerNodeKind.Folder })
        {
            AddExplorerFolderPaths(node, builder, includeCurrent: true);
        }

        return builder.ToImmutable();
    }

    private ImmutableHashSet<string> GetAllExplorerFolderPathSet()
    {
        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in activeExplorerTreeRoots)
        {
            AddExplorerFolderPaths(root, builder, includeCurrent: true);
        }

        return builder.ToImmutable();
    }

    private static ImmutableHashSet<string> CreateDefaultCollapsedExplorerNodePaths(ImmutableArray<FileExplorerNode> roots)
    {
        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (root.Kind != FileExplorerNodeKind.Folder)
            {
                continue;
            }

            foreach (var child in root.Children)
            {
                AddExplorerFolderPaths(child, builder, includeCurrent: true);
            }
        }

        return builder.ToImmutable();
    }

    private static ImmutableHashSet<string> CreateExplorerPathSet() =>
        ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    private static FileExplorerNode? FindExplorerNode(ImmutableArray<FileExplorerNode> roots, string path)
    {
        foreach (var root in roots)
        {
            var match = FindExplorerNode(root, path);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static FileExplorerNode? FindExplorerNode(FileExplorerNode node, string path)
    {
        if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var match = FindExplorerNode(child, path);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static void AddExplorerFolderPaths(
        FileExplorerNode node,
        ImmutableHashSet<string>.Builder builder,
        bool includeCurrent)
    {
        if (node.Kind != FileExplorerNodeKind.Folder)
        {
            return;
        }

        if (includeCurrent && !string.IsNullOrWhiteSpace(node.Path))
        {
            builder.Add(node.Path);
        }

        foreach (var child in node.Children)
        {
            AddExplorerFolderPaths(child, builder, includeCurrent: true);
        }
    }

    private bool IsWorkspaceExplorerCacheValid() =>
        workspaceExplorerCacheLoaded &&
        !string.IsNullOrWhiteSpace(currentRepositoryPath) &&
        string.Equals(workspaceExplorerRepositoryPath, currentRepositoryPath, StringComparison.Ordinal);

    private void InvalidateWorkspaceExplorerCache()
    {
        lock (workspaceExplorerLoadGate)
        {
            currentWorkspaceExplorerLoadTask = null;
            currentWorkspaceExplorerLoadRepositoryPath = null;
        }

        var operation = Interlocked.Exchange(ref currentWorkspaceExplorerOperation, null);
        operation?.Cancel();
        if (operation is not null)
        {
            CompleteOperation(operation, "Workspace file cache invalidated");
        }

        workspaceExplorerItems = [];
        workspaceExplorerTreeRoots = [];
        activeExplorerTreeRoots = [];
        workspaceExplorerRepositoryPath = null;
        workspaceExplorerWorkspacePath = null;
        workspaceExplorerCacheLoaded = false;
    }

    private void UpdateFileExplorerModeLabels()
    {
        FileExplorerTitleText = FileExplorerMode == FileExplorerMode.Workspace ? "Workspace Files" : "Changed Files";
        FileExplorerSearchPlaceholderText = FileExplorerMode == FileExplorerMode.Workspace
            ? "Find workspace file"
            : "Find changed file";
        UpdateFileExplorerModeStatus(allExplorerItems.Length);
    }

    private void UpdateFileExplorerModeStatus(int visibleCount)
    {
        if (FileExplorerMode == FileExplorerMode.Workspace)
        {
            var workspaceName = string.IsNullOrWhiteSpace(workspaceExplorerWorkspacePath)
                ? "workspace"
                : Path.GetFileName(workspaceExplorerWorkspacePath);
            FileExplorerModeStatusText = workspaceExplorerItems.IsDefaultOrEmpty
                ? "Workspace files"
                : $"{workspaceName} | {visibleCount:N0}/{workspaceExplorerItems.Length:N0} files";
            return;
        }

        FileExplorerModeStatusText = $"{visibleCount:N0}/{diffExplorerItems.Length:N0} changed files";
    }

    private void UpdateSemanticNavigation(SemanticGraph semanticGraph, ImmutableArray<DiffDocumentSnapshot> documents)
    {
        var index = new SemanticNavigationIndex();
        symbolBrowser.SetItems(index.Build(semanticGraph, documents));
        currentSemanticDocumentInsights = new SemanticDocumentInsightIndex()
            .Build(semanticGraph, documents)
            .ToImmutableDictionary(insight => insight.DocumentId);
        RefreshOpenFileDiffSemanticInsights();
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
        symbolBrowser.SetScope(string.IsNullOrWhiteSpace(filter?.FilterKey)
            ? SymbolBrowserSelection.AllKey
            : filter.FilterKey);
        ApplySemanticNavigationFilter();
    }

    public void SetSymbolKindFilter(SemanticSymbolKindFacetViewModel? facet)
    {
        symbolBrowser.ToggleKind(facet?.KindKey);
        ApplySemanticNavigationFilter();
    }

    public FocusRequest? SetSymbolDocumentFilter(SemanticSymbolDocumentFacetViewModel? facet)
    {
        symbolBrowser.ToggleDocument(facet?.DocumentId);
        ApplySemanticNavigationFilter();
        return FocusFirstSemanticResult();
    }

    public void ClearSymbolFilters()
    {
        SymbolSearchText = string.Empty;
        symbolBrowser.Clear();
        ApplySemanticNavigationFilter();
    }

    private void ApplySemanticNavigationFilter()
    {
        var view = symbolBrowser.Apply(SymbolSearchText);
        SemanticItems = view.Items
            .Select(SemanticNavigationItemViewModel.FromItem)
            .ToImmutableArray();
        SymbolCountText = view.CountText;
        SymbolFilterStatusText = view.FilterStatusText;
        RefreshSymbolInsightViewModels(view);
    }

    private FocusRequest? FocusFirstSemanticResult()
    {
        var first = SemanticItems.FirstOrDefault();
        return first is null ? null : FocusSemanticItem(first);
    }

    private void RefreshSymbolInsightViewModels(SymbolBrowserView view)
    {
        var insight = view.Insight;
        var selection = view.Selection;
        SymbolInsightSummaryText = insight.TotalSymbolCount == 0
            ? "No semantic symbols found for this diff"
            : $"{insight.TotalSymbolCount:N0} symbols across {insight.DocumentCount:N0} files | {insight.ChangedSymbolCount:N0} changed | {insight.LinkedSymbolCount:N0} linked";
        SymbolScopeFilters =
        [
            new SymbolScopeFilterViewModel(SymbolScopeFilterViewModel.AllKey, "All", insight.TotalSymbolCount, string.Equals(selection.ScopeKey, SymbolBrowserSelection.AllKey, StringComparison.Ordinal)),
            new SymbolScopeFilterViewModel(SymbolScopeFilterViewModel.ChangedKey, "Changed", insight.ChangedSymbolCount, string.Equals(selection.ScopeKey, SymbolBrowserSelection.ChangedKey, StringComparison.Ordinal)),
            new SymbolScopeFilterViewModel(SymbolScopeFilterViewModel.LinkedKey, "Linked", insight.LinkedSymbolCount, string.Equals(selection.ScopeKey, SymbolBrowserSelection.LinkedKey, StringComparison.Ordinal))
        ];
        SymbolKindFacets = insight.KindFacets
            .Select(facet => SemanticSymbolKindFacetViewModel.FromFacet(
                facet,
                string.Equals(selection.KindKey, facet.KindText, StringComparison.OrdinalIgnoreCase)))
            .ToImmutableArray();
        SymbolDocumentFacets = insight.DocumentFacets
            .Select(facet => SemanticSymbolDocumentFacetViewModel.FromFacet(
                facet,
                string.Equals(selection.DocumentId, facet.DocumentId.Value, StringComparison.Ordinal)))
            .ToImmutableArray();
        HotSemanticItems = insight.HotSymbols
            .Take(4)
            .Select(SemanticNavigationItemViewModel.FromItem)
            .ToImmutableArray();
    }

    private static string FormatCount(int count, string singular, string plural) => $"{count:N0} {(count == 1 ? singular : plural)}";

    private static string FormatThreadCount(int count) => FormatCount(count, "thread", "threads");
}
