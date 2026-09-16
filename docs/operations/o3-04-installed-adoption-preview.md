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
