#r "../src/FS.GG.Coordination.Protocol/bin/Release/net10.0/FS.GG.Coordination.Protocol.dll"
#r "../src/FS.GG.Coordination.Core/bin/Release/net10.0/FS.GG.Coordination.Core.dll"
#r "../src/FS.GG.Coordination.GitHub/bin/Release/net10.0/FS.GG.Coordination.GitHub.dll"

open System
open System.IO
open FS.GG.Coordination.GitHub

let args = fsi.CommandLineArgs |> Array.skip 1

let beforePath, afterPath, operationId =
    match args with
    | [| "--before"; beforePath; "--after"; afterPath; "--operation-id"; operationId |] ->
        beforePath, afterPath, operationId
    | _ ->
        failwith
            "usage: dotnet fsi eng/probe-v1-admission-installed-read.fsx -- --before <preinstall-evidence.json> --after <installed-evidence.json> --operation-id <id>"

let before =
    File.ReadAllBytes beforePath
    |> ReadOnlyMemory
    |> V1AdmissionGenesisGitRead.decode
    |> Result.defaultWith (String.concat "," >> failwith)

let now = DateTimeOffset.UtcNow

let plan =
    V1AdmissionGenesisGitRead.verifyPlan now operationId before
    |> Result.defaultWith (String.concat "," >> failwith)

let installed =
    File.ReadAllBytes afterPath
    |> ReadOnlyMemory
    |> V1AdmissionGenesisGitRead.decodeInstalled now plan
    |> Result.defaultWith (String.concat "," >> failwith)

let restored =
    V1AdmissionRegistry.verifyGenesisReadback plan installed
    |> Result.defaultWith (String.concat "," >> failwith)

printfn
    "V1_ADMISSION_INSTALLED_READBACK_OK ref=%s commit=%s generation=%d phase=%A"
    installed.Ref
    (V1AdmissionRegistry.head restored |> V1AdmissionRegistry.gitObjectIdValue)
    (V1AdmissionRegistry.generation restored)
    (V1AdmissionRegistry.phase restored)
