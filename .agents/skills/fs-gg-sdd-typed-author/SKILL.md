---
name: fs-gg-sdd-typed-author
description: Author explicit F# or exact-cache Quint Typed SDD authority through FS.GG.SDD.
---

# Typed SDD author

For Quint v2, run `fsgg-sdd typed-sdd author --work <id> --title <title> --agent <agent-id>
--session <session-id> --backend quint-specification-v1 --cache <cache-root>`. The cache contains the
Q1-qualified `objects/<sha256>` and is never acquired online. Two isolated tool runs must agree before
the complete typed-effect-bound authority commits. Omitted backend preserves F# v1. Never replace v1
with `author --accept`; use migration to retain authenticated rollback.
