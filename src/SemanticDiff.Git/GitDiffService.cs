using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Git;

public sealed class GitDiffService : IGitDiffService
{
    private readonly IGitCommandRunner commandRunner;
    private readonly GitDefaultBranchDiscovery defaultBranchDiscovery;
    private readonly object defaultBranchGate = new();
    private readonly Dictionary<string, string?> defaultBranches = new(StringComparer.Ordinal);

    public GitDiffService()
        : this(new GitCommandRunner())
    {
    }

    public GitDiffService(IGitCommandRunner commandRunner)
    {
        this.commandRunner = commandRunner;
        defaultBranchDiscovery = new GitDefaultBranchDiscovery(commandRunner);
    }

    public async Task<GitDiffSnapshot> GetDiffAsync(GitDiffRequest request, CancellationToken cancellationToken)
    {
        var defaultBranch = await GetDefaultBranchIfNeededAsync(request, cancellationToken).ConfigureAwait(false);
        var files = request.Scope switch
        {
            GitDiffScope.Worktree => await GetWorktreeFilesAsync(request, cancellationToken).ConfigureAwait(false),
            GitDiffScope.Unstaged => await GetUnstagedFilesAsync(request, defaultBranch, cancellationToken).ConfigureAwait(false),
            GitDiffScope.Branch when IsCurrentHeadReference(request.HeadRef) => await GetCurrentBranchFilesAsync(request, defaultBranch, cancellationToken).ConfigureAwait(false),
            _ => await GetNameStatusFilesAsync(request, defaultBranch, cancellationToken).ConfigureAwait(false)
        };

        return new GitDiffSnapshot(request.RepositoryPath, request, defaultBranch, files, DateTimeOffset.UtcNow);
    }

    public async Task<string> GetUnifiedDiffAsync(GitDiffRequest request, CancellationToken cancellationToken)
    {
        var defaultBranch = await GetDefaultBranchIfNeededAsync(request, cancellationToken).ConfigureAwait(false);
        var arguments = GitDiffCommandBuilder.BuildDiffArguments(request, defaultBranch);
        var result = await commandRunner.RunAsync(request.RepositoryPath, arguments, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput : string.Empty;
    }

    public async Task<GitFileDiff> GetFileDiffAsync(GitDiffRequest request, GitFileChange fileChange, CancellationToken cancellationToken)
    {
        if (fileChange.Status == DiffFileStatus.Untracked)
        {
            var filePath = Path.Combine(request.RepositoryPath, fileChange.Path);
            var text = File.Exists(filePath)
                ? await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false)
                : string.Empty;

            return new GitFileDiff(fileChange, CreateAddedFileDiff(fileChange.Path, text));
        }

        var defaultBranch = await GetDefaultBranchIfNeededAsync(request, cancellationToken).ConfigureAwait(false);
        var pathFilter = fileChange.Status == DiffFileStatus.Deleted && fileChange.OldPath is not null
            ? fileChange.OldPath
            : fileChange.Path;
        var fileRequest = request with { PathFilter = pathFilter };
        var arguments = GitDiffCommandBuilder.BuildDiffArguments(fileRequest, defaultBranch);
        var result = await commandRunner.RunAsync(request.RepositoryPath, arguments, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return new GitFileDiff(fileChange, result.StandardOutput);
        }

        if (fileChange.Status is DiffFileStatus.Added or DiffFileStatus.Copied)
        {
            var content = await GetFileContentAsync(request, fileChange, cancellationToken).ConfigureAwait(false);
            return new GitFileDiff(fileChange, CreateAddedFileDiff(fileChange.Path, content));
        }

        return new GitFileDiff(fileChange, result.Succeeded ? result.StandardOutput : string.Empty);
    }

    public async Task<string> GetFileContentAsync(GitDiffRequest request, GitFileChange fileChange, CancellationToken cancellationToken)
    {
        if (fileChange.Status == DiffFileStatus.Deleted)
        {
            return string.Empty;
        }

        if (ShouldReadWorktreeContent(request, fileChange.Status))
        {
            var filePath = Path.Combine(request.RepositoryPath, fileChange.Path);
            return File.Exists(filePath)
                ? await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false)
                : string.Empty;
        }

        var revision = GetContentRevision(request);
        var arguments = GitDiffCommandBuilder.BuildShowFileArguments(revision, fileChange.Path);
        var result = await commandRunner.RunAsync(request.RepositoryPath, arguments, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return result.StandardOutput;
        }

        var fallbackPath = Path.Combine(request.RepositoryPath, fileChange.Path);
        return File.Exists(fallbackPath)
            ? await File.ReadAllTextAsync(fallbackPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
    }

    private async Task<ImmutableArray<GitFileChange>> GetNameStatusFilesAsync(
        GitDiffRequest request,
        string? defaultBranch,
        CancellationToken cancellationToken)
    {
        var arguments = GitDiffCommandBuilder.BuildNameStatusArguments(request, defaultBranch);
        var result = await commandRunner.RunAsync(request.RepositoryPath, arguments, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? ParseNameStatus(result.StandardOutput) : ImmutableArray<GitFileChange>.Empty;
    }

    private async Task<string?> GetDefaultBranchIfNeededAsync(GitDiffRequest request, CancellationToken cancellationToken)
    {
        if (!NeedsDefaultBranch(request))
        {
            return null;
        }

        lock (defaultBranchGate)
        {
            if (defaultBranches.TryGetValue(request.RepositoryPath, out var cachedDefaultBranch))
            {
                return cachedDefaultBranch;
            }
        }

        var defaultBranch = await defaultBranchDiscovery.DiscoverAsync(request.RepositoryPath, cancellationToken).ConfigureAwait(false);
        lock (defaultBranchGate)
        {
            defaultBranches[request.RepositoryPath] = defaultBranch;
        }

        return defaultBranch;
    }

    private static bool NeedsDefaultBranch(GitDiffRequest request) =>
        request.Scope == GitDiffScope.Branch && string.IsNullOrWhiteSpace(request.BaseRef);

    private async Task<ImmutableArray<GitFileChange>> GetUnstagedFilesAsync(
        GitDiffRequest request,
        string? defaultBranch,
        CancellationToken cancellationToken)
    {
        var trackedFiles = await GetNameStatusFilesAsync(request, defaultBranch, cancellationToken).ConfigureAwait(false);
        var untrackedFiles = await GetUntrackedFilesAsync(request, cancellationToken).ConfigureAwait(false);
        return MergeFileChanges(trackedFiles, untrackedFiles);
    }

    private async Task<ImmutableArray<GitFileChange>> GetCurrentBranchFilesAsync(
        GitDiffRequest request,
        string? defaultBranch,
        CancellationToken cancellationToken)
    {
        var branchFiles = await GetNameStatusFilesAsync(request, defaultBranch, cancellationToken).ConfigureAwait(false);
        var untrackedFiles = await GetUntrackedFilesAsync(request, cancellationToken).ConfigureAwait(false);
        return MergeFileChanges(branchFiles, untrackedFiles);
    }

    private async Task<ImmutableArray<GitFileChange>> GetWorktreeFilesAsync(GitDiffRequest request, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(request.RepositoryPath, GitDiffCommandBuilder.BuildWorktreeStatusArguments(), cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? ParsePorcelainStatus(result.StandardOutput) : ImmutableArray<GitFileChange>.Empty;
    }

    private async Task<ImmutableArray<GitFileChange>> GetUntrackedFilesAsync(GitDiffRequest request, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(request.RepositoryPath, GitDiffCommandBuilder.BuildWorktreeStatusArguments(), cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? ParsePorcelainStatus(result.StandardOutput, DiffFileStatus.Untracked)
            : ImmutableArray<GitFileChange>.Empty;
    }

    private static ImmutableArray<GitFileChange> ParseNameStatus(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return ImmutableArray<GitFileChange>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<GitFileChange>(Math.Max(4, CountNullTokens(output) / 2));
        var tokenIndex = 0;

        while (TryReadNullTerminatedToken(output, ref tokenIndex, out var statusToken))
        {
            if (statusToken.Length == 0 || !TryReadNullTerminatedToken(output, ref tokenIndex, out var pathToken))
            {
                continue;
            }

            var status = ParseStatus(statusToken[0]);
            string? oldPath = null;
            var path = pathToken.ToString();

            if (status is DiffFileStatus.Renamed or DiffFileStatus.Copied &&
                TryReadNullTerminatedToken(output, ref tokenIndex, out var nextPathToken))
            {
                oldPath = path;
                path = nextPathToken.ToString();
            }

            builder.Add(new GitFileChange(path, oldPath, status, 0, 0, LanguageFromPath(path)));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<GitFileChange> ParsePorcelainStatus(string output, DiffFileStatus? statusFilter = null)
    {
        if (string.IsNullOrEmpty(output))
        {
            return ImmutableArray<GitFileChange>.Empty;
        }

        var filesByPath = new Dictionary<string, GitFileChange>(StringComparer.Ordinal);
        var tokenIndex = 0;

        while (TryReadNullTerminatedToken(output, ref tokenIndex, out var token))
        {
            if (token.Length < 4)
            {
                continue;
            }

            var indexStatus = token[0];
            var workTreeStatus = token[1];
            var path = token[3..].ToString();
            string? oldPath = null;
            var status = ParsePorcelainStatus(indexStatus, workTreeStatus);

            if (status == DiffFileStatus.Renamed &&
                TryReadNullTerminatedToken(output, ref tokenIndex, out var oldPathToken))
            {
                oldPath = oldPathToken.ToString();
            }

            if (statusFilter is not null && status != statusFilter)
            {
                continue;
            }

            filesByPath.TryAdd(path, new GitFileChange(path, oldPath, status, 0, 0, LanguageFromPath(path)));
        }

        return filesByPath.Count == 0
            ? ImmutableArray<GitFileChange>.Empty
            : filesByPath.Values.ToImmutableArray();
    }

    private static ImmutableArray<GitFileChange> MergeFileChanges(
        ImmutableArray<GitFileChange> primaryFiles,
        ImmutableArray<GitFileChange> additionalFiles)
    {
        if (primaryFiles.IsDefaultOrEmpty)
        {
            return additionalFiles.IsDefault ? ImmutableArray<GitFileChange>.Empty : additionalFiles;
        }

        if (additionalFiles.IsDefaultOrEmpty)
        {
            return primaryFiles;
        }

        var builder = ImmutableArray.CreateBuilder<GitFileChange>(primaryFiles.Length + additionalFiles.Length);
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in primaryFiles)
        {
            builder.Add(file);
            seenPaths.Add(file.Path);
        }

        foreach (var file in additionalFiles)
        {
            if (seenPaths.Add(file.Path))
            {
                builder.Add(file);
            }
        }

        return builder.ToImmutable();
    }

    private static DiffFileStatus ParseStatus(char status) => status switch
    {
        'A' => DiffFileStatus.Added,
        'D' => DiffFileStatus.Deleted,
        'R' => DiffFileStatus.Renamed,
        'C' => DiffFileStatus.Copied,
        '?' => DiffFileStatus.Untracked,
        'U' => DiffFileStatus.Conflicted,
        _ => DiffFileStatus.Modified
    };

    private static DiffFileStatus ParsePorcelainStatus(char indexStatus, char workTreeStatus)
    {
        if (indexStatus == '?' && workTreeStatus == '?')
        {
            return DiffFileStatus.Untracked;
        }

        if (IsUnmergedStatus(indexStatus, workTreeStatus))
        {
            return DiffFileStatus.Conflicted;
        }

        if (indexStatus == 'R' || workTreeStatus == 'R')
        {
            return DiffFileStatus.Renamed;
        }

        if (indexStatus == 'C' || workTreeStatus == 'C')
        {
            return DiffFileStatus.Copied;
        }

        if (indexStatus == 'A' || workTreeStatus == 'A')
        {
            return DiffFileStatus.Added;
        }

        if (indexStatus == 'D' || workTreeStatus == 'D')
        {
            return DiffFileStatus.Deleted;
        }

        return DiffFileStatus.Modified;
    }

    private static bool ShouldReadWorktreeContent(GitDiffRequest request, DiffFileStatus status) =>
        status == DiffFileStatus.Untracked ||
        request.Scope is GitDiffScope.Worktree or GitDiffScope.Unstaged or GitDiffScope.Head ||
        request.Scope == GitDiffScope.Branch && IsCurrentHeadReference(request.HeadRef);

    private static string? GetContentRevision(GitDiffRequest request) => request.Scope switch
    {
        GitDiffScope.Staged => null,
        GitDiffScope.Branch or GitDiffScope.CommitRange or GitDiffScope.Custom => string.IsNullOrWhiteSpace(request.HeadRef) ? "HEAD" : request.HeadRef,
        _ => "HEAD"
    };

    private static bool IsCurrentHeadReference(string? reference) =>
        string.IsNullOrWhiteSpace(reference) || string.Equals(reference.Trim(), "HEAD", StringComparison.Ordinal);

    private static bool IsUnmergedStatus(char indexStatus, char workTreeStatus) =>
        indexStatus == 'U' ||
        workTreeStatus == 'U' ||
        indexStatus == 'A' && workTreeStatus == 'A' ||
        indexStatus == 'D' && workTreeStatus == 'D';

    private static string CreateAddedFileDiff(string path, string text)
    {
        var lineCount = CountPatchLines(text);
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("--- /dev/null");
        builder.Append("+++ b/").AppendLine(path);
        builder.Append("@@ -0,0 +").Append(lineCount == 0 ? 0 : 1).Append(',').Append(lineCount).AppendLine(" @@");

        var lineStart = 0;
        while (TryReadPatchLine(text, ref lineStart, out var line))
        {
            builder.Append('+').Append(line).AppendLine();
        }

        return builder.ToString();
    }

    private static bool TryReadNullTerminatedToken(string text, ref int startIndex, out ReadOnlySpan<char> token)
    {
        while (startIndex < text.Length)
        {
            var tokenStart = startIndex;
            var terminatorOffset = text.AsSpan(startIndex).IndexOf('\0');
            var tokenEnd = terminatorOffset < 0 ? text.Length : startIndex + terminatorOffset;
            startIndex = terminatorOffset < 0 ? text.Length : tokenEnd + 1;
            if (tokenEnd > tokenStart)
            {
                token = text.AsSpan(tokenStart, tokenEnd - tokenStart);
                return true;
            }
        }

        token = ReadOnlySpan<char>.Empty;
        return false;
    }

    private static int CountNullTokens(string text)
    {
        var count = 0;
        foreach (var character in text)
        {
            if (character == '\0')
            {
                count++;
            }
        }

        return count;
    }

    private static int CountPatchLines(string text)
    {
        var count = 0;
        var lineStart = 0;
        while (TryReadPatchLine(text, ref lineStart, out _))
        {
            count++;
        }

        return count;
    }

    private static bool TryReadPatchLine(string text, ref int lineStart, out ReadOnlySpan<char> line)
    {
        if (lineStart >= text.Length)
        {
            line = ReadOnlySpan<char>.Empty;
            return false;
        }

        var newlineOffset = text.AsSpan(lineStart).IndexOfAny('\r', '\n');
        if (newlineOffset < 0)
        {
            line = text.AsSpan(lineStart);
            lineStart = text.Length;
            return true;
        }

        var lineEnd = lineStart + newlineOffset;
        line = text.AsSpan(lineStart, lineEnd - lineStart);
        lineStart = lineEnd + 1;
        if (text[lineEnd] == '\r' && lineStart < text.Length && text[lineStart] == '\n')
        {
            lineStart++;
        }

        return true;
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
            _ => extension.TrimStart('.').ToUpperInvariant()
        };
    }
}
