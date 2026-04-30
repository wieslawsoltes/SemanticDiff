using System.Net;
using SemanticDiff.Core;
using SemanticDiff.Git;

namespace SemanticDiff.Tests;

public sealed class GitReferenceDiscoveryServiceTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo")]
    [InlineData("git@github.com:owner/repo.git", "owner", "repo")]
    [InlineData("ssh://git@github.com/owner/repo.git", "owner", "repo")]
    public void TryParseGitHubRemoteUrl_ParsesCommonRemoteShapes(string remoteUrl, string owner, string name)
    {
        var repository = GitReferenceDiscoveryService.TryParseGitHubRemoteUrl(remoteUrl);

        Assert.NotNull(repository);
        Assert.Equal(owner, repository.Owner);
        Assert.Equal(name, repository.Name);
    }

    [Theory]
    [InlineData("https://gitlab.com/group/repo.git", "gitlab.com", "group/repo", "repo", "https")]
    [InlineData("http://gitlab.example.com/group/repo.git", "gitlab.example.com", "group/repo", "repo", "http")]
    [InlineData("git@gitlab.com:group/subgroup/repo.git", "gitlab.com", "group/subgroup/repo", "repo", "https")]
    [InlineData("ssh://git@gitlab.example.com/group/repo.git", "gitlab.example.com", "group/repo", "repo", "https")]
    public void TryParseGitLabRemoteUrl_ParsesCommonRemoteShapes(string remoteUrl, string host, string namespacePath, string name, string scheme)
    {
        var repository = GitReferenceDiscoveryService.TryParseGitLabRemoteUrl(remoteUrl);

        Assert.NotNull(repository);
        Assert.Equal(host, repository.Host);
        Assert.Equal(namespacePath, repository.NamespacePath);
        Assert.Equal(name, repository.Name);
        Assert.Equal(scheme, repository.Scheme);
    }

    [Fact]
    public void ParseBranches_SortsCurrentThenDefaultAndSkipsRemoteHead()
    {
        var branches = GitReferenceDiscoveryService.ParseBranches(
            "origin/HEAD\trefs/remotes/origin/HEAD\t\nfeature/review\trefs/heads/feature/review\t*\norigin/main\trefs/remotes/origin/main\t\norigin/feature/review\trefs/remotes/origin/feature/review\t\n",
            "feature/review",
            "origin/main");

        Assert.Equal("feature/review", branches[0].ReferenceName);
        Assert.True(branches[0].IsCurrent);
        Assert.Contains(branches, branch => branch.ReferenceName == "origin/main" && branch.IsDefault);
        Assert.DoesNotContain(branches, branch => branch.ReferenceName.EndsWith("/HEAD", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseGitHubRemotes_ReturnsFetchRemotesOnly()
    {
        var remotes = GitReferenceDiscoveryService.ParseGitHubRemotes("""
            origin  git@github.com:me/repo.git (fetch)
            origin  git@github.com:me/repo.git (push)
            upstream  https://github.com/owner/repo.git (fetch)
            docs  https://example.com/owner/repo.git (fetch)
            """);

        Assert.Collection(
            remotes,
            remote =>
            {
                Assert.Equal("origin", remote.Name);
                Assert.Equal("me/repo", remote.Repository.FullName);
            },
            remote =>
            {
                Assert.Equal("upstream", remote.Name);
                Assert.Equal("owner/repo", remote.Repository.FullName);
            });
    }

    [Fact]
    public void ParseGitLabRemotes_ReturnsFetchRemotesOnly()
    {
        var remotes = GitReferenceDiscoveryService.ParseGitLabRemotes("""
            origin  git@gitlab.com:me/repo.git (fetch)
            origin  git@gitlab.com:me/repo.git (push)
            upstream  https://gitlab.com/group/subgroup/repo.git (fetch)
            docs  https://example.com/owner/repo.git (fetch)
            """);

        Assert.Collection(
            remotes,
            remote =>
            {
                Assert.Equal("origin", remote.Name);
                Assert.Equal("me/repo", remote.Repository.FullName);
            },
            remote =>
            {
                Assert.Equal("upstream", remote.Name);
                Assert.Equal("group/subgroup/repo", remote.Repository.FullName);
            });
    }

    [Fact]
    public async Task GetReferencesAsync_UsesRemotePullRefsWhenGitHubApiUnavailable()
    {
        var runner = new FakeGitCommandRunner(arguments => arguments switch
        {
            _ when arguments.SequenceEqual(["symbolic-ref", "--short", "refs/remotes/origin/HEAD"]) => new GitCommandResult(0, "origin/main\n", string.Empty),
            _ when arguments.SequenceEqual(["for-each-ref", "--format=%(refname:short)%09%(refname)%09%(HEAD)", "refs/heads", "refs/remotes"]) => new GitCommandResult(0, "main\trefs/heads/main\t*\norigin/main\trefs/remotes/origin/main\t\norigin/feature\trefs/remotes/origin/feature\t\n", string.Empty),
            _ when arguments.SequenceEqual(["branch", "--show-current"]) => new GitCommandResult(0, "main\n", string.Empty),
            _ when arguments.SequenceEqual(["remote", "get-url", "origin"]) => new GitCommandResult(0, "https://github.com/owner/repo.git\n", string.Empty),
            _ when arguments.SequenceEqual(["ls-remote", "--refs", "origin", "refs/pull/*/head"]) => new GitCommandResult(0, "abc123\trefs/pull/42/head\n", string.Empty),
            _ => new GitCommandResult(1, string.Empty, "not found")
        });
        using var httpClient = new HttpClient(new StaticResponseHandler(HttpStatusCode.NotFound, "[]"));
        var service = new GitReferenceDiscoveryService(runner, httpClient);

        var snapshot = await service.GetReferencesAsync("/repo", CancellationToken.None);

        Assert.True(snapshot.SupportsReviewRequests);
        Assert.Equal(GitReviewRequestKind.PullRequest, snapshot.ReviewRequestKind);
        Assert.Contains(snapshot.Branches, branch => branch.ReferenceName == "origin/feature");
        var pullRequest = Assert.Single(snapshot.PullRequests);
        Assert.Equal(42, pullRequest.Number);
        Assert.Equal("main", pullRequest.BaseRefName);
        Assert.Equal(GitReviewRequestKind.PullRequest, pullRequest.Kind);
    }

    [Fact]
    public async Task GetReferencesAsync_PrefersUpstreamAndPaginatesGitHubPullRequests()
    {
        var runner = new FakeGitCommandRunner(arguments => arguments switch
        {
            _ when arguments.SequenceEqual(["symbolic-ref", "--short", "refs/remotes/origin/HEAD"]) => new GitCommandResult(0, "origin/main\n", string.Empty),
            _ when arguments.SequenceEqual(["for-each-ref", "--format=%(refname:short)%09%(refname)%09%(HEAD)", "refs/heads", "refs/remotes"]) => new GitCommandResult(0, "main\trefs/heads/main\t*\nupstream/main\trefs/remotes/upstream/main\t\n", string.Empty),
            _ when arguments.SequenceEqual(["branch", "--show-current"]) => new GitCommandResult(0, "main\n", string.Empty),
            _ when arguments.SequenceEqual(["remote", "-v"]) => new GitCommandResult(0, """
                origin  git@github.com:me/repo.git (fetch)
                origin  git@github.com:me/repo.git (push)
                upstream  https://github.com/owner/repo.git (fetch)
                upstream  https://github.com/owner/repo.git (push)
                """, string.Empty),
            _ => new GitCommandResult(1, string.Empty, "not found")
        });
        var handler = new PaginatedPullRequestResponseHandler();
        using var httpClient = new HttpClient(handler);
        var service = new GitReferenceDiscoveryService(runner, httpClient);

        var snapshot = await service.GetReferencesAsync("/repo", CancellationToken.None);

        Assert.True(snapshot.SupportsReviewRequests);
        Assert.Equal(GitReviewRequestKind.PullRequest, snapshot.ReviewRequestKind);
        Assert.Equal(122, snapshot.PullRequests.Length);
        Assert.All(snapshot.PullRequests, pullRequest => Assert.Equal("upstream", pullRequest.RemoteName));
        Assert.Equal(122, snapshot.PullRequests[0].Number);
        Assert.Contains("122 PRs from upstream", snapshot.StatusMessage);
        Assert.All(handler.Requests, request => Assert.Contains("/repos/owner/repo/pulls", request));
        Assert.Equal(1, runner.Calls.Count(call => call.SequenceEqual(["remote", "-v"])));
        Assert.DoesNotContain(runner.Calls, call => call.SequenceEqual(["branch", "--show-current"]));
        Assert.DoesNotContain(runner.Calls, call => call.SequenceEqual(["ls-remote", "--refs", "upstream", "refs/pull/*/head"]));
    }

    [Fact]
    public async Task GetReferencesAsync_FiltersGitHubMergedPullRequests()
    {
        var runner = new FakeGitCommandRunner(arguments => arguments switch
        {
            _ when arguments.SequenceEqual(["symbolic-ref", "--short", "refs/remotes/origin/HEAD"]) => new GitCommandResult(0, "origin/main\n", string.Empty),
            _ when arguments.SequenceEqual(["for-each-ref", "--format=%(refname:short)%09%(refname)%09%(HEAD)", "refs/heads", "refs/remotes"]) => new GitCommandResult(0, "main\trefs/heads/main\t*\norigin/main\trefs/remotes/origin/main\t\n", string.Empty),
            _ when arguments.SequenceEqual(["branch", "--show-current"]) => new GitCommandResult(0, "main\n", string.Empty),
            _ when arguments.SequenceEqual(["remote", "-v"]) => new GitCommandResult(0, "origin  https://github.com/owner/repo.git (fetch)\n", string.Empty),
            _ => new GitCommandResult(1, string.Empty, "not found")
        });
        var handler = new StaticResponseHandler(HttpStatusCode.OK, """
            [
              {
                "number": 10,
                "title": "Merged work",
                "state": "closed",
                "merged_at": "2024-01-02T03:04:05Z",
                "base": { "ref": "main" },
                "head": { "ref": "feature-merged", "repo": { "full_name": "owner/repo" } }
              },
              {
                "number": 9,
                "title": "Closed work",
                "state": "closed",
                "merged_at": null,
                "base": { "ref": "main" },
                "head": { "ref": "feature-closed", "repo": { "full_name": "owner/repo" } }
              }
            ]
            """);
        using var httpClient = new HttpClient(handler);
        var service = new GitReferenceDiscoveryService(runner, httpClient);

        var snapshot = await service.GetReferencesAsync("/repo", CancellationToken.None, GitReviewRequestState.Merged);

        var pullRequest = Assert.Single(snapshot.PullRequests);
        Assert.Equal(10, pullRequest.Number);
        Assert.Equal(GitReviewRequestState.Merged, pullRequest.State);
        Assert.Contains(handler.Requests, request => request.Contains("state=closed", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Calls, call => call.SequenceEqual(["ls-remote", "--refs", "origin", "refs/pull/*/head"]));
    }

    [Fact]
    public async Task GetReferencesAsync_PrefersGitLabMergeRequestsWhenAvailable()
    {
        var runner = new FakeGitCommandRunner(arguments => arguments switch
        {
            _ when arguments.SequenceEqual(["symbolic-ref", "--short", "refs/remotes/origin/HEAD"]) => new GitCommandResult(0, "origin/main\n", string.Empty),
            _ when arguments.SequenceEqual(["for-each-ref", "--format=%(refname:short)%09%(refname)%09%(HEAD)", "refs/heads", "refs/remotes"]) => new GitCommandResult(0, "main\trefs/heads/main\t*\nupstream/main\trefs/remotes/upstream/main\t\n", string.Empty),
            _ when arguments.SequenceEqual(["branch", "--show-current"]) => new GitCommandResult(0, "main\n", string.Empty),
            _ when arguments.SequenceEqual(["remote", "-v"]) => new GitCommandResult(0, """
                origin  git@github.com:me/repo.git (fetch)
                origin  git@github.com:me/repo.git (push)
                upstream  https://gitlab.com/group/repo.git (fetch)
                upstream  https://gitlab.com/group/repo.git (push)
                """, string.Empty),
            _ => new GitCommandResult(1, string.Empty, "not found")
        });
        var handler = new StaticResponseHandler(HttpStatusCode.OK, """
            [
              {
                "iid": 24,
                "title": "Improve review flow",
                "target_branch": "main",
                "source_branch": "feature/review-flow",
                "source_project_id": 10,
                "target_project_id": 10
              }
            ]
            """);
        using var httpClient = new HttpClient(handler);
        var service = new GitReferenceDiscoveryService(runner, httpClient);

        var snapshot = await service.GetReferencesAsync("/repo", CancellationToken.None);

        Assert.True(snapshot.SupportsReviewRequests);
        Assert.Equal(GitReviewRequestKind.MergeRequest, snapshot.ReviewRequestKind);
        var mergeRequest = Assert.Single(snapshot.PullRequests);
        Assert.Equal(24, mergeRequest.Number);
        Assert.Equal("Improve review flow", mergeRequest.Title);
        Assert.Equal("main", mergeRequest.BaseRefName);
        Assert.Equal("feature/review-flow", mergeRequest.HeadRefName);
        Assert.Equal("group/repo", mergeRequest.HeadRepository);
        Assert.Equal("upstream", mergeRequest.RemoteName);
        Assert.Equal(GitReviewRequestKind.MergeRequest, mergeRequest.Kind);
        Assert.Contains("1 MR from upstream", snapshot.StatusMessage);
        Assert.Contains(handler.Requests, request => request.Contains("/api/v4/projects/group%2Frepo/merge_requests", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Calls, call => call.SequenceEqual(["ls-remote", "--refs", "upstream", "refs/merge-requests/*/head"]));
    }

    [Fact]
    public async Task GetReferencesAsync_UsesGitLabMergeRequestStateFilter()
    {
        var runner = new FakeGitCommandRunner(arguments => arguments switch
        {
            _ when arguments.SequenceEqual(["symbolic-ref", "--short", "refs/remotes/origin/HEAD"]) => new GitCommandResult(0, "origin/main\n", string.Empty),
            _ when arguments.SequenceEqual(["for-each-ref", "--format=%(refname:short)%09%(refname)%09%(HEAD)", "refs/heads", "refs/remotes"]) => new GitCommandResult(0, "main\trefs/heads/main\t*\norigin/main\trefs/remotes/origin/main\t\n", string.Empty),
            _ when arguments.SequenceEqual(["branch", "--show-current"]) => new GitCommandResult(0, "main\n", string.Empty),
            _ when arguments.SequenceEqual(["remote", "-v"]) => new GitCommandResult(0, "origin  https://gitlab.com/group/repo.git (fetch)\n", string.Empty),
            _ => new GitCommandResult(1, string.Empty, "not found")
        });
        var handler = new StaticResponseHandler(HttpStatusCode.OK, """
            [
              {
                "iid": 24,
                "title": "Merged flow",
                "state": "merged",
                "target_branch": "main",
                "source_branch": "feature/review-flow",
                "source_project_id": 10,
                "target_project_id": 10
              }
            ]
            """);
        using var httpClient = new HttpClient(handler);
        var service = new GitReferenceDiscoveryService(runner, httpClient);

        var snapshot = await service.GetReferencesAsync("/repo", CancellationToken.None, GitReviewRequestState.Merged);

        var mergeRequest = Assert.Single(snapshot.PullRequests);
        Assert.Equal(24, mergeRequest.Number);
        Assert.Equal(GitReviewRequestState.Merged, mergeRequest.State);
        Assert.Contains(handler.Requests, request => request.Contains("state=merged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetReferencesAsync_UsesRemoteMergeRequestRefsWhenGitLabApiUnavailable()
    {
        var runner = new FakeGitCommandRunner(arguments => arguments switch
        {
            _ when arguments.SequenceEqual(["symbolic-ref", "--short", "refs/remotes/origin/HEAD"]) => new GitCommandResult(0, "origin/main\n", string.Empty),
            _ when arguments.SequenceEqual(["for-each-ref", "--format=%(refname:short)%09%(refname)%09%(HEAD)", "refs/heads", "refs/remotes"]) => new GitCommandResult(0, "main\trefs/heads/main\t*\norigin/main\trefs/remotes/origin/main\t\n", string.Empty),
            _ when arguments.SequenceEqual(["branch", "--show-current"]) => new GitCommandResult(0, "main\n", string.Empty),
            _ when arguments.SequenceEqual(["remote", "-v"]) => new GitCommandResult(0, "origin  https://gitlab.com/group/repo.git (fetch)\n", string.Empty),
            _ when arguments.SequenceEqual(["ls-remote", "--refs", "origin", "refs/merge-requests/*/head"]) => new GitCommandResult(0, "abc123\trefs/merge-requests/42/head\n", string.Empty),
            _ => new GitCommandResult(1, string.Empty, "not found")
        });
        using var httpClient = new HttpClient(new StaticResponseHandler(HttpStatusCode.NotFound, "[]"));
        var service = new GitReferenceDiscoveryService(runner, httpClient);

        var snapshot = await service.GetReferencesAsync("/repo", CancellationToken.None);

        Assert.True(snapshot.SupportsReviewRequests);
        Assert.Equal(GitReviewRequestKind.MergeRequest, snapshot.ReviewRequestKind);
        var mergeRequest = Assert.Single(snapshot.PullRequests);
        Assert.Equal(42, mergeRequest.Number);
        Assert.Equal("main", mergeRequest.BaseRefName);
        Assert.Equal("refs/merge-requests/42/head", mergeRequest.HeadRefName);
        Assert.Equal(GitReviewRequestKind.MergeRequest, mergeRequest.Kind);
    }

    [Fact]
    public async Task EnsurePullRequestHeadAsync_FetchesStableRemotePullReference()
    {
        var runner = new FakeGitCommandRunner(arguments =>
            arguments.SequenceEqual(["fetch", "--quiet", "origin", "pull/42/head:refs/remotes/origin/pull/42/head"])
                ? new GitCommandResult(0, string.Empty, string.Empty)
                : new GitCommandResult(1, string.Empty, "not found"));
        var service = new GitReferenceDiscoveryService(runner, new HttpClient(new StaticResponseHandler(HttpStatusCode.NotFound, "[]")));
        var pullRequest = new GitPullRequestInfo(42, "Add review", "main", "feature/review", "owner/repo", true);

        var headRef = await service.EnsurePullRequestHeadAsync("/repo", pullRequest, CancellationToken.None);

        Assert.Equal("refs/remotes/origin/pull/42/head", headRef);
        Assert.Contains(runner.Calls, call => call.SequenceEqual(["fetch", "--quiet", "origin", "pull/42/head:refs/remotes/origin/pull/42/head"]));
    }

    [Fact]
    public async Task EnsurePullRequestHeadAsync_FetchesFromPullRequestRemote()
    {
        var runner = new FakeGitCommandRunner(arguments =>
            arguments.SequenceEqual(["fetch", "--quiet", "upstream", "pull/42/head:refs/remotes/upstream/pull/42/head"])
                ? new GitCommandResult(0, string.Empty, string.Empty)
                : new GitCommandResult(1, string.Empty, "not found"));
        var service = new GitReferenceDiscoveryService(runner, new HttpClient(new StaticResponseHandler(HttpStatusCode.NotFound, "[]")));
        var pullRequest = new GitPullRequestInfo(42, "Add review", "main", "feature/review", "owner/repo", true, "upstream");

        var headRef = await service.EnsurePullRequestHeadAsync("/repo", pullRequest, CancellationToken.None);

        Assert.Equal("refs/remotes/upstream/pull/42/head", headRef);
        Assert.Contains(runner.Calls, call => call.SequenceEqual(["fetch", "--quiet", "upstream", "pull/42/head:refs/remotes/upstream/pull/42/head"]));
    }

    [Fact]
    public async Task EnsurePullRequestHeadAsync_FetchesGitLabMergeRequestReference()
    {
        var runner = new FakeGitCommandRunner(arguments =>
            arguments.SequenceEqual(["fetch", "--quiet", "upstream", "merge-requests/42/head:refs/remotes/upstream/merge-requests/42/head"])
                ? new GitCommandResult(0, string.Empty, string.Empty)
                : new GitCommandResult(1, string.Empty, "not found"));
        var service = new GitReferenceDiscoveryService(runner, new HttpClient(new StaticResponseHandler(HttpStatusCode.NotFound, "[]")));
        var mergeRequest = new GitPullRequestInfo(42, "Add review", "main", "feature/review", "group/repo", true, "upstream", GitReviewRequestKind.MergeRequest);

        var headRef = await service.EnsurePullRequestHeadAsync("/repo", mergeRequest, CancellationToken.None);

        Assert.Equal("refs/remotes/upstream/merge-requests/42/head", headRef);
        Assert.Contains(runner.Calls, call => call.SequenceEqual(["fetch", "--quiet", "upstream", "merge-requests/42/head:refs/remotes/upstream/merge-requests/42/head"]));
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode statusCode;
        private readonly string content;

        public List<string> Requests { get; } = [];

        public StaticResponseHandler(HttpStatusCode statusCode, string content)
        {
            this.statusCode = statusCode;
            this.content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            });
        }
    }

    private sealed class PaginatedPullRequestResponseHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;
            Requests.Add(requestUri);

            var page = requestUri.Contains("page=2", StringComparison.Ordinal) ? 2 : 1;
            var content = page == 1
                ? CreatePullRequestPage(1, 100)
                : CreatePullRequestPage(101, 22);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            });
        }

        private static string CreatePullRequestPage(int firstNumber, int count)
        {
            var items = Enumerable.Range(firstNumber, count).Select(number => $$"""
                {
                    "number": {{number}},
                    "title": "PR {{number}}",
                    "base": { "ref": "main" },
                    "head": { "ref": "feature-{{number}}", "repo": { "full_name": "owner/repo" } }
                }
                """);
            return $"[{string.Join(",", items)}]";
        }
    }
}
