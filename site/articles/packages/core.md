---
title: "SemanticDiff.Core"
---

# SemanticDiff.Core

`SemanticDiff.Core` contains the shared contracts used by every layer.

## Use this package when

- you need file diff, annotation, review, symbol, history, or blame models without service implementations,
- you are building adapters around SemanticDiff data,
- you want stable DTOs for tests, serialization, or UI view models.

## Main responsibilities

- diff document and line models,
- annotation categories and payloads,
- Git reference and review request data,
- review thread/comment models,
- semantic graph and navigation data,
- workspace tab descriptors.
