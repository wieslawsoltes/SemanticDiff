using SemanticDiff.Core;
using Microsoft.UI.Xaml;

namespace SemanticDiff.App.ViewModels;

public sealed record GitBranchOptionViewModel(
    string DisplayName,
    string ReferenceName,
    bool IsRemote,
    string RemoteName,
    bool IsCurrent,
    bool IsDefault)
{
    public string DetailText
    {
        get
        {
            var descriptors = new List<string>(capacity: 2);
            if (IsCurrent)
            {
                descriptors.Add("current");
            }

            if (IsDefault)
            {
                descriptors.Add("default");
            }

            if (IsRemote)
            {
                descriptors.Add(RemoteName);
            }

            return descriptors.Count == 0
                ? ReferenceName
                : $"{ReferenceName}  {string.Join(", ", descriptors)}";
        }
    }

    public string SearchText => $"{DisplayName} {ReferenceName}";

    public string ShortBranchName => IsRemote && ReferenceName.StartsWith($"{RemoteName}/", StringComparison.Ordinal)
        ? ReferenceName[(RemoteName.Length + 1)..]
        : ReferenceName;

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
        return new GitBranchOptionViewModel(
            $"{branch.ReferenceName}{suffix}",
            branch.ReferenceName,
            branch.IsRemote,
            branch.IsRemote ? GetRemoteName(branch.ReferenceName) : string.Empty,
            branch.IsCurrent,
            branch.IsDefault);
    }

    private static string GetRemoteName(string referenceName)
    {
        var separator = referenceName.IndexOf('/');
        return separator <= 0 ? "origin" : referenceName[..separator];
    }
}

public sealed record GitPullRequestOptionViewModel(
    string DisplayName,
    int Number,
    string Title,
    string BaseRefName,
    string HeadRefName,
    string HeadRepository,
    string RemoteName,
    bool IsFromSameRepository,
    GitReviewRequestKind Kind,
    GitReviewRequestState State)
{
    public string BaseReferenceName => FormatRemoteReference(RemoteName, BaseRefName);

    public string DetailText => State == GitReviewRequestState.Open
        ? $"{HeadRepository}:{HeadRefName} -> {BaseReferenceName}"
        : $"{FormatState(State)}  {HeadRepository}:{HeadRefName} -> {BaseReferenceName}";

    public string TreeDetailText => $"{NumberText}  {DetailText}";

    public string NumberText => Kind == GitReviewRequestKind.MergeRequest ? $"!{Number}" : $"#{Number}";

    public string KindText => Kind == GitReviewRequestKind.MergeRequest ? "MR" : "PR";

    public string SearchText => $"{NumberText} {KindText} {FormatState(State)} {Title} {BaseRefName} {HeadRefName} {HeadRepository} {RemoteName}";

    public GitPullRequestInfo ToPullRequestInfo() => new(Number, Title, BaseRefName, HeadRefName, HeadRepository, IsFromSameRepository, RemoteName, Kind, State);

    public static GitPullRequestOptionViewModel FromPullRequest(GitPullRequestInfo pullRequest)
    {
        var title = pullRequest.Title.Length <= 72 ? pullRequest.Title : $"{pullRequest.Title[..69]}...";
        var remoteSuffix = string.Equals(pullRequest.RemoteName, "origin", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $"  {pullRequest.RemoteName}";
        var numberText = pullRequest.Kind == GitReviewRequestKind.MergeRequest ? $"!{pullRequest.Number}" : $"#{pullRequest.Number}";
        var stateSuffix = pullRequest.State == GitReviewRequestState.Open ? string.Empty : $"  {FormatState(pullRequest.State)}";
        return new GitPullRequestOptionViewModel(
            $"{numberText} {title}{stateSuffix}{remoteSuffix}",
            pullRequest.Number,
            pullRequest.Title,
            pullRequest.BaseRefName,
            pullRequest.HeadRefName,
            pullRequest.HeadRepository,
            pullRequest.RemoteName,
            pullRequest.IsFromSameRepository,
            pullRequest.Kind,
            pullRequest.State);
    }

    private static string FormatState(GitReviewRequestState state) => state switch
    {
        GitReviewRequestState.Closed => "closed",
        GitReviewRequestState.Merged => "merged",
        GitReviewRequestState.All => "all",
        _ => "open"
    };

    private static string FormatRemoteReference(string remoteName, string referenceName)
    {
        var remote = string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName.Trim();
        var reference = string.IsNullOrWhiteSpace(referenceName) ? "main" : referenceName.Trim();
        return reference.StartsWith("refs/", StringComparison.Ordinal) || reference.StartsWith($"{remote}/", StringComparison.Ordinal)
            ? reference
            : $"{remote}/{reference}";
    }
}

public enum GitReferenceTreeItemKind
{
    Group,
    Remote,
    Branch,
    PullRequest
}

public sealed partial record GitReferenceTreeItemViewModel(
    string Id,
    GitReferenceTreeItemKind Kind,
    string DisplayName,
    string DetailText,
    int Depth,
    int Count,
    bool IsExpanded,
    bool HasChildren,
    bool IsSelected,
    GitBranchOptionViewModel? Branch,
    GitPullRequestOptionViewModel? PullRequest)
{
    public bool IsSelectableReference => Branch is not null || PullRequest is not null;

    public string DisclosureGlyph { get; } = HasChildren ? IsExpanded ? "\uE70D" : "\uE76C" : string.Empty;

    public Visibility DisclosureVisibility { get; } = HasChildren ? Visibility.Visible : Visibility.Collapsed;

    public Thickness IndentMargin { get; } = new(Math.Min(54, Depth * 14), 0, 0, 0);

    public string IconGlyph { get; } = Kind switch
    {
        GitReferenceTreeItemKind.Remote => "\uE8B7",
        GitReferenceTreeItemKind.PullRequest => "\uE8F1",
        GitReferenceTreeItemKind.Branch => "\uE8EE",
        _ => "\uE8B7"
    };

    public string BadgeText { get; } = CreateBadgeText(Count, Branch, PullRequest);

    public Visibility BadgeVisibility { get; } = string.IsNullOrWhiteSpace(CreateBadgeText(Count, Branch, PullRequest)) ? Visibility.Collapsed : Visibility.Visible;

    public string SearchText => $"{DisplayName} {DetailText} {Branch?.SearchText} {PullRequest?.SearchText}";

    public static GitReferenceTreeItemViewModel Group(string id, string displayName, string detailText, int depth, int count, bool isExpanded) =>
        new(id, GitReferenceTreeItemKind.Group, displayName, detailText, depth, count, isExpanded, count > 0, false, null, null);

    public static GitReferenceTreeItemViewModel Remote(string remoteName, int depth, int count, bool isExpanded) =>
        new($"remote:{remoteName}", GitReferenceTreeItemKind.Remote, remoteName, "Remote branches", depth, count, isExpanded, count > 0, false, null, null);

    public static GitReferenceTreeItemViewModel BranchItem(GitBranchOptionViewModel branch, int depth, bool isSelected) =>
        new(
            $"branch:{branch.ReferenceName}",
            GitReferenceTreeItemKind.Branch,
            branch.IsRemote ? branch.ShortBranchName : branch.ReferenceName,
            branch.DetailText,
            depth,
            0,
            false,
            false,
            isSelected,
            branch,
            null);

    public static GitReferenceTreeItemViewModel PullRequestItem(GitPullRequestOptionViewModel pullRequest, int depth, bool isSelected) =>
        new(
            $"{pullRequest.KindText.ToLowerInvariant()}:{pullRequest.Number}",
            GitReferenceTreeItemKind.PullRequest,
            pullRequest.Title,
            pullRequest.TreeDetailText,
            depth,
            0,
            false,
            false,
            isSelected,
            null,
            pullRequest);

    private static string CreateBadgeText(
        int count,
        GitBranchOptionViewModel? branch,
        GitPullRequestOptionViewModel? pullRequest)
    {
        if (branch is { IsCurrent: true })
        {
            return "current";
        }

        if (branch is { IsDefault: true })
        {
            return "default";
        }

        if (pullRequest is not null)
        {
            return pullRequest.NumberText;
        }

        return count > 0 ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
    }
}
