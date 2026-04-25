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
        var documents = CreateDocuments("A.cs", "B.cs", "C.cs", "D.cs");
        var request = new GraphLayoutRequest(documents, SemanticGraph.Empty, new Size2(620, 420), LayoutMode: GraphLayoutMode.Layered);
        var engine = new MsaglGraphLayoutEngine();

        var result = await engine.LayoutAsync(request, CancellationToken.None);

        var secondNode = Assert.Single(result.Nodes, node => node.DocumentId == documents[1].Id);
        Assert.Equal(716, secondNode.Bounds.X, precision: 3);
        Assert.Equal(0, secondNode.Bounds.Y, precision: 3);
    }

    [Fact]
    public async Task StatusLaneLayout_GroupsDocumentsByStatus()
    {
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
}