# Qualification architecture performance evaluation

Date: 2026-08-29

The comparison uses exact-head GitHub-hosted runs and separates queue time, job setup, gate execution, and terminal fan-in. Run `33248808361` at merge `e5de08fbe71c1ea28f8ca0345b68d6b3ca2bd008` is the immediate pre-change baseline. Run `33250382392` exercises the first implementation head `a30c8db5eea00e80bdb58df0277530ff8215f0a2`; attempt 1 is an exact-key cache miss and attempt 2 is an exact-key hit on the same head.

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
| Settled workflow | 366s | 385s | 463s | +5.2% | +26.5% |

The implementation does not change canonical Quint code, pins, inputs, or process inventory. Its 355s/435s candidate spread versus the 332s baseline is therefore reported as hosted solver/runner variance, not attributed to the workflow refactor. The terminal workflow remains bounded by canonical Quint plus evidence fan-in even though the owned compiler and recovery routes and total runner consumption improve materially.

## Cache adoption decision

Attempt 2 restored an exact 5.8 MB cache under key `Linux-nuget-4ce94908d328a5de3a47a054a9c96dec17fdf5021a6d4462d383adeae3849af4` in roughly two seconds. Locked solution restore still took roughly the same order of time as the miss, while deterministic/package wall time increased slightly and compiler time remained dominated by architecture execution variance. Cache hit and miss both produced the same seven passing artifacts and accepted terminal evidence.

Decision: reject NuGet action caching for this workflow. The dependency graph is too small for the extra action, plan fields, generated steps, cache lifecycle, and third-party acquisition surface to provide a material win. The final candidate removes that experiment and retains locked restore plus isolated cold security/formal/recovery paths.

## Local corroboration

The retained baseline TRX attributed 267.82 aggregate test-seconds to 58 bootstrap validator cases, each launching FSI. The compiled core executes the expanded 65-test corpus, including green/red adapter parity, in about four seconds locally. The complete architecture suite passes 167/167 in about 45 seconds locally. This directly explains the hosted compiler and recovery reductions without changing gate identities or assertions.

## Acceptance disposition

- Compiler/tests improvement exceeds 30% in both candidate attempts.
- Recovery improvement is 48.5% in both attempts.
- Aggregate runner-time improvement exceeds 10% in both attempts.
- Cache miss/hit semantics are equal and the non-beneficial cache is absent from the final projection.
- Overall settled latency is disclosed as canonical-Quint-bound; #80 owns any later exact-evidence reuse that can remove repeated formal qualification from that path.
