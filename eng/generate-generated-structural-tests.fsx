#load "../src/FS.GG.Coordination.Qualification.Contracts/GeneratedStructuralTests.fs"

open System
open FS.GG.Coordination.Qualification.Contracts

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.filter ((<>) "--") |> Array.toList

let root, output, checkOnly =
    match arguments with
    | [ "--root"; root; "--output"; output ] -> root, output, false
    | [ "--root"; root; "--check"; output ] -> root, output, true
    | _ ->
        eprintfn "usage: dotnet fsi eng/generate-generated-structural-tests.fsx -- --root ROOT (--output FILE|--check FILE)"
        exit 2

let result =
    if checkOnly then GeneratedStructuralTests.check root output
    else GeneratedStructuralTests.write root output

match result with
| Ok summary ->
    printfn "GENERATED_STRUCTURAL_TESTS_OK total=%d digest=%s" summary.TotalCount summary.SelfSha256
| Error error ->
    eprintfn "%s" error
    exit 1
