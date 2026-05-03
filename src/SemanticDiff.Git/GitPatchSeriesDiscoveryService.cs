using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using SemanticDiff.Core;

namespace SemanticDiff.Git;

public sealed class GitPatchSeriesDiscoveryService : IGitPatchSeriesDiscoveryService
{
    private readonly IGitCommandRunner commandRunner;
    private readonly string cacheRoot;

    public GitPatchSeriesDiscoveryService()
        : this(new GitCommandRunner())
    {
    }

    public GitPatchSeriesDiscoveryService(IGitCommandRunner commandRunner, string? cacheRoot = null)
    {
        this.commandRunner = commandRunner;
        this.cacheRoot = cacheRoot ?? Path.Combine(Path.GetTempPath(), "SemanticDiff", "patch-compare");
    }

    public async Task<GitPatchSeriesDiscoverySnapshot> DiscoverAsync(
        GitPatchSeriesDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        var repositoryPath = request.SourceKind == GitPatchSeriesRepositorySourceKind.RemoteUrl
            ? await PrepareRemoteRepositoryAsync(request.SourceText, cancellationToken).ConfigureAwait(false)
            : await ResolveLocalRepositoryAsync(request, cancellationToken).ConfigureAwait(false);

        var defaultRefName = await DiscoverDefaultRefNameAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var refsResult = await commandRunner.RunAsync(
            repositoryPath,
            [
                "for-each-ref",
                "--format=%(refname:short)%09%(refname)%09%(objectname:short)%09%(committerdate:iso8601)%09%(subject)%09%(HEAD)",
                "refs/heads",
                "refs/remotes",
                "refs/tags",
                "refs/pull",
                "refs/merge-requests"
            ],
            cancellationToken).ConfigureAwait(false);

        if (!refsResult.Succeeded)
        {
            throw new InvalidOperationException(CreateGitFailureMessage("Unable to inspect repository refs", refsResult));
        }

        var refs = ParseRefs(refsResult.StandardOutput, defaultRefName);
        var sourceText = request.SourceKind == GitPatchSeriesRepositorySourceKind.CurrentRepository
            ? "Current repository"
            : request.SourceText.Trim();
        var status = refs.IsDefaultOrEmpty
            ? $"No refs found in {sourceText}"
            : $"Discovered {refs.Length:N0} refs from {sourceText}";

        return new GitPatchSeriesDiscoverySnapshot(
            request,
            repositoryPath,
            sourceText,
            refs,
            status,
            DateTimeOffset.UtcNow);
    }

    public static GitPatchSeriesRepositorySourceKind InferSourceKind(string sourceText, string? fallbackRepositoryPath)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return GitPatchSeriesRepositorySourceKind.CurrentRepository;
        }

        var trimmed = sourceText.Trim();
        if (Directory.Exists(trimmed))
        {
            return GitPatchSeriesRepositorySourceKind.LocalRepository;
        }

        if (LooksLikeRemoteUrl(trimmed))
        {
            return GitPatchSeriesRepositorySourceKind.RemoteUrl;
        }

        if (!string.IsNullOrWhiteSpace(fallbackRepositoryPath) &&
            Directory.Exists(Path.GetFullPath(trimmed, fallbackRepositoryPath)))
        {
            return GitPatchSeriesRepositorySourceKind.LocalRepository;
        }

        return GitPatchSeriesRepositorySourceKind.LocalRepository;
    }

    internal static ImmutableArray<GitPatchSeriesRefInfo> ParseRefs(string output, string? defaultRefName)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return ImmutableArray<GitPatchSeriesRefInfo>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<GitPatchSeriesRefInfo>();
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = line.Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            var shortName = parts[0].Trim();
            var referenceName = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(shortName) ||
                string.IsNullOrWhiteSpace(referenceName) ||
                referenceName.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                continue;
            }

            var sha = parts.Length > 2 ? parts[2].Trim() : string.Empty;
            DateTimeOffset? commitTime = parts.Length > 3 && DateTimeOffset.TryParse(parts[3].Trim(), out var parsedTime)
                ? parsedTime
                : null;
            var subject = parts.Length > 4 ? parts[4].Trim() : string.Empty;
            var isCurrent = parts.Length > 5 && string.Equals(parts[5].Trim(), "*", StringComparison.Ordinal);
            var kind = GetRefKind(referenceName);
            var displayName = CreateDisplayName(shortName, referenceName, kind);
            var rangeName = CreateRangeName(shortName, referenceName, kind);
            var isDefault = IsDefaultRef(shortName, referenceName, defaultRefName);

            builder.Add(new GitPatchSeriesRefInfo(
                displayName,
                referenceName,
                rangeName,
                kind,
                sha,
                commitTime,
                subject,
                isDefault,
                isCurrent));
        }

        return builder
            .OrderByDescending(item => item.IsCurrent)
            .ThenByDescending(item => item.IsDefault)
            .ThenBy(item => GetKindSort(item.Kind))
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private async Task<string> ResolveLocalRepositoryAsync(
        GitPatchSeriesDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        var sourceText = string.IsNullOrWhiteSpace(request.SourceText)
            ? request.FallbackRepositoryPath
            : request.SourceText.Trim();
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            throw new InvalidOperationException("Open a repository or enter a local path or remote Git URL.");
        }

        var candidate = sourceText;
        if (!Path.IsPathRooted(candidate) && !string.IsNullOrWhiteSpace(request.FallbackRepositoryPath))
        {
            candidate = Path.GetFullPath(candidate, request.FallbackRepositoryPath);
        }

        if (!Directory.Exists(candidate))
        {
            throw new DirectoryNotFoundException($"Repository path was not found: {candidate}");
        }

        var result = await commandRunner.RunAsync(
            candidate,
            ["rev-parse", "--show-toplevel"],
            cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            var root = result.StandardOutput.Trim();
            if (!string.IsNullOrWhiteSpace(root))
            {
                return root;
            }
        }

        return candidate;
    }

    private async Task<string> PrepareRemoteRepositoryAsync(string sourceText, CancellationToken cancellationToken)
    {
        var remoteUrl = sourceText.Trim();
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            throw new InvalidOperationException("Enter a remote Git URL.");
        }

        Directory.CreateDirectory(cacheRoot);
        var repositoryPath = Path.Combine(cacheRoot, CreateStableDirectoryName(remoteUrl));
        if (!Directory.Exists(repositoryPath))
        {
            var cloneResult = await commandRunner.RunAsync(
                cacheRoot,
                ["clone", "--bare", "--filter=blob:none", remoteUrl, repositoryPath],
                cancellationToken).ConfigureAwait(false);
            if (!cloneResult.Succeeded)
            {
                throw new InvalidOperationException(CreateGitFailureMessage("Unable to clone remote repository", cloneResult));
            }
        }
        else
        {
            var fetchResult = await commandRunner.RunAsync(
                repositoryPath,
                ["fetch", "--prune", "--tags", "origin", "+refs/heads/*:refs/heads/*", "+refs/tags/*:refs/tags/*"],
                cancellationToken).ConfigureAwait(false);
            if (!fetchResult.Succeeded)
            {
                throw new InvalidOperationException(CreateGitFailureMessage("Unable to refresh remote repository cache", fetchResult));
            }
        }

        return repositoryPath;
    }

    private async Task<string?> DiscoverDefaultRefNameAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            repositoryPath,
            ["symbolic-ref", "--quiet", "--short", "HEAD"],
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    private static bool LooksLikeRemoteUrl(string sourceText)
    {
        if (sourceText.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(sourceText, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme is "http" or "https" or "ssh" or "git";
    }

    private static string CreateStableDirectoryName(string remoteUrl)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(remoteUrl));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static GitPatchSeriesRefKind GetRefKind(string referenceName)
    {
        if (referenceName.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            return GitPatchSeriesRefKind.Branch;
        }

        if (referenceName.StartsWith("refs/remotes/", StringComparison.Ordinal))
        {
            return GitPatchSeriesRefKind.RemoteBranch;
        }

        if (referenceName.StartsWith("refs/tags/", StringComparison.Ordinal))
        {
            return GitPatchSeriesRefKind.Tag;
        }

        if (referenceName.StartsWith("refs/pull/", StringComparison.Ordinal))
        {
            return GitPatchSeriesRefKind.PullRequest;
        }

        if (referenceName.StartsWith("refs/merge-requests/", StringComparison.Ordinal))
        {
            return GitPatchSeriesRefKind.MergeRequest;
        }

        return GitPatchSeriesRefKind.Other;
    }

    private static string CreateDisplayName(string shortName, string referenceName, GitPatchSeriesRefKind kind) =>
        kind switch
        {
            GitPatchSeriesRefKind.PullRequest => $"PR {referenceName["refs/pull/".Length..]}",
            GitPatchSeriesRefKind.MergeRequest => $"MR {referenceName["refs/merge-requests/".Length..]}",
            _ => shortName
        };

    private static string CreateRangeName(string shortName, string referenceName, GitPatchSeriesRefKind kind) =>
        kind switch
        {
            GitPatchSeriesRefKind.Tag => shortName,
            GitPatchSeriesRefKind.PullRequest => referenceName,
            GitPatchSeriesRefKind.MergeRequest => referenceName,
            _ => shortName
        };

    private static bool IsDefaultRef(string shortName, string referenceName, string? defaultRefName)
    {
        if (string.IsNullOrWhiteSpace(defaultRefName))
        {
            return false;
        }

        return string.Equals(shortName, defaultRefName, StringComparison.Ordinal) ||
               string.Equals(referenceName, defaultRefName, StringComparison.Ordinal) ||
               string.Equals(referenceName, $"refs/heads/{defaultRefName}", StringComparison.Ordinal) ||
               string.Equals(referenceName, $"refs/remotes/{defaultRefName}", StringComparison.Ordinal);
    }

    private static int GetKindSort(GitPatchSeriesRefKind kind) => kind switch
    {
        GitPatchSeriesRefKind.Branch => 0,
        GitPatchSeriesRefKind.RemoteBranch => 1,
        GitPatchSeriesRefKind.Tag => 2,
        GitPatchSeriesRefKind.PullRequest => 3,
        GitPatchSeriesRefKind.MergeRequest => 4,
        _ => 5
    };

    private static string CreateGitFailureMessage(string prefix, GitCommandResult result)
    {
        var details = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        return string.IsNullOrWhiteSpace(details)
            ? prefix
            : $"{prefix}: {details}";
    }
}
