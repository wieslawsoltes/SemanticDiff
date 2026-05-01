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

    private async Task<DiffDocumentSnapshot?> CreateWorkspaceFileDocumentAsync(string path, CancellationToken cancellationToken)
    {
        if (currentGitSnapshot is null || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return null;
        }

        var normalizedPath = NormalizeRepositoryPath(path);
        var language = LanguageFromPath(normalizedPath);
        var fileChange = new GitFileChange(normalizedPath, null, DiffFileStatus.Unchanged, 0, 0, language);
        string text;
        try
        {
            text = await new GitDiffService().GetFileContentAsync(currentGitSnapshot.Request, fileChange, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AddDiagnostic("Warning", $"Workspace file load failed: {exception.Message}");
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
        return await CreateTokenizedFullFileDocumentAsync(document, text, cancellationToken);
    }

    private async Task<bool> HasRepositoryFileAsync(string path, CancellationToken cancellationToken)
    {
        if (currentGitSnapshot is null || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return false;
        }

        if (File.Exists(Path.Combine(currentRepositoryPath, path)))
        {
            return true;
        }

        var result = await new GitCommandRunner()
            .RunAsync(currentRepositoryPath, ["cat-file", "-e", $"{ResolveRepositoryContentRevision()}:{path}"], cancellationToken)
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
            SelectedWorkspaceTab = existingTab;
            return;
        }

        var operation = BeginBackgroundOperation($"Opening file tab for {document.Metadata.Path}");
        try
        {
            ReportProgress(operation, 0.18, $"Loading full text for {document.Metadata.Path}");
            var fullText = await LoadFullFileTextAsync(document, operation.Token);
            ReportProgress(operation, 0.46, "Tokenizing file view");
            var fullFileDocument = await CreateTokenizedFullFileDocumentAsync(document, fullText, operation.Token);
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
