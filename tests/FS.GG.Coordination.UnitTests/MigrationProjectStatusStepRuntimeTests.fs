module FS.GG.Coordination.MigrationProjectStatusStepRuntimeTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.GitHub

let private sha (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let private commit value = String.replicate 40 value
let private expectedRevision = "2026-09-27T08:00:00.0000000+00:00"
let private desiredRevision = "2026-09-27T08:01:00.0000000+00:00"

let private fieldPayload =
    """{"__typename":"ProjectV2SingleSelectField","id":"FIELD_1","name":"Status","dataType":"SINGLE_SELECT","options":[{"id":"READY","name":"Ready"},{"id":"DONE","name":"Done"},{"id":"OTHER","name":"Other"},{"id":"none","name":"Literal none"}]}"""

let private binding desired =
    { ProjectNodeId="PROJECT_1"
      RepositoryId=42L
      ItemNodeId="ITEM_1"
      ContentNodeId="ISSUE_1"
      ContentKind=MigrationProjectStatusContentKind.Issue
      FieldNodeId="FIELD_1"
      ExpectedItemRevision=expectedRevision
      FieldPayloadSha256=sha fieldPayload
      StatusOptions=
        [ { Id="READY"; Name="Ready" }; { Id="DONE"; Name="Done" }; { Id="OTHER"; Name="Other" }
          { Id="none"; Name="Literal none" } ]
      DesiredOptionId=desired }

let private stepWithSelected current desired =
    let target = binding desired
    { OperationId="migration:project-status:1"
      IdempotencyKey="migration:manifest:project-status:1"
      ManifestSeal=sha "manifest"
      Effect=MigrationEffect.SetProjectField("PROJECT_1", "ITEM_1", "FIELD_1", desired)
      TargetIdentity="project:PROJECT_1/item:ITEM_1/field:FIELD_1"
      ExpectedTargetRevision=expectedRevision
      ExpectedTargetSha256=MigrationProjectStatusStepRuntime.targetSha256 target current
      DesiredTargetSha256=MigrationProjectStatusStepRuntime.targetSha256 target (Some desired)
      EpochGeneration=7L
      EpochCommit=commit "a"
      AuthorityFence=
        { AdmissionGeneration=3L; AdmissionCommit=commit "b"
          OperationGeneration=4L; OperationCommit=commit "c"; Claim=None
          SealCommit=commit "d"; RegistryCommit=commit "e" }
      JournalGeneration=11L
      JournalHead=commit "f"
      Seal="" }
    |> MigrationStepExecution.sealStep
    |> function Ok value -> value | Error failure -> failwithf "invalid test step: %A" failure

let private step current desired = stepWithSelected (Some current) desired

type private Authority(selected: MigrationExecutionStep) =
    let mutable journal: MigrationJournalAuthority option = None

    member _.Journal = journal

    member _.Port =
        { ObserveEpoch=(fun () ->
              Ok { Phase="SwitchedV2"; ManifestSeal=selected.ManifestSeal
                   Generation=selected.EpochGeneration; Commit=selected.EpochCommit
                   Complete=true; Authorized=true })
          ObserveAuthorityFence=(fun () -> Ok { Fence=selected.AuthorityFence; Complete=true; Authorized=true })
          ObserveJournal=(fun _ -> Ok journal)
          PersistIntent=(fun generation head operation seal ->
              if journal.IsSome || generation <> selected.JournalGeneration || head <> selected.JournalHead then
                  MigrationCasOutcome.Conflict
              else
                  let next =
                      { OperationId=operation; StepSeal=seal; Generation=generation + 1L; Commit=commit "1"
                        Stage=MigrationJournalStage.IntentPersisted; ResultSha256=None }
                  journal <- Some next
                  MigrationCasOutcome.Accepted next)
          MarkInFlight=(fun generation head operation ->
              match journal with
              | Some current when current.Generation = generation && current.Commit = head
                                  && current.OperationId = operation
                                  && current.Stage = MigrationJournalStage.IntentPersisted ->
                  let next =
                      { current with Generation=generation + 1L; Commit=commit "2"
                                     Stage=MigrationJournalStage.InFlight }
                  journal <- Some next
                  MigrationCasOutcome.Accepted next
              | _ -> MigrationCasOutcome.Conflict)
          PersistSettlement=(fun generation head operation result ->
              match journal with
              | Some current when current.Generation = generation && current.Commit = head
                                  && current.OperationId = operation
                                  && current.Stage = MigrationJournalStage.InFlight ->
                  let next =
                      { current with Generation=generation + 1L; Commit=commit "3"
                                     Stage=MigrationJournalStage.Settled; ResultSha256=Some result }
                  journal <- Some next
                  MigrationCasOutcome.Accepted next
              | _ -> MigrationCasOutcome.Conflict) }

type private StatusProvider(initial: string, ?applyMutation: bool, ?lostResponse: bool,
                            ?archived: bool, ?repositoryId: int64, ?draft: bool,
                            ?numericOptionId: bool, ?driftAfterFirstObservation: bool,
                            ?readUnavailableAfterMutation: bool,
                            ?unrelatedDriftAfterFirstObservation: bool,
                            ?unrelatedItemDriftAfterFirstObservation: bool) =
    let mutable selected = initial
    let mutable revision = expectedRevision
    let mutable mutations = 0
    let applyMutation = defaultArg applyMutation true
    let lostResponse = defaultArg lostResponse false
    let archived = defaultArg archived false
    let repositoryId = defaultArg repositoryId 42L
    let draft = defaultArg draft false
    let numericOptionId = defaultArg numericOptionId false
    let driftAfterFirstObservation = defaultArg driftAfterFirstObservation false
    let readUnavailableAfterMutation = defaultArg readUnavailableAfterMutation false
    let unrelatedDriftAfterFirstObservation = defaultArg unrelatedDriftAfterFirstObservation false
    let unrelatedItemDriftAfterFirstObservation = defaultArg unrelatedItemDriftAfterFirstObservation false
    let mutable valueReads = 0
    let mutable requestCount = 0
    let mutable mutationAttempted = false
    let mutable unrelatedFieldName = "Title"
    let mutable unrelatedItemRevision = "2026-09-27T07:00:00.0000000+00:00"

    member _.MutationCount = mutations
    member _.RequestCount = requestCount
    member _.Selected = selected

    interface IMigrationStepProviderTransport with
        member _.Send request =
            requestCount <- requestCount + 1
            let ok body =
                Response
                    { StatusCode=200; Headers=Map.empty; Body=body; ETag=None
                      RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
            match request with
            | GraphQL graph when graph.Document = MigrationGitHubRead.projectItemsQuery 1 ->
                Assert.Equal(ReplaySafe, graph.Idempotency)
                if readUnavailableAfterMutation && mutationAttempted then TimedOut
                else
                    let content =
                        if draft then """{"__typename":"DraftIssue","id":"ISSUE_1"}"""
                        else $"""{{"__typename":"Issue","id":"ISSUE_1","number":7,"repository":{{"databaseId":{repositoryId}}}}}"""
                    let item =
                        $"""{{"id":"ITEM_1","isArchived":{archived.ToString().ToLowerInvariant()},"updatedAt":"{revision}","content":{content}}}"""
                    let items, total =
                        if unrelatedItemDriftAfterFirstObservation then
                            let unrelated =
                                $"""{{"id":"ITEM_OTHER","isArchived":false,"updatedAt":"{unrelatedItemRevision}","content":{{"__typename":"Issue","id":"ISSUE_OTHER","number":8,"repository":{{"databaseId":42}}}}}}"""
                            $"{item},{unrelated}", 2
                        else item, 1
                    ok $"""{{"data":{{"organization":{{"projectV2":{{"id":"PROJECT_1","number":1,"items":{{"totalCount":{total},"nodes":[{items}],"pageInfo":{{"hasNextPage":false,"endCursor":null}}}}}}}}}}}}"""
            | GraphQL graph when graph.Document = MigrationGitHubRead.projectFieldsQuery 1 ->
                Assert.Equal(ReplaySafe, graph.Idempotency)
                if readUnavailableAfterMutation && mutationAttempted then TimedOut
                else
                    let fields, total =
                        if unrelatedDriftAfterFirstObservation then
                            let unrelated =
                                $"""{{"__typename":"ProjectV2Field","id":"FIELD_OTHER","name":"{unrelatedFieldName}","dataType":"TITLE"}}"""
                            $"{fieldPayload},{unrelated}", 2
                        else fieldPayload, 1
                    ok $"""{{"data":{{"organization":{{"projectV2":{{"id":"PROJECT_1","number":1,"fields":{{"totalCount":{total},"nodes":[{fields}],"pageInfo":{{"hasNextPage":false,"endCursor":null}}}}}}}}}}}}"""
            | GraphQL graph when graph.Document = MigrationGitHubRead.projectValuesQuery 1 ->
                Assert.Equal(ReplaySafe, graph.Idempotency)
                if readUnavailableAfterMutation && mutationAttempted then TimedOut
                else
                    valueReads <- valueReads + 1
                    let optionId = if numericOptionId then "17" else $"\"{selected}\""
                    // VALUE_OBJECT_9 is intentionally not a selectable option id.
                    let value =
                        $"""{{"__typename":"ProjectV2ItemFieldSingleSelectValue","id":"VALUE_OBJECT_9","updatedAt":"{revision}","field":{{"id":"FIELD_1"}},"optionId":{optionId},"name":"selected"}}"""
                    let item =
                        $"""{{"id":"ITEM_1","updatedAt":"{revision}","fieldValues":{{"totalCount":1,"nodes":[{value}],"pageInfo":{{"hasNextPage":false,"endCursor":null}}}}}}"""
                    let items, total =
                        if unrelatedItemDriftAfterFirstObservation then
                            let unrelated =
                                $"""{{"id":"ITEM_OTHER","updatedAt":"{unrelatedItemRevision}","fieldValues":{{"totalCount":0,"nodes":[],"pageInfo":{{"hasNextPage":false,"endCursor":null}}}}}}"""
                            $"{item},{unrelated}", 2
                        else item, 1
                    let response = ok $"""{{"data":{{"organization":{{"projectV2":{{"id":"PROJECT_1","number":1,"items":{{"totalCount":{total},"nodes":[{items}],"pageInfo":{{"hasNextPage":false,"endCursor":null}}}}}}}}}}}}"""
                    if driftAfterFirstObservation && valueReads = 1 then
                        selected <- "OTHER"
                        revision <- desiredRevision
                    if unrelatedDriftAfterFirstObservation && valueReads = 1 then
                        unrelatedFieldName <- "Renamed title"
                    if unrelatedItemDriftAfterFirstObservation && valueReads = 1 then
                        unrelatedItemRevision <- "2026-09-27T07:01:00.0000000+00:00"
                    response
            | GraphQL graph when graph.Document.StartsWith("mutation", StringComparison.Ordinal) ->
                Assert.Contains("updateProjectV2ItemFieldValue", graph.Document)
                Assert.Equal(NeverReplay, graph.Idempotency)
                Assert.Equal(Some "PROJECT_1", Map.tryFind "projectId" graph.Variables)
                Assert.Equal(Some "ITEM_1", Map.tryFind "itemId" graph.Variables)
                Assert.Equal(Some "FIELD_1", Map.tryFind "fieldId" graph.Variables)
                Assert.Equal(Some "DONE", Map.tryFind "optionId" graph.Variables)
                Assert.NotEqual(Some "VALUE_OBJECT_9", Map.tryFind "optionId" graph.Variables)
                mutations <- mutations + 1
                mutationAttempted <- true
                if applyMutation then
                    selected <- "DONE"
                    revision <- desiredRevision
                if lostResponse then TimedOut
                else ok """{"data":{"updateProjectV2ItemFieldValue":{"clientMutationId":"untrusted-response-only","projectV2Item":{"id":"ITEM_1"}}}}"""
            | _ -> failwithf "unexpected provider request: %A" request

let private readOptions =
    { GraphQLUri=Uri "https://api.github.test/graphql"; Token="controlled"; UserAgent="tests"
      Organization="FS-GG"; ProjectNumber=1; ExpectedProjectNodeId="PROJECT_1" }

let private providerOptions =
    { GraphQLUri=readOptions.GraphQLUri; Headers=Map.ofList [ "authorization", "Bearer controlled" ] }

let private runtime selected authority provider targetBinding =
    MigrationProjectStatusStepRuntime.create
        selected targetBinding readOptions providerOptions authority provider
    |> function Ok value -> value | Error reason -> failwith reason

let private advance selected cut runtime =
    MigrationStepExecution.advance selected cut runtime
    |> function Ok value -> value | Error failure -> failwithf "unexpected refusal: %A" failure

[<Fact>]
let ``Status option id is dispatched and settles only from two complete fresh observations`` () =
    let selected = step "READY" "DONE"
    let authority = Authority selected
    let provider = StatusProvider "READY"
    let adapter = runtime selected authority.Port provider (binding "DONE")

    Assert.Equal(MigrationAdvanceResult.Settled selected.DesiredTargetSha256,
                 advance selected MigrationAdvanceCut.NoCut adapter)
    Assert.Equal("DONE", provider.Selected)
    Assert.Equal(1, provider.MutationCount)
    Assert.Equal(43, provider.RequestCount)
    Assert.Equal(MigrationAdvanceResult.AlreadySettled selected.DesiredTargetSha256,
                 advance selected MigrationAdvanceCut.NoCut adapter)
    Assert.Equal(1, provider.MutationCount)
    Assert.Equal(55, provider.RequestCount)

[<Fact>]
let ``absent selection and literal none option have distinct target digests and prestate`` () =
    let target = binding "DONE"
    Assert.NotEqual(
        MigrationProjectStatusStepRuntime.targetSha256 target None,
        MigrationProjectStatusStepRuntime.targetSha256 target (Some "none"))

    let selected = stepWithSelected None "DONE"
    let authority = Authority selected
    let provider = StatusProvider "none"
    let adapter = runtime selected authority.Port provider target

    Assert.Equal(Error [ MigrationExecutionFailure.ChangedTarget ],
                 MigrationStepExecution.advance selected MigrationAdvanceCut.NoCut adapter)
    Assert.Equal(0, provider.MutationCount)

[<Fact>]
let ``lost mutation response and reconstructed runtime settle without a second send`` () =
    let selected = step "READY" "DONE"
    let authority = Authority selected
    let provider = StatusProvider("READY", lostResponse=true)
    let first = runtime selected authority.Port provider (binding "DONE")

    Assert.Equal(MigrationAdvanceResult.Interrupted "after-dispatch-before-response",
                 advance selected MigrationAdvanceCut.StopAfterDispatch first)
    Assert.Equal(1, provider.MutationCount)

    let reconstructed = runtime selected authority.Port provider (binding "DONE")
    Assert.Equal(MigrationAdvanceResult.Settled selected.DesiredTargetSha256,
                 advance selected MigrationAdvanceCut.NoCut reconstructed)
    Assert.Equal(1, provider.MutationCount)

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``unchanged state after an ambiguous or identity-only response stays in flight and never replays`` lostResponse =
    let selected = step "READY" "DONE"
    let authority = Authority selected
    let provider = StatusProvider("READY", applyMutation=false, lostResponse=lostResponse)
    let adapter = runtime selected authority.Port provider (binding "DONE")

    Assert.Equal(MigrationAdvanceResult.Pending "dispatch-outcome-unknown",
                 advance selected MigrationAdvanceCut.NoCut adapter)
    Assert.Equal(MigrationAdvanceResult.Pending "in-flight-absence-needs-exclusion",
                 advance selected MigrationAdvanceCut.NoCut
                     (runtime selected authority.Port provider (binding "DONE")))
    Assert.Equal(1, provider.MutationCount)

[<Fact>]
let ``unknown fresh readback remains pending and never authorizes a second send`` () =
    let selected = step "READY" "DONE"
    let authority = Authority selected
    let provider = StatusProvider("READY", readUnavailableAfterMutation=true)
    let adapter = runtime selected authority.Port provider (binding "DONE")

    Assert.Equal(MigrationAdvanceResult.Pending "dispatch-outcome-unknown",
                 advance selected MigrationAdvanceCut.NoCut adapter)
    Assert.Equal(MigrationAdvanceResult.Pending "effect-observation-unknown",
                 advance selected MigrationAdvanceCut.NoCut
                     (runtime selected authority.Port provider (binding "DONE")))
    Assert.Equal(1, provider.MutationCount)

[<Fact>]
let ``two complete observations must match before target knowledge is accepted`` () =
    let selected = step "READY" "DONE"
    let authority = Authority selected
    let provider = StatusProvider("READY", driftAfterFirstObservation=true)
    let adapter = runtime selected authority.Port provider (binding "DONE")

    match MigrationStepExecution.advance selected MigrationAdvanceCut.NoCut adapter with
    | Error [ MigrationExecutionFailure.JournalUnavailable "provider-project-observation-drift" ] -> ()
    | value -> failwithf "expected two-pass drift refusal, got %A" value
    Assert.Equal(0, provider.MutationCount)

[<Fact>]
let ``two-pass equality includes unrelated reconciled Project schema`` () =
    let selected = step "READY" "DONE"
    let authority = Authority selected
    let provider = StatusProvider("READY", unrelatedDriftAfterFirstObservation=true)
    let adapter = runtime selected authority.Port provider (binding "DONE")

    match MigrationStepExecution.advance selected MigrationAdvanceCut.NoCut adapter with
    | Error [ MigrationExecutionFailure.JournalUnavailable "provider-project-observation-drift" ] -> ()
    | value -> failwithf "expected full snapshot drift refusal, got %A" value
    Assert.Equal(0, provider.MutationCount)

[<Fact>]
let ``two-pass equality includes unrelated reconciled Project items`` () =
    let selected = step "READY" "DONE"
    let authority = Authority selected
    let provider = StatusProvider("READY", unrelatedItemDriftAfterFirstObservation=true)
    let adapter = runtime selected authority.Port provider (binding "DONE")

    match MigrationStepExecution.advance selected MigrationAdvanceCut.NoCut adapter with
    | Error [ MigrationExecutionFailure.JournalUnavailable "provider-project-observation-drift" ] -> ()
    | value -> failwithf "expected unrelated item drift refusal, got %A" value
    Assert.Equal(0, provider.MutationCount)

[<Fact>]
let ``dispatch requires the exact fresh in-flight journal authority`` () =
    let selected = step "READY" "DONE"
    let authority = Authority selected
    let provider = StatusProvider "READY"
    let adapter = runtime selected authority.Port provider (binding "DONE")

    Assert.Equal(MigrationDispatchOutcome.Refused "missing-fresh-in-flight-grant",
                 adapter.Dispatch(selected, selected.JournalGeneration + 2L, commit "2"))
    Assert.Equal(0, provider.MutationCount)

[<Theory>]
[<InlineData(true, 42L, false)>]
[<InlineData(false, 77L, false)>]
[<InlineData(false, 42L, true)>]
let ``archived foreign and draft memberships refuse before mutation`` archived repositoryId draft =
    let selected = step "READY" "DONE"
    let authority = Authority selected
    let provider = StatusProvider("READY", archived=archived, repositoryId=repositoryId, draft=draft)
    let adapter = runtime selected authority.Port provider (binding "DONE")

    match MigrationStepExecution.advance selected MigrationAdvanceCut.NoCut adapter with
    | Error [ MigrationExecutionFailure.JournalUnavailable _ ] -> ()
    | value -> failwithf "expected membership refusal, got %A" value
    Assert.Equal(0, provider.MutationCount)

[<Fact>]
let ``selected option must be a typed nonempty declared option`` () =
    let selected = step "READY" "DONE"
    let authority = Authority selected
    let provider = StatusProvider("READY", numericOptionId=true)
    let adapter = runtime selected authority.Port provider (binding "DONE")

    match MigrationStepExecution.advance selected MigrationAdvanceCut.NoCut adapter with
    | Error [ MigrationExecutionFailure.JournalUnavailable "provider-invalid-status-option-id" ] -> ()
    | value -> failwithf "expected option type refusal, got %A" value
    Assert.Equal(0, provider.MutationCount)

[<Fact>]
let ``contradictory stable Status prestate is target drift and does not send`` () =
    let selected = step "READY" "DONE"
    let authority = Authority selected
    let provider = StatusProvider "OTHER"
    let adapter = runtime selected authority.Port provider (binding "DONE")

    Assert.Equal(Error [ MigrationExecutionFailure.ChangedTarget ],
                 MigrationStepExecution.advance selected MigrationAdvanceCut.NoCut adapter)
    Assert.Equal(0, provider.MutationCount)

[<Fact>]
let ``unknown desired option and unsupported effect refuse at construction without reads`` () =
    let unknownBinding = binding "UNKNOWN"
    let baseline = step "READY" "DONE"
    let unknown =
        { baseline with
            Effect=MigrationEffect.SetProjectField("PROJECT_1", "ITEM_1", "FIELD_1", "UNKNOWN")
            DesiredTargetSha256=MigrationProjectStatusStepRuntime.targetSha256 unknownBinding (Some "UNKNOWN")
            Seal="" }
        |> MigrationStepExecution.sealStep
        |> function Ok value -> value | Error failure -> failwithf "%A" failure
    let authority = Authority unknown
    let provider = StatusProvider "READY"

    Assert.Equal(
        Error "invalid-project-status-binding",
        MigrationProjectStatusStepRuntime.create
            unknown unknownBinding readOptions providerOptions authority.Port provider)
    Assert.Equal(0, provider.MutationCount)
