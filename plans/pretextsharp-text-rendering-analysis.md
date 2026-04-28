# PretextSharp Text Rendering Integration Analysis

## Goal

Use the published PretextSharp `0.1.0-preview.5` packages as SemanticDiff's reusable text measurement and layout foundation instead of maintaining an internal Skia-only measurement implementation. Preserve current graph, file, diff, minimap, folding, selection, and annotation behavior while moving hot-path text measurement behind PretextSharp APIs.

## Current SemanticDiff Rendering Path

SemanticDiff currently has three major text-heavy surfaces:

| Surface | Current role | Text work performed |
| --- | --- | --- |
| Workspace graph nodes | `DiffSceneRenderer` in `SemanticDiff.Rendering` | Node title trimming, group labels, diff line numbers, code tokens, annotation chips, footer text, scrollbar text. |
| File/diff tabs | `CodeFileViewerControl` in `SemanticDiff.Controls.Uno` | Line numbers, fold markers, syntax tokens, text selection, diff annotations, minimap rendering, context-menu hit testing. |
| Shared metrics | `TextMetricsCache` in `SemanticDiff.Rendering` | Natural width cache, monospace advance cache, middle ellipsis, Skia typeface cache for drawing. |

Before `0.1.0-preview.5`, SemanticDiff could not reference the full PretextSharp package graph because backend packages referenced by the Pretext packages were not published. The interim implementation therefore added an internal `TextMetricsCache` that cached direct `SKFont.MeasureText` calls and formatted Pretext-compatible font descriptors for later replacement.

## PretextSharp 0.1.0-preview.5 Findings

The `v0.1.0-preview.5` release publishes the full package set needed by SemanticDiff:

| Package | Use in SemanticDiff |
| --- | --- |
| `Pretext` | Core `PretextLayout` APIs: `PrepareWithSegments`, `MeasureNaturalWidth`, wrapping and prepared segment model. |
| `Pretext.SkiaSharp` | Explicit SkiaSharp measurement backend matching SemanticDiff's Skia rendering surfaces. |
| `Pretext.Uno` | Reusable Uno render-scheduling helper for controls that currently use local deferred invalidation logic. |
| `Pretext.Contracts` | Transitive backend contracts used by `Pretext` and `Pretext.SkiaSharp`. |
| `Pretext.Layout` | Available for later wrapped text and obstacle-layout work; not required for this no-behavior-change migration. |

Relevant PretextSharp APIs:

| API | Integration use |
| --- | --- |
| `PretextLayout.SetTextMeasurerFactory(...)` | Select the SkiaSharp backend explicitly for deterministic graph and file viewer measurement. |
| `PretextLayout.PrepareWithSegments(text, font, options)` | Prepare measured text once per cache miss using the same CSS-like font descriptors already produced by SemanticDiff. |
| `PretextLayout.MeasureNaturalWidth(prepared)` | Replace SemanticDiff's direct `SKFont.MeasureText` width measurement. |
| `PrepareOptions(WhiteSpaceMode.PreWrap)` | Preserve spaces and hard breaks when measuring code/file text. SemanticDiff expands tabs to four spaces before measurement to match rendering and hit testing. |
| `Pretext.Uno.Controls.UiRenderScheduler` | Replace ad-hoc `DispatcherQueue.TryEnqueue` scheduling in the file viewer with the published Uno helper. |

## Design Decisions

| Decision | Rationale |
| --- | --- |
| Use `Pretext.SkiaSharp` explicitly instead of backend discovery | SemanticDiff renders with Skia, so Skia measurement best matches the drawn output and avoids ambiguous native backend selection. |
| Keep `TextMetricsCache` as SemanticDiff's app-level cache | Pretext caches font state and segment measurements internally, but SemanticDiff still needs bounded caching by `(font, text)` and existing middle-ellipsis behavior. |
| Keep Skia typeface/font creation for drawing | PretextSharp is responsible for preparation and measurement, not drawing. `DiffSceneRenderer` and `CodeFileViewerControl` still draw with Skia. |
| Keep fixed-row source-line model | Current interactions use source-line indexes for folding, diff annotations, minimap, review comments, and hit testing. Full Pretext wrapping would require a visual-row map and should be a separate feature. |
| Do not use `Pretext.Layout` in this phase | It is useful for future soft-wrap and obstacle layouts, but it would change current no-wrap file and node rendering behavior. |

## Implementation Plan

1. Add central package versions for `Pretext`, `Pretext.SkiaSharp`, and `Pretext.Uno` at `0.1.0-preview.5`.
2. Reference `Pretext` and `Pretext.SkiaSharp` from `SemanticDiff.Rendering`.
3. Reference `Pretext.Uno` from `SemanticDiff.Controls.Uno`.
4. Update `TextMetricsCache` so cache misses call:
   - `PretextLayout.SetTextMeasurerFactory(new SkiaSharpTextMeasurerFactory())` once per process;
   - four-space tab normalization before measurement;
   - `PretextLayout.PrepareWithSegments(measuredText, descriptor.ToPretextFontString(), new PrepareOptions(WhiteSpaceMode.PreWrap))`;
   - `PretextLayout.MeasureNaturalWidth(prepared)`.
5. Remove cached `SKFont` measurement state from `TextMetricsCache`; keep only the bounded width cache and drawing typeface cache.
6. Preserve existing `MiddleEllipsize` and monospace advance APIs so renderers do not need behavioral changes.
7. Improve drawing typeface parity by honoring italic in `GetTypeface`, matching `TextFontDescriptor.ToPretextFontString()`.
8. Replace the file viewer's local deferred render enqueue helper with `Pretext.Uno.Controls.UiRenderScheduler`.
9. Add tests proving text metrics are backed by Pretext-compatible behavior and continue to produce stable widths, middle ellipsis, and Pretext font strings.
10. Run restore, app build, tests, and diff validation.

## Implemented Scope

- Added `Pretext`, `Pretext.SkiaSharp`, and `Pretext.Uno` `0.1.0-preview.5` dependencies.
- Replaced direct `SKFont.MeasureText` measurement in `TextMetricsCache` with `PretextLayout.PrepareWithSegments` and `PretextLayout.MeasureNaturalWidth`.
- Preserved SemanticDiff's four-space code tab model before handing text to PretextSharp measurement.
- Kept Skia typeface creation only for drawing, not measurement.
- Updated typeface creation to honor italic style.
- Replaced `CodeFileViewerControl` deferred render scheduling with `UiRenderScheduler` from `Pretext.Uno`.

## Follow-Up Work

| Follow-up | Why it is separate |
| --- | --- |
| Optional soft-wrap in file/diff tabs | Requires mapping source lines to multiple visual rows and adapting selection, minimap, annotation, and fold hit testing. |
| Wrapped text inside diff nodes | Requires node body height/scroll model changes and line-to-review-comment navigation changes. |
| Rich inline chips using `PrepareRichInline` | Useful for annotation and review chips, but would change chip layout and hit testing. |
| `Pretext.Layout` obstacle layouts | Useful for future graph labels around annotations or minimap overlays, but not needed for direct measurement replacement. |

## Prior Graph Optimizations Preserved

This PretextSharp integration keeps the earlier large-graph rendering optimizations intact:

- scene render caches for normal and interactive frame modes;
- precomputed document line annotations, token/default text runs, and overview buckets;
- heatmap overview bodies for unreadable or over-budget node bodies;
- detailed text rendering for readable, selected, and pinned nodes within the detail budget;
- frame-local UI/code text style reuse instead of per-node Skia font and paint allocation;
- informative overview rendering during interactive drag, pan, and wheel zoom.
