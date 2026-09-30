# Portable workspace qualification image

This directory defines the Linux amd64 candidate image used to qualify the fixed portable workspace operations.
The base image and fetched Node.js and TypeScript archives are pinned in `inputs.json`; the helper verifies every
pin before it permits a download or build. The candidate is retained by CI for 14 days as qualification evidence,
not published as a release or installed runtime.

The workflow uses one linear job with a 20-minute cap. Its static preflight runs immediately after checkout and
checks the clean exact Git revision, approved recipe and archive hashes, Linux amd64 rootless Podman capability,
the non-root user mapping and absence of privileged or host PID/UTS/user namespace overrides. Only a passing
preflight permits the pinned base pull, image build and operation run. A known-bad pin, dirty or wrong revision,
unsupported rootless mapping or isolation override fails before that costly work.

No custom pipeline model is used. The job has no fan-out, retry, shared mutable cache, publication or conditional
admission state that would benefit from a state-machine model. Static validation directly covers the relevant
failure boundary and is expected to be reused on every image change. The implementation and validation effort is
capped at 20 minutes of runner time per attempt. Reassess if the workflow gains interacting jobs, evidence reuse,
retry or publication behavior.

The strict run mounts the exact checkout read-only at `/source`, provides only `/output` and `/tmp` as writable
storage, disables networking, drops capabilities, enables `no-new-privileges` and clears inherited environment.
Every operation must exit successfully and produce its exact reviewed output digest. The helper removes its scoped
container after every attempt; the workflow repeats scoped cleanup even on failure. Production executor acceptance
remains separate and requires its joined source and durable journal enforcement.
