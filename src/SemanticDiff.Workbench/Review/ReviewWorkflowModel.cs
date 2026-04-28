using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Workbench.Review;

public sealed record ReviewWorkflowView<TThread>(
    ImmutableArray<TThread> Items,
    string CountText,
    string StatusText,
    TThread? SelectedItem)
    where TThread : class;

public sealed class ReviewWorkflowModel<TThread>
    where TThread : class
{
    private readonly ReviewThreadFilter<TThread> threadFilter;
    private readonly Func<TThread, string> getId;

    public ReviewWorkflowModel(Func<TThread, string> getId, Func<TThread, string> getSearchText)
    {
        this.getId = getId;
        threadFilter = new ReviewThreadFilter<TThread>(getId, getSearchText);
    }

    public ImmutableArray<TThread> ThreadItems { get; private set; } = [];

    public ImmutableArray<GitReviewThreadInfo> Threads { get; private set; } = [];

    public string StatusText { get; private set; } = "Select a PR or MR";

    public void Clear(string status)
    {
        ThreadItems = [];
        Threads = [];
        StatusText = status;
    }

    public void BeginLoad(string status)
    {
        ThreadItems = [];
        Threads = [];
        StatusText = status;
    }

    public void SetThreads(
        ImmutableArray<GitReviewThreadInfo> threads,
        ImmutableArray<TThread> threadItems,
        string status)
    {
        Threads = threads.IsDefault ? ImmutableArray<GitReviewThreadInfo>.Empty : threads;
        ThreadItems = threadItems.IsDefault ? ImmutableArray<TThread>.Empty : threadItems;
        StatusText = status;
    }

    public void Restore(
        ImmutableArray<TThread> threadItems,
        ImmutableArray<GitReviewThreadInfo> threads,
        string status)
    {
        ThreadItems = threadItems.IsDefault ? ImmutableArray<TThread>.Empty : threadItems;
        Threads = threads.IsDefault ? ImmutableArray<GitReviewThreadInfo>.Empty : threads;
        StatusText = status;
    }

    public ReviewWorkflowView<TThread> ApplyFilter(string query, TThread? selectedItem)
    {
        var result = threadFilter.Apply(ThreadItems, query, selectedItem);
        return new ReviewWorkflowView<TThread>(result.Items, result.CountText, StatusText, result.SelectedItem);
    }

    public TThread? FindThread(string id) =>
        ThreadItems.FirstOrDefault(item => string.Equals(getId(item), id, StringComparison.Ordinal));
}
