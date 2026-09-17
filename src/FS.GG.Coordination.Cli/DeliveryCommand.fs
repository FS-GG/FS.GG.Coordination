namespace FS.GG.Coordination.Cli

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module DeliveryCommand =
    let private usage = "delivery <inspect|plan|advance> --observation FILE [--plan FILE] [--provider-response FILE]"

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

    let private observationJson (value: OrdinaryDeliveryObservation) =
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
        root.Add("headSha", value.HeadSha.ToLowerInvariant())
        root.Add("journalGeneration", value.JournalGeneration)
        root.Add("journalHead", value.JournalHead.ToLowerInvariant())
        root.Add("policyRevision", value.PolicyRevision.ToLowerInvariant())
        root.Add("pullRequestNodeId", value.PullRequestNodeId)
        root.Add("pullRequestNumber", value.PullRequestNumber)
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

    let run arguments =
        match arguments |> Array.toList with
        | operation :: tail ->
            match parseOptions (List.toArray tail) with
            | Error error -> eprintfn "%s; %s" error usage; 2
            | Ok options when not (options.ContainsKey "--observation") -> eprintfn "%s" usage; 2
            | Ok options ->
                match readObservation options["--observation"] with
                | Error error -> eprintfn "observation-refused:%s" error; 3
                | Ok observation ->
                    match operation with
                    | "inspect" ->
                        match OrdinaryDelivery.inspect observation with
                        | Ok value -> printfn "%s" (observationJson value); 0
                        | Error failures -> report failures
                    | "plan" ->
                        match OrdinaryDelivery.plan observation with
                        | Ok(_, bytes) -> Console.OpenStandardOutput().Write(bytes); 0
                        | Error failures -> report failures
                    | "advance" when options.ContainsKey "--plan" && options.ContainsKey "--provider-response" ->
                        let runtime = ControlledRuntime(observation, options["--provider-response"])
                        match OrdinaryDelivery.advance (ReadOnlyMemory(File.ReadAllBytes options["--plan"])) NoCut runtime with
                        | Error failures -> report failures
                        | Ok result ->
                            printfn "{\"dispatches\":%d,\"journalMutations\":%d,\"result\":\"%s\"}" runtime.Dispatches runtime.Mutations (string result)
                            0
                    | "advance" -> eprintfn "advance requires --plan and --provider-response; %s" usage; 2
                    | _ -> eprintfn "%s" usage; 2
        | [] -> eprintfn "%s" usage; 2
