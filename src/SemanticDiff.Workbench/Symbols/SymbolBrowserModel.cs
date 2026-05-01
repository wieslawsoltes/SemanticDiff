using System.Collections.Immutable;
using SemanticDiff.Semantics;

namespace SemanticDiff.Workbench.Symbols;

public sealed record SymbolBrowserSelection(string ScopeKey, string KindKey, string DocumentId)
{
    public const string AllKey = "All";
    public const string ChangedKey = "Changed";
    public const string LinkedKey = "Linked";

    public static SymbolBrowserSelection All { get; } = new(AllKey, AllKey, AllKey);

    public bool HasActiveFilters(string query) =>
        !string.IsNullOrWhiteSpace(query) ||
        !string.Equals(ScopeKey, AllKey, StringComparison.Ordinal) ||
        !string.Equals(KindKey, AllKey, StringComparison.Ordinal) ||
        !string.Equals(DocumentId, AllKey, StringComparison.Ordinal);
}

public sealed record SymbolBrowserView(
    ImmutableArray<SemanticNavigationItem> Items,
    SemanticSymbolInsightSummary Insight,
    SymbolBrowserSelection Selection,
    string CountText,
    string FilterStatusText);

public sealed class SymbolBrowserModel
{
    private ImmutableArray<SemanticNavigationItem> allItems = [];
    private SemanticSymbolInsightSummary insight = SemanticSymbolInsightSummary.Empty;

    public SymbolBrowserSelection Selection { get; private set; } = SymbolBrowserSelection.All;

    public ImmutableArray<SemanticNavigationItem> AllItems => allItems;

    public SemanticSymbolInsightSummary Insight => insight;

    public void SetItems(ImmutableArray<SemanticNavigationItem> items, SemanticSymbolInsightSummary? precomputedInsight = null)
    {
        allItems = items.IsDefault ? ImmutableArray<SemanticNavigationItem>.Empty : items;
        insight = precomputedInsight ?? new SemanticSymbolInsightIndex().Build(allItems);
        PruneSelection();
    }

    public void SetScope(string? scopeKey)
    {
        Selection = Selection with { ScopeKey = string.IsNullOrWhiteSpace(scopeKey) ? SymbolBrowserSelection.AllKey : scopeKey };
    }

    public void ToggleKind(string? kindKey)
    {
        var next = string.IsNullOrWhiteSpace(kindKey) ? SymbolBrowserSelection.AllKey : kindKey;
        Selection = Selection with
        {
            KindKey = string.Equals(Selection.KindKey, next, StringComparison.OrdinalIgnoreCase)
                ? SymbolBrowserSelection.AllKey
                : next
        };
    }

    public void ToggleDocument(string? documentId)
    {
        var next = string.IsNullOrWhiteSpace(documentId) ? SymbolBrowserSelection.AllKey : documentId;
        Selection = Selection with
        {
            DocumentId = string.Equals(Selection.DocumentId, next, StringComparison.Ordinal)
                ? SymbolBrowserSelection.AllKey
                : next
        };
    }

    public void Clear()
    {
        Selection = SymbolBrowserSelection.All;
    }

    public SymbolBrowserView Apply(string query)
    {
        var trimmedQuery = query.Trim();
        var filtered = allItems
            .Where(MatchesSelectedScope)
            .Where(MatchesSelectedKind)
            .Where(MatchesSelectedDocument)
            .Where(item => string.IsNullOrWhiteSpace(trimmedQuery) || item.SearchText.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();
        var countText = !Selection.HasActiveFilters(trimmedQuery)
            ? FormatCount(allItems.Length, "symbol", "symbols")
            : $"{filtered.Length:N0}/{allItems.Length:N0} symbols";
        return new SymbolBrowserView(filtered, insight, Selection, countText, BuildFilterStatus(filtered.Length, trimmedQuery));
    }

    private void PruneSelection()
    {
        if (!string.Equals(Selection.KindKey, SymbolBrowserSelection.AllKey, StringComparison.Ordinal) &&
            !allItems.Any(item => string.Equals(item.KindText, Selection.KindKey, StringComparison.OrdinalIgnoreCase)))
        {
            Selection = Selection with { KindKey = SymbolBrowserSelection.AllKey };
        }

        if (!string.Equals(Selection.DocumentId, SymbolBrowserSelection.AllKey, StringComparison.Ordinal) &&
            !allItems.Any(item => string.Equals(item.DocumentId.Value, Selection.DocumentId, StringComparison.Ordinal)))
        {
            Selection = Selection with { DocumentId = SymbolBrowserSelection.AllKey };
        }
    }

    private bool MatchesSelectedScope(SemanticNavigationItem item) => Selection.ScopeKey switch
    {
        SymbolBrowserSelection.ChangedKey => item.IsChanged,
        SymbolBrowserSelection.LinkedKey => item.IsLinked,
        _ => true
    };

    private bool MatchesSelectedKind(SemanticNavigationItem item) =>
        string.Equals(Selection.KindKey, SymbolBrowserSelection.AllKey, StringComparison.Ordinal) ||
        string.Equals(item.KindText, Selection.KindKey, StringComparison.OrdinalIgnoreCase);

    private bool MatchesSelectedDocument(SemanticNavigationItem item) =>
        string.Equals(Selection.DocumentId, SymbolBrowserSelection.AllKey, StringComparison.Ordinal) ||
        string.Equals(item.DocumentId.Value, Selection.DocumentId, StringComparison.Ordinal);

    private string BuildFilterStatus(int filteredCount, string query)
    {
        var parts = new List<string>();
        if (!string.Equals(Selection.ScopeKey, SymbolBrowserSelection.AllKey, StringComparison.Ordinal))
        {
            parts.Add(Selection.ScopeKey);
        }

        if (!string.Equals(Selection.KindKey, SymbolBrowserSelection.AllKey, StringComparison.Ordinal))
        {
            parts.Add(Selection.KindKey);
        }

        if (!string.Equals(Selection.DocumentId, SymbolBrowserSelection.AllKey, StringComparison.Ordinal))
        {
            var documentFacet = insight.DocumentFacets.FirstOrDefault(facet => string.Equals(facet.DocumentId.Value, Selection.DocumentId, StringComparison.Ordinal));
            parts.Add(string.IsNullOrWhiteSpace(documentFacet?.Path) ? Selection.DocumentId : ShortenPath(documentFacet.Path));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            parts.Add($"search \"{query}\"");
        }

        return parts.Count == 0
            ? "Showing all semantic symbols"
            : $"Showing {filteredCount:N0} symbols filtered by {string.Join(", ", parts)}";
    }

    private static string ShortenPath(string path) => path.Length <= 72 ? path : $"...{path[^69..]}";

    private static string FormatCount(int count, string singular, string plural) => $"{count:N0} {(count == 1 ? singular : plural)}";
}
