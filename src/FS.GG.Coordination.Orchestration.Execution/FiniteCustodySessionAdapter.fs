namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open FS.GG.Execution.Contracts

/// Only already-owned inert stream endpoints. Construction launches no process,
/// discovers no endpoint and has no credential or native bootstrap callback.
type FiniteCustodyInertChannels =
    { ChildResults: Stream
      ChildControl: Stream
      OwnerResponses: Stream
      OwnerRequests: Stream
      RequestRetirement: unit -> Task<bool>
      Termination: Task<bool option> }

/// Source result, distinct from installed/native profile qualification.
type FiniteCustodyInertResult =
    { StopReason: StopReason option
      LastThreadTerminated: bool
      InOriginalBudget: bool
      ProtocolComplete: bool
      LocalTasks: TaskState }

module internal FiniteCustodyCallbackBridge =
    exception Stopped of StopReason
    type Endpoint(clock: IMonotonicClock, window, invocation, binding, channels: FiniteCustodyInertChannels, token: CancellationToken) =
        let protocol = PhaseCallbackProtocol.Session(invocation, binding)
        let gate = obj()
        let tasks = ResizeArray<Task>()
        let mutable reader: Task option = None
        let mutable waiting: TaskCompletionSource<unit> option = None
        let mutable ended = false
        let mutable lastTick = (window |> FiniteCustodyWindow.originalBudget |> Budget.describe).StartedAt
        let mutable firstStop: StopReason option = None
        let fail reason =
            let actual = lock gate (fun () ->
                if firstStop.IsNone then firstStop <- Some reason
                firstStop.Value)
            protocol.Close()
            lock gate (fun () ->
                waiting |> Option.iter (fun value -> value.TrySetException(Stopped actual) |> ignore))
            actual
        let checkpoint normal =
            let reason = lock gate (fun () ->
                if firstStop.IsNone then
                    firstStop <-
                        try
                            let now = clock.Now()
                            if now.Ticks < lastTick then Some OwnerLost
                            else
                                lastTick <- now.Ticks
                                match FiniteCustodyWindow.remaining now window with
                                | Error _ -> Some OwnerLost
                                | Ok _ when token.IsCancellationRequested -> Some CallerCancellation
                                | Ok view when view.WorkExpired || (normal && not (FiniteCustodyWindow.normalWorkAllowed now window)) -> Some WorkDeadline
                                | Ok _ -> None
                        with _ -> Some OwnerLost
                firstStop)
            match reason with
            | Some reason -> raise (Stopped(fail reason))
            | None -> ()
        let startReader () = lock gate (fun () ->
            if reader.IsNone then
                let work = Task.Run(Func<Task>(fun () -> task {
                    // Reading is retained independently from writes and callbacks.
                    let buffer = Array.zeroCreate<byte> 1
                    try
                        let mutable reading = true
                        while reading do
                            let! count = channels.OwnerResponses.ReadAsync(buffer.AsMemory()).AsTask()
                            if count = 0 then
                                reading <- false
                                lock gate (fun () ->
                                    ended <- true
                                    if not protocol.TerminalRetained then fail OwnerLost |> ignore)
                            elif protocol.Feed(ReadOnlyMemory<byte>(buffer, 0, count)) then
                                lock gate (fun () -> waiting |> Option.iter (fun value -> value.TrySetResult() |> ignore))
                    with
                    | Stopped reason -> fail reason |> ignore
                    | _ -> fail SinkFailure |> ignore } :> Task))
                reader <- Some work
                tasks.Add work)
        let exchange normal create = task {
            try
                checkpoint normal
                let completion = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                // Reserve identity before reader start or any request byte write.
                let bytes = lock gate (fun () ->
                    if ended || waiting.IsSome then invalidOp "phase-callback-order"
                    let value = create ()
                    waiting <- Some completion
                    value)
                startReader ()
                checkpoint normal
                let write = channels.OwnerRequests.WriteAsync(ReadOnlyMemory<byte> bytes).AsTask()
                lock gate (fun () -> tasks.Add write)
                do! write
                protocol.ConfirmWrite bytes.Length
                do! completion.Task
                checkpoint normal
                let response = protocol.TakeResponse()
                lock gate (fun () -> waiting <- None)
                return Ok response
            with error ->
                let reason = match error with Stopped reason -> reason | _ -> SinkFailure
                fail reason |> ignore
                return Error "phase-callback-refused" }
        member _.Checkpoint(normal) = checkpoint normal
        member _.Bind() = exchange true protocol.Bind
        member _.Validate(snapshot: PhaseResultSnapshot) =
            exchange (snapshot.Kind = "ready" || snapshot.Kind = "issuance-possible") (fun () -> protocol.Validate snapshot)
            |> fun work -> work.GetAwaiter().GetResult() |> Result.map ignore
        member _.Retain(snapshot: PhaseResultSnapshot) = task {
            let! result = exchange (snapshot.Kind = "ready" || snapshot.Kind = "issuance-possible") (fun () -> protocol.Retain snapshot)
            return result |> Result.map (fun reply -> { Binding = reply.Binding; Ordinal = reply.Ordinal; FrameDigest = reply.FrameDigest }) }
        member _.Tasks = lock gate (fun () -> tasks.ToArray())
        member _.LocalTasks = lock gate (fun () -> if tasks |> Seq.exists (fun work -> not work.IsCompleted) then Outstanding else Settled)
        member _.Refused = protocol.Refused
        member _.StopReason = lock gate (fun () -> firstStop)

/// Retain this admission owner across source sessions; reconstructing it is not replay authority.
type FiniteCustodyInertAdmission() =
    let admission = FiniteCustodyComposition.Admission()
    member internal _.Inner = admission

/// Narrow callable facade over the maintained inert composition. Native mode
/// refuses before clocks, channels, retirement or any caller callback is invoked.
type FiniteCustodySessionAdapter private (session: FiniteCustodyComposition.Session, bridge: FiniteCustodyCallbackBridge.Endpoint) =
    /// Source fixture observation only; never a native grant.
    member internal _.ConfirmedAckOrdinal = session.ConfirmedAckOrdinal
    member _.RetainedTasks = Array.append session.RetainedTasks bridge.Tasks
    member _.LocalTasks = if session.LocalTasks = Outstanding || bridge.LocalTasks = Outstanding then Outstanding else Settled
    member _.JoinCleanup(existing, stamp, owner) =
        let result = session.JoinCleanup(existing, stamp, owner)
        if bridge.LocalTasks = Settled then result
        else { result with LocalTasks = Known(Outstanding, stamp); Disposition = Incomplete; Custody = RetainedByOwner owner }
    member this.Run() = task {
        let! result = session.Run()
        return result |> Result.map (fun outcome ->
            { StopReason = outcome.StopReason; LastThreadTerminated = outcome.LastThreadTerminated
              InOriginalBudget = outcome.InOriginalBudget
              ProtocolComplete = outcome.ProtocolComplete && not bridge.Refused
              LocalTasks = this.LocalTasks }) }

    static member Prepare(admission: FiniteCustodyInertAdmission, expected: FiniteCustodyInertDescriptor, actual: FiniteCustodyInertDescriptor,
                          clock: IMonotonicClock, original, binding: ReadOnlyMemory<byte>,
                          channels: FiniteCustodyInertChannels, token: CancellationToken) =
        let supported = try FiniteCustodyComposition.valid actual && actual = expected with _ -> false
        if not supported then Error "finite-inert-descriptor-refused"
        else
            try
                let owned = ReadOnlyMemory<byte>(binding.ToArray())
                match FiniteCustodyWindow.prepare (clock.Now()) original with
                | Error _ -> Error "finite-inert-window-refused"
                | Ok window ->
                    let bridge = FiniteCustodyCallbackBridge.Endpoint(clock, window, actual.Identity.InvocationId, owned, channels, token)
                    let mutable startAttempted = false
                    let mutable normalAck = true
                    let ports: FiniteCustodyComposition.Ports =
                        { Read = fun () -> task {
                            let buffer = Array.zeroCreate<byte> PhaseResultProtocol.maximumFrameBytes
                            let! count = channels.ChildResults.ReadAsync(buffer.AsMemory()).AsTask()
                            return ReadOnlyMemory<byte>(buffer, 0, count) }
                          Write = fun bytes -> task {
                            if not startAttempted then
                                startAttempted <- true
                                let! bound = bridge.Bind()
                                match bound with
                                | Error _ -> return 0
                                | Ok _ ->
                                    bridge.Checkpoint true
                                    do! channels.ChildControl.WriteAsync(bytes).AsTask()
                                    return bytes.Length
                            else
                                bridge.Checkpoint normalAck
                                do! channels.ChildControl.WriteAsync(bytes).AsTask()
                                return bytes.Length }
                          Validate = bridge.Validate
                          PersistAndReadback = fun snapshot -> task {
                              let! result = bridge.Retain snapshot
                              if Result.isOk result then normalAck <- snapshot.Kind <> "revocation-possible"
                              return result }
                          RequestRetirement = channels.RequestRetirement
                          Termination = channels.Termination }
                    match FiniteCustodyComposition.Session.Prepare(admission.Inner, expected, actual, clock, original, owned, ports, token, channelStop = (fun () -> bridge.StopReason)) with
                    | Error reason -> Error reason
                    | Ok session -> Ok(FiniteCustodySessionAdapter(session, bridge))
            with _ -> Error "finite-inert-preparation-refused"
