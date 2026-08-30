# Critique evidence and accountable acceptance

GS2-03.8 separates critique evidence from authorization. One Accountable Delivery Owner may implement,
critique, repair, and accept a candidate. Architecture, security, adapter, migration, and cutover are five
required evidence perspectives under distinct phase identities; they are not five people, accounts,
agents, approvals, or votes. This implements the authority boundary accepted in
[`ADR-0079`](https://github.com/FS-GG/.github/blob/8a420401b3cf53a51d06b4298e797355f64bf58a/docs/adr/0079-single-accountable-delivery-authority.md).

## Canonical contract

`CritiqueEvidence` generates `fsgg.coordination.critique-evidence/1`. A bundle binds:

- the exact candidate commit, tracked-tree digest, and roadmap-unit contract digest;
- a closed, ordinal inventory of evidence ids and SHA-256 fingerprints;
- one stable Accountable Delivery Owner;
- exactly one finding for each required perspective, with a distinct phase id, the same accountable author,
  content digest, decision, completion time, candidate fingerprint, evidence-set fingerprint, and self-digest;
- a roll-up with the fixed `all-required-bound-green/1` derivation, complete required and passing perspective
  inventories, the finding-set digest, and `accountable-owner-only` acceptance authority; and
- a root self-digest over canonical JSON.

The generator sorts evidence ids and perspectives before hashing, so authoring order does not affect identity.
Every object is serialized with ordinal property order and canonical UTC-second timestamps. A semantic change to
candidate, evidence, finding content, decision, phase, author, or time changes the bundle identity.

`changes-required` is valid critique evidence. It remains red: only five present, current, bound `passed`
findings derive a passed roll-up. Validation regenerates the complete expected canonical bundle from typed input
and byte-compares it. An absent, duplicate, stale, substituted, forged, truncated, reordered, prose-only, or
asserted-green artifact therefore cannot validate.

## Evidence storage evolution

The accepted `reviews/v1` schema is preserved. The reviews category selects `schemas/v2/reviews.schema.json`,
whose version is derived from its policy path and checked against its schema id. The storage validator now executes
supported Draft 2020-12 schemas for ordinary versioned categories, including exact five-finding bounds and closed
objects. The frozen corpus keeps its stronger provenance-specific validator instead of being double-validated by a
generic pass that would hide its typed diagnostics.

The JSON Schema proves storage shape. `CritiqueEvidence` proves semantic hashes, perspective completeness, phase
separation, exact bindings, owner identity, and roll-up derivation. Neither layer consults GitHub approval counts.
Native repository approvals remain zero; red declared technical predicates still block delivery.

Bootstrap qualification names `critique-evidence/1` as its review-policy component. That component is hashed into
the qualification subject, so evidence produced under the earlier structured-decision policy cannot be reused after
this contract becomes current even when every other tracked input is unchanged.

## Acceptance sequence

The implementation candidate is qualified at one immutable head. After its protected merge, the five perspective
content records and hosted evidence fingerprints are bound into a critique bundle for that exact merge. The
Accountable Delivery Owner records the sole acceptance decision, an append-only unit receipt binds the accepted
bundle, and only then is the canonical roadmap updated. No critique record may be relabelled onto a later tree.
