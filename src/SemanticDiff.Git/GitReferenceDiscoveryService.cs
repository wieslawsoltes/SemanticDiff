using System.Collections.Immutable;
using System.Text.Json;
using SemanticDiff.Core;

namespace SemanticDiff.Git;

public sealed record GitHubRepository(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";
}

public sealed record GitHubRemote(string Name, string Url, GitHubRepository Repository);

public sealed record GitLabRepository(string Host, string NamespacePath, string Name, string Scheme = "https")
{
    public string FullName => NamespacePath;

    public string EncodedProjectPath => Uri.EscapeDataString(NamespacePath);

    public string ApiBaseUrl => $"{Scheme}://{Host}";
}

public sealed record GitLabRemote(string Name, string Url, GitLabRepository Repository);

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

    public async Task<GitRepositoryReferenceSnapshot> GetReferencesAsync(
        string repositoryPath,
        CancellationToken cancellationToken,
        GitReviewRequestState reviewRequestState = GitReviewRequestState.Open)
    {
        var defaultBranchTask = defaultBranchDiscovery.DiscoverAsync(repositoryPath, cancellationToken);
        var branchListTask = GetBranchListOutputAsync(repositoryPath, cancellationToken);
        var remoteListTask = GetRemoteListOutputAsync(repositoryPath, cancellationToken);
        await Task.WhenAll(new Task[] { defaultBranchTask, branchListTask, remoteListTask }).ConfigureAwait(false);

        var defaultBranch = await defaultBranchTask.ConfigureAwait(false);
        var branches = ParseBranches(await branchListTask.ConfigureAwait(false), currentBranch: null, defaultBranch);
        var remoteList = await remoteListTask.ConfigureAwait(false);
        string? originUrl = null;
        var gitLabRemotes = ParseGitLabRemotes(remoteList);
        if (gitLabRemotes.IsDefaultOrEmpty)
        {
            originUrl = await GetOriginRemoteUrlAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
            gitLabRemotes = CreateGitLabOriginRemote(originUrl);
        }

        if (!gitLabRemotes.IsDefaultOrEmpty)
        {
            var mergeRequestRemote = SelectMergeRequestRemote(gitLabRemotes);
            var mergeRequests = await GetMergeRequestsAsync(repositoryPath, mergeRequestRemote, defaultBranch, reviewRequestState, cancellationToken).ConfigureAwait(false);
            var gitLabStatus = mergeRequests.IsDefaultOrEmpty
                ? $"{FormatBranchStatus(branches)} | {FormatReviewRequestState(reviewRequestState)} MRs unavailable from {mergeRequestRemote.Name}"
                : $"{FormatBranchStatus(branches)} | {FormatReviewRequestCount(mergeRequests.Length, GitReviewRequestKind.MergeRequest, reviewRequestState)} from {mergeRequestRemote.Name}";
            return new GitRepositoryReferenceSnapshot(branches, mergeRequests, true, GitReviewRequestKind.MergeRequest, gitLabStatus);
        }

        var githubRemotes = ParseGitHubRemotes(remoteList);
        if (githubRemotes.IsDefaultOrEmpty)
        {
            originUrl ??= await GetOriginRemoteUrlAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
            githubRemotes = CreateGitHubOriginRemote(originUrl);
        }

        if (githubRemotes.IsDefaultOrEmpty)
        {
            return new GitRepositoryReferenceSnapshot(branches, ImmutableArray<GitPullRequestInfo>.Empty, false, GitReviewRequestKind.PullRequest, FormatBranchStatus(branches));
        }

        var pullRequestRemote = SelectPullRequestRemote(githubRemotes);
        var pullRequests = await GetPullRequestsAsync(repositoryPath, pullRequestRemote, defaultBranch, reviewRequestState, cancellationToken).ConfigureAwait(false);
        var status = pullRequests.IsDefaultOrEmpty
            ? $"{FormatBranchStatus(branches)} | {FormatReviewRequestState(reviewRequestState)} PRs unavailable from {pullRequestRemote.Name}"
            : $"{FormatBranchStatus(branches)} | {FormatReviewRequestCount(pullRequests.Length, GitReviewRequestKind.PullRequest, reviewRequestState)} from {pullRequestRemote.Name}";
        return new GitRepositoryReferenceSnapshot(branches, pullRequests, true, GitReviewRequestKind.PullRequest, status);
    }

    public async Task<string?> EnsurePullRequestHeadAsync(string repositoryPath, GitPullRequestInfo pullRequest, CancellationToken cancellationToken)
    {
        if (pullRequest.Number <= 0)
        {
            return null;
        }

        var remoteName = NormalizeRemoteName(pullRequest.RemoteName);
        var reviewReferencePath = pullRequest.Kind == GitReviewRequestKind.MergeRequest
            ? $"merge-requests/{pullRequest.Number}/head"
            : $"pull/{pullRequest.Number}/head";
        var targetReference = $"refs/remotes/{remoteName}/{reviewReferencePath}";
        var fetchResult = await commandRunner.RunAsync(
            repositoryPath,
            ["fetch", "--quiet", remoteName, $"{reviewReferencePath}:{targetReference}"],
            cancellationToken).ConfigureAwait(false);

        if (fetchResult.Succeeded)
        {
            return targetReference;
        }

        return pullRequest.IsFromSameRepository && !string.IsNullOrWhiteSpace(pullRequest.HeadRefName)
            ? FormatRemoteReference(remoteName, pullRequest.HeadRefName)
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

    public static GitLabRepository? TryParseGitLabRemoteUrl(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var trimmed = remoteUrl.Trim();
        if (TryParseScpStyleGitRemote(trimmed, out var host, out var path) && IsGitLabHost(host))
        {
            return ParseGitLabRepository(host, path);
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && IsGitLabHost(uri.Host))
        {
            var scheme = uri.Scheme is "http" or "https" ? uri.Scheme : "https";
            return ParseGitLabRepository(uri.Host, uri.AbsolutePath.Trim('/'), scheme);
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

    public static ImmutableArray<GitHubRemote> ParseGitHubRemotes(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return ImmutableArray<GitHubRemote>.Empty;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = ImmutableArray.CreateBuilder<GitHubRemote>();
        foreach (var line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseRemoteLine(line, out var remoteName, out var url, out var isFetch) || !isFetch)
            {
                continue;
            }

            var repository = TryParseGitHubRemoteUrl(url);
            if (repository is null || !seen.Add(remoteName))
            {
                continue;
            }

            builder.Add(new GitHubRemote(remoteName, url, repository));
        }

        return builder.ToImmutable();
    }

    public static ImmutableArray<GitLabRemote> ParseGitLabRemotes(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return ImmutableArray<GitLabRemote>.Empty;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = ImmutableArray.CreateBuilder<GitLabRemote>();
        foreach (var line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseRemoteLine(line, out var remoteName, out var url, out var isFetch) || !isFetch)
            {
                continue;
            }

            var repository = TryParseGitLabRemoteUrl(url);
            if (repository is null || !seen.Add(remoteName))
            {
                continue;
            }

            builder.Add(new GitLabRemote(remoteName, url, repository));
        }

        return builder.ToImmutable();
    }

    private async Task<string> GetBranchListOutputAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var branchResult = await commandRunner.RunAsync(
            repositoryPath,
            ["for-each-ref", "--format=%(refname:short)%09%(refname)%09%(HEAD)", "refs/heads", "refs/remotes"],
            cancellationToken).ConfigureAwait(false);
        if (!branchResult.Succeeded)
        {
            return string.Empty;
        }

        return branchResult.StandardOutput;
    }

    private async Task<string> GetRemoteListOutputAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var remoteListResult = await commandRunner.RunAsync(repositoryPath, ["remote", "-v"], cancellationToken).ConfigureAwait(false);
        return remoteListResult.Succeeded ? remoteListResult.StandardOutput : string.Empty;
    }

    private async Task<string?> GetOriginRemoteUrlAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var originResult = await commandRunner.RunAsync(repositoryPath, ["remote", "get-url", "origin"], cancellationToken).ConfigureAwait(false);
        return originResult.Succeeded ? originResult.StandardOutput.Trim() : null;
    }

    private static ImmutableArray<GitLabRemote> CreateGitLabOriginRemote(string? originUrl)
    {
        if (string.IsNullOrWhiteSpace(originUrl))
        {
            return ImmutableArray<GitLabRemote>.Empty;
        }

        var originRepository = TryParseGitLabRemoteUrl(originUrl);
        return originRepository is null
            ? ImmutableArray<GitLabRemote>.Empty
            : [new GitLabRemote("origin", originUrl, originRepository)];
    }

    private static ImmutableArray<GitHubRemote> CreateGitHubOriginRemote(string? originUrl)
    {
        if (string.IsNullOrWhiteSpace(originUrl))
        {
            return ImmutableArray<GitHubRemote>.Empty;
        }

        var originRepository = TryParseGitHubRemoteUrl(originUrl);
        return originRepository is null
            ? ImmutableArray<GitHubRemote>.Empty
            : [new GitHubRemote("origin", originUrl, originRepository)];
    }

    private async Task<ImmutableArray<GitPullRequestInfo>> GetPullRequestsAsync(
        string repositoryPath,
        GitHubRemote remote,
        string? defaultBranch,
        GitReviewRequestState reviewRequestState,
        CancellationToken cancellationToken)
    {
        var apiPullRequests = await GetPullRequestsFromGitHubApiAsync(remote, reviewRequestState, cancellationToken).ConfigureAwait(false);
        return apiPullRequests.IsDefaultOrEmpty
            ? await GetPullRequestsFromRemoteRefsAsync(repositoryPath, remote, defaultBranch, reviewRequestState, cancellationToken).ConfigureAwait(false)
            : apiPullRequests;
    }

    private async Task<ImmutableArray<GitPullRequestInfo>> GetMergeRequestsAsync(
        string repositoryPath,
        GitLabRemote remote,
        string? defaultBranch,
        GitReviewRequestState reviewRequestState,
        CancellationToken cancellationToken)
    {
        var apiMergeRequests = await GetMergeRequestsFromGitLabApiAsync(remote, reviewRequestState, cancellationToken).ConfigureAwait(false);
        return apiMergeRequests.IsDefaultOrEmpty
            ? await GetMergeRequestsFromRemoteRefsAsync(repositoryPath, remote, defaultBranch, reviewRequestState, cancellationToken).ConfigureAwait(false)
            : apiMergeRequests;
    }

    private async Task<ImmutableArray<GitPullRequestInfo>> GetPullRequestsFromGitHubApiAsync(
        GitHubRemote remote,
        GitReviewRequestState reviewRequestState,
        CancellationToken cancellationToken)
    {
        try
        {
            const int pageSize = 100;
            const int maxPages = 20;
            var builder = ImmutableArray.CreateBuilder<GitPullRequestInfo>();
            for (var page = 1; page <= maxPages; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{remote.Repository.Owner}/{remote.Repository.Name}/pulls?state={FormatGitHubReviewRequestState(reviewRequestState)}&per_page={pageSize}&page={page}");
                GitApiRequestHeaders.AddGitHubHeaders(request);
                using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return builder.Count == 0
                        ? ImmutableArray<GitPullRequestInfo>.Empty
                        : SortPullRequests(builder.ToImmutable());
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return builder.Count == 0
                        ? ImmutableArray<GitPullRequestInfo>.Empty
                        : SortPullRequests(builder.ToImmutable());
                }

                var pageCount = 0;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    pageCount++;
                    if (!TryReadPullRequest(item, remote, out var pullRequest) ||
                        !IsReviewRequestStateIncluded(pullRequest.State, reviewRequestState))
                    {
                        continue;
                    }

                    builder.Add(pullRequest);
                }

                if (pageCount < pageSize)
                {
                    break;
                }
            }

            return SortPullRequests(builder.ToImmutable());
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

    private async Task<ImmutableArray<GitPullRequestInfo>> GetMergeRequestsFromGitLabApiAsync(
        GitLabRemote remote,
        GitReviewRequestState reviewRequestState,
        CancellationToken cancellationToken)
    {
        try
        {
            const int pageSize = 100;
            const int maxPages = 20;
            var builder = ImmutableArray.CreateBuilder<GitPullRequestInfo>();
            for (var page = 1; page <= maxPages; page++)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{remote.Repository.ApiBaseUrl}/api/v4/projects/{remote.Repository.EncodedProjectPath}/merge_requests?state={FormatGitLabReviewRequestState(reviewRequestState)}&per_page={pageSize}&page={page}");
                GitApiRequestHeaders.AddGitLabHeaders(request);
                using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return builder.Count == 0
                        ? ImmutableArray<GitPullRequestInfo>.Empty
                        : SortPullRequests(builder.ToImmutable());
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return builder.Count == 0
                        ? ImmutableArray<GitPullRequestInfo>.Empty
                        : SortPullRequests(builder.ToImmutable());
                }

                var pageCount = 0;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    pageCount++;
                    if (!TryReadMergeRequest(item, remote, out var mergeRequest) ||
                        !IsReviewRequestStateIncluded(mergeRequest.State, reviewRequestState))
                    {
                        continue;
                    }

                    builder.Add(mergeRequest);
                }

                if (pageCount < pageSize)
                {
                    break;
                }
            }

            return SortPullRequests(builder.ToImmutable());
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
        GitHubRemote remote,
        string? defaultBranch,
        GitReviewRequestState reviewRequestState,
        CancellationToken cancellationToken)
    {
        if (reviewRequestState != GitReviewRequestState.Open)
        {
            return ImmutableArray<GitPullRequestInfo>.Empty;
        }

        var result = await commandRunner.RunAsync(repositoryPath, ["ls-remote", "--refs", remote.Name, "refs/pull/*/head"], cancellationToken).ConfigureAwait(false);
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

            builder.Add(new GitPullRequestInfo(number, $"PR #{number}", baseRef, reference, remote.Repository.FullName, false, remote.Name));
        }

        return SortPullRequests(builder.ToImmutable());
    }

    private async Task<ImmutableArray<GitPullRequestInfo>> GetMergeRequestsFromRemoteRefsAsync(
        string repositoryPath,
        GitLabRemote remote,
        string? defaultBranch,
        GitReviewRequestState reviewRequestState,
        CancellationToken cancellationToken)
    {
        if (reviewRequestState != GitReviewRequestState.Open)
        {
            return ImmutableArray<GitPullRequestInfo>.Empty;
        }

        var result = await commandRunner.RunAsync(repositoryPath, ["ls-remote", "--refs", remote.Name, "refs/merge-requests/*/head"], cancellationToken).ConfigureAwait(false);
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

            builder.Add(new GitPullRequestInfo(
                number,
                $"MR !{number}",
                baseRef,
                reference,
                remote.Repository.FullName,
                false,
                remote.Name,
                GitReviewRequestKind.MergeRequest));
        }

        return SortPullRequests(builder.ToImmutable());
    }

    private static bool TryReadPullRequest(JsonElement item, GitHubRemote remote, out GitPullRequestInfo pullRequest)
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
        var headRepository = TryGetRepositoryFullName(headElement) ?? remote.Repository.FullName;
        var state = ParseGitHubReviewRequestState(item);
        pullRequest = new GitPullRequestInfo(
            number,
            title,
            baseRef,
            headRef,
            headRepository,
            string.Equals(headRepository, remote.Repository.FullName, StringComparison.OrdinalIgnoreCase),
            remote.Name,
            GitReviewRequestKind.PullRequest,
            state);
        return true;
    }

    private static bool TryReadMergeRequest(JsonElement item, GitLabRemote remote, out GitPullRequestInfo mergeRequest)
    {
        mergeRequest = default!;
        if (!TryGetInt32(item, "iid", out var number) || number <= 0)
        {
            return false;
        }

        var title = TryGetString(item, "title") ?? $"MR !{number}";
        var baseRef = TryGetString(item, "target_branch") ?? "main";
        var headRef = TryGetString(item, "source_branch") ?? string.Empty;
        var state = ParseGitLabReviewRequestState(item);
        var sameRepository = true;
        if (TryGetInt32(item, "source_project_id", out var sourceProjectId) &&
            TryGetInt32(item, "target_project_id", out var targetProjectId))
        {
            sameRepository = sourceProjectId == targetProjectId;
        }

        mergeRequest = new GitPullRequestInfo(
            number,
            title,
            baseRef,
            headRef,
            remote.Repository.FullName,
            sameRepository,
            remote.Name,
            GitReviewRequestKind.MergeRequest,
            state);
        return true;
    }

    private static GitHubRemote SelectPullRequestRemote(ImmutableArray<GitHubRemote> remotes)
    {
        foreach (var preferredName in new[] { "upstream", "origin" })
        {
            var match = remotes.FirstOrDefault(remote => string.Equals(remote.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return remotes[0];
    }

    private static GitLabRemote SelectMergeRequestRemote(ImmutableArray<GitLabRemote> remotes)
    {
        foreach (var preferredName in new[] { "upstream", "origin" })
        {
            var match = remotes.FirstOrDefault(remote => string.Equals(remote.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return remotes[0];
    }

    private static bool TryParseRemoteLine(string line, out string remoteName, out string url, out bool isFetch)
    {
        remoteName = string.Empty;
        url = string.Empty;
        isFetch = false;
        var trimmed = line.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        var firstSeparator = trimmed.IndexOfAny(['\t', ' ']);
        if (firstSeparator <= 0)
        {
            return false;
        }

        remoteName = trimmed[..firstSeparator].Trim();
        var remainder = trimmed[firstSeparator..].Trim();
        var secondSeparator = remainder.IndexOfAny(['\t', ' ']);
        url = secondSeparator < 0 ? remainder : remainder[..secondSeparator].Trim();
        var direction = secondSeparator < 0 ? string.Empty : remainder[secondSeparator..].Trim();
        isFetch = direction.Length == 0 || direction.Contains("(fetch)", StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrWhiteSpace(remoteName) && !string.IsNullOrWhiteSpace(url);
    }

    private static ImmutableArray<GitPullRequestInfo> SortPullRequests(ImmutableArray<GitPullRequestInfo> pullRequests) => pullRequests
        .OrderByDescending(pullRequest => pullRequest.Number)
        .ToImmutableArray();

    private static string FormatGitHubReviewRequestState(GitReviewRequestState state) => state switch
    {
        GitReviewRequestState.Closed or GitReviewRequestState.Merged => "closed",
        GitReviewRequestState.All => "all",
        _ => "open"
    };

    private static string FormatGitLabReviewRequestState(GitReviewRequestState state) => state switch
    {
        GitReviewRequestState.Closed => "closed",
        GitReviewRequestState.Merged => "merged",
        GitReviewRequestState.All => "all",
        _ => "opened"
    };

    private static bool IsReviewRequestStateIncluded(GitReviewRequestState actual, GitReviewRequestState requested) => requested switch
    {
        GitReviewRequestState.All => true,
        _ => actual == requested
    };

    private static GitReviewRequestState ParseGitHubReviewRequestState(JsonElement item)
    {
        var state = TryGetString(item, "state");
        if (string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase))
        {
            return HasStringValue(item, "merged_at") ? GitReviewRequestState.Merged : GitReviewRequestState.Closed;
        }

        return GitReviewRequestState.Open;
    }

    private static GitReviewRequestState ParseGitLabReviewRequestState(JsonElement item)
    {
        var state = TryGetString(item, "state");
        return state?.Trim().ToLowerInvariant() switch
        {
            "closed" => GitReviewRequestState.Closed,
            "merged" => GitReviewRequestState.Merged,
            _ => GitReviewRequestState.Open
        };
    }

    private static string NormalizeRemoteName(string? remoteName) => string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName.Trim();

    private static string FormatRemoteReference(string remoteName, string referenceName)
    {
        var reference = referenceName.Trim();
        return reference.StartsWith("refs/", StringComparison.Ordinal) || reference.StartsWith($"{remoteName}/", StringComparison.Ordinal)
            ? reference
            : $"{remoteName}/{reference}";
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

    private static bool HasStringValue(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString());

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

    private static GitLabRepository? ParseGitLabRepository(string host, string path, string scheme = "https")
    {
        var normalized = path.Trim('/');
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        return new GitLabRepository(host, string.Join('/', parts), parts[^1], scheme);
    }

    private static bool TryParseScpStyleGitRemote(string remoteUrl, out string host, out string path)
    {
        host = string.Empty;
        path = string.Empty;
        var separator = remoteUrl.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || remoteUrl.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var userAndHost = remoteUrl[..separator];
        var atIndex = userAndHost.LastIndexOf('@');
        host = atIndex >= 0 ? userAndHost[(atIndex + 1)..] : userAndHost;
        path = remoteUrl[(separator + 1)..];
        return !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(path);
    }

    private static bool IsGitLabHost(string host) =>
        string.Equals(host, "gitlab.com", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("gitlab.", StringComparison.OrdinalIgnoreCase) ||
        host.Contains(".gitlab", StringComparison.OrdinalIgnoreCase);

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

    private static string FormatReviewRequestState(GitReviewRequestState state) => state switch
    {
        GitReviewRequestState.Closed => "closed",
        GitReviewRequestState.Merged => "merged",
        GitReviewRequestState.All => "all",
        _ => "open"
    };

    private static string FormatReviewRequestCount(int count, GitReviewRequestKind kind, GitReviewRequestState state)
    {
        var singular = kind == GitReviewRequestKind.MergeRequest ? "MR" : "PR";
        var countText = count == 1
            ? $"1 {singular}"
            : $"{count:N0} {singular}s";
        return $"{FormatReviewRequestState(state)} {countText}";
    }
}
