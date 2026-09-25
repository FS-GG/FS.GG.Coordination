# Prospective isolated-operation v2 source proof

Status: draft, controlled offline runtime, unapproved. This is a new identity
and source boundary for the V2-CALL-01.4b isolated synthetic operation. It
records no live provider effect and grants no actor a credential, dispatch,
retry, or merge authority.

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
  `v2-call-01-4b-isolated-native-v2-provisional`. It exposes public `inspect`,
  pure readback classifiers, an injected one-attempt runtime, and
  `exercise-offline` against local JSON events. It has no HTTP client, token
  argument, live write command, or retry command.
- The runtime's native reader derives complete readback and transcript hashes
  from raw injected REST status, Link headers, and response bytes. It checks
  repository and branch refs, enumerates complete open-PR pages with a
  terminal probe, joins each PR detail and its listed node/repository identity,
  then rereads repository and branch refs. Protection reads also close over
  terminal branch and policy state. Singleton pagination, duplicate JSON
  members, nonfinite values, missing continuation, contradictory `last`,
  missing detail, duplicate PR IDs, moved refs, or changing snapshots refuse.
- Before a controlled POST/PUT it requires a complete absent prestate and a
  before-send attempt reservation. The offline CLI uses a local SQLite journal
  to show replay refusal across process restarts. A future installed runtime
  must use a separately qualified durable fence and native transport.
- PR readback requires two identical complete native census snapshots,
  repository identity, exact source and base branch revisions, one open
  nondraft unmerged PR, and its exact head/base repo/ref/SHA fields.
- Protection readback requires two identical complete snapshots, exact
  repository and branch, one required check with exact App ID, and explicit
  disabled force pushes and deletions. Provider write responses do not count
  as proof.
- Any incomplete, contradictory, duplicate, changed, malformed, or failed
  read yields `Unknown`. A lost response triggers readback without another
  send. Controlled provider exceptions and their chains are not surfaced.
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
branch-protection response semantics, including exact head/base, pagination,
force-push state, and credential capability. The injected fence and transport
must be replaced with qualified protected runtime components. No synthetic or
production effect is authorized by this draft.
