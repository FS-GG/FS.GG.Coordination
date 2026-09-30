# V2-HOST-01 fixed diagnostic qualification

This record binds the selected disposable `executor-compatibility/1` qualification
to exact protected source and served candidate bytes. It is a bounded V2 host
execution result, not an installed Home/Main operation.

## Source and artifacts

- [Coordination #893](https://github.com/FS-GG/FS.GG.Coordination/pull/893)
  delivered the closed Host operation at protected
  `93c3e16ffb13ea5c6fae4368d22413e310452b62`.
  Its later coherent run `36671874604` succeeded.
- Fresh protected Host run `36671955640` retained candidate `11078676212`
  with inner archive SHA-256
  `56542da2fd79ac0e109eca0af55060a6e9b3e75b73eccd2e1110c08136409f66`
  and payload SHA-256
  `ed28abb1c198a5196ae44f7c11592253573e4d23ee9938ce54a529e5420c9f4a`.
  Its verification artifact is `11078945210`.
- Fresh protected runner run `36671955521` retained candidate
  `11078750995` with inner archive SHA-256
  `c58d6eee83e4b9cde9fffd03ab5e49dec6835ae7ff2430bb0487bb22fe332691`
  and payload SHA-256
  `eb4919fe2f3e27bbe8a75b294f5da9dc1b13a8d153edb6bc6e04b52fbd3ba76b`.
  Its verification artifact is `11078885481`.
- [Coordination #895](https://github.com/FS-GG/FS.GG.Coordination/pull/895)
  delivered the reviewed fixed operation profile, helper and workflow at
  protected `d99aa20ab4fb07b865c2ac28b55909e63062c053`, tree
  `30a772eee81fcfd7be749a955899bcaadeed5daf`. The protected and
  required/coherent qualified-head trees match. The profile SHA-256 is
  `31c910e43a4ee947c2fc12fda8bb0c581272437dcafd78f33264c87c00225569`.

## Protected fixed operation

[Run `36681426685`](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36681426685)
completed successfully from exact protected `d99aa20`. The workflow accepts no
request arguments and selects only the reviewed, unexpired profile and exact
Host/runner artifact IDs above. It retains result artifact `11081962815`.
Independent authenticated download matched its API wrapper SHA-256
`3cbe2a4b861a534050044f29f0459d6d7bc3662260e0a7105e31e29715d927bb`.
The extracted result schema is
`fsgg.coordination.v2-host-fixed-qualification-result/1`; it binds workflow
revision `d99aa20`, source revision `93c3e16`, both payload hashes and the
profile hash above. Its scope is `compatibility-diagnostic-only`, outcome is
`passed`, cleanup is `complete`, and it records zero model sessions, zero
follow-up work and six untouched-surface checks. Provider observations are
limited to version and login status. No model or autonomous follow-up ran.

## Qualification boundary

The #895 postmerge coherent source [run `36681361583`](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36681361583)
succeeded with no failed jobs under ADR-0084. This closes the bounded .4/.5
source, artifact and fixed diagnostic result. The artifact pair
and retained result are time-bounded Actions candidates, not a permanent
release or installation. SystemAdmin source was unavailable here. Operational
Home/Main custody, account scope, clean installation, retained upgrade and
actual host operation remain unknown and separate follow-up work. Telemetry
returned `not-configured`; usage and cost are unknown.
