using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Git;
using SemanticDiff.Layout;
using SemanticDiff.Rendering;
using SemanticDiff.Semantics;
using SemanticDiff.Semantics.Roslyn;
using SemanticDiff.Semantics.Xaml;
using SemanticDiff.Workbench.Review;
using SemanticDiff.Workbench.Symbols;
using SemanticDiff.Workbench.Workspace;
using Windows.UI;

namespace SemanticDiff.App.ViewModels;

public sealed partial class MainViewModel
{
    public FocusRequest? FocusSemanticItem(SemanticNavigationItemViewModel? item)
    {
        if (item is null)
        {
            return null;
        }

        AddDiagnostic("Info", $"Focused {item.DisplayName}");
        return new FocusRequest(item.DocumentId, item.Line);
    }

    public SemanticNavigationItemViewModel? FindSemanticItemForLineContext(string documentId, int? lineNumber, string? symbolText)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return null;
        }

        var sourceDocumentId = ResolveSourceDocumentId(documentId);
        var documentItems = allSemanticNavigationItems
            .Where(item => string.Equals(item.DocumentId.Value, sourceDocumentId, StringComparison.Ordinal))
            .ToArray();
        if (documentItems.Length == 0)
        {
            return null;
        }

        var normalizedSymbol = NormalizeSymbolSearchText(symbolText);
        IEnumerable<SemanticNavigationItem> candidates = documentItems;
        if (lineNumber is > 0)
        {
            var exactLine = documentItems
                .Where(item => item.Line == lineNumber.Value)
                .ToArray();
            if (exactLine.Length > 0)
            {
                return SemanticNavigationItemViewModel.FromItem(ChooseSemanticLineCandidate(exactLine, normalizedSymbol, lineNumber.Value));
            }

            if (!string.IsNullOrWhiteSpace(normalizedSymbol))
            {
                var nearby = documentItems
                    .Where(item => Math.Abs(item.Line - lineNumber.Value) <= 2 && SymbolNameMatches(item.DisplayName, normalizedSymbol))
                    .ToArray();
                if (nearby.Length > 0)
                {
                    return SemanticNavigationItemViewModel.FromItem(ChooseSemanticLineCandidate(nearby, normalizedSymbol, lineNumber.Value));
                }
            }

            candidates = documentItems
                .Where(item => item.Line <= lineNumber.Value)
                .OrderByDescending(item => item.Line)
                .Take(12);
        }

        if (!string.IsNullOrWhiteSpace(normalizedSymbol))
        {
            var sameDocumentMatch = documentItems
                .Where(item => SymbolNameMatches(item.DisplayName, normalizedSymbol))
                .ToArray();
            if (sameDocumentMatch.Length > 0)
            {
                return SemanticNavigationItemViewModel.FromItem(ChooseSemanticLineCandidate(sameDocumentMatch, normalizedSymbol, lineNumber));
            }

            var globalMatch = ChooseGlobalSymbolCandidate(allSemanticNavigationItems, normalizedSymbol);
            if (globalMatch is not null)
            {
                return SemanticNavigationItemViewModel.FromItem(globalMatch);
            }
        }

        var fallbackCandidates = candidates.ToArray();
        if (fallbackCandidates.Length == 0 && lineNumber is > 0)
        {
            fallbackCandidates = documentItems
                .OrderBy(item => Math.Abs(item.Line - lineNumber.Value))
                .Take(12)
                .ToArray();
        }

        var fallback = fallbackCandidates
            .OrderByDescending(item => item.IsChanged)
            .ThenByDescending(item => item.IsLinked)
            .ThenByDescending(item => item.IncidentEdgeCount)
            .FirstOrDefault();
        return fallback is null ? null : SemanticNavigationItemViewModel.FromItem(fallback);
    }

    public void OpenSymbolGraphFromCurrentFilters()
    {
        var selection = symbolBrowser.Selection;
        OpenSymbolGraphTab(
            "Symbols graph",
            "Current symbol filters",
            allSemanticNavigationItems,
            $"filtered:{SymbolSearchText}|{selection.ScopeKey}|{selection.KindKey}|{selection.DocumentId}",
            initialSearchText: SymbolSearchText,
            initialScopeKey: selection.ScopeKey,
            initialKindKey: selection.KindKey,
            initialDocumentId: selection.DocumentId);
    }

    public void OpenSemanticMapFromCurrentFilters()
    {
        var selection = symbolBrowser.Selection;
        OpenSymbolGraphTab(
            "Semantic map",
            "Files connected to current symbol filters",
            allSemanticNavigationItems,
            $"filtered:{SymbolSearchText}|{selection.ScopeKey}|{selection.KindKey}|{selection.DocumentId}",
            initialSearchText: SymbolSearchText,
            initialScopeKey: selection.ScopeKey,
            initialKindKey: selection.KindKey,
            initialDocumentId: selection.DocumentId,
            viewMode: SymbolGraphViewMode.FilesAndSymbols);
    }

    public void OpenSymbolGraphForSymbol(SemanticNavigationItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var sourceItem = allSemanticNavigationItems.FirstOrDefault(candidate => string.Equals(candidate.AnchorId, item.AnchorId, StringComparison.Ordinal));
        if (sourceItem is null)
        {
            AddDiagnostic("Warning", $"No semantic symbol found for {item.DisplayName}");
            return;
        }

        var neighborhood = CreateSymbolNeighborhood(sourceItem.AnchorId);
        var title = $"Symbols {ShortenTitle(sourceItem.DisplayName)}";
        OpenSymbolGraphTab(
            title,
            $"{sourceItem.KindText} | {sourceItem.Path}:{sourceItem.Line}",
            neighborhood,
            $"symbol:{sourceItem.AnchorId}",
            focusAnchorId: sourceItem.AnchorId);
    }

    public void OpenSemanticMapForSymbol(SemanticNavigationItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var sourceItem = allSemanticNavigationItems.FirstOrDefault(candidate => string.Equals(candidate.AnchorId, item.AnchorId, StringComparison.Ordinal));
        if (sourceItem is null)
        {
            AddDiagnostic("Warning", $"No semantic symbol found for {item.DisplayName}");
            return;
        }

        OpenSymbolGraphTab(
            $"Map {ShortenTitle(sourceItem.DisplayName)}",
            $"{sourceItem.KindText} | {sourceItem.Path}:{sourceItem.Line}",
            CreateSymbolNeighborhood(sourceItem.AnchorId),
            $"symbol:{sourceItem.AnchorId}",
            focusAnchorId: sourceItem.AnchorId,
            viewMode: SymbolGraphViewMode.FilesAndSymbols);
    }

    public void OpenSymbolGraphForDocumentFacet(SemanticSymbolDocumentFacetViewModel? facet)
    {
        if (facet is null)
        {
            return;
        }

        OpenSymbolGraphForDocumentId(facet.DocumentId);
    }

    public void OpenSemanticMapForDocumentFacet(SemanticSymbolDocumentFacetViewModel? facet)
    {
        if (facet is null)
        {
            return;
        }

        OpenSymbolGraphForDocumentId(facet.DocumentId, SymbolGraphViewMode.FilesAndSymbols);
    }

    public void OpenSymbolGraphForExplorerNode(FileExplorerNodeViewModel? node)
    {
        OpenSymbolGraphForExplorerNode(node, SymbolGraphViewMode.SymbolsOnly);
    }

    public void OpenSemanticMapForExplorerNode(FileExplorerNodeViewModel? node)
    {
        OpenSymbolGraphForExplorerNode(node, SymbolGraphViewMode.FilesAndSymbols);
    }

    private void OpenSymbolGraphForExplorerNode(FileExplorerNodeViewModel? node, SymbolGraphViewMode viewMode)
    {
        if (node is null)
        {
            return;
        }

        if (node.IsFile)
        {
            if (string.IsNullOrWhiteSpace(node.DocumentId))
            {
                OpenSymbolGraphForPath(node.Path, viewMode);
                return;
            }

            OpenSymbolGraphForDocumentId(node.DocumentId, viewMode);
            return;
        }

        OpenSymbolGraphForPathPrefix(node.Path, viewMode);
    }

    public void OpenSymbolGraphForDocumentId(string? documentId)
    {
        OpenSymbolGraphForDocumentId(documentId, SymbolGraphViewMode.SymbolsOnly);
    }

    public void OpenSemanticMapForDocumentId(string? documentId)
    {
        OpenSymbolGraphForDocumentId(documentId, SymbolGraphViewMode.FilesAndSymbols);
    }

    private void OpenSymbolGraphForDocumentId(string? documentId, SymbolGraphViewMode viewMode)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        if (SymbolGraphDocumentIds.TryGetAnchorId(documentId) is { Length: > 0 } anchorId)
        {
            var symbol = allSemanticNavigationItems.FirstOrDefault(item => string.Equals(item.AnchorId, anchorId, StringComparison.Ordinal));
            if (symbol is not null)
            {
                OpenSymbolGraphTab(
                    viewMode == SymbolGraphViewMode.FilesAndSymbols ? $"Map {ShortenTitle(symbol.DisplayName)}" : $"Symbols {ShortenTitle(symbol.DisplayName)}",
                    $"{symbol.KindText} | {symbol.Path}:{symbol.Line}",
                    CreateSymbolNeighborhood(symbol.AnchorId),
                    $"symbol:{symbol.AnchorId}",
                    focusAnchorId: symbol.AnchorId,
                    viewMode: viewMode);
                return;
            }
        }

        var sourceDocumentId = ResolveSourceDocumentId(documentId);
        var document = currentDocuments.FirstOrDefault(document => string.Equals(document.Id.Value, sourceDocumentId, StringComparison.Ordinal));
        var path = document?.Metadata.Path
            ?? allSemanticNavigationItems.FirstOrDefault(item => string.Equals(item.DocumentId.Value, sourceDocumentId, StringComparison.Ordinal))?.Path
            ?? sourceDocumentId;
        var items = allSemanticNavigationItems
            .Where(item => string.Equals(item.DocumentId.Value, sourceDocumentId, StringComparison.Ordinal))
            .ToImmutableArray();
        OpenSymbolGraphTab(
            viewMode == SymbolGraphViewMode.FilesAndSymbols ? $"Map {Path.GetFileName(path)}" : $"Symbols {Path.GetFileName(path)}",
            path,
            items,
            $"document:{sourceDocumentId}",
            initialDocumentId: sourceDocumentId,
            viewMode: viewMode);
    }

    private void OpenSymbolGraphForPath(string path, SymbolGraphViewMode viewMode = SymbolGraphViewMode.SymbolsOnly)
    {
        var normalizedPath = NormalizeRepositoryPath(path);
        var document = currentDocuments.FirstOrDefault(document =>
            string.Equals(NormalizeRepositoryPath(document.Metadata.Path), normalizedPath, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(document.Metadata.OldPath) &&
                string.Equals(NormalizeRepositoryPath(document.Metadata.OldPath), normalizedPath, StringComparison.OrdinalIgnoreCase)));
        if (document is not null)
        {
            OpenSymbolGraphForDocumentId(document.Id.Value, viewMode);
            return;
        }

        var items = allSemanticNavigationItems
            .Where(item => string.Equals(NormalizeRepositoryPath(item.Path), normalizedPath, StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();
        OpenSymbolGraphTab(
            viewMode == SymbolGraphViewMode.FilesAndSymbols ? $"Map {Path.GetFileName(path)}" : $"Symbols {Path.GetFileName(path)}",
            path,
            items,
            $"path:{normalizedPath}",
            initialSearchText: path,
            viewMode: viewMode);
    }

    private void OpenSymbolGraphForPathPrefix(string path, SymbolGraphViewMode viewMode = SymbolGraphViewMode.SymbolsOnly)
    {
        var normalizedPath = NormalizeRepositoryPath(path).TrimEnd('/');
        var prefix = string.IsNullOrWhiteSpace(normalizedPath) ? string.Empty : $"{normalizedPath}/";
        var items = allSemanticNavigationItems
            .Where(item =>
            {
                var itemPath = NormalizeRepositoryPath(item.Path);
                return string.Equals(itemPath, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                    itemPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            })
            .ToImmutableArray();
        OpenSymbolGraphTab(
            viewMode == SymbolGraphViewMode.FilesAndSymbols ? $"Map {Path.GetFileName(normalizedPath)}" : $"Symbols {Path.GetFileName(normalizedPath)}",
            string.IsNullOrWhiteSpace(path) ? "Repository symbols" : $"{path}/",
            items,
            $"folder:{normalizedPath}",
            initialSearchText: normalizedPath,
            viewMode: viewMode);
    }

    private void OpenSymbolGraphTab(
        string title,
        string detailText,
        ImmutableArray<SemanticNavigationItem> items,
        string scopeKey,
        string initialSearchText = "",
        string initialScopeKey = SymbolBrowserSelection.AllKey,
        string initialKindKey = SymbolBrowserSelection.AllKey,
        string initialDocumentId = SymbolBrowserSelection.AllKey,
        string? focusAnchorId = null,
        SymbolGraphViewMode viewMode = SymbolGraphViewMode.SymbolsOnly)
    {
        if (items.IsDefaultOrEmpty)
        {
            AddDiagnostic("Warning", "No semantic symbols match this graph request");
            return;
        }

        var tabId = CreateSymbolGraphTabId(scopeKey, viewMode);
        if (SelectWorkspaceTab(tabId))
        {
            return;
        }

        var graph = new SymbolGraphTabViewModel(
            title,
            detailText,
            items,
            currentSemanticGraph,
            currentDocuments,
            initialSearchText,
            initialScopeKey,
            initialKindKey,
            initialDocumentId,
            SelectedLayoutModeOption?.Mode ?? GraphLayoutMode.Layered,
            GraphGroupingMode.Semantic,
            viewMode,
            focusAnchorId);
        var tab = WorkspaceTabViewModel.CreateSymbolGraph(tabId, title, detailText, graph);
        AddWorkspaceTab(tab);
        AddDiagnostic("Info", viewMode == SymbolGraphViewMode.FilesAndSymbols
            ? $"Opened semantic map for {detailText}"
            : $"Opened symbol graph for {detailText}");
    }

    private ImmutableArray<SemanticNavigationItem> CreateSymbolNeighborhood(string anchorId)
    {
        var anchorIds = new HashSet<string>(StringComparer.Ordinal) { anchorId };
        foreach (var edge in currentSemanticGraph.Edges)
        {
            if (string.Equals(edge.SourceAnchorId, anchorId, StringComparison.Ordinal))
            {
                anchorIds.Add(edge.TargetAnchorId);
            }
            else if (string.Equals(edge.TargetAnchorId, anchorId, StringComparison.Ordinal))
            {
                anchorIds.Add(edge.SourceAnchorId);
            }
        }

        var neighborhood = allSemanticNavigationItems
            .Where(item => anchorIds.Contains(item.AnchorId))
            .OrderByDescending(item => string.Equals(item.AnchorId, anchorId, StringComparison.Ordinal))
            .ThenByDescending(item => item.IsChanged)
            .ThenByDescending(item => item.IncidentEdgeCount)
            .ToImmutableArray();
        if (neighborhood.Length > 1)
        {
            return neighborhood;
        }

        var sourceItem = allSemanticNavigationItems.FirstOrDefault(item => string.Equals(item.AnchorId, anchorId, StringComparison.Ordinal));
        return sourceItem is null
            ? neighborhood
            : allSemanticNavigationItems
                .Where(item => string.Equals(item.DocumentId.Value, sourceItem.DocumentId.Value, StringComparison.Ordinal))
                .OrderByDescending(item => string.Equals(item.AnchorId, anchorId, StringComparison.Ordinal))
                .ThenBy(item => item.Line)
                .ToImmutableArray();
    }

    private static string ResolveSourceDocumentId(string documentId) =>
        SymbolGraphDocumentIds.TryGetSourceDocumentId(documentId) ?? documentId;

    private static string CreateSymbolGraphTabId(string scopeKey, SymbolGraphViewMode viewMode) =>
        $"symbols:{viewMode}:{((uint)scopeKey.GetHashCode(StringComparison.Ordinal)).ToString("x8", System.Globalization.CultureInfo.InvariantCulture)}";

    private static SemanticNavigationItem ChooseSemanticLineCandidate(IEnumerable<SemanticNavigationItem> candidates, string normalizedSymbol, int? lineNumber)
    {
        return candidates
            .OrderByDescending(item => !string.IsNullOrWhiteSpace(normalizedSymbol) && SymbolNameMatches(item.DisplayName, normalizedSymbol))
            .ThenBy(item => lineNumber is > 0 ? Math.Abs(item.Line - lineNumber.Value) : 0)
            .ThenByDescending(item => item.IsChanged)
            .ThenByDescending(item => item.IsLinked)
            .ThenByDescending(item => item.IncidentEdgeCount)
            .First();
    }

    private static string NormalizeSymbolSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().Trim('.', ':', ';', ',', '(', ')', '[', ']', '{', '}', '<', '>', '"', '\'', '/', '\\');
        var genericIndex = normalized.IndexOf('<', StringComparison.Ordinal);
        if (genericIndex > 0)
        {
            normalized = normalized[..genericIndex];
        }

        return normalized.Trim();
    }

    private static bool SymbolNameMatches(string displayName, string symbolText)
    {
        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(symbolText) || symbolText.Length < 2)
        {
            return false;
        }

        var simpleName = GetSimpleSymbolName(displayName);
        var simpleSymbol = GetSimpleSymbolName(symbolText);
        return string.Equals(simpleName, simpleSymbol, StringComparison.OrdinalIgnoreCase) ||
            displayName.EndsWith($".{simpleSymbol}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(displayName, symbolText, StringComparison.OrdinalIgnoreCase);
    }

    private static SemanticNavigationItem? ChooseGlobalSymbolCandidate(IEnumerable<SemanticNavigationItem> items, string symbolText)
    {
        if (string.IsNullOrWhiteSpace(symbolText) || symbolText.Length < 3)
        {
            return null;
        }

        var exactMatches = items
            .Where(item => string.Equals(item.DisplayName, symbolText, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactMatches.Length > 0)
        {
            return ChooseSemanticLineCandidate(exactMatches, symbolText, null);
        }

        var simpleSymbol = GetSimpleSymbolName(symbolText);
        var simpleMatches = items
            .Where(item => string.Equals(GetSimpleSymbolName(item.DisplayName), simpleSymbol, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return simpleMatches.Length == 1
            ? simpleMatches[0]
            : null;
    }

    private static string GetSimpleSymbolName(string value)
    {
        var separators = new[] { '.', ':', '/', '\\' };
        var trimmed = value.Trim();
        var index = trimmed.LastIndexOfAny(separators);
        return index >= 0 && index + 1 < trimmed.Length ? trimmed[(index + 1)..] : trimmed;
    }

    private static string ShortenTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "symbol";
        }

        return value.Length <= 28 ? value : $"{value[..12]}...{value[^13..]}";
    }
}
