---
title: "SemanticDiff.Workbench"
---

# SemanticDiff.Workbench

`SemanticDiff.Workbench` is the host-independent orchestration layer extracted from the app.

## Responsibilities

- workspace tab state,
- branch/PR/MR graph workspace cache,
- file tab and rich text view models,
- symbol browser filtering,
- review panel state,
- history and blame view models,
- semantic graph and semantic-map workflow models,
- reusable command-state helpers.

Use it when you want SemanticDiff behavior in a different UI shell.
