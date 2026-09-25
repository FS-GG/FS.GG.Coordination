# Decision draft: install a closed v2 grant inspection entry

Status: source-only design, 2026-09-25. No package, installed command, grant, workflow dispatch or provider effect is created by this draft. It follows the [versioned parser](grant-schema.md) in draft #554 and the separate [one-operation authority proposal](authority-amendment.md) in draft #550. The GS2-09.9 typed permission ceiling and Q3/Q6 gate/index/receipt stay held.

## Observed installed boundary

The retained accepted callable-readiness packet binds `FS.GG.Coordination.Cli` 0.1.1 and `fsgg-coordination` with `delivery inspect/plan/advance`; its historical Python operator is the v1 script. Current CLI source declares dotnet tool package `FS.GG.Coordination.Cli` 0.1.2 in `src/FS.GG.Coordination.Cli/FS.GG.Coordination.Cli.fsproj`. `Program.fs` dispatches F# commands and has no isolated-v2 entry. The `.fsproj` compiles F# files and packs `README.md`; it does not include Python modules. The accepted 0.1.1 package cannot acquire #554's Python parser from a source checkout or dispatch input. Current 0.1.2 source is not proof that a new package was published or adopted.

`delivery --provider github` is also the wrong boundary: it accepts a caller-named `--token-env` and optional `--api-base`, uses one runtime credential for the ordinary sharded journal and PR merge, and has an ordinary-delivery journal schema. `ordinary-settlement` has its own credential-reading production and rehearsal entries. Neither command is a no-grant v2 PR creation entry. Reusing either would widen the current operation without a reviewed contract.

## Selected packaging route for the next source candidate

Prepare a separate dependency-free Python zipapp named `fsgg-callable-isolated-v2.pyz`, containing exactly a new `__main__.py` and #554's `callable_isolated_v2_grant.py` at reviewed digests. Build it deterministically from an immutable Coordination source revision; retain the source/tree, builder, Python runtime, member list, archive SHA-256 and an independent clean-build byte comparison. Install that exact archive into an empty protected runner directory and invoke only `python3 -I /absolute/installed/fsgg-callable-isolated-v2.pyz inspect-grant`. The existing `fsgg-coordination` dotnet tool remains the ordinary CLI and is never the fallback for this entry. This route uses #554's parser directly and does not put a Python subprocess inside the .NET tool. The artifact is not installed today; a local staged copy or loopback run cannot supply that proof.

The first zipapp candidate must have only `inspect-grant` and `--help`. `inspect-grant` may take an exact grant artifact, expected protected-binding packet, replay observation and digest for offline validation, but its public result must remain `authorized:false`, `can_dispatch:false`, `live_effects:0`. Any `execute`, `post`, `--provider`, `--token-env`, target override or unrecognized command exits nonzero before token access, journal access or network construction. A future live effect entry requires a new reviewed artifact and protected workflow; it cannot be enabled by changing an argument or environment variable on the inspection artifact.

## Required no-grant proof at the installed artifact

| Control | Acceptance observation |
| --- | --- |
| Clean install and identity | Unpack only the exact reviewed archive in a fresh directory; verify archive and every member hash, Python interpreter path/version, fixed entry and no import from current working directory or user site. A changed member or invocation path refuses. |
| Absent, malformed, foreign or expired grant | Invoke the installed entry with missing artifact, changed digest, duplicate JSON, foreign source/run/target/actor/credential, broad permission or observed used grant. All return nonzero or an explicitly non-authorizing inspection result. |
| Token custody | In an in-process entry test, provide an environment mapping whose token-key reads fail the test. Also run the clean installed artifact with sentinel credential variables. No missing-grant path reads a token, creates a GitHub App JWT or mints an installation token. |
| Provider and journal ports | Inject spies that fail on any provider HTTP request, POST, native journal API read or journal write; run the complete missing-grant and valid-synthetic-grant inspection matrix. The archive has no live transport or journal module, so a clean artifact import/command census must find no reachable effect entry. |
| One-attempt boundary | This inspection candidate claims **zero** attempts. The later effect candidate must bind a protected CAS generation before any POST and prove at most one POST across lost response and process restart through its installed entry. #551 loopback and #554 replay-observation tests remain source controls, not that installed proof. |

The installed-artifact test must run against the exact archive consumed by the protected workflow, not a staged source copy. The later protected workflow must pin its own main revision, exact Coordination revision and artifact hash, grant verifier, separate observer/journal/execution roles, selected target and independent environment reviewer. It must re-read those identities before a native effect. No zipapp publication, receiver pin or workflow dispatch is proposed in this branch.

## Decision packet and next authority

The next source owner can implement only the zipapp builder, closed `__main__.py`, archive manifest and no-grant tests in disjoint files, then prepare a draft protected installation workflow. That source delivery still cannot issue a grant. Before approving any live v2 operation, the owner must select and seal one synthetic target, reviewer distinct from dispatch actor, protected workflow/environment, artifact revision/hash, App installation and exact roles, v2 CAS journal coordinates and complete prestate. These values then populate the one-use grant and separate operation authority in #550. The smallest nondelegable action is the independent protected environment approval of that exact run/attempt after durable intent and capability readback; it is not requested from this source draft.
