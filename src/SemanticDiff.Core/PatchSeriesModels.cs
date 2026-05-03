using System.Collections.Immutable;

namespace SemanticDiff.Core;

public sealed record GitPatchSeriesComparisonRequest(
    string RepositoryPath,
    string OldRange,
    string NewRange)
{
    public static GitPatchSeriesComparisonRequest FromRefPairs(
        string repositoryPath,
        string oldBaseRef,
        string oldHeadRef,
        string newBaseRef,
        string newHeadRef) => new(
        repositoryPath,
        $"{oldBaseRef}..{oldHeadRef}",
        $"{newBaseRef}..{newHeadRef}");
}

public enum GitPatchSeriesRepositorySourceKind
{
    CurrentRepository,
    LocalRepository,
    RemoteUrl
}

public sealed record GitPatchSeriesDiscoveryRequest(
    GitPatchSeriesRepositorySourceKind SourceKind,
    string SourceText,
    string? FallbackRepositoryPath = null);

public enum GitPatchSeriesRefKind
{
    Branch,
    RemoteBranch,
    Tag,
    PullRequest,
    MergeRequest,
    Other
}

public sealed record GitPatchSeriesRefInfo(
    string DisplayName,
    string ReferenceName,
    string RangeName,
    GitPatchSeriesRefKind Kind,
    string Sha,
    DateTimeOffset? CommitTime,
    string Subject,
    bool IsDefault,
    bool IsCurrent)
{
    public string SearchText => $"{DisplayName} {ReferenceName} {RangeName} {Kind} {Sha} {Subject}";
}

public sealed record GitPatchSeriesDiscoverySnapshot(
    GitPatchSeriesDiscoveryRequest Request,
    string RepositoryPath,
    string SourceText,
    ImmutableArray<GitPatchSeriesRefInfo> Refs,
    string StatusMessage,
    DateTimeOffset CreatedAt)
{
    public int RefCount => Refs.IsDefault ? 0 : Refs.Length;
}

public sealed record GitPatchSeriesComparisonSnapshot(
    GitPatchSeriesComparisonRequest Request,
    GitPatchSeriesInfo OldSeries,
    GitPatchSeriesInfo NewSeries,
    ImmutableArray<GitPatchSeriesComparisonItem> Items,
    string RawRangeDiff,
    string StatusMessage,
    DateTimeOffset CreatedAt)
{
    public int UnchangedCount => CountByKind(GitPatchSeriesComparisonKind.Unchanged);

    public int ModifiedCount => CountByKind(GitPatchSeriesComparisonKind.Modified);

    public int RemovedCount => CountByKind(GitPatchSeriesComparisonKind.Removed);

    public int AddedCount => CountByKind(GitPatchSeriesComparisonKind.Added);

    private int CountByKind(GitPatchSeriesComparisonKind kind) =>
        Items.IsDefault ? 0 : Items.Count(item => item.Kind == kind);
}

public sealed record GitPatchSeriesInfo(
    string RangeText,
    ImmutableArray<GitPatchCommitInfo> Commits,
    ImmutableArray<GitFileChange> Files)
{
    public int CommitCount => Commits.IsDefault ? 0 : Commits.Length;

    public int FileCount => Files.IsDefault ? 0 : Files.Length;
}

public sealed record GitPatchCommitInfo(
    string Id,
    string ShortId,
    string Author,
    DateTimeOffset? AuthorTime,
    string Subject);

public enum GitPatchSeriesComparisonKind
{
    Unchanged,
    Modified,
    Removed,
    Added,
    Unknown
}

public sealed record GitPatchSeriesComparisonItem(
    GitPatchSeriesComparisonKind Kind,
    int? OldIndex,
    string? OldCommit,
    int? NewIndex,
    string? NewCommit,
    string Subject,
    string DetailText,
    ImmutableArray<string> DetailLines);
