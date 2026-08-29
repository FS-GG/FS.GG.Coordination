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

Pending candidate and baseline run collection. This section will record five
samples per cohort, medians, p95 values, job/phase durations, immutable run
links, and the exact candidate head before acceptance.
