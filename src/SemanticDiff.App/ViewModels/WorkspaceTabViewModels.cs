using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SemanticDiff.Core;
using SemanticDiff.Rendering;
using Windows.Foundation;
using Windows.UI;

namespace SemanticDiff.App.ViewModels;

public enum WorkspaceTabKind
{
    Graph,
    GitHistory,
    FileDiff,
    Blame
}

public enum FileDiffDisplayMode
{
    DiffOnly,
    FullFile
}

public sealed partial class WorkspaceTabViewModel : ObservableObject
{
    private WorkspaceTabViewModel(
        string id,
        WorkspaceTabKind kind,
        string header,
        string detailText,
        string iconGlyph,
        bool isClosable)
    {
        Id = id;
        Kind = kind;
        Header = header;
        DetailText = detailText;
        IconGlyph = iconGlyph;
        IsClosable = isClosable;
    }

    public string Id { get; }

    public WorkspaceTabKind Kind { get; }

    public string Header { get; }

    public string DetailText { get; }

    public string IconGlyph { get; }

    public bool IsClosable { get; }

    public Visibility IconVisibility => Kind is WorkspaceTabKind.Graph or WorkspaceTabKind.GitHistory or WorkspaceTabKind.Blame
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CloseButtonVisibility => IsClosable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GraphVisibility => Kind == WorkspaceTabKind.Graph ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HistoryVisibility => Kind == WorkspaceTabKind.GitHistory ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FileDiffVisibility => Kind == WorkspaceTabKind.FileDiff ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BlameVisibility => Kind == WorkspaceTabKind.Blame ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingVisibility))]
    private bool isLoading;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private GitHistoryTimelineViewModel? history;

    [ObservableProperty]
    private FileDiffTabViewModel? fileDiff;

    [ObservableProperty]
    private BlameTabViewModel? blame;

    [ObservableProperty]
    private GitDiffRequest? graphRequest;

    [ObservableProperty]
    private string? graphBranchReferenceName;

    [ObservableProperty]
    private GitPullRequestInfo? graphReviewRequest;

    [ObservableProperty]
    private GraphWorkspaceState? graphState;

    public static WorkspaceTabViewModel Graph() => new(
        "graph",
        WorkspaceTabKind.Graph,
        "Diff Graph",
        "Semantic node canvas",
        "\uECA5",
        isClosable: false);

    public static WorkspaceTabViewModel CreateGraphWorkspace(
        string id,
        string header,
        string detailText,
        GitDiffRequest request,
        string? branchReferenceName,
        GitPullRequestInfo? reviewRequest) => new(
            id,
            WorkspaceTabKind.Graph,
            header,
            detailText,
            "\uECA5",
            isClosable: true)
        {
            GraphRequest = request,
            GraphBranchReferenceName = branchReferenceName,
            GraphReviewRequest = reviewRequest,
            StatusText = "Loading workspace"
        };

    public static WorkspaceTabViewModel CreateHistory(string id, string header, string detailText) => new(
        id,
        WorkspaceTabKind.GitHistory,
        header,
        detailText,
        "\uE81C",
        isClosable: true);

    public static WorkspaceTabViewModel CreateFileDiff(string id, string header, string detailText, FileDiffTabViewModel fileDiff) => new(
        id,
        WorkspaceTabKind.FileDiff,
        header,
        detailText,
        "\uE8A5",
        isClosable: true)
    {
        FileDiff = fileDiff,
        StatusText = fileDiff.StatusText
    };

    public static WorkspaceTabViewModel CreateBlame(string id, string header, string detailText, BlameTabViewModel blame) => new(
        id,
        WorkspaceTabKind.Blame,
        header,
        detailText,
        "\uE946",
        isClosable: true)
    {
        Blame = blame,
        StatusText = blame.StatusText
    };
}

public sealed record GraphWorkspaceState(
    string? RepositoryPath,
    GitDiffRequest? Request,
    GitPullRequestInfo? ReviewRequest,
    string RepositoryName,
    string ContextText,
    string StatusText,
    string StatusPrefix,
    bool DocumentsAreRepositoryDocuments,
    ImmutableArray<DiffDocumentSnapshot> Documents,
    SemanticGraph SemanticGraph,
    GitDiffSnapshot? GitSnapshot,
    GraphLayoutResult? PreviousLayout,
    ImmutableHashSet<DiffDocumentId> PinnedDocumentIds,
    DiffCanvasScene Scene,
    string? SelectedDocumentId,
    ImmutableArray<ReviewThreadItemViewModel> ReviewThreadItems,
    ImmutableArray<GitReviewThreadInfo> ReviewThreads,
    GitReviewRequestKind ReviewRequestKind);

public sealed partial class BlameTabViewModel : ObservableObject
{
    public BlameTabViewModel(
        string path,
        string language,
        string summaryText,
        string statusText,
        ImmutableArray<BlameCommitNodeViewModel> commitNodes,
        ImmutableArray<BlameTimelineItemViewModel> timelineItems)
    {
        Path = path;
        Language = language;
        SummaryText = summaryText;
        StatusText = statusText;
        CommitNodes = commitNodes;
        TimelineItems = timelineItems;
    }

    public string Path { get; }

    public string Language { get; }

    public string SummaryText { get; }

    public string StatusText { get; }

    public ImmutableArray<BlameCommitNodeViewModel> CommitNodes { get; }

    public ImmutableArray<BlameTimelineItemViewModel> TimelineItems { get; }

    public Visibility EmptyVisibility => CommitNodes.IsDefaultOrEmpty ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ContentVisibility => CommitNodes.IsDefaultOrEmpty ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CollapsedTimelineVisibility => IsTimelineExpanded ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ExpandedTimelineVisibility => IsTimelineExpanded ? Visibility.Visible : Visibility.Collapsed;

    public string TimelineToggleText => IsTimelineExpanded ? "Shrink timeline" : "Expand timeline";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CollapsedTimelineVisibility))]
    [NotifyPropertyChangedFor(nameof(ExpandedTimelineVisibility))]
    [NotifyPropertyChangedFor(nameof(TimelineToggleText))]
    private bool isTimelineExpanded;

    public void ToggleTimeline() => IsTimelineExpanded = !IsTimelineExpanded;

    public static BlameTabViewModel Loading(string path, string language) => new(
        path,
        language,
        "Loading blame and file history",
        $"{path} | loading blame",
        [],
        []);

    public static BlameTabViewModel FromBlame(string path, string language, GitFileBlame blame, ImmutableArray<GitCommitInfo> history)
    {
        if (blame.Lines.IsDefaultOrEmpty)
        {
            return new BlameTabViewModel(
                path,
                language,
                "No blame data available",
                $"{path} | blame unavailable",
                [],
                BuildTimelineItems(history, ImmutableDictionary<string, int>.Empty));
        }

        var totalLines = blame.Lines.Length;
        var lineCountsByCommit = blame.Lines
            .GroupBy(line => line.CommitId, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var nodes = blame.Lines
            .GroupBy(line => line.CommitId, StringComparer.Ordinal)
            .Select((group, index) => BlameCommitNodeViewModel.FromLines(group.Key, group.ToImmutableArray(), totalLines, index))
            .OrderByDescending(node => node.AuthorTime)
            .ThenBy(node => node.Author, StringComparer.Ordinal)
            .ToImmutableArray();
        var latest = nodes.FirstOrDefault();
        var authors = nodes
            .GroupBy(node => node.Author, StringComparer.Ordinal)
            .OrderByDescending(group => group.Sum(node => node.LineCount))
            .Take(2)
            .Select(group => $"{group.Key} {group.Sum(node => node.LineCount):N0}");
        var summary = $"{totalLines:N0} blamed lines | {nodes.Length:N0} commits | {string.Join(", ", authors)}";
        if (latest is not null)
        {
            summary += $" | latest {latest.Author} {latest.TimeText} {latest.ShortId}";
        }

        return new BlameTabViewModel(
            path,
            language,
            summary,
            $"{path} | {summary}",
            nodes,
            BuildTimelineItems(history, lineCountsByCommit));
    }

    private static ImmutableArray<BlameTimelineItemViewModel> BuildTimelineItems(
        ImmutableArray<GitCommitInfo> history,
        ImmutableDictionary<string, int> lineCountsByCommit)
    {
        if (history.IsDefaultOrEmpty && lineCountsByCommit.Count == 0)
        {
            return [];
        }

        var fromHistory = history
            .Select((commit, index) => BlameTimelineItemViewModel.FromCommit(commit, lineCountsByCommit.GetValueOrDefault(commit.Id), index))
            .ToImmutableArray();
        if (!fromHistory.IsDefaultOrEmpty)
        {
            return fromHistory;
        }

        return lineCountsByCommit
            .OrderByDescending(pair => pair.Value)
            .Select((pair, index) => BlameTimelineItemViewModel.FromBlameOnly(pair.Key, pair.Value, index))
            .ToImmutableArray();
    }
}

public sealed record BlameCommitNodeViewModel(
    string CommitId,
    string ShortId,
    string Author,
    DateTimeOffset? AuthorTime,
    string TimeText,
    string Summary,
    string LineRangeText,
    int LineCount,
    string LineCountText,
    string CoverageText,
    double CoverageWidth,
    SolidColorBrush AccentBrush,
    SolidColorBrush SoftBrush)
{
    public static BlameCommitNodeViewModel FromLines(string commitId, ImmutableArray<GitBlameLine> lines, int totalLines, int index)
    {
        var first = lines[0];
        var lineCount = lines.Length;
        var coverage = totalLines <= 0 ? 0 : (double)lineCount / totalLines;
        var accent = BlameInsightBrushes.GetBrush(index);
        return new BlameCommitNodeViewModel(
            commitId,
            Shorten(commitId),
            first.Author,
            first.AuthorTime,
            FormatTimestamp(first.AuthorTime),
            string.IsNullOrWhiteSpace(first.Summary) ? "No commit summary" : first.Summary,
            FormatLineRanges(lines.Select(line => line.LineNumber).Order().ToArray()),
            lineCount,
            lineCount == 1 ? "1 line" : $"{lineCount:N0} lines",
            $"{coverage:P0} of file",
            Math.Clamp(28 + coverage * 260, 28, 288),
            accent,
            BlameInsightBrushes.GetSoftBrush(accent));
    }

    private static string FormatLineRanges(IReadOnlyList<int> lineNumbers)
    {
        if (lineNumbers.Count == 0)
        {
            return "lines unknown";
        }

        var ranges = new List<string>();
        var start = lineNumbers[0];
        var previous = start;
        for (var index = 1; index < lineNumbers.Count; index++)
        {
            var line = lineNumbers[index];
            if (line == previous + 1)
            {
                previous = line;
                continue;
            }

            ranges.Add(start == previous ? start.ToString() : $"{start}-{previous}");
            start = previous = line;
        }

        ranges.Add(start == previous ? start.ToString() : $"{start}-{previous}");
        return $"lines {string.Join(", ", ranges.Take(6))}{(ranges.Count > 6 ? ", ..." : string.Empty)}";
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp is null
            ? "unknown date"
            : timestamp.Value.LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.CurrentCulture);

    private static string Shorten(string commitId) => commitId.Length <= 10 ? commitId : commitId[..10];
}

public sealed record BlameTimelineItemViewModel(
    string CommitId,
    string ShortId,
    string Subject,
    string Author,
    string TimeText,
    int BlamedLineCount,
    string BlamedLineText,
    double MarkerHeight,
    SolidColorBrush AccentBrush,
    SolidColorBrush SoftBrush)
{
    public Visibility BlamedLineVisibility => BlamedLineCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static BlameTimelineItemViewModel FromCommit(GitCommitInfo commit, int blamedLineCount, int index)
    {
        var accent = BlameInsightBrushes.GetBrush(index);
        return new BlameTimelineItemViewModel(
            commit.Id,
            commit.ShortId,
            string.IsNullOrWhiteSpace(commit.Subject) ? "No commit subject" : commit.Subject,
            string.IsNullOrWhiteSpace(commit.Author) ? "unknown" : commit.Author,
            FormatTimestamp(commit.AuthorTime),
            blamedLineCount,
            blamedLineCount == 1 ? "1 blamed line" : $"{blamedLineCount:N0} blamed lines",
            blamedLineCount == 0 ? 24 : Math.Clamp(32 + blamedLineCount * 1.8, 32, 96),
            accent,
            BlameInsightBrushes.GetSoftBrush(accent));
    }

    public static BlameTimelineItemViewModel FromBlameOnly(string commitId, int blamedLineCount, int index)
    {
        var accent = BlameInsightBrushes.GetBrush(index);
        return new BlameTimelineItemViewModel(
            commitId,
            commitId.Length <= 10 ? commitId : commitId[..10],
            "Commit not loaded in file history",
            "unknown",
            "unknown date",
            blamedLineCount,
            blamedLineCount == 1 ? "1 blamed line" : $"{blamedLineCount:N0} blamed lines",
            Math.Clamp(32 + blamedLineCount * 1.8, 32, 96),
            accent,
            BlameInsightBrushes.GetSoftBrush(accent));
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp is null
            ? "unknown date"
            : timestamp.Value.LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.CurrentCulture);
}

internal static class BlameInsightBrushes
{
    private static readonly Color[] Colors =
    [
        Color.FromArgb(255, 0, 122, 204),
        Color.FromArgb(255, 26, 127, 55),
        Color.FromArgb(255, 154, 103, 0),
        Color.FromArgb(255, 168, 85, 247),
        Color.FromArgb(255, 20, 184, 166),
        Color.FromArgb(255, 236, 72, 153),
        Color.FromArgb(255, 100, 116, 139)
    ];

    public static SolidColorBrush GetBrush(int index) => new(Colors[Math.Abs(index) % Colors.Length]);

    public static SolidColorBrush GetSoftBrush(SolidColorBrush brush)
    {
        var color = brush.Color;
        return new SolidColorBrush(Color.FromArgb(34, color.R, color.G, color.B));
    }
}

public sealed partial class GitHistoryTimelineViewModel : ObservableObject
{
    private readonly HashSet<string> seenCommitIds = new(StringComparer.Ordinal);
    private readonly GitHistoryGraphLayoutState graphLayout = new();
    private int nextSkip;

    public GitHistoryTimelineViewModel(
        string title,
        string referenceText,
        string rangeText,
        GitHistoryRequest request)
    {
        Title = title;
        ReferenceText = referenceText;
        RangeText = rangeText;
        Request = request with { Skip = 0 };
        nextSkip = Math.Max(0, request.Skip);
    }

    public string Title { get; }

    public string ReferenceText { get; }

    public string RangeText { get; }

    public GitHistoryRequest Request { get; }

    public ObservableCollection<GitHistoryItemViewModel> Commits { get; } = [];

    public int LoadedCount => Commits.Count;

    public string CountText => HasMore
        ? $"{LoadedCount:N0}+ commits"
        : LoadedCount == 1
            ? "1 commit"
            : $"{LoadedCount:N0} commits";

    public string FooterText => IsLoadingMore
        ? "Loading more commits..."
        : HasMore
            ? "Scroll to load more commits"
            : "End of history";

    public Visibility FooterVisibility => IsLoadingMore || HasMore ? Visibility.Visible : Visibility.Collapsed;

    public GitHistoryRequest NextPageRequest => Request with { Skip = nextSkip };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    [NotifyPropertyChangedFor(nameof(FooterVisibility))]
    private bool hasMore = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    [NotifyPropertyChangedFor(nameof(FooterVisibility))]
    private bool isLoadingMore;

    public void AppendSnapshot(GitHistorySnapshot snapshot)
    {
        foreach (var commit in snapshot.Commits)
        {
            if (seenCommitIds.Add(commit.Id))
            {
                Commits.Add(GitHistoryItemViewModel.FromCommit(commit, graphLayout.CreateRow(commit)));
            }
        }

        nextSkip = Math.Max(nextSkip, snapshot.Request.Skip + snapshot.Commits.Length);
        HasMore = snapshot.HasMore;
        OnPropertyChanged(nameof(LoadedCount));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(FooterVisibility));
    }

    public static GitHistoryTimelineViewModel Create(string title, GitHistoryRequest request)
    {
        var range = string.IsNullOrWhiteSpace(request.BaseRef)
            ? request.HeadRef
            : $"{request.BaseRef}..{request.HeadRef}";
        return new GitHistoryTimelineViewModel(title, request.HeadRef, range, request);
    }
}

public sealed record GitHistoryItemViewModel(
    string CommitId,
    string ShortId,
    string Subject,
    string Author,
    string AuthorTimeText,
    string Decorations,
    ImmutableArray<GitHistoryRefBadgeViewModel> RefBadges,
    string ParentText,
    double GraphWidth,
    double RowHeight,
    ImmutableArray<GitHistoryGraphPathViewModel> GraphPaths,
    double DotLeft,
    double DotTop,
    double DotSize,
    SolidColorBrush DotBrush,
    SolidColorBrush DotStroke,
    double RowMinHeight,
    string MergeText)
{
    public Visibility MergeVisibility => string.IsNullOrWhiteSpace(MergeText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DecorationsVisibility => RefBadges.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string MetaText => $"{Author} | {AuthorTimeText} | {ParentText}";

    public static GitHistoryItemViewModel FromCommit(GitCommitInfo commit, GitHistoryGraphRowViewModel graph)
    {
        var parentText = commit.ParentIds.Length == 0
            ? "root commit"
            : commit.ParentIds.Length == 1
                ? $"parent {Shorten(commit.ParentIds[0])}"
                : $"{commit.ParentIds.Length:N0} parents";
        return new GitHistoryItemViewModel(
            commit.Id,
            commit.ShortId,
            commit.Subject,
            string.IsNullOrWhiteSpace(commit.Author) ? "unknown" : commit.Author,
            FormatTimestamp(commit.AuthorTime),
            commit.Decorations,
            GitHistoryRefBadgeViewModel.FromDecorations(commit.Decorations),
            parentText,
            graph.Width,
            graph.Height,
            graph.Paths,
            graph.DotLeft,
            graph.DotTop,
            graph.DotSize,
            graph.DotBrush,
            graph.DotStroke,
            graph.Height,
            commit.IsMerge ? "merge" : string.Empty);
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp is null
            ? "unknown date"
            : timestamp.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    private static string Shorten(string commitId) => commitId.Length <= 12 ? commitId : commitId[..12];
}

public sealed record GitHistoryGraphRowViewModel(
    double Width,
    double Height,
    ImmutableArray<GitHistoryGraphPathViewModel> Paths,
    double DotLeft,
    double DotTop,
    double DotSize,
    SolidColorBrush DotBrush,
    SolidColorBrush DotStroke);

public sealed record GitHistoryGraphPathViewModel(Geometry Data, SolidColorBrush Stroke, double StrokeThickness, double Opacity);

internal sealed class GitHistoryGraphLayoutState
{
    private const double LaneSpacing = 14;
    private const double GraphLeft = 22;
    private const double GraphTop = 0;
    private const double RowHeight = 58;
    private const double CommitCenterY = 24;
    private const double CommitDotSize = 10;
    private const double StrokeThickness = 2.1;
    private const double GraphWidth = 214;

    private readonly List<string> lanes = [];
    private readonly Dictionary<string, int> colorsByCommit = new(StringComparer.Ordinal);
    private int nextColor;
    private int rowIndex;

    public GitHistoryGraphRowViewModel CreateRow(GitCommitInfo commit)
    {
        var paths = ImmutableArray.CreateBuilder<GitHistoryGraphPathViewModel>();
        var currentLane = lanes.FindIndex(lane => string.Equals(lane, commit.Id, StringComparison.Ordinal));
        var currentWasActive = currentLane >= 0;
        if (currentLane < 0)
        {
            currentLane = 0;
            lanes.Insert(0, commit.Id);
            AssignColor(commit.Id, nextColor++);
        }

        var currentColor = GetColor(commit.Id);
        var laneSnapshot = lanes.ToArray();
        for (var lane = 0; lane < laneSnapshot.Length; lane++)
        {
            if (lane == currentLane && !currentWasActive)
            {
                continue;
            }

            var brush = GitHistoryBrushes.GetLaneBrush(GetColor(laneSnapshot[lane]));
            paths.Add(CreateLine(LaneX(lane), GraphTop, LaneX(lane), CommitCenterY, brush));
        }

        var nextLanes = lanes.ToList();
        nextLanes.RemoveAt(currentLane);
        var parentLanes = ResolveParentLanes(commit, currentLane, nextLanes, currentColor);
        var parentIds = commit.ParentIds.ToHashSet(StringComparer.Ordinal);
        for (var lane = 0; lane < laneSnapshot.Length; lane++)
        {
            if (lane == currentLane)
            {
                continue;
            }

            var laneCommitId = laneSnapshot[lane];
            var nextLane = nextLanes.FindIndex(next => string.Equals(next, laneCommitId, StringComparison.Ordinal));
            if (nextLane < 0)
            {
                continue;
            }

            var sourceX = LaneX(lane);
            var targetX = LaneX(nextLane);
            var brush = GitHistoryBrushes.GetLaneBrush(GetColor(laneCommitId));
            var opacity = parentIds.Contains(laneCommitId) ? 0.58 : 1.0;
            paths.Add(Math.Abs(sourceX - targetX) < 0.1
                ? CreateLine(sourceX, CommitCenterY, targetX, RowHeight, brush, opacity)
                : CreateCurve(sourceX, CommitCenterY, targetX, RowHeight, brush, opacity));
        }

        foreach (var parent in parentLanes)
        {
            var sourceX = LaneX(currentLane);
            var targetX = LaneX(parent.Lane);
            var brush = GitHistoryBrushes.GetLaneBrush(parent.Color);
            paths.Add(Math.Abs(sourceX - targetX) < 0.1
                ? CreateLine(sourceX, CommitCenterY, targetX, RowHeight, brush)
                : CreateCurve(sourceX, CommitCenterY, targetX, RowHeight, brush));
        }

        lanes.Clear();
        lanes.AddRange(nextLanes);
        rowIndex++;

        return new GitHistoryGraphRowViewModel(
            GraphWidth,
            RowHeight,
            paths.ToImmutable(),
            LaneX(currentLane) - CommitDotSize / 2,
            CommitCenterY - CommitDotSize / 2,
            CommitDotSize,
            GitHistoryBrushes.GetLaneBrush(currentColor),
            GitHistoryBrushes.DotStrokeBrush);
    }

    private ImmutableArray<(int Lane, int Color)> ResolveParentLanes(GitCommitInfo commit, int currentLane, List<string> nextLanes, int currentColor)
    {
        if (commit.ParentIds.Length == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<(int Lane, int Color)>();
        var insertOffset = 0;
        for (var parentIndex = 0; parentIndex < commit.ParentIds.Length; parentIndex++)
        {
            var parentId = commit.ParentIds[parentIndex];
            var parentLane = nextLanes.FindIndex(lane => string.Equals(lane, parentId, StringComparison.Ordinal));
            if (parentLane < 0)
            {
                parentLane = Math.Clamp(currentLane + insertOffset, 0, nextLanes.Count);
                nextLanes.Insert(parentLane, parentId);
                insertOffset++;
            }

            var color = parentIndex == 0
                ? AssignColor(parentId, currentColor)
                : AssignColor(parentId, nextColor++);
            builder.Add((parentLane, color));
        }

        return builder.ToImmutable();
    }

    private int AssignColor(string commitId, int color)
    {
        if (colorsByCommit.TryGetValue(commitId, out var existing))
        {
            return existing;
        }

        colorsByCommit[commitId] = color;
        return color;
    }

    private int GetColor(string commitId)
    {
        if (colorsByCommit.TryGetValue(commitId, out var color))
        {
            return color;
        }

        colorsByCommit[commitId] = nextColor;
        return nextColor++;
    }

    private static double LaneX(int lane) => GraphLeft + lane * LaneSpacing;

    private static GitHistoryGraphPathViewModel CreateLine(double x1, double y1, double x2, double y2, SolidColorBrush brush, double opacity = 1) =>
        new(CreatePath(new Point(x1, y1), new LineSegment { Point = new Point(x2, y2) }), brush, StrokeThickness, opacity);

    private static GitHistoryGraphPathViewModel CreateCurve(double x1, double y1, double x2, double y2, SolidColorBrush brush, double opacity = 1)
    {
        var verticalDistance = Math.Max(1, y2 - y1);
        var segment = new BezierSegment
        {
            Point1 = new Point(x1, y1 + verticalDistance * 0.58),
            Point2 = new Point(x2, y2 - verticalDistance * 0.58),
            Point3 = new Point(x2, y2)
        };
        return new GitHistoryGraphPathViewModel(CreatePath(new Point(x1, y1), segment), brush, StrokeThickness, opacity);
    }

    private static PathGeometry CreatePath(Point startPoint, PathSegment segment)
    {
        var figure = new PathFigure
        {
            StartPoint = startPoint,
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(segment);

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}

public sealed record GitHistoryRefBadgeViewModel(string Text, SolidColorBrush Foreground, SolidColorBrush Background, SolidColorBrush Border)
{
    public static ImmutableArray<GitHistoryRefBadgeViewModel> FromDecorations(string decorations)
    {
        if (string.IsNullOrWhiteSpace(decorations))
        {
            return [];
        }

        return decorations
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(12)
            .Select(Create)
            .ToImmutableArray();
    }

    private static GitHistoryRefBadgeViewModel Create(string text)
    {
        var foreground = text.StartsWith("tag: ", StringComparison.OrdinalIgnoreCase)
            ? GitHistoryBrushes.TagBrush
            : text.Contains("HEAD", StringComparison.OrdinalIgnoreCase)
                ? GitHistoryBrushes.HeadBrush
                : GitHistoryBrushes.BranchBrush;
        return new GitHistoryRefBadgeViewModel(text, foreground, GitHistoryBrushes.GetSoftBrush(foreground), foreground);
    }
}

internal static class GitHistoryBrushes
{
    private static readonly Color[] LaneColors =
    [
        Color.FromArgb(255, 0, 122, 204),
        Color.FromArgb(255, 249, 115, 22),
        Color.FromArgb(255, 34, 197, 94),
        Color.FromArgb(255, 168, 85, 247),
        Color.FromArgb(255, 236, 72, 153),
        Color.FromArgb(255, 20, 184, 166),
        Color.FromArgb(255, 234, 179, 8),
        Color.FromArgb(255, 100, 116, 139)
    ];

    public static SolidColorBrush Transparent { get; } = new(Color.FromArgb(0, 0, 0, 0));

    public static SolidColorBrush DotStrokeBrush { get; } = new(Color.FromArgb(255, 255, 255, 255));

    public static SolidColorBrush HeadBrush { get; } = new(Color.FromArgb(255, 0, 122, 204));

    public static SolidColorBrush BranchBrush { get; } = new(Color.FromArgb(255, 26, 127, 55));

    public static SolidColorBrush TagBrush { get; } = new(Color.FromArgb(255, 154, 103, 0));

    public static SolidColorBrush GetLaneBrush(int lane) => new(LaneColors[Math.Abs(lane) % LaneColors.Length]);

    public static SolidColorBrush GetSoftBrush(SolidColorBrush brush)
    {
        var color = brush.Color;
        return new SolidColorBrush(Color.FromArgb(32, color.R, color.G, color.B));
    }
}

public sealed partial class FileDiffTabViewModel : ObservableObject
{
    public FileDiffTabViewModel(
        string documentId,
        string path,
        string language,
        DiffFileStatus status,
        ImmutableArray<DiffLine> diffOnlyLines,
        ImmutableArray<FileDiffLineViewModel> diffLines,
        string fullText,
        ImmutableArray<DiffLine> fullFileLines,
        ImmutableArray<CodeFoldRegion> foldRegions,
        FileDiffDisplayMode displayMode)
    {
        DocumentId = documentId;
        Path = path;
        Language = language;
        Status = status;
        DiffOnlyLines = diffOnlyLines;
        DiffLines = diffLines;
        FullFileLines = fullFileLines;
        FoldRegions = foldRegions;
        this.fullText = fullText;
        this.displayMode = displayMode;
    }

    public string DocumentId { get; }

    public string Path { get; }

    public string Language { get; }

    public DiffFileStatus Status { get; }

    public ImmutableArray<DiffLine> DiffOnlyLines { get; }

    public ImmutableArray<FileDiffLineViewModel> DiffLines { get; }

    public ImmutableArray<DiffLine> FullFileLines { get; }

    public ImmutableArray<CodeFoldRegion> FoldRegions { get; }

    public string SummaryText => $"{Status} | {Language} | {DiffLines.Length:N0} diff lines";

    public string StatusText => $"{Path} | {SummaryText}";

    public Visibility DiffOnlyVisibility => DisplayMode == FileDiffDisplayMode.DiffOnly ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FullFileVisibility => DisplayMode == FileDiffDisplayMode.FullFile ? Visibility.Visible : Visibility.Collapsed;

    public bool IsDiffOnlyMode => DisplayMode == FileDiffDisplayMode.DiffOnly;

    public bool IsFullFileMode => DisplayMode == FileDiffDisplayMode.FullFile;

    public string FullFileHeader => string.IsNullOrWhiteSpace(FullText)
        ? "Full file content unavailable"
        : $"{Path} | {FullFileLines.Length:N0} lines | {FoldRegions.Length:N0} fold regions";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffOnlyVisibility))]
    [NotifyPropertyChangedFor(nameof(FullFileVisibility))]
    [NotifyPropertyChangedFor(nameof(IsDiffOnlyMode))]
    [NotifyPropertyChangedFor(nameof(IsFullFileMode))]
    private FileDiffDisplayMode displayMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullFileHeader))]
    private string fullText;

    public void SetDisplayMode(FileDiffDisplayMode mode) => DisplayMode = mode;

    public static FileDiffTabViewModel FromDocument(
        DiffDocumentSnapshot document,
        DiffDocumentSnapshot fullFileDocument,
        string fullText,
        ImmutableArray<CodeFoldRegion> foldRegions,
        FileDiffDisplayMode displayMode) => new(
        document.Id.Value,
        document.Metadata.Path,
        document.Metadata.Language,
        document.Metadata.Status,
        document.Lines,
        document.Lines.Select(FileDiffLineViewModel.FromLine).ToImmutableArray(),
        fullText,
        fullFileDocument.Lines,
        foldRegions,
        displayMode);
}

public sealed record FileDiffLineViewModel(
    string OldLineNumberText,
    string NewLineNumberText,
    string Marker,
    string Text,
    string KindText)
{
    public static FileDiffLineViewModel FromLine(DiffLine line) => new(
        line.OldLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        line.NewLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        MarkerFor(line.Kind),
        line.Text,
        line.Kind.ToString());

    private static string MarkerFor(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Deleted => "-",
        DiffLineKind.Ignored => "~",
        DiffLineKind.Moved => ">",
        DiffLineKind.Conflict => "!",
        DiffLineKind.Metadata => "@",
        DiffLineKind.Imaginary => "...",
        _ => string.Empty
    };
}
