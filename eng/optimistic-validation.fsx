#load "../src/FS.GG.Coordination.Qualification.Contracts/QualificationReuse.fs"

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts.QualificationReuse

let args = fsi.CommandLineArgs |> Array.skip 1 |> Array.filter ((<>) "--") |> Array.toList
let option name = args |> List.tryFindIndex ((=) name) |> Option.bind (fun i -> args |> List.tryItem (i + 1))
let required name = option name |> Option.defaultWith (fun () -> failwith $"{name} is required")
let candidate = required "--candidate"
let baseRevision = required "--base"
let identity =
    { BehavioralSha256 = required "--behavioral"; CompiledContractSha256 = required "--contract"
      ToolchainProfileSha256 = required "--toolchain"; VerificationBoundsSha256 = required "--bounds"
      FormalCorpusSha256 = required "--corpus"; HarnessSha256 = required "--harness"; BindingSha256 = required "--binding" }
let obligation = createCandidateObligation candidate baseRevision (required "--tree") (required "--source") identity
match args.Head with
| "prepare" ->
    let obligations = required "--obligations" |> fun value -> value.Split(',', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
    let plan = createPartitionPlan obligation (option "--partitions" |> Option.map int |> Option.defaultValue 6) obligations
    let outputRoot = required "--output-root" |> Path.GetFullPath
    Directory.CreateDirectory outputRoot |> ignore
    File.WriteAllBytes(Path.Combine(outputRoot, "candidate-obligation.json"), candidateObligationBytes obligation)
    File.WriteAllBytes(Path.Combine(outputRoot, "partition-plan.json"), partitionPlanBytes plan)
    printfn "%s %d" plan.PlanSha256 plan.PartitionCount
| mode -> failwith $"unsupported optimistic validation phase: {mode}"
