#load "../src/FS.GG.Coordination.Qualification.Contracts/QualificationReuse.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/MilestoneQualification.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/QualificationCadence.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/BootstrapCi.fs"

open System
open FS.GG.Coordination.Qualification.Contracts

let arguments =
    fsi.CommandLineArgs
    |> Array.skip 1
    |> Array.filter ((<>) "--")
    |> Array.toList

let exitCode, output, error = BootstrapCi.execute arguments

if not (String.IsNullOrWhiteSpace output) then
    printfn "%s" output

if not (String.IsNullOrWhiteSpace error) then
    eprintfn "%s" error

exit exitCode
