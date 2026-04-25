using SemanticDiff.Git;

namespace SemanticDiff.Tests;

public sealed class GitReviewServiceTests
{
    [Fact]
    public async Task StageFileAsync_RunsGitAddForPath()
    {
        var runner = new FakeGitCommandRunner(arguments => new GitCommandResult(0, string.Empty, string.Empty));
        var service = new GitReviewService(runner);

        var result = await service.StageFileAsync("/repo", "src/App.cs", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(runner.Calls, call => call.SequenceEqual(["add", "--", "src/App.cs"]));
    }

    [Fact]
    public async Task UnstageFileAsync_RunsGitRestoreStagedForPath()
    {
        var runner = new FakeGitCommandRunner(arguments => new GitCommandResult(0, string.Empty, string.Empty));
        var service = new GitReviewService(runner);

        var result = await service.UnstageFileAsync("/repo", "src/App.cs", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(runner.Calls, call => call.SequenceEqual(["restore", "--staged", "--", "src/App.cs"]));
    }

    [Fact]
    public async Task StageFileAsync_ReturnsGitErrorMessage()
    {
        var runner = new FakeGitCommandRunner(arguments => new GitCommandResult(128, string.Empty, "fatal: bad path"));
        var service = new GitReviewService(runner);

        var result = await service.StageFileAsync("/repo", "src/Missing.cs", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("fatal: bad path", result.Message);
    }
}