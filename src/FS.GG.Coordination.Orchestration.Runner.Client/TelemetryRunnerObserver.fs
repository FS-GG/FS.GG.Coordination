namespace FS.GG.Coordination.Orchestration.Runner.Client

open FS.GG.Coordination.Orchestration.Execution.Codex
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Runner.Protocol

/// Keep native evidence first; queue compact Host facts without waiting on network in the stdout pump.
type TelemetryRunnerObserver(
    stateRoot: string,
    command: ExecutorCommandV2,
    publisher: TelemetryCliPublisher option,
    ?learning: PreparedLearningTelemetry,
    ?learningBinding: LearningExecutionBinding,
    ?causalAdmission: ExecutionCausalAdmission
) =
    let concreteJournal = TelemetryTurnJournal(stateRoot, command)
    let journal = concreteJournal :> ICodexTurnObserver
    let context = TelemetryFactBatches.invocationFor command causalAdmission
    let mutable turnCount = 0L
    let preparedLearning =
        match learning with
        | Some prepared -> Some prepared
        | None -> learningBinding |> Option.bind (LearningTelemetryFacts.fromExecutionBinding >> Result.toOption >> Option.flatten)
    let learningActive = learning.IsSome || learningBinding.IsSome

    let queue (name, payload) =
        publisher
        |> Option.iter (fun target ->
            match target.Queue(name, payload) with
            | Ok _ -> ()
            | Error code -> journal.Gap code)

    let recordGap code =
        let gapId = concreteJournal.RecordGap code
        TelemetryFactBatches.gap context gapId code |> queue

    let selectionGaps (turn: CodexTurnUsage) =
        let compare codeMissing codeMismatch requested observed =
            match requested, observed with
            | Some _, None -> Some codeMissing
            | Some expected, Some actual when not (System.String.Equals(expected, actual, System.StringComparison.Ordinal)) ->
                Some codeMismatch
            | _ -> None

        [ compare
              "learning-native-model-unobserved"
              "learning-native-model-mismatch"
              (Option.ofObj command.RequestedModel)
              turn.ObservedModel
          compare
              "learning-native-effort-unobserved"
              "learning-native-effort-mismatch"
              (Option.ofObj command.RequestedEffort)
              turn.ObservedEffort ]
        |> List.choose id

    do
        preparedLearning
        |> Option.iter (fun prepared ->
            let learningBatch = TelemetryFactBatches.learningPreparation context prepared
            concreteJournal.RecordLearningBatch learningBatch
            queue learningBatch)

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

            if learningActive then
                selectionGaps turn |> List.iter recordGap

        member _.Gap code =
            recordGap code

        member _.ProcessStarted(processId, at) =
            journal.ProcessStarted(processId, at)
            TelemetryFactBatches.processStart context processId at |> queue

        member this.ProcessTerminal(exitCode, threadId, at) =
            journal.ProcessTerminal(exitCode, threadId, at)

            if exitCode = 0 && turnCount = 0L then
                (this :> ICodexTurnObserver).Gap "exit-zero-without-usage"

            TelemetryFactBatches.processTerminal context exitCode threadId at |> queue

        member _.ThreadStarted(processId, threadId, at) =
            journal.ThreadStarted(processId, threadId, at)
            TelemetryFactBatches.threadStart context processId threadId |> queue

        member _.NativeTurnStarted(processId, threadId, turnId, sequence, at) =
            journal.NativeTurnStarted(processId, threadId, turnId, sequence, at)
            TelemetryFactBatches.turnStart context processId threadId turnId sequence |> queue
