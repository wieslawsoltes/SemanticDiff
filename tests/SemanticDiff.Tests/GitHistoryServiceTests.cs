using SemanticDiff.Core;
using SemanticDiff.Git;

namespace SemanticDiff.Tests;

public sealed class GitHistoryServiceTests
{
    [Fact]
    public async Task GetHistoryAsync_ParsesCommitTimeline()
    {
        const string output =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\u001faaaaaaa\u001fbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb cccccccccccccccccccccccccccccccccccccccc\u001fAda\u001fada@example.test\u001f2024-01-02T03:04:05+00:00\u001fHEAD -> feature\u001fMerge branch main\n" +
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\u001fbbbbbbb\u001f\u001fGrace\u001fgrace@example.test\u001f2024-01-01T01:02:03+00:00\u001f\u001fInitial commit\n" +
            "cccccccccccccccccccccccccccccccccccccccc\u001fccccccc\u001fbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\u001fGrace\u001fgrace@example.test\u001f2023-12-31T01:02:03+00:00\u001f\u001fMain commit";
        var runner = new FakeGitCommandRunner(arguments => new GitCommandResult(0, output, string.Empty));
        var service = new GitHistoryService(runner);

        var snapshot = await service.GetHistoryAsync(new GitHistoryRequest("/repo", "feature", "origin/main", 50), CancellationToken.None);

        Assert.Equal(3, snapshot.Commits.Length);
        Assert.Equal("feature", snapshot.Request.HeadRef);
        Assert.Equal("origin/main", snapshot.Request.BaseRef);
        Assert.Equal("Merge branch main", snapshot.Commits[0].Subject);
        Assert.True(snapshot.Commits[0].IsMerge);
        Assert.Equal("Grace", snapshot.Commits[1].Author);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", snapshot.Commits[0].ParentIds[0]);
        Assert.Equal("cccccccccccccccccccccccccccccccccccccccc", snapshot.Commits[0].ParentIds[1]);
        Assert.False(snapshot.HasMore);
        Assert.Contains("origin/main..feature", runner.Calls.Single());
        Assert.Contains("--max-count=50", runner.Calls.Single());
        Assert.Contains("--skip=0", runner.Calls.Single());
        Assert.Contains("--topo-order", runner.Calls.Single());
        Assert.DoesNotContain("--graph", runner.Calls.Single());
    }

    [Fact]
    public async Task GetHistoryAsync_ClampsMaxCountAndUsesHeadWhenBaseIsEmpty()
    {
        var runner = new FakeGitCommandRunner(arguments => new GitCommandResult(0, string.Empty, string.Empty));
        var service = new GitHistoryService(runner);

        var snapshot = await service.GetHistoryAsync(new GitHistoryRequest("/repo", "main", MaxCount: 10_000, Skip: -12), CancellationToken.None);

        Assert.Empty(snapshot.Commits);
        Assert.Contains("main", runner.Calls.Single());
        Assert.Contains("--max-count=1000", runner.Calls.Single());
        Assert.Contains("--skip=0", runner.Calls.Single());
    }

    [Fact]
    public async Task GetHistoryAsync_ReportsHasMoreWhenPageIsFull()
    {
        const string output =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\u001faaaaaaa\u001f\u001fAda\u001fada@example.test\u001f2024-01-02T03:04:05+00:00\u001f\u001fFirst\n" +
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\u001fbbbbbbb\u001faaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\u001fAda\u001fada@example.test\u001f2024-01-01T03:04:05+00:00\u001f\u001fSecond";
        var runner = new FakeGitCommandRunner(arguments => new GitCommandResult(0, output, string.Empty));
        var service = new GitHistoryService(runner);

        var snapshot = await service.GetHistoryAsync(new GitHistoryRequest("/repo", "main", MaxCount: 2, Skip: 200), CancellationToken.None);

        Assert.Equal(2, snapshot.Commits.Length);
        Assert.True(snapshot.HasMore);
        Assert.Equal(200, snapshot.Request.Skip);
        Assert.Contains("--skip=200", runner.Calls.Single());
    }

    [Fact]
    public async Task GetHistoryAsync_AddsPathFilterAfterRevision()
    {
        var runner = new FakeGitCommandRunner(arguments => new GitCommandResult(0, string.Empty, string.Empty));
        var service = new GitHistoryService(runner);

        await service.GetHistoryAsync(new GitHistoryRequest("/repo", "HEAD", MaxCount: 40, PathFilter: "src/App.cs"), CancellationToken.None);

        Assert.Equal(["--", "src/App.cs"], runner.Calls.Single().TakeLast(2).ToArray());
    }
}
