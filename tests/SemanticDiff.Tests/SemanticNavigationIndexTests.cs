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

    [Fact]
    public void Build_ReturnsAllSymbolsAndPrioritizesCodeSymbolsInMixedBranches()
    {
        var factory = new DiffDocumentFactory();
        var xamlDocument = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("samples/App.xaml"), "samples/App.xaml", null, DiffFileStatus.Added, "XAML", 1, 0),
            "<Page />");
        var csharpDocument = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("src/Changed.cs"), "src/Changed.cs", null, DiffFileStatus.Modified, "C#", 1, 0),
            "namespace Sample; public sealed class Changed { public void Run() { } }");
        var anchors = Enumerable.Range(0, 100)
            .Select(index => new SemanticAnchor($"xaml-resource-{index}", xamlDocument.Id, new TextRange(0, 1, index + 1, 1), SemanticAnchorKind.Resource, $"Resource{index}"))
            .Concat([
                new SemanticAnchor("type", csharpDocument.Id, new TextRange(0, 7, 1, 39), SemanticAnchorKind.Type, "Sample.Changed"),
                new SemanticAnchor("member", csharpDocument.Id, new TextRange(42, 3, 1, 61), SemanticAnchorKind.Member, "Run")
            ])
            .ToImmutableArray();
        var graph = new SemanticGraph(anchors, []);
        var index = new SemanticNavigationIndex();

        var items = index.Build(graph, [xamlDocument, csharpDocument]);

        Assert.Equal(102, items.Length);
        Assert.Equal(["Sample.Changed", "Run"], items.Take(2).Select(item => item.DisplayName).ToArray());
        Assert.Contains(items.Skip(80), item => item.DisplayName == "Resource80");
    }
}