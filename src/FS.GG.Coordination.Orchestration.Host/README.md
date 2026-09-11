# Main production composition

`serve` starts paused and records that pause before it exposes any route. When the
private GitHub configuration is present, an operator can submit one closed
`fsgg.orchestration.main-route-admission/1` document to `/v1/main/admit`. The
document binds the selected work item and route, original subscription and launch
bounds, executor enrollment, prompt/input, workspace manifest, and candidate
identity. The Host accepts byte-identical retries and refuses a different binding.

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
