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
    public Task StageSelectedFileAsync() => RunReviewActionAsync(
        "Staging",
        (repositoryPath, path, cancellationToken) => gitReviewService.StageFileAsync(repositoryPath, path, cancellationToken));

    public Task UnstageSelectedFileAsync() => RunReviewActionAsync(
        "Unstaging",
        (repositoryPath, path, cancellationToken) => gitReviewService.UnstageFileAsync(repositoryPath, path, cancellationToken));

    public Task RefreshReviewDiscussionAsync() => RefreshReviewDiscussionAsync(CancellationToken.None);

    public async Task RefreshReviewDiscussionAsync(CancellationToken cancellationToken)
    {
        var option = SelectedPullRequestOption;
        if (option is null || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            ClearReviewDiscussion("Select a PR or MR");
            return;
        }

        var operation = BeginBackgroundOperation(
            $"Loading {option.KindText} review",
            cancellationToken,
            drivesGlobalProgress: false);
        try
        {
            reviewWorkflow.BeginLoad($"Loading {option.KindText} review");
            ReviewPanelStatusText = reviewWorkflow.StatusText;
            RefreshSceneAnnotations();
            ReportProgress(operation, 0.2, $"Loading {option.KindText} review threads");
            var snapshot = await gitReviewDiscussionService.GetDiscussionAsync(currentRepositoryPath, option.ToPullRequestInfo(), operation.Token);
            ReportProgress(operation, 0.82, "Preparing review annotations");
            reviewWorkflow.SetThreads(
                snapshot.Threads,
                snapshot.Threads.Select(ReviewThreadItemViewModel.FromThread).ToImmutableArray(),
                snapshot.StatusMessage);
            ApplyReviewThreadFilter();
            RefreshSceneAnnotations();
            ReviewPanelStatusText = reviewWorkflow.StatusText;
            CompleteOperation(operation, snapshot.StatusMessage);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            ReviewPanelStatusText = "Review load canceled";
            CompleteOperation(operation, "Review load canceled");
        }
        catch (Exception exception)
        {
            ReviewPanelStatusText = "Review unavailable";
            AddDiagnostic("Warning", $"Review discussion failed: {exception.Message}");
            CompleteOperation(operation, "Review load failed");
        }
    }

    public async Task AddReviewCommentAsync()
    {
        var option = SelectedPullRequestOption;
        if (option is null || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            ReviewPanelStatusText = "Select a PR or MR";
            return;
        }

        var body = ReviewCommentText.Trim();
        if (string.IsNullOrWhiteSpace(body))
        {
            ReviewPanelStatusText = "Comment is empty";
            return;
        }

        var operation = BeginOperation($"Adding {option.KindText} comment");
        try
        {
            var result = await gitReviewDiscussionService.AddCommentAsync(currentRepositoryPath, option.ToPullRequestInfo(), body, operation.Token);
            ReviewPanelStatusText = result.Message;
            AddDiagnostic(result.Succeeded ? "Info" : "Warning", result.Message);
            if (result.Succeeded)
            {
                ReviewCommentText = string.Empty;
                await RefreshReviewDiscussionAsync(operation.Token);
            }

            CompleteOperation(operation, result.Succeeded ? "Comment added" : "Comment failed");
        }
        catch (OperationCanceledException)
        {
            ReviewPanelStatusText = "Comment canceled";
            CompleteOperation(operation, "Comment canceled");
        }
        catch (Exception exception)
        {
            ReviewPanelStatusText = "Comment failed";
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "Comment failed");
        }
    }

    public async Task ReplyToSelectedReviewThreadAsync()
    {
        var option = SelectedPullRequestOption;
        var thread = SelectedReviewThreadItem;
        if (option is null || thread is null || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            ReviewPanelStatusText = "Select a review thread";
            return;
        }

        var body = ReviewCommentText.Trim();
        if (string.IsNullOrWhiteSpace(body))
        {
            ReviewPanelStatusText = "Reply is empty";
            return;
        }

        var operation = BeginOperation($"Replying to {option.KindText} thread");
        try
        {
            var result = await gitReviewDiscussionService.ReplyToThreadAsync(currentRepositoryPath, option.ToPullRequestInfo(), thread.Id, body, operation.Token);
            ReviewPanelStatusText = result.Message;
            AddDiagnostic(result.Succeeded ? "Info" : "Warning", result.Message);
            if (result.Succeeded)
            {
                ReviewCommentText = string.Empty;
                await RefreshReviewDiscussionAsync(operation.Token);
            }

            CompleteOperation(operation, result.Succeeded ? "Reply added" : "Reply failed");
        }
        catch (OperationCanceledException)
        {
            ReviewPanelStatusText = "Reply canceled";
            CompleteOperation(operation, "Reply canceled");
        }
        catch (Exception exception)
        {
            ReviewPanelStatusText = "Reply failed";
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "Reply failed");
        }
    }

    public async Task ToggleSelectedReviewThreadResolvedAsync()
    {
        var option = SelectedPullRequestOption;
        var thread = SelectedReviewThreadItem;
        if (option is null || thread is null || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            ReviewPanelStatusText = "Select a review thread";
            return;
        }

        var shouldResolve = !thread.IsResolved;
        var operation = BeginOperation(shouldResolve ? "Resolving review thread" : "Reopening review thread");
        try
        {
            var result = await gitReviewDiscussionService.SetThreadResolvedAsync(currentRepositoryPath, option.ToPullRequestInfo(), thread.Id, shouldResolve, operation.Token);
            ReviewPanelStatusText = result.Message;
            AddDiagnostic(result.Succeeded ? "Info" : "Warning", result.Message);
            if (result.Succeeded)
            {
                await RefreshReviewDiscussionAsync(operation.Token);
            }

            CompleteOperation(operation, result.Succeeded ? "Thread updated" : "Thread update failed");
        }
        catch (OperationCanceledException)
        {
            ReviewPanelStatusText = "Thread update canceled";
            CompleteOperation(operation, "Thread update canceled");
        }
        catch (Exception exception)
        {
            ReviewPanelStatusText = "Thread update failed";
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "Thread update failed");
        }
    }
}
