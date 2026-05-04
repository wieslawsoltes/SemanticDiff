# AGENTS.md

## Engineering Baseline

This repository targets modern .NET and Uno Platform code. Keep changes fast, measurable, modular, and shippable as reusable libraries wherever possible.

## Performance Requirements

- Treat large diffs, large files, and dense graph canvases as primary workloads, not edge cases.
- Prefer allocation-free or allocation-light APIs in hot paths: `ReadOnlySpan<T>`, `Span<T>`, `ReadOnlyMemory<T>`, `Memory<T>`, `ArrayPool<T>`, `ValueTask`, `CollectionsMarshal`, `SearchValues<T>`, `FrozenDictionary`, and `FrozenSet` where appropriate.
- Use SIMD through `Vector<T>`, `Vector128<T>`, `Vector256<T>`, or platform intrinsics only when benchmarks show a benefit and a scalar fallback remains clear.
- Use source generators for repeated boilerplate, binding-safe metadata, and generated lookup tables when that reduces runtime work.
- Use `[GeneratedRegex]` for production regex. Do not allocate new `Regex` instances or use interpreted regex in hot paths.
- Avoid `string.Replace().Split()`, broad LINQ chains, repeated substring creation, repeated sorting, and per-frame allocations in parsing, tokenization, layout, and rendering loops.
- Prefer single-pass parsers over multi-pass transformations for diff text, git output, tokenization, and code layout.
- Keep render methods deterministic and allocation-light. Do not sort, group, query, or rebuild large collections per frame unless cached and invalidated explicitly.
- Use cancellation tokens for long-running git, workspace, tokenization, semantic, and rendering preparation work.
- Keep CPU-bound background work off the UI thread and report progress through reusable loading state.
- Benchmark large real repositories before and after performance changes. Include allocation measurements for parser, tokenizer, and rendering-prep changes.
- Add regression tests for parser and tokenizer behavior before replacing algorithms.

## Architecture Requirements

- Maintain a strict view/viewmodel split. Views own presentation, input plumbing, and control-specific rendering. ViewModels own state, commands, and navigation intent. Services own I/O, parsing, git, workspace, semantic, and persistence logic.
- Follow SOLID. Prefer small composable services with explicit interfaces over large feature classes with hidden dependencies.
- Reusable controls must live outside the app-specific layer when practical, avoid app-only dependencies, and expose clear contracts that can ship independently.
- Reusable services must avoid UI framework dependencies unless they are explicitly control or rendering services.
- Dependency direction should stay inward: app depends on controls, rendering, diff, git, and core; core libraries do not depend on the app.
- Keep cross-platform Uno code in shared projects and isolate platform-specific logic at edges through partial classes or platform services.
- Use dependency injection for expensive or replaceable services. Avoid service locators in reusable libraries.
- Make serialization and persistence contracts versioned and tolerant of missing fields.
- Keep public reusable APIs documented, testable, and package-ready.

## MVVM and UI Rules

- Prefer typed bindings and `x:Bind` for hot or high-frequency UI paths when available.
- Do not put git, file-system, workspace loading, semantic analysis, or tokenization work directly in views.
- Use virtualization for large item collections and avoid binding expressions that trigger expensive recomputation during scrolling.
- Batch observable collection updates. Avoid thousands of item-level UI notifications when a reset or range update is available.
- Keep commands asynchronous when they perform I/O or CPU-heavy work. Surface busy state and cancellation.

## Reusable Controls and Services

- Controls should be usable with minimal dependencies and no app-specific ViewModel requirement.
- Rendering helpers should accept immutable snapshots or explicit render models, not mutable application state.
- Text rendering, diff rendering, editor primitives, minimap, folding, selection, and annotation controls should be designed for stand-alone packaging.
- Diff, git, workspace, semantic, patch comparison, and persistence services should be usable by tests and CLI tools without loading the app UI.
- New reusable APIs should include unit tests and, for hot paths, benchmark or smoke-test coverage.

## Review Checklist

- Does the change avoid UI-thread blocking for large repositories?
- Does it reduce or preserve allocations in hot paths?
- Are spans, pooling, frozen collections, generated regex, or source generators appropriate here?
- Is the view/viewmodel/service boundary preserved?
- Can the new control or service be reused outside `SemanticDiff.App`?
- Are cancellation, progress, and error paths explicit?
- Are large-file and large-diff behavior covered by tests or benchmark notes?
