using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Rendering;
using SemanticDiff.Semantics;
using SemanticDiff.Workbench.FileDiff;

namespace SemanticDiff.App.ViewModels;

public sealed partial class MainViewModel
{
    private const double EditorCanvasNodeWidth = 820;
    private const double EditorCanvasNodeHeight = 560;
    private const double EditorCanvasNodeGap = 80;
    private const double EditorCanvasDropCascadeOffset = 38;

    public WorkspaceTabViewModel OpenEditorCanvasTab()
    {
        var tabId = $"editor:{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        var scene = CreateEditorCanvasScene([], null);
        var editorCanvas = new EditorCanvasTabViewModel(
            "Editor Canvas",
            "Drag files here to create editable code nodes",
            scene);
        var tab = WorkspaceTabViewModel.CreateEditorCanvas(tabId, "Editor Canvas", "Editable file graph", editorCanvas);
        AddWorkspaceTab(tab);
        AddDiagnostic("Info", "Opened empty editor canvas");
        return tab;
    }

    public async Task AddFileToEditorCanvasAsync(FileExplorerNodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        if (!node.IsFile)
        {
            ToggleExplorerNode(node);
            return;
        }

        await AddFilesToEditorCanvasAsync([node.Path], null, null, CancellationToken.None);
    }

    public Task AddFileToEditorCanvasAsync(string path) =>
        AddFilesToEditorCanvasAsync([path], null, null, CancellationToken.None);

    public async Task AddFilesToEditorCanvasAsync(
        IEnumerable<string> paths,
        WorkspaceTabViewModel? targetTab,
        Point2? dropWorldPoint,
        CancellationToken cancellationToken)
    {
        var candidatePaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidatePaths.Length == 0)
        {
            return;
        }

        var tab = ResolveEditorCanvasTab(targetTab);
        if (tab.EditorCanvas is not { } editorCanvas)
        {
            return;
        }

        var builder = editorCanvas.Documents.IsDefault
            ? ImmutableArray.CreateBuilder<EditorCanvasDocument>()
            : editorCanvas.Documents.ToBuilder();
        var existingIds = builder
            .Select(document => document.Document.Id.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var path in candidatePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var editorDocument = await CreateEditorCanvasDocumentAsync(path, cancellationToken);
            if (editorDocument is null)
            {
                AddDiagnostic("Warning", $"Could not add {path} to editor canvas");
                continue;
            }

            if (!existingIds.Add(editorDocument.Document.Id.Value))
            {
                AddDiagnostic("Info", $"{editorDocument.Document.Metadata.Path} is already on the editor canvas");
                continue;
            }

            builder.Add(editorDocument);
            added++;
        }

        if (added == 0)
        {
            SelectedWorkspaceTab = tab;
            return;
        }

        var documents = builder.ToImmutable();
        var scene = CreateEditorCanvasScene(editorCanvas, documents, dropWorldPoint);
        editorCanvas.SetDocuments(documents, scene);
        tab.StatusText = editorCanvas.StatusText;
        SelectedWorkspaceTab = tab;
        AddDiagnostic("Info", $"Added {added:N0} file{(added == 1 ? string.Empty : "s")} to editor canvas");
    }

    private WorkspaceTabViewModel ResolveEditorCanvasTab(WorkspaceTabViewModel? targetTab)
    {
        if (targetTab?.Kind == WorkspaceTabKind.EditorCanvas)
        {
            return targetTab;
        }

        if (SelectedWorkspaceTab?.Kind == WorkspaceTabKind.EditorCanvas)
        {
            return SelectedWorkspaceTab;
        }

        var existing = WorkspaceTabs.FirstOrDefault(tab => tab.Kind == WorkspaceTabKind.EditorCanvas);
        return existing ?? OpenEditorCanvasTab();
    }

    private async Task<EditorCanvasDocument?> CreateEditorCanvasDocumentAsync(string path, CancellationToken cancellationToken)
    {
        var normalizedInput = NormalizeRepositoryPath(path);
        var repositoryRelativePath = TryGetRepositoryRelativePath(path, out var relativePath)
            ? relativePath
            : (!Path.IsPathRooted(path) ? normalizedInput : null);
        var currentDocument = repositoryRelativePath is null
            ? null
            : currentDocuments.FirstOrDefault(document =>
                string.Equals(NormalizeRepositoryPath(document.Metadata.Path), repositoryRelativePath, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(document.Metadata.OldPath) &&
                    string.Equals(NormalizeRepositoryPath(document.Metadata.OldPath), repositoryRelativePath, StringComparison.OrdinalIgnoreCase)));

        if (currentDocument is not null)
        {
            var fullText = await LoadFullFileTextAsync(currentDocument, cancellationToken);
            return await CreateEditorCanvasDocumentAsync(currentDocument, fullText, cancellationToken);
        }

        if (repositoryRelativePath is not null)
        {
            var workspaceDocument = await CreateWorkspaceFileDocumentAsync(repositoryRelativePath, cancellationToken);
            if (workspaceDocument is not null)
            {
                return CreateEditorCanvasDocument(workspaceDocument, workspaceDocument.ToSourceText(), cancellationToken);
            }
        }

        var absolutePath = ResolveLocalFilePath(path, repositoryRelativePath);
        if (absolutePath is null || !File.Exists(absolutePath))
        {
            return null;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddDiagnostic("Warning", $"File load failed: {exception.Message}");
            return null;
        }

        var displayPath = repositoryRelativePath ?? absolutePath.Replace('\\', '/');
        var metadata = new DiffDocumentMetadata(
            new DiffDocumentId(displayPath),
            displayPath,
            null,
            DiffFileStatus.Unchanged,
            LanguageFromPath(displayPath),
            0,
            0);
        var sourceDocument = new DiffDocumentFactory().CreateFromText(metadata, text, DiffLineKind.Context);
        return await CreateEditorCanvasDocumentAsync(sourceDocument, text, cancellationToken);
    }

    private async Task<EditorCanvasDocument> CreateEditorCanvasDocumentAsync(
        DiffDocumentSnapshot sourceDocument,
        string fullText,
        CancellationToken cancellationToken)
    {
        var fullDocument = await CreateTokenizedFullFileDocumentAsync(sourceDocument, fullText, cancellationToken);
        return CreateEditorCanvasDocument(fullDocument, fullText, cancellationToken);
    }

    private static EditorCanvasDocument CreateEditorCanvasDocument(
        DiffDocumentSnapshot fullDocument,
        string fullText,
        CancellationToken cancellationToken)
    {
        var foldRegions = CreateFoldRegions(fullDocument, cancellationToken);
        var fileView = new FileDiffDocumentBuilder().Build(fullDocument, fullDocument, fullText, foldRegions);
        var annotatedDocument = fullDocument with { Lines = fileView.AnnotatedFullFileLines };
        return new EditorCanvasDocument(annotatedDocument, fileView.FullText, fileView.FoldRegions);
    }

    private DiffCanvasScene CreateEditorCanvasScene(
        EditorCanvasTabViewModel editorCanvas,
        ImmutableArray<EditorCanvasDocument> documents,
        Point2? dropWorldPoint)
    {
        var existingLayouts = editorCanvas.Scene.GetCurrentLayout();
        return CreateEditorCanvasScene(documents, BuildEditorCanvasLayout(documents, existingLayouts, dropWorldPoint));
    }

    private DiffCanvasScene CreateEditorCanvasScene(
        ImmutableArray<EditorCanvasDocument> documents,
        GraphLayoutResult? layout)
    {
        var snapshots = documents.Select(document => document.Document).ToImmutableArray();
        var scene = DiffCanvasScene.FromDocuments(
            snapshots,
            SemanticGraph.Empty,
            layout,
            CreateEdgeOptions(),
            [],
            appState.EffectiveAnnotationVisibility,
            GraphGroupingMode.None);
        scene.SetFullFileDocuments(documents.Select(document =>
            new DiffNodeFullFileContent(
                document.Document.Id,
                document.Document,
                document.FoldRegions,
                document.FullText)));
        scene.SetShowFullFileNodes(true);
        scene.SetNodeEditingEnabled(true);
        return scene;
    }

    private static GraphLayoutResult? BuildEditorCanvasLayout(
        ImmutableArray<EditorCanvasDocument> documents,
        ImmutableArray<DiffNodeLayout> existingLayouts,
        Point2? dropWorldPoint)
    {
        if (documents.IsDefaultOrEmpty)
        {
            return null;
        }

        var existingByDocumentId = existingLayouts.IsDefault
            ? new Dictionary<DiffDocumentId, DiffNodeLayout>()
            : existingLayouts.ToDictionary(layout => layout.DocumentId, layout => layout);
        var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(documents.Length);
        var newIndex = 0;

        foreach (var editorDocument in documents)
        {
            if (existingByDocumentId.TryGetValue(editorDocument.Document.Id, out var existingLayout))
            {
                builder.Add(existingLayout);
                continue;
            }

            var fallbackIndex = builder.Count;
            var point = dropWorldPoint is { } dropPoint
                ? dropPoint.Translate(newIndex * EditorCanvasDropCascadeOffset, newIndex * EditorCanvasDropCascadeOffset)
                : new Point2(
                    (fallbackIndex % 3) * (EditorCanvasNodeWidth + EditorCanvasNodeGap),
                    (fallbackIndex / 3) * (EditorCanvasNodeHeight + EditorCanvasNodeGap));
            builder.Add(new DiffNodeLayout(
                editorDocument.Document.Id,
                new Rect2(point.X, point.Y, EditorCanvasNodeWidth, EditorCanvasNodeHeight),
                IsPinned: false,
                FontSize: 14));
            newIndex++;
        }

        return new GraphLayoutResult(builder.ToImmutable());
    }

    private bool TryGetRepositoryRelativePath(string path, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var repositoryPath = Path.GetFullPath(currentRepositoryPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(repositoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
        {
            return false;
        }

        relativePath = NormalizeRepositoryPath(Path.GetRelativePath(repositoryPath, fullPath));
        return !string.IsNullOrWhiteSpace(relativePath);
    }

    private string? ResolveLocalFilePath(string path, string? repositoryRelativePath)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        if (!string.IsNullOrWhiteSpace(repositoryRelativePath) && !string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return Path.Combine(currentRepositoryPath, repositoryRelativePath);
        }

        return null;
    }
}
