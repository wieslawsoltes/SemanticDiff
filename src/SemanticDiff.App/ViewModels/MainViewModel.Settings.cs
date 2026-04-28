using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Git;
using SemanticDiff.Layout;
using SemanticDiff.Rendering;
using SemanticDiff.Semantics;
using SemanticDiff.Semantics.Roslyn;
using SemanticDiff.Semantics.Xaml;
using SemanticDiff.Workbench.Review;
using SemanticDiff.Workbench.Symbols;
using SemanticDiff.Workbench.Workspace;
using Windows.UI;

namespace SemanticDiff.App.ViewModels;

public sealed partial class MainViewModel
{
    public async Task SetAutoRefreshAsync(bool isEnabled)
    {
        appState = appState with { WatchRepositoryChanges = isEnabled };
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);

        if (isEnabled && !string.IsNullOrWhiteSpace(currentRepositoryPath) && Directory.Exists(currentRepositoryPath))
        {
            await RestartRepositoryWatcherAsync(currentRepositoryPath, CancellationToken.None);
            AddDiagnostic("Info", "Automatic refresh enabled");
        }
        else
        {
            await StopRepositoryWatcherAsync();
            WatchStatusText = "Watch off";
            AddDiagnostic("Info", "Automatic refresh disabled");
        }

        RefreshSceneAnnotations();
    }

    public async Task SetDiffContextModeAsync(DiffContextMode contextMode)
    {
        if (appState.DiffContextMode == contextMode)
        {
            ApplyAppStateToPresentation();
            return;
        }

        CacheCurrentDiffView();
        appState = appState with { DiffContextMode = contextMode };
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Diff context changed to {FormatDiffContextMode(contextMode)}");
        await LoadRepositoryAsync(loadAppState: false, operationMessage: "Reloading diff context");
    }

    public async Task SetReviewModeAsync(bool isNoiseFilterEnabled)
    {
        var nextMode = isNoiseFilterEnabled ? DiffReviewMode.IgnoreWhitespace : DiffReviewMode.Precise;
        if (appState.ReviewMode == nextMode)
        {
            ApplyAppStateToPresentation();
            return;
        }

        CacheCurrentDiffView();
        appState = appState with { ReviewMode = nextMode };
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Review mode changed to {FormatReviewMode(nextMode)}");
        await LoadRepositoryAsync(loadAppState: false, operationMessage: "Reloading review mode");
    }

    public async Task SetContextFoldingAsync(bool isContextFoldingEnabled)
    {
        if (appState.CollapseUnchangedContext == isContextFoldingEnabled)
        {
            ApplyAppStateToPresentation();
            return;
        }

        CacheCurrentDiffView();
        appState = appState with { CollapseUnchangedContext = isContextFoldingEnabled };
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", isContextFoldingEnabled ? "Collapsed unchanged context" : "Expanded unchanged context");
        await LoadRepositoryAsync(loadAppState: false, operationMessage: "Reloading context folding");
    }

    public async Task SetThemeAsync(bool isLightThemeEnabled)
    {
        var nextTheme = isLightThemeEnabled ? SemanticDiffThemeMode.Light : SemanticDiffThemeMode.Dark;
        if (appState.ThemeMode == nextTheme)
        {
            ApplyAppStateToPresentation();
            return;
        }

        appState = appState with { ThemeMode = nextTheme };
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Theme changed to {nextTheme}");
    }

    public async Task SetSemanticEdgesAsync(bool isEnabled)
    {
        if (appState.ShowSemanticEdges == isEnabled)
        {
            ApplyAppStateToPresentation();
            return;
        }

        CaptureLayoutState(Scene);
        appState = appState with { ShowSemanticEdges = isEnabled };
        ApplyAppStateToPresentation();
        Scene = CreateScene(currentDocuments, currentSemanticGraph, previousLayout);
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", isEnabled ? "Semantic edges shown" : "Semantic edges hidden");
    }

    public async Task SetSemanticAnalysisModeAsync(SemanticAnalysisMode analysisMode)
    {
        if (appState.SemanticAnalysisMode == analysisMode)
        {
            ApplyAppStateToPresentation();
            return;
        }

        CacheCurrentDiffView();
        appState = appState with { SemanticAnalysisMode = analysisMode, LayoutNodes = null };
        previousLayout = null;
        pinnedDocumentIds = ImmutableHashSet<DiffDocumentId>.Empty;
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Semantic mode changed to {FormatSemanticAnalysisMode(analysisMode)}");
        await LoadRepositoryAsync(loadAppState: false, operationMessage: "Reloading semantic analysis");
    }

    public async Task SetLayoutModeAsync(GraphLayoutMode layoutMode, DiffCanvasScene? currentScene)
    {
        if (appState.LayoutMode == layoutMode)
        {
            ApplyAppStateToPresentation();
            return;
        }

        appState = appState with { LayoutMode = layoutMode, LayoutNodes = null };
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Layout mode changed to {FormatLayoutMode(layoutMode)}");
        await RelayoutAsync(currentScene);
    }

    public async Task SetGroupingModeAsync(GraphGroupingMode groupingMode)
    {
        if (appState.GroupingMode == groupingMode)
        {
            ApplyAppStateToPresentation();
            return;
        }

        var viewState = Scene.CaptureViewState();
        CaptureLayoutState(Scene);
        appState = appState with { GroupingMode = groupingMode };
        ApplyAppStateToPresentation();
        var nextScene = CreateScene(currentDocuments, currentSemanticGraph, previousLayout);
        nextScene.ApplyViewState(viewState);
        Scene = nextScene;
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Grouping changed to {FormatGroupingMode(groupingMode)}");
    }

    public async Task SetReviewRequestStateAsync(GitReviewRequestState reviewRequestState)
    {
        if (appState.ReviewRequestState == reviewRequestState)
        {
            ApplyAppStateToPresentation();
            return;
        }

        appState = appState with { ReviewRequestState = reviewRequestState };
        ApplyAppStateToPresentation();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"Review request list changed to {FormatReviewRequestState(reviewRequestState)}");

        if (!string.IsNullOrWhiteSpace(currentRepositoryPath) && Directory.Exists(currentRepositoryPath))
        {
            var requestId = repositoryLoadRequests.BeginRequest();
            await RefreshRepositoryReferencesAsync(currentRepositoryPath, requestId, CancellationToken.None);
        }
    }

    public async Task SetVisualizationLayerAsync(string layer, bool isEnabled)
    {
        var visibility = appState.EffectiveAnnotationVisibility;
        var nextVisibility = layer switch
        {
            "Git" => visibility with { ShowGitStatus = isEnabled },
            "Semantic" => visibility with { ShowSemantic = isEnabled },
            "Diagnostics" => visibility with { ShowDiagnostics = isEnabled },
            "Review" => visibility with { ShowReview = isEnabled },
            "ReviewComments" => visibility with { ShowReviewComments = isEnabled },
            "History" => visibility with { ShowHistory = isEnabled },
            "Navigation" => visibility with { ShowNavigation = isEnabled },
            "Context" => visibility with { ShowContext = isEnabled },
            _ => visibility
        };

        if (nextVisibility == visibility)
        {
            ApplyAppStateToPresentation();
            return;
        }

        appState = appState with { AnnotationVisibility = nextVisibility };
        ApplyAppStateToPresentation();
        RefreshSceneAnnotations();
        await SaveOptionsAsync(CancellationToken.None);
        AddDiagnostic("Info", $"{layer} visualization {(isEnabled ? "shown" : "hidden")}");
    }

    public async Task SetLeftPaneWidthAsync(double width)
    {
        var normalizedWidth = NormalizeLeftPaneWidth(width);
        LeftPaneWidth = normalizedWidth;
        if (Math.Abs(appState.LeftPaneWidth - normalizedWidth) < 0.5)
        {
            return;
        }

        appState = appState with { LeftPaneWidth = normalizedWidth };
        await SaveOptionsAsync(CancellationToken.None);
    }
}
