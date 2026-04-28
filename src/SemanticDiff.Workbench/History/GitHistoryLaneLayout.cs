using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Workbench.History;

public sealed record GitHistoryLanePath(
    ImmutableArray<Point2> Points,
    bool IsCurve,
    int ColorIndex,
    double StrokeThickness,
    double Opacity);

public sealed record GitHistoryLaneRow(
    double Width,
    double Height,
    ImmutableArray<GitHistoryLanePath> Paths,
    double DotLeft,
    double DotTop,
    double DotSize,
    int DotColorIndex,
    double DotStrokeThickness);

public sealed class GitHistoryLaneLayout
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

    public GitHistoryLaneRow CreateRow(GitCommitInfo commit)
    {
        var paths = ImmutableArray.CreateBuilder<GitHistoryLanePath>();
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

            paths.Add(CreateLine(LaneX(lane), GraphTop, LaneX(lane), CommitCenterY, GetColor(laneSnapshot[lane])));
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
            var color = GetColor(laneCommitId);
            var opacity = parentIds.Contains(laneCommitId) ? 0.58 : 1.0;
            paths.Add(Math.Abs(sourceX - targetX) < 0.1
                ? CreateLine(sourceX, CommitCenterY, targetX, RowHeight, color, opacity)
                : CreateCurve(sourceX, CommitCenterY, targetX, RowHeight, color, opacity));
        }

        foreach (var parent in parentLanes)
        {
            var sourceX = LaneX(currentLane);
            var targetX = LaneX(parent.Lane);
            paths.Add(Math.Abs(sourceX - targetX) < 0.1
                ? CreateLine(sourceX, CommitCenterY, targetX, RowHeight, parent.Color)
                : CreateCurve(sourceX, CommitCenterY, targetX, RowHeight, parent.Color));
        }

        lanes.Clear();
        lanes.AddRange(nextLanes);

        return new GitHistoryLaneRow(
            GraphWidth,
            RowHeight,
            paths.ToImmutable(),
            LaneX(currentLane) - CommitDotSize / 2,
            CommitCenterY - CommitDotSize / 2,
            CommitDotSize,
            currentColor,
            StrokeThickness);
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

    private static GitHistoryLanePath CreateLine(double x1, double y1, double x2, double y2, int color, double opacity = 1) =>
        new([new Point2(x1, y1), new Point2(x2, y2)], false, color, StrokeThickness, opacity);

    private static GitHistoryLanePath CreateCurve(double x1, double y1, double x2, double y2, int color, double opacity = 1)
    {
        var verticalDistance = Math.Max(1, y2 - y1);
        return new GitHistoryLanePath(
            [
                new Point2(x1, y1),
                new Point2(x1, y1 + verticalDistance * 0.58),
                new Point2(x2, y2 - verticalDistance * 0.58),
                new Point2(x2, y2)
            ],
            true,
            color,
            StrokeThickness,
            opacity);
    }
}
