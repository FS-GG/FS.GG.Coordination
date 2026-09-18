# Hosted-writer Choreo correspondence

Owner: FS-GG Coordination. Review the model and replay whenever a hosted-writer
message, identity binding, journal transition, recovery gate, or effect policy
changes. Regenerate traces and run the bounded/parity gate in each such PR;
protected CI remains the acceptance gate for the complete formal inventory.

The canonical source is [Protocol.md](../../src/FS.GG.Coordination.Protocol/Protocol.md).
It contains the pinned Choreo library, four-process model, qualification roots,
and executable scenarios. [The qualification decision](choreo-qualification.md)
records bounds, parity, and the retained legacy abstraction. The
[roadmap](../roadmaps/choreo-akka-fsharp-trace-correspondence.md) records delivery
evidence and scope.

## Production boundaries

Host admission, workflow and effect-driver decisions correspond to `Host`.
`HostedWriterJournal` and the PostgreSQL store correspond to `Journal`.
`ExecutionSessionActor` and its neutral coordinator correspond to `Runner`.
The production callbacks and recording GitHub authority reads correspond to
`GitHubProvider`. Providers supply facts; the production seams decide whether
those facts authorize progress.

The eight [raw ITF fixtures](../../tests/FS.GG.Coordination.Orchestration.Host.Tests/Fixtures/Choreo/manifest.json)
are genuine Quint output. Replay consumes them through production seams using
both memory and real PostgreSQL journals. It does not recreate the model's
transition function in F#. Message-level microsteps are compared at the manifest's
durable milestones, with the first divergent raw state and Quint action reported.

## Reproduce the evidence

Use Linux x64, the SDK selected by `global.json`, and the repository's normal
build prerequisites. Tool preparation additionally needs `curl`, `tar`, `sha256sum`,
`jq`, and Go's supported native build environment. The script downloads the exact
Go, Quint, evaluator, Apalache and JRE inputs and verifies their hashes. Python 3
is used for parity projection. The private integration slice needs PostgreSQL
server utilities; the accepted exercise used PostgreSQL 18.6.

From the repository root, prepare a fresh toolchain without supplying an existing
`FSGG_QUINT_TOOLCHAIN_ARCHIVE`:

```bash
choreo_tools="$(mktemp -d)"
unset FSGG_QUINT_TOOLCHAIN_ARCHIVE
FSGG_QUINT_PREPARE_ONLY=1 \
  FSGG_QUINT_TOOLCHAIN_OUTPUT="$choreo_tools/toolchain.tar.gz" \
  bash eng/qualify-canonical-quint.sh
export FSGG_QUINT_TOOLCHAIN_ARCHIVE="$choreo_tools/toolchain.tar.gz"
```

Run all named scenarios, randomized safety exploration, both complete bounded
fault graphs, all eight exact trace regenerations, retained legacy safety, and
legacy/Choreo parity:

```bash
bash eng/verify-choreo-c2-bounded.sh
```

The script verifies the archive's Quint binary and supplies its JRE. An individual
trace can also be checked using the extracted, pinned binary:

```bash
mkdir "$choreo_tools/unpacked"
tar -xzf "$FSGG_QUINT_TOOLCHAIN_ARCHIVE" -C "$choreo_tools/unpacked"
export FSGG_QUINT_BIN="$choreo_tools/unpacked/cache/objects/939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f"
bash eng/verify-choreo-c3-traces.sh --scenario happy-path
```

Run production replay and source/license guards:

```bash
dotnet test tests/FS.GG.Coordination.Orchestration.Host.Tests -c Release
dotnet test tests/FS.GG.Coordination.ArchitectureTests -c Release \
  --filter FullyQualifiedName~ChoreoSourcePinArchitectureTests
(
  cd tests/FS.GG.Coordination.Orchestration.PostgreSql.Tests
  DOTNET_EXE="$(command -v dotnet)" bash run-private-postgres.sh
)
```

The PostgreSQL script creates and stops its own private temporary cluster. It
uses `DOTNET_EXE` when an explicit SDK executable is needed.

For the full canonical inventory, including temporal properties, negative
controls, deterministic counterexamples and budgets:

```bash
FSGG_QUINT_RECEIPT="$choreo_tools/qualification.json" \
  bash eng/qualify-canonical-quint.sh
```

Protected CI runs the same validator through
`eng/bootstrap-gates/canonical-quint-shard.sh`, followed by the performance and
aggregate scripts in that directory. The shared base shard also runs the complete
Choreo scenario, trace and parity gate for both bootstrap and optimistic CI.
Both reuse identities bind these scripts, source provenance, fixture bytes and
retained counterexamples, so a change to that evidence requires qualification.
Accept the complete aggregate and its
accounting/performance receipts; a passing isolated shard is not the full exit.
An ordinary reproduction must not set `FSGG_REFRESH_FORMAL_EVIDENCE`.

## Accepted continuation exercise

On 2026-09-18, a fresh worktree of merged C5 commit
`d53786bbe68eef26c5977523bb3efe2ba8176253` ran cold preparation, bounded/trace/parity and replay commands above with a newly
prepared toolchain and no pre-existing build output. All fifteen scenarios,
both complete bounded graphs, all eight trace comparisons, legacy safety and six
parity comparisons passed, followed by 78 Host, 9 source/license and 34 private
PostgreSQL tests. The checkout remained clean. The complete formal inventory,
negative controls and performance/accounting receipts passed the
[protected C5 qualification](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/35307759477);
[the final combined-head run](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/35309867844)
accepted that unchanged formal subject before merge.

## Diagnose a failure

- A source/tool/license digest failure means the input identity differs. Inspect
  the changed bytes and accepted pin before regenerating anything.
- A Quint invariant or temporal failure requires inspecting the raw counterexample
  and named action. Repair the implementation or model defect; do not delete the
  invariant, widen bounds silently, or mark a failed run as a new baseline.
- An ITF digest mismatch means execution or normalization changed. Compare raw
  states and the source/tool identities. `--write` still requires the expected
  digest; it is not a bypass for accepting a changed trace.
- A replay divergence reports scenario, raw state and source action. Start at that
  production seam. An absent observation must not invoke completion; retry needs
  explicit authorization and the same operation identity with its next revision.
- A parity divergence shows the first mismatching observable milestone. Any
  intentional scope change needs a documented decision and corresponding
  production evidence. The comparison must not become a second transition oracle.
- A budget or counterexample-reproducibility failure blocks qualification. Retain
  the failing log and inspect state growth, scheduling assumptions, or tool
  determinism before changing a budget.

After an intentional model edit, commit the canonical source, regenerate its
published-compiler outputs, and update the trace source commit/digest together
with the fail-closed parser pin. Regenerate all eight traces. Refresh retained
formal evidence only in the reviewed change using the exact canonical validator,
and update the measured baseline from successful receipts. Historical accepted
operational receipts stay immutable.

## Upstream and security review

Choreo remains pinned to `000cf4eed315187dc6f216a148781cff7dde6521`; the
[source manifest](../../eng/choreo-source-pin.json) records all hashes and the
single allowed import rewrite. [Apache-2.0](../../eng/vendor/choreo/LICENSE)
licensing and original notices are retained. The vendored Quint modules are
source-time modeling code, contain no runtime adapter or deployment dependency,
and are checked from committed bytes without an upstream fetch during model
execution. The modeling toolchain is separately pinned and hash-verified.

Update upstream only in a dedicated PR: review the upstream diff and license,
select an immutable commit, update copied regions and hashes together, verify
that reversing the import rewrite reproduces upstream bytes, and rerun source
mutation guards, all trace/parity/replay checks, and complete canonical
qualification. Review upstream changes when a model/toolchain incompatibility or
security/license issue arises; do not follow a floating branch automatically.

## Adoption boundary

O3 installed adoption remains a separate explicit operation. This work does not
activate a permit, install credentials, dispatch an external effect, publish a
package, or add Choreo to a deployed binary.

Other actor or wire protocols were considered for follow-on use. No expansion is
approved by this roadmap: a new proposal must identify its own protocol boundary,
invariants, raw trace evidence, production replay seam and qualification budget.
The hosted-writer evidence is the prerequisite, not an implied fleet rollout.
