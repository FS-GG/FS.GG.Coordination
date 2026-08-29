module FS.GG.Coordination.QualificationManifestTests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private time minute = DateTimeOffset(2026, 8, 29, 0, minute, 0, TimeSpan.Zero)
let private digest character = String(character, 64)

let private content id producer minute =
    { Id = id
      Sha256 = SHA256.HashData(Encoding.UTF8.GetBytes id) |> Convert.ToHexString |> _.ToLowerInvariant()
      Bytes = 100L
      MediaType = "application/json"
      Producer = producer
      ObservedAt = time minute }

let private validInput () =
    { Candidate =
        { CommitSha = String('a', 40)
          TreeSha256 = digest 'b'
          ContractSha256 = digest 'c'
          Producer = "candidate-builder" }
      CreatedAt = time 10
      Sources = [ content "source-a" "candidate-builder" 1; content "source-b" "candidate-builder" 1 ]
      Model = [ content "model-a" "model-compiler" 1 ]
      Compiler = [ content "compiler-a" "model-compiler" 1 ]
      Dependencies = [ content "dependencies-a" "dependency-reader" 1 ]
      GeneratedCases = [ content "generated-a" "case-generator" 2 ]
      IndependentCases = [ content "independent-a" "oracle-author" 2 ]
      ExternalFixtures = [ content "fixture-a" "fixture-curator" 2 ]
      Packages = [ content "package-a" "package-builder" 3 ]
      Environment =
        { Os = "linux"
          Architecture = "x64"
          Runtime = ".NET 10.0.11"
          Locale = "C.UTF-8"
          Timezone = "UTC"
          NetworkMode = "isolated"
          Producer = "environment-observer"
          ObservedAt = time 3 }
      Results =
        [ { Id = "result-q1"; QGate = "Q1"; Sha256 = digest 'd'; Producer = "gate-runner"; CompletedAt = time 5 }
          { Id = "result-q2"; QGate = "Q2"; Sha256 = digest 'e'; Producer = "gate-runner"; CompletedAt = time 6 }
          { Id = "result-q7"; QGate = "Q7"; Sha256 = digest 'f'; Producer = "gate-runner"; CompletedAt = time 7 } ]
      Reviewers =
        [ { Id = "review-architecture"
            Role = "architecture"
            Sha256 = digest '1'
            Principal = "independent-critic"
            CompletedAt = time 8 } ] }

let private generated input =
    match QualificationManifest.generate input with
    | Ok bytes -> bytes
    | Error findings -> failwith (String.concat "; " (findings |> List.map (fun item -> item.Code + "@" + item.Path)))

let private codes bytes =
    match QualificationManifest.validate (ReadOnlyMemory<byte>(bytes)) with
    | Ok _ -> Set.empty
    | Error findings -> findings |> List.map _.Code |> Set.ofList

[<Fact>]
let ``complete qualification manifest is canonical deterministic and self bound`` () =
    let input = validInput ()
    let first = generated input
    let reordered = generated { input with Sources = List.rev input.Sources; Results = List.rev input.Results }
    Assert.Equal<byte>(first, reordered)
    Assert.True(Set.isEmpty (codes first))
    use document = JsonDocument.Parse first
    Assert.Equal(QualificationManifest.Schema, document.RootElement.GetProperty("schema").GetString())
    Assert.Equal(64, document.RootElement.GetProperty("digest").GetString().Length)
    Assert.Equal(64, document.RootElement.GetProperty("candidate").GetProperty("inputSetSha256").GetString().Length)

[<Fact>]
let ``semantic input changes manifest identity`` () =
    let input = validInput ()
    let changedSource = { input.Sources.Head with Sha256 = digest '9' }
    let before = generated input
    let after = generated { input with Sources = changedSource :: input.Sources.Tail }
    Assert.NotEqual<byte>(before, after)
    use beforeDocument = JsonDocument.Parse before
    use afterDocument = JsonDocument.Parse after
    Assert.NotEqual<string>(
        beforeDocument.RootElement.GetProperty("digest").GetString(),
        afterDocument.RootElement.GetProperty("digest").GetString()
    )

let private mutate (bytes: byte array) (action: JsonObject -> unit) =
    let root = JsonNode.Parse(ReadOnlySpan<byte>(bytes)).AsObject()
    action root
    Text.Encoding.UTF8.GetBytes(root.ToJsonString(JsonSerializerOptions(WriteIndented = false)))

[<Fact>]
let ``qualification manifest omissions substitutions and independence failures are red`` () =
    let baseline = generated (validInput ())
    let cases: (string * (JsonObject -> unit)) list =
        [ "QM-CATEGORY", fun root -> root.Remove("model") |> ignore
          "QM-CATEGORY-EMPTY", fun root -> root["sources"] <- JsonArray()
          "QM-ENTRY-DUPLICATE", fun root ->
              let entries = root["sources"].AsArray()
              entries.Add(JsonNode.Parse(entries[0].ToJsonString()))
          "QM-CANDIDATE-BINDING", fun root -> root["sources"].AsArray().[0].AsObject()["candidateSha"] <- String('9', 40)
          "QM-INPUT-SET", fun root -> root["candidate"].AsObject()["inputSetSha256"] <- digest '9'
          "QM-INDEPENDENCE", fun root -> root["independentCases"].AsArray().[0].AsObject()["producer"] <- "candidate-builder"
          "QM-SELF-REVIEW", fun root -> root["reviewers"].AsArray().[0].AsObject()["principal"] <- "gate-runner"
          "QM-TIME-ORDER", fun root -> root["reviewers"].AsArray().[0].AsObject()["completedAt"] <- "2026-08-29T00:04:00Z"
          "QM-SCHEMA", fun root -> root["schema"] <- "fsgg.coordination.qualification-manifest/2"
          "QM-SELF-DIGEST", fun root -> root["digest"] <- digest '0'
          "QM-ROOT-SHAPE", fun root -> root["unexpected"] <- true ]
    for expected, mutation in cases do
        let observed = mutate baseline mutation |> codes
        Assert.Contains(expected, observed)
