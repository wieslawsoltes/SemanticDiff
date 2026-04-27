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

    [Fact]
    public void Build_MarksChangedSymbolsAndInsightFacets()
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
        var graph = new SemanticGraph([type, member], [new SemanticEdge("contains", type.Id, member.Id, SemanticEdgeKind.Contains, 1, "member")]);
        var navigationIndex = new SemanticNavigationIndex();
        var insightIndex = new SemanticSymbolInsightIndex();

        var items = navigationIndex.Build(graph, [document]);
        var insight = insightIndex.Build(items);

        Assert.False(items.Single(item => item.DisplayName == "Feature").IsChanged);
        Assert.True(items.Single(item => item.DisplayName == "Run").IsChanged);
        Assert.Equal(2, insight.TotalSymbolCount);
        Assert.Equal(1, insight.ChangedSymbolCount);
        Assert.Equal(2, insight.LinkedSymbolCount);
        Assert.Contains(insight.KindFacets, facet => facet.Kind == SemanticAnchorKind.Member && facet.ChangedCount == 1);
        Assert.Contains(insight.DocumentFacets, facet => facet.Path == "src/Feature.cs" && facet.ChangedCount == 1);
        Assert.Equal("Run", insight.HotSymbols[0].DisplayName);
    }

    [Fact]
    public void Build_AddsFileFallbackForDocumentsWithoutNavigableAnchors()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("docs/notes.md"), "docs/notes.md", null, DiffFileStatus.Modified, "Markdown", 2, 0),
            "# Notes",
            DiffLineKind.Added);
        var index = new SemanticNavigationIndex();

        var items = index.Build(SemanticGraph.Empty, [document]);
        var insight = new SemanticSymbolInsightIndex().Build(items);

        var item = Assert.Single(items);
        Assert.Equal(SemanticAnchorKind.File, item.Kind);
        Assert.Equal("File", item.KindText);
        Assert.Equal("notes.md", item.DisplayName);
        Assert.True(item.IsChanged);
        Assert.Equal(1, insight.DocumentCount);
        Assert.Contains(insight.DocumentFacets, facet => facet.Path == "docs/notes.md");
    }

    [Fact]
    public void Build_UsesFileAnchorWhenOnlyNonNavigableAnchorsExist()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("src/OnlyNamespace.cs"), "src/OnlyNamespace.cs", null, DiffFileStatus.Modified, "C#", 1, 0),
            "namespace Sample;");
        var file = new SemanticAnchor("file", document.Id, new TextRange(0, 0, 1, 1), SemanticAnchorKind.File, "src/OnlyNamespace.cs");
        var ns = new SemanticAnchor("namespace", document.Id, new TextRange(0, 9, 1, 11), SemanticAnchorKind.Namespace, "Sample");
        var graph = new SemanticGraph([file, ns], [new SemanticEdge("file-namespace", file.Id, ns.Id, SemanticEdgeKind.Contains, 1, "namespace")]);
        var index = new SemanticNavigationIndex();

        var items = index.Build(graph, [document]);

        var item = Assert.Single(items);
        Assert.Equal(SemanticAnchorKind.File, item.Kind);
        Assert.Equal(file.Id, item.AnchorId);
        Assert.Equal("OnlyNamespace.cs", item.DisplayName);
        Assert.Equal(1, item.IncidentEdgeCount);
        Assert.True(item.IsLinked);
    }
}
