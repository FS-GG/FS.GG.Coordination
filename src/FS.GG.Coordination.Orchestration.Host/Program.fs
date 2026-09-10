open System
open System.Text.Json
open System.Threading
open System.Runtime.InteropServices
open FS.GG.Coordination.Orchestration.Host

let private usage () =
    eprintfn "usage: fsgg-coord-orchestration-host init --connection-file <absolute-private-path>"
    eprintfn "   or: fsgg-coord-orchestration-host serve --connection-file <path> --token-file <path> --prefix <loopback-http-root> --store-id <id> --backup-identity <uuid> --minimum-generation-fence <n> --permit-id <uuid> --pilot-principal <id>"
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
                let source, store = HostRuntime.createStore configuration
                use source = source
                use shutdown = new CancellationTokenSource()
                Console.CancelKeyPress.Add(fun event -> event.Cancel <- true; shutdown.Cancel())
                use terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, fun context -> context.Cancel <- true; shutdown.Cancel())
                HostRuntime.serve TimeProvider.System configuration store shutdown.Token |> _.GetAwaiter().GetResult()
                0
            with error -> eprintfn "host-refused:%s" error.Message; 3
    | _ -> usage ()
