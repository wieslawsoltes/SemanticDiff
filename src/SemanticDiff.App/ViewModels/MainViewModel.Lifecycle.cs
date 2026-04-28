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
    public void ReportInteractionError(string message) => AddDiagnostic("Error", message);

    public void ReportInteractionInfo(string message) => AddDiagnostic("Info", message);

    public async ValueTask DisposeAsync()
    {
        currentOperation?.Cancel();
        currentSemanticRefinementOperation?.Cancel();
        currentBlameOperation?.Cancel();
        pendingAutoReload?.Cancel();
        await StopRepositoryWatcherAsync();
        currentOperation?.Dispose();
        currentSemanticRefinementOperation?.Dispose();
        currentBlameOperation?.Dispose();
        currentOperation = null;
        currentSemanticRefinementOperation = null;
        currentBlameOperation = null;
    }

    public void CancelCurrentOperation()
    {
        currentOperation?.Cancel();
        currentSemanticRefinementOperation?.Cancel();
        AddDiagnostic("Info", "Cancel requested");
    }
}
