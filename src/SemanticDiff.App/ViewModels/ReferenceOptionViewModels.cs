using SemanticDiff.Core;

namespace SemanticDiff.App.ViewModels;

public sealed record GitBranchOptionViewModel(
    string DisplayName,
    string ReferenceName,
    bool IsCurrent,
    bool IsDefault)
{
    public static GitBranchOptionViewModel FromBranch(GitBranchInfo branch)
    {
        var suffix = branch switch
        {
            { IsCurrent: true, IsDefault: true } => "  current, default",
            { IsCurrent: true } => "  current",
            { IsDefault: true } => "  default",
            { IsRemote: true } => "  remote",
            _ => string.Empty
        };
        return new GitBranchOptionViewModel($"{branch.ReferenceName}{suffix}", branch.ReferenceName, branch.IsCurrent, branch.IsDefault);
    }
}

public sealed record GitPullRequestOptionViewModel(
    string DisplayName,
    int Number,
    string Title,
    string BaseRefName,
    string HeadRefName,
    string HeadRepository,
    bool IsFromSameRepository)
{
    public string BaseReferenceName => $"origin/{BaseRefName}";

    public GitPullRequestInfo ToPullRequestInfo() => new(Number, Title, BaseRefName, HeadRefName, HeadRepository, IsFromSameRepository);

    public static GitPullRequestOptionViewModel FromPullRequest(GitPullRequestInfo pullRequest)
    {
        var title = pullRequest.Title.Length <= 72 ? pullRequest.Title : $"{pullRequest.Title[..69]}...";
        return new GitPullRequestOptionViewModel(
            $"#{pullRequest.Number} {title}",
            pullRequest.Number,
            pullRequest.Title,
            pullRequest.BaseRefName,
            pullRequest.HeadRefName,
            pullRequest.HeadRepository,
            pullRequest.IsFromSameRepository);
    }
}