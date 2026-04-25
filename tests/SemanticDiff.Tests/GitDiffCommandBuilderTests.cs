using SemanticDiff.Core;
using SemanticDiff.Git;

namespace SemanticDiff.Tests;

public sealed class GitDiffCommandBuilderTests
{
    [Fact]
    public void BuildDiffArguments_UsesDefaultBranchForBranchDiff()
    {
        var request = new GitDiffRequest("/repo", GitDiffScope.Branch);

        var arguments = GitDiffCommandBuilder.BuildDiffArguments(request, "origin/main");

        Assert.Contains("origin/main...HEAD", arguments);
    }

    [Fact]
    public void BuildDiffArguments_UsesCachedFlagForStagedDiff()
    {
        var request = new GitDiffRequest("/repo", GitDiffScope.Staged);

        var arguments = GitDiffCommandBuilder.BuildDiffArguments(request, null);

        Assert.Contains("--cached", arguments);
    }

    [Fact]
    public void BuildDiffArguments_UsesHeadForWorktreeDiff()
    {
        var request = new GitDiffRequest("/repo", GitDiffScope.Worktree);

        var arguments = GitDiffCommandBuilder.BuildDiffArguments(request, null);

        Assert.Contains("HEAD", arguments);
    }

    [Fact]
    public void BuildDiffArguments_UsesZeroContextByDefault()
    {
        var request = new GitDiffRequest("/repo", GitDiffScope.Worktree);

        var arguments = GitDiffCommandBuilder.BuildDiffArguments(request, null);

        Assert.Contains("--unified=0", arguments);
    }

    [Fact]
    public void BuildDiffArguments_UsesRequestedContextLines()
    {
        var request = new GitDiffRequest("/repo", GitDiffScope.Worktree, ContextLines: 1_000_000);

        var arguments = GitDiffCommandBuilder.BuildDiffArguments(request, null);

        Assert.Contains("--unified=1000000", arguments);
    }

    [Fact]
    public void BuildShowFileArguments_UsesIndexWhenRevisionIsNull()
    {
        var arguments = GitDiffCommandBuilder.BuildShowFileArguments(null, "src/App.cs");

        Assert.Equal(["show", ":src/App.cs"], arguments);
    }

    [Fact]
    public void BuildShowFileArguments_UsesRevisionPathSpec()
    {
        var arguments = GitDiffCommandBuilder.BuildShowFileArguments("HEAD", "src/App.cs");

        Assert.Equal(["show", "HEAD:src/App.cs"], arguments);
    }

    [Fact]
    public void BuildDiffArguments_UsesExplicitCommitRange()
    {
        var request = new GitDiffRequest("/repo", GitDiffScope.CommitRange, "v1.0", "feature/diff");

        var arguments = GitDiffCommandBuilder.BuildDiffArguments(request, "origin/main");

        Assert.Contains("v1.0...feature/diff", arguments);
    }
}