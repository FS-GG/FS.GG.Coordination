# LEARN-01.4 W8 served-diagnostic evidence

Status: selected technical window complete, 2026-09-29. LEARN-01.4 remains open.

[#891](https://github.com/FS-GG/FS.GG.Coordination/pull/891) delivered the
non-execution served compatibility diagnostic at protected commit
`c6fff6590055672174037b313642ade1502b71c4`, tree
`4034a5a8adbbeebdbceb5e400df5a810b4697fb5`. The merge has the same tree as
tested candidate `ef7185ba80510833176a505ea2a169b2ad49c8ce`. Native required and
coherent checks passed on that candidate. This evidence closes the bounded W8
technical window selected by the user in
[#3971](https://github.com/FS-GG/.github/pull/3971); it does not close the installed
learning window.

## Fresh paired artifacts

Both candidate workflows ran successfully on exact protected `main`. Their
prepared and fresh-served checks passed, and independent root readback reproduced
each downloaded hosted verification receipt byte for byte.

| Component | Workflow and retained artifacts | Inner archive and executable payload |
|---|---|---|
| Host | Workflow `355347969`, [run 36572192445](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36572192445). Candidate [11036570454](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36572192445/artifacts/11036570454): 42,094,433 bytes, outer SHA-256 `981687d141e15e7c8773e5a447b4e0f0a4fb0cc372287fa189669eca8651cd7e`. Verification [11036645379](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36572192445/artifacts/11036645379): 769 bytes, outer SHA-256 `6509f14e2ee8b6584e1d0b496e4edd2cc2d3c3ca55c7f475375f6505d49a2bf2`. Both expire `2026-12-28T13:02:11Z`. | Inner archive: 42,090,793 bytes, SHA-256 `4790a600356bf3ce5dca7db6555cf0249fc08db0b08398962953adad577eb1a7`. Apphost: 103,989,488 bytes, SHA-256 `936dbc95c82430194367c7743a42b27fe4351b1dc5342cdf87630c70e1d4397c`. Prepared receipt SHA-256 `ed405cc8f5069d493e539c6d041553742801e500f0f5c1456b77a71136c0a663`; generated and hosted verification receipt SHA-256 `d1432634346e24c9727114522a690d410ab80c1ab510eb89b8757e699eb69071`. |
| Runner | Workflow `355606824`, [run 36572197020](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36572197020). Candidate [11036140315](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36572197020/artifacts/11036140315): 34,512,107 bytes, outer SHA-256 `2139f98323fba1a121f705471dfa325739f3d69b75ff156603818a6add9b2b7d`. Verification [11035321485](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36572197020/artifacts/11035321485): 780 bytes, outer SHA-256 `908e3dc773d1791261a7ac3ee3e71ec8926a4dabf5dd0c49bbb42a9aa86c9265`. Both expire `2026-12-28T13:02:11Z`. | Inner archive: 34,508,399 bytes, SHA-256 `27ae2cef55f475829a0da1a1dfb91cfe429a304b0ca47e52c9b6152acd15410d`. Apphost: 80,099,779 bytes, SHA-256 `7197fa482355b167a5a733d6458327477eefc6af1a53df8605446f2ea5bbfcae`. Prepared receipt SHA-256 `420a1bc24c33919234a2f92caa72c36c401f20092c2e1440fbe5c8aa27e436bb`; generated and hosted verification receipt SHA-256 `8ff510c6835d893a0a5589702d22339a8e0cce881e7d9aa73c95ba3c2851adb2`. |

The artifacts are 90-day Actions candidates. This is qualified distribution
preparation, not permanent publication, a package release or installation.

## Actual controlled receiver

One authorized invocation used the freshly downloaded apphosts through the actual
Host `probe-executor-compatibility` command, runner `diagnostic-stdio` entrypoint
and a generated non-model provider sentinel. It passed these four cases:

- explicit `0.158.0` matched, then made exactly one `login status` call;
- omitted version retained the exact `0.154.0` default and matched, then made
  exactly one `login status` call;
- observed `0.159.0` against expected `0.158.0` returned version mismatch after
  one version call and made no authentication call; and
- a version containing a terminal line feed was refused before the provider ran.

All three diagnostic responses had distinct nonzero correlations and bound the
exact Host, runner and controlled-provider hashes. The parent environment digest,
both executable hashes and six sentinels for repository, workspace, input, state,
artifact and telemetry-outbox roots were unchanged. The bounded private result
receipt has SHA-256
`9a007fb1ffdc78183c48eb3eca89dc47398e4df90b7954c61fcbccbea2f7119a`;
its 23-entry output manifest has SHA-256
`164c78cf2492ad6f01cf05c10ede51e4a2a21607384ef7f8f860f3bea312b250`.

The optional old-runner case was not run in this downloaded-pair invocation
because no separately qualified extracted old runner was supplied. Source-native
evidence remains the bounded claim: an actual runner from protected
`424f0c7bcba19e512ac425c2f64a56d53bae8d43` refused the new command with
`compatibility-runner-unsupported`; no execution fallback occurred.

## Remaining boundary

The observed authentication string came from the controlled sentinel. It is not
evidence of a provider account, private Home authentication, model or effort
support, a model call, resume execution, installation, collector custody,
`ProviderReadiness`, learning admission or complete telemetry. The diagnostic
does not certify incidental filesystem behavior for a later genuine Codex
executable. Telemetry was not configured, so native usage and economics remain
unknown.

W6 genuine owner composition, permanent artifact publication, Main/SystemAdmin
installation and human/account readiness acceptance, prospective capture/cost
evidence and the installed current/focused experiment remain follow-up work under
their existing controls. No original, R5 cohort or frozen statistical contract
changes.
