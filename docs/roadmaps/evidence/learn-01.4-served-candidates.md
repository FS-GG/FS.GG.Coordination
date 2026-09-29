# LEARN-01.4 served-candidate evidence

Status: candidate distribution preparation qualified, 2026-09-29. This evidence binds the Host and runner archives prepared from protected Coordination commit `c6e378e1d00e49eda5ee372bc50cd294460be04a`, tree `344a4bd42b2938ce5cc062246bd36bdac7637e53`.

[#887](https://github.com/FS-GG/FS.GG.Coordination/pull/887) merged qualified head `6558949469f7a58872201f67a3a2f231a254339e` as that protected commit. The exact-head [bootstrap run](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36535183320) and [coherent run](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36535183649) passed. The native PostgreSQL 18.6 job passed 40/40 restart, idempotence and conflict tests.

## Immutable identities

| Candidate | Hosted evidence | Immutable identities |
|---|---|---|
| Orchestration Host, Linux x64 | [Run 36541708227](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36541708227); [candidate artifact 11020513847](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36541708227/artifacts/11020513847); [verification artifact 11020803091](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36541708227/artifacts/11020803091) | GitHub artifact SHA-256 `fd74a7d38c3a71f3b1367944e782bf86fb7abaab8c03bbe242b08d8b33274a6d`; archive SHA-256 `7e735ffd5314011fb95e9998ff4bfeedc76aafac36efcf40e719491c99f7f878`; payload SHA-256 `1a93e700b623fa61bbde0b4eb48d14f2f382f6bb897e5dcab3cea7ea43a264ea`; verification SHA-256 `3ebc7025c174590948dba22347ee569567a7354e046d76d9dba3bc9087dac711` |
| Orchestration runner client, Linux x64 | [Run 36541711540](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36541711540); [candidate artifact 11020737943](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36541711540/artifacts/11020737943); [verification artifact 11020907640](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/36541711540/artifacts/11020907640) | GitHub artifact SHA-256 `f8e89c14a0c027a321d3bd676f2905d4b6f3369f8122d919cb028e6e9097dbc8`; archive SHA-256 `7f9fe80e05648d7b4c977bf5605f70d701d9cebf2f000bb376f7b4d98b676506`; payload SHA-256 `6cdccd0fd9221b9f02070cf0093ccf4f242fe285f1c5881627999a0106c246cf`; verification SHA-256 `e7238b442052921bb2ac67e9d7e6822524d3ca23d2451d7b21e30e235e30efac` |

Both attempt-1 workflows ran on protected `main` with `contents: read`. Each built twice, proved byte-identical archives, uploaded one candidate, downloaded it into a fresh directory, rehashed and executed the served archive, and retained a terminal verification receipt. The workflows retain both artifacts for 90 days.

Independent qualification downloaded all four artifact ZIPs by ID. Their outer hashes matched the GitHub artifact API digests. The repository-native `verify` and `verify-served` helpers accepted fresh copies using the exact artifact IDs, names, URLs and digests. Their generated verification receipts were byte-identical to the hosted receipts. Both helper self-tests rejected all 11 workflow, archive-layout, timestamp, mode, RID, size, reproducibility and artifact-route mutations.

The Host readback exercised inert usage and production-command parsing plus unknown-option refusal without database or network authority. The runner readback exercised usage, oversized-request refusal before credential or network access, closed-frame refusals and a readiness exchange against a local `--version` and `login status` sentinel. It invoked no model or provider service.

## Remaining installed boundary

These are qualified served candidates in GitHub Actions storage. They are not public release or package-feed promotion, an installed Main adoption, proof of an actual Codex executable/account/model capability, authorization to open the learning window, or evidence of complete population, native capture, provider usage or shared-cost allocation.

Main/SystemAdmin still owns authenticated readiness, approved configuration destination and applicable effect authority. Installed qualification must bind these exact archive identities, exercise clean and retained upgrade routes, and prove provider support and complete prospective capture before enrollment. LEARN-01.4 remains open until the installed current/focused window reaches its locked dataset/report or genuine stopped-window endpoint.
