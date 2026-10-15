namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FS.GG.Execution.Contracts

/// Source-only composition. Construction has no native launcher or credential port.
module internal FiniteCustodyComposition =
    type Descriptor =
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
        && value |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

    let private valid descriptor =
        descriptor.Mode = "inert"
        && descriptor.Identity.ProfileId = FiniteCustodyWindow.profileId
        && descriptor.Identity.InvocationId <> Guid.Empty
        && descriptor.OriginalGeneration > 0L
        && (match descriptor.Identity.Execution with
            | None -> true
            | Some execution -> execution.Generation = descriptor.OriginalGeneration
                                && execution.AssignmentId <> Guid.Empty && execution.AttemptId <> Guid.Empty)
        && descriptor.Process.Pid > 0
        && not (String.IsNullOrWhiteSpace descriptor.Process.BirthToken)
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
    let private joins descriptor window (binding: ReadOnlyMemory<byte>) =
        try
            match PhaseResultProtocol.bindingDigest binding with
            | Error _ -> false
            | Ok _ ->
                use document = JsonDocument.Parse binding
                let root = document.RootElement
                let field (value: JsonElement) (name: string) = value.GetProperty name
                let text value name = (field value name).GetString()
                let process = field root "process"
                let source = field root "source"
                let workflow = field root "workflow"
                let app = field root "app"
                let clock = field root "clock"
                let budget = window |> FiniteCustodyWindow.originalBudget |> Budget.describe
                let number name expected = text clock name = string expected
                text root "mode" = "inert" && text root "profile" = descriptor.DomainProfile
                && text root "operation" = descriptor.OriginalOperation
                && (field root "originalGeneration").GetInt64() = descriptor.OriginalGeneration
                && text root "invocation" = string descriptor.Identity.InvocationId
                && (field process "pid").GetInt32() = descriptor.Process.Pid
                && text process "birth" = descriptor.Process.BirthToken
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

    type Session private (clock: IMonotonicClock, window, binding, ports: Ports, token: CancellationToken) =
        let protocol = PhaseResultProtocol.Session binding
        let gate = obj()
        let tasks = ResizeArray<Task>()
        let mutable starting = 0
        let mutable refusal = false
        let mutable started = false
        let mutable inputComplete = false
        let mutable confirmedAckOrdinal = 0
        let mutable stopReason = None
        let mutable lastTick = (window |> FiniteCustodyWindow.originalBudget |> Budget.describe).StartedAt
        let mutable retirement: Task<bool> option = None
        let queue = Channel.CreateBounded<PhaseResultSnapshot>(BoundedChannelOptions(6, SingleWriter = true, SingleReader = true))
        let refuse () = lock gate (fun () -> refusal <- true)
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
        let admitted () =
            match sample () with
            | Ok(now, _) -> not token.IsCancellationRequested && not (lock gate (fun () -> refusal))
                            && FiniteCustodyWindow.normalWorkAllowed now window
            | _ -> false
        let terminated () =
            ports.Termination.IsCompletedSuccessfully && ports.Termination.Result = Some true
        let localTasks () =
            lock gate (fun () ->
                if starting > 0 || (tasks |> Seq.exists (fun task -> not task.IsCompleted))
                   || protocol.LocalTasks <> Settled then Outstanding else Settled)
        let write (bytes: ReadOnlyMemory<byte>) = task {
            // A thrown/partial write remains possibly delivered in the real reducer.
            let! count = ports.Write bytes
            if count <> bytes.Length then refuse (); return false
            else return true }
        let parse () = task {
            try
                let mutable reading = true
                while reading do
                    let! available = queue.Reader.WaitToReadAsync().AsTask()
                    if not available then reading <- false
                    else
                        let mutable snapshot = Unchecked.defaultof<PhaseResultSnapshot>
                        while queue.Reader.TryRead(&snapshot) do
                            match protocol.AcceptChild(snapshot, ports.Validate) with
                            | Error _ -> refuse ()
                            | Ok false ->
                                // Non-ACK phases, including terminal failure, still
                                // pass through the real monotone result sink.
                                let! retained = ports.PersistAndReadback snapshot
                                match retained with
                                | Ok receipt when receipt.Binding = snapshot.Binding
                                                  && receipt.Ordinal = snapshot.Ordinal
                                                  && receipt.FrameDigest = snapshot.Digest -> ()
                                | _ -> refuse ()
                            | Ok true ->
                                match protocol.BeginRetention(snapshot, ports.PersistAndReadback) with
                                | Error _ -> refuse ()
                                | Ok retained ->
                                    let! _ = retained.Completion
                                    // A late callback cannot obtain another clock grant.
                                    match sample () with
                                    | Error _ -> refuse ()
                                    | Ok(now, _) when not token.IsCancellationRequested && not (lock gate (fun () -> refusal)) ->
                                        match protocol.TakeAck(now, window, retained) with
                                        | Error _ -> refuse ()
                                        | Ok bytes ->
                                            let! complete = write bytes
                                            if complete then
                                                match sample () with
                                                | Ok(sent, _) when not token.IsCancellationRequested && not (lock gate (fun () -> refusal)) ->
                                                    match protocol.ConfirmAckSent(sent, window, snapshot) with
                                                    | Error _ -> refuse ()
                                                    | Ok () -> lock gate (fun () -> confirmedAckOrdinal <- snapshot.Ordinal)
                                                | _ -> refuse ()
                                    | _ -> refuse ()
                if protocol.FinishInput() |> Result.isError then refuse ()
                else lock gate (fun () -> inputComplete <- true)
            with _ -> refuse () }
        let read () = task {
            try
                let mutable reading = true
                while reading do
                    let! bytes = ports.Read()
                    if bytes.IsEmpty then reading <- false
                    else
                        // Feed at ingress, independently of blocked validation, retention
                        // or ACK writes. This preserves the protocol's pre-ACK barrier.
                        match protocol.Feed bytes with
                        | Error _ -> refuse (); reading <- false
                        | Ok snapshots ->
                            for snapshot in snapshots do
                                if not (queue.Writer.TryWrite snapshot) then refuse (); reading <- false
            with _ -> refuse ()
            queue.Writer.TryComplete() |> ignore }

        member _.Protocol = protocol
        /// Inert harness observation; it grants no native channel authority.
        member _.ConfirmedAckOrdinal = lock gate (fun () -> confirmedAckOrdinal)
        member _.LocalTasks = localTasks ()
        member _.RetainedTasks = lock gate (fun () -> tasks.ToArray())

        /// Preserve all existing cleanup facts; outstanding local work or unknown
        /// termination always retains the caller's existing custody owner.
        member _.JoinCleanup(existing: CleanupResult, stamp: ObservationStamp, owner: OwnerReference) =
            let state = localTasks ()
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
            elif not (admitted ()) then return Error "finite-composition-expired"
            else
                launch (fun () -> task {
                    try
                        let! complete = write (protocol.StartFrame())
                        if complete && admitted () then
                            launch (fun () -> parse () :> Task) |> ignore
                            launch (fun () -> read () :> Task) |> ignore
                        else refuse ()
                    with _ -> refuse () } :> Task) |> ignore
                let mutable waiting = true
                while waiting do
                    let current = sample ()
                    let reason =
                        match current with
                        | Error _ -> Some OwnerLost
                        | Ok _ when token.IsCancellationRequested -> Some CallerCancellation
                        | Ok(_,view) when view.WorkExpired -> Some WorkDeadline
                        | _ when protocol.Refused || lock gate (fun () -> refusal) -> Some SinkFailure
                        | _ when ports.Termination.IsCompleted && not (terminated ()) -> Some OwnerLost
                        | _ -> None
                    match reason with
                    | Some value ->
                        lock gate (fun () ->
                            if stopReason.IsNone then stopReason <- Some value
                            refusal <- true)
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
                return Ok
                    { StopReason = stopReason
                      SignalRequested = retirement.IsSome
                      SignalAccepted = retirement |> Option.exists (fun signal -> signal.IsCompletedSuccessfully && signal.Result)
                      LastThreadTerminated = terminated ()
                      InOriginalBudget = within
                      ProtocolComplete = lock gate (fun () -> inputComplete && not refusal) }
        }

        static member Prepare(admission: Admission, expected, actual, clock: IMonotonicClock, original, binding, ports, token) =
            // Reject native descriptors and unsupported runners before clock callbacks,
            // descriptor consumption, channel construction, launch or acquisition.
            if not (valid actual) || actual <> expected then Error "finite-composition-descriptor-refused"
            else
                try
                    match FiniteCustodyWindow.prepare (clock.Now()) original with
                    | Error _ -> Error "finite-composition-window-refused"
                    | Ok window ->
                        match admission.Take(actual, expected, window, binding) with
                        | Error reason -> Error reason
                        | Ok () -> Ok(Session(clock, window, binding, ports, token))
                with _ -> Error "finite-composition-prepare-refused"
