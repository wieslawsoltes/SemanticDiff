---
title: "App Publishing"
---

# App Publishing

SemanticDiff has local publishing scripts and CI publishing workflows for unsigned app artifacts.

## Local publishing

```bash
scripts/publish-app.sh --rid osx-arm64 --format app
scripts/publish-app.sh --rid linux-x64 --format folder
scripts/publish-app.sh --rid win-x64 --format folder
```

## CI publishing

The build and release workflows publish app artifacts for Linux, Windows, and macOS. Artifacts are unsigned for now and are uploaded as zipped build outputs.

## Package publishing

NuGet package publishing is handled by the release workflow and uses the package metadata defined in `Directory.Build.props`.
