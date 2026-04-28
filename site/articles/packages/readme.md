---
title: "Packages"
---

# Packages

SemanticDiff ships reusable packages for applications that need only part of the desktop workbench.

| Package | Responsibility |
| --- | --- |
| [SemanticDiff.Core](core) | Shared data contracts for diffs, annotations, Git refs, reviews, symbols, and graph models. |
| [SemanticDiff.Diff](diff) | Unified diff parsing, hunk modeling, file text models, and TextMate tokenization. |
| [SemanticDiff.Git](git) | Git command execution, branch/PR/MR discovery, reviews, history, and blame services. |
| [SemanticDiff.Layout](layout) | File, symbol, semantic-map, blame, and workspace graph layout services. |
| [SemanticDiff.Rendering](rendering) | Skia scene models, graph rendering, annotations, and export services. |
| [SemanticDiff.Semantics](semantics) | Language-neutral semantic graph and symbol relationship models. |
| [SemanticDiff.Semantics.Roslyn](semantics-roslyn) | C# semantic extraction backed by Roslyn and MSBuild workspace loading. |
| [SemanticDiff.Semantics.Xaml](semantics-xaml) | XAML semantic extraction and relationship detection. |
| [SemanticDiff.Workbench](workbench) | Host-independent orchestration for file tabs, Git refs, reviews, symbols, history, blame, and workspace tabs. |
| [SemanticDiff.Controls.Uno](controls-uno) | Reusable Uno controls for graph, diff, file, annotation, and review surfaces. |

## API docs

The generated [API Reference](../../api) includes the package projects listed above.
