namespace FS.GG.Coordination.Orchestration.Execution.Tests

open System
open System.Buffers.Binary
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open FS.GG.Execution.Contracts
open FS.GG.Coordination.Orchestration.Execution

module FiniteCustodyWindowTests =
    let private domain = { Id = Guid.Parse "7c287080-68e9-4846-b369-cb46f37ec374"; Frequency = 1000L }
    let private at ticks = { Domain = domain; Ticks = ticks }
    let private accepted = function Ok value -> value | Error reason -> failwithf "%A" reason
    let private budget (total: float) (reserve: float) =
        Budget.create (at 1000L) (TimeSpan.FromSeconds total) (TimeSpan.FromSeconds reserve) |> accepted

    [<Fact>]
    let ``preparation spends the original clock and retains both reserves`` () =
        let original = budget 300. 15.
        let window = FiniteCustodyWindow.prepare (at 21000L) original |> accepted
        let description = window |> FiniteCustodyWindow.originalBudget |> Budget.describe
        Assert.Equal(1000L, description.StartedAt)
        Assert.Equal(286000L, description.WorkExpiresAt)
        Assert.Equal(301000L, description.TotalExpiresAt)
        Assert.Equal(271000L, FiniteCustodyWindow.normalWorkExpiresAt window)
        Assert.False(FiniteCustodyWindow.normalWorkAllowed (at 271000L) window)

    [<Fact>]
    let ``shorter original grant clips normal work child work and cleanup`` () =
        let window = FiniteCustodyWindow.prepare (at 1000L) (budget 100. 15.) |> accepted
        let description = window |> FiniteCustodyWindow.originalBudget |> Budget.describe
        Assert.Equal(71000L, FiniteCustodyWindow.normalWorkExpiresAt window)
        Assert.Equal(86000L, description.WorkExpiresAt)
        Assert.Equal(101000L, description.TotalExpiresAt)

    [<Fact>]
    let ``larger caller budget never widens finite profile`` () =
        let window = FiniteCustodyWindow.prepare (at 1000L) (budget 600. 15.) |> accepted
        let description = window |> FiniteCustodyWindow.originalBudget |> Budget.describe
        Assert.Equal(301000L, description.TotalExpiresAt)
        Assert.Equal(286000L, description.WorkExpiresAt)
        Assert.Equal(271000L, FiniteCustodyWindow.normalWorkExpiresAt window)

    [<Fact>]
    let ``late preparation cannot renew normal admission`` () =
        Assert.Equal(Error BudgetExpired, FiniteCustodyWindow.prepare (at 271000L) (budget 300. 15.))

    [<Fact>]
    let ``insufficient original reserves refuse before launch`` () =
        Assert.Equal(Error InvalidBudget, FiniteCustodyWindow.prepare (at 1000L) (budget 30. 15.))

    [<Fact>]
    let ``foreign clock domain cannot restore a live grant`` () =
        let foreign = { Domain = { domain with Id = Guid.NewGuid() }; Ticks = 1000L }
        Assert.Equal(Error ClockDomainMismatch, FiniteCustodyWindow.prepare foreign (budget 300. 15.))

    [<Fact>]
    let ``source profile remains unavailable for production`` () =
        Assert.False FiniteCustodyWindow.productionAvailable

    [<Fact>]
    let ``larger original cleanup reserve is retained by composition preparation`` () =
        let original = budget 100. 30.
        let window = FiniteCustodyWindow.prepare (at 1000L) original |> accepted
        let description = window |> FiniteCustodyWindow.originalBudget |> Budget.describe
        Assert.Equal(1000L, description.StartedAt)
        Assert.Equal(56000L, FiniteCustodyWindow.normalWorkExpiresAt window)
        Assert.Equal(71000L, description.WorkExpiresAt)
        Assert.Equal(101000L, description.TotalExpiresAt)

    [<Fact>]
    let ``same clock UUID with changed frequency cannot join original grant`` () =
        let foreign = { Domain = { domain with Frequency = 1001L }; Ticks = 1000L }
        Assert.Equal(Error ClockDomainMismatch, FiniteCustodyWindow.prepare foreign (budget 300. 15.))

    let private require name condition = Assert.True(condition, name)
    let private rejected = function Error _ -> true | Ok _ -> false
    let private framed (body: string) =
        let content = Encoding.ASCII.GetBytes body
        let bytes = Array.zeroCreate<byte> (content.Length+4)
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0,4),uint32 content.Length)
        content.CopyTo(bytes,4)
        ReadOnlyMemory<byte>(bytes)
    let private readback (snapshot: PhaseResultSnapshot) =
        Task.FromResult(Ok { Binding=snapshot.Binding; Ordinal=snapshot.Ordinal; FrameDigest=snapshot.Digest })
    let private original = budget 300. 15.
    open FiniteCustodyComposition

    let private descriptor =
        { Mode = "inert"
          Identity = { InvocationId = Guid.Parse "a4305dba-ce1b-4938-8804-b8d1a7189fb8"
                       Execution = None; LaunchOrdinal = 0UL
                       SourceDigest = String.replicate 64 "a"
                       ProfileId = FiniteCustodyWindow.profileId; ProfileDigest = String.replicate 64 "b" }
          OriginalOperation = "utel-rel-21"; OriginalGeneration = 2L
          DomainProfile = "utel-rel-21/original-tag-authentication-only/2"
          Process = { Platform = "inert"; ScopeId = "fixture"; Pid = 1; BirthToken = "abc" }
          ChildRevision = String.replicate 40 "c"; RunnerRevision = String.replicate 40 "d"
          RunnerClosure = String.replicate 64 "e"; ContractDigest = String.replicate 64 "f"
          RuntimeClosure = String.replicate 64 "0"
          WorkflowRepository = 1269292704UL; WorkflowPath = ".github/workflows/release-successor-publish.yml"
          WorkflowRevision = String.replicate 40 "1"; WorkflowRun = 0UL; WorkflowAttempt = 0u
          App = 4882140UL; Installation = 123UL
          ChannelRoles = ["parent-start-and-ack"; "child-phase-result"]
          ResultDestination = "inert-result-slot" }

    let private bindingText =
        sprintf """{"app":{"id":4882140,"installation":123},"clock":{"domain":"%O","frequency":"1000","normal":"271000","start":"1000","total":"301000","work":"286000"},"invocation":"%O","mode":"inert","operation":"utel-rel-21","originalGeneration":2,"process":{"birth":"abc","pid":1},"profile":"%s","source":{"childClosure":"%s","childRevision":"%s","contract":"%s","runnerClosure":"%s","runnerRevision":"%s"},"workflow":{"attempt":0,"path":".github/workflows/release-successor-publish.yml","repositoryId":1269292704,"revision":"%s","run":0}}"""
            domain.Id descriptor.Identity.InvocationId descriptor.DomainProfile descriptor.Identity.SourceDigest
            descriptor.ChildRevision descriptor.ContractDigest descriptor.RunnerClosure descriptor.RunnerRevision descriptor.WorkflowRevision
    let private wireBinding = ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes bindingText)
    let private wireDigest = PhaseResultProtocol.bindingDigest wireBinding |> accepted
    let private child ordinal kind payload =
        framed (sprintf "{\"b\":\"%s\",\"k\":\"%s\",\"n\":%d,\"p\":%s,\"v\":\"%s\"}" wireDigest kind ordinal payload PhaseResultProtocol.identifier)
    let private wait (work: Task<'T>) = work.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()
    let private waitUntil predicate = task {
        let limit = DateTime.UtcNow + TimeSpan.FromSeconds 2.
        while not (predicate ()) && DateTime.UtcNow < limit do do! Task.Delay 1
        require "composed-fixture-progress" (predicate ()) }

    type private Fixture(?cancellation: CancellationToken) =
        let sessions = ResizeArray<Session>()
        let runs = ResizeArray<Task>()
        let cleanup = ResizeArray<unit -> unit>()
        let mutable ticks = 1000L
        let mutable clockDomain = domain
        let mutable retirement = fun () -> Task.FromResult true
        let mutable validation = fun (_: PhaseResultSnapshot) -> Ok ()
        let mutable retirements = 0
        let mutable reads = 0
        let input = Channel.CreateUnbounded<ReadOnlyMemory<byte>>()
        let writes = ResizeArray<byte array>()
        let termination = TaskCompletionSource<bool option>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable persist = readback
        let mutable writeCount = fun (bytes: ReadOnlyMemory<byte>) -> bytes.Length
        let clock = { new IMonotonicClock with member _.Now() = { Domain=clockDomain; Ticks=Interlocked.Read(&ticks) } }
        let ports =
            { Read = fun () -> task {
                  Interlocked.Increment(&reads) |> ignore
                  let! available = input.Reader.WaitToReadAsync().AsTask()
                  if available then return! input.Reader.ReadAsync().AsTask()
                  else return ReadOnlyMemory<byte>.Empty }
              Write = fun bytes -> task {
                  lock writes (fun () -> writes.Add(bytes.ToArray()))
                  return writeCount bytes }
              Validate = fun snapshot -> validation snapshot
              PersistAndReadback = fun snapshot -> persist snapshot
              RequestRetirement = fun () ->
                  Interlocked.Increment(&retirements) |> ignore
                  retirement ()
              Termination = termination.Task }
        member _.Prepare(admission, expected, actual, ?binding: ReadOnlyMemory<byte>) =
            let result = Session.Prepare(admission, expected, actual, clock, original, defaultArg binding wireBinding, ports, defaultArg cancellation CancellationToken.None)
            match result with Ok session -> sessions.Add session | _ -> ()
            result
        member _.Run(session: Session) =
            let work = session.Run()
            runs.Add(work :> Task)
            work
        member _.RegisterCleanup action = cleanup.Add action
        member _.ClockDomain with set value = clockDomain <- value
        member _.Validation with set callback = validation <- callback
        member _.Retirement with set callback = retirement <- callback
        member _.Send bytes = require "composed-fixture-enqueue" (input.Writer.TryWrite bytes)
        member _.Eof() = input.Writer.TryComplete() |> ignore
        member _.FailRead() = input.Writer.TryComplete(InvalidOperationException "inert-read-failure") |> ignore
        member _.Terminate() = termination.TrySetResult(Some true) |> ignore
        member _.Expire() = Interlocked.Exchange(&ticks,301000L) |> ignore
        member _.NormalExpires() = Interlocked.Exchange(&ticks,271000L) |> ignore
        member _.WorkExpires() = Interlocked.Exchange(&ticks,286000L) |> ignore
        member _.Persist with set callback = persist <- callback
        member _.WriteCount with set callback = writeCount <- callback
        member _.Reads = Interlocked.CompareExchange(&reads,0,0)
        member _.Writes = lock writes (fun () -> writes.Count)
        member _.Retirements = Interlocked.CompareExchange(&retirements,0,0)

        interface IDisposable with
            member _.Dispose() =
                // Release every fixture-owned blocking port even if an assertion fails.
                for action in cleanup do action ()
                input.Writer.TryComplete() |> ignore
                termination.TrySetResult(Some true) |> ignore
                Interlocked.Exchange(&ticks,301000L) |> ignore
                Task.WhenAll(runs.ToArray()).WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()
                let retained = sessions |> Seq.collect _.RetainedTasks |> Seq.toArray
                Task.WhenAll(retained).WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()

    [<Fact>]
    let ``descriptor admission refuses native drift and replay`` () =
        use admissionFixture = new Fixture()
        let admission = Admission()
        let nativeDescriptor = { descriptor with Mode="native" }
        require "composed-native-refused-before-channel-work"
            (admissionFixture.Prepare(admission,nativeDescriptor,nativeDescriptor) |> rejected)
        require "composed-wrong-source-refused-before-channel-work"
            (admissionFixture.Prepare(admission,descriptor,{descriptor with Identity={descriptor.Identity with SourceDigest=String.replicate 64 "0"}}) |> rejected)
        let unsupportedRunner = { descriptor with Identity={descriptor.Identity with ProfileId="unsupported"} }
        require "composed-unsupported-runner-refused"
            (admissionFixture.Prepare(admission,unsupportedRunner,unsupportedRunner) |> rejected)
        require "composed-no-admission-io" (admissionFixture.Writes=0 && admissionFixture.Retirements=0)
        admissionFixture.Prepare(admission,descriptor,descriptor) |> accepted |> ignore
        require "composed-repeated-descriptor-refused" (admissionFixture.Prepare(admission,descriptor,descriptor) |> rejected)

    [<Fact>]
    let ``real composed inert protocol completes once`` () =
        use successful = new Fixture()
        let composed = successful.Prepare(Admission(),descriptor,descriptor) |> accepted
        let running = successful.Run composed
        waitUntil (fun () -> successful.Writes=1) |> fun work -> work.GetAwaiter().GetResult()
        for ordinal,kind,payload,needsAck in
            [1,"ready","{\"binding\":"+bindingText+"}",true
             2,"issuance-possible","{}",true; 3,"token-owned","{}",false
             4,"revocation-possible","{}",true; 5,"revoked","{}",false; 6,"terminal","{}",false] do
            successful.Send(child ordinal kind payload)
            waitUntil (fun () -> composed.Accepted.Length >= ordinal && (not needsAck || composed.ConfirmedAckOrdinal=ordinal))
            |> fun work -> work.GetAwaiter().GetResult()
        successful.Eof()
        successful.Terminate()
        let finished = wait running |> accepted
        require "composed-real-session-completes" (finished.ProtocolComplete && finished.LastThreadTerminated && not finished.SignalRequested)
        require "composed-run-cannot-repeat" (wait (composed.Run()) |> rejected)

    [<Fact>]
    let ``blocked retention expiry preserves custody and rejects late ACK`` () =
        use blockedFixture = new Fixture()
        let blockedRetention = TaskCompletionSource<Result<PhaseResultReadback,string>>(TaskCreationOptions.RunContinuationsAsynchronously)
        blockedFixture.RegisterCleanup(fun () -> blockedRetention.TrySetResult(Error "fixture-stop") |> ignore)
        blockedFixture.Persist <- fun _ -> blockedRetention.Task
        let blockedSession = blockedFixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let blockedRun = blockedFixture.Run blockedSession
        waitUntil (fun () -> blockedFixture.Writes=1) |> fun work -> work.GetAwaiter().GetResult()
        blockedFixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
        waitUntil (fun () -> blockedSession.RetentionTasks.Length=1) |> fun work -> work.GetAwaiter().GetResult()
        blockedFixture.WorkExpires()
        waitUntil (fun () -> blockedFixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        require "composed-signal-does-not-prove-termination" (blockedSession.LocalTasks=Outstanding)
        blockedFixture.Expire()
        let stopped = wait blockedRun |> accepted
        require "composed-unknown-retains-outstanding-work" (stopped.SignalRequested && not stopped.LastThreadTerminated && blockedSession.LocalTasks=Outstanding)
        let blockedSnapshot = blockedSession.Accepted |> List.exactlyOne
        blockedRetention.SetResult(Ok { Binding=blockedSnapshot.Binding; Ordinal=blockedSnapshot.Ordinal; FrameDigest=blockedSnapshot.Digest })
        blockedFixture.Eof()
        waitUntil (fun () -> blockedSession.LocalTasks=Settled) |> fun work -> work.GetAwaiter().GetResult()
        require "composed-late-readback-does-not-send-ack" (blockedFixture.Writes=1 && blockedSession.ConfirmedAckOrdinal=0)

    [<Fact>]
    let ``partial composed ACK retains delivery and unknown termination`` () =
        use partialFixture = new Fixture()
        partialFixture.WriteCount <- fun bytes -> if partialFixture.Writes=1 then bytes.Length else bytes.Length-1
        let partialSession = partialFixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let partialRun = partialFixture.Run partialSession
        waitUntil (fun () -> partialFixture.Writes=1) |> fun work -> work.GetAwaiter().GetResult()
        partialFixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
        waitUntil (fun () -> partialFixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        require "composed-partial-ack-retains-possible-delivery" (partialSession.ConfirmedAckOrdinal=0 && partialSession.Accepted.Length=1)
        partialFixture.Eof(); partialFixture.Expire()
        let partialResult = wait partialRun |> accepted
        require "composed-partial-write-cannot-qualify" (not partialResult.ProtocolComplete && not partialResult.LastThreadTerminated)


    let private cleanupFixture () =
        let evidence: EvidenceRef = { Kind="inert"; Reference="fixture"; Digest=None }
        let stamp = { At=Some(at 1000L); Scope="fixture"; Generation=Some descriptor.Process; Provenance=evidence }
        let owner = { OwnerId="inert-owner"; Evidence=evidence }
        let existing =
            { Disposition=Complete; RequestedActions=[]
              LeaderTermination=Known(true,stamp); WorkloadEmpty=Known(true,stamp)
              OwnedChildrenReaped=Known(true,stamp); LocalTasks=Known(Settled,stamp)
              Unresolved=[]; OmittedUnresolved=0L; Custody=Released stamp }
        existing,stamp,owner

    [<Fact>]
    let ``cleanup cannot release before one-shot supervision is sealed`` () =
        use fixture = new Fixture()
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let existing,stamp,owner = cleanupFixture ()
        fixture.Terminate()
        let joined = session.JoinCleanup(existing,stamp,owner)
        require "prepared-session-retains-custody" (joined.Custody=RetainedByOwner owner && joined.Disposition=Incomplete)
        require "unstarted-supervision-is-outstanding" (match joined.LocalTasks with Known(Outstanding,_) -> true | _ -> false)

    [<Fact>]
    let ``binding source extra fields and clock mismatches refuse before I O`` () =
        use fixture = new Fixture()
        let wrongSource = { descriptor with Identity={descriptor.Identity with SourceDigest=String.replicate 64 "0"} }
        require "independent-wire-source-must-join" (fixture.Prepare(Admission(),wrongSource,wrongSource) |> rejected)
        // Keep lexical key order so the closed-key check, rather than canonical
        // framing, must reject the otherwise valid extra field.
        let extraText = bindingText.Replace("\"invocation\":", "\"extra\":true,\"invocation\":")
        let extra = ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes extraText)
        require "closed-wire-fields" (fixture.Prepare(Admission(),descriptor,descriptor,binding=extra) |> rejected)
        fixture.ClockDomain <- {domain with Id=Guid.NewGuid()}
        require "original-clock-domain-must-join" (fixture.Prepare(Admission(),descriptor,descriptor) |> rejected)
        require "binding-refusals-have-no-channel-work" (fixture.Writes=0 && fixture.Retirements=0)

    [<Fact>]
    let ``admission owns binding bytes before later caller mutation`` () =
        use fixture = new Fixture()
        let mutableBytes = Encoding.ASCII.GetBytes bindingText
        let session = fixture.Prepare(Admission(),descriptor,descriptor,binding=ReadOnlyMemory<byte>(mutableBytes)) |> accepted
        Array.fill mutableBytes 0 mutableBytes.Length 0uy
        let running = fixture.Run session
        waitUntil (fun () -> fixture.Writes=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
        waitUntil (fun () -> session.ConfirmedAckOrdinal=1) |> fun work -> work.GetAwaiter().GetResult()
        require "owned-binding-survives-caller-mutation" (session.Accepted.Length=1)
        fixture.Eof(); fixture.Expire()
        wait running |> ignore

    [<Fact>]
    let ``cancellation before Run retires held runner without starting channel`` () =
        use cancellation = new CancellationTokenSource()
        use fixture = new Fixture(cancellation=cancellation.Token)
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        cancellation.Cancel()
        let running = fixture.Run session
        waitUntil (fun () -> fixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Expire()
        let outcome = wait running |> accepted
        require "cancelled-held-identity-is-retired" (outcome.StopReason=Some CallerCancellation && outcome.SignalRequested && not outcome.LastThreadTerminated)
        require "prestart-cancellation-has-no-channel-write" (fixture.Writes=0)

    [<Fact>]
    let ``blocked validator returning after expiry cannot start retention`` () =
        use fixture = new Fixture()
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable persisted = 0
        fixture.RegisterCleanup(fun () -> release.TrySetResult() |> ignore)
        fixture.Validation <- fun _ ->
            entered.TrySetResult() |> ignore
            release.Task.GetAwaiter().GetResult()
            Ok ()
        fixture.Persist <- fun snapshot -> Interlocked.Increment(&persisted) |> ignore; readback snapshot
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let running = fixture.Run session
        waitUntil (fun () -> fixture.Writes=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
        wait entered.Task
        fixture.WorkExpires()
        waitUntil (fun () -> fixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Expire()
        wait running |> ignore
        release.SetResult()
        fixture.Eof()
        waitUntil (fun () -> session.LocalTasks=Settled) |> fun work -> work.GetAwaiter().GetResult()
        require "late-validator-cannot-admit-phase-or-sink" (session.Accepted.IsEmpty && persisted=0 && fixture.Writes=1)

    [<Fact>]
    let ``retirement failure preserves original callback cause`` () =
        use fixture = new Fixture()
        fixture.Validation <- fun _ -> Error "inert-domain-failure"
        fixture.Retirement <- fun () -> Task.FromException<bool>(InvalidOperationException "inert-retirement-failure")
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let running = fixture.Run session
        waitUntil (fun () -> fixture.Writes=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
        waitUntil (fun () -> fixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Eof(); fixture.Expire()
        let outcome = wait running |> accepted
        require "first-sink-failure-survives-cleanup-and-deadline" (outcome.StopReason=Some SinkFailure && outcome.SignalRequested && not outcome.SignalAccepted)

    [<Fact>]
    let ``read failure requests retirement and preserves unknown custody`` () =
        use fixture = new Fixture()
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let running = fixture.Run session
        waitUntil (fun () -> fixture.Writes=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.FailRead()
        waitUntil (fun () -> fixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Expire()
        let outcome = wait running |> accepted
        let existing,stamp,owner = cleanupFixture ()
        let joined = session.JoinCleanup(existing,stamp,owner)
        require "failed-read-retains-custody" (outcome.StopReason=Some SinkFailure && not outcome.LastThreadTerminated && joined.Custody=RetainedByOwner owner)

    [<Fact>]
    let ``early successor ingress while retention blocks never gets an ACK`` () =
        use fixture = new Fixture()
        let pending = TaskCompletionSource<Result<PhaseResultReadback,string>>(TaskCreationOptions.RunContinuationsAsynchronously)
        fixture.RegisterCleanup(fun () -> pending.TrySetResult(Error "fixture-stop") |> ignore)
        fixture.Persist <- fun _ -> pending.Task
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let running = fixture.Run session
        waitUntil (fun () -> fixture.Writes=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
        waitUntil (fun () -> session.RetentionTasks.Length=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Send(child 2 "issuance-possible" "{}")
        waitUntil (fun () -> fixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Expire()
        let outcome = wait running |> accepted
        require "blocked-retention-remains-held-after-early-ingress" (session.LocalTasks=Outstanding && not outcome.ProtocolComplete)
        pending.TrySetResult(Error "inert-stop") |> ignore
        fixture.Eof()
        waitUntil (fun () -> session.LocalTasks=Settled) |> fun work -> work.GetAwaiter().GetResult()
        require "no-ack-after-early-ingress" (fixture.Writes=1 && session.ConfirmedAckOrdinal=0)

    [<Fact>]
    let ``settled composition preserves earlier unknown cleanup tasks`` () =
        use fixture = new Fixture()
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        fixture.Terminate()
        fixture.Run session |> wait |> accepted |> ignore
        let existing,stamp,owner = cleanupFixture ()
        let diagnostic = { Code="earlier-task-unknown"; Message="inert"; Evidence=None }
        let existing = { existing with LocalTasks=Unknown diagnostic }
        let joined = session.JoinCleanup(existing,stamp,owner)
        require "earlier-unknown-cannot-become-settled" (joined.LocalTasks=existing.LocalTasks && joined.Custody=RetainedByOwner owner)

    [<Fact>]
    let ``blocked retirement remains retained after confirmed process termination`` () =
        use cancellation = new CancellationTokenSource()
        use fixture = new Fixture(cancellation=cancellation.Token)
        let pending = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        fixture.RegisterCleanup(fun () -> pending.TrySetResult false |> ignore)
        fixture.Retirement <- fun () -> pending.Task
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        cancellation.Cancel()
        let running = fixture.Run session
        waitUntil (fun () -> fixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Terminate(); fixture.Expire()
        let outcome = wait running |> accepted
        let existing,stamp,owner = cleanupFixture ()
        let joined = session.JoinCleanup(existing,stamp,owner)
        require "retirement-callback-outlives-process" (outcome.LastThreadTerminated && not outcome.SignalAccepted && session.LocalTasks=Outstanding)
        require "callback-obligation-retains-owner" (joined.Custody=RetainedByOwner owner && joined.Disposition=Incomplete)
        pending.TrySetResult true |> ignore
        waitUntil (fun () -> session.LocalTasks=Settled) |> fun work -> work.GetAwaiter().GetResult()

    [<Fact>]
    let ``wrong durable readback prevents composed ACK`` () =
        use fixture = new Fixture()
        fixture.Persist <- fun snapshot -> Task.FromResult(Ok { Binding=snapshot.Binding; Ordinal=snapshot.Ordinal; FrameDigest=String.replicate 64 "0" })
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let running = fixture.Run session
        waitUntil (fun () -> fixture.Writes=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
        waitUntil (fun () -> fixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Eof(); fixture.Expire()
        let outcome = wait running |> accepted
        require "mismatched-sink-cannot-enable-ack" (fixture.Writes=1 && session.ConfirmedAckOrdinal=0 && not outcome.ProtocolComplete)

    [<Theory>]
    [<InlineData(1, "cancellation")>]
    [<InlineData(1, "normal-expiry")>]
    [<InlineData(1, "work-expiry")>]
    [<InlineData(2, "cancellation")>]
    [<InlineData(2, "normal-expiry")>]
    [<InlineData(2, "work-expiry")>]
    let ``successful write classifies stop source before next supervisor poll`` (writeOrdinal: int, cause: string) =
        use cancellation = new CancellationTokenSource()
        use fixture = new Fixture(cancellation=cancellation.Token)
        let completed = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        fixture.WriteCount <- fun bytes ->
            if fixture.Writes=writeOrdinal then
                match cause with
                | "cancellation" -> cancellation.Cancel()
                | "normal-expiry" -> fixture.NormalExpires()
                | "work-expiry" -> fixture.WorkExpires()
                | _ -> failwith "unsupported-inert-cause"
                completed.TrySetResult() |> ignore
            // The transport succeeds fully; classification must describe the
            // cancellation/deadline observed after this callback, not sink failure.
            bytes.Length
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let running = fixture.Run session
        if writeOrdinal=2 then
            waitUntil (fun () -> fixture.Writes=1) |> fun work -> work.GetAwaiter().GetResult()
            fixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
        wait completed.Task
        waitUntil (fun () -> fixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Eof(); fixture.Expire()
        let outcome = wait running |> accepted
        let expected = if cause="cancellation" then CallerCancellation else WorkDeadline
        require "successful-write-retains-actual-stop-source" (outcome.StopReason=Some expected)
        require "stopped-successful-write-never-opens-next-phase" (fixture.Writes=writeOrdinal && session.ConfirmedAckOrdinal=0 && not outcome.ProtocolComplete)

    [<Theory>]
    [<InlineData("cancellation")>]
    [<InlineData("normal-expiry")>]
    [<InlineData("work-expiry")>]
    let ``successful retention classifies stop source before taking ACK`` (cause: string) =
        use cancellation = new CancellationTokenSource()
        use fixture = new Fixture(cancellation=cancellation.Token)
        let completed = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        fixture.Persist <- fun snapshot ->
            match cause with
            | "cancellation" -> cancellation.Cancel()
            | "normal-expiry" -> fixture.NormalExpires()
            | "work-expiry" -> fixture.WorkExpires()
            | _ -> failwith "unsupported-inert-cause"
            completed.TrySetResult() |> ignore
            readback snapshot
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let running = fixture.Run session
        waitUntil (fun () -> fixture.Writes=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
        wait completed.Task
        waitUntil (fun () -> fixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Eof(); fixture.Expire()
        let outcome = wait running |> accepted
        let expected = if cause="cancellation" then CallerCancellation else WorkDeadline
        require "successful-retention-retains-actual-stop-source" (outcome.StopReason=Some expected)
        require "stopped-retention-never-takes-ack" (fixture.Writes=1 && session.ConfirmedAckOrdinal=0 && not outcome.ProtocolComplete)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``queued terminal cannot start callbacks after original work or total`` (afterTotal: bool) =
        use fixture = new Fixture()
        let readyEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let readyPending = TaskCompletionSource<Result<PhaseResultReadback,string>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let validated = ResizeArray<string>()
        let persisted = ResizeArray<string>()
        fixture.RegisterCleanup(fun () -> readyPending.TrySetResult(Error "fixture-stop") |> ignore)
        fixture.Validation <- fun snapshot -> lock validated (fun () -> validated.Add snapshot.Kind); Ok ()
        fixture.Persist <- fun snapshot ->
            lock persisted (fun () -> persisted.Add snapshot.Kind)
            if snapshot.Kind="ready" then
                readyEntered.TrySetResult() |> ignore
                readyPending.Task
            else readback snapshot
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let running = fixture.Run session
        waitUntil (fun () -> fixture.Reads=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
        wait readyEntered.Task
        fixture.Send(child 2 "terminal" "{}")
        // The next read proves terminal ingress was decoded/queued while the
        // earlier real persistence callback was still blocked and work was live.
        waitUntil (fun () -> fixture.Reads=3) |> fun work -> work.GetAwaiter().GetResult()
        fixture.WorkExpires()
        waitUntil (fun () -> fixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        if afterTotal then
            fixture.Expire()
            wait running |> ignore
        let readySnapshot = session.Accepted |> List.exactlyOne
        readyPending.TrySetResult(Ok { Binding=readySnapshot.Binding; Ordinal=readySnapshot.Ordinal; FrameDigest=readySnapshot.Digest }) |> ignore
        fixture.Eof()
        waitUntil (fun () -> session.RetainedTasks |> Array.forall _.IsCompleted) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Expire()
        let outcome = wait running |> accepted
        require "queued-terminal-starts-no-new-validator" (lock validated (fun () -> List.ofSeq validated=["ready"]))
        require "queued-terminal-starts-no-new-persistence" (lock persisted (fun () -> List.ofSeq persisted=["ready"]))
        require "late-ready-settlement-never-restores-admission" (session.Accepted.Length=1 && fixture.Writes=1 && session.ConfirmedAckOrdinal=0)
        require "queued-terminal-preserves-original-work-cause" (outcome.StopReason=Some WorkDeadline && not outcome.ProtocolComplete)

    [<Fact>]
    let ``pre ACK terminal may retain failure evidence before work expiry`` () =
        use fixture = new Fixture()
        let readyEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let readyPending = TaskCompletionSource<Result<PhaseResultReadback,string>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let terminalRetained = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        fixture.RegisterCleanup(fun () -> readyPending.TrySetResult(Error "fixture-stop") |> ignore)
        fixture.Persist <- fun snapshot ->
            if snapshot.Kind="ready" then
                readyEntered.TrySetResult() |> ignore
                readyPending.Task
            else
                terminalRetained.TrySetResult() |> ignore
                readback snapshot
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let running = fixture.Run session
        waitUntil (fun () -> fixture.Reads=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
        wait readyEntered.Task
        fixture.Send(child 2 "terminal" "{}")
        waitUntil (fun () -> fixture.Reads=3) |> fun work -> work.GetAwaiter().GetResult()
        let readySnapshot = session.Accepted |> List.exactlyOne
        readyPending.TrySetResult(Ok { Binding=readySnapshot.Binding; Ordinal=readySnapshot.Ordinal; FrameDigest=readySnapshot.Digest }) |> ignore
        wait terminalRetained.Task
        fixture.Eof(); fixture.Terminate()
        let outcome = wait running |> accepted
        require "in-time-failure-terminal-is-retained" (session.Accepted.Length=2 && session.Accepted[1].Kind="terminal")
        require "failure-terminal-does-not-revive-pending-ACK" (fixture.Writes=1 && session.ConfirmedAckOrdinal=0 && not outcome.ProtocolComplete)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``late read observes bytes without decoding after work or total`` (afterTotal: bool) =
        use fixture = new Fixture()
        let mutable validations = 0
        let mutable persistences = 0
        fixture.Validation <- fun _ -> Interlocked.Increment(&validations) |> ignore; Ok ()
        fixture.Persist <- fun snapshot -> Interlocked.Increment(&persistences) |> ignore; readback snapshot
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let running = fixture.Run session
        waitUntil (fun () -> fixture.Reads=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.WorkExpires()
        waitUntil (fun () -> fixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        if afterTotal then
            fixture.Expire()
            wait running |> ignore
        let lateFrame = child 1 "ready" ("{\"binding\":"+bindingText+"}")
        fixture.Send lateFrame
        waitUntil (fun () -> session.UnadmittedIngressBytes=int64 lateFrame.Length) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Eof(); fixture.Expire()
        let outcome = wait running |> accepted
        waitUntil (fun () -> session.LocalTasks=Settled) |> fun work -> work.GetAwaiter().GetResult()
        require "late-transport-observation-is-not-a-new-phase" (session.Accepted.IsEmpty && session.RetentionTasks.IsEmpty && validations=0 && persistences=0)
        require "late-read-cannot-open-an-ACK" (fixture.Writes=1 && outcome.StopReason=Some WorkDeadline && not outcome.ProtocolComplete)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``terminal validation settling after cutoff cannot begin persistence`` (afterTotal: bool) =
        use fixture = new Fixture()
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable terminalPersistences = 0
        fixture.RegisterCleanup(fun () -> release.TrySetResult() |> ignore)
        fixture.Validation <- fun snapshot ->
            if snapshot.Kind="terminal" then
                entered.TrySetResult() |> ignore
                release.Task.GetAwaiter().GetResult()
            Ok ()
        fixture.Persist <- fun snapshot ->
            if snapshot.Kind="terminal" then Interlocked.Increment(&terminalPersistences) |> ignore
            readback snapshot
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let running = fixture.Run session
        waitUntil (fun () -> fixture.Reads=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
        waitUntil (fun () -> session.ConfirmedAckOrdinal=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Send(child 2 "terminal" "{}")
        wait entered.Task
        fixture.WorkExpires()
        waitUntil (fun () -> fixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        if afterTotal then
            fixture.Expire()
            wait running |> ignore
        release.TrySetResult() |> ignore
        fixture.Eof()
        waitUntil (fun () -> session.RetainedTasks |> Array.forall _.IsCompleted) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Expire()
        let outcome = wait running |> accepted
        require "late-terminal-validation-gets-no-successor" (terminalPersistences=0 && session.Accepted.Length=1 && fixture.Writes=2)
        require "late-terminal-validation-keeps-first-cause" (outcome.StopReason=Some WorkDeadline && not outcome.ProtocolComplete)

    [<Fact>]
    let ``terminal persistence already in flight stays owned through late settlement`` () =
        use fixture = new Fixture()
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let pending = TaskCompletionSource<Result<PhaseResultReadback,string>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable terminalPersistences = 0
        fixture.RegisterCleanup(fun () -> pending.TrySetResult(Error "fixture-stop") |> ignore)
        fixture.Persist <- fun snapshot ->
            if snapshot.Kind="terminal" then
                Interlocked.Increment(&terminalPersistences) |> ignore
                entered.TrySetResult() |> ignore
                pending.Task
            else readback snapshot
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let running = fixture.Run session
        waitUntil (fun () -> fixture.Reads=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
        waitUntil (fun () -> session.ConfirmedAckOrdinal=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Send(child 2 "terminal" "{}")
        wait entered.Task
        fixture.WorkExpires()
        waitUntil (fun () -> fixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Expire()
        let outcome = wait running |> accepted
        require "already-started-terminal-writer-is-retained" (session.LocalTasks=Outstanding && terminalPersistences=1)
        let terminal = session.Accepted[1]
        pending.TrySetResult(Ok { Binding=terminal.Binding; Ordinal=terminal.Ordinal; FrameDigest=terminal.Digest }) |> ignore
        fixture.Eof()
        waitUntil (fun () -> session.LocalTasks=Settled) |> fun work -> work.GetAwaiter().GetResult()
        require "late-settlement-neither-repeats-nor-ACKs" (terminalPersistences=1 && fixture.Writes=2 && session.ConfirmedAckOrdinal=1)
        require "late-terminal-writer-does-not-qualify" (outcome.StopReason=Some WorkDeadline && not outcome.ProtocolComplete)

    [<Theory>]
    [<InlineData("failed-readback")>]
    [<InlineData("partial-ACK")>]
    let ``queued terminal cannot reopen failed local callback or channel`` (failure: string) =
        use fixture = new Fixture()
        let readyEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let readyPending = TaskCompletionSource<Result<PhaseResultReadback,string>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let validated = ResizeArray<string>()
        let persisted = ResizeArray<string>()
        fixture.RegisterCleanup(fun () -> readyPending.TrySetResult(Error "fixture-stop") |> ignore)
        fixture.Validation <- fun snapshot -> lock validated (fun () -> validated.Add snapshot.Kind); Ok ()
        fixture.Persist <- fun snapshot ->
            lock persisted (fun () -> persisted.Add snapshot.Kind)
            if snapshot.Kind="ready" && failure="failed-readback" then
                readyEntered.TrySetResult() |> ignore
                readyPending.Task
            else readback snapshot
        fixture.WriteCount <- fun bytes ->
            if fixture.Writes=2 && failure="partial-ACK" then
                // Queue terminal while this actual ACK callback is in flight;
                // its subsequently partial result must close new callback entry.
                fixture.Send(child 2 "terminal" "{}")
                waitUntil (fun () -> fixture.Reads=3) |> fun work -> work.GetAwaiter().GetResult()
                bytes.Length-1
            else bytes.Length
        let session = fixture.Prepare(Admission(),descriptor,descriptor) |> accepted
        let running = fixture.Run session
        waitUntil (fun () -> fixture.Reads=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
        if failure="failed-readback" then
            wait readyEntered.Task
            fixture.Send(child 2 "terminal" "{}")
            waitUntil (fun () -> fixture.Reads=3) |> fun work -> work.GetAwaiter().GetResult()
            readyPending.TrySetResult(Error "inert-uncertain-readback") |> ignore
        waitUntil (fun () -> fixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
        fixture.Eof(); fixture.Terminate()
        let outcome = wait running |> accepted
        require "failed-local-operation-closes-terminal-validator" (lock validated (fun () -> List.ofSeq validated=["ready"]))
        require "failed-local-operation-closes-terminal-sink" (lock persisted (fun () -> List.ofSeq persisted=["ready"]))
        require "queued-terminal-never-restores-failed-local-session" (session.Accepted.Length=1 && session.ConfirmedAckOrdinal=0 && outcome.StopReason=Some SinkFailure && not outcome.ProtocolComplete)

    let private nativePlan () : FiniteCustodyNativeInertPlan =
        { Schema = FiniteCustodyNativeInertPlan.schema
          Mode = "native-inert-source"
          Abi = FiniteCustodyNativeInertPlan.abi
          Binding = descriptor
          OriginalWindow = FiniteCustodyWindow.prepare (at 1000L) original |> accepted
          ChildFds = FiniteCustodyNativeInertPlan.childFds
          CredentialSourceKind = "inert-fixed-sentinel/1"
          CapabilityId = Guid.Parse "afbc9c2c-7e49-4732-b610-c536a4c5aec5"
          ProviderSourceClosure = None
          MaximumCredentialBodyBytes = 128
          ExecutableRecipe = "assembly-static-inert-child/1"
          ExecutableSha256 = None
          FilterSha256 = None
          MaximumProtocolFrameBytes = PhaseResultProtocol.maximumFrameBytes
          MaximumDiagnosticBytes = 1024
          ProductionAvailable = false }

    let private nativeReason owner expected actual =
        match FiniteCustodySessionAdapter.PrepareNativeInert(owner, expected, actual) with
        | Error reason -> reason
        | Ok _ -> failwith "native preparation must remain unavailable"

    [<Fact>]
    let ``native inert plan accepts only matching data without claiming native readiness`` () =
        let plan = nativePlan ()
        Assert.Equal(Ok (), FiniteCustodyNativeInertPlan.validate plan plan)
        Assert.False FiniteCustodyNativeInertPlan.productionAvailable
        Assert.Equal("finite-custody-native-inert-unavailable", nativeReason (FiniteCustodyNativeInertAdmission()) plan plan)

    [<Fact>]
    let ``native inert plan rejects malformed fixed descriptor roles even when expected repeats them`` () =
        let plan = nativePlan ()
        let first = List.head plan.ChildFds
        let rest = List.tail plan.ChildFds
        let mutations =
            [ List.rev plan.ChildFds
              { first with Number = 4 } :: rest
              { first with Number = 1 } :: rest
              { first with Role = "owner-response" } :: rest
              { first with Direction = "write" } :: rest
              { first with Kind = "pathname" } :: rest
              plan.ChildFds @ [ { first with Number = 4 } ]
              rest ]
        for fds in mutations do
            let bad = { plan with ChildFds = fds }
            Assert.True(Result.isError (FiniteCustodyNativeInertPlan.validate bad bad))

    [<Fact>]
    let ``native inert plan rejects credential and executable escape discriminators`` () =
        let plan = nativePlan ()
        let mutations =
            [ { plan with Schema = "other" }
              { plan with Mode = "production" }
              { plan with Abi = "other" }
              { plan with CredentialSourceKind = "app-key" }
              { plan with ExecutableRecipe = "/arbitrary/program" }
              { plan with MaximumCredentialBodyBytes = 129 }
              { plan with MaximumProtocolFrameBytes = PhaseResultProtocol.maximumFrameBytes + 1 }
              { plan with MaximumDiagnosticBytes = 0 }
              { plan with MaximumDiagnosticBytes = PhaseResultProtocol.maximumFrameBytes + 1 }
              { plan with CapabilityId = Guid.Empty }
              { plan with ProductionAvailable = true } ]
        for bad in mutations do
            Assert.True(Result.isError (FiniteCustodyNativeInertPlan.validate bad bad))

    [<Fact>]
    let ``native inert plan preserves synthetic workflow and existing inert process rules`` () =
        let plan = nativePlan ()
        let mutations =
            [ { descriptor with WorkflowRun = 1UL }
              { descriptor with WorkflowAttempt = 1u }
              { descriptor with Process = { descriptor.Process with Platform = "linux" } }
              { descriptor with OriginalGeneration = 0L }
              { descriptor with Identity = { descriptor.Identity with ProfileId = "other" } } ]
        for binding in mutations do
            let bad = { plan with Binding = binding }
            Assert.True(Result.isError (FiniteCustodyNativeInertPlan.validate bad bad))

    [<Fact>]
    let ``native inert plan requires independent source and original clock joins`` () =
        let plan = nativePlan ()
        let resetOriginal = Budget.create (at 2000L) (TimeSpan.FromSeconds 300.) (TimeSpan.FromSeconds 15.) |> accepted
        let resetWindow = FiniteCustodyWindow.prepare (at 2000L) resetOriginal |> accepted
        let mutations =
            [ { plan with OriginalWindow = resetWindow }
              { plan with Binding = { descriptor with ChildRevision = String.replicate 40 "2" } }
              { plan with Binding = { descriptor with RuntimeClosure = String.replicate 64 "3" } }
              { plan with Binding = { descriptor with ResultDestination = "other-slot" } }
              { plan with CapabilityId = Guid.NewGuid() } ]
        for bad in mutations do
            Assert.True(Result.isError (FiniteCustodyNativeInertPlan.validate plan bad))

    [<Fact>]
    let ``native inert draft pins and populated pins cannot construct a native session`` () =
        let plan = nativePlan ()
        let pinned = { plan with ProviderSourceClosure = Some(String.replicate 64 "a")
                                 ExecutableSha256 = Some(String.replicate 64 "b")
                                 FilterSha256 = Some(String.replicate 64 "c") }
        Assert.Equal(Ok (), FiniteCustodyNativeInertPlan.validate pinned pinned)
        Assert.Equal("finite-custody-native-inert-unavailable", nativeReason (FiniteCustodyNativeInertAdmission()) pinned pinned)
        for bad in [ { plan with ProviderSourceClosure = Some "bad" }
                     { plan with ExecutableSha256 = Some "bad" }
                     { plan with FilterSha256 = Some "bad" } ] do
            Assert.True(Result.isError (FiniteCustodyNativeInertPlan.validate bad bad))

    [<Fact>]
    let ``native inert admission retains invocation and capability reservation after refusal`` () =
        let plan = nativePlan ()
        let owner = FiniteCustodyNativeInertAdmission()
        Assert.Equal("finite-custody-native-inert-unavailable", nativeReason owner plan plan)
        Assert.Equal("finite-custody-native-inert-admission-consumed", nativeReason owner plan plan)
        let anotherInvocation = { plan with Binding = { descriptor with Identity = { descriptor.Identity with InvocationId = Guid.NewGuid() } } }
        Assert.Equal("finite-custody-native-inert-admission-consumed", nativeReason owner anotherInvocation anotherInvocation)
        let anotherCapability = { plan with CapabilityId = Guid.NewGuid() }
        Assert.Equal("finite-custody-native-inert-admission-consumed", nativeReason owner anotherCapability anotherCapability)

    [<Fact>]
    let ``native inert malformed plan cannot consume the valid original capability`` () =
        let plan = nativePlan ()
        let owner = FiniteCustodyNativeInertAdmission()
        let malformed = { plan with ChildFds = [] }
        Assert.Equal("finite-custody-native-inert-plan-refused", nativeReason owner malformed malformed)
        Assert.Equal("finite-custody-native-inert-unavailable", nativeReason owner plan plan)

    [<Fact>]
    let ``native inert missing owner or window refuses without constructing ports`` () =
        let plan = nativePlan ()
        Assert.Equal("finite-custody-native-inert-unavailable", nativeReason (Unchecked.defaultof<FiniteCustodyNativeInertAdmission>) plan plan)
        let missingWindow = { plan with OriginalWindow = Unchecked.defaultof<FiniteCustodyWindow> }
        Assert.True(Result.isError (FiniteCustodyNativeInertPlan.validate missingWindow missingWindow))
