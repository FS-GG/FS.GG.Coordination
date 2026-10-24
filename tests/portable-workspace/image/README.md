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


## Closed SC2 image profile

`eng/portable-workspace-image.py` also owns `--profile sc2-headless`, implemented
by its internal `portable_workspace_sc2.py` module. It has no second executable
entrypoint. This source preparation does not qualify the current native engines,
image, product journeys, hard containment or cleanup after loss of the caller.
The historical image and small Buildah pilot are not current qualification.
Shared storage preflight rejects known global-store scope before filesystem
observation, then retains symlink, resolved-alias and disjointness checks. The
first hosted aggregate exposed Python 3.12 raising PermissionError for those
forbidden paths before the intended refusal; the corrected source does not need
permission to inspect a store it already refuses to use.

The externally selected, private `--operation-input` and
`--expected-operation-input-sha256` bind exact helper/product revisions, trees and
file hashes, support binaries/libraries, pre-provisioned allocation/configuration
objects, topology/resource-disposition references, explicit license acceptance and
one original boot/monotonic interval. That correspondence is not permission to run.
Actual product base digest, local tag and OCI export name are also restricted
selection data. The helper permits only a digest-pinned Microsoft .NET SDK base
which independently matches the hash-bound recipe's two-stage FROM structure;
the selected output tag is local-store-only. The native builder must report the
supported1.43.1 family in addition to matching its externally selected file pin.
Private source revisions, pins and admission evidence stay in the restricted
selection; public controls use independently committed synthetic repositories.
The internal implementation is compiled from bounded regular source bytes,
bypassing bytecode cache reads and writes. Its captured source digest must match
the external selection before dispatch; source checks do not run cached code first.
Unknown/duplicate fields, other-profile acquisition flags, external Git pointers,
dirty/ignored output, source substitutions and changed config/support objects
refuse. The complete tracked product tree is independently checked, including
Git's assume-unchanged blind spot; context directory tokens fence later entry
changes. These are observations under single-owner custody, not immutable mounts.

`check-inputs` is inert: fixed local Git reads and file/configuration checks, no
engine, acquisition, state reservation or build. It returns the actual proposed
argv and input object identity. `preflight` and `qualify` consume an exclusive
operation directory and original journal. Reusing an operation refuses. Native
preflight checks resource disposition, four-CPU affinity, exact separate VFS stores,
rootless platform and current user/group mapping before dependent work. Buildah
pulls the pinned base once (retry0), builds the maintained licensed headless target,
retains the actual image ID and exports once. Only the fully verified, rehashed
same OCI object can cross to the designated Podman load store. Loaded config,
rootfs diffIDs and the selected local tag must match. No product container is run.

All commands, sampling helpers and archive/hash loops share the original
3600-second work aperture plus120-second retirement aperture. Phase limits are
clipped; none renew the clock. The controller samples scoped RSS and whole logical
disk every15 seconds, refuses observation gaps above60 seconds, and retains the
4-CPU affinity/12GiB RSS/80GiB population policy. Affinity and sampled RSS are not
kernel quotas. Disk accounting includes the allocation's context, build store,
archive and evidence plus the disjoint designated load store, each path once.
Both storage filesystems need capacity. Shared-host reservations are charged at
the shared host; a separately hash-bound topology relation is required before
omitting those reservations from the inner-cgroup headroom calculation. Unknown
relevant conflicts refuse. This never settles another operation's reservation.

The reserved-storage alternative binds one fresh, host-protected, fully
preallocated HDD sibling ext4 filesystem. Its exact mounted root `R` contains
`R/allocation` and the separate `R/current-store`; the allocation retains the
existing closed context, state, build, runroot and namespace layout. Both stores,
downloads, archive and operation evidence share this finite filesystem. A genuine
bind of its whole filesystem root into the selected container is permitted;
missing mounts, a directory beneath the mount, subdirectory roots, stacked or
nested mounts, and overlay/tmpfs fallback are not equivalent. Existing stores
and their images remain untouched. Loading into an older store is a separate
effect, outside this profile.

This closed profile uses `R=/var/tmp/fsgg-sc2-reserved/<32-lowercase-hex-operation>`
and a 256 GiB backing, 4096-byte filesystem blocks and 2,097,152 requested inodes.
The owner verifies at least 208 GiB available and 1,048,576 free inodes after
formatting; the receipt binds actual filesystem totals. The helper keeps the
runtime 80 GiB free-byte floor and exactly 1,048,576 free-inode floor. Setup uses
no reserved-block percentage, discard or lazy inode/journal initialization. Its
separate accepted owner evidence covers the format profile, two preallocated
1 MiB control/error files and bounded host metadata outside the guest filesystem.
These source values select a finite budget; they do not establish a reservation
or permission to format anything.

The supplying owner must establish actual backing allocation, protected object
custody, mount correspondence and retention through explicit disposition. An
externally selected receipt digest binds those accepted facts; parsing a receipt
does not authenticate its author or grant setup or execution authority. The guest
checks its observable directory, mount namespace, filesystem and capacity facts
again at determining boundaries. It cannot independently establish host backing
custody from a guest mount label. The selected owner excludes backing truncation,
hole punching, discard/trim, resizing and automatic reclamation. Partial setup,
expiry or caller loss retains reached objects and outputs; there is no positive
cleanup or expansion branch.

The original 80 GiB combined logical ceiling and 80 GiB free-space admission floor
both remain. Two paths on this filesystem share one capacity, not two reserves.
The supplying owner selects a finite backing and inode population plus a positive
free-inode floor before setup. This is a bounded attempt, not a guarantee of image
fit: ENOSPC, inode exhaustion and sampled excess remain failures with their original
first cause and reached artifacts retained. No limit reduction, expansion or
automatic retry follows. Shared physical memory and CPU reservations remain
charged. Only disk conflicts proven outside the selected protected filesystem
may be retained as excluded from its disk accounting; unresolved conflicts that
can consume or reclaim that reservation still refuse.

The existing admission object adds `storageReservation: {path, sha256}` only for
this alternative, joined by `resourceDomain.storageReservationSha256`. The exact
`fsgg.sc2.reserved-storage/1` receipt binds the operation/backend, guest root and
identity, mount namespace/ID/device/root/type/flags, filesystem UUID/totals/inode
floor, backing identity/size/allocation/evidence digest and owner retention
evidence. Host-only fields remain externally authenticated facts. The associated
resource disposition keeps shared-memory/CPU amounts and the relevant unknown
conflict list, replacing the legacy disk amount with
`diskDomain: {reservationSha256, reservedBytes, retainedOutsideDiskConflicts}`.
Each retained outside entry has its original `id`, host `sourceDevice` and
accepted `evidenceSha256`; matching either the reserved filesystem or its parent
backing filesystem refuses.
This is a disk-only exclusion in the accepted operation, not physical zero,
settlement or a way to ignore a relevant writer. The legacy exact four-field
disposition and designated store continue unchanged when no reservation is bound.

Finite source bounds are8MiB combined raw command output across the operation,
32MiB retained metadata,128KiB per record/result,2048 records,512 commands and512
sampled processes (bounded8192-entry visible process scan). Source checking bounds
8192 tracked leaves/512MiB total,256MiB per leaf and16384 context entries. OCI
policy bounds are16GiB archive,32GiB cumulative expanded bytes,16GiB per layer,
128 layers,200000 layer headers,4096-byte paths and1MiB JSON/extension records.
Streaming tar readers discard Python's header cache, charge cumulative path and
derived-parent metadata against64MiB, bound nested extensions to16, and refuse
global PAX/sparse expansion before stdlib parsing can retain/expand it. No file
content is retained merely for its hash. These finite object/byte bounds are
source safeguards; current native output, time and memory fit remain unproved.

The SC2-only virtual layer policy additionally represents a zero-size FIFO only
at the bounded `tmp/clr-debug-pipe-<number>-<number>-(in|out)` name with exact
type6/mode0700/UID0/GID0/empty link/device0:0 metadata. Its numeric name is not
process identity. It is counted but never opened, extracted or assigned a file
digest. Other special types and different metadata refuse. Ordinary layer
replacement and whiteout rules retain the new type; aliases to/traversing a FIFO
and hardlinks targeting one refuse. Required source/input evidence remains an
effective regular file plus its exact digest. The outer archive admits only
regular JSON/blob members. Neither earlier profile receives this exception.

Intent precedes Popen. Actual PID/kernel birth, session/group/cgroup, available
namespace observations, raw partial streams, EOF and wait are distinct records.
The consumed operation-directory entry and journal entry are fsynced before
the first intent; each intent is then fsynced before launch. Sync failure blocks
launch and retains the consumed directory. Ordered fsync controls establish this
source contract, not power-loss recovery or storage-device guarantees.
Very short commands can lose namespace links before waitpid reports exit; that
observation gap is retained, never invented as namespace proof. Kernel birth
still binds the actual direct child. Failed calls retain their first cause and
unknown effects; only that retained birth permits direct-child signalling.
There is no command-line matching, global prune, foreign-container removal or
new deadline for cleanup. Native container censuses are retained even when they
refuse; unowned identities remain obligations. A leader exit or an empty named
container census never proves escaped-descendant settlement. Image assertions,
retirement gaps and reporting outcomes remain separate in the existing journal
and canonical manifest route.

Run the inert actual-caller controls with
`python3 tests/portable-workspace/image/test_sc2_profile.py`. They route real
check-inputs/preflight/qualify through synthetic repositories and controlled
native Popen responses while the shared capture drains genuine small Python
children. They do not invoke Buildah/Podman or establish native readiness.
The selected linear qualification uses static/actual-caller preflight, not a new
pipeline model or framework. Existing required repository CI remains required;
the SC2 native capability and full-image operation need separate admission.
Even path-filter or inert-step workflow edits require refreshing the exact
current execution-pin extension digests. The full document/reference census
complements the existing strict hosted validator; unchanged Action references
do not make changed workflow bytes equivalent to their registered digest.

## Common-Arch source input checking

`eng/portable-workspace-image.py --profile common-arch check-inputs` extends this
existing helper with an inert input checker and fresh private staging. The default
profile is `portable-python`; its existing native invocations and output shape remain
unchanged. Reviewed common `preflight` and `qualify` source dispatch requires all
exact input identities plus a closed `--engine-allocation`; missing prerequisites
refuse before filesystem access or effects. Python rejects common-only flags and
`check-inputs`. Operational launch requires separate admission and native qualification.

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

For this finite immutable donor, the selected policy tuple is donor global
`Required DatabaseOptional`, donor local `Optional`, final global
`Required DatabaseOptional`, final local `Required`. Source candidate policy remains
`Required DatabaseOptional` / `Required`. The global spelling maps to the observed
PackageRequired+PackageTrustedOnly+DatabaseOptional+DatabaseTrustedOnly tokens;
donor local Optional maps to PackageOptional+PackageTrustedOnly; candidate local
Required maps to PackageRequired+PackageTrustedOnly under the exact selected config
bytes. Local Optional→Required tightens signature requirements. Repository `file://`
transport remains governed by global/repository SigLevel and is distinct from
local-file upgrade policy. This is one fixed correspondence, not a policy normalizer
or generic strength comparison. Donor local falsely Required, unknown/weakened/
stricter tuples, TrustAll and repository overrides all refuse.

The guard additionally pins donor reference
`docker.io/library/archlinux@sha256:363129b67b2bc6c5a634f8fb4961a7e2eba213a2ec075d159127b1260290a722`
and config `sha256:5a30b4acc83ff841682eb21795e00f7ff030796f5e6fd2a127decd7a45ba7cef`.
`donorPacmanConfigSha256` must be
`1b1c6195b21865a660480f41a29a172f103b1aa3e846d1db877e04cd6220e454`.
The existing `donorPolicyEvidenceRef` descriptor must identify 1751 bytes with SHA256
`60d18150be3245f6df3e53a6a301380e9eb5cab17047d5112b6709eedaf9d0ac`, and its actual
retained proof bytes are independently rehashed. `acquisitionConfigSha256` must be
`513b2a7cb4de9afdbaf275d880b7a077bdb96c3975f4eac50ae9bcfd4d0c1cc9`; the final config
retains the existing COMMON_PINS pacman.conf value. Full donor manifest/index/layer
joins remain unchanged. No new lock fields or configuration flags are introduced.

The acquisition config is a selected future input, with no acquisition transaction
acceptance implied. `reconciliationEvidenceRef` and `keyringEvidenceRef` remain
required. Their authentic interpretation, whole-lock selection and custody remain
acquisition owner/root responsibilities. Source controls patch only expected effective
proof digest/size to visibly synthetic bytes for inert success; an unpatched control
proves those synthetic bytes cannot pass the production pins. Genuine donor policy
readback establishes this narrow source reconciliation, never complete package
acquisition, transaction/hooks success or common native readiness.

`transaction.beforeInventory.path` is a proof file; its descriptor must equal the referenced evidence descriptor. `finalInventory.path` is exactly `acquired/package-inventory.expected` and joins one inventory files record. Both inventories are canonical `LC_ALL=C pacman -Q | LC_ALL=C sort` text, terminal newline, unique package names, one space between name and opaque version, bounded by file/input limits. Every donor package must remain in final inventory unless explicitly named in removals or the removed side of replacements; every package archive name/version must appear in final inventory. Every final package must be accounted for by donor inventory or one staged package. Removals/replaced names must exist before and be absent after; replacement installed names must exist after. Duplicates or conflicting removals/replacements refuse. This is set correspondence, not an independent dependency/transaction solver.

Providers carry bounded human-readable observations plus authentic evidence references; an empty providers/removals/replacements list still has the required resolution and offline-transaction evidence. Hook evidence is required even when its authentic observation says no hooks ran. Do not infer successful hooks from absence of a log. The pure checker does not recognize arbitrary log syntax or certify transaction success; the accepted externally pinned lock and native offline replay supply that boundary.

Package architecture is `x86_64` or `any`, repository core or extra. Package paths end `.pkg.tar.zst`; one archive descriptor per package identity and one package record per archive descriptor. Package signature `kind` is `detached` or `database-embedded`. Detached carrierPath ends `.pkg.tar.zst.sig` and joins the package-signature file descriptor. Database-embedded carrierPath is that repository's staged database path; signature sha256 identifies the actual signature bytes as retained in its proof, and signature evidence must bind them to this archive and signer. Fingerprints are 40 or 64 uppercase hexadecimal characters. Trusted signature verification is **not** established by matching fingerprint syntax. No new crypto/trust engine is implemented here; package manager verification still happens in the admitted offline transaction.

Files roles are the five enumerated in helper-window.md; no `SHA256SUMS` self-row. Distinct DB/package/signature/inventory descriptors must join exactly all staged payload files; no dangling metadata and no unclassified extra bytes. Signature files are optional only according to the explicit detached/embedded representation and policy. Source `SHA256SUMS` coverage remains exact. Evidence descriptors are all referenced, hash/size verified and bounded; a caller cannot substitute remote URLs or absolute filesystem paths.

The metadata-first donor descriptor prepared by v3_env is only a proposed operation and does not fill this slot. Its eventual manifest observation can contribute one donor evidence ref, but cannot satisfy acquired config/layers, package trust or transaction refs. Full package acquisition remains separate after authentic expanded-size/capacity bounds exist. No field above manufactures an old operation identity or grants a new effect.


## Python consumer profile fences

The maintained executor qualification, installed Python hello preparation and portable
release helpers accept explicit `portable-python` or authentic legacy profile absence
only with the established Python recipe/input/image identity. Exact Containerfile
SHA256 is `41f2ae90901851254556d87a85e17011b816e4f4847103376f213f651ef537b4`;
input manifest SHA256 is `c5ef4bb365b37c9213015166758e0fd62bd9230d2bde51c2f2d265ae21f66065`;
image name is `localhost/fsgg-portable-workspace:python-3.14.0-node-24.8.0-ts-5.9.2`,
with a lowercase64hex digest reference under that exact name. Explicit common,
unknown, null/nonstring profiles and absent/unpinned future manifests refuse.
A source revision/tree label or a relabeled common recipe cannot replace these pins.
Existing manifest/OCI/qualification/authentication correspondence remains required;
the finite profile predicate adds no independent trust or execution authority.

Executor checks the manifest before creating any state, store or evidence directories.
Installed preparation checks it before package identity/extraction or OCI scanning.
Release checks it before Git processes/package/archive operations and output creation.
The existing image test entrypoint verifies executor ordering and pins; its
`run_executor_profile_controls(dotnet)` exercises the actual F# consumer with inert
executable sentinels under a separately admitted CLR/Execution build closure. Existing
installed/release fixtures test actual entrypoints and preserve legacy and explicit
Python acceptance, with synthetic receipt identity clearly distinguished from real
image qualification. Pure source assertions do not replace F# compilation or actual
F# caller controls. These reader guards remain unchanged when reviewed common
image-helper dispatch is enabled; they do not accept common consumer images.


Authentic Arch epoch versions can contain a literal colon in package archive and
signature basenames (for example `conmon-1:2.2.1-1-x86_64.pkg.tar.zst`). The restricted
repository basename alphabet is `[A-Za-z0-9_+.:-]+`, matching the reviewed recipe
checksum guard; traversal, separators, controls and unsupported filename characters
remain refused. Version strings stay opaque. Package/database bytes and filenames
are preserved exactly; no package rename or custom version resolver is introduced.
The reviewed common Containerfile pin is
`cde44e5b391adf6bf6c9ae3d98d64869f7e1d352143c25b00f5fd5bb23a15576`.
The filename correction alone does not establish native execution or acquired
lock/transaction authenticity. Original failed checks remain in owner evidence.


## Common native profile

The maintained `preflight` and `qualify` modes contain reviewed common-Arch source
flow with `COMMON_NATIVE_DISPATCH_ENABLED = True`. Source dispatch acceptance does
not admit a native operation. Launch still requires an independently admitted
operation with actual capacity, input authenticity, custody and teardown.
The three Python consumers retain their qualified guards and refuse common images.

Common native preparation requires `--engine-allocation` and an explicit closed
Podman prefix. Build and save commands append literal command tokens to the whole
prefix. No fixed token slicing, caller command override, host-network fallback,
network acquisition or guessed Buildah cleanup is used. The finite owned configuration
uses ten configured default capabilities, excluding NET_BIND_SERVICE, empty crun
runtime flags and environment arrays, no proxy or host environment injection, VFS
storage, empty registry search/authentication/connections, and empty hooks/mounts.
Unknown configuration keys and environment variables refuse. The build requests
rootless isolation, network none, no-new-privileges and only NET_BIND_SERVICE removal;
Buildah's drop-all handling would defeat capability additions and is not used here.
Actual runtime-effective properties require native evidence.

Preflight binds the literal original donor reference363129… to config5a30…, its actual
RepoDigests/platform, reviewed Podman6.1.3/Buildah1.45.1 and effective storage. An OCI
annotation or recompressed donor export does not supply that local reference binding.
Clean source revisions, context bytes and configuration hashes are rechecked before
build, cases and export. These checks complement single-owner custody; they do not
prevent concurrent filesystem substitution. Build failure retains bounded diagnostics
and external-container observation for the allocation owner's teardown. Case cleanup
requires the actual CID, run label and image identity; reporting failures cannot replace
the original failure. Unknown create effects remain unconfirmed and are not removed by
proposed name.

The finite cases compare the complete pacman inventory to acquired and retained bytes,
check the nonroot user/home/workdir and absence of engine/private-key state, perform
offline Python file/hash work and run passive packaged Git/Podman version commands.
They do not establish nested-engine, CLR, product or consumer readiness. Existing
receipt families distinguish built manifest digest, exported manifest digest and config
identity. Fresh reload and publication remain separate qualifications.

The common OCI scanner validates compressed descriptors and uncompressed diffIDs,
then applies layer whiteouts and opaque directories before checking the effective
filesystem. It never extracts to the host. Header, metadata, expanded bytes and effective
population are bounded; hardlink content provenance is bound in tar order before later
same-layer or cross-layer replacements. Forward hardlinks refuse.
The donor's zero-byte legacy secring and empty private-key directories may occur in
ancestors but must be absent from the final filesystem. Nonzero ancestor private-key
payloads and aliases are refused even if later whiteouted. This scoped check makes no
broader secret-discovery claim. Synthetic transport intercepts every native call;
`test_common_arch_inputs.py` supplies source controls, not native evidence.

The common inputs pin is `53c30494e1098af88d7efb78b3b0b80f7bee7894df6b6a902a82e40f3b8da8ce`,
aligning the descriptive framework preparation with Coordination's pinned SDK400.
Prior source/lock proof remains retained; current source identity needs a genuine rebind.

Common calls explicitly select the bounded branch of the existing subprocess `run`
function: at most 8MiB combined stdout/stderr is captured while reading, with a finite
per-command deadline. Overflow and timeout retain their original cause and bounded
diagnostic tails. Only the owned direct child is terminated/reaped (one second for
TERM, then one second for KILL/wait); descendant and Buildah/container effects remain
unconfirmed until the allocation owner observes and tears down the exact population.
No process-group cleanup or allocation retirement is inferred. Portable-Python keeps
its existing `subprocess.run(capture_output=True)` behavior. Actual native operation
still needs separately admitted whole-operation time, memory, disk and output limits.
Owned ordinary-process controls test exact-bound success, combined-output overflow,
TERM-ignoring timeout, reaping and reporting failure; they launch no native engine.


## Common engine scratch backing

Common native modes accept optional `--engine-scratch-dir`. It changes only the
closed environment's `TMPDIR`, preserving the engine allocation, full Podman prefix,
libpod `--tmpdir`, graphroot/runroot and configuration. The override must be an existing
empty directory owned by the invoking uid with mode0700 on tmpfs, disjoint from sources,
acquisition, state, storage and other engine namespace directories. Preflight records
its device/inode identity and rechecks it before later effects. The helper creates,
mounts and deletes no scratch allocation; allocation custody remains external.
Without this flag, the existing `allocation/download-tmp` behavior is preserved.
Portable-Python rejects the flag before I/O.

The supported backing constraint comes from pinned upstream source: Buildah1.45.1
[creates its context overlay before recipe execution](https://github.com/containers/podman/blob/v6.1.3/vendor/go.podman.io/buildah/imagebuildah/build.go),
including with VFS image storage. Its [upper/work scaffolding](https://github.com/containers/podman/blob/v6.1.3/vendor/go.podman.io/buildah/imagebuildah/build_linux.go)
uses [TMPDIR first](https://github.com/containers/podman/blob/v6.1.3/vendor/go.podman.io/buildah/internal/tmpdir/tmpdir.go).
The immutable build context remains the lower directory; it need not be copied to
tmpfs. [Linux OverlayFS documentation](https://www.kernel.org/doc/html/v6.5/filesystems/overlayfs.html)
allows another overlay filesystem as lower and requires upper/work filesystem support.
Actual kernel acceptance remains native qualification; the source guard does not prove it.

Keep the libpod temporary path stable for a retained store: Podman6.1.3
[persists and validates that path](https://github.com/containers/podman/blob/v6.1.3/libpod/sqlite_state.go).
Changing the whole engine namespace would also change it. No database reset or rewrite
is a scratch repair. TMPDIR additionally governs
[temporary image-copy storage](https://github.com/containers/podman/blob/v6.1.3/vendor/go.podman.io/common/pkg/config/config.go)
and [full OCI layout staging before archive creation](https://github.com/containers/podman/blob/v6.1.3/vendor/go.podman.io/image/v5/oci/archive/oci_dest.go).
Account scratch pages in memory as well as disk/inode capacity; actor RSS alone excludes
those tmpfs charges. Neither a fixed tmpfs size nor an empty scaffold proves enough
capacity for export. Native admission retains whole-operation limits, exact teardown
and original failures. Controls exercise the actual production path against a small
owned tmpfs directory; intercepted engine transport supplies no native acceptance.

## Closed BAR image profile

Issue [1032](https://github.com/FS-GG/FS.GG.Coordination/issues/1032) owns the
`--profile bar-headless` addition to the same entrypoint. It builds the complete
`headless-integration` target from the accepted FSBar recipe, verifies an OCI
export and loads that exact image into a fresh, separate operation store. It has
no arbitrary command, image-runtime, native-game or old-attempt resume mode.
FSBar's six recipe files remain product-owned inputs; the current prerequisite
is the controller profile, not a newly asserted recipe defect.

The private operation document uses schema `fsgg.bar-headless-operation/1` and
requires exactly `schema`, `operationId`, `helper`, `product`, `allocation`,
`build`, `load`, `support`, `clock`, `admission`, `resourceDomain`, and
`licenseAccepted`. It follows the existing closed SC2 field shapes, with these
BAR constraints:

- The helper revision/tree and all three executing local source files are pinned:
  `eng/portable-workspace-image.py`, `eng/portable_workspace_bar.py`, and
  `eng/portable_workspace_sc2.py`. The unchanged SC2 module supplies process,
  deadline, capture, custody and sampling primitives; the entrypoint supplies
  the bounded subprocess and OCI reader. There are no other local execution
  imports. Both internal modules are captured from bounded regular source without
  consulting bytecode caches, and their captured hashes are checked. Native
  executable/library pins remain required separately.
- Product revision/tree and the complete tracked product context are checked.
  The six individually pinned files are `Containerfile`, `resources.json`,
  `build-image.py`, `entrypoint.sh`, `test_source.py`, and `README.md` below
  `containers/barc-stock-headless/`. The exact approved Ubuntu base digest and
  three-stage FROM structure must match; only a local output tag is accepted.
- Allocation contains `context`, `state`, `build/graphroot`, `build/runroot`, and
  `load/runroot`, plus the existing private namespace configuration directories.
  Load storage is exactly `$HOME/.local/share/fsgg/headless-images/bar/<operationId>`.
  Both image roots and both runroots must initially be empty. Native census refuses foreign
  containers and requires an empty BAR load-image population before acquisition
  and again before loading. An existing operation directory cannot be consumed
  a second time. No cleanup removes another generation or prunes a global store.
- BAR has no `storageReservation` extension or disk-budget exclusion. The same
  original boot/monotonic clock provides 3600 seconds of work and 120 seconds
  for retirement. Existing admission still requires four CPUs, 12 GiB memory
  reservation plus 2 GiB headroom, quantified relevant shared reservations,
  sufficient enclosing capacity/PIDs, and 80 GiB free on both selected stores.
  Unknown resource conflicts refuse the operation. Four-CPU affinity, one build
  job, an 80 GiB sampled storage ceiling and 12 GiB observed process RSS ceiling
  retain their existing scope; these are not hard per-operation containment or
  measured fit guarantees.

Fixed commands pull the approved digest, build with no retry and no layer cache,
export the actual image ID, verify the archive, load by that verified archive,
then compare loaded image ID/config/rootfs and read back the selected local tag.
The supported Buildah family remains 1.43.1. Preflight also requires the final
UID/GID10001 mapping. The final image must have Linux amd64, user10001:10001,
working directory `/work` and entrypoint `bash /opt/barc/entrypoint.sh`; its CMD
is empty because that final ENTRYPOINT resets the development stage's CMD.
See the [Dockerfile CMD/ENTRYPOINT rules](https://docs.docker.com/reference/dockerfile/#understand-how-cmd-and-entrypoint-interact).
No product container is started by this profile.

The streaming verifier requires the exact recipe/resources/entrypoint, selected
headless engine binary, map and game SDP hashes. It additionally requires and
records the actual hashes and sizes of the nonempty plugin, plugin hash manifest,
source manifest, toolchain package/status/hash manifests and maintained HighBar
launcher. These are output identities, not invented expected plugin bytes. The
image recipe's pinned build performs its own complete engine/game acquisition,
compiled controls and dependency checks. The verifier retains the existing
16 GiB archive, 32 GiB expanded, 16 GiB per-layer, 200000-header, 64 MiB path
metadata and 1 MiB JSON bounds. BAR allows no CLR FIFO metadata exception.
The BAR policy also requires the produced `/opt/barc/STATIC-ELF.json`. Only this
closed manifest path is retained as bounded content by the streaming OCI reader;
other profiles retain their existing behavior. Verification occurs after every
layer's whiteouts, replacements and aliases have been applied and before load.
It checks the versioned schema, selected roots and expected engine digest, all
declared file hashes/sizes, recursive needed/interpreter edges, the producer's
closed candidate-directory lookup and exact alias population against that
effective filesystem. Missing, malformed, oversized or changed evidence refuses
load. The result retains the actual manifest hash and scoped correspondence.

This reads back the pinned recipe's produced static metadata; it does not reparse
ELF bytes, reproduce dynamic-loader ordering, or prove symbols, versions, dlopen
populations or loaded ABI. `loadedAbiAccepted` remains false. Bounds include256
files/512MiB declared file bytes,8192 aliases,128 dependencies/129 edges per file
and the existing1MiB JSON limit. Validation loops call the original operation's
observer; they neither renew its clock nor suppress its resource/deadline failure.
The image/runtime, authentic old-writer/fence and ordinary BAR storage conditions
remain independent prerequisites. Synthetic archive/control success is not an
actual image or resource acceptance.
The original 8 MiB aggregate command capture, 32 MiB metadata, 2048 journal
records and 512 command bounds remain shared across phases, including failure
retention. Archive mutation or failed verification blocks load; first failure,
direct-child settlement and remaining retirement uncertainty stay distinct.

### Hosted-VM resource domain

[FS-GG/.github#4778](https://github.com/FS-GG/.github/issues/4778#issuecomment-6359585393)
selected route (b): each BAR image operation runs on one fresh GitHub-hosted
`ubuntu-24.04` VM. The [capacity probe](https://github.com/FS-GG/.github/issues/4778#issuecomment-6363750262)
showed this route is feasible on the measured VM. `resourceDomain.relation` therefore admits a second closed
value, `dedicated-ephemeral-hosted-vm`. That relation additionally requires
`resourceDomain.domainEvidenceSha256` and `admission.domain`, and the two must correspond.

- **Evidence.** The admission document is root-produced `fsgg.bar-hosted-domain/1` evidence from
  `eng/bar_hosted_domain.py admit`, with verdict `admitted` and no refusals. It records:
  - the operation's own boot ID, which must equal `clock.bootId`, so evidence from another VM or boot refuses;
  - the `ubuntu24` GitHub-hosted runner at the explicitly selected image version `20261012.334.1`. Image drift refuses until source selects the new version;
  - `containerd.service`, `docker.service`, `docker.socket` and `fstrim.timer` stopped, with no daemon processes left (a periodic trim would punch the loop backing sparse);
  - rootless user namespaces allowed;
  - one ext4 operation volume, formatted `nodiscard`. It is the mounted loop device's actual backing file (offset 0, no size limit) on a separate device, and that file still holds at least its full size after `mkfs`;
  - Buildah 1.43.1 in the userland;
  - at least 14 GiB MemAvailable and four CPUs.
- **Reader checks.** The BAR reader requires the allocation and the load store to be on that volume's device. It also requires a resource disposition with zero shared-host reservations.
- **What still runs.** The shared resource admission and every other profile check still run unchanged at the operation boundary. The evidence is correspondence, not authority.

`.github/workflows/bar-hosted-domain-preflight.yml` is dispatch-only and uses no product content. It does the following:
- builds the digest-pinned Arch userland, with Buildah 1.43.1 from the dated archive (signature-verified and checked against the probe's recorded sha256);
- removes preinstalled toolchains;
- stops the container daemons and turns swap off;
- allows rootless user namespaces;
- provisions the preallocated volume;
- writes the domain evidence;
- runs `userland-check` as the unprivileged runner, inside a delegated systemd unit with its own mount, PID and cgroup namespaces.

`userland-check` exercises the following:
- the controller's own `host_resources` observation;
- rootless UID and GID 10001 mapping;
- the `0::/` cgroup-namespace view, the enclosing CPU/memory/task headroom and the runner's uid 1001;
- a scratch working container in the VFS build store;
- an empty load-store census;
- the Arch support pins.

No image is pulled or built.

Run the inert source controls with:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 tests/portable-workspace/image/test_bar_profile.py
```

The hosted-domain helper's pure refusal controls and its contract with the BAR reader run with:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 tests/portable-workspace/image/test_bar_hosted_domain.py
```

They use synthetic Git inputs, tiny Python capture children and injected native
ports. They do not execute Buildah, Podman, a product runtime or private assets.
The existing image workflow runs them before its existing portable-Python image
work; adding this profile does not start a BAR build in CI. This static/actual-
caller preflight catches malformed selections, wrong products, drift, occupied
stores, capacity failures and failed/invalid exports before dependent effects.
The linear workflow needs no new scheduler or custom model. Reuse the existing
20-minute job cap, keep warm source controls below60 seconds, and reassess if
retry, sharing or publication behavior changes. No CI savings are claimed.

The expanded manifest controls measured97.44 seconds on one local CPU, above that
60-second target. Independent malformed/tampered archive variants reuse one
synthetic source selection; one actual controller case proves refusal before the
load port. This preserves the meaningful boundaries without repeating the full
synthetic qualification for every variant. The first120-second capped aggregate
failed and remains retained; the revised aggregate passed within a180-second
local bound. Existing hosted job limits remain unchanged, and hosted latency and
net savings for this increment are not established.

Source review and required exact-head CI establish only the controller source
prerequisite. Full issue1032 acceptance still requires root's separate fresh
capacity/interference admission, the actual complete image build, manifest and
static ELF/dependency correspondence, independent readback and observed scoped
retirement. Source-only or intermediate-toolchain completion cannot close it.
Loaded engine/plugin ABI, Count1, useful play (still0/6), B2 adoption, publication,
parent acceptance and original holds remain separate. Prepared inputs and
`check-inputs` correspondence never grant execution authority.

## Hosted complete BAR image generation

The dispatch-only `bar-hosted-image-qualification.yml` is the complete image
route for the same issue1032. It uses the existing `bar-headless` controller,
with the accepted FSBar recipe at `4ddd070650e3131916a13bb2c5e75f99376e1c3a`
and tree `f6e4dc108bf065cd2fd44d48e0faf8807e1b8e3c`. The fixed wrapper
`eng/bar_hosted_operation.py` binds all six recipe files and the three pinned
executing controller files. The recipe retains its exact Ubuntu snapshot,
vcpkg/toolchain, Recoil headers/archive, HighBar stock plugin and compiled
controls, complete BAR Rapid game graph, and Avalanche3.4 map selections.

Each generation needs Programme/root's separate admission and one fresh hosted
VM. Source delivery, the full-image dispatch choice and the earlier no-content
preflight do not grant that admission. The workflow accepts only the first
attempt on protected main. Both Git checkouts have complete internal histories;
the product copy is a clean, private context on the operation volume. The
wrapper creates a fresh operation input, exact namespace configurations,
resolved support-file hashes, current-boot domain references and new empty
build/load stores. The earlier scratch probe's stores cannot be reused. There
is one fixed `qualify --profile bar-headless` invocation and no arbitrary
command, retry, resume or product-runtime interface.

The pinned checkout action creates local `gc.auto=0`, which the unchanged closed
source guard rejects. Before provisioning, the fixed two-checkout bootstrap
requires that exact singleton key/value and representation, the expected public
origin/revision/tree, complete internal history and a clean worktree. It removes
only that local key, verifies every other configuration byte is unchanged, then
runs the existing strict source-identity guard. Missing, duplicate, nonzero or
unexpected configuration refuses before image work. Global/system configuration
and credentials are left untouched; this is not a general configuration cleaner.
The first refusal and any already completed role receipt are retained.

The shared preflight uses the distinct pinned CLI protocols: Buildah1.43.1
`info` emits JSON by default and accepts only a Go template for `--format`;
Podman6.1.4 `info --format=json` explicitly selects JSON instead of YAML.
See the pinned [Buildah implementation](https://github.com/containers/buildah/blob/310b1c8f53f3c0a7884f82fe51083577c473490a/cmd/buildah/info.go)
and [Podman implementation](https://github.com/containers/podman/blob/464c15fec06f0074c916f9eff87c4806869c2833/cmd/podman/system/info.go).
The source controls exercise both actual profile preflights with a closed
role-specific CLI port, rejecting the original shared-format command before
image work. JSON parsing and exact store/rootless/platform guards remain in
force. A successful support-byte or no-content domain probe does not establish
that the full controller's command protocol is valid. The failed original full
attempt remains failed; direct-child EOF does not prove descendant settlement
or provider disposal.

The same dispatch-only workflow now has two fixed purposes. A separately
admitted prerequisite request uses `purpose: network-prerequisite` with
`full_image: false`; a full generation uses `purpose: full-image` with
`full_image: true`. Other combinations cannot run. The prerequisite invokes
`preflight --profile bar-headless` through the same maintained controller and
Lifetime. It pulls the exact existing Ubuntu base and performs one fixed tiny
shell/coreutils RUN with the same private network/PID/IPC namespaces, crun,
rootless stores and no-new-privileges flag. It checks UID0, NoNewPrivs1, proc/PID
correspondence and distinct private namespace identities. It acquires no BAR,
engine, map or toolchain content. A full generation repeats this prerequisite
in its own fresh VM and continues under the same original clock; it does not
reuse a prior prerequisite VM, store or image. Prerequisite success proves
network setup and that fixed RUN, not external egress or a full BAR candidate.
It cannot export, load or encrypt a candidate, or close issue1032.

The original15 support roles remain mandatory. Additional network evidence binds
the fixed pasta aliases and both passt/AVX2 executable objects before effects.
The existing managed owner lists and seals their resolved ELF loader/library
closure, aliases and loader cache before the RUN, and guards these identities
through retirement. The signed installed passt package/version is checked;
unknown native hashes are observed on the fresh VM rather than invented.
The closed configuration explicitly selects the observed pasta backend and
fixed helper search order `/usr/bin`, `/usr/lib/podman`, so a different helper
cannot silently precede the sealed pasta path. Helpers keep their original pathname and host policy. This route does not
change the backend, grant privileges, drop NNP, or select host networking.

Root-produced security evidence and bounded post-operation kernel observations
retain proc mounts, namespace/credential/status/current-label information and
Unknowns. Podman's rootless `apparmorEnabled: false` is not evidence that the
host LSM is absent. An empty or inaccessible audit log also cannot establish
that absence. The third failed original's pasta netns EACCES remains a failure;
this source adds a diagnostic prerequisite, not a proved fix for its denier.
Failed direct-child EOF/reaping still leaves descendant settlement unproved.

The optional privileged security readback writes into a fresh private runner
staging directory outside canonical controller evidence. Readback, ownership,
collision or malformed-output failures become bounded secondary Unknowns;
they cannot skip canonical receipt or poison its primary metadata inventory.
Primary native errors and raw streams retain precedence. A successful metadata
receipt cannot turn a failed qualification into success. The new network build
also repeats the canonical resource admission immediately after base pull and
before its build; a usage-ceiling sample does not prove fresh free headroom.

The pinned upstream contracts are [Buildah network setup](https://github.com/containers/buildah/blob/310b1c8f53f3c0a7884f82fe51083577c473490a/run_linux.go),
[passt AVX2 selection](https://passt.top/passt/tree/arch.c?id=cba357068dc586e7540971eb40bb56c5e62c4de1)
and [Podman rootless AppArmor support reporting](https://github.com/containers/podman/blob/464c15fec06f0074c916f9eff87c4806869c2833/vendor/go.podman.io/common/pkg/apparmor/internal/supported/supported.go).

The outer90-minute profile,3600+120 controller clock and resource floors remain
unchanged. The fixed RUN has a nested120-second cap and base pull a600-second
cap, both consuming that same work clock. The30-second kernel read shares the
existing9-minute receipt aperture; it adds no time to the20-minute transport
allowance. Pure validation uses focused actual-owner/closed-port controls with
120-second wall limits; the historical139.1-second BAR suite overrun remains
separate from its semantic test success and is not repeated to conceal it.

The fresh VM repeats the selected image-version, preallocated nodiscard volume,
daemon/user-namespace and capacity refusals. The maintained pivot-root entry
places the Arch userland at its mount namespace root before dropping to
uid/gid1001. Source is bound read-only. The delegated4096-task unit has a3900s
maximum runtime and120s stop allowance with control-group cleanup. These
platform bounds do not change the controller's3600s work and120s retirement
clock, four-CPU affinity,12GiB observed RSS or80GiB sampled disk ceiling. The
controller still reports hard containment and whole descendant settlement as
unproved.

The job's90-minute limit includes provisioning and bounded evidence transport.
Before the controller starts, the wrapper requires enough of that original
outer clock to retain the entire62-minute controller aperture and20 minutes
for receipts/transport. It refuses if preparation has consumed that room.
Neither repeated local stage timeouts nor a completed toolchain stage renews
the clock or closes the full image promise. Build time, aggregate8MiB command
output and80GiB VFS/export/load fit remain native qualification questions.

The controller exports the actual image ID, verifies the bounded OCI archive
and produced static ELF/file/hash/alias correspondence, loads only that archive
into the new separate store, and reads back the loaded image/config/rootfs and
selected local tag. The wrapper admits candidate transport only after joining
that result to the content-addressed manifest, original input/source/clock and
archive digest. It copies only bounded regular metadata and original command
streams into a private transport directory; malformed or failed results cannot
authorize candidate transport. Reporter failures emit bounded diagnostics in
the native job log. Independent readback must retain the native source/run/
attempt/step joins, artifact identities/digests, full candidate bytes and all
first-cause/retirement gaps.

Run the inert source controls with:

```sh
python3 -I -B tests/portable-workspace/image/test_bar_hosted_operation.py
```

They exercise the actual closed input/domain/configuration readers and fixed
wrapper/entry shell with authored tiny fixtures and intercepted engine ports.
Tiny synthetic OpenSSL controls generate temporary fixture keys and verify
streaming roundtrip, wrong-key and tamper refusal; they use no product bytes or
operator private key. No compiler, container, source/content acquisition or
product runtime runs.
The existing linear controller and these source/configuration checks cover this
route; no additional scheduler or custom model is introduced. Required source
CI and independent review remain separate from actual image qualification.

The complete image result still leaves loaded engine/plugin ABI, Count1, useful
play, B2 adoption, publication and parent acceptance open. Direct-handle reaping,
the final store census and native job termination are observed separately from
any unavailable provider physical-disposal evidence. No global prune, old
attempt cleanup or historical Home capacity release follows from this route.

### Encrypted candidate custody

Only recipient-encrypted image bytes enter the candidate artifact. The reviewed
public RSA3072 certificate is [bar-hosted-recipient.pem](../../../eng/bar-hosted-recipient.pem);
the operator keeps its private key outside the repository, runner and artifacts.
Before image work, the wrapper seals the exact certificate fingerprint and
actual host OpenSSL, resolved loader/libraries and loader-cache hashes. That
seal joins the controller's topology/input evidence and original monotonic
90-minute outer deadline. All identities are checked again around encryption.
The child environment disables inherited OpenSSL configuration/modules.

The fixed [OpenSSL CMS](https://docs.openssl.org/3.0/man1/openssl-cms/) command
uses AES-256-GCM AuthEnvelopedData with RSA-OAEP SHA256/MGF1-SHA256, binary mode
and streaming. The `-outform DER` CLI option with `-stream` produces indefinite
BER encoding. Encryption consumes the original verified archive without a
second plaintext copy. The source-sealed archive is at most16GiB; ciphertext is
bounded by the actual archive size plus64MiB. Before encryption, the volume must
have an additional16GiB+256MiB free for ciphertext, metadata copies and overhead.
The pinned upload action streams its ZIP at compression level0. Metadata staging
holds at most60MiB original files plus a4MiB receipt; candidate transport carries
only the bounded `candidate.cms`. Both artifacts retain for30 days. Receipt,
metadata upload and candidate upload have9/1/10-minute caps within the original
20-minute transport allowance. Transport fit remains measured qualification.

Root's separately selected private readback first verifies native artifact
identity/digest and bounded member shape, then decrypts into a private quarantine
file with `openssl cms -decrypt -binary -inform DER`, the reviewed certificate
and the retained private key. Decryption must exit successfully before any
plaintext is accepted; authenticated decryption can leave partial quarantine
bytes on failure. The original archive digest and16GiB bound must match before
the existing bounded OCI reader independently checks the candidate. Ciphertext
retention alone does not establish that readback. Encryption failures retain
bounded original controller metadata/streams and forbid candidate transport.
