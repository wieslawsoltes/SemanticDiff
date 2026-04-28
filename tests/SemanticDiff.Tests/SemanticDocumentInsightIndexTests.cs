using SemanticDiff.Core;
using SemanticDiff.Semantics;

namespace SemanticDiff.Tests;

public sealed class SemanticDocumentInsightIndexTests
{
    [Fact]
    public void Build_GroupsAnchorsByDocumentLineAndMarksChangedImpact()
    {
        var documentId = new DiffDocumentId("src/Feature.cs");
        var document = new DiffDocumentSnapshot(
            documentId,
            new DiffDocumentMetadata(documentId, "src/Feature.cs", null, DiffFileStatus.Modified, "C#", 1, 0),
            [
                new DiffLine(0, 1, 1, DiffLineKind.Context, "public sealed class Feature", []),
                new DiffLine(1, 2, 2, DiffLineKind.Modified, "public void Run() { }", [])
            ]);
        var type = new SemanticAnchor("type", documentId, new TextRange(0, 7, 1, 21), SemanticAnchorKind.Type, "Feature");
        var member = new SemanticAnchor("member", documentId, new TextRange(26, 3, 2, 13), SemanticAnchorKind.Member, "Run");
        var graph = new SemanticGraph(
            [type, member],
            [new SemanticEdge("contains", type.Id, member.Id, SemanticEdgeKind.Contains, 1, "member")]);
        var index = new SemanticDocumentInsightIndex();

        var insight = Assert.Single(index.Build(graph, [document]));

        Assert.Equal(2, insight.AnchorCount);
        Assert.Equal(1, insight.ChangedAnchorCount);
        Assert.Equal(2, insight.LinkedAnchorCount);
        Assert.Equal(1, insight.ImpactedEdgeCount);
        Assert.Equal(2, insight.Lines.Length);
        Assert.Contains(insight.Lines, line => line.LineNumber == 2 && line.IsChanged && line.LinkCount == 1 && line.Detail.Contains("Run", StringComparison.Ordinal));
        Assert.Contains(insight.Lines, line => line.LineNumber == 1 && line.IsImpacted && line.Detail.Contains("Feature", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ReportsParserDiagnosticsAsUnknownLineInsights()
    {
        var documentId = new DiffDocumentId("src/View.xaml");
        var document = new DiffDocumentSnapshot(
            documentId,
            new DiffDocumentMetadata(documentId, "src/View.xaml", null, DiffFileStatus.Modified, "XAML", 0, 0),
            [new DiffLine(0, 1, 1, DiffLineKind.Context, "<Page>", [])]);
        var graph = new SemanticGraph(
            [new SemanticAnchor("parse", documentId, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Unknown, "XML parse error: bad tag")],
            []);

        var insight = Assert.Single(new SemanticDocumentInsightIndex().Build(graph, [document]));

        var line = Assert.Single(insight.Lines);
        Assert.Equal(SemanticAnchorKind.Unknown, line.Kind);
        Assert.Equal("parse", line.Label);
        Assert.Contains("XML parse error", line.Detail, StringComparison.Ordinal);
    }
}
