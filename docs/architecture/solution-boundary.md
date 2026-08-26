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
GitHub, HTTP, and ASP.NET references out of Protocol and Core across project, package,
assembly, framework, root-SDK, child-SDK, and import-SDK declarations, and prevents App
from becoming an executable or runtime-bound host. The App guard covers executable output
types, hosting packages and references, and web, Razor, or worker SDKs declared on the root,
child SDK elements, or imports. `FS.GG.Coordination.ArchitectureTests` runs
the policy against the real repository plus independent invalid project-edge,
framework-reference, root-web-SDK, child-web-SDK, App-hosting, and App import-SDK fixtures. The `Boundary qualification`
workflow invokes the locked restore, Release build, test suite, and dependency policy on
GitHub-hosted runners.

The following remain outside this unit:

- FS.GG.SDD kernel binding (GS2-01.4)
- bootstrap CI and evidence-manifest validation (GS2-01.5)
- lifecycle command behavior and protocol semantics (GS2-02 and later)
- any App listener, deployment identity, webhook registration, secret,
  subscription, or production mutation authority
