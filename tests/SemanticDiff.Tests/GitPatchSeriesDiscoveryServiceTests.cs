using SemanticDiff.Core;
using SemanticDiff.Git;

namespace SemanticDiff.Tests;

public sealed class GitPatchSeriesDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoverAsync_LocalRepository_ParsesBranchesRemoteBranchesAndTags()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), "semanticdiff-discovery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryPath);
        var runner = new FakeGitCommandRunner(arguments => arguments switch
        {
            _ when arguments.SequenceEqual(["rev-parse", "--show-toplevel"]) =>
                new GitCommandResult(0, $"{repositoryPath}\n", string.Empty),
            _ when arguments.SequenceEqual(["symbolic-ref", "--quiet", "--short", "HEAD"]) =>
                new GitCommandResult(0, "main\n", string.Empty),
            _ when arguments[0] == "for-each-ref" =>
                new GitCommandResult(0, """
                    main	refs/heads/main	1111111	2024-01-02 00:00:00 +0000	main commit	*
                    feature/x	refs/heads/feature/x	2222222	2024-01-03 00:00:00 +0000	feature commit	-
                    origin/HEAD	refs/remotes/origin/HEAD	1111111	2024-01-02 00:00:00 +0000	main commit	-
                    origin/main	refs/remotes/origin/main	3333333	2024-01-04 00:00:00 +0000	remote main	-
                    v1.0.0	refs/tags/v1.0.0	4444444	2024-01-05 00:00:00 +0000	release	-
                    """, string.Empty),
            _ => new GitCommandResult(1, string.Empty, $"unexpected command: {string.Join(' ', arguments)}")
        });
        var service = new GitPatchSeriesDiscoveryService(runner);
        var request = new GitPatchSeriesDiscoveryRequest(
            GitPatchSeriesRepositorySourceKind.LocalRepository,
            repositoryPath);

        var snapshot = await service.DiscoverAsync(request, CancellationToken.None);

        Assert.Equal(repositoryPath, snapshot.RepositoryPath);
        Assert.Equal(4, snapshot.RefCount);
        Assert.Equal("main", snapshot.Refs[0].RangeName);
        Assert.True(snapshot.Refs[0].IsCurrent);
        Assert.True(snapshot.Refs[0].IsDefault);
        Assert.Contains(snapshot.Refs, item => item.Kind == GitPatchSeriesRefKind.RemoteBranch && item.RangeName == "origin/main");
        Assert.Contains(snapshot.Refs, item => item.Kind == GitPatchSeriesRefKind.Tag && item.RangeName == "v1.0.0");
    }

    [Fact]
    public async Task DiscoverAsync_RemoteUrl_ClonesIntoStableCacheAndDiscoversRefs()
    {
        const string remoteUrl = "https://example.com/org/repo.git";
        var cacheRoot = Path.Combine(Path.GetTempPath(), "semanticdiff-discovery-tests", Guid.NewGuid().ToString("N"));
        var runner = new FakeGitCommandRunner(arguments => arguments switch
        {
            _ when arguments.Count == 5 && arguments[0] == "clone" && arguments[1] == "--bare" =>
                new GitCommandResult(0, string.Empty, string.Empty),
            _ when arguments.SequenceEqual(["symbolic-ref", "--quiet", "--short", "HEAD"]) =>
                new GitCommandResult(0, "main\n", string.Empty),
            _ when arguments[0] == "for-each-ref" =>
                new GitCommandResult(0, """
                    main	refs/heads/main	1111111	2024-01-02 00:00:00 +0000	main commit	*
                    chrome/m119	refs/heads/chrome/m119	2222222	2024-01-03 00:00:00 +0000	chrome branch	-
                    """, string.Empty),
            _ => new GitCommandResult(1, string.Empty, $"unexpected command: {string.Join(' ', arguments)}")
        });
        var service = new GitPatchSeriesDiscoveryService(runner, cacheRoot);
        var request = new GitPatchSeriesDiscoveryRequest(
            GitPatchSeriesRepositorySourceKind.RemoteUrl,
            remoteUrl);

        var snapshot = await service.DiscoverAsync(request, CancellationToken.None);

        Assert.StartsWith(cacheRoot, snapshot.RepositoryPath, StringComparison.Ordinal);
        Assert.Contains(runner.Calls, call => call.Count == 5 && call[0] == "clone" && call[3] == remoteUrl);
        Assert.Contains(snapshot.Refs, item => item.RangeName == "chrome/m119");
    }

    [Theory]
    [InlineData("", GitPatchSeriesRepositorySourceKind.CurrentRepository)]
    [InlineData("https://example.com/org/repo.git", GitPatchSeriesRepositorySourceKind.RemoteUrl)]
    [InlineData("git@example.com:org/repo.git", GitPatchSeriesRepositorySourceKind.RemoteUrl)]
    public void InferSourceKind_ClassifiesCommonSources(string sourceText, GitPatchSeriesRepositorySourceKind expected)
    {
        var kind = GitPatchSeriesDiscoveryService.InferSourceKind(sourceText, null);

        Assert.Equal(expected, kind);
    }
}
