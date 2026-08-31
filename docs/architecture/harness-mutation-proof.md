# Harness mutation proof

GS2-03.9 closes a gap between having many negative tests and proving that the negative-test inventory is complete. The qualification evidence boundary has ten classes: sources, model, compiler, dependencies, generated cases, independent cases, external fixtures, packages, results, and reviewers. The roadmap fixes six invalid modes: vacuous, absent, stale, truncated, forged, and generated-only. `HarnessMutationProof` owns those two closed lists and derives their 60-cell Cartesian product plus one healthy control per class.

## Production observations, not declarations

The caller supplies only an independently retained healthy qualification inventory and manifest plus exact candidate, unit-contract, validator, and tracked-tree fingerprints. The proof generator creates every mutation itself, invokes `QualificationManifest.validate`, records the actual sorted diagnostic set, and refuses any unexpectedly green cell. The validator regenerates the complete proof from the same independent inputs and byte-compares canonical output. A caller cannot submit an outcome, diagnostic, coverage count, or hand-picked subset.

The proof is canonical JSON with exact inventory and baseline digests, ordered gate and mutation inventories, ten controls, 60 negative observations, content bindings, and a self digest. The `mutation-proofs/v1` evidence schema constrains the stored projection; the typed validator supplies the stronger Cartesian, semantic, and regeneration checks.

Bootstrap qualification advances its hashed review-policy identity to `critique-and-mutation-proof/1`. This changes no workflow lane or ordering edge: it makes the complete-tree reuse subject acknowledge that both retained critique and mutation proof are acceptance inputs.

## Forgery and generated provenance

Qualification-manifest v1 intentionally records artifact hashes without loading every artifact byte. Consequently, a valid arbitrary hash can be resealed into a structurally correct manifest. Treating malformed hex as “forged” would give false confidence. The mutation proof therefore changes a digest to another valid SHA-256, recomputes every dependent manifest digest, confirms the ordinary structural validator would accept it, and then rejects it as `HMP-FORGED-FINGERPRINT` because it differs from the independently supplied frozen healthy baseline.

Generated-only evidence is a different failure class. `QualificationManifest` now derives the generated-case producer set and rejects it as the sole provenance for any non-generated content class, result set, or review set. This preserves generated cases as useful inputs while preventing them from proving their own correctness. Existing qualification inventory/manifest v1 bytes and public inputs remain compatible.

## Authority and lifecycle

The proof is repository-local and grants no network, GitHub mutation, deployment, publication, or production authority. One Accountable Delivery Owner may generate, critique, and accept it; no reviewer quorum or external account is required. Acceptance still binds the exact protected implementation candidate, independently retained baseline and validator bytes, Q7 results, hosted execution, and the five critique perspectives.

This is the standard response to a second related late-stage defect: stop retries, inventory the entire failure family, make completeness derived, add a production-bound negative oracle, and resume only from the repaired class boundary.
