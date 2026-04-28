using System.Collections.Immutable;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SemanticDiff.Core;
using SemanticDiff.Git;
using SemanticDiff.Layout;
using SemanticDiff.Semantics;
using Windows.UI;

namespace SemanticDiff.App.ViewModels;

public sealed record ExplorerItemViewModel(string Path, DiffFileStatus Status, string Language)
{
    public string DocumentId => Path;

    public string DisplayName => $"{StatusText}  {Path}";

    public string SearchText => $"{StatusText} {Path} {FileName} {FolderPath} {Language}";

    public string FileName => System.IO.Path.GetFileName(Path);

    public string FolderPath
    {
        get
        {
            var folderPath = System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? string.Empty;
            return string.IsNullOrWhiteSpace(folderPath) ? "/" : folderPath;
        }
    }

    public string StatusText => Status switch
    {
        DiffFileStatus.Added => "A",
        DiffFileStatus.Deleted => "D",
        DiffFileStatus.Renamed => "R",
        DiffFileStatus.Untracked => "?",
        DiffFileStatus.Conflicted => "!",
        _ => "M"
    };
}

public sealed record LayoutModeOptionViewModel(GraphLayoutMode Mode, string DisplayName)
{
    public static ImmutableArray<LayoutModeOptionViewModel> All { get; } =
    [
        new(GraphLayoutMode.Auto, "Auto"),
        new(GraphLayoutMode.Layered, "Layered"),
        new(GraphLayoutMode.Grid, "Grid"),
        new(GraphLayoutMode.CompactGrid, "Compact grid"),
        new(GraphLayoutMode.StatusLanes, "Status lanes")
    ];

    public override string ToString() => DisplayName;
}

public sealed record GroupingModeOptionViewModel(GraphGroupingMode Mode, string DisplayName)
{
    public static ImmutableArray<GroupingModeOptionViewModel> All { get; } =
    [
        new(GraphGroupingMode.None, "None"),
        new(GraphGroupingMode.Folder, "Folders"),
        new(GraphGroupingMode.Semantic, "Semantic"),
        new(GraphGroupingMode.Language, "Language"),
        new(GraphGroupingMode.Status, "Status")
    ];

    public override string ToString() => DisplayName;
}

public sealed record ReviewRequestStateOptionViewModel(GitReviewRequestState State, string DisplayName, string Description)
{
    public static ImmutableArray<ReviewRequestStateOptionViewModel> All { get; } =
    [
        new(GitReviewRequestState.Open, "Open", "Open PRs/MRs"),
        new(GitReviewRequestState.Closed, "Closed", "Closed without merge"),
        new(GitReviewRequestState.Merged, "Merged", "Merged review requests"),
        new(GitReviewRequestState.All, "All", "Open, closed, and merged")
    ];

    public override string ToString() => DisplayName;
}

public sealed record SemanticNavigationItemViewModel(
    string AnchorId,
    string DocumentId,
    string Path,
    string KindText,
    string DisplayName,
    int Line,
    int IncidentEdgeCount,
    bool IsChanged,
    bool IsLinked)
{
    public string LocationText => $"{Path}:{Line}";

    public string EdgeText => IncidentEdgeCount == 1 ? "1 link" : $"{IncidentEdgeCount:N0} links";

    public string SignalText => (IsChanged, IsLinked) switch
    {
        (true, true) => "changed + linked",
        (true, false) => "changed",
        (false, true) => "linked",
        _ => string.Empty
    };

    public Visibility SignalVisibility => string.IsNullOrWhiteSpace(SignalText) ? Visibility.Collapsed : Visibility.Visible;

    public static SemanticNavigationItemViewModel FromItem(SemanticNavigationItem item) => new(
        item.AnchorId,
        item.DocumentId.Value,
        item.Path,
        item.KindText,
        item.DisplayName,
        item.Line,
        item.IncidentEdgeCount,
        item.IsChanged,
        item.IsLinked);
}

public sealed record SymbolScopeFilterViewModel(string FilterKey, string DisplayName, int Count, bool IsSelected)
{
    public const string AllKey = "All";
    public const string ChangedKey = "Changed";
    public const string LinkedKey = "Linked";

    public string DisplayText => $"{DisplayName} {Count:N0}";

    public SolidColorBrush Background => IsSelected ? SymbolInsightBrushes.SelectedBackground : SymbolInsightBrushes.Transparent;

    public SolidColorBrush Border => IsSelected ? SymbolInsightBrushes.Accent : SymbolInsightBrushes.SubtleBorder;

    public SolidColorBrush Foreground => IsSelected ? SymbolInsightBrushes.SelectedForeground : SymbolInsightBrushes.Secondary;
}

public sealed record SemanticSymbolKindFacetViewModel(
    string KindKey,
    string KindText,
    int Count,
    int ChangedCount,
    int LinkedCount,
    bool IsSelected)
{
    public string DisplayText => $"{KindText} {Count:N0}";

    public string DetailText => $"{ChangedCount:N0} changed | {LinkedCount:N0} linked";

    public SolidColorBrush Background => IsSelected ? SymbolInsightBrushes.SelectedBackground : SymbolInsightBrushes.Transparent;

    public SolidColorBrush Border => IsSelected ? SymbolInsightBrushes.Accent : SymbolInsightBrushes.SubtleBorder;

    public SolidColorBrush Foreground => IsSelected ? SymbolInsightBrushes.SelectedForeground : SymbolInsightBrushes.Primary;

    public static SemanticSymbolKindFacetViewModel FromFacet(SemanticSymbolKindFacet facet, bool isSelected) => new(
        facet.KindText,
        facet.KindText,
        facet.Count,
        facet.ChangedCount,
        facet.LinkedCount,
        isSelected);
}

public sealed record SemanticSymbolDocumentFacetViewModel(
    string DocumentId,
    string Path,
    string FileName,
    int Count,
    int ChangedCount,
    int LinkedCount,
    bool IsSelected)
{
    public string DetailText => $"{Count:N0} symbols | {ChangedCount:N0} changed | {LinkedCount:N0} linked";

    public SolidColorBrush Background => IsSelected ? SymbolInsightBrushes.SelectedBackground : SymbolInsightBrushes.Transparent;

    public SolidColorBrush Border => IsSelected ? SymbolInsightBrushes.Accent : SymbolInsightBrushes.SubtleBorder;

    public SolidColorBrush Foreground => IsSelected ? SymbolInsightBrushes.SelectedForeground : SymbolInsightBrushes.Primary;

    public static SemanticSymbolDocumentFacetViewModel FromFacet(SemanticSymbolDocumentFacet facet, bool isSelected)
    {
        var fileName = System.IO.Path.GetFileName(facet.Path);
        return new SemanticSymbolDocumentFacetViewModel(
            facet.DocumentId.Value,
            facet.Path,
            string.IsNullOrWhiteSpace(fileName) ? facet.Path : fileName,
            facet.Count,
            facet.ChangedCount,
            facet.LinkedCount,
            isSelected);
    }
}

internal static class SymbolInsightBrushes
{
    public static SolidColorBrush Transparent { get; } = new(Color.FromArgb(0, 0, 0, 0));

    public static SolidColorBrush SelectedBackground { get; } = new(Color.FromArgb(38, 0, 122, 204));

    public static SolidColorBrush Accent { get; } = new(Color.FromArgb(255, 0, 122, 204));

    public static SolidColorBrush SubtleBorder { get; } = new(Color.FromArgb(255, 154, 166, 180));

    public static SolidColorBrush SelectedForeground { get; } = new(Color.FromArgb(255, 0, 92, 150));

    public static SolidColorBrush Primary { get; } = new(Color.FromArgb(255, 20, 32, 51));

    public static SolidColorBrush Secondary { get; } = new(Color.FromArgb(255, 82, 97, 114));
}

public sealed record FocusRequest(string DocumentId, int? Line);

public sealed record DiagnosticItemViewModel(string TimeText, string Level, string Message)
{
    public string DisplayText => $"{TimeText}  {Level}  {Message}";
}
