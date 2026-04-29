using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Rendering;
using SemanticDiff.Workbench.FileDiff;

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
    public void FullFileNodeMode_SwitchesBetweenDiffAndFullDocuments()
    {
        var factory = new DiffDocumentFactory();
        var diff = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 1, 0), "class A { }");
        var full = factory.CreateFromText(diff.Metadata, "using System;\nclass A\n{\n}", DiffLineKind.Context);
        var scene = DiffCanvasScene.FromDocuments([diff]);

        scene.SetFullFileDocuments([new DiffNodeFullFileContent(diff.Id, full, [], full.ToSourceText())]);
        scene.SetShowFullFileNodes(true);

        Assert.True(scene.Nodes[0].IsShowingFullFile);
        Assert.Equal(4, scene.Nodes[0].Document.LineCount);

        scene.SetShowFullFileNodes(false);

        Assert.False(scene.Nodes[0].IsShowingFullFile);
        Assert.Equal(diff.LineCount, scene.Nodes[0].Document.LineCount);
    }

    [Fact]
    public void FullFileNodeMode_CollapsedFoldsReduceVisibleRows()
    {
        var factory = new DiffDocumentFactory();
        var diff = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 1, 0), "class A { }");
        var full = factory.CreateFromText(diff.Metadata, "class A\n{\n    void M()\n    {\n    }\n}", DiffLineKind.Context);
        var scene = DiffCanvasScene.FromDocuments([diff]);
        scene.SetFullFileDocuments([new DiffNodeFullFileContent(diff.Id, full, [new CodeFoldRegion(0, 5, "class A")], full.ToSourceText())]);
        scene.SetShowFullFileNodes(true);
        var node = scene.Nodes[0];

        var before = node.VisibleLineCount;
        Assert.True(node.ToggleFold(0));

        Assert.Equal(1, node.VisibleLineCount);
        Assert.True(before > node.VisibleLineCount);
    }

    [Fact]
    public void FullFileNodeMode_UsesAnnotatedFullFileLines()
    {
        var factory = new DiffDocumentFactory();
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 1, 0);
        var diff = new DiffDocumentSnapshot(
            metadata.Id,
            metadata,
            [
                new DiffLine(0, 1, 1, DiffLineKind.Context, "class A", []),
                new DiffLine(1, 2, 2, DiffLineKind.Modified, "{", []),
                new DiffLine(2, 3, 3, DiffLineKind.Context, "}", [])
            ]);
        var full = factory.CreateFromText(metadata, "class A\n{\n}", DiffLineKind.Context);
        var fullView = new FileDiffDocumentBuilder().Build(diff, full, full.ToSourceText(), []);
        var annotatedFull = full with { Lines = fullView.AnnotatedFullFileLines };
        var scene = DiffCanvasScene.FromDocuments([diff]);

        scene.SetFullFileDocuments([new DiffNodeFullFileContent(diff.Id, annotatedFull, fullView.FoldRegions, fullView.FullText)]);
        scene.SetShowFullFileNodes(true);

        Assert.Equal(DiffLineKind.Modified, scene.Nodes[0].Document.Lines[1].Kind);
    }

    [Fact]
    public void ToggleNodeEditing_ReEnablesEditingAfterFullFileViewWasHidden()
    {
        var factory = new DiffDocumentFactory();
        var diff = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 1, 0), "class A { }");
        var full = factory.CreateFromText(diff.Metadata, "class A\n{\n}", DiffLineKind.Context);
        var scene = DiffCanvasScene.FromDocuments([diff]);
        scene.SetFullFileDocuments([new DiffNodeFullFileContent(diff.Id, full, [], full.ToSourceText())]);
        var node = scene.Nodes[0];

        Assert.True(scene.ToggleNodeEditing(diff.Id.Value));
        Assert.True(node.IsShowingFullFile);
        Assert.True(node.IsEditingActive);
        Assert.True(scene.ToggleNodeFullFileView(diff.Id.Value));
        Assert.False(node.IsShowingFullFile);
        Assert.False(node.IsEditingActive);

        Assert.True(scene.ToggleNodeEditing(diff.Id.Value));

        Assert.True(node.IsShowingFullFile);
        Assert.True(node.IsEditingActive);
    }

    [Fact]
    public void NodeEditing_UpdatesInMemoryFullFileDocument()
    {
        var factory = new DiffDocumentFactory();
        var diff = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 1, 0), "class A { }");
        var full = factory.CreateFromText(diff.Metadata, "class A\n{\n}", DiffLineKind.Context);
        var scene = DiffCanvasScene.FromDocuments([diff]);
        scene.SetFullFileDocuments([new DiffNodeFullFileContent(diff.Id, full, [], full.ToSourceText())]);
        scene.SetShowFullFileNodes(true);
        scene.SetNodeEditingEnabled(true);
        var node = scene.Nodes[0];
        node.SetEditorFocus(true);
        node.MoveCaret(1, 0);
        node.SetCaretColumn(0);

        Assert.True(scene.InsertTextInFocusedEditor("// "));

        Assert.StartsWith("// ", node.Document.Lines[1].Text, StringComparison.Ordinal);
        Assert.Equal(node.Document.ToSourceText(), node.FullText);
    }

    [Fact]
    public void NodeEditing_LineCommands_UpdateFullFileDocument()
    {
        var factory = new DiffDocumentFactory();
        var diff = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 1, 0), "class A { }");
        var full = factory.CreateFromText(diff.Metadata, "class A\n{\n    void M()\n    {\n    }\n}", DiffLineKind.Context);
        var scene = DiffCanvasScene.FromDocuments([diff]);
        scene.SetFullFileDocuments([new DiffNodeFullFileContent(diff.Id, full, [], full.ToSourceText())]);
        scene.SetShowFullFileNodes(true);
        scene.SetNodeEditingEnabled(true);
        var node = scene.Nodes[0];
        node.SetEditorFocus(true);
        node.MoveCaret(2, 0);
        node.SetCaretColumn(8);

        Assert.True(scene.MoveFocusedEditorCaretToSmartLineStart());
        Assert.Equal(4, node.CaretColumn);
        Assert.True(scene.OutdentFocusedEditorLine());
        Assert.Equal("void M()", node.Document.Lines[2].Text);
        Assert.Equal(0, node.CaretColumn);
        Assert.True(scene.DuplicateFocusedEditorLine());
        Assert.Equal("void M()", node.Document.Lines[2].Text);
        Assert.Equal("void M()", node.Document.Lines[3].Text);
        Assert.True(scene.DeleteFocusedEditorLine());
        Assert.Equal("    {", node.Document.Lines[3].Text);
    }

    [Fact]
    public void NodeEditing_WordCommands_DeleteWordRanges()
    {
        var factory = new DiffDocumentFactory();
        var diff = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 1, 0), "class A { }");
        var full = factory.CreateFromText(diff.Metadata, "class A\n{\n    var valueName = nextValue;\n}", DiffLineKind.Context);
        var scene = DiffCanvasScene.FromDocuments([diff]);
        scene.SetFullFileDocuments([new DiffNodeFullFileContent(diff.Id, full, [], full.ToSourceText())]);
        scene.SetShowFullFileNodes(true);
        scene.SetNodeEditingEnabled(true);
        var node = scene.Nodes[0];
        node.SetEditorFocus(true);
        node.MoveCaret(2, 0);
        node.SetCaretColumn("    var valueName".Length);

        Assert.True(scene.BackspaceWordInFocusedEditor());
        Assert.Equal("    var  = nextValue;", node.Document.Lines[2].Text);
        node.SetCaretColumn("    var  = ".Length);
        Assert.True(scene.DeleteWordInFocusedEditor());
        Assert.Equal("    var  = ;", node.Document.Lines[2].Text);
        Assert.True(scene.MoveFocusedEditorCaretToDocumentEnd());
        Assert.Equal(node.Document.LineCount - 1, node.CaretLineIndex);
        Assert.True(scene.MoveFocusedEditorCaretToDocumentStart());
        Assert.Equal(0, node.CaretLineIndex);
        Assert.Equal(0, node.CaretColumn);
        node.MoveCaret(2, 0);
        node.SetCaretColumn(4);
        Assert.True(scene.MoveFocusedEditorCaretWord(1));
        Assert.True(node.CaretColumn > 4);
    }

    [Fact]
    public void NodeEditing_CharacterNavigationCrossesLineBoundaries()
    {
        var factory = new DiffDocumentFactory();
        var diff = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 1, 0), "class A { }");
        var full = factory.CreateFromText(diff.Metadata, "a\nbc", DiffLineKind.Context);
        var scene = DiffCanvasScene.FromDocuments([diff]);
        scene.SetFullFileDocuments([new DiffNodeFullFileContent(diff.Id, full, [], full.ToSourceText())]);
        scene.SetShowFullFileNodes(true);
        scene.SetNodeEditingEnabled(true);
        var node = scene.Nodes[0];
        node.SetEditorFocus(true);

        Assert.True(scene.MoveFocusedEditorCaret(0, 1));
        Assert.Equal(0, node.CaretLineIndex);
        Assert.Equal(1, node.CaretColumn);
        Assert.True(scene.MoveFocusedEditorCaret(0, 1));
        Assert.Equal(1, node.CaretLineIndex);
        Assert.Equal(0, node.CaretColumn);
        Assert.True(scene.MoveFocusedEditorCaret(0, -1));
        Assert.Equal(0, node.CaretLineIndex);
        Assert.Equal(1, node.CaretColumn);
    }

    [Fact]
    public void NodeEditing_EmptyFullFileRemainsNavigable()
    {
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0);
        var diff = new DiffDocumentSnapshot(metadata.Id, metadata, []);
        var full = new DiffDocumentSnapshot(metadata.Id, metadata, []);
        var scene = DiffCanvasScene.FromDocuments([diff]);
        scene.SetFullFileDocuments([new DiffNodeFullFileContent(diff.Id, full, [], string.Empty)]);
        scene.SetShowFullFileNodes(true);
        scene.SetNodeEditingEnabled(true);
        var node = scene.Nodes[0];
        node.SetEditorFocus(true);

        Assert.True(scene.MoveFocusedEditorCaretToDocumentEnd());
        Assert.True(scene.InsertTextInFocusedEditor("x"));
        Assert.Equal("x", node.FullText);
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
    public void MoveNodeTo_CanApplyContinuousPointerOffsetUpdates()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var scene = DiffCanvasScene.FromDocuments([document]);
        var node = scene.Nodes[0];
        var pointerOffset = new Point2(36, 14);

        foreach (var pointerWorldPoint in new[] { new Point2(140, 80), new Point2(170, 112), new Point2(220, 160) })
        {
            scene.MoveNodeTo(node, pointerWorldPoint.X - pointerOffset.X, pointerWorldPoint.Y - pointerOffset.Y);

            Assert.Equal(pointerWorldPoint.X - pointerOffset.X, node.Bounds.X);
            Assert.Equal(pointerWorldPoint.Y - pointerOffset.Y, node.Bounds.Y);
        }

        Assert.True(node.IsPinned);
    }

    [Fact]
    public void SelectNodesInRect_SupportsReplaceAddAndToggleModes()
    {
        var factory = new DiffDocumentFactory();
        var first = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var second = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("B.cs"), "B.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class B { }");
        var third = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("C.cs"), "C.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class C { }");
        var layout = new GraphLayoutResult([
            new DiffNodeLayout(first.Id, new Rect2(0, 0, 620, 420)),
            new DiffNodeLayout(second.Id, new Rect2(700, 0, 620, 420)),
            new DiffNodeLayout(third.Id, new Rect2(0, 600, 620, 420))
        ]);
        var scene = DiffCanvasScene.FromDocuments([first, second, third], layoutResult: layout);
        var topRowSelection = new Rect2(0, 0, 780, 440);
        var thirdSelection = new Rect2(0, 600, 620, 420);

        var replacedCount = scene.SelectNodesInRect(topRowSelection, DiffNodeSelectionMode.Replace);
        var selectedAfterReplace = scene.SelectedNodes.ToArray();
        scene.SelectNodesInRect(thirdSelection, DiffNodeSelectionMode.Add);
        var selectedAfterAdd = scene.SelectedNodes.ToArray();
        scene.SelectNodesInRect(topRowSelection, DiffNodeSelectionMode.Toggle);
        var selectedAfterToggle = scene.SelectedNodes.ToArray();

        Assert.Equal(2, replacedCount);
        Assert.Equal(new[] { first.Id, second.Id }, selectedAfterReplace.Select(node => node.Document.Id).ToArray());
        Assert.Equal(new[] { first.Id, second.Id, third.Id }, selectedAfterAdd.Select(node => node.Document.Id).ToArray());
        Assert.Equal(new[] { third.Id }, selectedAfterToggle.Select(node => node.Document.Id).ToArray());
    }

    [Fact]
    public void SelectAllAndInvertSelection_UpdateSelectionSet()
    {
        var factory = new DiffDocumentFactory();
        var first = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var second = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("B.cs"), "B.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class B { }");
        var third = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("C.cs"), "C.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class C { }");
        var scene = DiffCanvasScene.FromDocuments([first, second, third]);

        var allCount = scene.SelectAllNodes();
        var selectedAfterAll = scene.SelectedNodes.Select(node => node.DiffDocument.Id).ToArray();
        scene.SelectNode(scene.Nodes[0]);
        var invertedCount = scene.InvertNodeSelection();
        var selectedAfterInvert = scene.SelectedNodes.Select(node => node.DiffDocument.Id).ToArray();

        Assert.Equal(3, allCount);
        Assert.Equal([first.Id, second.Id, third.Id], selectedAfterAll);
        Assert.Equal(2, invertedCount);
        Assert.Equal([second.Id, third.Id], selectedAfterInvert);
    }

    [Fact]
    public void SelectNode_ClearsEditorFocusFromDeselectedNode()
    {
        var factory = new DiffDocumentFactory();
        var first = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var second = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("B.cs"), "B.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class B { }");
        var firstFull = factory.CreateFromText(first.Metadata, "class A\n{\n}", DiffLineKind.Context);
        var secondFull = factory.CreateFromText(second.Metadata, "class B\n{\n}", DiffLineKind.Context);
        var scene = DiffCanvasScene.FromDocuments([first, second]);
        scene.SetFullFileDocuments([
            new DiffNodeFullFileContent(first.Id, firstFull, [], firstFull.ToSourceText()),
            new DiffNodeFullFileContent(second.Id, secondFull, [], secondFull.ToSourceText())
        ]);
        scene.SetShowFullFileNodes(true);
        scene.SetNodeEditingEnabled(true);
        var firstNode = scene.Nodes.Single(node => node.DiffDocument.Id == first.Id);
        var secondNode = scene.Nodes.Single(node => node.DiffDocument.Id == second.Id);
        firstNode.SetEditorFocus(true);

        scene.SelectNode(secondNode);

        Assert.False(firstNode.IsEditorFocused);
        Assert.True(secondNode.IsSelected);
    }

    [Fact]
    public void ToggleNodeSelection_ClearsEditorFocusWhenNodeIsDeselected()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var full = factory.CreateFromText(document.Metadata, "class A\n{\n}", DiffLineKind.Context);
        var scene = DiffCanvasScene.FromDocuments([document]);
        scene.SetFullFileDocuments([new DiffNodeFullFileContent(document.Id, full, [], full.ToSourceText())]);
        scene.SetShowFullFileNodes(true);
        scene.SetNodeEditingEnabled(true);
        var node = scene.Nodes[0];
        scene.SelectNode(node);
        node.SetEditorFocus(true);

        scene.ToggleNodeSelection(node);

        Assert.False(node.IsSelected);
        Assert.False(node.IsEditorFocused);
    }

    [Fact]
    public void SelectConnectedNodes_SupportsDirectionAndSelectionMode()
    {
        var factory = new DiffDocumentFactory();
        var first = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var second = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("B.cs"), "B.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class B { }");
        var third = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("C.cs"), "C.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class C { }");
        var firstNode = new DiffNode(first, new Rect2(0, 0, 620, 420));
        var secondNode = new DiffNode(second, new Rect2(700, 0, 620, 420));
        var thirdNode = new DiffNode(third, new Rect2(1400, 0, 620, 420));
        var scene = new DiffCanvasScene(
            [firstNode, secondNode, thirdNode],
            [
                new GraphEdge(first.Id.Value, second.Id.Value, SemanticEdgeKind.SymbolReference, 0.9, "A to B"),
                new GraphEdge(third.Id.Value, first.Id.Value, SemanticEdgeKind.SymbolReference, 0.8, "C to A")
            ]);

        var outgoingCount = scene.SelectConnectedNodes(firstNode, DiffNodeSelectionMode.Replace, DiffNodeSelectionScope.Outgoing);
        var outgoingSelection = scene.SelectedNodes.Select(node => node.DiffDocument.Id).ToArray();
        scene.SelectConnectedNodes(firstNode, DiffNodeSelectionMode.Add, DiffNodeSelectionScope.Incoming);
        var incomingAddedSelection = scene.SelectedNodes.Select(node => node.DiffDocument.Id).ToArray();

        Assert.Equal(2, outgoingCount);
        Assert.Equal([first.Id, second.Id], outgoingSelection);
        Assert.Equal([first.Id, second.Id, third.Id], incomingAddedSelection);
    }

    [Fact]
    public void SelectGroupNodes_SelectsOnlyGroupMembers()
    {
        var factory = new DiffDocumentFactory();
        var first = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("src/App/A.cs"), "src/App/A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var second = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("src/App/B.cs"), "src/App/B.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class B { }");
        var third = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("tests/App.Tests/A.cs"), "tests/App.Tests/A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class ATests { }");
        var layout = new GraphLayoutResult([
            new DiffNodeLayout(first.Id, new Rect2(0, 0, 620, 420)),
            new DiffNodeLayout(second.Id, new Rect2(700, 0, 620, 420)),
            new DiffNodeLayout(third.Id, new Rect2(0, 600, 620, 420))
        ]);
        var scene = DiffCanvasScene.FromDocuments([first, second, third], layoutResult: layout, groupingMode: GraphGroupingMode.Folder);
        var group = Assert.Single(scene.Groups);

        var count = scene.SelectGroupNodes(group, DiffNodeSelectionMode.Replace);
        var selected = scene.SelectedNodes.Select(node => node.DiffDocument.Id).ToArray();

        Assert.Equal(2, count);
        Assert.Equal([first.Id, second.Id], selected);
    }

    [Fact]
    public void MoveSelectedNodes_TranslatesOnlySelectedNodesAndPinsThem()
    {
        var factory = new DiffDocumentFactory();
        var first = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var second = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("B.cs"), "B.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class B { }");
        var third = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("C.cs"), "C.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class C { }");
        var layout = new GraphLayoutResult([
            new DiffNodeLayout(first.Id, new Rect2(0, 0, 620, 420)),
            new DiffNodeLayout(second.Id, new Rect2(700, 0, 620, 420)),
            new DiffNodeLayout(third.Id, new Rect2(0, 600, 620, 420))
        ]);
        var scene = DiffCanvasScene.FromDocuments([first, second, third], layoutResult: layout);
        var firstNode = scene.Nodes.Single(node => node.Document.Id == first.Id);
        var secondNode = scene.Nodes.Single(node => node.Document.Id == second.Id);
        var thirdNode = scene.Nodes.Single(node => node.Document.Id == third.Id);
        var firstBounds = firstNode.Bounds;
        var secondBounds = secondNode.Bounds;
        var thirdBounds = thirdNode.Bounds;
        scene.SelectNodes([firstNode, thirdNode]);

        var moved = scene.MoveSelectedNodes(42, -17);

        Assert.True(moved);
        Assert.Equal(firstBounds.Translate(42, -17), firstNode.Bounds);
        Assert.Equal(secondBounds, secondNode.Bounds);
        Assert.Equal(thirdBounds.Translate(42, -17), thirdNode.Bounds);
        Assert.True(firstNode.IsPinned);
        Assert.False(secondNode.IsPinned);
        Assert.True(thirdNode.IsPinned);
    }

    [Fact]
    public void MoveGroup_TranslatesMemberNodesAndPinsThem()
    {
        var factory = new DiffDocumentFactory();
        var app = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("src/App/A.cs"), "src/App/A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var view = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("src/App/B.cs"), "src/App/B.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class B { }");
        var test = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("tests/App.Tests/A.cs"), "tests/App.Tests/A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class ATests { }");
        var layout = new GraphLayoutResult([
            new DiffNodeLayout(app.Id, new Rect2(100, 120, 620, 420)),
            new DiffNodeLayout(view.Id, new Rect2(820, 120, 620, 420)),
            new DiffNodeLayout(test.Id, new Rect2(100, 700, 620, 420))
        ]);
        var scene = DiffCanvasScene.FromDocuments([app, view, test], layoutResult: layout, groupingMode: GraphGroupingMode.Folder);
        var group = Assert.Single(scene.Groups);
        var appNode = scene.Nodes.Single(node => node.Document.Id == app.Id);
        var viewNode = scene.Nodes.Single(node => node.Document.Id == view.Id);
        var testNode = scene.Nodes.Single(node => node.Document.Id == test.Id);
        var appBounds = appNode.Bounds;
        var viewBounds = viewNode.Bounds;
        var testBounds = testNode.Bounds;
        var groupBounds = group.Bounds;

        var movedGroup = scene.MoveGroup(group, 55, -30);

        Assert.NotNull(movedGroup);
        Assert.Equal(groupBounds.Translate(55, -30), movedGroup.Bounds);
        Assert.Equal(appBounds.Translate(55, -30), appNode.Bounds);
        Assert.Equal(viewBounds.Translate(55, -30), viewNode.Bounds);
        Assert.Equal(testBounds, testNode.Bounds);
        Assert.True(appNode.IsPinned);
        Assert.True(viewNode.IsPinned);
        Assert.False(testNode.IsPinned);
    }

    [Fact]
    public void TryHitTestGroup_DetectsGroupRegionWhenNoNodeIsHit()
    {
        var factory = new DiffDocumentFactory();
        var app = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("src/App/A.cs"), "src/App/A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var view = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("src/App/B.cs"), "src/App/B.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class B { }");
        var layout = new GraphLayoutResult([
            new DiffNodeLayout(app.Id, new Rect2(100, 120, 620, 420)),
            new DiffNodeLayout(view.Id, new Rect2(820, 120, 620, 420))
        ]);
        var scene = DiffCanvasScene.FromDocuments([app, view], layoutResult: layout, groupingMode: GraphGroupingMode.Folder);
        var group = Assert.Single(scene.Groups);

        var hit = scene.TryHitTestGroup(scene.Camera.WorldToScreen(new Point2(group.Bounds.Left + 12, group.Bounds.Top + 12)), out var hitGroup);

        Assert.True(hit);
        Assert.Same(group, hitGroup);
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
    public void TryHitTestAnnotation_DetectsVisibleLineCommentAndTracksHover()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "first\nsecond\nthird");
        var annotation = new DiffAnnotation(
            "A.cs:review-comment:thread-1",
            document.Id,
            DiffAnnotationKind.ReviewComment,
            DiffAnnotationTarget.Line,
            1,
            2,
            "review",
            "Please verify this line.",
            DiffAnnotationSeverity.Warning,
            DiffAnnotationActionKind.ReviewThread,
            "thread-1");
        var scene = DiffCanvasScene.FromDocuments([document], annotations: [annotation]);
        var node = scene.Nodes[0];
        var worldPoint = new Point2(node.BodyBounds.Right - 9, node.BodyBounds.Top + node.LineHeight + 8);

        var hit = scene.TryHitTestAnnotation(scene.Camera.WorldToScreen(worldPoint), out var annotationHit);
        var hoverChanged = scene.SetHoveredAnnotation(annotationHit?.Annotation);

        Assert.True(hit);
        Assert.NotNull(annotationHit);
        Assert.Same(node, annotationHit.Node);
        Assert.Equal(annotation.Id, annotationHit.Annotation.Id);
        Assert.True(hoverChanged);
        Assert.Equal(annotation.Id, scene.HoveredAnnotationId);
    }

    [Fact]
    public void TryHitTestAnnotation_RespectsAnnotationVisibility()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "first\nsecond");
        var annotation = new DiffAnnotation(
            "A.cs:review-comment:thread-1",
            document.Id,
            DiffAnnotationKind.ReviewComment,
            DiffAnnotationTarget.Line,
            0,
            1,
            "review",
            "Hidden review comment.",
            DiffAnnotationSeverity.Warning,
            DiffAnnotationActionKind.ReviewThread,
            "thread-1");
        var scene = DiffCanvasScene.FromDocuments(
            [document],
            annotations: [annotation],
            annotationVisibility: new DiffAnnotationVisibilityState(ShowReviewComments: false));
        var node = scene.Nodes[0];
        var worldPoint = new Point2(node.BodyBounds.Right - 9, node.BodyBounds.Top + 8);

        var hit = scene.TryHitTestAnnotation(scene.Camera.WorldToScreen(worldPoint), out var annotationHit);

        Assert.False(hit);
        Assert.Null(annotationHit);
    }

    [Fact]
    public void TryHitTestResizeHandle_DoesNotCaptureScrollbarThumb()
    {
        var factory = new DiffDocumentFactory();
        var text = string.Join('\n', Enumerable.Range(1, 120).Select(line => $"line {line}"));
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), text);
        var scene = DiffCanvasScene.FromDocuments([document]);
        var node = scene.Nodes[0];
        var thumb = node.GetScrollbarThumbBounds(scene.Camera.Scale);
        var screenPoint = scene.Camera.WorldToScreen(thumb.Center);

        var resizeHit = scene.TryHitTestResizeHandle(screenPoint, out _, out _);
        var scrollbarHit = scene.TryHitTestScrollbarThumb(screenPoint, out var hitNode, out _);

        Assert.False(resizeHit);
        Assert.True(scrollbarHit);
        Assert.Same(node, hitNode);
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
    public void FontSizeButtons_AreHiddenWhenNodeIsTooSmallOnScreen()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var scene = DiffCanvasScene.FromDocuments([document]);
        var node = scene.Nodes[0];

        scene.ZoomAt(new Point2(120, 80), 0.2);

        Assert.False(node.CanShowFontSizeButtons(scene.Camera.Scale));
        Assert.True(node.GetFontSizeButtonBounds(DiffNodeFontSizeAction.Increase, scene.Camera.Scale).IsEmpty);
        Assert.False(scene.TryHitTestFontSizeButton(scene.Camera.WorldToScreen(node.TitleBounds.Center), out _, out _));
    }

    [Fact]
    public void FontSizeButtons_StayInsideNodeTitleAcrossUsableZoomLevels()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("A.cs"), "A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var scene = DiffCanvasScene.FromDocuments([document]);
        var node = scene.Nodes[0];

        AssertFontButtonsInsideTitle(node, scene.Camera.Scale);
        scene.ZoomAt(new Point2(120, 80), 2.5);
        AssertFontButtonsInsideTitle(node, scene.Camera.Scale);
    }

    private static void AssertFontButtonsInsideTitle(DiffNode node, double cameraScale)
    {
        Assert.True(node.CanShowFontSizeButtons(cameraScale));
        foreach (var action in new[] { DiffNodeFontSizeAction.Decrease, DiffNodeFontSizeAction.Increase })
        {
            var bounds = node.GetFontSizeButtonBounds(action, cameraScale);
            Assert.False(bounds.IsEmpty);
            Assert.True(bounds.Left >= node.TitleBounds.Left);
            Assert.True(bounds.Right <= node.TitleBounds.Right);
            Assert.True(bounds.Right <= node.TitleBounds.Right - DiffNode.FontControlLineCountInset);
            Assert.True(bounds.Top >= node.TitleBounds.Top);
            Assert.True(bounds.Bottom <= node.TitleBounds.Bottom);
        }
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

    [Fact]
    public void FromDocuments_BuildsFolderGroupsFromLaidOutNodes()
    {
        var factory = new DiffDocumentFactory();
        var app = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("src/App/Main.cs"), "src/App/Main.cs", null, DiffFileStatus.Modified, "C#", 4, 2), "class Main { }");
        var view = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("src/App/MainPage.xaml"), "src/App/MainPage.xaml", null, DiffFileStatus.Modified, "XAML", 3, 1), "<Page />");
        var test = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("tests/App.Tests/MainTests.cs"), "tests/App.Tests/MainTests.cs", null, DiffFileStatus.Modified, "C#", 1, 0), "class MainTests { }");
        var layout = new GraphLayoutResult([
            new DiffNodeLayout(app.Id, new Rect2(100, 120, 620, 420)),
            new DiffNodeLayout(view.Id, new Rect2(820, 120, 620, 420)),
            new DiffNodeLayout(test.Id, new Rect2(100, 700, 620, 420))
        ]);

        var scene = DiffCanvasScene.FromDocuments([app, view, test], layoutResult: layout, groupingMode: GraphGroupingMode.Folder);

        var group = Assert.Single(scene.Groups);
        Assert.Equal("src/App", group.Label);
        Assert.Equal(2, group.DocumentCount);
        Assert.Contains(app.Id, group.DocumentIds);
        Assert.Contains(view.Id, group.DocumentIds);
        Assert.DoesNotContain(test.Id, group.DocumentIds);
        Assert.Equal(7, group.AddedLines);
        Assert.Equal(3, group.DeletedLines);
        Assert.True(group.Bounds.Left < 100);
        Assert.True(group.Bounds.Right > 1_440);
    }

    [Fact]
    public void FromDocuments_BuildsSemanticGroupsFromAnchors()
    {
        var factory = new DiffDocumentFactory();
        var firstView = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("Views/MainPage.xaml"), "Views/MainPage.xaml", null, DiffFileStatus.Modified, "XAML", 2, 0), "<Page />");
        var secondView = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("Views/SettingsPage.xaml"), "Views/SettingsPage.xaml", null, DiffFileStatus.Modified, "XAML", 2, 0), "<Page />");
        var model = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("Models/Widget.cs"), "Models/Widget.cs", null, DiffFileStatus.Modified, "C#", 2, 0), "class Widget { }");
        var graph = new SemanticGraph(
            [
                new SemanticAnchor("main:xaml-root", firstView.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.XamlRoot, "Page"),
                new SemanticAnchor("settings:xaml-root", secondView.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.XamlRoot, "Page"),
                new SemanticAnchor("widget:type", model.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, "Widget")
            ],
            []);

        var scene = DiffCanvasScene.FromDocuments([firstView, secondView, model], graph, groupingMode: GraphGroupingMode.Semantic);

        var group = Assert.Single(scene.Groups);
        Assert.Equal("UI/XAML", group.Label);
        Assert.Equal(GraphGroupingMode.Semantic, group.Mode);
        Assert.Equal(2, group.DocumentCount);
    }

    [Fact]
    public void FromDocuments_AllowsGroupingToBeDisabled()
    {
        var factory = new DiffDocumentFactory();
        var first = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("src/App/A.cs"), "src/App/A.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class A { }");
        var second = factory.CreateFromText(new DiffDocumentMetadata(new DiffDocumentId("src/App/B.cs"), "src/App/B.cs", null, DiffFileStatus.Modified, "C#", 0, 0), "class B { }");

        var scene = DiffCanvasScene.FromDocuments([first, second], groupingMode: GraphGroupingMode.None);

        Assert.Empty(scene.Groups);
    }
}
