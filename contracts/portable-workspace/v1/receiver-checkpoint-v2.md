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

The lexical `receiver-checkpoint-v2-vectors.json` corpus retains its historical
opaque controller bytes and does not establish authenticated caller/store behavior.
The separate `receiver-checkpoint-v2-signed-source-journeys.json` contains independent
OpenSSH test signatures, nine explicit public role pins and fixed controlled policy,
configuration and clock inputs for zero and selected-sixteen journeys. Its input-only
flags remain false; actual managed qualification belongs to the exact source run.

The [controlled source factory](../../../src/FS.GG.Coordination.Orchestration.Execution/PortableHostAllocation.fs) verifies creation, genesis,
output wire and native acquisition against copied **controlled test data**. It derives
observations and complete role bundles from exact bytes and the executing verifier;
a public structural transcript adapter or parsed bundle cannot construct the private
successful transition. Native receipt bytes come from the verified proof envelope.
Selected V2 issuance binds the current proof head without knowing the later receipt
hash; zero V1 retains its independently selected receipt hash. The sixteen-member
manifest limit is separate from event count. Existing receiver eighteen-observation
and controller four-observation caps still apply before per-role cryptography.

Append and unchanged responses follow their canonical mode. An unchanged response
retains the exact last signed envelope, including its genesis role when applicable.
Warm verification and `reverifyControlledSourceV4` recheck retained signatures,
independent original/configuration bindings and role policies. Fresh revocation and
clock observations remain separate from historical bundle metadata. Verifier binary
and profile digests come from the actual loaded assemblies. Production provenance
refuses; these APIs expose no production enrollment or execution capability.

The [supplier journal](../../../src/FS.GG.Coordination.Orchestration.Execution/PortableWorkspaceExecutor.fs) `StageControlledV4` accepts only the private immutable
transition and delegates the existing exact serializer, expected original/revision/
fence CAS and fresh-byte/cold-state readback. A stage readback is structural data;
only separate current-policy revalidation returns the private controlled-source
readback. Durability remains Unknown, with ACK, host effects and provider/native
qualification outside this source boundary.

`CreateControlledFailureTest` selects one fixed test cut in this same current
source store: before writing, before file flush, before replacement, after
replacement before fresh readback, or before cold reopen. A failed Stage returns
commit uncertainty, without a source readback or ACK. Earlier cuts retain the
prior record; later cuts can leave the exact proposed record. A later explicit
fresh read and current signature/policy verification can authenticate those
persisted source bytes, while directory durability remains Unknown. The
controls operate on current `/4` source records.

The SDK-generated `packages.lock.json` files retain NuGet package-content hashes.
For signed packages that identity can differ from a raw archive SHA512: NuGet's
[immutable package reader source](https://github.com/NuGet/NuGet.Client/blob/d3a633a04da7cd83900498334ec5e7650a52fa86/src/NuGet.Core/NuGet.Packaging/PackageArchiveReader.cs)
separates `GetContentHash` and `GetArchiveHashAsync`. Compare each hash with the same
identity kind and retain original archive observations as archive evidence. A lock
hash does not replace package-signature, dependency/security or executed-artifact
qualification.
