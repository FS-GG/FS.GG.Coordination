# FS.GG.Native.Qualification

Private caller repository for the BAR hosted full-image qualification on the
dedicated, ephemeral Ubuntu 24.04 LTS (noble) guest. It exists only because the FS-GG
organization is on the GitHub Free plan: a self-hosted runner may be bound to
this private repository and to no public one.

This README, `.github/workflows/bar-hosted-image-qualification.yml` and
`.github/actionlint.yaml` are byte-identical copies of the reviewed template in public
[FS.GG.Coordination](https://github.com/FS-GG/FS.GG.Coordination) at
`deployment/qualification-runner/native-qualification/`. `MANIFEST.sha256` there pins
all three. Change them only by a Coordination pull request first, then copy the new bytes
here. The unmodified workflow refuses a dispatch whose copy differs from the template at
the pinned Coordination commit.

## Security model

1. **Private repository only.** The runner is a repository-level, ephemeral JIT runner
   registered on this repository in the `Default` group. No public repository is ever
   enabled for it, so no public workflow, fork or pull request can be assigned to it.
2. **Dispatch only.** The sole workflow trigger is `workflow_dispatch`. There is no
   `pull_request`, `pull_request_target`, `push`, `schedule` or `workflow_run` trigger,
   so fork pull requests cannot start a job here, and a fork of a private repository
   cannot be created by anyone outside the organization.
3. **Guards.** In the unmodified workflow the job is skipped, and never takes the runner,
   unless the run is on `refs/heads/main` and is attempt 1. Its first step then refuses
   unless the runner is in group `Default`, its name starts with `fsgg-q-`, the native job
   record matches this repository, run, commit and runner, and the Root selection clock is
   within 300 s. A plain unprivileged step then refuses unless the pinned Coordination
   commit is an ancestor of Coordination `main` and this caller's three files equal the
   template there, before any pinned-source code runs as root. These guards live in the
   workflow itself, so they bind only an unmodified caller; see the last paragraph of this
   section for the controls against a modified one.
4. **Least privilege.** `permissions: contents: read` plus `actions: read` (needed only to
   read the run's own job record in the first step). No repository secret is used or
   needed. Only the default token reaches the first step; checkouts persist no
   credentials.
5. **Pinned source.** Public FS.GG.Coordination is checked out at the explicit 40-hex
   `coordination_sha` input, which must be an ancestor of Coordination `main`.
6. **Disposable guest.** One job per VM. The guest is destroyed afterwards and never
   reused. The job runs privileged inside that disposable guest only; it is not a
   sandbox for untrusted code.

Branch protection and rulesets are unavailable for private repositories on the Free
plan. Therefore the remaining controls are: the workflow guards above, a collaborator list
limited to trusted owners, the host-side assignment guard that stops a VM whose runner is
assigned any job other than the dispatched run, and a review of every change to this
repository.

## Dispatch

Only Root dispatches, after a separate admission naming the VM and the
Coordination commit. Inputs: `purpose`, `full_image`, `coordination_sha`,
`selected_utc`, `latest_start_utc`, `observer_deadline_utc`. The runner label is
`fsgg-native-linux-x64-v1`.
