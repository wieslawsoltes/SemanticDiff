---
title: "Semantic Analysis"
---

# Semantic Analysis

SemanticDiff combines language services with diff information.

## Analysis modes

| Mode | Behavior |
| --- | --- |
| MSBuild semantics | Uses Roslyn workspace loading for richer C# symbol relationships. |
| Fast syntax | Uses syntax extraction when full workspace loading is too expensive or unavailable. |
| XAML semantics | Extracts XAML resources, bindings, names, and type/member anchors. |
| File fallback | Emits file-level symbols when a file has no supported language provider. |

## Graph outputs

- Symbol-only graphs focus on symbols and their relationships.
- Hybrid semantic maps connect real file diff nodes to contained symbol nodes.
- Filtering can narrow by text, scope, kind, file, edge kind, view mode, layout, and grouping.

## Why file fallback matters

Unsupported file types should not disappear from semantic workflows. File-level fallback keeps every changed file navigable, even when only TextMate coloring is available.
