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
        int maxFiles,
        DiffContextMode contextMode,
        CancellationToken cancellationToken)
    {
        var gitSnapshot = await gitDiffService.GetDiffAsync(request, cancellationToken).ConfigureAwait(false);
        var documentCapacity = Math.Min(Math.Max(0, maxFiles), gitSnapshot.Files.Length);
        var documents = ImmutableArray.CreateBuilder<DiffDocumentSnapshot>(documentCapacity);
        var diffRequest = contextMode == DiffContextMode.FullFileDiff
            ? request with { ContextLines = FullFileContextLines }
            : request;

        foreach (var fileChange in SelectFilesForInitialLoad(gitSnapshot.Files, maxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileDiff = await gitDiffService.GetFileDiffAsync(diffRequest, fileChange, cancellationToken).ConfigureAwait(false);
            var metadata = CreateMetadata(fileChange, fileDiff.UnifiedDiff);
            var document = contextMode == DiffContextMode.CurrentFile
                ? documentFactory.CreateFromText(metadata, await gitDiffService.GetFileContentAsync(request, fileChange, cancellationToken).ConfigureAwait(false))
                : documentFactory.CreateFromUnifiedDiff(metadata, fileDiff.UnifiedDiff);
            documents.Add(document);
        }

        return new GitDiffDocumentSnapshot(gitSnapshot, documents.ToImmutable());
    }

    private static IEnumerable<GitFileChange> SelectFilesForInitialLoad(ImmutableArray<GitFileChange> files, int maxFiles)
    {
        var limit = Math.Max(0, maxFiles);
        if (limit == 0 || files.IsDefaultOrEmpty)
        {
            return [];
        }

        if (files.Length <= limit)
        {
            return files;
        }

        return files
            .Select((file, index) => new { File = file, Index = index })
            .OrderBy(item => GetInitialLoadPriority(item.File.Status))
            .ThenBy(item => item.Index)
            .Take(limit)
            .Select(item => item.File);
    }

    private static int GetInitialLoadPriority(DiffFileStatus status) => status switch
    {
        DiffFileStatus.Conflicted => 0,
        DiffFileStatus.Added or DiffFileStatus.Untracked or DiffFileStatus.Copied => 1,
        DiffFileStatus.Renamed => 2,
        DiffFileStatus.Deleted => 3,
        DiffFileStatus.Modified => 4,
        _ => 5
    };

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