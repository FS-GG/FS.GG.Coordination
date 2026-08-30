#load "../src/FS.GG.Coordination.Qualification.Contracts/FaultInjection.fs"

open System
open System.IO
open FS.GG.Coordination.Qualification.Contracts

let fail detail =
    eprintfn "FAULT_INJECTION_RED %s" detail
    exit 1

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.filter ((<>) "--") |> Array.toList

let rec parse root output checkOnly remaining =
    match remaining with
    | [] -> Path.GetFullPath root, output, checkOnly
    | "--root" :: value :: tail -> parse value output checkOnly tail
    | "--output" :: value :: tail -> parse root value checkOnly tail
    | "--check" :: tail -> parse root output true tail
    | value :: _ -> fail $"unknown argument: %s{value}"

let root, output, checkOnly =
    parse "." "src/FS.GG.Coordination.Qualification.Contracts/Generated/fault-injection.json" false arguments

let result =
    if checkOnly then FaultInjection.check root output
    else FaultInjection.write root output

match result with
| Error error -> fail error
| Ok summary ->
    printfn
        "FAULT_INJECTION_OK scenarios=%d converged=%d refused=%d source=%s self=%s"
        summary.ScenarioCount
        summary.ConvergedCount
        summary.RefusedCount
        summary.SourceSha256
        summary.SelfSha256
