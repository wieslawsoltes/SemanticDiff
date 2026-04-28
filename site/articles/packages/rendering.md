---
title: "SemanticDiff.Rendering"
---

# SemanticDiff.Rendering

`SemanticDiff.Rendering` owns Skia-ready scene data and export services.

## Responsibilities

- graph node and edge scene models,
- annotation rendering primitives,
- syntax and diff token visualization support,
- hover and hit-test metadata,
- SVG, PNG, and PDF export paths,
- reusable rendering helpers for graph, file, symbol, review, history, and blame surfaces.

Use it when a host wants SemanticDiff visuals without adopting the app's view models.
