---
title: "SemanticDiff.Git"
---

# SemanticDiff.Git

`SemanticDiff.Git` owns repository-facing operations.

## Responsibilities

- Git command abstraction and execution,
- worktree, staged, unstaged, branch, and range diffs,
- branch and remote reference discovery,
- GitHub pull request and GitLab merge request discovery,
- review comments, replies, and resolvable thread state where providers support it,
- paged commit history,
- file blame and file-history data,
- provider-aware remote URL parsing.

The package intentionally keeps provider APIs isolated from UI code so hosts can test Git behavior without loading the desktop app.
