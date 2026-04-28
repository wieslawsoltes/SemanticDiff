---
title: "Architecture"
---

# Architecture

SemanticDiff is split into library layers and a desktop app shell.

| Layer | Project | Role |
| --- | --- | --- |
| Contracts | `SemanticDiff.Core` | Shared models for diffs, annotations, Git refs, reviews, symbols, history, blame, and tabs. |
| Text | `SemanticDiff.Diff` | Diff parsing, text models, tokenization, line annotations, folding metadata. |
| Git | `SemanticDiff.Git` | Git commands, provider APIs, PR/MR review workflows, history, blame. |
| Semantics | `SemanticDiff.Semantics.*` | Symbol and relationship extraction for C#, XAML, and fallback file types. |
| Layout | `SemanticDiff.Layout` | Graph positioning and grouping. |
| Rendering | `SemanticDiff.Rendering` | Skia-ready scenes, annotations, and export. |
| Workbench | `SemanticDiff.Workbench` | Host-independent orchestration and tab state. |
| Controls | `SemanticDiff.Controls.Uno` | Reusable Uno controls. |
| App | `SemanticDiff.App` | Desktop shell, command wiring, settings, and workflow composition. |

## Design goal

The desktop app should be a composition layer. Reusable parsing, Git, semantic, layout, rendering, and workbench logic belongs in package projects so other hosts can reuse the same behavior.
