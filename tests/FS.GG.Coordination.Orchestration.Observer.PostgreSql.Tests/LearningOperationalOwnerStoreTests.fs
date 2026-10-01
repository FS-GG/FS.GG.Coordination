module FS.GG.Coordination.Orchestration.Observer.PostgreSql.Tests.LearningOperationalOwnerStoreTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Orchestration.Observer
open FS.GG.Coordination.Orchestration.PostgreSql

module private OwnerFixture =
    let environment name fallback =
        match Environment.GetEnvironmentVariable name with
        | null | "" -> fallback
        | value -> value

    let root =
        environment "FSGG_PG_ROOT" (File.ReadAllText("/tmp/o0-postgresql-current-path").Trim())

    let host = environment "FSGG_PG_HOST" (Path.Combine(root, "socket"))
    let port = environment "FSGG_PG_PORT" "55439"
    let username = environment "FSGG_PG_USERNAME" "developer"

    let dataSource () =
        NpgsqlDataSource.Create($"Host={host};Port={port};Database=orchestration_o0;Username={username};Pooling=false")

    let options source identity =
        {
            DataSource = source
            StoreId = "owner-test"
            BackupIdentity = identity
            MinimumGenerationFence = 0L
            RuntimeSchemaVersion = 1
            SupportedEventSchemaVersions = Set [ 1 ]
            SupportedSerializerVersions = Set [ EventEnvelope.serializerVersion ]
            MaximumCandidateBytes = 1_048_576L
        }

    let reset () =
        task {
            let source = dataSource ()
            use! connection = source.OpenConnectionAsync()
            use command = new NpgsqlCommand("DROP SCHEMA IF EXISTS fsgg_orchestration CASCADE", connection)
            let! _ = command.ExecuteNonQueryAsync()
            do! connection.CloseAsync()
            let! identity = PostgreSqlSchema.migrate source CancellationToken.None
            do! PostgreSqlObserverSchema.migrate source CancellationToken.None
            return source, identity
        }

[<Fact>]
let ``owner admission append is idempotent and recovers exact roster after restart`` () =
    task {
        let! source, identity = OwnerFixture.reset ()
        use source = source
        let store = PostgreSqlObserverStore(OwnerFixture.options source identity) :> IObserverJournalStore
        let now = DateTimeOffset.Parse("2026-10-01T10:00:00Z")
        let sha character = String.replicate 64 character
        let session = Id.session(Guid.Parse "22000000-0000-0000-0000-000000000002")
        let project = Id.project(Guid.Parse "12000000-0000-0000-0000-000000000001")
        let attempt = Id.attempt(Guid.Parse "32000000-0000-0000-0000-000000000003")
        let proposalId = ProposalId.create(Guid.Parse "42000000-0000-0000-0000-000000000004")
        let observerId = ObserverJournal.observerId session
        let mutable state = Observer.initial
        let mutable lastRequest: ObserverAppendRequest option = None

        let append command =
            task {
                let envelope =
                    {
                        CommandId = Id.command(Guid.NewGuid())
                        ExpectedSequence = state.Sequence
                        PrincipalId = "scheduler-owner"
                        IssuedAt = now.AddMinutes -1.
                        ExpiresAt = now.AddMinutes 1.
                        Command = command
                    }

                let decision = Observer.decide now state envelope
                Assert.Equal(ObserverAccepted, decision.Receipt.Disposition)
                let request = ObserverJournal.appendRequest observerId now envelope decision
                let! outcome = store.AppendObserver(request, CancellationToken.None)
                Assert.Equal<ObserverAppendOutcome>(ObserverAppended decision.Receipt.Sequence, outcome)
                state <- decision.Events |> List.fold Observer.evolve state
                lastRequest <- Some request
            }

        let budget =
            { TokenLimit = 100L; RuntimeSecondsLimit = 100L; CostMicrosLimit = 100L; Deadline = now.AddHours 1. }

        do! append (OpenSession(session, project, budget))

        let roles =
            [ LearningOperationalOwnerRole.Root; LearningOperationalOwnerRole.Child; LearningOperationalOwnerRole.Retry
              LearningOperationalOwnerRole.Review; LearningOperationalOwnerRole.Rescue; LearningOperationalOwnerRole.Repair ]

        let observation0 =
            {
                ProjectId = project
                SourceRevision = "protected"
                WorkflowRevision = Id.revision 7L
                Generation = Id.generation 3L
                ObservationSha256 = sha "0"
                Provenance =
                    { Provider = "github"; QuerySha256 = sha "1"; EvidenceSha256 = sha "2"; CapturedAt = now.AddMinutes -5. }
                WorkItems =
                    roles
                    |> List.mapi (fun index _ ->
                        { Identity = WorkItemIdentity.create "R" 1L $"I{index}" (int64 index + 1L)
                          MembershipItemId = (if index = 0 then "original" else $"member-{index}")
                          Archived = index = 4 })
                NonWorkItemCount = 0
            }

        let observation = { observation0 with ObservationSha256 = Observer.observationSha256 observation0 }
        do! append (RecordProjectObservation observation)
        do! append (StartPlanningAttempt(attempt, { Tokens = 10L; RuntimeSeconds = 10L; CostMicros = 10L }, now.AddMinutes -4.))

        let proposalInput =
            {
                ProposalId = proposalId
                AttemptId = attempt
                ObservationSha256 = observation.ObservationSha256
                WorkflowRevision = observation.WorkflowRevision
                Generation = observation.Generation
                Scope = "learn"
                NarrativeSha256 = sha "3"
                Actions = [ { Kind = InspectWorkItem; WorkItem = observation.WorkItems.Head.Identity; ParametersSha256 = sha "4" } ]
                ProposedAt = now.AddMinutes -3.
            }

        do! append (CompletePlanningAttempt(attempt, { Tokens = 5L; RuntimeSeconds = 5L; CostMicros = 5L }, proposalInput))
        let proposal = state.Proposals[proposalId]

        do!
            append
                (ApproveProposal
                    {
                        ProposalId = proposalId
                        PlanSha256 = proposal.PlanSha256
                        PlanningBudgetSha256 = Observer.budgetSha256 budget
                        PrincipalId = "scheduler-owner"
                        Scope = proposal.Scope
                        ExpectedWorkflowRevision = observation.WorkflowRevision
                        ExpectedGeneration = observation.Generation
                        CommandId = Id.command(Guid.NewGuid())
                        CommandBodySha256 = sha "5"
                        Kind = ExplicitApproval
                        ApprovedAt = now.AddMinutes -2.
                    })

        do!
            append
                (AdmitLearningOperationalOwnerWindow
                    {
                        WindowId = "window-1"
                        OriginalItemId = "original"
                        ProposalId = proposalId
                        Repository = "FS-GG/FS.GG.Coordination"
                        WorkClassId = "learn"
                        CalendarAdmissionBlock = "block"
                        SeedReferenceSha256 = sha "6"
                        SharedAllocationRosterReference = "allocation-1"
                        Members =
                            roles
                            |> List.mapi (fun index role ->
                                { ItemId = (if index = 0 then "original" else $"member-{index}"); Role = role })
                        ExpectedWorkflowRevision = observation.WorkflowRevision
                        ExpectedGeneration = observation.Generation
                        OptedInAt = now.AddHours -2.
                        EnrollmentOpensAt = now.AddHours -1.
                        EnrollmentClosesAt = now.AddHours 1.
                        AdmittedAt = now
                    })

        let request = lastRequest.Value
        let! duplicate = store.AppendObserver(request, CancellationToken.None)
        Assert.Equal<ObserverAppendOutcome>(ObserverDuplicate state.Sequence, duplicate)
        let! recovered = store.RecoverObserver(observerId, CancellationToken.None)
        let recovered = recovered |> Result.defaultWith (fun failures -> failwithf "%A" failures)
        let key = Observer.learningOperationalOwnerKey "window-1" "original"
        let durable = recovered.State.LearningOperationalOwnerWindows[key]
        Assert.Equal(6, durable.Members.Length)
        Assert.Equal(state.Sequence, recovered.State.Sequence)
    }
