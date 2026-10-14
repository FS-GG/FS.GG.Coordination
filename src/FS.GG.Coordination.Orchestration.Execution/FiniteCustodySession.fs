namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Threading
open System.Threading.Tasks
open FS.GG.Execution.Contracts

/// A view of an existing live budget. Construction never starts another clock.
type FiniteCustodyWindow = private {
      Budget: ExecutionBudget
      NormalWorkExpiresAt: int64 }

[<RequireQualifiedAccess>]
module FiniteCustodyWindow =
    let profileId = "linux-x64-finite-no-process-descendants/1"

    /// Source preparation is not qualification of an installed profile.
    let productionAvailable = false

    let originalBudget window = window.Budget

    let normalWorkExpiresAt window = window.NormalWorkExpiresAt

    let private duration (frequency: int64) (ticks: int64) =
        TimeSpan.FromTicks(int64 (bigint ticks * bigint TimeSpan.TicksPerSecond / bigint frequency))

    let prepare (now: ClockStamp) original =
        match Budget.remaining now original with
        | Error reason -> Error reason
        | Ok _ ->
            let description = Budget.describe original
            let start = { Domain = description.Domain; Ticks = description.StartedAt }
            let total = min (TimeSpan.FromSeconds 300.) (duration start.Domain.Frequency (description.TotalExpiresAt - start.Ticks))
            let originalWork = duration start.Domain.Frequency (description.WorkExpiresAt - start.Ticks)
            // Preserve both reserves when the original grant is shorter. No stage may
            // manufacture another grace period after its predecessor expires.
            let work = min originalWork (min (TimeSpan.FromSeconds 285.) (total - TimeSpan.FromSeconds 15.))
            let normal = min (TimeSpan.FromSeconds 270.) (work - TimeSpan.FromSeconds 15.)
            if normal <= TimeSpan.Zero then Error InvalidBudget
            else
                match Budget.create start total (total - work), Budget.create start normal TimeSpan.Zero with
                | Ok bounded, Ok admission ->
                    let cutoff = (Budget.describe admission).WorkExpiresAt
                    if now.Ticks >= cutoff then Error BudgetExpired
                    else Ok { Budget = bounded; NormalWorkExpiresAt = cutoff }
                | Error reason, _ | _, Error reason -> Error reason

    let remaining now window = Budget.remaining now window.Budget

    let normalWorkAllowed now window =
        match remaining now window with
        | Ok view -> not view.WorkExpired && now.Ticks < window.NormalWorkExpiresAt
        | Error _ -> false

/// The finite retained owner never equates a successful signal with retirement.
type internal FiniteCustodyRetirement =
    { StopReason: StopReason option
      SignalRequested: bool
      SignalAccepted: bool
      LastThreadTerminated: bool
      InOriginalBudget: bool }

/// Extends the existing held-pidfd owner; it is not a launcher or effect authority.
/// The launch composition must retain this owner and its lease after an Unknown.
[<RequireQualifiedAccess>]
module internal FiniteCustodySession =
    /// Every poll is clipped to the same original total, including terminal readback.
    /// No arbitrary child callback or blocked stream read can delay the stop decision.
    let superviseHeld
        (clock: IMonotonicClock)
        window
        (lease: CustodyProcessLease)
        (token: CancellationToken)
        =
        task {
            let mutable stop = false
            let mutable reason = None
            let mutable waiting = true
            let mutable lastTick = (Budget.describe (FiniteCustodyWindow.originalBudget window)).StartedAt
            let sample () =
                try
                    let now = clock.Now()
                    if now.Ticks < lastTick then Error ClockMovedBackwards
                    else
                        lastTick <- now.Ticks
                        FiniteCustodyWindow.remaining now window
                with _ -> Error InvalidBudget
            let delay (duration: TimeSpan) =
                max 0 (int (Math.Floor (min 10. duration.TotalMilliseconds)))
            while waiting do
                match lease.Status, sample () with
                | _, Error _ -> stop <- true; reason <- Some OwnerLost; waiting <- false
                | _, Ok _ when token.IsCancellationRequested ->
                    stop <- true; reason <- Some CallerCancellation; waiting <- false
                | _, Ok view when view.WorkExpired ->
                    stop <- true
                    reason <- Some WorkDeadline
                    waiting <- false
                | CustodyProcessStatus.Terminated, _ -> waiting <- false
                | CustodyProcessStatus.Unknown, _ ->
                    stop <- true; reason <- Some OwnerLost; waiting <- false
                | _, Ok view ->
                    let milliseconds = delay view.WorkRemaining
                    if milliseconds > 0 then do! Task.Delay milliseconds
                    else stop <- true; reason <- Some WorkDeadline; waiting <- false

            // Even an expired budget permits requesting retirement of the already held
            // identity. It grants no new wait, success claim or descriptor release.
            let mutable signalled = false
            let mutable signalRequested = false
            if stop && lease.Status <> CustodyProcessStatus.Terminated then
                signalRequested <- true
                signalled <- lease.Terminate()

            let mutable settled = lease.Status = CustodyProcessStatus.Terminated
            let mutable observe = not settled
            while observe do
                match sample () with
                | Ok view when not view.TotalExpired ->
                    let milliseconds = delay view.TotalRemaining
                    if milliseconds > 0 then
                        do! Task.Delay milliseconds
                        settled <- lease.Status = CustodyProcessStatus.Terminated
                        observe <- not settled
                    else observe <- false
                | _ -> observe <- false

            let within =
                match sample () with
                | Ok view -> not view.TotalExpired
                | Error _ -> false
            return
                { StopReason = reason
                  SignalRequested = signalRequested
                  SignalAccepted = signalled
                  LastThreadTerminated = settled
                  InOriginalBudget = within }
        }
