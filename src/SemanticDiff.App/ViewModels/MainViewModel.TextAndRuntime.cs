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
    private async Task<string> LoadFullFileTextAsync(DiffDocumentSnapshot document, CancellationToken cancellationToken)
    {
        if (currentGitSnapshot is null || string.IsNullOrWhiteSpace(currentRepositoryPath))
        {
            return document.ToSourceText();
        }

        var fileChange = currentGitSnapshot.Files.FirstOrDefault(file =>
            string.Equals(NormalizeRepositoryPath(file.Path), NormalizeRepositoryPath(document.Metadata.Path), StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(file.OldPath) &&
                string.Equals(NormalizeRepositoryPath(file.OldPath), NormalizeRepositoryPath(document.Metadata.Path), StringComparison.OrdinalIgnoreCase)));
        if (fileChange is null)
        {
            return document.ToSourceText();
        }

        try
        {
            var content = await new GitDiffService().GetFileContentAsync(currentGitSnapshot.Request, fileChange, cancellationToken);
            return string.IsNullOrEmpty(content) ? document.ToSourceText() : content;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AddDiagnostic("Warning", $"Full file load failed: {exception.Message}");
            return document.ToSourceText();
        }
    }

    private static async Task<DiffDocumentSnapshot> CreateTokenizedFullFileDocumentAsync(
        DiffDocumentSnapshot sourceDocument,
        string fullText,
        CancellationToken cancellationToken)
    {
        const int tokenPageSize = 128;
        var metadata = sourceDocument.Metadata with
        {
            AddedLines = 0,
            DeletedLines = 0
        };
        var document = new DiffDocumentFactory().CreateFromText(metadata, fullText, DiffLineKind.Context);
        var tokenizer = new TextMateDocumentTokenizer(tokenPageSize);
        var lineBuilder = ImmutableArray.CreateBuilder<DiffLine>(document.LineCount);

        for (var firstLineIndex = 0; firstLineIndex < document.LineCount; firstLineIndex += tokenPageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokenizedLines = await tokenizer.TokenizePageAsync(document, firstLineIndex, tokenPageSize, cancellationToken).ConfigureAwait(false);
            lineBuilder.AddRange(tokenizedLines);
        }

        return document with { Lines = lineBuilder.ToImmutable() };
    }

    private DiffCanvasScene CreateScene(ImmutableArray<DiffDocumentSnapshot> documents, SemanticGraph semanticGraph, GraphLayoutResult? layout) =>
        DiffCanvasScene.FromDocuments(
            documents,
            semanticGraph,
            layout,
            CreateEdgeOptions(),
            CreateAnnotations(documents, semanticGraph),
            appState.EffectiveAnnotationVisibility,
            appState.GroupingMode);

    private void RefreshSceneAnnotations()
    {
        if (currentDocuments.IsDefaultOrEmpty)
        {
            return;
        }

        Scene = Scene.WithAnnotations(CreateAnnotations(currentDocuments, currentSemanticGraph), appState.EffectiveAnnotationVisibility);
    }

    private ImmutableArray<DiffAnnotation> CreateAnnotations(ImmutableArray<DiffDocumentSnapshot> documents, SemanticGraph semanticGraph)
    {
        if (documents.IsDefaultOrEmpty)
        {
            return ImmutableArray<DiffAnnotation>.Empty;
        }

        var context = CreateAnnotationContext();
        var request = new DiffAnnotationRequest(documents, semanticGraph, context, reviewWorkflow.Threads);
        return annotationProviders
            .SelectMany(provider => provider.CreateAnnotations(request))
            .GroupBy(annotation => annotation.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToImmutableArray();
    }

    private ImmutableDictionary<string, string> CreateAnnotationContext()
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        builder[DiffAnnotationContextKeys.DiffScope] = DiffScopeText;
        builder[DiffAnnotationContextKeys.DiffContextMode] = DiffContextModeText;
        builder[DiffAnnotationContextKeys.ReviewMode] = ReviewModeText;
        builder[DiffAnnotationContextKeys.ReferenceRange] = FormatReferenceText(appState);
        builder[DiffAnnotationContextKeys.WatchStatus] = WatchStatusText;

        if (selectedExplorerItem is not null)
        {
            builder[DiffAnnotationContextKeys.SelectedDocumentId] = selectedExplorerItem.DocumentId;
            builder[DiffAnnotationContextKeys.BlameSummary] = BlameSummaryText;
            builder[DiffAnnotationContextKeys.ReviewActionStatus] = ReviewActionStatusText;
        }

        if (currentChangeNavigationIndex >= 0 && currentChangeNavigationIndex < changeNavigationItems.Length)
        {
            var item = changeNavigationItems[currentChangeNavigationIndex];
            builder[DiffAnnotationContextKeys.CurrentChangeDocumentId] = item.DocumentId.Value;
            builder[DiffAnnotationContextKeys.CurrentChangeLineIndex] = item.LineIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            builder[DiffAnnotationContextKeys.CurrentChangeText] = ChangeNavigationText;
        }

        return builder.ToImmutable();
    }

    private CancellationTokenSource BeginOperation(string message)
    {
        currentOperation?.Cancel();
        currentSemanticRefinementOperation?.Cancel();
        var operation = new CancellationTokenSource();
        currentOperation = operation;
        IsBusy = true;
        ProgressValue = 0;
        ProgressText = message;
        AddDiagnostic("Info", message);
        return operation;
    }

    private CancellationTokenSource BeginSemanticRefinementOperation()
    {
        var operation = new CancellationTokenSource();
        var previousOperation = Interlocked.Exchange(ref currentSemanticRefinementOperation, operation);
        previousOperation?.Cancel();
        return operation;
    }

    private bool IsCurrentOperation(CancellationTokenSource operation) => ReferenceEquals(currentOperation, operation);

    private bool IsCurrentRepositoryRequest(long repositoryRequestId) => repositoryLoadRequests.IsCurrent(repositoryRequestId);

    private void EnsureCurrentRepositoryRequest(long repositoryRequestId, CancellationToken cancellationToken) =>
        repositoryLoadRequests.ThrowIfStale(repositoryRequestId, cancellationToken);

    private void CompleteOperation(CancellationTokenSource operation, string message)
    {
        if (ReferenceEquals(currentOperation, operation))
        {
            IsBusy = false;
            ProgressValue = 1;
            ProgressText = message;
            currentOperation = null;
        }

        operation.Dispose();
    }

    private void ReportProgress(double value, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProgressValue = Math.Clamp(value, 0, 1);
        ProgressText = message;
    }

    private void AddDiagnostic(string level, string message)
    {
        var item = new DiagnosticItemViewModel(DateTimeOffset.Now.ToString("HH:mm:ss"), level, message);
        Diagnostics = Diagnostics.Insert(0, item).Take(8).ToImmutableArray();
        DiagnosticsCountText = FormatCount(Diagnostics.Length, "diagnostic", "diagnostics");
        LatestDiagnosticText = item.DisplayText;
    }
}
