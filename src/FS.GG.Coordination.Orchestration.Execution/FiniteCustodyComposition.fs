namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FS.GG.Execution.Contracts

// Deliberate testability boundary: only the maintained execution test assembly.
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("FS.GG.Coordination.Orchestration.Execution.Tests")>]
do ()

/// Independently held descriptor for the source-only inert adapter.
type FiniteCustodyInertDescriptor =
    { Mode: string
      Identity: OperationIdentity
      OriginalOperation: string
      OriginalGeneration: int64
      DomainProfile: string
      Process: ProcessGeneration
      ChildRevision: string
      RunnerRevision: string
      RunnerClosure: string
      ContractDigest: string
      RuntimeClosure: string
      WorkflowRepository: uint64
      WorkflowPath: string
      WorkflowRevision: string
      WorkflowRun: uint64
      WorkflowAttempt: uint32
      App: uint64
      Installation: uint64
      ChannelRoles: string list
      ResultDestination: string }

/// Source-only composition. Construction has no native launcher or credential port.
module internal FiniteCustodyComposition =
    type Descriptor = FiniteCustodyInertDescriptor

    /// All callbacks are explicitly inert and run on retained tasks outside supervision.
    /// Termination is an observation of last-thread termination, not signal acceptance.
    type Ports =
        { Read: unit -> Task<ReadOnlyMemory<byte>>
          Write: ReadOnlyMemory<byte> -> Task<int>
          Validate: PhaseResultSnapshot -> Result<unit,string>
          PersistAndReadback: PhaseResultSnapshot -> Task<Result<PhaseResultReadback,string>>
          RequestRetirement: unit -> Task<bool>
          Termination: Task<bool option> }

    type Retirement =
        { StopReason: StopReason option
          SignalRequested: bool
          SignalAccepted: bool
          LastThreadTerminated: bool
          InOriginalBudget: bool
          ProtocolComplete: bool }

    let private hex size (value: string) =
        not (isNull value) && value.Length = size
        && (value |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))

    let valid (descriptor: Descriptor) =
        descriptor.Mode = "inert"
        && descriptor.Identity.ProfileId = FiniteCustodyWindow.profileId
        && descriptor.Identity.InvocationId <> Guid.Empty
        && descriptor.OriginalGeneration > 0L
        && (match descriptor.Identity.Execution with
            | None -> true
            | Some execution -> execution.Generation = descriptor.OriginalGeneration
                                && execution.AssignmentId <> Guid.Empty && execution.AttemptId <> Guid.Empty)
        && descriptor.Process.Platform = "inert"
        && descriptor.Process.Pid > 0
        && not (String.IsNullOrWhiteSpace descriptor.Process.ScopeId)
        && not (isNull descriptor.Process.BirthToken)
        && descriptor.Process.BirthToken.Length >= 1 && descriptor.Process.BirthToken.Length <= 64
        && hex descriptor.Process.BirthToken.Length descriptor.Process.BirthToken
        && not (String.IsNullOrWhiteSpace descriptor.OriginalOperation)
        && not (String.IsNullOrWhiteSpace descriptor.DomainProfile)
        && hex 64 descriptor.Identity.SourceDigest && hex 64 descriptor.Identity.ProfileDigest
        && hex 40 descriptor.ChildRevision && hex 40 descriptor.RunnerRevision
        && hex 64 descriptor.RunnerClosure && hex 64 descriptor.ContractDigest
        && hex 64 descriptor.RuntimeClosure && hex 40 descriptor.WorkflowRevision
        && descriptor.WorkflowRepository > 0UL && descriptor.App > 0UL && descriptor.Installation > 0UL
        && descriptor.WorkflowRun = 0UL && descriptor.WorkflowAttempt = 0u
        && not (String.IsNullOrWhiteSpace descriptor.WorkflowPath)
        && descriptor.ChannelRoles = ["parent-start-and-ack"; "child-phase-result"]
        && not (String.IsNullOrWhiteSpace descriptor.ResultDestination)

    /// Binding bytes come independently from the domain owner; never infer a current
    /// identity, clock or native authority from a child echo.
    let private joins (descriptor: Descriptor) window (binding: ReadOnlyMemory<byte>) =
        try
            match PhaseResultProtocol.bindingDigest binding with
            | Error _ -> false
            | Ok _ ->
                use document = JsonDocument.Parse binding
                let root = document.RootElement
                let keys names (value: JsonElement) =
                    value.ValueKind = JsonValueKind.Object
                    && (value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq) = Set.ofList names
                let field (value: JsonElement) (name: string) = value.GetProperty name
                let text value name = (field value name).GetString()
                let processBinding = field root "process"
                let source = field root "source"
                let workflow = field root "workflow"
                let app = field root "app"
                let clock = field root "clock"
                let budget = window |> FiniteCustodyWindow.originalBudget |> Budget.describe
                let number name expected = text clock name = string expected
                keys ["mode"; "profile"; "operation"; "originalGeneration"; "invocation"; "process"; "source"; "workflow"; "clock"; "app"] root
                && keys ["pid"; "birth"] processBinding
                && keys ["childRevision"; "childClosure"; "runnerRevision"; "runnerClosure"; "contract"] source
                && keys ["repositoryId"; "path"; "revision"; "run"; "attempt"] workflow
                && keys ["domain"; "frequency"; "start"; "normal"; "work"; "total"] clock
                && keys ["id"; "installation"] app
                && text root "mode" = "inert" && text root "profile" = descriptor.DomainProfile
                && text root "operation" = descriptor.OriginalOperation
                && (field root "originalGeneration").GetInt64() = descriptor.OriginalGeneration
                && text root "invocation" = string descriptor.Identity.InvocationId
                && (field processBinding "pid").GetInt32() = descriptor.Process.Pid
                && text processBinding "birth" = descriptor.Process.BirthToken
                && text source "childRevision" = descriptor.ChildRevision
                && text source "childClosure" = descriptor.Identity.SourceDigest
                && text source "runnerRevision" = descriptor.RunnerRevision
                && text source "runnerClosure" = descriptor.RunnerClosure
                && text source "contract" = descriptor.ContractDigest
                && (field workflow "repositoryId").GetUInt64() = descriptor.WorkflowRepository
                && text workflow "path" = descriptor.WorkflowPath
                && text workflow "revision" = descriptor.WorkflowRevision
                && (field workflow "run").GetUInt64() = 0UL
                && (field workflow "attempt").GetUInt32() = 0u
                && (field app "id").GetUInt64() = descriptor.App
                && (field app "installation").GetUInt64() = descriptor.Installation
                && text clock "domain" = string budget.Domain.Id
                && number "frequency" budget.Domain.Frequency && number "start" budget.StartedAt
                && number "normal" (FiniteCustodyWindow.normalWorkExpiresAt window)
                && number "work" budget.WorkExpiresAt && number "total" budget.TotalExpiresAt
        with _ -> false

    /// One admission owner retains consumed identities for its entire operation window.
    /// Recreating this owner is not permission to replay an original operation.
    type Admission() =
        let consumed = HashSet<Guid>()
        member _.Take(actual, expected, window, binding) =
            lock consumed (fun () ->
                if not (valid actual) || actual <> expected || not (joins actual window binding)
                   || not (consumed.Add actual.Identity.InvocationId) then Error "finite-composition-admission-refused"
                else Ok ())

    type Session private (clock: IMonotonicClock, window, binding, ports: Ports, token: CancellationToken, channelStop: unit -> StopReason option) =
        let protocol = PhaseResultProtocol.Session binding
        // Construct and bound the codec's real start frame during admission, before
        // any callback. It will be written at most once by the retained writer.
        let startFrame = protocol.StartFrame()
        let gate = obj()
        let tasks = ResizeArray<Task>()
        let mutable starting = 0
        let mutable refusal = false
        let mutable started = false
        let mutable inputComplete = false
        let mutable confirmedAckOrdinal = 0
        let mutable unadmittedIngressBytes = 0L
        let mutable stopReason = None
        let mutable supervisionFinished = false
        let mutable lastTick = (window |> FiniteCustodyWindow.originalBudget |> Budget.describe).StartedAt
        let mutable retirement: Task<bool> option = None
        let queue = Channel.CreateBounded<PhaseResultSnapshot>(BoundedChannelOptions(6, SingleWriter = true, SingleReader = true))
        let recordStop reason = lock gate (fun () ->
            if stopReason.IsNone then stopReason <- Some reason)
        let requestStop reason = lock gate (fun () ->
            recordStop reason
            refusal <- true)
        let refuse () = requestStop (defaultArg (channelStop ()) SinkFailure)
        let launch (work: unit -> Task) =
            lock gate (fun () -> starting <- starting + 1)
            try
                let pending = Task.Run(Func<Task>(work))
                lock gate (fun () -> tasks.Add pending; starting <- starting - 1)
                pending
            with _ -> refuse (); reraise () // Retain the unresolved scheduling reservation.
        let sample () =
            try
                lock gate (fun () ->
                    let now = clock.Now()
                    if now.Ticks < lastTick then Error ClockMovedBackwards
                    else
                        lastTick <- now.Ticks
                        FiniteCustodyWindow.remaining now window |> Result.map (fun view -> now, view))
            with _ -> Error InvalidBudget
        // Inspect the actual stop source immediately after successful callbacks.
        // The independent supervisor need not win a scheduling race to classify it.
        let checkpoint normalWork requireRunning =
            match channelStop (), sample () with
            | Some reason, _ -> requestStop reason; None
            | None, Error _ -> requestStop OwnerLost; None
            | None, Ok _ when token.IsCancellationRequested -> requestStop CallerCancellation; None
            | None, Ok(now, view) when view.WorkExpired || (normalWork && not (FiniteCustodyWindow.normalWorkAllowed now window)) ->
                requestStop WorkDeadline
                None
            | None, Ok _ when requireRunning && ports.Termination.IsCompleted -> requestStop OwnerLost; None
            | None, Ok _ when lock gate (fun () -> refusal) -> None
            | None, Ok(now, _) -> Some now
        let callbackCheckpoint (snapshot: PhaseResultSnapshot) =
            if snapshot.Kind = "terminal" then
                // Failure evidence may survive an invalidated child ACK, but it
                // receives no exception from the original work cutoff or cancellation.
                match channelStop (), sample () with
                | Some reason, _ -> requestStop reason; None
                | None, Error _ -> requestStop OwnerLost; None
                | None, Ok _ when token.IsCancellationRequested -> requestStop CallerCancellation; None
                | None, Ok(_, view) when view.WorkExpired -> requestStop WorkDeadline; None
                | None, Ok _ when lock gate (fun () -> refusal) -> None
                | None, Ok(now, _) -> Some now
            else
                let normalWork = snapshot.Kind = "ready" || snapshot.Kind = "issuance-possible"
                checkpoint normalWork false
        let admitted () = checkpoint true true |> Option.isSome
        let terminated () =
            ports.Termination.IsCompletedSuccessfully && ports.Termination.Result = Some true
        let localTasks () =
            lock gate (fun () ->
                if starting > 0 || (tasks |> Seq.exists (fun task -> not task.IsCompleted))
                   || protocol.LocalTasks <> Settled then Outstanding else Settled)
        let write normalWork (bytes: ReadOnlyMemory<byte>) = task {
            if checkpoint normalWork false |> Option.isNone then return false
            else
                // A thrown/partial write remains possibly delivered in the real reducer.
                let! count = ports.Write bytes
                if count <> bytes.Length then refuse (); return false
                else return true }
        let persist (snapshot: PhaseResultSnapshot) =
            // BeginRetention schedules this delegate on its retained task. Check
            // at actual port entry, not only before task scheduling: total reserve
            // settles existing work and never grants a new persistence operation.
            match callbackCheckpoint snapshot with
            | None -> Task.FromResult(Error "finite-composition-callback-stopped")
            | Some _ -> ports.PersistAndReadback snapshot
        let parse () = task {
            try
                let mutable reading = true
                while reading do
                    let! available = queue.Reader.WaitToReadAsync().AsTask()
                    if not available then reading <- false
                    else
                        let mutable snapshot = Unchecked.defaultof<PhaseResultSnapshot>
                        while queue.Reader.TryRead(&snapshot) do
                            let mayReduce () = callbackCheckpoint snapshot |> Option.isSome
                            let validate value =
                                // The domain callback itself must enter within the
                                // applicable original cutoff, even if decoding was earlier.
                                if not (mayReduce ()) then Error "finite-composition-stopped"
                                else
                                    let result = ports.Validate value
                                    if Result.isError result then refuse ()
                                    // Late validation settles its own obligation, but
                                    // cannot authorize reduction or another callback.
                                    if mayReduce () then result else Error "finite-composition-stopped"
                            let accepted =
                                if mayReduce () then protocol.AcceptChild(snapshot, validate)
                                else Error "finite-composition-stopped"
                            match accepted with
                            | Error _ -> refuse ()
                            | Ok false ->
                                // Non-ACK phases, including terminal failure, still
                                // pass through the real monotone result sink.
                                let! retained = persist snapshot
                                match retained with
                                | Ok receipt when receipt.Binding = snapshot.Binding
                                                  && receipt.Ordinal = snapshot.Ordinal
                                                  && receipt.FrameDigest = snapshot.Digest ->
                                    callbackCheckpoint snapshot |> ignore
                                | _ -> refuse ()
                            | Ok true ->
                                match protocol.BeginRetention(snapshot, persist) with
                                | Error _ -> refuse ()
                                | Ok retained ->
                                    let! readback = retained.Completion
                                    // Record a genuine callback/readback failure before
                                    // inspecting cancellation or deadlines that follow it.
                                    let exact =
                                        match readback with
                                        | Ok receipt -> receipt.Binding = snapshot.Binding
                                                        && receipt.Ordinal = snapshot.Ordinal
                                                        && receipt.FrameDigest = snapshot.Digest
                                        | Error _ -> false
                                    if not exact then refuse ()
                                    else
                                        let normalWork = snapshot.Kind <> "revocation-possible"
                                        // A late successful callback cannot obtain a new
                                        // grant, and its actual stop source stays visible.
                                        match checkpoint normalWork false with
                                        | None -> ()
                                        | Some now ->
                                            match protocol.TakeAck(now, window, retained) with
                                            | Error _ -> recordStop SinkFailure
                                            | Ok bytes ->
                                                let! complete = write normalWork bytes
                                                if complete then
                                                    match checkpoint normalWork false with
                                                    | None -> ()
                                                    | Some sent ->
                                                        match protocol.ConfirmAckSent(sent, window, snapshot) with
                                                        | Error _ -> recordStop SinkFailure
                                                        | Ok () -> lock gate (fun () -> confirmedAckOrdinal <- snapshot.Ordinal)
                if protocol.FinishInput() |> Result.isError then refuse ()
                else lock gate (fun () -> inputComplete <- true)
            with _ -> refuse () }
        let read () = task {
            try
                let mutable reading = true
                while reading do
                    if checkpoint false false |> Option.isNone then reading <- false
                    else
                        let! bytes = ports.Read()
                        match checkpoint false false with
                        | None ->
                            // A read already in flight can settle late. Retain its
                            // actual byte observation without decoding/admitting a frame.
                            lock gate (fun () ->
                                let count = int64 bytes.Length
                                unadmittedIngressBytes <-
                                    if count > Int64.MaxValue - unadmittedIngressBytes then Int64.MaxValue
                                    else unadmittedIngressBytes + count)
                            reading <- false
                        | Some _ when bytes.IsEmpty -> reading <- false
                        | Some _ ->
                            // Feed at ingress independently of blocked callbacks;
                            // preserve the protocol's pre-ACK barrier while work is live.
                            match protocol.Feed bytes with
                            | Error _ -> refuse (); reading <- false
                            | Ok snapshots ->
                                for snapshot in snapshots do
                                    if not (queue.Writer.TryWrite snapshot) then refuse (); reading <- false
            with _ -> refuse ()
            queue.Writer.TryComplete() |> ignore }

        member _.Accepted = protocol.Accepted
        member _.RetentionTasks = protocol.RetentionTasks
        /// Bytes returned by an already-started read after admission closed; not decoded.
        member _.UnadmittedIngressBytes = lock gate (fun () -> unadmittedIngressBytes)
        /// Inert harness observation; it grants no native channel authority.
        member _.ConfirmedAckOrdinal = lock gate (fun () -> confirmedAckOrdinal)
        member _.LocalTasks =
            if lock gate (fun () -> supervisionFinished) then localTasks () else Outstanding
        member _.RetainedTasks = lock gate (fun () -> tasks.ToArray())

        /// Preserve all existing cleanup facts; outstanding local work or unknown
        /// termination always retains the caller's existing custody owner.
        member _.JoinCleanup(existing: CleanupResult, stamp: ObservationStamp, owner: OwnerReference) =
            let state = if lock gate (fun () -> supervisionFinished) then localTasks () else Outstanding
            let joined =
                match state, existing.LocalTasks with
                | Outstanding, _ -> Known(Outstanding, stamp)
                | Settled, Known(Settled, _) -> Known(Settled, stamp)
                | _ -> existing.LocalTasks
            let safe = terminated () && (match joined with Known(Settled, _) -> true | _ -> false)
            { existing with
                LocalTasks = joined
                Disposition = if safe then existing.Disposition else Incomplete
                Custody = if safe then existing.Custody else RetainedByOwner owner }

        member _.Run() = task {
            let first = lock gate (fun () ->
                if started then false
                else
                    started <- true
                    true)
            if not first then return Error "finite-composition-reuse-refused"
            else
                if admitted () then
                    launch (fun () -> task {
                        try
                            // Scheduling never renews a grant checked by the caller.
                            if admitted () then
                                let! complete = write true startFrame
                                if complete && admitted () then
                                    launch (fun () -> parse () :> Task) |> ignore
                                    launch (fun () -> read () :> Task) |> ignore
                                // Partial/failed writes already latched their cause;
                                // successful writes are classified by admitted above.
                        with _ -> refuse () } :> Task) |> ignore
                let mutable waiting = true
                while waiting do
                    let current = sample ()
                    let reason =
                        match channelStop (), current with
                        | Some reason, _ -> Some reason
                        | None, Error _ -> Some OwnerLost
                        | None, Ok _ when token.IsCancellationRequested -> Some CallerCancellation
                        | None, Ok(_,view) when view.WorkExpired -> Some WorkDeadline
                        | None, _ when protocol.Refused || lock gate (fun () -> refusal) -> Some SinkFailure
                        | None, _ when ports.Termination.IsCompleted && not (terminated ()) -> Some OwnerLost
                        | None, _ -> None
                    match reason with
                    | Some value ->
                        // Protocol invalidation (notably an in-time pre-ACK
                        // terminal) does not itself imply a failed local callback.
                        // The separate local refusal fence remains sticky.
                        if value = SinkFailure && protocol.Refused then recordStop value
                        else requestStop value
                        if retirement.IsNone && not (terminated ()) then
                            let completion = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                            retirement <- Some completion.Task
                            launch (fun () -> task {
                                try
                                    let! accepted = ports.RequestRetirement()
                                    completion.TrySetResult accepted |> ignore
                                with _ -> completion.TrySetResult false |> ignore } :> Task) |> ignore
                    | None -> ()
                    match current with
                    | Error _ -> waiting <- false
                    | Ok(_,view) when view.TotalExpired -> waiting <- false
                    | _ when terminated () && localTasks () = Settled -> waiting <- false
                    | _ -> do! Task.Delay 1
                let within = match sample () with Ok(_,view) -> not view.TotalExpired | Error _ -> false
                lock gate (fun () -> supervisionFinished <- true)
                return Ok
                    { StopReason = lock gate (fun () -> stopReason)
                      SignalRequested = retirement.IsSome
                      SignalAccepted = retirement |> Option.exists (fun signal -> signal.IsCompletedSuccessfully && signal.Result)
                      LastThreadTerminated = terminated ()
                      InOriginalBudget = within
                      ProtocolComplete = lock gate (fun () -> inputComplete && not refusal) }
        }

        static member Prepare(admission: Admission, expected, actual, clock: IMonotonicClock, original, binding: ReadOnlyMemory<byte>, ports, token, ?channelStop: unit -> StopReason option) =
            // Reject native descriptors and unsupported runners before clock callbacks,
            // descriptor consumption, channel construction, launch or acquisition.
            if not (valid actual) || actual <> expected then Error "finite-composition-descriptor-refused"
            elif binding.Length > PhaseResultProtocol.maximumFrameBytes - 4 then Error "finite-composition-binding-refused"
            else
                try
                    // Own canonical bytes before invoking any caller port. A mutable
                    // backing array cannot change the admitted identity afterward.
                    let ownedBinding = ReadOnlyMemory<byte>(binding.ToArray())
                    match FiniteCustodyWindow.prepare (clock.Now()) original with
                    | Error _ -> Error "finite-composition-window-refused"
                    | Ok window ->
                        match admission.Take(actual, expected, window, ownedBinding) with
                        | Error reason -> Error reason
                        | Ok () -> Ok(Session(clock, window, ownedBinding, ports, token, defaultArg channelStop (fun () -> None)))
                with _ -> Error "finite-composition-prepare-refused"
