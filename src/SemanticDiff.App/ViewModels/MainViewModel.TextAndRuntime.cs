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
using SemanticDiff.Workbench.FileDiff;
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

    private static async Task<DiffDocumentSnapshot> CreateFullFileDocumentAsync(
        DiffDocumentSnapshot sourceDocument,
        string fullText,
        bool enableTokenization,
        CancellationToken cancellationToken)
    {
        const int tokenPageSize = 128;
        var document = new DiffDocumentFactory().CreateFromText(sourceDocument.Metadata, fullText, DiffLineKind.Context);
        if (!enableTokenization || ShouldDeferFullFileTextMateTokenization(document))
        {
            return document;
        }

        var tokenizer = new AdaptiveDocumentTokenizer(tokenPageSize);
        var lineBuilder = ImmutableArray.CreateBuilder<DiffLine>(document.LineCount);

        for (var firstLineIndex = 0; firstLineIndex < document.LineCount; firstLineIndex += tokenPageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokenizedLines = await tokenizer.TokenizePageAsync(document, firstLineIndex, tokenPageSize, cancellationToken).ConfigureAwait(false);
            lineBuilder.AddRange(tokenizedLines);
        }

        return document with { Lines = lineBuilder.ToImmutable() };
    }

    private static bool ShouldDeferFullFileTextMateTokenization(DiffDocumentSnapshot document)
    {
        const int lineThreshold = 2_000;
        const int characterThreshold = 250_000;

        if (!IsCSharpDocument(document))
        {
            return false;
        }

        if (document.LineCount >= lineThreshold)
        {
            return true;
        }

        var characterCount = 0L;
        foreach (var line in document.Lines)
        {
            characterCount += line.Text.Length;
            if (characterCount >= characterThreshold)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCSharpDocument(DiffDocumentSnapshot document)
    {
        var extension = System.IO.Path.GetExtension(document.Metadata.Path).TrimStart('.');
        return string.Equals(extension, "cs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "csx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(document.Metadata.Language, "C#", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(document.Metadata.Language, "csharp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(document.Metadata.Language, "cs", StringComparison.OrdinalIgnoreCase);
    }

    private static ImmutableArray<CodeFoldRegion> CreateFoldRegions(DiffDocumentSnapshot document, CancellationToken cancellationToken = default)
    {
        if (RoslynCSharpCodeFoldingService.CanFold(document))
        {
            return new RoslynCSharpCodeFoldingService().CreateFoldRegions(document, cancellationToken);
        }

        return new CodeFoldingService().CreateFoldRegions(document);
    }

    private DiffCanvasScene CreateScene(ImmutableArray<DiffDocumentSnapshot> documents, SemanticGraph semanticGraph, GraphLayoutResult? layout) =>
        CreateScene(documents, semanticGraph, layout, reviewWorkflow.Threads);

    private DiffCanvasScene CreateScene(
        ImmutableArray<DiffDocumentSnapshot> documents,
        SemanticGraph semanticGraph,
        GraphLayoutResult? layout,
        ImmutableArray<GitReviewThreadInfo> reviewThreads)
    {
        var scene = DiffCanvasScene.FromDocuments(
            documents,
            semanticGraph,
            layout,
            CreateEdgeOptions(),
            CreateAnnotations(documents, semanticGraph, reviewThreads),
            appState.EffectiveAnnotationVisibility,
            appState.GroupingMode);
        scene.SetShowFullFileNodes(IsFullCodeWorkspaceEnabled);
        scene.SetNodeEditingEnabled(IsNodeEditingWorkspaceEnabled);
        return scene;
    }

    public async Task SetFullCodeWorkspaceAsync(bool enabled)
    {
        if (enabled)
        {
            await EnsureSceneFullFileDocumentsAsync(Scene, null, CancellationToken.None);
        }

        IsFullCodeWorkspaceEnabled = enabled;
        Scene.SetShowFullFileNodes(enabled);
        OnPropertyChanged(nameof(Scene));
        AddDiagnostic("Info", enabled ? "Workspace nodes now show full file code" : "Workspace nodes now show diff hunks");
    }

    public async Task SetNodeEditingWorkspaceAsync(bool enabled)
    {
        if (enabled && !IsFullCodeWorkspaceEnabled)
        {
            await EnsureSceneFullFileDocumentsAsync(Scene, null, CancellationToken.None);
            IsFullCodeWorkspaceEnabled = true;
            Scene.SetShowFullFileNodes(true);
        }

        IsNodeEditingWorkspaceEnabled = enabled;
        Scene.SetNodeEditingEnabled(enabled);
        OnPropertyChanged(nameof(Scene));
        AddDiagnostic("Info", enabled ? "Node editing enabled for full-code nodes" : "Node editing disabled");
    }

    public async Task ToggleNodeFullFileViewAsync(string documentId)
    {
        await ToggleNodeFullFileViewAsync(documentId, Scene);
    }

    public async Task ToggleNodeFullFileViewAsync(string documentId, DiffCanvasScene? targetScene)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        var scene = targetScene ?? Scene;
        await EnsureSceneFullFileDocumentsAsync(scene, documentId, CancellationToken.None);
        if (scene.ToggleNodeFullFileView(documentId) && ReferenceEquals(scene, Scene))
        {
            OnPropertyChanged(nameof(Scene));
        }
    }

    public void ClearNodeFullFileViewOverride(string documentId)
    {
        ClearNodeFullFileViewOverride(documentId, Scene);
    }

    public void ClearNodeFullFileViewOverride(string documentId, DiffCanvasScene? targetScene)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        var scene = targetScene ?? Scene;
        if (scene.ClearNodeFullFileViewOverride(documentId) && ReferenceEquals(scene, Scene))
        {
            OnPropertyChanged(nameof(Scene));
        }
    }

    public async Task ToggleNodeEditingAsync(string documentId)
    {
        await ToggleNodeEditingAsync(documentId, Scene);
    }

    public async Task ToggleNodeEditingAsync(string documentId, DiffCanvasScene? targetScene)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        var scene = targetScene ?? Scene;
        await EnsureSceneFullFileDocumentsAsync(scene, documentId, CancellationToken.None);
        if (scene.ToggleNodeEditing(documentId) && ReferenceEquals(scene, Scene))
        {
            OnPropertyChanged(nameof(Scene));
        }
    }

    public void ClearNodeEditingOverride(string documentId)
    {
        ClearNodeEditingOverride(documentId, Scene);
    }

    public void ClearNodeEditingOverride(string documentId, DiffCanvasScene? targetScene)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        var scene = targetScene ?? Scene;
        if (scene.ClearNodeEditingOverride(documentId) && ReferenceEquals(scene, Scene))
        {
            OnPropertyChanged(nameof(Scene));
        }
    }

    private async Task EnsureSceneFullFileDocumentsAsync(DiffCanvasScene targetScene, string? documentId, CancellationToken cancellationToken)
    {
        var missingNodes = targetScene.Nodes
            .Where(node =>
                (documentId is null || string.Equals(node.DiffDocument.Id.Value, documentId, StringComparison.Ordinal)) &&
                !node.HasFullFileDocument)
            .ToArray();
        if (missingNodes.Length == 0)
        {
            return;
        }

        var operation = BeginBackgroundOperation(
            documentId is null ? "Loading full-code node content" : "Loading node full file content",
            cancellationToken,
            drivesGlobalProgress: true);
        var builder = ImmutableArray.CreateBuilder<DiffNodeFullFileContent>(missingNodes.Length);
        var fileDiffDocumentBuilder = new FileDiffDocumentBuilder();
        try
        {
            for (var index = 0; index < missingNodes.Length; index++)
            {
                var node = missingNodes[index];
                operation.Token.ThrowIfCancellationRequested();
                ReportProgress(
                    operation,
                    0.1 + (double)index / missingNodes.Length * 0.8,
                    $"Loading full-code node {index + 1:N0}/{missingNodes.Length:N0}: {ShortenPath(node.DiffDocument.Metadata.Path)}");
                var fullText = await LoadFileContentByPathAsync(node.DiffDocument.Metadata.Path, operation.Token)
                    ?? await LoadFullFileTextAsync(node.DiffDocument, operation.Token);
                var fullDocument = await CreateFullFileDocumentAsync(node.DiffDocument, fullText, appState.EnableTokenization, operation.Token);
                var foldRegions = CreateFoldRegions(fullDocument, operation.Token);
                var fileView = fileDiffDocumentBuilder.Build(node.DiffDocument, fullDocument, fullText, foldRegions);
                var annotatedFullDocument = fullDocument with { Lines = fileView.AnnotatedFullFileLines };
                builder.Add(new DiffNodeFullFileContent(node.DiffDocument.Id, annotatedFullDocument, fileView.FoldRegions, fileView.FullText));
            }

            targetScene.SetFullFileDocuments(builder.ToImmutable());
            if (ReferenceEquals(targetScene, Scene))
            {
                OnPropertyChanged(nameof(Scene));
            }

            CompleteOperation(operation, "Full-code node content ready");
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(operation, "Full-code node load canceled");
            throw;
        }
        catch
        {
            CompleteOperation(operation, "Full-code node load failed");
            throw;
        }
    }

    private void RefreshSceneAnnotations()
    {
        if (currentDocuments.IsDefaultOrEmpty)
        {
            return;
        }

        Scene = Scene.WithAnnotations(CreateAnnotations(currentDocuments, currentSemanticGraph), appState.EffectiveAnnotationVisibility);
    }

    private ImmutableArray<DiffAnnotation> CreateAnnotations(ImmutableArray<DiffDocumentSnapshot> documents, SemanticGraph semanticGraph) =>
        CreateAnnotations(documents, semanticGraph, reviewWorkflow.Threads);

    private ImmutableArray<DiffAnnotation> CreateAnnotations(
        ImmutableArray<DiffDocumentSnapshot> documents,
        SemanticGraph semanticGraph,
        ImmutableArray<GitReviewThreadInfo> reviewThreads)
    {
        if (documents.IsDefaultOrEmpty)
        {
            return ImmutableArray<DiffAnnotation>.Empty;
        }

        var context = CreateAnnotationContext();
        var request = new DiffAnnotationRequest(documents, semanticGraph, context, reviewThreads);
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

    private readonly Dictionary<CancellationTokenSource, LoadingOperationRegistration> loadingOperations = new();

    private CancellationTokenSource BeginOperation(string message)
    {
        currentOperation?.Cancel();
        currentSemanticRefinementOperation?.Cancel();
        var operation = BeginLoadingOperation(
            message,
            LoadingOperationKind.Foreground,
            tab: null,
            externalCancellationToken: default,
            drivesGlobalProgress: true,
            logDiagnostic: true);
        currentOperation = operation;
        IsBusy = true;
        return operation;
    }

    private CancellationTokenSource BeginBackgroundOperation(
        string message,
        CancellationToken externalCancellationToken = default,
        bool drivesGlobalProgress = true,
        bool logDiagnostic = false) =>
        BeginLoadingOperation(
            message,
            LoadingOperationKind.Background,
            tab: null,
            externalCancellationToken,
            drivesGlobalProgress,
            logDiagnostic);

    private CancellationTokenSource BeginTabOperation(
        WorkspaceTabViewModel tab,
        string message,
        CancellationToken externalCancellationToken = default,
        bool drivesGlobalProgress = true,
        bool logDiagnostic = false) =>
        BeginLoadingOperation(
            message,
            LoadingOperationKind.Tab,
            tab,
            externalCancellationToken,
            drivesGlobalProgress,
            logDiagnostic);

    private CancellationTokenSource BeginLoadingOperation(
        string message,
        LoadingOperationKind kind,
        WorkspaceTabViewModel? tab,
        CancellationToken externalCancellationToken,
        bool drivesGlobalProgress,
        bool logDiagnostic)
    {
        var operation = externalCancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken)
            : new CancellationTokenSource();
        var item = new LoadingOperationViewModel(
            Guid.NewGuid().ToString("N"),
            kind,
            message,
            drivesGlobalProgress);
        loadingOperations[operation] = new LoadingOperationRegistration(item, tab);
        LoadingOperations.Add(item);
        if (tab is not null)
        {
            tab.IsLoading = true;
            tab.IsLoadingIndeterminate = true;
            tab.LoadingProgress = 0;
            tab.StatusText = message;
        }

        if (drivesGlobalProgress || kind == LoadingOperationKind.Foreground)
        {
            ProgressValue = 0;
            ProgressText = message;
        }

        if (logDiagnostic)
        {
            AddDiagnostic("Info", message);
        }

        return operation;
    }

    private CancellationTokenSource BeginSemanticRefinementOperation()
    {
        var operation = BeginBackgroundOperation(
            "Refining MSBuild semantics",
            drivesGlobalProgress: true,
            logDiagnostic: false);
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
        if (synchronizationContext is not null && SynchronizationContext.Current != synchronizationContext)
        {
            synchronizationContext.Post(_ => CompleteOperation(operation, message), null);
            return;
        }

        if (loadingOperations.Remove(operation, out var registration))
        {
            registration.Item.Report(1, message);
            LoadingOperations.Remove(registration.Item);
            if (registration.Tab is { } tab &&
                !loadingOperations.Any(pair => ReferenceEquals(pair.Value.Tab, tab)))
            {
                tab.IsLoading = false;
                tab.IsLoadingIndeterminate = false;
                tab.LoadingProgress = 1;
                tab.StatusText = message;
            }
        }

        if (ReferenceEquals(currentOperation, operation))
        {
            currentOperation = null;
        }

        if (ReferenceEquals(currentSemanticRefinementOperation, operation))
        {
            currentSemanticRefinementOperation = null;
        }

        if (ReferenceEquals(currentWorkspaceExplorerOperation, operation))
        {
            currentWorkspaceExplorerOperation = null;
        }

        UpdateLoadingPresentation(message);
        operation.Dispose();
    }

    private void ReportProgress(double value, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (currentOperation is { } operation && CanReportOperation(operation))
        {
            ReportProgress(operation, value, message);
            return;
        }

        ProgressValue = Math.Clamp(value, 0, 1);
        ProgressText = message;
    }

    private void ReportProgress(CancellationTokenSource operation, double value, string message)
    {
        if (synchronizationContext is not null && SynchronizationContext.Current != synchronizationContext)
        {
            synchronizationContext.Post(_ => ReportProgress(operation, value, message), null);
            return;
        }

        if (!loadingOperations.TryGetValue(operation, out var registration))
        {
            return;
        }

        if (!CanReportOperation(operation))
        {
            return;
        }

        registration.Item.Report(value, message);
        if (registration.Tab is { } tab)
        {
            tab.IsLoading = true;
            tab.IsLoadingIndeterminate = false;
            tab.LoadingProgress = Math.Clamp(value, 0, 1);
            tab.StatusText = message;
        }

        if (registration.Item.DrivesGlobalProgress || ReferenceEquals(currentOperation, operation))
        {
            ProgressValue = Math.Clamp(value, 0, 1);
            ProgressText = registration.Item.SummaryText;
        }
    }

    private void ReportIndeterminate(CancellationTokenSource operation, string message)
    {
        if (synchronizationContext is not null && SynchronizationContext.Current != synchronizationContext)
        {
            synchronizationContext.Post(_ => ReportIndeterminate(operation, message), null);
            return;
        }

        if (!loadingOperations.TryGetValue(operation, out var registration))
        {
            return;
        }

        if (!CanReportOperation(operation))
        {
            return;
        }

        registration.Item.ReportIndeterminate(message);
        if (registration.Tab is { } tab)
        {
            tab.IsLoading = true;
            tab.IsLoadingIndeterminate = true;
            tab.StatusText = message;
        }

        if (registration.Item.DrivesGlobalProgress || ReferenceEquals(currentOperation, operation))
        {
            ProgressText = message;
        }
    }

    private static bool CanReportOperation(CancellationTokenSource operation)
    {
        try
        {
            return !operation.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void UpdateLoadingPresentation(string fallbackMessage)
    {
        var foregroundRegistration = currentOperation is not null &&
            loadingOperations.TryGetValue(currentOperation, out var foreground)
                ? foreground
                : null;
        if (foregroundRegistration is not null)
        {
            IsBusy = true;
            ProgressValue = foregroundRegistration.Item.Progress;
            ProgressText = foregroundRegistration.Item.SummaryText;
            return;
        }

        IsBusy = false;
        var active = loadingOperations.Values
            .Where(registration => registration.Item.DrivesGlobalProgress)
            .OrderByDescending(registration => registration.Item.StartedAt)
            .FirstOrDefault();
        if (active is not null)
        {
            ProgressValue = active.Item.IsIndeterminate ? 0 : active.Item.Progress;
            ProgressText = active.Item.SummaryText;
            return;
        }

        ProgressValue = 1;
        ProgressText = fallbackMessage;
    }

    private void CancelLoadingOperations()
    {
        foreach (var operation in loadingOperations.Keys.ToArray())
        {
            operation.Cancel();
        }
    }

    private void ClearLoadingOperations()
    {
        foreach (var operation in loadingOperations.Keys.ToArray())
        {
            operation.Dispose();
        }

        loadingOperations.Clear();
        LoadingOperations.Clear();
    }

    private sealed record LoadingOperationRegistration(
        LoadingOperationViewModel Item,
        WorkspaceTabViewModel? Tab);

    private void AddDiagnostic(string level, string message)
    {
        if (synchronizationContext is not null && SynchronizationContext.Current != synchronizationContext)
        {
            synchronizationContext.Post(_ => AddDiagnostic(level, message), null);
            return;
        }

        var item = new DiagnosticItemViewModel(DateTimeOffset.Now.ToString("HH:mm:ss"), level, message);
        Diagnostics = Diagnostics.Insert(0, item).Take(8).ToImmutableArray();
        DiagnosticsCountText = FormatCount(Diagnostics.Length, "diagnostic", "diagnostics");
        LatestDiagnosticText = item.DisplayText;
    }
}
