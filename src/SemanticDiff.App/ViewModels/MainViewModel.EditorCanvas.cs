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
    private const double EditorCanvasFolderIndent = 180;
    private const double EditorCanvasDropCascadeOffset = 38;
    private static readonly string[] EditorCanvasIgnoredDirectoryNames =
    [
        ".git",
        ".idea",
        ".vs",
        ".vscode",
        "artifacts",
        "bin",
        "node_modules",
        "obj"
    ];

    public WorkspaceTabViewModel OpenEditorCanvasTab(string? title = null, string? detailText = null)
    {
        var tabId = $"editor:{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        var scene = CreateEditorCanvasScene([], null);
        var canvasTitle = string.IsNullOrWhiteSpace(title) ? "Editor Canvas" : title.Trim();
        var canvasDetail = string.IsNullOrWhiteSpace(detailText) ? "Editable file graph" : detailText.Trim();
        var editorCanvas = new EditorCanvasTabViewModel(
            canvasTitle,
            "Drag files here to create editable code nodes",
            scene);
        var tab = WorkspaceTabViewModel.CreateEditorCanvas(tabId, canvasTitle, canvasDetail, editorCanvas);
        AddWorkspaceTab(tab);
        AddDiagnostic("Info", "Opened empty editor canvas");
        return tab;
    }

    public Task AddFileToEditorCanvasAsync(FileExplorerNodeViewModel? node) =>
        AddExplorerNodeToEditorCanvasAsync(node);

    public async Task AddExplorerNodeToEditorCanvasAsync(FileExplorerNodeViewModel? node)
    {
        var paths = await GetEditorCanvasPathsForExplorerNodeAsync(node, CancellationToken.None);
        await AddFilesToEditorCanvasAsync(paths, null, null, CancellationToken.None, autoLayoutByFolder: true);
    }

    public async Task AddExplorerNodeToNewEditorCanvasAsync(FileExplorerNodeViewModel? node)
    {
        var paths = await GetEditorCanvasPathsForExplorerNodeAsync(node, CancellationToken.None);
        if (paths.IsDefaultOrEmpty)
        {
            return;
        }

        var title = FormatEditorCanvasTitle(node);
        var detail = node?.IsFile == true
            ? node.Path
            : $"{node?.Path}/ | {paths.Length:N0} files";
        var tab = OpenEditorCanvasTab(title, detail);
        await AddFilesToEditorCanvasAsync(paths, tab, null, CancellationToken.None, autoLayoutByFolder: true);
    }

    public Task AddFileToEditorCanvasAsync(string path) =>
        AddFilesToEditorCanvasAsync([path], null, null, CancellationToken.None);

    public async Task AddFilesToEditorCanvasAsync(
        IEnumerable<string> paths,
        WorkspaceTabViewModel? targetTab,
        Point2? dropWorldPoint,
        CancellationToken cancellationToken,
        bool autoLayoutByFolder = false)
    {
        var tab = ResolveEditorCanvasTab(targetTab);
        if (tab.EditorCanvas is not { } editorCanvas)
        {
            return;
        }

        var operation = BeginTabOperation(
            tab,
            "Preparing editor canvas files",
            cancellationToken,
            drivesGlobalProgress: true);
        try
        {
            var candidatePaths = await Task.Run(() => ExpandEditorCanvasInputPaths(paths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => NormalizeRepositoryPath(path), StringComparer.OrdinalIgnoreCase)
                .ToArray(), operation.Token);
            if (candidatePaths.Length == 0)
            {
                CompleteOperation(operation, "No files to add");
                return;
            }

            var builder = editorCanvas.Documents.IsDefault
                ? ImmutableArray.CreateBuilder<EditorCanvasDocument>()
                : editorCanvas.Documents.ToBuilder();
            var existingIds = builder
                .Select(document => document.Document.Id.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = 0;

            for (var index = 0; index < candidatePaths.Length; index++)
            {
                var path = candidatePaths[index];
                operation.Token.ThrowIfCancellationRequested();
                ReportProgress(
                    operation,
                    0.12 + (candidatePaths.Length == 0 ? 0 : (double)index / candidatePaths.Length) * 0.68,
                    $"Loading editor node {index + 1:N0}/{candidatePaths.Length:N0}: {ShortenPath(path)}");
                var editorDocument = await CreateEditorCanvasDocumentAsync(path, operation.Token);
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
                CompleteOperation(operation, "No new editor nodes added");
                return;
            }

            ReportProgress(operation, 0.9, "Laying out editor canvas");
            var documents = builder.ToImmutable();
            var scene = await CreateEditorCanvasSceneAsync(editorCanvas, documents, dropWorldPoint, autoLayoutByFolder, operation.Token);
            editorCanvas.SetDocuments(documents, scene);
            tab.StatusText = editorCanvas.StatusText;
            SelectedWorkspaceTab = tab;
            AddDiagnostic("Info", $"Added {added:N0} file{(added == 1 ? string.Empty : "s")} to editor canvas");
            CompleteOperation(operation, $"Added {added:N0} editor node{(added == 1 ? string.Empty : "s")}");
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(operation, "Editor canvas add canceled");
        }
        catch (Exception exception)
        {
            AddDiagnostic("Error", exception.Message);
            CompleteOperation(operation, "Editor canvas add failed");
        }
    }

    private ImmutableArray<string> ExpandEditorCanvasInputPaths(IEnumerable<string> paths)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            AddEditorCanvasInputPath(path, builder, seen);
        }

        return builder.ToImmutable();
    }

    private void AddEditorCanvasInputPath(
        string? path,
        ImmutableArray<string>.Builder builder,
        ISet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var trimmedPath = path.Trim();
        if (TryAddExplorerFolderPaths(trimmedPath, builder, seen))
        {
            return;
        }

        string? absolutePath = null;
        if (TryGetRepositoryRelativePath(trimmedPath, out var repositoryRelativePath))
        {
            absolutePath = ResolveLocalFilePath(trimmedPath, repositoryRelativePath);
        }
        else if (Path.IsPathRooted(trimmedPath))
        {
            absolutePath = trimmedPath;
        }
        else if (!string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            absolutePath = Path.Combine(currentRepositoryPath, NormalizeRepositoryPath(trimmedPath));
        }

        if (!string.IsNullOrWhiteSpace(absolutePath) && Directory.Exists(absolutePath))
        {
            AddEditorCanvasDirectoryFiles(absolutePath, builder, seen);
            return;
        }

        AddEditorCanvasFilePath(
            string.IsNullOrWhiteSpace(repositoryRelativePath) ? trimmedPath : repositoryRelativePath,
            builder,
            seen);
    }

    private bool TryAddExplorerFolderPaths(
        string path,
        ImmutableArray<string>.Builder builder,
        ISet<string> seen)
    {
        var folderPath = NormalizeRepositoryPath(path);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        var folderPrefix = $"{folderPath}/";
        var matches = allExplorerItems
            .Where(item =>
            {
                var itemPath = NormalizeRepositoryPath(item.Path);
                return itemPath.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase);
            })
            .Select(item => NormalizeRepositoryPath(item.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(itemPath => itemPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var match in matches)
        {
            AddEditorCanvasFilePath(match, builder, seen);
        }

        return matches.Length > 0;
    }

    private void AddEditorCanvasDirectoryFiles(
        string directoryPath,
        ImmutableArray<string>.Builder builder,
        ISet<string> seen)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
                .Where(path => !ShouldSkipEditorCanvasPath(path))
                .OrderBy(path => NormalizeRepositoryPath(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            AddDiagnostic("Warning", $"Folder drop failed: {exception.Message}");
            return;
        }

        foreach (var file in files)
        {
            var path = TryGetRepositoryRelativePath(file, out var repositoryRelativePath)
                ? repositoryRelativePath
                : file;
            AddEditorCanvasFilePath(path, builder, seen);
        }
    }

    private static bool ShouldSkipEditorCanvasPath(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => EditorCanvasIgnoredDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static void AddEditorCanvasFilePath(
        string path,
        ImmutableArray<string>.Builder builder,
        ISet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var normalizedPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : NormalizeRepositoryPath(path);
        if (seen.Add(normalizedPath))
        {
            builder.Add(normalizedPath);
        }
    }

    private async Task<ImmutableArray<string>> GetEditorCanvasPathsForExplorerNodeAsync(
        FileExplorerNodeViewModel? node,
        CancellationToken cancellationToken)
    {
        if (node is null)
        {
            return [];
        }

        if (node.IsFile)
        {
            return string.IsNullOrWhiteSpace(node.Path)
                ? []
                : ImmutableArray.Create(NormalizeRepositoryPath(node.Path));
        }

        var explorerItems = allExplorerItems;
        var nodePath = node.Path;
        var paths = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderPath = NormalizeRepositoryPath(nodePath);
            var folderPrefix = string.IsNullOrWhiteSpace(folderPath) ? string.Empty : $"{folderPath}/";
            return explorerItems
                .Where(item => string.IsNullOrWhiteSpace(folderPrefix) ||
                    NormalizeRepositoryPath(item.Path).StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(item => NormalizeRepositoryPath(item.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
        }, cancellationToken);

        if (paths.IsDefaultOrEmpty)
        {
            AddDiagnostic("Info", $"Folder {node.Path} has no files in the current file explorer mode");
        }

        return paths;
    }

    private static string FormatEditorCanvasTitle(FileExplorerNodeViewModel? node)
    {
        if (node is null)
        {
            return "Editor Canvas";
        }

        var name = string.IsNullOrWhiteSpace(node.Name) ? node.Path : node.Name;
        return $"Edit {name}";
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
        var fullDocument = await CreateFullFileDocumentAsync(sourceDocument, fullText, appState.EnableTokenization, cancellationToken);
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
        Point2? dropWorldPoint,
        bool autoLayoutByFolder)
    {
        var existingLayouts = editorCanvas.Scene.GetCurrentLayout();
        return CreateEditorCanvasScene(documents, BuildEditorCanvasLayout(documents, existingLayouts, dropWorldPoint, autoLayoutByFolder));
    }

    private Task<DiffCanvasScene> CreateEditorCanvasSceneAsync(
        EditorCanvasTabViewModel editorCanvas,
        ImmutableArray<EditorCanvasDocument> documents,
        Point2? dropWorldPoint,
        bool autoLayoutByFolder,
        CancellationToken cancellationToken)
    {
        var existingLayouts = editorCanvas.Scene.GetCurrentLayout();
        var edgeOptions = CreateEdgeOptions();
        var annotationVisibility = appState.EffectiveAnnotationVisibility;
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var layout = BuildEditorCanvasLayout(documents, existingLayouts, dropWorldPoint, autoLayoutByFolder);
            return CreateEditorCanvasScene(documents, layout, edgeOptions, annotationVisibility);
        }, cancellationToken);
    }

    private DiffCanvasScene CreateEditorCanvasScene(
        ImmutableArray<EditorCanvasDocument> documents,
        GraphLayoutResult? layout) =>
        CreateEditorCanvasScene(documents, layout, CreateEdgeOptions(), appState.EffectiveAnnotationVisibility);

    private static DiffCanvasScene CreateEditorCanvasScene(
        ImmutableArray<EditorCanvasDocument> documents,
        GraphLayoutResult? layout,
        EdgeProjectionOptions edgeOptions,
        DiffAnnotationVisibilityState annotationVisibility)
    {
        var snapshots = documents.Select(document => document.Document).ToImmutableArray();
        var scene = DiffCanvasScene.FromDocuments(
            snapshots,
            SemanticGraph.Empty,
            layout,
            edgeOptions,
            [],
            annotationVisibility,
            GraphGroupingMode.Folder);
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
        Point2? dropWorldPoint,
        bool autoLayoutByFolder)
    {
        if (documents.IsDefaultOrEmpty)
        {
            return null;
        }

        if (autoLayoutByFolder)
        {
            return BuildFolderStructuredEditorCanvasLayout(documents);
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

    private static GraphLayoutResult BuildFolderStructuredEditorCanvasLayout(ImmutableArray<EditorCanvasDocument> documents)
    {
        var orderedDocuments = documents
            .OrderBy(document => NormalizeRepositoryPath(document.Document.Metadata.Path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var root = new EditorCanvasFolderNode(string.Empty, string.Empty, depth: 0);
        foreach (var document in orderedDocuments)
        {
            root.Add(document);
        }

        var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(documents.Length);
        var y = 0.0;
        LayoutEditorCanvasFolder(root, ref y, builder);
        return new GraphLayoutResult(builder.ToImmutable());
    }

    private static void LayoutEditorCanvasFolder(
        EditorCanvasFolderNode folder,
        ref double y,
        ImmutableArray<DiffNodeLayout>.Builder builder)
    {
        var documents = folder.Documents
            .OrderBy(document => document.Document.Metadata.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (documents.Length > 0)
        {
            var baseX = Math.Max(0, folder.Depth - 1) * EditorCanvasFolderIndent;
            for (var index = 0; index < documents.Length; index++)
            {
                var document = documents[index];
                var x = baseX + index * (EditorCanvasNodeWidth + EditorCanvasNodeGap);
                builder.Add(new DiffNodeLayout(
                    document.Document.Id,
                    new Rect2(x, y, EditorCanvasNodeWidth, EditorCanvasNodeHeight),
                    IsPinned: false,
                    FontSize: 14));
            }

            y += EditorCanvasNodeHeight + EditorCanvasNodeGap;
        }

        foreach (var child in folder.Children.Values)
        {
            LayoutEditorCanvasFolder(child, ref y, builder);
        }
    }

    private sealed class EditorCanvasFolderNode
    {
        public EditorCanvasFolderNode(string name, string path, int depth)
        {
            Name = name;
            Path = path;
            Depth = depth;
        }

        public string Name { get; }

        public string Path { get; }

        public int Depth { get; }

        public SortedDictionary<string, EditorCanvasFolderNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<EditorCanvasDocument> Documents { get; } = [];

        public int DescendantDocumentCount => Children.Values.Sum(child => child.Documents.Count + child.DescendantDocumentCount);

        public void Add(EditorCanvasDocument document)
        {
            var normalizedPath = NormalizeRepositoryPath(document.Document.Metadata.Path);
            var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length <= 1)
            {
                Documents.Add(document);
                return;
            }

            var current = this;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var segment = segments[index];
                var childPath = string.IsNullOrWhiteSpace(current.Path) ? segment : $"{current.Path}/{segment}";
                if (!current.Children.TryGetValue(segment, out var child))
                {
                    child = new EditorCanvasFolderNode(segment, childPath, current.Depth + 1);
                    current.Children.Add(segment, child);
                }

                current = child;
            }

            current.Documents.Add(document);
        }
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
