# Corpus inputs

This directory is the closed, ordered GS2-03.2 import of the 21 Q0 corpus
cases. Each `C-*.json` record is compact canonical metadata indexed by exact
bytes. Its `source` object binds an immutable `.github` Git object to the raw
payload under `originals/`; raw payloads are deliberately stored as `.source`
so their JSON, YAML, Markdown, shell, Python, or F# bytes are never parsed or
normalized by evidence discovery.

`provenance/` preserves the accepted Q0 manifest and evidence documents byte
for byte. The validator pins their SHA-256 identities, reconstructs the Q0
join, verifies payload length, SHA-256 and Git blob SHA-1, and refuses any
missing, duplicate, extra, reordered, unsafe, stale, or contradictory record.

Expected behavior is not a test result. `expectedBehavior` records the Q0
oracle expectation, `ambiguity` preserves the two explicit Indeterminate
cases, and `currentV1Result` reports a pass only for the two source artifacts
with direct exact-head workflow evidence. The other 19 cases remain explicitly
`not-atomically-observed`; no green result is inferred from a multi-case file.
