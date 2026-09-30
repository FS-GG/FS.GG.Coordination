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
## Main retirement amendment — 2026-09-30

The user selected removal of operational Main and work-main as roadmap and runtime
prerequisites. Stop expanding Main. CI owns builds, releases and fixed disposable
qualification; a dedicated collector container owns private persistent telemetry
storage. Development agents run in isolated development environments. Work-main
has a final existing-record/access/retirement handoff role. Replacement operation
has its own accountable owner and does not require that agent.

Completed .1–.5 and historical Main receipts retain their original scopes. This
amendment does not reopen migration or make the LEARN experiment a full-V2 gate.
The [user-direction handoff](https://github.com/FS-GG/.github/blob/9dc0a67b1e46adaae12c5c07c1caf935542d33f9/MAILBOX.md)
supersedes the earlier Main-specific installation destination.

### Replacement and trust boundaries

Use strict Linux x64 rootless containers on a supported host. CI qualification
uses disposable hosted Ubuntu with private PID/UTS namespaces; the nested local
Podman host cannot currently qualify those namespaces. No host-namespace fallback
supplies acceptance. No Main account, filesystem, SSH route, container socket or
broad host mount is a runtime dependency.

The first topology is a dedicated collector and a fresh isolated development
container on the same qualified host, with a newly provisioned private native-source
volume. Development writes its genuine native source there; fixed collection reads
the original source under declared custody. Receiver store, enrollment credentials
and retained evidence stay outside development-writable mounts. Exact UID ownership,
executable/account binding and protected config custody must be verified.

Existing `collect-native` needs local durable admission and protected executable,
native-home and evidence custody; `export-learning` consumes retained capture.
Arbitrary uploaded receipts or copied logs cannot acquire verified native-source
meaning. Do not copy Main's native home or authentication. Physically remote capture
is later work unless its transport preserves these contracts; if the first local
topology cannot satisfy them, implement the smallest reviewed extension and publish
any changed Host under a new immutable version.

The authorized receiver owner can issue a genuine scoped collector locally: create
protected config/2 with a private CSPRNG secret, native-collector role, unique
workspace/producer/stream, GrantId and positive generation, then use existing init
and enroll-producer to persist that authority. A separate external grant issuer is
not required. Actual independently authenticated native-home custody and observed
provider capability remain real inputs. Installation binds requested model/effort
strings; version/login readiness does not prove those choices are supported.

Reuse the existing telemetry Host 0.2.1 publication lane. Host 0.2.0 predates #3986's
capture/2 hardening. Adopt only actual served successor package/manifest/payload
readback. Prepared source, image build and installed adoption are separate results.
No secrets enter images, logs or CI artifacts. Unavailable authentication remains
unknown and does not prevent source or controlled qualification work.

### Executable milestones

- [ ] **V2-HOST-01.6 — Freeze Main expansion and prepare the replacement — routine.**
  Depends on completed .1–.5 and this amendment. No new Main service, agent or custody
  request is selected. A concrete recipe names exact Host package/manifest, OCI base
  and final image digests, manager/native-reader/Codex identities, reviewed fixed
  operation revisions, private mounts, UID owner, scoped credentials, network paths,
  finite runtime/output and cleanup. No floating image tag is an acceptance identity.
  Reuse the independent Host release's protected eight-effect journal and both-feed
  readback; image qualification and installed adoption remain separate.
- [ ] **V2-HOST-01.7 — Closed native capability diagnostic — routine.**
  Reuse production `ObserveLearningSelection` in a separately versioned fixed
  operation for frozen `gpt-5.6-sol` / `medium`. The reviewed profile supplies pins,
  account scope and bounds. Requests accept no prompt, model override, script,
  arbitrary executable/environment or follow-up. Retain eight pages, 100 items per
  page, bounded bytes/deadline, correlation, executable/config drift and finite
  cleanup. Supported, unsupported, unavailable auth, incomplete/duplicate pages,
  timeout and changed executable have truthful distinct results; no thread or turn
  starts. Controlled CI does not impersonate authenticated installed support.
  Advertised support differs from actual turn identity; no LEARN activation follows.
- [ ] **V2-HOST-01.8 — Qualify collector/devcontainer with Main unavailable — routine.**
  Depends on .6's exact published Host and reviewed recipe; .7 where model support
  is consumed. Prevent Main access and omit its accounts, paths, keys and routes.
  Initialize fresh private state, enroll the scoped principal and install exact
  capture/2 custody. Qualify source/admission binding, authenticated receipt transport,
  protected capture/export and trusted analyzer readback. Recreate development and
  restart/interruption-recover collection without duplicate facts or lost acknowledged
  history. Refuse wrong/revoked grants, wrong producers, requested/observed mismatch,
  altered/imported source, absent native records and unavailable receiver. Controlled
  fixtures and genuine native capture receive separate verdicts. Native receipts
  require genuine source and observed identity, not capability listing alone.
  Retain private raw evidence and safe public dispositions. Owned disposable
  containers/processes/temp credentials disappear; persistent accepted data and
  verified backups remain. CI restart survival does not prove production durability.
- [ ] **V2-HOST-01.9 — Cut over selected telemetry routing — routine and bounded operation.**
  Depends on independent installed placement, exact adopted artifacts, genuine
  credentials and .8 at the claimed scope. Select the receiver for explicit streams
  and verify capture/drain with Main unreachable, preserved history and recovery.
  Avoid silent dual writing and preserve original-item/attempt/source attribution.
  Keep LEARN disabled until its separate readiness, census, allocation and frozen
  research contract admit enrollment; collection does not close .4/.5 or prove savings.
- [ ] **V2-HOST-01.10 — Preserve records and retire Main dependencies — bounded operation.**
  Depends on .9 and the actual old-writer/record inventory. Verify backups and
  replacement health, reconcile pending publication intents, then disable only
  inventoried services/agents/recurrence, remove routing and revoke credentials
  proven obsolete. Read back stopped/disabled state and absence of old recurrence.
  Shared/uncertain credentials remain preserved; delete no historical evidence.
  Qualify any retained CI public publisher's own identity and source-fence/CAS path,
  or keep publication disabled. No competing writers share a public ref. Inaccessible
  retirement effects stay pending without blocking independent qualified operation.

### Parallel lanes and workspace impact

Coordination's capability owner owns its new module, focused tests and minimum
Host entrypoint seam. The `.github` container owner owns new deployment recipes,
focused custody/restart qualification and dedicated workflow. The existing 0.2.1
release owner keeps its exclusive release touch-set. Root owns project/lockfile
registrations, shared inventories, these plans, Unified projection and PR admission.
Sol-medium implements the accepted Astra-high plan; no new work-main runtime lane.

No generated SDD/Templates bytes or lifecycle/provider defaults change in this
source window. First enabled behavior changes at explicit routing cutover .9.
Clean state and retained-history archive/migration are separately qualified.
Preserve V2/LEARN lineage and the frozen R5 cohort. Telemetry observation remains
`not-configured`; usage, population and economics stay unknown until observed.
