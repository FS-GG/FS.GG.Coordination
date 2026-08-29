#load "../src/FS.GG.Coordination.Qualification.Contracts/GeneratedStructuralTests.fs"

open System
open FS.GG.Coordination.Qualification.Contracts

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.filter ((<>) "--") |> Array.toList

let root, artifact =
    match arguments with
    | [ "--root"; root; "--artifact"; artifact ] -> root, artifact
    | _ ->
        eprintfn "usage: dotnet fsi eng/validate-generated-structural-tests.fsx -- --root ROOT --artifact FILE"
        exit 2

match GeneratedStructuralTests.check root artifact with
| Ok summary ->
    let counts = summary.CategoryCounts |> List.map (fun (category, count) -> $"%s{category}=%d{count}") |> String.concat ","
    printfn "GENERATED_STRUCTURAL_TESTS_VALID total=%d categories=%s digest=%s" summary.TotalCount counts summary.SelfSha256
| Error error ->
    eprintfn "%s" error
    exit 1
