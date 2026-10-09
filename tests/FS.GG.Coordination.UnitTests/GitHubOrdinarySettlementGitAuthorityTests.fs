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

type private GitHubAuthorityTransport(?badExclusion: bool, ?omitActors: bool, ?badEpoch: bool, ?patchOutcome: TransportOutcome, ?mainDirectory: bool) =
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
    let storageRules (body: string) =
        if defaultArg mainDirectory false then
            body.Replace("refs/heads/fsgg/v2/journal/**/*", "refs/heads/main")
                .Replace("[\"refs/heads/fsgg/v2/journal/cutover/d5\"]", "[]")
        else body
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
            | Get when path.EndsWith("/rulesets/21872113") -> githubResponse 200 (storageRules writer)
            | Get when path.EndsWith("/rulesets/21872115") -> githubResponse 200 (storageRules integrity)
            | Get when path.Contains("/rules/branches/") -> githubResponse 200 effective
            | Get when path.Contains("/contents/ordinary-v2/") || path.Contains("/contents/state/ordinary-v2/") -> githubResponse 404 "{}"
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

let private mainOptions =
    { authorityOptions with
        WriterIncludes = Set [ "refs/heads/main" ]; WriterExcludes = Set.empty
        IntegrityIncludes = Set [ "refs/heads/main" ]; IntegrityExcludes = Set.empty }

[<Fact>]
let ``main storage requires exact main protection and visible actors`` () =
    let create options wire =
        OrdinarySettlementGitHubAuthority.Transport(options, wire, OrdinarySettlementGitStorage.MainDirectory)
        :> IOrdinarySettlementGitAuthorityTransport
    Assert.Equal(Ok(), (create mainOptions (GitHubAuthorityTransport(mainDirectory = true))).VerifyCurrentProtection(address, 9001L))
    Assert.Equal(Error "authority-ruleset-drift", (create authorityOptions (GitHubAuthorityTransport())).VerifyCurrentProtection(address, 9001L))
    Assert.Equal(Error "authority-ruleset-drift", (create mainOptions (GitHubAuthorityTransport(mainDirectory = true, omitActors = true))).VerifyCurrentProtection(address, 9001L))

/// Local Git supplies actual objects, trees, parents, and atomic refs; the HTTP seam supplies only
/// the GitHub wire envelope and pinned synthetic protection/epoch observations. No remote effects.
type private LocalGitMainTransport() =
    let directory = IO.Path.Combine(IO.Path.GetTempPath(), "ordinary-main-" + Guid.NewGuid().ToString("N"))
    let policy = GitHubAuthorityTransport(mainDirectory = true) :> IOrdinaryGitHubTransport
    let mutable raceNextPatch = false
    let mutable loseNextReply = false
    let mutable patches = 0
    let run arguments input =
        let start = Diagnostics.ProcessStartInfo("git")
        start.WorkingDirectory <- directory
        start.RedirectStandardInput <- true
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.UseShellExecute <- false
        for argument in arguments do start.ArgumentList.Add argument
        for key, value in [ "GIT_AUTHOR_NAME", "fixture"; "GIT_AUTHOR_EMAIL", "fixture@example.invalid"
                            "GIT_COMMITTER_NAME", "fixture"; "GIT_COMMITTER_EMAIL", "fixture@example.invalid"
                            "GIT_CONFIG_NOSYSTEM", "1"; "GIT_CONFIG_GLOBAL", IO.Path.Combine(directory, "empty-config") ] do
            start.Environment[key] <- value
        use child = Diagnostics.Process.Start start
        child.StandardInput.Write(defaultArg input "")
        child.StandardInput.Close()
        let output = child.StandardOutput.ReadToEnd()
        let error = child.StandardError.ReadToEnd()
        child.WaitForExit()
        child.ExitCode, output, error
    let git arguments input =
        let code, output, error = run arguments input
        if code <> 0 then failwithf "local Git failed: %A: %s" arguments error
        output.TrimEnd('\n', '\r')
    let refResponse status revision =
        githubResponse status (JsonSerializer.Serialize {| ``object`` = {| ``type`` = "commit"; sha = revision |} |})
    do
        IO.Directory.CreateDirectory directory |> ignore
        git [ "init"; "--initial-branch=main"; "--quiet" ] None |> ignore
        IO.File.WriteAllText(IO.Path.Combine(directory, "README.md"), "retained main content\n")
        git [ "add"; "README.md" ] None |> ignore
        git [ "commit"; "--quiet"; "-m"; "seed" ] None |> ignore
    member _.Head = git [ "rev-parse"; "refs/heads/main" ] None
    member _.Read path =
        match run [ "show"; "refs/heads/main:" + path ] None with
        | 0, value, _ -> value
        | _, _, error -> failwith error
    member _.Patches = patches
    member _.RaceNextPatch with set value = raceNextPatch <- value
    member _.LoseNextReply with set value = loseNextReply <- value
    member _.RemoveMain() = git [ "update-ref"; "-d"; "refs/heads/main" ] None |> ignore
    interface IDisposable with
        member _.Dispose() = IO.Directory.Delete(directory, true)
    interface IOrdinaryGitHubTransport with
        member _.Send request =
            let rest = match request with Rest value -> value | _ -> failwith "REST required"
            let path = Uri.UnescapeDataString rest.Uri.AbsolutePath
            let body () = JsonDocument.Parse rest.Body.Value
            match rest.Method with
            | Get when path.EndsWith("/git/ref/heads/main") ->
                match run [ "rev-parse"; "--verify"; "refs/heads/main" ] None with
                | 0, value, _ -> refResponse 200 (value.Trim())
                | _ -> githubResponse 404 "{}"
            | Get when path.Contains("/contents/state/ordinary-v2/") ->
                let file = path.Substring(path.IndexOf("/contents/", StringComparison.Ordinal) + 10)
                let revision = Uri.UnescapeDataString(rest.Uri.Query.Substring(5))
                match run [ "show"; revision + ":" + file ] None with
                | 0, value, _ -> githubResponse 200 (JsonSerializer.Serialize {| encoding = "base64"; content = Convert.ToBase64String(Encoding.UTF8.GetBytes value) |})
                | _ -> githubResponse 404 "{}"
            | Get when path.Contains("/git/commits/") ->
                let revision = path.Substring(path.LastIndexOf('/') + 1)
                let tree = git [ "rev-parse"; revision + "^{tree}" ] None
                githubResponse 200 (JsonSerializer.Serialize {| tree = {| sha = tree |} |})
            | Post when path.EndsWith("/git/blobs") ->
                use document = body ()
                let bytes = Convert.FromBase64String(document.RootElement.GetProperty("content").GetString())
                let blob = git [ "hash-object"; "-w"; "--stdin" ] (Some(Encoding.UTF8.GetString bytes))
                githubResponse 201 (JsonSerializer.Serialize {| sha = blob |})
            | Post when path.EndsWith("/git/trees") ->
                use document = body ()
                let root = document.RootElement
                git [ "read-tree"; root.GetProperty("base_tree").GetString() ] None |> ignore
                for entry in root.GetProperty("tree").EnumerateArray() do
                    git [ "update-index"; "--add"; "--cacheinfo"; entry.GetProperty("mode").GetString()
                          entry.GetProperty("sha").GetString(); entry.GetProperty("path").GetString() ] None |> ignore
                let tree = git [ "write-tree" ] None
                githubResponse 201 (JsonSerializer.Serialize {| sha = tree |})
            | Post when path.EndsWith("/git/commits") ->
                use document = body ()
                let root = document.RootElement
                let tree = root.GetProperty("tree").GetString()
                let parents = root.GetProperty("parents").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
                let arguments = [ "commit-tree"; tree ] @ (parents |> List.collect (fun parent -> [ "-p"; parent ]))
                let revision = git arguments (Some(root.GetProperty("message").GetString()))
                githubResponse 201 (JsonSerializer.Serialize {| sha = revision; tree = {| sha = tree |}; parents = parents |> List.map (fun parent -> {| sha = parent |}) |})
            | Patch when path.EndsWith("/git/refs/heads/main") ->
                use document = body ()
                Assert.False(document.RootElement.GetProperty("force").GetBoolean())
                let proposed = document.RootElement.GetProperty("sha").GetString()
                let before = git [ "rev-parse"; "refs/heads/main" ] None
                if raceNextPatch then
                    raceNextPatch <- false
                    let tree = git [ "rev-parse"; before + "^{tree}" ] None
                    let sibling = git [ "commit-tree"; tree; "-p"; before ] (Some "competing main writer")
                    git [ "update-ref"; "refs/heads/main"; sibling; before ] None |> ignore
                let current = git [ "rev-parse"; "refs/heads/main" ] None
                match run [ "merge-base"; "--is-ancestor"; current; proposed ] None with
                | 0, _, _ ->
                    match run [ "update-ref"; "refs/heads/main"; proposed; current ] None with
                    | 0, _, _ ->
                        patches <- patches + 1
                        if loseNextReply then loseNextReply <- false; NetworkFailure
                        else refResponse 200 proposed
                    | _ -> githubResponse 422 "{}"
                | _ -> githubResponse 422 "{}"
            | _ -> policy.Send request

[<Fact>]
let ``main storage completes and replays real local Git settlement preserving unrelated state`` () =
    use wire = new LocalGitMainTransport()
    let transport = OrdinarySettlementGitHubAuthority.Transport(mainOptions, wire, OrdinarySettlementGitStorage.MainDirectory) :> IOrdinarySettlementGitAuthorityTransport
    let otherAddress = ShardedJournalAdapter.address Operation "ordinary:101:another-pr" |> Result.defaultWith (string >> failwith)
    let other = { Address = otherAddress; Revision = None; Entries = Map.empty; Effects = Map.empty }
    let otherBytes = OrdinarySettlementAuthorityDocument.encode other
    Assert.Equal(SettlementCasAccepted, transport.CompareExchangeDocument(otherAddress, Some wire.Head, otherBytes))
    let runtime = OrdinarySettlementGitAuthority.Runtime(binding.AppId, transport)
    let rsa, anchor, authorization = signed ()
    use _key = rsa
    wire.LoseNextReply <- true
    let receipt =
        match OrdinaryPostMergeSettlement.execute plan binding anchor authorization SettlementNoCut runtime with
        | Ok(SettlementSucceeded value) -> value
        | value -> failwithf "unexpected settlement: %A" value
    Assert.Equal(5, wire.Patches)
    Assert.Equal(Ok(SettlementAlreadyComplete receipt), OrdinaryPostMergeSettlement.execute plan binding anchor authorization SettlementNoCut runtime)
    Assert.Equal(5, wire.Patches)
    Assert.Equal("retained main content\n", wire.Read "README.md")
    Assert.Equal(Encoding.UTF8.GetString otherBytes, wire.Read $"state/ordinary-v2/{otherAddress.Digest}.json")
    match transport.ReadDocument address with
    | SettlementDocumentPresent(revision, bytes) ->
        let decoded = OrdinarySettlementAuthorityDocument.decode address (Some revision) (ReadOnlyMemory bytes) |> Result.defaultWith failwith
        Assert.Equal(address, decoded.Address)
        Assert.Equal(receipt, decoded.Effects[plan.OperationId])
    | value -> failwithf "unexpected read: %A" value

[<Fact>]
let ``main storage refuses sibling race stale reads and absent main without bootstrap`` () =
    use wire = new LocalGitMainTransport()
    let transport = OrdinarySettlementGitHubAuthority.Transport(mainOptions, wire, OrdinarySettlementGitStorage.MainDirectory) :> IOrdinarySettlementGitAuthorityTransport
    let original = wire.Head
    wire.RaceNextPatch <- true
    Assert.Equal(SettlementCasConflict, transport.CompareExchangeDocument(address, Some original, [| 1uy |]))
    Assert.NotEqual(original, wire.Head)
    Assert.Equal(SettlementCasConflict, transport.CompareExchangeDocument(address, Some original, [| 1uy |]))
    Assert.Equal(0, wire.Patches)
    wire.RemoveMain()
    Assert.Equal(SettlementDocumentReadUnknown "authority-main-missing", transport.ReadDocument address)
    Assert.Equal(SettlementCasConflict, transport.CompareExchangeDocument(address, None, [| 1uy |]))
    Assert.Equal(0, wire.Patches)
