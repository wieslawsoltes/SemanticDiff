using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Rendering;
using SemanticDiff.Semantics;

namespace SemanticDiff.App.ViewModels;

internal sealed record DiffViewCacheEntry(
    string CacheKey,
    string RepositoryPath,
    ImmutableArray<DiffDocumentSnapshot> Documents,
    SemanticGraph SemanticGraph,
    DiffCanvasScene Scene,
    GraphLayoutResult? PreviousLayout,
    ImmutableHashSet<DiffDocumentId> PinnedDocumentIds,
    GitDiffSnapshot? GitSnapshot,
    string StatusPrefix,
    string? SelectedDocumentId,
    DateTimeOffset CreatedAt);