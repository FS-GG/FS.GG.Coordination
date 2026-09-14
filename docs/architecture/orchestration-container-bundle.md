# Orchestration container application bundle

The selected Main deployment has two rootless Podman containers. The orchestration container runs the
Coordination Host and its trusted bounded runner/model child. PostgreSQL runs in the other container with
its own durable volume. Host systemd owns fixed container lifecycle and ordering; it does not relay
executor messages or call `podman exec` for ordinary work.

Coordination publishes one Linux x64 application bundle for the orchestration image. The bundle contains
the existing self-contained Host and runner executables from the same protected-main commit and source
tree. Its canonical manifest binds both component archives, prepared receipts, manifests and executable
bytes. The component workflows still prove their native command surfaces independently.

The bundle layout is fixed:

```text
fsgg-coord-orchestration-linux-x64/
  host/fsgg-coord-orchestration-host
  runner/fsgg-coord-orchestration-runner
  manifest.json
```

`eng/orchestration-container-bundle.py` assembles and verifies the bundle. The protected-main workflow
builds both components, assembles the bundle once, uploads it once and verifies freshly downloaded bytes.
The artifact is an input to the SystemAdmin image build. It does not itself install units, create
containers, initialize PostgreSQL, migrate the paused deployment or enable dispatch.

The runtime contract is deliberately small. In Main mode, the Host starts the bundled runner directly in
compiled `executor-stdio` mode. It passes fixed absolute repository, workspace, input, state and artifact
roots as structured arguments, then serializes bounded framed requests and responses over the child's
standard input and output. A cancelled exchange, unexpected exit, malformed frame or stale command identity
terminates that child; the next reconciliation starts a clean child against durable state. Host shutdown
closes the input stream and uses a bounded wait before terminating the process tree.

The runner receives the selected provider session and worktree paths, while the Host alone receives
PostgreSQL and GitHub delivery credentials. This arrangement uses the accepted trusted-runner model; it is
not a hostile-process credential boundary. The production path has no HTTP executor relay, `podman exec`,
development-container dependency, Podman socket or actor remoting between Host and runner.

SystemAdmin owns the OCI build and two systemd user units. The orchestration container has no Podman socket,
host PID namespace, broad home mount or container-management authority. It receives only declared
read-only configuration and credential files plus bounded writable state, workspace and temporary paths.
PostgreSQL is reachable only on the private container channel and is not published on a host interface.
