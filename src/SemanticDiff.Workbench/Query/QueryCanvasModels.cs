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

public sealed record QueryFileMetrics(
    long SizeBytes,
    int? LineCount = null);

public static class QueryCanvasSampleCatalog
{
    public static QueryCanvasSample Default { get; } = new(
        "Changed C# files",
        "ChangedFiles.Where(f => f.Language == \"C#\").OrderByDescending(f => f.AddedLines).Take(80)",
        "Changed C# files ordered by added lines.");

    public static QueryCanvasSample WorkspaceOverview { get; } = new(
        "Workspace overview",
        "WorkspaceFiles.OrderBy(f => f.Path).Take(240)",
        "Browse files from the loaded MSBuild or directory workspace.",
        QueryCanvasScope.Workspace);

    public static QueryCanvasSample WorkspaceLargestFiles { get; } = new(
        "Workspace largest files",
        "WorkspaceFiles.OrderByDescending(f => f.SizeBytes).Take(80)",
        "Largest files in the opened workspace by on-disk byte size.",
        QueryCanvasScope.Workspace);

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
        WorkspaceOverview,
        WorkspaceLargestFiles,
        new(
            "Workspace largest C# files",
            "WorkspaceFiles.Where(f => f.Language == \"C#\").OrderByDescending(f => f.SizeBytes).Take(80)",
            "Largest C# files in the opened workspace.",
            QueryCanvasScope.Workspace),
        new(
            "Workspace largest docs",
            "WorkspaceFiles.Where(f => f.Language == \"Markdown\" || f.Directory.Contains(\"docs\") || f.Directory.Contains(\"documentation\")).OrderByDescending(f => f.SizeBytes).Take(80)",
            "Largest documentation files in the opened workspace.",
            QueryCanvasScope.Workspace),
        new(
            "Workspace vendored files",
            "WorkspaceFiles.Where(f => f.Path.Contains(\"third_party\") || f.Path.Contains(\"externals\") || f.Path.Contains(\"vendor\")).OrderByDescending(f => f.SizeBytes).Take(120)",
            "Large vendored or external files that can dominate workspace analysis.",
            QueryCanvasScope.Workspace),
        new(
            "Workspace generated candidates",
            "WorkspaceFiles.Where(f => f.Name.EndsWith(\".g.cs\") || f.Name.EndsWith(\".Designer.cs\") || f.Directory.Contains(\"Generated\")).OrderByDescending(f => f.SizeBytes).Take(120)",
            "Generated-looking files worth excluding or reviewing separately.",
            QueryCanvasScope.Workspace),
        new(
            "Workspace by language",
            "WorkspaceFiles.OrderBy(f => f.Language).ThenBy(f => f.Path).Take(240)",
            "A language-sorted workspace slice for quick composition review.",
            QueryCanvasScope.Workspace),
        new(
            "Workspace C# files",
            "WorkspaceFiles.Where(f => f.Language == \"C#\").OrderBy(f => f.Path).Take(160)",
            "C# files from the loaded MSBuild or directory workspace.",
            QueryCanvasScope.Workspace),
        new(
            "Workspace XAML files",
            "WorkspaceFiles.Where(f => f.Language == \"XAML\" || f.Path.EndsWith(\".xaml\")).OrderBy(f => f.Path).Take(160)",
            "XAML files from the loaded MSBuild or directory workspace.",
            QueryCanvasScope.Workspace),
        new(
            "Workspace tests",
            "WorkspaceFiles.Where(f => f.Path.Contains(\"Tests\") || f.Name.EndsWith(\"Tests.cs\")).OrderBy(f => f.Path).Take(180)",
            "Test-related files from the loaded MSBuild or directory workspace.",
            QueryCanvasScope.Workspace),
        new(
            "Workspace docs",
            "WorkspaceFiles.Where(f => f.Language == \"Markdown\" || f.Directory.Contains(\"docs\") || f.Directory.Contains(\"documentation\")).OrderBy(f => f.Path).Take(180)",
            "Documentation and Markdown files from the opened workspace.",
            QueryCanvasScope.Workspace),
        new(
            "Workspace build files",
            "WorkspaceFiles.Where(f => f.Language == \"MSBuild\" || f.Name.EndsWith(\".props\") || f.Name.EndsWith(\".targets\") || f.Name.EndsWith(\".sln\")).OrderBy(f => f.Path).Take(180)",
            "Project, solution, props, and targets files from the opened workspace.",
            QueryCanvasScope.Workspace),
        new(
            "Workspace configs",
            "WorkspaceFiles.Where(f => f.Language == \"JSON\" || f.Name.EndsWith(\".yml\") || f.Name.EndsWith(\".yaml\") || f.Name.EndsWith(\".editorconfig\")).OrderBy(f => f.Path).Take(180)",
            "Configuration files from the opened workspace.",
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
    SymbolGraphViewMode DefaultSymbolViewMode = SymbolGraphViewMode.FilesAndSymbols,
    ImmutableDictionary<DiffDocumentId, QueryFileMetrics>? FileMetrics = null)
{
    public ImmutableArray<DiffDocumentSnapshot> DocumentsForScope(QueryCanvasScope scope) => scope switch
    {
        QueryCanvasScope.Workspace when !WorkspaceDocuments.IsDefaultOrEmpty => WorkspaceDocuments,
        _ => DiffDocuments.IsDefault ? ImmutableArray<DiffDocumentSnapshot>.Empty : DiffDocuments
    };

    public QueryFileMetrics? MetricsFor(DiffDocumentSnapshot document) =>
        FileMetrics is not null && FileMetrics.TryGetValue(document.Id, out var metrics)
            ? metrics
            : null;
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
