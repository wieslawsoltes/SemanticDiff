using SemanticDiff.Core;

namespace SemanticDiff.Git;

public static class GitDiffCommandBuilder
{
    public static IReadOnlyList<string> BuildDiffArguments(GitDiffRequest request, string? defaultBranch)
    {
        var contextLines = Math.Clamp(request.ContextLines, 0, 1_000_000);
        var arguments = new List<string> { "diff", "--find-renames", "--find-copies", "--no-ext-diff", $"--unified={contextLines}" };

        switch (request.Scope)
        {
            case GitDiffScope.Worktree:
                arguments.Add("HEAD");
                break;
            case GitDiffScope.Unstaged:
                break;
            case GitDiffScope.Staged:
                arguments.Add("--cached");
                break;
            case GitDiffScope.Head:
                arguments.Add("HEAD");
                break;
            case GitDiffScope.Branch:
                AddBranchArguments(arguments, request, defaultBranch);
                break;
            case GitDiffScope.CommitRange:
            case GitDiffScope.Custom:
                if (BuildEndpointRange(request) is { } range)
                {
                    arguments.Add(range);
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

    private static void AddBranchArguments(List<string> arguments, GitDiffRequest request, string? defaultBranch)
    {
        var baseRef = NormalizeRef(request.BaseRef) ?? defaultBranch ?? "origin/main";
        var headRef = NormalizeRef(request.HeadRef);

        if (IsCurrentHeadReference(headRef))
        {
            arguments.Add("--merge-base");
            arguments.Add(baseRef);
            return;
        }

        arguments.Add($"{baseRef}...{headRef}");
    }

    private static string? BuildEndpointRange(GitDiffRequest request)
    {
        var baseRef = NormalizeRef(request.BaseRef);
        var headRef = NormalizeRef(request.HeadRef);
        return baseRef is not null && headRef is not null ? $"{baseRef}..{headRef}" : null;
    }

    private static string? NormalizeRef(string? reference) => string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();

    private static bool IsCurrentHeadReference(string? reference) =>
        string.IsNullOrWhiteSpace(reference) || string.Equals(reference.Trim(), "HEAD", StringComparison.Ordinal);
}