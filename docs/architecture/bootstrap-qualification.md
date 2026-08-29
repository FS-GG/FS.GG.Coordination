# Bootstrap qualification

FS.GG.Coordination qualifies an inert bootstrap substrate through six independently scheduled, read-only prerequisite jobs and one evidence join. The workflow runs for pull requests and pushes to `main`; every third-party action is pinned to an immutable commit and the workflow receives only `contents: read`.

## Gate contract

The machine-readable contract is `eng/bootstrap-ci-contract.json`. `eng/bootstrap-ci.fsx workflow --root .` requires exactly these jobs:

- `deterministic-build`: locked restore and warnings-as-errors Release build.
- `compiler-and-tests`: unit and architecture suites, retaining architecture TRX.
- `canonical-quint`: one shared deterministic preparation followed by separately attributable Q1 and Q2 qualification, retaining a versioned JSON receipt.
- `dependency-and-security`: evaluated dependency policy plus a complete NuGet vulnerability report from the HTTPS public feed.
- `package-install-smoke`: a CI-only Protocol package restored and executed by a fresh consumer.
- `bootstrap-recovery`: reconstruction and clean-consumer execution from committed bytes.
- `evidence-manifest`: an exact-head manifest assembled only after all six prerequisite jobs pass.

The validator rejects missing or extra jobs, mutable action references, expanded permissions, release/deployment routes, live GitHub write authority, and imported v1 coordination completion machinery. The readable semantic rules are backed by an exact workflow-byte digest in the reviewed contract, so unreviewed action steps, inputs, environments, commands, or YAML structure cannot sit outside the declared surface.

## Evidence binding

The final job downloads each prerequisite artifact and copies the reviewed contract bytes into its evidence tree. It then emits `fsgg.coordination.bootstrap-evidence/1`, binding:

- the exact 40-hex candidate revision;
- the exact seven gate identities;
- each gate's reviewed command fragments and artifact path;
- SHA-256 of the contract and every gate artifact.

Validation recomputes all digests from downloaded bytes and fails closed on a stale candidate, absent/duplicate/unknown gate, altered command contract, unsafe path, missing artifact, or digest mismatch.

## Canonical Quint execution

Formal qualification starts at the same time as the build and architecture paths; it does not wait behind them. `eng/qualify-canonical-quint.sh` acquires the exact digest-pinned toolchain and invokes the formal validator once. That invocation performs extraction, generation, projection checks, and typechecking once, emits the Q1 marker, and reuses the same prepared modules for Q2.

The eight positive invariants are checked by one pinned Quint multi-invariant invocation. The runner derives its completed inventory from execution and requires exactly 56 rejected Quint mutation processes before Q2 can pass; this corrects the earlier unmeasured assumption of 51. Removing or unexpectedly greening one control changes the derived count and fails qualification. The 41 independent Rust mutation checks use an explicit concurrency cap of two; Apalache checks remain sequential so their counterexample artifacts and process isolation are unchanged. A persistent Apalache server was rejected after it stalled on a counterexample-producing verification despite improving a narrow positive-invariant benchmark.

The retained `fsgg.coordination.canonical-quint-qualification/1` receipt records separate Q1/Q2 outcomes, derived positive and negative inventory cardinalities, preparation/Q2/total durations, direct external and Quint CLI counts, Apalache verify-invocation count, exact tool and input digests, an optional preparation digest, a hashed failure diagnostic, and a result digest over those outcome facts. Q1 failure is recorded as `failed/not-run`; a later failure is `passed/failed`. The upload step uses the sole permitted `always()` condition so a failed job retains its receipt, while the evidence join remains skipped and cannot accept it. Passing evidence is accepted only when both phases pass, all 8 invariants and 56 rejections are observed, the pins match, and the failure field is null. SDD lifecycle commands are intentionally outside the reusable formal runner and remain obligations of the active roadmap work item.

## Authority ceiling

Bootstrap qualification builds, tests, inspects dependencies, creates a temporary `0.0.0-bootstrap` package, and uploads CI artifacts. It does not publish packages, create releases, deploy software, contact production mutation routes, or decide review/delivery/done state. Those concerns remain outside this bootstrap boundary.
