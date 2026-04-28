#!/bin/bash
set -euo pipefail

# Lunet API extraction invokes several nested dotnet builds. Keep the process
# self-contained so local macOS runs do not exhaust file handles through reused
# MSBuild or compiler server processes.
ulimit -n 1048576 2>/dev/null || ulimit -n 8192 2>/dev/null || true

export DOTNET_NOLOGO=true
export DOTNET_CLI_TELEMETRY_OPTOUT=true
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true
export MSBUILDNOINPROCNODE=1
export MSBUILDDISABLENODEREUSE=1
export MSBuildDisableNodeReuse=1
export BuildInParallel=false
export RestoreDisableParallel=true
export UseSharedCompilation=false

dotnet build-server shutdown >/dev/null 2>&1 || true

dotnet tool restore
cd site
dotnet tool run lunet --stacktrace build
