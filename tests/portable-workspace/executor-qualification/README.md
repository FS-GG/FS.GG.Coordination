# Portable executor native qualification

This fixture is copied into a disposable Git repository by
`eng/portable-workspace-executor-qualification.fsx`. The harness consumes a
verified `fsgg.portable-workspace-local-image/1` manifest and an already loaded
immutable image from the supplied rootless Podman VFS store.

Build `FS.GG.Coordination.Orchestration.Execution` in Debug configuration, then
run the script with absolute paths for the image manifest, state, evidence,
Podman store and runroot. Each tool invocation and executor command has a finite
deadline. Exit code 0 means strict native acceptance, 2 means the runtime could
not establish native acceptance, and 1 means a qualification check failed.

The emitted JSON records operation counts, immutable image and source identity,
duplicate or recovery behavior, prelaunch refusal checks, cleanup state and the
strict acceptance result. State and evidence directories are private run inputs;
they are not committed.
