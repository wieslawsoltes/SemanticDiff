using SemanticDiff.Git;

namespace SemanticDiff.Tests;

public sealed class GitRepositoryDiscoveryTests
{
    [Fact]
    public async Task DiscoverRootAsync_UsesRevParseRoot()
    {
        var runner = new FakeGitCommandRunner(arguments =>
            arguments.SequenceEqual(["rev-parse", "--show-toplevel"])
                ? new GitCommandResult(0, "/repo/root\n", string.Empty)
                : new GitCommandResult(1, string.Empty, "unexpected"));
        var discovery = new GitRepositoryDiscovery(runner);

        var root = await discovery.DiscoverRootAsync("/repo/root/src", CancellationToken.None);

        Assert.Equal("/repo/root", root);
    }
}