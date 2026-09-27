namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type MigrationProjectStatusContentKind =
    | Issue
    | PullRequest

type MigrationProjectStatusBinding =
    { ProjectNodeId: string
      RepositoryId: int64
      ItemNodeId: string
      ContentNodeId: string
      ContentKind: MigrationProjectStatusContentKind
      FieldNodeId: string
      ExpectedItemRevision: string
      FieldPayloadSha256: string
      StatusOptions: MigrationProjectFieldOption list
      DesiredOptionId: string }

[<RequireQualifiedAccess>]
module MigrationProjectStatusStepRuntime =
    let private text (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private isSha value =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let targetSha256 binding selectedOptionId =
        let kind =
            match binding.ContentKind with
            | MigrationProjectStatusContentKind.Issue -> "issue"
            | MigrationProjectStatusContentKind.PullRequest -> "pull-request"
        ([ "project-status/v1"; binding.ProjectNodeId; string binding.RepositoryId
           binding.ItemNodeId; kind; binding.ContentNodeId; binding.FieldNodeId
           binding.ExpectedItemRevision; binding.FieldPayloadSha256; binding.DesiredOptionId ]
         @ (binding.StatusOptions
            |> List.sortBy (fun option -> option.Id)
            |> List.collect (fun option -> [ option.Id; option.Name ]))
         @ (match selectedOptionId with
            | None -> [ "selected:none" ]
            | Some optionId -> [ "selected:some"; optionId ]))
        |> List.map frame
        |> String.concat ""
        |> sha

    type private ProviderReadTransport(transport: IMigrationStepProviderTransport) =
        interface IMigrationGitHubReadTransport with
            member _.Send request = transport.Send request

    type private BoundObservation =
        { Revision: string
          SelectedOptionId: string option
          Digest: string
          SnapshotSha256: string }

    let private liveId value =
        LiveId.tryCreate value |> Result.mapError (fun _ -> "provider-invalid-live-id")

    let private semanticName value =
        SemanticName.tryCreate value |> Result.mapError (fun _ -> "provider-invalid-semantic-name")

    let private selectedOptionId (field: MigrationProjectFieldValueRecord option) =
        match field with
        | None -> Ok None
        | Some value when value.ValueKind <> "ProjectV2ItemFieldSingleSelectValue" ->
            Error "provider-status-value-kind-drift"
        | Some value ->
            try
                use document = JsonDocument.Parse value.PayloadJson
                let mutable option = Unchecked.defaultof<JsonElement>
                if document.RootElement.ValueKind = JsonValueKind.Object
                   && document.RootElement.TryGetProperty("optionId", &option)
                   && option.ValueKind = JsonValueKind.String
                   && text (option.GetString()) then
                    Ok(Some(option.GetString()))
                else Error "provider-invalid-status-option-id"
            with :? JsonException -> Error "provider-malformed-status-value"

    let private contentIdentity = function
        | MigrationProjectContent.Issue(nodeId, repositoryId, _) ->
            MigrationProjectStatusContentKind.Issue, nodeId, repositoryId
        | MigrationProjectContent.PullRequest(nodeId, repositoryId, _) ->
            MigrationProjectStatusContentKind.PullRequest, nodeId, repositoryId
        | MigrationProjectContent.DraftIssue nodeId ->
            MigrationProjectStatusContentKind.Issue, nodeId, -1L

    let private projectContent expectedRepositoryId expectedRepository = function
        | MigrationProjectContent.Issue(nodeId, repositoryId, number) ->
            liveId nodeId
            |> Result.map (fun id ->
                let repository =
                    if repositoryId = expectedRepositoryId then expectedRepository
                    else { Owner="foreign"; Name=string repositoryId }
                RepositoryIssue(repository, number, id))
        | MigrationProjectContent.PullRequest(nodeId, repositoryId, number) ->
            liveId nodeId
            |> Result.map (fun id ->
                let repository =
                    if repositoryId = expectedRepositoryId then expectedRepository
                    else { Owner="foreign"; Name=string repositoryId }
                ProjectContent.PullRequest(repository, number, id))
        | MigrationProjectContent.DraftIssue nodeId -> liveId nodeId |> Result.map DraftIssue

    let private verifyWithAdapters binding (snapshot: MigrationProjectSnapshot) =
        let expectedRepository = { Owner="repository"; Name=string binding.RepositoryId }
        match liveId binding.ProjectNodeId, liveId binding.ItemNodeId, liveId binding.ContentNodeId,
              liveId binding.FieldNodeId with
        | Ok projectId, Ok itemId, Ok contentId, Ok fieldId ->
            let converted =
                snapshot.Items.Items
                |> List.map (fun item ->
                    projectContent binding.RepositoryId expectedRepository item.Content
                    |> Result.map (fun content ->
                        { ProjectId=projectId
                          ItemId=LiveId.tryCreate item.ItemNodeId |> Result.defaultWith invalidOp
                          Content=content
                          Archived=item.Archived }))
            match converted |> List.tryPick (function Error reason -> Some reason | _ -> None) with
            | Some reason -> Error reason
            | None ->
                let items = converted |> List.choose (function Ok item -> Some item | _ -> None)
                let observation = ProjectComplete(snapshot.NormalizedSha256, [ { Number=1; Items=items; TerminalPage=true } ])
                match ProjectAdapter.readProject observation with
                | Error _ -> Error "provider-project-membership-refused"
                | Ok project ->
                    match ProjectAdapter.resolveMembership expectedRepository contentId project with
                    | Ok(ActiveMembership item) when item.ItemId = itemId -> Ok()
                    | _ -> Error "provider-project-membership-ineligible"
        | _ -> Error "invalid-project-status-binding"

    let private bindObservation binding (snapshot: MigrationProjectSnapshot) =
        match verifyWithAdapters binding snapshot with
        | Error reason -> Error reason
        | Ok () ->
            let matchingItems = snapshot.Items.Items |> List.filter (fun item -> item.ItemNodeId = binding.ItemNodeId)
            let matchingFields = snapshot.Fields.Fields |> List.filter (fun field -> field.FieldNodeId = binding.FieldNodeId)
            let matchingValues = snapshot.Values.Items |> List.filter (fun item -> item.ItemNodeId = binding.ItemNodeId)
            match matchingItems, matchingFields, matchingValues with
            | [ item ], [ field ], [ values ] ->
                let observedKind, contentNodeId, repositoryId = contentIdentity item.Content
                let schemaMatches =
                    snapshot.ProjectNodeId = binding.ProjectNodeId
                    && observedKind = binding.ContentKind
                    && contentNodeId = binding.ContentNodeId
                    && repositoryId = binding.RepositoryId
                    && not item.Archived
                    && field.Name = "Status"
                    && field.DataType = "SINGLE_SELECT"
                    && field.Kind = MigrationProjectFieldKind.SingleSelect
                    && field.PayloadSha256 = binding.FieldPayloadSha256
                    && field.Options = binding.StatusOptions
                    && field.Options |> List.exists (fun option -> option.Id = binding.DesiredOptionId)
                if not schemaMatches then Error "provider-project-status-binding-drift"
                else
                    let statusValue = values.FieldValues |> List.tryFind (fun value -> value.FieldNodeId = binding.FieldNodeId)
                    selectedOptionId statusValue
                    |> Result.bind (fun selected ->
                        match liveId binding.ProjectNodeId, liveId binding.ItemNodeId, liveId binding.FieldNodeId,
                              semanticName field.Name with
                        | Ok projectId, Ok itemId, Ok fieldId, Ok fieldName ->
                            let convertedOptions =
                                field.Options
                                |> List.map (fun option ->
                                    match liveId option.Id, semanticName option.Name with
                                    | Ok id, Ok name -> Ok({ Id=id; Name=name }: StatusOptionProjection)
                                    | _ -> Error "provider-invalid-status-option")
                            match convertedOptions |> List.tryPick (function Error reason -> Some reason | _ -> None) with
                            | Some reason -> Error reason
                            | None ->
                                let options = convertedOptions |> List.choose (function Ok value -> Some value | _ -> None)
                                let selectedLive =
                                    match selected with
                                    | None -> Ok None
                                    | Some value -> liveId value |> Result.map Some
                                selectedLive
                                |> Result.bind (fun selectedId ->
                                    let projection =
                                        { ProjectId=projectId; ItemId=itemId; FieldId=fieldId; FieldName=fieldName
                                          Options=options; SelectedOptionId=selectedId }
                                    let revision = item.UpdatedAt.ToUniversalTime().ToString("O")
                                    let observation =
                                        StatusComplete(revision, { PageCount=1; NodeCount=1; TerminalPage=true }, [ projection ])
                                    match ProjectAdapter.readStatus projectId itemId observation with
                                    | Error _ -> Error "provider-status-projection-refused"
                                    | Ok _ ->
                                        Ok { Revision=revision; SelectedOptionId=selected
                                             Digest=targetSha256 binding selected
                                             SnapshotSha256=snapshot.NormalizedSha256 })
                        | _ -> Error "invalid-project-status-binding")
            | _ -> Error "provider-project-status-target-missing-or-duplicated"

    let private observeOnce binding readOptions transport =
        let reader = ProviderReadTransport transport :> IMigrationGitHubReadTransport
        match MigrationGitHubRead.readProjectItems readOptions reader,
              MigrationGitHubRead.readProjectFields readOptions reader,
              MigrationGitHubRead.readProjectValues readOptions reader with
        | Ok items, Ok fields, Ok values ->
            MigrationGitHubRead.reconcileProject items fields values
            |> Result.mapError (fun failure -> $"provider-project-reconcile:{failure}")
            |> Result.bind (bindObservation binding)
        | Error failure, _, _ | _, Error failure, _ | _, _, Error failure ->
            Error $"provider-project-read:{failure}"

    let private observe binding readOptions transport =
        match observeOnce binding readOptions transport, observeOnce binding readOptions transport with
        | Ok first, Ok second when first = second -> Ok first
        | Ok _, Ok _ -> Error "provider-project-observation-drift"
        | Error reason, _ | _, Error reason -> Error reason

    let private mutation =
        "mutation($projectId:ID!,$itemId:ID!,$fieldId:ID!,$optionId:String!,$clientMutationId:String!) { updateProjectV2ItemFieldValue(input:{projectId:$projectId,itemId:$itemId,fieldId:$fieldId,value:{singleSelectOptionId:$optionId},clientMutationId:$clientMutationId}) { clientMutationId projectV2Item { id } } }"

    let private dispatch (step: MigrationExecutionStep) binding
                         (readOptions: MigrationProjectReadOptions)
                         (options: MigrationStepProviderOptions)
                         (transport: IMigrationStepProviderTransport) =
        let request =
            GraphQL
                { Uri=options.GraphQLUri; Document=mutation
                  Variables=Map.ofList [ "projectId", binding.ProjectNodeId; "itemId", binding.ItemNodeId
                                         "fieldId", binding.FieldNodeId; "optionId", binding.DesiredOptionId
                                         "clientMutationId", step.OperationId ]
                  Headers=options.Headers; ApiVersion=ApiVersion.required; Idempotency=NeverReplay }
        transport.Send request |> ignore
        // A mutation response is never settlement evidence. Only a new, complete,
        // two-pass Project observation may report the effect as applied.
        match observe binding readOptions transport with
        | Ok observed when observed.Revision <> binding.ExpectedItemRevision
                           && observed.SelectedOptionId = Some binding.DesiredOptionId
                           && observed.Digest = step.DesiredTargetSha256 -> MigrationDispatchOutcome.Applied
        | _ -> MigrationDispatchOutcome.Unknown

    let private validBinding binding =
        binding.RepositoryId > 0L
        && ([ binding.ProjectNodeId; binding.ItemNodeId; binding.ContentNodeId; binding.FieldNodeId
              binding.ExpectedItemRevision; binding.FieldPayloadSha256; binding.DesiredOptionId ]
            |> List.forall text)
        && isSha binding.FieldPayloadSha256
        && not (obj.ReferenceEquals(binding.StatusOptions, null))
        && not binding.StatusOptions.IsEmpty
        && binding.StatusOptions |> List.forall (fun option -> text option.Id && text option.Name)
        && (binding.StatusOptions |> List.map _.Id |> Set.ofList |> Set.count) = binding.StatusOptions.Length
        && (binding.StatusOptions |> List.map _.Name |> Set.ofList |> Set.count) = binding.StatusOptions.Length
        && binding.StatusOptions |> List.exists (fun option -> option.Id = binding.DesiredOptionId)

    let create (step: MigrationExecutionStep) binding
               (readOptions: MigrationProjectReadOptions)
               (providerOptions: MigrationStepProviderOptions)
               authority (transport: IMigrationStepProviderTransport) =
        match MigrationStepExecution.sealStep step, step.Effect with
        | Ok sealedStep, MigrationEffect.SetProjectField(projectId, itemId, fieldId, optionId)
            when sealedStep.Seal = step.Seal
                 && validBinding binding
                 && projectId = binding.ProjectNodeId
                 && itemId = binding.ItemNodeId
                 && fieldId = binding.FieldNodeId
                 && optionId = binding.DesiredOptionId
                 && step.ExpectedTargetRevision = binding.ExpectedItemRevision
                 && step.DesiredTargetSha256 = targetSha256 binding (Some binding.DesiredOptionId)
                 && readOptions.ExpectedProjectNodeId = binding.ProjectNodeId
                 && readOptions.GraphQLUri = providerOptions.GraphQLUri
                 && not (isNull providerOptions.GraphQLUri)
                 && providerOptions.GraphQLUri.IsAbsoluteUri
                 && providerOptions.GraphQLUri.Scheme = Uri.UriSchemeHttps ->
            { new IMigrationStepRuntime with
                member _.ObserveEpoch() = authority.ObserveEpoch()
                member _.ObserveAuthorityFence() = authority.ObserveAuthorityFence()
                member _.ObserveJournal operationId = authority.ObserveJournal operationId
                member _.PersistIntent(generation, head, operationId, seal) =
                    authority.PersistIntent generation head operationId seal
                member _.MarkInFlight(generation, head, operationId) =
                    authority.MarkInFlight generation head operationId
                member _.PersistSettlement(generation, head, operationId, result) =
                    authority.PersistSettlement generation head operationId result
                member _.ObserveTarget effect =
                    if effect <> step.Effect then Error "unsupported-or-cross-step-effect"
                    else
                        observe binding readOptions transport
                        |> Result.map (fun observed ->
                            { Identity=step.TargetIdentity; Revision=observed.Revision; Sha256=observed.Digest
                              Complete=true; Authorized=true })
                member _.ObserveEffect(operationId, effect) =
                    if operationId <> step.OperationId || effect <> step.Effect then
                        Error "unsupported-or-cross-step-effect"
                    else
                        match observe binding readOptions transport with
                        | Error _ -> Ok MigrationEffectObservation.Unknown
                        | Ok observed ->
                            if observed.Revision <> binding.ExpectedItemRevision
                               && observed.SelectedOptionId = Some binding.DesiredOptionId
                               && observed.Digest = step.DesiredTargetSha256 then
                                Ok(MigrationEffectObservation.Applied step.DesiredTargetSha256)
                            elif observed.Revision = binding.ExpectedItemRevision
                                 && observed.Digest = step.ExpectedTargetSha256 then
                                Ok MigrationEffectObservation.ProvenAbsent
                            else Ok(MigrationEffectObservation.Partial "provider-state-neither-exact-expected-nor-desired")
                member _.Dispatch(candidate, generation, commit) =
                    if candidate <> step then MigrationDispatchOutcome.Refused "cross-step-dispatch"
                    else
                        match authority.ObserveJournal step.OperationId with
                        | Ok(Some observed)
                            when observed.OperationId = step.OperationId
                                 && observed.StepSeal = step.Seal
                                 && observed.Stage = MigrationJournalStage.InFlight
                                 && observed.Generation = generation
                                 && observed.Commit = commit ->
                            dispatch step binding readOptions providerOptions transport
                        | _ -> MigrationDispatchOutcome.Refused "missing-fresh-in-flight-grant" }
            |> Ok
        | Ok _, MigrationEffect.SetProjectField _ -> Error "invalid-project-status-binding"
        | Ok _, _ -> Error "unsupported-migration-effect"
        | _ -> Error "invalid-migration-step"
