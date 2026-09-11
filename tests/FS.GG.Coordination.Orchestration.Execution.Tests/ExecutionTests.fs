namespace FS.GG.Coordination.Orchestration.Execution.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Pattern
open FS.GG.Coordination.Orchestration.Execution
open Xunit

type MutableClock(initial:DateTimeOffset) =
    inherit TimeProvider()
    let mutable now=initial
    member _.Advance(value) = now <- now + value
    override _.GetUtcNow() = now

type MemoryJournal() =
    let streams=Dictionary<Guid*Guid,ResizeArray<SessionEvent>>()
    let mutable failNext=false
    member _.FailNextAppend() = failNext <- true
    member _.Events(key:ExecutionKey) =
        match streams.TryGetValue((key.AssignmentId,key.AttemptId)) with
        | true,events -> List.ofSeq events
        | _ -> []
    interface IExecutionSessionJournal with
        member _.ReadAttempt(assignmentId,attemptId,_) = task {
            match streams.TryGetValue((assignmentId,attemptId)) with
            | true,events -> return Some { Revision=int64 events.Count;Events=List.ofSeq events }
            | _ -> return None }
        member _.AppendAttempt(assignmentId,attemptId,revision,eventValue,_) = task {
            if failNext then failNext<-false; return AppendConflict
            else
                let key=assignmentId,attemptId
                let events =
                    match streams.TryGetValue key with
                    | true,value -> value
                    | _ ->
                        let value=ResizeArray()
                        streams.Add(key,value)
                        value
                if int64 events.Count<>revision then return AppendConflict
                elif events |> Seq.exists((=) eventValue) then return DuplicateEvent
                else events.Add eventValue;return Appended }

type FakeProvider(style:string,supportsResume:bool,knownUsage:bool) =
    let sessions=Dictionary<ExecutionKey,SessionObservation>()
    let mutable launches=0
    let mutable intentWasPersisted=true
    let mutable beforeLaunch=(fun (_:LaunchIntent) -> true)
    member _.Launches=launches
    member _.Seed(intent,observation)=sessions[intent.Key] <- observation
    member _.BeforeLaunch with set value=beforeLaunch<-value
    member _.IntentWasPersisted=intentWasPersisted
    member private _.Observation(intent,lifecycle) =
        let reference =
            match ProviderSessionReference.create ($"{style}::{intent.Key.AttemptId:N}") with
            | Ok value -> value
            | Error reason -> failwith reason
        let usage =
            if knownUsage then
                { Values=Map ["work-units",UsageKnown(7L,"units",style+"-counter")]
                  Cost=CostUnknown(style+"-has-no-normalized-price") }
            else
                { Values=Map ["provider-usage",UsageUnknown(style+"-does-not-report-usage")]
                  Cost=CostNotApplicable(style+"-fixture-has-no-billing") }
        { Provider={Provider=style;AdapterVersion="fixture-1"};Session=reference
          Resolved={Model=(if style="stream" then Some "stream-model" else None);Effort=None};Lifecycle=lifecycle
          Output=if style="stream" then [{Kind="chunk-log";Reference="memory://chunks";Digest=None}] else [{Kind="final-text";Reference="memory://result";Digest=Some(String.replicate 64 "a")}]
          LifecycleReferences=if style="stream" then [{Kind="resume-cursor";Reference="opaque://cursor/7";Digest=None}] else []
          Usage=usage;Candidate=None;ObservedAt=intent.RecordedAt.AddSeconds 1. }
    interface IExecutionProvider with
        member _.ObserveReadiness _ = Task.FromResult {Identity={Provider=style;AdapterVersion="fixture-1"};Authentication=Authenticated(style+"-fixture");SupportsResume=supportsResume;ObservedAt=DateTimeOffset.UnixEpoch}
        member this.Launch(intent,_) = task {
            launches<-launches+1
            intentWasPersisted <- beforeLaunch intent
            let observation=this.Observation(intent,Running)
            sessions[intent.Key]<-observation
            return LaunchStarted observation }
        member _.Observe(session,_) = task {
            match sessions.Values |> Seq.tryFind(fun value -> value.Session=session) with
            | Some value -> return Ok value
            | None -> return Error "session-not-found" }
        member _.Reconcile(intent,_) = task {
            match sessions.TryGetValue intent.Key with
            | true,value when supportsResume -> return Reconciled value
            | true,_ -> return ReconcileUnknown "resume-unsupported"
            | _ -> return ConfirmedAbsent }
        member _.Cancel(session,_) = task {
            return if sessions.Values |> Seq.exists(fun value -> value.Session=session) then CancelAccepted else CancelUnknown "session-not-found" }

module Fixture =
    let now=DateTimeOffset(2026,9,11,12,0,0,TimeSpan.Zero)
    let key generation={AssignmentId=Guid.Parse "10000000-0000-0000-0000-000000000001";AttemptId=Guid.Parse "20000000-0000-0000-0000-000000000002";Generation=generation}
    let intent generation =
        { Schema=ExecutionProtocol.launchSchema;Key=key generation;InputDigest=String.replicate 64 "a";Workspace="/work/repository"
          Requested={Model=Some "requested";Effort=Some "medium"}
          Limits={Deadline=now.AddMinutes 5.;MaximumRuntime=TimeSpan.FromMinutes 3.;MaximumAttempts=1};RecordedAt=now }
    let state = function
        | SessionAdvanced state | SessionDuplicate state -> state
        | other -> failwithf "expected session state, got %A" other

type ExecutionTests() =
    [<Theory>]
    [<InlineData("Codex",true,"subscription-session")>]
    [<InlineData("Claude",true,"session-capability-observed")>]
    [<InlineData("OpenCode",false,"resume-not-assumed")>]
    [<InlineData("DeepSeek",false,"transport-and-auth-provider-specific")>]
    member _.``intended adapters fit the neutral readiness contract``(providerName:string,supportsResume:bool,provenance:string) =
        let readiness =
            { Identity={Provider=providerName;AdapterVersion="future-adapter"}
              Authentication=AuthenticationUnknown provenance
              SupportsResume=supportsResume
              ObservedAt=Fixture.now }
        Assert.Equal(providerName,readiness.Identity.Provider)
        Assert.Equal(AuthenticationUnknown provenance,readiness.Authentication)

    [<Fact>]
    member _.``new schema is closed negotiated and byte stable``() =
        let intent=Fixture.intent 3L
        let first=ExecutionProtocol.encode intent
        Assert.Equal<byte>(first,ExecutionProtocol.encode intent)
        Assert.Equal(Ok ExecutionProtocol.launchSchema,ExecutionProtocol.negotiate ["fsgg.orchestration.runner-assignment/1";ExecutionProtocol.launchSchema])
        Assert.Equal(Error "execution-schema-not-supported",ExecutionProtocol.negotiate ["fsgg.orchestration.runner-assignment/1"])
        match ExecutionProtocol.decode first with
        | Error reason -> failwith reason
        | Ok decoded -> Assert.Equal<byte>(first,ExecutionProtocol.encode decoded)
        let json=System.Text.Encoding.UTF8.GetString first
        let changed=("{\"unknown\":true,"+json.Substring(1)) |> System.Text.Encoding.UTF8.GetBytes
        Assert.Equal(Error "execution-launch-shape-refused",ExecutionProtocol.decode changed)

    [<Fact>]
    member _.``intent is durable before provider launch and duplicate reconciles without spawning``() = task {
        let journal=MemoryJournal()
        let provider=FakeProvider("stream",true,true)
        provider.BeforeLaunch <- fun intent -> journal.Events(intent.Key) |> List.exists(function LaunchIntentRecorded _ -> true | _ -> false)
        let coordinator=ExecutionSessionCoordinator(provider,journal,MutableClock(Fixture.now))
        let! first=coordinator.Launch(Fixture.intent 1L,CancellationToken.None)
        let! duplicate=coordinator.Launch(Fixture.intent 1L,CancellationToken.None)
        Assert.True(provider.IntentWasPersisted)
        Assert.Equal(1,provider.Launches)
        Assert.Equal(Running,(Fixture.state first).Observation.Value.Lifecycle)
        Assert.Equal(Running,(Fixture.state duplicate).Observation.Value.Lifecycle) }

    [<Fact>]
    member _.``crash before spawn launches only after confirmed absence with original budget``() = task {
        let journal=MemoryJournal()
        let intent=Fixture.intent 1L
        let! _=(journal :> IExecutionSessionJournal).AppendAttempt(intent.Key.AssignmentId,intent.Key.AttemptId,0L,LaunchIntentRecorded intent,CancellationToken.None)
        let provider=FakeProvider("stream",true,true)
        let clock=MutableClock(Fixture.now.AddMinutes 1.)
        let coordinator=ExecutionSessionCoordinator(provider,journal,clock)
        let! recovered=coordinator.Launch(intent,CancellationToken.None)
        Assert.Equal(1,provider.Launches)
        Assert.Equal(intent.Limits.Deadline,(Fixture.state recovered).Intent.Limits.Deadline) }

    [<Fact>]
    member _.``crash after provider start receipt reconciles without duplicate process``() = task {
        let journal=MemoryJournal()
        let provider=FakeProvider("stream",true,true)
        let coordinator=ExecutionSessionCoordinator(provider,journal,MutableClock(Fixture.now))
        provider.BeforeLaunch <- fun _ -> journal.FailNextAppend();true
        let! failed=coordinator.Launch(Fixture.intent 1L,CancellationToken.None)
        Assert.Equal(SessionNeedsReconciliation "journal-write-conflict",failed)
        let! recovered=coordinator.Launch(Fixture.intent 1L,CancellationToken.None)
        Assert.Equal(1,provider.Launches)
        Assert.Equal(Running,(Fixture.state recovered).Observation.Value.Lifecycle) }

    [<Fact>]
    member _.``generation is fenced on the attempt stream``() = task {
        let journal=MemoryJournal()
        let provider=FakeProvider("stream",true,true)
        let coordinator=ExecutionSessionCoordinator(provider,journal,MutableClock(Fixture.now))
        let! _=coordinator.Launch(Fixture.intent 2L,CancellationToken.None)
        let! stale=coordinator.Launch(Fixture.intent 1L,CancellationToken.None)
        Assert.Equal(SessionRefused "execution-key-conflict",stale)
        Assert.Equal(1,provider.Launches) }

    [<Fact>]
    member _.``deadline cannot renew and unsupported resume stays ambiguous``() = task {
        let journal=MemoryJournal()
        let provider=FakeProvider("aggregate",false,false)
        let intent=Fixture.intent 1L
        let! _=(journal :> IExecutionSessionJournal).AppendAttempt(intent.Key.AssignmentId,intent.Key.AttemptId,0L,LaunchIntentRecorded intent,CancellationToken.None)
        let expired=ExecutionSessionCoordinator(provider,journal,MutableClock(intent.Limits.Deadline.AddSeconds 1.))
        let! refused=expired.Launch(intent,CancellationToken.None)
        Assert.Equal(SessionRefused "execution-budget-expired",refused)
        Assert.Equal(0,provider.Launches) }

    [<Fact>]
    member _.``deadline requests cancellation without claiming observed termination``() = task {
        let journal=MemoryJournal()
        let provider=FakeProvider("stream",true,true)
        let clock=MutableClock(Fixture.now)
        let coordinator=ExecutionSessionCoordinator(provider,journal,clock)
        let! _=coordinator.Launch(Fixture.intent 1L,CancellationToken.None)
        clock.Advance(TimeSpan.FromMinutes 4.)
        let! deadline=coordinator.Observe(Fixture.key 1L,CancellationToken.None)
        let state=Fixture.state deadline
        Assert.True(state.CancelWasRequested)
        Assert.Equal(Running,state.Observation.Value.Lifecycle) }

    [<Fact>]
    member _.``provider differences retain unknown and not applicable instead of zero``() = task {
        let journal=MemoryJournal()
        let provider=FakeProvider("aggregate",false,false)
        let coordinator=ExecutionSessionCoordinator(provider,journal,MutableClock(Fixture.now))
        let! launched=coordinator.Launch(Fixture.intent 1L,CancellationToken.None)
        let observation=(Fixture.state launched).Observation.Value
        Assert.Equal(UsageUnknown "aggregate-does-not-report-usage",observation.Usage.Values["provider-usage"])
        Assert.Equal(CostNotApplicable "aggregate-fixture-has-no-billing",observation.Usage.Cost)
        let! reconnect=coordinator.Launch(Fixture.intent 1L,CancellationToken.None)
        Assert.Equal(SessionNeedsReconciliation "resume-unsupported",reconnect)
        Assert.Equal(1,provider.Launches) }

    [<Fact>]
    member _.``malformed usage and unbounded output are refused``() = task {
        let journal=MemoryJournal()
        let provider=FakeProvider("stream",true,true)
        let intent=Fixture.intent 1L
        let bad=(provider :> IExecutionProvider)
        let coordinator=ExecutionSessionCoordinator(bad,journal,MutableClock(Fixture.now))
        let! launched=coordinator.Launch(intent,CancellationToken.None)
        let state=Fixture.state launched
        let malformed={state.Observation.Value with Usage={Values=Map["tokens",UsageKnown(-1L,"tokens","bad")];Cost=CostKnown(-1M,"USD","bad")}}
        provider.Seed(intent,malformed)
        let! refused=coordinator.Launch(intent,CancellationToken.None)
        Assert.Equal(SessionRefused "provider-observation-bounds-refused",refused) }

    [<Fact>]
    member _.``cancel request is not terminal until provider termination is observed``() = task {
        let journal=MemoryJournal()
        let provider=FakeProvider("stream",true,true)
        let coordinator=ExecutionSessionCoordinator(provider,journal,MutableClock(Fixture.now))
        let! launched=coordinator.Launch(Fixture.intent 1L,CancellationToken.None)
        let! cancelling=coordinator.Cancel(Fixture.key 1L,CancellationToken.None)
        let value=Fixture.state cancelling
        Assert.True(value.CancelWasRequested)
        Assert.Equal(Running,value.Observation.Value.Lifecycle)
        let terminated={value.Observation.Value with Lifecycle=Cancelled;ObservedAt=Fixture.now.AddMinutes 4.}
        provider.Seed(Fixture.intent 1L,terminated)
        let! observed=coordinator.Observe(Fixture.key 1L,CancellationToken.None)
        Assert.Equal(Cancelled,(Fixture.state observed).Observation.Value.Lifecycle)
        let! repeated=coordinator.Cancel(Fixture.key 1L,CancellationToken.None)
        match repeated with
        | SessionDuplicate duplicate -> Assert.Equal(Cancelled,duplicate.Observation.Value.Lifecycle)
        | other -> failwithf "expected duplicate cancellation, got %A" other }

    [<Fact>]
    member _.``thin Akka actor delegates launch to neutral coordinator``() = task {
        let journal=MemoryJournal()
        let provider=FakeProvider("stream",true,true)
        let coordinator=ExecutionSessionCoordinator(provider,journal,MutableClock(Fixture.now))
        use system=ActorSystem.Create("execution-session-test")
        let actor=system.ActorOf(ExecutionSessionActor.Props coordinator)
        let! result=actor.Ask<CoordinationResult>(Launch(Fixture.intent 1L),TimeSpan.FromSeconds 3.)
        Assert.Equal(Running,(Fixture.state result).Observation.Value.Lifecycle)
        do! system.Terminate() }
