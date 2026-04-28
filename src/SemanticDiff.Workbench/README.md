# SemanticDiff.Workbench

UI-framework-free orchestration and builder services extracted from `SemanticDiff.App`.

## Included Services

| Area | Type | Purpose |
| --- | --- | --- |
| File diffs | `FileDiffDocumentBuilder` | Builds changed-hunk, full-diff, annotated full-file, and fold-aware file views from diff/full-file snapshots. |
| Symbol graphs | `SymbolBrowserModel`, `SymbolGraphSceneBuilder` | Owns symbol filtering/facets and builds symbol-only or file+symbol graph scenes. |
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

## Packaging Decision

Do not publish this project as a NuGet package yet. It is ready for in-repo reuse and unit testing, but the API should settle through one more internal consumer or sample before package publication.

| Area | Before Packing |
| --- | --- |
| Workspace load pipeline | Decide whether `RepositoryDiffLoader` accepts injected services by interface everywhere. |
| Generic browser models | Keep generic type parameters or introduce stable adapter interfaces. |
| Scene builders | Freeze graph-scene DTO contracts shared with `SemanticDiff.Rendering`. |
| Versioning | Add package metadata, API review notes, and a compatibility policy. |
