---
title: "Rendering Pipeline"
---

# Rendering Pipeline

SemanticDiff renders through Skia-backed scene data.

## Pipeline

1. Git commands produce diff, history, blame, and review data.
2. Diff parsing builds document and line models.
3. Tokenization and semantic providers enrich the text model.
4. Layout services place file, symbol, review, history, or blame nodes.
5. Rendering services build Skia-ready scenes.
6. Uno controls display the scene and route input back to workbench state.
7. Export services render the same scene to SVG, PNG, or PDF.

## Full fidelity export

Graph export uses the same rendering data as the live canvas. That keeps node labels, annotations, grouping, semantic edges, and visible layout consistent between the app and exported documents.
