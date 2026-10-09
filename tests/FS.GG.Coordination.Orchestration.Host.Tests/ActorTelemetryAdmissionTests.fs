module FS.GG.Coordination.Orchestration.Host.Tests.ActorTelemetryAdmissionTests

open System
open System.Text
open System.Text.Json
open System.Threading
open Xunit
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.Runner.Protocol
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Host.Tests.HostTests

let private unwrap result = Result.defaultWith failwith result
let private causal () =
    ExecutionCausalAdmission.create "original" "member" (Guid.NewGuid()) (Guid.NewGuid()) 1L Fixture.now ExecutionCausalAdmission.unclassified None |> unwrap

[<Fact>]
let ``causal root child and retry retain exact canonical lineage and declaration`` () =
    let root = causal ()
    let declaration = { ExecutionCausalAdmission.unclassified with Purpose = "repair"; RetryOfInvocationId = root.InvocationId }
    let child = ExecutionCausalAdmission.create root.OriginalItemId "child" (Guid.NewGuid()) (Guid.NewGuid()) 2L Fixture.now declaration (Some root) |> unwrap
    Assert.Equal(root.RootInvocationId, child.RootInvocationId)
    Assert.Equal(root.InvocationId, child.ParentInvocationId)
    Assert.Equal(root.InvocationId, child.Declaration.RetryOfInvocationId)
    Assert.NotEqual(root.InvocationId, child.InvocationId)
    Assert.Equal(child, ExecutionCausalAdmission.encode child |> ExecutionCausalAdmission.parse |> unwrap)
    Assert.True(ExecutionCausalAdmission.validate { child with Generation = 1L } |> Result.isError)
    Assert.True(ExecutionCausalAdmission.validate { child with ParentInvocationId = "foreign" } |> Result.isError)

[<Fact>]
let ``parent generation stays between declared root and child on direct and wire admission`` () =
    let root = causal ()
    let child = ExecutionCausalAdmission.create root.OriginalItemId "child" (Guid.NewGuid()) (Guid.NewGuid()) 2L Fixture.now root.Declaration (Some root) |> unwrap
    let followUp = ExecutionCausalAdmission.create root.OriginalItemId "follow-up" (Guid.NewGuid()) (Guid.NewGuid()) 3L Fixture.now root.Declaration (Some child) |> unwrap
    for valid in [ root; child; { followUp with Relation = "follow-up" } ] do
        Assert.True(ExecutionCausalAdmission.validate valid |> Result.isOk)
        Assert.Equal(valid, ExecutionCausalAdmission.encode valid |> ExecutionCausalAdmission.parse |> unwrap)
    for generation in [ -1L; 0L; child.Generation; child.Generation + 1L ] do
        let malformed =
            { child with
                ParentGeneration = Nullable generation
                ParentInvocationId = ExecutionCausalAdmission.invocationId child.OriginalItemId child.ParentAttemptId.Value generation }
        Assert.True(ExecutionCausalAdmission.validate malformed |> Result.isError)
        Assert.True(ExecutionCausalAdmission.encode malformed |> ExecutionCausalAdmission.parse |> Result.isError)
    let beforeRoot = { child with Generation = 0L; InvocationId = ExecutionCausalAdmission.invocationId child.OriginalItemId child.AttemptId 0L }
    Assert.True(ExecutionCausalAdmission.validate beforeRoot |> Result.isError)
    Assert.True(ExecutionCausalAdmission.validate { root with RootGeneration = -1L } |> Result.isError)

[<Fact>]
let ``declarations refuse invented purpose duplicate edges missing provenance and overflow`` () =
    let root = causal ()
    let edge = { OriginalItemId = "other-original"; InvocationId = "dependency"; SourceReference = "source:exact-admission" }
    for declaration in
        [ { root.Declaration with Purpose = "actor-role" }
          { root.Declaration with Dependencies = [| edge; edge |] }
          { root.Declaration with Dependencies = [| { edge with SourceReference = "" } |] }
          { root.Declaration with Dependencies = Array.create 17 edge }
          { root.Declaration with RetryOfInvocationId = "unresolved-parent" } ] do
        Assert.True(ExecutionCausalAdmission.validate { root with Declaration = declaration } |> Result.isError)
    let encoded = ExecutionCausalAdmission.encode root |> Encoding.UTF8.GetString
    let duplicate = encoded.Replace("\"schema\":", "\"schema\":\"duplicate\",\"schema\":") |> Encoding.UTF8.GetBytes
    Assert.True(ExecutionCausalAdmission.parse duplicate |> Result.isError)
    Assert.True(ExecutionCausalAdmission.parse (Array.create 4097 32uy) |> Result.isError)

[<Fact>]
let ``shared admission projector is exact replay and retains parent dispatch semantics`` () =
    let root = causal ()
    let child = ExecutionCausalAdmission.create root.OriginalItemId "child" (Guid.NewGuid()) (Guid.NewGuid()) 2L Fixture.now root.Declaration (Some root) |> unwrap
    let firstName, first = ExecutionAdmissionFacts.prepare child (Some "model") (Some "medium") |> unwrap
    let secondName, second = ExecutionAdmissionFacts.prepare child (Some "model") (Some "medium") |> unwrap
    Assert.Equal(firstName, secondName)
    Assert.True(first.AsSpan().SequenceEqual(second.AsSpan()))
    use document = JsonDocument.Parse first
    let events = document.RootElement.GetProperty("events")
    Assert.Equal(4, events.GetArrayLength())
    Assert.Equal(ExecutionAdmissionFacts.dispatchId root.OriginalItemId root.AttemptId root.Generation, events[0].GetProperty("parentDispatchId").GetString())
    Assert.Equal(root.InvocationId, events[1].GetProperty("parentInvocationId").GetString())

[<Fact>]
let ``real qualified preparer persists route before intent and resolver recovers exact metadata`` () =
    task {
        let journal = Fixture.MemoryJournal()
        let executor = Fixture.MemoryExecutor(fun () -> journal.State)
        let request = Fixture.preparationRequest ()
        let! result = MainAdmissionPreparer.prepareCausal (Fixture.FixedClock()) journal executor executor Fixture.permit.SubjectId "pilot-route" request
                            (Encoding.UTF8.GetBytes "Keep exact mandatory source") ExecutionCausalAdmission.unclassified CancellationToken.None
        let _ = result |> unwrap
        let! stored = (executor :> IExecutorCommandStore).ReadRoute(request.ProcessOperationId, request.AttemptId, CancellationToken.None)
        let route, causal, _ = stored |> unwrap |> QualifiedExecutorWire.parseRoute |> unwrap
        let admission = causal |> Option.defaultWith (fun () -> failwith "causal binding missing")
        Assert.Equal(WorkItemIdentity.persistenceId Fixture.permit.SubjectId, admission.OriginalItemId)
        Assert.Equal(route.AssignmentId, admission.AssignmentId)
        let! stream = (executor :> IExecutionSessionJournal).ReadAttempt(route.AssignmentId, route.AttemptId, CancellationToken.None)
        let intent = stream.Value.Events |> List.head |> function LaunchIntentRecorded value -> value | _ -> failwith "first intent absent"
        Assert.Equal(intent.RecordedAt, admission.AdmittedAt)
        let resolver = PostgreSqlExecutorBindingResolver(executor, executor, route.AssignmentId, route.AttemptId) :> IExecutorBindingResolver
        let! resolved = resolver.Resolve(intent, CancellationToken.None)
        Assert.True(Result.isOk resolved)
        Assert.True(stored |> unwrap |> ExecutorWire.parseRouteBinding |> Result.isError) // old receiver does not fall back
    }

[<Fact>]
let ``recovery queue is replay stable and partial census never claims complete coverage`` () =
    task {
        let admission = causal ()
        let request = Fixture.preparationRequest ()
        let route0: ExecutorRouteBinding =
            { Schema = ExecutorWire.routeBindingSchema; BindingSha256 = ""
              WorkItemPersistenceId = admission.MemberItemId; RouteId = request.RouteId
              RouteOperationId = admission.AssignmentId; ProcessOperationId = admission.AssignmentId
              AssignmentId = admission.AssignmentId; AttemptId = admission.AttemptId; CandidateId = request.CandidateId
              Generation = admission.Generation; RepositoryBinding = request.RepositoryBinding; BaselineObjectId = request.BaselineObjectId
              PromptDigest = String.replicate 64 "a"; WorkspaceManifestSha256 = String.replicate 64 "b"; ExecutorBinding = "runner"
              ParentAttemptId = Nullable(); ParentGeneration = Nullable(); TelemetryRelation = null }
        let route = { route0 with BindingSha256 = ExecutorWire.routeBindingDigest route0 }
        let bytes = QualifiedExecutorWire.wrapRoute route admission |> unwrap
        Assert.True(ExecutorWire.parseRouteBinding bytes |> Result.isError)
        Assert.True(QualifiedExecutorWire.wrapRoute { route with WorkItemPersistenceId = "foreign" } admission |> Result.isError)
        let intent: LaunchIntent =
            { Schema = ExecutionProtocol.launchSchema; Key = { AssignmentId = admission.AssignmentId; AttemptId = admission.AttemptId; Generation = admission.Generation }
              InputDigest = route.PromptDigest; Workspace = "workspace"; Requested = { Model = Some "model"; Effort = None }
              Limits = { Deadline = Fixture.now.AddMinutes 1.; MaximumRuntime = TimeSpan.FromMinutes 1.; MaximumAttempts = 1 }
              RecordedAt = Fixture.now }
        let root = System.IO.Directory.CreateTempSubdirectory("actor-admission-unit-").FullName
        try
            let publisher = FS.GG.Coordination.Orchestration.Runner.Client.TelemetryCliPublisher
                                { Executable = "/unselected"; Config = "/unselected"; CredentialFile = "/unselected"
                                  CertificateAuthorityFile = "/unselected"; Outbox = root; Repository = request.RepositoryBinding; BindingDigest = String.replicate 64 "a" }
            let page = { Admissions = [ route, admission, intent ]; NextCursor = Some "cursor"; Truncated = true; Coverage = "partial-page" }
            let bridge = TelemetryAdmissionBridge((fun _ -> System.Threading.Tasks.Task.FromResult(Ok page)), publisher)
            let! first = bridge.Recover(1, None, CancellationToken.None)
            let! replay = bridge.Recover(1, None, CancellationToken.None)
            Assert.Equal("partial-page", first.Coverage)
            Assert.Equal(first, replay)
            Assert.Equal(1, publisher.PendingCount)
            let files = System.IO.Directory.GetFiles(root, "*.json")
            let firstBytes = System.IO.File.ReadAllBytes files[0]
            let! _ = bridge.Recover(1, None, CancellationToken.None)
            Assert.True(firstBytes.AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(files[0]).AsSpan()))
            let unavailable = TelemetryAdmissionBridge((fun _ -> System.Threading.Tasks.Task.FromResult(Error "source-unavailable")), publisher)
            let! failure = unavailable.Recover(1, None, CancellationToken.None)
            Assert.Equal("unknown", failure.Coverage)
            Assert.Single(failure.Gaps) |> ignore
        finally System.IO.Directory.Delete(root, true)
    }
