# Receiver checkpoint /2 source contract

The explicitly selected `fsgg.v3.receiver-genesis-checkpoint/2` and
`fsgg.v3.receiver-output-checkpoint/2` codecs retain immutable checkpoint data in
fresh-only supplier journal `/4`. All fields use sorted compact ASCII C1 with one LF,
canonical decimal u64 strings, lowercase hex digests and canonical nonzero UUIDs.
Both sections are at most 16384 bytes. Unknown, duplicate, missing, numeric and
noncanonical fields refuse. Existing wire schemas and their sixteen hash domains
remain unchanged.

The common field table is reflected by `PortableSupplierCheckpointCommon` and the
closed codecs. Revision and store fence identify the exact transition creating the
checkpoint and each advance by one without wrapping. Genesis has sequence zero,
null predecessor/previous head and an exact genesis payload/head. Output references
the retained genesis checkpoint and separately retains current wire and later native
acquisition references. Selected manifests require raw SHA, manifest domain digest,
positive member count (at most16) and total bytes; none requires all four keys null.

These codecs construct structural values only. They cannot authenticate signatures,
produce a durable commit witness, sign an ACK or dispatch a host effect. Serialized
`qualified-production` is data. The source caller must recompute every field from
retained exact originals, paired exchanges, typed challenges and independently
verified policies, then stage/reopen/revalidate them in the existing supplier store.
Cold decode never restores authentication or durability. `prepareAck` and
`reserveHost` continue refusing until separately qualified production evidence exists.

The additive `receiver-checkpoint-v2-vectors.json` corpus currently qualifies lexical
codecs only. Its historical wire preimages and deliberately opaque controller fixture
bytes are explicitly structural, with authentication and semantic journey false.
These vectors cannot count as authenticated caller/store positives. Complete selected
and zero-census journeys, real independently generated test signatures, `/4` CAS/cold
readback and their failure controls remain required by the owning implementation.
