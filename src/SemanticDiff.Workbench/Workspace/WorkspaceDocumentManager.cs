namespace SemanticDiff.Workbench.Workspace;

public sealed class WorkspaceDocumentManager<TTab>
    where TTab : class
{
    private readonly IList<TTab> tabs;
    private readonly Func<TTab, string> getId;
    private readonly Func<TTab, bool> canClose;

    public WorkspaceDocumentManager(IList<TTab> tabs, Func<TTab, string> getId, Func<TTab, bool> canClose)
    {
        this.tabs = tabs;
        this.getId = getId;
        this.canClose = canClose;
    }

    public TTab? Find(string id) => tabs.FirstOrDefault(tab => string.Equals(getId(tab), id, StringComparison.Ordinal));

    public bool TrySelect(string id, Action<TTab> select)
    {
        var existing = Find(id);
        if (existing is null)
        {
            return false;
        }

        select(existing);
        return true;
    }

    public void AddAndSelect(TTab tab, Action<TTab> select)
    {
        tabs.Add(tab);
        select(tab);
    }

    public TTab Close(TTab? tab, TTab fallback, Action<TTab> select)
    {
        if (tab is null || !canClose(tab))
        {
            select(fallback);
            return fallback;
        }

        var removedIndex = tabs.IndexOf(tab);
        if (removedIndex >= 0)
        {
            tabs.RemoveAt(removedIndex);
        }

        if (tabs.Count == 0)
        {
            tabs.Add(fallback);
            select(fallback);
            return fallback;
        }

        var nextIndex = Math.Clamp(removedIndex, 0, tabs.Count - 1);
        var next = tabs[nextIndex];
        select(next);
        return next;
    }
}
