---
title: "SemanticDiff.Semantics.Roslyn"
---

# SemanticDiff.Semantics.Roslyn

`SemanticDiff.Semantics.Roslyn` provides C# semantic extraction.

## Responsibilities

- Roslyn workspace loading,
- C# declaration and member extraction,
- symbol identity and location mapping,
- references and relationship extraction,
- fast syntax fallback when full MSBuild analysis is unavailable,
- integration with changed-file and generated-file contexts.
