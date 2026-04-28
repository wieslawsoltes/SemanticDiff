---
title: "Workspace Tabs"
---

# Workspace Tabs

The main area is a multi-document review surface.

## Tab types

| Tab type | Purpose |
| --- | --- |
| Diff Graph | Current repository diff workspace. |
| Branch/PR/MR graph | Cached graph workspace for another reference without replacing the current one. |
| File tab | Focused diff-only or full-file reading. |
| History tab | Visual commit timeline for a branch, PR, or MR. |
| Blame tab | File blame timeline and connected change graph. |
| Symbol graph | Symbol-only semantic relationship graph. |
| Semantic map | Hybrid file + symbol graph. |

## Caching policy

Graph workspaces keep their loaded documents, semantic graph, layout, selected file, review threads, and viewport state. This makes switching between several PRs or MRs practical during review.
