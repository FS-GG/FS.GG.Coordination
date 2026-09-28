# GS2-09.7 clean V2 pilot

GS2-09.7 is a clean-start pilot of the already installed ordinary V2 settlement path. It does not migrate V1 state. The first pilot repository is `FS-GG/.github`, using its protected-main workflow `.github/workflows/v2-ci-ordinary-settlement.yml` and the published `FS.GG.Coordination.Cli` 0.1.2 `ordinary-settlement execute` command.

The source route already captures the merged pull request and required checks, mints the repository-scoped ordinary V2 App credential, observes the protected fleet epoch, signs the settlement intent, appends through expected-parent Git CAS, and rereads an unknown response before reporting success. GS2-09.7 therefore adds no migration interpreter, V1 admission service, controller, or new runtime.

## Clean-start activation

The protected cutover authority currently points to generation 1 at commit `42a25b1480203207183f37c56d315c4161fb627b` in `OperatingV1`. The owner-approved clean-start action appends generation 2 directly as `OpenV2`. This is a new operating profile; it is not evidence that the historical `Preparing`, `Frozen`, `SwitchedV2`, or `VerifiedV2` migration phases occurred.

The exact canonical event bytes, including their terminal line feed, are:

```json
{"fleetId":"fs-gg-production","phase":"OpenV2","schema":"fsgg.github-substrate.epoch-event/1"}
```

Their SHA-256 is `35c1b88f2a7ba336397145ec90f63947a94b6b6f7c74ece2e6530b7edc14b6d6`. The generation 2 journal head retains the production cutover address, sets `priorHeadDigest` to `5c33161a5c0af66a3521a2b23789cc2ddae56bb6bd455a4d663eb7b73621db79`, and binds that event digest. Its canonical candidate SHA-256 is `62015663fcf88aae45e9631d00af4cdb341dbf84fd7c580db325fb3185d74c86`. These values are preparation inputs; only protected native readback can establish the installed commit.

The native owner sequence is:

1. Recheck the current cutover ref, head bytes, ruleset, ordinary V2 environment, policy, anchor, workflow revision, and required checks.
2. Grant a bounded one-shot path for the generation 2 append, update the cutover ref by exact expected parent, reread the ref and both canonical files, and restore the cutover rule immediately.
3. Merge the `.github` activation change that sets `policy/v2-ci-ordinary-settlement.json` to `status: installed` and `credentialJob.installed: true`. The current anchor stays unchanged for the pinned 0.1.2 route.
4. Use that normal protected-main merge as the pilot operation. Verify the workflow conclusion, journal CAS result, fresh provider readback, and settled replay.
5. Perform one normal rerun smoke. A failure disables the policy or lands a conventional repair-forward change; no old data is overwritten or deleted.

The workflow triggers on every qualifying `.github` main push. The activation merge is the first pilot and successful readback moves directly to GS2-09.8 continuous rollout. This route does not promise a single-run selector.

## Acceptance boundary

The source gate runs the focused existing tests for the installed provider and the ordinary Git authority. Native completion additionally requires the exact protected generation 2 readback, restored cutover protection, an installed policy readback, and one successful normal merge settlement plus its rerun. GS2-09.1 through GS2-09.6 remain historical accepted source receipts, and GS2-09.9 remains historical callable qualification; none is a prerequisite for this clean-start pilot.
