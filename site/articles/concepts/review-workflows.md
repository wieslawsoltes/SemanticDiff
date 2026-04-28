---
title: "Review Workflows"
---

# Review Workflows

SemanticDiff treats review comments as navigable graph data.

## Providers

| Provider | Supported data |
| --- | --- |
| GitHub | Pull requests, issue comments, review comments, GraphQL review threads when authenticated. |
| GitLab | Merge requests, discussions, notes, resolvable thread state. |

## Comment routing

Review items preserve provider identity, file path, line location, thread id, and comment counts. The Review panel and graph annotations use the same routing data so selecting a thread can focus the matching file node and line.

## Actions

Supported actions depend on provider and authentication:

- add overview comments,
- reply to existing threads,
- resolve or reopen supported threads,
- reload review discussions,
- navigate from thread to code,
- navigate from annotation to thread.
