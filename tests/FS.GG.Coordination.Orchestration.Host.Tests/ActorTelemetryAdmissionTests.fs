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
            let _, retained, routeDigest = QualifiedExecutorWire.parseRouteWithAdmissionBytes bytes |> unwrap
            let _, raw = retained |> Option.defaultWith (fun () -> failwith "exact admission absent")
            let source =
                { Route = route; Admission = admission; AdmissionBytes = raw
                  RouteBindingSha256 = routeDigest; LaunchIntentSha256 = RunnerWire.sha256(SessionEventCodec.encode (LaunchIntentRecorded intent)); Intent = intent }
            let page = { Admissions = [ source ]; NextCursor = Some "cursor"; Truncated = true; Coverage = "partial-page" }
            let bridge = TelemetryAdmissionBridge((fun _ -> System.Threading.Tasks.Task.FromResult(Ok page)), publisher)
            let! first = bridge.Recover(1, None, CancellationToken.None)
            let! replay = bridge.Recover(1, None, CancellationToken.None)
            Assert.Equal("partial-page", first.Coverage)
            Assert.Equal(first, replay)
            Assert.Equal(2, publisher.PendingCount)
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

let private fixedCausalFixtures () =
    let directory = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "Learning", "CausalAdmission")
    let manifestBytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(directory, "manifest.json"))
    Assert.Equal("58de080758af83f2e243faf6995a44a529d00f810d43b2ac64753d14f59ce880", RunnerWire.sha256 manifestBytes)
    use manifest = JsonDocument.Parse manifestBytes
    let root = manifest.RootElement
    Assert.Equal("fsgg.learn.causal-admission-fixtures/1", root.GetProperty("schema").GetString())
    Assert.Equal("synthetic-source-contract-not-native-installation", root.GetProperty("provenance").GetString())
    let cases = root.GetProperty("cases").EnumerateArray() |> Seq.toArray
    Assert.Equal(4, root.GetProperty("caseCount").GetInt32())
    Assert.Equal(4, cases.Length)
    cases |> Array.map (fun fixture ->
        let read prefix =
            let bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(directory, fixture.GetProperty(prefix + "File").GetString()))
            Assert.Equal(fixture.GetProperty(prefix + "Bytes").GetInt32(), bytes.Length)
            Assert.Equal(fixture.GetProperty(prefix + "Sha256").GetString(), RunnerWire.sha256 bytes)
            bytes
        let raw, event = read "admission", read "event"
        let admission = ExecutionCausalAdmission.parse raw |> unwrap
        Assert.Equal(fixture.GetProperty("invocationId").GetString(), admission.InvocationId)
        fixture.GetProperty("name").GetString(), admission, raw, event)

[<Fact>]
let ``fixed causal declaration fixtures retain exact raw bytes and separate immutable batch identity`` () =
    for name, admission, raw, expectedEvent in fixedCausalFixtures () do
        use expected = JsonDocument.Parse expectedEvent
        let event = expected.RootElement
        let oldName, oldBytes = ExecutionAdmissionFacts.prepare admission None None |> unwrap
        let batchName, batchBytes =
            ExecutionCausalAdmissionFacts.prepare admission raw (event.GetProperty("routeBindingSha256").GetString()) (event.GetProperty("launchIntentSha256").GetString()) |> unwrap
        let replayName, replayBytes =
            ExecutionCausalAdmissionFacts.prepare admission raw (event.GetProperty("routeBindingSha256").GetString()) (event.GetProperty("launchIntentSha256").GetString()) |> unwrap
        Assert.Equal(batchName, replayName)
        Assert.True(batchBytes.AsSpan().SequenceEqual(replayBytes.AsSpan()))
        Assert.NotEqual(oldName, batchName)
        let sameOldName, sameOldBytes = ExecutionAdmissionFacts.prepare admission None None |> unwrap
        Assert.Equal(oldName, sameOldName)
        Assert.True(oldBytes.AsSpan().SequenceEqual(sameOldBytes.AsSpan()))
        use batch = JsonDocument.Parse batchBytes
        let actual = (batch.RootElement.GetProperty("events")).[0]
        Assert.Equal(9, actual.EnumerateObject() |> Seq.length)
        Assert.Equal(0L, actual.GetProperty("revision").GetInt64())
        for key in [ "kind"; "identity"; "itemId"; "dispatchId"; "admissionBase64"; "admissionSha256"; "routeBindingSha256"; "launchIntentSha256" ] do
            Assert.Equal(event.GetProperty(key).GetString(), actual.GetProperty(key).GetString())
        Assert.True(raw.AsSpan().SequenceEqual(Convert.FromBase64String(actual.GetProperty("admissionBase64").GetString()).AsSpan()))
        if name = "root" then Assert.False(raw.AsSpan().SequenceEqual((ExecutionCausalAdmission.encode admission).AsSpan()))
        let identityDigest = RunnerWire.sha256(Encoding.UTF8.GetBytes("execution-causal-admission/1" + "\u001f" + actual.GetProperty("identity").GetString()))
        Assert.Equal("batch-" + identityDigest, batchName)
        Assert.Equal(identityDigest, batch.RootElement.GetProperty("cursor").GetString())
        Assert.Equal(admission.InvocationId, batch.RootElement.GetProperty("generation").GetString())
        Assert.Equal("coordination", batch.RootElement.GetProperty("sourceIdentity").GetString())
        Assert.True(ExecutionCausalAdmissionFacts.prepare { admission with MemberItemId = "changed" } raw (String.replicate 64 "a") (String.replicate 64 "b") |> Result.isError)
        Assert.True(ExecutionCausalAdmissionFacts.prepare admission raw (String.replicate 64 "A") (String.replicate 64 "b") |> Result.isError)

let private fixedCommittedSource (admission: ExecutionCausalAdmission) (raw: byte array) =
    let route0: ExecutorRouteBinding =
        { Schema = (if admission.ParentAttemptId.HasValue then ExecutorWire.routeBindingSchemaV2 else ExecutorWire.routeBindingSchema); BindingSha256 = ""
          WorkItemPersistenceId = admission.MemberItemId; RouteId = Guid.NewGuid()
          RouteOperationId = admission.AssignmentId; ProcessOperationId = admission.AssignmentId
          AssignmentId = admission.AssignmentId; AttemptId = admission.AttemptId; CandidateId = Guid.NewGuid()
          Generation = admission.Generation; RepositoryBinding = "FS-GG/synthetic"; BaselineObjectId = String.replicate 40 "a"
          PromptDigest = String.replicate 64 "a"; WorkspaceManifestSha256 = String.replicate 64 "b"; ExecutorBinding = "runner"
          ParentAttemptId = admission.ParentAttemptId; ParentGeneration = admission.ParentGeneration
          TelemetryRelation = if admission.Relation = "root" then null else admission.Relation }
    let route = { route0 with BindingSha256 = ExecutorWire.routeBindingDigest route0 }
    let wrapper: QualifiedExecutorRoute =
        { Schema = QualifiedExecutorWire.routeSchema; BindingSha256 = ""; Binding = route; AdmissionBase64 = Convert.ToBase64String raw }
    let retainedRoute = QualifiedExecutorWire.encodeRoute { wrapper with BindingSha256 = QualifiedExecutorWire.routeDigest wrapper }
    let _, retained, digest = QualifiedExecutorWire.parseRouteWithAdmissionBytes retainedRoute |> unwrap
    let parsed, exact = retained |> Option.defaultWith (fun () -> failwith "fixed admission missing")
    Assert.Equal(admission, parsed)
    Assert.True(raw.AsSpan().SequenceEqual(exact.AsSpan()))
    let intent: LaunchIntent =
        { Schema = ExecutionProtocol.launchSchema; Key = { AssignmentId = admission.AssignmentId; AttemptId = admission.AttemptId; Generation = admission.Generation }
          InputDigest = route.PromptDigest; Workspace = "synthetic-source-workspace"; Requested = { Model = None; Effort = None }
          Limits = { Deadline = admission.AdmittedAt.AddMinutes 1.; MaximumRuntime = TimeSpan.FromMinutes 1.; MaximumAttempts = 1 }
          RecordedAt = admission.AdmittedAt }
    { Route = route; Admission = admission; AdmissionBytes = exact; RouteBindingSha256 = digest
      LaunchIntentSha256 = RunnerWire.sha256(SessionEventCodec.encode (LaunchIntentRecorded intent)); Intent = intent }

[<Fact>]
let ``lossless route extraction preserves fixed raw bytes and ordinary parser results`` () =
    for _, admission, raw, _ in fixedCausalFixtures () do
        let source = fixedCommittedSource admission raw
        let name, bytes = ExecutionCausalAdmissionFacts.prepare source.Admission source.AdmissionBytes source.RouteBindingSha256 source.LaunchIntentSha256 |> unwrap
        Assert.False(String.IsNullOrWhiteSpace name)
        use batch = JsonDocument.Parse bytes
        Assert.Equal(RunnerWire.sha256 raw, (batch.RootElement.GetProperty("events")).[0].GetProperty("admissionSha256").GetString())
        let legacyBytes = ExecutorWire.encodeRouteBinding source.Route
        let oldRoute, oldAdmission, oldDigest = QualifiedExecutorWire.parseRoute legacyBytes |> unwrap
        let recoveredRoute, recoveredAdmission, recoveredDigest = QualifiedExecutorWire.parseRouteWithAdmissionBytes legacyBytes |> unwrap
        Assert.Equal(oldRoute, recoveredRoute)
        Assert.True(oldAdmission.IsNone && recoveredAdmission.IsNone)
        Assert.Equal(oldDigest, recoveredDigest)

[<Fact>]
let ``independent queue obligations survive capacity and legacy conflicts without double counting`` () =
    task {
        let _, admission, raw, _ = fixedCausalFixtures () |> Array.head
        let source = fixedCommittedSource admission raw
        let oldName, oldBytes = ExecutionAdmissionFacts.prepare admission None None |> unwrap
        let newName, newBytes = ExecutionCausalAdmissionFacts.prepare admission raw source.RouteBindingSha256 source.LaunchIntentSha256 |> unwrap
        for conflictFirst in [ false; true ] do
            let root = System.IO.Directory.CreateTempSubdirectory("causal-queue-unit-").FullName
            try
                let publisher = FS.GG.Coordination.Orchestration.Runner.Client.TelemetryCliPublisher
                                    { Executable = "/unselected"; Config = "/unselected"; CredentialFile = "/unselected"
                                      CertificateAuthorityFile = "/unselected"; Outbox = root; Repository = "FS-GG/synthetic"; BindingDigest = String.replicate 64 "a" }
                if conflictFirst then publisher.Queue(oldName, Encoding.UTF8.GetBytes "conflicting-old-bytes") |> unwrap |> ignore
                else
                    for index in 1..127 do publisher.Queue("filler-" + string index, Encoding.UTF8.GetBytes "{}") |> unwrap |> ignore
                let page = { Admissions = [ source ]; NextCursor = None; Truncated = false; Coverage = "complete-read-snapshot" }
                let bridge = TelemetryAdmissionBridge((fun _ -> System.Threading.Tasks.Task.FromResult(Ok page)), publisher)
                let! partial = bridge.Recover(1, None, CancellationToken.None)
                Assert.Equal(1, partial.Observed)
                Assert.Equal(0, partial.Queued)
                Assert.Equal("publication-incomplete", partial.Coverage)
                Assert.Single(partial.Gaps) |> ignore
                if conflictFirst then
                    Assert.True(System.IO.File.ReadAllBytes(System.IO.Path.Combine(root, newName + ".json")).AsSpan().SequenceEqual(newBytes.AsSpan()))
                    System.IO.File.Delete(System.IO.Path.Combine(root, oldName + ".json"))
                else
                    Assert.True(System.IO.File.ReadAllBytes(System.IO.Path.Combine(root, oldName + ".json")).AsSpan().SequenceEqual(oldBytes.AsSpan()))
                    Assert.False(System.IO.File.Exists(System.IO.Path.Combine(root, newName + ".json")))
                    System.IO.File.Delete(System.IO.Path.Combine(root, "filler-1.json"))
                let! complete = bridge.Recover(1, None, CancellationToken.None)
                let! replay = bridge.Recover(1, None, CancellationToken.None)
                Assert.Equal(1, complete.Observed)
                Assert.Equal(1, complete.Queued)
                Assert.Empty(complete.Gaps)
                Assert.Equal(complete, replay)
                for name, expected in [ oldName, oldBytes; newName, newBytes ] do
                    Assert.True(System.IO.File.ReadAllBytes(System.IO.Path.Combine(root, name + ".json")).AsSpan().SequenceEqual(expected.AsSpan()))
                let changed = { admission with Declaration = { admission.Declaration with Purpose = "review" } }
                let changedName, changedBytes = ExecutionCausalAdmissionFacts.prepare changed (ExecutionCausalAdmission.encode changed) source.RouteBindingSha256 source.LaunchIntentSha256 |> unwrap
                Assert.Equal(newName, changedName)
                Assert.True(publisher.Queue(changedName, changedBytes) |> Result.isError)
            finally System.IO.Directory.Delete(root, true)
    }
