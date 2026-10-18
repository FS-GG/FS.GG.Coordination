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
