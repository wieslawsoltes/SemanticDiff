using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SemanticDiff.Core;
using SemanticDiff.Rendering;
using SemanticDiff.Semantics;
using Windows.Foundation;
using Windows.UI;

namespace SemanticDiff.App.ViewModels;

public enum WorkspaceTabKind
{
    Graph,
    GitHistory,
    FileDiff,
    Blame,
    SymbolGraph
}

public enum FileDiffDisplayMode
{
    DiffOnly,
    FullFile
}

public enum FileDiffScopeMode
{
    Changes,
    FullFileDiff
}

public enum BlameDisplayMode
{
    CommitTimeline,
    ChangeGraph
}

public enum SymbolGraphViewMode
{
    SymbolsOnly,
    FilesAndSymbols
}

public sealed partial class WorkspaceTabViewModel : ObservableObject
{
    private WorkspaceTabViewModel(
        string id,
        WorkspaceTabKind kind,
        string header,
        string detailText,
        string iconGlyph,
        bool isClosable)
    {
        Id = id;
        Kind = kind;
        Header = header;
        DetailText = detailText;
        IconGlyph = iconGlyph;
        IsClosable = isClosable;
    }

    public string Id { get; }

    public WorkspaceTabKind Kind { get; }

    public string Header { get; }

    public string DetailText { get; }

    public string IconGlyph { get; }

    public bool IsClosable { get; }

    public Visibility IconVisibility => Kind is WorkspaceTabKind.Graph or WorkspaceTabKind.GitHistory or WorkspaceTabKind.Blame or WorkspaceTabKind.SymbolGraph
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CloseButtonVisibility => IsClosable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GraphVisibility => Kind == WorkspaceTabKind.Graph ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HistoryVisibility => Kind == WorkspaceTabKind.GitHistory ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FileDiffVisibility => Kind == WorkspaceTabKind.FileDiff ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BlameVisibility => Kind == WorkspaceTabKind.Blame ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SymbolGraphVisibility => Kind == WorkspaceTabKind.SymbolGraph ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingVisibility))]
    private bool isLoading;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private GitHistoryTimelineViewModel? history;

    [ObservableProperty]
    private FileDiffTabViewModel? fileDiff;

    [ObservableProperty]
    private BlameTabViewModel? blame;

    [ObservableProperty]
    private SymbolGraphTabViewModel? symbolGraph;

    [ObservableProperty]
    private GitDiffRequest? graphRequest;

    [ObservableProperty]
    private string? graphBranchReferenceName;

    [ObservableProperty]
    private GitPullRequestInfo? graphReviewRequest;

    [ObservableProperty]
    private GraphWorkspaceState? graphState;

    [ObservableProperty]
    private bool isLightTheme;

    public static WorkspaceTabViewModel Graph() => new(
        "graph",
        WorkspaceTabKind.Graph,
        "Diff Graph",
        "Semantic node canvas",
        "\uECA5",
        isClosable: false);

    public static WorkspaceTabViewModel CreateGraphWorkspace(
        string id,
        string header,
        string detailText,
        GitDiffRequest request,
        string? branchReferenceName,
        GitPullRequestInfo? reviewRequest) => new(
            id,
            WorkspaceTabKind.Graph,
            header,
            detailText,
            "\uECA5",
            isClosable: true)
        {
            GraphRequest = request,
            GraphBranchReferenceName = branchReferenceName,
            GraphReviewRequest = reviewRequest,
            StatusText = "Loading workspace"
        };

    public static WorkspaceTabViewModel CreateHistory(string id, string header, string detailText) => new(
        id,
        WorkspaceTabKind.GitHistory,
        header,
        detailText,
        "\uE81C",
        isClosable: true);

    public static WorkspaceTabViewModel CreateFileDiff(string id, string header, string detailText, FileDiffTabViewModel fileDiff) => new(
        id,
        WorkspaceTabKind.FileDiff,
        header,
        detailText,
        "\uE8A5",
        isClosable: true)
    {
        FileDiff = fileDiff,
        StatusText = fileDiff.StatusText
    };

    public static WorkspaceTabViewModel CreateBlame(string id, string header, string detailText, BlameTabViewModel blame) => new(
        id,
        WorkspaceTabKind.Blame,
        header,
        detailText,
        "\uE946",
        isClosable: true)
    {
        Blame = blame,
        StatusText = blame.StatusText
    };

    public static WorkspaceTabViewModel CreateSymbolGraph(string id, string header, string detailText, SymbolGraphTabViewModel symbolGraph) => new(
        id,
        WorkspaceTabKind.SymbolGraph,
        header,
        detailText,
        "\uE8D2",
        isClosable: true)
    {
        SymbolGraph = symbolGraph,
        StatusText = symbolGraph.StatusText
    };
}

public sealed partial class SymbolGraphTabViewModel : ObservableObject
{
    private const int MaxRenderedSymbols = 240;
    private const string AllKey = "All";
    private const string ChangedKey = "Changed";
    private const string LinkedKey = "Linked";
    private const string UnlinkedKey = "Unlinked";

    private readonly ImmutableArray<SemanticNavigationItem> sourceItems;
    private readonly SemanticGraph sourceGraph;
    private readonly ImmutableArray<DiffDocumentSnapshot> sourceDocuments;
    private readonly string? focusAnchorId;
    private bool isRefreshing;

    public SymbolGraphTabViewModel(
        string title,
        string description,
        ImmutableArray<SemanticNavigationItem> sourceItems,
        SemanticGraph sourceGraph,
        ImmutableArray<DiffDocumentSnapshot> sourceDocuments,
        string initialSearchText = "",
        string initialScopeKey = AllKey,
        string initialKindKey = AllKey,
        string initialDocumentId = AllKey,
        GraphLayoutMode initialLayoutMode = GraphLayoutMode.Layered,
        GraphGroupingMode initialGroupingMode = GraphGroupingMode.Semantic,
        SymbolGraphViewMode initialViewMode = SymbolGraphViewMode.SymbolsOnly,
        string? focusAnchorId = null)
    {
        Title = title;
        Description = description;
        this.sourceItems = sourceItems.IsDefault ? [] : sourceItems;
        this.sourceGraph = sourceGraph;
        this.sourceDocuments = sourceDocuments.IsDefault ? [] : sourceDocuments;
        this.focusAnchorId = focusAnchorId;

        ScopeOptions =
        [
            new SymbolGraphFilterOptionViewModel(AllKey, "All", "All symbols"),
            new SymbolGraphFilterOptionViewModel(ChangedKey, "Changed", "Symbols touched by the diff"),
            new SymbolGraphFilterOptionViewModel(LinkedKey, "Linked", "Symbols with semantic edges"),
            new SymbolGraphFilterOptionViewModel(UnlinkedKey, "Unlinked", "Symbols without semantic edges")
        ];
        KindOptions = CreateKindOptions(this.sourceItems);
        DocumentOptions = CreateDocumentOptions(this.sourceItems);
        EdgeKindOptions = CreateEdgeKindOptions(this.sourceGraph);
        LayoutOptions = LayoutModeOptionViewModel.All;
        GroupingOptions = GroupingModeOptionViewModel.All;
        ViewModeOptions = SymbolGraphViewModeOptionViewModel.All;

        searchText = initialSearchText;
        selectedScopeOption = ScopeOptions.FirstOrDefault(option => string.Equals(option.Key, initialScopeKey, StringComparison.OrdinalIgnoreCase)) ?? ScopeOptions[0];
        selectedKindOption = KindOptions.FirstOrDefault(option => string.Equals(option.Key, initialKindKey, StringComparison.OrdinalIgnoreCase)) ?? KindOptions[0];
        selectedDocumentOption = DocumentOptions.FirstOrDefault(option => string.Equals(option.Key, initialDocumentId, StringComparison.Ordinal)) ?? DocumentOptions[0];
        selectedEdgeKindOption = EdgeKindOptions[0];
        selectedLayoutOption = LayoutOptions.FirstOrDefault(option => option.Mode == initialLayoutMode) ?? LayoutOptions[1];
        selectedGroupingOption = GroupingOptions.FirstOrDefault(option => option.Mode == initialGroupingMode) ?? GroupingOptions[2];
        selectedViewModeOption = ViewModeOptions.FirstOrDefault(option => option.Mode == initialViewMode) ?? ViewModeOptions[0];

        RefreshScene();
    }

    public string Title { get; }

    public string Description { get; }

    public ImmutableArray<SymbolGraphFilterOptionViewModel> ScopeOptions { get; }

    public ImmutableArray<SymbolGraphFilterOptionViewModel> KindOptions { get; }

    public ImmutableArray<SymbolGraphFilterOptionViewModel> DocumentOptions { get; }

    public ImmutableArray<SymbolGraphEdgeKindOptionViewModel> EdgeKindOptions { get; }

    public ImmutableArray<LayoutModeOptionViewModel> LayoutOptions { get; }

    public ImmutableArray<GroupingModeOptionViewModel> GroupingOptions { get; }

    public ImmutableArray<SymbolGraphViewModeOptionViewModel> ViewModeOptions { get; }

    public Visibility EmptyVisibility => RenderedSymbolCount == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ContentVisibility => RenderedSymbolCount == 0 ? Visibility.Collapsed : Visibility.Visible;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private SymbolGraphFilterOptionViewModel selectedScopeOption = new(AllKey, "All", "All symbols");

    [ObservableProperty]
    private SymbolGraphFilterOptionViewModel selectedKindOption = new(AllKey, "All kinds", "All symbol kinds");

    [ObservableProperty]
    private SymbolGraphFilterOptionViewModel selectedDocumentOption = new(AllKey, "All files", "All files");

    [ObservableProperty]
    private SymbolGraphEdgeKindOptionViewModel selectedEdgeKindOption = new(null, AllKey, "All edges", "All semantic edges");

    [ObservableProperty]
    private LayoutModeOptionViewModel selectedLayoutOption = LayoutModeOptionViewModel.All[1];

    [ObservableProperty]
    private GroupingModeOptionViewModel selectedGroupingOption = GroupingModeOptionViewModel.All[2];

    [ObservableProperty]
    private SymbolGraphViewModeOptionViewModel selectedViewModeOption = SymbolGraphViewModeOptionViewModel.All[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    [NotifyPropertyChangedFor(nameof(ContentVisibility))]
    private int renderedSymbolCount;

    [ObservableProperty]
    private int renderedFileCount;

    [ObservableProperty]
    private int filteredSymbolCount;

    [ObservableProperty]
    private int renderedEdgeCount;

    [ObservableProperty]
    private string summaryText = "No symbols";

    [ObservableProperty]
    private string filterStatusText = "Showing all symbols";

    [ObservableProperty]
    private string statusText = "Symbol graph ready";

    [ObservableProperty]
    private DiffCanvasScene scene = DiffCanvasScene.FromDocuments([]);

    partial void OnSearchTextChanged(string value) => RefreshScene();

    partial void OnSelectedScopeOptionChanged(SymbolGraphFilterOptionViewModel value) => RefreshScene();

    partial void OnSelectedKindOptionChanged(SymbolGraphFilterOptionViewModel value) => RefreshScene();

    partial void OnSelectedDocumentOptionChanged(SymbolGraphFilterOptionViewModel value) => RefreshScene();

    partial void OnSelectedEdgeKindOptionChanged(SymbolGraphEdgeKindOptionViewModel value) => RefreshScene();

    partial void OnSelectedLayoutOptionChanged(LayoutModeOptionViewModel value) => RefreshScene();

    partial void OnSelectedGroupingOptionChanged(GroupingModeOptionViewModel value) => RefreshScene();

    partial void OnSelectedViewModeOptionChanged(SymbolGraphViewModeOptionViewModel value) => RefreshScene();

    public void ResetFilters()
    {
        SearchText = string.Empty;
        SelectedScopeOption = ScopeOptions[0];
        SelectedKindOption = KindOptions[0];
        SelectedDocumentOption = DocumentOptions[0];
        SelectedEdgeKindOption = EdgeKindOptions[0];
        RefreshScene();
    }

    private void RefreshScene()
    {
        if (isRefreshing)
        {
            return;
        }

        isRefreshing = true;
        try
        {
            var query = SearchText.Trim();
            var edgeKindAnchorIds = CreateEdgeKindAnchorSet(SelectedEdgeKindOption.Kind);
            var filtered = sourceItems
                .Where(item => MatchesScope(item, SelectedScopeOption.Key))
                .Where(item => MatchesOption(item.KindText, SelectedKindOption.Key, ignoreCase: true))
                .Where(item => MatchesOption(item.DocumentId.Value, SelectedDocumentOption.Key, ignoreCase: false))
                .Where(item => edgeKindAnchorIds is null || edgeKindAnchorIds.Contains(item.AnchorId))
                .Where(item => string.IsNullOrWhiteSpace(query) || item.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray();
            var selected = filtered
                .OrderByDescending(item => string.Equals(item.AnchorId, focusAnchorId, StringComparison.Ordinal))
                .ThenByDescending(item => item.IsChanged)
                .ThenByDescending(item => item.IsLinked)
                .ThenByDescending(item => item.IncidentEdgeCount)
                .ThenBy(item => item.KindText, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Line)
                .Take(MaxRenderedSymbols)
                .ToImmutableArray();

            FilteredSymbolCount = filtered.Length;
            var sceneResult = BuildScene(selected);
            Scene = sceneResult.Scene;
            RenderedSymbolCount = selected.Length;
            RenderedFileCount = sceneResult.FileCount;
            RenderedEdgeCount = Scene.Edges.Count;
            SummaryText = BuildSummary(filtered.Length, selected.Length, RenderedFileCount, RenderedEdgeCount, SelectedViewModeOption.Mode);
            FilterStatusText = BuildFilterStatus(filtered.Length, selected.Length, query);
            StatusText = $"{SummaryText} | {SelectedLayoutOption.DisplayName} | {SelectedGroupingOption.DisplayName}";
        }
        finally
        {
            isRefreshing = false;
        }
    }

    private SymbolGraphSceneResult BuildScene(ImmutableArray<SemanticNavigationItem> items) =>
        SelectedViewModeOption.Mode == SymbolGraphViewMode.FilesAndSymbols
            ? BuildFileSymbolScene(items)
            : BuildSymbolOnlyScene(items);

    private SymbolGraphSceneResult BuildSymbolOnlyScene(ImmutableArray<SemanticNavigationItem> items)
    {
        if (items.IsDefaultOrEmpty)
        {
            return new SymbolGraphSceneResult(DiffCanvasScene.FromDocuments([]), 0);
        }

        var sourceDocumentsById = sourceDocuments.ToDictionary(document => document.Id.Value, StringComparer.Ordinal);
        var selectedAnchorIds = items.Select(item => item.AnchorId).ToHashSet(StringComparer.Ordinal);
        var documentIdsByAnchorId = new Dictionary<string, DiffDocumentId>(StringComparer.Ordinal);
        var documents = ImmutableArray.CreateBuilder<DiffDocumentSnapshot>(items.Length);
        var anchors = ImmutableArray.CreateBuilder<SemanticAnchor>(items.Length);

        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            sourceDocumentsById.TryGetValue(item.DocumentId.Value, out var sourceDocument);
            var documentId = SymbolGraphDocumentIds.Create(item.DocumentId, item.AnchorId);
            documentIdsByAnchorId[item.AnchorId] = documentId;
            documents.Add(CreateSymbolDocument(documentId, item, sourceDocument, index));
            anchors.Add(new SemanticAnchor(item.AnchorId, documentId, new TextRange(0, 1, Math.Max(1, item.Line), 1), item.Kind, item.DisplayName));
        }

        var selectedEdgeKind = SelectedEdgeKindOption.Kind;
        var edges = sourceGraph.Edges
            .Where(edge => selectedAnchorIds.Contains(edge.SourceAnchorId) && selectedAnchorIds.Contains(edge.TargetAnchorId))
            .Where(edge => selectedEdgeKind is null || edge.Kind == selectedEdgeKind)
            .ToImmutableArray();
        var symbolGraph = new SemanticGraph(anchors.ToImmutable(), edges);
        var layout = CreateLayout(documents.ToImmutable(), items, edges, SelectedLayoutOption.Mode);
        var scene = DiffCanvasScene.FromDocuments(
            documents.ToImmutable(),
            symbolGraph,
            layout,
            new EdgeProjectionOptions(MinimumConfidence: 0, MaxEdgesPerDocumentPair: 4),
            groupingMode: SelectedGroupingOption.Mode);
        return new SymbolGraphSceneResult(scene, 0);
    }

    private SymbolGraphSceneResult BuildFileSymbolScene(ImmutableArray<SemanticNavigationItem> items)
    {
        if (items.IsDefaultOrEmpty)
        {
            return new SymbolGraphSceneResult(DiffCanvasScene.FromDocuments([]), 0);
        }

        var sourceDocumentsById = sourceDocuments.ToDictionary(document => document.Id.Value, StringComparer.Ordinal);
        var selectedAnchorIds = items.Select(item => item.AnchorId).ToHashSet(StringComparer.Ordinal);
        var selectedDocuments = items
            .Select(item => item.DocumentId.Value)
            .Distinct(StringComparer.Ordinal)
            .Select(documentId => sourceDocumentsById.TryGetValue(documentId, out var document) ? document : null)
            .OfType<DiffDocumentSnapshot>()
            .OrderBy(document => document.Metadata.Path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var documents = ImmutableArray.CreateBuilder<DiffDocumentSnapshot>(selectedDocuments.Length + items.Length);
        documents.AddRange(selectedDocuments);

        var anchors = ImmutableArray.CreateBuilder<SemanticAnchor>(selectedDocuments.Length + items.Length);
        var fileAnchorIdsByDocumentId = new Dictionary<DiffDocumentId, string>();
        foreach (var document in selectedDocuments)
        {
            var anchorId = SymbolGraphDocumentIds.CreateFileAnchorId(document.Id);
            fileAnchorIdsByDocumentId[document.Id] = anchorId;
            anchors.Add(new SemanticAnchor(
                anchorId,
                document.Id,
                new TextRange(0, 1, 1, 1),
                SemanticAnchorKind.File,
                Path.GetFileName(document.Metadata.Path)));
        }

        var symbolDocumentIdsByAnchorId = new Dictionary<string, DiffDocumentId>(StringComparer.Ordinal);
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            sourceDocumentsById.TryGetValue(item.DocumentId.Value, out var sourceDocument);
            var documentId = SymbolGraphDocumentIds.Create(item.DocumentId, item.AnchorId);
            symbolDocumentIdsByAnchorId[item.AnchorId] = documentId;
            documents.Add(CreateSymbolDocument(documentId, item, sourceDocument, index));
            anchors.Add(new SemanticAnchor(item.AnchorId, documentId, new TextRange(0, 1, Math.Max(1, item.Line), 1), item.Kind, item.DisplayName));
        }

        var selectedEdgeKind = SelectedEdgeKindOption.Kind;
        var edges = ImmutableArray.CreateBuilder<SemanticEdge>();
        foreach (var item in items)
        {
            if (!fileAnchorIdsByDocumentId.TryGetValue(item.DocumentId, out var fileAnchorId))
            {
                continue;
            }

            edges.Add(new SemanticEdge(
                $"contains:{fileAnchorId}:{item.AnchorId}",
                fileAnchorId,
                item.AnchorId,
                SemanticEdgeKind.Contains,
                1,
                "declares"));
        }

        if (selectedEdgeKind != SemanticEdgeKind.Contains)
        {
            edges.AddRange(sourceGraph.Edges
                .Where(edge => selectedAnchorIds.Contains(edge.SourceAnchorId) && selectedAnchorIds.Contains(edge.TargetAnchorId))
                .Where(edge => selectedEdgeKind is null || edge.Kind == selectedEdgeKind));
        }

        var hybridGraph = new SemanticGraph(anchors.ToImmutable(), edges.ToImmutable());
        var layout = CreateFileSymbolLayout(selectedDocuments, items, symbolDocumentIdsByAnchorId, SelectedLayoutOption.Mode);
        var scene = DiffCanvasScene.FromDocuments(
            documents.ToImmutable(),
            hybridGraph,
            layout,
            new EdgeProjectionOptions(MinimumConfidence: 0, MaxEdgesPerDocumentPair: 6),
            groupingMode: SelectedGroupingOption.Mode);
        return new SymbolGraphSceneResult(scene, selectedDocuments.Length);
    }

    private static DiffDocumentSnapshot CreateSymbolDocument(
        DiffDocumentId documentId,
        SemanticNavigationItem item,
        DiffDocumentSnapshot? sourceDocument,
        int index)
    {
        var sourceLine = sourceDocument?.Lines.FirstOrDefault(line => line.NewLineNumber == item.Line || line.OldLineNumber == item.Line);
        var sourceText = sourceLine is null ? string.Empty : sourceLine.Text.Trim();
        var signal = item switch
        {
            { IsChanged: true, IsLinked: true } => "changed + linked",
            { IsChanged: true } => "changed",
            { IsLinked: true } => "linked",
            _ => "symbol"
        };
        var metadata = new DiffDocumentMetadata(
            documentId,
            $"{item.KindText}/{ShortenForPath(item.DisplayName, 56)}",
            item.Path,
            item.IsChanged ? DiffFileStatus.Modified : DiffFileStatus.Unchanged,
            string.IsNullOrWhiteSpace(sourceDocument?.Metadata.Language) ? item.KindText : sourceDocument.Metadata.Language,
            item.IsChanged ? 1 : 0,
            0);
        var lines = ImmutableArray.CreateBuilder<DiffLine>();
        AddLine(lines, 0, DiffLineKind.Metadata, $"symbol {index + 1}: {item.DisplayName}");
        AddLine(lines, 1, DiffLineKind.Metadata, $"kind: {item.KindText}");
        AddLine(lines, 2, DiffLineKind.Context, $"file: {item.Path}:{item.Line}");
        AddLine(lines, 3, item.IsChanged ? DiffLineKind.Modified : DiffLineKind.Context, $"signal: {signal}");
        AddLine(lines, 4, item.IsLinked ? DiffLineKind.Modified : DiffLineKind.Context, $"links: {item.IncidentEdgeCount:N0}");
        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            AddLine(lines, 5, DiffLineKind.Context, sourceText);
        }

        return new DiffDocumentSnapshot(documentId, metadata, lines.ToImmutable());
    }

    private static void AddLine(ImmutableArray<DiffLine>.Builder lines, int index, DiffLineKind kind, string text) =>
        lines.Add(new DiffLine(index, index + 1, index + 1, kind, text, ImmutableArray<TokenSpan>.Empty));

    private static GraphLayoutResult CreateLayout(
        ImmutableArray<DiffDocumentSnapshot> documents,
        ImmutableArray<SemanticNavigationItem> items,
        ImmutableArray<SemanticEdge> edges,
        GraphLayoutMode layoutMode)
    {
        var mode = layoutMode == GraphLayoutMode.Auto
            ? documents.Length > 90 ? GraphLayoutMode.CompactGrid : GraphLayoutMode.Layered
            : layoutMode;
        var nodeWidth = mode == GraphLayoutMode.CompactGrid ? 380 : 460;
        var nodeHeight = mode == GraphLayoutMode.CompactGrid ? 220 : 260;
        var horizontalGap = mode == GraphLayoutMode.CompactGrid ? 34 : 72;
        var verticalGap = mode == GraphLayoutMode.CompactGrid ? 34 : 68;
        var layouts = ImmutableArray.CreateBuilder<DiffNodeLayout>(documents.Length);

        if (mode is GraphLayoutMode.Layered or GraphLayoutMode.StatusLanes)
        {
            var laneKeys = mode == GraphLayoutMode.StatusLanes
                ? items.Select(GetStatusLane).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : items.Select(item => item.KindText).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Array.Sort(laneKeys, StringComparer.OrdinalIgnoreCase);
            var laneIndexes = laneKeys.Select((key, index) => (key, index)).ToDictionary(pair => pair.key, pair => pair.index, StringComparer.OrdinalIgnoreCase);
            var laneCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < documents.Length; index++)
            {
                var lane = mode == GraphLayoutMode.StatusLanes ? GetStatusLane(items[index]) : items[index].KindText;
                var laneIndex = laneIndexes[lane];
                laneCounts.TryGetValue(lane, out var row);
                laneCounts[lane] = row + 1;
                layouts.Add(new DiffNodeLayout(
                    documents[index].Id,
                    new Rect2(laneIndex * (nodeWidth + horizontalGap), row * (nodeHeight + verticalGap), nodeWidth, nodeHeight),
                    FontSize: 12.5));
            }

            return new GraphLayoutResult(layouts.ToImmutable());
        }

        var columns = mode == GraphLayoutMode.Grid
            ? Math.Max(1, (int)Math.Ceiling(Math.Sqrt(documents.Length)))
            : Math.Max(1, (int)Math.Ceiling(Math.Sqrt(documents.Length * 1.4)));
        for (var index = 0; index < documents.Length; index++)
        {
            var row = index / columns;
            var column = index % columns;
            layouts.Add(new DiffNodeLayout(
                documents[index].Id,
                new Rect2(column * (nodeWidth + horizontalGap), row * (nodeHeight + verticalGap), nodeWidth, nodeHeight),
                FontSize: 12.5));
        }

        return new GraphLayoutResult(SpreadHighDegreeNodes(layouts.ToImmutable(), items, edges, nodeWidth, nodeHeight, horizontalGap, verticalGap));
    }

    private static GraphLayoutResult CreateFileSymbolLayout(
        ImmutableArray<DiffDocumentSnapshot> fileDocuments,
        ImmutableArray<SemanticNavigationItem> items,
        IReadOnlyDictionary<string, DiffDocumentId> symbolDocumentIdsByAnchorId,
        GraphLayoutMode layoutMode)
    {
        if (fileDocuments.IsDefaultOrEmpty)
        {
            return new GraphLayoutResult([]);
        }

        var mode = layoutMode == GraphLayoutMode.Auto ? GraphLayoutMode.Layered : layoutMode;
        if (mode is GraphLayoutMode.Grid or GraphLayoutMode.CompactGrid)
        {
            return CreateFileSymbolGridLayout(fileDocuments, items, symbolDocumentIdsByAnchorId, mode);
        }

        const double fileWidth = 620;
        const double fileHeight = 420;
        const double symbolWidth = 380;
        const double symbolHeight = 190;
        const double horizontalGap = 84;
        const double symbolColumnGap = 32;
        const double symbolGap = 26;
        const double sectionGap = 88;
        var layouts = ImmutableArray.CreateBuilder<DiffNodeLayout>(fileDocuments.Length + items.Length);
        var itemsByDocumentId = items
            .GroupBy(item => item.DocumentId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var y = 0.0;

        foreach (var fileDocument in fileDocuments)
        {
            var fileSymbols = itemsByDocumentId.GetValueOrDefault(fileDocument.Id) ?? [];
            var symbolColumns = Math.Max(1, Math.Min(3, (int)Math.Ceiling(Math.Sqrt(Math.Max(1, fileSymbols.Length) / 1.7))));
            var symbolsPerColumn = Math.Max(1, (int)Math.Ceiling(fileSymbols.Length / (double)symbolColumns));
            var symbolBlockHeight = fileSymbols.Length == 0
                ? 0
                : symbolsPerColumn * symbolHeight + Math.Max(0, symbolsPerColumn - 1) * symbolGap;
            var rowHeight = Math.Max(fileHeight, symbolBlockHeight);
            var fileY = y + Math.Max(0, rowHeight - fileHeight) * 0.5;
            layouts.Add(new DiffNodeLayout(fileDocument.Id, new Rect2(0, fileY, fileWidth, fileHeight), FontSize: 12.5));

            for (var index = 0; index < fileSymbols.Length; index++)
            {
                var item = fileSymbols[index];
                if (!symbolDocumentIdsByAnchorId.TryGetValue(item.AnchorId, out var documentId))
                {
                    continue;
                }

                var column = index / symbolsPerColumn;
                var row = index % symbolsPerColumn;
                var x = fileWidth + horizontalGap + column * (symbolWidth + symbolColumnGap);
                var symbolY = y + row * (symbolHeight + symbolGap);
                layouts.Add(new DiffNodeLayout(documentId, new Rect2(x, symbolY, symbolWidth, symbolHeight), FontSize: 12.5));
            }

            y += rowHeight + sectionGap;
        }

        return new GraphLayoutResult(layouts.ToImmutable());
    }

    private static GraphLayoutResult CreateFileSymbolGridLayout(
        ImmutableArray<DiffDocumentSnapshot> fileDocuments,
        ImmutableArray<SemanticNavigationItem> items,
        IReadOnlyDictionary<string, DiffDocumentId> symbolDocumentIdsByAnchorId,
        GraphLayoutMode layoutMode)
    {
        var documents = ImmutableArray.CreateBuilder<DiffDocumentId>(fileDocuments.Length + items.Length);
        documents.AddRange(fileDocuments.Select(document => document.Id));
        foreach (var item in items)
        {
            if (symbolDocumentIdsByAnchorId.TryGetValue(item.AnchorId, out var documentId))
            {
                documents.Add(documentId);
            }
        }
        var nodeWidth = layoutMode == GraphLayoutMode.CompactGrid ? 360 : 460;
        var nodeHeight = layoutMode == GraphLayoutMode.CompactGrid ? 190 : 260;
        var horizontalGap = layoutMode == GraphLayoutMode.CompactGrid ? 34 : 72;
        var verticalGap = layoutMode == GraphLayoutMode.CompactGrid ? 34 : 68;
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(documents.Count * 1.25)));
        var layouts = ImmutableArray.CreateBuilder<DiffNodeLayout>(documents.Count);
        for (var index = 0; index < documents.Count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            layouts.Add(new DiffNodeLayout(
                documents[index],
                new Rect2(column * (nodeWidth + horizontalGap), row * (nodeHeight + verticalGap), nodeWidth, nodeHeight),
                FontSize: 12.5));
        }

        return new GraphLayoutResult(layouts.ToImmutable());
    }

    private static ImmutableArray<DiffNodeLayout> SpreadHighDegreeNodes(
        ImmutableArray<DiffNodeLayout> layouts,
        ImmutableArray<SemanticNavigationItem> items,
        ImmutableArray<SemanticEdge> edges,
        double nodeWidth,
        double nodeHeight,
        double horizontalGap,
        double verticalGap)
    {
        if (layouts.Length < 8 || edges.IsDefaultOrEmpty)
        {
            return layouts;
        }

        var degreeByAnchor = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            degreeByAnchor[edge.SourceAnchorId] = degreeByAnchor.GetValueOrDefault(edge.SourceAnchorId) + 1;
            degreeByAnchor[edge.TargetAnchorId] = degreeByAnchor.GetValueOrDefault(edge.TargetAnchorId) + 1;
        }

        var builder = layouts.ToBuilder();
        var highDegreeIndexes = items
            .Select((item, index) => (index, degree: degreeByAnchor.GetValueOrDefault(item.AnchorId)))
            .Where(pair => pair.degree > 2)
            .OrderByDescending(pair => pair.degree)
            .ThenBy(pair => items[pair.index].DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Min(6, items.Length))
            .Select(pair => pair.index)
            .ToArray();
        for (var index = 0; index < highDegreeIndexes.Length; index++)
        {
            var layoutIndex = highDegreeIndexes[index];
            builder[layoutIndex] = builder[layoutIndex] with
            {
                Bounds = new Rect2(index * (nodeWidth + horizontalGap), -nodeHeight - verticalGap, nodeWidth, nodeHeight)
            };
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<SymbolGraphFilterOptionViewModel> CreateKindOptions(ImmutableArray<SemanticNavigationItem> items) =>
    [
        new SymbolGraphFilterOptionViewModel(AllKey, "All kinds", "All symbol kinds"),
        .. items
            .GroupBy(item => item.KindText, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SymbolGraphFilterOptionViewModel(group.Key, group.Key, $"{group.Count():N0} symbols"))
    ];

    private static ImmutableArray<SymbolGraphFilterOptionViewModel> CreateDocumentOptions(ImmutableArray<SemanticNavigationItem> items) =>
    [
        new SymbolGraphFilterOptionViewModel(AllKey, "All files", "All files"),
        .. items
            .GroupBy(item => item.DocumentId.Value, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var fileName = System.IO.Path.GetFileName(first.Path);
                return new SymbolGraphFilterOptionViewModel(group.Key, string.IsNullOrWhiteSpace(fileName) ? first.Path : fileName, $"{group.Count():N0} symbols | {first.Path}");
            })
            .OrderByDescending(option => ExtractLeadingCount(option.DetailText))
            .ThenBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
    ];

    private static ImmutableArray<SymbolGraphEdgeKindOptionViewModel> CreateEdgeKindOptions(SemanticGraph graph) =>
    [
        new SymbolGraphEdgeKindOptionViewModel(null, AllKey, "All edges", "All semantic edges"),
        new SymbolGraphEdgeKindOptionViewModel(SemanticEdgeKind.Contains, SemanticEdgeKind.Contains.ToString(), "Contains", "File to symbol declarations"),
        .. graph.Edges
            .Where(edge => edge.Kind != SemanticEdgeKind.Contains)
            .GroupBy(edge => edge.Kind)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new SymbolGraphEdgeKindOptionViewModel(group.Key, group.Key.ToString(), group.Key.ToString(), $"{group.Count():N0} edges"))
    ];

    private static bool MatchesScope(SemanticNavigationItem item, string scopeKey) => scopeKey switch
    {
        ChangedKey => item.IsChanged,
        LinkedKey => item.IsLinked,
        UnlinkedKey => !item.IsLinked,
        _ => true
    };

    private static bool MatchesOption(string value, string key, bool ignoreCase)
    {
        if (string.Equals(key, AllKey, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(value, key, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private HashSet<string>? CreateEdgeKindAnchorSet(SemanticEdgeKind? edgeKind)
    {
        if (edgeKind is null)
        {
            return null;
        }

        if (edgeKind == SemanticEdgeKind.Contains)
        {
            return sourceItems.Select(item => item.AnchorId).ToHashSet(StringComparer.Ordinal);
        }

        var anchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in sourceGraph.Edges.Where(edge => edge.Kind == edgeKind))
        {
            anchors.Add(edge.SourceAnchorId);
            anchors.Add(edge.TargetAnchorId);
        }

        return anchors;
    }

    private static string BuildSummary(int filteredCount, int renderedCount, int renderedFileCount, int edgeCount, SymbolGraphViewMode viewMode)
    {
        var cappedText = filteredCount > renderedCount ? $"showing first {renderedCount:N0} of " : string.Empty;
        var fileText = viewMode == SymbolGraphViewMode.FilesAndSymbols ? $" | {renderedFileCount:N0} files" : string.Empty;
        return $"{cappedText}{filteredCount:N0} symbols{fileText} | {edgeCount:N0} links";
    }

    private string BuildFilterStatus(int filteredCount, int renderedCount, string query)
    {
        var parts = new List<string>();
        if (!string.Equals(SelectedScopeOption.Key, AllKey, StringComparison.Ordinal))
        {
            parts.Add(SelectedScopeOption.DisplayName);
        }

        if (!string.Equals(SelectedKindOption.Key, AllKey, StringComparison.Ordinal))
        {
            parts.Add(SelectedKindOption.DisplayName);
        }

        if (!string.Equals(SelectedDocumentOption.Key, AllKey, StringComparison.Ordinal))
        {
            parts.Add(SelectedDocumentOption.DisplayName);
        }

        if (!string.Equals(SelectedEdgeKindOption.Key, AllKey, StringComparison.Ordinal))
        {
            parts.Add(SelectedEdgeKindOption.DisplayName);
        }

        if (SelectedViewModeOption.Mode == SymbolGraphViewMode.FilesAndSymbols)
        {
            parts.Add("files + symbols");
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            parts.Add($"search \"{query}\"");
        }

        var filterText = parts.Count == 0 ? "Showing all symbols" : $"Filtered by {string.Join(", ", parts)}";
        return filteredCount > renderedCount
            ? $"{filterText}; rendering first {renderedCount:N0} for graph readability"
            : filterText;
    }

    private static string GetStatusLane(SemanticNavigationItem item) => item switch
    {
        { IsChanged: true, IsLinked: true } => "Changed + linked",
        { IsChanged: true } => "Changed",
        { IsLinked: true } => "Linked",
        _ => "Other"
    };

    private static int ExtractLeadingCount(string text)
    {
        var digits = new string(text.TakeWhile(character => char.IsDigit(character) || character == ',' || character == '.').ToArray());
        return int.TryParse(digits.Replace(",", string.Empty).Replace(".", string.Empty), out var count) ? count : 0;
    }

    private static string ShortenForPath(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return string.IsNullOrWhiteSpace(value) ? "symbol" : value;
        }

        var side = Math.Max(4, (maxLength - 3) / 2);
        return $"{value[..side]}...{value[^side..]}";
    }
}

public sealed partial record SymbolGraphFilterOptionViewModel(string Key, string DisplayName, string DetailText)
{
    public override string ToString() => DisplayName;
}

public sealed partial record SymbolGraphEdgeKindOptionViewModel(SemanticEdgeKind? Kind, string Key, string DisplayName, string DetailText)
{
    public override string ToString() => DisplayName;
}

public sealed partial record SymbolGraphViewModeOptionViewModel(SymbolGraphViewMode Mode, string DisplayName, string DetailText)
{
    public static ImmutableArray<SymbolGraphViewModeOptionViewModel> All { get; } =
    [
        new(SymbolGraphViewMode.SymbolsOnly, "Symbols", "Only semantic symbols"),
        new(SymbolGraphViewMode.FilesAndSymbols, "Files + symbols", "Real file diff nodes connected to symbol nodes")
    ];

    public override string ToString() => DisplayName;
}

internal sealed record SymbolGraphSceneResult(DiffCanvasScene Scene, int FileCount);

internal static class SymbolGraphDocumentIds
{
    private const string Prefix = "symbol:";
    private const string FileAnchorPrefix = "file-anchor:";

    public static DiffDocumentId Create(DiffDocumentId sourceDocumentId, string anchorId) =>
        new($"{Prefix}{Encode(sourceDocumentId.Value)}:{Encode(anchorId)}");

    public static string CreateFileAnchorId(DiffDocumentId sourceDocumentId) =>
        $"{FileAnchorPrefix}{Encode(sourceDocumentId.Value)}";

    public static string? TryGetSourceDocumentId(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId) || !documentId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var payload = documentId[Prefix.Length..];
        var separator = payload.IndexOf(':');
        return separator <= 0 ? null : Decode(payload[..separator]);
    }

    public static string? TryGetAnchorId(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId) || !documentId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var payload = documentId[Prefix.Length..];
        var separator = payload.IndexOf(':');
        return separator <= 0 || separator >= payload.Length - 1 ? null : Decode(payload[(separator + 1)..]);
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Decode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - normalized.Length % 4) % 4;
        normalized = normalized.PadRight(normalized.Length + padding, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
    }
}

public sealed record GraphWorkspaceState(
    string? RepositoryPath,
    GitDiffRequest? Request,
    GitPullRequestInfo? ReviewRequest,
    string RepositoryName,
    string ContextText,
    string StatusText,
    string StatusPrefix,
    bool DocumentsAreRepositoryDocuments,
    ImmutableArray<DiffDocumentSnapshot> Documents,
    SemanticGraph SemanticGraph,
    GitDiffSnapshot? GitSnapshot,
    GraphLayoutResult? PreviousLayout,
    ImmutableHashSet<DiffDocumentId> PinnedDocumentIds,
    DiffCanvasScene Scene,
    string? SelectedDocumentId,
    ImmutableArray<ReviewThreadItemViewModel> ReviewThreadItems,
    ImmutableArray<GitReviewThreadInfo> ReviewThreads,
    GitReviewRequestKind ReviewRequestKind);

public sealed partial class BlameTabViewModel : ObservableObject
{
    public BlameTabViewModel(
        string path,
        string language,
        string summaryText,
        string statusText,
        ImmutableArray<BlameCommitNodeViewModel> commitNodes,
        ImmutableArray<BlameTimelineItemViewModel> timelineItems,
        DiffCanvasScene? changeGraphScene = null,
        string changeGraphSummaryText = "")
    {
        Path = path;
        Language = language;
        SummaryText = summaryText;
        StatusText = statusText;
        CommitNodes = commitNodes;
        TimelineItems = timelineItems;
        ChangeGraphScene = changeGraphScene ?? DiffCanvasScene.FromDocuments([]);
        ChangeGraphSummaryText = string.IsNullOrWhiteSpace(changeGraphSummaryText)
            ? "Change graph unavailable"
            : changeGraphSummaryText;
    }

    public string Path { get; }

    public string Language { get; }

    public string SummaryText { get; }

    public string StatusText { get; }

    public ImmutableArray<BlameCommitNodeViewModel> CommitNodes { get; }

    public ImmutableArray<BlameTimelineItemViewModel> TimelineItems { get; }

    public DiffCanvasScene ChangeGraphScene { get; }

    public string ChangeGraphSummaryText { get; }

    public Visibility EmptyVisibility => CommitNodes.IsDefaultOrEmpty ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ContentVisibility => CommitNodes.IsDefaultOrEmpty ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CommitTimelineVisibility => ContentVisibility == Visibility.Visible && DisplayMode == BlameDisplayMode.CommitTimeline
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ChangeGraphVisibility => ContentVisibility == Visibility.Visible && DisplayMode == BlameDisplayMode.ChangeGraph
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CollapsedTimelineVisibility => IsTimelineExpanded ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ExpandedTimelineVisibility => IsTimelineExpanded ? Visibility.Visible : Visibility.Collapsed;

    public string TimelineToggleText => IsTimelineExpanded ? "Shrink timeline" : "Expand timeline";

    public bool IsCommitTimelineMode => DisplayMode == BlameDisplayMode.CommitTimeline;

    public bool IsChangeGraphMode => DisplayMode == BlameDisplayMode.ChangeGraph;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CollapsedTimelineVisibility))]
    [NotifyPropertyChangedFor(nameof(ExpandedTimelineVisibility))]
    [NotifyPropertyChangedFor(nameof(TimelineToggleText))]
    private bool isTimelineExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitTimelineVisibility))]
    [NotifyPropertyChangedFor(nameof(ChangeGraphVisibility))]
    [NotifyPropertyChangedFor(nameof(IsCommitTimelineMode))]
    [NotifyPropertyChangedFor(nameof(IsChangeGraphMode))]
    private BlameDisplayMode displayMode = BlameDisplayMode.CommitTimeline;

    public void ToggleTimeline() => IsTimelineExpanded = !IsTimelineExpanded;

    public void SetDisplayMode(BlameDisplayMode mode) => DisplayMode = mode;

    public static BlameTabViewModel Loading(string path, string language) => new(
        path,
        language,
        "Loading blame and file history",
        $"{path} | loading blame",
        [],
        []);

    public static BlameTabViewModel FromBlame(string path, string language, GitFileBlame blame, ImmutableArray<GitCommitInfo> history)
    {
        if (blame.Lines.IsDefaultOrEmpty)
        {
            return new BlameTabViewModel(
                path,
                language,
                "No blame data available",
                $"{path} | blame unavailable",
                [],
                BuildTimelineItems(history, ImmutableDictionary<string, int>.Empty));
        }

        var totalLines = blame.Lines.Length;
        var lineCountsByCommit = blame.Lines
            .GroupBy(line => line.CommitId, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var groupsByCommit = blame.Lines
            .GroupBy(line => line.CommitId, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.ToImmutableArray(), StringComparer.Ordinal);
        var nodes = groupsByCommit
            .Select((pair, index) => BlameCommitNodeViewModel.FromLines(pair.Key, pair.Value, totalLines, index))
            .OrderByDescending(node => node.AuthorTime)
            .ThenBy(node => node.Author, StringComparer.Ordinal)
            .ToImmutableArray();
        var latest = nodes.FirstOrDefault();
        var authors = nodes
            .GroupBy(node => node.Author, StringComparer.Ordinal)
            .OrderByDescending(group => group.Sum(node => node.LineCount))
            .Take(2)
            .Select(group => $"{group.Key} {group.Sum(node => node.LineCount):N0}");
        var summary = $"{totalLines:N0} blamed lines | {nodes.Length:N0} commits | {string.Join(", ", authors)}";
        if (latest is not null)
        {
            summary += $" | latest {latest.Author} {latest.TimeText} {latest.ShortId}";
        }

        var timelineItems = BuildTimelineItems(history, lineCountsByCommit);
        return new BlameTabViewModel(
            path,
            language,
            summary,
            $"{path} | {summary}",
            nodes,
            timelineItems,
            BuildChangeGraphScene(path, language, timelineItems, groupsByCommit),
            $"{timelineItems.Length:N0} history nodes | {Math.Max(0, timelineItems.Length - 1):N0} history links | blamed file changes rendered as diff nodes");
    }

    private static DiffCanvasScene BuildChangeGraphScene(
        string path,
        string language,
        ImmutableArray<BlameTimelineItemViewModel> timelineItems,
        ImmutableDictionary<string, ImmutableArray<GitBlameLine>> groupsByCommit)
    {
        if (timelineItems.IsDefaultOrEmpty)
        {
            return DiffCanvasScene.FromDocuments([]);
        }

        var documents = timelineItems
            .Select((item, index) =>
            {
                groupsByCommit.TryGetValue(item.CommitId, out var lines);
                return CreateBlameCommitDocument(path, language, item, lines, index);
            })
            .ToImmutableArray();
        var anchors = documents
            .Select(document => new SemanticAnchor(
                $"anchor:{document.Id.Value}",
                document.Id,
                new TextRange(0, 0, 1, 1),
                SemanticAnchorKind.File,
                document.Metadata.Path))
            .ToImmutableArray();
        var edges = anchors
            .Zip(anchors.Skip(1), (source, target) => new SemanticEdge(
                $"history:{source.DocumentId.Value}->{target.DocumentId.Value}",
                source.Id,
                target.Id,
                SemanticEdgeKind.Contains,
                1,
                "previous"))
            .ToImmutableArray();
        var layout = new GraphLayoutResult(documents
            .Select((document, index) =>
            {
                const double nodeWidth = 560;
                const double nodeHeight = 360;
                var column = index % 3;
                var row = index / 3;
                return new DiffNodeLayout(
                    document.Id,
                    new Rect2(column * 660, row * 430, nodeWidth, nodeHeight),
                    IsPinned: true,
                    FontSize: 12.0);
            })
            .ToImmutableArray());
        return DiffCanvasScene.FromDocuments(
            documents,
            new SemanticGraph(anchors, edges),
            layout,
            groupingMode: GraphGroupingMode.None);
    }

    private static DiffDocumentSnapshot CreateBlameCommitDocument(
        string path,
        string language,
        BlameTimelineItemViewModel item,
        ImmutableArray<GitBlameLine> lines,
        int index)
    {
        var documentId = new DiffDocumentId($"blame:{SanitizeId(path)}:{item.ShortId}:{index}");
        var metadata = new DiffDocumentMetadata(
            documentId,
            $"{System.IO.Path.GetFileName(path)} @ {item.ShortId}",
            path,
            DiffFileStatus.Modified,
            language,
            item.BlamedLineCount,
            0);
        var sourceLines = lines.IsDefault ? ImmutableArray<GitBlameLine>.Empty : lines;
        var sortedLines = sourceLines
            .OrderBy(line => line.LineNumber)
            .ToImmutableArray();
        var builder = ImmutableArray.CreateBuilder<DiffLine>(sortedLines.Length + 4);
        AddMetadataLine(builder, $"commit {item.CommitId}");
        AddMetadataLine(builder, item.Subject);
        AddMetadataLine(builder, $"{item.Author} | {item.TimeText} | {FormatLineRanges(sortedLines.Select(line => line.LineNumber).ToArray())}");
        AddMetadataLine(builder, string.Empty);

        if (sortedLines.IsDefaultOrEmpty)
        {
            builder.Add(new DiffLine(
                builder.Count,
                null,
                null,
                DiffLineKind.Ignored,
                "No current blamed lines retained at the active revision.",
                ImmutableArray<TokenSpan>.Empty));
        }
        else
        {
            foreach (var line in sortedLines)
            {
                builder.Add(new DiffLine(
                    builder.Count,
                    null,
                    line.LineNumber,
                    DiffLineKind.Added,
                    string.IsNullOrEmpty(line.Text) ? " " : line.Text,
                    ImmutableArray<TokenSpan>.Empty));
            }
        }

        return new DiffDocumentSnapshot(documentId, metadata, builder.ToImmutable());
    }

    private static void AddMetadataLine(ImmutableArray<DiffLine>.Builder builder, string text)
    {
        builder.Add(new DiffLine(
            builder.Count,
            null,
            null,
            DiffLineKind.Metadata,
            text,
            ImmutableArray<TokenSpan>.Empty));
    }

    private static string SanitizeId(string value)
    {
        var normalized = value.Replace('\\', '/');
        var chars = normalized.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private static string FormatLineRanges(IReadOnlyList<int> lineNumbers)
    {
        if (lineNumbers.Count == 0)
        {
            return "no retained blamed lines";
        }

        var ranges = new List<string>();
        var start = lineNumbers[0];
        var previous = start;
        for (var index = 1; index < lineNumbers.Count; index++)
        {
            var line = lineNumbers[index];
            if (line == previous + 1)
            {
                previous = line;
                continue;
            }

            ranges.Add(start == previous ? start.ToString() : $"{start}-{previous}");
            start = previous = line;
        }

        ranges.Add(start == previous ? start.ToString() : $"{start}-{previous}");
        return $"lines {string.Join(", ", ranges.Take(6))}{(ranges.Count > 6 ? ", ..." : string.Empty)}";
    }

    private static ImmutableArray<BlameTimelineItemViewModel> BuildTimelineItems(
        ImmutableArray<GitCommitInfo> history,
        ImmutableDictionary<string, int> lineCountsByCommit)
    {
        if (history.IsDefaultOrEmpty && lineCountsByCommit.Count == 0)
        {
            return [];
        }

        var fromHistory = history
            .Select((commit, index) => BlameTimelineItemViewModel.FromCommit(commit, lineCountsByCommit.GetValueOrDefault(commit.Id), index))
            .ToImmutableArray();
        if (!fromHistory.IsDefaultOrEmpty)
        {
            return fromHistory;
        }

        return lineCountsByCommit
            .OrderByDescending(pair => pair.Value)
            .Select((pair, index) => BlameTimelineItemViewModel.FromBlameOnly(pair.Key, pair.Value, index))
            .ToImmutableArray();
    }
}

public sealed record BlameCommitNodeViewModel(
    string CommitId,
    string ShortId,
    string Author,
    DateTimeOffset? AuthorTime,
    string TimeText,
    string Summary,
    string LineRangeText,
    int LineCount,
    string LineCountText,
    string CoverageText,
    double CoverageWidth,
    SolidColorBrush AccentBrush,
    SolidColorBrush SoftBrush)
{
    public static BlameCommitNodeViewModel FromLines(string commitId, ImmutableArray<GitBlameLine> lines, int totalLines, int index)
    {
        var first = lines[0];
        var lineCount = lines.Length;
        var coverage = totalLines <= 0 ? 0 : (double)lineCount / totalLines;
        var accent = BlameInsightBrushes.GetBrush(index);
        return new BlameCommitNodeViewModel(
            commitId,
            Shorten(commitId),
            first.Author,
            first.AuthorTime,
            FormatTimestamp(first.AuthorTime),
            string.IsNullOrWhiteSpace(first.Summary) ? "No commit summary" : first.Summary,
            FormatLineRanges(lines.Select(line => line.LineNumber).Order().ToArray()),
            lineCount,
            lineCount == 1 ? "1 line" : $"{lineCount:N0} lines",
            $"{coverage:P0} of file",
            Math.Clamp(28 + coverage * 260, 28, 288),
            accent,
            BlameInsightBrushes.GetSoftBrush(accent));
    }

    private static string FormatLineRanges(IReadOnlyList<int> lineNumbers)
    {
        if (lineNumbers.Count == 0)
        {
            return "lines unknown";
        }

        var ranges = new List<string>();
        var start = lineNumbers[0];
        var previous = start;
        for (var index = 1; index < lineNumbers.Count; index++)
        {
            var line = lineNumbers[index];
            if (line == previous + 1)
            {
                previous = line;
                continue;
            }

            ranges.Add(start == previous ? start.ToString() : $"{start}-{previous}");
            start = previous = line;
        }

        ranges.Add(start == previous ? start.ToString() : $"{start}-{previous}");
        return $"lines {string.Join(", ", ranges.Take(6))}{(ranges.Count > 6 ? ", ..." : string.Empty)}";
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp is null
            ? "unknown date"
            : timestamp.Value.LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.CurrentCulture);

    private static string Shorten(string commitId) => commitId.Length <= 10 ? commitId : commitId[..10];
}

public sealed record BlameTimelineItemViewModel(
    string CommitId,
    string ShortId,
    string Subject,
    string Author,
    string TimeText,
    int BlamedLineCount,
    string BlamedLineText,
    double MarkerHeight,
    SolidColorBrush AccentBrush,
    SolidColorBrush SoftBrush)
{
    public Visibility BlamedLineVisibility => BlamedLineCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static BlameTimelineItemViewModel FromCommit(GitCommitInfo commit, int blamedLineCount, int index)
    {
        var accent = BlameInsightBrushes.GetBrush(index);
        return new BlameTimelineItemViewModel(
            commit.Id,
            commit.ShortId,
            string.IsNullOrWhiteSpace(commit.Subject) ? "No commit subject" : commit.Subject,
            string.IsNullOrWhiteSpace(commit.Author) ? "unknown" : commit.Author,
            FormatTimestamp(commit.AuthorTime),
            blamedLineCount,
            blamedLineCount == 1 ? "1 blamed line" : $"{blamedLineCount:N0} blamed lines",
            blamedLineCount == 0 ? 24 : Math.Clamp(32 + blamedLineCount * 1.8, 32, 96),
            accent,
            BlameInsightBrushes.GetSoftBrush(accent));
    }

    public static BlameTimelineItemViewModel FromBlameOnly(string commitId, int blamedLineCount, int index)
    {
        var accent = BlameInsightBrushes.GetBrush(index);
        return new BlameTimelineItemViewModel(
            commitId,
            commitId.Length <= 10 ? commitId : commitId[..10],
            "Commit not loaded in file history",
            "unknown",
            "unknown date",
            blamedLineCount,
            blamedLineCount == 1 ? "1 blamed line" : $"{blamedLineCount:N0} blamed lines",
            Math.Clamp(32 + blamedLineCount * 1.8, 32, 96),
            accent,
            BlameInsightBrushes.GetSoftBrush(accent));
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp is null
            ? "unknown date"
            : timestamp.Value.LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.CurrentCulture);
}

internal static class BlameInsightBrushes
{
    private static readonly Color[] Colors =
    [
        Color.FromArgb(255, 0, 122, 204),
        Color.FromArgb(255, 26, 127, 55),
        Color.FromArgb(255, 154, 103, 0),
        Color.FromArgb(255, 168, 85, 247),
        Color.FromArgb(255, 20, 184, 166),
        Color.FromArgb(255, 236, 72, 153),
        Color.FromArgb(255, 100, 116, 139)
    ];

    public static SolidColorBrush GetBrush(int index) => new(Colors[Math.Abs(index) % Colors.Length]);

    public static SolidColorBrush GetSoftBrush(SolidColorBrush brush)
    {
        var color = brush.Color;
        return new SolidColorBrush(Color.FromArgb(34, color.R, color.G, color.B));
    }
}

public sealed partial class GitHistoryTimelineViewModel : ObservableObject
{
    private readonly HashSet<string> seenCommitIds = new(StringComparer.Ordinal);
    private readonly GitHistoryGraphLayoutState graphLayout = new();
    private int nextSkip;

    public GitHistoryTimelineViewModel(
        string title,
        string referenceText,
        string rangeText,
        GitHistoryRequest request)
    {
        Title = title;
        ReferenceText = referenceText;
        RangeText = rangeText;
        Request = request with { Skip = 0 };
        nextSkip = Math.Max(0, request.Skip);
    }

    public string Title { get; }

    public string ReferenceText { get; }

    public string RangeText { get; }

    public GitHistoryRequest Request { get; }

    public ObservableCollection<GitHistoryItemViewModel> Commits { get; } = [];

    public int LoadedCount => Commits.Count;

    public string CountText => HasMore
        ? $"{LoadedCount:N0}+ commits"
        : LoadedCount == 1
            ? "1 commit"
            : $"{LoadedCount:N0} commits";

    public string FooterText => IsLoadingMore
        ? "Loading more commits..."
        : HasMore
            ? "Scroll to load more commits"
            : "End of history";

    public Visibility FooterVisibility => IsLoadingMore || HasMore ? Visibility.Visible : Visibility.Collapsed;

    public GitHistoryRequest NextPageRequest => Request with { Skip = nextSkip };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    [NotifyPropertyChangedFor(nameof(FooterVisibility))]
    private bool hasMore = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    [NotifyPropertyChangedFor(nameof(FooterVisibility))]
    private bool isLoadingMore;

    public void AppendSnapshot(GitHistorySnapshot snapshot)
    {
        foreach (var commit in snapshot.Commits)
        {
            if (seenCommitIds.Add(commit.Id))
            {
                Commits.Add(GitHistoryItemViewModel.FromCommit(commit, graphLayout.CreateRow(commit)));
            }
        }

        nextSkip = Math.Max(nextSkip, snapshot.Request.Skip + snapshot.Commits.Length);
        HasMore = snapshot.HasMore;
        OnPropertyChanged(nameof(LoadedCount));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(FooterVisibility));
    }

    public static GitHistoryTimelineViewModel Create(string title, GitHistoryRequest request)
    {
        var range = string.IsNullOrWhiteSpace(request.BaseRef)
            ? request.HeadRef
            : $"{request.BaseRef}..{request.HeadRef}";
        return new GitHistoryTimelineViewModel(title, request.HeadRef, range, request);
    }
}

public sealed record GitHistoryItemViewModel(
    string CommitId,
    string ShortId,
    string Subject,
    string Author,
    string AuthorTimeText,
    string Decorations,
    ImmutableArray<GitHistoryRefBadgeViewModel> RefBadges,
    string ParentText,
    double GraphWidth,
    double RowHeight,
    ImmutableArray<GitHistoryGraphPathViewModel> GraphPaths,
    double DotLeft,
    double DotTop,
    double DotSize,
    SolidColorBrush DotBrush,
    SolidColorBrush DotStroke,
    double RowMinHeight,
    string MergeText)
{
    public Visibility MergeVisibility => string.IsNullOrWhiteSpace(MergeText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DecorationsVisibility => RefBadges.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string MetaText => $"{Author} | {AuthorTimeText} | {ParentText}";

    public static GitHistoryItemViewModel FromCommit(GitCommitInfo commit, GitHistoryGraphRowViewModel graph)
    {
        var parentText = commit.ParentIds.Length == 0
            ? "root commit"
            : commit.ParentIds.Length == 1
                ? $"parent {Shorten(commit.ParentIds[0])}"
                : $"{commit.ParentIds.Length:N0} parents";
        return new GitHistoryItemViewModel(
            commit.Id,
            commit.ShortId,
            commit.Subject,
            string.IsNullOrWhiteSpace(commit.Author) ? "unknown" : commit.Author,
            FormatTimestamp(commit.AuthorTime),
            commit.Decorations,
            GitHistoryRefBadgeViewModel.FromDecorations(commit.Decorations),
            parentText,
            graph.Width,
            graph.Height,
            graph.Paths,
            graph.DotLeft,
            graph.DotTop,
            graph.DotSize,
            graph.DotBrush,
            graph.DotStroke,
            graph.Height,
            commit.IsMerge ? "merge" : string.Empty);
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp is null
            ? "unknown date"
            : timestamp.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    private static string Shorten(string commitId) => commitId.Length <= 12 ? commitId : commitId[..12];
}

public sealed record GitHistoryGraphRowViewModel(
    double Width,
    double Height,
    ImmutableArray<GitHistoryGraphPathViewModel> Paths,
    double DotLeft,
    double DotTop,
    double DotSize,
    SolidColorBrush DotBrush,
    SolidColorBrush DotStroke);

public sealed record GitHistoryGraphPathViewModel(Geometry Data, SolidColorBrush Stroke, double StrokeThickness, double Opacity);

internal sealed class GitHistoryGraphLayoutState
{
    private const double LaneSpacing = 14;
    private const double GraphLeft = 22;
    private const double GraphTop = 0;
    private const double RowHeight = 58;
    private const double CommitCenterY = 24;
    private const double CommitDotSize = 10;
    private const double StrokeThickness = 2.1;
    private const double GraphWidth = 214;

    private readonly List<string> lanes = [];
    private readonly Dictionary<string, int> colorsByCommit = new(StringComparer.Ordinal);
    private int nextColor;
    private int rowIndex;

    public GitHistoryGraphRowViewModel CreateRow(GitCommitInfo commit)
    {
        var paths = ImmutableArray.CreateBuilder<GitHistoryGraphPathViewModel>();
        var currentLane = lanes.FindIndex(lane => string.Equals(lane, commit.Id, StringComparison.Ordinal));
        var currentWasActive = currentLane >= 0;
        if (currentLane < 0)
        {
            currentLane = 0;
            lanes.Insert(0, commit.Id);
            AssignColor(commit.Id, nextColor++);
        }

        var currentColor = GetColor(commit.Id);
        var laneSnapshot = lanes.ToArray();
        for (var lane = 0; lane < laneSnapshot.Length; lane++)
        {
            if (lane == currentLane && !currentWasActive)
            {
                continue;
            }

            var brush = GitHistoryBrushes.GetLaneBrush(GetColor(laneSnapshot[lane]));
            paths.Add(CreateLine(LaneX(lane), GraphTop, LaneX(lane), CommitCenterY, brush));
        }

        var nextLanes = lanes.ToList();
        nextLanes.RemoveAt(currentLane);
        var parentLanes = ResolveParentLanes(commit, currentLane, nextLanes, currentColor);
        var parentIds = commit.ParentIds.ToHashSet(StringComparer.Ordinal);
        for (var lane = 0; lane < laneSnapshot.Length; lane++)
        {
            if (lane == currentLane)
            {
                continue;
            }

            var laneCommitId = laneSnapshot[lane];
            var nextLane = nextLanes.FindIndex(next => string.Equals(next, laneCommitId, StringComparison.Ordinal));
            if (nextLane < 0)
            {
                continue;
            }

            var sourceX = LaneX(lane);
            var targetX = LaneX(nextLane);
            var brush = GitHistoryBrushes.GetLaneBrush(GetColor(laneCommitId));
            var opacity = parentIds.Contains(laneCommitId) ? 0.58 : 1.0;
            paths.Add(Math.Abs(sourceX - targetX) < 0.1
                ? CreateLine(sourceX, CommitCenterY, targetX, RowHeight, brush, opacity)
                : CreateCurve(sourceX, CommitCenterY, targetX, RowHeight, brush, opacity));
        }

        foreach (var parent in parentLanes)
        {
            var sourceX = LaneX(currentLane);
            var targetX = LaneX(parent.Lane);
            var brush = GitHistoryBrushes.GetLaneBrush(parent.Color);
            paths.Add(Math.Abs(sourceX - targetX) < 0.1
                ? CreateLine(sourceX, CommitCenterY, targetX, RowHeight, brush)
                : CreateCurve(sourceX, CommitCenterY, targetX, RowHeight, brush));
        }

        lanes.Clear();
        lanes.AddRange(nextLanes);
        rowIndex++;

        return new GitHistoryGraphRowViewModel(
            GraphWidth,
            RowHeight,
            paths.ToImmutable(),
            LaneX(currentLane) - CommitDotSize / 2,
            CommitCenterY - CommitDotSize / 2,
            CommitDotSize,
            GitHistoryBrushes.GetLaneBrush(currentColor),
            GitHistoryBrushes.DotStrokeBrush);
    }

    private ImmutableArray<(int Lane, int Color)> ResolveParentLanes(GitCommitInfo commit, int currentLane, List<string> nextLanes, int currentColor)
    {
        if (commit.ParentIds.Length == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<(int Lane, int Color)>();
        var insertOffset = 0;
        for (var parentIndex = 0; parentIndex < commit.ParentIds.Length; parentIndex++)
        {
            var parentId = commit.ParentIds[parentIndex];
            var parentLane = nextLanes.FindIndex(lane => string.Equals(lane, parentId, StringComparison.Ordinal));
            if (parentLane < 0)
            {
                parentLane = Math.Clamp(currentLane + insertOffset, 0, nextLanes.Count);
                nextLanes.Insert(parentLane, parentId);
                insertOffset++;
            }

            var color = parentIndex == 0
                ? AssignColor(parentId, currentColor)
                : AssignColor(parentId, nextColor++);
            builder.Add((parentLane, color));
        }

        return builder.ToImmutable();
    }

    private int AssignColor(string commitId, int color)
    {
        if (colorsByCommit.TryGetValue(commitId, out var existing))
        {
            return existing;
        }

        colorsByCommit[commitId] = color;
        return color;
    }

    private int GetColor(string commitId)
    {
        if (colorsByCommit.TryGetValue(commitId, out var color))
        {
            return color;
        }

        colorsByCommit[commitId] = nextColor;
        return nextColor++;
    }

    private static double LaneX(int lane) => GraphLeft + lane * LaneSpacing;

    private static GitHistoryGraphPathViewModel CreateLine(double x1, double y1, double x2, double y2, SolidColorBrush brush, double opacity = 1) =>
        new(CreatePath(new Point(x1, y1), new LineSegment { Point = new Point(x2, y2) }), brush, StrokeThickness, opacity);

    private static GitHistoryGraphPathViewModel CreateCurve(double x1, double y1, double x2, double y2, SolidColorBrush brush, double opacity = 1)
    {
        var verticalDistance = Math.Max(1, y2 - y1);
        var segment = new BezierSegment
        {
            Point1 = new Point(x1, y1 + verticalDistance * 0.58),
            Point2 = new Point(x2, y2 - verticalDistance * 0.58),
            Point3 = new Point(x2, y2)
        };
        return new GitHistoryGraphPathViewModel(CreatePath(new Point(x1, y1), segment), brush, StrokeThickness, opacity);
    }

    private static PathGeometry CreatePath(Point startPoint, PathSegment segment)
    {
        var figure = new PathFigure
        {
            StartPoint = startPoint,
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(segment);

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}

public sealed record GitHistoryRefBadgeViewModel(string Text, SolidColorBrush Foreground, SolidColorBrush Background, SolidColorBrush Border)
{
    public static ImmutableArray<GitHistoryRefBadgeViewModel> FromDecorations(string decorations)
    {
        if (string.IsNullOrWhiteSpace(decorations))
        {
            return [];
        }

        return decorations
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(12)
            .Select(Create)
            .ToImmutableArray();
    }

    private static GitHistoryRefBadgeViewModel Create(string text)
    {
        var foreground = text.StartsWith("tag: ", StringComparison.OrdinalIgnoreCase)
            ? GitHistoryBrushes.TagBrush
            : text.Contains("HEAD", StringComparison.OrdinalIgnoreCase)
                ? GitHistoryBrushes.HeadBrush
                : GitHistoryBrushes.BranchBrush;
        return new GitHistoryRefBadgeViewModel(text, foreground, GitHistoryBrushes.GetSoftBrush(foreground), foreground);
    }
}

internal static class GitHistoryBrushes
{
    private static readonly Color[] LaneColors =
    [
        Color.FromArgb(255, 0, 122, 204),
        Color.FromArgb(255, 249, 115, 22),
        Color.FromArgb(255, 34, 197, 94),
        Color.FromArgb(255, 168, 85, 247),
        Color.FromArgb(255, 236, 72, 153),
        Color.FromArgb(255, 20, 184, 166),
        Color.FromArgb(255, 234, 179, 8),
        Color.FromArgb(255, 100, 116, 139)
    ];

    public static SolidColorBrush Transparent { get; } = new(Color.FromArgb(0, 0, 0, 0));

    public static SolidColorBrush DotStrokeBrush { get; } = new(Color.FromArgb(255, 255, 255, 255));

    public static SolidColorBrush HeadBrush { get; } = new(Color.FromArgb(255, 0, 122, 204));

    public static SolidColorBrush BranchBrush { get; } = new(Color.FromArgb(255, 26, 127, 55));

    public static SolidColorBrush TagBrush { get; } = new(Color.FromArgb(255, 154, 103, 0));

    public static SolidColorBrush GetLaneBrush(int lane) => new(LaneColors[Math.Abs(lane) % LaneColors.Length]);

    public static SolidColorBrush GetSoftBrush(SolidColorBrush brush)
    {
        var color = brush.Color;
        return new SolidColorBrush(Color.FromArgb(32, color.R, color.G, color.B));
    }
}

public sealed partial class FileDiffTabViewModel : ObservableObject
{
    public FileDiffTabViewModel(
        string documentId,
        string path,
        string language,
        DiffFileStatus status,
        ImmutableArray<DiffLine> diffOnlyLines,
        ImmutableArray<DiffLine> fullDiffLines,
        ImmutableArray<FileDiffLineViewModel> diffLines,
        string fullText,
        ImmutableArray<DiffLine> fullFileLines,
        ImmutableArray<DiffLine> annotatedFullFileLines,
        ImmutableArray<CodeFoldRegion> foldRegions,
        FileDiffDisplayMode displayMode)
    {
        DocumentId = documentId;
        Path = path;
        Language = language;
        Status = status;
        DiffOnlyLines = diffOnlyLines;
        FullDiffLines = fullDiffLines;
        DiffLines = diffLines;
        FullFileLines = fullFileLines;
        AnnotatedFullFileLines = annotatedFullFileLines;
        FoldRegions = foldRegions;
        this.fullText = fullText;
        this.displayMode = displayMode;
    }

    public string DocumentId { get; }

    public string Path { get; }

    public string Language { get; }

    public DiffFileStatus Status { get; }

    public ImmutableArray<DiffLine> DiffOnlyLines { get; }

    public ImmutableArray<DiffLine> FullDiffLines { get; }

    public ImmutableArray<FileDiffLineViewModel> DiffLines { get; }

    public ImmutableArray<DiffLine> FullFileLines { get; }

    public ImmutableArray<DiffLine> AnnotatedFullFileLines { get; }

    public ImmutableArray<CodeFoldRegion> FoldRegions { get; }

    public ImmutableArray<DiffLine> CurrentDiffOnlyLines => DiffScopeMode == FileDiffScopeMode.FullFileDiff ? FullDiffLines : DiffOnlyLines;

    public ImmutableArray<DiffLine> CurrentFullFileLines => IsDiffAnnotationEnabled ? AnnotatedFullFileLines : FullFileLines;

    public string SummaryText => $"{Status} | {Language} | {CurrentDiffOnlyLines.Length:N0} diff lines";

    public string StatusText => $"{Path} | {SummaryText}";

    public Visibility DiffOnlyVisibility => DisplayMode == FileDiffDisplayMode.DiffOnly ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FullFileVisibility => DisplayMode == FileDiffDisplayMode.FullFile ? Visibility.Visible : Visibility.Collapsed;

    public bool IsDiffOnlyMode => DisplayMode == FileDiffDisplayMode.DiffOnly;

    public bool IsFullFileMode => DisplayMode == FileDiffDisplayMode.FullFile;

    public bool IsChangesDiffScopeMode => DiffScopeMode == FileDiffScopeMode.Changes;

    public bool IsFullFileDiffScopeMode => DiffScopeMode == FileDiffScopeMode.FullFileDiff;

    public string FullFileHeader => string.IsNullOrWhiteSpace(FullText)
        ? "Full file content unavailable"
        : $"{Path} | {FullFileLines.Length:N0} lines | {FoldRegions.Length:N0} fold regions | diff annotations {(IsDiffAnnotationEnabled ? "on" : "off")}";

    public string DiffOnlyHeader => DiffScopeMode == FileDiffScopeMode.FullFileDiff
        ? $"{Path} | full file diff | {CurrentDiffOnlyLines.Length:N0} lines"
        : $"{Path} | changed hunks | {CurrentDiffOnlyLines.Length:N0} lines";

    public string RefreshKey => $"{DisplayMode}:{DiffScopeMode}:{IsDiffAnnotationEnabled}:{CodeFontSize:0.##}";

    public ImmutableArray<double> CodeFontSizeOptions { get; } = [10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28];

    public string CodeFontSizeText => $"{CodeFontSize:0} px";

    public bool CanDecreaseCodeFontSize => CodeFontSize > CodeFontSizeOptions[0];

    public bool CanIncreaseCodeFontSize => CodeFontSize < CodeFontSizeOptions[^1];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffOnlyVisibility))]
    [NotifyPropertyChangedFor(nameof(FullFileVisibility))]
    [NotifyPropertyChangedFor(nameof(IsDiffOnlyMode))]
    [NotifyPropertyChangedFor(nameof(IsFullFileMode))]
    [NotifyPropertyChangedFor(nameof(RefreshKey))]
    private FileDiffDisplayMode displayMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentDiffOnlyLines))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(DiffOnlyHeader))]
    [NotifyPropertyChangedFor(nameof(IsChangesDiffScopeMode))]
    [NotifyPropertyChangedFor(nameof(IsFullFileDiffScopeMode))]
    [NotifyPropertyChangedFor(nameof(RefreshKey))]
    private FileDiffScopeMode diffScopeMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentFullFileLines))]
    [NotifyPropertyChangedFor(nameof(FullFileHeader))]
    [NotifyPropertyChangedFor(nameof(RefreshKey))]
    private bool isDiffAnnotationEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullFileHeader))]
    private string fullText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeFontSizeText))]
    [NotifyPropertyChangedFor(nameof(CanDecreaseCodeFontSize))]
    [NotifyPropertyChangedFor(nameof(CanIncreaseCodeFontSize))]
    [NotifyPropertyChangedFor(nameof(RefreshKey))]
    private double codeFontSize = 15;

    public void SetDisplayMode(FileDiffDisplayMode mode) => DisplayMode = mode;

    public void SetDiffScopeMode(FileDiffScopeMode mode) => DiffScopeMode = mode;

    public void SetDiffAnnotationVisibility(bool isEnabled) => IsDiffAnnotationEnabled = isEnabled;

    public void IncreaseCodeFontSize() => SetCodeFontSize(CodeFontSize + 1);

    public void DecreaseCodeFontSize() => SetCodeFontSize(CodeFontSize - 1);

    public void SetCodeFontSize(double value)
    {
        if (CodeFontSizeOptions.Length == 0)
        {
            CodeFontSize = value;
            return;
        }

        var minimum = CodeFontSizeOptions[0];
        var maximum = CodeFontSizeOptions[^1];
        CodeFontSize = Math.Clamp(value, minimum, maximum);
    }

    public static FileDiffTabViewModel FromDocument(
        DiffDocumentSnapshot document,
        DiffDocumentSnapshot fullFileDocument,
        string fullText,
        ImmutableArray<CodeFoldRegion> foldRegions,
        FileDiffDisplayMode displayMode) => new(
        document.Id.Value,
        document.Metadata.Path,
        document.Metadata.Language,
        document.Metadata.Status,
        document.Lines,
        CreateFullDiffLines(document, fullFileDocument),
        document.Lines.Select(FileDiffLineViewModel.FromLine).ToImmutableArray(),
        fullText,
        fullFileDocument.Lines,
        CreateAnnotatedFullFileLines(document, fullFileDocument),
        foldRegions,
        displayMode);

    private static ImmutableArray<DiffLine> CreateAnnotatedFullFileLines(DiffDocumentSnapshot diffDocument, DiffDocumentSnapshot fullFileDocument)
    {
        if (fullFileDocument.Lines.IsDefaultOrEmpty)
        {
            return [];
        }

        var diffLineByNewNumber = diffDocument.Lines
            .Where(line => line.NewLineNumber is > 0 && IsVisibleFullFileAnnotationKind(line.Kind))
            .GroupBy(line => line.NewLineNumber!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(AnnotationPriority).First());
        return fullFileDocument.Lines
            .Select((line, index) =>
            {
                var lineNumber = line.NewLineNumber ?? index + 1;
                return diffLineByNewNumber.TryGetValue(lineNumber, out var diffLine)
                    ? ReindexLine(line with
                    {
                        Kind = diffLine.Kind,
                        InlineSpans = diffLine.InlineSpans
                    }, index)
                    : ReindexLine(line with { Kind = DiffLineKind.Context }, index);
            })
            .ToImmutableArray();
    }

    private static ImmutableArray<DiffLine> CreateFullDiffLines(DiffDocumentSnapshot diffDocument, DiffDocumentSnapshot fullFileDocument)
    {
        if (fullFileDocument.Lines.IsDefaultOrEmpty)
        {
            return diffDocument.Lines;
        }

        var fullLinesByNumber = fullFileDocument.Lines
            .Select((line, index) => (LineNumber: line.NewLineNumber ?? index + 1, Line: line))
            .ToDictionary(pair => pair.LineNumber, pair => pair.Line);
        var builder = ImmutableArray.CreateBuilder<DiffLine>();
        var nextFullLineNumber = 1;

        foreach (var diffLine in diffDocument.Lines)
        {
            if (diffLine.Kind == DiffLineKind.Imaginary)
            {
                continue;
            }

            if (diffLine.Kind == DiffLineKind.Metadata)
            {
                builder.Add(ReindexLine(diffLine, builder.Count));
                continue;
            }

            if (diffLine.NewLineNumber is { } newLineNumber)
            {
                AddFullContextLines(builder, fullLinesByNumber, nextFullLineNumber, newLineNumber - 1);
                if (fullLinesByNumber.TryGetValue(newLineNumber, out var fullLine))
                {
                    builder.Add(ReindexLine(fullLine with
                    {
                        Kind = IsVisibleFullFileAnnotationKind(diffLine.Kind) ? diffLine.Kind : DiffLineKind.Context,
                        OldLineNumber = diffLine.OldLineNumber,
                        NewLineNumber = diffLine.NewLineNumber,
                        InlineSpans = diffLine.InlineSpans
                    }, builder.Count));
                }
                else
                {
                    builder.Add(ReindexLine(diffLine, builder.Count));
                }

                nextFullLineNumber = Math.Max(nextFullLineNumber, newLineNumber + 1);
                continue;
            }

            if (diffLine.Kind == DiffLineKind.Deleted)
            {
                builder.Add(ReindexLine(diffLine, builder.Count));
            }
        }

        AddFullContextLines(builder, fullLinesByNumber, nextFullLineNumber, fullLinesByNumber.Count);
        return builder.ToImmutable();
    }

    private static void AddFullContextLines(ImmutableArray<DiffLine>.Builder builder, IReadOnlyDictionary<int, DiffLine> linesByNumber, int firstLineNumber, int lastLineNumber)
    {
        for (var lineNumber = Math.Max(1, firstLineNumber); lineNumber <= lastLineNumber; lineNumber++)
        {
            if (linesByNumber.TryGetValue(lineNumber, out var line))
            {
                builder.Add(ReindexLine(line with
                {
                    OldLineNumber = lineNumber,
                    NewLineNumber = lineNumber,
                    Kind = DiffLineKind.Context
                }, builder.Count));
            }
        }
    }

    private static DiffLine ReindexLine(DiffLine line, int index) => line with { Index = index };

    private static bool IsVisibleFullFileAnnotationKind(DiffLineKind kind) =>
        kind is DiffLineKind.Added or DiffLineKind.Modified or DiffLineKind.Moved or DiffLineKind.Conflict or DiffLineKind.Ignored;

    private static int AnnotationPriority(DiffLine line) => line.Kind switch
    {
        DiffLineKind.Conflict => 0,
        DiffLineKind.Modified => 1,
        DiffLineKind.Moved => 2,
        DiffLineKind.Added => 3,
        DiffLineKind.Ignored => 4,
        _ => 9
    };
}

public sealed record FileDiffLineViewModel(
    string OldLineNumberText,
    string NewLineNumberText,
    string Marker,
    string Text,
    string KindText)
{
    public static FileDiffLineViewModel FromLine(DiffLine line) => new(
        line.OldLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        line.NewLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        MarkerFor(line.Kind),
        line.Text,
        line.Kind.ToString());

    private static string MarkerFor(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Deleted => "-",
        DiffLineKind.Ignored => "~",
        DiffLineKind.Moved => ">",
        DiffLineKind.Conflict => "!",
        DiffLineKind.Metadata => "@",
        DiffLineKind.Imaginary => "...",
        _ => string.Empty
    };
}
