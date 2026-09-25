# Prospective isolated-operation v2 source proof

Status: draft, import-only, unapproved. This is a new identity and source
boundary for the V2-CALL-01.4b isolated synthetic operation. It records no
live provider effect and grants no actor a credential, dispatch, retry, or
merge authority.

## Reason for revision

An offline adversarial review of the historical v1 operator found three
material false-green paths: a unique PR returned after an ambiguous POST was
accepted without checking its head and base revisions; branch protection was
accepted with force pushes enabled; and a provider URL exception left a
credential sentinel in the formatted exception chain. The existing sealed
operator, contract, proposal, validator, native evidence, and historical
receipt stay byte-for-byte historical. PR #547 records the red-before
characterization and is not acceptance evidence.

## This source boundary

- `eng/callable-cli-isolated-operation-v2.py` has identity
  `v2-call-01-4b-isolated-native-v2-provisional`. It exposes only public
  `inspect` and pure one-attempt readback classifiers. It has no HTTP client,
  token argument, write command, or retry command.
- PR readback requires two identical complete native census snapshots,
  repository identity, exact source and base branch revisions, one open
  nondraft unmerged PR, and its exact head/base repo/ref/SHA fields.
- Protection readback requires two identical complete snapshots, exact
  repository and branch, one required check with exact App ID, and explicit
  disabled force pushes and deletions. Provider write responses do not count
  as proof.
- Any incomplete, contradictory, duplicate, changed, malformed, or failed
  read yields `Unknown`. A durable one-attempt record and qualified native
  reader remain external prerequisites; the classifiers cannot infer them.
- `inspect` binds the prospective v5 contract, proposal, source/control
  hashes, and the old preflight solely as an immutable historical observation.
  It always reports `authorized:false` and `liveEffects:0`.

## Governed follow-up before GS2-09.9 acceptance

The separate contract owner will prepare a new v5 contract, proposal, and F#
validator with exact source and control digests. A later shared index/catalog
rotation and fresh Q3/Q6 negative controls must bind the new version and pass
before any GS2-09.9 receipt or acceptance. The #545 roadmap-pin source repair
may be considered separately; it is not a receipt or authority for this
revision. An installed protected probe must prove complete native PR and
branch-protection response semantics, including exact head/base and force
push state. No synthetic or production effect is authorized by this draft.
