#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
rid=""
format="folder"

case "$(uname -s 2>/dev/null || echo unknown)" in
  Darwin)
    format="app"
    case "$(uname -m)" in
      arm64|aarch64) rid="osx-arm64" ;;
      *) rid="osx-x64" ;;
    esac
    ;;
  Linux)
    case "$(uname -m)" in
      arm64|aarch64) rid="linux-arm64" ;;
      *) rid="linux-x64" ;;
    esac
    ;;
  MINGW*|MSYS*|CYGWIN*|Windows_NT)
    rid="win-x64"
    ;;
  *)
    echo "Unable to infer runtime identifier; use scripts/publish-app.sh --rid <rid>." >&2
    exit 2
    ;;
esac

exec "$script_dir/publish-app.sh" --rid "$rid" --format "$format" "$@"
