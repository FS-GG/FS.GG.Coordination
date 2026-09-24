#nowarn "3391"

module FS.GG.Coordination.GitHubOrdinaryPostMergeSettlementTests

open System
open System.Security.Cryptography
open Xunit
open FS.GG.Coordination.GitHub

let private sha character = String.replicate 40 character

let private observation epoch =
    { Repository = "FS-GG/FS.GG.Coordination"
      RepositoryId = 101L
      PullRequestNumber = 421
      PullRequestNodeId = "PR_kwDOordinary"
      BaseRef = "main"
      BaseSha = sha "a"
      HeadSha = sha "b"
      PolicyRevision = sha "c"
      Checks =
        [ { Identity = "coherent-qualification"; AppId = 10L; Conclusion = CheckPassed }
          { Identity = "ordinary-settlement-contract"; AppId = 11L; Conclusion = CheckPassed } ]
      Epoch = epoch
      EpochGeneration = 3L
      EpochCommit = sha "9"
      JournalGeneration = 7L
      JournalHead = sha "d"
      SourceComplete = true
      ChecksComplete = true
      Authorized = true
      Supported = true }

let private association merged commit =
    { Number = 421
      NodeId = "PR_kwDOordinary"
      Repository = "FS-GG/FS.GG.Coordination"
      BaseRef = "main"
      MergeCommit = commit
      Merged = merged }

let private writerBinding: OrdinarySettlementCredentialBinding =
    { AppId = 7001L
      InstallationId = 8001L
      RepositoryIds = [ 202L ]
      Permissions = Map [ "contents", "write"; "metadata", "read" ] }

let private readBinding: OrdinarySettlementReadBinding =
    { AppId = 7002L
      InstallationId = 8002L
      RepositoryIds = [ 101L ]
      Permissions =
        Map [ "administration", "read"; "checks", "read"; "contents", "read"; "metadata", "read"; "pull_requests", "read" ] }

let private prepare
    (observed: OrdinaryDeliveryObservation)
    (associations: OrdinaryMergedPullRequest list)
    (reader: OrdinarySettlementReadBinding)
    (writer: OrdinarySettlementCredentialBinding)
    =
    OrdinaryPostMergeSettlement.prepare
        "workflow-run:36004284186:attempt:1"
        202L
        (sha "e")
        (sha "f")
        "ordinary-post-merge-delivery-settlement"
        "ordinary-v2"
        observed
        associations
        reader
        writer

let private prepared () =
    prepare (observation "OpenV2") [ association true (sha "b") ] readBinding writerBinding
    |> Result.defaultWith (sprintf "%A" >> failwith)
    |> fst

let private authorization (rsa: RSA) keyId (plan: OrdinarySettlementPlan) binding =
    let intent = OrdinaryPostMergeSettlement.canonicalIntent plan binding
    { KeyId = keyId
      PublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem()
      IntentSha256 = Convert.ToHexString(SHA256.HashData intent).ToLowerInvariant()
      Signature = rsa.SignData(intent, HashAlgorithmName.SHA256, RSASignaturePadding.Pss) }

let private anchor (rsa: RSA) keyId =
    { KeyId = keyId
      PublicKeySpkiSha256 = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
      AppId = writerBinding.AppId
      InstallationId = writerBinding.InstallationId
      RepositoryId = 202L
      Permissions = writerBinding.Permissions }

type private Runtime(plan: OrdinarySettlementPlan, initialRevision: string option, unrelated: OrdinarySettlementEntry option) =
    let mutable revisionCounter = 0
    let mutable shard =
        { Address = plan.JournalAddress
          Revision = initialRevision
          Entries = unrelated |> Option.map (fun value -> Map [ value.OperationId, value ]) |> Option.defaultValue Map.empty }
    let mutable effect: OrdinarySettlementEffectObservation = SettlementEffectAbsent
    let mutable applyCount = 0
    let mutable unknownWrite = false
    let mutable rejectNextWrite = false
    let mutable lostReply = false
    let mutable readbackOverride: string option option = None
    let writes = ResizeArray<string option * OrdinarySettlementShard>()

    member _.Shard = shard
    member _.ApplyCount = applyCount
    member _.Writes = writes |> Seq.toList
    member _.Effect with get () = effect and set value = effect <- value
    member _.UnknownWrite with get () = unknownWrite and set value = unknownWrite <- value
    member _.RejectNextWrite with get () = rejectNextWrite and set value = rejectNextWrite <- value
    member _.LostReply with get () = lostReply and set value = lostReply <- value
    member _.ReadbackOverride with get () = readbackOverride and set value = readbackOverride <- value

    interface IOrdinaryPostMergeSettlementRuntime with
        member _.ReadShard address =
            if address = shard.Address then SettlementShardObserved shard
            else SettlementShardUnknown "wrong-shard"

        member _.CompareExchangeShard(expected, proposed) =
            writes.Add(expected, proposed)
            if rejectNextWrite then
                rejectNextWrite <- false
                SettlementCasConflict
            elif expected <> shard.Revision then SettlementCasConflict
            else
                revisionCounter <- revisionCounter + 1
                shard <- { proposed with Revision = Some(String.replicate 39 "0" + string revisionCounter) }
                if unknownWrite then
                    unknownWrite <- false
                    SettlementCasUnknown
                else SettlementCasAccepted

        member _.ObserveEffect _ = Ok effect

        member _.ApplyEffect(_, _) =
            applyCount <- applyCount + 1
            let receipt = sha "7"
            effect <- SettlementEffectApplied receipt
            if lostReply then SettlementEffectResponseUnknown else SettlementEffectAccepted receipt

        member _.ReadBack _ =
            match readbackOverride with
            | Some value -> Ok value
            | None ->
                match effect with
                | SettlementEffectApplied receipt -> Ok(Some receipt)
                | _ -> Ok None

let private signedPlan () =
    let plan = prepared ()
    let rsa = RSA.Create(2048)
    plan, rsa, anchor rsa "v2-test-key", authorization rsa "v2-test-key" plan writerBinding

let private errors result =
    match result with
    | Error failures -> failures
    | Ok _ -> failwith "expected refusal"

[<Fact>]
let ``preparation binds one merged main PR and stable workflow identity`` () =
    let observed = observation "OpenV2"
    let first = prepare observed [ association true observed.HeadSha ] readBinding writerBinding
    let second = prepare observed [ association true observed.HeadSha ] readBinding writerBinding
    Assert.Equal(first, second)
    let plan = first |> Result.defaultWith (sprintf "%A" >> failwith) |> fst
    Assert.Equal(101L, plan.RepositoryId)
    Assert.Equal(202L, plan.AuthorityRepositoryId)
    Assert.Equal(plan.OperationId + ":attempt:1", plan.AttemptId)

    Assert.Equal(Error [ MissingMergedPullRequest ], prepare observed [] readBinding writerBinding)
    Assert.Equal(Error [ MissingMergedPullRequest ], prepare observed [ association false observed.HeadSha ] readBinding writerBinding)
    Assert.Equal(Error [ AmbiguousMergedPullRequest ], prepare observed [ association true observed.HeadSha; association true observed.HeadSha ] readBinding writerBinding)
    Assert.Equal(Error [ MismatchedMergedPullRequest ], prepare observed [ association true (sha "8") ] readBinding writerBinding)

[<Fact>]
let ``preparation refuses stale source qualification epoch and credential scope`` () =
    let observed = observation "OpenV2"
    let associations = [ association true observed.HeadSha ]
    let failed = { observed with Checks = [ { Identity = "coherent-qualification"; AppId = 10L; Conclusion = CheckFailed } ] }
    match prepare failed associations readBinding writerBinding with
    | Error failures -> Assert.Contains(SettlementSourceFailure(RequiredCheckNotPassed "coherent-qualification"), failures)
    | Ok _ -> Assert.Fail "failed qualification accepted"
    Assert.Contains(SettlementPreOpenV2, prepare { observed with Epoch = "OperatingV1" } associations readBinding writerBinding |> errors)
    let broadWriter = { writerBinding with RepositoryIds = [ 101L; 202L ] }
    Assert.Contains(SettlementCredentialMismatch, prepare observed associations readBinding broadWriter |> errors)
    let broadReader = { readBinding with RepositoryIds = [ 101L; 202L ] }
    Assert.Contains(SettlementCredentialMismatch, prepare observed associations broadReader writerBinding |> errors)

[<Fact>]
let ``canonical authorization rejects wrong key anchor altered intent and stale plan`` () =
    let plan, rsa, trusted, signed = signedPlan ()
    Assert.Equal(Ok(), OrdinaryPostMergeSettlement.verifyAuthorization plan writerBinding trusted signed)
    use wrong = RSA.Create(2048)
    let wrongSignature = authorization wrong "v2-test-key" plan writerBinding
    Assert.Equal(Error [ SettlementTrustMismatch ], OrdinaryPostMergeSettlement.verifyAuthorization plan writerBinding trusted wrongSignature)
    let wrongKey = { signed with KeyId = "other" }
    Assert.Equal(Error [ SettlementTrustMismatch ], OrdinaryPostMergeSettlement.verifyAuthorization plan writerBinding trusted wrongKey)
    let invalidSignature = { signed with Signature = Array.zeroCreate signed.Signature.Length }
    Assert.Equal(Error [ SettlementSignatureInvalid ], OrdinaryPostMergeSettlement.verifyAuthorization plan writerBinding trusted invalidSignature)
    let altered = { signed with IntentSha256 = String.replicate 64 "0" }
    Assert.Equal(Error [ SettlementAlteredPlan ], OrdinaryPostMergeSettlement.verifyAuthorization plan writerBinding trusted altered)
    let stalePolicy = { plan with PolicyRevision = sha "1" }
    Assert.Contains(SettlementAlteredPlan, OrdinaryPostMergeSettlement.verifyAuthorization stalePolicy writerBinding trusted signed |> errors)
    let staleSource = { plan with SourceCommit = sha "2" }
    Assert.Contains(SettlementAlteredPlan, OrdinaryPostMergeSettlement.verifyAuthorization staleSource writerBinding trusted signed |> errors)
    let staleEpoch = { plan with EpochCommit = sha "3"; EpochGeneration = plan.EpochGeneration + 1L }
    Assert.Contains(SettlementAlteredPlan, OrdinaryPostMergeSettlement.verifyAuthorization staleEpoch writerBinding trusted signed |> errors)
    rsa.Dispose()

[<Fact>]
let ``expected-parent CAS initializes absent shard and preserves existing entries`` () =
    let plan, rsa, trusted, signed = signedPlan ()
    let first = Runtime(plan, None, None)
    Assert.Equal(Ok(SettlementSucceeded(sha "7")), OrdinaryPostMergeSettlement.execute plan writerBinding trusted signed SettlementNoCut first)
    Assert.Equal(None, first.Writes.Head |> fst)
    Assert.Equal(1, first.ApplyCount)

    let unrelated =
        { OperationId = "unrelated"; AttemptId = "old"; PlanDigest = sha "4"; Generation = 9L
          Stage = SettlementComplete; ReceiptDigest = Some(sha "5") }
    let existing = Runtime(plan, Some(sha "6"), Some unrelated)
    Assert.Equal(Ok(SettlementSucceeded(sha "7")), OrdinaryPostMergeSettlement.execute plan writerBinding trusted signed SettlementNoCut existing)
    Assert.Equal(Some unrelated, Map.tryFind "unrelated" existing.Shard.Entries)
    Assert.Equal(Some(sha "6"), existing.Writes.Head |> fst)
    rsa.Dispose()

[<Fact>]
let ``lost replies crashes and replay reconcile without duplicate effect`` () =
    let plan, rsa, trusted, signed = signedPlan ()
    let runtime = Runtime(plan, None, None)
    Assert.Equal(Ok(SettlementInterrupted "before-journal"), OrdinaryPostMergeSettlement.execute plan writerBinding trusted signed StopBeforeJournal runtime)
    Assert.Equal(Ok(SettlementInterrupted "after-intent"), OrdinaryPostMergeSettlement.execute plan writerBinding trusted signed OrdinarySettlementCut.StopAfterIntent runtime)
    runtime.LostReply <- true
    Assert.Equal(Ok(SettlementPending "effect-response-unknown"), OrdinaryPostMergeSettlement.execute plan writerBinding trusted signed SettlementNoCut runtime)
    Assert.Equal(1, runtime.ApplyCount)
    Assert.Equal(Ok(SettlementSucceeded(sha "7")), OrdinaryPostMergeSettlement.execute plan writerBinding trusted signed SettlementNoCut runtime)
    Assert.Equal(1, runtime.ApplyCount)
    Assert.Equal(Ok(SettlementAlreadyComplete(sha "7")), OrdinaryPostMergeSettlement.execute plan writerBinding trusted signed SettlementNoCut runtime)
    Assert.Equal(1, runtime.ApplyCount)

    let afterWrite = Runtime(plan, None, None)
    Assert.Equal(Ok(SettlementInterrupted "after-effect"), OrdinaryPostMergeSettlement.execute plan writerBinding trusted signed OrdinarySettlementCut.StopAfterEffect afterWrite)
    Assert.Equal(Ok(SettlementSucceeded(sha "7")), OrdinaryPostMergeSettlement.execute plan writerBinding trusted signed SettlementNoCut afterWrite)
    Assert.Equal(1, afterWrite.ApplyCount)
    rsa.Dispose()

[<Fact>]
let ``CAS race unknown write and independent readback fail closed`` () =
    let plan, rsa, trusted, signed = signedPlan ()
    let race = Runtime(plan, None, None)
    race.RejectNextWrite <- true
    Assert.Equal(Error [ SettlementJournalConflict ], OrdinaryPostMergeSettlement.execute plan writerBinding trusted signed SettlementNoCut race)

    let unknown = Runtime(plan, None, None)
    unknown.UnknownWrite <- true
    Assert.Equal(Ok(SettlementSucceeded(sha "7")), OrdinaryPostMergeSettlement.execute plan writerBinding trusted signed SettlementNoCut unknown)

    let mismatched = Runtime(plan, None, None)
    mismatched.ReadbackOverride <- Some(Some(sha "8"))
    Assert.Equal(Error [ SettlementReadbackMismatch ], OrdinaryPostMergeSettlement.execute plan writerBinding trusted signed SettlementNoCut mismatched)
    Assert.Equal(1, mismatched.ApplyCount)
    rsa.Dispose()

[<Fact>]
let ``one-attempt command refuses when trusted workflow provider is not installed`` () =
    Assert.Equal(
        Error [ SettlementProviderUnavailable "installed-provider-unavailable" ],
        OrdinarySettlementCommandContract.executeOneAttempt None
    )
