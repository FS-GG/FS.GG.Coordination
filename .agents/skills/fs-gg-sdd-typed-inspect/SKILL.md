---
name: fs-gg-sdd-typed-inspect
description: Inspect versioned F# or Quint Typed SDD authority and semantic closure.
---

# Typed SDD inspect

Run `fsgg-sdd typed-sdd inspect --work <id>`. Dispatch comes only from manifest version/backend.
V1 retains exact F# checks. V2 verifies the exact profile/toolchain/package and the semantic closure
of Markdown, fences, generated Quint, typed effect, source map, contract, bindings, receipt, and any
rollback inventory. Resolve diagnostic IDs exactly; never infer from files or bypass the manifest.
