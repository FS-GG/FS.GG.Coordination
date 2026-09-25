# GS2-09.9 isolated operator rotation candidate

Status: provisional source preparation, 2026-09-25. This packet does not authorize an effect, merge, unit receipt, or GS2-09.9 acceptance.

## Defect and preserved evidence

The previously sealed `eng/callable-cli-isolated-operation.py` has three demonstrated gaps: ambiguous pull request creation can accept a sole wrong-head or wrong-base candidate; ambiguous protection setup can accept force pushes; and a URL exception chain can retain sensitive response text. [PR #547](https://github.com/FS-GG/FS.GG.Coordination/pull/547) retains the offline red-before characterization. The old Q3 validator's 28 controls do not cover these cases. PR #545's earlier Q3/Q6 exit-zero evidence is therefore disputed even if its six required hosted checks pass. Auto-merge is disabled and #545 remains unmerged. The old source, `/4` contract and proposal, old validator, native acceptance archive, and their digests remain historical bytes. A passing historical native operation is one observed synthetic subject, not proof that every ambiguous result is safe.

The separate [versioned source PR #549](https://github.com/FS-GG/FS.GG.Coordination/pull/549) provides an injected controlled runtime reader with 12 offline controls. Its CLI can exercise one simulated POST or PUT against a local transcript and SQLite attempt fence, but exposes no credential or live network transport. This packet binds that exact source and control file by SHA-256 and labels the old preflight as `historical-observation-only`. The preflight's 2026-09-18 observations do not assert current provider authority.

## Proposed source bindings

| Role | Path | SHA-256 or binding |
| --- | --- | --- |
| Versioned source | `eng/callable-cli-isolated-operation-v2.py` | Current #549 draft `748af1842aaf9b62b747926c4f824b174eb816230c4d10301735d38092e4dbaf`; final digest pending native closure repair |
| Offline controls | `eng/tests/fsc07-isolated-operation/test_versioned_operator_readback.py` | Current #549 draft `8e92cf365c147eaf4bde189e747b4759f22cf5ffa7ccf2e181467fa860746a76`; final digest pending native closure repair |
| Historical preflight | `evidence/github-substrate-v2/gs2-09-9/isolated-operation-preflight.json` | Raw SHA-256 `ca9ee64633866425ced8d4578964df70eb3dc4cac2fd420f2d2963d5222e7de8`; self-digest `46c9af2263a8f686c59e90aacb79cc5466c2e1d3c6422221f86b1373fe5813eb` |
| Provisional contract | `eng/callable-cli-isolated-operation-v2-contract.json` | `/5`, `prepared-not-authorized`, exact source/control/historical bindings |
| Provisional proposal | `eng/callable-cli-isolated-operation-v2-proposal.json` | `/5`, exact contract self-digest and source binding, same historical observation |
| Offline validator | `eng/validate-github-callable-isolated-operation-v2.fsx` | Exact old-byte immutability, v5 bindings, zero-effect inspect, seven controls |

The v5 operator's `inspect` result must remain `authorized:false`, `historicalObservationOnly:true`, `liveEffects:0`, and `refused-no-compatible-admitted-target`. The `exercise-offline` command uses an injected transcript and local attempt fence; its results must retain `simulationOnly:true` and `liveEffects:0`. Both `--phase contract` and `--phase recovery` in the provisional validator check the exact source and all 12 controlled tests, then require the named Q3 or Q6 controls for that phase. A green result proves this bounded source packet only.

## Proposed gate rotation and remaining closure

The eventual exact candidate must register new Q3 and Q6 gate IDs and command digests in `eng/github-substrate-v2-gates.json`, then update the GS2-09.9 `gateCommands`, `gateContracts`, exit text, and `contractSha256` in `eng/github-substrate-v2-units.json`. The historical protected acceptance Q6 validator remains a separate gate. The old Q3 source validator must not be counted as proof of the repaired ambiguity semantics. The gate catalog and typed index are intentionally untouched in this provisional PR because the versioned source is still draft and the full exit gate is unresolved.

The proposed Q3 gate ID is `github-callable-isolated-operation-v2-contract`, running `dotnet fsi eng/validate-github-callable-isolated-operation-v2.fsx -- --root . --phase contract`. The proposed Q6 gate ID is `github-callable-isolated-operation-v2-recovery-contract`, running the same script with `--phase recovery`. Their command hashes, the final v5 source/control digests, the catalog digest, and the GS2-09.9 contract digest must be computed only from the final integrated candidate. `RoadmapWorkArchitectureTests.fs` literal expectations must follow the accepted catalog/index bytes without weakening its stale-index negatives. A prospective accepted receipt and `evidence/index.json` row are a later, separate protected acceptance change.

The current catalog has 75 commands. Adding both proposed IDs would make 77; replacing the old isolated Q3 command instead would make 76. The final typed exit decision must choose and document which historical command remains registered. Each `commandSha256` is SHA-256 of the executable and argument strings joined with NUL bytes, as implemented in `RoadmapWorkArchitectureTests.fs`. No command count or digest is changed by this packet.

The shared rotation touch set also includes the existing literal GS2-09.9 contract digest in `eng/validate-github-callable-ordinary-delivery.fsx`; otherwise both ordinary Q3 and Q6 refuse the newly registered unit. This literal must be changed to the recomputed final contract digest and the ordinary gates rerun on the same candidate. Keep the old proposal and native acceptance files as immutable historical inputs to those ordinary gates.

| Gate | Controlled case | Required result |
| --- | --- | --- |
| Q3 | One PR POST returns a complete response or loses its response; two complete native reads bind exact repository ID, source ref/head, base ref/head, PR number/node, and operation identity | Accept only the unique exact candidate; one POST attempt |
| Q3 | A sole PR after response loss has a wrong head, base, repository, or operation identity | Unknown or refusal; no second POST and no accepted PR |
| Q3 | PR census is incomplete, duplicated, or changes between complete reads | Unknown or refusal; no accepted PR |
| Q3 | A singleton read supplies an unexpected Link header, or the terminal repository/ref/branch identity changes after the final PR detail or policy response | Unknown or refusal; no complete poststate claim |
| Q3 | One protection PUT returns a complete response or loses its response; two complete native reads bind the protected branch and full required policy | Accept only the exact no-force-push, no-deletion policy; one PUT attempt |
| Q3 | Protection readback permits force push or deletion, has missing policy fields, targets another branch, or drifts between reads | Unknown or refusal; no accepted protection |
| Q6 | Response loss occurs before or after a persisted attempt record, and the process restarts | Reconcile by exact native readback; never repeat an uncertain write |
| Q6 | A settled operation is replayed in a fresh process | Same settled identity with zero new write attempts |
| Q3/Q6 | Transport, HTTP, or URL exception text contains a secret sentinel | Public refusal or unknown result contains no sentinel, including its exception chain and traceback |
| Q3/Q6 | Contract, proposal, source/control bytes, old preflight, or native readback drift | Refusal before effect; retained historical evidence remains unchanged |

Before registration or receipt, a corrected integrated runtime path must exercise the operation boundary against controlled GitHub responses and prove persisted-before-effect recovery, ambiguous readback refusal, settled replay, and no sensitive traceback. Required negative cases include wrong PR head, wrong base, foreign repository or identity, duplicate or incomplete PR census, force-push and deletion-enabled branch protection, drift between two complete native reads, failed readback after an ambiguous write, and exception text containing a sentinel. The current #549 controlled runtime supplies an offline request path and 12 focused cases. Its native completeness and terminal identity closure are under review, and it does not authorize an installed or live provider path. The ordinary Q3/Q6 gates must continue to exercise the installed CLI production boundary on the same final candidate. A corrected versioned operator also needs an installed or genuinely equivalent controlled end-to-end execution that binds one durable attempt, complete native poststate, loss reconciliation, and replay. Offline predicates plus the historical v1 native operation are insufficient for that obligation; the typed exit gate must not be narrowed merely to accept this packet.

Once those clauses are implemented and reviewed, qualify one fresh immutable replacement candidate at the exact live roadmap pin with complete Q3/Q6 gates, required hosted checks, independent native archive/readiness/handoff revalidation, and protected merge readback. That candidate must carry #545's narrow roadmap pin and the reviewed v5 rotation together. PR #545 remains unmerged with auto-merge disabled, then closes as superseded after the replacement's protected acceptance. Append a prospective `accepted/GS2-09.9.json` receipt and evidence-index row using the replacement's actual protected merge and acceptance time. Preserve #531's source merge, #545's disputed candidate, and the historical isolated run as distinct facts.
