using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Layout;

namespace SemanticDiff.Tests;

public sealed class GraphLayoutEngineTests
{
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

    private static ImmutableArray<DiffDocumentSnapshot> CreateDocuments(params string[] paths)
    {
        var factory = new DiffDocumentFactory();
        return paths
            .Select(path => factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId(path), path, null, DiffFileStatus.Modified, "C#", 0, 0), "class Sample { }"))
            .ToImmutableArray();
    }
}