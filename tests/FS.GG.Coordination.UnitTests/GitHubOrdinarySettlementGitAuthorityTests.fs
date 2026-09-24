#nowarn "3391"

module FS.GG.Coordination.GitHubOrdinarySettlementGitAuthorityTests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open FS.GG.Coordination.GitHub

let private sha character = String.replicate 40 character

let private address =
    ShardedJournalAdapter.address Operation "ordinary:101:PR_node"
    |> Result.defaultWith (string >> failwith)

let private plan =
    { Schema = "fsgg.coordination.ordinary-post-merge-settlement-plan/1"
      OperationId = "ordinary-settlement:" + String.replicate 64 "1"
      AttemptId = "ordinary-settlement:" + String.replicate 64 "1" + ":attempt:1"
      OriginalPlanId = "ordinary-push:" + sha "8"
      Repository = "fs-gg/.github"
      RepositoryId = 101L
      AuthorityRepositoryId = 1351660651L
      PullRequestNumber = 42
      PullRequestNodeId = "PR_node"
      PullRequestHeadCommit = sha "2"
      SourceCommit = sha "8"
      SourceTree = sha "3"
      WorkflowRevision = sha "8"
      PolicyRevision = String.replicate 64 "4"
      Environment = "ordinary-v2"
      OperationClass = "ordinary-post-merge-delivery-settlement"
      Epoch = "OpenV2"
      EpochGeneration = 7L
      EpochCommit = sha "5"
      SourcePlanSeal = String.replicate 64 "6"
      RequiredChecks =
        [ { Identity = "contract-coherence / coherence"; AppId = 15368L; Conclusion = CheckPassed }
          { Identity = "routine-eligibility"; AppId = 15368L; Conclusion = CheckPassed } ]
      JournalAddress = address
      Seal = String.replicate 64 "7" }

let private binding =
    { AppId = 9001L; InstallationId = 9002L; RepositoryIds = [ 1351660651L ]
      Permissions = Map [ "contents", "write"; "metadata", "read" ] }

let private signed () =
    let rsa = RSA.Create(2048)
    let intent = OrdinaryPostMergeSettlement.canonicalIntent plan binding
    let digest = SHA256.HashData intent |> Convert.ToHexString |> _.ToLowerInvariant()
    let spki = rsa.ExportSubjectPublicKeyInfo() |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let anchor =
        { KeyId = "synthetic"; PublicKeySpkiSha256 = spki; AppId = binding.AppId
          InstallationId = binding.InstallationId; RepositoryId = 1351660651L; Permissions = binding.Permissions }
    let authorization =
        { KeyId = "synthetic"; PublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem(); IntentSha256 = digest
          Signature = rsa.SignData(intent, HashAlgorithmName.SHA256, RSASignaturePadding.Pss) }
    rsa, anchor, authorization

type private Transport() =
    let mutable state: (string * byte array) option = None
    let mutable revision = 0
    let mutable nextUnknown = false
    let mutable nextConflict = false
    let mutable protection = true
    let mutable writes = 0

    member _.Writes = writes
    member _.NextUnknown with get () = nextUnknown and set value = nextUnknown <- value
    member _.NextConflict with get () = nextConflict and set value = nextConflict <- value
    member _.Protection with get () = protection and set value = protection <- value

    interface IOrdinarySettlementGitAuthorityTransport with
        member _.ReadDocument observedAddress =
            if observedAddress <> address then SettlementDocumentReadUnknown "wrong-address"
            else
                match state with
                | None -> SettlementDocumentAbsent
                | Some(head, bytes) -> SettlementDocumentPresent(head, Array.copy bytes)

        member _.CompareExchangeDocument(observedAddress, expected, bytes) =
            let current = state |> Option.map fst
            if observedAddress <> address || nextConflict || current <> expected then
                nextConflict <- false
                SettlementCasConflict
            else
                writes <- writes + 1
                revision <- revision + 1
                let head = String.replicate 39 "0" + string revision
                state <- Some(head, Array.copy bytes)
                if nextUnknown then
                    nextUnknown <- false
                    SettlementCasUnknown
                else SettlementCasAccepted

        member _.VerifyCurrentProtection(_, appId) =
            if protection && appId = binding.AppId then Ok() else Error "ruleset-drift"

[<Fact>]
let ``canonical authority document preserves unrelated entries and effects`` () =
    let unrelated =
        { OperationId = "unrelated"; AttemptId = "attempt"; PlanDigest = String.replicate 64 "a"
          Generation = 2L; Stage = SettlementComplete; ReceiptDigest = Some(String.replicate 64 "b") }
    let source =
        { Address = address; Revision = Some(sha "c"); Entries = Map [ unrelated.OperationId, unrelated ]
          Effects = Map [ unrelated.OperationId, String.replicate 64 "b" ] }
    let encoded = OrdinarySettlementAuthorityDocument.encode source
    let decoded =
        OrdinarySettlementAuthorityDocument.decode address source.Revision (ReadOnlyMemory encoded)
        |> Result.defaultWith failwith
    Assert.Equal(source, decoded)
    Assert.Equal<byte>(encoded, OrdinarySettlementAuthorityDocument.encode decoded)

[<Fact>]
let ``synthetic authority reconciles lost effect response without duplicate CAS`` () =
    let transport = Transport()
    let runtime = OrdinarySettlementGitAuthority.Runtime(binding.AppId, transport)
    let rsa, anchor, authorization = signed ()
    transport.NextUnknown <- true
    // The first unknown is the intent CAS; durable reread must reconcile it.
    let first = OrdinaryPostMergeSettlement.execute plan binding anchor authorization SettlementNoCut runtime
    let receipt =
        match first with
        | Ok(SettlementSucceeded value) when value.Length = 64 -> value
        | value -> failwithf "unexpected settlement: %A" value
    Assert.Equal(4, transport.Writes)
    let replay = OrdinaryPostMergeSettlement.execute plan binding anchor authorization SettlementNoCut runtime
    Assert.Equal(Ok(SettlementAlreadyComplete receipt), replay)
    Assert.Equal(4, transport.Writes)
    rsa.Dispose()

[<Fact>]
let ``ruleset drift and competing sibling refuse before an effect`` () =
    let rsa, anchor, authorization = signed ()
    let drift = Transport()
    drift.Protection <- false
    let driftRuntime = OrdinarySettlementGitAuthority.Runtime(binding.AppId, drift)
    Assert.Equal(Error [ SettlementJournalConflict ], OrdinaryPostMergeSettlement.execute plan binding anchor authorization SettlementNoCut driftRuntime)
    Assert.Equal(0, drift.Writes)

    let sibling = Transport()
    sibling.NextConflict <- true
    let siblingRuntime = OrdinarySettlementGitAuthority.Runtime(binding.AppId, sibling)
    Assert.Equal(Error [ SettlementJournalConflict ], OrdinaryPostMergeSettlement.execute plan binding anchor authorization SettlementNoCut siblingRuntime)
    Assert.Equal(0, sibling.Writes)
    rsa.Dispose()

let private githubResponse status body =
    Response
        { StatusCode = status; Headers = Map.empty; Body = body; ETag = None
          RateBudget = { Limit = None; Remaining = None; ResetAt = None; Cost = Some 1 } }

let private authorityOptions =
    { ApiBase = Uri "https://api.github.invalid/"
      Token = "synthetic-authority-token"
      UserAgent = "fsgg-test"
      Repository = "FS-GG/FS.GG.Coordination.Authority"
      RepositoryId = 1351660651L
      ExpectedAppId = 9001L
      WriterRulesetId = 21872113L
      WriterRulesetName = "v2-journal-writer"
      IntegrityRulesetId = 21872115L
      IntegrityRulesetName = "v2-journal-integrity"
      RetainedWriterAppIds = [ 4882140L ]
      WriterIncludes = Set [ "refs/heads/fsgg/v2/journal/**/*" ]
      WriterExcludes = Set [ "refs/heads/fsgg/v2/journal/cutover/d5" ]
      IntegrityIncludes = Set [ "refs/heads/fsgg/v2/journal/**/*" ]
      IntegrityExcludes = Set.empty
      WriterUpdatedAt = "2026-09-09T09:25:34.606Z"
      IntegrityUpdatedAt = "2026-09-09T09:26:21.532Z"
      ExpectedEpochCommit = sha "9"
      ExpectedEpochGeneration = 7L
      EpochAggregateId = "fleet-cutover:fs-gg-production"
      EpochFleetId = "fs-gg-production"
      EpochRef = "refs/heads/fsgg/v2/journal/cutover/d5" }

type private GitHubAuthorityTransport(?badExclusion: bool, ?omitActors: bool, ?badEpoch: bool, ?patchOutcome: TransportOutcome) =
    let requests = ResizeArray<RestRequest>()
    let badExclusion = defaultArg badExclusion false
    let oidA, oidB, oidC, oidD, oidE = sha "a", sha "b", sha "c", sha "d", sha "e"
    let patchOutcome = defaultArg patchOutcome (githubResponse 200 $"{{\"object\":{{\"type\":\"commit\",\"sha\":\"{oidE}\"}}}}")
    let omitActors = defaultArg omitActors false
    let badEpoch = defaultArg badEpoch false
    let writerActors =
        if omitActors then ""
        else ",\"bypass_actors\":[{\"actor_id\":4882140,\"actor_type\":\"Integration\",\"bypass_mode\":\"always\"},{\"actor_id\":9001,\"actor_type\":\"Integration\",\"bypass_mode\":\"always\"}]"
    let writer =
        let exclusion = if badExclusion then "refs/heads/fsgg/v2/journal/other" else "refs/heads/fsgg/v2/journal/cutover/d5"
        $"{{\"id\":21872113,\"name\":\"v2-journal-writer\",\"enforcement\":\"active\",\"updated_at\":\"2026-09-09T11:25:34.606+02:00\",\"conditions\":{{\"ref_name\":{{\"include\":[\"refs/heads/fsgg/v2/journal/**/*\"],\"exclude\":[\"{exclusion}\"]}}}},\"rules\":[{{\"type\":\"creation\"}},{{\"type\":\"update\"}}]{writerActors}}}"
    let integrityActors = if omitActors then "" else ",\"bypass_actors\":[]"
    let integrity =
        $"{{\"id\":21872115,\"name\":\"v2-journal-integrity\",\"enforcement\":\"active\",\"updated_at\":\"2026-09-09T09:26:21.532Z\",\"conditions\":{{\"ref_name\":{{\"include\":[\"refs/heads/fsgg/v2/journal/**/*\"],\"exclude\":[]}}}},\"rules\":[{{\"type\":\"deletion\"}},{{\"type\":\"non_fast_forward\"}}]{integrityActors}}}"
    let effective =
        "[{\"type\":\"deletion\",\"ruleset_source_type\":\"Repository\",\"ruleset_source\":\"FS-GG/FS.GG.Coordination.Authority\",\"ruleset_id\":21872115},{\"type\":\"non_fast_forward\",\"ruleset_source_type\":\"Repository\",\"ruleset_source\":\"FS-GG/FS.GG.Coordination.Authority\",\"ruleset_id\":21872115},{\"type\":\"creation\",\"ruleset_source_type\":\"Repository\",\"ruleset_source\":\"FS-GG/FS.GG.Coordination.Authority\",\"ruleset_id\":21872113},{\"type\":\"update\",\"ruleset_source_type\":\"Repository\",\"ruleset_source\":\"FS-GG/FS.GG.Coordination.Authority\",\"ruleset_id\":21872113}]"
    let epochEvent = Encoding.UTF8.GetBytes "{\"fleetId\":\"fs-gg-production\",\"phase\":\"OpenV2\",\"schema\":\"fsgg.github-substrate.epoch-event/1\"}"
    let epochDigest = SHA256.HashData epochEvent |> Convert.ToHexString |> _.ToLowerInvariant()
    let epochOid = sha "9"
    let encoded bytes = Convert.ToBase64String bytes
    member _.Requests = requests |> Seq.toList
    interface IOrdinaryGitHubTransport with
        member _.Send request =
            let rest = match request with Rest value -> value | _ -> failwith "REST required"
            requests.Add rest
            let path = rest.Uri.AbsolutePath
            match rest.Method with
            | Get when path = "/repos/FS-GG/FS.GG.Coordination.Authority" ->
                githubResponse 200 "{\"id\":1351660651,\"full_name\":\"FS-GG/FS.GG.Coordination.Authority\"}"
            | Get when path = "/installation/repositories" ->
                githubResponse 200 "{\"total_count\":1,\"repositories\":[{\"id\":1351660651,\"full_name\":\"FS-GG/FS.GG.Coordination.Authority\"}]}"
            | Get when path.EndsWith("/git/ref/heads/fsgg/v2/journal/cutover/d5") ->
                let currentEpochOid = if badEpoch then sha "8" else epochOid
                githubResponse 200 $"{{\"object\":{{\"type\":\"commit\",\"sha\":\"{currentEpochOid}\"}}}}"
            | Get when path.EndsWith("/contents/head.json") ->
                let bytes = Encoding.UTF8.GetBytes $"{{\"aggregateDigest\":\"d546289f29b34a4967e27425acba1c9ad2feb4f4b2110f5db41a5544976cb363\",\"aggregateId\":\"fleet-cutover:fs-gg-production\",\"eventDigest\":\"{epochDigest}\",\"generation\":7,\"journalKind\":\"cutover\",\"shard\":\"d5\"}}"
                githubResponse 200 $"{{\"encoding\":\"base64\",\"content\":\"{encoded bytes}\"}}"
            | Get when path.EndsWith("/contents/event.json") ->
                githubResponse 200 $"{{\"encoding\":\"base64\",\"content\":\"{encoded epochEvent}\"}}"
            | Get when path.EndsWith("/rulesets/21872113") -> githubResponse 200 writer
            | Get when path.EndsWith("/rulesets/21872115") -> githubResponse 200 integrity
            | Get when path.Contains("/rules/branches/") -> githubResponse 200 effective
            | Get when path.Contains("/contents/ordinary-v2/") -> githubResponse 404 "{}"
            | Get when path.Contains("/git/ref/") -> githubResponse 200 $"{{\"object\":{{\"type\":\"commit\",\"sha\":\"{oidA}\"}}}}"
            | Get when path.Contains("/git/commits/") -> githubResponse 200 $"{{\"tree\":{{\"sha\":\"{oidB}\"}}}}"
            | Post when path.EndsWith("/git/blobs") -> githubResponse 201 $"{{\"sha\":\"{oidC}\"}}"
            | Post when path.EndsWith("/git/trees") -> githubResponse 201 $"{{\"sha\":\"{oidD}\"}}"
            | Post when path.EndsWith("/git/commits") -> githubResponse 201 $"{{\"sha\":\"{oidE}\",\"tree\":{{\"sha\":\"{oidD}\"}},\"parents\":[{{\"sha\":\"{oidA}\"}}]}}"
            | Patch when path.Contains("/git/refs/") -> patchOutcome
            | _ -> failwithf "unmatched request %A %s" rest.Method path

[<Fact>]
let ``authenticated ruleset read binds exact exclusions actors and effective rules`` () =
    let goodTransport = GitHubAuthorityTransport()
    let good = OrdinarySettlementGitHubAuthority.Transport(authorityOptions, goodTransport) :> IOrdinarySettlementGitAuthorityTransport
    Assert.Equal(Ok(), good.VerifyCurrentProtection(address, 9001L))
    let lowerPrivilegeTransport = GitHubAuthorityTransport(omitActors = true)
    let lowerPrivilege = OrdinarySettlementGitHubAuthority.Transport(authorityOptions, lowerPrivilegeTransport) :> IOrdinarySettlementGitAuthorityTransport
    Assert.Equal(Ok(), lowerPrivilege.VerifyCurrentProtection(address, 9001L))
    let badTransport = GitHubAuthorityTransport(badExclusion = true)
    let bad = OrdinarySettlementGitHubAuthority.Transport(authorityOptions, badTransport) :> IOrdinarySettlementGitAuthorityTransport
    Assert.Equal(Error "authority-ruleset-drift", bad.VerifyCurrentProtection(address, 9001L))

[<Fact>]
let ``git authority CAS uses sole observed parent and refuses sibling update`` () =
    let loopback = GitHubAuthorityTransport(patchOutcome = githubResponse 422 "{}")
    let transport = OrdinarySettlementGitHubAuthority.Transport(authorityOptions, loopback) :> IOrdinarySettlementGitAuthorityTransport
    let outcome = transport.CompareExchangeDocument(address, Some(sha "a"), [| 1uy; 2uy |])
    Assert.Equal(SettlementCasConflict, outcome)
    let commitRequest = loopback.Requests |> List.find (fun value -> value.Method = Post && value.Uri.AbsolutePath.EndsWith("/git/commits"))
    use commit = JsonDocument.Parse(commitRequest.Body.Value)
    let parents = commit.RootElement.GetProperty("parents").EnumerateArray() |> Seq.toList
    Assert.Single(parents) |> ignore
    Assert.Equal(sha "a", parents[0].GetString())
    let patch = loopback.Requests |> List.find (fun value -> value.Method = Patch)
    use patchBody = JsonDocument.Parse(patch.Body.Value)
    Assert.False(patchBody.RootElement.GetProperty("force").GetBoolean())

[<Fact>]
let ``existing shared shard treats a different aggregate file as known empty at its parent`` () =
    let otherAddress =
        Seq.initInfinite (fun index -> ShardedJournalAdapter.address Operation $"ordinary:101:PR_other_{index}")
        |> Seq.choose Result.toOption
        |> Seq.find (fun candidate -> candidate.Ref = address.Ref && candidate.Digest <> address.Digest)
    let loopback = GitHubAuthorityTransport()
    let transport = OrdinarySettlementGitHubAuthority.Transport(authorityOptions, loopback) :> IOrdinarySettlementGitAuthorityTransport
    Assert.Equal(SettlementDocumentKnownEmpty(sha "a"), transport.ReadDocument otherAddress)
    Assert.Equal(SettlementCasAccepted, transport.CompareExchangeDocument(otherAddress, Some(sha "a"), [| 1uy |]))
    let treeRequest = loopback.Requests |> List.find (fun value -> value.Method = Post && value.Uri.AbsolutePath.EndsWith("/git/trees"))
    use tree = JsonDocument.Parse(treeRequest.Body.Value)
    Assert.Equal(sha "b", tree.RootElement.GetProperty("base_tree").GetString())
    let treeEntries = tree.RootElement.GetProperty("tree")
    let writtenPath = treeEntries[0].GetProperty("path").GetString()
    Assert.Equal($"ordinary-v2/{otherAddress.Digest}.json", writtenPath)

[<Fact>]
let ``git authority CAS reports lost ref reply as unknown for durable reconciliation`` () =
    let loopback = GitHubAuthorityTransport(patchOutcome = NetworkFailure)
    let transport = OrdinarySettlementGitHubAuthority.Transport(authorityOptions, loopback) :> IOrdinarySettlementGitAuthorityTransport
    Assert.Equal(SettlementCasUnknown, transport.CompareExchangeDocument(address, Some(sha "a"), [| 1uy |]))

[<Fact>]
let ``git authority CAS refuses when current epoch moved before write`` () =
    let loopback = GitHubAuthorityTransport(badEpoch = true)
    let transport = OrdinarySettlementGitHubAuthority.Transport(authorityOptions, loopback) :> IOrdinarySettlementGitAuthorityTransport
    Assert.Equal(SettlementCasConflict, transport.CompareExchangeDocument(address, Some(sha "a"), [| 1uy |]))
    Assert.DoesNotContain(loopback.Requests, fun value -> value.Method = Patch)

[<Fact>]
let ``git authority CAS accepts only a response bound to the proposed commit`` () =
    let acceptedLoopback = GitHubAuthorityTransport()
    let accepted = OrdinarySettlementGitHubAuthority.Transport(authorityOptions, acceptedLoopback) :> IOrdinarySettlementGitAuthorityTransport
    Assert.Equal(SettlementCasAccepted, accepted.CompareExchangeDocument(address, Some(sha "a"), [| 1uy |]))
    let unboundLoopback = GitHubAuthorityTransport(patchOutcome = githubResponse 200 "{}")
    let unbound = OrdinarySettlementGitHubAuthority.Transport(authorityOptions, unboundLoopback) :> IOrdinarySettlementGitAuthorityTransport
    Assert.Equal(SettlementCasUnknown, unbound.CompareExchangeDocument(address, Some(sha "a"), [| 1uy |]))

[<Fact>]
let ``production command profile is pinned and refuses isolated rehearsal`` () =
    Assert.Equal(1351660651L, OrdinarySettlementAuthorityProfiles.production.RepositoryId)
    Assert.Equal(21872113L, OrdinarySettlementAuthorityProfiles.production.WriterRulesetId)
    Assert.Equal(1385801070L, OrdinarySettlementAuthorityProfiles.rehearsal.RepositoryId)
    Assert.Equal(23947019L, OrdinarySettlementAuthorityProfiles.rehearsal.WriterRulesetId)
    Assert.Equal("v2-journal-writer-rehearsal", OrdinarySettlementAuthorityProfiles.rehearsal.WriterRulesetName)
    Assert.Equal("refs/heads/ordinary-v2-rehearsal-epoch", OrdinarySettlementAuthorityProfiles.rehearsal.EpochRef)
    let rehearsalEpoch =
        ShardedJournalAdapter.address Cutover OrdinarySettlementAuthorityProfiles.rehearsal.EpochAggregateId
        |> Result.defaultWith (string >> failwith)
    Assert.Equal("2ad14cd9bc05b8290320fbadd752f873464ee7c256926cd1bb4a32fdf23abdc5", rehearsalEpoch.Digest)
    Assert.Equal(Ok OrdinarySettlementAuthorityProfiles.production,
                 OrdinarySettlementAuthorityProfiles.requireProduction OrdinarySettlementAuthorityProfiles.production)
    Assert.Equal(Error "production-command-refuses-rehearsal-profile",
                 OrdinarySettlementAuthorityProfiles.requireProduction OrdinarySettlementAuthorityProfiles.rehearsal)

let private publicAnchor exclusion retainedActor =
    let spki = String.replicate 64 "a"
    let sourceCommit = sha "f"
    let retained =
        if retainedActor then
            ",{\"actorId\":4882140,\"actorType\":\"Integration\",\"bypassMode\":\"always\"}"
        else ""
    $"{{\"schema\":\"fsgg.github.v2-ci-ordinary-settlement-anchor/1\",\"policyId\":\"v2-ci-i1-ordinary-settlement-v1\",\"operationClass\":\"ordinary-post-merge-delivery-settlement\",\"authorizer\":{{\"keyId\":\"key-1\",\"algorithm\":\"RSA-PSS-SHA256\",\"publicKeySpkiSha256\":\"{spki}\"}},\"writer\":{{\"appId\":9001,\"installationId\":9002,\"repository\":\"FS-GG/FS.GG.Coordination.Authority\",\"repositoryId\":1351660651,\"permissions\":{{\"contents\":\"write\",\"metadata\":\"read\"}}}},\"rulesets\":{{\"writer\":{{\"id\":21872113,\"name\":\"v2-journal-writer\",\"enforcement\":\"active\",\"updatedAt\":\"2026-09-09T09:25:34.606Z\",\"conditions\":{{\"ref_name\":{{\"include\":[\"refs/heads/fsgg/v2/journal/**/*\"],\"exclude\":[\"{exclusion}\"]}}}},\"rules\":[{{\"type\":\"creation\"}},{{\"type\":\"update\"}}],\"bypassActors\":[{{\"actorId\":9001,\"actorType\":\"Integration\",\"bypassMode\":\"always\"}}{retained}]}},\"integrity\":{{\"id\":21872115,\"name\":\"v2-journal-integrity\",\"enforcement\":\"active\",\"updatedAt\":\"2026-09-09T09:26:21.532Z\",\"conditions\":{{\"ref_name\":{{\"include\":[\"refs/heads/fsgg/v2/journal/**/*\"],\"exclude\":[]}}}},\"rules\":[{{\"type\":\"deletion\"}},{{\"type\":\"non_fast_forward\"}}],\"bypassActors\":[]}}}},\"effectiveRules\":[{{\"type\":\"creation\",\"rulesetId\":21872113,\"rulesetSourceType\":\"Repository\",\"rulesetSource\":\"FS-GG/FS.GG.Coordination.Authority\"}},{{\"type\":\"update\",\"rulesetId\":21872113,\"rulesetSourceType\":\"Repository\",\"rulesetSource\":\"FS-GG/FS.GG.Coordination.Authority\"}},{{\"type\":\"deletion\",\"rulesetId\":21872115,\"rulesetSourceType\":\"Repository\",\"rulesetSource\":\"FS-GG/FS.GG.Coordination.Authority\"}},{{\"type\":\"non_fast_forward\",\"rulesetId\":21872115,\"rulesetSourceType\":\"Repository\",\"rulesetSource\":\"FS-GG/FS.GG.Coordination.Authority\"}}],\"acceptedAt\":\"2026-09-24T00:00:00Z\",\"sourceCommit\":\"{sourceCommit}\"}}"
    |> Encoding.UTF8.GetBytes

[<Fact>]
let ``public anchor pins exact production exclusion and bypass actor population`` () =
    let profile = OrdinarySettlementAuthorityProfiles.production
    let good = OrdinarySettlementPublicAnchor.parse profile (ReadOnlyMemory(publicAnchor "refs/heads/fsgg/v2/journal/cutover/d5" true))
    Assert.True(Result.isOk good)
    let missingIncumbent = OrdinarySettlementPublicAnchor.parse profile (ReadOnlyMemory(publicAnchor "refs/heads/fsgg/v2/journal/cutover/d5" false))
    Assert.Equal(Error "ordinary-settlement-anchor-binding", missingIncumbent)
    let changedExclusion = OrdinarySettlementPublicAnchor.parse profile (ReadOnlyMemory(publicAnchor "refs/heads/fsgg/v2/journal/other" true))
    Assert.Equal(Error "ordinary-settlement-anchor-binding", changedExclusion)
    let tooSoon =
        publicAnchor "refs/heads/fsgg/v2/journal/cutover/d5" true
        |> Encoding.UTF8.GetString
        |> _.Replace("2026-09-24T00:00:00Z", "2026-09-09T09:26:30Z")
        |> Encoding.UTF8.GetBytes
    Assert.Equal(Error "ordinary-settlement-anchor-binding", OrdinarySettlementPublicAnchor.parse profile (ReadOnlyMemory tooSoon))

[<Fact>]
let ``public anchor accepts only the pinned rehearsal repository and ruleset names`` () =
    let bytes =
        publicAnchor "refs/heads/fsgg/v2/journal/cutover/d5" true
        |> Encoding.UTF8.GetString
        |> _.Replace("v2-ci-i1-ordinary-settlement-v1", "v2-ci-i1-ordinary-settlement-rehearsal-v1")
        |> _.Replace("FS-GG/FS.GG.Coordination.Authority", "FS-GG/FS.GG.Coordination.Authority.Sandbox")
        |> _.Replace("1351660651", "1385801070")
        |> _.Replace("21872113", "23947019")
        |> _.Replace("21872115", "23947025")
        |> _.Replace("v2-journal-writer", "v2-journal-writer-rehearsal")
        |> _.Replace("v2-journal-integrity", "v2-journal-integrity-rehearsal")
        |> _.Replace("[\"refs/heads/fsgg/v2/journal/cutover/d5\"]", "[]")
        |> _.Replace(",{\"actorId\":4882140,\"actorType\":\"Integration\",\"bypassMode\":\"always\"}", "")
        |> Encoding.UTF8.GetBytes
    Assert.True(
        OrdinarySettlementPublicAnchor.parse OrdinarySettlementAuthorityProfiles.rehearsal (ReadOnlyMemory bytes)
        |> Result.isOk)
