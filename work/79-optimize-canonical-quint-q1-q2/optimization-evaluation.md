# Canonical Quint optimization evaluation

Date: 2026-08-29

All local measurements used the pinned Quint `0.32.0` binary, Apalache
`0.56.1`, Temurin 21.0.9+10 JRE, the canonical GS2-02.11 generated model,
and identical assertions. Times are wall-clock seconds. Hosted exact-head
measurements are recorded below when the candidate workflow runs complete.

## Bounded Rust mutation concurrency

The representative workload ran four independent
`quint test --backend rust --match ^test` processes per sample. Five samples
were taken for each explicit cap.

| Cap | Samples (s) | Median (s) | p95 (s) | Outcome |
| ---: | --- | ---: | ---: | --- |
| 1 | 11.541, 11.460, 11.557, 11.715, 11.671 | 11.557 | 11.715 | 4/4 checks passed in every sample |
| 2 | 5.956, 6.006, 5.950, 5.939, 6.031 | 5.956 | 6.031 | 4/4 checks passed in every sample |
| 4 | 3.168, 3.183, 3.162, 3.174, 3.209 | 3.174 | 3.209 | 4/4 checks passed in every sample |

Decision: adopt cap 2. It reduced the representative median by 48.5% while
keeping peak concurrency and resource pressure below the fastest cap. The
production scheduler covers the 41 independent Rust mutation checks only;
the 14 Apalache checks remain sequential and retain distinct counterexample
artifacts. The complete local qualification passed with exactly 8 positive
invariants, 56 rejected controls, 61 Quint invocations, and 14 Apalache
verifications. Its Q2 duration fell from 184.697s to 139.929s and total
duration from 224.337s to 175.504s (21.8% total reduction).

## Persistent Apalache server

The positive eight-invariant verification was sampled five times in each
mode.

| Mode | Samples (s) | Median (s) | p95 (s) | Outcome |
| --- | --- | ---: | ---: | --- |
| Per-process | 8.250, 8.125, 8.501, 8.229, 8.135 | 8.229 | 8.501 | invariant held in every sample |
| Server | 6.234, 5.196, 5.201, 5.132, 4.569 | 5.196 | 6.234 | invariant held in every sample |

Decision: reject. Although the narrow median improved by 36.9%, the full
qualification stalled for more than 172 seconds on the first
counterexample-producing verification (`relationChangesPreserveUnrelatedEdges`)
and had to be terminated after total elapsed time exceeded the prior full-run
baseline. Server reuse therefore failed the stable-completion requirement and
is not present in the production validator.

## Hosted exact-head comparison

Candidate head: `7764fd94d88a7bfe1d22bb6c1fcd408ac67d8a7c`.
All candidate attempts passed with the identical 8-positive, 56-negative,
61-Quint, and 14-Apalache inventory. p95 uses the nearest-rank value, which is
the maximum of a five-sample cohort.

The baseline is the five most recent successful `main` runs before this
change. Formal qualification was then serialized inside `compiler-and-tests`.

| Baseline run | Workflow (s) | Compiler job (s) | Formal step (s) |
| --- | ---: | ---: | ---: |
| [33241482759](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33241482759) | 914 | 888 | 617 |
| [33239786404](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33239786404) | 853 | 830 | 582 |
| [33232073099](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33232073099) | 708 | 685 | 485 |
| [33228843157](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33228843157) | 904 | 881 | 617 |
| [33224839552](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33224839552) | 904 | 880 | 611 |
| **median** | **904** | **880** | **611** |
| **p95** | **914** | **888** | **617** |

The candidate cohort is five attempts of the same exact head. Attempt 1 is the
full workflow under ordinary sibling-job load. Attempts 2–5 rerun the
canonical job alone, retaining the same hosted image, pins, command, and head;
this resource-context distinction is intentional and explicit.

| Candidate attempt | Job (s) | Formal step (s) | Q1 (s) | Q2 (s) | Receipt total (s) |
| --- | ---: | ---: | ---: | ---: | ---: |
| [1](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33245399602/attempts/1) | 483 | 465 | 94.688 | 340.750 | 435.438 |
| [2](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33245399602/attempts/2) | 340 | 324 | 67.872 | 237.450 | 305.322 |
| [3](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33245399602/attempts/3) | 416 | 403 | 76.136 | 306.404 | 382.540 |
| [4](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33245399602/attempts/4) | 409 | 394 | 73.971 | 299.363 | 373.334 |
| [5](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33245399602/attempts/5) | 393 | 378 | 70.586 | 287.922 | 358.508 |
| **median** | **409** | **394** | **73.971** | **299.363** | **373.334** |
| **p95** | **483** | **465** | **94.688** | **340.750** | **435.438** |

The candidate formal-step median is 35.5% below baseline and its p95 is 24.6%
below baseline. The ordinary fully concurrent candidate workflow completed in
509s, 43.7% below the 904s baseline median. Only one full-topology candidate
sample exists because the four measurement reruns intentionally isolated the
formal job; no multi-sample workflow p95 is claimed.
