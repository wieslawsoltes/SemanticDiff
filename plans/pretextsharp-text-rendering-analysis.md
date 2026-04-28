# PretextSharp Text Rendering Integration Analysis

## Goal

Use PretextSharp's text-preparation model to reduce repeated layout and measurement work in SemanticDiff's node graph, file diff, and full-file text renderers while preserving the current one-source-line-to-one-visual-row interaction model.

## PretextSharp Findings

PretextSharp provides a platform-neutral preparation and line-layout layer with:

- reusable prepared text snapshots for repeated layout at different widths;
- grapheme-aware wrapping and line-range walking;
- locale-aware segmentation and bidi-aware measurement paths;
- a pluggable `IPretextTextMeasurerFactory` backend model;
- SkiaSharp and Uno companion projects in the local source tree.

The local source is available at `/Users/wieslawsoltes/GitHub/PretextSharp`. The path originally provided for local code, `/Users/wieslawsoltes/GitHub/PhotosSharp`, is unrelated to PretextSharp.

NuGet status checked on 2026-04-28:

- `Pretext` is published at `0.1.0-preview.4`.
- `Pretext.Uno` is published at `0.1.0-preview.4`.
- Required backend packages referenced by the nuspec, including `Pretext.Contracts`, `Pretext.SkiaSharp`, `Pretext.Layout`, `Pretext.CoreText`, `Pretext.DirectWrite`, and `Pretext.FreeType`, are not available through the NuGet flat-container endpoints.

That package graph currently makes a direct package dependency unsafe for SemanticDiff CI and release builds. A sibling project reference would work locally but would make the repo non-reproducible outside this machine.

## Current SemanticDiff Bottlenecks

Node graph rendering in `SemanticDiff.Rendering` currently:

- creates short-lived `SKTypeface`, `SKFont`, and `SKPaint` style objects for every detailed node render;
- measures repeated strings with `SKFont.MeasureText` in hot paths, including node titles, group labels, footer chips, annotation chips, and code columns;
- draws the full code line in the default style and then overlays token text, which doubles text work and can produce glyph overdraw;
- recomputes annotation line groups every node render;
- uses binary-search middle ellipsis with repeated direct measurements.

File/diff rendering in `SemanticDiff.Controls.Uno` currently:

- measures monospace character advance repeatedly in paint, hit-test, and context-menu paths;
- measures line numbers and folded-line chips directly on every draw;
- uses immediate-mode token range drawing, which is correct but should reuse cached text metrics;
- relies on the same monospaced visual-column model for selection, context menus, minimap, diff gutters, and folding.

## Integration Design

SemanticDiff should keep the current row model stable and add a reusable Pretext-style measurement layer:

1. Add a bounded shared text metrics cache in `SemanticDiff.Rendering`.
2. Use font descriptors compatible with PretextSharp (`bold 17px "SF Pro Text"`, `15px "Cascadia Mono"`, etc.).
3. Route text width, monospace advance, and middle ellipsis through the shared cache.
4. Use the cache from both graph node rendering and Uno file/diff rendering.
5. Stop drawing tokenized node lines twice; render default gaps and colored token ranges once.
6. Keep the seam narrow so a future published `Pretext.SkiaSharp` package can replace the Skia-backed local measurer without touching renderers.

## Why Not Full Pretext Line Wrapping Yet

PretextSharp's `PrepareWithSegments`, `LayoutWithLines`, and `WalkLineRanges` are most valuable when a logical line can wrap into multiple visual rows. SemanticDiff's current graph and file views use line index as the interaction primitive for:

- scrollbar math;
- line hit testing;
- annotation lookup;
- review/comment navigation;
- folded-region mapping;
- selection coordinates;
- minimap projection.

Changing this to wrapped visual rows requires a broader model migration. The first high-value step is cached measurement and single-pass token drawing, which improves current rendering without destabilizing navigation semantics.

## Implementation Plan

1. Create `TextMetricsCache` in `SemanticDiff.Rendering` with bounded caches for natural width, monospace advance, and middle ellipsis.
2. Add Pretext-compatible font descriptor helpers and keep the API renderer-neutral.
3. Wire node graph rendering to use cached metrics for group labels, title ellipsis, font controls, inline highlights, annotation chips, and footer chips.
4. Change node code rendering to draw non-token and token runs exactly once.
5. Wire file/diff rendering to use cached monospace advance for paint, hit testing, gutter layout, selection, and context menu line resolution.
6. Use cached widths for line numbers, folded-region chips, and empty-state positioning.
7. Add unit tests for metric caching, font descriptors, and middle ellipsis behavior.
8. Validate with app build and test suite.

## Future Work

When all PretextSharp backend packages are published, replace the internal Skia measurement implementation behind `TextMetricsCache` with `PretextLayout.PrepareWithSegments` and `MeasureNaturalWidth`. After that, introduce a wrapped visual row map for optional soft-wrap mode in file tabs and node bodies.

## Implemented In This Change

- Added `TextMetricsCache` and `TextFontDescriptor` in `SemanticDiff.Rendering`.
- Reused cached typefaces and font measurements through a shared metrics service.
- Replaced repeated direct measurements in the graph renderer with cached width and monospace-advance lookups.
- Switched node token rendering from full-line plus overlay to single-pass default/token run rendering.
- Reused cached metrics in the Uno file/diff renderer for character advance, line numbers, diff gutter alignment, folded-region chip widths, empty-state positioning, selection, hit testing, and context-menu line resolution.
- Added unit coverage for stable measurements, middle ellipsis, and Pretext-compatible font descriptor formatting.

## Additional Large-Graph Optimization Pass

Follow-up profiling showed huge graph slowdown came from detailed body rendering for too many visible nodes, not only raw text measurement. The renderer now also:

- keeps a scene render cache for every frame mode, including interactive frames;
- precomputes per-document line annotations, token/default text runs, and overview buckets;
- renders unreadable or over-budget node bodies as fast diff heatmap overviews instead of detailed code text;
- preserves detailed text rendering for readable nodes, selected nodes, and pinned nodes when within the detail budget;
- reuses frame-local UI/code text style resources instead of allocating Skia fonts and paints per node;
- keeps interactive drag/pan rendering informative by drawing overview bodies instead of generic placeholders.
