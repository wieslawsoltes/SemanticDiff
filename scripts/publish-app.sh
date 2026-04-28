#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/.." && pwd)
app_project="$repo_root/src/SemanticDiff.App/SemanticDiff.App.csproj"
configuration="Release"
target_framework="net10.0-desktop"
rid=""
package_format="folder"
output_root="$repo_root/artifacts/publish/app"
archive="true"
archive_name=""
version=""
self_contained="true"
verbosity="minimal"

usage() {
  cat <<USAGE
Usage: scripts/publish-app.sh [options]

Publishes the SemanticDiff Uno desktop app for local use or CI artifacts.
The default output is artifacts/publish/app. macOS app bundle publishing is
unsigned by default; signing can be added later by passing MSBuild properties.

Options:
  --rid <rid>                 Runtime identifier, e.g. osx-arm64, linux-x64, win-x64.
  --format <folder|app>       Publish as a runtime folder or macOS .app bundle. Default: folder.
  --configuration <name>      Build configuration. Default: Release.
  --framework <tfm>           Target framework. Default: net10.0-desktop.
  --output <path>             Publish output directory. Default: artifacts/publish/app.
  --archive-name <name>       Archive file name without extension.
  --version <version>         MSBuild Version value.
  --self-contained <bool>     Self-contained publish. Default: true.
  --no-archive                Leave publish output unarchived.
  --verbosity <level>         dotnet publish verbosity. Default: minimal.
  -h, --help                  Show this help.

Examples:
  scripts/publish-app.sh --rid osx-arm64 --format app
  scripts/publish-app.sh --rid linux-x64 --format folder --archive-name SemanticDiff.App-linux-x64
USAGE
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --rid)
      rid="${2:?Missing value for --rid}"
      shift 2
      ;;
    --format)
      package_format="${2:?Missing value for --format}"
      shift 2
      ;;
    --configuration)
      configuration="${2:?Missing value for --configuration}"
      shift 2
      ;;
    --framework)
      target_framework="${2:?Missing value for --framework}"
      shift 2
      ;;
    --output)
      output_root="${2:?Missing value for --output}"
      shift 2
      ;;
    --archive-name)
      archive_name="${2:?Missing value for --archive-name}"
      shift 2
      ;;
    --version)
      version="${2:?Missing value for --version}"
      shift 2
      ;;
    --self-contained)
      self_contained="${2:?Missing value for --self-contained}"
      shift 2
      ;;
    --no-archive)
      archive="false"
      shift
      ;;
    --verbosity)
      verbosity="${2:?Missing value for --verbosity}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

case "$package_format" in
  folder|app) ;;
  *)
    echo "Unsupported package format '$package_format'. Use 'folder' or 'app'." >&2
    exit 2
    ;;
esac

if [ -z "$rid" ]; then
  echo "--rid is required for repeatable CI/local publish output." >&2
  exit 2
fi

publish_base_dir="$output_root/$rid/$package_format"
publish_dir="$publish_base_dir"
if [ "$package_format" = "folder" ]; then
  publish_dir="$publish_base_dir/SemanticDiff.App"
fi
archive_dir="$output_root"
if [ -z "$archive_name" ]; then
  archive_name="SemanticDiff.App-$rid-$package_format"
fi
archive_path="$archive_dir/$archive_name.zip"
archive_source="$publish_dir"

rm -rf "$publish_base_dir" "$archive_path"
mkdir -p "$publish_dir" "$archive_dir"

set -- dotnet publish "$app_project" \
  -c "$configuration" \
  -f "$target_framework" \
  -r "$rid" \
  -o "$publish_dir" \
  -v "$verbosity" \
  -p:SelfContained="$self_contained" \
  -p:GeneratePackageOnBuild=false

if [ -n "$version" ]; then
  set -- "$@" -p:Version="$version"
fi

if [ "$package_format" = "app" ]; then
  set -- "$@" -p:PackageFormat=app
fi

"$@"

if [ "$package_format" = "app" ]; then
  app_bundle=$(find "$publish_dir" -maxdepth 1 -type d -name "*.app" | head -n 1)
  if [ -z "$app_bundle" ]; then
    echo "PackageFormat=app did not produce a .app bundle in $publish_dir." >&2
    exit 1
  fi
  archive_source="$app_bundle"
fi

if [ "$archive" = "true" ]; then
  rm -f "$archive_path"
  if command -v python3 >/dev/null 2>&1; then
    ARCHIVE_SOURCE="$archive_source" ARCHIVE_PATH="$archive_path" python3 - <<'PY'
from pathlib import Path
import os
import zipfile

source = Path(os.environ["ARCHIVE_SOURCE"]).resolve()
archive = Path(os.environ["ARCHIVE_PATH"]).resolve()
archive.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as zip_file:
    for path in sorted(source.rglob("*")):
        if path.is_file() or path.is_symlink():
            zip_file.write(path, path.relative_to(source.parent))
PY
  elif command -v zip >/dev/null 2>&1; then
    parent_dir=$(dirname -- "$archive_source")
    base_dir=$(basename -- "$archive_source")
    (cd "$parent_dir" && zip -qr "$archive_path" "$base_dir")
  elif command -v powershell >/dev/null 2>&1; then
    ARCHIVE_SOURCE="$archive_source" ARCHIVE_PATH="$archive_path" powershell -NoProfile -Command 'Compress-Archive -Path $env:ARCHIVE_SOURCE -DestinationPath $env:ARCHIVE_PATH -Force'
  elif command -v powershell.exe >/dev/null 2>&1; then
    ARCHIVE_SOURCE="$archive_source" ARCHIVE_PATH="$archive_path" powershell.exe -NoProfile -Command 'Compress-Archive -Path $env:ARCHIVE_SOURCE -DestinationPath $env:ARCHIVE_PATH -Force'
  else
    echo "None of python3, zip, or PowerShell is available; cannot create archive." >&2
    exit 1
  fi
  echo "Published app: $publish_dir"
  echo "Published archive: $archive_path"
else
  echo "Published app: $publish_dir"
fi
