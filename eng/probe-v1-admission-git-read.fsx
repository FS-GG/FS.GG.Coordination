#r "../src/FS.GG.Coordination.Protocol/bin/Release/net10.0/FS.GG.Coordination.Protocol.dll"
#r "../src/FS.GG.Coordination.Core/bin/Release/net10.0/FS.GG.Coordination.Core.dll"
#r "../src/FS.GG.Coordination.GitHub/bin/Release/net10.0/FS.GG.Coordination.GitHub.dll"

open System
open System.IO
open FS.GG.Coordination.GitHub

let args = fsi.CommandLineArgs |> Array.skip 1

let input =
    match args with
    | [| "--input"; path |] -> path
    | _ -> failwith "usage: dotnet fsi eng/probe-v1-admission-git-read.fsx -- --input <read-only evidence json>"

let evidence =
    File.ReadAllBytes input
    |> ReadOnlyMemory
    |> V1AdmissionGenesisGitRead.decode
    |> Result.defaultWith (String.concat "," >> failwith)

let plan =
    V1AdmissionGenesisGitRead.verifyPlan
        DateTimeOffset.UtcNow
        "fleet-v1-admission:fs-gg-production"
        evidence
    |> Result.defaultWith (String.concat "," >> failwith)

printfn "verified live OperatingV1 authority commit: %s"
    (V1AdmissionRegistry.genesisAuthorityCommit plan |> V1AdmissionRegistry.gitObjectIdValue)
printfn "planned admission genesis commit: %s"
    (V1AdmissionRegistry.genesisCommit plan).CommitOid
