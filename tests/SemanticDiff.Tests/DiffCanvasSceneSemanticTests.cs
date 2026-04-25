using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Rendering;

namespace SemanticDiff.Tests;

public sealed class DiffCanvasSceneSemanticTests
{
    [Fact]
    public void FromDocuments_ProjectsSemanticEdgesToDocumentEdges()
    {
        var factory = new DiffDocumentFactory();
        var source = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var target = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("B.cs"), "B.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class B { }");
        var graph = new SemanticGraph(
            [
                new SemanticAnchor("A:type", source.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "A"),
                new SemanticAnchor("B:type", target.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "B")
            ],
            [new SemanticEdge("A->B", "A:type", "B:type", SemanticEdgeKind.SymbolReference, 0.8, "B")]);

        var scene = DiffCanvasScene.FromDocuments([source, target], graph);

        Assert.Contains(scene.Edges, edge => edge.SourceNodeId == "A.cs" && edge.TargetNodeId == "B.cs" && edge.Kind == SemanticEdgeKind.SymbolReference);
    }

    [Fact]
    public void FromDocuments_BundlesParallelEdgesAndFiltersLowConfidenceEdges()
    {
        var factory = new DiffDocumentFactory();
        var source = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var target = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("B.cs"), "B.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class B { }");
        var graph = new SemanticGraph(
            [
                new SemanticAnchor("A:type", source.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "A"),
                new SemanticAnchor("B:type", target.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "B")
            ],
            [
                new SemanticEdge("A->B:1", "A:type", "B:type", SemanticEdgeKind.SymbolReference, 0.8, "B"),
                new SemanticEdge("A->B:2", "A:type", "B:type", SemanticEdgeKind.SymbolReference, 0.9, "Create"),
                new SemanticEdge("A->B:weak", "A:type", "B:type", SemanticEdgeKind.SymbolReference, 0.2, "weak")
            ]);

        var scene = DiffCanvasScene.FromDocuments([source, target], graph, edgeOptions: new EdgeProjectionOptions(MinimumConfidence: 0.65));

        var edge = Assert.Single(scene.Edges);
        Assert.Equal(2, edge.BundleCount);
        Assert.Equal(0.9, edge.Confidence);
        Assert.Equal("2 semantic links", edge.Label);
    }

    [Fact]
    public void TogglePinned_ExposesPinnedDocumentIdsAndCurrentLayout()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var scene = DiffCanvasScene.FromDocuments([document]);

        scene.TogglePinned(scene.Nodes[0]);

        Assert.Contains(document.Id, scene.GetPinnedDocumentIds());
        Assert.Contains(scene.GetCurrentLayout(), node => node.DocumentId == document.Id && node.IsPinned);
    }

    [Fact]
    public void MoveNode_UpdatesBoundsAndPinsNode()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var scene = DiffCanvasScene.FromDocuments([document]);
        var node = scene.Nodes[0];
        var initialBounds = node.Bounds;

        scene.MoveNode(node, 35, -18);

        Assert.Equal(initialBounds.X + 35, node.Bounds.X);
        Assert.Equal(initialBounds.Y - 18, node.Bounds.Y);
        Assert.True(node.IsPinned);
    }

    [Fact]
    public void MoveNodeTo_UsesAbsolutePositionAndPinsNode()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var scene = DiffCanvasScene.FromDocuments([document]);
        var node = scene.Nodes[0];

        scene.MoveNodeTo(node, 125, 240);

        Assert.Equal(125, node.Bounds.X);
        Assert.Equal(240, node.Bounds.Y);
        Assert.True(node.IsPinned);
    }

    [Fact]
    public void TryHitTestTitleBar_DetectsChromeButNotBody()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var scene = DiffCanvasScene.FromDocuments([document]);
        var node = scene.Nodes[0];

        var titleHit = scene.TryHitTestTitleBar(scene.Camera.WorldToScreen(new Point2(node.Bounds.X + 80, node.Bounds.Y + 14)), out var titleNode);
        var bodyHit = scene.TryHitTestTitleBar(scene.Camera.WorldToScreen(new Point2(node.BodyBounds.X + 80, node.BodyBounds.Y + 14)), out _);

        Assert.True(titleHit);
        Assert.Same(node, titleNode);
        Assert.False(bodyHit);
    }

    [Fact]
    public void ResizeNode_UpdatesBoundsClampsMinimumSizeAndPinsNode()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var scene = DiffCanvasScene.FromDocuments([document]);
        var node = scene.Nodes[0];

        scene.ResizeNode(node, DiffNodeResizeHandle.BottomRight, -1_000, -1_000);

        Assert.Equal(DiffNode.MinWidth, node.Bounds.Width);
        Assert.Equal(DiffNode.MinHeight, node.Bounds.Height);
        Assert.True(node.IsPinned);
    }

    [Fact]
    public void TryHitTestResizeHandle_DetectsBottomRightHandle()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var scene = DiffCanvasScene.FromDocuments([document]);
        var node = scene.Nodes[0];
        var screenPoint = scene.Camera.WorldToScreen(new Point2(node.Bounds.Right, node.Bounds.Bottom));

        var hit = scene.TryHitTestResizeHandle(screenPoint, out var hitNode, out var handle);

        Assert.True(hit);
        Assert.Same(node, hitNode);
        Assert.Equal(DiffNodeResizeHandle.BottomRight, handle);
    }

    [Fact]
    public void TryHitTestResizeHandle_UsesScreenStableHitTargetAcrossZoom()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var scene = DiffCanvasScene.FromDocuments([document]);
        var node = scene.Nodes[0];

        var normalScalePoint = scene.Camera.WorldToScreen(new Point2(node.Bounds.Right, node.Bounds.Center.Y)).Translate(8, 0);
        var normalHit = scene.TryHitTestResizeHandle(normalScalePoint, out _, out var normalHandle);
        scene.ZoomAt(new Point2(120, 80), 2.5);
        var zoomedScalePoint = scene.Camera.WorldToScreen(new Point2(node.Bounds.Right, node.Bounds.Center.Y)).Translate(8, 0);
        var zoomedHit = scene.TryHitTestResizeHandle(zoomedScalePoint, out _, out var zoomedHandle);

        Assert.True(normalHit);
        Assert.True(zoomedHit);
        Assert.Equal(DiffNodeResizeHandle.Right, normalHandle);
        Assert.Equal(DiffNodeResizeHandle.Right, zoomedHandle);
    }

    [Fact]
    public void DragScrollbarThumb_UpdatesNodeScrollOffset()
    {
        var factory = new DiffDocumentFactory();
        var text = string.Join('\n', Enumerable.Range(1, 120).Select(line => $"line {line}"));
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), text);
        var scene = DiffCanvasScene.FromDocuments([document]);
        var node = scene.Nodes[0];
        var thumb = node.GetScrollbarThumbBounds(scene.Camera.Scale);
        var screenPoint = scene.Camera.WorldToScreen(new Point2(thumb.Center.X, thumb.Center.Y));

        var hit = scene.TryHitTestScrollbarThumb(screenPoint, out var hitNode, out var grabOffsetY);
        scene.DragScrollbarThumb(node, node.BodyBounds.Bottom - thumb.Height / 2, grabOffsetY);

        Assert.True(hit);
        Assert.Same(node, hitNode);
        Assert.True(node.ScrollOffsetY > 0);
    }

    [Fact]
    public void FontSizeButton_AdjustsFontSizeAndLineHeight()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var scene = DiffCanvasScene.FromDocuments([document]);
        var node = scene.Nodes[0];
        var initialFontSize = node.FontSize;
        var initialLineHeight = node.LineHeight;
        var button = node.GetFontSizeButtonBounds(DiffNodeFontSizeAction.Increase, scene.Camera.Scale);

        var hit = scene.TryHitTestFontSizeButton(scene.Camera.WorldToScreen(button.Center), out var hitNode, out var action);
        scene.AdjustNodeFontSize(node, action);

        Assert.True(hit);
        Assert.Same(node, hitNode);
        Assert.Equal(DiffNodeFontSizeAction.Increase, action);
        Assert.True(node.FontSize > initialFontSize);
        Assert.True(node.LineHeight > initialLineHeight);
        Assert.True(node.IsPinned);
    }

    [Fact]
    public void ApplyViewState_PreservesCameraNodeGeometrySelectionAndScroll()
    {
        var factory = new DiffDocumentFactory();
        var text = string.Join('\n', Enumerable.Range(1, 120).Select(line => $"line {line}"));
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), text);
        var scene = DiffCanvasScene.FromDocuments([document]);
        var node = scene.Nodes[0];
        scene.Pan(80, -35);
        scene.ZoomAt(new Point2(200, 140), 1.5);
        scene.SelectNode(node);
        scene.MoveNode(node, 45, 25);
        scene.AdjustNodeFontSize(node, DiffNodeFontSizeAction.Increase);
        node.ScrollBy(240);
        var viewState = scene.CaptureViewState();
        var refreshedDocument = factory.CreateFromText(new DiffDocumentMetadata(document.Id, "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), text + "\nnew line");

        var refreshedScene = DiffCanvasScene.FromDocuments([refreshedDocument]);
        refreshedScene.ApplyViewState(viewState);
        var refreshedNode = refreshedScene.Nodes[0];

        Assert.Equal(scene.Camera, refreshedScene.Camera);
        Assert.Equal(node.Bounds, refreshedNode.Bounds);
        Assert.Equal(node.FontSize, refreshedNode.FontSize);
        Assert.Equal(node.ScrollOffsetY, refreshedNode.ScrollOffsetY);
        Assert.True(refreshedNode.IsSelected);
        Assert.True(refreshedNode.IsPinned);
    }

    [Fact]
    public void FromDocuments_CanHideSemanticEdgesWithEmptyKindFilter()
    {
        var factory = new DiffDocumentFactory();
        var source = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var target = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("B.cs"), "B.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class B { }");
        var graph = new SemanticGraph(
            [
                new SemanticAnchor("A:type", source.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "A"),
                new SemanticAnchor("B:type", target.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "B")
            ],
            [new SemanticEdge("A->B", "A:type", "B:type", SemanticEdgeKind.SymbolReference, 0.8, "B")]);

        var scene = DiffCanvasScene.FromDocuments([source, target], graph, edgeOptions: new EdgeProjectionOptions(IncludedEdgeKinds: []));

        Assert.Empty(scene.Edges);
    }
}