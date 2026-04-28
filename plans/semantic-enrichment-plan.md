# Semantic Enrichment Implementation Plan

## Current Model

SemanticDiff already produces a language-neutral `SemanticGraph` made of anchors and edges:

- `SemanticAnchor` identifies file, type, member, XAML, resource, binding, project, and diagnostic locations.
- `SemanticEdge` connects anchors with reference, containment, partial type, XAML, binding, resource, project, generated-file, and rename/move relationships.
- `SemanticNavigationIndex` turns anchors into navigable symbols for the Symbols panel and symbol graph views.
- `SemanticImpactAnalyzer` marks changed symbols by intersecting anchor lines with changed diff lines.
- `BuiltInDiffAnnotationProvider` projects part of the graph into node and line annotations for the canvas.

The weak point is projection reuse. Graph nodes, file tabs, diff tabs, minimaps, and context menus each see different slices of semantic information. File/diff views mainly render diff line kinds and token colors, so semantic anchors and impact links are not visible at the line level.

## Design Goals

1. Build one reusable semantic document insight index from `SemanticGraph + DiffDocumentSnapshot[]`.
2. Expose per-document summary counts and per-line semantic hints.
3. Use the same insights in graph annotations, full file views, diff-only views, minimaps, hover tooltips, and context menus.
4. Keep rendering fast: pre-aggregate by document and line, draw only visible rows, and avoid per-frame graph traversals.
5. Preserve existing workflows and annotation visibility settings.

## Data Model

Add presentation-safe records in `SemanticDiff.Core`:

- `SemanticLineInsight`: line number, label, detail, dominant anchor kind, anchor count, link count, changed/impacted flags.
- `SemanticDocumentInsight`: document id, aggregate counts, and pre-grouped line insights.

Add `SemanticDocumentInsightIndex` in `SemanticDiff.Semantics`:

- Builds incident edge counts once.
- Builds changed anchor set from existing changed-line index.
- Groups anchors by document and source line.
- Marks line insights as changed when a changed anchor is present.
- Marks line insights as impacted when a line owns linked anchors, especially links touching changed anchors.
- Produces short labels and detailed strings that can be used by graph annotations and tooltips.

## Graph Integration

Update `BuiltInDiffAnnotationProvider` to use the document insight index:

- Add a node-level semantic summary annotation for each document with semantic content.
- Replace per-anchor semantic line spam with aggregated per-line semantic annotations.
- Keep parser diagnostics as diagnostics and keep existing impact annotations for compatibility.

## File/Diff Text Integration

Update `FileDiffTabViewModel`:

- Store `SemanticDocumentInsight` and line insights.
- Surface semantic summary text in file tab headers.
- Allow open tabs to refresh semantic insights after asynchronous MSBuild refinement.

Update `MainViewModel`:

- Maintain a current semantic document insight lookup alongside symbol navigation.
- Refresh open file tabs whenever semantic navigation is refreshed.
- Pass the correct insight to newly opened file diff/full file tabs.

Update `CodeFileViewerControl`:

- Add `SemanticLineInsights` and `ShowSemanticInsights` dependency properties.
- Pre-index insights by line number on property updates.
- Draw semantic markers in the gutter for diff-only and full-file modes.
- Draw right-side semantic chips for visible lines when space allows.
- Add minimap semantic ticks independent of diff ticks.
- Add hover state and tooltip text for semantic markers.

## Navigation Integration

Reuse existing context-menu navigation:

- Line context menus already find symbols for the selected line and can open symbol maps/graphs.
- Add the line semantic insight detail to the menu so users can see why the line is important even before opening a graph.

## Tests

Add/update tests for:

- Semantic document insight aggregation.
- Graph annotation projection using semantic document insights.
- File diff view model preserving semantic insight data.
