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

        return FocusExplorerItem(item);
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

        return string.IsNullOrWhiteSpace(node.DocumentId)
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

        return string.IsNullOrWhiteSpace(node.DocumentId)
            ? OpenBlameTabByPathAsync(node.Path)
            : OpenBlameTabAsync(node.DocumentId);
    }

    public async Task OpenFileDiffTabAsync(string documentId, FileDiffDisplayMode displayMode)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        var sourceDocumentId = ResolveSourceDocumentId(documentId);
        var document = currentDocuments.FirstOrDefault(document => string.Equals(document.Id.Value, sourceDocumentId, StringComparison.Ordinal));
        if (document is null)
        {
            AddDiagnostic("Warning", $"No document node for {sourceDocumentId}");
            return;
        }

        await OpenFileDiffTabAsync(document, displayMode);
    }

    public async Task OpenBlameTabAsync(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        var sourceDocumentId = ResolveSourceDocumentId(documentId);
        var document = currentDocuments.FirstOrDefault(document => string.Equals(document.Id.Value, sourceDocumentId, StringComparison.Ordinal));
        if (document is null)
        {
            AddDiagnostic("Warning", $"No document node for {sourceDocumentId}");
            return;
        }

        await OpenBlameTabAsync(document);
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
            AddDiagnostic("Warning", $"No document node for {path}");
            return;
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
            AddDiagnostic("Warning", $"No document node for {path}");
            return;
        }

        await OpenBlameTabAsync(document);
    }

    private async Task OpenFileDiffTabAsync(DiffDocumentSnapshot document, FileDiffDisplayMode displayMode)
    {
        var tabId = $"file:{document.Id.Value}";
        if (FindWorkspaceTab(tabId) is { FileDiff: not null } existingTab)
        {
            existingTab.FileDiff.SetDisplayMode(displayMode);
            SelectedWorkspaceTab = existingTab;
            return;
        }

        var fullText = await LoadFullFileTextAsync(document, CancellationToken.None);
        var fullFileDocument = await CreateTokenizedFullFileDocumentAsync(document, fullText, CancellationToken.None);
        var foldRegions = new CodeFoldingService().CreateFoldRegions(fullFileDocument);
        var fileDiff = FileDiffTabViewModel.FromDocument(document, fullFileDocument, fullText, foldRegions, displayMode);
        var tab = WorkspaceTabViewModel.CreateFileDiff(tabId, Path.GetFileName(document.Metadata.Path), document.Metadata.Path, fileDiff);
        AddWorkspaceTab(tab);
        AddDiagnostic("Info", $"Opened file diff tab for {document.Metadata.Path}");
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
        tab.IsLoading = true;
        tab.StatusText = $"Loading blame for {path}";
        var operation = BeginOperation($"Loading blame for {path}");
        try
        {
            var blameRevision = GetActiveBlameRevision();
            var blameTask = gitBlameService.GetFileBlameAsync(currentRepositoryPath, path, operation.Token, blameRevision);
            var historyTask = gitHistoryService.GetHistoryAsync(
                new GitHistoryRequest(currentRepositoryPath, blameRevision ?? "HEAD", MaxCount: 160, PathFilter: path),
                operation.Token);
            await Task.WhenAll(blameTask, historyTask);

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
        finally
        {
            tab.IsLoading = false;
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

    public void RevealDocumentInExplorer(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        var sourceDocumentId = ResolveSourceDocumentId(documentId);
        var item = allExplorerItems.FirstOrDefault(item => string.Equals(item.DocumentId, sourceDocumentId, StringComparison.Ordinal));
        if (item is null)
        {
            AddDiagnostic("Warning", $"No file tree item for {sourceDocumentId}");
            return;
        }

        FileSearchText = string.Empty;
        ExpandAncestors(item.Path);
        ApplyExplorerFilter();
        SelectExplorerItem(item);
        SelectedRailTabIndex = 0;
        AddDiagnostic("Info", $"Revealed {item.Path} in the file tree");
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
