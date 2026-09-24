# FS.GG Coordination CLI

`fsgg-coordination` is the callable FS.GG Coordination boundary. Its ordinary delivery command observes and
plans by default; advancement remains guarded by the authoritative epoch, exact sealed plan, protected journal,
and explicit receiver/provider selection. Installing this package does not enable a production writer.

`fsgg-coordination ordinary-settlement execute` is the non-interactive post-merge settlement entry point.
It accepts no plan, token, or arbitrary operation arguments. The installed provider reruns the protected
source observer using `GH_TOKEN`, reads the receipt path from `FSGG_V2_PREFLIGHT_RECEIPT`, and accepts only
the production Authority profile. It reads the dedicated App ID/private key and RSA-PSS authorizer key from
`V2_ORDINARY_APP_ID`, `V2_ORDINARY_APP_PRIVATE_KEY`, and
`V2_ORDINARY_AUTHORIZER_PRIVATE_KEY`. Missing enrollment, a disabled policy, pre-`OpenV2` authority, a
changed source/anchor/ruleset, or the rehearsal profile returns a typed refusal before a journal ref write.

`fsgg-coordination ordinary-settlement rehearse` is a separate sandbox-only entry point. It pins repository
`FS-GG/FS.GG.Coordination.Authority.Sandbox`, environment `ordinary-v2-rehearsal`, epoch ref
`refs/heads/ordinary-v2-rehearsal-epoch`, and the rehearsal rulesets. It reads
`policy/v2-ci-ordinary-settlement-rehearsal.json`,
`policy/v2-ci-ordinary-settlement-rehearsal-anchor.json`,
`FSGG_V2_REHEARSAL_PREFLIGHT_RECEIPT`, `V2_ORDINARY_REHEARSAL_APP_ID`,
`V2_ORDINARY_REHEARSAL_APP_PRIVATE_KEY`, and `V2_ORDINARY_REHEARSAL_AUTHORIZER_PRIVATE_KEY`. The production
entry point cannot select this profile. Its source verifier command is `verify-rehearsal`; the rehearsal receipt
uses the production receipt schema with `environment` set to `ordinary-v2-rehearsal` and retains the exact two
settlement checks and eight gate checks. A protected-main workflow may set `FSGG_V2_REHEARSAL_FAULT` to only
`none`, `lost-ref-reply`, or `ref-conflict`. The lost-reply case applies the sandbox ref update and hides its
successful response so durable readback must reconcile it; the conflict case returns a definite 422 without a
ref effect. The production entry point refuses any rehearsal fault setting.

The contents-only App cannot observe ruleset bypass actors. Enrollment therefore anchors an administrator's
complete actor readback only after at least 60 seconds without a ruleset edit. Each attempt compares the exact
anchored `updatedAt`, visible ruleset shape, and effective rules twice. This is an explicit timestamp based
drift-detection assumption; it does not claim a privileged runtime actor read.
