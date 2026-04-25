using System.Collections.Immutable;

namespace SemanticDiff.Core;

public enum DiffAnnotationKind
{
    GitStatus,
    ReferenceRange,
    Syntax,
    SemanticAnchor,
    ParserDiagnostic,
    ReviewNoise,
    MovedCode,
    InlineChange,
    Impact,
    Conflict,
    ContextFold,
    Navigation,
    HistoryBlame,
    ReviewAction,
    RepositoryWatch
}

public enum DiffAnnotationTarget
{
    Node,
    Line
}

public enum DiffAnnotationSeverity
{
    Hint,
    Info,
    Warning,
    Error
}

public sealed record DiffAnnotation(
    string Id,
    DiffDocumentId DocumentId,
    DiffAnnotationKind Kind,
    DiffAnnotationTarget Target,
    int? LineIndex,
    int? DisplayLineNumber,
    string Label,
    string Detail,
    DiffAnnotationSeverity Severity = DiffAnnotationSeverity.Info)
{
    public static DiffAnnotation Node(
        DiffDocumentId documentId,
        DiffAnnotationKind kind,
        string label,
        string detail,
        DiffAnnotationSeverity severity = DiffAnnotationSeverity.Info) =>
        new($"{documentId}:node:{kind}:{label}", documentId, kind, DiffAnnotationTarget.Node, null, null, label, detail, severity);

    public static DiffAnnotation Line(
        DiffDocumentId documentId,
        DiffAnnotationKind kind,
        int lineIndex,
        int? displayLineNumber,
        string label,
        string detail,
        DiffAnnotationSeverity severity = DiffAnnotationSeverity.Info) =>
        new($"{documentId}:line:{lineIndex}:{kind}:{label}", documentId, kind, DiffAnnotationTarget.Line, lineIndex, displayLineNumber, label, detail, severity);
}

public sealed record DiffAnnotationVisibilityState(
    bool ShowGitStatus = true,
    bool ShowSemantic = true,
    bool ShowDiagnostics = true,
    bool ShowReview = true,
    bool ShowHistory = true,
    bool ShowNavigation = true,
    bool ShowContext = true)
{
    public static DiffAnnotationVisibilityState Default { get; } = new();

    public int EnabledLayerCount => new[]
    {
        ShowGitStatus,
        ShowSemantic,
        ShowDiagnostics,
        ShowReview,
        ShowHistory,
        ShowNavigation,
        ShowContext
    }.Count(isEnabled => isEnabled);

    public bool IsVisible(DiffAnnotationKind kind) => kind switch
    {
        DiffAnnotationKind.GitStatus or DiffAnnotationKind.ReferenceRange or DiffAnnotationKind.RepositoryWatch => ShowGitStatus,
        DiffAnnotationKind.Syntax or DiffAnnotationKind.SemanticAnchor or DiffAnnotationKind.Impact => ShowSemantic,
        DiffAnnotationKind.ParserDiagnostic => ShowDiagnostics,
        DiffAnnotationKind.ReviewNoise or DiffAnnotationKind.MovedCode or DiffAnnotationKind.InlineChange or DiffAnnotationKind.Conflict or DiffAnnotationKind.ReviewAction => ShowReview,
        DiffAnnotationKind.HistoryBlame => ShowHistory,
        DiffAnnotationKind.Navigation => ShowNavigation,
        DiffAnnotationKind.ContextFold => ShowContext,
        _ => true
    };
}

public static class DiffAnnotationContextKeys
{
    public const string DiffScope = nameof(DiffScope);
    public const string DiffContextMode = nameof(DiffContextMode);
    public const string ReviewMode = nameof(ReviewMode);
    public const string ReferenceRange = nameof(ReferenceRange);
    public const string WatchStatus = nameof(WatchStatus);
    public const string SelectedDocumentId = nameof(SelectedDocumentId);
    public const string BlameSummary = nameof(BlameSummary);
    public const string ReviewActionStatus = nameof(ReviewActionStatus);
    public const string CurrentChangeDocumentId = nameof(CurrentChangeDocumentId);
    public const string CurrentChangeLineIndex = nameof(CurrentChangeLineIndex);
    public const string CurrentChangeText = nameof(CurrentChangeText);
}

public sealed record DiffAnnotationRequest(
    ImmutableArray<DiffDocumentSnapshot> Documents,
    SemanticGraph SemanticGraph,
    ImmutableDictionary<string, string> Context)
{
    public string? GetContext(string key) => Context.TryGetValue(key, out var value) ? value : null;
}

public interface IDiffAnnotationProvider
{
    string Id { get; }

    ImmutableArray<DiffAnnotation> CreateAnnotations(DiffAnnotationRequest request);
}