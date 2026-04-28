---
title: "SemanticDiff.Diff"
---

# SemanticDiff.Diff

`SemanticDiff.Diff` parses Git unified diffs and builds text models used by graph nodes and file tabs.

## Responsibilities

- unified diff parsing,
- hunk and line snapshot modeling,
- changed-only and full-file diff views,
- TextMateSharp tokenization,
- language registry fallback metadata,
- folding and line annotation metadata for rich file views.

Use it when you need a diff/file rendering backend without the app shell.
