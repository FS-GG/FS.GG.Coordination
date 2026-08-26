# Solution boundary

GS2-01.3 establishes compilation and dependency ownership without implementing
coordination semantics or connecting to GitHub.

The production dependency graph is:

```text
Protocol
├──> Core
│    └──> GitHub
│         ├──> CLI
│         └──> App (inert class library)
└──> Qualification.Contracts
```

CLI and App also declare direct references to Protocol and Core so their complete
allowed dependency set remains visible in project XML. All arrows point outward
from policy/contracts toward adapters and hosts; no inward layer may reference an
outward layer.

`eng/verify-dependencies.fsx` is the executable policy. It reads project XML,
requires the complete six-project production set, rejects undeclared edges, keeps
GitHub and HTTP references out of Protocol and Core, and prevents App from becoming
an executable or web-SDK host. `FS.GG.Coordination.ArchitectureTests` runs the
policy against both the real repository and an intentionally invalid fixture.

The following remain outside this unit:

- FS.GG.SDD kernel binding (GS2-01.4)
- bootstrap CI and evidence-manifest validation (GS2-01.5)
- lifecycle command behavior and protocol semantics (GS2-02 and later)
- any App listener, deployment identity, webhook registration, secret,
  subscription, or production mutation authority
