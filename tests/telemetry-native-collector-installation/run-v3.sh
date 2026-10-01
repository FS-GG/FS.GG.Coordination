#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
MODULE=${1:?absolute canonical learn_01_native_source.py path required}
FIXTURE_ROOT=${2:?absolute disposable fixture output path required}
dotnet restore "$ROOT/eng/telemetry-host-manager/TelemetryHostManager.fsproj" --locked-mode --nologo
dotnet build "$ROOT/eng/telemetry-host-manager/TelemetryHostManager.fsproj" --no-restore --nologo -m:2
dotnet restore "$ROOT/tests/telemetry-native-collector-installation/NativeCollectorInstallationV3.fsproj" --nologo
dotnet run --project "$ROOT/tests/telemetry-native-collector-installation/NativeCollectorInstallationV3.fsproj" --no-restore -- \
  "$ROOT/eng/telemetry-host-manager/bin/Debug/net10.0/TelemetryHostManager.dll" \
  "$MODULE" "$FIXTURE_ROOT"
