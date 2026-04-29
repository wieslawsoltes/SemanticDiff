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
        UpdateFileExplorerModeStatus(filtered.Length);
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
        if (FileExplorerMode == mode && (mode == FileExplorerMode.Diff || !workspaceExplorerItems.IsDefaultOrEmpty))
        {
            return;
        }

        FileExplorerMode = mode;
        IsDiffFileExplorerModeSelected = mode == FileExplorerMode.Diff;
        IsWorkspaceFileExplorerModeSelected = mode == FileExplorerMode.Workspace;
        UpdateFileExplorerModeLabels();

        if (mode == FileExplorerMode.Diff)
        {
            SetActiveExplorerItems(diffExplorerItems);
            return;
        }

        if (IsWorkspaceExplorerCacheValid())
        {
            SetActiveExplorerItems(workspaceExplorerItems);
            return;
        }

        await LoadWorkspaceExplorerAsync(CancellationToken.None);
    }

    private async Task LoadWorkspaceExplorerAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            workspaceExplorerItems = [];
            workspaceExplorerRepositoryPath = null;
            workspaceExplorerWorkspacePath = null;
            SetActiveExplorerItems(workspaceExplorerItems);
            FileExplorerModeStatusText = "Open a repository to load the MSBuild workspace";
            return;
        }

        var repositoryPath = currentRepositoryPath;
        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previousOperation = Interlocked.Exchange(ref currentWorkspaceExplorerOperation, operation);
        previousOperation?.Cancel();
        previousOperation?.Dispose();

        try
        {
            if (FileExplorerMode == FileExplorerMode.Workspace)
            {
                SetActiveExplorerItems([]);
            }

            FileExplorerModeStatusText = "Loading MSBuild workspace...";
            var result = await workspaceFileDiscoveryService.LoadFilesAsync(repositoryPath, operation.Token).ConfigureAwait(false);
            var items = result.Files
                .Select(file => new ExplorerItemViewModel(file.Path, file.Status, file.Language))
                .ToImmutableArray();

            PostToCapturedContext(() =>
            {
                if (operation.IsCancellationRequested ||
                    !string.Equals(currentRepositoryPath, repositoryPath, StringComparison.Ordinal))
                {
                    return;
                }

                workspaceExplorerItems = items;
                workspaceExplorerRepositoryPath = repositoryPath;
                workspaceExplorerWorkspacePath = result.WorkspacePath;
                if (FileExplorerMode == FileExplorerMode.Workspace)
                {
                    SetActiveExplorerItems(workspaceExplorerItems);
                }

                var workspaceName = string.IsNullOrWhiteSpace(result.WorkspacePath)
                    ? "MSBuild workspace"
                    : Path.GetFileName(result.WorkspacePath);
                if (workspaceExplorerItems.IsDefaultOrEmpty)
                {
                    FileExplorerModeStatusText = "No MSBuild workspace files found";
                }
                else if (FileExplorerMode != FileExplorerMode.Workspace)
                {
                    FileExplorerModeStatusText = $"{workspaceName} | {workspaceExplorerItems.Length:N0} files";
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            PostToCapturedContext(() =>
            {
                if (!string.Equals(currentRepositoryPath, repositoryPath, StringComparison.Ordinal))
                {
                    return;
                }

                workspaceExplorerItems = [];
                workspaceExplorerRepositoryPath = repositoryPath;
                workspaceExplorerWorkspacePath = null;
                if (FileExplorerMode == FileExplorerMode.Workspace)
                {
                    SetActiveExplorerItems(workspaceExplorerItems);
                }

                FileExplorerModeStatusText = $"MSBuild workspace unavailable: {exception.Message}";
                AddDiagnostic("Warning", FileExplorerModeStatusText);
            });
        }
        finally
        {
            if (ReferenceEquals(currentWorkspaceExplorerOperation, operation))
            {
                currentWorkspaceExplorerOperation = null;
            }

            operation.Dispose();
        }
    }

    private void SetActiveExplorerItems(ImmutableArray<ExplorerItemViewModel> items)
    {
        allExplorerItems = items.IsDefault ? ImmutableArray<ExplorerItemViewModel>.Empty : items;
        ApplyExplorerFilter();
    }

    private bool IsWorkspaceExplorerCacheValid() =>
        !workspaceExplorerItems.IsDefaultOrEmpty &&
        !string.IsNullOrWhiteSpace(currentRepositoryPath) &&
        string.Equals(workspaceExplorerRepositoryPath, currentRepositoryPath, StringComparison.Ordinal);

    private void InvalidateWorkspaceExplorerCache()
    {
        var operation = Interlocked.Exchange(ref currentWorkspaceExplorerOperation, null);
        operation?.Cancel();
        operation?.Dispose();
        workspaceExplorerItems = [];
        workspaceExplorerRepositoryPath = null;
        workspaceExplorerWorkspacePath = null;
    }

    private void UpdateFileExplorerModeLabels()
    {
        FileExplorerTitleText = FileExplorerMode == FileExplorerMode.Workspace ? "MSBuild Workspace" : "Changed Files";
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
                ? "MSBuild workspace"
                : Path.GetFileName(workspaceExplorerWorkspacePath);
            FileExplorerModeStatusText = workspaceExplorerItems.IsDefaultOrEmpty
                ? "MSBuild workspace"
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
