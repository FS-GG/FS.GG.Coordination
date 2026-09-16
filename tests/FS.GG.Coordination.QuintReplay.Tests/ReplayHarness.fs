namespace FS.GG.Coordination.QuintReplay.Tests

open System.Threading.Tasks
open FS.GG.SDD.Artifacts.TypedSpecifications

type ReplayDriver<'runtime> =
    {
        Initialize: QuintReplayState -> Result<'runtime, string>
        Apply: QuintReplayStep -> 'runtime -> Task<Result<'runtime, string>>
        Observe: 'runtime -> Result<QuintReplayState, string>
    }

[<RequireQualifiedAccess>]
module ReplayHarness =
    let rec private applySteps driver runtime observations steps =
        task {
            match steps with
            | [] -> return Ok(List.rev observations)
            | step :: remaining ->
                match! driver.Apply step runtime with
                | Error message -> return Error $"step {step.Index} ({step.Action}): {message}"
                | Ok nextRuntime ->
                    match driver.Observe nextRuntime with
                    | Error message -> return Error $"observe step {step.Index} ({step.Action}): {message}"
                    | Ok actual ->
                        let observation: QuintReplayObservation =
                            {
                                Index = step.Index
                                Action = step.Action
                                Source = step.Source
                                Actual = actual
                            }

                        return! applySteps driver nextRuntime (observation :: observations) remaining
        }

    let run (trace: QuintReplayTrace) (driver: ReplayDriver<'runtime>) =
        task {
            match QuintReplay.validateTrace trace with
            | findings when not (List.isEmpty findings) -> return Error $"invalid Quint replay trace: %A{findings}"
            | _ ->
                match driver.Initialize trace.Initial with
                | Error message -> return Error $"initialize: {message}"
                | Ok initialRuntime ->
                    match driver.Observe initialRuntime with
                    | Error message -> return Error $"observe initial state: {message}"
                    | Ok actualInitial when actualInitial <> trace.Initial ->
                        return
                            Error
                                $"initial state diverged: expected {trace.Initial.Identity}, actual {actualInitial.Identity}"
                    | Ok _ ->
                        match! applySteps driver initialRuntime [] trace.Steps with
                        | Error message -> return Error message
                        | Ok observations ->
                            return
                                QuintReplay.compare trace observations
                                |> Result.mapError (fun findings -> $"compare: %A{findings}")
        }
