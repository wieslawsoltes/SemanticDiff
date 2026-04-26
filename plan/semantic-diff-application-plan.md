# SemanticDiff Application Plan

## 1. Product Vision

SemanticDiff is a high-performance visual diff workbench for large Git repositories. It presents every changed file as a scrollable document node on an infinite canvas, then uses semantic analysis to connect related files, symbols, XAML roots, bindings, resources, and project references. The first implementation targets Uno Platform with a Skia-first desktop experience and keeps the architecture ready for future mobile, browser, and native Windows heads.

Visual thesis: a quiet engineering workbench with dense code surfaces, sharp graph structure, restrained color, and immediate spatial orientation.

Interaction thesis:

- Pan and zoom should feel like a graphics editor: pointer drag pans, wheel or trackpad zooms around the cursor, double-click zooms to the clicked node or all selected nodes.
- Diff nodes should behave like real documents: independent vertical scrolling, line-number gutters, selection, hover affordances, and stable visual anchors.
- Semantic connections should fade in by relevance and follow the user's focus rather than covering the whole workspace with noise.

## 2. Requirement Traceability

| Requirement | Planned design response | Initial implementation target |
| --- | --- | --- |
| Visual node display, each file diff is a node document in an infinite canvas | `DiffCanvasView` renders a graph scene where every `DiffNode` owns a `DiffDocumentSnapshot` and viewport state | Skia-backed canvas control, node model, sample nodes |
| Fast pan and zoom | Camera transform with inertial-ready pan/zoom state, cursor-centered zoom, viewport culling | Pointer wheel/drag support, transform math |
| Fast zoom-to-fit for nodes on double click | `ViewportController.ZoomToBounds` fits one node, selection, or graph bounds with margin | Double-click node hit test and animated camera target placeholder |
| Node diffs are scrollable | Node-local `ScrollOffsetY`, wheel routing to hovered node when pointer is inside node document region | Per-node scroll state and clipped document rendering |
| Pluggable architecture but AOT friendly | Contracts plus compile-time registration through `IPluginModule`; no reflection scanning required | Core contracts and in-assembly module registry |
| Initially just in assembly via contracts | Built-in modules register in application composition root | `BuiltinPluginModule` registrations |
| Fast diff rendering up to millions of lines | Immutable line block model, chunked line index, token spans, viewport-only drawing, tile caches, SKPicture cache, async tokenization | Lightweight document snapshot, visible-line renderer, line number gutter |
| Lightweight document rendering with numbered lines and coloring | Renderer inspired by AvaloniaEdit concepts: line model, visual lines, text runs, caret-independent rendering; TextMateSharp grammar pipeline | `DiffDocumentRenderer`, token span contract, placeholder tokenizer |
| Semantic context to connect nodes | Analysis providers emit semantic anchors and edges: Roslyn for C#, MSBuildWorkspace for resolution, XamlX plus XmlParser-Roslyn for XAML/AXAML/XML | Contracts and provider shells |
| Roslyn for C# | `CSharpSemanticProvider` loads solutions/projects with `MSBuildWorkspace`, emits symbols, references, inheritance, partial declarations | Project shell with dependency references |
| MSBuild workspaces for .NET/C# type resolution | Workspace service opens `.sln`, `.slnx`, `.csproj`, caches `Solution` snapshots | Service shell and plan hooks |
| XamlX and XmlParser-Roslyn for XAML | XAML provider parses XML/XAML, resolves namespaces, x:Class, resource references, bindings, style selectors | Provider shell and contracts |
| Automatic graph layout | `IGraphLayoutEngine` converts semantic graph to MSAGL layout, then maps to canvas node positions | Layout interface and deterministic grid fallback; MSAGL package planned |
| Connect nodes | `SemanticEdge` includes source/target anchors, kind, confidence, and visual style | Edge model and renderer |
| Use SkiaSharp for fast rendering of nodes and graphs | Canvas and document renderer draw directly into `SKCanvas` | Skia renderer project and canvas control |
| Explorer-like browser for project navigation | Left project explorer groups changed files by status, project, folder, and semantic type | Shell panel and view model skeleton |
| Full Git support | `IGitDiffService` supports worktree, staged, unstaged, combined, branch/base diff, file status, rename detection | Git CLI-backed service shell |
| Fast Git repository diff display | Use porcelain/status/raw/diff commands with streaming parsers and lazy file body loading | Initial command service plus models |
| Uncommitted, non-staged, staged | `GitDiffMode.Worktree`, `Staged`, `Unstaged` | Mode enum and command mapping |
| Branch diff with default branch discovery | Discover upstream/default via `symbolic-ref refs/remotes/origin/HEAD`, remote default, then fallback to `main`, `master`, current upstream | Discovery contract and implementation shell |

## 3. Target Solution Shape

The repository should use a layered solution so UI, rendering, Git, and semantic analysis can evolve independently.

```text
SemanticDiff.slnx
src/
  SemanticDiff.App/                 Uno Platform app head and composition root
  SemanticDiff.Core/                Shared contracts, immutable models, plugin contracts
  SemanticDiff.Rendering/           Skia scene, graph, and document rendering primitives
  SemanticDiff.Diff/                Text/diff document storage and visible-line model
  SemanticDiff.Git/                 Git repository discovery and diff providers
  SemanticDiff.Semantics/           Semantic graph contracts and orchestration
  SemanticDiff.Semantics.Roslyn/    C# and MSBuildWorkspace semantic provider
  SemanticDiff.Semantics.Xaml/      XamlX / XmlParser-Roslyn semantic provider
  SemanticDiff.Layout/              Graph layout abstraction and MSAGL implementation
tests/
  SemanticDiff.Tests/               Unit tests for models, parsers, layout, Git command mapping
plan/
  semantic-diff-application-plan.md
```

Initial implementation may keep some projects thin, but the public boundaries should be established early to avoid UI-driven coupling.

## 4. UI Design

SemanticDiff is an operational tool, so the first screen is the workbench, not a landing page.

### 4.1 Main Shell Mockup

```text
+--------------------------------------------------------------------------------------------------+
| SemanticDiff                 current branch -> origin/main        Worktree  Staged  Branch Diff  |
+----------------------------+---------------------------------------------------------------------+
| Explorer                   | Canvas Toolbar: fit graph | layout | semantic edges | search       |
|                            +---------------------------------------------------------------------+
| Repository                 |                                                                     |
|  SemanticDiff              |      .---------------------------.       uses symbol       .------.  |
|                            |      | App.xaml.cs               |---------------------->| App  |  |
| Changed files              |      | M  1 using Microsoft...    |                       | .xaml|  |
|  M src/App/MainPage.xaml   |      | M  2 namespace Semantic...  |<----- x:Class ------- |      |  |
|  M src/Core/DiffNode.cs    |      |    3 public sealed...       |                       '------'  |
|  A src/Git/GitService.cs   |      |    ... scrollable ...       |                                |
|  D old/File.cs             |      '---------------------------'                                |
|                            |                                                                     |
| Projects                   |    Pan: drag empty space    Zoom: wheel/trackpad    Fit: double click|
|  SemanticDiff.App          |                                                                     |
|  SemanticDiff.Core         |                                                                     |
+----------------------------+---------------------------------------------------------------------+
| Status: 42 files changed | 12 semantic edges | layout complete | tokenization pending: 3 files       |
+--------------------------------------------------------------------------------------------------+
```

### 4.2 Diff Node Mockup

```text
+.---------------------------------------------------------------------------------------.
| M  MainPage.xaml.cs                                                1,248 lines   C#     |
+--------+---+------------------------------------------------------------------------+
| old/new|   | namespace SemanticDiff.App;                                           |
|    104 |   | public sealed partial class MainPage : Page                           |
|    105 | - | {                                                                      |
|    106 | - |     public MainPage() => InitializeComponent();                        |
|    105 | + | {                                                                      |
|    106 | + |     public MainPage()                                                  |
|    107 | + |     {                                                                  |
|    108 | + |         InitializeComponent();                                         |
|    109 | + |         ViewModel = App.Services.GetRequiredService<MainViewModel>();  |
|        |   | ... clipped visible lines only ...                                     |
+--------+---+------------------------------------------------------------------------+
| semantic anchors: MainPage, ViewModel, InitializeComponent                         |
'---------------------------------------------------------------------------------------'
```

### 4.3 Explorer Mockup

```text
Repository
  SemanticDiff

Diff Scope
  (*) Worktree
  ( ) Unstaged
  ( ) Staged
  ( ) Current branch vs origin/main
  ( ) Custom ref...

Changed Files
  C#
    M MainPage.xaml.cs
    A GitDiffService.cs
  XAML
    M MainPage.xaml
    M App.xaml
  Other
    M README.md

Semantic Groups
  App shell
  Git providers
  Rendering
  Layout
```

### 4.4 Visual Language

- Background: near-black or dark graphite workspace with subtle grid only when it aids scale.
- Nodes: compact rectangular document surfaces, radius <= 8px, strong title bars, status color chips, no nested cards.
- Diff colors: green additions, red deletions, amber modifications, blue/purple reserved for semantic focus only.
- Edges: thin antialiased cubic lines with arrowheads, grouped by kind and confidence.
- Typography: monospace for code, compact sans-serif for shell chrome.

## 5. Architecture

### 5.1 Runtime Dataflow

```text
RepositoryRoot
   |
   v
IGitDiffService ----> GitDiffSnapshot ----> DiffDocumentFactory ----> DiffDocumentSnapshot
   |                         |                         |                         |
   |                         v                         v                         v
   |                  FileDiffSummary         Tokenization Queue          Render Scene
   |                         |                         |                         |
   v                         v                         v                         v
DefaultBranchService   ISemanticProvider[] --> SemanticGraph ----> IGraphLayoutEngine
                                                                  |
                                                                  v
                                                         DiffCanvasScene
```

### 5.2 Application Composition

The application composition root registers modules explicitly:

```csharp
var registry = new PluginRegistry();
new BuiltinDiffModule().Register(registry);
new BuiltinGitModule().Register(registry);
new BuiltinRenderingModule().Register(registry);
new BuiltinSemanticModule().Register(registry);
```

This keeps the design plugin-friendly while avoiding reflection scanning, assembly probing, and dynamic loading in the first AOT-safe version.

### 5.3 Core Contracts

Important contracts:

- `IPluginModule`: registers services, renderers, semantic providers, and commands.
- `IGitDiffService`: produces repository/file diff snapshots for a requested scope.
- `IDiffDocumentFactory`: creates memory-efficient document snapshots from text or diff hunks.
- `IDocumentTokenizer`: returns token spans for visible or prefetch line ranges.
- `ISemanticProvider`: emits semantic nodes, anchors, and edges for changed files.
- `IGraphLayoutEngine`: lays out document nodes and semantic edges.
- `IDiffSceneRenderer`: renders visible graph and node documents into a Skia canvas.

### 5.4 AOT-Friendly Plugin Model

The long-term model supports external assemblies, but the initial model is in-assembly:

- Modules are normal C# classes created by the app composition root.
- Contracts are plain interfaces and immutable records.
- No dependency on dynamic code generation, expression compilation, or reflection-only discovery.
- Optional source generator later can create a module manifest from `[SemanticDiffModule]` attributes.
- External plugin loading later can be desktop-only and opt-in, while AOT heads use generated manifests.

## 6. Git Diff Architecture

### 6.1 Diff Scopes

```csharp
public enum GitDiffScope
{
    Worktree,
    Unstaged,
    Staged,
    Head,
    Branch,
    CommitRange,
    Custom
}
```

Scope mapping:

- Worktree: combined staged + unstaged working tree view.
- Unstaged: `git diff --`.
- Staged: `git diff --cached --`.
- Current branch vs default branch: `git diff <merge-base(default,current)>...HEAD`.
- Branch diff: configurable base/head refs with default base discovery.

### 6.2 Default Branch Discovery

Discovery order:

1. Current branch upstream base if available.
2. `refs/remotes/origin/HEAD` using `git symbolic-ref --short refs/remotes/origin/HEAD`.
3. Remote default from `git remote show origin` if local symbolic ref is absent.
4. Existing refs among `origin/main`, `origin/master`, `main`, `master`, `trunk`.
5. User prompt/config fallback.

### 6.3 Fast Git Strategy

The initial implementation uses Git CLI because it is already optimized, reliable, and easy to keep outside AOT concerns. The service should parse streaming command output and only load file bodies/hunks when needed.

Commands:

- Status index: `git status --porcelain=v2 -z --branch`.
- Raw changed paths: `git diff --raw -z --find-renames`.
- Numstat: `git diff --numstat -z`.
- Hunk data: `git diff --unified=0 --find-renames -- <path>` or wider context on demand.
- Staged hunk data: `git diff --cached --unified=0 --find-renames -- <path>`.

Future backends:

- LibGit2Sharp for library-only environments.
- Git object database reader for specialized huge repository operations.

## 7. Diff Document Rendering

### 7.1 Rendering Principles

The document renderer borrows the core ideas from AvaloniaEdit without copying UI implementation:

- Immutable text snapshots.
- A line index decoupled from text storage.
- Visual line construction only for visible line ranges.
- Syntax token spans independent from layout.
- Gutter, markers, and text runs composed as separate render layers.
- Cached shaped text and color spans per line.

### 7.2 Large File Model

For millions of lines, avoid one object per visual element.

```text
DiffDocumentSnapshot
  LineCount
  LineIndex: chunked offsets, one chunk per 4K or 16K lines
  Blocks: immutable text blocks or memory-mapped source slices
  HunkMap: old/new ranges and change kind per line
  TokenCache: sparse line-range token pages
  MetricsCache: line height, gutter width, longest visible line estimate
```

Memory policy:

- Keep line metadata in compact arrays.
- Load full text lazily for files above a configured threshold.
- Render only visible lines plus prefetch margins.
- Store token spans as compact structs: start column, length, style id.
- Pool temporary line buffers and avoid per-frame allocations.

### 7.3 TextMateSharp Coloring

TextMateSharp should provide grammar tokenization for source language coloring:

- Map file extension and detected language to grammar scope.
- Tokenize asynchronously in line pages.
- Cache token pages by document version and line range.
- Use semantic token overlays from Roslyn/XAML providers when available.
- Fallback to plain diff colors if grammar is unavailable.

### 7.4 Skia Document Renderer

Render layers:

1. Node shadow/border/title.
2. Document background.
3. Gutter background and separators.
4. Diff hunk backgrounds.
5. Line numbers.
6. Syntax-colored text runs.
7. Inline semantic markers.
8. Scrollbar thumb.
9. Selection/hover overlays.

The renderer should use `SKCanvas.Save`, `ClipRect`, and `Restore` around each node document body. Cached `SKTextBlob` or `SKPicture` pages can be introduced after the visible-line path is stable.

## 8. Infinite Canvas and Graph Rendering

### 8.1 Camera

```text
world point -> camera transform -> screen point

CameraState
  OffsetX
  OffsetY
  Scale
  MinScale
  MaxScale
```

Behavior:

- Drag empty canvas to pan.
- Wheel/trackpad zoom around pointer.
- Double-click node title/body to zoom to node bounds.
- Double-click empty canvas to zoom to all nodes.
- `F` or toolbar fit command zooms to graph/selection.

### 8.2 Hit Testing

Hit test order:

1. Resize handles, if enabled later.
2. Node title bar.
3. Node document body.
4. Node scrollbar.
5. Edge hover targets.
6. Empty canvas.

### 8.3 Edge Rendering

Semantic edges should be visually useful under scale changes:

- At low zoom: bundle edges and show only high-confidence connections.
- At medium zoom: show per-file edges with labels on hover.
- At high zoom: connect symbol anchors inside nodes.
- Edge kinds: symbol reference, type inheritance, partial class, XAML x:Class, binding, resource, project reference, generated file, rename/move.

## 9. Semantic Analysis Architecture

### 9.1 Shared Semantic Model

```csharp
public sealed record SemanticAnchor(
    string Id,
    DocumentId DocumentId,
    TextSpan Range,
    SemanticAnchorKind Kind,
    string DisplayName);

public sealed record SemanticEdge(
    string Id,
    string SourceAnchorId,
    string TargetAnchorId,
    SemanticEdgeKind Kind,
    double Confidence,
    string? Label);
```

### 9.2 Roslyn Provider

Responsibilities:

- Discover solution/project context for changed `.cs` files.
- Load projects with `MSBuildWorkspace`.
- Build or reuse compilations for changed projects.
- Resolve symbols for declarations and references near diff hunks first.
- Emit anchors for types, members, partial declarations, generated counterparts, using directives, base types, implemented interfaces, and referenced symbols.
- Emit edges between changed files and between changed files and unchanged context nodes when useful.

Performance strategy:

- First pass: syntax-only anchors for immediate graph display.
- Second pass: semantic model for changed documents.
- Third pass: project-level dependency edges and transitive context.
- Cache workspace snapshots by repository path, solution path, and commit/base refs.

### 9.3 XAML/AXAML/XML Provider

Responsibilities:

- Parse `.xaml`, `.axaml`, `.xml`, and framework-specific XAML-like files.
- Use XmlParser-Roslyn for syntax/tree diagnostics and fast source ranges.
- Use XamlX-style type mapping to resolve `x:Class`, XML namespaces, markup extensions, resources, styles, templates, and bindings.
- Emit edges from XAML roots to code-behind partial classes, view models, resources, styles, and referenced controls.

Framework modes:

- Uno/WinUI XAML.
- Avalonia AXAML.
- Generic XAML 2009-compatible mode.
- XML fallback mode for project/config/resource files.

### 9.4 Semantic Orchestrator

The orchestrator merges provider outputs:

```text
Changed files -> provider eligibility -> async semantic jobs -> anchor/edge merge -> layout invalidation
```

Rules:

- Providers declare supported file patterns and required services.
- Providers can return partial results quickly and update later.
- Results are versioned by repository snapshot and document version.
- Merging is stable, deterministic, and deduplicates equivalent anchors.

## 10. Graph Layout

### 10.1 Layout Engine

Use Microsoft Automatic Graph Layout as the primary engine:

- Build graph nodes from `DiffNode.Id` and preferred document sizes.
- Build edges from semantic connections and Git relationships.
- Use layered layout for dependency-like graphs.
- Use MDS or force-directed layout for exploratory semantic graphs.
- Preserve pinned user positions and incrementally place new nodes.

### 10.2 Fallback Layout

Fallback grid layout is useful for tests and before MSAGL integration:

- Group by project/folder/language/status.
- Sort by semantic centrality, then path.
- Place nodes in columns with stable deterministic positions.

## 11. Explorer and Navigation

Explorer capabilities:

- Repository picker and current repository root.
- Diff scope selector.
- Changed file tree grouped by project, folder, language, and status.
- Search by path, symbol, and token.
- Filters: C#, XAML, generated, renamed, deleted, large files, semantic edges only.
- Commands: focus node, open external editor, stage/unstage file, discard hunk later with explicit confirmation.

Navigation behavior:

- Selecting an explorer item selects and centers its node.
- Multi-selection fits selected nodes.
- Search result selects matching anchor inside node and scrolls the node body.
- Breadcrumb/status bar exposes current branch/base and analysis state.

## 12. Performance Plan

### 12.1 Frame Budget

For smooth pan and zoom, the renderer should target:

- 60 FPS for normal repositories.
- <= 16 ms frame budget for camera-only frames.
- No full document relayout during camera movement.
- Only visible nodes and visible line ranges draw per frame.

### 12.2 Large Repository Strategy

- Stage 1: display changed-file nodes from Git metadata immediately.
- Stage 2: load hunks lazily when node enters viewport or is selected.
- Stage 3: tokenize visible ranges.
- Stage 4: run semantic analysis in priority order: selected file, visible files, connected files, rest.
- Stage 5: refine graph layout as semantic edges arrive.

### 12.3 Large File Strategy

- Cap initial rendered lines per frame and continue progressively.
- Use line-height constant for fast scroll geometry.
- Treat oversized files as collapsed summaries until expanded.
- Allow opt-in full load for generated or vendored files.
- Use cancellation tokens aggressively when camera/selection changes.

## 13. Implementation Phases

### Phase 0: Plan and Skeleton

- [x] Write this plan.
- [x] Create Uno desktop solution.
- [x] Add core contracts and model projects.
- [x] Add Skia canvas shell with sample nodes.
- [x] Add initial unit test project and solution build wiring.

Current status: complete. The repository now has a layered `SemanticDiff.slnx`, a `net10.0-desktop` Uno app, core contracts, rendering/diff/Git/semantic/layout projects, and sample canvas data.

### Phase 1: Rendering MVP

- [x] Implement camera transform.
- [x] Implement hit testing and wheel routing.
- [x] Implement node-local scrolling.
- [x] Render line numbers, diff backgrounds, and text.
- [x] Add zoom-to-fit for graph and individual nodes.
- [x] Render semantic edges with Skia.
- [x] Route wheel zoom over nodes, with Shift+Wheel reserved for node-local scrolling.

Current status: complete for the first usable slice. Wheel zoom now works consistently over empty canvas and document nodes, while Shift+Wheel preserves node-local document scrolling. Remaining rendering work moves into Phase 3, Phase 5, and Phase 6 as scale/performance refinements.

### Phase 2: Git MVP

- [x] Discover repository root.
- [x] Discover default branch.
- [x] Load changed-file metadata for staged, unstaged, worktree, and branch modes.
- [x] Load hunks for staged, unstaged, worktree, and branch diff.
- [x] Convert real Git hunks into `DiffDocumentSnapshot` instances.
- [x] Bind explorer and canvas to real Git data.
- [x] Add parser and command mapping tests for repository/root/diff-hunk flows.

Current status: complete for the first Git MVP. `GitDiffService` discovers the default branch, reads worktree status including untracked files, loads per-file unified diffs, and `GitDiffDocumentService` converts them into `DiffDocumentSnapshot` instances for the app. The Uno shell now loads real repository worktree changes when available and falls back to sample data when Git data is unavailable or empty.

### Phase 3: Text Coloring

- [x] Add TextMateSharp grammar registry.
- [x] Tokenize visible/sample lines through tokenizer contract.
- [x] Add style theme mapping to Skia paints for the first C#/XAML token styles.
- [x] Add incremental token cache.
- [x] Add line-page tokenization for large documents.

Current status: complete for the first text-coloring slice. `TextMateDocumentTokenizer` now creates a bundled TextMateSharp grammar registry, resolves C#/XML-compatible scopes, tokenizes line pages with grammar rule-state continuity, caches token pages by immutable document instance, and falls back to the lightweight tokenizer when a grammar is unavailable. The renderer maps TextMate-derived style ids to Skia text paints.

### Phase 4: Semantic Providers

- [x] Add Roslyn syntax-only provider.
- [x] Add MSBuildWorkspace semantic provider with project/solution loading.
- [x] Add XAML/XML provider shell.
- [x] Add XAML provider with XmlParser-Roslyn parsing.
- [x] Add XamlX-inspired namespace/type resolution.
- [x] Add semantic edge model and first Skia edge rendering.
- [x] Add semantic edge filters and focus modes.

Current status: complete for the first semantic slice. The Roslyn provider now prefers `MSBuildWorkspace` project/solution loading and falls back to in-memory compilation, emitting namespace/type/member anchors plus inheritance, partial-class, contains, and symbol-reference edges. The XAML provider emits root, namespace, resolved type, `x:Class`, name, resource, and binding anchors/edges using an XamlX-inspired namespace/type resolver. `SemanticOrchestrator` deduplicates provider output, supports confidence/kind/focus-document filtering, and infers cross-provider XAML class and partial-class links. `XmlParserRoslynXamlParser` now provides the dedicated XML/XAML parsing adapter with line/column diagnostics and malformed-document fallback anchors.

### Phase 5: Layout

- [x] Integrate MSAGL package and adapter.
- [x] Add deterministic fallback layout.
- [x] Apply layout engine output to live canvas scenes.
- [x] Add incremental layout and pinned nodes.
- [x] Add edge bundling and semantic confidence filtering.

Current status: complete for the first layout slice. Layout requests now carry previous node positions and pinned document ids, the MSAGL and deterministic fallback engines stabilize refreshes around prior layout state, pinned nodes preserve their previous bounds, live scenes mark pinned nodes, right-click toggles a node pin, the toolbar layout command reruns layout from the current scene state, and semantic canvas edges are confidence-filtered and bundled before rendering.

### Phase 6: Production Hardening

- [x] Add cancellation, progress, and diagnostics UI.
- [x] Add repository settings and persisted layout state.
- [x] Add initial tests for diff parsing, Git command mapping, and camera math.
- [x] Add broader tests and benchmarks.
- [x] Add packaging for desktop head.
- [x] Resolve or document transitive dependency vulnerability warnings from Uno packages.

Current status: complete for the first production-hardening slice. Repository loading, tokenization, semantic analysis, and layout now report progress and honor cancellation; the shell exposes Reload, Cancel, progress, and diagnostics controls; layout positions and pinned nodes persist through an AOT-friendly JSON app-state store; broader state and large-document smoke tests are in place; desktop publishing is available through `DesktopFolder.pubxml` and `scripts/package-desktop.sh`; optional Uno Hot Design/MCP design-time packages are disabled for this production app head; and the known transitive `Tmds.DBus.Protocol` advisory is documented in `docs/dependency-advisories.md` while remaining visible in builds.

### Phase 7: Repository Selection and Live Updates

- [x] Add in-app Git repository folder picker.
- [x] Persist selected repository path and app options.
- [x] Add in-app options for diff scope, max loaded files, and automatic refresh.
- [x] Add repository file watcher API behind core contracts.
- [x] Implement Git-backed `FileSystemWatcher` adapter with path filtering.
- [x] Debounce repository file changes and automatically reload diffs.
- [x] Add watcher and option persistence tests.

Current status: complete for the first live-update slice. The app shell now exposes an Open command for selecting a repository, functional diff-scope controls, an automatic refresh toggle, and a max-files option. `SemanticDiffAppState` persists the selected repository and options, `IRepositoryFileWatcher`/`IRepositoryFileWatcherFactory` define the watcher API, the Git layer provides a filtered `FileSystemWatcher` implementation, and `MainViewModel` restarts watchers after repository loads, debounces file-system changes, and reloads the current diff automatically. Tests cover option round-tripping, watcher path filtering, and real file-system change notifications.

### Phase 8: Theme Support

- [x] Add persisted light/dark theme option.
- [x] Add compact theme toggle to the workbench header.
- [x] Move shell palette brushes into XAML light/dark theme dictionaries.
- [x] Apply Uno `RequestedTheme` from app state.
- [x] Add light/dark Skia renderer palettes for graph, nodes, diff lines, syntax colors, edges, and scrollbars.
- [x] Add renderer smoke coverage for theme-specific canvas output.

Current status: complete for the first theme slice. The workbench now has a persisted light/dark theme toggle, the XAML shell uses theme-aware palette resources, and the Skia canvas renderer receives the same theme state so document nodes and semantic graph surfaces switch with the surrounding UI.

### Phase 9: Diff Context Modes

- [x] Add a persisted diff context mode option.
- [x] Keep the zero-context changed-hunk diff view as the default mode.
- [x] Add full-file diff mode with expanded Git context around hunks.
- [x] Add current-file mode that renders the display-side file contents without deleted-line overlays.
- [x] Add compact workbench controls for switching context modes.
- [x] Add regression coverage for context command generation, current-content loading, document construction, and state persistence.

Current status: complete for the first context-mode slice. The app now lets users switch between Changed, Full diff, and Current file modes from the options rail. The selected mode is persisted, automatic reloads reuse it, and the Git/document pipeline loads either zero-context changed hunks, full-context diff output, or plain current file contents before tokenization and semantic analysis.

### Industry Gap Analysis for Professional Semantic Diff Features

Review of mature tools highlights three missing capabilities that matter most for a semantic-first desktop diff experience. Difftastic emphasizes syntax-aware review that ignores formatter noise, wrapping changes, and line-oriented false positives. JetBrains diff viewers emphasize fast navigation across changed files and differences, whitespace/import handling, collapse/noise controls, and review-oriented summaries/actions. Semantic merge-class tools emphasize understanding moved/renamed code and presenting impacted symbols rather than only raw lines. The next phases therefore prioritize noise-aware review, semantic navigation, and change-intelligence summaries before lower-priority merge/edit workflows.

### Phase 10: Noise-Aware Review Mode

- [x] Add a persisted review mode option for precise vs noise-aware review.
- [x] Suppress formatting-only hunks by comparing deleted/added code without whitespace.
- [x] Suppress import-order-only changes for C#/XAML-style import declarations.
- [x] Render ignored/noise lines with a distinct muted treatment instead of added/deleted emphasis.
- [x] Add compact UI controls for toggling the noise filter.
- [x] Add regression coverage for whitespace wrapping, import-order, moved-line classification, rendering compatibility, and state persistence.

Current status: complete for the first pro-review slice. `DiffReviewMode` is persisted in app state, the options rail exposes a Noise filter toggle, the document pipeline classifies formatting-only and import-order-only changes as ignored noise, and the renderer gives ignored lines a muted treatment so formatter churn no longer dominates review.

### Phase 11: Semantic Symbol Navigation

- [x] Build a semantic navigation index from anchors and incident graph edges.
- [x] Add symbol search and compact result list to the workbench rail.
- [x] Focus and fit the canvas to the selected file or symbol.
- [x] Scroll focused document nodes to the selected semantic anchor line.
- [x] Keep navigation reactive after repository reload, context-mode changes, and semantic reanalysis.
- [x] Add regression coverage for navigation index ordering and incident edge counts.

Current status: complete for the first navigation slice. The workbench now includes a searchable Symbols list built from semantic anchors, file and symbol selections focus the Skia graph, and focused symbols scroll their document node to the anchor line.

### Phase 12: Semantic Impact and Moved-Code Summary

- [x] Detect exact moved/copied line blocks across deleted and added diff lines.
- [x] Render moved code with its own visual treatment distinct from add/delete.
- [x] Compute a semantic impact summary from changed anchors and graph edges.
- [x] Surface changed-symbol, impacted-edge, moved-line, and ignored-noise counts in the workbench.
- [x] Feed impact data into diagnostics/status text for quick review triage.
- [x] Add regression coverage for moved-line detection and impact-summary calculation.

Current status: complete for the first change-intelligence slice. The diff transformer marks exact moved lines, the renderer uses a distinct moved-line treatment, `SemanticImpactAnalyzer` summarizes changed symbols and impacted graph links, and the canvas overlay/status stream now reports impact, moved-line, and ignored-noise counts.

### Second Industry Gap Analysis for Professional Semantic Diff Features

After the first pro-review pass, the largest remaining gaps are review execution and deeper line context. JetBrains-style diff viewers provide word/character-level highlighting, side-by-side or unified modes, accept/revert actions, Git blame annotations, conflict resolution, and changed-file navigation. VS Code emphasizes source-control actions such as stage/unstage, line-level staging, blame, file history, and merge conflict handling. Difftastic reinforces that semantic diff quality depends on showing the smallest meaningful changed expression instead of only coloring whole lines. The next phases therefore prioritize inline changed-span highlighting, Git review actions, and blame/history context before larger side-by-side editing or three-way merge workflows.

### Phase 13: Inline Word and Character Diff Highlighting

- [x] Add an inline diff-span model that can coexist with syntax token spans.
- [x] Detect word/character-level changed spans for paired deleted/added lines.
- [x] Render inline spans inside document nodes without disrupting syntax coloring.
- [x] Keep inline highlighting compatible with ignored-noise and moved-line classifications.
- [x] Add regression coverage for single and multiple inline changed spans.

Current status: complete for the inline-granularity slice. `DiffLine` now carries inline diff spans alongside syntax token spans, `InlineDiffAnnotator` computes word/symbol-level changed runs for paired deleted/added lines, the Skia renderer paints inline highlights behind code text, and tests cover single-span, multi-span, ignored-noise, and renderer compatibility cases.

### Phase 14: Git Review Actions

- [x] Add a Git review service behind core contracts.
- [x] Implement stage and unstage selected file operations through safe Git CLI commands.
- [x] Surface selected-file review actions in the compact workbench shell.
- [x] Reload the current repository view after review actions complete.
- [x] Add regression coverage for Git command mapping and action result handling.

Current status: complete for the review-action slice. `IGitReviewService` and `GitReviewService` now provide file-level stage and unstage operations, the workbench enables actions for the selected repository file, successful actions reload the current repository diff, and tests cover command mapping plus failure result handling.

### Phase 15: Blame and History Context

- [x] Add a Git blame service behind core contracts.
- [x] Parse `git blame --line-porcelain` output into line annotations.
- [x] Summarize top authors and latest blamed commit for the selected file.
- [x] Surface blame context in the workbench without crowding the graph canvas.
- [x] Add regression coverage for blame parsing and empty/untracked-file behavior.

Current status: complete for the history-context slice. `IGitBlameService` and `GitBlameService` now parse porcelain blame output into immutable line annotations, selecting a repository file loads a compact blame summary with top authors and latest commit context, and tests cover successful parsing plus empty blame behavior when Git cannot provide data.

### Third Industry Gap Analysis for Professional Semantic Diff Features

After inline highlighting, file-level review actions, and blame summaries, the largest remaining gaps are review flow and merge readiness. JetBrains diff viewers provide previous/next difference navigation, changed-file navigation, collapse unchanged fragments, synchronized side-by-side viewing, and merge actions for accepting or applying non-conflicting changes. VS Code emphasizes side-by-side review, line/selection staging, source-control graph and timeline, and a 3-way merge editor for conflicts. Beyond Compare and P4Merge reinforce folder/file filtering, merge conflict triage, and compact comparison views that keep unchanged code from overwhelming the reviewer. The next phases therefore prioritize collapsed unchanged context, keyboard/button-ready change navigation, and conflict intelligence before larger editable side-by-side merge panes or hunk-level write operations.

### Phase 16: Collapsed Unchanged Context

- [x] Add a persisted option to collapse long unchanged context fragments.
- [x] Fold long context runs into stable placeholder lines while keeping nearby changed lines visible.
- [x] Preserve line numbers, syntax tokenization, inline spans, semantic analysis, and graph layout compatibility.
- [x] Surface a compact workbench toggle for collapsed context review.
- [x] Add regression coverage for folding thresholds, placeholder content, and app-state persistence.

Current status: complete for the collapsed-context slice. `SemanticDiffAppState` now persists a collapse-context review option, `DiffContextFolder` replaces long unchanged runs with stable `Imaginary` placeholders, the workbench exposes a compact toggle, and regression tests cover folding behavior plus state round-tripping.

### Phase 17: Previous and Next Change Navigation

- [x] Build a deterministic navigation index over review-relevant changed lines.
- [x] Add previous/next change commands that wrap across files.
- [x] Focus the canvas node and scroll to the selected changed line.
- [x] Surface compact navigation controls and current change position text.
- [x] Add regression coverage for ordering, wrapping, and ignored placeholder exclusion.

Current status: complete for the navigation slice. `DiffChangeNavigationIndex` now builds a deterministic line-level review index, the ViewModel exposes wrapping previous/next commands through the existing `FocusRequest` path, the header and canvas overlay show compact navigation controls/status, and tests cover ordering, wrapping, and placeholder/noise exclusion.

### Phase 18: Merge Conflict Intelligence

- [x] Detect Git conflict marker regions in changed or current-file documents.
- [x] Render conflict marker and region lines with a distinct treatment.
- [x] Summarize conflicted files and unresolved regions in the workbench review signal.
- [x] Keep conflict analysis non-destructive and separate from future merge editing commands.
- [x] Add regression coverage for marker parsing, renderer compatibility, and impact-summary integration.

Current status: complete for the conflict-intelligence slice. `DiffConflictAnalyzer` now detects marker regions, conflict lines render with a dedicated treatment, conflicted files use a `!` badge, conflict regions are included in change navigation and review signals, and tests cover marker parsing, unterminated markers, conflicted status files, and renderer compatibility.

### Original Requirements Closure Audit

Reviewing the original requirements against the current implementation leaves three practical gaps: the Phase 4 XAML parsing checkbox needed a dedicated parser/diagnostic adapter, the explorer needed path-level search and real semantic-edge visibility controls rather than a static toggle, and the Git branch/custom scope model needed user-editable base/head refs in the workbench. The following phases close those gaps while keeping larger post-MVP editing workflows, such as side-by-side merge editing and hunk-level apply/revert, as future product expansions rather than unmet original requirements.

### Phase 19: XAML Parser Diagnostics Closure

- [x] Add a dedicated `XmlParserRoslynXamlParser` adapter for XML/XAML source parsing.
- [x] Preserve line and column diagnostics for malformed XAML documents.
- [x] Keep `x:Class` fallback anchors for malformed documents.
- [x] Surface parser diagnostics as semantic anchors without blocking graph construction.
- [x] Add regression coverage for parser diagnostics and malformed-XAML fallback behavior.

Current status: complete. The XAML semantic provider now runs through the parser adapter, valid XAML still emits resolved type/resource/binding anchors, malformed XAML produces diagnostic anchors with source locations, and `x:Class` fallback linking remains intact.

### Phase 20: Explorer Search and Semantic Edge Controls

- [x] Add changed-file search in the explorer rail.
- [x] Keep filtered and total file counts visible while searching.
- [x] Make semantic-edge visibility a real persisted option.
- [x] Rebuild the canvas scene without semantic edges when edge display is disabled.
- [x] Add regression coverage for edge suppression.

Current status: complete. The explorer now filters changed files by path, filename, folder, status, and language. Semantic edge visibility is persisted in app state and controls both the options rail and canvas overlay toggle.

### Phase 21: Custom Reference and Commit Range Diff Scope

- [x] Add persisted base/head ref options to app state.
- [x] Add compact workbench controls for editable base/head refs.
- [x] Add a range scope in the workbench that maps to `GitDiffScope.CommitRange`.
- [x] Pass configured refs through `GitDiffRequest` for branch/range/custom diff commands.
- [x] Add regression coverage for explicit commit-range command generation and state persistence.

Current status: complete. Branch and range diff loading now honor editable base/head refs, the workbench exposes a `Range` scope and `Apply refs` command, and the Git command builder covers explicit commit ranges.

### Visualization Coverage Audit

Reviewing Phases 0 through 21 showed that several capabilities were already visible directly on the canvas, including syntax coloring, diff backgrounds, semantic edges, inline spans, moved/noise/conflict line treatments, collapsed-context placeholders, and pinned layout state. Other high-value signals were only present in side panels, status text, or diagnostics, including parser diagnostics, semantic anchor categories, impact signals, blame/history, repository watch state, selected review action state, current change navigation, reference range context, and Git scope. The next phases make those signals first-class canvas annotations with independently controllable visibility so the diff graph can carry semantic context without forcing every reviewer to see every layer at once.

### Phase 22: Pluggable Canvas Annotation and Diagnostic Model

- [x] Add a typed canvas annotation model with kind, target, severity, document, line, label, and detail fields.
- [x] Add a persisted annotation visibility state for Git/status, semantic, diagnostics, review, history, navigation, and context layers.
- [x] Add an `IDiffAnnotationProvider` contract so built-in and future plugin modules can emit annotations without renderer-specific coupling.
- [x] Add a request context model that carries current diff documents, semantic graph data, and workbench state such as refs, watch status, selection, blame, review action, and focused change.
- [x] Keep the model AOT-friendly and compatible with source-generated JSON app-state persistence.

Current status: complete. `DiffAnnotation`, `DiffAnnotationVisibilityState`, `DiffAnnotationRequest`, and `IDiffAnnotationProvider` now define the extension surface for semantic canvas diagnostics and annotations.

### Phase 23: Built-In Phase-Aware Annotation Providers and Canvas Rendering

- [x] Add a built-in annotation provider that maps Git status, refs, syntax tokenization, semantic anchors, parser diagnostics, review noise, moved code, inline changes, semantic impact, conflicts, context folds, change navigation, blame/history, review actions, and repository watch state into canvas annotations.
- [x] Project annotations into `DiffCanvasScene` alongside nodes, edges, camera state, and visibility settings.
- [x] Preserve camera and layout state when annotations or visibility settings refresh.
- [x] Render line-level annotation bands, right-edge rail markers, and compact labels for conflicts, parser diagnostics, focused navigation targets, and semantic impact.
- [x] Render node-level footer chips for Git/status, refs, syntax, watch, history, and review annotations.
- [x] Add regression coverage for provider output and annotation renderer pixels.

Current status: complete. The canvas now has a distinctive visual grammar for semantic anchors, diagnostics, impact, conflicts, moved/noise/inline review signals, context folds, blame, watch state, refs, and navigation focus, with layer visibility applied before rendering.

### Phase 24: Visualization Popup Controls and Persistence

- [x] Add a compact Visualizations popup button to the workbench header.
- [x] Add popup toggles for Git and refs, semantic anchors, diagnostics, review signals, blame history, navigation focus, and context folds.
- [x] Persist visualization visibility in app state and restore it on startup.
- [x] Refresh canvas annotations when visibility, selected file, blame summary, review action state, watch status, semantic edges, layout, or focused change changes.
- [x] Surface a compact visualization-layer count in the canvas overlay.
- [x] Add state persistence regression coverage for annotation visibility.

Current status: complete. Reviewers can now tune canvas visual density from the Settings tab while keeping semantic diagnostics, review annotations, and context overlays available on demand.

### Phase 25: Tabbed Left Rail and Fast Semantic Loading

- [x] Split the left workbench rail into focused Files, Symbols, and Settings tabs.
- [x] Keep repository identity and load status visible above the tab group.
- [x] Move diff scope, reference range, repository refresh, review actions, theme, semantic edge, and visualization controls into Settings.
- [x] Add a persisted semantic analysis mode option for MSBuild-backed analysis versus fast syntax-only loading.
- [x] Route fast syntax mode directly to the Roslyn in-memory analysis path so repositories can load without opening `MSBuildWorkspace`.
- [x] Reload analysis when semantic mode changes and preserve the setting across app restarts.
- [x] Add regression coverage for semantic analysis mode persistence and MSBuildWorkspace bypass behavior.

Current status: complete. The left panel now behaves like a compact workbench sidebar instead of one long mixed rail, and users can choose Fast syntax mode for repositories where MSBuild project loading is unsupported or too slow.

### Phase 26: Direct Canvas Node Manipulation

- [x] Make normal left-drag on document nodes move the node instead of panning the canvas.
- [x] Add resize hit testing and selected-node resize handles for document nodes.
- [x] Resize nodes from edges and corners while enforcing minimum readable node dimensions.
- [x] Pin nodes automatically when users drag or resize them so subsequent layout passes preserve intentional placement.
- [x] Make normal wheel input over a node document body scroll that node.
- [x] Reserve Ctrl/Cmd-modified drag and wheel gestures for canvas pan and zoom, while preserving middle-button panning.
- [x] Add regression coverage for node scrolling, Ctrl/Cmd zoom policy, direct movement, resize clamping, and resize-handle hit testing.

Current status: complete. The canvas now supports direct graph editing gestures: nodes can be repositioned and resized in place, document bodies scroll under the pointer by default, and camera movement is explicitly modifier-driven for normal pointer gestures.

### Phase 27: Smart Watch Refresh and Node Status Color System

- [x] Capture live canvas state before file-watch auto refreshes, including camera zoom/pan, node bounds, node selection, pin state, and document scroll offsets.
- [x] Restore matching nodes after refreshed documents, semantic analysis, and layout are rebuilt so repository updates do not reset the reviewer's workspace.
- [x] Preserve existing layout state as the preferred layout seed during smart refresh while still allowing newly changed files to appear.
- [x] Keep ordinary scope, reference, semantic-mode, and explicit layout changes free to recompute when the user intentionally changes review context.
- [x] Add node-level Git status color coding with industry-standard semantics: green for new/untracked, amber for modified/updated, red for deleted/error, purple for renamed/copied/moved, and orange-red for conflicts.
- [x] Render status colors as node borders, left accent strips, and compact title badges without overpowering code content.
- [x] Add regression coverage for scene-state transfer and distinct rendered node status colors.

Current status: complete. Watch-driven refreshes now sync new repository data into the existing canvas without disrupting zoom, pan, node placement, selection, or scroll position for files that still exist. Document nodes also carry clear Git-style color signals for new, modified, deleted, moved/renamed, untracked, copied, and conflicted files.

### Phase 28: Native File Explorer Tree and Navigation Menus

- [x] Replace the flat changed-file list with a proper collapsible repository file tree grouped by folder path.
- [x] Add a testable file explorer tree model that classifies common source, project, solution, config, Git, image, and text file types.
- [x] Add native platform icon profiles for Windows, macOS, and Linux at the Uno app boundary while keeping deterministic file-type classification in core models.
- [x] Apply the same Git-style status colors used by canvas nodes to file rows, folder aggregate status accents, and compact status badges.
- [x] Preserve search, filtered counts, selection, review action state, and blame loading when moving from the flat list to the tree.
- [x] Add context-menu navigation from file explorer rows to canvas document nodes.
- [x] Add canvas node context menus for revealing nodes in the file tree, focusing the node, and pinning or unpinning without interrupting normal pointer gestures.
- [x] Add regression coverage for explorer hierarchy creation, status aggregation, file-type icon classification, and document input support.

Current status: complete. The Files rail now behaves like a compact source-control explorer with folder hierarchy, platform-aware native icon profiles, matching node status colors, and bidirectional navigation between explorer rows and canvas document nodes.

### Phase 29: Resizable Workbench Splitter

- [x] Add a compact drag splitter between the left file/navigation rail and the right Skia canvas.
- [x] Enforce minimum rail and canvas widths so resizing does not collapse core workbench surfaces.
- [x] Persist the resized left pane width in app state and restore it on startup.
- [x] Keep the splitter visually restrained with a narrow divider and larger transparent hit target.
- [x] Add regression coverage for splitter width persistence through the app-state store.

Current status: complete. The workbench now supports resizing the left rail against the canvas with a dedicated splitter, preserving the user's preferred width across sessions while keeping both panes usable.

### Phase 30: Precision Node Interaction and Per-Node Typography

- [x] Make node dragging start only from the chrome/title bar so document body interaction no longer unexpectedly moves nodes.
- [x] Replace incremental node-drag deltas with absolute pointer-offset tracking to prevent jumps when dragging begins or the camera is zoomed.
- [x] Add draggable node scrollbars so users can scroll long document nodes by thumb as well as wheel input.
- [x] Keep resize handle rendering and hit testing stable in screen pixels across zoom levels.
- [x] Add per-node `-` and `+` title-bar controls plus context menu actions for font-size adjustment.
- [x] Persist per-node font size through layout state and smart refresh view-state transfer.
- [x] Add regression coverage for title-only drag hit testing, absolute node movement, scrollbar dragging, zoom-stable handles, font-size controls, and font-size persistence.

Current status: complete. Canvas nodes now behave more like editor panes: chrome/title dragging is smooth and intentional, scrollbars are directly draggable, resize handles stay usable at any zoom level, and reviewers can tune each node's code font size independently.

### Phase 31: Load and Reset Canvas Auto-Fit

- [x] Detect newly loaded or reset scenes that still have the default camera state.
- [x] Fit those scenes to the available canvas once the control has a measured Skia surface or layout size.
- [x] Preserve intentional camera state during annotation-only refreshes and smart watch refreshes.
- [x] Keep manual Fit behavior working even if invoked before the canvas has a valid size.
- [x] Validate the change with compiler diagnostics and the full solution test suite.

Current status: complete. Fresh loads and reset-style scene rebuilds now land fitted in the canvas instead of appearing at an arbitrary default pan/zoom, while preserved review sessions keep their current camera.

### Phase 32: Splitter Cursor Feedback

- [x] Set a horizontal resize cursor when the pointer enters the workbench splitter hit target.
- [x] Keep the resize cursor active while the splitter has pointer capture during drag.
- [x] Restore the default cursor after the pointer leaves or dragging completes.
- [x] Validate the change with compiler diagnostics and the full solution test suite.

Current status: complete. The left-rail splitter now gives platform cursor feedback that matches its resize behavior, making the hit target easier to discover and use.

### Phase 33: Zoom-Safe Font Controls and Live Node Dragging

- [x] Hide node font-size controls when the node title is too small on screen for stable interaction.
- [x] Keep visible font-size controls inside the node title bar across usable zoom levels.
- [x] Preserve screen-stable font-control hit targets without spilling outside zoomed-out nodes.
- [x] Route canvas pointer hit testing through the owning canvas control so captured node drags repaint continuously.
- [x] Add regression coverage for zoom-aware font controls and repeated node-drag position updates.
- [x] Validate the change with compiler diagnostics and the full solution test suite.

Current status: complete. Per-node font controls now behave predictably across zoom levels, and document node dragging updates through the canvas control during the drag instead of appearing only after the final position is reached.

### Phase 34: Complete Diff Scope Coverage

- [x] Review Git scope command generation for worktree, unstaged, staged, current-branch, explicit branch, commit range, and custom range modes.
- [x] Include untracked files in unstaged and current-branch review scopes so newly added local files are not silently hidden.
- [x] Compare current branch scope from the merge base to the working tree so committed branch changes and local tracked edits are both visible.
- [x] Keep explicit commit/custom ranges as endpoint comparisons and keep explicit branch-head comparisons as merge-base branch comparisons.
- [x] Enable copy detection and parse copied, renamed, conflicted, added, deleted, and modified status records without losing path data.
- [x] Synthesize added/copied file diffs from the target revision when Git returns no patch body for a listed file.
- [x] Prioritize added, untracked, copied, renamed, deleted, and conflicted files when the initial max-file cap would otherwise load only modified files.
- [x] Add regression coverage for branch/untracked scope behavior, copy parsing, endpoint range commands, added-file fallback diffs, and capped initial loading.

Current status: complete. Diff scope loading now preserves newly added files across unstaged and current-branch review, handles copy/rename/conflict records more accurately, and avoids letting the initial file cap hide structural changes behind a long modified-file list.

### Phase 35: Full Change-Set Surfacing Across Explorer, Symbols, and Graph

- [x] Reproduce branch-mode behavior against `/Users/wieslawsoltes/GitHub/Dock` on `dock-uno-platform-port` and confirm Git reports 405 changed files while the app surfaced only 24 nodes.
- [x] Remove the document-loading cap from the Git document snapshot path so every file reported by the selected diff scope becomes a document, explorer item, and graph node.
- [x] Keep branch/worktree/range scope metadata counts aligned with the actual document count used by the canvas and navigation models.
- [x] Merge Roslyn syntax analysis back into workspace-backed analysis when `MSBuildWorkspace` only covers part of the changed C# file set.
- [x] Analyze XAML/XML files from the current repository file text when available, falling back to diff hunks for deleted or unavailable files.
- [x] Add regression coverage for all-files document loading below the previous max-file cap, C# files outside loaded projects, and XAML hunks that require full file text.
- [x] Validate the change with compiler diagnostics and the full solution test suite.

Current status: complete. Branch mode and the other diff scopes now surface the full changed-file set consistently through the file explorer, semantic symbol index, and node graph instead of truncating the working model to the old initial load limit.

### Phase 36: Huge Layout Rendering Performance and Layout Modes

- [x] Add viewport-aware renderer culling so offscreen document nodes are skipped instead of fully drawn on every frame.
- [x] Cache semantic edge endpoint lookup during rendering and cull edges whose projected world bounds do not intersect the viewport.
- [x] Group visible annotations by document once per frame instead of repeatedly scanning all annotations for each node.
- [x] Keep zoomed-out visible nodes rendering their normal document detail while using viewport culling for scale.
- [x] Add render statistics for regression coverage and diagnostics of total, drawn, and detailed node/edge counts.
- [x] Add graph layout modes for Auto, Layered, Grid, Compact grid, and Status lanes, with Auto selecting compact grid for very large change sets.
- [x] Persist the selected layout mode in app state and expose it through a compact Settings dropdown.
- [x] Add regression coverage for renderer culling, low-zoom compact rendering, compact auto layout, status lanes, and layout-mode state persistence.
- [x] Validate the change with focused tests and the Uno app build.

Current status: complete. Large branch-mode layouts now avoid drawing offscreen nodes while keeping visible zoomed-out nodes rendered with their normal document detail, the graph engine can choose compact visualizations for hundreds of changed files, and the Settings rail exposes persisted layout modes so reviewers can switch between semantic layering, deterministic grids, compact grids, and status-grouped lanes.

### Phase 37: Layered Layout Default

- [x] Make Layered the default graph layout mode for new app state and layout requests.
- [x] Keep Auto available as an explicit user-selectable option rather than the initial default.
- [x] Initialize the Settings layout dropdown to Layered before persisted state is loaded.
- [x] Add regression coverage for default app-state and graph-layout request modes.
- [x] Validate the change with focused tests and the full solution test suite.

Current status: complete. New sessions now start with the semantic layered graph layout by default, while Auto, Grid, Compact grid, and Status lanes remain available from the layout dropdown.

### Phase 38: Large Branch Graph Fit and Semantic Quality

- [x] Lower the camera fit scale floor so very large branch layouts can actually fit inside the canvas instead of clamping to an over-zoomed view.
- [x] Build layered layouts from capped high-signal document-pair edges instead of feeding every raw semantic edge into MSAGL.
- [x] Compact disconnected layered components so independent clusters and isolated files do not explode the world bounds.
- [x] Avoid false cross-provider class edges between generic XAML element names such as `Page`, `Grid`, and `ResourceDictionary`.
- [x] Infer higher-quality XAML resource and binding edges from resource keys and C# member anchors when changed documents provide both sides.
- [x] Add regression coverage for huge-graph fit, no-edge layered fallback, generic XAML false-edge suppression, and resource/binding edge inference.
- [x] Validate the change with focused tests and the full solution test suite.

Current status: complete. Branch-mode graphs now fit correctly at very large world sizes, layered layouts use stronger document-level semantic signals, disconnected clusters are packed more tightly, and semantic inference avoids generic XAML false positives while adding more useful resource and binding relationships.

### Phase 39: Graph Grouping Visualization

- [x] Add a persisted graph grouping mode for None, Folders, Semantic, Language, and Status grouping.
- [x] Build folder-area groups from laid-out document nodes so large branch graphs show changed repository areas such as `src/App` and `tests/App.Tests`.
- [x] Build semantic groups from document language and semantic anchors so XAML/UI, C# symbols, tests, projects, resources, docs, and config changes can be spotted quickly.
- [x] Render cullable group regions behind nodes with stable screen-readable labels and compact document counts.
- [x] Expose grouping selection in the Settings layout section without forcing a graph relayout or losing camera/node state.
- [x] Add regression coverage for grouping state persistence, folder grouping, semantic grouping, disabled grouping, and group-region rendering.
- [x] Validate the change with focused tests and the full solution test suite.

Current status: complete. The canvas can now overlay folder, semantic, language, or status group regions on top of any layout mode, making large change sets easier to scan by repository area or semantic purpose without hiding individual document nodes.

### Phase 40: Zoom-Stable Canvas Labels and Controls

- [x] Keep graph group title labels rendered as screen-space chrome so their text size stays readable while zooming in or out.
- [x] Move node font-size `-` and `+` controls out of the scaled world transform and render them as screen-space overlays above visible nodes.
- [x] Preserve existing hit testing by continuing to derive font-control world bounds from the camera scale before projecting them to screen coordinates for rendering.
- [x] Add renderer regression coverage that measures group-label and font-control pixel extents at different zoom levels.
- [x] Validate the change with focused renderer tests and the full solution test suite.

Current status: complete. Group labels and per-node font controls now keep stable device-pixel sizing during zoom, improving readability on large grouped layouts without changing node geometry or interaction behavior.

### Phase 41: Repository Open Refresh Reliability

- [x] Add a latest-request gate for repository loads so an older open, reload, semantic analysis, or layout continuation cannot publish state after a newer folder selection.
- [x] Reset the workbench to a fresh empty scene as soon as a new repository root is accepted, clearing stale nodes, explorer items, semantic navigation, pinned layout state, and selected file state while the real diff loads.
- [x] Refresh the canvas explicitly when the view model publishes a new scene so the Skia host receives scene changes even if Uno binding timing is delayed.
- [x] Replace stale no-changes behavior with an explicit empty repository scene and status summary instead of leaving the previous repository graph visible.
- [x] Harden watcher shutdown so late cleanup from an older repository load cannot clear a watcher created by a newer load.
- [x] Add regression coverage for latest-request invalidation.
- [x] Validate the change with focused request-gate tests and the full solution test suite.

Current status: complete. Opening repositories back-to-back now keeps only the newest request eligible to update the workbench, and empty or no-change repository states actively refresh the canvas instead of leaving stale graph content behind.

### Phase 42: Progressive Repository Loading Performance

- [x] Analyze the repository load pipeline by stage: repository discovery, changed-file discovery, per-file diff loading, review transforms, initial layout, tokenization, semantic analysis, semantic layout, state persistence, and watcher restart.
- [x] Avoid default-branch discovery for scopes that do not need it, and cache discovered branch refs inside each Git diff service instance so branch-mode per-file diffs do not repeatedly run the same Git commands.
- [x] Load per-file Git diffs with bounded parallelism while preserving the Git-reported document order used by the explorer, canvas, and navigation models.
- [x] Publish an initial document graph immediately after review transforms and a fast layout so small repositories show useful canvas content before syntax coloring, MSBuild workspace loading, or semantic layout finishes.
- [x] Move TextMate tokenization after first paint and republish the scene with preserved camera/node state once syntax coloring is ready.
- [x] In MSBuild semantic mode, run fast syntax semantics on the critical path and continue full MSBuild semantic refinement in the background, preserving all semantic functionality without blocking initial repository usability.
- [x] Cancel pending background semantic refinement when a new load, relayout, repository action, or explicit cancellation starts so stale semantic results cannot overwrite newer work.
- [x] Add regression coverage for bounded concurrent file diff loading, document-order preservation, default-branch discovery skipping, and branch default caching.
- [x] Validate the change with focused Git loading tests and the full solution test suite.

Current status: complete. Repository loading now produces a usable graph in progressive stages, reduces redundant Git process work, parallelizes independent file diff reads, and keeps full MSBuild-backed semantic quality as a cancellable refinement rather than a first-paint blocker.

### Phase 43: Complete Symbol Navigation for Large Mixed Branches

- [x] Reproduce the symbol-navigation symptom against `/Users/wieslawsoltes/GitHub/Avalonia.Controls.TreeDataGrid` on `feat/uno-treedatagrid-port`, confirming branch mode has 185 changed C# files plus XAML/XML changes.
- [x] Remove the silent 80-item Symbols rail cap so all filtered semantic navigation entries are surfaced instead of hiding C# symbols behind early XAML paths.
- [x] Prefer code type/member symbols before resource-heavy XAML anchors in the default semantic navigation order while preserving search over all symbols.
- [x] Optimize C# semantic provider anchor and edge deduplication with hash sets so large C# branches do not pay quadratic duplicate checks while emitting many symbols.
- [x] Optimize XAML semantic provider deduplication with the same hash-set strategy to keep mixed XAML/C# branch analysis responsive.
- [x] Add regression coverage for large changed C# symbol sets, generic-looking C# file names, more-than-80 mixed semantic navigation entries, and C# symbol priority over XAML resources.
- [x] Validate the change with focused semantic-provider/navigation tests and the full solution test suite.

Current status: complete. Large mixed branches now load and display complete semantic navigation results, including C# type/member symbols that were previously loaded or loadable but hidden by the rail cap and path-first ordering.

### Phase 44: Large Branch Layout Quality and Skia Rendering Cache

- [x] Analyze `/Users/wieslawsoltes/GitHub/Dock` on `dock-uno-platform-port`, confirming the branch-mode shape is 405 changed files with dominant `src/Dock.Uno`, `src/Dock.Uno.Themes.Fluent`, and sample areas.
- [x] Lower the camera scale floor so Fit can frame multi-million-world-unit graphs instead of clamping to an over-zoomed viewport.
- [x] Replace large Layered-mode MSAGL layouts with a compact semantic-cluster layout for large branches, grouping files by repository area and ordering central/high-signal documents first.
- [x] Keep default Layered layout semantics for normal repositories while avoiding sparse giant worlds for hundreds-file branch diffs.
- [x] Improve semantic graph quality with path-based XAML/code-behind companion edges and repository-area cohesion edges so large branch layouts have useful structural connections even when symbol references alone are sparse.
- [x] Optimize semantic graph filtering and binding inference with indexed anchor/member lookups instead of repeated full-anchor scans.
- [x] Add Skia renderer scene caching for node lookup, annotation grouping, edge bounds, and reusable edge `SKPath` geometry so dense canvases redraw without rebuilding dictionaries and cubic paths every frame.
- [x] Add regression coverage for ultra-large Fit, Dock-shaped large branch clustering, path companion inference, repository-area cohesion links, and renderer edge-cache reuse.
- [x] Validate the change with focused layout/viewport/semantic/renderer tests and the full solution test suite.

Current status: complete. Large branch graphs now fit correctly, organize hundreds of nodes into compact repository-area clusters, gain better semantic/structural connection signals, and redraw dense edge sets with cached Skia geometry instead of per-frame edge reconstruction.

### Phase 45: Node Scrollbar Drag Hit Testing

- [x] Trace node scrollbar pointer handling through `DiffCanvasControl`, `DiffCanvasScene`, and `DiffNode.GetScrollbarThumbBounds`.
- [x] Identify that right-edge resize hit testing can capture pointer presses on the scrollbar thumb before the scrollbar drag path runs.
- [x] Exclude non-empty scrollbar thumb bounds from resize-handle hit testing so node scrollbars receive drag gestures reliably.
- [x] Add regression coverage proving scrollbar-thumb clicks are not reported as resize-handle hits and still start scrollbar interaction.
- [x] Validate the change with focused canvas interaction tests and the full solution test suite.

Current status: complete. Scrollbar thumbs inside document nodes now win hit testing over right-edge resize handles, restoring direct draggable node scrolling without weakening resize behavior elsewhere.

### Phase 46: Cached Native System File Icons

- [x] Replace the explorer's platform-name glyph icon profile with an app-layer system file icon provider.
- [x] Use macOS `NSWorkspace` and AppKit/Foundation interop to obtain real Finder-quality folder and file-type icons from the operating system.
- [x] Cache native icon PNGs by stable folder, extension, or special filename keys under the local application data folder so large trees do not repeatedly call native APIs.
- [x] Keep deleted, generated, and unavailable files working through extension-based system icon lookup with a compact glyph fallback when native icon extraction is unavailable.
- [x] Render explorer icons through `ImageSource` bindings while preserving status accents, automation text, and tooltips.
- [x] Validate the change with compiler diagnostics, app build, runtime launch smoke testing, and cached icon artifact inspection.

Current status: complete. The file explorer now uses real cached macOS system icons through AppKit interop instead of simulated icon-font glyphs, while retaining deterministic fallbacks for unsupported platforms or unavailable icon data.

## 14. Initial Technical Decisions

- Target framework: `net10.0`, matching installed SDK and Uno template default.
- Initial platform: Uno Skia Desktop. Other heads can be added after the canvas path is stable.
- UI style: Fluent, XAML-first, MVVM-ready.
- Renderer: SkiaSharp canvas for graph and diff documents.
- Git backend: Git CLI process backend first, behind contracts.
- Semantic backend: contracts first, provider shells second, real Roslyn/XAML analyzers staged in.
- Plugin model: in-assembly modules with explicit registration.

## 15. Testing Strategy

Unit tests:

- `GitDefaultBranchDiscoveryTests` for fallback order.
- `GitDiffCommandBuilderTests` for scope command mapping.
- `DiffDocumentSnapshotTests` for line indexing and hunk mapping.
- `ViewportControllerTests` for pan, zoom, and fit bounds.
- `GraphLayoutTests` for deterministic fallback layout.

Rendering tests:

- Snapshot-style tests for renderer geometry once a headless path is available.
- Pixel checks for line number gutter, diff backgrounds, clipping, and edge rendering.

Performance tests:

- Synthetic million-line document snapshot creation.
- Visible-line rendering allocation budget.
- Git metadata parsing on large `-z` outputs.
- Semantic provider cancellation and partial result latency.

## 16. Risks and Mitigations

| Risk | Mitigation |
| --- | --- |
| SkiaSharp Uno package/API mismatch | Keep renderer core independent of the XAML host control; isolate host-specific canvas adapter |
| Millions-line files exhaust memory | Chunked indexes, lazy body loading, visible range rendering, compact token spans |
| MSBuildWorkspace loads are slow | Syntax-first pass, prioritized semantic jobs, cache solution snapshots |
| Semantic graph becomes visually noisy | Confidence filters, edge bundling, focus mode, hover reveal |
| AOT conflicts with plugins | Explicit module registration, no reflection scanning, generated manifests later |
| Git command output varies by version | Use porcelain v2 and `-z` formats, add parser tests |
| XAML dialects differ | Provider modes for Uno, Avalonia, generic XAML, XML fallback |

## 17. Definition of Done for First Usable Slice

The first usable slice is complete when:

- The Uno desktop app opens to the workbench shell.
- The explorer lists sample or real changed files.
- The canvas shows each file as a document node.
- Panning, zooming, double-click fit, and node scrolling work.
- The renderer draws numbered diff lines with status coloring.
- Core contracts exist for Git, rendering, diff documents, semantic providers, layout, and plugins.
- Git scope models and command mapping are implemented.
- Semantic provider shells exist for Roslyn and XAML.
- A build succeeds for the initial desktop target or failures are documented with exact blockers.