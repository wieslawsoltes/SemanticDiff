# Code File Editing Support Plan

## Reference Architecture: AvaloniaEdit

AvaloniaEdit splits editor responsibilities into narrowly scoped layers:

| Layer | AvaloniaEdit type | Responsibility | SemanticDiff adaptation |
| --- | --- | --- | --- |
| Control shell | `TextEditor` | Template host, document property, options, text changed events | `CodeFileViewerControl` remains the Uno/Skia host and exposes editable dependency properties |
| Document model | `TextDocument`, `DocumentLineTree`, `UndoStack` | Mutable text, line indexing, snapshots, undo/redo, change events | Add `CodeTextEditorDocument` with normalized lines, caret-aware replace operations, and undo/redo snapshots |
| Input/caret | `TextArea`, `Caret`, input handlers | Keyboard, pointer selection, caret navigation, clipboard, IME hook points | Add focused keyboard handling, character input, caret movement, selection ranges, and clipboard support in `CodeFileViewerControl` |
| Rendering | `TextView`, visual lines, layers | Virtualized visual lines, selection layer, caret layer, margins | Reuse existing Skia/Pretext renderer; draw caret and selection over visible rows without replacing the renderer |
| Folding | `FoldingManager`, margins | Collapsible regions integrated with visual line generation | Keep existing `CodeTextLayout` fold row builder and collapse state; reset stale collapsed state after edits |
| Services | TextView services, highlighters | Extensible syntax, margins, completion, semantic features | Preserve `DiffLine` tokens/semantic annotations for unchanged lines and invalidate edited lines until re-tokenized |

## Design Goals

- Keep existing diff, minimap, semantic annotations, folding, and context-menu navigation intact.
- Make full-file views editable while keeping diff-only patch views read-only by default.
- Treat editing as an engine capability first: reusable in any host through dependency properties and events.
- Avoid coupling editing to repository writes in this phase. The editor updates the in-memory tab text; save/apply workflows can be layered on top.
- Preserve performance: text is line-backed, visible-row rendering remains virtualized, and edits invalidate only layout/cache state.

## Public Surface

`CodeFileViewerControl` gains:

| API | Type | Purpose |
| --- | --- | --- |
| `IsEditable` | `bool` DP | Enables editing for non-diff file views |
| `Text` | `string?` DP | Editable source text, suitable for two-way binding |
| `TextEdited` | event | Notifies hosts after a text mutation |

The control continues to accept `Lines` and `FoldRegions`. When editing is enabled, `Text` is the authoritative buffer; if `Text` is not provided, the buffer is initialized from `Lines`.

## Editing Behavior

| Interaction | Behavior |
| --- | --- |
| Pointer click | Focuses the viewer and places the caret at the hit-tested text position |
| Pointer drag | Extends selection across visible rows |
| Character input | Replaces selection or inserts at caret |
| Enter | Splits the current line |
| Tab | Inserts four spaces |
| Backspace/Delete | Deletes selection or adjacent character/newline |
| Arrow keys | Moves caret by character/line; Shift extends selection |
| Home/End | Moves to line start/end; Ctrl/Cmd moves to document start/end |
| PageUp/PageDown | Moves by viewport-sized pages |
| Ctrl/Cmd+A | Selects all visible/editable text |
| Ctrl/Cmd+C/X/V | Copy, cut, paste using platform clipboard |
| Ctrl/Cmd+Z/Y | Undo and redo document snapshots |
| Ctrl/Cmd+wheel | Existing font-size zoom remains unchanged |

## Rendering Behavior

- Selection remains drawn as translucent line spans.
- Caret is drawn as a focused editor layer in the code text area.
- Existing line numbers, folding markers, semantic badges, minimap, and diff annotations remain active.
- Edited lines are emitted as context `DiffLine`s with empty token spans until a future tokenizer service re-colors them.
- Unchanged lines can reuse existing `DiffLine` token metadata when their text still matches the original line at the same index.

## Scope Boundaries

Implemented now:

- Editable in-memory full-file buffer.
- Caret, selection, keyboard editing, clipboard, undo/redo.
- Two-way binding from file tabs into `FileDiffTabViewModel.FullText`.
- Edit toggle in file tabs.

Deferred follow-up work:

- Persist edited text back to disk or generate a patch.
- Incremental TextMate retokenization after edits.
- IME composition UI beyond Uno `CharacterReceived` support.
- Rectangular selection, virtual space, multi-caret editing, and find/replace.
