# O3-04 installed adoption preview

This is a non-dispatching preview for one serial, two-project installed adoption. It defines the next protected
operation but does not authorize, invoke, install, enable, start, resume, reboot, or run a model.

## Immutable inputs

1. Read the merged Coordination PR for `docs/roadmaps/o3-controlled-adoption.md` and bind
   `sourceRevision` to its exact 40-character merge commit. Refuse an open PR, a moved head, or a source revision
   that is not an ancestor of protected `FS-GG/FS.GG.Coordination:main`.
2. Dispatch `.github/workflows/orchestration-container-bundle.yml` on that exact protected-main revision with
   `expected_sha=<sourceRevision>`. Accept only the downloaded
   `orchestration-container-linux-x64-<sourceRevision>` artifact after
   `eng/orchestration-container-bundle.py verify` succeeds against its `prepared.json`, archive, manifest,
   `sourceRevision`, and `sourceTree` bindings.
3. Bind the SystemAdmin image/unit definitions to accepted SystemAdmin PR 108 merge `9046d2b3`. Record the exact
   image digest, Host/runner payload digests, PostgreSQL volume identity, credential-file identities, two project
   IDs, and two canonical subjects before installation. Do not substitute a rebuilt or differently compressed
   archive.

## Previewed operation

Use a fresh store for the first adoption. Existing-store adoption is a separate later operation. Install the exact
bundle with both systemd user services stopped and disabled. Verify owner-only operator, runner, database, and
mTLS credential files; loopback Host binding; the private PostgreSQL channel; no Podman socket or broad home
mount; and authenticated status/control rejection for absent, wrong, stale, or changed credentials.

Prepare two immutable admissions, A and B, with different project IDs and canonical subjects, ordinary capacity
1, and the existing per-project recovery configuration. Start only the infrastructure needed for readback, with
the Host paused and dispatch disabled. Do not start a provider/model child. Confirm:

Before enabling or starting either service, create one owner-private request file for the installed Host's offline
verifier. Use the exact schema and field set below; substitute only observed values. `issuedAt` through `expiresAt`
must span no more than ten minutes. `expectedExecutableSha256` is the SHA-256 of the installed Host payload,
`expectedBackupIdentity` is returned by the one-time initialization of the dedicated fresh store, and
`expectedGenerationFence` is its exact observed fence.

```json
{
  "schema": "fsgg.orchestration.installed-adoption-request/1",
  "operationId": "<new-nonzero-guid>",
  "expectedSourceRevision": "<exact-o3-03a-protected-main-merge-sha>",
  "expectedExecutableSha256": "<installed-host-sha256>",
  "storeId": "<new-dedicated-qualification-volume-id>",
  "expectedBackupIdentity": "<new-dedicated-store-backup-guid>",
  "expectedSchemaVersion": 2,
  "expectedGenerationFence": 0,
  "issuedAt": "<utc-instant>",
  "expiresAt": "<utc-instant-no-more-than-10-minutes-later>",
  "ordinaryCapacity": 1,
  "recoveryCapacityPerProject": 1,
  "projectA": {
    "projectId": "f73401b4-b65d-4688-a960-a5de59b8f830",
    "repositoryNodeId": "R_project_a",
    "repositoryDatabaseId": 8101,
    "issueNodeId": "I_subject_a",
    "issueDatabaseId": 9101
  },
  "projectB": {
    "projectId": "1e2807c3-8669-4bf3-baf2-c184532b3ea4",
    "repositoryNodeId": "R_project_b",
    "repositoryDatabaseId": 8102,
    "issueNodeId": "I_subject_b",
    "issueDatabaseId": 9102
  }
}
```

With the service units stopped and disabled, invoke exactly:

```text
/opt/fsgg/orchestration/host/fsgg-coord-orchestration-host verify-installed-adoption --connection-file <owner-private-absolute-path> --request-file <owner-private-absolute-path>
```

Accept only exit code 0 and one stdout object with schema
`fsgg.orchestration.installed-adoption-result/1`, scope `offline-installed-store-qualification`, exact operation,
request/source/executable/store bindings, all named assertions passed, and observed final counts of zero for active
reservations, commands, candidates, and external effects. A count state of `unknown` is not zero and blocks the
operation. A failure retains its fixture state and does not authorize retry, renewal, reset, cleanup, activation,
or migration.

The retained first-run evidence is
`/home/developer/.local/state/fs-gg/o3-controlled-adoption/main-installed-adoption-20260916`. Its volume
`fsgg-o3-installed-0e3e44a87df34d8e91d743e884492189` and backup identity
`1d136222-a22e-409d-b605-80d3ea113e44` are evidence only: the recorded startup-pause stream makes that store
nonempty, so the verifier must refuse it. For the corrected Main rerun, create and initialize a new dedicated
volume, record its fresh volume ID, backup identity, schema version, and generation fence, and run the offline
verifier before `serve` is ever started against it. Do not reset, migrate, or reuse the retained volume. Keep the
fixed A/B identities above. The source revision and executable digest remain pending until the O3-03a merge is
bundled, verified, and installed under the separate Main operation.

The offline verifier establishes only the installed executable/store behavior in the preceding paragraph. The
board projection, route/claim readback, ambiguous mutation, ownership, and scoped Host recovery claims below stay
bound to the previously accepted source, integration, model, native-delivery, and reboot evidence. Do not report
those reused claims as observations made by the installed-store verifier.

- A's exact admission can reserve the sole ordinary slot; B is capacity-refused without changing B's generation,
  journal, budget, ownership, candidate history, or operation history.
- duplicate board membership for either subject resolves to one canonical local owner;
- timeout, rate-limit, or incomplete board readback retains known membership and authorizes no effect;
- route and claim readback failure blocks provider effects, while an ambiguous mutation remains
  `NeedsObservation` until native readback settles it;
- scoped recovery of A preserves its admission, both clocks, candidate, and operation history; B remains
  unchanged; the replacement Host remains paused until a fresh authenticated readback and a separate resume;
- wrong subject, displaced claim, expired authority, or changed admission fails closed;
- exact release of A permits the original unexpired B request; unknown usage/accounting alone does not release A;
  and reconciliation remains possible while the ordinary slot is occupied.

Cleanup only the declared A/B temporary state after terminal readback. Never renew a permit, admission, attempt,
execution deadline, or delivery deadline. Finish with both services stopped and disabled, no model process, no
reboot, no active reservation, and the exact installed image and source/bundle bindings recorded. Any missing or
ambiguous readback leaves the affected state pending and blocks cleanup or activation.
