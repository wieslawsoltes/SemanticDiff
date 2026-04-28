---
title: "Diff Graph"
---

# Diff Graph

The diff graph is the main workspace. It renders changed files as nodes and overlays relationships that are hard to see in a flat patch.

## Node contents

File nodes can show:

- file name and status,
- syntax-colored code snippets,
- diff line markers,
- moved/noise line markers,
- review comment annotations,
- semantic anchors,
- blame and history annotations,
- fold and context annotations.

## Edges

Edges connect files and symbols through:

- semantic references,
- generated-file relationships,
- XAML bindings/resources,
- review comment navigation,
- blame and history context,
- folder or group containment.

## Grouping

Groups can represent folders, languages, semantic categories, or statuses. Moving a group moves the related nodes so review context stays together.
