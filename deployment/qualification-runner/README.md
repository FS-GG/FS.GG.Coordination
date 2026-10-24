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

## Registration and security boundary

Configure the organization runner group `fsgg-native-qualification` for only the
listed protected-main workflows; verify that restriction independently before any
registration. The fixed routing label is `fsgg-native-linux-x64-v1`. No PR or fork
job may use this group. Registration runs as UID1001, validates the root-owned
instance/image lock and actual UUID, and accepts only a short-lived registration
token in the supported `ACTIONS_RUNNER_INPUT_TOKEN` environment input. It does not
accept a PAT, put a token on argv, print it, use `--replace`, or reuse credentials.

`register-ephemeral-runner.sh --register fsgg-qualification-NAME` performs one
`--ephemeral --disableupdate` registration and runs the existing runner foreground.
The operator must retain runner diagnostics externally and enforce GitHub's runner
update/security deadlines. Ephemeral deregistration does not destroy the VM.

The existing BAR orchestration requires privileged sudo/systemd setup. Consequently
this is a trusted-main, privileged **disposable VM**, not an untrusted multi-tenant
sandbox. The image recipe explicitly installs `runner.sudoers`: runner can orchestrate
root commands without a password, matching the existing workflow's privileged
setup. This role is confined to this dedicated disposable image and the restricted
protected-main runner group. It is not a security sandbox against those jobs.
Do not install it on shared machines. The provider's finite runner/VM custodian
is still missing until the actual target is supplied. Native workload NoNewPrivileges remains required: BAR's Buildah/RUN uses its existing
`--security-opt=no-new-privileges` and fixed RUN checks NoNewPrivs=1. Its privileged
outer systemd orchestration has no NNP property at the protected baseline; this source
does not change that fact. Control-group retirement and existing delegation remain. Host policy permission
is neither workload authority nor evidence of correct rootless execution.

## Actual qualification routing still required

At Coordination `8fb93bc9c37fdcc1760341dd182756de3e9ae42a`, the maintained gate is
`eng/bar_hosted_domain.py`, called by `bar-hosted-domain-preflight.yml` and the manual
`bar-hosted-image-qualification.yml`. Its legacy profile requires actual GitHub
Ubuntu24 image identity; a self-hosted Ubuntu26.10 VM must not fabricate those facts.

PR1062's actual protected delivery `115f5a52ecbf203deeda327ce7abe7b9692c8265`
is integrated. This source adds a closed dedicated profile in that same gate and its existing consumers
`eng/bar_hosted_operation.py`, `eng/portable_workspace_bar.py` and
`eng/portable_workspace_sc2.py`, plus their focused controls and maintained pins.
It retains the legacy hosted profile and shares capacity, stopped-service, loop/ext4/nodiscard,
Buildah1.43.1 and actual AppArmor/maxuserns checks. Bind the dedicated profile to the
accepted root-owned image/instance/group identities. The existing manual
BAR operation is routed to the fixed group/label. Existing BAR useful-work/outer/retirement
clocks and original intent remain determining. The manual full-image/network-prerequisite job now routes to the fixed group/label.
Its added closed 60-second profile phase uses the existing preparation owner and
original clock before product/assets; unset image/provider pins refuse. No acquired
image, registration, source admission or native execution follows from this routing.
The Templates owner coordinates its actual native callers to the same baseline;
ordinary PR jobs stay hosted. No source lock here may replace a published catalog gate.

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
