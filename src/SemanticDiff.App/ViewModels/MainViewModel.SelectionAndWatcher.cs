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
    private void SelectExplorerItem(ExplorerItemViewModel? item)
    {
        if (ExplorerItemsEqual(selectedExplorerItem, item))
        {
            return;
        }

        selectedExplorerItem = item;
        UpdateSelectedExplorerTreeNode();
        currentBlameOperation?.Cancel();
        HasSelectedRepositoryFile = item is not null && !string.IsNullOrWhiteSpace(currentRepositoryPath);
        SelectedFileReviewText = item is null ? "Select a changed file" : ShortenPath(item.Path);
        ReviewActionStatusText = HasSelectedRepositoryFile ? "Ready" : "No repository file selected";
        BlameSummaryText = HasSelectedRepositoryFile ? "Loading blame..." : "Blame unavailable";

        if (HasSelectedRepositoryFile && item is not null)
        {
            _ = LoadBlameSummaryAsync(item.Path);
        }

        RefreshSceneAnnotations();
    }

    private static bool ExplorerItemsEqual(ExplorerItemViewModel? left, ExplorerItemViewModel? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(left.DocumentId, right.DocumentId, StringComparison.Ordinal) &&
            string.Equals(left.Path, right.Path, StringComparison.Ordinal);
    }

    private async Task LoadBlameSummaryAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return;
        }

        var blameOperation = new CancellationTokenSource();
        var previousOperation = Interlocked.Exchange(ref currentBlameOperation, blameOperation);
        previousOperation?.Cancel();
        previousOperation?.Dispose();

        try
        {
            var blame = await gitBlameService.GetFileBlameAsync(currentRepositoryPath, path, blameOperation.Token, GetActiveBlameRevision()).ConfigureAwait(false);
            PostToCapturedContext(() => ApplyBlameSummary(path, blame));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            PostToCapturedContext(() =>
            {
                BlameSummaryText = "Blame unavailable";
                AddDiagnostic("Warning", $"Blame failed: {exception.Message}");
                RefreshSceneAnnotations();
            });
        }
        finally
        {
            if (ReferenceEquals(currentBlameOperation, blameOperation))
            {
                currentBlameOperation = null;
            }

            blameOperation.Dispose();
        }
    }

    private void ApplyBlameSummary(string path, GitFileBlame blame)
    {
        if (selectedExplorerItem is null || !string.Equals(selectedExplorerItem.Path, path, StringComparison.Ordinal))
        {
            return;
        }

        if (blame.Lines.IsDefaultOrEmpty)
        {
            BlameSummaryText = "No blame data";
            RefreshSceneAnnotations();
            return;
        }

        var topAuthors = blame.Lines
            .GroupBy(line => line.Author, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(2)
            .Select(group => $"{group.Key} {group.Count():N0}");
        var latest = blame.Lines
            .Where(line => line.AuthorTime is not null)
            .OrderByDescending(line => line.AuthorTime)
            .FirstOrDefault();
        var latestText = latest is null
            ? "latest unknown"
            : $"latest {latest.Author} {latest.AuthorTime!.Value:yyyy-MM-dd} {ShortCommit(latest.CommitId)}";
        BlameSummaryText = $"Blame {string.Join(", ", topAuthors)} | {latestText}";
        RefreshSceneAnnotations();
    }

    private static string ShortCommit(string commitId) => commitId.Length <= 8 ? commitId : commitId[..8];

    private async Task RunReviewActionAsync(
        string operationName,
        Func<string, string, CancellationToken, Task<GitReviewOperationResult>> action)
    {
        if (selectedExplorerItem is null || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            ReviewActionStatusText = "Select a repository file";
            AddDiagnostic("Warning", "Select a changed file before running a review action");
            return;
        }

        var path = selectedExplorerItem.Path;
        var operation = BeginOperation($"{operationName} {path}");
        try
        {
            var result = await action(currentRepositoryPath, path, operation.Token);
            if (!result.Succeeded)
            {
                ReviewActionStatusText = "Action failed";
                AddDiagnostic("Error", result.Message);
                RefreshSceneAnnotations();
                CompleteOperation(operation, "Review action failed");
                return;
            }

            ReviewActionStatusText = result.Message;
            AddDiagnostic("Info", result.Message);
            RefreshSceneAnnotations();
            CompleteOperation(operation, "Review action complete");
            await LoadRepositoryAsync(loadAppState: false, operationMessage: "Reloading review state");
        }
        catch (OperationCanceledException)
        {
            ReviewActionStatusText = "Action canceled";
            CompleteOperation(operation, "Review action canceled");
        }
        catch (Exception exception)
        {
            ReviewActionStatusText = "Action failed";
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "Review action failed");
        }
    }

    private async Task RestartRepositoryWatcherAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        await StopRepositoryWatcherAsync();
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsAutoRefreshEnabled || string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            WatchStatusText = IsAutoRefreshEnabled ? "Watch unavailable" : "Watch off";
            RefreshSceneAnnotations();
            return;
        }

        try
        {
            repositoryFileWatcher = repositoryFileWatcherFactory.Watch(repositoryPath, new RepositoryFileWatcherOptions());
            repositoryFileWatcher.Changed += OnRepositoryFileChanged;
            WatchStatusText = "Watching";
            RefreshSceneAnnotations();
        }
        catch (Exception exception)
        {
            WatchStatusText = "Watch unavailable";
            AddDiagnostic("Warning", $"File watcher unavailable: {exception.Message}");
            RefreshSceneAnnotations();
        }
    }

    private async ValueTask StopRepositoryWatcherAsync()
    {
        pendingAutoReload?.Cancel();
        var watcher = repositoryFileWatcher;
        if (watcher is null)
        {
            return;
        }

        watcher.Changed -= OnRepositoryFileChanged;
        await watcher.DisposeAsync();
        if (ReferenceEquals(repositoryFileWatcher, watcher))
        {
            repositoryFileWatcher = null;
        }
    }

    private void OnRepositoryFileChanged(object? sender, RepositoryFileChangedEventArgs args)
    {
        var relativePath = currentRepositoryPath is null
            ? args.FullPath
            : Path.GetRelativePath(currentRepositoryPath, args.FullPath).Replace('\\', '/');
        PostToCapturedContext(() => WatchStatusText = $"Changed {ShortenPath(relativePath)}");
        PostToCapturedContext(RefreshSceneAnnotations);
        ScheduleRepositoryReload(relativePath);
    }

    private void ScheduleRepositoryReload(string relativePath)
    {
        var reloadTokenSource = new CancellationTokenSource();
        var previousTokenSource = Interlocked.Exchange(ref pendingAutoReload, reloadTokenSource);
        previousTokenSource?.Cancel();
        _ = DebounceRepositoryReloadAsync(reloadTokenSource, relativePath);
    }

    private async Task DebounceRepositoryReloadAsync(CancellationTokenSource reloadTokenSource, string relativePath)
    {
        try
        {
            await Task.Delay(Math.Clamp(appState.AutoReloadDelayMs, 250, 10_000), reloadTokenSource.Token).ConfigureAwait(false);
            await RunOnCapturedContextAsync(async () =>
            {
                if (!IsAutoRefreshEnabled)
                {
                    return;
                }

                AddDiagnostic("Info", $"Auto refresh after {ShortenPath(relativePath)}");
                await LoadRepositoryAsync(loadAppState: false, operationMessage: "Auto refreshing repository", Scene.CaptureViewState());
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(pendingAutoReload, reloadTokenSource))
            {
                pendingAutoReload = null;
            }

            reloadTokenSource.Dispose();
        }
    }

    private void PostToCapturedContext(Action action)
    {
        if (synchronizationContext is null || SynchronizationContext.Current == synchronizationContext)
        {
            action();
            return;
        }

        synchronizationContext.Post(_ => action(), null);
    }

    private Task RunOnCapturedContextAsync(Func<Task> action)
    {
        if (synchronizationContext is null || SynchronizationContext.Current == synchronizationContext)
        {
            return action();
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        synchronizationContext.Post(async _ =>
        {
            try
            {
                await action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }, null);
        return completion.Task;
    }

    private static string ShortenPath(string path) => path.Length <= 72 ? path : $"...{path[^69..]}";

    private static string NormalizeRepositoryPath(string path) => path.Replace('\\', '/').Trim('/');
}
