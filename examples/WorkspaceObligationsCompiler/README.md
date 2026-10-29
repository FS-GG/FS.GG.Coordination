# Source-bound workspace obligation compiler

This nonpackable reference project composes published SDD.Artifacts2.5.3 data with
Coordination.Core's existing `OrdinaryAssignment.AuthoritativeObligations`. It has
no Host/Main reference, production route, reservation allocation or package release.

`Compiler.compile` takes existing artifact bytes, an independently accepted model
fingerprint and module/source mapping, explicit physical components, independent
completeness and original `Requirements` facts. Its small input types are in-memory
call parameters, not another workspace protocol. It uses the existing SDD parsers,
workspace lifecycle, authored work-model producer and ordinary authority digest.
It never evaluates input code, invokes declared commands or marks a capability
available. `productionProjection` always refuses.

The compiler checks raw hashes, exact accepted-module coverage, current generated
work against regeneration from independently supplied authored bytes, independent
expected work keys and complete original-fact population. Additional source closure
(code, profile, catalog and provenance) stays separate from lifecycle producer inputs.
Component sources must lie under their actual relative root; `Root="."` refuses.
Source path and AllowedPaths protections remain unchanged. Generated products use
the actual prepared descriptor and effective parameters joined to schema2/3
provenance through the existing readers. Tool versions, language, product shape and
effective parameters remain in the referenced typed source declarations.

Sources and original policy/operation facts are supplied by their accountable
custodians. Matching bytes and a model fingerprint do not authenticate a supplier,
prove repository history, observe an installed tool or grant operation authority.
The caller supplies the observation time; the original source deadline is never
renewed. Missing source/facts, contradictory mappings, unsupported versions and
exhausted original clocks refuse. Source bytes are bounded to1MiB each/8MiB total,
128 source inputs,32works,16components and8repositories; existing canonical bounds
also apply. These are source-composition limits, not a native containment profile.

## The genuine1081 source cohort

The first live corpus is `work/1081-workspace-obligations-compiler`, authored for this
Delivery. Programme/root independently selected T001–T004 and EV001–EV004, and
[accepted the exact model/profile](https://github.com/FS-GG/FS.GG.Coordination/issues/1081#issuecomment-6498296777)
at13:41:48.939187UTC on2026-10-29. The accepted revision1 fingerprint is
`f04022214c6525525016da008d93551db60cec085769fd22f65de49183b70cca`.
`ReferenceConsumer.accept` used the published lifecycle's exact-base reduction with
that actual actor, evidence and time. The first proposal remains unaccepted history.

The authored task/evidence files preserve the work declaration at source selection;
pending/missing declarations do not claim completed outputs. Actual qualification
receipts and Root's subsequent source acceptance remain separate. Changing an
accepted authored byte needs a reconciled proposal/acceptance, not a regenerated
fingerprint silently substituted by the caller.

`Source1.fs` records Root's actual source-only assignment, original13:14:12.430911UTC
start and16:14:12.430911UTC deadline, declared resource demand and independently
supplied semantic/acceptance obligations. It is neither a ProviderOriginal nor a
native retry. Root's subjective assessment remains its original judgement; only the
three resolved input gaps were cleared after acceptance. The explicit local custody
label joins actual base666e0ff and Root's authored manifest digest; it does not claim
new local bytes existed in that Git commit. Every consumed file also has its raw hash.

Physical components separately bind authored `work/…`, reference `examples/…`,
maintained `.fsgg` and generated `readiness/…` source paths. The workspace locator is
the actual caller-selected checkout path, independently supplied by Root for this run.

## Build and invoke

```sh
dotnet restore examples/WorkspaceObligationsCompiler/WorkspaceObligationsCompiler.fsproj --locked-mode
dotnet build examples/WorkspaceObligationsCompiler/WorkspaceObligationsCompiler.fsproj --configuration Release --no-restore
dotnet examples/WorkspaceObligationsCompiler/bin/Release/net10.0/WorkspaceObligationsCompiler.dll reference /absolute/path/to/the/1081/checkout
dotnet test tests/FS.GG.Coordination.WorkspaceCompiler.Tests/FS.GG.Coordination.WorkspaceCompiler.Tests.fsproj --configuration Release
```

The `reference` invocation calls the real typed API with Root's fixed source-1
facts/accepted fingerprint and current observation time. It refuses after the
original deadline. It prints a source authority digest and work keys, with production
projection explicitly unsupported; it does not persist an execution protocol.
The API also supports other independently supplied data without reusing this
example's source operation.

The `propose ROOT` and `accept ROOT FINGERPRINT ACTOR EVIDENCE TIME` helpers demonstrate
the existing lifecycle over this cohort. A proposal is not acceptance. The latter
requires an actual authorized acceptance supplied by the caller; strings do not
authenticate their author. Never rerun these helpers to erase original history or
fabricate Root approval.

The isolated reference and controls projects explicitly override Artifacts2.5.3 and
FSharp.Core10.1.401. The reference lock has five public packages plus Core/Protocol;
the controls add their normal test-only dependencies. Neither graph imports
Qualification.Contracts or Host. The global Artifacts[1.5.0] pin and existing locks
remain unchanged. The new controls run explicitly; registering the projects in the
solution establishes compilation, not evidence that a named test partition ran.

Control fixtures are marked synthetic and never certify a generated provider.
The suite separately invokes the real cohort consumer and changes independently
selected source/population/fact inputs to demand causal refusals. Published source
composition and tests do not establish runtime capabilities, physical Governance423(b),
containedglibc2.44, installed adoption, publication or wholeSDD928 acceptance.
