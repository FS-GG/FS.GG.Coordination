# Portable workspace qualification image

This directory defines the Linux amd64 candidate image used to qualify the fixed portable workspace operations.
The base image and fetched Node.js and TypeScript archives are pinned in `inputs.json`; the helper verifies every
pin before it permits a download or build. The candidate is retained by CI for 14 days as qualification evidence,
not published as a release or installed runtime.

The workflow uses one linear job with a 20-minute cap. Its static preflight runs immediately after checkout and
checks the clean exact Git revision, approved recipe and archive hashes, Linux amd64 rootless Podman capability,
the non-root user mapping and absence of privileged or host PID/UTS/user namespace overrides. Only a passing
preflight permits the pinned base pull, image build and operation run. A known-bad pin, dirty or wrong revision,
unsupported rootless mapping or isolation override fails before that costly work.

Declared graphroot, runroot and evidence state must be disjoint from each other, the source checkout
and global container stores. Parent traversal and symlink components are refused before state creation
or an engine command. Preflight also requires Podman's effective graphroot/runroot and VFS driver to
match the explicit request. These checks do not allocate a fresh store, prove ownership of another run,
prevent concurrent path substitution, establish disk backing or grant deletion authority. Provisioned
host allocation and independent cleanup remain separate prerequisites. Pure controls run with
`python3 tests/portable-workspace/image/test_storage_preflight.py` without invoking Podman.

No custom pipeline model is used. The job has no fan-out, retry, shared mutable cache, publication or conditional
admission state that would benefit from a state-machine model. Static validation directly covers the relevant
failure boundary and is expected to be reused on every image change. The implementation and validation effort is
capped at 20 minutes of runner time per attempt. Reassess if the workflow gains interacting jobs, evidence reuse,
retry or publication behavior.

The strict run mounts the exact checkout read-only at `/source`, provides only `/output` and `/tmp` as writable
storage, disables networking, drops capabilities, enables `no-new-privileges` and clears inherited environment.
Every operation must exit successfully and produce its exact reviewed output digest. The helper removes its scoped
container after every attempt; the workflow repeats scoped cleanup even on failure. Production executor acceptance
remains separate and requires its joined source and durable journal enforcement.


## Common-Arch source input checking (native execution disabled)

`eng/portable-workspace-image.py --profile common-arch check-inputs` extends this
existing helper with an inert input checker and fresh private staging. The default
profile is `portable-python`; its existing native invocations and output shape remain
unchanged. Common `preflight` and `qualify` refuse immediately after parsing, before
filesystem access or effects. Python rejects common-only flags and `check-inputs`.
Activation requires separate consumer guards and native qualification.

Supply `--source`, `--expected-source-revision` for the exact clean executing
Coordination checkout; `--image-source`, `--expected-image-source-revision` for the
exact clean `.github` recipe checkout; `--acquisition-dir` for the completed private
bundle; and `--expected-acquired-inputs-sha256` selected from reviewed authentic
acquisition evidence. The existing `--state-dir`, `--root`, `--runroot` and optional
`--podman` specify paths and fixed command data. No graphroot/runroot is allocated.
State/storage must be disjoint from both sources, acquisition and global stores.
Raw traversal/symlink components refuse before source checks.

The bundle contains `inputs.acquired.json`, `proof/` and `acquired/`. Only the pinned
`Containerfile`, `pacman.conf`, and exact `acquired/` population enter a newly created
mode0700 `<state>/common-arch-context`; an existing context refuses. Staging streams
no-follow one-link regular inputs, checks open/path consistency, copies with exclusive
files and verifies staged bytes again. Failed-copy cleanup checks that context device/inode still identifies the newly
created directory; substituted contexts are retained with cleanup uncertainty and the
original failure. Cleanup removes only the verified owned context. SHA256SUMS covers every declared payload once relative to `acquired/`.
The context digest covers sorted path/size/content-digest rows; proof and the lock
remain outside the context. Single-owner input custody is required: these checks do
not establish OS isolation against concurrent mutation.

Bounds: lock 8MiB; 32768 payload descriptors totaling at most8GiB; 4096 evidence
descriptors totaling at most64MiB; relative paths4096 UTF-8 bytes. Traversal is
entry-bounded and streaming hashing avoids buffering payload archives. These are
parser/staging limits, not runtime resource admission. Unknown fields, duplicate JSON
keys, unsafe paths, aliases, special/hardlinked files, missing/extra population,
size/digest drift, unsigned/unbound records and inventory correspondence failures
refuse. Inventory is canonical sorted `pacman -Q` text and includes unchanged donor
packages; archive additions alone cannot stand in for final inventory.

Stdout contains only `profile`, `helperSourceRevision`, `imageSourceRevision`,
`acquiredInputsSha256`, `context`, `contextSha256`, `buildCommands`, and
`nativeExecutionEnabled: false`. Commands are fixed offline Podman VFS build/save
arrays, never executed here. This is ordinary JSON, with no receipt schema,
qualification outcome, image digest, operation identity or permission.

Byte equality and structural joins cannot authenticate arbitrary historical logs,
prove signature trust, successful hooks or pacman resolver semantics. The externally
selected genuine lock digest must come from the acquisition owner's reviewed evidence
and custody. Native activation separately replays the trusted complete offline
transaction against a fresh exact donor. There is no authentic acquired lock supplied
by source tests. `test_common_arch_inputs.py` uses visibly SYNTHETIC temporary fixtures;
its passing controls prove input/staging/refusal behavior only, never native readiness.

The exact reserved `acquiredInputs` interface follows. Source declaration fields and
historical pending trust/unresolved text stay unchanged outside classification and
this slot.

# Exact acquiredInputs extension for the source-only helper

This is the finite field contract for the reserved `acquiredInputs` slot described in helper-window.md. It is documentation for that existing input structure, not another schema/receipt family. All keys listed below are required; unknown keys refuse. Source fields outside `classification`/`acquiredInputs` equal the pinned source specification. There is no authentic acquired instance yet.

Primitive rules: `sha` is 64 lowercase hex; `oci` is `sha256:` plus `sha`; `revision` is 40 lowercase hex; `bytes` is a nonnegative JSON integer excluding booleans. `ref` is a key in evidence. Evidence IDs use `[a-z0-9][a-z0-9-]{0,63}`. Strings are bounded, nonempty, without control characters; package versions are opaque non-whitespace strings, not parsed with a new dependency solver.

```text
acquiredInputs = {
  imageSource: {
    repository, revision, tree,
    containerfileSha256, pacmanConfigSha256, inputsSha256
  },
  donor: {
    reference, indexDigest, configDigest,
    layers: [{sha256, bytes}], evidenceRefs: [ref]
  },
  snapshot: {
    date, acquisitionServer, repositories: ["core", "extra"],
    databases: [
      {repository, path, bytes, sha256,
       signature: {availability, carrierPath, sha256, evidenceRef},
       evidenceRef}
    ]
  },
  trust: {
    pacmanVersion, libalpmVersion, keyringPackageVersion,
    donorPacmanConfigSha256, acquisitionConfigSha256, finalConfigSha256,
    donorEffectiveSigLevel, donorEffectiveLocalFileSigLevel,
    finalEffectiveSigLevel, finalEffectiveLocalFileSigLevel,
    donorPolicyEvidenceRef, keyringEvidenceRef, reconciliationEvidenceRef
  },
  transaction: {
    method: "pacman-full-system", targets: [source targets in order],
    resolutionEvidenceRef, downloadEvidenceRef,
    offlineTransactionEvidenceRef, hookEvidenceRef,
    beforeInventory: {path, bytes, sha256, evidenceRef},
    finalInventory: {path, bytes, sha256},
    providers: [{observation, evidenceRef}],
    replacements: [{removedName, installedName, evidenceRef}],
    removals: [{name, evidenceRef}]
  },
  packages: [
    {repository, name, version, architecture, path, bytes, sha256,
     signature: {kind, carrierPath, sha256, signerFingerprint, evidenceRef},
     transactionEvidenceRef}
  ],
  files: [{path, bytes, sha256, role}],
  checksumsSha256,
  evidence: {id: {path, bytes, sha256}}
}
```

`imageSource.repository` is `FS-GG/.github`; source revision/tree match the selected clean recipe checkout. Donor fields match source exactly. Layer array preserves source order; at least one authentic donor evidence ref is required. Snapshot has exactly one core and one extra database record. All payload descriptor paths start `acquired/`; checksum rows omit that prefix. Proof paths start `proof/` and never enter build context.

Database signature `availability` is `present` or `absent-observed`. If present, carrierPath is the staged `<repo>.db.sig` and sha256 equals its file descriptor. If absent-observed, carrierPath and sha256 are JSON null, with required evidenceRef preserving the actual absence observation; this is permitted only under reconciled DatabaseOptional. No unknown/unobserved signature availability passes. Both database bytes and the signature observation have genuine evidence refs.

For this finite source selection, final policy strings must equal the source's `Required DatabaseOptional` and `Required` values exactly. Donor *effective* policy strings must equal those values too; unsupported/different/stricter donor policy is an explicit mismatch requiring reviewed source reconciliation, not an attempted generic policy-strength comparison. These fields represent externally observed effective policy, including overrides, rather than a naive grep of pacman.conf. Config hashes and referenced evidence are checked as bytes; authenticity/effective-policy correctness remain acquisition owner/root responsibilities.

`transaction.beforeInventory.path` is a proof file; its descriptor must equal the referenced evidence descriptor. `finalInventory.path` is exactly `acquired/package-inventory.expected` and joins one inventory files record. Both inventories are canonical `LC_ALL=C pacman -Q | LC_ALL=C sort` text, terminal newline, unique package names, one space between name and opaque version, bounded by file/input limits. Every donor package must remain in final inventory unless explicitly named in removals or the removed side of replacements; every package archive name/version must appear in final inventory. Every final package must be accounted for by donor inventory or one staged package. Removals/replaced names must exist before and be absent after; replacement installed names must exist after. Duplicates or conflicting removals/replacements refuse. This is set correspondence, not an independent dependency/transaction solver.

Providers carry bounded human-readable observations plus authentic evidence references; an empty providers/removals/replacements list still has the required resolution and offline-transaction evidence. Hook evidence is required even when its authentic observation says no hooks ran. Do not infer successful hooks from absence of a log. The pure checker does not recognize arbitrary log syntax or certify transaction success; the accepted externally pinned lock and native offline replay supply that boundary.

Package architecture is `x86_64` or `any`, repository core or extra. Package paths end `.pkg.tar.zst`; one archive descriptor per package identity and one package record per archive descriptor. Package signature `kind` is `detached` or `database-embedded`. Detached carrierPath ends `.pkg.tar.zst.sig` and joins the package-signature file descriptor. Database-embedded carrierPath is that repository's staged database path; signature sha256 identifies the actual signature bytes as retained in its proof, and signature evidence must bind them to this archive and signer. Fingerprints are 40 or 64 uppercase hexadecimal characters. Trusted signature verification is **not** established by matching fingerprint syntax. No new crypto/trust engine is implemented here; package manager verification still happens in the admitted offline transaction.

Files roles are the five enumerated in helper-window.md; no `SHA256SUMS` self-row. Distinct DB/package/signature/inventory descriptors must join exactly all staged payload files; no dangling metadata and no unclassified extra bytes. Signature files are optional only according to the explicit detached/embedded representation and policy. Source `SHA256SUMS` coverage remains exact. Evidence descriptors are all referenced, hash/size verified and bounded; a caller cannot substitute remote URLs or absolute filesystem paths.

The metadata-first donor descriptor prepared by v3_env is only a proposed operation and does not fill this slot. Its eventual manifest observation can contribute one donor evidence ref, but cannot satisfy acquired config/layers, package trust or transaction refs. Full package acquisition remains separate after authentic expanded-size/capacity bounds exist. No field above manufactures an old operation identity or grants a new effect.
