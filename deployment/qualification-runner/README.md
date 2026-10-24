# Dedicated disposable qualification runner

This source recipe is a prerequisite for `.github#4778` and Coordination#1032.
No machine, acquired image, runner registration or native qualification is accepted
by this source. `baseline.json` deliberately leaves those inputs unset. Ordinary
pull-request CI remains on GitHub-hosted runners.

The candidate carrier is released Ubuntu 26.10 x86_64. Its official cloud manifest
lists glibc `2.44-1ubuntu2` and AppArmor. This matches the *version input* required
by SDD's catalog source; it does not establish its 30 exports, layouts, executable
closure, user namespaces or actual catalog behavior. Ubuntu 26.10 is not yet in
the published .NET 10 supported-OS table. Its OpenSSL 4 compatibility is unqualified.
Fedora 45 was still prerelease on 2026-10-24; an absent AppArmor knob cannot satisfy
the maintained policy-zero contract. Neither SELinux nor NoNewPrivileges is disabled.

The source identities and complete 30-export requirement are in `baseline.json`.
The determining gate remains the actual published SDD catalog, with SDK 10.0.401
and runtime 10.0.12 on the actual carrier. Governance's separate >=2.40/four-export
contract remains unchanged. The accepted complete SDK input manifest is an input
identity, not an installed image or host qualification. Optional tracing's unresolved
`liblttng-ust.so.0` remains separate.

## Reprovisioning contract

1. The operator supplies the actual provider, disposable instance/image IDs, UUID,
   region, isolated network and teardown API. The shared Codex machine is forbidden.
   Home proposes `home-kvm-qualification-v1`: four vCPUs, 16 GiB guest RAM and
   256 GiB physically preallocated raw backing, one job per VM. This supersedes
   the conservative32GiB reference sizing; it is not an observed machine or fit.
   [Home owns the candidate](https://github.com/FS-GG/.github/issues/4516#issuecomment-6380878293)
   through4516(primary)/4517(secondary), using a new dedicated rootless Podman/QEMU
   KVM launcher whose source, executable/image/firmware closure and finite custody
   remain unqualified. Root selects actual effects. Home must reserve the16GiB
   guest plus2GiB VMM/metadata margin while preserving8GiB host available before
   creation; a free-memory observation alone is not a reservation. The unchanged
   BAR gate requires14GiB **actually available** guest memory and80GiB free backing.
   A16GiB allocation cannot prove those gates or workload/observer headroom; actual
   post-boot, adjacent, enclosing-resource and operation fit checks must pass.
   Otherwise activation refuses; no smaller workload, observer omission or false
   capacity fact is substituted.
2. Separately admit acquisition of the immutable Ubuntu image, signed checksum and
   signer identity; the moving `current` index is discovery metadata only. Acquire
   a complete signed offline APT transaction and expected final package inventory,
   the fixed runner release, SDK archive and Go archive. Retain full file/hash
   population and qualification receipts in the acquired-input lock. No credentials
   belong in that lock. A matching digest is not signature or provider authority.
3. Admit the image-build operation on that exact machine. The operator installs a
   root-owned mode-0600 `/run/fsgg-image-build-selection.json` binding target bytes,
   provider instance and actual DMI UUID. Run `provision-image.sh --apply TARGET
   ACQUIRED-LOCK /mnt/fsgg-qualification-inputs` only within that selected image build.
   The script refuses absent inputs and checks the complete offline file population.
   Qualified archives must already have passed traversal/link/type/size extraction
   review; this source does not grant acquisition or archive qualification.
4. The recipe uses only the locked offline APT transaction and compares the whole
   installed package inventory. It preconfigures AppArmor's observed userns knob
   to zero *in this disposable image only*. No workflow setter or restoration
   window is introduced. Missing knob refuses. Retain actual kernel, LSM, mappings,
   package and ELF-loader/dependency identities; qualify SDK/OpenSSL, catalog and
   real Go 1.27.1 CGO/race under separately selected resource/clock profiles.
5. Publish an immutable qualified image only after those gates. Root supplies its
   accepted image/instance binding, finite foreground runner custodian and provider
   teardown admission. A new VM is required after every job or failed registration.

The Go race role requires the actual compiler executable, headers manifest, linker,
libgcc/runtime and loader closure identities in the signed transaction. It cannot
report unsupported/skip as success. Its separate toolchain root does not replace
the existing Go 1.24.1 canonical LMT input. Exact Go/runner archive identities and
all new native/compiler qualification costs remain unselected.

## Qualified-image layer: bake once, bind per boot

The GitHub Free plan has no restricted runner groups, so the runner serves exactly one
**private** repository, `FS-GG/FS.GG.Native.Qualification` (see `native-qualification/`).
The layer below is what makes an ephemeral Ubuntu 26.10 guest the *qualified image*
for that caller. Everything slow is baked ahead of time so the caller's 300 s first-step
deadline is not spent on installation.

| Requirement | Where it is applied | Readback (`dedicated_qualification_runner.py verify-guest`) |
|---|---|---|
| AppArmor `apparmor_restrict_unprivileged_userns = 0` drop-in (image only; no job setter) | bake: `provision-image.sh`; re-applied by `guest-boot.sh` | drop-in bytes equal `90-fsgg-qualification.conf`; live value reads `0` |
| `acquired-input-lock.json` (root, 0644) | bake | present, valid, runner version equals the baked runner |
| SDK 10.0.401 / runtime 10.0.12, Go 1.27.1 | bake, from the hash-checked acquired inputs | fixed roots `/opt/fsgg/toolchains/{dotnet-10.0.401,go-1.27.1}`; exactly one SDK; `VERSION` is `go1.27.1` |
| Buildah 1.43.1 | not a guest package: the job builds the sha256-pinned Arch userland | reported as a fact; the domain gate checks the version in the job |
| Runner path, name and UID | bake: `/opt/fsgg/actions-runner`, user `runner` UID/GID 1001, `/home/runner`; names `fsgg-q-*` | account, group, path, ownership, never-configured runner |
| Runner dependencies | bake (`libicu78 libkrb5-3 zlib1g`) instead of a live `installdependencies.sh` | `Runner.Listener` present |
| Passwordless root for `runner` | bake: `runner.sudoers`, this disposable guest only | sudoers bytes equal `runner.sudoers`, 0440 |
| `instance-binding.json` (root, 0644) | **per boot**: `guest-boot.sh --apply REQUEST.json fsgg-q-NAME` | schema, `Default` group, label, repository, `jit-config`, runner name, actual DMI UUID |

Bake once in the separately admitted image build:
`provision-image.sh --apply TARGET ACQUIRED-LOCK /mnt/fsgg-qualification-inputs`. It installs
the reviewed layer under `/opt/fsgg/qualification-layer` (with a `MANIFEST.sha256`) and ends
by running `verify-guest --phase image`; a refusal means the image must not be published.

Each boot, from the launcher's cloud-init `runcmd` (as root, before the JIT config is used):

```
/opt/fsgg/qualification-layer/deployment/qualification-runner/guest-boot.sh --apply /run/fsgg/binding-request.json fsgg-q-NAME
ACTIONS_RUNNER_INPUT_JITCONFIG=... /opt/fsgg/qualification-layer/deployment/qualification-runner/run-jit-runner.sh --run fsgg-q-NAME
```

`binding-request.json` has schema `fsgg.dedicated-qualification-binding-request/1` and exactly
`baselineId`, `providerInstanceId`, `providerImageId`, `systemUuid` (the actual guest DMI UUID),
`runnerName`, `qualifiedImageSha256`, `rootInstanceAcceptanceSha256`,
`registrationAdmissionSha256` and `repositoryScopeVerified` (`true` only after the runner's
single-repository scope was independently read back). The request is data, not authority.
Neither script accepts a PAT or registration token, uses `--replace`, or reuses credentials;
`run-jit-runner.sh` takes the one-job JIT configuration only through the supported
`ACTIONS_RUNNER_INPUT_JITCONFIG` environment input and refuses unless the baked layer and
sealed binding read back clean immediately before it `exec`s the runner.

Known blocker found by the readback: Ubuntu 26.10 ships OpenSSL 4.0.x, while
`eng/bar_hosted_operation.py` accepts only the OpenSSL 3 family for the encrypted candidate.
`verify-guest` therefore reports `openssl-family-not-3` and the bake refuses to finish. It
needs a Root decision (qualify OpenSSL 4 for the CMS sealing, or provide a pinned OpenSSL 3
binary) before the first dispatch; it is not silently relaxed here.

## Caller and security boundary

The only workflow that may use label `fsgg-native-linux-x64-v1` is the private caller. Its
reviewed source of truth is `native-qualification/bar-hosted-image-qualification.yml`; it is
deliberately *not* under `.github/workflows`, so public Coordination can never queue it and
no Coordination workflow targets the label (`.github/actionlint.yaml` no longer declares it).
The runner is a repository-level, ephemeral JIT runner in group `Default`; public
repositories are never enabled for it, so fork pull requests cannot reach it. The caller is
dispatch-only, runs on `main` attempt 1, and checks out this repository at an explicit
`coordination_sha` that must be an ancestor of Coordination `main`. Its first step refuses
unless the native job record shows group `Default`, a runner name starting `fsgg-q-`, and the
caller repository, run and commit. The caller refuses a run whose copy of the template differs
from the template at that commit. `MANIFEST.sha256` pins the template workflow and README bytes,
and `eng/bar_hosted_operation.py` reads the fixed preparation scripts from that pinned template.

The existing BAR orchestration requires privileged sudo/systemd setup. Consequently
this is a trusted-dispatch, privileged **disposable VM**, not an untrusted multi-tenant
sandbox. The image recipe installs `runner.sudoers`: runner can orchestrate root commands
without a password, matching the workflow's privileged setup. Do not install it on shared
machines. Native workload NoNewPrivileges remains required: BAR's Buildah/RUN uses its existing
`--security-opt=no-new-privileges` and fixed RUN checks NoNewPrivs=1. Its privileged outer
systemd orchestration has no NNP property at the protected baseline; this source does not
change that fact. Control-group retirement and existing delegation remain.

## Actual qualification routing still required

The maintained gate is `eng/bar_hosted_domain.py`, called by `bar-hosted-domain-preflight.yml`
(hosted, no content) and by the private caller's fixed `dedicated-preconfigured` profile. Its
legacy profile requires actual GitHub Ubuntu24 image identity; a self-hosted Ubuntu26.10 VM
must not fabricate those facts. `DEDICATED_IMAGE_SHA256` and `DEDICATED_PROVIDER_IMAGE_ID` in
`eng/portable_workspace_sc2.py` stay unset, so the profile refuses until a later reviewed source
change records the accepted baked image's identity. No acquired image, registration, source
admission or native execution follows from this source.
The Templates owner's actual native callers (public repositories) are unrouted: each would
need its own private-repository dispatch caller. Ordinary PR jobs stay hosted.

## Teardown and retirement

Home owns the `home-kvm-qualification-v1` provider candidate; its actual
instance and qualified launcher/custodian are not supplied yet. No guessed cloud
CLI, copied VMM controller or generic command executor is emitted. The target-specific operator must select a fixed delete-instance action
and independently read back instance absence, ephemeral disks/network attachments
and runner deregistration. Export original logs, hashes, command EOF/reap, unit and
cgroup birth/absence before destruction under the original retirement budget.
If export, deregistration or provider deletion is uncertain, preserve that Unknown
and retire the reservation as held; never reuse the VM or retry an ambiguous create,
register or delete blindly. Provider disposal is separate from local process absence.

## Source validation and primary references

The existing portable-workspace source preflight calls the14input/refusal and62operation
controls before Podman/acquisition; its watchers include the new recipe/helper/tests.
The manual owned preparation also calls the14controls. This is source wiring, not
an observed CI result. The focused unittest module exercises authored DATA/refusal
and tiny file-population checks only. It does not run SDKs, compilers, userns probes, provisioning or registration.

- [Ubuntu 26.10 release notes](https://documentation.ubuntu.com/release-notes/26.10/)
- [Official Ubuntu cloud image metadata](https://cloud-images.ubuntu.com/stonking/current/)
- [Official cloud package manifest](https://cloud-images.ubuntu.com/stonking/current/stonking-server-cloudimg-amd64.manifest)
- [.NET supported OS policy](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [GitHub self-hosted runner requirements](https://docs.github.com/en/actions/reference/runners/self-hosted-runners)
- [GitHub ephemeral runner guidance](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/autoscale)
- [Runner supported environment inputs](https://github.com/actions/runner/blob/main/src/Runner.Listener/CommandSettings.cs)
