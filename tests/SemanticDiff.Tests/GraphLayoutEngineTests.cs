using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Layout;

namespace SemanticDiff.Tests;

public sealed class GraphLayoutEngineTests
{
    [Fact]
    public void GraphLayoutRequest_DefaultsToLayeredLayout()
    {
        var request = new GraphLayoutRequest([], SemanticGraph.Empty, new Size2(620, 420));

        Assert.Equal(GraphLayoutMode.Layered, request.LayoutMode);
    }

    [Fact]
    public async Task GridLayout_PreservesPinnedNodeBoundsFromPreviousLayout()
    {
        GraphLayoutCacheDiagnostics.Clear();
        var documents = CreateDocuments("A.cs", "B.cs");
        var previousBounds = new Rect2(500, 640, 620, 420);
        var previousNodes = ImmutableArray.Create(new DiffNodeLayout(documents[1].Id, previousBounds));
        var request = new GraphLayoutRequest(
            documents,
            SemanticGraph.Empty,
            new Size2(620, 420),
            previousNodes,
            ImmutableHashSet.Create(documents[1].Id));
        var engine = new GridGraphLayoutEngine();

        var result = await engine.LayoutAsync(request, CancellationToken.None);

        var pinnedNode = Assert.Single(result.Nodes, node => node.DocumentId == documents[1].Id);
        Assert.True(pinnedNode.IsPinned);
        Assert.Equal(previousBounds, pinnedNode.Bounds);
    }

    [Fact]
    public async Task GridLayout_StabilizesIncrementalLayoutAroundPreviousAnchor()
    {
        GraphLayoutCacheDiagnostics.Clear();
        var documents = CreateDocuments("A.cs", "B.cs");
        var previousBounds = new Rect2(900, 700, 620, 420);
        var previousNodes = ImmutableArray.Create(new DiffNodeLayout(documents[0].Id, previousBounds));
        var request = new GraphLayoutRequest(documents, SemanticGraph.Empty, new Size2(620, 420), previousNodes);
        var engine = new GridGraphLayoutEngine();

        var result = await engine.LayoutAsync(request, CancellationToken.None);

        var anchorNode = Assert.Single(result.Nodes, node => node.DocumentId == documents[0].Id);
        Assert.Equal(previousBounds.Center.X, anchorNode.Bounds.Center.X);
        Assert.Equal(previousBounds.Center.Y, anchorNode.Bounds.Center.Y);
    }

    [Fact]
    public async Task MsaglLayout_PropagatesCancellation()
    {
        GraphLayoutCacheDiagnostics.Clear();
        var documents = CreateDocuments("A.cs", "B.cs");
        var request = new GraphLayoutRequest(documents, SemanticGraph.Empty, new Size2(620, 420));
        var engine = new MsaglGraphLayoutEngine();
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await engine.LayoutAsync(request, cancellationTokenSource.Token).AsTask());
    }

    [Fact]
    public async Task AutoLayout_UsesCompactGridForLargeChangeSets()
    {
        GraphLayoutCacheDiagnostics.Clear();
        var documents = CreateDocuments(Enumerable.Range(0, 180).Select(index => $"File{index:000}.cs").ToArray());
        var request = new GraphLayoutRequest(documents, SemanticGraph.Empty, new Size2(620, 420), LayoutMode: GraphLayoutMode.Auto);
        var engine = new MsaglGraphLayoutEngine();

        var result = await engine.LayoutAsync(request, CancellationToken.None);

        Assert.Equal(documents.Length, result.Nodes.Length);
        var secondNode = Assert.Single(result.Nodes, node => node.DocumentId == documents[1].Id);
        Assert.Equal(668, secondNode.Bounds.X, precision: 3);
        Assert.Equal(0, secondNode.Bounds.Y, precision: 3);
    }

    [Fact]
    public async Task LayeredLayout_UsesDeterministicGridWhenNoSemanticLayoutEdgesExist()
    {
        GraphLayoutCacheDiagnostics.Clear();
        var documents = CreateDocuments("A.cs", "B.cs", "C.cs", "D.cs");
        var request = new GraphLayoutRequest(documents, SemanticGraph.Empty, new Size2(620, 420), LayoutMode: GraphLayoutMode.Layered);
        var engine = new MsaglGraphLayoutEngine();

        var result = await engine.LayoutAsync(request, CancellationToken.None);

        var secondNode = Assert.Single(result.Nodes, node => node.DocumentId == documents[1].Id);
        Assert.Equal(716, secondNode.Bounds.X, precision: 3);
        Assert.Equal(0, secondNode.Bounds.Y, precision: 3);
    }

    [Fact]
    public async Task LayeredLayout_ReusesPreparedAndArrangedSemanticLayoutForEquivalentRequests()
    {
        GraphLayoutCacheDiagnostics.Clear();
        var documents = CreateDocuments("A.cs", "B.cs");
        var graph = CreateTypeReferenceGraph(documents[0], documents[1]);
        var request = new GraphLayoutRequest(documents, graph, new Size2(620, 420), LayoutMode: GraphLayoutMode.Layered);
        var equivalentDocuments = CreateDocuments("A.cs", "B.cs");
        var equivalentRequest = new GraphLayoutRequest(
            equivalentDocuments,
            CreateTypeReferenceGraph(equivalentDocuments[0], equivalentDocuments[1]),
            new Size2(620, 420),
            LayoutMode: GraphLayoutMode.Layered);
        var engine = new MsaglGraphLayoutEngine();

        await engine.LayoutAsync(request, CancellationToken.None);
        var first = GraphLayoutCacheDiagnostics.Snapshot();
        await engine.LayoutAsync(equivalentRequest, CancellationToken.None);
        var second = GraphLayoutCacheDiagnostics.Snapshot();

        Assert.Equal(1, first.PrepareMisses);
        Assert.Equal(1, first.ArrangeMisses);
        Assert.Equal(first.PrepareHits + 1, second.PrepareHits);
        Assert.Equal(first.ArrangeHits + 1, second.ArrangeHits);
        Assert.Equal(first.MeasureMisses, second.MeasureMisses);
    }

    [Fact]
    public async Task LayeredLayout_UsesCompactSemanticClustersForLargeBranchShape()
    {
        GraphLayoutCacheDiagnostics.Clear();
        var paths = Enumerable.Range(0, 236).Select(index => $"src/Dock.Uno/File{index:000}.cs")
            .Concat(Enumerable.Range(0, 95).Select(index => $"src/Dock.Uno.Themes.Fluent/Theme{index:000}.xaml"))
            .Concat(Enumerable.Range(0, 60).Select(index => $"samples/DockUnoMvvmSample/Sample{index:000}.cs"))
            .Concat(Enumerable.Range(0, 8).Select(index => $"samples/DockUnoSample/Sample{index:000}.cs"))
            .Concat(["Dock.slnx", "Directory.Packages.props", "README.md", "build/Uno.props", "report/dock-uno-avalonia-to-uno-migration-log.md"])
            .ToArray();
        var documents = CreateDocuments(paths);
        var request = new GraphLayoutRequest(documents, SemanticGraph.Empty, new Size2(620, 420), LayoutMode: GraphLayoutMode.Layered);
        var engine = new MsaglGraphLayoutEngine();

        var result = await engine.LayoutAsync(request, CancellationToken.None);

        var bounds = Rect2.Union(result.Nodes.Select(node => node.Bounds));
        Assert.Equal(documents.Length, result.Nodes.Length);
        Assert.True(bounds.Width < 22_000, $"Expected compact semantic cluster width, actual {bounds.Width:0.##}.");
        Assert.True(bounds.Height < 12_000, $"Expected compact semantic cluster height, actual {bounds.Height:0.##}.");
        var sourceNode = Assert.Single(result.Nodes, node => node.DocumentId.Value == "src/Dock.Uno/File000.cs");
        var sampleNode = Assert.Single(result.Nodes, node => node.DocumentId.Value == "samples/DockUnoMvvmSample/Sample000.cs");
        Assert.True(sampleNode.Bounds.Top > sourceNode.Bounds.Top);
    }

    [Fact]
    public async Task StatusLaneLayout_GroupsDocumentsByStatus()
    {
        GraphLayoutCacheDiagnostics.Clear();
        var documents = CreateDocuments(
            ("Deleted.cs", DiffFileStatus.Deleted),
            ("Added.cs", DiffFileStatus.Added),
            ("Modified.cs", DiffFileStatus.Modified));
        var request = new GraphLayoutRequest(documents, SemanticGraph.Empty, new Size2(620, 420), LayoutMode: GraphLayoutMode.StatusLanes);
        var engine = new GridGraphLayoutEngine();

        var result = await engine.LayoutAsync(request, CancellationToken.None);

        var addedNode = Assert.Single(result.Nodes, node => node.DocumentId == documents[1].Id);
        var modifiedNode = Assert.Single(result.Nodes, node => node.DocumentId == documents[2].Id);
        var deletedNode = Assert.Single(result.Nodes, node => node.DocumentId == documents[0].Id);
        Assert.True(addedNode.Bounds.X < modifiedNode.Bounds.X);
        Assert.True(modifiedNode.Bounds.X < deletedNode.Bounds.X);
    }

    [Fact]
    public async Task GridLayout_UsesPretextMeasuredNodeBoundsForLongTitles()
    {
        GraphLayoutCacheDiagnostics.Clear();
        var longPath = "src/SemanticDiff.App/Very/Long/Path/That/Would/Overflow/Without/Measured/Text/And/Cached/Layout/ExtremelyLongDiffNodeTitleForPretextMeasurement.cs";
        var documents = CreateDocuments((longPath, DiffFileStatus.Modified, string.Join('\n', Enumerable.Repeat("class Sample { }", 1400))));
        var request = new GraphLayoutRequest(documents, SemanticGraph.Empty, new Size2(620, 420), LayoutMode: GraphLayoutMode.Grid);
        var engine = new GridGraphLayoutEngine();

        var result = await engine.LayoutAsync(request, CancellationToken.None);

        var node = Assert.Single(result.Nodes);
        Assert.True(node.Bounds.Width > 620);
        Assert.True(node.Bounds.Height > 420);
    }

    [Fact]
    public async Task GridLayout_ReusesPreparedMeasurementAndArrangeCacheForEquivalentRequests()
    {
        GraphLayoutCacheDiagnostics.Clear();
        var documents = CreateDocuments(
            "src/SemanticDiff.App/ViewModels/MainViewModel.cs",
            "src/SemanticDiff.App/Views/DiffCanvasControl.cs",
            "src/SemanticDiff.Layout/GridGraphLayoutEngine.cs");
        var request = new GraphLayoutRequest(documents, SemanticGraph.Empty, new Size2(620, 420), LayoutMode: GraphLayoutMode.Grid);
        var equivalentRequest = new GraphLayoutRequest(
            CreateDocuments(
                "src/SemanticDiff.App/ViewModels/MainViewModel.cs",
                "src/SemanticDiff.App/Views/DiffCanvasControl.cs",
                "src/SemanticDiff.Layout/GridGraphLayoutEngine.cs"),
            SemanticGraph.Empty,
            new Size2(620, 420),
            LayoutMode: GraphLayoutMode.Grid);
        var engine = new GridGraphLayoutEngine();

        await engine.LayoutAsync(request, CancellationToken.None);
        var first = GraphLayoutCacheDiagnostics.Snapshot();
        await engine.LayoutAsync(equivalentRequest, CancellationToken.None);
        var second = GraphLayoutCacheDiagnostics.Snapshot();

        Assert.Equal(1, first.PrepareMisses);
        Assert.Equal(0, first.PrepareHits);
        Assert.True(first.MeasureMisses >= documents.Length);
        Assert.Equal(1, first.ArrangeMisses);
        Assert.Equal(first.PrepareHits + 1, second.PrepareHits);
        Assert.Equal(first.PrepareMisses, second.PrepareMisses);
        Assert.Equal(first.MeasureHits, second.MeasureHits);
        Assert.Equal(first.MeasureMisses, second.MeasureMisses);
        Assert.Equal(first.ArrangeHits + 1, second.ArrangeHits);
        Assert.Equal(first.ArrangeMisses, second.ArrangeMisses);
        Assert.Equal(1, second.PreparedEntryCount);
    }

    [Fact]
    public async Task GridLayout_CacheFingerprintIgnoresSemanticGraphWhenEdgesAreNotUsed()
    {
        GraphLayoutCacheDiagnostics.Clear();
        var documents = CreateDocuments("A.cs", "B.cs");
        var request = new GraphLayoutRequest(documents, SemanticGraph.Empty, new Size2(620, 420), LayoutMode: GraphLayoutMode.Grid);
        var graphOnlyRequest = request with { SemanticGraph = CreateTypeReferenceGraph(documents[0], documents[1]) };
        var engine = new GridGraphLayoutEngine();

        await engine.LayoutAsync(request, CancellationToken.None);
        var first = GraphLayoutCacheDiagnostics.Snapshot();
        await engine.LayoutAsync(graphOnlyRequest, CancellationToken.None);
        var second = GraphLayoutCacheDiagnostics.Snapshot();

        Assert.Equal(first.PrepareHits + 1, second.PrepareHits);
        Assert.Equal(first.ArrangeHits + 1, second.ArrangeHits);
        Assert.Equal(first.MeasureMisses, second.MeasureMisses);
    }

    [Fact]
    public async Task CachedGridLayout_StillAppliesPinnedPreviousBounds()
    {
        GraphLayoutCacheDiagnostics.Clear();
        var documents = CreateDocuments("A.cs", "B.cs", "C.cs");
        var request = new GraphLayoutRequest(documents, SemanticGraph.Empty, new Size2(620, 420), LayoutMode: GraphLayoutMode.Grid);
        var engine = new GridGraphLayoutEngine();
        await engine.LayoutAsync(request, CancellationToken.None);
        var previousBounds = new Rect2(1600, 1200, 700, 500);
        var previousNodes = ImmutableArray.Create(new DiffNodeLayout(documents[1].Id, previousBounds));
        var pinnedRequest = request with
        {
            PreviousNodes = previousNodes,
            PinnedDocumentIds = ImmutableHashSet.Create(documents[1].Id)
        };

        var result = await engine.LayoutAsync(pinnedRequest, CancellationToken.None);

        var pinnedNode = Assert.Single(result.Nodes, node => node.DocumentId == documents[1].Id);
        Assert.True(GraphLayoutCacheDiagnostics.Snapshot().ArrangeHits > 0);
        Assert.True(pinnedNode.IsPinned);
        Assert.Equal(previousBounds, pinnedNode.Bounds);
    }

    private static ImmutableArray<DiffDocumentSnapshot> CreateDocuments(params string[] paths)
    {
        return CreateDocuments(paths.Select(path => (path, DiffFileStatus.Modified)).ToArray());
    }

    private static ImmutableArray<DiffDocumentSnapshot> CreateDocuments(params (string Path, DiffFileStatus Status)[] files)
    {
        var factory = new DiffDocumentFactory();
        return files
            .Select(file => factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId(file.Path), file.Path, null, file.Status, "C#", 0, 0), "class Sample { }"))
            .ToImmutableArray();
    }

    private static ImmutableArray<DiffDocumentSnapshot> CreateDocuments(params (string Path, DiffFileStatus Status, string Text)[] files)
    {
        var factory = new DiffDocumentFactory();
        return files
            .Select(file => factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId(file.Path), file.Path, null, file.Status, "C#", 0, 0), file.Text))
            .ToImmutableArray();
    }

    private static SemanticGraph CreateTypeReferenceGraph(DiffDocumentSnapshot source, DiffDocumentSnapshot target)
    {
        var sourceAnchor = new SemanticAnchor("A:type", source.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "A");
        var targetAnchor = new SemanticAnchor("B:type", target.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "B");
        var edge = new SemanticEdge("A-B", sourceAnchor.Id, targetAnchor.Id, SemanticEdgeKind.SymbolReference, 1, "uses");
        return new SemanticGraph([sourceAnchor, targetAnchor], [edge]);
    }

}
