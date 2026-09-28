module FS.GG.Coordination.MigrationReceiverAuthorityComposerTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Cli
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private sha (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData
    |> Convert.ToHexString |> _.ToLowerInvariant()

let private receiverNames =
    [ "sdd"; "rendering"; "governance"; "templates"; "game"; "audio"; "net" ]

let private repository =
    { Id=42L; NodeId="R_42"; FullName="FS-GG/FS.GG.GitHub.Substrate.Sandbox"
      SourceHead=String.replicate 40 "a"; TargetHead=String.replicate 40 "b" }

let private revision index = String.replicate 39 "a" + string index
let private targetRevision index = String.replicate 39 "d" + string index
let private tree index = String.replicate 39 "b" + string index
let private receiverRef receiver = $"refs/heads/gs2-09-7/run/receivers/{receiver}"

let private cohort =
    { Repositories=[ repository ]
      Receivers=
        receiverNames
        |> List.mapi (fun index receiver ->
            { Receiver=receiver; RepositoryId=repository.Id; RefName=receiverRef receiver
              ExpectedHead=targetRevision (index + 1) })
      ProjectOrganization="FS-GG"; ProjectNumber=2; ProjectNodeId="PVT_2"
      SourceRevision=String.replicate 40 "c"; Isolated=true }

let private page uri payload identity =
    { RequestedUri=uri; RequestIdentitySha256=sha uri; RawBody=payload; PayloadSha256=sha payload
      NextRequestIdentitySha256=None
      Subjects=[ { Identity=identity; Revision=sha identity; PayloadSha256=sha payload } ] }

let private proof authority uri =
    let evidence = page uri $"{{\"authority\":\"{authority}\"}}" $"subject:{authority}"
    { CohortSha256=GitHubMigrationInspect.cohortSha256 cohort
      ScopeVerified=true; SubjectsParsedFromRaw=true
      Read={ Authority=authority; ObservedAt=DateTimeOffset.Parse "2026-09-28T00:00:00Z"
             PageCount=1; ItemCount=1; Terminal=true; NextCursor=None
             HighWaterMark=sha authority; Subjects=evidence.Subjects }
      Pages=[ evidence ] }

let private mappings =
    receiverNames
    |> List.mapi (fun index receiver ->
        { ReceiverCopyId=receiver; ReceiverSourceRepository=$"FS-GG/source-{receiver}"; ReceiverCopyRepository=repository.FullName
          ReceiverCopySourceRevision=revision (index + 1); ReceiverCopySourceTree=tree (index + 1)
          ReceiverCopyPlannedRef=receiverRef receiver; ReceiverCopyRequiredEntries=[]
          ReceiverCopyMissingBlobSha1s=[] })

let private bindings =
    receiverNames
    |> List.mapi (fun index receiver ->
        { ReceiverName=receiver; RepositoryId=repository.Id; RepositoryNodeId=repository.NodeId
          RepositoryFullName=repository.FullName; RefName=receiverRef receiver
          CommitSha=targetRevision (index + 1); TreeSha=tree (index + 1) })

let private rosterRepository =
    { RosterRepositoryId=repository.Id; RosterRepositoryNodeId=repository.NodeId
      RosterRepositoryFullName=repository.FullName; RosterPrivate=true
      RosterArchived=false; RosterDisabled=false; RosterPermissions=Map [ "pull", true ] }

let private facts =
    { Cohort=cohort; CopyPlanFingerprint=String.replicate 64 "d"
      BlobCoverageFingerprint=String.replicate 64 "e"; BlobObjectCount=8999
      Mappings=mappings; Bindings=bindings; SignedHeadReceiverNames=receiverNames
      RosterRepositories=[ rosterRepository ]; ScopedSettingsSha256=String.replicate 64 "f"
      RepositoryRosterSha256=String.replicate 64 "1"
      RosterPages=[ page "https://api.github.test/app/installations/7" "{}" "receiver-scope:installation:7" ]
      ReceiverProof=proof "receiver-identities/declared" "https://api.github.test/receiver-proof"
      WorkflowProof=proof "workflow-pins/provider-tree-signed-tools" "https://api.github.test/workflow-proof" }

let private compose value = MigrationReceiverAuthorityComposer.composeVerifiedForTests value
let private expectError (fragment: string) (result: Result<MigrationReceiverAuthorityComposition, string>) =
    match result with
    | Error reason -> Assert.Contains(fragment, reason)
    | Ok _ -> Assert.Fail($"expected refusal containing {fragment}")

[<Fact>]
let ``verified exhaustive evidence composes both canonical authorities`` () =
    match compose facts with
    | Error reason -> Assert.Fail reason
    | Ok result ->
        let firstReceiver, secondReceiver = result.ReceiverIdentities
        let firstWorkflow, secondWorkflow = result.WorkflowPins
        Assert.Equal("receiver-identities", firstReceiver.Read.Authority)
        Assert.Equal("workflow-pins", firstWorkflow.Read.Authority)
        Assert.Equal(firstReceiver, secondReceiver)
        Assert.Equal(firstWorkflow, secondWorkflow)
        Assert.Equal(2, firstReceiver.Read.PageCount)
        Assert.Equal(2, firstWorkflow.Read.PageCount)
        Assert.Equal(8999, facts.BlobObjectCount)
        Assert.Equal(facts.CopyPlanFingerprint, result.CopyPlanFingerprint)
        Assert.False(facts.ReceiverProof.Read.HighWaterMark = firstReceiver.Read.HighWaterMark)

[<Fact>]
let ``partial unknown or contradictory inputs retain explicit refusal`` () =
    compose { facts with BlobObjectCount=8998 } |> expectError "copy-custody"
    compose { facts with SignedHeadReceiverNames=receiverNames.Tail } |> expectError "receiver-population"
    compose { facts with RosterRepositories=[] } |> expectError "provider-roster-mismatch"
    compose { facts with ReceiverProof={ facts.ReceiverProof with ScopeVerified=false } }
    |> expectError "partial-proof"
    let contradictory =
        { facts.WorkflowProof with
            Pages=[ page facts.ReceiverProof.Pages.Head.RequestedUri "changed" "changed" ] }
    compose { facts with WorkflowProof=contradictory }
    |> expectError "contradictory-provider-page"

[<Fact>]
let ``copy ref revision tree and repository correspondence are exact`` () =
    let first = facts.Bindings.Head
    compose { facts with Bindings={ first with RefName=first.RefName + "-changed" } :: facts.Bindings.Tail }
    |> expectError "copy-plan-provider-mismatch"
    compose { facts with Bindings={ first with TreeSha=String.replicate 40 "0" } :: facts.Bindings.Tail }
    |> expectError "copy-plan-provider-mismatch"
    compose { facts with Bindings={ first with RepositoryFullName="FS-GG/other" } :: facts.Bindings.Tail }
    |> expectError "copy-plan-provider-mismatch"

[<Fact>]
let ``partial proof names cannot be promoted by the pure composer`` () =
    compose { facts with ReceiverProof={ facts.ReceiverProof with Read={ facts.ReceiverProof.Read with Authority="receiver-identities" } } }
    |> expectError "partial-proof"
    compose { facts with WorkflowProof={ facts.WorkflowProof with Read={ facts.WorkflowProof.Read with Authority="workflow-pins" } } }
    |> expectError "partial-proof"

type private FakeRosterTransport(responses: TransportOutcome list) =
    let responses = Queue<TransportOutcome>(responses)
    interface IMigrationGitHubReadTransport with
        member _.Send _ = if responses.Count = 0 then NetworkFailure else responses.Dequeue()

let private response body =
    Response
        { StatusCode=200; Headers=Map.empty; Body=body; ETag=None
          RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }

let private rosterOptions =
    { ApiBase=Uri "https://api.github.test/"; InstallationId=7L
      AccountLogin="FS-GG"; AccountId=9L; AccountNodeId="ORG_9"
      RequiredPermissions=Map [ "contents", "read"; "metadata", "read" ]
      AppToken="app"; InstallationToken="installation"; UserAgent="composer-test" }

let private installation =
    """{"id":7,"target_id":9,"target_type":"Organization","account":{"login":"FS-GG","id":9,"node_id":"ORG_9"},"repository_selection":"selected","permissions":{"contents":"read","metadata":"read"},"repositories_url":"https://api.github.test/installation/repositories","suspended_at":null}"""

let private repositoryPage =
    """{"total_count":1,"repositories":[{"id":42,"node_id":"R_42","full_name":"FS-GG/FS.GG.GitHub.Substrate.Sandbox","private":true,"archived":false,"disabled":false,"permissions":{"pull":true}}]}"""

let private rosterCapture () =
    match MigrationReceiverRosterRead.captureTwoPass
              rosterOptions
              (FakeRosterTransport [ response installation; response repositoryPage
                                     response installation; response repositoryPage ]) with
    | Ok capture -> capture
    | Error reason -> failwith reason

let private providerOptions =
    { Cohort=cohort
      Repository=
        { ApiBase=Uri "https://api.github.test/"; GraphQLUri=Uri "https://api.github.test/graphql"
          Token="token"; UserAgent="composer-test"; Owner="FS-GG"
          Repository="FS.GG.GitHub.Substrate.Sandbox"; ExpectedRepositoryId=42L }
      Project=
        { GraphQLUri=Uri "https://api.github.test/graphql"; Token="token"; UserAgent="composer-test"
          Organization="FS-GG"; ProjectNumber=2; ExpectedProjectNodeId="PVT_2" } }

let private incompletePublicRequest capture options =
    let candidate = String.replicate 40 "a"
    { Options=providerOptions; RosterOptions=options; RosterCapture=capture
      AcceptedEvidence=
        { Gs2083ReceiptBytes=ReadOnlyMemory<byte>.Empty
          ReceiverSourceBindingBytes=ReadOnlyMemory<byte>.Empty
          ReceiverCensusBytes=ReadOnlyMemory<byte>.Empty
          ReceiverSourceManifestsGzipBytes=ReadOnlyMemory<byte>.Empty
          ReceiverSourceBlobsGzipBytes=ReadOnlyMemory<byte>.Empty }
      RunIdentity=
        { CandidateSha=candidate; WorkflowRunId=1L; WorkflowRunAttempt=1
          RunNonce=$"1-1-{candidate}"; CorpusSha256=String.replicate 64 "b" }
      CopyPlan=Unchecked.defaultof<_>; BlobBatches=[]; BlobArtifacts=[]
      BlobCoverage=Unchecked.defaultof<_>; PinCapture=Unchecked.defaultof<_> }

[<Fact>]
let ``public boundary reparses roster settings and raw pages before custody`` () =
    let captured = rosterCapture ()
    MigrationReceiverAuthorityComposer.compose (incompletePublicRequest captured rosterOptions)
    |> expectError "copy-plan:"

    let alteredPage = { captured.First.Pages.Head with RosterRawSha256=String.replicate 64 "0" }
    let alteredPass = { captured.First with Pages=alteredPage :: captured.First.Pages.Tail }
    let alteredCapture = { captured with First=alteredPass; Second=alteredPass }
    MigrationReceiverAuthorityComposer.compose (incompletePublicRequest alteredCapture rosterOptions)
    |> expectError "roster-page-digest"

    let incompleteSettings = { rosterOptions with RequiredPermissions=Map [ "contents", "read" ] }
    MigrationReceiverAuthorityComposer.compose (incompletePublicRequest captured incompleteSettings)
    |> expectError "roster-scoped-settings"
