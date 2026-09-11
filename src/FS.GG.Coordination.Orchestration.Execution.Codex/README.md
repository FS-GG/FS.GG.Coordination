# Codex subscription execution adapter

This adapter binds the provider-neutral execution contract to the pinned Codex CLI 0.154.0
subscription session. It invokes the stable non-interactive surface as `codex exec`, supplies the
prompt only on standard input, selects the workspace with `-C`, requests JSONL with `--json`, and
captures the final structured response with `--output-schema` and `--output-last-message`. A known
Codex thread can be expressed with `codex exec ... resume <thread-id> -`, but recovery never resumes
an ambiguous spawn automatically.

Readiness requires both the exact CLI version and `codex login status` reporting a ChatGPT login.
The provider-reported model is not present in Codex exec JSONL, so the resolved selection is the
locally selected CLI model/effort, not a claim about backend routing. Complete token fields are
normalized with JSONL provenance; missing or malformed usage stays unknown. Subscription execution
has no per-invocation price here, so cost is not applicable rather than zero.

`CodexExecution.supervisedActorProps` composes the concrete provider, neutral durable coordinator,
and thin Akka actor. The caller must provide a digest-addressed input reader, candidate inspector,
and durable journal. The production Host still needs to bind those interfaces to its PostgreSQL
journal and assignment workspace before activation.

Environment allow-listing is process hygiene only. The child remains a cooperative, same-user
runner with the user's filesystem and network authority; stronger credential isolation remains
deferred. Claude, OpenCode, and DeepSeek remain separate future adapters over the neutral contract.
