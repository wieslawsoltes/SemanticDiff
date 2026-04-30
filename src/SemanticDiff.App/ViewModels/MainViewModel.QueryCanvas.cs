using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
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
        queryCanvas.QueryChanged += (_, _) => ScheduleQueryCanvasExecution(tab, TimeSpan.FromMilliseconds(260));
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
            previousOperation.Dispose();
        }

        var operation = new CancellationTokenSource();
        queryCanvasOperations[tab.Id] = operation;
        queryCanvas.SetExecuting();
        tab.StatusText = queryCanvas.StatusText;
        _ = ExecuteQueryCanvasAsync(tab, queryCanvas, delay, operation);
    }

    private async Task ExecuteQueryCanvasAsync(
        WorkspaceTabViewModel tab,
        QueryCanvasTabViewModel queryCanvas,
        TimeSpan delay,
        CancellationTokenSource operation)
    {
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
            var context = CreateQueryCanvasContext();
            var result = queryCanvasEngine.Execute(queryCanvas.QueryText, context, queryCanvas.Scope);
            operation.Token.ThrowIfCancellationRequested();
            PostToCapturedContext(() =>
            {
                if (operation.IsCancellationRequested)
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

            operation.Dispose();
        }
    }

    private QueryCanvasContext CreateQueryCanvasContext() => new(
        currentDocuments.IsDefault ? [] : currentDocuments,
        CreateWorkspaceQueryDocuments(),
        allSemanticNavigationItems.IsDefault ? [] : allSemanticNavigationItems,
        currentSemanticGraph,
        appState.LayoutMode,
        appState.GroupingMode,
        CreateEdgeOptions(),
        appState.EffectiveAnnotationVisibility);

    private ImmutableArray<DiffDocumentSnapshot> CreateWorkspaceQueryDocuments()
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
            operation.Dispose();
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
        operation.Dispose();
    }
}
