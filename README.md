# SemanticDiff

[![Build](https://github.com/wieslawsoltes/SemanticDiff/actions/workflows/build.yml/badge.svg)](https://github.com/wieslawsoltes/SemanticDiff/actions/workflows/build.yml)
[![Package Integration](https://github.com/wieslawsoltes/SemanticDiff/actions/workflows/integration.yml/badge.svg)](https://github.com/wieslawsoltes/SemanticDiff/actions/workflows/integration.yml)
[![Docs](https://github.com/wieslawsoltes/SemanticDiff/actions/workflows/docs.yml/badge.svg)](https://github.com/wieslawsoltes/SemanticDiff/actions/workflows/docs.yml)
[![Release](https://github.com/wieslawsoltes/SemanticDiff/actions/workflows/release.yml/badge.svg)](https://github.com/wieslawsoltes/SemanticDiff/actions/workflows/release.yml)

[![NuGet](https://img.shields.io/nuget/vpre/SemanticDiff.Workbench.svg)](https://www.nuget.org/packages/SemanticDiff.Workbench/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SemanticDiff.Workbench.svg)](https://www.nuget.org/packages/SemanticDiff.Workbench/)
[![GitHub release](https://img.shields.io/github/v/release/wieslawsoltes/SemanticDiff.svg?include_prereleases)](https://github.com/wieslawsoltes/SemanticDiff/releases)
[![GitHub downloads](https://img.shields.io/github/downloads/wieslawsoltes/SemanticDiff/total.svg)](https://github.com/wieslawsoltes/SemanticDiff/releases)
[![License](https://img.shields.io/github/license/wieslawsoltes/SemanticDiff.svg)](LICENSE)

SemanticDiff is a desktop Git diff explorer that turns repository changes into an interactive, semantic graph. It combines Git-aware diff loading, syntax and semantic analysis, node-based visualization, file/reference navigation, and GitHub/GitLab review workflows in a single Uno Platform app.

The app is designed for code review sessions where a flat patch is not enough: it groups changes by file, folder, language, or semantic structure, highlights syntax and review signals, preserves navigation state, and can load or write remote review discussion data for pull requests and merge requests.

## Documentation

The Lunet documentation site is published from the `docs.yml` workflow to GitHub Pages and can be built locally with:

```bash
dotnet tool restore
./build-docs.sh
```

The site source lives under `site/` and includes guides, package documentation, concepts, reference pages, and generated API documentation for the reusable libraries.

## NuGet Packages

Reusable SemanticDiff packages are published independently so apps can consume the core models, Git/diff services, semantic analysis, layout/rendering services, workbench orchestration, or Uno controls without referencing the desktop app.

| Package ID | NuGet | Downloads |
| --- | --- | --- |
| `SemanticDiff.Core` | [![NuGet](https://img.shields.io/nuget/vpre/SemanticDiff.Core.svg)](https://www.nuget.org/packages/SemanticDiff.Core/) | [![NuGet Downloads](https://img.shields.io/nuget/dt/SemanticDiff.Core.svg)](https://www.nuget.org/packages/SemanticDiff.Core/) |
| `SemanticDiff.Diff` | [![NuGet](https://img.shields.io/nuget/vpre/SemanticDiff.Diff.svg)](https://www.nuget.org/packages/SemanticDiff.Diff/) | [![NuGet Downloads](https://img.shields.io/nuget/dt/SemanticDiff.Diff.svg)](https://www.nuget.org/packages/SemanticDiff.Diff/) |
| `SemanticDiff.Git` | [![NuGet](https://img.shields.io/nuget/vpre/SemanticDiff.Git.svg)](https://www.nuget.org/packages/SemanticDiff.Git/) | [![NuGet Downloads](https://img.shields.io/nuget/dt/SemanticDiff.Git.svg)](https://www.nuget.org/packages/SemanticDiff.Git/) |
| `SemanticDiff.Layout` | [![NuGet](https://img.shields.io/nuget/vpre/SemanticDiff.Layout.svg)](https://www.nuget.org/packages/SemanticDiff.Layout/) | [![NuGet Downloads](https://img.shields.io/nuget/dt/SemanticDiff.Layout.svg)](https://www.nuget.org/packages/SemanticDiff.Layout/) |
| `SemanticDiff.Rendering` | [![NuGet](https://img.shields.io/nuget/vpre/SemanticDiff.Rendering.svg)](https://www.nuget.org/packages/SemanticDiff.Rendering/) | [![NuGet Downloads](https://img.shields.io/nuget/dt/SemanticDiff.Rendering.svg)](https://www.nuget.org/packages/SemanticDiff.Rendering/) |
| `SemanticDiff.Semantics` | [![NuGet](https://img.shields.io/nuget/vpre/SemanticDiff.Semantics.svg)](https://www.nuget.org/packages/SemanticDiff.Semantics/) | [![NuGet Downloads](https://img.shields.io/nuget/dt/SemanticDiff.Semantics.svg)](https://www.nuget.org/packages/SemanticDiff.Semantics/) |
| `SemanticDiff.Semantics.Roslyn` | [![NuGet](https://img.shields.io/nuget/vpre/SemanticDiff.Semantics.Roslyn.svg)](https://www.nuget.org/packages/SemanticDiff.Semantics.Roslyn/) | [![NuGet Downloads](https://img.shields.io/nuget/dt/SemanticDiff.Semantics.Roslyn.svg)](https://www.nuget.org/packages/SemanticDiff.Semantics.Roslyn/) |
| `SemanticDiff.Semantics.Xaml` | [![NuGet](https://img.shields.io/nuget/vpre/SemanticDiff.Semantics.Xaml.svg)](https://www.nuget.org/packages/SemanticDiff.Semantics.Xaml/) | [![NuGet Downloads](https://img.shields.io/nuget/dt/SemanticDiff.Semantics.Xaml.svg)](https://www.nuget.org/packages/SemanticDiff.Semantics.Xaml/) |
| `SemanticDiff.Workbench` | [![NuGet](https://img.shields.io/nuget/vpre/SemanticDiff.Workbench.svg)](https://www.nuget.org/packages/SemanticDiff.Workbench/) | [![NuGet Downloads](https://img.shields.io/nuget/dt/SemanticDiff.Workbench.svg)](https://www.nuget.org/packages/SemanticDiff.Workbench/) |
| `SemanticDiff.Controls.Uno` | [![NuGet](https://img.shields.io/nuget/vpre/SemanticDiff.Controls.Uno.svg)](https://www.nuget.org/packages/SemanticDiff.Controls.Uno/) | [![NuGet Downloads](https://img.shields.io/nuget/dt/SemanticDiff.Controls.Uno.svg)](https://www.nuget.org/packages/SemanticDiff.Controls.Uno/) |

## Highlights

| Area | Capability |
| --- | --- |
| Interactive diff graph | Skia-rendered canvas with draggable nodes, pannable viewport, zoom/fit controls, selectable graph nodes, persistent node layout, grouping containers, and SVG/PNG/PDF export. |
| Git-native workflow | Worktree, unstaged, staged, branch, and custom range diff scopes backed by `git` commands. |
| Branch and review request discovery | Searchable Git tab with local branches, remote branches, GitHub pull requests, and GitLab merge requests. |
| Review discussion workflow | Searchable Review tab for GitHub/GitLab threads, comments, replies, code navigation, comment annotations, and supported thread resolve/reopen operations. |
| Branch/PR/MR workspaces | Open branches, GitHub PRs, or GitLab MRs as cached graph workspace tabs so multiple review contexts stay available without reloading each time. |
| History and blame exploration | Workspace tabs for commit history timelines, file blame summaries, blame commit nodes, and per-file history context. |
| File diff tabs | Open file-specific diff/full-file tabs with syntax coloring, line numbers, folding metadata, text selection, font controls, annotation toggles, and diff-only/full-file switching. |
| Semantic analysis | Roslyn-backed C# analysis, XAML semantic analysis, syntax fallback, semantic edge projection, symbol navigation, symbol graph workspaces, hybrid semantic maps, and impact signals. |
| Symbol and semantic map workspaces | Open filtered symbol graphs, symbol-neighborhood graphs, file/folder semantic maps, and grouped node/edge views from the Symbols panel, file tree, or graph nodes. |
| Rich token rendering | TextMate tokenization plus language-service metadata for languages that are not covered by semantic diff providers. |
| Review assistance | Noise filtering, inline change markup, moved-code signals, conflict/context annotations, stage/unstage actions, blame summary, and review status feedback. |
| Cross-platform UI foundation | Uno Platform desktop target with Skia rendering and MVVM-oriented app state. |

## Screens and Feature Matrix

### Main Workspace

| Surface | Feature | Details |
| --- | --- | --- |
| Top bar | Repository identity | Shows the selected repository name and current diff context. |
| Top bar | Navigation actions | Previous/next change, open repository, reload, fit, layout, cancel. |
| Left pane | Tabbed workflow | Files, Git, Review, Symbols, and Settings tabs. |
| Workspace tabs | Multi-document review surface | Keeps the active diff graph open while adding closable branch/PR/MR graph workspaces, Git history, blame, and file diff tabs in the main area. |
| Workspace tabs | Cached graph state | Branch/PR/MR graph tabs preserve their loaded documents, semantic graph, layout, selected file, review threads, and viewport state when switching tabs. |
| Workspace tabs | Overflow navigation | Left/right tab strip buttons keep graph, PR/MR, history, blame, file, and symbol graph tabs reachable when many tabs are open. |
| Top bar | Quick graph controls | Layout, grouping, export, and settings controls are available directly from the workspace toolbar. |
| Canvas | Diff node graph | Renders changed files as code nodes with status badges, syntax colors, line numbers, inline change marks, and annotations. |
| Canvas | Groups | Groups related nodes and supports moving a group together with connected nodes. |
| Canvas | Selection | Selecting a node updates file/review context and enables node commands. |
| Canvas | Navigation | Focus selected node, reveal node in file tree, open file tabs, open blame tabs, previous/next changed line. |
| Canvas | Annotations | Hoverable/clickable annotations can focus related code, open review threads, or open blame/history context depending on the annotation type. |
| Canvas | Viewport | Smooth drag, pan, zoom, and fit-to-scene interaction. |
| Canvas | Export | Saves the current workspace graph as SVG, PNG, or PDF using Skia-backed rendering for full-fidelity output. |
| Status bar | Diagnostics | Shows current operation, review signals, cache state, watch state, and navigation status. |

### Files Tab

| Feature | Details |
| --- | --- |
| Changed file tree | Hierarchical file tree grouped by repository paths. |
| Search | Filters changed files by name/path. |
| Status badges | Displays change status, counts, and status color bars. |
| File icons | Uses platform/file-type icons with fallback glyphs. |
| Selection | Selecting a file focuses the corresponding diff node. |
| Context action | Right-click menu can navigate to the corresponding node, open diff/full-file tabs, open a blame tab, or open file/folder semantic map tabs. |

### Git Tab

| Feature | Details |
| --- | --- |
| Searchable references | Filters branches, remotes, pull requests, and merge requests. |
| Local branches | Shows current/default metadata where available. |
| Remote branches | Groups remote refs by remote name. |
| GitHub pull requests | Loads open PRs from the GitHub REST API, with pagination and remote-ref fallback. |
| GitLab merge requests | Loads open MRs from the GitLab API, with pagination and `refs/merge-requests/*/head` fallback. |
| Review request state | Settings can filter review requests by open, closed, merged, or all; open is the default. |
| Upstream preference | Prefers `upstream` over `origin` when selecting remote PR/MR sources. |
| Provider-aware labels | GitHub uses `#number` and PR labels; GitLab uses `!iid` and MR labels. |
| Selection | Selecting a PR/MR fetches a stable remote head ref and loads the corresponding branch diff. |
| Workspace tabs | Right-click a branch, PR, or MR to open it as a cached graph workspace tab without replacing other open graph workspaces. |
| History tabs | Right-click a branch, PR, or MR to open a visual commit timeline without replacing the graph tab. |
| History context menu | Right-click a history commit to copy its hash or set it as the comparison range start/end. |

### History and Blame Tabs

| Feature | Details |
| --- | --- |
| Visual commit timeline | Opens branch, PR, or MR history in a workspace tab with colored graph lanes, merge curves, commit dots, ref badges, and merge badges. |
| Progressive loading | Loads history in pages while scrolling so large repositories can be inspected without blocking the graph. |
| Commit actions | Context menu copies full commit hashes and can push a commit directly into the Settings range start/end fields. |
| File blame tab | Opens from file tree or graph node actions and groups file lines by commit. |
| Blame commit nodes | Shows commit id, subject, author/time, touched line ranges, and coverage bars for the selected file. |
| Blame change graph | Switches the blame tab from commit cards to a connected diff-node graph with one node per loaded file-history commit. |
| Blame timeline | Displays a compact or expanded bottom timeline for file history and blamed-line ownership. |

### Review Tab

| Feature | GitHub | GitLab |
| --- | --- | --- |
| Searchable review overview | Yes | Yes |
| Conversation comments | Loads PR issue comments | Loads MR discussions/notes |
| Diff review threads | Loads PR review comments via REST, or review threads via GraphQL when authenticated | Loads MR diff discussions |
| Add overview comment | Posts issue comment | Creates MR discussion |
| Reply to thread | Posts PR review-comment reply for REST threads; uses GraphQL for GraphQL thread IDs | Posts discussion note |
| Resolve/reopen thread | Supported for authenticated GraphQL review thread IDs | Supported for resolvable MR discussions |
| Navigate to code | Opens linked diff threads in the related canvas node and line | Opens linked diff discussions in the related canvas node and line |
| Comment annotations | Renders linked thread markers in diff nodes with a dedicated visibility toggle | Renders linked discussion markers in diff nodes with a dedicated visibility toggle |
| Annotation interaction | Clicking a linked comment annotation selects the matching Review item and focuses the related code | Clicking a linked discussion annotation selects the matching Review item and focuses the related code |
| System notes | Not separately marked by REST fallback | Marked when GitLab reports system notes |
| Authentication | `GITHUB_TOKEN` or `GH_TOKEN` | `GITLAB_TOKEN` or `GL_TOKEN` |

### Symbols Tab

| Feature | Details |
| --- | --- |
| Symbol search | Filters semantic navigation items. |
| Insight summary | Shows total symbols, changed symbols, linked symbols, and symbol-bearing file count. |
| Scope filters | Quick filters for all, changed, and linked symbols. |
| Facet filters | Scrollable kind and file facets let large diffs narrow the symbol list without shrinking the main result area. |
| Facet layout | Splitter between facets and symbol results lets the kind/file facet area be resized for large repositories. |
| Semantic navigation | Focuses declarations/symbols in the diff graph where semantic providers can resolve them. |
| Impact metadata | Shows symbol kind/location and edge information. |
| Symbol graph workspaces | Opens symbol-only graph tabs from the panel, filtered results, hot symbols, and file facets. |
| Semantic map workspaces | Opens hybrid file + symbol graph tabs from filtered results, symbol rows, file facets, file tree entries, and graph node context menus. |
| Semantic map visualization | Renders real file diff nodes connected to contained symbol nodes, while preserving filtered cross-symbol links such as references, bindings, resources, inheritance, and generated-file relationships. |
| Symbol graph filtering | Graph tabs can filter by search text, scope, kind, file, edge kind, view mode, layout, and grouping mode. |
| Symbol graph navigation | Symbol and semantic-map nodes preserve source-document routing so they can reveal source, open diff/full-file tabs, or open blame tabs. |
| Provider fallback | Uses fast syntax analysis when full MSBuild semantic workspace analysis is not selected or unavailable. |
| File fallback | Files without navigable type/member/resource anchors still appear as file-level symbols so unsupported file types are not hidden. |

### Settings Tab

| Section | Feature | Options |
| --- | --- | --- |
| Diff Scope | Diff source | Worktree, unstaged, staged, branch, range/custom refs. |
| References | Manual refs | Base ref and head ref text boxes with Apply refs. |
| Repository | Auto refresh | Watches repository changes and reloads after a debounce. |
| Semantic Analysis | Analysis mode | MSBuild workspace or fast syntax-only. |
| Layout | Graph layout | Auto, layered, grid, compact grid, status lanes. |
| Grouping | Graph grouping | None, folder, semantic, language, status. |
| Context | Diff context | Changed hunks, full diff, current file. |
| Review | Review mode | Precise or noise filter. |
| Review | Review request state | Open, closed, merged, or all PRs/MRs; defaults to open. |
| Review | Context folding | Toggle folded unchanged context annotations. |
| Review | Git staging | Stage/unstage selected changed file. |
| View | Theme | Dark/light mode. |
| View | Quick settings popup | Workspace toolbar Settings button opens the same settings controls without requiring a left-pane tab switch. |
| View | Semantic edges | Toggle semantic graph edge projection. |
| View | Annotation layers | Git refs, semantic, diagnostics, review, review comments, history, navigation, context folds. |

## Complete Feature Table

| Category | Feature | Implementation Notes |
| --- | --- | --- |
| Repository discovery | Open repository from picker | Resolves Git root from selected folder. |
| Repository watching | Auto refresh | File watcher ignores common build directories and reloads repository state. |
| Git diff loading | Worktree scope | Loads working tree changes. |
| Git diff loading | Unstaged scope | Loads unstaged changes. |
| Git diff loading | Staged scope | Loads staged changes. |
| Git diff loading | Branch/range scope | Loads branch or custom range diffs from selected refs. |
| Git metadata | Default branch discovery | Reads remote default branch where possible. |
| Git metadata | Branch discovery | Reads local and remote refs. |
| Git metadata | Review request discovery | Reads GitHub PRs and GitLab MRs from provider APIs with fallback refs. |
| Git metadata | Review request state filtering | Queries open, closed, merged, or all PRs/MRs and persists the selected state. |
| Git metadata | Visual history timeline | Streams paged `git log --graph` history for branches and PR/MR ranges, with merge lanes, curved branch/merge paths, branch/tag badges, and on-scroll loading. |
| Git metadata | History commit actions | Copies full commit hashes and sets Settings range start/end from the history context menu. |
| Git metadata | Blame summary | Shows selected-file blame metadata in review settings and opens a dedicated blame workspace tab. |
| Review actions | Stage/unstage | Runs `git add` and `git restore --staged` for selected file. |
| Review actions | Remote comments | Adds overview comments and replies to provider review threads. |
| Review actions | Thread state | Resolves/reopens GitLab discussions and authenticated GitHub GraphQL review threads. |
| Review navigation | Thread-to-code links | Opens selected review threads at matching changed-file nodes and line numbers. |
| Review navigation | Annotation-to-thread links | Comment annotations are hoverable/clickable entry points into the Review tab and matching code location. |
| Workspace tabs | File diff tabs | Opens file-specific tabs from the file tree or node context menu. |
| Workspace tabs | File display modes | File tabs can switch between diff-only and full-file display; diff-only can show changed hunks or an expanded full-file diff. |
| Workspace tabs | File annotations | File tabs can toggle diff annotation backgrounds, gutter markers, and accent lanes in both diff-only and full-file views. |
| Workspace tabs | Rich file viewer | File tabs use syntax-colored rendering, line numbering, diff gutters, fold-region metadata, text selection, font-size controls, and `Ctrl`/`Cmd` + mouse wheel zoom. |
| Workspace tabs | Branch/PR/MR graph workspaces | Opens selected Git refs or review requests as closable graph tabs and restores cached graph/review/layout state when switching between them. |
| Workspace tabs | Git history tabs | Opens timeline tabs for branches, PRs, and MRs without replacing the graph. |
| Workspace tabs | Blame tabs | Opens file-focused blame tabs with commit grouping, connected change graph view, coverage bars, and file-history timeline. |
| Workspace tabs | Symbol graph and semantic map tabs | Opens symbol-only graph workspaces and hybrid file + symbol semantic maps from filters, symbol list items, file/folder items, and graph nodes. |
| Diff parsing | Unified diff model | Converts Git diffs into document/line snapshots. |
| Diff parsing | Context modes | Supports changed-only, full-file, and current-file contexts. |
| Diff parsing | Noise filtering | Can hide or de-emphasize whitespace-only review noise. |
| Tokenization | TextMate grammars | Uses TextMateSharp and bundled grammars. |
| Tokenization | Language registry | Adds language definitions, comments, brackets, and token metadata for unsupported semantic file types. |
| Semantics | C# provider | Uses Roslyn workspace analysis merged with syntax fallback for better symbol coverage. |
| Semantics | XAML provider | Extracts XAML semantic structure. |
| Semantics | Fast mode | Syntax-only fallback for quicker analysis. |
| Semantics | Symbol insights | Provides searchable symbol results, scope filters, kind/file facets, hot symbols, changed/linked counts, and file-level fallback symbols. |
| Semantics | Symbol graph visualization | Builds symbol-only node/edge graphs with search, scope, kind, file, edge-kind, view-mode, layout, and grouping filters. |
| Semantics | Hybrid semantic maps | Builds file + symbol workspaces that connect actual diff file nodes to contained symbol nodes, then overlays filtered semantic edges between those symbols. |
| Graph layout | MSAGL layout | Lays out semantic graph structures. |
| Graph layout | Grouping modes | Groups nodes by folder, semantic structure, language, status, or no grouping. |
| Rendering | Skia canvas | Uses SkiaSharp rendering through Uno desktop. |
| Rendering | Graph export | Exports current workspace graphs to SVG, PNG, and PDF using Skia-backed rendering paths. |
| Rendering | Rich annotations | Renders Git status, syntax, semantic anchors, diagnostics, review signals, review comments, history, navigation, and context folds. |
| Rendering | Annotation interactions | Provides hover feedback and click behavior for navigating from annotations to review comments, blame tabs, and focused code. |
| Rendering | Node controls | Supports selection, dragging, pinning, font sizing, and layout persistence. |
| Rendering | Viewport controls | Pan, zoom, and fit. |
| State | Persistence | Saves repository path, theme, layout, visibility, scope, refs, selected PR/MR number, review request state, and pane width. |
| Keyboard | Accelerators | Provides shortcuts for common navigation, view, review, and layout commands. |
| Testing | Unit coverage | Tests Git discovery, review discussions, rendering, tokenization, layout, app state, and services. |

## Keyboard Shortcuts

The app registers Control shortcuts and, where supported, platform command-key variants for Control-based commands.

| Shortcut | Action |
| --- | --- |
| `Ctrl+O` | Open repository picker. |
| `Ctrl+R` | Reload repository. |
| `Ctrl+L` | Re-layout graph and fit to scene. |
| `Ctrl+0` | Fit canvas to scene. |
| `Esc` | Cancel current operation. |
| `F7` | Previous change. |
| `F8` | Next change. |
| `Alt+1` .. `Alt+5` | Select Files, Git, Review, Symbols, Settings tabs. |
| `Ctrl+F` | Focus file search. |
| `Ctrl+Shift+F` | Focus symbol search. |
| `Ctrl+B` | Focus Git reference search. |
| `Ctrl+Shift+P` | Focus Git reference search. |
| `Ctrl+Shift+Y` | Focus Review search. |
| `Ctrl+Enter` | Apply manual base/head refs. |
| `Ctrl+Alt+1` .. `Ctrl+Alt+5` | Set diff scope: worktree, unstaged, staged, branch, range. |
| `Ctrl+Alt+6` .. `Ctrl+Alt+8` | Set context: changed hunks, full diff, current file. |
| `Ctrl+Alt+A` | Toggle auto refresh. |
| `Ctrl+Alt+M` | Use MSBuild semantic analysis. |
| `Ctrl+Alt+F` | Use fast syntax semantic analysis. |
| `Ctrl+Alt+N` | Toggle review noise filter. |
| `Ctrl+Alt+C` | Toggle context folding. |
| `Ctrl+Alt+T` | Toggle light/dark theme. |
| `Ctrl+Alt+E` | Toggle semantic edges. |
| `Ctrl+Alt+G` | Toggle Git annotation layer. |
| `Ctrl+Alt+S` | Toggle semantic annotation layer. |
| `Ctrl+Alt+D` | Toggle diagnostics annotation layer. |
| `Ctrl+Alt+R` | Toggle review annotation layer. |
| `Ctrl+Alt+Q` | Toggle review comment annotation layer. |
| `Ctrl+Alt+H` | Toggle history/blame annotation layer. |
| `Ctrl+Alt+V` | Toggle navigation annotation layer. |
| `Ctrl+Alt+X` | Toggle context-fold annotation layer. |
| `Ctrl+Shift+S` | Stage selected file. |
| `Ctrl+Shift+U` | Unstage selected file. |
| `Ctrl+Shift+1` .. `Ctrl+Shift+5` | Set layout mode: auto, layered, grid, compact grid, status lanes. |
| `Ctrl+Shift+6` .. `Ctrl+Shift+0` | Set grouping mode: none, folder, semantic, language, status. |
| `Ctrl+Shift+R` | Reveal selected canvas node in file tree. |
| `Ctrl+Shift+B` | Open blame tab for the selected canvas node. |
| `Ctrl+Shift+N` | Focus selected canvas node. |
| `Ctrl+Alt+P` | Pin/unpin selected canvas node. |
| `Ctrl++` | Increase selected node font size. |
| `Ctrl+-` | Decrease selected node font size. |
| `Ctrl`/`Cmd` + mouse wheel in file tabs | Increase or decrease file diff/full-file text font size. |

## GitHub and GitLab Authentication

SemanticDiff can read public GitHub/GitLab metadata without a token when provider APIs allow it. Private repositories and write operations require provider tokens in environment variables.

| Provider | Environment Variables | Used For |
| --- | --- | --- |
| GitHub | `GITHUB_TOKEN` or `GH_TOKEN` | PR discovery, issue comments, review comments, GraphQL review threads, replies, resolve/reopen. |
| GitLab | `GITLAB_TOKEN` or `GL_TOKEN` | MR discovery, discussions, notes, replies, resolve/reopen. |

Notes:

- GitHub overview comments are PR issue comments.
- GitHub REST review comments can be read and replied to using `in_reply_to`.
- GitHub resolved state requires GraphQL review thread IDs, so the full thread state workflow is available when authenticated GraphQL review thread loading succeeds.
- GitLab discussion resolution is available for resolvable MR discussions.
- The app never stores provider tokens in app state; it reads them from the process environment.

## Architecture

| Project | Responsibility |
| --- | --- |
| `SemanticDiff.App` | Uno desktop app, XAML views, view models, keyboard accelerators, app composition. |
| `SemanticDiff.Controls.Uno` | Reusable Uno controls, resource dictionaries, graph canvas, code viewer, and UI primitives consumable without `SemanticDiff.App`. |
| `SemanticDiff.Workbench` | UI-framework-free builders/controllers for file diff tabs, symbol graphs, blame graphs, history lanes, reference browsing, review workflow state, repository loading, and workspace caching. |
| `SemanticDiff.Core` | Shared models, app state, service contracts, annotations. |
| `SemanticDiff.Git` | Git command integration, diff loading, branch/PR/MR discovery, blame, review discussions. |
| `SemanticDiff.Diff` | Diff document construction, tokenization, language registry, sample documents. |
| `SemanticDiff.Semantics` | Provider abstractions and semantic graph model integration. |
| `SemanticDiff.Semantics.Roslyn` | C# semantic/syntax analysis. |
| `SemanticDiff.Semantics.Xaml` | XAML semantic analysis. |
| `SemanticDiff.Layout` | Graph layout, grouping, and layout strategies. |
| `SemanticDiff.Rendering` | Skia scene model, renderer, and SVG/PNG/PDF export support. |
| `SemanticDiff.Tests` | Unit tests for core behavior, Git services, rendering, layout, and tokenization. |

Reusable package notes:

- `src/SemanticDiff.Controls.Uno/README.md` documents reusable Uno controls, resource dictionaries, and package readiness.
- `src/SemanticDiff.Workbench/README.md` documents UI-framework-free workbench services and package readiness.
- `samples/SemanticDiff.Controls.Uno.Sample` is a standalone Uno host that references `SemanticDiff.Controls.Uno` without referencing `SemanticDiff.App`.

## Technology Stack

| Technology | Usage |
| --- | --- |
| .NET | `net10.0` libraries and `net10.0-desktop` app target. |
| Uno Platform | Desktop application framework and XAML UI. |
| SkiaSharp | Custom diff graph rendering. |
| Roslyn | C# semantic analysis. |
| TextMateSharp | Syntax tokenization and grammar-backed highlighting. |
| MSAGL | Graph layout. |
| xUnit | Automated tests. |

## Requirements

- .NET SDK capable of building `net10.0` and `net10.0-desktop` projects.
- Git CLI available on `PATH`.
- A Git repository to inspect.
- Optional provider token for private or writable GitHub/GitLab review workflows.

## Build, Run, Test

```bash
dotnet restore
```

```bash
dotnet build src/SemanticDiff.App/SemanticDiff.App.csproj
```

```bash
dotnet run --project src/SemanticDiff.App/SemanticDiff.App.csproj -f net10.0-desktop
```

To run the standalone reusable controls sample:

```bash
dotnet run --project samples/SemanticDiff.Controls.Uno.Sample/SemanticDiff.Controls.Uno.Sample.csproj
```

```bash
dotnet test tests/SemanticDiff.Tests/SemanticDiff.Tests.csproj --no-restore
```

To publish for the current desktop host:

```bash
scripts/package-desktop.sh
```

To publish a specific unsigned app artifact locally, use the reusable publish script. This mirrors CI output and follows Uno's desktop publish model: folder packages for Linux/Windows and an unsigned `.app` bundle for macOS. See the [Uno publishing overview](https://platform.uno/docs/articles/uno-publishing-overview.html) for platform-specific signing and store-publishing details.

```bash
scripts/publish-app.sh --rid osx-arm64 --format app
```

```bash
scripts/publish-app.sh --rid linux-x64 --format folder
```

```bash
scripts/publish-app.sh --rid win-x64 --format folder
```

CI publishes unsigned app archives for `linux-x64`, `win-x64`, and `osx-arm64`. Release builds attach those archives to the GitHub release together with the NuGet packages. Signing is intentionally not configured yet.

## Typical Workflow

1. Open a repository with `Open` or `Ctrl+O`.
2. Choose a diff scope in Settings, or select a branch/PR/MR in the Git tab.
3. If needed, change the review request state filter from the default `Open` to `Closed`, `Merged`, or `All`.
4. Use the graph to inspect changed files, groups, semantic edges, and annotations.
5. Right-click branches or PR/MR entries in Git to open cached graph workspace tabs for side-by-side review contexts.
6. Right-click branches or PR/MR entries in Git to open visual history timeline tabs.
7. Right-click history commits to copy hashes or set the comparison range start/end.
8. Right-click files or graph nodes to open file diff tabs, then switch between diff-only/full-file display, changed-hunk/full-diff scope, and annotation visibility.
9. Open blame tabs from files or graph nodes when line ownership/history is part of the review.
10. Use Files and Symbols tabs to jump between files, file-level fallbacks, and semantic items.
11. Open symbol graphs or hybrid semantic maps from Symbols, file facets, file tree folders/files, or graph nodes to inspect how changed files contain and connect through symbols.
12. Use the Review tab to load discussion threads, search comments, add overview comments, reply to threads, or resolve supported threads.
13. Click comment annotations in nodes to select the linked Review item and focus the related code.
14. Stage or unstage selected files from Settings when reviewing local changes.
15. Tune layout, grouping, semantic mode, context, annotation layers, and graph export options for the review task.

## App State

SemanticDiff stores UI state in the current user's application data directory under `SemanticDiff/app-state.json`. Persisted state includes repository path, diff scope, theme, layout, grouping, selected branch/PR/MR number, review request state, annotation visibility, pane width, and saved node layout.

## Dependency Advisory Tracking

Current builds may report `NU1903` for the transitive Uno desktop dependency `Tmds.DBus.Protocol 0.21.2`. The warning is intentionally not suppressed. See [docs/dependency-advisories.md](docs/dependency-advisories.md).

## License

SemanticDiff is licensed under the [MIT License](LICENSE).
