namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography

type ProviderRequestIdentity =
    {
        OperationId: string
        EffectId: string
        Attempt: int64
        RequestDigest: Sha256Digest
        CanonicalRequestBytes: byte array
    }

type ProviderAppliedReadback =
    {
        Identity: ProviderRequestIdentity
        ResponseDigest: Sha256Digest
        EvidenceBytes: byte array
    }

type ProviderExclusionKind =
    | IdempotencyKeyExclusion
    | ConditionalFenceExclusionProof
    | OriginalRequestRetirementProof

type ProviderExclusionReadback =
    {
        Identity: ProviderRequestIdentity
        Kind: ProviderExclusionKind
        EvidenceBytes: byte array
    }

type ProviderNativeEffectObservation =
    | AppliedReadback of ProviderAppliedReadback
    | ExclusionReadback of ProviderExclusionReadback
    | PartialReadback of string
    | IndeterminateReadback of string

type ProviderReconciliationNativeRead =
    {
        Read: ProviderRequestIdentity -> Result<ProviderNativeEffectObservation, string>
        VerifyApplied: ProviderRequestIdentity -> ProviderAppliedReadback -> Result<bool, string>
        VerifyExclusion: ProviderRequestIdentity -> ProviderExclusionReadback -> Result<bool, string>
    }

module V1AdmissionProviderReconciliation =
    let private digest (bytes: byte array) =
        SHA256.HashData bytes
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()
        |> V1AdmissionRegistry.sha256Digest
        |> Result.defaultWith invalidOp

    let private copyIdentity (identity: ProviderRequestIdentity) =
        { identity with CanonicalRequestBytes = Array.copy identity.CanonicalRequestBytes }

    let private matches expected actual =
        not (isNull actual.CanonicalRequestBytes)
        && actual.OperationId = expected.OperationId
        && actual.EffectId = expected.EffectId
        && actual.Attempt = expected.Attempt
        && actual.RequestDigest = expected.RequestDigest
        && actual.CanonicalRequestBytes = expected.CanonicalRequestBytes

    let private indeterminate reason = Ok(ProviderIndeterminate reason)

    let port (native: ProviderReconciliationNativeRead) : ProviderReconciliationPort =
        {
            Read =
                fun operationId effectId attempt canonicalRequestBytes ->
                    if String.IsNullOrWhiteSpace operationId
                       || String.IsNullOrWhiteSpace effectId
                       || attempt <= 0L
                       || isNull canonicalRequestBytes
                       || canonicalRequestBytes.Length = 0 then
                        Error "provider-request-invalid"
                    else
                        let original = Array.copy canonicalRequestBytes
                        let expected =
                            {
                                OperationId = operationId
                                EffectId = effectId
                                Attempt = attempt
                                RequestDigest = digest original
                                CanonicalRequestBytes = original
                            }

                        try
                            match native.Read (copyIdentity expected) with
                            | Error _ -> indeterminate "provider-read-unavailable"
                            | Ok(AppliedReadback evidence) ->
                                if not (matches expected evidence.Identity)
                                   || isNull evidence.EvidenceBytes
                                   || evidence.EvidenceBytes.Length = 0 then
                                    indeterminate "provider-applied-binding"
                                else
                                    let frozen =
                                        { evidence with
                                            Identity = copyIdentity evidence.Identity
                                            EvidenceBytes = Array.copy evidence.EvidenceBytes }

                                    let verificationRequest = copyIdentity expected
                                    match native.VerifyApplied verificationRequest frozen with
                                    | Ok true when matches expected verificationRequest
                                                   && matches expected frozen.Identity
                                                   && frozen.EvidenceBytes = evidence.EvidenceBytes ->
                                        Ok(ProviderApplied frozen.ResponseDigest)
                                    | _ -> indeterminate "provider-applied-unverified"
                            | Ok(ExclusionReadback evidence) ->
                                if not (matches expected evidence.Identity)
                                   || isNull evidence.EvidenceBytes
                                   || evidence.EvidenceBytes.Length = 0 then
                                    indeterminate "provider-exclusion-binding"
                                else
                                    let frozen =
                                        { evidence with
                                            Identity = copyIdentity evidence.Identity
                                            EvidenceBytes = Array.copy evidence.EvidenceBytes }

                                    let verificationRequest = copyIdentity expected
                                    match native.VerifyExclusion verificationRequest frozen with
                                    | Ok true when matches expected verificationRequest
                                                   && matches expected frozen.Identity
                                                   && frozen.EvidenceBytes = evidence.EvidenceBytes ->
                                        let proofDigest = digest frozen.EvidenceBytes
                                        match frozen.Kind with
                                        | IdempotencyKeyExclusion ->
                                            ProviderIdempotencyExclusion(expected.RequestDigest, proofDigest)
                                        | ConditionalFenceExclusionProof ->
                                            ConditionalFenceExclusion(expected.RequestDigest, proofDigest)
                                        | OriginalRequestRetirementProof ->
                                            OriginalRequestRetirement(expected.RequestDigest, proofDigest)
                                        |> ProviderStronglyAbsent
                                        |> Ok
                                    | _ -> indeterminate "provider-exclusion-unverified"
                            | Ok(PartialReadback _) -> Ok(ProviderPartial "provider-partial")
                            | Ok(IndeterminateReadback _) -> indeterminate "provider-indeterminate"
                        with _ ->
                            indeterminate "provider-read-unavailable"
        }
