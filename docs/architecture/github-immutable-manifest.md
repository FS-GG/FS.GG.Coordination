# Immutable migration manifest

GS2-09.2 defines the sealed input to the GitHub substrate migration. The manifest consumes the exact
accepted GS2-09.1 discovery receipt and its complete, quiescent subject population. Qualification is pure:
it normalizes caller-supplied facts, validates that the population is complete and ordered, and seals the
result. It performs no provider read or write and creates no live migration manifest.

The sealed payload binds all roadmap-required families:

- old and new model fingerprints
- artifact fingerprints
- stable subject identities and global IDs
- old bytes and values
- v2 results
- live operations
- receiver heads
- settings plans
- archive digests, lookup indexes, and verifier digests
- dispositions
- phase plans
- reviewers
- rollback inputs

Every discovered subject appears exactly once. Old and v2 global IDs are unique, artifacts and operational
populations are sorted and unique, and the archive population exactly matches the closed discovery authority
set. Each phase has a contiguous order and refers only to retained rollback inputs. At least two distinct
reviewers bind decision digests. The old and new model fingerprints must differ.

Length-framed canonical bytes cover the governing roadmap, unit contract, predecessor receipt, discovery
source/digest/seal, every manifest family, and the creation instant. The normalized SHA-256 and derived seal
refuse omissions, reordering, altered values, changed population, unknown rollback references, and replay
under a different expected seal. Q5 exercises the complete manifest contract; Q6 reconstructs the same
payload in a fresh validation process and requires byte-equivalent qualification.

This unit records the immutable schema and deterministic qualification boundary. GS2-09.3 owns typed
transforms and disposition semantics. Later units own live-operation policy, archive production, rollback
execution, provider mutation, rehearsal, and cutover. Opaque nonempty values in those fields are bindings to
future decisions, not authority to make or execute them.
