# FsQuint replay dependency

FSQUINT-01 moves generic replay ownership to the public FsQuint package. Host and
journal tests pin 0.1.0-preview.1 through central package management and lock files.
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

Public-package checks: Host 78, PostgreSQL 34; journal 2 and correspondence/reuse
architecture 107 passed during migration. Full canonical qualification is recorded
in the PR once complete. Preview 1's malformed UTF-16 fingerprint bug is tracked
upstream and is the subsequent qualified-update exercise before stable adoption.

Generic defects are fixed in FsQuint first. Renovate's NuGet manager proposes pinned
updates here; actual replay, malformed-input controls and relevant canonical gates
must pass. A rejected update retains the prior immutable pin and compatible evidence;
rollback never creates a local generic source fork.
