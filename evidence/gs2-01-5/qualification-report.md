# GS2-01.5 qualification report

Candidate: populated by the pull-request exact-head review record.

## Positive observations

- `dotnet fsi eng/bootstrap-ci.fsx workflow --root .` accepted the exact five-job, read-only, immutable-action workflow.
- Locked Release build completed with warnings as errors.
- Unit and architecture test projects passed; retained TRX files are under `test-results/`.
- The evaluated production dependency policy accepted all six production projects.
- A complete NuGet vulnerability report covered all eight solution projects through the declared HTTPS source and contained no vulnerability entries.
- `eng/package-install-smoke.sh` packed Protocol at `0.0.0-bootstrap`, restored a fresh consumer through an isolated source map, built it, and observed `FS.GG.Coordination.Protocol:1`.

## Independent negative controls

Architecture tests prove rejection of a missing workflow gate, mutable action reference, expanded write authority, partial vulnerability report, vulnerable report, malformed report, stale evidence candidate, missing evidence gate, and artifact bytes changed after collection. Existing dependency controls independently reject prohibited project edges, runtime/framework references, checkout-relative feeds, producer copies, malformed evaluated projects, and published-kernel metadata bypasses.

## Boundary conclusion

The bootstrap workflow has build/test/security/package/evidence authority only. It contains no v1 review, delivery, done, release, deployment, or live GitHub mutation route.
