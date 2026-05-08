using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Rendering;
using SemanticDiff.Semantics;
using SemanticDiff.Workbench.Query;
using SemanticDiff.Workbench.Symbols;

namespace SemanticDiff.Tests;

public sealed class QueryCanvasEngineTests
{
    private readonly DiffDocumentFactory factory = new();

    [Fact]
    public void Execute_FiltersAndOrdersFileNodes()
    {
        var a = CreateDocument("src/App.xaml.cs", DiffFileStatus.Modified, "C#", added: 2, deleted: 0);
        var b = CreateDocument("src/Controls/Button.cs", DiffFileStatus.Modified, "C#", added: 8, deleted: 1);
        var c = CreateDocument("README.md", DiffFileStatus.Unchanged, "Markdown", added: 0, deleted: 0);
        var context = CreateContext([a, b, c], [], [], SemanticGraph.Empty);

        var result = new QueryCanvasEngine().Execute(
            "Files.Where(f => f.Path.Contains(\"Controls\") && f.IsChanged).OrderByDescending(f => f.AddedLines).Take(5)",
            context,
            QueryCanvasScope.Diff);

        Assert.False(result.HasError);
        Assert.Equal(QueryCanvasResultKind.Files, result.Kind);
        var node = Assert.Single(result.Scene.Nodes);
        Assert.Equal("src/Controls/Button.cs", node.DiffDocument.Metadata.Path);
    }

    [Fact]
    public void Execute_UsesWorkspaceScopeFiles()
    {
        var diff = CreateDocument("src/Changed.cs", DiffFileStatus.Modified, "C#", added: 1, deleted: 0);
        var workspace = CreateDocument("src/Services/WorkspaceQuery.cs", DiffFileStatus.Unchanged, "C#", added: 0, deleted: 0);
        var context = CreateContext([diff], [workspace], [], SemanticGraph.Empty);

        var result = new QueryCanvasEngine().Execute(
            "WorkspaceFiles.Where(f => f.Path.Contains(\"Services\")).Take(10)",
            context,
            QueryCanvasScope.Workspace);

        Assert.False(result.HasError);
        var node = Assert.Single(result.Scene.Nodes);
        Assert.Equal("src/Services/WorkspaceQuery.cs", node.DiffDocument.Metadata.Path);
    }

    [Fact]
    public void Execute_UsesWorkspaceOverviewWhenWorkspaceQueryIsEmpty()
    {
        var diff = CreateDocument("src/Changed.cs", DiffFileStatus.Modified, "C#", added: 1, deleted: 0);
        var workspace = CreateDocument("docs/guide.md", DiffFileStatus.Unchanged, "Markdown", added: 0, deleted: 0);
        var context = CreateContext([diff], [workspace], [], SemanticGraph.Empty);

        var result = new QueryCanvasEngine().Execute(
            "",
            context,
            QueryCanvasScope.Workspace);

        Assert.False(result.HasError);
        var node = Assert.Single(result.Scene.Nodes);
        Assert.Equal("docs/guide.md", node.DiffDocument.Metadata.Path);
    }

    [Fact]
    public void Execute_OrdersWorkspaceFilesByProvidedSizeBytes()
    {
        var small = CreateDocument("src/Small.cs", DiffFileStatus.Unchanged, "C#", added: 0, deleted: 0);
        var large = CreateDocument("src/Large.cs", DiffFileStatus.Unchanged, "C#", added: 0, deleted: 0);
        var context = CreateContext(
            [],
            [small, large],
            [],
            SemanticGraph.Empty,
            new Dictionary<DiffDocumentId, QueryFileMetrics>
            {
                [small.Id] = new(128),
                [large.Id] = new(4096)
            }.ToImmutableDictionary());

        var result = new QueryCanvasEngine().Execute(
            "WorkspaceFiles.OrderByDescending(f => f.SizeBytes).Take(1)",
            context,
            QueryCanvasScope.Workspace);

        Assert.False(result.HasError);
        var node = Assert.Single(result.Scene.Nodes);
        Assert.Equal("src/Large.cs", node.DiffDocument.Metadata.Path);
    }

    [Fact]
    public void Execute_AllowsLinqPadStyleDumpAndTrailingSemicolon()
    {
        var document = CreateDocument("src/A.cs", DiffFileStatus.Modified, "C#", added: 1, deleted: 0);
        var context = CreateContext([document], [], [], SemanticGraph.Empty);

        var result = new QueryCanvasEngine().Execute("Files.Take(1).Dump();", context, QueryCanvasScope.Diff);

        Assert.False(result.HasError);
        Assert.Single(result.Scene.Nodes);
    }

    [Fact]
    public void Execute_RendersSymbolMapWithFileNodesAndSemanticEdges()
    {
        var source = CreateDocument("src/A.cs", DiffFileStatus.Modified, "C#", added: 1, deleted: 0);
        var target = CreateDocument("src/B.cs", DiffFileStatus.Modified, "C#", added: 1, deleted: 0);
        var graph = new SemanticGraph(
            [
                new SemanticAnchor("A:type", source.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "A"),
                new SemanticAnchor("B:type", target.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "B")
            ],
            [new SemanticEdge("A->B", "A:type", "B:type", SemanticEdgeKind.SymbolReference, 0.9, "uses")]);
        var symbols = new[]
        {
            new SemanticNavigationItem("A:type", source.Id, source.Metadata.Path, SemanticAnchorKind.Type, "A", 1, 1, 1, true),
            new SemanticNavigationItem("B:type", target.Id, target.Metadata.Path, SemanticAnchorKind.Type, "B", 1, 1, 1, true)
        };
        var context = CreateContext([source, target], [], symbols, graph);

        var result = new QueryCanvasEngine().Execute(
            "LinkedSymbols.Where(s => s.Links > 0).Map().Take(20)",
            context,
            QueryCanvasScope.Diff);

        Assert.False(result.HasError);
        Assert.Equal(QueryCanvasResultKind.Mixed, result.Kind);
        Assert.True(result.Scene.Nodes.Count >= 4);
        Assert.Contains(result.Scene.Edges, edge => edge.Kind == SemanticEdgeKind.SymbolReference);
    }

    [Fact]
    public void Execute_ReturnsErrorForUnknownProperty()
    {
        var document = CreateDocument("src/A.cs", DiffFileStatus.Modified, "C#", added: 1, deleted: 0);
        var context = CreateContext([document], [], [], SemanticGraph.Empty);

        var result = new QueryCanvasEngine().Execute("Files.Where(f => f.DoesNotExist == 1)", context, QueryCanvasScope.Diff);

        Assert.True(result.HasError);
        Assert.Equal(QueryCanvasResultKind.Error, result.Kind);
        Assert.Contains("Unknown file property", result.StatusText);
    }

    [Theory]
    [MemberData(nameof(QuerySamples))]
    public void Execute_QuerySamplesDoNotError(QueryCanvasSample sample)
    {
        var context = CreateSampleContext();

        var result = new QueryCanvasEngine().Execute(sample.Query, context, sample.PreferredScope);

        Assert.False(result.HasError, $"{sample.DisplayName}: {result.StatusText}");
    }

    public static IEnumerable<object[]> QuerySamples() =>
        QueryCanvasSampleCatalog.All.Select(sample => new object[] { sample });

    private DiffDocumentSnapshot CreateDocument(string path, DiffFileStatus status, string language, int added, int deleted)
    {
        var metadata = new DiffDocumentMetadata(new DiffDocumentId(path), path, null, status, language, added, deleted);
        return factory.CreateFromText(metadata, $"class {Path.GetFileNameWithoutExtension(path).Replace('.', '_')} {{ }}", DiffLineKind.Context);
    }

    private QueryCanvasContext CreateSampleContext()
    {
        var controls = CreateDocument("src/Controls/Button.cs", DiffFileStatus.Modified, "C#", added: 8, deleted: 1);
        var rendering = CreateDocument("src/Rendering/Renderer.cs", DiffFileStatus.Modified, "C#", added: 4, deleted: 2);
        var addedTest = CreateDocument("tests/SemanticDiff.Tests/ButtonTests.cs", DiffFileStatus.Added, "C#", added: 24, deleted: 0);
        var deleted = CreateDocument("src/Legacy/OldService.cs", DiffFileStatus.Deleted, "C#", added: 0, deleted: 16);
        var xaml = CreateDocument("src/Controls/View.xaml", DiffFileStatus.Modified, "XAML", added: 2, deleted: 1);
        var workspaceUtility = CreateDocument("src/Workspace/WorkspaceQuery.cs", DiffFileStatus.Unchanged, "C#", added: 0, deleted: 0);
        var workspaceXaml = CreateDocument("src/Workspace/App.xaml", DiffFileStatus.Unchanged, "XAML", added: 0, deleted: 0);
        var workspaceTest = CreateDocument("tests/Workspace/WorkspaceTests.cs", DiffFileStatus.Unchanged, "C#", added: 0, deleted: 0);
        var graph = new SemanticGraph(
            [
                new SemanticAnchor("button:type", controls.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "Button"),
                new SemanticAnchor("button:member", controls.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Member, "Button.Click"),
                new SemanticAnchor("renderer:type", rendering.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "Renderer"),
                new SemanticAnchor("view:xaml", xaml.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Resource, "View")
            ],
            [
                new SemanticEdge("button-renderer", "button:type", "renderer:type", SemanticEdgeKind.SymbolReference, 1, "uses"),
                new SemanticEdge("button-member", "button:type", "button:member", SemanticEdgeKind.Contains, 1, "contains"),
                new SemanticEdge("button-view", "button:type", "view:xaml", SemanticEdgeKind.Resource, 1, "uses")
            ]);
        var symbols = new[]
        {
            new SemanticNavigationItem("button:type", controls.Id, controls.Metadata.Path, SemanticAnchorKind.Type, "Button", 1, 1, 3, true),
            new SemanticNavigationItem("button:member", controls.Id, controls.Metadata.Path, SemanticAnchorKind.Member, "Button.Click", 1, 1, 2, true),
            new SemanticNavigationItem("renderer:type", rendering.Id, rendering.Metadata.Path, SemanticAnchorKind.Type, "Renderer", 1, 1, 1, true),
            new SemanticNavigationItem("view:xaml", xaml.Id, xaml.Metadata.Path, SemanticAnchorKind.Resource, "View", 1, 1, 1, true)
        };
        return CreateContext(
            [controls, rendering, addedTest, deleted, xaml],
            [workspaceUtility, workspaceXaml, workspaceTest],
            symbols,
            graph);
    }

    private static QueryCanvasContext CreateContext(
        DiffDocumentSnapshot[] diffDocuments,
        DiffDocumentSnapshot[] workspaceDocuments,
        SemanticNavigationItem[] symbols,
        SemanticGraph graph,
        ImmutableDictionary<DiffDocumentId, QueryFileMetrics>? fileMetrics = null) => new(
        [.. diffDocuments],
        [.. workspaceDocuments],
        [.. symbols],
        graph,
        GraphLayoutMode.Layered,
        GraphGroupingMode.Folder,
        new EdgeProjectionOptions(MinimumConfidence: 0, MaxEdgesPerDocumentPair: 6),
        DiffAnnotationVisibilityState.Default,
        SymbolGraphViewMode.FilesAndSymbols,
        fileMetrics);
}
