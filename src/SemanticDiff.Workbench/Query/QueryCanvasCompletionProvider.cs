using System.Collections.Immutable;
using SemanticDiff.Diff;

namespace SemanticDiff.Workbench.Query;

public sealed class QueryCanvasCompletionProvider : ICodeCompletionProvider
{
    private static readonly ImmutableArray<CodeCompletionItem> RootItems =
    [
        Root("Files", "Files in the selected query scope"),
        Root("ChangedFiles", "Files with changed status or changed lines"),
        Root("DiffFiles", "Files from the active diff graph"),
        Root("WorkspaceFiles", "Files from the loaded MSBuild workspace"),
        Root("Nodes", "Alias for Files"),
        Root("Symbols", "Semantic anchors from the active semantic model"),
        Root("ChangedSymbols", "Semantic anchors touched by the diff"),
        Root("LinkedSymbols", "Semantic anchors with semantic graph edges")
    ];

    private static readonly ImmutableArray<CodeCompletionItem> OperationItems =
    [
        Snippet("Where", "Where(x => x.Path.Contains(\"\"))", "Filter query results"),
        Snippet("Take", "Take(80)", "Limit rendered results"),
        Snippet("Skip", "Skip(20)", "Skip initial results"),
        Snippet("OrderBy", "OrderBy(x => x.Name)", "Sort ascending"),
        Snippet("OrderByDescending", "OrderByDescending(x => x.LineCount)", "Sort descending"),
        Snippet("Map", "Map()", "Render symbols connected to their files"),
        Snippet("Graph", "Graph()", "Render a graph view"),
        Snippet("SymbolsOnly", "SymbolsOnly()", "Render only symbol nodes"),
        Snippet("Dump", "Dump()", "LINQPad-style no-op that renders the current result"),
        Snippet("ToList", "ToList()", "Materialize as a list before rendering")
    ];

    private static readonly ImmutableArray<CodeCompletionItem> FilePropertyItems =
    [
        Property("Path", "Repository-relative file path"),
        Property("Name", "File name"),
        Property("Directory", "Containing directory"),
        Property("OldPath", "Previous file path for renames"),
        Property("Language", "Detected language"),
        Property("Status", "Diff status"),
        Property("AddedLines", "Added line count"),
        Property("DeletedLines", "Deleted line count"),
        Property("LineCount", "Rendered line count"),
        Property("IsChanged", "True when the file has changes"),
        Property("IsAdded", "True when the file is added"),
        Property("IsDeleted", "True when the file is deleted")
    ];

    private static readonly ImmutableArray<CodeCompletionItem> SymbolPropertyItems =
    [
        Property("Name", "Symbol display name"),
        Property("DisplayName", "Symbol display name"),
        Property("Kind", "Semantic anchor kind"),
        Property("KindText", "Semantic anchor kind text"),
        Property("Path", "Declaring file path"),
        Property("File", "Declaring file path"),
        Property("Line", "Declaration line"),
        Property("Column", "Declaration column"),
        Property("Links", "Incident semantic edge count"),
        Property("IncidentEdgeCount", "Incident semantic edge count"),
        Property("IsChanged", "True when touched by the diff"),
        Property("IsLinked", "True when connected to other anchors")
    ];

    private static readonly ImmutableArray<CodeCompletionItem> ExampleItems = QueryCanvasSampleCatalog.All
        .Select(sample => Snippet($"sample: {sample.DisplayName}", sample.Query, sample.Description))
        .ToImmutableArray();

    public ValueTask<CodeCompletionResult> GetCompletionsAsync(CodeCompletionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = request.Document.ToSourceText();
        var line = request.LineIndex >= 0 && request.LineIndex < request.Document.Lines.Length
            ? request.Document.Lines[request.LineIndex].Text ?? string.Empty
            : string.Empty;
        var column = Math.Clamp(request.Column, 0, line.Length);
        var range = FindReplacementRange(line, column);
        var filter = line[range.Start..column];
        var candidates = SelectCandidates(text, line, column)
            .Where(item => Matches(item, filter, request.IsExplicit))
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.EffectiveSortText, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(request.MaxItems, 1, 200))
            .ToImmutableArray();
        return new ValueTask<CodeCompletionResult>(new CodeCompletionResult(
            candidates,
            range.Start,
            Math.Max(0, range.End - range.Start),
            filter,
            IsIncomplete: false));
    }

    private static IEnumerable<CodeCompletionItem> SelectCandidates(string text, string line, int column)
    {
        if (IsAfterDot(line, column))
        {
            return OperationItems.Concat(FilePropertyItems).Concat(SymbolPropertyItems);
        }

        if (LooksLikeSymbolQuery(text))
        {
            return SymbolPropertyItems.Concat(OperationItems).Concat(ExampleItems);
        }

        if (LooksLikeFileQuery(text))
        {
            return FilePropertyItems.Concat(OperationItems).Concat(ExampleItems);
        }

        return RootItems.Concat(OperationItems).Concat(ExampleItems);
    }

    private static bool LooksLikeSymbolQuery(string text) =>
        text.Contains("Symbols", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("ChangedSymbols", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("LinkedSymbols", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("s =>", StringComparison.Ordinal);

    private static bool LooksLikeFileQuery(string text) =>
        text.Contains("Files", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("WorkspaceFiles", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("f =>", StringComparison.Ordinal);

    private static bool IsAfterDot(string line, int column)
    {
        var index = Math.Max(0, column - 1);
        while (index >= 0 && (char.IsLetterOrDigit(line[index]) || line[index] == '_'))
        {
            index--;
        }

        return index >= 0 && line[index] == '.';
    }

    private static (int Start, int End) FindReplacementRange(string line, int column)
    {
        var start = column;
        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_'))
        {
            start--;
        }

        var end = column;
        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
        {
            end++;
        }

        return (start, end);
    }

    private static bool Matches(CodeCompletionItem item, string filter, bool isExplicit) =>
        isExplicit ||
        string.IsNullOrWhiteSpace(filter) ||
        item.EffectiveFilterText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        item.DisplayText.StartsWith(filter, StringComparison.OrdinalIgnoreCase);

    private static CodeCompletionItem Root(string text, string description) => new(
        text,
        text,
        CodeCompletionItemKind.Variable,
        description,
        SortText: $"0_{text}",
        Priority: 10);

    private static CodeCompletionItem Property(string text, string description) => new(
        text,
        text,
        CodeCompletionItemKind.Property,
        description,
        SortText: $"1_{text}",
        Priority: 8);

    private static CodeCompletionItem Snippet(string text, string insertionText, string description) => new(
        text,
        insertionText,
        CodeCompletionItemKind.Snippet,
        description,
        SortText: $"2_{text}",
        Priority: 6);
}
