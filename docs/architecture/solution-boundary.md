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
requires the complete six-project production set, and rejects undeclared edges. Protocol
and Core fail closed on runtime dependencies: their only allowed package, assembly, and
framework references are `FSharp.Core` and the SDK's implicit `Microsoft.NETCore.App`.
This closed set rejects GitHub and ASP.NET dependencies, HTTP clients such as RestSharp,
and future transport aliases without relying on a dependency-name blacklist. Raw root,
child, and import SDK declarations are also inspected so Web or Razor SDKs remain forbidden.
The App guard prevents an executable or runtime-bound host, covering executable output
types, hosting packages and references, and web, Razor, or worker SDKs declared on the root,
child SDK elements, or imports. The policy queries each evaluated MSBuild project as well as its
authored XML so ordinary imported props and targets cannot hide dependency edges or runtime bindings;
an evaluation failure is itself a policy violation.

GS2-01.4 adds one deliberate package edge from `Qualification.Contracts` to the
published `FS.GG.SDD.Artifacts` kernel. That edge is governed by
[Published Quint kernel](published-quint-kernel.md): it is not permitted in any
other production project and does not change the one-way project graph.
`FS.GG.Coordination.ArchitectureTests` runs
the policy against the real repository plus independent invalid project-edge,
framework-reference, RestSharp-package, root-web-SDK, child-web-SDK, App-hosting,
App import-SDK, and deterministic missing-import evaluation-failure fixtures. The
evaluation fixture otherwise preserves the allowed project graph so it proves an
unreadable evaluated project fails closed on `project-evaluation-failed` rather than
being rejected first by another policy rule. The `Boundary qualification`
workflow invokes the locked restore, Release build, test suite, and dependency policy on
GitHub-hosted runners.

The following remain outside this unit:

- bootstrap CI and evidence-manifest validation (GS2-01.5)
- lifecycle command behavior and protocol semantics (GS2-02 and later)
- any App listener, deployment identity, webhook registration, secret,
  subscription, or production mutation authority
