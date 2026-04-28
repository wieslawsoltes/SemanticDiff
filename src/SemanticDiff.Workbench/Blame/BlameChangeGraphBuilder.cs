using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Rendering;

namespace SemanticDiff.Workbench.Blame;

public sealed record BlameChangeGraphCommit(
    string CommitId,
    string ShortId,
    string Subject,
    string Author,
    string TimeText,
    int BlamedLineCount);

public sealed record BlameChangeGraphBuildRequest(
    string Path,
    string Language,
    ImmutableArray<BlameChangeGraphCommit> Commits,
    ImmutableDictionary<string, ImmutableArray<GitBlameLine>> LinesByCommit);

public sealed record BlameChangeGraphBuildResult(DiffCanvasScene Scene, string SummaryText);

public sealed class BlameChangeGraphBuilder
{
    public BlameChangeGraphBuildResult Build(BlameChangeGraphBuildRequest request)
    {
        var commits = request.Commits.IsDefault ? ImmutableArray<BlameChangeGraphCommit>.Empty : request.Commits;
        if (commits.IsDefaultOrEmpty)
        {
            return new BlameChangeGraphBuildResult(DiffCanvasScene.FromDocuments([]), "0 history nodes | 0 history links | blamed file changes rendered as diff nodes");
        }

        var documents = commits
            .Select((item, index) =>
            {
                request.LinesByCommit.TryGetValue(item.CommitId, out var lines);
                return CreateBlameCommitDocument(request.Path, request.Language, item, lines, index);
            })
            .ToImmutableArray();
        var anchors = documents
            .Select(document => new SemanticAnchor(
                $"anchor:{document.Id.Value}",
                document.Id,
                new TextRange(0, 0, 1, 1),
                SemanticAnchorKind.File,
                document.Metadata.Path))
            .ToImmutableArray();
        var edges = anchors
            .Zip(anchors.Skip(1), (source, target) => new SemanticEdge(
                $"history:{source.DocumentId.Value}->{target.DocumentId.Value}",
                source.Id,
                target.Id,
                SemanticEdgeKind.Contains,
                1,
                "previous"))
            .ToImmutableArray();
        var layout = new GraphLayoutResult(documents
            .Select((document, index) =>
            {
                const double nodeWidth = 560;
                const double nodeHeight = 360;
                var column = index % 3;
                var row = index / 3;
                return new DiffNodeLayout(
                    document.Id,
                    new Rect2(column * 660, row * 430, nodeWidth, nodeHeight),
                    IsPinned: true,
                    FontSize: 12.0);
            })
            .ToImmutableArray());
        var scene = DiffCanvasScene.FromDocuments(
            documents,
            new SemanticGraph(anchors, edges),
            layout,
            groupingMode: GraphGroupingMode.None);
        return new BlameChangeGraphBuildResult(
            scene,
            $"{commits.Length:N0} history nodes | {Math.Max(0, commits.Length - 1):N0} history links | blamed file changes rendered as diff nodes");
    }

    private static DiffDocumentSnapshot CreateBlameCommitDocument(
        string path,
        string language,
        BlameChangeGraphCommit item,
        ImmutableArray<GitBlameLine> lines,
        int index)
    {
        var documentId = new DiffDocumentId($"blame:{SanitizeId(path)}:{item.ShortId}:{index}");
        var metadata = new DiffDocumentMetadata(
            documentId,
            $"{Path.GetFileName(path)} @ {item.ShortId}",
            path,
            DiffFileStatus.Modified,
            language,
            item.BlamedLineCount,
            0);
        var sourceLines = lines.IsDefault ? ImmutableArray<GitBlameLine>.Empty : lines;
        var sortedLines = sourceLines
            .OrderBy(line => line.LineNumber)
            .ToImmutableArray();
        var builder = ImmutableArray.CreateBuilder<DiffLine>(sortedLines.Length + 4);
        AddMetadataLine(builder, $"commit {item.CommitId}");
        AddMetadataLine(builder, item.Subject);
        AddMetadataLine(builder, $"{item.Author} | {item.TimeText} | {FormatLineRanges(sortedLines.Select(line => line.LineNumber).ToArray())}");
        AddMetadataLine(builder, string.Empty);

        if (sortedLines.IsDefaultOrEmpty)
        {
            builder.Add(new DiffLine(
                builder.Count,
                null,
                null,
                DiffLineKind.Ignored,
                "No current blamed lines retained at the active revision.",
                ImmutableArray<TokenSpan>.Empty));
        }
        else
        {
            foreach (var line in sortedLines)
            {
                builder.Add(new DiffLine(
                    builder.Count,
                    null,
                    line.LineNumber,
                    DiffLineKind.Added,
                    string.IsNullOrEmpty(line.Text) ? " " : line.Text,
                    ImmutableArray<TokenSpan>.Empty));
            }
        }

        return new DiffDocumentSnapshot(documentId, metadata, builder.ToImmutable());
    }

    private static void AddMetadataLine(ImmutableArray<DiffLine>.Builder builder, string text)
    {
        builder.Add(new DiffLine(
            builder.Count,
            null,
            null,
            DiffLineKind.Metadata,
            text,
            ImmutableArray<TokenSpan>.Empty));
    }

    private static string SanitizeId(string value)
    {
        var normalized = value.Replace('\\', '/');
        var chars = normalized.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private static string FormatLineRanges(IReadOnlyList<int> lineNumbers)
    {
        if (lineNumbers.Count == 0)
        {
            return "no retained blamed lines";
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
}
