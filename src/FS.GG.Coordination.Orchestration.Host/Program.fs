open System
open System.IO
open System.Text.Json
open System.Threading
open System.Net.Http
open System.Runtime.InteropServices
open Akka.Actor
open Npgsql
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Runner.Client

let private usage () =
    eprintfn "usage: fsgg-coord-orchestration-host init --connection-file <absolute-private-path>"

    eprintfn
        "   or: fsgg-coord-orchestration-host prepare-main-admission --connection-file <path> --store-id <id> --backup-identity <guid> --minimum-generation-fence <n> --pilot-principal <id> --repository-node-id <id> --repository-database-id <n> --issue-node-id <id> --issue-database-id <n> --request-file <path> --input-file <path> --output-file <path>"

    eprintfn
        "   or: fsgg-coord-orchestration-host verify-installed-adoption --connection-file <absolute-private-path> --request-file <absolute-private-path>"

    eprintfn
        "   or: fsgg-coord-orchestration-host serve ... [--github-token-file <path> --github-repository <owner/repo> --github-issue-number <n> --github-base-ref <ref> --runner-executable <absolute-path> --runner-repository-root <absolute-path> --runner-workspace-root <absolute-path> --runner-input-root <absolute-path> --runner-state-root <absolute-path> --runner-artifact-root <absolute-path> --codex-executable <absolute-path> --executor-binding <identity> [--telemetry-executable <absolute-path> --telemetry-config <absolute-path> --telemetry-credential-file <absolute-path> --telemetry-ca-file <absolute-path> --telemetry-outbox <absolute-path> --telemetry-binding-digest <sha256> --telemetry-repository <owner/repo>]]"

    2

[<EntryPoint>]
let main arguments =
    match Array.tryHead arguments with
    | Some "init" ->
        match HostConfiguration.parseInit arguments[1..] with
        | Error reason ->
            eprintfn "%s" reason
            2
        | Ok connection ->
            try
                let identity =
                    HostInitialization.initialize connection CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                printfn
                    "%s"
                    (JsonSerializer.Serialize
                        {|
                            schema = "fsgg.orchestration.host-init/1"
                            backupIdentity = identity
                        |})

                0
            with error ->
                eprintfn "initialization-refused:%s" error.Message
                3
    | Some "prepare-main-admission" ->
        match HostConfiguration.parseMainAdmissionPreparer arguments[1..] with
        | Error reason ->
            eprintfn "%s" reason
            2
        | Ok configuration ->
            try
                let readBounded maximum path =
                    let info = FileInfo path

                    if not info.Exists || info.Length <= 0L || info.Length > int64 maximum then
                        failwith "preparation-input-size-refused"

                    File.ReadAllBytes path

                let requestBytes = readBounded 65536 configuration.RequestFile
                let inputBytes = readBounded (1024 * 1024) configuration.InputFile

                let request =
                    MainAdmissionPreparer.decodeRequest requestBytes |> Result.defaultWith failwith

                use source = NpgsqlDataSource.Create configuration.ConnectionString

                let options: StoreOptions =
                    {
                        DataSource = source
                        StoreId = configuration.StoreId
                        BackupIdentity = configuration.BackupIdentity
                        MinimumGenerationFence = configuration.MinimumGenerationFence
                        RuntimeSchemaVersion = 2
                        SupportedEventSchemaVersions = set[1]
                        SupportedSerializerVersions =
                            set[EventEnvelope.legacySerializerVersion
                                EventEnvelope.serializerVersion]
                        MaximumCandidateBytes = 104857600L
                    }

                let workItems = PostgreSqlStore(options) :> IJournalStore
                let executions = PostgreSqlExecutionStore(options)

                match
                    MainAdmissionPreparer.prepare
                        TimeProvider.System
                        workItems
                        executions
                        executions
                        configuration.WorkItemId
                        configuration.PilotPrincipalId
                        request
                        inputBytes
                        CancellationToken.None
                    |> _.GetAwaiter().GetResult()
                with
                | Error reason ->
                    eprintfn "main-admission-preparation-refused:%s" reason
                    3
                | Ok bytes ->
                    match MainAdmissionPreparer.writeAtomicPrivate configuration.OutputFile bytes with
                    | Error reason ->
                        eprintfn "%s" reason
                        3
                    | Ok() ->
                        printfn "%s" configuration.OutputFile
                        0
            with error ->
                eprintfn "main-admission-preparation-refused:%s" error.Message
                3
    | Some "verify-installed-adoption" ->
        let executablePath =
            Environment.ProcessPath |> Option.ofObj |> Option.defaultValue ""

        let runtime =
            {
                SourceRevision = InstalledAdoptionVerification.embeddedSourceRevision ()
                ExecutablePath = executablePath
                Clock = TimeProvider.System
                AfterAReserved = None
            }

        let result =
            match HostConfiguration.parseInstalledAdoptionVerification arguments[1..] with
            | Error reason -> InstalledAdoptionVerification.configurationFailure runtime reason
            | Ok configuration ->
                InstalledAdoptionVerification.run
                    configuration.ConnectionString
                    configuration.RequestBytes
                    runtime
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()

        printfn "%s" (InstalledAdoptionVerification.serializeResult result)
        if result.Passed then 0 else 3
    | Some "serve" ->
        match HostConfiguration.parseServe arguments[1..] with
        | Error reason ->
            eprintfn "%s" reason
            2
        | Ok configuration ->
            try
                let source, store, executionStore = HostRuntime.createProductionStores configuration
                use source = source
                use shutdown = new CancellationTokenSource()

                Console.CancelKeyPress.Add(fun event ->
                    event.Cancel <- true
                    shutdown.Cancel())

                use terminate =
                    PosixSignalRegistration.Create(
                        PosixSignal.SIGTERM,
                        fun context ->
                            context.Cancel <- true
                            shutdown.Cancel()
                    )

                match
                    HostedWriterJournal.persistStartupPause
                        TimeProvider.System
                        store.WorkItems
                        configuration.WorkItemId
                        configuration.PilotPrincipalId
                        shutdown.Token
                    |> _.GetAwaiter().GetResult()
                with
                | Error reason -> failwith $"startup-pause-refused:{reason}"
                | Ok _ -> ()

                match configuration.GitHub with
                | None ->
                    HostRuntime.serve TimeProvider.System configuration store shutdown.Token
                    |> _.GetAwaiter().GetResult()
                | Some githubConfiguration ->
                    let localConfiguration =
                        configuration.LocalExecutor
                        |> Option.defaultWith (fun () -> failwith "local-executor-configuration-required")

                    use transport = new LocalExecutorTransport(localConfiguration)
                    use httpClient = new HttpClient()
                    use actorSystem = ActorSystem.Create("fsgg-coordination-main")

                    let githubExecutor =
                        HttpGitHubRequestExecutor(httpClient, githubConfiguration.Token, 2 * 1024 * 1024)
                        :> IGitHubRequestExecutor

                    let publisher =
                        GitBundlePublisher(githubConfiguration.RemoteUri, githubConfiguration.Token, 1024 * 1024)
                        :> IGitCandidatePublisher

                    let github =
                        GitHubRouteClient(
                            githubExecutor,
                            publisher,
                            {
                                ApiRoot = githubConfiguration.ApiRoot
                                Repository = githubConfiguration.Repository
                                IssueNumber = githubConfiguration.IssueNumber
                                Principal = configuration.PilotPrincipalId
                                BaseRef = githubConfiguration.BaseRef
                                RoutineOperation = githubConfiguration.RoutineOperation
                                ClaimLease = TimeSpan.FromMinutes 30.
                            },
                            TimeProvider.System
                        )

                    let outcomeBridge =
                        localConfiguration.Telemetry
                        |> Option.map (fun telemetry ->
                            let outbox =
                                Path.Combine(Path.GetDirectoryName telemetry.Outbox, "host-outcome-outbox")

                            let publisher =
                                TelemetryCliPublisher
                                    {
                                        Executable = telemetry.Executable
                                        Config = telemetry.Config
                                        CredentialFile = telemetry.CredentialFile
                                        CertificateAuthorityFile = telemetry.CertificateAuthorityFile
                                        Outbox = outbox
                                        Repository = telemetry.Repository
                                        BindingDigest = telemetry.BindingDigest
                                    }

                            TelemetryOutcomeBridge(telemetry.Repository, github, publisher))

                    let admission =
                        MainProductionAdmission(
                            actorSystem,
                            TimeProvider.System,
                            store.WorkItems,
                            store.Candidates,
                            executionStore,
                            configuration.WorkItemId,
                            configuration.PilotPrincipalId,
                            github,
                            transport,
                            shutdown.Token,
                            ?outcomeBridge = outcomeBridge
                        )
                        :> IMainRouteAdmissionHandler

                    HostRuntime.serveMainLocal TimeProvider.System configuration store admission shutdown.Token
                    |> _.GetAwaiter().GetResult()

                    actorSystem.Terminate() |> ignore

                0
            with error ->
                eprintfn "host-refused:%s" error.Message
                3
    | _ -> usage ()
