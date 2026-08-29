# Qualification architecture performance evaluation

Date: 2026-08-29

The five-run cohort uses exact-head GitHub-hosted runs. The [immediate pre-change baseline](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33248808361) is merge `e5de08fbe71c1ea28f8ca0345b68d6b3ca2bd008`. [Candidate attempt 1](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33250382392/attempts/1) is an exact-key cache miss and [attempt 2](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33250382392/attempts/2) is an exact-key hit on the same head. The [cache-free candidate](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33251281115) and [receipt-bound cache-free candidate](https://github.com/FS-GG/FS.GG.Coordination/actions/runs/33251621507) supply two further successful samples.

Queue is workflow creation to the earliest prerequisite job start. Setup is the aggregate prerequisite-job time through completion of checkout and setup-dotnet. Subject execution is the aggregate time after setup-dotnet through the start of evidence upload. Fan-in is the terminal evidence job. Runner time sums all seven job durations; settled total is workflow creation through terminal completion. These definitions use the linked GitHub job and step timestamps rather than log prose.

| Sample | Queue | Setup | Subject execution | Fan-in | Runner total | Settled total |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Baseline | 2s | 31s | 1033s | 22s | 1103s | 366s |
| Cache miss | 3s | 25s | 708s | 23s | 778s | 384s |
| Cache hit | 2s | 29s | 854s | 26s | 934s | 465s |
| Cache-free | 3s | 26s | 797s | 18s | 861s | 431s |
| Receipt-bound cache-free | 49s | 24s | 775s | 24s | 840s | 478s |

## Route comparison

| Route | Baseline | Candidate miss | Candidate hit | Miss change | Hit change |
| --- | ---: | ---: | ---: | ---: | ---: |
| Compiler/tests job | 315s | 132s | 188s | -58.1% | -40.3% |
| Architecture test execution | 275s | 100s | 137s | -63.6% | -50.2% |
| Bootstrap recovery job | 338s | 174s | 174s | -48.5% | -48.5% |
| Deterministic build | 40s | 34s | 38s | -15.0% | -5.0% |
| Package smoke | 27s | 29s | 32s | +7.4% | +18.5% |
| Dependency/security | 29s | 31s | 41s | +6.9% | +41.4% |
| Canonical Quint | 332s | 355s | 435s | +6.9% | +31.0% |
| Evidence fan-in | 22s | 23s | 26s | +4.5% | +18.2% |
| Aggregate runner time | 1103s | 778s | 934s | -29.5% | -15.3% |
| Settled workflow | 366s | 384s | 465s | +4.9% | +27.0% |

The implementation does not change canonical Quint code, pins, inputs, or process inventory. Its 355s/435s candidate spread versus the 332s baseline is therefore reported as hosted solver/runner variance, not attributed to the workflow refactor. The terminal workflow remains bounded by canonical Quint plus evidence fan-in even though the owned compiler and recovery routes and total runner consumption improve materially.

## Cache adoption decision

Attempt 2 restored an exact 5.8 MB cache under key `Linux-nuget-4ce94908d328a5de3a47a054a9c96dec17fdf5021a6d4462d383adeae3849af4` in roughly two seconds. Locked solution restore still took roughly the same order of time as the miss, while deterministic/package wall time increased slightly and compiler time remained dominated by architecture execution variance. Cache hit and miss both produced the same seven passing artifacts and accepted terminal evidence.

Decision: reject NuGet action caching for this workflow. The dependency graph is too small for the extra action, plan fields, generated steps, cache lifecycle, and third-party acquisition surface to provide a material win. The final candidate removes that experiment and retains locked restore plus isolated cold security/formal/recovery paths.

## Final cache-free confirmation

Run `33251281115` qualified the cache-free head `c88ac69acf167a0ffc832e9e9be53fdb06cf0ca9` with all seven jobs green. It confirms that the retained improvement comes from compiled validation and the reduced control surface, not caching.

| Route | Baseline | Cache-free candidate | Change |
| --- | ---: | ---: | ---: |
| Compiler/tests job | 315s | 165s | -47.6% |
| Bootstrap recovery job | 338s | 173s | -48.8% |
| Deterministic build | 40s | 42s | +5.0% |
| Package smoke | 27s | 25s | -7.4% |
| Dependency/security | 29s | 30s | +3.4% |
| Canonical Quint | 332s | 408s | +22.9% |
| Evidence fan-in | 22s | 18s | -18.2% |
| Aggregate runner time | 1103s | 861s | -21.9% |
| Settled workflow | 366s | 431s | +17.8% |

The final candidate again exceeds both owned-route and runner-consumption thresholds. Its settled workflow remains entirely canonical-Quint-bound: the unchanged formal route ran 76 seconds above baseline while compiler/tests and recovery finished roughly 150 and 165 seconds earlier respectively.

The later receipt-bound cache-free sample independently recorded compiler/tests at 170 seconds (-46.0%), recovery at 150 seconds (-55.6%), aggregate runner time at 840 seconds (-23.8%), and canonical Quint at 403 seconds. Its 49-second queue is reported separately; it is not executable regression.

## Local corroboration

The retained baseline TRX attributed 267.82 aggregate test-seconds to 58 bootstrap validator cases, each launching FSI. The compiled core executes the expanded 74-test corpus, including green/red adapter parity, representative-gate change amplification, seven direct missing-subject entry-point inversions, and a source-linked performance-evidence check, in about five seconds locally. The complete architecture suite passes 176/176 in about 46 seconds. This directly explains the hosted compiler and recovery reductions without changing gate identities or assertions.

## Acceptance disposition

- Compiler/tests improvement exceeds 30% in all four candidate attempts.
- Recovery improvement exceeds 48% in all four candidate attempts.
- Aggregate runner-time improvement exceeds 10% in all four candidate attempts.
- The final cache-free run reduces aggregate runner time by 21.9%, compiler/tests by 47.6%, and recovery by 48.8%.
- Cache miss/hit semantics are equal and the non-beneficial cache is absent from the final projection.
- Overall settled latency is disclosed as canonical-Quint-bound; #80 owns any later exact-evidence reuse that can remove repeated formal qualification from that path.

## Review repair provenance

Independent review identified that `BootstrapRecoveryTests.fs`—changed when recovery validation moved from the FSI adapter to the compiled core—was omitted from the live issue and route touch sets. The held declaration was widened in place, route decision revision 3 (`96000776c1aa3d0fab7ced039b03b52661ad7a30514a4955dd5e7b66d98efe7b`) records the same SDD route with that path added, and the live `verify-paths` command now reports `FSGG-PATHS OK`. This is an authority/provenance repair; it changes no qualification behavior or timing claim.
