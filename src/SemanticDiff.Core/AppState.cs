using System.Text.Json;
using System.Text.Json.Serialization;

namespace SemanticDiff.Core;

public sealed record SemanticDiffAppState(
    string? RepositoryPath = null,
    GitDiffScope DiffScope = GitDiffScope.Worktree,
    bool WatchRepositoryChanges = true,
    int AutoReloadDelayMs = 700,
    SemanticDiffThemeMode ThemeMode = SemanticDiffThemeMode.Dark,
    DiffContextMode DiffContextMode = DiffContextMode.ChangedHunks,
    DiffReviewMode ReviewMode = DiffReviewMode.Precise,
    bool CollapseUnchangedContext = false,
    DiffNodeLayoutState[]? LayoutNodes = null,
    string? BaseRef = null,
    string? HeadRef = null,
    bool ShowSemanticEdges = true,
    DiffAnnotationVisibilityState? AnnotationVisibility = null,
    SemanticAnalysisMode SemanticAnalysisMode = SemanticAnalysisMode.WorkspaceThenSyntax,
    GraphLayoutMode LayoutMode = GraphLayoutMode.Layered,
    GraphGroupingMode GroupingMode = GraphGroupingMode.Folder,
    GitReviewRequestState ReviewRequestState = GitReviewRequestState.Open,
    string? SelectedBranchRef = null,
    int? SelectedPullRequestNumber = null,
    bool UseInteractiveLevelOfDetail = true,
    bool EnableTokenization = true,
    string[]? IncludedFileTypeKeys = null,
    CodeCompletionMode CodeCompletionMode = CodeCompletionMode.LanguageServicesThenDocument,
    double LeftPaneWidth = 260,
    WorkspaceSessionState? WorkspaceSession = null)
{
    public DiffNodeLayoutState[] EffectiveLayoutNodes => LayoutNodes ?? [];

    public DiffAnnotationVisibilityState EffectiveAnnotationVisibility => AnnotationVisibility ?? DiffAnnotationVisibilityState.Default;

    public string[]? EffectiveIncludedFileTypeKeys => IncludedFileTypeKeys;

    public WorkspaceSessionState EffectiveWorkspaceSession => WorkspaceSession ?? WorkspaceSessionState.Empty;
}

public enum SemanticDiffThemeMode
{
    Dark,
    Light
}

public enum CodeCompletionMode
{
    LanguageServicesThenDocument,
    DocumentOnly
}

public sealed record DiffNodeLayoutState(
    string DocumentId,
    double X,
    double Y,
    double Width,
    double Height,
    bool IsPinned,
    double FontSize = 12.5)
{
    public DiffNodeLayout ToLayout() => new(new DiffDocumentId(DocumentId), new Rect2(X, Y, Width, Height), IsPinned, FontSize);

    public static DiffNodeLayoutState FromLayout(DiffNodeLayout layout) => new(
        layout.DocumentId.Value,
        layout.Bounds.X,
        layout.Bounds.Y,
        layout.Bounds.Width,
        layout.Bounds.Height,
        layout.IsPinned,
        layout.FontSize);
}

public enum WorkspaceSessionTabKind
{
    Graph,
    GitHistory,
    FileDiff,
    Blame,
    SymbolGraph,
    EditorCanvas,
    QueryCanvas,
    PatchCompare
}

public enum WorkspaceSessionFileExplorerMode
{
    Diff,
    Workspace
}

public sealed record WorkspaceSessionState(
    int Version = 1,
    string? RepositoryPath = null,
    string? SelectedTabId = null,
    string? SelectedExplorerDocumentId = null,
    string? FileSearchText = null,
    WorkspaceSessionFileExplorerMode FileExplorerMode = WorkspaceSessionFileExplorerMode.Diff,
    string? GitReferenceSearchText = null,
    string? ReviewSearchText = null,
    string? SymbolSearchText = null,
    WorkspaceTabState[]? Tabs = null)
{
    public static WorkspaceSessionState Empty { get; } = new();

    public WorkspaceTabState[] EffectiveTabs => Tabs ?? [];
}

public sealed record WorkspaceTabState(
    string Id,
    WorkspaceSessionTabKind Kind,
    string Header,
    string DetailText,
    bool IsClosable,
    string? StatusText = null,
    WorkspaceCanvasState? Canvas = null,
    GitDiffRequest? GraphRequest = null,
    string? GraphBranchReferenceName = null,
    GitPullRequestInfo? GraphReviewRequest = null,
    GitHistoryTabState? History = null,
    FileDiffTabState? FileDiff = null,
    BlameTabState? Blame = null,
    SymbolGraphTabState? SymbolGraph = null,
    EditorCanvasTabState? EditorCanvas = null,
    QueryCanvasTabState? QueryCanvas = null,
    PatchCompareTabState? PatchCompare = null);

public sealed record WorkspaceCanvasState(
    double CameraOffsetX = 32,
    double CameraOffsetY = 32,
    double CameraScale = 1,
    bool ShowFullFileNodes = false,
    bool EnableNodeEditing = false,
    WorkspaceNodeState[]? Nodes = null)
{
    public WorkspaceNodeState[] EffectiveNodes => Nodes ?? [];
}

public sealed record WorkspaceNodeState(
    string DocumentId,
    double X,
    double Y,
    double Width,
    double Height,
    double ScrollOffsetY = 0,
    bool IsSelected = false,
    bool IsPinned = false,
    double FontSize = 12.5,
    bool? FullFileViewOverride = null,
    bool? EditingOverride = null,
    int CaretLineIndex = 0,
    int CaretColumn = 0,
    string? FullText = null);

public sealed record GitHistoryTabState(
    GitHistoryRequest Request,
    int LoadedCount = 0);

public sealed record FileDiffTabState(
    string Path,
    string DocumentId,
    FileDiffDisplayState DisplayMode = FileDiffDisplayState.DiffOnly,
    FileDiffScopeState ScopeMode = FileDiffScopeState.Changes,
    bool IsDiffAnnotationEnabled = true,
    bool IsEditingEnabled = false,
    double CodeFontSize = 15,
    string? FullText = null);

public enum FileDiffDisplayState
{
    DiffOnly,
    FullFile
}

public enum FileDiffScopeState
{
    Changes,
    FullFileDiff
}

public sealed record BlameTabState(
    string Path,
    string Language,
    BlameDisplayState DisplayMode = BlameDisplayState.Timeline,
    bool IsTimelineExpanded = true);

public enum BlameDisplayState
{
    Timeline,
    ChangeGraph
}

public sealed record SymbolGraphTabState(
    string SearchText = "",
    string ScopeKey = "All",
    string KindKey = "All",
    string DocumentKey = "All",
    string EdgeKindKey = "All",
    GraphLayoutMode LayoutMode = GraphLayoutMode.Layered,
    GraphGroupingMode GroupingMode = GraphGroupingMode.Semantic,
    SymbolGraphDisplayState ViewMode = SymbolGraphDisplayState.SymbolsOnly,
    string? FocusAnchorId = null);

public enum SymbolGraphDisplayState
{
    SymbolsOnly,
    FilesAndSymbols
}

public sealed record EditorCanvasTabState(
    EditorCanvasDocumentState[]? Documents = null)
{
    public EditorCanvasDocumentState[] EffectiveDocuments => Documents ?? [];
}

public sealed record EditorCanvasDocumentState(
    string Path,
    string? FullText = null);

public sealed record QueryCanvasTabState(
    string QueryText = "",
    string Scope = "Diff",
    string? SampleName = null);

public sealed record PatchCompareTabState(
    string OldRangeText = "",
    string NewRangeText = "",
    string WizardRepositoryText = "",
    string WizardFilterText = "",
    string? ComparisonRepositoryPath = null);

public interface IAppStateStore
{
    Task<SemanticDiffAppState> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(SemanticDiffAppState state, CancellationToken cancellationToken);
}

public sealed class JsonAppStateStore : IAppStateStore
{
    public JsonAppStateStore(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public static JsonAppStateStore CreateDefault()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appDataPath))
        {
            appDataPath = AppContext.BaseDirectory;
        }

        return new JsonAppStateStore(Path.Combine(appDataPath, "SemanticDiff", "app-state.json"));
    }

    public async Task<SemanticDiffAppState> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new SemanticDiffAppState();
            }

            await using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync(stream, SemanticDiffJsonContext.Default.SemanticDiffAppState, cancellationToken).ConfigureAwait(false)
                ?? new SemanticDiffAppState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SemanticDiffAppState();
        }
    }

    public async Task SaveAsync(SemanticDiffAppState state, CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        await using var stream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(stream, state, SemanticDiffJsonContext.Default.SemanticDiffAppState, cancellationToken).ConfigureAwait(false);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SemanticDiffAppState))]
[JsonSerializable(typeof(WorkspaceSessionState))]
[JsonSerializable(typeof(WorkspaceTabState[]))]
internal sealed partial class SemanticDiffJsonContext : JsonSerializerContext;
