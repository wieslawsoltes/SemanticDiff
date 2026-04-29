# SemanticDiff.Workbench

UI-framework-free orchestration and builder services extracted from `SemanticDiff.App`.

## Included Services

| Area | Type | Purpose |
| --- | --- | --- |
| File diffs | `FileDiffDocumentBuilder` | Builds changed-hunk, full-diff, annotated full-file, and fold-aware file views from diff/full-file snapshots. |
| Symbol graphs | `SymbolBrowserModel`, `SymbolGraphSceneBuilder` | Owns symbol filtering/facets and builds symbol-only or file+symbol graph scenes. |
| Query canvas | `QueryCanvasEngine`, `QueryCanvasCompletionProvider` | Executes a safe LINQ-style query subset over files, workspace files, and semantic symbols, then builds file-node, symbol-only, or hybrid semantic-map scenes. |
| Blame | `BlameChangeGraphBuilder` | Builds diff-node style blame history graphs from file blame commits. |
| Git history | `GitHistoryLaneLayout` | Computes reusable visual lanes for merge/split history rendering. |
| Workspace loading | `RepositoryDiffLoader` | Loads Git diff documents and applies review/noise/context/inline annotation preparation. |
| Workspace cache | `DiffWorkspaceCache` | Caches branch workspace documents, scene, semantic graph, layout, selected file, and Git snapshot state. |
| Workspace documents | `WorkspaceDocumentManager<TTab>` | Adds, selects, finds, and closes workspace document tabs independent of any UI framework collection type. |
| Git references | `GitReferenceBrowserModel<TBranch,TReviewRequest>` | Filters and counts branch/PR/MR references and owns reference tree expansion state. |
| Reviews | `ReviewThreadFilter<TThread>`, `ReviewWorkflowModel<TThread>` | Filters review threads, preserves selection, and owns loaded review thread status/state. |

## Dependency Boundary

`SemanticDiff.Workbench` intentionally has no Uno, WinUI, Windows, or SkiaSharp view-host dependency. It can be tested and consumed from command-line tools, services, or alternate UI hosts.

Allowed dependencies:

| Project | Reason |
| --- | --- |
| `SemanticDiff.Core` | Shared diff, Git, annotation, semantic, and app-neutral records. |
| `SemanticDiff.Diff` | Diff document loading/transformation pipeline. |
| `SemanticDiff.Git` | Git diff/reference/review service contracts. |
| `SemanticDiff.Layout` | Graph layout options/results. |
| `SemanticDiff.Rendering` | Scene DTOs used by builders. |
| `SemanticDiff.Semantics` | Semantic graph and symbol navigation models. |

Forbidden dependencies are enforced by tests: `Microsoft.UI`, `Windows`, `SkiaSharp.Views`, and Uno packages.

## Usage Example

```csharp
var loader = new RepositoryDiffLoader();
var result = await loader.LoadAsync(
    new RepositoryDiffLoadRequest(
        repositoryPath,
        GitDiffScope.Branch,
        BaseRef: "main",
        HeadRef: "feature/work",
        DiffContextMode.ChangedHunks,
        DiffReviewMode.Precise,
        CollapseUnchangedContext: true),
    cancellationToken);

var cache = new DiffWorkspaceCache(capacity: 12);
var symbols = new SymbolBrowserModel();
symbols.SetItems(new SemanticNavigationIndex().Build(semanticGraph, result.Documents));
```

Query Canvas example:

```csharp
var queryContext = new QueryCanvasContext(
    DiffDocuments: result.Documents,
    WorkspaceDocuments: result.Documents,
    Symbols: symbols.AllItems,
    SemanticGraph: semanticGraph,
    LayoutMode: GraphLayoutMode.Layered,
    GroupingMode: GraphGroupingMode.Semantic,
    EdgeOptions: EdgeProjectionOptions.Default,
    AnnotationVisibility: DiffAnnotationVisibilityState.Default);

var queryResult = new QueryCanvasEngine().Execute(
    "ChangedSymbols.Where(s => s.Links > 2).Map().Take(120)",
    queryContext,
    QueryCanvasScope.Diff);
```

## NuGet Packaging

This project is packable as `SemanticDiff.Workbench`. Common NuGet metadata, repository metadata, license expression, symbols, and the default version are inherited from the repository `Directory.Build.props`.

| Metadata | Value |
| --- | --- |
| Package | `SemanticDiff.Workbench` |
| Version | `0.1.0-preview.1` by default, overridable with `VersionPrefix` and `VersionSuffix` |
| Readme | This `README.md` is included at the package root |
| Symbols | `snupkg` symbol package generation is enabled |
| License | MIT license expression |

Use `dotnet pack src/SemanticDiff.Workbench/SemanticDiff.Workbench.csproj -c Release` to create the package.
