# Ordinary evaluator v1

This separately identified investigation continues V2-PROC-01.1. The unavailable evaluator
`8b56f4cce419b8e727f8acb96507025c44a01d51` and its A1–A4 definitions remain unknown.
The five `ordinary-*` identifiers in `inputs.json` have new explicit meanings. This source is
an evaluation harness, not a shared API, backend choice, consumer migration or containment supervisor.

`Program.cs` compares a minimal .NET Process baseline, CliWrap 3.10.5 and ProcessKit 2.12.0
through public APIs. Both streams share a 4096-byte caller retention cap. The timer permits 500ms
of work inside a 1000ms total, or requests cancellation at 100ms. Baseline cleanup uses remaining
time; candidate tasks use a remaining-time wait that reports unresolved work rather than claiming
it stopped. No success is inferred from cancellation or library task completion. Inherited environment
is explicit; only bounded output prefixes and error type names are recorded.

`fixture.py` emits finite attributable output, overflow, deadline/cancel sleepers or an inherited
writer whose leader exits first. Sleepers self-expire after 1500ms. `run.py` owns a fresh evaluator
session, clips its diagnostic output, and uses an independent 3000ms outer guard. Its separate
natural-expiry observation window does not extend the evaluator's total budget or qualify cleanup.
It fresh-reads registered PID/start-time generations after expiry and reports absence, live,
zombie, different-generation or unknown. Registration covers fixtures only; helpers and complete
descendant/reaping coverage remain unknown. No foreign PID is signaled.

The local `global.json` and runtime configuration select SDK 10.0.401 / runtime 10.0.12 with
roll-forward disabled. Local build property files isolate the evaluator from repository consumer
package defaults. Exact direct and transitive versions are in `packages.lock.json`; downloaded
archives, declared repository commits, source archive hashes and installed runtime file hashes
are in `inputs.json`. Nuspec source metadata is a declared join, not reproducible-build evidence.
ProcessKit's declared source project version is 2.11.0 although its package is 2.12.0. Complete
SDK/compiler and loaded native dependency closures still need stronger evidence before qualification.

A root-scheduled one-CPU slot built this source and ran fifteen serial ordinary observations.
`observations.json` records the bounded results and preparation failures. Every dual-stream case
returned the exact expected 512 bytes; every overflow retained at most 4096 bytes and preserved its
cause. Baseline held-pipe output remained incomplete after leader exit at its 1000ms bound.
Candidate error paths currently leave exit and cleanup unreported. All registered fixture generations
were absent after the independent outer expiry window; that is a limited observation, not whole-tree
cleanup. One ordered sample per case supports no latency, throughput, leak or benefit claim.

For another explicitly scheduled slot, verify downloaded package hashes against `inputs.json`,
restore from those pinned archives into a fresh private package cache, and build from this directory:

```console
dotnet restore OrdinaryEvaluation.csproj --configfile /private/pinned/NuGet.Config --locked-mode -p:NuGetAudit=false -p:RestorePackagesPath=/private/cache
dotnet build OrdinaryEvaluation.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
python3 run.py --output /private/new-observations
```

The NuGet configuration must clear other sources and point only to the five verified archives.
`run.py` requires a new output directory and verifies runtime-host/interpreter pins. It does not
restore or build. Allow one evaluator CLR plus guard and up to two fixture processes, with build
compiler processes counted separately. Execution requires the integrator's resource slot. Further
work is to adapt public error paths into bounded exit/EOF/cleanup observations, run meaningful
negative controls and existing consumer fixtures, and independently admit any contained evaluation.
V2-PROC-01.1 remains open; existing consumer implementations stay selected.
