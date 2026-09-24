# Scoped admission CAS source slice

## Change

Add a dormant production Git receive-pack path for the already-typed public
admission CAS plan. No journal installation, dispatch, standing credential
issuer, or ordinary CLI enablement is part of this slice.

## Spec impact

The canonical `Protocol.md` remains unchanged. `MutationResultsAreBound`,
`UncertainMutationOutcomesStayUnknown`, and
`DurablePlansAreOrderedAndResumable` must continue to hold: transport response
alone never supplies a terminal admission result.

## Implementation and gates

1. Extend the ordinary-App transport with fixed-repository Git fetch/push using
   its already-scoped installation token. Supply the token to Git only through
   an inherited descriptor and a fixed askpass helper. The helper sends the
   token only through Git's private askpass pipe; the parent process places no
   token in arguments, environment, output, or logs. Gate: provider controls
   and canonical Quint verification.
2. Add an import-only CAS entry point that validates the public plan, fetches
   the exact parent, stages its exact objects, and pushes under an exact lease.
   A failed or lost response remains unknown; only the typed port's separate
   journal readback can confirm success. Gate: local competing-parent and
   unknown-response controls plus canonical Quint verification.
3. Run Q6 operational validation and update the source-only status. Keep the
   CLI fence and live protected operation unchanged.

If a canonical invariant fails, stop and revise this plan rather than altering
the spec.

## Status

Steps 1–3 are source-complete. The focused provider and CAS controls pass,
including a real Git credential-prompt exercise that caught and corrected a
host-only askpass prompt mismatch. Q6 and all 690 architecture tests pass on
the clean commit. The full unchanged canonical Quint qualification passed with
contract digest `137852914a1a7ec6e3af62be0f5c0c890390e02640775cddf97afa789dcb7d8b`;
its local receipt is `/tmp/fsgg-v1-admission-scoped-cas-quint-receipt.json`.
No live credential, network push, journal installation, or ordinary CLI change
has occurred. A real HTTPS receive-pack acceptance test remains an installed
runtime gate, not a result of these source tests.
