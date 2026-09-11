# Execution-provider core

This project owns normalized execution-session lifecycle semantics. It does not own a concrete
provider adapter and assumes neither a CLI nor an API, subscription nor token authentication,
priced usage, JSONL output, or resumable conversations.

| Intended adapter | Possible transport/auth shape | Resume and output are capabilities, not assumptions |
| --- | --- | --- |
| Codex | subscription-backed CLI session | adapter reports observed support and normalized references |
| Claude | session-backed CLI | adapter may expose different session and usage semantics |
| OpenCode | session-backed CLI | adapter may aggregate output and usage differently |
| DeepSeek | provider-specific transport and authentication | adapter may have no resumable session or applicable local cost |

O2-S1 supplies only the neutral contract, coordinator, Akka mailbox boundary, and deterministic
fakes. O2-S2 owns the first concrete Codex adapter. Claude, OpenCode, and DeepSeek are follow-on
adapters over this same contract; none may add provider-specific decisions to orchestration core.

`ProviderSessionReference` is deliberately opaque and is never a runner enrollment identity.
The journal's attempt stream is keyed by assignment plus attempt; generation is fenced inside the
persisted intent. A launch intent is durable before spawn. Recovery reconciles the original intent
and its original finite deadline and limits, so ambiguity cannot create a second process or renew a
budget. Cancellation records a request separately from an observed terminal lifecycle.
