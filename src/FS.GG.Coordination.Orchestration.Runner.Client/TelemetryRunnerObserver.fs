namespace FS.GG.Coordination.Orchestration.Runner.Client

open FS.GG.Coordination.Orchestration.Execution.Codex
open FS.GG.Coordination.Orchestration.Runner.Protocol

/// Keep native evidence first; queue compact Host facts without waiting on network in the stdout pump.
type TelemetryRunnerObserver(
    stateRoot: string,
    command: ExecutorCommandV2,
    publisher: TelemetryCliPublisher option
) =
    let journal = TelemetryTurnJournal(stateRoot, command) :> ICodexTurnObserver
    let context = TelemetryFactBatches.rootInvocation command
    let mutable gapSequence = 0L
    let mutable turnCount = 0L

    let queue (name, payload) =
        publisher
        |> Option.iter (fun target ->
            match target.Queue(name, payload) with
            | Ok _ -> ()
            | Error code -> journal.Gap code)

    interface ICodexTurnObserver with
        member _.TurnCompleted turn =
            journal.TurnCompleted turn
            turnCount <- turnCount + 1L

            TelemetryFactBatches.completedTurn
                context
                (Option.ofObj command.RequestedModel)
                (Option.ofObj command.RequestedEffort)
                turn
            |> queue

        member _.Gap code =
            journal.Gap code
            gapSequence <- gapSequence + 1L
            TelemetryFactBatches.gap context gapSequence code |> queue

        member _.ProcessStarted(processId, at) =
            journal.ProcessStarted(processId, at)
            TelemetryFactBatches.processStart context processId at |> queue

        member this.ProcessTerminal(exitCode, threadId, at) =
            journal.ProcessTerminal(exitCode, threadId, at)

            if exitCode = 0 && turnCount = 0L then
                (this :> ICodexTurnObserver).Gap "exit-zero-without-usage"

            TelemetryFactBatches.processTerminal context exitCode threadId at |> queue
