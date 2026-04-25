using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Semantics;

namespace SemanticDiff.Tests;

public sealed class SemanticNavigationIndexTests
{
    [Fact]
    public void Build_ReturnsNavigableAnchorsWithIncidentEdgeCounts()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("src/Sample.cs"), "src/Sample.cs", null, DiffFileStatus.Modified, "C#", 1, 0),
            "public sealed class Sample { public void Run() { } }");
        var type = new SemanticAnchor("type", document.Id, new TextRange(0, 6, 1, 21), SemanticAnchorKind.Type, "Sample");
        var member = new SemanticAnchor("member", document.Id, new TextRange(29, 3, 1, 35), SemanticAnchorKind.Member, "Run");
        var file = new SemanticAnchor("file", document.Id, new TextRange(0, 0, 1, 1), SemanticAnchorKind.File, "src/Sample.cs");
        var edge = new SemanticEdge("contains", type.Id, member.Id, SemanticEdgeKind.Contains, 1, "member");
        var graph = new SemanticGraph([file, member, type], [edge]);
        var index = new SemanticNavigationIndex();

        var items = index.Build(graph, ImmutableArray.Create(document));

        Assert.Equal(["Sample", "Run"], items.Select(item => item.DisplayName).ToArray());
        Assert.DoesNotContain(items, item => item.Kind == SemanticAnchorKind.File);
        Assert.All(items, item => Assert.Equal(1, item.IncidentEdgeCount));
    }
}