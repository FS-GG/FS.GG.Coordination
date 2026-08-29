# Exact-head qualification reuse performance

## Measurement method

Wall time is GitHub's run `created_at` to `updated_at`. Runner time is the sum of completed successful job `started_at`/`completed_at` intervals from the Actions jobs API; skipped contexts contribute zero. These values are observational and do not authorize reuse. Every source below is a completed successful run of `.github/workflows/bootstrap-qualification.yml` on 2026-08-29.

## Comparable pre-change cohort

| Run | Head | Route | Wall | Runner time |
|---|---|---:|---:|---:|
| [33253358688](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33253358688) | `1627adb` | full execution | 424s | 829s |
| [33252878834](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33252878834) | `0aa5c6e` | full execution | 450s | 825s |
| [33252344338](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33252344338) | `c3bd5bc` | full execution | 444s | 905s |

The cohort median is 444 wall-seconds and 829 runner-seconds.

## Execute/reuse pair

The pair has different commit SHAs but the same Git tree `30c9b48940f9a598af170183049bde9f0494693c`.

| Run | Head | Decision | Decision job | Terminal job | Wall | Runner time |
|---|---|---|---:|---:|---:|---:|
| [33255549867](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33255549867) | `f0d92f2` | `execute: no-compatible-prior` | 22s | 29s | 522s | 922s |
| [33255929882](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33255929882) | `f36712b` | `reuse: identical-complete-tree` | 22s | 27s | 55s | 49s |

The reuse route saved 467 wall-seconds (89.5%) and 873 runner-seconds (94.7%, 14m33s) relative to its exact subject source. It settled in 55 seconds, below the 180-second target, and avoided more than the required 10 runner-minutes. The execute miss added 78 wall-seconds over the comparable cohort median, below the 90-second ceiling.

The reused terminal manifest names current candidate `f36712bf14aad355a134919ad134c03c317a488c`, prior run `33255549867`, prior head `f0d92f2d4ae05f75e0a8971c64dc817181eed10e`, subject digest `4f886334d02eb84724ffdcaa1c1480da3431231f61d445fd4f5f356bf0fb0bc6`, and all seven retained artifact digests. All six execution jobs were skipped; `evidence-manifest` succeeded after cross-run download and byte revalidation.

## Telemetry correction

The first hosted reuse proved the authorization and artifact route but exposed that its receipt serialized `runnerMinutes: 0` instead of the measured 15.366667 source runner-minutes. The implementation now derives the value from the completed run's bounded job census and represents an unavailable measurement as `null`; route selection remains unchanged for measured versus unavailable telemetry. This prevents a missing metric from becoming either a false zero or an authorization input.
