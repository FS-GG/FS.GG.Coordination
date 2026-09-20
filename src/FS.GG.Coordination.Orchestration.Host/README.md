# Main production composition

`serve` starts paused and records that pause before it exposes any route. When the
private GitHub configuration is present, an operator can submit one closed
`fsgg.orchestration.main-route-admission/1` document to `/v1/main/admit`. The
document binds the selected work item and route, original subscription and launch
bounds, executor enrollment, prompt/input, workspace manifest, and candidate
identity. The Host accepts byte-identical retries and refuses a different binding.
Authenticated `/v1/status`, `/v1/pause`, `/v1/resume`, `/v1/revoke`, and
`/v1/cancel` operate on that same Core journal. Pause and revoke fence future
effects; cancel separately persists its request and reconciles the execution actor,
and never reports process termination merely because the request was accepted.

After a process restart, submit the exact original admission bytes to the
authenticated `/v1/main/recover` route. The Host validates those bytes against the
durable Core, execution, input, and workspace records, reads the current repository,
issue, and external claim from GitHub, records a new route readback, and returns an
`fsgg.orchestration.main-route-recovery-receipt/1` document while remaining paused.
Read `/v1/status`; only then may the operator submit a distinct
`fsgg.orchestration.host-control/1` request to `/v1/resume` using that status
sequence and generation. Recovery never accepts reconstructed admission content,
renews a claim, creates another attempt, or implicitly resumes delivery.
If the delivery deadline or external claim is no longer current, the same route
binds an observation-only graph: `/v1/status` keeps readback and dispatch disabled,
while the Host may reconcile an already exposed provider operation under its
durable operation identity. It cannot launch a model or issue a new GitHub effect.
The PostgreSQL composition qualification uses the packaged executor with controlled
GitHub responses; only an external pilot can establish native GitHub delivery.

For the narrow case where an expired, paused route has an observation-required
`StoreCandidate` and the runner's candidate inspection failed, an authenticated
operator can submit a fresh `fsgg.orchestration.host-control/1` request to
`/v1/settle-absent-candidate`. Set `reason` to `candidate-touch-set-refused` and
use the current `/v1/status` sequence and generation. The original admission
must first be rebound with `/v1/main/recover`. The Host refuses settlement unless
the delivery deadline has passed, the Core route has no downstream effect intents,
the exact candidate row is absent, the execution journal's bound observation has
no candidate and is outcome-unknown, GitHub confirms the repository and issue
identity, and the route branch, pull request and active claim are absent. It then
records candidate absence, terminalizes the absent deliverable, releases the
execution reservation, revokes the old generation, and clears the expired claim
obligation through Core events. A refusal or partial result requires a fresh
status/readback before retry; never edit the journal or delete the historical
claim comment. This route does not admit or launch replacement work.

Main owns the PostgreSQL journals, effect intents, candidate object, GitHub delivery
identity, and native delivery readback. A launcher authenticates to the Host-owned
`/v1/executor/poll` and `/v1/executor/complete` routes and relays bounded frames to
the runner's `executor-stdio` mode. The runner receives no PostgreSQL credential and
has no GitHub-delivery role. Environment scrubbing is hygiene under the accepted
cooperative same-user trust model; it is not credential containment.

The source and tests qualify authenticated admission, a packaged deterministic
runner, real PostgreSQL recovery, immutable Git bundle transfer, and all seven
effects through merged native readback. They do not publish or install artifacts,
configure the launcher, copy a subscription session, activate a Host, or invoke a
live model. Those remain separate deployment and observed-pilot operations.

`verify-installed-adoption` is a separate offline command for a dedicated,
initialized, empty schema-2 qualification store. It consumes owner-private
connection and request files, verifies the embedded source revision and running
executable digest, and exercises two deterministic project/subject fixtures against
the production execution journal and subscription store at ordinary capacity 1.
It never starts `serve`, a provider, runner, model, HTTP listener, GitHub client, or
child process, and never migrates or resets the store. Its single result document is
defined by `fsgg.orchestration.installed-adoption-result/1`; a failed run retains
its durable fixture history and requires operator adjudication.

The current GitHub qualification implementation is deliberately bounded to the
selected `.github` `internal-docs` pilot route. It reads the exact base-owned
routine policy and native pull-request/check state, selects the latest check run
per required context, and refuses unsupported qualification profiles. It is not a
general implementation of every source-change/coherent-validation profile. A
pending or unknown external observation backs off for 15 seconds while local
progress keeps the 250 ms cadence. The HTTP transport serializes requests, treats
header names case-insensitively, and honors `Retry-After` and exhausted rate-reset
facts; it never rotates credentials or mutates work to recover quota.
