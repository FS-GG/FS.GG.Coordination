# Portable host export source context

`PortableWorkspaceExecutor.HostExportSourceJournal` retains a fresh full export original,
a consumed export-attempt intent and independently authenticated receiving observations under
the existing host journal key and owner lock. It is a source API only. It supplies no copy,
transport, acknowledgement, cleanup, host CLI or runtime-profile activation.

The [implementation](../../src/FS.GG.Coordination.Orchestration.Execution/PortableWorkspaceExecutor.fs)
and [controls](../../tests/FS.GG.Coordination.Orchestration.Execution.Tests/PortableHostExecutorTests.fs)
define the format and current behavior. No production implementation of
`IPortableHostExportReceivingVerifier` is supplied. A parser, claimed issuer, checksum or local
copy result cannot implement its authentication obligation.

## Complete original encoding

`fsgg.portable-host-export-context/1` is opaque binary data, capped at 65,536 bytes.
`HostExportSource.createOriginal` freezes the complete context;
`parseOriginal` requires exact re-encoding, no trailing bytes and the same field bounds.
The context digest is lowercase SHA-256 of ASCII
`fsgg.portable-host-export-context/1` followed by one NUL byte and the exact binary context.
A raw SHA-256 of the context is a different digest.

Integers use little-endian .NET `BinaryWriter` encodings. Strings use UTF-8 preceded by the
minimal unsigned 7-bit byte-length encoding. The new codecs use strict UTF-8: invalid surrogate
input or malformed encoded bytes refuse. New fields and the exact aggregate encoded size are
checked before context serialization; bounded backing streams enforce context, attempt and journal
ceilings during writes. Legacy encoding behavior remains unchanged. GUIDs are lowercase `D` strings. The ordered fields are:

1. Schema string and fresh export-operation GUID.
2. Full existing host reservation: the binding's base64 canonical command bytes, qualified image,
   allocation ID, worker ID and allocation-receipt digest; admitted and host-lease dates (each signed
   64-bit ticks followed by offset ticks); signed 64-bit maximum-runtime, export-reserve and
   cleanup-reserve ticks; selected-output tag byte `1`, manifest digest string, unsigned 64-bit member
   count and total bytes.
3. Signed 32-bit manifest byte length followed by the exact selected-manifest bytes. Zero selection
   is outside this slice. The existing manifest grammar and digest domain remain unchanged.
4. Original host-boot GUID; unsigned 64-bit admitted, export-deadline and lease-deadline monotonic
   nanoseconds. The lease interval must equal the original effective UTC lease interval. Export must
   end before the original cleanup reserve. These are data assertions pending qualified acquisition.
5. Source root, signed 32-bit retiring-root count and roots in strict ordinal path order, then destination
   root. Each root is a path string and unsigned 64-bit device, inode, owner UID and owner GID.

There are 1–16 retiring roots. Source must lie within one, destination must be outside all retiring
paths and on a different device from each and the source. Root paths are canonical absolute paths;
these checks do not establish descriptor custody, physical backing, mount topology or receiver durability.
All arrays returned by the immutable original are copies. Full binding and reservation bytes remain
available independently; no subset digest substitutes for the complete original.

## Existing journal and once-only observation ingress

The source-only record uses `fsgg.portable-host-export-source-journal/1` in the existing
`journal-v1` root, SHA-256 workspace/idempotency key and exclusive owner lock. A fresh record requires
current existing host-authenticated evidence with the exact original reservation, empty lifecycle and
an allocation root identity matching a retained retiring-root pin. Occupied legacy, host or supplier
records refuse replacement. No old record gains a fabricated boot, monotonic clock or reserve.

`PrepareAttempt` requires a current authenticated writer-termination observation, original boot and
remaining original export clock. It persists one consumed intent before returning its bytes. A cold
reopen does not clear the intent, retry copying or renew a clock. Copy observations are bounded opaque
bytes joined to that exact attempt; they never become known durable-export evidence.

Receiving ingress invokes the future typed verifier once over a private input snapshot. The result
must bind the entire existing host reservation and identity, context digest, exact attempt and local-copy
observation digests, destination root, complete selected manifest, original boot/monotonic clock,
writer-termination receipt and durable-receiving commit receipt. Existing host ingress additionally
checks freshness, binding and lifecycle observation completeness. Known export requires the exact durable
manifest readback and a commit observation after termination within the original lease. A future verifier
must authenticate these observations and independent custody; returning a caller's data is insufficient.

The journal rechecks the cap's joins under its owner lock. The existing lifecycle reconciler retains
original first failure and conflicts. Cold records retain observation bytes and lifecycle data but never
recreate an authentication capability. Fully pre-encoded supplier `/4` and export-source `/1` writes
now use the Linux descriptor-relative source primitive. Under the existing exclusive owner lock it
pins an `O_RDONLY|O_DIRECTORY|O_CLOEXEC|O_NOFOLLOW` journal descriptor, binds the held lock identity
to its directory entry using the documented Linux `statx` ABI, creates a private exclusive temporary
file, writes the bounded candidate exactly, flushes the file, replaces the record with `renameat`,
then calls `fsync` on that same directory before descriptor-relative exact and cold structural readback.
Unsupported platforms or native operations refuse; legacy record write paths retain their behavior.

Successful completion establishes this syscall ordering only. Every successful source write/readback
still reports directory durability Unknown. Production separately requires authenticated, durably
provisioned pre-existing `stateRoot/journal-v1` ancestry, qualified backing/filesystem assumptions and
stable namespace custody. The unchanged root initializer supplies none of that provenance; this is
not a hardware or power-loss durability claim. A blocked syscall retains its owner lock until it
returns; managed timeout cannot release custody while fsync continues. An outer process owner must
retain an interrupted or unconfirmed operation as Unknown.

Failures before replacement preserve the exact previous record. Failures after replacement are
potentially committed and return the existing generic journal refusal. Cleanup removes only this
attempt's own temporary name, preserves the first failure and never deletes the record or lock.
Cold-read the same original; a retained consumed intent refuses another preparation. Old bytes after
storage rollback cannot prove that an earlier attempt never escaped or authorize resending it.
No production Executor interprets this source record as a runnable or destructively recoverable host record.

## Compatibility and remaining delivery

Existing execution/host `/1` and supplier `/2`, `/3`, `/4` readers and write bytes remain unchanged.
Readers predating this source tag refuse it; there is no automatic read-time upgrade, conversion,
legacy reset or fallback to another ledger. The source record preserves the existing aggregate journal
size ceiling; individual context and observation sections are capped at 65,536 bytes.

The containing Execution project is not independently packable. Its API is project-referenced by the
packable Coordination CLI. This change does not publish or reinstall a CLI or contracts package, and
cannot authorize a different payload under an already published version. A future coherent release
must select a new applicable version and qualify its exact consumer bytes and versioned reader behavior.
Installed adoption, concrete verifier/authenticated transport, descriptor-backed copy, independently qualified storage/ancestry commit
barrier and any native export or retirement remain separate work.

An ordinary owner copy from an old held allocation can be a separately admitted fresh non-destructive
acquisition. It must preserve that allocation's original Unknown outcomes and holds; this API does not
backfill a new reservation, clock or retrospective receipt for it.
