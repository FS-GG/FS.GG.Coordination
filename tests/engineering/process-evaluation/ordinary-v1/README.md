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

## Error and output observation follow-up

The next local investigation keeps the five fixture meanings and the earlier source-window
observations intact. Baseline and CliWrap now record caller-owned byte-reader EOF separately from
cancelled/faulted reads; ProcessKit's tee API does not expose EOF, so it retains `not-exposed` reader
observations separately from capture completeness. Library task settlement (`completed`, `cancelled`,
`faulted`, `unresolved`) and returned result errors are separate fields. A completed error-returning
task is not successful command execution or observed cleanup. Public ProcessKit error exit codes are
retained when available; absent codes remain unknown. Cancellation registration preserves the first
cause before later sink failures. This remains evaluator-only source, not the shared .2 contracts.

`ObservationControls.cs` supplies thirteen pure controls for shared byte charging, retained first
cause, task settlement and the source-pinned consumer meanings in `consumer-controls.json`. SDD
held-pipe mapping requires positive prior-exit evidence; an unknown prior exit cannot become either
held-pipe or child timeout. FsQuint's output/cancel/deadline causes remain distinct, and mechanical
completion cannot establish executable identity, version or Quint domain success. These are meanings
checked in the evaluator, not the existing consumers' acceptance tests. The root-scheduled follow-up
build and its serial fixture findings are retained separately in `contract-observations.json`.

## Prepared finite sink-failure slice

The optional fifth evaluator argument `sink-failure` accepts only the existing finite
`ordinary-dual` fixture. Its stdout sink charges the attempted bytes, retains none from the failed
write, records `sink-failure` before throwing, and preserves any earlier cause. Normal mode keeps the
existing fixture meanings. Three added pure controls specify this non-overflow failure, first-cause
preservation and a negative case where disabling fault injection cannot satisfy the failure assertion.
These controls and the changed C# source were **uncompiled and unexecuted** when this slice was
prepared. The later qualification below records the scoped compiled result.

`run.py --sink-failure-only` prepares three serial baseline/CliWrap/ProcessKit observations. A guard
failure, missing observation or unresolved registered fixture generation stops affected collection.
The earlier13 pure/15 ordinary observations remain historical; they do not validate this new source.
`sink-source-preparation.json` records static checks and the deferred qualification boundary.

The historical build manifest stays intact. Source mismatch intentionally refuses the old binary;
using `run.py` for the new mode requires a separately qualified current manifest. The original
proposed 30s source window granted no workload authority. Later admitted operations used their own
resource and custody boundaries. Every future effect requires current admission; no historical
window or resource exception transfers. Candidate exit/cleanup, ProcessKit tee EOF and full
descendant coverage remain unknown.

## Private observation source preparation

Five added pure controls give twenty-one total. These exact C# bytes have since been compiled,
and all twenty-one pure controls passed in a separately admitted operation.
Exit observation requires a present public code, including ProcessKit success. CliWrap's public
success result can supply a code if it has settled when sampled after a remaining-time failure;
fault/cancellation exposes no code through this API. No handle is acquired from a bare library
PID. ProcessKit tee EOF stays not exposed. Task settlement is separate from cleanup evidence.

The exact current source manifest is embedded in `sink-source-preparation.json`. Historical
`build-manifest.json` remains unchanged and rejects the new sources. Separate retained qualification
evidence joins the current C# bytes to the compiled artifacts; `run.py` still refuses the historical
binary and is not the entry point used for that qualification.

The bounded wait records an observed `TimeoutException` as deadline before timer freeze, even
when the cancellation token has not fired. The added uncancelled-token control would fail if that
recording guard were removed; a late successful public exit preserves the first timeout cause.

## Scoped qualification evidence

A separately admitted locked restore and build produced a 23-file compiled closure. The current
`Program.cs` SHA256 is `ef715564398feb3d3d726bf288fe9fff43ef5c8b73cca444ee08d2bef58405a8`;
`ObservationControls.cs` is `ad1332391b335babdfc789a9b7b321dff70fc17e281d4c38aca6afe67b4408c7`.
The compiled evaluator DLL is `725793c8fe591c815e4a9599f309fbc99245a1d6f3b0dd93e27bbea4085eaa16`.
All twenty-one compiled pure meaning controls passed. One subsequent finite baseline
`ordinary-dual/normal` observation recorded 512 charged and retained bytes, leader exit 0, both reader
EOFs, completed read tasks and no failure cause within its 1000ms total. Its separate native custody
operation recorded known owned cleanup and complete descriptor closure. The evaluator's own
descendant/reaping cleanup remains unknown, and its acceptance field remains `not-established`.

These observations used an existing reviewed custody entry point, not the historical `run.py`.
The admitted native scope passed independently of the broader standalone guard result. Earlier
failed attempts remain failed; the consumed baseline operation must not be replayed. The new-source
CliWrap/ProcessKit matrix and sink-failure observations, production SDD/FsQuint acceptance, complete
descendant coverage and the V2-PROC-01.1 reuse/adaptation decision remain open. Exact private
operation artifacts are retained by the programme owner; no backend or consumer adoption follows
from these pure controls or the single baseline observation.
