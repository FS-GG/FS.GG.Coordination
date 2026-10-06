# C2 pure phase byte contract checkpoint

This Tier 1 source window owns the Coordination CLI producer's closed C2
request/result bytes, outside the DIAG 0.2.1 release. The pure implementation
passed a separately admitted local source qualification at `aafa2f46`: 37 C2 API
cases and the existing v1 contract case passed with exact TRX identities.
There is no command registration, trusted enrollment, image selection or native
C2 workload acceptance in this source window. Exact delivery-head hosted checks
remain required.

The internal `PortableWorkspaceC2Phase` signature separates opaque decoded
documents from ordinary supplied `DeclaredSelection` records. `bindDeclared`
checks structural correspondence with the two fixed scaffold/compiler fixture
profiles. The supplied selection cannot enable another executable, environment,
step sequence or translation syntax. A `BoundDeclaration` establishes consistency
only. `validateResultJoin` similarly cannot establish observed retirement,
physical readiness, semantic evaluator acceptance or launch permission.

The tracked schemas describe closed wire shapes; the decoder additionally checks
strict canonical bytes and semantic obligations. Canonical JSON is compact UTF-8,
ordinal sorted property names, ordered arrays, literal Unicode and one terminal
LF. Reject malformed UTF-8, duplicate properties at every depth, unknown/null
fields, unsupported versions and noncanonical escaping or numbers. Proposed
whole-document limits are 1 MiB request and 2 MiB result; these bound each whole document before decoding. The existing
32 inputs, 2 steps, 64 arguments per step and 1,024 output records are additional
limits: individually valid combinations that exceed the document cap refuse. Enforce the
request's aggregate captured-byte, handoff-byte and handoff-entry limits on joins.
Diagnostic lists contain 1–32 entries, with field paths at most 256 characters
and reason tokens at most 128 characters. Refusal cannot silently truncate input.
Whole-document overflow reports `document-byte-limit` before parsing. Decoded
stdout and stderr count together against the request's capture allowance; output
records count together against its handoff bytes and entries. Base64 is the
canonical padded standard alphabet: no whitespace, missing padding or nonzero
unused bits. Individually permitted fields cannot widen an aggregate allowance.

Preserve literal declared and container argv. Reconstruct every translated slot
from the declared host/container role paths, exact prefix and safe relative path;
all other slots remain byte-identical, including an empty string in a literal
parameter slot. Empty slots cannot be omitted or coalesced; fixed template IDs,
option keys, executable and translated role paths remain constrained. Binding requires the exact fixed role set,
including handoff; compiler restore-state is present only when declared restore
inputs require it. Host role paths cannot overlap. The compiler `-p:OtherFlags=--sig:` prefix
is the sole selected nonempty translation prefix. Reject traversal, duplicated
roles/indices/input IDs, arbitrary executable/environment and reordered steps.
The baseline is outside the compiler's exact four-file source root. The public
compiler fixture declares no restore state: correspondence can succeed without
physical readiness, which remains a distinct future admission prerequisite.

The original 60-second phase includes setup and caller finish. Its selected
subdivision is at most 40 seconds work, 15 seconds cleanup and 5 seconds caller
finish. Compare the request with the supplied original start/deadline; never read
or reset a timer here. Completed results require the entire selected sequence,
known zero exits and total known step work within the original work allowance,
with known timing within the original controller return cutoff
(55 seconds for these fixtures), complete bounded output/handoff and retired
helpers/containers with complete cleanup. Other dispositions carry an ordered
prefix that stops at the first unknown or nonzero exit. Honest late (120,000 ms)
and unknown timing observations remain decodable without supplying permission.

`fixtures/` contains synthetic public-safe wire data derived from the private
source02 contract review. Paths are relocated to `/fixture`, and the scaffold's
single raw argument deliberately contains literal `é名`. Request and result
digests were authored for these actual bytes; the golden manifest pins them and
actual API tests must reproduce them. The public compiler baseline uses a sibling
compiler-baseline path, outside its exact four-file compiler-source root.
Input byte/hash declarations still describe selected review inputs, not public
files or newly qualified package custody. Completed result fixtures are authored
test expectations, not process observations.

The UnitTests source calls the actual internal API. The CLI registers the signature
then its implementation; UnitTests registers the test source and copies these
fixtures to C2Fixtures/ in its output. Existing CLI friend metadata admits
UnitTests without new visibility. The implementation uses System.Text.Json and
standard crypto; canonical quoting explicitly preserves literal Unicode where
general JSON encoders may escape more characters. Opaque documents own their
decoded and encoded data, and returned arrays are copies. No filesystem or clock
read occurs inside the producer module.

Verification proceeds signature/API review → admitted pure implementation →
admitted compiled API-call tests → ordinary source PR checks. Future enrollment,
helper/container custody, original-budget execution, distribution and both
consumer acceptance routes require their own selected windows. Existing v1,
Python P4, DIAG release, consumer legacy routes and generated workspace behavior
remain unchanged. This additive contract has no migration of v1 documents.

The additive internal C2 options parser accepts only the exact argument tail
`execute-c2-phase --request FILE` or `recover-c2-phase --request FILE`. It
projects the literal file token and rejects missing, duplicate, unknown or
whitespace syntax tokens. It does not read the request, normalize its path,
register a command, resolve enrollment or dispatch work. Root-owned offline
qualification at source `c1d8bedb` passed all 48 selected cases: 37 phase API
cases, 10 options cases and one existing pure v1 case. All six commands exited
zero, with clean custody and no resource or storage failure. Earlier failed
windows remain retained. This qualifies the pure source and options only; exact
delivery-head hosted checks and installed authority remain separately required.


The next source-only admission checkpoint selects two fixed mappings:
`local-c2-dotnet-scaffold-fixture-v1` to root-owned
`/etc/fsgg/portable-workspaces/c2-dotnet-scaffold-v1.json`, and
`local-c2-dotnet-compiler-fixture-v1` to root-owned
`/etc/fsgg/portable-workspaces/c2-dotnet-compiler-v1.json`. First-fixture effective
UID is 1000; state/request roots are respectively
`/var/lib/fsgg/portable-workspaces/c2/1000` and its `requests` child. Actual
protected enrollment, normal installed CLI, SDK image and operation are absent.
`enrollment.schema.json` describes required shape only, not permission or current
provisioning. Installation must bind the genuine package suffix under
`/opt/fsgg/coordination/c2/installed`, complete active host/runtime/search closure,
and independent SDK10.0.401/runtime10.0.12 image qualification; no fallback pull,
restore, user probing or copied source DLL may substitute.

`PortableWorkspaceC2Admission.fsi` and its API exercises remain excluded drafts,
without reader/resolver/authority/handoff bodies or project registration.
`describeRequest` is only an immutable six-field declaration projection of the
already validated request and its digest. Opaque EntryContext, captured roles,
trusted enrollment, Admission and RetiredHandoff cannot be constructed from it.
Request capture requires held no-follow regular descriptors and current physical
identity joins, at most 1MiB and a 5s entry-read ceiling clipped by independently
trusted outer bounds. That ceiling adds no phase allowance: all setup/read time
is charged to the original independently enrolled 60s window (40work/15cleanup/
5caller finish), with unchanged UTC/boot/monotonic origin and checked arithmetic.
Recovery creates neither a replacement deadline nor launch/cleanup permission.

Enrollment reads are bounded to64KiB; immutable role capture to32 files/16MiB.
Installed payload remains4096 entries/128MiB/16MiB per file. Complete retired
handoff remains16MiB/1024 entries; aggregate streams1MiB and result2MiB. Both
helper and container retirement precede verified no-replace transfer and scratch
deletion. Admission context generation and exact independently enrolled operation
fence must join every capture, producer/image/root/input/source/request identity.
No schema success, declared flag or exit0 proves these observations. Refusal
fixtures and pre-body API calls are uncompiled review source, not qualification.

Proposed later CLI convention is2 syntax,3 bounded prelaunch refusal,4 existing
pending/recovery,5 observed failed/unknown/incomplete,0 only authoritative complete
custody plus semantic result. No dispatcher uses those conventions now; once any
effect may exist, missing output is unknown/recovery rather than prelaunch refusal.
Existing Python enrollment/default, v1 runtime, DIAG gates and product behavior
remain unchanged.


The separately selected observation successor registers the matching Admission
body and API tests. It uses Linux-x64 no-follow openat descriptor chains, bounded
regular reads, owner/mode/access/default ACL checks, retained parent handles and
before/after object/current-path identity joins. Descriptor authority is removed
before close; ambiguous close is never retried. Every read/traversal checks the
original remaining deadline, including after final close. These checks are source
only and not native-qualified. No directory provisioning or workload launch occurs.

A pure closed enrollment decoder returns only EnrollmentDeclaration, distinct
from TrustedEnrollment. The readonly request observation projection includes the
SHA256 of the ordinal-key canonical input array with one terminal LF, preserving
row order; enrollment inputInventorySha256 must equal it, and every physically
observed file must match declared path/bytes/hash. This digest is a declaration
join until complete physical inventory succeeds. Inputs use the fixed
state-root/inputs/operationId/role/relativePath layout. Writable-role declarations
use state-root/execution/operationId/role and cannot alias; writable-root creation,
absence/namespace proof and mounting are not implemented or authorized.

Current installed entry, root-owned payload inventory and selected apphost/host/
executable hashes are independently checked under the original budget. Active
host/runtime/search closure, normal installer qualification receipt and currently
loaded qualified image still lack an independently selected owner-proof seam.
Neither hash fields nor schema decode can provide that proof. The resolver ends
in image-availability-unproved and cannot return TrustedEnrollment; captureRoles
and admit retain context/request/clock checks but cannot create positive authority.
No RetiredHandoff constructor, runtime dispatcher, Program wiring, Podman/journal
integration or result-completion factory is added. These missing proof dependencies
must be selected in the existing owning plan before any positive integration.
