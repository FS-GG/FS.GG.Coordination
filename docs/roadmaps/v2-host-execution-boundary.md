# V2-HOST-01 — Fixed host execution boundary

Status: source candidate for V2-HOST-01.1 and V2-HOST-01.2 is ready on `routine/v2-host-01-20260930`; delivery, served-artifact qualification and installed operation remain pending. Route: routine. Owner: Coordination for source and diagnostic artifacts; SystemAdmin for separately selected installed-host configuration and operation. Backlink: the `.github` [required host execution boundary](https://github.com/FS-GG/.github/blob/main/docs/github-substrate-v2-roadmap.md#required-host-execution-boundary--2026-09-29) and [full V2 acceptance amendment](https://github.com/FS-GG/.github/blob/main/docs/2026-09-07-154210-fs-gg-unified-development-roadmap.md#full-v2-acceptance-amendment--2026-09-29).

## Boundary

The only admitted operation is `executor-compatibility/1`. A restricted job selects one reviewed profile and one result path through:

```text
fsgg-coord-orchestration-host qualify-fixed-job \
  --profile <absolute-reviewed-profile-path> \
  --result <absolute-result-path>
```

The exact profile schema is `fsgg.orchestration.host-fixed-qualification/1`. It binds its revision, operation, Host/runner/provider SHA-256 identities and executable locations, runner protocol, adapter and Codex versions, environment key allowlist, read-only login-status credential scope, 1–30 second runtime, expiry, one disposable workspace and `delete-owned-workspace/1`. Unknown or duplicate profile fields refuse. The invocation has no command, recipe, URL, environment value, model, session or follow-up input. The emitted profile digest records the selected bytes; it does not authorize those bytes. The fixed job and its protected artifact selection must establish that the profile is the reviewed one.

The result schema is `fsgg.orchestration.host-fixed-qualification-result/1`. It records the selected profile/artifact identities, protocol, scope, allowed environment keys, runtime, timestamps, disposition, stable detail, the bounded diagnostic when available, and explicit process-tree/workspace cleanup markers. Exit zero requires both a matching diagnostic and successful cleanup. Invocation/result-path failure exits 2; profile, diagnostic or cleanup refusal exits 3 and records the stable reason in `detail` whenever the result path is usable.

The earlier `probe-executor-compatibility` route remains the W8 developer diagnostic. It is not an installed operation profile, does not gain operation authority, and is not replaced or relabelled by this feature.

## Milestones

- [ ] **V2-HOST-01.1 — Closed reviewed diagnostic operation.** Deliver the strict reviewed-profile parser and the fixed `executor-compatibility/1` CLI. Exact shape, operation, component pins, protocol/version, expiry and the absence of executable request fields fail closed before provider launch. The local source candidate and positive/injection controls are ready; completion requires source merge readback.
- [ ] **V2-HOST-01.2 — Bounded environment, runtime and cleanup.** Scrub the Host-to-runner environment to the reviewed key allowlist, retain the runner-to-provider allowlist, enforce the selected 1–30 second deadline, kill the process tree on timeout/failure, remove the owned disposable workspace and report cleanup truthfully. The local source candidate and timeout/post-kill controls are ready; completion requires source merge readback.
- [ ] **V2-HOST-01.3 — Fixed disposable qualification job.** Add the separate repository workflow/job that constructs or selects the reviewed profile, pins the exact served artifacts, invokes only the CLI above on a disposable machine, and retains the canonical result. The job must include pin drift, expiry, protocol/version mismatch, injection, timeout and cancellation cases. This source lane does not edit `eng/` or workflows.
- [ ] **V2-HOST-01.4 — Protected source and served artifact evidence.** Qualify exact protected source and freshly downloaded Host/runner artifacts, retain their manifests and result/cleanup evidence, and distinguish candidate artifacts from installation.
- [ ] **V2-HOST-01.5 — Bounded V2 result.** Join source, served-artifact and fixed-job evidence into the selected V2 acceptance result. Genuine SystemAdmin installation, credentials and installed-host validation remain an explicitly selected follow-up; missing evidence stays unknown.

## Verification and current evidence

The focused Host test surface covers the successful reviewed profile, injected executable field refusal before provider launch, timeout process-tree termination and workspace removal, pin drift, malformed/stale/extra protocol responses, old-runner refusal and the unchanged W8 diagnostic. The Host project builds without warnings. Hosted disposable-job qualification and protected served-byte evidence belong to V2-HOST-01.3/.4 and remain pending.

## Unified §9.9 workspace impact

There is no generated workspace change. No SDD, Spec Kit, none, typed-SDD or product-language family changes fresh creation, retained workspace behavior, defaults or enabled runtime behavior. The source merge adds a dormant Host CLI operation. V2-HOST-01.3 first makes the operation reachable only inside its separately reviewed disposable qualification job; SystemAdmin installation and adoption remain separate, explicit work. No producer publication or receiver adoption is claimed here, and upgrade handling is not applicable to this source candidate.
