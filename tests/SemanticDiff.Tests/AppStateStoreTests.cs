using SemanticDiff.Core;

namespace SemanticDiff.Tests;

public sealed class AppStateStoreTests
{
    [Fact]
    public void SemanticDiffAppState_DefaultsToLayeredLayout()
    {
        var state = new SemanticDiffAppState();

        Assert.Equal(GraphLayoutMode.Layered, state.LayoutMode);
        Assert.Equal(GraphGroupingMode.Folder, state.GroupingMode);
        Assert.Equal(GitReviewRequestState.Open, state.ReviewRequestState);
        Assert.True(state.UseInteractiveLevelOfDetail);
    }

    [Fact]
    public async Task JsonAppStateStore_RoundTripsRepositoryAndLayoutState()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"SemanticDiffState-{Guid.NewGuid():N}");
        var filePath = Path.Combine(directoryPath, "app-state.json");
        var store = new JsonAppStateStore(filePath);
        var state = new SemanticDiffAppState(
            RepositoryPath: "/repo",
            DiffScope: GitDiffScope.Worktree,
            WatchRepositoryChanges: true,
            AutoReloadDelayMs: 700,
            ThemeMode: SemanticDiffThemeMode.Light,
            DiffContextMode: DiffContextMode.FullFileDiff,
            ReviewMode: DiffReviewMode.IgnoreWhitespace,
            CollapseUnchangedContext: true,
            LayoutNodes: [new DiffNodeLayoutState("A.cs", 10, 20, 620, 420, true, 15.5)],
            BaseRef: "origin/main",
            HeadRef: "feature/work",
            ShowSemanticEdges: false,
            AnnotationVisibility: new DiffAnnotationVisibilityState(
                ShowGitStatus: true,
                ShowSemantic: false,
                ShowDiagnostics: true,
                ShowReview: false,
                ShowHistory: true,
                ShowNavigation: false,
                ShowContext: true,
                ShowReviewComments: false),
            SemanticAnalysisMode: SemanticAnalysisMode.FastSyntaxOnly,
            LayoutMode: GraphLayoutMode.StatusLanes,
            GroupingMode: GraphGroupingMode.Semantic,
            ReviewRequestState: GitReviewRequestState.Merged,
            SelectedBranchRef: "origin/feature/work",
            SelectedPullRequestNumber: 42,
            UseInteractiveLevelOfDetail: false,
            CodeCompletionMode: CodeCompletionMode.DocumentOnly,
            LeftPaneWidth: 344);

        try
        {
            await store.SaveAsync(state, CancellationToken.None);

            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.Equal("/repo", loaded.RepositoryPath);
            Assert.True(loaded.WatchRepositoryChanges);
            Assert.Equal(700, loaded.AutoReloadDelayMs);
            Assert.Equal(SemanticDiffThemeMode.Light, loaded.ThemeMode);
            Assert.Equal(DiffContextMode.FullFileDiff, loaded.DiffContextMode);
            Assert.Equal(DiffReviewMode.IgnoreWhitespace, loaded.ReviewMode);
            Assert.True(loaded.CollapseUnchangedContext);
            Assert.Equal("origin/main", loaded.BaseRef);
            Assert.Equal("feature/work", loaded.HeadRef);
            Assert.False(loaded.ShowSemanticEdges);
            Assert.False(loaded.EffectiveAnnotationVisibility.ShowSemantic);
            Assert.False(loaded.EffectiveAnnotationVisibility.ShowReview);
            Assert.False(loaded.EffectiveAnnotationVisibility.ShowReviewComments);
            Assert.False(loaded.EffectiveAnnotationVisibility.ShowNavigation);
            Assert.Equal(SemanticAnalysisMode.FastSyntaxOnly, loaded.SemanticAnalysisMode);
            Assert.Equal(GraphLayoutMode.StatusLanes, loaded.LayoutMode);
            Assert.Equal(GraphGroupingMode.Semantic, loaded.GroupingMode);
            Assert.Equal(GitReviewRequestState.Merged, loaded.ReviewRequestState);
            Assert.Equal("origin/feature/work", loaded.SelectedBranchRef);
            Assert.Equal(42, loaded.SelectedPullRequestNumber);
            Assert.False(loaded.UseInteractiveLevelOfDetail);
            Assert.Equal(CodeCompletionMode.DocumentOnly, loaded.CodeCompletionMode);
            Assert.Equal(344, loaded.LeftPaneWidth);
            var node = Assert.Single(loaded.EffectiveLayoutNodes);
            Assert.Equal("A.cs", node.DocumentId);
            Assert.True(node.IsPinned);
            Assert.Equal(10, node.X);
            Assert.Equal(15.5, node.FontSize);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task JsonAppStateStore_RoundTripsWorkspaceSessionTabsAndCanvasState()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"SemanticDiffState-{Guid.NewGuid():N}");
        var filePath = Path.Combine(directoryPath, "app-state.json");
        var store = new JsonAppStateStore(filePath);
        var session = new WorkspaceSessionState(
            Version: 1,
            RepositoryPath: "/repo",
            SelectedTabId: "file:src/App.cs",
            SelectedExplorerDocumentId: "src/App.cs",
            FileSearchText: "App",
            FileExplorerMode: WorkspaceSessionFileExplorerMode.Workspace,
            GitReferenceSearchText: "feature",
            ReviewSearchText: "thread",
            SymbolSearchText: "Window",
            Tabs:
            [
                new WorkspaceTabState(
                    "graph",
                    WorkspaceSessionTabKind.Graph,
                    "Diff Graph",
                    "Semantic node canvas",
                    false,
                    "Ready",
                    new WorkspaceCanvasState(
                        CameraOffsetX: 120,
                        CameraOffsetY: 80,
                        CameraScale: 1.35,
                        ShowFullFileNodes: true,
                        EnableNodeEditing: true,
                        Nodes:
                        [
                            new WorkspaceNodeState(
                                "src/App.cs",
                                10,
                                20,
                                640,
                                420,
                                ScrollOffsetY: 55,
                                IsSelected: true,
                                IsPinned: true,
                                FontSize: 16,
                                FullFileViewOverride: true,
                                EditingOverride: true,
                                CaretLineIndex: 12,
                                CaretColumn: 8,
                                FullText: "class App { }")
                        ]),
                    GraphRequest: new GitDiffRequest("/repo", GitDiffScope.Branch, "origin/main", "feature/work"),
                    GraphBranchReferenceName: "feature/work",
                    GraphReviewRequest: new GitPullRequestInfo(42, "Feature work", "main", "feature/work", "owner/repo", true)),
                new WorkspaceTabState(
                    "history:main",
                    WorkspaceSessionTabKind.GitHistory,
                    "History main",
                    "main",
                    true,
                    History: new GitHistoryTabState(new GitHistoryRequest("/repo", "main", MaxCount: 200), LoadedCount: 400)),
                new WorkspaceTabState(
                    "file:src/App.cs",
                    WorkspaceSessionTabKind.FileDiff,
                    "App.cs",
                    "src/App.cs",
                    true,
                    FileDiff: new FileDiffTabState(
                        "src/App.cs",
                        "src/App.cs",
                        FileDiffDisplayState.FullFile,
                        FileDiffScopeState.FullFileDiff,
                        IsDiffAnnotationEnabled: false,
                        IsEditingEnabled: true,
                        CodeFontSize: 18,
                        FullText: "namespace Demo;")),
                new WorkspaceTabState(
                    "symbols:changed",
                    WorkspaceSessionTabKind.SymbolGraph,
                    "Symbol Graph",
                    "changed symbols",
                    true,
                    SymbolGraph: new SymbolGraphTabState(
                        SearchText: "Window",
                        ScopeKey: "Changed",
                        KindKey: "Type",
                        DocumentKey: "src/App.cs",
                        EdgeKindKey: "DependsOn",
                        LayoutMode: GraphLayoutMode.Grid,
                        GroupingMode: GraphGroupingMode.Semantic,
                        ViewMode: SymbolGraphDisplayState.FilesAndSymbols,
                        FocusAnchorId: "symbol:App")),
                new WorkspaceTabState(
                    "editor:src",
                    WorkspaceSessionTabKind.EditorCanvas,
                    "Edit src",
                    "2 files",
                    true,
                    EditorCanvas: new EditorCanvasTabState(
                    [
                        new EditorCanvasDocumentState("src/App.cs", "class App { }"),
                        new EditorCanvasDocumentState("src/View.cs", "class View { }")
                    ])),
                new WorkspaceTabState(
                    "query:1",
                    WorkspaceSessionTabKind.QueryCanvas,
                    "Query Canvas",
                    "Live LINQ graph",
                    true,
                    QueryCanvas: new QueryCanvasTabState("Symbols.Where(s => s.IsChanged)", "Workspace", "Changed symbols")),
                new WorkspaceTabState(
                    "patch:1",
                    WorkspaceSessionTabKind.PatchCompare,
                    "Patch Compare",
                    "range-diff",
                    true,
                    PatchCompare: new PatchCompareTabState(
                        "fork/m119..fork/m119-patches",
                        "fork/m147..fork/m147-patches",
                        "/repo",
                        "chrome",
                        "/repo"))
            ]);
        var state = new SemanticDiffAppState(RepositoryPath: "/repo", WorkspaceSession: session);

        try
        {
            await store.SaveAsync(state, CancellationToken.None);

            var loaded = await store.LoadAsync(CancellationToken.None);

            var loadedSession = loaded.EffectiveWorkspaceSession;
            Assert.Equal("file:src/App.cs", loadedSession.SelectedTabId);
            Assert.Equal(WorkspaceSessionFileExplorerMode.Workspace, loadedSession.FileExplorerMode);
            Assert.Equal("thread", loadedSession.ReviewSearchText);
            Assert.Equal(7, loadedSession.EffectiveTabs.Length);
            var graph = loadedSession.EffectiveTabs[0];
            Assert.Equal(WorkspaceSessionTabKind.Graph, graph.Kind);
            Assert.Equal(GitDiffScope.Branch, graph.GraphRequest?.Scope);
            Assert.Equal(42, graph.GraphReviewRequest?.Number);
            Assert.True(graph.Canvas?.ShowFullFileNodes);
            var node = Assert.Single(graph.Canvas!.EffectiveNodes);
            Assert.True(node.IsPinned);
            Assert.True(node.FullFileViewOverride);
            Assert.True(node.EditingOverride);
            Assert.Equal(12, node.CaretLineIndex);
            Assert.Equal("class App { }", node.FullText);
            Assert.Equal(400, loadedSession.EffectiveTabs[1].History?.LoadedCount);
            Assert.Equal(FileDiffDisplayState.FullFile, loadedSession.EffectiveTabs[2].FileDiff?.DisplayMode);
            Assert.Equal("DependsOn", loadedSession.EffectiveTabs[3].SymbolGraph?.EdgeKindKey);
            Assert.Equal("symbol:App", loadedSession.EffectiveTabs[3].SymbolGraph?.FocusAnchorId);
            Assert.Equal(2, loadedSession.EffectiveTabs[4].EditorCanvas?.EffectiveDocuments.Length);
            Assert.Equal("Workspace", loadedSession.EffectiveTabs[5].QueryCanvas?.Scope);
            Assert.Equal("chrome", loadedSession.EffectiveTabs[6].PatchCompare?.WizardFilterText);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task JsonAppStateStore_LoadsMissingInteractionLevelOfDetailAsEnabled()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"SemanticDiffState-{Guid.NewGuid():N}");
        var filePath = Path.Combine(directoryPath, "app-state.json");
        Directory.CreateDirectory(directoryPath);
        await File.WriteAllTextAsync(filePath, """
            {
              "RepositoryPath": "/repo"
            }
            """);

        var store = new JsonAppStateStore(filePath);

        try
        {
            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.True(loaded.UseInteractiveLevelOfDetail);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
