using System.Collections.Immutable;

namespace SemanticDiff.Core;

public interface IDiffDocumentFactory
{
    DiffDocumentSnapshot CreateFromText(DiffDocumentMetadata metadata, string text, DiffLineKind lineKind = DiffLineKind.Context);

    DiffDocumentSnapshot CreateFromUnifiedDiff(DiffDocumentMetadata metadata, string unifiedDiff);
}

public interface IDocumentTokenizer
{
    string Id { get; }

    ValueTask<ImmutableArray<TokenSpan>> TokenizeLineAsync(
        DiffDocumentSnapshot document,
        DiffLine line,
        CancellationToken cancellationToken);

    ValueTask<ImmutableArray<DiffLine>> TokenizePageAsync(
        DiffDocumentSnapshot document,
        int firstLineIndex,
        int lineCount,
        CancellationToken cancellationToken);
}

public interface IGitDiffService
{
    Task<GitDiffSnapshot> GetDiffAsync(GitDiffRequest request, CancellationToken cancellationToken);

    Task<GitFileDiff> GetFileDiffAsync(GitDiffRequest request, GitFileChange fileChange, CancellationToken cancellationToken);

    Task<string> GetFileContentAsync(GitDiffRequest request, GitFileChange fileChange, CancellationToken cancellationToken);
}

public interface IGitRepositoryDiscovery
{
    Task<string?> DiscoverRootAsync(string startPath, CancellationToken cancellationToken);
}

public interface IGitDiffDocumentService
{
    Task<GitDiffDocumentSnapshot> LoadDocumentsAsync(GitDiffRequest request, int maxFiles, DiffContextMode contextMode, CancellationToken cancellationToken);
}

public interface IGitReviewService
{
    Task<GitReviewOperationResult> StageFileAsync(string repositoryPath, string path, CancellationToken cancellationToken);

    Task<GitReviewOperationResult> UnstageFileAsync(string repositoryPath, string path, CancellationToken cancellationToken);
}

public sealed record GitReviewOperationResult(bool Succeeded, string Message);

public interface IGitBlameService
{
    Task<GitFileBlame> GetFileBlameAsync(string repositoryPath, string path, CancellationToken cancellationToken);
}

public sealed record GitFileBlame(string Path, ImmutableArray<GitBlameLine> Lines)
{
    public static GitFileBlame Empty(string path) => new(path, ImmutableArray<GitBlameLine>.Empty);
}

public sealed record GitBlameLine(
    int LineNumber,
    string CommitId,
    string Author,
    DateTimeOffset? AuthorTime,
    string Summary);

public interface IRepositoryFileWatcher : IAsyncDisposable
{
    string RepositoryPath { get; }

    event EventHandler<RepositoryFileChangedEventArgs>? Changed;
}

public interface IRepositoryFileWatcherFactory
{
    IRepositoryFileWatcher Watch(string repositoryPath, RepositoryFileWatcherOptions options);
}

public sealed record RepositoryFileWatcherOptions(
    bool IncludeGitMetadata = true,
    ImmutableHashSet<string>? IgnoredDirectoryNames = null)
{
    public ImmutableHashSet<string> EffectiveIgnoredDirectoryNames => IgnoredDirectoryNames ?? ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "bin", "obj", "artifacts", ".vs");
}

public sealed record RepositoryFileChangedEventArgs(
    string FullPath,
    RepositoryFileChangeKind Kind);

public enum RepositoryFileChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed
}

public interface ISemanticProvider
{
    string Id { get; }

    bool CanAnalyze(GitFileChange fileChange);

    ValueTask<SemanticGraph> AnalyzeAsync(SemanticAnalysisRequest request, CancellationToken cancellationToken);
}

public interface IGraphLayoutEngine
{
    string Id { get; }

    ValueTask<GraphLayoutResult> LayoutAsync(GraphLayoutRequest request, CancellationToken cancellationToken);
}