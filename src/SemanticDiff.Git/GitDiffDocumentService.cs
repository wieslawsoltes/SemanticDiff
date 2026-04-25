using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Git;

public sealed class GitDiffDocumentService : IGitDiffDocumentService
{
    private const int FullFileContextLines = 1_000_000;
    private readonly IGitDiffService gitDiffService;
    private readonly IDiffDocumentFactory documentFactory;

    public GitDiffDocumentService()
        : this(new GitDiffService(), new DiffDocumentFactory())
    {
    }

    public GitDiffDocumentService(IGitDiffService gitDiffService, IDiffDocumentFactory documentFactory)
    {
        this.gitDiffService = gitDiffService;
        this.documentFactory = documentFactory;
    }

    public async Task<GitDiffDocumentSnapshot> LoadDocumentsAsync(
        GitDiffRequest request,
        DiffContextMode contextMode,
        CancellationToken cancellationToken)
    {
        var gitSnapshot = await gitDiffService.GetDiffAsync(request, cancellationToken).ConfigureAwait(false);
        var diffRequest = contextMode == DiffContextMode.FullFileDiff
            ? request with { ContextLines = FullFileContextLines }
            : request;
        var documents = new DiffDocumentSnapshot[gitSnapshot.Files.Length];
        using var concurrency = new SemaphoreSlim(GetMaxConcurrentFileLoads(gitSnapshot.Files.Length));

        var loadTasks = gitSnapshot.Files.Select(async (fileChange, fileIndex) =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                documents[fileIndex] = await LoadDocumentAsync(request, diffRequest, contextMode, fileChange, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        });

        await Task.WhenAll(loadTasks).ConfigureAwait(false);
        return new GitDiffDocumentSnapshot(gitSnapshot, documents.ToImmutableArray());
    }

    private async Task<DiffDocumentSnapshot> LoadDocumentAsync(
        GitDiffRequest request,
        GitDiffRequest diffRequest,
        DiffContextMode contextMode,
        GitFileChange fileChange,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileDiff = await gitDiffService.GetFileDiffAsync(diffRequest, fileChange, cancellationToken).ConfigureAwait(false);
        var metadata = CreateMetadata(fileChange, fileDiff.UnifiedDiff);
        return contextMode == DiffContextMode.CurrentFile
            ? documentFactory.CreateFromText(metadata, await gitDiffService.GetFileContentAsync(request, fileChange, cancellationToken).ConfigureAwait(false))
            : documentFactory.CreateFromUnifiedDiff(metadata, fileDiff.UnifiedDiff);
    }

    private static int GetMaxConcurrentFileLoads(int fileCount) => Math.Min(fileCount, Math.Clamp(Environment.ProcessorCount / 2, 2, 6));

    private static DiffDocumentMetadata CreateMetadata(GitFileChange fileChange, string unifiedDiff)
    {
        var addedLines = 0;
        var deletedLines = 0;

        foreach (var line in unifiedDiff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('+'))
            {
                addedLines++;
            }
            else if (line.StartsWith('-'))
            {
                deletedLines++;
            }
        }

        return new DiffDocumentMetadata(
            new DiffDocumentId(fileChange.Path),
            fileChange.Path,
            fileChange.OldPath,
            fileChange.Status,
            fileChange.Language,
            addedLines,
            deletedLines);
    }
}