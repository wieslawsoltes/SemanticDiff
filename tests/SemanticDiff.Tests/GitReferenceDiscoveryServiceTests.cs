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

        Assert.True(snapshot.IsGitHubRepository);
        Assert.Contains(snapshot.Branches, branch => branch.ReferenceName == "origin/feature");
        var pullRequest = Assert.Single(snapshot.PullRequests);
        Assert.Equal(42, pullRequest.Number);
        Assert.Equal("main", pullRequest.BaseRefName);
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

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode statusCode;
        private readonly string content;

        public StaticResponseHandler(HttpStatusCode statusCode, string content)
        {
            this.statusCode = statusCode;
            this.content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content)
        });
    }
}