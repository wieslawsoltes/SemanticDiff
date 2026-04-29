# Node Code Workspace Design

## Goal

Add an IDE-like graph workspace mode where each file node can render the full file instead of only the diff hunk view. The mode must work beside the existing diff graph, preserve graph interactions, support per-node and global display toggles, and allow in-node text editing without replacing the high-performance Skia canvas renderer.

## Scope

- Add a global workspace toggle for full-code nodes.
- Add a global workspace toggle for node editing.
- Add per-node context actions to show full code, return to diff view, and enable/disable editing.
- Render full-code nodes with line numbers, code coloring, folded regions, collapsed-fold placeholders, and IDE-style fold guides.
- Keep graph layout, selection, panning, zooming, export, semantic edges, annotations, and file/review navigation intact.
- Load full-file content lazily from the active Git comparison when the full-code mode is enabled or a node requests full code.

## Non-Goals

- Persist edited node text back to disk or Git. Node editing is an in-memory workspace editing surface until save/apply workflows are designed.
- Replace the file/diff tab editor. The tab editor remains the richer single-file editor; graph nodes provide a spatial multi-file editor.
- Embed XAML editor controls into graph nodes. The graph uses Skia rendering for scale, export fidelity, and performance.

## Architecture

### Scene Model

`DiffNode` remains the identity and layout owner for a file. It now owns two document views:

- `DiffDocument`: the original diff/hunk document used by the classic diff graph.
- `FullFileDocument`: an optional tokenized full-file document with diff annotations applied to line kinds.

The node exposes an effective `Document` that switches between the diff and full-file documents based on:

- `DiffCanvasScene.ShowFullFileNodes`, the global workspace mode.
- `DiffNode.FullFileViewOverride`, the nullable per-node override.

The scene bumps its render version whenever the effective document, fold state, or editor state changes so renderer caches stay correct.

### Folding

Full-file nodes store fold regions from `CodeFoldingService`. The node builds visible rows from full document lines and collapsed fold starts. The renderer consumes visible rows instead of raw line indexes so collapsed regions affect scrolling, hit testing, and rendering consistently.

### Editing

Editing is handled by a lightweight in-node text buffer owned by `DiffNode`. `DiffCanvasControl` routes text input to the focused editable node. Editing operations update an in-memory `DiffDocumentSnapshot` using plain context lines so scroll, line numbers, and selection remain stable. Initial full-file syntax coloring is preserved until the user edits the buffer.

### Loading

`MainViewModel` loads node full-file documents using the same path as file tabs:

1. `LoadFullFileTextAsync` obtains the file content for the active comparison.
2. `CreateTokenizedFullFileDocumentAsync` tokenizes it with TextMate.
3. `CodeFoldingService` creates fold regions.
4. `FileDiffDocumentBuilder` overlays changed-line kinds onto full-file lines.
5. The scene attaches the result to the corresponding node.

## UI

- Workspace toolbar:
  - `Code nodes`: global toggle for full-file node bodies.
  - `Edit nodes`: global toggle allowing editable full-file node bodies.
- Node context menu:
  - `Show full code in node` / `Show diff in node`.
  - `Use workspace code mode` when a node has an override.
  - `Enable node editing` / `Disable node editing`.
- Node title/footer:
  - Shows `full` and `edit` state labels when active.
- In-node editor:
  - Click in code body places the caret.
  - Character input, Enter, Tab, Backspace, Delete, arrows, Home, End, PageUp, and PageDown operate on the focused node.

## Implementation Plan

1. Extend rendering model with full-file documents, node fold rows, and editor state.
2. Update scene hit testing, scrolling, view-state capture, and context menu actions.
3. Update the renderer to draw visible rows, full-file line numbers, fold guides, collapsed fold markers, and editing caret.
4. Add view-model commands to lazily attach full files and toggle global workspace modes.
5. Add toolbar controls and event wiring in `MainPage`.
6. Add tests for scene mode switching, per-node overrides, fold row scrolling, and in-node editing.
7. Run build and tests.
