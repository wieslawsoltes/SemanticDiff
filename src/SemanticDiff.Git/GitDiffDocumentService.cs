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
        var diffRequest = contextMode == DiffContextMode.FullFileDiff
            ? request with { ContextLines = FullFileContextLines }
            : request;
        var gitSnapshotTask = gitDiffService.GetDiffAsync(request, cancellationToken);
        var batchedFileDiffsTask = ShouldLoadBatchedDiffConcurrently(request)
            ? LoadBatchedFileDiffsAsync(diffRequest, cancellationToken)
            : null;

        var gitSnapshot = await gitSnapshotTask.ConfigureAwait(false);
        IReadOnlyDictionary<string, BatchedFileDiff> batchedFileDiffs;
        if (gitSnapshot.Files.IsDefaultOrEmpty)
        {
            if (batchedFileDiffsTask is not null)
            {
                await batchedFileDiffsTask.ConfigureAwait(false);
            }

            batchedFileDiffs = new Dictionary<string, BatchedFileDiff>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            batchedFileDiffs = batchedFileDiffsTask is not null
                ? await batchedFileDiffsTask.ConfigureAwait(false)
                : await LoadBatchedFileDiffsAsync(diffRequest, cancellationToken).ConfigureAwait(false);
        }

        if (contextMode != DiffContextMode.CurrentFile)
        {
            var batchedDocuments = TryLoadBatchedDocuments(gitSnapshot.Files, batchedFileDiffs, cancellationToken);
            if (batchedDocuments is not null)
            {
                return new GitDiffDocumentSnapshot(gitSnapshot, batchedDocuments.Value);
            }
        }

        var documents = new DiffDocumentSnapshot[gitSnapshot.Files.Length];
        using var concurrency = new SemaphoreSlim(GetMaxConcurrentFileLoads(gitSnapshot.Files.Length));

        var loadTasks = gitSnapshot.Files.Select(async (fileChange, fileIndex) =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                documents[fileIndex] = await LoadDocumentAsync(request, diffRequest, contextMode, fileChange, batchedFileDiffs, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        });

        await Task.WhenAll(loadTasks).ConfigureAwait(false);
        return new GitDiffDocumentSnapshot(gitSnapshot, documents.ToImmutableArray());
    }

    private static bool ShouldLoadBatchedDiffConcurrently(GitDiffRequest request) =>
        request.Scope is GitDiffScope.CommitRange or GitDiffScope.Custom ||
        request.Scope == GitDiffScope.Branch && !string.IsNullOrWhiteSpace(request.BaseRef);

    private ImmutableArray<DiffDocumentSnapshot>? TryLoadBatchedDocuments(
        ImmutableArray<GitFileChange> files,
        IReadOnlyDictionary<string, BatchedFileDiff> batchedFileDiffs,
        CancellationToken cancellationToken)
    {
        if (files.IsDefaultOrEmpty)
        {
            return ImmutableArray<DiffDocumentSnapshot>.Empty;
        }

        foreach (var file in files)
        {
            if (!TryGetBatchedFileDiff(batchedFileDiffs, file, out _))
            {
                return null;
            }
        }

        var documents = new DiffDocumentSnapshot[files.Length];
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Min(files.Length, Math.Clamp(Environment.ProcessorCount, 2, 8))
        };

        Parallel.For(0, files.Length, parallelOptions, fileIndex =>
        {
            var fileChange = files[fileIndex];
            if (!TryGetBatchedFileDiff(batchedFileDiffs, fileChange, out var batchedDiff))
            {
                throw new InvalidOperationException($"Missing batched git diff for '{fileChange.Path}'.");
            }

            var metadata = CreateMetadata(fileChange, batchedDiff);
            documents[fileIndex] = documentFactory.CreateFromUnifiedDiff(metadata, batchedDiff.UnifiedDiff);
        });

        return documents.ToImmutableArray();
    }

    private async Task<DiffDocumentSnapshot> LoadDocumentAsync(
        GitDiffRequest request,
        GitDiffRequest diffRequest,
        DiffContextMode contextMode,
        GitFileChange fileChange,
        IReadOnlyDictionary<string, BatchedFileDiff> batchedFileDiffs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BatchedFileDiff batchedDiff;
        if (!TryGetBatchedFileDiff(batchedFileDiffs, fileChange, out batchedDiff))
        {
            var fileDiff = await gitDiffService.GetFileDiffAsync(diffRequest, fileChange, cancellationToken).ConfigureAwait(false);
            batchedDiff = CreateBatchedFileDiff(fileDiff.UnifiedDiff);
        }

        var metadata = CreateMetadata(fileChange, batchedDiff);
        return contextMode == DiffContextMode.CurrentFile
            ? documentFactory.CreateFromText(metadata, await gitDiffService.GetFileContentAsync(request, fileChange, cancellationToken).ConfigureAwait(false))
            : documentFactory.CreateFromUnifiedDiff(metadata, batchedDiff.UnifiedDiff);
    }

    private static int GetMaxConcurrentFileLoads(int fileCount) => Math.Min(fileCount, Math.Clamp(Environment.ProcessorCount / 2, 2, 6));

    private async Task<IReadOnlyDictionary<string, BatchedFileDiff>> LoadBatchedFileDiffsAsync(
        GitDiffRequest diffRequest,
        CancellationToken cancellationToken)
    {
        var unifiedDiff = await gitDiffService.GetUnifiedDiffAsync(diffRequest, cancellationToken).ConfigureAwait(false);
        return SplitUnifiedDiffByPath(unifiedDiff);
    }

    private static bool TryGetBatchedFileDiff(
        IReadOnlyDictionary<string, BatchedFileDiff> fileDiffs,
        GitFileChange fileChange,
        out BatchedFileDiff batchedDiff)
    {
        if (fileDiffs.TryGetValue(NormalizePathKey(fileChange.Path), out batchedDiff!))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(fileChange.OldPath) &&
            fileDiffs.TryGetValue(NormalizePathKey(fileChange.OldPath), out batchedDiff!))
        {
            return true;
        }

        batchedDiff = BatchedFileDiff.Empty;
        return false;
    }

    private static IReadOnlyDictionary<string, BatchedFileDiff> SplitUnifiedDiffByPath(string unifiedDiff)
    {
        if (string.IsNullOrWhiteSpace(unifiedDiff))
        {
            return new Dictionary<string, BatchedFileDiff>(StringComparer.OrdinalIgnoreCase);
        }

        var chunks = new Dictionary<string, BatchedFileDiff>(StringComparer.OrdinalIgnoreCase);
        var chunkStart = 0;
        var activePath = string.Empty;
        var addedLines = 0;
        var deletedLines = 0;

        for (var lineStart = 0; lineStart < unifiedDiff.Length;)
        {
            var nextLineStart = GetNextLineStart(unifiedDiff, lineStart, out var lineLength);
            var line = unifiedDiff.AsSpan(lineStart, lineLength);

            if (line.StartsWith("diff --git ".AsSpan(), StringComparison.Ordinal) && lineStart > chunkStart)
            {
                AddChunk(chunks, activePath, unifiedDiff, chunkStart, lineStart, addedLines, deletedLines);
                chunkStart = lineStart;
                activePath = string.Empty;
                addedLines = 0;
                deletedLines = 0;
            }

            if (line.StartsWith("+++ ".AsSpan(), StringComparison.Ordinal))
            {
                activePath = ParseDiffHeaderPath(line[4..]) ?? activePath;
            }
            else if (string.IsNullOrWhiteSpace(activePath) && line.StartsWith("--- ".AsSpan(), StringComparison.Ordinal))
            {
                activePath = ParseDiffHeaderPath(line[4..]) ?? activePath;
            }

            if (line.Length > 0)
            {
                if (line[0] == '+' && !IsFileHeaderLine(line))
                {
                    addedLines++;
                }
                else if (line[0] == '-' && !IsFileHeaderLine(line))
                {
                    deletedLines++;
                }
            }

            lineStart = nextLineStart;
        }

        AddChunk(chunks, activePath, unifiedDiff, chunkStart, unifiedDiff.Length, addedLines, deletedLines);

        return chunks;
    }

    private static int GetNextLineStart(string text, int lineStart, out int lineLength)
    {
        var newlineOffset = text.AsSpan(lineStart).IndexOfAny('\r', '\n');
        if (newlineOffset < 0)
        {
            lineLength = text.Length - lineStart;
            return text.Length;
        }

        var lineEnd = lineStart + newlineOffset;
        lineLength = lineEnd - lineStart;
        var nextLineStart = lineEnd + 1;
        if (text[lineEnd] == '\r' && nextLineStart < text.Length && text[nextLineStart] == '\n')
        {
            nextLineStart++;
        }

        return nextLineStart;
    }

    private static string? ParseDiffHeaderPath(ReadOnlySpan<char> rawPath)
    {
        var path = rawPath.Trim();
        if (path.Equals("/dev/null".AsSpan(), StringComparison.Ordinal))
        {
            return null;
        }

        if (path.StartsWith("a/".AsSpan(), StringComparison.Ordinal) || path.StartsWith("b/".AsSpan(), StringComparison.Ordinal))
        {
            path = path[2..];
        }

        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            path = path[1..^1];
        }

        return path.Length == 0 ? null : NormalizePathKey(path.ToString());
    }

    private static void AddChunk(
        Dictionary<string, BatchedFileDiff> chunks,
        string path,
        string unifiedDiff,
        int start,
        int end,
        int addedLines,
        int deletedLines)
    {
        if (!string.IsNullOrWhiteSpace(path) && end > start)
        {
            chunks[NormalizePathKey(path)] = new BatchedFileDiff(unifiedDiff[start..end], addedLines, deletedLines);
        }
    }

    private static BatchedFileDiff CreateBatchedFileDiff(string unifiedDiff)
    {
        var addedLines = 0;
        var deletedLines = 0;

        for (var lineStart = 0; lineStart < unifiedDiff.Length;)
        {
            var nextLineStart = GetNextLineStart(unifiedDiff, lineStart, out var lineLength);
            var line = unifiedDiff.AsSpan(lineStart, lineLength);
            if (line.Length > 0)
            {
                if (line[0] == '+' && !IsFileHeaderLine(line))
                {
                    addedLines++;
                }
                else if (line[0] == '-' && !IsFileHeaderLine(line))
                {
                    deletedLines++;
                }
            }

            lineStart = nextLineStart;
        }

        return new BatchedFileDiff(unifiedDiff, addedLines, deletedLines);
    }

    private static string NormalizePathKey(string path) => path.Replace('\\', '/').Trim('/');

    private static bool IsFileHeaderLine(ReadOnlySpan<char> line) =>
        line.Length > 3 &&
        (line.StartsWith("+++".AsSpan(), StringComparison.Ordinal) ||
            line.StartsWith("---".AsSpan(), StringComparison.Ordinal)) &&
        char.IsWhiteSpace(line[3]);

    private static DiffDocumentMetadata CreateMetadata(GitFileChange fileChange, BatchedFileDiff diff) =>
        new(
            new DiffDocumentId(fileChange.Path),
            fileChange.Path,
            fileChange.OldPath,
            fileChange.Status,
            fileChange.Language,
            diff.AddedLines,
            diff.DeletedLines);

    private sealed record BatchedFileDiff(string UnifiedDiff, int AddedLines, int DeletedLines)
    {
        public static BatchedFileDiff Empty { get; } = new(string.Empty, 0, 0);
    }
}
