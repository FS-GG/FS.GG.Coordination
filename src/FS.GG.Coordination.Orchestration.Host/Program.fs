open System
open System.Text.Json
open System.Threading
open System.Runtime.InteropServices
open FS.GG.Coordination.Orchestration.Host

let private usage () =
    eprintfn "usage: fsgg-coord-orchestration-host init --connection-file <absolute-private-path>"
    eprintfn "   or: fsgg-coord-orchestration-host serve --connection-file <path> --token-file <path> --runner-token-file <path> --prefix <loopback-http-root> --store-id <id> --backup-identity <uuid> --minimum-generation-fence <n> --permit-id <uuid> --pilot-principal <id> --repository-node-id <id> --repository-database-id <n> --issue-node-id <id> --issue-database-id <n>"
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
                let source, store, _executionStore = HostRuntime.createProductionStores configuration
                use source = source
                use shutdown = new CancellationTokenSource()
                Console.CancelKeyPress.Add(fun event -> event.Cancel <- true; shutdown.Cancel())
                use terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, fun context -> context.Cancel <- true; shutdown.Cancel())
                match HostedWriterJournal.persistStartupPause TimeProvider.System store.WorkItems configuration.WorkItemId configuration.PilotPrincipalId shutdown.Token |> _.GetAwaiter().GetResult() with
                | Error reason -> failwith $"startup-pause-refused:{reason}"
                | Ok _ -> ()
                HostRuntime.serve TimeProvider.System configuration store shutdown.Token |> _.GetAwaiter().GetResult()
                0
            with error -> eprintfn "host-refused:%s" error.Message; 3
    | _ -> usage ()
