using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Workbench.Workspace;

public sealed record GitReferenceBrowserView<TBranch, TReviewRequest>(
    ImmutableArray<TBranch> Branches,
    ImmutableArray<TReviewRequest> ReviewRequests,
    string CountText,
    string ReviewRequestGroupTitle,
    string ReviewRequestGroupDetail,
    string ReviewRequestCountLabel,
    bool ForceExpanded);

public sealed class GitReferenceBrowserModel<TBranch, TReviewRequest>
{
    private readonly Func<TBranch, string> getBranchSearchText;
    private readonly Func<TReviewRequest, string> getReviewRequestSearchText;
    private ImmutableArray<TBranch> allBranches = [];
    private ImmutableArray<TReviewRequest> allReviewRequests = [];
    private ImmutableHashSet<string> collapsedNodeIds = ImmutableHashSet<string>.Empty;

    public GitReferenceBrowserModel(
        Func<TBranch, string> getBranchSearchText,
        Func<TReviewRequest, string> getReviewRequestSearchText)
    {
        this.getBranchSearchText = getBranchSearchText;
        this.getReviewRequestSearchText = getReviewRequestSearchText;
    }

    public ImmutableArray<TBranch> AllBranches => allBranches;

    public ImmutableArray<TReviewRequest> AllReviewRequests => allReviewRequests;

    public GitReviewRequestKind ReviewRequestKind { get; private set; } = GitReviewRequestKind.PullRequest;

    public bool SupportsReviewRequests { get; private set; }

    public bool HasBranches => !allBranches.IsDefaultOrEmpty;

    public bool HasReviewRequests => !allReviewRequests.IsDefaultOrEmpty;

    public void SetReferences(
        ImmutableArray<TBranch> branches,
        ImmutableArray<TReviewRequest> reviewRequests,
        GitReviewRequestKind reviewRequestKind,
        bool supportsReviewRequests)
    {
        allBranches = branches.IsDefault ? ImmutableArray<TBranch>.Empty : branches;
        allReviewRequests = reviewRequests.IsDefault ? ImmutableArray<TReviewRequest>.Empty : reviewRequests;
        ReviewRequestKind = reviewRequestKind;
        SupportsReviewRequests = supportsReviewRequests;
    }

    public void SetReviewRequestKind(GitReviewRequestKind reviewRequestKind)
    {
        ReviewRequestKind = reviewRequestKind;
    }

    public void Clear()
    {
        allBranches = [];
        allReviewRequests = [];
        collapsedNodeIds = ImmutableHashSet<string>.Empty;
        ReviewRequestKind = GitReviewRequestKind.PullRequest;
        SupportsReviewRequests = false;
    }

    public bool ToggleNode(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        collapsedNodeIds = collapsedNodeIds.Contains(id)
            ? collapsedNodeIds.Remove(id)
            : collapsedNodeIds.Add(id);
        return true;
    }

    public bool IsExpanded(string id, bool forceExpanded) =>
        forceExpanded || !collapsedNodeIds.Contains(id);

    public GitReferenceBrowserView<TBranch, TReviewRequest> Apply(string query, string reviewRequestStateText)
    {
        var trimmedQuery = query.Trim();
        var branches = Filter(allBranches, trimmedQuery, getBranchSearchText);
        var reviewRequests = Filter(allReviewRequests, trimmedQuery, getReviewRequestSearchText);
        var reviewRequestLabel = GetReviewRequestCountLabel(allReviewRequests.Length);
        var countText = string.IsNullOrWhiteSpace(trimmedQuery)
            ? $"{allBranches.Length:N0} branches | {allReviewRequests.Length:N0} {reviewRequestLabel}"
            : $"{branches.Length:N0}/{allBranches.Length:N0} branches | {reviewRequests.Length:N0}/{allReviewRequests.Length:N0} {reviewRequestLabel}";

        return new GitReferenceBrowserView<TBranch, TReviewRequest>(
            branches,
            reviewRequests,
            countText,
            GetReviewRequestGroupTitle(),
            GetReviewRequestGroupDetail(reviewRequestStateText),
            reviewRequestLabel,
            !string.IsNullOrWhiteSpace(trimmedQuery));
    }

    private static ImmutableArray<TItem> Filter<TItem>(
        ImmutableArray<TItem> items,
        string query,
        Func<TItem, string> getSearchText)
    {
        return string.IsNullOrWhiteSpace(query)
            ? items
            : items.Where(item => getSearchText(item).Contains(query, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
    }

    private string GetReviewRequestGroupTitle() =>
        ReviewRequestKind == GitReviewRequestKind.MergeRequest ? "Merge Requests" : "Pull Requests";

    private string GetReviewRequestGroupDetail(string reviewRequestStateText) =>
        ReviewRequestKind == GitReviewRequestKind.MergeRequest
            ? $"{reviewRequestStateText} GitLab merge requests"
            : $"{reviewRequestStateText} GitHub pull requests";

    private string GetReviewRequestCountLabel(int count)
    {
        var singular = ReviewRequestKind == GitReviewRequestKind.MergeRequest ? "MR" : "PR";
        return count == 1 ? singular : $"{singular}s";
    }
}
