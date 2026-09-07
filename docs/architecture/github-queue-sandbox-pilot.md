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

Recovery resumes only from the same durable checkpoint. Every applied operation
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

Q4 and Q6 are independently executable offline gates. Each reads tracked
provider/authority inputs, runs separately authored generated and independent
negative controls, and validates canonical serialization, sealing, replay,
authority denial, and cleanup. The live transcript supplies the hosted
observation; the pure gates make that observation reproducible without granting
production, fleet, release, package, or successor authority.
