# Closed native-collector fake bootstrap mode

This source adds one fake-only mode to the existing custody bootstrap producer.
It does not add a Host API, admission, installed capability, model execution or
network access. The native assets and managed digest pins now bind a genuine bounded producer
regeneration and actual compiled filter exports. Ordinary execution and fake
namespace custody remain separately unqualified. The existing ordinary
`--bootstrap` and `--export-filter` implementations are preserved.

The closed entry is:

```text
bootstrap --native-collector-fake-v1 OPERATION REMAINING_MS PROFILE_ROOT OPERATION_ROOT CASE
```

`OPERATION` is32 lowercase hexadecimal characters. `REMAINING_MS` is1–60000
canonical decimal milliseconds derived by the owning controller from its
original deadline. It limits helper preparation; the controller still owns the
original whole-operation deadline, cancellation, output, current init retirement
and direct helper settlement. This helper neither grants a fresh operation clock
nor supervises the workload after its same-PID exec.

Both roots are absolute normalized paths, distinct and non-nested. The operation
root's final component must equal `OPERATION`. `CASE` is exactly `positive`,
`namespace`, `failure` or `timeout`. No caller executable, argv array, environment
entries, mount list, profile Boolean or custody receipt is accepted. No shell
interprets root arguments; printable spaces and metacharacters remain literal.

## Fixed roles and trust boundary

The profile root contains `fake.sh`, `packet.txt` and `schema.txt`, whose bytes
must match the compiled synthetic fixture exactly. The operation root contains
an exact synthetic `private-canary.txt` and an empty `scratch` directory. Private
directories must be owned by the invoking non-root UID with mode0700; input files
must be regular, single-link, same-UID mode0600. All path components are opened
without following links; group/other writable and set-ID components refuse.
Parents may be root-owned or owned by that UID. Fixed system input components
must be root-owned, without group/other write or set-ID permissions.

This is a trusted-controller prototype. It does not defeat malicious concurrent
writers with that same host UID or privileged installation replacement. The
future installed verifier must independently bind the executable/loader/profile
inventory and its lifetime before positive Host admission. This mode checks role
shape and exact synthetic input bytes; it does not pretend to hash every system
binary internally. Fixed system paths target the explicit linux-x64 pilot
closure; another library layout requires a new reviewed profile.

The only mounts are fixed workload executables/loaders, fixed read-only synthetic
inputs and one writable scratch. The Host anchor, store, grants, credentials,
original home, proc/sys/run/dev and descriptor are not exposed. The synthetic
canary remains outside projected roles. Empty scratch prevents an existing
hardlink/file from making the fixture's output overwrite an unrelated host file.

## Filter and descriptor recipe

The helper closes all inherited descriptors>=3, refusing any failure. It verifies
the fixed inputs, establishes no-new-privileges, writes the compiled fake BPF to
one child-local memfd, seals write/grow/shrink/seal, verifies the seals, rewinds it
and clears CLOEXEC on exactlyFD3. Only then does the same process exec the fixed
root-owned bubblewrap path with a closed environment and argv recipe. There is
no fork, second launcher, service or caller-selected filter file.

The selected profile is `untrusted-workload-userns-seccomp-v1`. Initial
user/PID/mount/IPC/UTS/network namespaces remain mandatory. Root and inputs are
read-only, capabilities dropped and environment cleared. The filter denies all
clone/fork/vfork/unshare/setns attempts, returns ENOSYS for clone3, and rejects
wrong ABI/x32. This is single-thread fake scope, not the ordinary thread filter
or a model-compatible policy. Trusted bubblewrap setup/init remains outside the
application filter. No sysctl write, writable proc, remount of host proc or
`--disable-userns` fallback is used.

This producer mode uses standard streams only from its controller:
`--json-status-fd2`, `--block-fd0`, `--seccomp3`; stdout1 is captured separately.
There is no sync FD. Application stdin and stderr are closed by bubblewrap. This
is a material difference from the previously qualified private four-FD fixture;
its qualification is not inherited. In particular, the previous namespace/fork
EPERM diagnostics cannot be assumed observable through the closed application
stderr. Actual status/held-init/direct-helper retirement and meaningful outcome
controls require separate qualification before any positive consumer is enabled.

EOF on the block stream can release bubblewrap. A future controller must retire
owned identities before disposing that stream on refusal. It must not treat
status, monitor exit, EOF or a caller Boolean as strong retirement. The existing
internal custody owner and its selected distribution remain the authority; this
source does not copy a process supervision engine into Host.

## Producer and validation boundaries

`--export-native-collector-fake-filter` exports only the compiled fake filter.
The existing build script will compare that actual export with the selected
SHA256 and bind the new header plus filter bytes in its generated manifest. It
continues to mark Host admission and native qualification unavailable. The coherent artifact successor copies the actual bounded regeneration outputs
and derives the three managed digest pins from those bytes. The actual fake
and ordinary exports match their selected filters; this does not qualify the
fake stdio profile, ordinary process custody or positive Host admission.

`tests/custody-native-collector-fake/test_source.py` supplies pure source controls
for original-mode equality, fixture/filter declarations, closed recipe, refusal
ordering and generated-asset custody. `test_fake.c` supplies authored native
controls for the actual parser/recipe and memfd refusal paths through injected
syscalls; its unchanged parser/recipe/memfd bodies passed the separate bounded C-first
compilation and injected-control execution. Compilation,
real memfd/descriptor/filter behavior, namespace execution, deadline and complete
retirement need a separately bounded native selection. Source tests alone do not
establish C compilation or platform acceptance.
