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
                for _, admission, intent in page.Admissions do
                    match ExecutionAdmissionFacts.prepare admission intent.Requested.Model intent.Requested.Effort with
                    | Error reason -> gaps.Add reason
                    | Ok(name, bytes) ->
                        match publisher.Queue(name, bytes) with
                        | Ok _ -> queued <- queued + 1
                        | Error reason -> gaps.Add reason
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
