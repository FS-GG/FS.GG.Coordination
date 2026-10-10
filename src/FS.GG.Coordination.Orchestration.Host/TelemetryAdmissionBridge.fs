namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Runner.Protocol
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Runner.Client

type TelemetryAdmissionRecovery =
    { Observed: int
      Queued: int
      NextCursor: string option
      Coverage: string
      Gaps: string list }

/// Advisory publication from committed PostgreSQL admission, never from a staged route or caller payload.
type TelemetryAdmissionBridge
    (read: int * string option * CancellationToken -> Task<Result<CommittedExecutionAdmissionPage, string>>,
     publisher: TelemetryCliPublisher) =
    member _.Recover(maximum, cursor, token) =
        task {
            let! page = read(maximum, cursor, token)
            match page with
            | Error reason -> return { Observed = 0; Queued = 0; NextCursor = cursor; Coverage = "unknown"; Gaps = [ reason ] }
            | Ok page ->
                let gaps = ResizeArray<string>()
                let mutable queued = 0
                for source in page.Admissions do
                    let queue obligation projected =
                        match projected with
                        | Error reason -> gaps.Add(obligation + ":" + reason); false
                        | Ok(name, bytes) ->
                            match publisher.Queue(name, bytes) with
                            | Ok _ -> true
                            | Error reason -> gaps.Add(obligation + ":" + reason); false
                    let legacyQueued =
                        ExecutionAdmissionFacts.prepare source.Admission source.Intent.Requested.Model source.Intent.Requested.Effort
                        |> queue "admission-facts"
                    let declarationQueued =
                        ExecutionCausalAdmissionFacts.prepare source.Admission source.AdmissionBytes source.RouteBindingSha256 source.LaunchIntentSha256
                        |> queue "causal-declaration"
                    if legacyQueued && declarationQueued then queued <- queued + 1
                return { Observed = page.Admissions.Length; Queued = queued; NextCursor = page.NextCursor
                         Coverage = if gaps.Count > 0 then "publication-incomplete" else page.Coverage
                         Gaps = List.ofSeq gaps }
        }
    member this.RecoverUntilCancelled(token: CancellationToken) =
        task {
            let mutable cursor = None
            try
                while not token.IsCancellationRequested do
                    try
                        let! result = this.Recover(64, cursor, token)
                        cursor <- result.NextCursor
                        let! _ = publisher.Flush token
                        ()
                    with
                    | :? OperationCanceledException when token.IsCancellationRequested -> ()
                    | _ -> () // Source/outbox will be retried; exception supplies no coverage or acceptance.
                    do! Task.Delay(TimeSpan.FromSeconds 5., token)
            with :? OperationCanceledException when token.IsCancellationRequested -> ()
        }
