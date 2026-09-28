#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export NUGET_PACKAGES="${NUGET_PACKAGES:-/tmp/nuget-coordination-learn}"
dotnet restore "$ROOT/eng/telemetry-host-manager/TelemetryHostManager.fsproj" --locked-mode --nologo
dotnet build "$ROOT/eng/telemetry-host-manager/TelemetryHostManager.fsproj" --no-restore --nologo
PYTHONDONTWRITEBYTECODE=1 python3 "$ROOT/tests/telemetry-native-collector-installation/run.py"
