# FsQuint replay dependency

FSQUINT-01 moves generic replay ownership to the public FsQuint package. Host and
journal tests pin 0.1.0-preview.2 through central package management and lock files.
The linked ReplayHarness implementation is removed. Drivers implement the package
Initialize/Apply/Observe/Cleanup contract and require both Equivalent and clean cleanup.
The Host test project no longer needs SDD for replay types; Qualification.Contracts
keeps its unrelated, reviewed SDD compiler dependency at 1.5.0.

ChoreoTrace uses FsQuint's bounded raw ITF reader before applying Coordination-owned
source pins, exact metadata, operation-envelope guards and milestone projection.
The eight raw fixtures remain unchanged (180 states, 69 milestones). Models, fairness,
projection and production actor/PostgreSQL semantics stay here. No installed activation
is part of this change; the separate O3 operational follow-up remains unchanged.

Evidence reuse now binds the central package pin, Host package lock and projection/
driver sources. Mutation tests show those changes invalidate formal correspondence
reuse; unrelated-file positive controls remain. A library update does not select a
new Quint tool version, alter model bounds or refresh golden traces automatically.

Public-package checks: Host 78, PostgreSQL 34; journal 3 and correspondence/reuse
architecture 107 passed during migration. The initial migration passed full canonical qualification: Q1/Q2, eight positive
invariants and 166 negative controls, with unchanged model/tool fingerprints and
budgets. See [PR429](https://github.com/FS-GG/FS.GG.Coordination/pull/429).

Generic defects are fixed in FsQuint first. Renovate's NuGet manager proposes pinned
updates here; actual replay, malformed-input controls and relevant canonical gates
must pass. A rejected update retains the prior immutable pin and compatible evidence;
rollback never creates a local generic source fork.

## Qualified preview 2 update

The new journal regression fails against public preview 1 and passes after updating
the package pin to public preview 2. It rejects unpaired UTF-16 surrogates before
fingerprinting while retaining valid replacement characters and emoji. No fixture,
projection or Quint tool pin changes. Journal 3, Host 78 and PostgreSQL 34 pass.

A deliberate downgrade candidate restores preview 1 successfully but fails exactly
the new Unicode regression (two existing journal controls still pass). Restoring the
immutable preview 2 pin and locks, restoring packages and rerunning all three journal
tests passes. This is a local qualification exercise, not a production incident.
The existing dependency/projection mutation tests require changed package pins and
lockfiles to invalidate correspondence evidence reuse; unrelated changes remain reusable.

Package updates use the organization Renovate authority already recorded by GS2-06.7.
Its accepted NuGet manager covers the central package pin; there is no competing
repository-local updater configuration or direct-push route. Every update remains
a pull request subject to the consumer qualification gates.
