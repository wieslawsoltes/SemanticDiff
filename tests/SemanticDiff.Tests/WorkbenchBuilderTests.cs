using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Rendering;
using SemanticDiff.Rendering.Export;
using SemanticDiff.Semantics;
using SemanticDiff.Workbench.Blame;
using SemanticDiff.Workbench.FileDiff;
using SemanticDiff.Workbench.History;
using SemanticDiff.Workbench.Review;
using SemanticDiff.Workbench.Symbols;
using SemanticDiff.Workbench.Workspace;

namespace SemanticDiff.Tests;

public sealed class WorkbenchBuilderTests
{
    [Fact]
    public void FileDiffDocumentBuilder_BuildsFullDiffAndAnnotatedFullFileLines()
    {
        var factory = new DiffDocumentFactory();
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("src/Sample.cs"), "src/Sample.cs", null, DiffFileStatus.Modified, "C#", 2, 1);
        var diff = factory.CreateFromUnifiedDiff(
            metadata,
            """
            --- a/src/Sample.cs
            +++ b/src/Sample.cs
            @@ -1,3 +1,4 @@
             alpha
            -beta
            +beta changed
             gamma
            +delta
            """);
        var full = factory.CreateFromText(metadata, "alpha\nbeta changed\ngamma\ndelta");
        var builder = new FileDiffDocumentBuilder();

        var view = builder.Build(diff, full, "alpha\nbeta changed\ngamma\ndelta", []);

        Assert.Equal(diff.Lines.Length, view.ChangedHunkLines.Length);
        Assert.Contains(view.FullDiffLines, line => line.Kind == DiffLineKind.Deleted && line.Text == "beta");
        Assert.Contains(view.FullDiffLines, line => line.Kind == DiffLineKind.Added && line.Text == "beta changed");
        Assert.Contains(view.FullDiffLines, line => line.Kind == DiffLineKind.Added && line.Text == "delta");
        Assert.Equal(4, view.AnnotatedFullFileLines.Length);
        Assert.Equal(DiffLineKind.Added, view.AnnotatedFullFileLines[1].Kind);
        Assert.Equal(DiffLineKind.Added, view.AnnotatedFullFileLines[3].Kind);
    }

    [Fact]
    public void SymbolGraphSceneBuilder_BuildsSymbolOnlyAndHybridScenes()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("src/Sample.cs"), "src/Sample.cs", null, DiffFileStatus.Modified, "C#", 1, 0),
            "public sealed class Sample { public void Run() { } }");
        var type = new SemanticAnchor("type", document.Id, new TextRange(0, 6, 1, 21), SemanticAnchorKind.Type, "Sample");
        var member = new SemanticAnchor("member", document.Id, new TextRange(29, 3, 1, 35), SemanticAnchorKind.Member, "Run");
        var graph = new SemanticGraph([type, member], [new SemanticEdge("type-member", type.Id, member.Id, SemanticEdgeKind.SymbolReference, 1, "uses")]);
        var items = new SemanticNavigationIndex().Build(graph, [document]);
        var builder = new SymbolGraphSceneBuilder();

        var symbolsOnly = builder.Build(new SymbolGraphSceneBuildRequest(items, graph, [document], GraphLayoutMode.Layered, GraphGroupingMode.None, SymbolGraphViewMode.SymbolsOnly));
        var hybrid = builder.Build(new SymbolGraphSceneBuildRequest(items, graph, [document], GraphLayoutMode.Layered, GraphGroupingMode.None, SymbolGraphViewMode.FilesAndSymbols));

        Assert.Equal(2, symbolsOnly.Scene.Nodes.Count);
        Assert.Single(symbolsOnly.Scene.Edges);
        Assert.Equal(3, hybrid.Scene.Nodes.Count);
        Assert.Equal(1, hybrid.FileCount);
        Assert.True(hybrid.Scene.Edges.Count >= 2);
    }

    [Fact]
    public void BlameChangeGraphBuilder_CreatesOneNodePerHistoryCommit()
    {
        var commits = ImmutableArray.Create(
            new BlameChangeGraphCommit("commit-3", "commit-3", "latest", "Ada", "2026-04-27", 1),
            new BlameChangeGraphCommit("commit-2", "commit-2", "middle", "Grace", "2026-04-26", 2),
            new BlameChangeGraphCommit("commit-1", "commit-1", "root", "Linus", "2026-04-25", 0));
        var linesByCommit = ImmutableDictionary<string, ImmutableArray<GitBlameLine>>.Empty
            .Add("commit-3", [new GitBlameLine(3, "commit-3", "Ada", null, "latest", "return true;")])
            .Add("commit-2", [new GitBlameLine(1, "commit-2", "Grace", null, "middle", "class Sample"), new GitBlameLine(2, "commit-2", "Grace", null, "middle", "{")]);
        var builder = new BlameChangeGraphBuilder();

        var result = builder.Build(new BlameChangeGraphBuildRequest("src/Sample.cs", "C#", commits, linesByCommit));

        Assert.Equal(commits.Length, result.Scene.Nodes.Count);
        Assert.Equal(commits.Length - 1, result.Scene.Edges.Count);
        Assert.Contains("3 history nodes", result.SummaryText);
    }

    [Fact]
    public void GitHistoryLaneLayout_ReturnsNeutralPathsForLinearHistory()
    {
        var layout = new GitHistoryLaneLayout();
        var newest = Commit("commit-3", ["commit-2"]);
        var middle = Commit("commit-2", ["commit-1"]);
        var root = Commit("commit-1", []);

        var firstRow = layout.CreateRow(newest);
        var secondRow = layout.CreateRow(middle);
        var thirdRow = layout.CreateRow(root);

        Assert.NotEmpty(firstRow.Paths);
        Assert.NotEmpty(secondRow.Paths);
        Assert.True(thirdRow.DotSize > 0);
        Assert.All(firstRow.Paths.Concat(secondRow.Paths), path => Assert.True(path.Points.Length >= 2));
    }


    [Fact]
    public void SymbolBrowserModel_FiltersByScopeKindDocumentAndQuery()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("src/Sample.cs"), "src/Sample.cs", null, DiffFileStatus.Modified, "C#", 1, 0),
            "public sealed class Sample { public void Run() { } }");
        var type = new SemanticAnchor("type", document.Id, new TextRange(0, 6, 1, 21), SemanticAnchorKind.Type, "Sample");
        var member = new SemanticAnchor("member", document.Id, new TextRange(29, 3, 1, 35), SemanticAnchorKind.Member, "Run");
        var graph = new SemanticGraph([type, member], [new SemanticEdge("type-member", type.Id, member.Id, SemanticEdgeKind.SymbolReference, 1, "uses")]);
        var items = new SemanticNavigationIndex().Build(graph, [document]);
        var browser = new SymbolBrowserModel();

        browser.SetItems(items);
        browser.SetScope(SymbolBrowserSelection.LinkedKey);
        browser.ToggleKind("Member");
        browser.ToggleDocument(document.Id.Value);
        var view = browser.Apply("Run");

        Assert.Single(view.Items);
        Assert.Equal("Run", view.Items[0].DisplayName);
        Assert.Contains("search", view.FilterStatusText);
        Assert.Equal("1/2 symbols", view.CountText);
    }

    [Fact]
    public void DiffWorkspaceCache_StoresOnlyCacheableBranchViewsAndTrimsOldEntries()
    {
        var documents = SampleDiffDocuments.Create();
        var scene = DiffCanvasScene.FromDocuments(documents);
        var cache = new DiffWorkspaceCache(capacity: 1);
        var options = new DiffWorkspaceCacheKeyOptions(
            DiffContextMode.ChangedHunks,
            DiffReviewMode.Precise,
            CollapseUnchangedContext: false,
            SemanticAnalysisMode.FastSyntaxOnly,
            GraphLayoutMode.Layered,
            GraphGroupingMode.Folder,
            ShowSemanticEdges: true);
        var firstRequest = new GitDiffRequest("/repo", GitDiffScope.Branch, "main", "feature/a");
        var secondRequest = new GitDiffRequest("/repo", GitDiffScope.Branch, "main", "feature/b");

        cache.Store("/repo", firstRequest, options, documents, SemanticGraph.Empty, scene, null, [], null, "first", documents[0].Id.Value);
        cache.Store("/repo", secondRequest, options, documents, SemanticGraph.Empty, scene, null, [], null, "second", documents[0].Id.Value);

        Assert.False(cache.TryGet("/repo", firstRequest, options, out _));
        Assert.True(cache.TryGet("/repo", secondRequest, options, out var entry));
        Assert.Equal("second", entry.StatusPrefix);
        Assert.False(DiffWorkspaceCache.IsCacheable(new GitDiffRequest("/repo", GitDiffScope.Branch, "main", "HEAD")));
    }

    [Fact]
    public void ReviewThreadFilter_PreservesSelectedFilteredThread()
    {
        var threads = ImmutableArray.Create(
            new TestThread("1", "alpha review"),
            new TestThread("2", "beta review"));
        var filter = new ReviewThreadFilter<TestThread>(thread => thread.Id, thread => thread.SearchText);

        var result = filter.Apply(threads, "beta", threads[1]);

        Assert.Single(result.Items);
        Assert.Same(threads[1], result.SelectedItem);
        Assert.Equal("1/2 threads", result.CountText);
    }

    [Fact]
    public void GitReferenceBrowserModel_FiltersCountsAndTracksExpansion()
    {
        var browser = new GitReferenceBrowserModel<TestRef, TestRef>(item => item.SearchText, item => item.SearchText);
        browser.SetReferences(
            [new TestRef("main branch"), new TestRef("feature branch")],
            [new TestRef("fix merge request")],
            GitReviewRequestKind.MergeRequest,
            supportsReviewRequests: true);

        browser.ToggleNode("git:branches");
        var unfiltered = browser.Apply(string.Empty, "Open");
        var filtered = browser.Apply("fix", "Open");

        Assert.Equal("2 branches | 1 MR", unfiltered.CountText);
        Assert.Equal("0/2 branches | 1/1 MR", filtered.CountText);
        Assert.Equal("Merge Requests", filtered.ReviewRequestGroupTitle);
        Assert.Equal("Open GitLab merge requests", filtered.ReviewRequestGroupDetail);
        Assert.False(browser.IsExpanded("git:branches", forceExpanded: false));
        Assert.True(browser.IsExpanded("git:branches", forceExpanded: true));
    }

    [Fact]
    public void ReviewWorkflowModel_RestoresThreadsAndFiltersSelection()
    {
        var thread = new GitReviewThreadInfo(
            "thread-1",
            GitReviewThreadKind.Diff,
            "Sample.cs by Ada",
            "Sample.cs",
            42,
            false,
            true,
            true,
            []);
        var model = new ReviewWorkflowModel<TestThread>(item => item.Id, item => item.SearchText);

        model.SetThreads([thread], [new TestThread("thread-1", "Sample.cs review")], "Review ready");
        var view = model.ApplyFilter("sample", null);

        Assert.Single(view.Items);
        Assert.Equal("Review ready", view.StatusText);
        Assert.Equal("thread-1", model.FindThread("thread-1")?.Id);
        Assert.Single(model.Threads);
    }

    [Fact]
    public void WorkspaceDocumentManager_AddsSelectsFindsAndClosesTabs()
    {
        var tabs = new List<TestTab> { new("graph", false) };
        var manager = new WorkspaceDocumentManager<TestTab>(tabs, tab => tab.Id, tab => tab.IsClosable);
        TestTab? selected = null;

        manager.AddAndSelect(new TestTab("history", true), tab => selected = tab);
        var selectedExisting = manager.TrySelect("history", tab => selected = tab);
        manager.Close(selected, tabs[0], tab => selected = tab);

        Assert.True(selectedExisting);
        Assert.Single(tabs);
        Assert.Equal("graph", selected?.Id);
        Assert.Null(manager.Find("history"));
    }

    [Fact]
    public void RepositoryDiffLoader_PrepareDocuments_AppliesReviewPipeline()
    {
        var factory = new DiffDocumentFactory();
        var diff = factory.CreateFromUnifiedDiff(
            new DiffDocumentMetadata(new DiffDocumentId("src/Sample.cs"), "src/Sample.cs", null, DiffFileStatus.Modified, "C#", 1, 1),
            """
            --- a/src/Sample.cs
            +++ b/src/Sample.cs
            @@ -1,2 +1,2 @@
            -alpha
            +alpha changed
             beta
            """);

        var prepared = RepositoryDiffLoader.PrepareDocuments([diff], DiffReviewMode.Precise, collapseUnchangedContext: true);

        Assert.Single(prepared);
        Assert.Contains(prepared[0].Lines, line => line.Kind == DiffLineKind.Deleted);
        Assert.Contains(prepared[0].Lines, line => line.Kind == DiffLineKind.Added);
    }

    [Theory]
    [InlineData(DiffSceneExportFormat.Svg)]
    [InlineData(DiffSceneExportFormat.Png)]
    [InlineData(DiffSceneExportFormat.Pdf)]
    public void DiffSceneExportService_WritesRequestedFormat(DiffSceneExportFormat format)
    {
        var scene = DiffCanvasScene.FromDocuments(SampleDiffDocuments.Create());
        using var stream = new MemoryStream();
        var exporter = new DiffSceneExportService();

        exporter.Export(scene, stream, new DiffSceneExportOptions(format, IsLightTheme: true));

        Assert.True(stream.Length > 100);
    }

    private static GitCommitInfo Commit(string id, ImmutableArray<string> parents) =>
        new(id, id[..Math.Min(8, id.Length)], parents, "Tester", "tester@example.com", DateTimeOffset.Parse("2026-04-27T12:00:00Z"), string.Empty, $"Commit {id}");

    private sealed record TestThread(string Id, string SearchText);

    private sealed record TestRef(string SearchText);

    private sealed record TestTab(string Id, bool IsClosable);
}
