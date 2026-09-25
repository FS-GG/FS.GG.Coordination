# Prospective installed v2 effect adapter

Status: source proposal, 2026-09-25. No live command, credential, protected grant, target, or provider effect is authorized by this file. The one-operation boundary is in [the rotation plan](plan.md#smallest-prospective-protected-v2-native-operation).

## Current executable boundary

The retained [readiness packet](../../evidence/github-substrate-v2/gs2-09-9/callable-readiness.json) identifies installed `FS.GG.Coordination.Cli` 0.1.1, its `delivery inspect`, `delivery plan`, and `delivery advance` commands, and the historical Python operator `eng/callable-cli-isolated-operation.py`. `delivery advance` is ordinary source delivery with its own sharded journal. It is not a command to create this synthetic PR. The historical `.github` executor pins old Coordination revision `bcb8453c3b92a9ea6097e66e326bd1ec3350b667` and mints an execution token with `contents:write`, `checks:read`, `administration:read`, and `pull_requests:write`. Changing dispatch inputs cannot turn that executor into a narrow v2 actor.

Draft #549 and stacked #551 define `eng/callable-cli-isolated-operation-v2.py`. Its public CLI offers only `inspect` and `exercise-offline`. `NativeReadAdapter` consumes injected REST responses; `run_pull_once` requires an injected before-send fence; `LoopbackHttpTransport` accepts only literal `127.0.0.1`. None obtains a GitHub token or supplies a protected cross-run journal. Draft #550 binds these exact source bytes and keeps `authorized:false`. The current GS2-09.9 typed permission ceiling allows historical native revalidation and excludes new provider mutation and external acceptance. An installed live adapter cannot be inferred from any of those drafts or from the v1 native archive.

## Proposed closed entry and custody

One new protected `execute-native-pull` entry would live in the v2 operator, behind a separately reviewed `/6` or other new versioned contract and a new `.github` plan/authorize/execute workflow. Its implementation must be unreachable from `inspect` and `exercise-offline`; both retain `authorized:false` and zero live effects. The command must refuse before constructing a write transport when any required input is absent. There is no default target, default token environment variable, implicit workflow context, local approval flag, or fallback to the v1 executor.

| Port | Exact input and responsibility | Failure boundary |
| --- | --- | --- |
| Installed artifact | Protected workflow checks out immutable Coordination revision, verifies the v2 source and contract hashes and executable path, and records the installed CLI/package and workflow bytes. The separate ordinary `delivery` command remains pinned for its own journey. | A checkout path or source hash mismatch refuses before token mint or effect. |
| Authority observer | Read-only `.github` App credential verifies the protected run ID/attempt, main revision, workflow digest, environment ID, exact reviewer approval and active reviewer membership; it downloads the single canonical grant artifact with an exact archive/payload digest and expiry. | Dispatch strings are lookup hints. Missing, self-approved, expired, replayed, multi-file or drifting grants refuse. The actor's repository flags are not credential proof. |
| Target observer | Selected-repository App read credential independently verifies installation ID, repository numeric/node ID, visibility and scope, source/base refs and SHAs, complete PR census and required policy. It rechecks these at dispatch and supplies two complete native poststate reads through `NativeReadAdapter`. | Missing pagination, foreign identities, branch/policy drift or unsupported capability refuses. A GS2-09.7 Q4 mint proof cannot admit this target or serve as the v2 grant schema. |
| Journal writer | Separately scoped credential commits and reads back one operation ID, canonical POST body digest, target/ref identities, grant/run binding and `attempt-may-have-started` state through a qualified protected compare-and-swap backend. A local SQLite fence and a workflow artifact alone do not satisfy this port. | CAS loss, unreadable generation or uncertain prior dispatch forbids a new POST. A fresh process must load the same durable generation. |
| Execution writer | Only after all preceding proofs, mint or expose a short-lived App installation token selected to the one repository with `pull_requests:write` and the minimum necessary read permissions. The transport admits exactly `POST /repos/{owner}/{repo}/pulls` with the sealed canonical body, `GET` for the exact readback allowlist, fixed `https://api.github.com`, no redirect following, bounded response bytes, and no token in output. It issues at most one POST for that operation ID. | Any other method/path/body/host, extra repository, broad or unknown permission, explicit 3xx/4xx, or secret-bearing exception is a refusal or Unknown. Response loss and 5xx trigger readback only. |
| Cleanup writer | A distinct expiring cleanup grant and token become available only after the native receipt is independently verified and a cleanup intent is durably committed. | Unknown native result has no automatic compensation. Cleanup readback cannot replace pre-cleanup native proof. |

The future adapter should call the existing `run_pull_once` classification and `NativeReadAdapter` only after binding its grant, prestate and journal to the same operation. It must preserve `Unknown` for incomplete or drifting native reads and never use a 2xx response alone as acceptance. The planned one POST does not exercise `run_protection_once`; a protection PUT needs a separate grant and native window if required by the final unit contract.

## Required negative controls before installation

These controls belong to the later exact executable candidate and must run through its real CLI entry and protected-workflow adapter. The #551 loopback suite already proves classifier behavior and a local one-attempt fence; it cannot be relabeled as these installed controls.

| Case | Required observation |
| --- | --- |
| Invoke without grant, with `authorized:false`, or from an unprotected run | Exit nonzero; zero execution-token mint, journal write and POST. |
| Swap workflow/source/contract hash, reviewer or grant artifact; expire the grant; select another repository | Refuse before execution-token mint and POST. |
| Supply a valid grant but lose journal commit acknowledgement or readback | No POST; restart reconciles the journal generation without creating a second attempt. |
| Lose the POST response, return 500, or restart after `attempt-may-have-started` | At most one POST across processes; two complete native reads classify exact poststate or Unknown. |
| Return explicit 302/401 with scripted exact poststate | Unknown and zero repeat POST; never follow the redirect or attribute Applied. |
| Change PR head/base, node ID, repository identity, policy, pagination or terminal ref between reads | Unknown; no accepted native receipt or cleanup grant. |
| Put a secret sentinel in a transport exception, HTTP body or URL | No sentinel in public result, exception chain, traceback or retained sanitized artifact. |

Implementation is held at the grant and journal ports. The v5 contract has no live authority schema, the v2 script has no protected token or cross-run CAS reader, and the v1 executor pins incompatible bytes and broader scopes. Adding a callable effect subcommand with an injected or local boolean approval would create an unqualified live path. The next source candidate must first define and test the canonical grant, exact protected observation and durable journal interfaces with no effect route; only then should the installed transport and workflow be attached and independently qualified. Separate protected one-operation authority and environment approval remain the final authorization point before any native POST.
