namespace FS.GG.Coordination.Cli

open System
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type MigrationReceiverCohortOrchestrationRequest =
    {
        InstallationOptions: MigrationReceiverInstallationReadOptions
        ProviderOptions: MigrationInspectProviderOptions
        AcceptedEvidence: MigrationReceiverCopyAcceptedEvidence
        RunIdentity: MigrationSandboxSeedRequest
        CopyPlan: MigrationReceiverCopyPlanResult
        BlobBatches: MigrationReceiverCopyBlobBatch list
        BlobArtifacts: MigrationReceiverCopyBlobBatchArtifact list
    }

type MigrationReceiverCohortOrchestrationResult =
    {
        Installation: MigrationReceiverInstallationCapture
        PinCapture: MigrationReceiverPinTwoPass
        BlobCoverage: MigrationReceiverCopyBlobCoverage
        AuthorityComposition: MigrationReceiverAuthorityComposition
    }

[<RequireQualifiedAccess>]
module MigrationReceiverCohortOrchestration =
    let private receiverIds =
        set [ "sdd"; "rendering"; "governance"; "templates"; "game"; "audio"; "net" ]

    let private unavailable reason =
        Error $"receiver-cohort-orchestration-unavailable:{reason}"

    let private splitRepositoryName (value: string) =
        if isNull value then
            None
        else
            match value.Split('/') with
            | [| owner; repository |] when
                not (String.IsNullOrWhiteSpace owner)
                && not (String.IsNullOrWhiteSpace repository)
                ->
                Some(owner, repository)
            | _ -> None

    let internal validateBindingsForTests
        (installation: MigrationReceiverInstallationCapture)
        (options: MigrationInspectProviderOptions)
        (copyPlan: MigrationReceiverCopyPlanResult)
        =
        let cohort = options.Cohort
        let mappings = copyPlan.ReceiverCopyMappings
        let receivers = cohort.Receivers
        let repositories = cohort.Repositories
        let roster = installation.ComposerRosterCapture.First.Repositories
        let mappingNames = mappings |> List.map _.ReceiverCopyId
        let receiverNames = receivers |> List.map _.Receiver

        if not (GitHubMigrationInspect.validCohort cohort) then
            unavailable "invalid-cohort"
        elif
            installation.ComposerRosterCapture.First
            <> installation.ComposerRosterCapture.Second
        then
            unavailable "installation-two-pass-drift"
        elif repositories.Length <> 1 || roster.Length <> 1 then
            unavailable "one-sandbox-repository-required"
        elif
            mappings.Length <> 7
            || Set.ofList mappingNames <> receiverIds
            || mappingNames.Length <> (mappingNames |> Set.ofList |> Set.count)
        then
            unavailable "copy-plan-seven-receivers"
        elif
            receivers.Length <> 7
            || Set.ofList receiverNames <> receiverIds
            || receiverNames.Length <> (receiverNames |> Set.ofList |> Set.count)
        then
            unavailable "cohort-seven-receivers"
        else
            let repository = repositories.Head
            let rosterRepository = roster.Head
            let declaredRepositories = installation.ComposerRosterCapture.Second.Repositories
            let selectedRepositories = installation.ComposerRosterOptions

            let repositoryBinding =
                repository.Id = rosterRepository.RosterRepositoryId
                && repository.NodeId = rosterRepository.RosterRepositoryNodeId
                && String.Equals(
                    repository.FullName,
                    rosterRepository.RosterRepositoryFullName,
                    StringComparison.OrdinalIgnoreCase
                )
                && rosterRepository.RosterPrivate
                && not rosterRepository.RosterArchived
                && not rosterRepository.RosterDisabled
                && declaredRepositories = roster
                && selectedRepositories.InstallationId =
                    installation.ComposerRosterCapture.First.ScopeSettings.InstallationId

            if not repositoryBinding then
                unavailable "installation-repository-mismatch"
            else
                match splitRepositoryName repository.FullName with
                | None -> unavailable "repository-name"
                | Some(owner, name) when
                    options.Repository.ExpectedRepositoryId <> repository.Id
                    || not (String.Equals(options.Repository.Owner, owner, StringComparison.OrdinalIgnoreCase))
                    || not (String.Equals(options.Repository.Repository, name, StringComparison.OrdinalIgnoreCase))
                    ->
                    unavailable "provider-repository-mismatch"
                | Some _ when
                    not (
                        String.Equals(
                            options.Project.Organization,
                            cohort.ProjectOrganization,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    || options.Project.ProjectNumber <> cohort.ProjectNumber
                    || options.Project.ExpectedProjectNodeId <> cohort.ProjectNodeId
                    ->
                    unavailable "provider-project-mismatch"
                | Some _ ->
                    let mappingByName =
                        mappings |> List.map (fun item -> item.ReceiverCopyId, item) |> Map.ofList

                    let mismatch =
                        receivers
                        |> List.exists (fun receiver ->
                            match Map.tryFind receiver.Receiver mappingByName with
                            | None -> true
                            | Some mapping ->
                                receiver.RepositoryId <> repository.Id
                                || not (
                                    String.Equals(
                                        mapping.ReceiverCopyRepository,
                                        repository.FullName,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                || receiver.RefName <> mapping.ReceiverCopyPlannedRef
                                || receiver.ExpectedHead <> mapping.ReceiverCopySourceRevision)

                    if mismatch then
                        unavailable "copy-plan-ref-mismatch"
                    else
                        Ok()

    let compose request (transport: IMigrationGitHubReadTransport) =
        MigrationReceiverInstallationRead.captureForComposer request.InstallationOptions transport
        |> Result.mapError (fun reason -> $"receiver-cohort-orchestration-unavailable:installation:{reason}")
        |> Result.bind (fun installation ->
            MigrationReceiverCopyPlan.verify request.AcceptedEvidence request.RunIdentity request.CopyPlan
            |> Result.mapError (fun reason -> $"receiver-cohort-orchestration-unavailable:copy-plan:{reason}")
            |> Result.bind (fun copyPlan ->
                validateBindingsForTests installation request.ProviderOptions copyPlan
                |> Result.bind (fun () ->
                    MigrationReceiverCapture.captureWorkflowPinsTwoPass
                        request.ProviderOptions.Cohort
                        request.ProviderOptions.Repository
                        transport
                    |> Result.mapError (fun reason ->
                        $"receiver-cohort-orchestration-unavailable:workflow-pins:{reason}")
                    |> Result.bind (fun pinCapture ->
                        MigrationReceiverCopyBlobCapture.verifyCoverage
                            request.AcceptedEvidence
                            request.RunIdentity
                            copyPlan
                            request.BlobBatches
                            request.BlobArtifacts
                        |> Result.mapError (fun reason ->
                            $"receiver-cohort-orchestration-unavailable:blob-coverage:{reason}")
                        |> Result.bind (fun coverage ->
                            MigrationReceiverAuthorityComposer.compose
                                {
                                    Options = request.ProviderOptions
                                    RosterOptions = installation.ComposerRosterOptions
                                    RosterCapture = installation.ComposerRosterCapture
                                    AcceptedEvidence = request.AcceptedEvidence
                                    RunIdentity = request.RunIdentity
                                    CopyPlan = copyPlan
                                    BlobBatches = request.BlobBatches
                                    BlobArtifacts = request.BlobArtifacts
                                    BlobCoverage = coverage
                                    PinCapture = pinCapture
                                }
                            |> Result.mapError (fun reason ->
                                $"receiver-cohort-orchestration-unavailable:composer:{reason}")
                            |> Result.map (fun composition ->
                                {
                                    Installation = installation
                                    PinCapture = pinCapture
                                    BlobCoverage = coverage
                                    AuthorityComposition = composition
                                }))))))
