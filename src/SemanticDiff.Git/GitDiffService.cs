using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Git;

public sealed class GitDiffService : IGitDiffService
{
    private readonly IGitCommandRunner commandRunner;
    private readonly GitDefaultBranchDiscovery defaultBranchDiscovery;

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
        var defaultBranch = await defaultBranchDiscovery.DiscoverAsync(request.RepositoryPath, cancellationToken).ConfigureAwait(false);
        var files = request.Scope == GitDiffScope.Worktree
            ? await GetWorktreeFilesAsync(request, cancellationToken).ConfigureAwait(false)
            : await GetNameStatusFilesAsync(request, defaultBranch, cancellationToken).ConfigureAwait(false);

        return new GitDiffSnapshot(request.RepositoryPath, request, defaultBranch, files, DateTimeOffset.UtcNow);
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

        var defaultBranch = await defaultBranchDiscovery.DiscoverAsync(request.RepositoryPath, cancellationToken).ConfigureAwait(false);
        var pathFilter = fileChange.Status == DiffFileStatus.Deleted && fileChange.OldPath is not null
            ? fileChange.OldPath
            : fileChange.Path;
        var fileRequest = request with { PathFilter = pathFilter };
        var arguments = GitDiffCommandBuilder.BuildDiffArguments(fileRequest, defaultBranch);
        var result = await commandRunner.RunAsync(request.RepositoryPath, arguments, cancellationToken).ConfigureAwait(false);

        return new GitFileDiff(fileChange, result.Succeeded ? result.StandardOutput : string.Empty);
    }

    public async Task<string> GetFileContentAsync(GitDiffRequest request, GitFileChange fileChange, CancellationToken cancellationToken)
    {
        if (fileChange.Status == DiffFileStatus.Deleted)
        {
            return string.Empty;
        }

        if (ShouldReadWorktreeContent(request.Scope, fileChange.Status))
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

    private async Task<ImmutableArray<GitFileChange>> GetWorktreeFilesAsync(GitDiffRequest request, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(request.RepositoryPath, GitDiffCommandBuilder.BuildWorktreeStatusArguments(), cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? ParsePorcelainStatus(result.StandardOutput) : ImmutableArray<GitFileChange>.Empty;
    }

    private static ImmutableArray<GitFileChange> ParseNameStatus(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return ImmutableArray<GitFileChange>.Empty;
        }

        var tokens = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var builder = ImmutableArray.CreateBuilder<GitFileChange>();

        for (var tokenIndex = 0; tokenIndex < tokens.Length; tokenIndex++)
        {
            var statusToken = tokens[tokenIndex];
            if (statusToken.Length == 0 || tokenIndex + 1 >= tokens.Length)
            {
                continue;
            }

            var status = ParseStatus(statusToken[0]);
            string? oldPath = null;
            var path = tokens[++tokenIndex];

            if (status == DiffFileStatus.Renamed && tokenIndex + 1 < tokens.Length)
            {
                oldPath = path;
                path = tokens[++tokenIndex];
            }

            builder.Add(new GitFileChange(path, oldPath, status, 0, 0, LanguageFromPath(path)));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<GitFileChange> ParsePorcelainStatus(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return ImmutableArray<GitFileChange>.Empty;
        }

        var tokens = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var builder = ImmutableArray.CreateBuilder<GitFileChange>();

        for (var tokenIndex = 0; tokenIndex < tokens.Length; tokenIndex++)
        {
            var token = tokens[tokenIndex];
            if (token.Length < 4)
            {
                continue;
            }

            var indexStatus = token[0];
            var workTreeStatus = token[1];
            var path = token[3..];
            string? oldPath = null;
            var status = ParsePorcelainStatus(indexStatus, workTreeStatus);

            if (status == DiffFileStatus.Renamed && tokenIndex + 1 < tokens.Length)
            {
                oldPath = tokens[++tokenIndex];
            }

            builder.Add(new GitFileChange(path, oldPath, status, 0, 0, LanguageFromPath(path)));
        }

        return builder
            .GroupBy(file => file.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToImmutableArray();
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

        if (indexStatus == 'R' || workTreeStatus == 'R')
        {
            return DiffFileStatus.Renamed;
        }

        if (indexStatus == 'A' || workTreeStatus == 'A')
        {
            return DiffFileStatus.Added;
        }

        if (indexStatus == 'D' || workTreeStatus == 'D')
        {
            return DiffFileStatus.Deleted;
        }

        if (indexStatus == 'U' || workTreeStatus == 'U')
        {
            return DiffFileStatus.Conflicted;
        }

        return DiffFileStatus.Modified;
    }

    private static bool ShouldReadWorktreeContent(GitDiffScope scope, DiffFileStatus status) =>
        status == DiffFileStatus.Untracked || scope is GitDiffScope.Worktree or GitDiffScope.Unstaged or GitDiffScope.Head;

    private static string? GetContentRevision(GitDiffRequest request) => request.Scope switch
    {
        GitDiffScope.Staged => null,
        GitDiffScope.Branch or GitDiffScope.CommitRange or GitDiffScope.Custom => string.IsNullOrWhiteSpace(request.HeadRef) ? "HEAD" : request.HeadRef,
        _ => "HEAD"
    };

    private static string CreateAddedFileDiff(string path, string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("--- /dev/null");
        builder.Append("+++ b/").AppendLine(path);
        builder.Append("@@ -0,0 +1,").Append(lines.Length).AppendLine(" @@");

        foreach (var line in lines)
        {
            builder.Append('+').AppendLine(line);
        }

        return builder.ToString();
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