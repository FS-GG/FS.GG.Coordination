namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.Orchestration.Execution.Codex
open FS.GG.Coordination.Orchestration.Runner.Client
open FS.GG.Coordination.Orchestration.Runner.Protocol
open Xunit

type LearningTelemetryFactsTests() =
    let digest character = String(character, 64)

    let treatment =
        {
            OriginalItemId = "I-001"
            Arm = LearningTreatmentArm.Focused
            AssignmentSha256 = digest 'a'
            ProposalSha256 = digest 'b'
            ContextManifestSha256 = digest 'c'
            OwnerPrincipalId = "owner-1"
            WorkflowRevision = "workflow-r7"
            Generation = 3L
            AssignedAt = DateTimeOffset.Parse "2026-09-29T08:01:00Z"
        }

    let configuration =
        {
            SubjectBindingSha256 = digest 'f'
            ExperimentContractId = LearningTelemetryFacts.ExperimentContractId
            PolicyRepository = LearningTelemetryFacts.PolicyRepository
            PolicyRevision = LearningTelemetryFacts.PolicyRevision
            PolicyPath = LearningTelemetryFacts.PolicyPath
            PolicySha256 = LearningTelemetryFacts.PolicySha256
            PolicyStatus = LearningTelemetryFacts.PolicyStatus
            WorkClassId = LearningTelemetryFacts.WorkClassId
            QualificationOnly = true
            SnapshotId = "task-I-001"
            RubricVersion = "1"
            SnapshotDigest = digest 'd'
            CapturedAt = DateTimeOffset.Parse "2026-09-29T08:00:00Z"
            RecipeId = "focused-recipe-v1"
            RecipeDigest = digest 'e'
            ManifestId = "manifest-I-001"
            ManifestDigest = digest 'c'
            ManifestVersion = "1"
        }

    let context =
        {
            ItemId = "child-I-002"
            AttemptId = "attempt-1"
            ActivationId = "activation-1"
            DispatchId = "dispatch-1"
            InvocationId = "invocation-1"
        }

    let command requestedModel requestedEffort =
        let now = DateTimeOffset.Parse "2026-09-29T08:02:00Z"
        let unsigned =
            {
                Schema = ExecutorWire.commandSchemaV2
                CommandId = Guid.NewGuid()
                BodySha256 = ""
                Kind = "learning-qualification"
                WorkItemPersistenceId = "I-001"
                RouteOperationId = Guid.Parse "10000000-0000-0000-0000-000000000001"
                AssignmentId = Guid.Parse "10000000-0000-0000-0000-000000000001"
                AttemptId = Guid.Parse "20000000-0000-0000-0000-000000000002"
                CandidateId = Guid.Parse "30000000-0000-0000-0000-000000000003"
                Generation = 3L
                ExpectedRevision = 1L
                RecordedAt = now
                Deadline = now.AddMinutes 5.
                MaximumRuntimeSeconds = 300L
                MaximumAttempts = 1
                Workspace = "pilot"
                WorkspaceManifestSha256 = digest '1'
                RequestedModel = requestedModel
                RequestedEffort = requestedEffort
                InputDigest = digest '2'
                ExecutorBinding = "fixture-executor"
                ProviderSessionReference = null
                ArtifactDigest = null
                ContentOffset = 0L
                ContentLength = 0
                ParentAttemptId = Nullable()
                ParentGeneration = Nullable()
                TelemetryRelation = null
            }

        { unsigned with BodySha256 = ExecutorWire.commandV2Digest unsigned }

    let prepared () =
        LearningTelemetryFacts.prepare treatment configuration |> Result.defaultWith failwith

    let publisher outbox =
        TelemetryCliPublisher
            {
                Executable = "/missing/client"
                Config = "/missing/config"
                CredentialFile = "/missing/credential"
                CertificateAuthorityFile = "/missing/ca"
                Outbox = outbox
                Repository = "FS-GG/.github"
                BindingDigest = digest '8'
            }

    [<Fact>]
    member _.``shadow qualification emits canonical facts with explicit deviation``() =
        let prepared =
            configuration
            |> LearningTelemetryFacts.prepare treatment
            |> Result.defaultWith failwith

        let _, bytes = TelemetryFactBatches.learningPreparation context prepared
        use document = JsonDocument.Parse bytes
        let events = document.RootElement.GetProperty "events"

        Assert.Equal(3, events.GetArrayLength())
        Assert.All(events.EnumerateArray(), fun fact -> Assert.Equal("I-001", fact.GetProperty("itemId").GetString()))
        Assert.Equal("focused", events[2].GetProperty("arm").GetString())
        Assert.Equal("qualification-only-not-enrolled", events[2].GetProperty("deviation").GetString())
        Assert.Equal("qualification:learn-01.3:" + digest 'a', events[2].GetProperty("windowId").GetString())

    [<Fact>]
    member _.``exact replay is byte identical``() =
        let prepared =
            configuration
            |> LearningTelemetryFacts.prepare treatment
            |> Result.defaultWith failwith

        let firstName, firstBytes = TelemetryFactBatches.learningPreparation context prepared
        let secondName, secondBytes = TelemetryFactBatches.learningPreparation context prepared
        Assert.Equal(firstName, secondName)
        Assert.Equal<byte>(firstBytes, secondBytes)

        let childContext =
            { context with
                ItemId = "I-001-child"
                AttemptId = "attempt-child"
                InvocationId = "invocation-child"
            }

        let childName, childBytes = TelemetryFactBatches.learningPreparation childContext prepared
        Assert.Equal(firstName, childName)
        Assert.Equal<byte>(firstBytes, childBytes)

    [<Fact>]
    member _.``configuration cannot replace durable manifest identity``() =
        let invalid =
            { configuration with
                ManifestDigest = digest '9'
            }

        match LearningTelemetryFacts.prepare treatment invalid with
        | Error code -> Assert.Equal("learning-telemetry-manifest-mismatch", code)
        | Ok _ -> failwith "mismatched manifest unexpectedly accepted"

    [<Fact>]
    member _.``non qualification configuration refuses projection``() =
        let invalid = { configuration with QualificationOnly = false }

        match LearningTelemetryFacts.prepare treatment invalid with
        | Error code -> Assert.Equal("learning-telemetry-not-qualification-only", code)
        | Ok _ -> failwith "invalid provenance unexpectedly accepted"

    [<Fact>]
    member _.``learning batch is journaled before publication and exact replay recovers it``() =
        let root = Directory.CreateTempSubdirectory("learning-telemetry-replay-").FullName
        let command = command "gpt-5.6-sol" "medium"
        let prepared = prepared ()

        TelemetryRunnerObserver(root, command, None, learning = prepared) |> ignore

        let journalDirectory =
            Path.Combine(root, "telemetry-turns", command.AssignmentId.ToString("N"), command.AttemptId.ToString("N"), "3")

        Assert.Single(Directory.GetFiles(journalDirectory, "learning-*.json")) |> ignore

        let outbox = Path.Combine(root, "recovered")
        let recovered = publisher outbox
        Assert.Empty(TelemetryJournalRecovery.requeue root command recovered)
        Assert.Single(Directory.GetFiles(outbox, "*.json")) |> ignore
        Assert.Empty(TelemetryJournalRecovery.requeue root command recovered)
        Assert.Single(Directory.GetFiles(outbox, "*.json")) |> ignore

    [<Fact>]
    member _.``conflicting learning payload under one identity refuses``() =
        let root = Directory.CreateTempSubdirectory("learning-telemetry-conflict-").FullName
        let command = command "gpt-5.6-sol" "medium"
        let journal = TelemetryTurnJournal(root, command)
        journal.RecordLearningBatch("batch-learning", [| 1uy |])
        journal.RecordLearningBatch("batch-learning", [| 1uy |])

        Assert.Throws<InvalidOperationException>(fun () ->
            journal.RecordLearningBatch("batch-learning", [| 2uy |]))
        |> ignore

    [<Fact>]
    member _.``delayed native mismatch and absence retain gaps with incurred usage``() =
        let root = Directory.CreateTempSubdirectory("learning-telemetry-selection-").FullName
        let command = command "gpt-5.6-sol" "medium"
        let outbox = Path.Combine(root, "outbox")
        let observer =
            TelemetryRunnerObserver(root, command, Some(publisher outbox), learning = prepared ())
            :> ICodexTurnObserver

        observer.TurnCompleted
            {
                ThreadId = "thread-1"
                TurnId = Some "turn-delayed"
                TurnSequence = 1L
                Provider = Some "openai"
                ObservedModel = Some "gpt-6-sol"
                ObservedEffort = None
                Backend = Some "codex"
                Input = 10L
                CachedInput = 2L
                Output = 4L
                Reasoning = Some 1L
                Total = 14L
            }

        let events =
            Directory.GetFiles(outbox, "*.json")
            |> Array.map (fun path ->
                use document = JsonDocument.Parse(File.ReadAllBytes path)
                ((document.RootElement.GetProperty "events")[0]).Clone())

        Assert.Contains(events, fun event -> event.GetProperty("kind").GetString() = "runtime-turn-usage")
        Assert.Contains(events, fun event ->
            event.GetProperty("kind").GetString() = "runtime-gap"
            && event.GetProperty("code").GetString() = "learning-native-model-mismatch")
        Assert.Contains(events, fun event ->
            event.GetProperty("kind").GetString() = "runtime-gap"
            && event.GetProperty("code").GetString() = "learning-native-effort-unobserved")
