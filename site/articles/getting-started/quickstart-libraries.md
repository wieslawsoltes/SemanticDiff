---
title: "Quickstart: Libraries"
---

# Quickstart: Libraries

SemanticDiff packages are split so hosts can choose the smallest useful layer.

## Add packages

```bash
dotnet add package SemanticDiff.Workbench --prerelease
dotnet add package SemanticDiff.Controls.Uno --prerelease
```

Use lower-level packages directly when you only need models, Git services, diff parsing, semantic analysis, rendering, or layout.

## Typical host composition

| Need | Package |
| --- | --- |
| Data contracts | `SemanticDiff.Core` |
| Unified diff parsing and TextMate tokens | `SemanticDiff.Diff` |
| Git commands, refs, PRs, MRs, reviews, history, blame | `SemanticDiff.Git` |
| Semantic relationships | `SemanticDiff.Semantics`, `SemanticDiff.Semantics.Roslyn`, `SemanticDiff.Semantics.Xaml` |
| Layout and scene construction | `SemanticDiff.Layout`, `SemanticDiff.Rendering` |
| Host-independent workflow state | `SemanticDiff.Workbench` |
| Uno controls | `SemanticDiff.Controls.Uno` |

## Minimal model use

```csharp
using SemanticDiff.Core;
using SemanticDiff.Workbench.Workspace;

var cache = new DiffWorkspaceCache(capacity: 4);
Console.WriteLine(cache.StatusText);
```

Use the generated [API Reference](../../api) for exact constructors, records, and services.
