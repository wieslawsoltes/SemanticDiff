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
                return new GitCommandResult(0, "M\0src/Changed.cs\0A\0src/New.xaml\0R100\0src/Old.cs\0src/Renamed.cs\0C100\0src/Original.cs\0src/Copied.cs\0", string.Empty);
            }

            return new GitCommandResult(1, string.Empty, "not found");
        });
        var service = new GitDiffService(runner);

        var snapshot = await service.GetDiffAsync(new GitDiffRequest("/repo", GitDiffScope.Staged), CancellationToken.None);

        Assert.Equal(4, snapshot.Files.Length);
        Assert.Contains(snapshot.Files, file => file.Path == "src/Changed.cs" && file.Status == DiffFileStatus.Modified && file.Language == "C#");
        Assert.Contains(snapshot.Files, file => file.Path == "src/New.xaml" && file.Status == DiffFileStatus.Added && file.Language == "XAML");
        Assert.Contains(snapshot.Files, file => file.Path == "src/Renamed.cs" && file.OldPath == "src/Old.cs" && file.Status == DiffFileStatus.Renamed);
        Assert.Contains(snapshot.Files, file => file.Path == "src/Copied.cs" && file.OldPath == "src/Original.cs" && file.Status == DiffFileStatus.Copied);
    }

    [Fact]
    public async Task GetDiffAsync_IncludesUntrackedFilesForUnstagedDiff()
    {
        var runner = new FakeGitCommandRunner(arguments =>
        {
            if (arguments.SequenceEqual(["symbolic-ref", "--short", "refs/remotes/origin/HEAD"]))
            {
                return new GitCommandResult(0, "origin/main\n", string.Empty);
            }

            if (arguments.Contains("--name-status") && !arguments.Contains("--cached") && !arguments.Contains("HEAD") && !arguments.Contains("--merge-base"))
            {
                return new GitCommandResult(0, "M\0src/TrackedEdit.cs\0", string.Empty);
            }

            if (arguments.SequenceEqual(["status", "--porcelain=v1", "-z", "--untracked-files=all"]))
            {
                return new GitCommandResult(0, "?? src/Untracked.cs\0 M src/TrackedEdit.cs\0", string.Empty);
            }

            return new GitCommandResult(1, string.Empty, "not found");
        });
        var service = new GitDiffService(runner);

        var snapshot = await service.GetDiffAsync(new GitDiffRequest("/repo", GitDiffScope.Unstaged), CancellationToken.None);

        Assert.Equal(2, snapshot.Files.Length);
        Assert.Contains(snapshot.Files, file => file.Path == "src/TrackedEdit.cs" && file.Status == DiffFileStatus.Modified);
        Assert.Contains(snapshot.Files, file => file.Path == "src/Untracked.cs" && file.Status == DiffFileStatus.Untracked);
    }

    [Fact]
    public async Task GetDiffAsync_IncludesCurrentBranchWorkingTreeAndUntrackedFiles()
    {
        var runner = new FakeGitCommandRunner(arguments =>
        {
            if (arguments.SequenceEqual(["symbolic-ref", "--short", "refs/remotes/origin/HEAD"]))
            {
                return new GitCommandResult(0, "origin/main\n", string.Empty);
            }

            if (arguments.Contains("--name-status") && arguments.Contains("--merge-base") && arguments.Contains("origin/main"))
            {
                return new GitCommandResult(0, "A\0src/CommittedNew.cs\0M\0src/TrackedEdit.cs\0", string.Empty);
            }

            if (arguments.SequenceEqual(["status", "--porcelain=v1", "-z", "--untracked-files=all"]))
            {
                return new GitCommandResult(0, "?? src/Untracked.cs\0 M src/TrackedEdit.cs\0", string.Empty);
            }

            return new GitCommandResult(1, string.Empty, "not found");
        });
        var service = new GitDiffService(runner);

        var snapshot = await service.GetDiffAsync(new GitDiffRequest("/repo", GitDiffScope.Branch), CancellationToken.None);

        Assert.Equal(3, snapshot.Files.Length);
        Assert.Contains(snapshot.Files, file => file.Path == "src/CommittedNew.cs" && file.Status == DiffFileStatus.Added);
        Assert.Contains(snapshot.Files, file => file.Path == "src/TrackedEdit.cs" && file.Status == DiffFileStatus.Modified);
        Assert.Contains(snapshot.Files, file => file.Path == "src/Untracked.cs" && file.Status == DiffFileStatus.Untracked);
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

    [Fact]
    public async Task GetFileContentAsync_ReadsWorktreeContentForCurrentBranchDiff()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"SemanticDiffTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repositoryPath, "src"));
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "src", "Changed.cs"), "public sealed class BranchWorktree\n{\n}\n");

        try
        {
            var service = new GitDiffService(new FakeGitCommandRunner(_ => new GitCommandResult(1, string.Empty, "not found")));
            var fileChange = new GitFileChange("src/Changed.cs", null, DiffFileStatus.Modified, 0, 0, "C#");

            var content = await service.GetFileContentAsync(new GitDiffRequest(repositoryPath, GitDiffScope.Branch, "origin/main", "HEAD"), fileChange, CancellationToken.None);

            Assert.Contains("public sealed class BranchWorktree", content);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task GetFileDiffAsync_SynthesizesAddedDiffFromHeadRevisionWhenRangeDiffIsEmpty()
    {
        var runner = new FakeGitCommandRunner(arguments =>
        {
            if (arguments.SequenceEqual(["symbolic-ref", "--short", "refs/remotes/origin/HEAD"]))
            {
                return new GitCommandResult(0, "origin/main\n", string.Empty);
            }

            if (arguments.Contains("main..feature") && arguments.Contains("src/New.cs"))
            {
                return new GitCommandResult(0, string.Empty, string.Empty);
            }

            if (arguments.SequenceEqual(["show", "feature:src/New.cs"]))
            {
                return new GitCommandResult(0, "public sealed class New\n{\n}\n", string.Empty);
            }

            return new GitCommandResult(1, string.Empty, "not found");
        });
        var service = new GitDiffService(runner);
        var fileChange = new GitFileChange("src/New.cs", null, DiffFileStatus.Added, 0, 0, "C#");

        var fileDiff = await service.GetFileDiffAsync(new GitDiffRequest("/repo", GitDiffScope.CommitRange, "main", "feature"), fileChange, CancellationToken.None);

        Assert.Contains("--- /dev/null", fileDiff.UnifiedDiff);
        Assert.Contains("+++ b/src/New.cs", fileDiff.UnifiedDiff);
        Assert.Contains("+public sealed class New", fileDiff.UnifiedDiff);
    }
}