module FS.GG.Coordination.GitHubV1AdmissionProviderReconciliationTests

open System
open System.Text
open FS.GG.Coordination.GitHub
open Xunit

module Registry = V1AdmissionRegistry
module Adapter = V1AdmissionProviderReconciliation

let private request = Encoding.UTF8.GetBytes "canonical-provider-request"
let private response = String.replicate 64 "a" |> Registry.sha256Digest |> Result.defaultWith failwith

let private identity =
    {
        OperationId = "operation-1"
        EffectId = "effect-1"
        Attempt = 2L
        RequestDigest =
            System.Security.Cryptography.SHA256.HashData request
            |> Convert.ToHexString
            |> fun value -> value.ToLowerInvariant()
            |> Registry.sha256Digest
            |> Result.defaultWith failwith
        CanonicalRequestBytes = request
    }

let private applied =
    AppliedReadback
        {
            Identity = identity
            ResponseDigest = response
            EvidenceBytes = Encoding.UTF8.GetBytes "provider-readback"
        }

let private excluded kind =
    ExclusionReadback
        {
            Identity = identity
            Kind = kind
            EvidenceBytes = Encoding.UTF8.GetBytes "provider-exclusion-proof"
        }

let private native observation appliedVerified exclusionVerified =
    {
        Read = fun _ -> Ok observation
        VerifyApplied = fun _ _ -> Ok appliedVerified
        VerifyExclusion = fun _ _ -> Ok exclusionVerified
    }

let private read native =
    let port = Adapter.port native
    port.Read identity.OperationId identity.EffectId identity.Attempt (Array.copy request)
    |> Result.defaultWith failwith

let private indeterminate observation =
    Assert.True(match observation with ProviderIndeterminate _ -> true | _ -> false)

[<Fact>]
let ``exact applied effect needs independent readback`` () =
    Assert.Equal(ProviderApplied response, read (native applied true false))
    indeterminate (read (native applied false false))
    let lostRead = { native applied true false with Read = fun _ -> Error "lost-response" }
    indeterminate (read lostRead)

[<Fact>]
let ``applied readback must bind original identity and bytes`` () =
    let wrongIdentities =
        [ { identity with OperationId = "other-operation" }
          { identity with EffectId = "other-effect" }
          { identity with Attempt = 3L }
          { identity with RequestDigest = response }
          { identity with CanonicalRequestBytes = Encoding.UTF8.GetBytes "different-request" } ]
    for wrong in wrongIdentities do
        let observation =
            AppliedReadback
                { Identity = wrong
                  ResponseDigest = response
                  EvidenceBytes = Encoding.UTF8.GetBytes "provider-readback" }
        let mutable verified = false
        let readPort =
            { native observation true false with
                VerifyApplied = fun _ _ -> verified <- true; Ok true }
        indeterminate (read readPort)
        Assert.False verified

[<Fact>]
let ``ordinary absence and lost verifier never prove retry safety`` () =
    indeterminate (read (native (IndeterminateReadback "404") true true))
    indeterminate (read (native (excluded IdempotencyKeyExclusion) false false))
    let unavailable =
        { native (excluded IdempotencyKeyExclusion) false true with
            VerifyExclusion = fun _ _ -> Error "provider-timeout" }
    indeterminate (read unavailable)

[<Theory>]
[<InlineData(0)>]
[<InlineData(1)>]
[<InlineData(2)>]
let ``verified exclusion retains proof kind and exact request binding`` kindIndex =
    let kind =
        match kindIndex with
        | 0 -> IdempotencyKeyExclusion
        | 1 -> ConditionalFenceExclusionProof
        | _ -> OriginalRequestRetirementProof
    let observation = read (native (excluded kind) false true)
    match observation with
    | ProviderStronglyAbsent evidence ->
        let bound =
            match evidence with
            | ProviderIdempotencyExclusion(requestDigest, _) when kindIndex = 0 -> requestDigest
            | ConditionalFenceExclusion(requestDigest, _) when kindIndex = 1 -> requestDigest
            | OriginalRequestRetirement(requestDigest, _) when kindIndex = 2 -> requestDigest
            | _ -> failwith "wrong exclusion kind"
        Assert.Equal(identity.RequestDigest, bound)
    | _ -> Assert.Fail "verified exclusion was not accepted"

[<Fact>]
let ``exclusion proof with wrong request or empty bytes cannot authorize retry`` () =
    let wrong =
        ExclusionReadback
            { Identity = { identity with EffectId = "different" }
              Kind = ConditionalFenceExclusionProof
              EvidenceBytes = Encoding.UTF8.GetBytes "proof" }
    let mutable verified = false
    let readPort =
        { native wrong false true with
            VerifyExclusion = fun _ _ -> verified <- true; Ok true }
    indeterminate (read readPort)
    Assert.False verified
    let empty =
        ExclusionReadback
            { Identity = identity
              Kind = OriginalRequestRetirementProof
              EvidenceBytes = Array.empty }
    indeterminate (read (native empty false true))

[<Fact>]
let ``partial and callback exceptions remain recovery only`` () =
    Assert.Equal(ProviderPartial "provider-partial", read (native (PartialReadback "incomplete") false false))
    let throwing =
        { native applied true false with
            VerifyApplied = fun _ _ -> failwith "unreadable" }
    indeterminate (read throwing)

[<Fact>]
let ``verifier cannot rewrite the request or exclusion proof being accepted`` () =
    let mutable changedRequest = false
    let requestChanging =
        { native applied true false with
            VerifyApplied =
                fun expected _ ->
                    expected.CanonicalRequestBytes[0] <- 0uy
                    changedRequest <- true
                    Ok true }
    indeterminate (read requestChanging)
    Assert.True changedRequest
    Assert.Equal(Encoding.UTF8.GetBytes "canonical-provider-request", request)

    let proofChanging =
        { native (excluded IdempotencyKeyExclusion) false true with
            VerifyExclusion =
                fun _ evidence ->
                    evidence.EvidenceBytes[0] <- 0uy
                    Ok true }
    indeterminate (read proofChanging)
