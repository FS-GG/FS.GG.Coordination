open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

let args = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList
let skipCold = args |> List.contains "--skip-cold"
let rootArg = args |> List.filter ((<>) "--skip-cold") |> List.tryLast |> Option.defaultWith (fun () -> failwith "usage: dotnet fsi eng/validate-github-runtime-recovery.fsx -- <root> [--skip-cold]")
let root = Path.GetFullPath rootArg
let path relative = Path.Combine(root, relative)
let read relative = File.ReadAllText(path relative)
let shaFile relative = File.ReadAllBytes(path relative) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let json relative = JsonDocument.Parse(read relative)
let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()
let expected =
    [ "github-event-envelope-contract"; "github-narrow-reconciliation-contract"; "github-audit-repair-contract"
      "github-event-security-contract"; "github-merge-group-support-contract"; "github-queue-sandbox-pilot-contract"
      "github-queue-sandbox-recovery-contract"; "github-queue-routine-burst-contract"
      "github-event-benefit-measurement-contract"; "github-event-benefit-provider-observation-contract"
      "github-runtime-operations-contract" ]
let index = json "eng/github-substrate-v2-units.json"
let unit = index.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> text "id" value = "GS2-07.8")
let unitCommands = unit.GetProperty("gateCommands").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
if unitCommands <> expected @ [ "github-runtime-recovery-contract" ] then failwith "GS2-07 comprehensive command inventory differs"
let accepted =
    [ "GS2-07.1", "825781cedeebbd56aad3a3d41499d6f9bbc647da372f8a91df7c7e2a5ed336e1"
      "GS2-07.2", "6ae56a7c9dce52f3ac25e39145b275ed5e8127a1020ee8c65a392b976661c298"
      "GS2-07.3", "4c6a18a3c8cca8ebd59ce040f63f0192c07f9468ad155e7941f676b0b611719c"
      "GS2-07.4", "d2cf3b943fc153047652d73de77bfdcb35fe6a087f414a494eec35542edd2a50"
      "GS2-07.5", "dd321136fe28e135ba5ee29a3b81a2041b81c8eb29126762cf893bb98ece34d8"
      "GS2-07.6", "eaf032038cc3ed1fb3f1a21db81a32f7af7969f84a0d9b77cd1d7eea68346bc6"
      "GS2-07.7", "2cd764adfab89480a1272c329a2ed06101766ee92a6be98f76498c5380440e87" ]
for id, digest in accepted do
    use receipt = json $"evidence/github-substrate-v2/accepted/{id}.json"
    if text "state" receipt.RootElement <> "accepted" || text "digest" receipt.RootElement <> digest then failwithf "accepted child %s differs" id
if shaFile "eng/quint-qualification.json" <> "486e1a956d53f9809f183d336bb97824785b4937f9627e34c558a8c0ef548bc2" then failwith "model identity differs"
let catalog = json "eng/github-substrate-v2-gates.json"
let commands = catalog.RootElement.GetProperty("commands").EnumerateArray() |> Seq.toList
let selected = expected |> List.map (fun id -> commands |> List.find (fun value -> text "id" value = id))
let runAt workingDirectory executable arguments =
    let info = ProcessStartInfo(executable)
    info.WorkingDirectory <- workingDirectory; info.UseShellExecute <- false; info.RedirectStandardOutput <- true; info.RedirectStandardError <- true
    for argument in arguments do info.ArgumentList.Add argument
    use child = Process.Start info
    let output, error = child.StandardOutput.ReadToEnd(), child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output, error
if not skipCold then
    let historicalRoot = Path.Combine(Path.GetTempPath(), $"gs2-07-8-gs2-07-7-{Guid.NewGuid():N}")
    let mutable failure: string option = None
    let setupCode, setupOutput, setupError = runAt root "git" [ "worktree"; "add"; "--detach"; historicalRoot; "32985e9b62a287cb8854dad8da5d1f8561b3a5ee" ]
    if setupCode <> 0 then failwithf "historical GS2-07.7 checkout failed: %s %s" setupOutput setupError
    let restoreCode, restoreOutput, restoreError = runAt historicalRoot "dotnet" [ "restore"; "src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj"; "--locked-mode" ]
    if restoreCode <> 0 then failure <- Some $"historical restore failed: {restoreOutput} {restoreError}"
    if failure.IsNone then
        let buildCode, buildOutput, buildError = runAt historicalRoot "dotnet" [ "build"; "src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj"; "-c"; "Release"; "--no-restore" ]
        if buildCode <> 0 then failure <- Some $"historical build failed: {buildOutput} {buildError}"
    for command in selected do
        if failure.IsNone then
            let id = text "id" command
            let commandRoot = if id.StartsWith("github-event-benefit-", StringComparison.Ordinal) then historicalRoot else root
            let commandArgs = command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
            let code, output, error = runAt commandRoot (text "executable" command) commandArgs
            if code <> 0 then failure <- Some $"cold command {id} failed ({code}): {output} {error}"
    let cleanupCode, cleanupOutput, cleanupError = runAt root "git" [ "worktree"; "remove"; "--force"; historicalRoot ]
    if cleanupCode <> 0 && failure.IsNone then failure <- Some $"historical checkout cleanup failed: {cleanupOutput} {cleanupError}"
    match failure with Some message -> failwith message | None -> ()
let contract = json "evidence/github-substrate-v2/gs2-07-8/contract.json"
if text "closureMode" contract.RootElement <> "comprehensive-cold" then failwith "closure mode differs"
let closure = json "evidence/github-substrate-v2/gs2-07-8/comprehensive-closure.json"
let closureRoot = closure.RootElement
if text "schema" closureRoot <> "fsgg.coordination.github-runtime-closure/1" || text "unit" closureRoot <> "GS2-07.8" || text "parent" closureRoot <> "GS2-07" || text "mode" closureRoot <> "comprehensive-cold" || text "state" closureRoot <> "qualified" then failwith "retained closure identity differs"
if text "sourceRevision" closureRoot <> "b9a78c71dff89e7502e9b1ccdfbd95828fd9d79b" || text "roadmapRevision" closureRoot <> "6d3c8283042184557d4f0db07fcc353571494bb5" || text "roadmapSha256" closureRoot <> "04bad334e0a48ed119bcd0df2b40a6db5333c06b1475c9ce52d078513ce8311c" then failwith "retained closure source differs"
if text "qualificationReportSha256" closureRoot <> shaFile "evidence/github-substrate-v2/gs2-07-8/qualification-report.json" || text "modelIdentity" closureRoot <> shaFile "eng/quint-qualification.json" then failwith "retained closure artifact differs"
let closureCommands = closureRoot.GetProperty("commands").EnumerateArray() |> Seq.map (text "id") |> Seq.toList
if closureCommands <> expected || closureRoot.GetProperty("commands").EnumerateArray() |> Seq.exists (fun value -> text "execution" value <> "cold-fresh-process" || text "result" value <> "pass") then failwith "retained cold command result differs"
let currentReceipt = json "evidence/github-substrate-v2/accepted/GS2-07.8.json"
let currentReceiptRoot = currentReceipt.RootElement
let currentArtifacts = currentReceiptRoot.GetProperty("artifacts").EnumerateArray() |> Seq.toList
let hasArtifact name digest =
    currentArtifacts |> List.exists (fun artifact -> text "name" artifact = name && text "sha256" artifact = digest)
if text "schema" currentReceiptRoot <> "fsgg.coordination.unit-acceptance/1"
   || text "unitId" currentReceiptRoot <> "GS2-07.8"
   || text "state" currentReceiptRoot <> "accepted"
   || text "unitContractSha256" currentReceiptRoot <> "34e0a41c1a379916d87af62dc19f51cba89ebcfb123461543b5974e65b907286"
   || text "sourceRevision" currentReceiptRoot <> "1ae51fdfd696a74f737b96048379a0b7eb64f7cd"
   || text "digest" currentReceiptRoot <> "daf215f425227509b99df5068cbffe352bda4da61fb278efbcf2d9a6ae8f5679"
   || not(hasArtifact "qualification-report" (shaFile "evidence/github-substrate-v2/gs2-07-8/qualification-report.json"))
   || not(hasArtifact "comprehensive-cold-closure" (shaFile "evidence/github-substrate-v2/gs2-07-8/comprehensive-closure.json")) then
    failwith "accepted GS2-07.8 result differs"
printfn "GITHUB_RUNTIME_RECOVERY_OK children=%d commands=%d cold=%b model=%s" accepted.Length selected.Length (not skipCold) (shaFile "eng/quint-qualification.json")
