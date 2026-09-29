namespace FS.GG.Coordination.Orchestration.Observer

open System
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.Core.Orchestration

type RetrievedLearningSource =
    {
        Source: LearningContextSource
        Content: byte array
    }

type LearningAssignmentPreparationRequest =
    {
        ProposalRequest: LearningProposalRequest
        AcceptedProposal: LearningProposal
        SourceState: ObserverState
        ExpectedContractId: string
        ExpectedContractRevision: string
        ExpectedAuthoritativeObligations: string list
        RetrievedSources: RetrievedLearningSource list
        AssignedAt: DateTimeOffset
    }

type PreparedLearningAssignment =
    {
        Disposition: LearningPlanningDisposition
        Proposal: LearningProposal
        ContextManifest: LearningContextManifest
        Assignment: LearningTreatmentAssignmentInput
        PreparedTreatment: PreparedLearningTreatment
    }

type LearningAssignmentPreparationRefusal =
    | ProposalRefused of LearningProposalRefusal
    | AcceptedProposalMismatch
    | InvalidPlanningDisposition
    | ContextManifestRequired of LearningPlanningDisposition
    | SourceObserverStateInvalid
    | CanonicalWorkItemMissing of persistenceId: string
    | CanonicalWorkItemAmbiguous of persistenceId: string
    | CanonicalWorkItemArchived of persistenceId: string
    | CanonicalLineageInvalid
    | ContractMismatch
    | AuthoritativeObligationMismatch
    | InvalidSourceDeclaration of repository: string * path: string
    | SourceRevisionMismatch of repository: string * path: string * revision: string
    | RetrievedSourceSetMismatch
    | RetrievedSourceContentMismatch of repository: string * path: string * revision: string
    | RetrievedSourceSizeMismatch of repository: string * path: string
    | RetrievedSourceTextInvalid of repository: string * path: string
    | RenderedInputOversized of bytes: int

[<RequireQualifiedAccess>]
module LearningAssignmentAdapter =
    [<Literal>]
    let ContractVersion = "learn-01-assignment-preparation/1"

    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private sourceKey (source: LearningContextSource) =
        source.Repository, source.Path, source.Revision, source.Sha256.ToLowerInvariant()

    let private canonicalRepositoryFile (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value = value.Trim()
        && value.Length <= 512
        && not (value.StartsWith('/') || value.EndsWith('/'))
        && not (value.Contains("//") || value.Contains('\\'))
        && value.Split('/') |> Array.forall (fun segment -> segment <> "" && segment <> "." && segment <> "..")

    let private validRepository (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value = value.Trim()
        && value.Length <= 256
        && (value.Split('/')
            |> function
                | [| owner; repository |] ->
                    canonicalRepositoryFile owner
                    && canonicalRepositoryFile repository
                    && not (owner.Contains('/'))
                    && not (repository.Contains('/'))
                | _ -> false)

    let private validObservation (state: ObserverState) =
        match state.SessionId, state.Observation with
        | Some sessionId, Some observation when
            Observer.observationSha256 observation = observation.ObservationSha256
            && (ObserverJournal.observerId sessionId |> String.IsNullOrWhiteSpace |> not)
            ->
            Some observation
        | _ -> None

    let tryPlanningDisposition (proposal: LearningProposal) =
        match proposal.Action, proposal.Planner, proposal.DirectSmallEligible, proposal.ContextManifest with
        | Keep, None, false, Some _ -> Ok LearningPlanningDisposition.ReuseValidPlan
        | _, None, true, _ -> Ok LearningPlanningDisposition.DirectSmall
        | _, Some planner, false, _ when
            planner.Model = LearningProposal.PlannerModel
            && planner.Effort = LearningProposal.PlannerEffort
            ->
            Ok LearningPlanningDisposition.Planned
        | _ -> Error InvalidPlanningDisposition

    let private validateCanonicalIdentity observation (manifest: LearningContextManifest) =
        let find persistenceId =
            observation.WorkItems
            |> List.filter (fun item -> WorkItemIdentity.persistenceId item.Identity = persistenceId)
            |> function
                | [] -> Error(CanonicalWorkItemMissing persistenceId)
                | [ item ] when item.Archived -> Error(CanonicalWorkItemArchived persistenceId)
                | [ item ] -> Ok item
                | _ -> Error(CanonicalWorkItemAmbiguous persistenceId)

        find manifest.OriginalItemId
        |> Result.bind (fun _ ->
            find manifest.ItemId
            |> Result.bind (fun _ ->
                match manifest.Relation with
                | Original when manifest.ItemId = manifest.OriginalItemId -> Ok()
                | Descendant parent
                | Retry parent when parent <> manifest.ItemId -> find parent |> Result.map ignore
                | _ -> Error CanonicalLineageInvalid))

    let private validateSources sourceRevision (manifest: LearningContextManifest) retrieved =
        let required =
            manifest.PlanSource
            :: ((manifest.MandatoryReferences @ manifest.OptionalReferences) |> List.map _.Source)
            |> List.distinctBy sourceKey
            |> List.map sourceKey
            |> Set.ofList

        let supplied = retrieved |> List.map (fun value -> sourceKey value.Source)

        let invalidDeclaration =
            retrieved
            |> List.tryFind (fun value ->
                not (validRepository value.Source.Repository)
                || not (canonicalRepositoryFile value.Source.Path))

        let staleRevision =
            retrieved
            |> List.tryFind (fun value -> value.Source.Revision <> sourceRevision)

        match invalidDeclaration, staleRevision with
        | Some value, _ -> Error(InvalidSourceDeclaration(value.Source.Repository, value.Source.Path))
        | _, Some value ->
            Error(SourceRevisionMismatch(value.Source.Repository, value.Source.Path, value.Source.Revision))
        | _ when supplied.Length <> (supplied |> List.distinct |> List.length) || (supplied |> Set.ofList) <> required ->
            Error RetrievedSourceSetMismatch
        | _ ->
            retrieved
            |> List.tryPick (fun value ->
                let actual = sha256 value.Content

                if String.Equals(actual, value.Source.Sha256, StringComparison.OrdinalIgnoreCase) then
                    None
                else
                    Some(
                        RetrievedSourceContentMismatch(
                            value.Source.Repository,
                            value.Source.Path,
                            value.Source.Revision
                        )
                    ))
            |> function
                | Some refusal -> Error refusal
                | None -> Ok()

    let private renderInput (manifest: LearningContextManifest) (retrieved: RetrievedLearningSource list) =
        let estimated =
            manifest.MandatoryReferences @ manifest.OptionalReferences
            |> List.map (fun reference -> sourceKey reference.Source, reference.EstimatedBytes)
            |> Map.ofList

        let sizeMismatch =
            retrieved
            |> List.tryFind (fun value ->
                estimated
                |> Map.tryFind (sourceKey value.Source)
                |> Option.exists (fun expected -> expected <> value.Content.LongLength))

        match sizeMismatch with
        | Some value -> Error(RetrievedSourceSizeMismatch(value.Source.Repository, value.Source.Path))
        | None ->
            try
                let strictUtf8 = UTF8Encoding(false, true)
                let builder = StringBuilder()
                builder.AppendLine("fsgg.learning.rendered-input/1") |> ignore
                builder.AppendLine($"item={manifest.ItemId}") |> ignore
                builder.AppendLine($"original={manifest.OriginalItemId}") |> ignore
                builder.AppendLine($"manifest={manifest.CanonicalSha256}") |> ignore

                retrieved
                |> List.sortBy (fun value -> sourceKey value.Source)
                |> List.iter (fun value ->
                    builder.AppendLine("--- source ---") |> ignore
                    builder.AppendLine($"repository={value.Source.Repository}") |> ignore
                    builder.AppendLine($"path={value.Source.Path}") |> ignore
                    builder.AppendLine($"revision={value.Source.Revision}") |> ignore
                    builder.AppendLine($"sha256={value.Source.Sha256.ToLowerInvariant()}") |> ignore
                    builder.AppendLine(strictUtf8.GetString value.Content) |> ignore)

                let bytes = strictUtf8.GetBytes(builder.ToString())
                if bytes.Length > 1024 * 1024 then Error(RenderedInputOversized bytes.Length) else Ok bytes
            with :? DecoderFallbackException ->
                let invalid =
                    retrieved
                    |> List.find (fun value ->
                        try
                            UTF8Encoding(false, true).GetString value.Content |> ignore
                            false
                        with :? DecoderFallbackException -> true)
                Error(RetrievedSourceTextInvalid(invalid.Source.Repository, invalid.Source.Path))

    let prepare (request: LearningAssignmentPreparationRequest) =
        LearningProposal.propose request.ProposalRequest
        |> Result.mapError ProposalRefused
        |> Result.bind (fun produced ->
            if produced <> request.AcceptedProposal then
                Error AcceptedProposalMismatch
            elif
                not produced.ShadowOnly
                || produced.CanDispatch
                || produced.CanWrite
                || produced.IsAssignmentFact
            then
                Error AcceptedProposalMismatch
            else
                tryPlanningDisposition produced
                |> Result.bind (fun disposition ->
                    match produced.ContextManifest with
                    | None -> Error(ContextManifestRequired disposition)
                    | Some manifest ->
                        match validObservation request.SourceState with
                        | None -> Error SourceObserverStateInvalid
                        | Some observation when
                            manifest.ItemId <> produced.ItemId
                            || manifest.OriginalItemId <> produced.OriginalItemId
                            ->
                            Error CanonicalLineageInvalid
                        | Some observation ->
                            validateCanonicalIdentity observation manifest
                            |> Result.bind (fun () ->
                                if
                                    request.ExpectedContractId <> LearningContext.ContractId
                                    || manifest.ContractRevision <> request.ExpectedContractRevision
                                then
                                    Error ContractMismatch
                                elif
                                    (manifest.AuthoritativeObligations |> List.sort)
                                    <> (request.ExpectedAuthoritativeObligations |> List.sort)
                                then
                                    Error AuthoritativeObligationMismatch
                                else
                                    validateSources observation.SourceRevision manifest request.RetrievedSources
                                    |> Result.bind (fun () -> renderInput manifest request.RetrievedSources))
                            |> Result.map (fun renderedInput ->
                                let sourceObserverId =
                                    ObserverJournal.observerId request.SourceState.SessionId.Value

                                let assignment =
                                    {
                                        SourceObserverId = sourceObserverId
                                        SourceSequence = request.SourceState.Sequence
                                        SourceObservationSha256 = observation.ObservationSha256
                                        ItemId = produced.ItemId
                                        OriginalItemId = produced.OriginalItemId
                                        Relation = manifest.Relation
                                        Arm = manifest.Arm
                                        ProposalSha256 = produced.CanonicalSha256
                                        ContextManifestSha256 = manifest.CanonicalSha256
                                        Planner = produced.Planner
                                        Worker = produced.Worker
                                        DirectSmallEligible = produced.DirectSmallEligible
                                        ExpectedWorkflowRevision = observation.WorkflowRevision
                                        ExpectedGeneration = observation.Generation
                                        AssignedAt = request.AssignedAt
                                    }

                                {
                                    Disposition = disposition
                                    Proposal = produced
                                    ContextManifest = manifest
                                    Assignment = assignment
                                    PreparedTreatment =
                                        PreparedLearningTreatment(
                                            ContractVersion,
                                            disposition,
                                            assignment,
                                            renderedInput,
                                            sha256 renderedInput,
                                            manifest.Recipe.RecipeId,
                                            manifest.Recipe.ManifestVersion
                                        )
                                })))
