using System.Collections.Immutable;

namespace SemanticDiff.Workbench.Review;

public sealed record ReviewThreadFilterResult<TThread>(ImmutableArray<TThread> Items, string CountText, TThread? SelectedItem)
    where TThread : class;

public sealed class ReviewThreadFilter<TThread>
    where TThread : class
{
    private readonly Func<TThread, string> getId;
    private readonly Func<TThread, string> getSearchText;

    public ReviewThreadFilter(Func<TThread, string> getId, Func<TThread, string> getSearchText)
    {
        this.getId = getId;
        this.getSearchText = getSearchText;
    }

    public ReviewThreadFilterResult<TThread> Apply(ImmutableArray<TThread> allItems, string query, TThread? selectedItem)
    {
        var trimmedQuery = query.Trim();
        var filtered = string.IsNullOrWhiteSpace(trimmedQuery)
            ? allItems
            : allItems.Where(item => getSearchText(item).Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
        var nextSelected = selectedItem is not null && filtered.Any(item => string.Equals(getId(item), getId(selectedItem), StringComparison.Ordinal))
            ? selectedItem
            : filtered.FirstOrDefault();
        var countText = string.IsNullOrWhiteSpace(trimmedQuery)
            ? FormatThreadCount(allItems.Length)
            : $"{filtered.Length:N0}/{allItems.Length:N0} threads";
        return new ReviewThreadFilterResult<TThread>(filtered, countText, nextSelected);
    }

    private static string FormatThreadCount(int count) => $"{count:N0} {(count == 1 ? "thread" : "threads")}";
}
