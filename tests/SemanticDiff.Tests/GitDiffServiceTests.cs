using SemanticDiff.Core;
using SemanticDiff.Git;

namespace SemanticDiff.Tests;

public sealed class GitDiffServiceTests
{
    [Fact]
    public async Task GetDiffAsync_ParsesWorktreeStatus()
    {
        var runner = new FakeGitCommandRunner(arguments =>
        {
            if (arguments.SequenceEqual(["status", "--porcelain=v1", "-z", "--untracked-files=all"]))
            {
                return new GitCommandResult(0, "?? src/NewFile.cs\0 M src/Changed.axaml\0", string.Empty);
            }

            return new GitCommandResult(1, string.Empty, "not found");
        });
        var service = new GitDiffService(runner);

        var snapshot = await service.GetDiffAsync(new GitDiffRequest("/repo", GitDiffScope.Worktree), CancellationToken.None);

        Assert.Equal(2, snapshot.Files.Length);
        Assert.Contains(snapshot.Files, file => file.Path == "src/NewFile.cs" && file.Status == DiffFileStatus.Untracked && file.Language == "C#");
        Assert.Contains(snapshot.Files, file => file.Path == "src/Changed.axaml" && file.Status == DiffFileStatus.Modified && file.Language == "AXAML");
    }

    [Fact]
    public async Task GetDiffAsync_ParsesNameStatusWithoutBuilderCapacityFailure()
    {
        var runner = new FakeGitCommandRunner(arguments =>
        {
            if (arguments.SequenceEqual(["symbolic-ref", "--short", "refs/remotes/origin/HEAD"]))
            {
                return new GitCommandResult(0, "origin/main\n", string.Empty);
            }

            if (arguments.Contains("--name-status") && arguments.Contains("--cached"))
            {
                return new GitCommandResult(0, "M\0src/Changed.cs\0A\0src/New.xaml\0R100\0src/Old.cs\0src/Renamed.cs\0", string.Empty);
            }

            return new GitCommandResult(1, string.Empty, "not found");
        });
        var service = new GitDiffService(runner);

        var snapshot = await service.GetDiffAsync(new GitDiffRequest("/repo", GitDiffScope.Staged), CancellationToken.None);

        Assert.Equal(3, snapshot.Files.Length);
        Assert.Contains(snapshot.Files, file => file.Path == "src/Changed.cs" && file.Status == DiffFileStatus.Modified && file.Language == "C#");
        Assert.Contains(snapshot.Files, file => file.Path == "src/New.xaml" && file.Status == DiffFileStatus.Added && file.Language == "XAML");
        Assert.Contains(snapshot.Files, file => file.Path == "src/Renamed.cs" && file.OldPath == "src/Old.cs" && file.Status == DiffFileStatus.Renamed);
    }

    [Fact]
    public async Task GetFileDiffAsync_CreatesUnifiedDiffForUntrackedFile()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"SemanticDiffTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repositoryPath, "src"));
        var filePath = Path.Combine(repositoryPath, "src", "NewFile.cs");
        await File.WriteAllTextAsync(filePath, "public sealed class NewFile\n{\n}\n");

        try
        {
            var service = new GitDiffService(new FakeGitCommandRunner(_ => new GitCommandResult(1, string.Empty, "not found")));
            var fileChange = new GitFileChange("src/NewFile.cs", null, DiffFileStatus.Untracked, 0, 0, "C#");

            var fileDiff = await service.GetFileDiffAsync(new GitDiffRequest(repositoryPath, GitDiffScope.Worktree), fileChange, CancellationToken.None);

            Assert.Contains("--- /dev/null", fileDiff.UnifiedDiff);
            Assert.Contains("+++ b/src/NewFile.cs", fileDiff.UnifiedDiff);
            Assert.Contains("+public sealed class NewFile", fileDiff.UnifiedDiff);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task GetFileContentAsync_ReadsWorktreeContentForWorktreeDiff()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"SemanticDiffTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repositoryPath, "src"));
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "src", "Changed.cs"), "public sealed class Changed\n{\n}\n");

        try
        {
            var service = new GitDiffService(new FakeGitCommandRunner(_ => new GitCommandResult(1, string.Empty, "not found")));
            var fileChange = new GitFileChange("src/Changed.cs", null, DiffFileStatus.Modified, 0, 0, "C#");

            var content = await service.GetFileContentAsync(new GitDiffRequest(repositoryPath, GitDiffScope.Worktree), fileChange, CancellationToken.None);

            Assert.Contains("public sealed class Changed", content);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task GetFileContentAsync_UsesIndexForStagedDiff()
    {
        var runner = new FakeGitCommandRunner(arguments =>
            arguments.SequenceEqual(["show", ":src/Staged.cs"])
                ? new GitCommandResult(0, "public sealed class Staged\n{\n}\n", string.Empty)
                : new GitCommandResult(1, string.Empty, "not found"));
        var service = new GitDiffService(runner);
        var fileChange = new GitFileChange("src/Staged.cs", null, DiffFileStatus.Modified, 0, 0, "C#");

        var content = await service.GetFileContentAsync(new GitDiffRequest("/repo", GitDiffScope.Staged), fileChange, CancellationToken.None);

        Assert.Contains("public sealed class Staged", content);
        Assert.Contains(runner.Calls, call => call.SequenceEqual(["show", ":src/Staged.cs"]));
    }
}