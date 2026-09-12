open System
open System.Text.Json
open System.Threading
open System.Net.Http
open System.Runtime.InteropServices
open Akka.Actor
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Execution

let private usage () =
    eprintfn "usage: fsgg-coord-orchestration-host init --connection-file <absolute-private-path>"
    eprintfn "   or: fsgg-coord-orchestration-host serve ... [--github-token-file <path> --github-repository <owner/repo> --github-issue-number <n> --github-base-ref <ref>]"
    2

[<EntryPoint>]
let main arguments =
    match Array.tryHead arguments with
    | Some "init" ->
        match HostConfiguration.parseInit arguments[1..] with
        | Error reason -> eprintfn "%s" reason; 2
        | Ok connection ->
            try
                let identity = HostInitialization.initialize connection CancellationToken.None |> _.GetAwaiter().GetResult()
                printfn "%s" (JsonSerializer.Serialize {| schema = "fsgg.orchestration.host-init/1"; backupIdentity = identity |})
                0
            with error -> eprintfn "initialization-refused:%s" error.Message; 3
    | Some "serve" ->
        match HostConfiguration.parseServe arguments[1..] with
        | Error reason -> eprintfn "%s" reason; 2
        | Ok configuration ->
            try
                let source, store, executionStore = HostRuntime.createProductionStores configuration
                use source = source
                use shutdown = new CancellationTokenSource()
                Console.CancelKeyPress.Add(fun event -> event.Cancel <- true; shutdown.Cancel())
                use terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, fun context -> context.Cancel <- true; shutdown.Cancel())
                match HostedWriterJournal.persistStartupPause TimeProvider.System store.WorkItems configuration.WorkItemId configuration.PilotPrincipalId shutdown.Token |> _.GetAwaiter().GetResult() with
                | Error reason -> failwith $"startup-pause-refused:{reason}"
                | Ok _ -> ()
                match configuration.GitHub with
                | None->HostRuntime.serve TimeProvider.System configuration store shutdown.Token |> _.GetAwaiter().GetResult()
                | Some githubConfiguration->
                    use httpClient=new HttpClient()
                    use actorSystem=ActorSystem.Create("fsgg-coordination-main")
                    let relay=HostExecutorRelay(4,2*1024*1024)
                    let githubExecutor=HttpGitHubRequestExecutor(httpClient,githubConfiguration.Token,2*1024*1024) :> IGitHubRequestExecutor
                    let publisher=GitBundlePublisher(githubConfiguration.RemoteUri,githubConfiguration.Token,1024*1024) :> IGitCandidatePublisher
                    let github=GitHubRouteClient(githubExecutor,publisher,{ApiRoot=githubConfiguration.ApiRoot;Repository=githubConfiguration.Repository;IssueNumber=githubConfiguration.IssueNumber;Principal=configuration.PilotPrincipalId;BaseRef=githubConfiguration.BaseRef;RoutineOperation=githubConfiguration.RoutineOperation;ClaimLease=TimeSpan.FromMinutes 30.},TimeProvider.System)
                    let admission=
                        MainProductionAdmission(actorSystem,TimeProvider.System,store.WorkItems,store.Candidates,executionStore,
                            configuration.WorkItemId,configuration.PilotPrincipalId,github,relay,shutdown.Token)
                        :> IMainRouteAdmissionHandler
                    HostRuntime.serveMain TimeProvider.System configuration store relay admission shutdown.Token |> _.GetAwaiter().GetResult()
                    actorSystem.Terminate()|>ignore
                0
            with error -> eprintfn "host-refused:%s" error.Message; 3
    | _ -> usage ()
