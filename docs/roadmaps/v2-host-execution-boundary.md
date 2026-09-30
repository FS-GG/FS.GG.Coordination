# V2-HOST-01 — Fixed host execution boundary

Status: V2-HOST-01.1–.3 protected source and the fixed disposable qualification job are delivered through Coordination #893 and #895. Served-artifact/postmerge coherent qualification and the bounded V2 result are closed through the protected fixed run and owning evidence; installed operation is separate. Route: routine. Owner: Coordination for source and diagnostic artifacts; SystemAdmin for separately selected installed-host configuration and operation. Backlink: the `.github` [required host execution boundary](https://github.com/FS-GG/.github/blob/main/docs/github-substrate-v2-roadmap.md#required-host-execution-boundary--2026-09-29) and [full V2 acceptance amendment](https://github.com/FS-GG/.github/blob/main/docs/2026-09-07-154210-fs-gg-unified-development-roadmap.md#full-v2-acceptance-amendment--2026-09-29).

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

- [x] **V2-HOST-01.1 — Closed reviewed diagnostic operation.** Deliver the strict reviewed-profile parser and the fixed `executor-compatibility/1` CLI. Exact shape, operation, component pins, protocol/version, expiry and the absence of executable request fields fail closed before provider launch. Positive and injection controls passed and protected source was read back at `93c3e16` in #893.
- [x] **V2-HOST-01.2 — Bounded environment, runtime and cleanup.** Scrub the Host-to-runner environment to the reviewed key allowlist, retain the runner-to-provider allowlist, enforce the selected 1–30 second deadline, kill the process tree on timeout/failure, remove the owned disposable workspace and report cleanup truthfully. Timeout and post-kill controls passed and protected source was read back at `93c3e16` in #893.
- [x] **V2-HOST-01.3 — Fixed disposable qualification job.** Coordination #895 merged the reviewed profile, one-shot workflow, helper, pin inventory and refusal tests at protected `d99aa20ab4fb07b865c2ac28b55909e63062c053`; its exact-head required/coherent checks passed. Protected fixed run `36681426685` completed on that source and retained result artifact `11081962815`. The source and job accept no arbitrary command, path, environment or model-session request.
- [x] **V2-HOST-01.4 — Protected source and served artifact evidence.** Protected #893 and #895 source, independently downloaded exact Host/runner artifacts and the fixed protected result are bound in [owning evidence](evidence/v2-host-01-fixed-qualification.md). #895 postmerge coherent run `36681361583` succeeded under ADR-0084. The artifacts remain time-bounded candidates, not an installation.
- [x] **V2-HOST-01.5 — Bounded V2 result.** The [owning evidence](evidence/v2-host-01-fixed-qualification.md) joins protected source, served artifact identities, the fixed protected diagnostic and successful postmerge coherent qualification. Genuine SystemAdmin installation, credentials and installed-host validation remain an explicitly selected follow-up; missing evidence stays unknown.

## Verification and current evidence

The protected #893 focused Host test surface covers the successful reviewed profile, injected executable field refusal before provider launch, timeout process-tree termination and workspace removal, pin drift, malformed/stale/extra protocol responses, old-runner refusal and the unchanged W8 diagnostic. The Host project builds without warnings. Fresh Host and runner candidate artifact runs `36671955640` and `36671955521` passed from protected `93c3e16`; independent downloads and one local fixed diagnostic passed with complete cleanup. Postmerge coherent run `36671874604` for #893 succeeded. #895's qualified head and protected source have the same tree `30a772eee81fcfd7be749a955899bcaadeed5daf`. Its fixed workflow run `36681426685` succeeded from protected source. Independent artifact readback verified outer SHA-256 `3cbe2a4b861a534050044f29f0459d6d7bc3662260e0a7105e31e29715d927bb`, matching the API digest, and a result with scope `compatibility-diagnostic-only`, outcome `passed`, complete cleanup, zero model sessions and zero follow-up work. The later #895 protected coherent run `36681361583` succeeded under ADR-0084, closing the bounded .4/.5 diagnostic result. It does not prove installed Home/Main operation.

## Unified §9.9 workspace impact

There is no generated workspace change. No SDD, Spec Kit, none, typed-SDD or product-language family changes fresh creation, retained workspace behavior, defaults or enabled runtime behavior. The source merge adds a dormant Host CLI operation. V2-HOST-01.3 first makes the operation reachable only inside its separately reviewed disposable qualification job; SystemAdmin installation and adoption remain separate, explicit work. No producer publication or receiver adoption is claimed here, and upgrade handling is not applicable to this source candidate.
