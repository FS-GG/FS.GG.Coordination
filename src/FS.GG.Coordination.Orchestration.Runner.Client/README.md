# Orchestration runner client

The self-contained runner retains its existing `post` mode and also exposes the closed,
length-prefixed `executor-stdio` mode. Main remains the authority for admission, journal state,
generation and budgets. The executor receives no PostgreSQL credential and has no GitHub-delivery
role: it owns only a fixed candidate workspace, digest-addressed input and output roots, the selected
provider subprocess and reproducible Git candidate bundles. Under the accepted cooperative same-user
trust model, this does not remove ambient authority available to that user.

`executor-command/2` adds the workspace-manifest digest and a separate artifact digest without
changing `executor-command/1`. A launch is durably marked before process creation; the persisted ack
is distinct from process-creation observation. Exact retries reconcile, while conflicting identity,
stale generation or missing local state remains unknown and never authorizes another process.

The workspace manifest binds the selected repository, baseline Git object, allowed paths,
validation names and prompt digest. Candidate acceptance checks the actual baseline-to-head diff,
clean worktree, ancestry, tree identity, symlink absence and fixed validation implementations. The
artifact is a Git bundle plus a digest-bound manifest and bounded replay chunks, so Main can
independently reconstruct it before durable candidate acceptance.

The first concrete adapter is Codex subscription execution. Codex 0.154.0 documents
`--ignore-user-config` as retaining `CODEX_HOME` authentication while ignoring user configuration;
the adapter combines it with `--strict-config` so inherited alternate provider configuration cannot
silently replace the selected ChatGPT session. Environment filtering is hygiene under the accepted
cooperative same-user trust model, not credential containment. Claude, OpenCode and DeepSeek remain
future adapters behind the provider-neutral execution contract.
