# .NET 10 Performance and Architecture Optimization Plan

## Goals

- Move hot-path code toward modern .NET allocation-light primitives.
- Preserve MVVM, service boundaries, and reusable control packaging.
- Improve input diff parsing, tokenization, text layout, and rendering without changing user-visible behavior.
- Document repo-wide expectations in `AGENTS.md` so future work keeps performance and architecture constraints explicit.

## Findings

| Area | Hot spot | Impact | Planned response |
| --- | --- | --- | --- |
| Inline diff annotations | Segment splitting stores substring text for every segment. LCS compares allocated strings. | High allocation on large changed files. | Store source string plus start/length and compare spans. |
| Plain text tokenization | Identifier tokenization slices strings and uppercases SQL identifiers before keyword lookup. | Allocates for every identifier, including non-keywords. | Add cached keyword lookup with length prefilter and span-based candidate checks. |
| TextMate tokenization | Page lookup uses LINQ `Where().OrderByDescending().First()` per missing page. | Extra allocation and O(n log n) behavior as cache grows. | Replace with one dictionary scan for nearest prior page. |
| Code file rendering | Token drawing sorts tokens and creates replaced substrings per run during rendering. | Per-frame allocations and sorting during scroll. | Trust tokenizer ordering, avoid LINQ, avoid tab replacement unless needed. |
| Minimap rendering | Visual column calculation rescans each line from the start for every token. | O(tokens * line length) for wide lines. | Use no-tab fast path and bounded visual-column helper. |
| Node diff rendering | Line layout sorts tokens and text drawing allocates tab-normalized strings. | Per-cache and per-render overhead in large graphs. | Remove token sort and avoid tab replacement unless needed. |
| Interactive graph rendering | Interactive edge drawing rebuilds a node dictionary on every render. | Dragging and zooming allocate and scale with graph size. | Reuse resolved render-cache edge endpoints. |
| Editor text mutations | Edit document normalization uses `Replace().Split()` and LINQ when applying replacement lines. | Large paste and edit operations allocate intermediate arrays. | Use single-pass newline normalization and explicit line splitting. |
| Node editor text input | Inserted node text is normalized with chained `Replace` calls before insertion. | Pasting into code nodes allocates full normalized copies. | Stream CR/LF normalization while inserting characters. |
| Code layout | Visible row building uses grouping/sorting/LINQ and snapshots active regions per row. | Potential high allocation in huge full-file views. | Replace LINQ with a start-indexed fold map, single sorted region array, and versioned active-region snapshots. |
| Editable tokenization | Editing clears token cache and retokenizes the full document. | Slow typing in large files. | Defer larger incremental tokenizer design to a separate editor pass. |

## Implementation Plan

1. Add repository guidance in `AGENTS.md` for .NET 10 performance APIs, generated regex, SIMD, source generators, MVVM, SOLID, reusable controls, and service packaging.
2. Optimize inline diff segment matching with span-backed segments.
3. Optimize plain text keyword lookup with cached length prefilters and case-aware span handling.
4. Remove render-time token sorting and avoid unnecessary tab replacement in file and node text rendering.
5. Add no-tab fast paths for visual-column calculations used by editor, minimap, and layout code.
6. Replace TextMate cached-page lookup LINQ with a single-pass lookup.
7. Reuse cached render graph edges during interactive rendering so dragging and zooming do not rebuild node dictionaries.
8. Replace editor document newline normalization and line splitting with single-pass helpers.
9. Stream node editor inserted text without allocating a normalized copy.
10. Run focused tests around diff/tokenizer behavior, then full unit tests and `git diff --check`.
11. Replace diff text line splitting with span-based CR/LF scanning to avoid full-document normalization and split arrays.
12. Replace inline diff LCS matrix allocation with pooled one-dimensional storage and span-backed segment comparisons.
13. Replace code layout LINQ grouping and per-row active-region snapshots with indexed fold lookup and snapshot reuse.

## Deferred Larger Work

- Replace full-document editable tokenization with incremental page invalidation and background token refresh.
- Continue toward a dedicated interval index for fold regions if visible-row construction shows up again in profiling.
- Introduce reusable text-run or text-blob caches for Skia rendering once invalidation and memory limits are defined.
- Add BenchmarkDotNet suites for tokenizer, inline diff, full-file render preparation, minimap preparation, and graph render-cache construction.
- Evaluate source-generated language keyword tables so fallback tokenization can avoid runtime hash-set construction entirely.

## Verification

- Unit tests must pass for diff parsing, inline diff annotation, tokenization, file rendering models, and git diff loading.
- `git diff --check` must pass.
- Performance-sensitive changes should include before/after notes when benchmark coverage exists.
