#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
smoke_root="${1:?usage: package-install-smoke.sh SCRATCH_DIR}"
feed_dir="$smoke_root/feed"
consumer_dir="$smoke_root/consumer"
export NUGET_PACKAGES="$smoke_root/packages"

mkdir -p "$feed_dir" "$consumer_dir"

package_path="$feed_dir/FS.GG.Coordination.Protocol.0.0.0-bootstrap.nupkg"
if [[ -n "${FSGG_BOOTSTRAP_PACKAGE_OVERRIDE:-}" ]]; then
  cp "$FSGG_BOOTSTRAP_PACKAGE_OVERRIDE" "$package_path"
else
  dotnet pack "$repo_root/src/FS.GG.Coordination.Protocol/FS.GG.Coordination.Protocol.fsproj" \
    --configuration Release \
    --no-restore \
    --output "$feed_dir" \
    -p:IsPackable=true \
    -p:PackageVersion=0.0.0-bootstrap
fi

cp "$repo_root/tests/fixtures/bootstrap-package-consumer/Bootstrap.Consumer.fsproj" "$consumer_dir/Bootstrap.Consumer.fsproj"
cp "$repo_root/tests/fixtures/bootstrap-package-consumer/Program.fs" "$consumer_dir/Program.fs"
printf '%s\n' \
  '<?xml version="1.0" encoding="utf-8"?>' \
  '<configuration>' \
  '  <packageSources>' \
  '    <clear />' \
  '    <add key="bootstrap" value="../feed" />' \
  '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />' \
  '  </packageSources>' \
  '  <packageSourceMapping>' \
  '    <packageSource key="bootstrap"><package pattern="FS.GG.Coordination.*" /></packageSource>' \
  '    <packageSource key="nuget.org"><package pattern="*" /></packageSource>' \
  '  </packageSourceMapping>' \
  '</configuration>' > "$consumer_dir/NuGet.Config"

dotnet restore "$consumer_dir/Bootstrap.Consumer.fsproj" \
  --use-lock-file \
  --force-evaluate \
  --configfile "$consumer_dir/NuGet.Config" \
  --source "$feed_dir" \
  --source https://api.nuget.org/v3/index.json
dotnet restore "$consumer_dir/Bootstrap.Consumer.fsproj" \
  --locked-mode \
  --configfile "$consumer_dir/NuGet.Config" \
  --source "$feed_dir" \
  --source https://api.nuget.org/v3/index.json
dotnet build "$consumer_dir/Bootstrap.Consumer.fsproj" --configuration Release --no-restore --warnaserror
dotnet run --project "$consumer_dir/Bootstrap.Consumer.fsproj" --configuration Release --no-build --no-restore \
  | grep -Fx 'FS.GG.Coordination.Protocol:1'

test -f "$package_path"
printf 'PACKAGE_INSTALL_SMOKE_OK package=%s\n' "$package_path"
