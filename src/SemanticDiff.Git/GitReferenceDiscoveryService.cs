using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text.Json;
using SemanticDiff.Core;

namespace SemanticDiff.Git;

public sealed record GitHubRepository(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";
}

public sealed class GitReferenceDiscoveryService : IGitReferenceDiscoveryService
{
    private readonly IGitCommandRunner commandRunner;
    private readonly GitDefaultBranchDiscovery defaultBranchDiscovery;
    private readonly HttpClient httpClient;

    public GitReferenceDiscoveryService()
        : this(new GitCommandRunner(), new HttpClient())
    {
    }

    public GitReferenceDiscoveryService(IGitCommandRunner commandRunner, HttpClient? httpClient = null)
    {
        this.commandRunner = commandRunner;
        this.httpClient = httpClient ?? new HttpClient();
        defaultBranchDiscovery = new GitDefaultBranchDiscovery(commandRunner);
    }

    public async Task<GitRepositoryReferenceSnapshot> GetReferencesAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var defaultBranch = await defaultBranchDiscovery.DiscoverAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var branches = await GetBranchesAsync(repositoryPath, defaultBranch, cancellationToken).ConfigureAwait(false);
        var remoteUrl = await GetOriginRemoteUrlAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var githubRepository = TryParseGitHubRemoteUrl(remoteUrl);
        if (githubRepository is null)
        {
            return new GitRepositoryReferenceSnapshot(branches, ImmutableArray<GitPullRequestInfo>.Empty, false, FormatBranchStatus(branches));
        }

        var pullRequests = await GetPullRequestsAsync(repositoryPath, githubRepository, defaultBranch, cancellationToken).ConfigureAwait(false);
        var status = pullRequests.IsDefaultOrEmpty
            ? $"{FormatBranchStatus(branches)} | GitHub PRs unavailable"
            : $"{FormatBranchStatus(branches)} | {pullRequests.Length:N0} PRs";
        return new GitRepositoryReferenceSnapshot(branches, pullRequests, true, status);
    }

    public async Task<string?> EnsurePullRequestHeadAsync(string repositoryPath, GitPullRequestInfo pullRequest, CancellationToken cancellationToken)
    {
        if (pullRequest.Number <= 0)
        {
            return null;
        }

        var targetReference = $"refs/remotes/origin/pull/{pullRequest.Number}/head";
        var fetchResult = await commandRunner.RunAsync(
            repositoryPath,
            ["fetch", "--quiet", "origin", $"pull/{pullRequest.Number}/head:{targetReference}"],
            cancellationToken).ConfigureAwait(false);

        if (fetchResult.Succeeded)
        {
            return targetReference;
        }

        return pullRequest.IsFromSameRepository && !string.IsNullOrWhiteSpace(pullRequest.HeadRefName)
            ? $"origin/{pullRequest.HeadRefName}"
            : null;
    }

    public static GitHubRepository? TryParseGitHubRemoteUrl(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var trimmed = remoteUrl.Trim();
        if (trimmed.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            return ParseOwnerRepository(trimmed[15..]);
        }

        if (trimmed.StartsWith("ssh://git@github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return ParseOwnerRepository(trimmed[21..]);
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return ParseOwnerRepository(uri.AbsolutePath.Trim('/'));
        }

        return null;
    }

    public static ImmutableArray<GitBranchInfo> ParseBranches(string output, string? currentBranch, string? defaultBranch)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return ImmutableArray<GitBranchInfo>.Empty;
        }

        var normalizedDefaultBranch = NormalizeDefaultBranch(defaultBranch);
        var current = string.IsNullOrWhiteSpace(currentBranch) ? null : currentBranch.Trim();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<GitBranchInfo>();
        foreach (var line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Split('\t');
            if (columns.Length == 0)
            {
                continue;
            }

            var referenceName = columns[0].Trim();
            if (string.IsNullOrWhiteSpace(referenceName) || referenceName.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!seen.Add(referenceName))
            {
                continue;
            }

            var isRemote = referenceName.Contains('/', StringComparison.Ordinal);
            var shortName = isRemote && referenceName.StartsWith("origin/", StringComparison.Ordinal)
                ? referenceName[7..]
                : referenceName;
            var isCurrent = string.Equals(shortName, current, StringComparison.Ordinal) || columns.Any(column => column.Trim() == "*");
            var isDefault = string.Equals(referenceName, defaultBranch, StringComparison.Ordinal) ||
                string.Equals(shortName, normalizedDefaultBranch, StringComparison.Ordinal);
            builder.Add(new GitBranchInfo(shortName, referenceName, isCurrent, isRemote, isDefault));
        }

        return builder
            .OrderByDescending(branch => branch.IsCurrent)
            .ThenByDescending(branch => branch.IsDefault)
            .ThenBy(branch => branch.IsRemote)
            .ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(branch => branch.ReferenceName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private async Task<ImmutableArray<GitBranchInfo>> GetBranchesAsync(
        string repositoryPath,
        string? defaultBranch,
        CancellationToken cancellationToken)
    {
        var branchResult = await commandRunner.RunAsync(
            repositoryPath,
            ["for-each-ref", "--format=%(refname:short)%09%(refname)%09%(HEAD)", "refs/heads", "refs/remotes"],
            cancellationToken).ConfigureAwait(false);
        if (!branchResult.Succeeded)
        {
            return ImmutableArray<GitBranchInfo>.Empty;
        }

        var currentBranchResult = await commandRunner.RunAsync(repositoryPath, ["branch", "--show-current"], cancellationToken).ConfigureAwait(false);
        var currentBranch = currentBranchResult.Succeeded ? currentBranchResult.StandardOutput.Trim() : null;
        return ParseBranches(branchResult.StandardOutput, currentBranch, defaultBranch);
    }

    private async Task<string?> GetOriginRemoteUrlAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(repositoryPath, ["remote", "get-url", "origin"], cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    private async Task<ImmutableArray<GitPullRequestInfo>> GetPullRequestsAsync(
        string repositoryPath,
        GitHubRepository repository,
        string? defaultBranch,
        CancellationToken cancellationToken)
    {
        var apiPullRequests = await GetPullRequestsFromGitHubApiAsync(repository, cancellationToken).ConfigureAwait(false);
        return apiPullRequests.IsDefaultOrEmpty
            ? await GetPullRequestsFromRemoteRefsAsync(repositoryPath, repository, defaultBranch, cancellationToken).ConfigureAwait(false)
            : apiPullRequests;
    }

    private async Task<ImmutableArray<GitPullRequestInfo>> GetPullRequestsFromGitHubApiAsync(
        GitHubRepository repository,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository.Owner}/{repository.Name}/pulls?state=open&per_page=100");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SemanticDiff", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ImmutableArray<GitPullRequestInfo>.Empty;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return ImmutableArray<GitPullRequestInfo>.Empty;
            }

            var builder = ImmutableArray.CreateBuilder<GitPullRequestInfo>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!TryReadPullRequest(item, repository, out var pullRequest))
                {
                    continue;
                }

                builder.Add(pullRequest);
            }

            return builder
                .OrderByDescending(pullRequest => pullRequest.Number)
                .ToImmutableArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or TaskCanceledException)
        {
            return ImmutableArray<GitPullRequestInfo>.Empty;
        }
    }

    private async Task<ImmutableArray<GitPullRequestInfo>> GetPullRequestsFromRemoteRefsAsync(
        string repositoryPath,
        GitHubRepository repository,
        string? defaultBranch,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(repositoryPath, ["ls-remote", "--refs", "origin", "refs/pull/*/head"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return ImmutableArray<GitPullRequestInfo>.Empty;
        }

        var baseRef = NormalizeDefaultBranch(defaultBranch) ?? "main";
        var builder = ImmutableArray.CreateBuilder<GitPullRequestInfo>();
        foreach (var line in result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tabIndex = line.IndexOf('\t');
            if (tabIndex < 0)
            {
                continue;
            }

            var reference = line[(tabIndex + 1)..].Trim();
            var parts = reference.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !int.TryParse(parts[2], out var number))
            {
                continue;
            }

            builder.Add(new GitPullRequestInfo(number, $"PR #{number}", baseRef, reference, repository.FullName, false));
        }

        return builder
            .OrderByDescending(pullRequest => pullRequest.Number)
            .ToImmutableArray();
    }

    private static bool TryReadPullRequest(JsonElement item, GitHubRepository repository, out GitPullRequestInfo pullRequest)
    {
        pullRequest = default!;
        if (!TryGetInt32(item, "number", out var number) || number <= 0)
        {
            return false;
        }

        var title = TryGetString(item, "title") ?? $"PR #{number}";
        if (!item.TryGetProperty("base", out var baseElement) || !item.TryGetProperty("head", out var headElement))
        {
            return false;
        }

        var baseRef = TryGetString(baseElement, "ref") ?? "main";
        var headRef = TryGetString(headElement, "ref") ?? string.Empty;
        var headRepository = TryGetRepositoryFullName(headElement) ?? repository.FullName;
        pullRequest = new GitPullRequestInfo(number, title, baseRef, headRef, headRepository, string.Equals(headRepository, repository.FullName, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    private static string? TryGetRepositoryFullName(JsonElement headElement)
    {
        if (!headElement.TryGetProperty("repo", out var repositoryElement) || repositoryElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return TryGetString(repositoryElement, "full_name");
    }

    private static bool TryGetInt32(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static GitHubRepository? ParseOwnerRepository(string path)
    {
        var normalized = path.Trim('/');
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 ? new GitHubRepository(parts[0], parts[1]) : null;
    }

    private static string? NormalizeDefaultBranch(string? defaultBranch)
    {
        if (string.IsNullOrWhiteSpace(defaultBranch))
        {
            return null;
        }

        var normalized = defaultBranch.Trim();
        return normalized.StartsWith("origin/", StringComparison.Ordinal) ? normalized[7..] : normalized;
    }

    private static string FormatBranchStatus(ImmutableArray<GitBranchInfo> branches) =>
        branches.IsDefaultOrEmpty ? "Branches unavailable" : $"{branches.Length:N0} branches";
}