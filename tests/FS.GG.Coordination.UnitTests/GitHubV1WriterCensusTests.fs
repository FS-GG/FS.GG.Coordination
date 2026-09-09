module FS.GG.Coordination.GitHubV1WriterCensusTests

open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private digest character = System.String(character, 64)
let private revision character = System.String(character, 40)
let private commands =
    [ for index in 0 .. 53 ->
        { Name = $"command-{index:D2}"
          Writes = if index < 20 then "always" elif index < 26 then "conditional" else "never" } ]
let private sources =
    [ for index in 0 .. 59 do
          yield
              { Path = $"src/source-{index:D2}.fs"
                Disposition = "read-only"
                SinkKinds = []
                Sha256 = digest 'c'
                NonWriterJustification = None }
      yield { Path = "tests/fixture.sh"
              Disposition = "test-only"
              SinkKinds = [ "git-push" ]
              Sha256 = digest 'b'
              NonWriterJustification = Some "Isolated fixture has no production authority." }
      yield { Path = "tools/routine-delivery.py"
              Disposition = "remote-writer"
              SinkKinds = [ "dynamic-process" ]
              Sha256 = digest 'a'
              NonWriterJustification = None } ]
let private snapshot =
    { Schema = "fsgg.v1-writer-census-qualification/1"
      ProducerRevision = revision 'a'; ProducerTree = revision 'b'
      RoadmapRevision = revision 'c'; RoadmapSha256 = digest 'd'
      AcceptedEpochReceiptSha256 = digest 'e'; SourceBaseRevision = revision 'f'
      Q0EvidenceSha256 = digest '1'; Q0CorpusSha256 = digest '2'
      CensusSha256 = digest '3'; CommandContractSha256 = digest '4'
      CheckerSha256 = digest '5'; FixtureRunnerSha256 = digest '6'
      Commands = commands; Sources = sources }

[<Fact>]
let ``complete independently bound census qualifies`` () =
    Assert.Equal(Ok(), GitHubV1WriterCensusQualification.validateSnapshot snapshot)

[<Fact>]
let ``unknown command and changed write class refuse`` () =
    let unknown = { snapshot with Commands = snapshot.Commands @ [ { Name = "new"; Writes = "always" } ] }
    Assert.True(GitHubV1WriterCensusQualification.validateSnapshot unknown |> Result.isError)
    let changed = { snapshot with Commands = { snapshot.Commands.Head with Writes = "never" } :: snapshot.Commands.Tail }
    Assert.True(GitHubV1WriterCensusQualification.validateSnapshot changed |> Result.isError)

[<Fact>]
let ``dynamic source omission and malformed source identity refuse`` () =
    Assert.True(GitHubV1WriterCensusQualification.validateSnapshot { snapshot with Sources = sources.Tail } |> Result.isError)
    let malformed = { sources.Head with Sha256 = "main" }
    Assert.True(GitHubV1WriterCensusQualification.validateSnapshot { snapshot with Sources = malformed :: sources.Tail } |> Result.isError)

[<Fact>]
let ``non-writer sink requires an explicit justification`` () =
    let fixture = sources |> List.find (_.Path >> (=) "tests/fixture.sh")
    let unjustified = { fixture with NonWriterJustification = None }
    let changed = sources |> List.map (fun source -> if source.Path = fixture.Path then unjustified else source)
    Assert.True(GitHubV1WriterCensusQualification.validateSnapshot { snapshot with Sources = changed } |> Result.isError)

[<Fact>]
let ``generated and independent controls must both close every mutation`` () =
    let passing: GitHubV1WriterCensusControlResult list =
        GitHubV1WriterCensusQualification.requiredControls
        |> List.map (fun control -> { Control = control; BaselineGreen = true; MutationRed = true })
    Assert.Equal(Ok(), GitHubV1WriterCensusQualification.validateControls passing passing)
    let broken = passing |> List.map (fun value -> if value.Control = DynamicWriterRefusal then { value with MutationRed = false } else value)
    Assert.True(GitHubV1WriterCensusQualification.validateControls passing broken |> Result.isError)
