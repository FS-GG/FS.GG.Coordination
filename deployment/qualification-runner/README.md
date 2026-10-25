# Dedicated disposable qualification runner

This source recipe is a prerequisite for `.github#4778` and Coordination#1032.
No machine, acquired image, runner registration or native qualification is accepted
by this source. `baseline.json` deliberately leaves those inputs unset. Ordinary
pull-request CI remains on GitHub-hosted runners.

The carrier is the released Ubuntu 24.04 LTS (noble) x86_64 cloud image,
`releases/noble/release-20261017`, pinned by the sha256 in its signed `SHA256SUMS`
(UEC Image Automatic Signing Key `D2EB4462...7C81`). Root chose it over Ubuntu 26.10
([#4778](https://github.com/FS-GG/.github/issues/4778#issuecomment-6390313337)) because
noble ships OpenSSL 3.0.x, which satisfies the unchanged `crypto_identity` OpenSSL-3 gate
of `eng/bar_hosted_operation.py`, while 26.10's OpenSSL 4 does not; the gate is not relaxed.
Noble carries the AppArmor `apparmor_restrict_unprivileged_userns` knob, so the
policy-zero drop-in applies. Neither SELinux nor NoNewPrivileges is disabled.

Known consequence, stated here rather than hidden: noble's glibc is `2.39`, whereas
`FS.GG.SDD`'s `CatalogScaffoldLinux.fs` accepts exactly glibc `2.44`. This carrier therefore
serves the BAR image qualification (which does not use that catalog) but **cannot satisfy
the SDD catalog scaffold gate**; the Templates callers that need that catalog remain
unrouted. The source identities and 30-export requirement for that separate supplier stay
in `baseline.json`. Governance's separate >=2.40/four-export contract is unchanged. The
accepted SDK input manifest is an input identity, not an installed image or host
qualification. Optional tracing's `liblttng-ust.so.0` remains a separate, uninstalled role.

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
   population and qualification receipts in the acquired-input lock
   (`noble/acquired-input-lock.json`, see "Noble inputs" below). No credentials
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

The GitHub Free plan has no restricted runner groups, so each ephemeral runner serves exactly one
**private** repository, and only one of two is allowed: `FS-GG/FS.GG.Native.Qualification` (the BAR
caller, see `native-qualification/`) or `FS-GG/FS.GG.FourD` (Root decision on `.github#4819`; its
`fourd-native-image-qualification.yml`, FourD #48 `f52a9bb1`). The layer below is what makes an
ephemeral Ubuntu 24.04 (noble) guest the *qualified image* for either caller. Everything slow is baked ahead of time so the caller's 300 s first-step
deadline is not spent on installation.

| Requirement | Where it is applied | Readback (`dedicated_qualification_runner.py verify-guest`) |
|---|---|---|
| AppArmor `apparmor_restrict_unprivileged_userns = 0` drop-in (image only; no job setter) | bake: `provision-image.sh`; applied by `systemd-sysctl` at boot (no script writes policy at boot) | drop-in bytes equal `90-fsgg-qualification.conf`; live value reads `0` |
| `acquired-input-lock.json` (root, 0644) | bake | present, valid, runner version equals the baked runner |
| SDK 10.0.401 / runtime 10.0.12, Go 1.27.1 | bake, from the hash-checked acquired inputs | fixed roots `/opt/fsgg/toolchains/{dotnet-10.0.401,go-1.27.1}`; exactly one SDK; `VERSION` is `go1.27.1` |
| Buildah 1.43.1 | not a guest package: the job builds the sha256-pinned Arch userland | reported as a fact; the domain gate checks the version in the job |
| Runner path, name and UID | bake: `/opt/fsgg/actions-runner`, user `runner` UID/GID 1001, `/home/runner`; names `fsgg-q-*` | account, group, path, ownership, never-configured runner |
| Runner dependencies | bake (`libicu74 libkrb5-3 zlib1g`; `libssl3t64` is in the image) instead of a live `installdependencies.sh` | `Runner.Listener` present; its version equals the lock |
| Passwordless root for `runner` | bake: `runner.sudoers`, this disposable guest only | sudoers bytes equal `runner.sudoers`, 0440 |
| The layer itself | bake: `/opt/fsgg/qualification-layer` with `MANIFEST.sha256` | every listed file hashes equal, the five required files are listed, nothing is unlisted |
| `instance-binding.json` (root, 0644) | **per boot**: `guest-boot.sh --apply REQUEST.json fsgg-q-NAME` | schema, `Default` group, label, one of the two repositories with its allowed workflow and job, `jit-config`, runner name, actual DMI UUID |

Bake once in the separately admitted image build:
`provision-image.sh --apply TARGET ACQUIRED-LOCK /mnt/fsgg-qualification-inputs`. It installs
the reviewed layer under `/opt/fsgg/qualification-layer` (with a `MANIFEST.sha256`) and ends
by running `verify-guest --phase image`; a refusal means the image must not be published.

Each boot, from the launcher's root guest script (not from a cloud-init `runcmd` line: cloud-init
persists those, so the JIT value must come from a root-only tmpfs file, as in Home's
`guest-runner.sh`), as root and in this order:

```
/opt/fsgg/qualification-layer/deployment/qualification-runner/guest-boot.sh --apply /run/fsgg/binding-request.json fsgg-q-NAME
ACTIONS_RUNNER_INPUT_JITCONFIG=$(cat /run/fsgg/jitconfig) /opt/fsgg/qualification-layer/deployment/qualification-runner/run-jit-runner.sh --run fsgg-q-NAME
```

`binding-request.json` has schema `fsgg.dedicated-qualification-binding-request/1` and exactly
`baselineId`, `providerInstanceId`, `providerImageId`, `systemUuid` (the actual guest DMI UUID),
`runnerName`, `runnerRepository`, `qualifiedImageSha256`, `rootInstanceAcceptanceSha256`,
`registrationAdmissionSha256` and `repositoryScopeVerified` (`true` only after the runner's
single-repository scope was independently read back). `runnerRepository` is required and must be exactly
one of the two targets below; the sealed binding then also carries the `allowedWorkflow` and `allowedJob`
of that row, derived by the layer and never taken from the request. The request is data, not authority.

| `runnerRepository` | `allowedWorkflow` (always on `main`) | `allowedJob` |
|---|---|---|
| `FS-GG/FS.GG.Native.Qualification` | `FS-GG/FS.GG.Native.Qualification/.github/workflows/bar-hosted-image-qualification.yml@refs/heads/main` | `full-image` |
| `FS-GG/FS.GG.FourD` | `FS-GG/FS.GG.FourD/.github/workflows/fourd-native-image-qualification.yml@refs/heads/main` | `native-qualification` |

Any other repository, a workflow or job from the other row, or a missing field refuses
(`binding_from_request`, `validate_registration_data`, and so `guest-boot.sh`, `run-jit-runner.sh` and
`verify-guest`). The BAR domain (`eng/bar_hosted_domain.py`) additionally admits only a binding whose target is
the Native.Qualification row, so a FourD-bound guest cannot be used for BAR work; FourD's own gate owns its row.
`allowedJob` is the job id, which for both workflows is also the job-record `name` that each workflow's first step
compares (neither sets a `name:` override; a workflow that did would have to change its table row in the same
reviewed change). The host-side allowed-workflow list of the runner launcher must name the same workflow for the
same repository.
Neither script accepts a PAT or registration token, uses `--replace`, or reuses credentials;
`run-jit-runner.sh` takes the one-job JIT configuration only through the supported
`ACTIONS_RUNNER_INPUT_JITCONFIG` environment input and refuses unless the baked layer and
sealed binding read back clean immediately before it `exec`s the runner.

OpenSSL: the guest's `openssl` package must be the 3.x family (noble: `3.0.13`), because
`eng/bar_hosted_operation.py` `crypto_identity` accepts only `OpenSSL 3.`. `verify-guest` reads
the installed package version (nothing is executed from the guest) and reports
`openssl-family-not-3` otherwise, so a later carrier with OpenSSL 4 stays refused.

A baked runner ages. The retired registration path passed `--disableupdate`; whether a
JIT-launched runner self-updates is unverified and `run-jit-runner.sh` cannot control it, so
re-bake the current runner before GitHub's runner update deadline instead of spending an
update, or running unpinned bytes, inside the 300 s first-step window.

## Noble inputs: acquisition, lock and expected inventory

`noble/` holds the reviewed, credential-free records of the acquired-input population:

| File | Meaning |
|---|---|
| `acquired-input-lock.json` | schema `fsgg.dedicated-qualification-acquired-inputs/1`; image, SDK, runner, Go, compiler identities and the complete 76-row file population (path, sha256, bytes, origin URL) |
| `package-inventory.expected` | the exact `dpkg-query` inventory (717 packages) the bake must reproduce: the image's 664 packages, 4 upgrades and 53 additions |
| `apt-transaction.json` | the signed transaction: immutable Ubuntu snapshot `20261024T000000Z`, signer, the three suites' InRelease digests, the install request and every locked `.deb` |
| `image-acquisition-receipt.json`, `image/SHA256SUMS(.gpg)` | the signed checksum file, its detached signature and what was verified |
| `runner-release.json` | the GitHub release record whose asset digest pins runner 2.338.1 |

Verification chain, all without credentials: the image sha256 comes from `SHA256SUMS`, whose
detached signature verifies under the UEC Image Automatic Signing Key (the same fingerprint
was fetched from keyserver.ubuntu.com and appears in `docker.io/library/ubuntu:24.04`'s
`ubuntu-cloudimage-keyring.gpg`). APT inputs are the archive's own `InRelease` files (Ubuntu
Archive Automatic Signing Key 2018, `F6ECB376...C93C`), their `Packages.xz` hashes, and each
`.deb` against the signed index; the guest's apt re-verifies the same signatures offline.
The SDK bytes equal the accepted sha512 pin, the runner bytes equal GitHub's release digest
(and Home's r3 pin `08e9ccb3...`), and Go 1.27.1's sha256 is go.dev's published value.

`noble-inputs.py acquire --out FRESH_DIR` downloads exactly the locked URLs and verifies all of it;
`noble-inputs.py verify --inputs DIR` re-verifies an existing population; `provision-image.sh` then
runs `dedicated_qualification_runner.py validate-inputs` over `/mnt/fsgg-qualification-inputs`.
Package-level compiler identities in the lock (`compilerSha256`, `headersManifestSha256`,
`linkerSha256`, `runtimeClosureSha256`) hash the locked package set per role; actual cgo/race stays a
separately selected qualification.

The expected inventory was produced, not guessed: the cloud image was extracted rootlessly
(`qemu-img convert` plus `debugfs rdump` of its root partition) in a scratch directory; its
`dpkg` package set equals the signed `.manifest` exactly; then `simulate-noble-bake.sh` (the same
`noble-apt.sh` and `runner.sources.list` as the bake, network disabled, apt sandbox user off only
because rootless containers cannot map `_apt`) installed the transaction offline and wrote the
inventory. Regenerate everything with `noble-inputs.py lock` after choosing a new snapshot.
The image-build VM must be offline from mirrors and must not have run `apt-daily` or
`unattended-upgrades` before the bake, or the byte-for-byte inventory comparison refuses.

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
legacy profile requires actual GitHub Ubuntu24 image identity; a self-hosted Ubuntu 24.04 dedicated VM
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

- [Ubuntu 24.04 cloud image releases](https://cloud-images.ubuntu.com/releases/noble/)
- [Pinned release SHA256SUMS](https://cloud-images.ubuntu.com/releases/noble/release-20261017/SHA256SUMS) and its [signature](https://cloud-images.ubuntu.com/releases/noble/release-20261017/SHA256SUMS.gpg)
- [Ubuntu archive snapshot service](https://snapshot.ubuntu.com/)
- [.NET supported OS policy](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [GitHub self-hosted runner requirements](https://docs.github.com/en/actions/reference/runners/self-hosted-runners)
- [GitHub ephemeral runner guidance](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/autoscale)
- [Runner supported environment inputs](https://github.com/actions/runner/blob/main/src/Runner.Listener/CommandSettings.cs)
