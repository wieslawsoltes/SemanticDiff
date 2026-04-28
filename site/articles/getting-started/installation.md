---
title: "Installation"
---

# Installation

## Prerequisites

- .NET 10 SDK matching `global.json`.
- Git available on `PATH`.
- Uno Platform workloads for building the desktop app and Uno controls.
- Optional `GITHUB_TOKEN` or `GH_TOKEN` for GitHub review APIs.
- Optional `GITLAB_TOKEN` or `GL_TOKEN` for GitLab merge request APIs.

## Clone and restore

```bash
git clone https://github.com/wieslawsoltes/SemanticDiff.git
cd SemanticDiff
dotnet restore SemanticDiff.slnx
```

## Build and test

```bash
dotnet build tests/SemanticDiff.Tests/SemanticDiff.Tests.csproj -c Release
dotnet test tests/SemanticDiff.Tests/SemanticDiff.Tests.csproj -c Release --no-build
```

## Build the desktop app

```bash
dotnet build src/SemanticDiff.App/SemanticDiff.App.csproj -c Release -f net10.0-desktop
```

## Publish the app locally

```bash
scripts/publish-app.sh --rid osx-arm64 --format app
scripts/publish-app.sh --rid linux-x64 --format folder
scripts/publish-app.sh --rid win-x64 --format folder
```

Generated artifacts are written under `artifacts/publish/app`.
