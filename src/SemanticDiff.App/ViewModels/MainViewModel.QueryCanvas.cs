using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Rendering;
using SemanticDiff.Semantics;
using SemanticDiff.Workbench.Query;

namespace SemanticDiff.App.ViewModels;

public sealed partial class MainViewModel
{
    public WorkspaceTabViewModel OpenQueryCanvasTab()
    {
        var queryCanvas = new QueryCanvasTabViewModel(
            "Query Canvas",
            "LINQ over files, symbols, and graph nodes",
            queryCanvasCompletionProvider);
        var tab = WorkspaceTabViewModel.CreateQueryCanvas(
            $"query:{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}",
            "Query Canvas",
            "Live LINQ graph",
            queryCanvas);
        queryCanvas.QueryChanged += (_, _) =>
        {
            ScheduleQueryCanvasExecution(tab, TimeSpan.FromMilliseconds(260));
            RequestWorkspaceSessionSave();
        };
        AddWorkspaceTab(tab);
        ScheduleQueryCanvasExecution(tab, TimeSpan.Zero);
        AddDiagnostic("Info", "Opened query canvas");
        return tab;
    }

    public void RunSelectedQueryCanvas()
    {
        if (SelectedWorkspaceTab is { Kind: WorkspaceTabKind.QueryCanvas } tab)
        {
            ScheduleQueryCanvasExecution(tab, TimeSpan.Zero);
        }
    }

    public void ResetSelectedQueryCanvas()
    {
        if (SelectedWorkspaceTab?.QueryCanvas is not { } queryCanvas)
        {
            return;
        }

        queryCanvas.QueryText = QueryCanvasTabViewModel.DefaultQuery;
        RunSelectedQueryCanvas();
    }

    private void ScheduleQueryCanvasExecution(WorkspaceTabViewModel tab, TimeSpan delay)
    {
        if (tab.QueryCanvas is not { } queryCanvas)
        {
            return;
        }

        if (queryCanvasOperations.TryGetValue(tab.Id, out var previousOperation))
        {
            previousOperation.Cancel();
        }

        var operation = BeginTabOperation(
            tab,
            "Running query",
            drivesGlobalProgress: false,
            logDiagnostic: false);
        queryCanvasOperations[tab.Id] = operation;
        queryCanvas.SetExecuting();
        tab.StatusText = queryCanvas.StatusText;
        ReportIndeterminate(operation, queryCanvas.StatusText);
        _ = ExecuteQueryCanvasAsync(tab, queryCanvas, delay, operation);
    }

    private async Task ExecuteQueryCanvasAsync(
        WorkspaceTabViewModel tab,
        QueryCanvasTabViewModel queryCanvas,
        TimeSpan delay,
        CancellationTokenSource operation)
    {
        var completionMessage = "Query canceled";
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, operation.Token).ConfigureAwait(false);
            }

            if (queryCanvas.Scope == QueryCanvasScope.Workspace && !IsWorkspaceExplorerCacheValid())
            {
                await LoadWorkspaceExplorerAsync(operation.Token).ConfigureAwait(false);
            }

            operation.Token.ThrowIfCancellationRequested();
            var queryText = queryCanvas.QueryText;
            var queryScope = queryCanvas.Scope;
            var diffDocuments = currentDocuments.IsDefault ? [] : currentDocuments;
            var workspaceItems = workspaceExplorerItems;
            var explorerItems = allExplorerItems;
            var changedItems = diffExplorerItems;
            var semanticItems = allSemanticNavigationItems.IsDefault ? [] : allSemanticNavigationItems;
            var semanticGraph = currentSemanticGraph;
            var layoutMode = appState.LayoutMode;
            var groupingMode = appState.GroupingMode;
            var edgeOptions = CreateEdgeOptions();
            var annotationVisibility = appState.EffectiveAnnotationVisibility;
            ReportProgress(operation, 0.35, "Preparing query context");
            var result = await Task.Run(
                () =>
                {
                    var context = CreateQueryCanvasContext(
                        diffDocuments,
                        workspaceItems,
                        explorerItems,
                        changedItems,
                        semanticItems,
                        semanticGraph,
                        layoutMode,
                        groupingMode,
                        edgeOptions,
                        annotationVisibility);
                    operation.Token.ThrowIfCancellationRequested();
                    return queryCanvasEngine.Execute(queryText, context, queryScope);
                },
                operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            completionMessage = result.StatusText;
            ReportProgress(operation, 0.82, "Rendering query canvas");
            PostToCapturedContext(() =>
            {
                if (!CanReportOperation(operation))
                {
                    return;
                }

                queryCanvas.SetResult(result);
                tab.StatusText = result.StatusText;
                if (result.HasError)
                {
                    AddDiagnostic("Warning", result.StatusText);
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            completionMessage = exception.Message;
            PostToCapturedContext(() =>
            {
                var result = QueryCanvasExecutionResult.Error(exception.Message);
                queryCanvas.SetResult(result);
                tab.StatusText = result.StatusText;
                AddDiagnostic("Error", exception.Message);
            });
        }
        finally
        {
            if (queryCanvasOperations.TryGetValue(tab.Id, out var current) && ReferenceEquals(current, operation))
            {
                queryCanvasOperations.Remove(tab.Id);
            }

            CompleteOperation(operation, completionMessage);
        }
    }

    private static QueryCanvasContext CreateQueryCanvasContext(
        ImmutableArray<DiffDocumentSnapshot> diffDocuments,
        ImmutableArray<ExplorerItemViewModel> workspaceItems,
        ImmutableArray<ExplorerItemViewModel> explorerItems,
        ImmutableArray<ExplorerItemViewModel> changedItems,
        ImmutableArray<SemanticNavigationItem> semanticItems,
        SemanticGraph semanticGraph,
        GraphLayoutMode layoutMode,
        GraphGroupingMode groupingMode,
        EdgeProjectionOptions edgeOptions,
        DiffAnnotationVisibilityState annotationVisibility) => new(
        diffDocuments,
        CreateWorkspaceQueryDocuments(diffDocuments, workspaceItems, explorerItems, changedItems),
        semanticItems,
        semanticGraph,
        layoutMode,
        groupingMode,
        edgeOptions,
        annotationVisibility);

    private static ImmutableArray<DiffDocumentSnapshot> CreateWorkspaceQueryDocuments(
        ImmutableArray<DiffDocumentSnapshot> currentDocuments,
        ImmutableArray<ExplorerItemViewModel> workspaceExplorerItems,
        ImmutableArray<ExplorerItemViewModel> allExplorerItems,
        ImmutableArray<ExplorerItemViewModel> diffExplorerItems)
    {
        var sourceItems = !workspaceExplorerItems.IsDefaultOrEmpty
            ? workspaceExplorerItems
            : (!allExplorerItems.IsDefaultOrEmpty ? allExplorerItems : diffExplorerItems);
        if (sourceItems.IsDefaultOrEmpty)
        {
            return currentDocuments.IsDefault ? [] : currentDocuments;
        }

        var currentDocumentsByPath = currentDocuments.IsDefault
            ? new Dictionary<string, DiffDocumentSnapshot>(StringComparer.OrdinalIgnoreCase)
            : currentDocuments
                .GroupBy(document => NormalizeRepositoryPath(document.Metadata.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var factory = new DiffDocumentFactory();
        var builder = ImmutableArray.CreateBuilder<DiffDocumentSnapshot>(sourceItems.Length);

        foreach (var item in sourceItems)
        {
            var normalizedPath = NormalizeRepositoryPath(item.Path);
            if (currentDocumentsByPath.TryGetValue(normalizedPath, out var currentDocument))
            {
                builder.Add(currentDocument);
                continue;
            }

            var metadata = new DiffDocumentMetadata(
                new DiffDocumentId(normalizedPath),
                normalizedPath,
                null,
                item.Status,
                string.IsNullOrWhiteSpace(item.Language) ? LanguageFromPath(normalizedPath) : item.Language,
                0,
                0);
            var text = $"// {normalizedPath}{Environment.NewLine}// {metadata.Language} workspace file";
            builder.Add(factory.CreateFromText(metadata, text, DiffLineKind.Context));
        }

        return builder.ToImmutable();
    }

    private void CancelQueryCanvasOperations()
    {
        foreach (var operation in queryCanvasOperations.Values)
        {
            operation.Cancel();
        }

        queryCanvasOperations.Clear();
    }

    private void CancelQueryCanvasOperation(string tabId)
    {
        if (!queryCanvasOperations.Remove(tabId, out var operation))
        {
            return;
        }

        operation.Cancel();
    }
}
