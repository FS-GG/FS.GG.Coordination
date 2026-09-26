#load "../src/FS.GG.Coordination.Qualification.Contracts/QualificationReuse.fs"

open System
open System.IO
open System.Text
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts.QualificationReuse

let args =
    fsi.CommandLineArgs |> Array.skip 1 |> Array.filter ((<>) "--") |> Array.toList

let option name =
    args
    |> List.tryFindIndex ((=) name)
    |> Option.bind (fun i -> args |> List.tryItem (i + 1))

let required name =
    option name |> Option.defaultWith (fun () -> failwith $"{name} is required")

match args.Head with
| "prepare" ->
    let identity =
        {
            BehavioralSha256 = required "--behavioral"
            CompiledContractSha256 = required "--contract"
            ToolchainProfileSha256 = required "--toolchain"
            VerificationBoundsSha256 = required "--bounds"
            FormalCorpusSha256 = required "--corpus"
            HarnessSha256 = required "--harness"
            BindingSha256 = required "--binding"
        }

    let obligation =
        createCandidateObligation
            (required "--candidate")
            (required "--base")
            (required "--tree")
            (required "--source")
            identity

    let obligations =
        required "--obligations"
        |> fun value -> value.Split(',', StringSplitOptions.RemoveEmptyEntries) |> Array.toList

    let plan =
        createPartitionPlan
            obligation
            (required "--qualification-plan")
            (option "--partitions" |> Option.map int |> Option.defaultValue 6)
            obligations

    let outputRoot = required "--output-root" |> Path.GetFullPath
    Directory.CreateDirectory outputRoot |> ignore
    File.WriteAllBytes(Path.Combine(outputRoot, "candidate-obligation.json"), candidateObligationBytes obligation)
    File.WriteAllBytes(Path.Combine(outputRoot, "partition-plan.json"), partitionPlanBytes plan)
    printfn "%s %d" plan.PlanSha256 plan.PartitionCount
| "run-partition" ->
    let obligation =
        File.ReadAllBytes(required "--obligation")
        |> parseCandidateObligation
        |> Result.defaultWith failwith

    let plan =
        File.ReadAllBytes(required "--plan")
        |> parsePartitionPlan obligation
        |> Result.defaultWith failwith

    let partition = required "--partition" |> int
    let obligations = plan.Partitions |> List.find (fst >> (=) partition) |> snd
    let passed = required "--passed" |> Boolean.Parse

    File.WriteAllBytes(
        required "--output",
        createPartitionReceipt plan partition obligations passed
        |> partitionReceiptBytes
    )
| "partition-obligation" ->
    let obligation =
        File.ReadAllBytes(required "--obligation")
        |> parseCandidateObligation
        |> Result.defaultWith failwith

    let plan =
        File.ReadAllBytes(required "--plan")
        |> parsePartitionPlan obligation
        |> Result.defaultWith failwith

    let partition = required "--partition" |> int

    resolvePartitionObligation plan partition
    |> Result.defaultWith failwith
    |> printfn "%s"
| "validate-prior" ->
    let current =
        File.ReadAllBytes(required "--obligation")
        |> parseCandidateObligation
        |> Result.defaultWith failwith

    let currentPlan =
        File.ReadAllBytes(required "--current-plan")
        |> parsePartitionPlan current
        |> Result.defaultWith failwith

    let old =
        File.ReadAllBytes(required "--prior-obligation")
        |> parseCandidateObligation
        |> Result.defaultWith failwith

    let priorPlan =
        File.ReadAllBytes(required "--prior-plan")
        |> parsePartitionPlan old
        |> Result.defaultWith failwith

    let aggregate =
        File.ReadAllBytes(required "--aggregate-receipt")
        |> parseCoherentAggregateReceipt
        |> Result.defaultWith failwith

    validatePriorAggregateBinding currentPlan priorPlan aggregate
    |> Result.defaultWith failwith
| "classify" ->
    let current =
        File.ReadAllBytes(required "--obligation")
        |> parseCandidateObligation
        |> Result.defaultWith failwith

    let prior =
        option "--prior-obligation"
        |> Option.map (fun path ->
            let old =
                File.ReadAllBytes path
                |> parseCandidateObligation
                |> Result.defaultWith failwith

            {
                Candidate = old
                RunId = required "--prior-run" |> int64
                Attempt = required "--prior-attempt" |> int
                ExecutedReceiptSha256 = required "--prior-receipt"
                CompletedAt = required "--prior-completed"
                ExpiresAt = required "--prior-expires"
                Authentic = true
                Complete = true
            })

    let semanticFields value =
        [
            value.Identity.BehavioralSha256
            value.Identity.CompiledContractSha256
            value.Identity.ToolchainProfileSha256
            value.Identity.VerificationBoundsSha256
            value.Identity.FormalCorpusSha256
            value.Identity.HarnessSha256
        ]

    let oldFields = prior |> Option.map (fun value -> semanticFields value.Candidate)
    let currentFields = semanticFields current
    let empty = oldFields = Some currentFields

    let deltaBytes =
        Encoding.UTF8.GetBytes(String.concat "\n" ((oldFields |> Option.defaultValue []) @ currentFields))

    let semantic =
        {
            EvaluatorSha256 =
                sha256 (File.ReadAllBytes(Path.Combine(__SOURCE_DIRECTORY__, "optimistic-validation.fsx")))
            DeltaSha256 = sha256 deltaBytes
            IsEmpty = empty
        }

    option "--aggregate-receipt"
    |> Option.iter (fun path ->
        let old =
            prior
            |> Option.map (fun value -> value.Candidate)
            |> Option.defaultWith (fun () -> failwith "prior aggregate receipt requires a prior candidate")

        let currentPlan =
            File.ReadAllBytes(required "--current-plan")
            |> parsePartitionPlan current
            |> Result.defaultWith failwith

        let priorPlan =
            File.ReadAllBytes(required "--prior-plan")
            |> parsePartitionPlan old
            |> Result.defaultWith failwith

        let aggregate =
            File.ReadAllBytes path
            |> parseCoherentAggregateReceipt
            |> Result.defaultWith failwith

        validatePriorAggregateBinding currentPlan priorPlan aggregate
        |> Result.defaultWith failwith)

    let selected =
        selectReusable DateTimeOffset.UtcNow current prior semantic (option "--binding-correspondence")

    File.WriteAllBytes(required "--output", selectionBytes selected)
| "aggregate" ->
    let obligation =
        File.ReadAllBytes(required "--obligation")
        |> parseCandidateObligation
        |> Result.defaultWith failwith

    let plan =
        File.ReadAllBytes(required "--plan")
        |> parsePartitionPlan obligation
        |> Result.defaultWith failwith

    let receiptRoot = required "--receipts"

    let receipts =
        if Directory.Exists receiptRoot then
            Directory.GetFiles(receiptRoot, "receipt.json", SearchOption.AllDirectories)
            |> Array.map (File.ReadAllBytes >> parsePartitionReceipt >> Result.defaultWith failwith)
            |> Array.toList
        else
            []

    match createCoherentAggregateReceipt plan receipts with
    | Ok receipt when receipt.Passed ->
        option "--output"
        |> Option.iter (fun path -> File.WriteAllBytes(path, coherentAggregateReceiptBytes receipt))

        printfn "coherent aggregate passed: %s" plan.PlanSha256
    | Ok _ -> failwith "coherent aggregate contains a failed partition"
    | Error error -> failwith error
| "aggregate-scoped" ->
    let obligation =
        File.ReadAllBytes(required "--obligation")
        |> parseCandidateObligation
        |> Result.defaultWith failwith

    let plan =
        File.ReadAllBytes(required "--plan")
        |> parsePartitionPlan obligation
        |> Result.defaultWith failwith

    let receipts =
        Directory.GetFiles(required "--receipts", "receipt.json", SearchOption.AllDirectories)
        |> Array.map (File.ReadAllBytes >> parsePartitionReceipt >> Result.defaultWith failwith)
        |> Array.sortBy _.Partition
        |> Array.toList

    if plan.PartitionCount <> 6 || (receipts |> List.map _.Partition) <> [ 0; 2; 3; 4; 5 ] then
        failwith "scoped receipt coverage must be exactly the five nonformal partitions"

    for receipt in receipts do
        let expected = plan.Partitions |> List.find (fst >> (=) receipt.Partition) |> snd

        if receipt.PlanSha256 <> plan.PlanSha256 || receipt.Obligations <> expected || not receipt.Passed then
            failwith "scoped receipt is stale, substituted, or failed"

    use profileDocument = JsonDocument.Parse(File.ReadAllBytes(required "--profile"))
    let profile = profileDocument.RootElement
    let get (key: string) = profile.GetProperty(key).GetString()

    if
        get "schema" <> "fsgg.coordination.optimistic-profile/1"
        || get "profile" <> "scoped"
        || get "candidate" <> obligation.Candidate
        || get "baseRevision" <> obligation.BaseRevision
        || get "candidateObligationSha256" <> obligation.ObligationSha256
    then
        failwith "scoped profile does not bind the candidate"

    let payloadBytes =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteString("schema", "fsgg.coordination.scoped-aggregate-receipt/1")
        writer.WriteString("candidateObligationSha256", obligation.ObligationSha256)
        writer.WriteString("planSha256", plan.PlanSha256)
        writer.WriteString("profileSha256", get "profileSha256")
        writer.WriteString("selectionSha256", get "selectionSha256")
        writer.WriteString("fullDonorReceiptSha256", get "fullDonorReceiptSha256")
        writer.WriteStartArray("partitionReceiptSha256")
        receipts |> List.iter (fun receipt -> writer.WriteStringValue receipt.ReceiptSha256)
        writer.WriteEndArray()
        writer.WriteBoolean("passed", true)
        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()

    use payloadDocument = JsonDocument.Parse payloadBytes
    use output = new MemoryStream()
    use writer = new Utf8JsonWriter(output)
    writer.WriteStartObject()

    for property in payloadDocument.RootElement.EnumerateObject() do
        property.WriteTo writer

    writer.WriteString("receiptSha256", sha256 payloadBytes)
    writer.WriteEndObject()
    writer.Flush()
    File.WriteAllBytes(required "--output", Array.append (output.ToArray()) [| byte '\n' |])
| mode -> failwith $"unsupported optimistic validation phase: {mode}"
