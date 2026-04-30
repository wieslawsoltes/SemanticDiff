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
        var batchedFileDiffs = gitSnapshot.Files.IsDefaultOrEmpty
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : await LoadBatchedFileDiffsAsync(diffRequest, cancellationToken).ConfigureAwait(false);
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

    private async Task<DiffDocumentSnapshot> LoadDocumentAsync(
        GitDiffRequest request,
        GitDiffRequest diffRequest,
        DiffContextMode contextMode,
        GitFileChange fileChange,
        IReadOnlyDictionary<string, string> batchedFileDiffs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var unifiedDiff = TryGetBatchedFileDiff(batchedFileDiffs, fileChange, out var batchedDiff)
            ? batchedDiff
            : (await gitDiffService.GetFileDiffAsync(diffRequest, fileChange, cancellationToken).ConfigureAwait(false)).UnifiedDiff;
        var metadata = CreateMetadata(fileChange, unifiedDiff);
        return contextMode == DiffContextMode.CurrentFile
            ? documentFactory.CreateFromText(metadata, await gitDiffService.GetFileContentAsync(request, fileChange, cancellationToken).ConfigureAwait(false))
            : documentFactory.CreateFromUnifiedDiff(metadata, unifiedDiff);
    }

    private static int GetMaxConcurrentFileLoads(int fileCount) => Math.Min(fileCount, Math.Clamp(Environment.ProcessorCount / 2, 2, 6));

    private async Task<IReadOnlyDictionary<string, string>> LoadBatchedFileDiffsAsync(
        GitDiffRequest diffRequest,
        CancellationToken cancellationToken)
    {
        var unifiedDiff = await gitDiffService.GetUnifiedDiffAsync(diffRequest, cancellationToken).ConfigureAwait(false);
        return SplitUnifiedDiffByPath(unifiedDiff);
    }

    private static bool TryGetBatchedFileDiff(
        IReadOnlyDictionary<string, string> fileDiffs,
        GitFileChange fileChange,
        out string unifiedDiff)
    {
        if (fileDiffs.TryGetValue(NormalizePathKey(fileChange.Path), out unifiedDiff!))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(fileChange.OldPath) &&
            fileDiffs.TryGetValue(NormalizePathKey(fileChange.OldPath), out unifiedDiff!))
        {
            return true;
        }

        unifiedDiff = string.Empty;
        return false;
    }

    private static IReadOnlyDictionary<string, string> SplitUnifiedDiffByPath(string unifiedDiff)
    {
        if (string.IsNullOrWhiteSpace(unifiedDiff))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var chunks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var builder = new System.Text.StringBuilder();
        var activePath = string.Empty;
        foreach (var line in SplitLinesPreservingText(unifiedDiff))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) && builder.Length > 0)
            {
                AddChunk(chunks, activePath, builder.ToString());
                builder.Clear();
                activePath = string.Empty;
            }

            builder.AppendLine(line);
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                activePath = ParseDiffHeaderPath(line[4..]) ?? activePath;
            }
            else if (string.IsNullOrWhiteSpace(activePath) && line.StartsWith("--- ", StringComparison.Ordinal))
            {
                activePath = ParseDiffHeaderPath(line[4..]) ?? activePath;
            }
        }

        if (builder.Length > 0)
        {
            AddChunk(chunks, activePath, builder.ToString());
        }

        return chunks;
    }

    private static IEnumerable<string> SplitLinesPreservingText(string text)
    {
        using var reader = new StringReader(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }

    private static string? ParseDiffHeaderPath(string rawPath)
    {
        var path = rawPath.Trim();
        if (path.Equals("/dev/null", StringComparison.Ordinal))
        {
            return null;
        }

        if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            path = path[1..^1];
        }

        return string.IsNullOrWhiteSpace(path) ? null : NormalizePathKey(path);
    }

    private static void AddChunk(Dictionary<string, string> chunks, string path, string unifiedDiff)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            chunks[NormalizePathKey(path)] = unifiedDiff;
        }
    }

    private static string NormalizePathKey(string path) => path.Replace('\\', '/').Trim('/');

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
