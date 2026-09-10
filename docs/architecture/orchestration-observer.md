# Orchestration observer and shadow boundary

O1 adds an inert observer assembly. Its composition requires four explicit capabilities: complete project read,
bounded planning, authoritative command/effect readback, and the typed observer journal. It has no runner
dispatcher, provider mutation adapter, hosted listener, credential source, or reference to the inert App project.
The absence of those constructors is the shadow boundary; there is no mutable `enabled` or read-only mode flag.

The GitHub bridge accepts `ProjectAdapter.readProject` only after terminal pagination succeeds. Every repository
issue must also have provider-read immutable repository node/database identity and issue node/number identity.
Repository names and board item IDs remain observation facts. Canonical WorkItem identity remains the O0
repository database ID plus issue number, so board removal, repository rename, or legacy/new GraphQL node-ID
formats cannot create a second owner. Incomplete or unknown observations produce no event and cannot erase the
last durable observation.

Planning sessions select token, runtime, cost, and deadline limits independently from execution budgets. Starting
a planning attempt first appends its full reservation to PostgreSQL. Only an acknowledged new append invokes the
planning capability; a duplicate command never launches the agent again. Completion releases the reservation and
charges observed use. Agent loss retains the reservation and blocks another attempt until an explicit
reconciliation consumes the reservation conservatively. Restart and reconnect do not renew authority.

Proposal digests cover the observed WorkItems, observation digest, workflow revision, generation, scope, action
kind, and parameter digest. Action kinds are closed and every action must name a WorkItem present in the complete
observation. A newer observation clears the current proposal pointer while retaining history. Approval covers the
exact proposal digest, selected planning budget, principal, scope, expected workflow revision and generation,
stable command ID, and canonical command body digest. The approval budget must still be live.

Durable command acceptance and completed-effect rows enter through `ICommandReadbackCapability`. Each carries
provider revision, evidence digest, and observation time; effects also bind their operation to the accepted command.
The planning capability can only propose and cannot create these readback facts. The observer journal uses a closed
versioned event codec and separate PostgreSQL stream/event/inbox tables. Telemetry SQLite rows and opaque payloads
never substitute for orchestration state.

`fsgg-coord observer-view --events <path> --format text|json` is a local projection entry point over base64-encoded
closed observer events. Because that file has no journal envelope, sequence proof, or PostgreSQL integrity gate,
both formats label it `unverified-event-export`; it is useful for offline inspection and cannot establish durable
acceptance or effect completion. Authoritative projections consume `RecoverObserver` state after the PostgreSQL
adapter validates its typed event rows. Rows keep conversation, proposal, approval, durable command acceptance,
and known effect completion distinct. JSON is a dashboard-consumable read model, not a deployed dashboard or
hosted service.

This milestone does not enable Main, a GitHub writer, runner dispatch, listener startup, production credentials,
or O2 pilot authority. Physical PostgreSQL failover, power loss, full disk, and hostile broad-token planner
containment remain outside O1 qualification.
