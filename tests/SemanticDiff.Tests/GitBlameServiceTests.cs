using SemanticDiff.Git;

namespace SemanticDiff.Tests;

public sealed class GitBlameServiceTests
{
    [Fact]
    public async Task GetFileBlameAsync_RunsBlameAndParsesLinePorcelainOutput()
    {
        var runner = new FakeGitCommandRunner(arguments => new GitCommandResult(0, BlameOutput, string.Empty));
        var service = new GitBlameService(runner);

        var blame = await service.GetFileBlameAsync("/repo", "src/App.cs", CancellationToken.None);

        Assert.Contains(runner.Calls, call => call.SequenceEqual(["blame", "--line-porcelain", "--", "src/App.cs"]));
        Assert.Equal(2, blame.Lines.Length);
        Assert.Equal("Ada Lovelace", blame.Lines[0].Author);
        Assert.Equal("Initial semantic graph", blame.Lines[0].Summary);
        Assert.Equal(2, blame.Lines[1].LineNumber);
        Assert.Equal("Grace Hopper", blame.Lines[1].Author);
    }

    [Fact]
    public async Task GetFileBlameAsync_ReturnsEmptyBlameWhenGitFails()
    {
        var runner = new FakeGitCommandRunner(arguments => new GitCommandResult(128, string.Empty, "fatal: no such path"));
        var service = new GitBlameService(runner);

        var blame = await service.GetFileBlameAsync("/repo", "src/Missing.cs", CancellationToken.None);

        Assert.Empty(blame.Lines);
    }

    private const string BlameOutput =
        "1111111111111111111111111111111111111111 1 1 1\n" +
        "author Ada Lovelace\n" +
        "author-time 1700000000\n" +
        "summary Initial semantic graph\n" +
        "\tpublic sealed class App\n" +
        "2222222222222222222222222222222222222222 2 2 1\n" +
        "author Grace Hopper\n" +
        "author-time 1710000000\n" +
        "summary Add review actions\n" +
        "\t{\n";
}