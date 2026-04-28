---
title: "CI and Docs"
---

# CI and Docs

## Workflows

| Workflow | Purpose |
| --- | --- |
| `build.yml` | Builds, tests, packs, and publishes CI app artifacts. |
| `integration.yml` | Validates package consumption from a local feed. |
| `release.yml` | Publishes release packages and app artifacts. |
| `docs.yml` | Builds the Lunet documentation site and deploys GitHub Pages from the default branch. |

## Local docs build

```bash
dotnet tool restore
./build-docs.sh
```

The build output is written to `site/.lunet/build/www` and is not intended to be committed.

## GitHub Pages

The docs workflow uploads `site/.lunet/build/www` as an artifact and deploys it through `peaceiris/actions-gh-pages` on pushes to the repository default branch.
