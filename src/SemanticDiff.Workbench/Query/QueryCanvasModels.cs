using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Rendering;
using SemanticDiff.Semantics;
using SemanticDiff.Workbench.Symbols;

namespace SemanticDiff.Workbench.Query;

public enum QueryCanvasScope
{
    Diff,
    Workspace
}

public enum QueryCanvasResultKind
{
    Empty,
    Files,
    Symbols,
    Mixed,
    Error
}

public sealed record QueryCanvasScopeOption(
    QueryCanvasScope Scope,
    string DisplayName,
    string Description);

public sealed record QueryCanvasSample(
    string DisplayName,
    string Query,
    string Description,
    QueryCanvasScope PreferredScope = QueryCanvasScope.Diff);

public static class QueryCanvasSampleCatalog
{
    public static QueryCanvasSample Default { get; } = new(
        "Changed C# files",
        "ChangedFiles.Where(f => f.Language == \"C#\").OrderByDescending(f => f.AddedLines).Take(80)",
        "Changed C# files ordered by added lines.");

    public static ImmutableArray<QueryCanvasSample> All { get; } =
    [
        Default,
        new(
            "Large changed files",
            "ChangedFiles.OrderByDescending(f => f.LineCount).Take(60)",
            "Largest changed files in the current diff scope."),
        new(
            "Added tests",
            "ChangedFiles.Where(f => f.IsAdded && f.Path.Contains(\"Tests\")).OrderBy(f => f.Path).Take(100)",
            "New test files in the current diff scope."),
        new(
            "Deleted files",
            "ChangedFiles.Where(f => f.IsDeleted).OrderBy(f => f.Path).Take(80)",
            "Deleted files from the current diff scope."),
        new(
            "Hot folders",
            "ChangedFiles.Where(f => f.Directory.Contains(\"Controls\") || f.Directory.Contains(\"Rendering\")).OrderBy(f => f.Directory).Take(120)",
            "Changes concentrated in Controls or Rendering folders."),
        new(
            "Workspace C# files",
            "WorkspaceFiles.Where(f => f.Language == \"C#\").OrderBy(f => f.Path).Take(160)",
            "C# files from the loaded MSBuild workspace.",
            QueryCanvasScope.Workspace),
        new(
            "Workspace XAML files",
            "WorkspaceFiles.Where(f => f.Language == \"XAML\" || f.Path.EndsWith(\".xaml\")).OrderBy(f => f.Path).Take(160)",
            "XAML files from the loaded MSBuild workspace.",
            QueryCanvasScope.Workspace),
        new(
            "Workspace tests",
            "WorkspaceFiles.Where(f => f.Path.Contains(\"Tests\") || f.Name.EndsWith(\"Tests.cs\")).OrderBy(f => f.Path).Take(180)",
            "Test-related files from the loaded MSBuild workspace.",
            QueryCanvasScope.Workspace),
        new(
            "Changed types map",
            "ChangedSymbols.Where(s => s.Kind == \"Type\").Map().Take(120)",
            "Changed type anchors connected back to their files."),
        new(
            "Changed members map",
            "ChangedSymbols.Where(s => s.Kind == \"Member\").Map().Take(160)",
            "Changed member anchors connected back to their files."),
        new(
            "Linked symbols only",
            "LinkedSymbols.Where(s => s.Links > 1).OrderByDescending(s => s.Links).SymbolsOnly().Take(160)",
            "High-connectivity semantic anchors without file nodes."),
        new(
            "Linked symbols map",
            "LinkedSymbols.Where(s => s.Links > 2).OrderByDescending(s => s.Links).Map().Take(160)",
            "High-connectivity semantic anchors connected to file nodes."),
        new(
            "Symbol namespace slice",
            "Symbols.Where(s => s.Path.Contains(\"Controls\") && s.Line > 0).Map().Take(180)",
            "Symbols declared in paths containing Controls."),
        new(
            "Changed XAML symbols",
            "ChangedSymbols.Where(s => s.File.EndsWith(\".xaml\") || s.Path.EndsWith(\".xaml\")).Map().Take(120)",
            "Changed symbols coming from XAML files.")
    ];
}

public sealed record QueryCanvasContext(
    ImmutableArray<DiffDocumentSnapshot> DiffDocuments,
    ImmutableArray<DiffDocumentSnapshot> WorkspaceDocuments,
    ImmutableArray<SemanticNavigationItem> Symbols,
    SemanticGraph SemanticGraph,
    GraphLayoutMode LayoutMode,
    GraphGroupingMode GroupingMode,
    EdgeProjectionOptions EdgeOptions,
    DiffAnnotationVisibilityState AnnotationVisibility,
    SymbolGraphViewMode DefaultSymbolViewMode = SymbolGraphViewMode.FilesAndSymbols)
{
    public ImmutableArray<DiffDocumentSnapshot> DocumentsForScope(QueryCanvasScope scope) => scope switch
    {
        QueryCanvasScope.Workspace when !WorkspaceDocuments.IsDefaultOrEmpty => WorkspaceDocuments,
        _ => DiffDocuments.IsDefault ? ImmutableArray<DiffDocumentSnapshot>.Empty : DiffDocuments
    };
}

public sealed record QueryCanvasExecutionResult(
    DiffCanvasScene Scene,
    QueryCanvasResultKind Kind,
    string StatusText,
    string DetailText,
    bool HasError = false)
{
    public static QueryCanvasExecutionResult Error(string message) => new(
        DiffCanvasScene.FromDocuments([]),
        QueryCanvasResultKind.Error,
        message,
        message,
        HasError: true);
}
