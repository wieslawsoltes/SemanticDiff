---
title: "Overview"
---

# Overview

SemanticDiff is optimized for code review sessions where the relationship between files, symbols, comments, commits, and generated or linked code matters more than a linear patch.

## Primary workflows

| Workflow | Use it when |
| --- | --- |
| Diff Graph | You need a visual map of changed files with semantic links and review annotations. |
| Git References | You want to switch between worktree, branches, ranges, GitHub PRs, or GitLab MRs without typing refs manually. |
| Review Panel | You need to inspect, filter, add, reply to, resolve, and navigate review comments. |
| History Tabs | You want commit context for a branch, PR, or MR while keeping the graph open. |
| Blame Tabs | You need file ownership and change history for a specific file. |
| File Tabs | You want focused diff-only or full-file views with coloring, line numbers, annotations, folding metadata, and font controls. |
| Symbol Maps | You want semantic relationships across symbols and the file nodes that contain them. |

## App versus libraries

Use the desktop app when you want a complete review workflow.

Use the libraries when you want to embed SemanticDiff behavior in another application:

- `SemanticDiff.Core` for contracts and models.
- `SemanticDiff.Diff` for diff parsing and tokenization.
- `SemanticDiff.Git` for Git, PR/MR, review, history, and blame services.
- `SemanticDiff.Layout` and `SemanticDiff.Rendering` for graph layout and Skia rendering.
- `SemanticDiff.Semantics.*` for symbol extraction and semantic relationships.
- `SemanticDiff.Workbench` for host-independent orchestration.
- `SemanticDiff.Controls.Uno` for reusable Uno UI controls.
