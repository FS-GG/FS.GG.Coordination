# GS2-09.7 clean V2 pilot

The bounded GS2-09.7 clean-start pilot and normal rerun are complete; GS2-09.8 continues ordinary V2 settlement in `.github`. It does not migrate V1 state. The first pilot repository is `FS-GG/.github`, using its protected-main workflow `.github/workflows/v2-ci-ordinary-settlement.yml` and the published `FS.GG.Coordination.Cli` 0.1.2 `ordinary-settlement execute` command.

The source route already captures the merged pull request and required checks, mints the repository-scoped ordinary V2 App credential, observes the protected fleet epoch, signs the settlement intent, appends through expected-parent Git CAS, and rereads an unknown response before reporting success. GS2-09.7 therefore adds no migration interpreter, V1 admission service, controller, or new runtime.

## Recorded activation inputs

Before activation, the protected cutover authority pointed to generation 1 at commit `42a25b1480203207183f37c56d315c4161fb627b` in `OperatingV1`. The owner-approved clean-start action appended generation 2 directly as `OpenV2`. This is a new operating profile; it is not evidence that the historical `Preparing`, `Frozen`, `SwitchedV2`, or `VerifiedV2` migration phases occurred.

The exact canonical event bytes, including their terminal line feed, are:

```json
{"fleetId":"fs-gg-production","phase":"OpenV2","schema":"fsgg.github-substrate.epoch-event/1"}
```

Their SHA-256 is `35c1b88f2a7ba336397145ec90f63947a94b6b6f7c74ece2e6530b7edc14b6d6`. The generation 2 journal head retains the production cutover address, sets `priorHeadDigest` to `5c33161a5c0af66a3521a2b23789cc2ddae56bb6bd455a4d663eb7b73621db79`, and binds that event digest. Its canonical candidate SHA-256 is `62015663fcf88aae45e9631d00af4cdb341dbf84fd7c580db325fb3185d74c86`. These values are preparation inputs; only protected native readback can establish the installed commit.

## Observed clean-start result — 2026-09-28

The shared authority advanced by a fast-forward append to generation 2 `OpenV2` at
[commit `26d1882`](https://github.com/FS-GG/FS.GG.Coordination.Authority/commit/26d1882af9293b264df17a1fa98515e108313fe5).
The original cutover-ref writer rule was restored and independently read back; the conflicting V1
genesis workflow is disabled. [Activation PR #3913](https://github.com/FS-GG/.github/pull/3913)
installed the `.github` policy at protected main `a98162fb119c43f2e5d60c2b284d01e81ac468db`.

[Run `36395767759`](https://github.com/FS-GG/.github/actions/runs/36395767759) returned
`SettlementSucceeded` on attempt 3 and `SettlementAlreadyComplete` on the normal whole-run replay,
attempt 4. Both bind settlement digest
`96eebd38b0639d4c446d62dcfa413adffae32c839b387006d68322b1ca7399d6`.
The [protected roadmap result](https://github.com/FS-GG/.github/blob/792bea56adeb09597d7b2c6db8220983b689e779/docs/github-substrate-v2-roadmap.md#observed-clean-start-result--2026-09-28)
records the native readbacks and completed bounded pilot. Earlier attempts stopped before settlement.

The workflow remains enabled for qualifying `.github` main pushes. Other repositories require
explicit enrollment; their adoption does not reopen this pilot or establish fleet-wide activation.
Defects are repaired forward or by disabling the affected writer, without overwriting old evidence.

## Acceptance boundary

The source gate runs the focused existing tests for the installed provider and the ordinary Git authority. The native result above supplies the protected generation 2 readback, restored cutover protection, installed policy, successful normal merge settlement and rerun required for this bounded completion. GS2-09.1 through GS2-09.6 remain historical accepted source receipts, and GS2-09.9 remains historical callable qualification; none is a prerequisite for this clean-start pilot.
