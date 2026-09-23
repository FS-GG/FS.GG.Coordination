# Production v1 admission repair

The accepted GS2-08 bridge is fail-closed in production. On 2026-09-23 the protected fleet cutover ref existed, while the canonical `fleet-v1-admission:fs-gg-production` operation ref returned 404. The ordinary CLI must not infer an operation scope, use an ambient token as protected authority, or initialize this ref.

## Formal constraint

Keep the authored `src/FS.GG.Coordination.Protocol/Protocol.md` unchanged. The relevant gates are `protocolEnvelopesAreValidAndOrdered`, `durableProtocolCheckpointsArePreserved`, `mutationResultsAreBound`, `uncertainMutationOutcomesStayUnknown`, and `durablePlansAreOrderedAndResumable`. A failed invariant stops implementation; it is not repaired by weakening the specification.

## Sequence

1. Coordination `V1AdmissionRegistry.planGenesis`: derive exact, replayable root Git objects only from a verified `OperatingV1` authority and two absent journal heads. No provider write or authorization is supplied by this plan. `verifyGenesisReadback` requires the exact planned root. Unit controls reject existing, moved, incomplete, unauthorized and non-`OperatingV1` observations. **Source complete: 467/467 unit tests and the full pinned canonical Quint qualification passed against the unchanged protocol.**
2. Separately protected operation: bind the genesis plan to exact source, current authority/manifest, effective rules, dedicated writer identity and human approval; recheck absence immediately before one expected-absent ref creation, then independently read back every object and the ref. A source plan or chat authorization alone cannot create the ref. The source now has a trust-anchored RSA-PSS signature verifier over the exact genesis-object bytes, source and workflow identity, protected run ID, and bounded expiry. A separate native-approval verifier checks the exact workflow SHA-256, run, reviewed artifact, reviewer rule, and non-self approval. The workflow is prepared locally in `.github` branch `routine/v1-admission-protected-workflow` but is not filed, merged, or dispatched. The validated intake draft is `dotgithub-workflow-intake.json`; `intake apply` currently refuses because production v1 admission itself is unavailable. Effective Authority rules, fresh authority/absence, protected transport, and readback remain required.
3. Protected admission service: inventory incumbent operation and claim generations, validate each `MutationContext` against the fresh fleet authority, and append admissions under exact-parent CAS. The ordinary CLI may recover only these already-admitted handles; it cannot invent an admission from argv.
4. Producer: implement live raw-Git authority/journal readers, exact-parent CAS writer, durable operation-scope composition and provider reconciliation. Keep `UnavailableProductionMutationFence` until all inputs and controls are installed. Prove unknown response, lost append, concurrent parent, stale epoch, retry, and old-client refusals against independent fake providers.
5. Publish/adopt a coherent producer and verify installed runtime behavior with the existing guarded intake. Only then resume GS2-09.7 filing and protected acceptance; do not infer migration acceptance from this repair.

The current step does not install the journal, enable v1 writes, or satisfy the remaining roadmap gates.
