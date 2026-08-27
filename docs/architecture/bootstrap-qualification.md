# Bootstrap qualification

FS.GG.Coordination qualifies an inert bootstrap substrate through five independent, read-only CI jobs. The workflow runs for pull requests and pushes to `main`; every third-party action is pinned to an immutable commit and the workflow receives only `contents: read`.

## Gate contract

The machine-readable contract is `eng/bootstrap-ci-contract.json`. `eng/bootstrap-ci.fsx workflow --root .` requires exactly these jobs:

- `deterministic-build`: locked restore and warnings-as-errors Release build.
- `compiler-and-tests`: unit and architecture suites, retaining architecture TRX.
- `dependency-and-security`: evaluated dependency policy plus a complete NuGet vulnerability report from the HTTPS public feed.
- `package-install-smoke`: a CI-only Protocol package restored and executed by a fresh consumer.
- `evidence-manifest`: an exact-head manifest assembled only after the other four jobs pass.

The validator rejects missing or extra jobs, mutable action references, expanded permissions, release/deployment routes, live GitHub write authority, and imported v1 coordination completion machinery.

## Evidence binding

The final job downloads each prerequisite artifact and copies the reviewed contract bytes into its evidence tree. It then emits `fsgg.coordination.bootstrap-evidence/1`, binding:

- the exact 40-hex candidate revision;
- the exact five gate identities;
- each gate's reviewed command fragments and artifact path;
- SHA-256 of the contract and every gate artifact.

Validation recomputes all digests from downloaded bytes and fails closed on a stale candidate, absent/duplicate/unknown gate, altered command contract, unsafe path, missing artifact, or digest mismatch.

## Authority ceiling

Bootstrap qualification builds, tests, inspects dependencies, creates a temporary `0.0.0-bootstrap` package, and uploads CI artifacts. It does not publish packages, create releases, deploy software, contact production mutation routes, or decide review/delivery/done state. Those concerns remain outside this bootstrap boundary.
