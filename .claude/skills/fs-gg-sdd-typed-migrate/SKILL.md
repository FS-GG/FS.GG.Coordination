---
name: fs-gg-sdd-typed-migrate
description: Analyze, accept, and roll back Standard SDD to Typed SDD migrations.
---

# Typed SDD migrate

Run the explicit Quint command with `--backend quint-specification-v1 --cache <cache-root> --agent
<agent-id> --session <session-id>` and without `--accept` first. Review `Migrated`, `Ambiguous`, or
`Unsupported`; only repeat `Migrated` with `--accept`. Review the exact canonical v1 semantic-payload
digest. Every legacy identity, reference, acceptance effect, and semantic text field is lowered into
the bounded compiled contract. The raw executable Quint module remains the closed Q1 slice, so
compatibility metadata is not misrepresented as raw Quint code. The complete v1 model is also retained
losslessly in Markdown, the compiled-contract digest, and the authenticated rollback inventory.
`typed-sdd rollback --work <id> --accept`
validates and restores the exact tree while removing v2 outputs; corruption blocks without partial
mutation.
