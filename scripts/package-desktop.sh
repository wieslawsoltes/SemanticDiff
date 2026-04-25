#!/usr/bin/env sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)

dotnet publish "$repo_root/src/SemanticDiff.App/SemanticDiff.App.csproj" \
  -c Release \
  -f net10.0-desktop \
  /p:PublishProfile=DesktopFolder