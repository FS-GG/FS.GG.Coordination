#load "../src/FS.GG.Coordination.Qualification.Contracts/QualificationReuse.fs"

open System
open System.IO
open System.Text
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts.QualificationReuse

let args = fsi.CommandLineArgs |> Array.skip 1 |> Array.filter ((<>) "--") |> Array.toList
let option name = args |> List.tryFindIndex ((=) name) |> Option.bind (fun i -> args |> List.tryItem (i + 1))
let required name = option name |> Option.defaultWith (fun () -> failwith $"{name} is required")
match args.Head with
| "prepare" ->
    let identity =
        { BehavioralSha256 = required "--behavioral"; CompiledContractSha256 = required "--contract"
          ToolchainProfileSha256 = required "--toolchain"; VerificationBoundsSha256 = required "--bounds"
          FormalCorpusSha256 = required "--corpus"; HarnessSha256 = required "--harness"; BindingSha256 = required "--binding" }
    let obligation = createCandidateObligation (required "--candidate") (required "--base") (required "--tree") (required "--source") identity
    let obligations = required "--obligations" |> fun value -> value.Split(',', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
    let plan = createPartitionPlan obligation (option "--partitions" |> Option.map int |> Option.defaultValue 6) obligations
    let outputRoot = required "--output-root" |> Path.GetFullPath
    Directory.CreateDirectory outputRoot |> ignore
    File.WriteAllBytes(Path.Combine(outputRoot, "candidate-obligation.json"), candidateObligationBytes obligation)
    File.WriteAllBytes(Path.Combine(outputRoot, "partition-plan.json"), partitionPlanBytes plan)
    printfn "%s %d" plan.PlanSha256 plan.PartitionCount
| "run-partition" ->
    let obligation = File.ReadAllBytes(required "--obligation") |> parseCandidateObligation |> Result.defaultWith failwith
    let plan = File.ReadAllBytes(required "--plan") |> parsePartitionPlan obligation |> Result.defaultWith failwith
    let partition = required "--partition" |> int
    let obligations = plan.Partitions |> List.find (fst >> (=) partition) |> snd
    let passed = required "--passed" |> Boolean.Parse
    File.WriteAllBytes(required "--output", createPartitionReceipt plan partition obligations passed |> partitionReceiptBytes)
| "classify" ->
    let current = File.ReadAllBytes(required "--obligation") |> parseCandidateObligation |> Result.defaultWith failwith
    let prior =
        option "--prior-obligation"
        |> Option.map (fun path ->
            let old = File.ReadAllBytes path |> parseCandidateObligation |> Result.defaultWith failwith
            { Candidate = old; RunId = required "--prior-run" |> int64; Attempt = required "--prior-attempt" |> int
              ExecutedReceiptSha256 = required "--prior-receipt"; CompletedAt = required "--prior-completed"
              ExpiresAt = required "--prior-expires"; Authentic = true; Complete = true })
    let semanticFields value =
        [ value.Identity.BehavioralSha256; value.Identity.CompiledContractSha256; value.Identity.ToolchainProfileSha256
          value.Identity.VerificationBoundsSha256; value.Identity.FormalCorpusSha256; value.Identity.HarnessSha256 ]
    let oldFields = prior |> Option.map (fun value -> semanticFields value.Candidate)
    let currentFields = semanticFields current
    let empty = oldFields = Some currentFields
    let deltaBytes = Encoding.UTF8.GetBytes(String.concat "\n" ((oldFields |> Option.defaultValue []) @ currentFields))
    let semantic = { EvaluatorSha256 = sha256 (File.ReadAllBytes(Path.Combine(__SOURCE_DIRECTORY__, "optimistic-validation.fsx"))); DeltaSha256 = sha256 deltaBytes; IsEmpty = empty }
    option "--aggregate-receipt"
    |> Option.iter (fun path ->
        let aggregate = File.ReadAllBytes path |> parseCoherentAggregateReceipt |> Result.defaultWith failwith
        if not aggregate.Passed || aggregate.CandidateObligationSha256 <> (prior |> Option.map (fun value -> value.Candidate.ObligationSha256) |> Option.defaultValue "") then
            failwith "prior aggregate receipt does not bind the prior candidate")
    let selected = selectReusable DateTimeOffset.UtcNow current prior semantic (option "--binding-correspondence")
    File.WriteAllBytes(required "--output", selectionBytes selected)
| "aggregate" ->
    let obligation = File.ReadAllBytes(required "--obligation") |> parseCandidateObligation |> Result.defaultWith failwith
    let plan = File.ReadAllBytes(required "--plan") |> parsePartitionPlan obligation |> Result.defaultWith failwith
    let receipts = Directory.GetFiles(required "--receipts", "receipt.json", SearchOption.AllDirectories) |> Array.map (File.ReadAllBytes >> parsePartitionReceipt >> Result.defaultWith failwith) |> Array.toList
    match createCoherentAggregateReceipt plan receipts with
    | Ok receipt when receipt.Passed ->
        option "--output" |> Option.iter (fun path -> File.WriteAllBytes(path, coherentAggregateReceiptBytes receipt))
        printfn "coherent aggregate passed: %s" plan.PlanSha256
    | Ok _ -> failwith "coherent aggregate contains a failed partition"
    | Error error -> failwith error
| mode -> failwith $"unsupported optimistic validation phase: {mode}"
