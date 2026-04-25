using SemanticDiff.Core;
using SemanticDiff.Semantics;

namespace SemanticDiff.Tests;

public sealed class SemanticImpactAnalyzerTests
{
    [Fact]
    public void Analyze_CountsChangedSymbolsImpactedEdgesMovedAndIgnoredLines()
    {
        var documentId = new DiffDocumentId("Sample.cs");
        var metadata = new DiffDocumentMetadata(documentId, "Sample.cs", null, DiffFileStatus.Modified, "C#", 1, 1);
        var document = new DiffDocumentSnapshot(
            documentId,
            metadata,
            [
                new DiffLine(0, 1, null, DiffLineKind.Deleted, "old", []),
                new DiffLine(1, null, 1, DiffLineKind.Moved, "Move();", []),
                new DiffLine(2, null, 2, DiffLineKind.Ignored, "Call(", [])
            ]);
        var type = new SemanticAnchor("type", documentId, new TextRange(0, 3, 1, 1), SemanticAnchorKind.Type, "Sample");
        var member = new SemanticAnchor("member", documentId, new TextRange(8, 4, 2, 1), SemanticAnchorKind.Member, "Move");
        var edge = new SemanticEdge("edge", type.Id, member.Id, SemanticEdgeKind.Contains, 1, "member");
        var graph = new SemanticGraph([type, member], [edge]);
        var analyzer = new SemanticImpactAnalyzer();

        var summary = analyzer.Analyze([document], graph);

        Assert.Equal(2, summary.ChangedSymbolCount);
        Assert.Equal(1, summary.ImpactedEdgeCount);
        Assert.Equal(1, summary.MovedLineCount);
        Assert.Equal(1, summary.IgnoredLineCount);
    }
}