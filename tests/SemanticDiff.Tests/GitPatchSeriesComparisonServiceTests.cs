using SemanticDiff.Core;
using SemanticDiff.Git;

namespace SemanticDiff.Tests;

public sealed class GitPatchSeriesComparisonServiceTests
{
    [Fact]
    public async Task CompareAsync_UsesArbitraryRangesAndParsesRangeDiff()
    {
        const string oldRange = "chrome/m119..skia-fork/m119-patches";
        const string newRange = "chrome/m147..skia-fork/m147-patches";
        var runner = new FakeGitCommandRunner(arguments => arguments switch
        {
            _ when IsLog(arguments, oldRange) => new GitCommandResult(0,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\u001faaaaaaa\u001fAda\u001f2024-01-01T00:00:00+00:00\u001fkeep patch\n" +
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\u001fbbbbbbb\u001fAda\u001f2024-01-02T00:00:00+00:00\u001fupdate patch\n" +
                "cccccccccccccccccccccccccccccccccccccccc\u001fccccccc\u001fAda\u001f2024-01-03T00:00:00+00:00\u001fdropped patch", string.Empty),
            _ when IsLog(arguments, newRange) => new GitCommandResult(0,
                "dddddddddddddddddddddddddddddddddddddddd\u001fddddddd\u001fGrace\u001f2024-02-01T00:00:00+00:00\u001fkeep patch\n" +
                "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\u001feeeeeee\u001fGrace\u001f2024-02-02T00:00:00+00:00\u001fupdate patch\n" +
                "ffffffffffffffffffffffffffffffffffffffff\u001fffffff\u001fGrace\u001f2024-02-03T00:00:00+00:00\u001fnew patch", string.Empty),
            _ when IsDiff(arguments, oldRange) => new GitCommandResult(0, "M\0src/Old.cs\0A\0src/Added.cs\0", string.Empty),
            _ when IsDiff(arguments, newRange) => new GitCommandResult(0, "M\0src/New.cs\0R100\0src/Before.cs\0src/After.cs\0", string.Empty),
            _ when arguments.SequenceEqual(["range-diff", "--no-color", oldRange, newRange]) => new GitCommandResult(0, """
                1:  aaaaaaa = 1:  ddddddd keep patch
                2:  bbbbbbb ! 2:  eeeeeee update patch
                    @@ commit message
                3:  ccccccc < -:  ------- dropped patch
                -:  ------- > 3:  fffffff new patch
                """, string.Empty),
            _ => new GitCommandResult(1, string.Empty, $"unexpected command: {string.Join(' ', arguments)}")
        });
        var service = new GitPatchSeriesComparisonService(runner);

        var snapshot = await service.CompareAsync(new GitPatchSeriesComparisonRequest("/repo", oldRange, newRange), CancellationToken.None);

        Assert.Equal(oldRange, snapshot.OldSeries.RangeText);
        Assert.Equal(newRange, snapshot.NewSeries.RangeText);
        Assert.Equal(3, snapshot.OldSeries.CommitCount);
        Assert.Equal(3, snapshot.NewSeries.CommitCount);
        Assert.Equal(2, snapshot.OldSeries.FileCount);
        Assert.Equal(2, snapshot.NewSeries.FileCount);
        Assert.Equal(4, snapshot.Items.Length);
        Assert.Equal(1, snapshot.UnchangedCount);
        Assert.Equal(1, snapshot.ModifiedCount);
        Assert.Equal(1, snapshot.RemovedCount);
        Assert.Equal(1, snapshot.AddedCount);
        Assert.Equal("@@ commit message", snapshot.Items[1].DetailText);
        Assert.Contains(runner.Calls, call => call.SequenceEqual(["range-diff", "--no-color", oldRange, newRange]));
        Assert.Contains(runner.Calls, call => IsLog(call, oldRange));
        Assert.Contains(runner.Calls, call => IsLog(call, newRange));
    }

    [Fact]
    public async Task CompareAsync_ReturnsFailureStatusWhenRangeDiffFails()
    {
        var runner = new FakeGitCommandRunner(arguments => arguments switch
        {
            _ when arguments[0] is "log" => new GitCommandResult(0, string.Empty, string.Empty),
            _ when arguments[0] is "diff" => new GitCommandResult(0, string.Empty, string.Empty),
            _ when arguments[0] is "range-diff" => new GitCommandResult(128, string.Empty, "fatal: bad revision"),
            _ => new GitCommandResult(1, string.Empty, "unexpected")
        });
        var service = new GitPatchSeriesComparisonService(runner);

        var snapshot = await service.CompareAsync(new GitPatchSeriesComparisonRequest("/repo", "old..head", "new..head"), CancellationToken.None);

        Assert.StartsWith("Patch comparison failed:", snapshot.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(snapshot.Items);
    }

    private static bool IsLog(IReadOnlyList<string> arguments, string range) =>
        arguments.Count == 4 &&
        arguments[0] == "log" &&
        arguments[1] == "--reverse" &&
        arguments[3] == range;

    private static bool IsDiff(IReadOnlyList<string> arguments, string range) =>
        arguments.SequenceEqual(["diff", "--name-status", "-z", range]);
}
