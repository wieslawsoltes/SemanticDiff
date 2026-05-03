# Patch Series Comparison Plan

## Goal

Support workflows where any two Git patch series need to be compared for logical patch continuity. The feature must be universal, not tied to Skia: users can compare fork-vs-upstream patch stacks, tag-to-tag migrations, release branches, local branches, remote branches, or commit SHA spans.

Example Skia/Chrome workflow:

- Old patch series: `chrome/m119..119`
- New patch series: `chrome/m147..147`
- Comparison: `git range-diff chrome/m119..119 chrome/m147..147`

This answers whether patches carried on the old fork version are still present in the new fork version even when commit hashes changed.

## Supported Inputs

- Any Git range expression accepted by `git log`, `git diff`, and `git range-diff`.
- Local branch ranges, for example `main..feature/work`.
- Remote branch ranges, for example `upstream/main..origin/topic`.
- Tag/release ranges, for example `v1.0.0..v1.1.0`.
- Commit SHA ranges, for example `abc1234..def5678`.
- Provider-specific branch naming, for example `chrome/m119..119` and `chrome/m147..147`.
- Wizard-discovered refs from the current repository, another local repository path, or a remote Git URL.

## Design

### Git Layer

- Add core models for patch-series comparison requests, snapshots, series metadata, and comparison rows.
- Add `IGitPatchSeriesComparisonService` so the feature is reusable outside the app.
- Add `IGitPatchSeriesDiscoveryService` so repository/ref discovery is reusable outside the app.
- Implement `GitPatchSeriesComparisonService` using Git-native primitives:
  - `git log --reverse --format=... <range>` for old and new patch-series commit summaries.
  - `git diff --name-status -z <range>` for touched file summaries.
  - `git range-diff --no-color <old-range> <new-range>` for patch identity and evolution.
- Parse range-diff header rows into stable statuses:
  - `=` unchanged patch present in both series.
  - `!` same logical patch, changed content or message.
  - `<` old patch no longer present.
  - `>` new patch introduced in the new series.
- Preserve raw range-diff details for auditability and troubleshooting.
- Implement `GitPatchSeriesDiscoveryService` using:
  - `git rev-parse --show-toplevel` for local repository normalization.
  - Bare blobless remote caches under the SemanticDiff patch-compare temp directory.
  - `git for-each-ref` over branches, remotes, tags, and fetched review refs for visual source selection.

### App Layer

- Add a new workspace tab kind: `PatchCompare`.
- Add a top workspace action button: `Patch compare`.
- Use two direct editable range expressions, not Skia-specific base/head fields:
  - Old patch series.
  - New patch series.
- Add a visual wizard above the manual ranges:
  - Source field accepts blank/current repository, local path, or remote Git URL.
  - Inspect loads refs and normalizes the repository path used by comparison.
  - Four selectors choose old base, old head, new base, and new head.
  - Apply refs fills the manual range fields while keeping them editable.
- Open a new patch-compare tab each time so multiple comparisons can remain visible.
- Provide optional convenience actions:
  - `Skia example` loads `chrome/m119..119` and `chrome/m147..147`.
  - `Clear` resets the tab to blank universal inputs.
- Show summary counts and two series summaries.
- Show a result table with old/new indices, old/new short hashes, status, subject, and detail preview.
- Show raw `git range-diff` output for auditability.

### Validation

- Unit-test `GitPatchSeriesComparisonService` using the fake Git runner.
- Verify arbitrary range expressions are passed through verbatim.
- Verify failure states from `git range-diff` are surfaced instead of crashing.
- Build the solution.
- Run the test suite.
