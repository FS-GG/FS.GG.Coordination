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
if not skipCold then
    for command in selected do
        let executable = text "executable" command
        let info = ProcessStartInfo(executable)
        info.WorkingDirectory <- root; info.UseShellExecute <- false; info.RedirectStandardOutput <- true; info.RedirectStandardError <- true
        for argument in command.GetProperty("args").EnumerateArray() do info.ArgumentList.Add(argument.GetString())
        use child = Process.Start info
        let output, error = child.StandardOutput.ReadToEnd(), child.StandardError.ReadToEnd()
        child.WaitForExit()
        if child.ExitCode <> 0 then failwithf "cold command %s failed (%d): %s %s" (text "id" command) child.ExitCode output error
let contract = json "evidence/github-substrate-v2/gs2-07-8/contract.json"
if text "closureMode" contract.RootElement <> "comprehensive-cold" then failwith "closure mode differs"
printfn "GITHUB_RUNTIME_RECOVERY_OK children=%d commands=%d cold=%b model=%s" accepted.Length selected.Length (not skipCold) (shaFile "eng/quint-qualification.json")
