using System.Collections.Immutable;

namespace SemanticDiff.Core;

public readonly record struct TextRange(int Start, int Length, int Line, int Column);

public enum SemanticAnchorKind
{
    File,
    Namespace,
    Type,
    Member,
    XamlRoot,
    XamlName,
    Resource,
    Binding,
    Project,
    Unknown
}

public enum SemanticEdgeKind
{
    SymbolReference,
    TypeInheritance,
    PartialClass,
    XamlClass,
    Binding,
    Resource,
    ProjectReference,
    GeneratedFile,
    RenameOrMove,
    Contains
}

public sealed record SemanticAnchor(
    string Id,
    DiffDocumentId DocumentId,
    TextRange Range,
    SemanticAnchorKind Kind,
    string DisplayName);

public sealed record SemanticEdge(
    string Id,
    string SourceAnchorId,
    string TargetAnchorId,
    SemanticEdgeKind Kind,
    double Confidence,
    string? Label);

public sealed record SemanticGraph(
    ImmutableArray<SemanticAnchor> Anchors,
    ImmutableArray<SemanticEdge> Edges)
{
    public static SemanticGraph Empty { get; } = new(ImmutableArray<SemanticAnchor>.Empty, ImmutableArray<SemanticEdge>.Empty);
}

public sealed record SemanticGraphFilter(
    ImmutableHashSet<SemanticEdgeKind>? IncludedEdgeKinds = null,
    double MinimumConfidence = 0,
    ImmutableHashSet<DiffDocumentId>? FocusDocuments = null);

public sealed record SemanticAnalysisRequest(
    string RepositoryPath,
    GitDiffSnapshot? GitSnapshot,
    ImmutableArray<DiffDocumentSnapshot> Documents,
    SemanticAnalysisMode AnalysisMode = SemanticAnalysisMode.WorkspaceThenSyntax);

public enum SemanticAnalysisMode
{
    WorkspaceThenSyntax,
    FastSyntaxOnly
}

public sealed record DiffNodeLayout(DiffDocumentId DocumentId, Rect2 Bounds, bool IsPinned = false, double FontSize = 12.5);

public enum GraphLayoutMode
{
    Auto,
    Layered,
    Grid,
    CompactGrid,
    StatusLanes
}

public enum GraphGroupingMode
{
    None,
    Folder,
    Semantic,
    Language,
    Status
}

public sealed record GraphLayoutRequest(
    ImmutableArray<DiffDocumentSnapshot> Documents,
    SemanticGraph SemanticGraph,
    Size2 DefaultNodeSize,
    ImmutableArray<DiffNodeLayout> PreviousNodes = default,
    ImmutableHashSet<DiffDocumentId>? PinnedDocumentIds = null,
    GraphLayoutMode LayoutMode = GraphLayoutMode.Layered);

public sealed record GraphLayoutResult(ImmutableArray<DiffNodeLayout> Nodes);