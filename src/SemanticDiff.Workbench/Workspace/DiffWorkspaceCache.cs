using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Rendering;
using SemanticDiff.Semantics;

namespace SemanticDiff.Workbench.Workspace;

public sealed record DiffWorkspaceCacheEntry(
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

public sealed record DiffWorkspaceCacheKeyOptions(
    DiffContextMode DiffContextMode,
    DiffReviewMode ReviewMode,
    bool CollapseUnchangedContext,
    SemanticAnalysisMode SemanticAnalysisMode,
    GraphLayoutMode LayoutMode,
    GraphGroupingMode GroupingMode,
    bool ShowSemanticEdges);

public sealed class DiffWorkspaceCache
{
    private readonly Dictionary<string, DiffWorkspaceCacheEntry> entries = new(StringComparer.Ordinal);
    private readonly Queue<string> insertionOrder = new();

    public DiffWorkspaceCache(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Cache capacity must be positive.");
        }

        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count => entries.Count;

    public string StatusText => Count == 0 ? "Cache empty" : $"{Count:N0} cached views";

    public static bool IsCacheable(GitDiffRequest request) =>
        request.Scope == GitDiffScope.Branch && !IsCurrentHeadReference(request.HeadRef);

    public static string CreateKey(string repositoryPath, GitDiffRequest request, DiffWorkspaceCacheKeyOptions options)
    {
        var normalizedRepositoryPath = Path.GetFullPath(repositoryPath);
        return string.Join('\u001f',
            normalizedRepositoryPath,
            request.Scope.ToString(),
            NormalizeRef(request.BaseRef) ?? string.Empty,
            NormalizeRef(request.HeadRef) ?? string.Empty,
            options.DiffContextMode.ToString(),
            options.ReviewMode.ToString(),
            options.CollapseUnchangedContext ? "fold" : "full",
            options.SemanticAnalysisMode.ToString(),
            options.LayoutMode.ToString(),
            options.GroupingMode.ToString(),
            options.ShowSemanticEdges ? "edges" : "no-edges");
    }

    public bool TryGet(string repositoryPath, GitDiffRequest request, DiffWorkspaceCacheKeyOptions options, out DiffWorkspaceCacheEntry entry)
    {
        if (!IsCacheable(request))
        {
            entry = null!;
            return false;
        }

        return entries.TryGetValue(CreateKey(repositoryPath, request, options), out entry!);
    }

    public DiffWorkspaceCacheEntry Store(DiffWorkspaceCacheEntry entry)
    {
        entries[entry.CacheKey] = entry;
        if (!insertionOrder.Contains(entry.CacheKey, StringComparer.Ordinal))
        {
            insertionOrder.Enqueue(entry.CacheKey);
        }

        Trim(entry.CacheKey);
        return entry;
    }

    public DiffWorkspaceCacheEntry Store(
        string repositoryPath,
        GitDiffRequest request,
        DiffWorkspaceCacheKeyOptions options,
        ImmutableArray<DiffDocumentSnapshot> documents,
        SemanticGraph semanticGraph,
        DiffCanvasScene scene,
        GraphLayoutResult? previousLayout,
        ImmutableHashSet<DiffDocumentId> pinnedDocumentIds,
        GitDiffSnapshot? gitSnapshot,
        string statusPrefix,
        string? selectedDocumentId)
    {
        var key = CreateKey(repositoryPath, request, options);
        return Store(new DiffWorkspaceCacheEntry(
            key,
            repositoryPath,
            documents,
            semanticGraph,
            scene,
            previousLayout,
            pinnedDocumentIds,
            gitSnapshot,
            statusPrefix,
            selectedDocumentId,
            DateTimeOffset.UtcNow));
    }

    private void Trim(string currentKey)
    {
        while (insertionOrder.Count > Capacity)
        {
            var staleKey = insertionOrder.Dequeue();
            if (!string.Equals(staleKey, currentKey, StringComparison.Ordinal))
            {
                entries.Remove(staleKey);
            }
        }
    }

    private static bool IsCurrentHeadReference(string? reference) =>
        string.IsNullOrWhiteSpace(reference) || string.Equals(reference.Trim(), "HEAD", StringComparison.Ordinal);

    private static string? NormalizeRef(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
