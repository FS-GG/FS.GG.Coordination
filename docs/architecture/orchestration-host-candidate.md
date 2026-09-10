# Immutable orchestration host candidate

The nonpackable orchestration host uses one manual, predeployment candidate route. The workflow accepts an
an expected protected-main revision only as an inert comparison. The job is gated to the trusted main dispatch ref, checks out and builds `github.sha`, projects only its tracked bytes, and builds a self-contained single-file
Linux x64 executable twice in the same revision-derived clean path. Both executable bytes and canonical ZIP
bytes must be identical before the workflow uploads anything.

The ZIP contains exactly `fsgg-coord-orchestration-host-linux-x64/fsgg-coord-orchestration-host` at mode
0500 and a canonical `manifest.json` at mode 0400. Entries are ordered, use the fixed 2000-01-01 timestamp,
and bind the source commit and tree, tracked projection, Linux RID lock, pinned SDK/runtime/F# compiler,
build controls, executable digest and byte count. Both the executable and ZIP must be at most 100 MiB.

The candidate is uploaded once as `orchestration-host-linux-x64-<40-character-source-sha>` with 90-day
retention. The same run downloads it into a fresh directory, compares the exact ZIP and payload bytes, and
executes the native usage path. A separate terminal verification artifact binds the Actions artifact ID,
name, URL and outer digest to the inner ZIP, executable, source and usage digests. The workflow has read-only
repository permission and cannot create a tag, release, package, deployment, permit, runner or Main state.

The artifact remains a candidate until Main's deployment contract pins the exact artifact and verification
receipt and a separately authorized inert preview verifies installed bytes. Workflow success alone grants no
installation, database initialization, service activation, dispatch or provider mutation authority.
