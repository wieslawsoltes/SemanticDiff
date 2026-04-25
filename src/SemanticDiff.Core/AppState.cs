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
    double LeftPaneWidth = 260)
{
    public DiffNodeLayoutState[] EffectiveLayoutNodes => LayoutNodes ?? [];

    public DiffAnnotationVisibilityState EffectiveAnnotationVisibility => AnnotationVisibility ?? DiffAnnotationVisibilityState.Default;
}

public enum SemanticDiffThemeMode
{
    Dark,
    Light
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
internal sealed partial class SemanticDiffJsonContext : JsonSerializerContext;