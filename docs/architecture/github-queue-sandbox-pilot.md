# GitHub queue sandbox pilot

GS2-07.6 qualifies merge-queue admission and interrupted recovery against only
`FS-GG/FS.GG.GitHub.Substrate.Sandbox` (repository id `1353050537`). The product
surface is deliberately pure: callers supply provider observations and receive a
sealed plan or typed refusal. Network access and credentials remain outside the
qualification contract.

The admission seal binds the candidate head separately from the merge-group
head, the full base ref, prior and forward base SHAs, a monotonically newer base
observation, the complete grown required-check inventory and results, and the
current claim, review, dependency, release, and settings authorities. Admission
must occur before the bound expiry. Unsupported or unknown provider capability,
changed authority, a moved candidate, stale base evaluation, incomplete checks,
or an expired window fails closed.

Recovery is a real process boundary: `prepare` exits after writing and sealing
the checkpoint, journal and observed authority, while a separately identified
`resume` process verifies those exact bytes before acting. It refuses the expired
admission, re-observes claim/review/dependency/settings authority, and only then
creates a fresh admission. Every applied operation
has one deterministic retry with the same result digest and next attempt number.
Compensations name applied operations in reverse order, have unique identities,
and end in digest-bound states. A valid receipt requires no duplicate effects,
no temporary resources, private visibility, the original settings digest, and
an authoritative recoverable readback.

The hosted exercise is bounded by `hosted-prestate.json`. It captures the exact
private settings, branches and workflow inventory, proves zero repository
secrets and zero environments, and records the provider 403 that permits a
temporary public transition. Live execution must install rollback before the
first write, use uniquely named temporary branches, workflow and ruleset, and
delete them before restoring private visibility. The final readback must match
the settings, branch, and active-workflow digests. GitHub retains the removed
workflow's historical registry row alongside immutable run history; cleanup
therefore disables that row and proves that its file is absent from every
remaining branch.

The accepted Section 12.2 amendment is covered by a separate bounded fixture.
It issued four rapid body edits on sandbox PR 19 while two workflow effects were
in flight, retained five provider edit records, and treated each newer hint as
superseding only the prior hint for that same PR. Distinct sandbox PR 20 stayed
independent and merged successfully. The fixture then moved PR 19's source,
observed the unrelated merge advance its base, changed required contexts from
`queue-pilot` to `queue-growth` plus `queue-pilot`, and accepted only green checks
at the current head. The earlier green head was retained as an explicit
`refused-stale-green` decision. No in-flight run was cancelled. Its measured
attributable work was 22.852 seconds and waiting was 77.790 seconds; these are a
single observed fixture, not a performance target or cohort claim.

Q4 and Q6 plus the Q4 routine-burst command are independently executable offline gates. Each reads tracked
provider/authority inputs, runs separately authored generated and independent
negative controls, and validates canonical serialization, sealing, replay,
authority denial, and cleanup. The live transcript supplies the hosted
observation; the pure gates make that observation reproducible without granting
production, fleet, release, package, or successor authority.
