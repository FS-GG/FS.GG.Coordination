namespace FS.GG.Coordination.Cli

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module DeliveryCommand =
    type private ResultBuilder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return value = Ok value
        member _.ReturnFrom value = value
        member _.Zero() = Ok()
        member _.Delay(generator) = generator
        member _.Run(generator) = generator()
        member _.Combine(value, continuation) = Result.bind (fun () -> continuation()) value

    let private result = ResultBuilder()

    let private usage =
        "delivery <inspect|plan|advance> (--observation FILE [--provider-response FILE] | --provider github --repository OWNER/REPO --pr NUMBER --token-env NAME --policy-ref REF --epoch-repository OWNER/REPO --epoch-ref REF --epoch-path PATH --journal-repository OWNER/REPO) [--plan FILE]"

    let private parseOptions (arguments: string array) =
        let values = Dictionary<string, string>(StringComparer.Ordinal)
        let mutable index = 0
        let mutable error = None

        while index < arguments.Length && error.IsNone do
            if index + 1 >= arguments.Length || not (arguments[index].StartsWith("--", StringComparison.Ordinal)) then
                error <- Some "expected --name value"
            elif values.ContainsKey arguments[index] then
                error <- Some $"duplicate option: {arguments[index]}"
            else
                values.Add(arguments[index], arguments[index + 1])
                index <- index + 2

        match error with Some value -> Error value | None -> Ok values

    let private text (name: string) (root: JsonElement) = root.GetProperty(name).GetString()

    let private conclusion =
        function
        | "passed" -> CheckPassed
        | "failed" -> CheckFailed
        | "pending" -> CheckPending
        | _ -> CheckUnknown

    let private conclusionText =
        function
        | CheckPassed -> "passed"
        | CheckFailed -> "failed"
        | CheckPending -> "pending"
        | CheckUnknown -> "unknown"

    let private readObservation path =
        try
            use document = JsonDocument.Parse(File.ReadAllBytes path)
            let root = document.RootElement
            let checks =
                root.GetProperty("checks").EnumerateArray()
                |> Seq.map (fun check ->
                    { Identity = text "identity" check; AppId = check.GetProperty("appId").GetInt64(); Conclusion = conclusion (text "conclusion" check) })
                |> Seq.toList

            Ok
                {
                    Repository = text "repository" root
                    RepositoryId = root.GetProperty("repositoryId").GetInt64()
                    PullRequestNumber = root.GetProperty("pullRequestNumber").GetInt32()
                    PullRequestNodeId = text "pullRequestNodeId" root
                    BaseRef = text "baseRef" root
                    BaseSha = text "baseSha" root
                    HeadSha = text "headSha" root
                    PolicyRevision = text "policyRevision" root
                    Checks = checks
                    Epoch = text "epoch" root
                    EpochGeneration = root.GetProperty("epochGeneration").GetInt64()
                    EpochCommit = text "epochCommit" root
                    JournalGeneration = root.GetProperty("journalGeneration").GetInt64()
                    JournalHead = text "journalHead" root
                    SourceComplete = root.GetProperty("sourceComplete").GetBoolean()
                    ChecksComplete = root.GetProperty("checksComplete").GetBoolean()
                    Authorized = root.GetProperty("authorized").GetBoolean()
                    Supported = root.GetProperty("supported").GetBoolean()
                }
        with error -> Error error.Message

    let private failureText = sprintf "%A"

    let private report failures =
        failures |> List.map failureText |> String.concat "," |> eprintfn "delivery-refused:%s"
        3

    let private observationJson (providerMode: string) (value: OrdinaryDeliveryObservation) =
        let checks = JsonArray()
        for check in value.Checks do
            let item = JsonObject()
            item.Add("appId", check.AppId)
            item.Add("conclusion", conclusionText check.Conclusion)
            item.Add("identity", check.Identity)
            checks.Add item
        let root = JsonObject()
        root.Add("authorized", value.Authorized)
        root.Add("baseRef", value.BaseRef)
        root.Add("baseSha", value.BaseSha.ToLowerInvariant())
        root.Add("checks", checks)
        root.Add("checksComplete", value.ChecksComplete)
        root.Add("epoch", value.Epoch)
        root.Add("epochCommit", value.EpochCommit.ToLowerInvariant())
        root.Add("epochGeneration", value.EpochGeneration)
        root.Add("headSha", value.HeadSha.ToLowerInvariant())
        root.Add("journalGeneration", value.JournalGeneration)
        root.Add("journalHead", value.JournalHead.ToLowerInvariant())
        root.Add("policyRevision", value.PolicyRevision.ToLowerInvariant())
        root.Add("pullRequestNodeId", value.PullRequestNodeId)
        root.Add("pullRequestNumber", value.PullRequestNumber)
        root.Add("providerMode", providerMode)
        root.Add("repository", value.Repository.ToLowerInvariant())
        root.Add("repositoryId", value.RepositoryId)
        root.Add("sourceComplete", value.SourceComplete)
        root.Add("supported", value.Supported)
        root.ToJsonString(JsonSerializerOptions(WriteIndented = false))

    type private ControlledRuntime(observation, providerPath: string) =
        let mutable journal: OrdinaryJournalAuthority option = None
        let mutable effect: OrdinaryEffectObservation = OrdinaryEffectObservation.EffectProvenAbsent
        let mutable mutations = 0
        let mutable dispatches = 0

        do
            if not (String.IsNullOrWhiteSpace providerPath) then
                use document = JsonDocument.Parse(File.ReadAllBytes providerPath)
                let root = document.RootElement
                effect <-
                    match text "effect" root with
                    | "applied" -> OrdinaryEffectObservation.EffectApplied(text "mergeCommit" root)
                    | "unknown" -> OrdinaryEffectObservation.EffectOutcomeUnknown
                    | _ -> OrdinaryEffectObservation.EffectProvenAbsent

        member _.Mutations = mutations
        member _.Dispatches = dispatches

        interface IOrdinaryDeliveryRuntime with
            member _.Observe() = Ok observation
            member _.ObserveJournal _ = Ok journal
            member _.PersistIntent(expected, digest, operation) =
                if journal.IsSome || expected <> observation.JournalGeneration then CasConflict else
                let value = { OperationId = operation; PlanDigest = digest; Generation = expected + 1L; Stage = OrdinaryIntentPersisted; MergeCommit = None }
                journal <- Some value
                mutations <- mutations + 1
                CasAccepted value
            member _.MarkPending(expected, operation) =
                match journal with
                | Some current when current.Generation = expected && current.OperationId = operation ->
                    let value = { current with Generation = expected + 1L; Stage = OrdinaryEffectPending }
                    journal <- Some value
                    mutations <- mutations + 1
                    CasAccepted value
                | _ -> CasConflict
            member _.ObserveEffect _ = Ok effect
            member _.DispatchMerge(operation, _, _, expectedHead) =
                dispatches <- dispatches + 1
                match effect with
                | OrdinaryEffectObservation.EffectApplied commit -> DispatchApplied commit
                | OrdinaryEffectObservation.EffectOutcomeUnknown -> DispatchOutcomeUnknown
                | OrdinaryEffectObservation.EffectProvenAbsent ->
                    let commit = expectedHead
                    effect <- OrdinaryEffectObservation.EffectApplied commit
                    DispatchApplied commit
            member _.PersistSettlement(expected, operation, commit) =
                match journal with
                | Some current when current.Generation = expected && current.OperationId = operation ->
                    let value = { current with Generation = expected + 1L; Stage = OrdinarySettled; MergeCommit = Some commit }
                    journal <- Some value
                    mutations <- mutations + 1
                    CasAccepted value
                | _ -> CasConflict

    let private githubRuntime (options: Dictionary<string, string>) =
        let required name =
            match options.TryGetValue name with
            | true, value when not (String.IsNullOrWhiteSpace value) -> Ok value
            | _ -> Error $"missing {name}"

        result {
            let! repository = required "--repository"
            let! prText = required "--pr"
            let! tokenEnvironment = required "--token-env"
            let! policyRef = required "--policy-ref"
            let! epochRepository = required "--epoch-repository"
            let! epochRef = required "--epoch-ref"
            let! epochPath = required "--epoch-path"
            let! journalRepository = required "--journal-repository"
            let apiBase =
                match options.TryGetValue "--api-base" with
                | true, value -> value
                | _ -> "https://api.github.com/"
            let mutable pr = 0
            let mutable uri = Unchecked.defaultof<Uri>
            if not (Int32.TryParse(prText, &pr)) || pr <= 0 then return! Error "invalid --pr"
            if not (Uri.TryCreate(apiBase, UriKind.Absolute, &uri)) then return! Error "invalid --api-base"
            let token = Environment.GetEnvironmentVariable tokenEnvironment
            if String.IsNullOrWhiteSpace token then return! Error $"missing token environment: {tokenEnvironment}"
            let handler = new HttpClientHandler(AllowAutoRedirect = false)
            let client = new HttpClient(handler, true, Timeout = TimeSpan.FromSeconds 30.0)
            let runtime =
                OrdinaryGitHubRuntime.Runtime(
                    {
                        ApiBase = uri
                        Token = token
                        UserAgent = "fsgg-coordination/0.1.0"
                        Repository = repository
                        PullRequestNumber = pr
                        PolicyRef = policyRef
                        EpochRepository = epochRepository
                        EpochRef = epochRef
                        EpochPath = epochPath
                        JournalRepository = journalRepository
                    },
                    HttpOrdinaryGitHubTransport(client)
                )
            return runtime :> IOrdinaryDeliveryRuntime
        }

    let private runWithObservation (providerMode: string) (operation: string) (options: Dictionary<string, string>) observation (runtime: IOrdinaryDeliveryRuntime option) metrics =
        match operation with
        | "inspect" ->
            match OrdinaryDelivery.inspect observation with
            | Ok value -> printfn "%s" (observationJson providerMode value); 0
            | Error failures -> report failures
        | "plan" ->
            match OrdinaryDelivery.plan observation with
            | Ok(_, bytes) -> Console.OpenStandardOutput().Write(bytes); 0
            | Error failures -> report failures
        | "advance" when options.ContainsKey "--plan" && runtime.IsSome ->
            match OrdinaryDelivery.advance (ReadOnlyMemory(File.ReadAllBytes options["--plan"])) NoCut runtime.Value with
            | Error failures -> report failures
            | Ok result ->
                let measurements = metrics |> Option.map (fun read -> read()) |> Option.defaultValue ""
                printfn "{\"providerMode\":\"%s\",\"providerOutcome\":\"%s\",\"result\":%s%s}" providerMode (if providerMode = "github" then "native-readback" else "simulated") (JsonSerializer.Serialize(string result)) measurements
                0
        | "advance" -> eprintfn "advance requires --plan and a configured runtime; %s" usage; 2
        | _ -> eprintfn "%s" usage; 2

    let run arguments =
        match arguments |> Array.toList with
        | operation :: tail ->
            match parseOptions (List.toArray tail) with
            | Error error -> eprintfn "%s; %s" error usage; 2
            | Ok options ->
                if options.ContainsKey "--observation" then
                    match readObservation options["--observation"] with
                    | Error error -> eprintfn "observation-refused:%s" error; 3
                    | Ok observation ->
                        if operation = "advance" && not (options.ContainsKey "--provider-response") then
                            eprintfn "controlled advance requires --provider-response; %s" usage
                            2
                        else
                            let providerPath =
                                match options.TryGetValue "--provider-response" with true, value -> value | _ -> ""
                            let runtime = ControlledRuntime(observation, providerPath)
                            let metrics () = $",\"mutations\":{runtime.Mutations},\"dispatches\":{runtime.Dispatches}"
                            runWithObservation "controlled" operation options observation (Some(runtime :> IOrdinaryDeliveryRuntime)) (Some metrics)
                else
                    match options.TryGetValue "--provider" with
                    | true, "github" ->
                        match githubRuntime options with
                        | Error error -> eprintfn "provider-refused:%s; %s" error usage; 2
                        | Ok runtime ->
                            match runtime.Observe() with
                            | Error error -> eprintfn "observation-refused:%s" error; 3
                            | Ok observation -> runWithObservation "github" operation options observation (Some runtime) None
                    | _ ->
                        eprintfn "%s" usage
                        2
        | [] -> eprintfn "%s" usage; 2
