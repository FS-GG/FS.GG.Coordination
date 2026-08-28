#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
scratch_parent="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
qualification_root="$(mktemp -d "$scratch_parent/fsgg-canonical-quint-ci.XXXXXX")"
downloads="$qualification_root/downloads"
cache="$qualification_root/cache"
runtime="$qualification_root/runtime"
tool_path="$qualification_root/dotnet-tools"
quint_home="$qualification_root/home/.quint"
nuget_config="$qualification_root/NuGet.Config"

quint_sha="939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f"
lmt_sha="37e0b0365c2641edce40b48605471f61fa12e97c3e2376152f0e849abdc31f10"
evaluator_archive_sha="61755a09d5052d93a4e75e840059edfd0d3674aeda164b9d2464be3d6e21b1c2"
evaluator_sha="b2efdeac5713d153e41bf2143b94ed75d888fdd5637f4a5d61a04c695313510a"
apalache_archive_sha="a61c07569d7195ddc589f01037fa10fafef4fb0796af2f1c9cb45226375dfbfc"
apalache_jar_sha="4753c0ebb2cbb266e2c6ac19ab5ca3827d726cc80fd1fc5d7c1eeb64736cd60b"
jre_archive_sha="aeab55d064a1a27a3744b0880b9b414077b4ed2b1790817eea3df60aec946431"
java_sha="e865867065e48928c58293f30e7ae26a79c842f8607fa51d7e2e9fb90b602786"
go_archive_sha="cb2396bae64183cdccf81a9a6df0aea3bce9511fc21469fb89a0c00470088073"

mkdir -p "$downloads" "$cache/objects" "$runtime" "$tool_path" "$quint_home/rust-evaluator-v0.6.0"
printf '%s\n' \
  '<?xml version="1.0" encoding="utf-8"?>' \
  '<configuration>' \
  '  <packageSources>' \
  '    <clear />' \
  '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />' \
  '  </packageSources>' \
  '</configuration>' > "$nuget_config"

download() {
  local url="$1"
  local target="$2"
  local expected="$3"
  curl --fail --location --retry 5 --retry-all-errors --silent --show-error "$url" --output "$target"
  printf '%s  %s\n' "$expected" "$target" | sha256sum --check --status
}

download \
  "https://github.com/quint-co/quint/releases/download/v0.32.0/quint-linux-amd64" \
  "$cache/objects/$quint_sha" \
  "$quint_sha"
download \
  "https://github.com/quint-co/quint/releases/download/evaluator/v0.6.0/quint_evaluator-x86_64-unknown-linux-gnu.tar.gz" \
  "$downloads/quint-evaluator.tar.gz" \
  "$evaluator_archive_sha"
download \
  "https://github.com/apalache-mc/apalache/releases/download/v0.56.1/apalache-0.56.1.tgz" \
  "$downloads/apalache.tgz" \
  "$apalache_archive_sha"
download \
  "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.9%2B10/OpenJDK21U-jre_x64_linux_hotspot_21.0.9_10.tar.gz" \
  "$downloads/temurin-jre.tar.gz" \
  "$jre_archive_sha"
download \
  "https://go.dev/dl/go1.24.1.linux-amd64.tar.gz" \
  "$downloads/go.tar.gz" \
  "$go_archive_sha"

chmod +x "$cache/objects/$quint_sha"
tar -xzf "$downloads/quint-evaluator.tar.gz" -C "$quint_home/rust-evaluator-v0.6.0"
printf '%s  %s\n' "$evaluator_sha" "$quint_home/rust-evaluator-v0.6.0/quint_evaluator" | sha256sum --check --status
chmod +x "$quint_home/rust-evaluator-v0.6.0/quint_evaluator"

tar -xzf "$downloads/apalache.tgz" -C "$runtime"
mkdir -p "$quint_home/apalache-dist-0.56.1"
ln -s "$runtime/apalache-0.56.1" "$quint_home/apalache-dist-0.56.1/apalache"
printf '%s  %s\n' "$apalache_jar_sha" "$runtime/apalache-0.56.1/lib/apalache.jar" | sha256sum --check --status

tar -xzf "$downloads/temurin-jre.tar.gz" -C "$runtime"
java_home="$runtime/jdk-21.0.9+10-jre"
printf '%s  %s\n' "$java_sha" "$java_home/bin/java" | sha256sum --check --status

tar -xzf "$downloads/go.tar.gz" -C "$runtime"
go="$runtime/go/bin/go"
test -x "$go"

dotnet tool install FS.GG.SDD.Cli \
  --version 1.5.0 \
  --tool-path "$tool_path" \
  --configfile "$nuget_config"

: "${NUGET_PACKAGES:=$HOME/.nuget/packages}"
lmt_source="$NUGET_PACKAGES/fs.gg.sdd.artifacts/1.5.0/quint/lmt/main.go"
test -f "$lmt_source"
GO111MODULE=off "$go" build \
  -trimpath \
  -ldflags '-buildid=IvXAt1kJ-3iINki1alCT/Ut12KGabgkWIkwVpw-xO/c4zkZMLAubfWHvjZOY8o/8-oR_8tNNndNgfMVoD8F -B 0x03d1703027f57ed4dd2ba90b7cdfc8cdea2815da' \
  -o "$cache/objects/$lmt_sha" \
  "$lmt_source"
printf '%s  %s\n' "$lmt_sha" "$cache/objects/$lmt_sha" | sha256sum --check --status
chmod +x "$cache/objects/$lmt_sha"

export FSGG_QUINT_CACHE="$cache"
export FSGG_QUINT_HOME="$quint_home"
export FSGG_SDD_CLI="$tool_path/fsgg-sdd"
export JAVA_HOME="$java_home"
export HOME="$qualification_root/home"

cd "$repo_root"
dotnet fsi eng/validate-canonical-quint-protocol.fsx -- --root . --compiler-only
dotnet fsi eng/validate-canonical-quint-protocol.fsx -- --root .

printf 'CANONICAL_QUINT_HOSTED_QUALIFICATION_OK root=%s\n' "$repo_root"
