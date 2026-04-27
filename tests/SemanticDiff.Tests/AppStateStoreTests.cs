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
}
