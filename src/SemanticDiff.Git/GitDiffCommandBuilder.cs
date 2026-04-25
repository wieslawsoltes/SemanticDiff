using SemanticDiff.Core;

namespace SemanticDiff.Git;

public static class GitDiffCommandBuilder
{
    public static IReadOnlyList<string> BuildDiffArguments(GitDiffRequest request, string? defaultBranch)
    {
        var contextLines = Math.Clamp(request.ContextLines, 0, 1_000_000);
        var arguments = new List<string> { "diff", "--find-renames", "--no-ext-diff", $"--unified={contextLines}" };

        switch (request.Scope)
        {
            case GitDiffScope.Worktree:
                arguments.Add("HEAD");
                break;
            case GitDiffScope.Staged:
                arguments.Add("--cached");
                break;
            case GitDiffScope.Head:
                arguments.Add("HEAD");
                break;
            case GitDiffScope.Branch:
                arguments.Add(BuildBranchRange(request, defaultBranch));
                break;
            case GitDiffScope.CommitRange:
            case GitDiffScope.Custom:
                if (!string.IsNullOrWhiteSpace(request.BaseRef) && !string.IsNullOrWhiteSpace(request.HeadRef))
                {
                    arguments.Add($"{request.BaseRef}...{request.HeadRef}");
                }
                break;
        }

        if (!string.IsNullOrWhiteSpace(request.PathFilter))
        {
            arguments.Add("--");
            arguments.Add(request.PathFilter);
        }

        return arguments;
    }

    public static IReadOnlyList<string> BuildShowFileArguments(string? revision, string path) =>
        ["show", string.IsNullOrEmpty(revision) ? $":{path}" : $"{revision}:{path}"];

    public static IReadOnlyList<string> BuildNameStatusArguments(GitDiffRequest request, string? defaultBranch)
    {
        var arguments = BuildDiffArguments(request, defaultBranch).ToList();
        arguments.Insert(1, "--name-status");
        arguments.Insert(2, "-z");
        return arguments;
    }

    public static IReadOnlyList<string> BuildWorktreeStatusArguments() =>
        ["status", "--porcelain=v1", "-z", "--untracked-files=all"];

    private static string BuildBranchRange(GitDiffRequest request, string? defaultBranch)
    {
        var baseRef = request.BaseRef ?? defaultBranch ?? "origin/main";
        var headRef = request.HeadRef ?? "HEAD";
        return $"{baseRef}...{headRef}";
    }
}