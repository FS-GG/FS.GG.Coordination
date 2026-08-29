#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
pass=0
fail=0
ok() { pass=$((pass + 1)); printf 'PASS  %s\n' "$1"; }
bad() { fail=$((fail + 1)); printf 'FAIL  %s\n' "$1" >&2; }
has() { grep -Eq "$2" "$ROOT/$1" && ok "$3" || bad "$3"; }

# Expand-and-contract: the new authority is live while predecessor inputs still have an explicit,
# fail-closed interpretation instead of disappearing between slices.
has src/FS.GG.Coord.GitHub/Done.fs 'LegacyReceipt' 'legacy completion evidence remains diagnosable during migration'
has scripts/fsgg-coord-guards.sh 'self-host.*verify.*replay' 'candidate read-only verify/replay remains available without write authority'
has scripts/release-saga.py 'commands.add_parser\("promote"\)' 'the predecessor promotion command remains available'
has src/FS.GG.Coord.Cli.Kernel/Options.fs 'commandCatalogue' 'catalogue metadata remains centralized after coexistence'

# Every compatibility slice has both a green parity artifact and a source inversion that can red.
for artifact in \
  command-catalogue-kernel.test-report.xml \
  lifecycle-model-core.test-report.xml \
  completion-receipts-core.test-report.xml \
  self-host-core.test-report.xml \
  change-completeness-kernel.test-report.xml \
  release-recovery.junit.xml; do
  report="$ROOT/work/coordination-change-risk-mitigation/artifacts/$artifact"
  if [ -s "$report" ] && python3 - "$report" <<'PY'
import pathlib, sys, xml.etree.ElementTree as ET
root = ET.parse(pathlib.Path(sys.argv[1])).getroot()
if root.tag.endswith("TestRun"):
    counters = next(node for node in root.iter() if node.tag.endswith("Counters"))
    assert int(counters.attrib.get("failed", "0")) == 0
    assert int(counters.attrib.get("executed", "0")) > 0
else:
    suites = [root] if root.tag.endswith("testsuite") else list(root.iter("testsuite"))
    assert suites and sum(int(row.attrib.get("tests", "0")) for row in suites) > 0
    assert sum(int(row.attrib.get("failures", "0")) for row in suites) == 0
PY
  then
    ok "observed green compatibility evidence: $artifact"
  else
    bad "missing, empty, or red compatibility evidence: $artifact"
  fi
done

has tests/FS.GG.Coord.Cli.Kernel.Tests/CommandSurfaceTests.fs 'mutant' 'catalogue slice retains an effective omission inversion'
has tests/FS.GG.Coord.Core.Tests/DeliveryTests.fs 'mutation' 'lifecycle slice retains decision-boundary mutations'
has tests/FS.GG.Coord.Core.Tests/SelfHostTests.fs 'tamper|mutable' 'self-host slice retains digest inversions'
has tests/change-completeness/run.sh 'missing named stage' 'CI slice reds when a structural family disappears'
has tests/release-saga/run.sh 'tampered|expected receiver receipt identity tampering' 'release slice retains receiver-receipt inversion'

printf '\nchange-risk compatibility: %d passed, %d failed\n' "$pass" "$fail"
[ "$fail" -eq 0 ]
