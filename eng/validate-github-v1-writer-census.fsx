#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubV1WriterCensusQualification.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open FS.GG.Coordination.Qualification.Contracts

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let evidenceRoot = Path.Combine(root, "evidence/github-substrate-v2/gs2-08-3")
let readBytes name = File.ReadAllBytes(Path.Combine(evidenceRoot, name))
let parse (bytes: byte array) = JsonNode.Parse(ReadOnlySpan<byte>(bytes)).AsObject()
let binding = readBytes "source-binding.json" |> parse
let censusBytes = readBytes "producer-v1-writer-census.json"
let contractBytes = readBytes "producer-command-contract.json"
let census = parse censusBytes
let contract = parse contractBytes
let expected = readBytes "independent-expectations.json" |> parse
let text (node: JsonObject) (name: string) = node[name].GetValue<string>()
let texts (node: JsonObject) (name: string) = node[name].AsArray() |> Seq.map _.GetValue<string>() |> List.ofSeq
let sha256 (bytes: byte array) = bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let commands (node: JsonObject) =
    node["commandRoots"].AsArray()
    |> Seq.map (fun value ->
        let row = value.AsObject()
        { Name = text row "name"; Writes = text row "writes" })
    |> List.ofSeq

let sources =
    census["sources"].AsArray()
    |> Seq.map (fun value ->
        let row = value.AsObject()
        { Path = text row "path"
          Disposition = text row "disposition"
          SinkKinds = texts row "sinkKinds"
          Sha256 = text row "sha256"
          NonWriterJustification =
            if isNull row["nonWriterJustification"] then None
            else Some(text row "nonWriterJustification") })
    |> List.ofSeq

if text census "schema" <> "fsgg.v1-writer-census/1" then failwith "producer census schema differs"
if text contract "schema" <> "fsgg.coord.commands/1" then failwith "candidate command contract schema differs"
if sha256 censusBytes <> text binding "censusSha256" then failwith "producer census byte digest differs"
if sha256 contractBytes <> text binding "commandContractSha256" then failwith "candidate command-contract byte digest differs"
if text binding "acceptedEpochReceiptDigest" <> "49c70359ebfbc00331ba90c7c5b100a292efa4cc95a5dfa8007867ceceec5c31" then
    failwith "accepted GS2-08.1 receipt binding differs"
if text binding "sourceBaseRevision" <> "95de1c77674b9dd8d7a9ce568d1ee175a7797e5e" then failwith "historical Q0 source base differs"
if text binding "q0EvidenceSha256" <> "ef07c245e4ab3dd0b97d997a32940cdd70485070bfedc9c44e5b4ed2422078c5" then
    failwith "historical Q0 evidence binding differs"
if text binding "q0CorpusSha256" <> "5c94fa3ee60e02b7fbee80918b45e5e2046a152a2342f6b88044ac169c1dc67b" then
    failwith "historical Q0 corpus binding differs"

let contractCommands =
    contract["commands"].AsArray()
    |> Seq.map (fun value -> let row = value.AsObject() in text row "name", text row "writes")
    |> List.ofSeq
let censusCommands = commands census
let censusPairs = censusCommands |> List.map (fun value -> value.Name, value.Writes)
if contractCommands <> censusPairs then failwith "candidate-built command contract differs from the producer census"

let snapshot =
    { Schema = "fsgg.v1-writer-census-qualification/1"
      ProducerRevision = text binding "producerLandedRevision"
      ProducerTree = text binding "producerTree"
      RoadmapRevision = text binding "producerLandedRevision"
      RoadmapSha256 = text binding "nativeRoadmapSha256"
      AcceptedEpochReceiptSha256 = text binding "acceptedEpochReceiptDigest"
      SourceBaseRevision = text binding "sourceBaseRevision"
      Q0EvidenceSha256 = text binding "q0EvidenceSha256"
      Q0CorpusSha256 = text binding "q0CorpusSha256"
      CensusSha256 = text binding "censusSha256"
      CommandContractSha256 = text binding "commandContractSha256"
      CheckerSha256 = text binding "checkerSha256"
      FixtureRunnerSha256 = text binding "fixtureRunnerSha256"
      Commands = censusCommands
      Sources = sources }

GitHubV1WriterCensusQualification.validateSnapshot snapshot
|> Result.defaultWith (failwithf "v1 writer census baseline refused: %A")

let expectedCount (name: string) = expected[name].GetValue<int>()
let count (writes: string) = censusCommands |> List.filter (_.Writes >> (=) writes) |> List.length
if censusCommands.Length <> expectedCount "commandRootCount"
   || count "always" <> expectedCount "alwaysCount"
   || count "conditional" <> expectedCount "conditionalCount"
   || count "never" <> expectedCount "neverCount"
   || sources.Length <> expectedCount "sourceIdentityCount" then
    failwith "independent census counts differ"
if binding["trackedExecutableCount"].GetValue<int>() <> expectedCount "trackedExecutableCount" then
    failwith "tracked executable population count differs"

let sourceByPath = sources |> List.map (fun value -> value.Path, value) |> Map.ofList
for property in expected["mandatorySources"].AsObject() do
    match Map.tryFind property.Key sourceByPath with
    | Some source when source.Disposition = property.Value.GetValue<string>() -> ()
    | _ -> failwith $"mandatory writer source classification differs: {property.Key}"
for path in texts expected "telemetryLocalOnly" do
    match Map.tryFind path sourceByPath with
    | Some source when source.Disposition = "local-only" && source.SinkKinds.IsEmpty -> ()
    | _ -> failwith $"telemetry source is not explicitly local-only: {path}"

let refused candidate = GitHubV1WriterCensusQualification.validateSnapshot candidate |> Result.isError
let scope = text binding "qualificationScope"
let mandatoryPath = "tools/routine-delivery.py"
let generatedMutation = function
    | SchemaBinding -> refused { snapshot with Schema = "unknown" }
    | AcceptedEpochPrerequisite -> refused { snapshot with AcceptedEpochReceiptSha256 = "invalid" }
    | RoadmapBinding -> refused { snapshot with RoadmapRevision = "main"; RoadmapSha256 = "invalid" }
    | HistoricalQ0Binding -> refused { snapshot with SourceBaseRevision = "main"; Q0EvidenceSha256 = "invalid" }
    | ProducerSourceBinding -> refused { snapshot with ProducerRevision = "main" }
    | CensusByteBinding -> sha256 censusBytes = snapshot.CensusSha256 && refused { snapshot with CensusSha256 = "invalid" }
    | CandidateCommandContract -> contractCommands = censusPairs && contractCommands @ [ ("unknown", "always") ] <> censusPairs
    | CheckerBinding -> snapshot.CheckerSha256 = "2b90d16f7d4b1a4ea1f9afd78eed306655f32f60470b20370d9e895f3c999619"
    | CompleteCommandRoots -> refused { snapshot with Commands = snapshot.Commands.Tail }
    | CompleteSourcePopulation -> refused { snapshot with Sources = [] }
    | StableUniqueOrdering -> refused { snapshot with Commands = List.rev snapshot.Commands }
    | ExactWriteClassification -> refused { snapshot with Commands = { snapshot.Commands.Head with Writes = "never" } :: snapshot.Commands.Tail }
    | SourceIdentity -> refused { snapshot with Sources = { snapshot.Sources.Head with Sha256 = "main" } :: snapshot.Sources.Tail }
    | SinkDisposition -> refused { snapshot with Sources = { snapshot.Sources.Head with Disposition = "unknown" } :: snapshot.Sources.Tail }
    | UnknownCommandRefusal -> refused { snapshot with Commands = snapshot.Commands @ [ { Name = "unknown"; Writes = "always" } ] }
    | DynamicWriterRefusal -> sourceByPath.ContainsKey mandatoryPath && not ((sourceByPath.Remove mandatoryPath).ContainsKey mandatoryPath)
    | NoFenceClaim -> scope.Contains("no installed ledger") && scope.Contains("effect fence")
    | NoReceiverClaim -> scope.Contains("no installed ledger") && scope.Contains("receiver") && scope.Contains("publication")

let independentMutation = function
    | SchemaBinding -> text census "schema" = "fsgg.v1-writer-census/1" && text contract "schema" = "fsgg.coord.commands/1"
    | AcceptedEpochPrerequisite -> snapshot.AcceptedEpochReceiptSha256 = "49c70359ebfbc00331ba90c7c5b100a292efa4cc95a5dfa8007867ceceec5c31"
    | RoadmapBinding -> snapshot.RoadmapSha256 = "20f4f2bcdcd6e2bfdd787edc66efbe30a21289f7ddf2030a8b1b3ae2150b7c52"
    | HistoricalQ0Binding -> snapshot.Q0EvidenceSha256 = "ef07c245e4ab3dd0b97d997a32940cdd70485070bfedc9c44e5b4ed2422078c5" && snapshot.Q0CorpusSha256 = "5c94fa3ee60e02b7fbee80918b45e5e2046a152a2342f6b88044ac169c1dc67b"
    | ProducerSourceBinding -> snapshot.ProducerTree = "dcae3fcb5fb261294c0a6d3e85ba02e42e0f6eee" && snapshot.ProducerRevision.Length = 40
    | CensusByteBinding -> sha256 censusBytes = "2b6565d358dd900f81874773ec2d8da42d6dcd54d12ca27780f16483f1cd73c8"
    | CandidateCommandContract -> sha256 contractBytes = "780b3a8779b9e91358b2200f8d696de54b4e76341f0e3ddf2962d5b0ddd8a441" && contractCommands = censusPairs
    | CheckerBinding -> snapshot.CheckerSha256 = "2b90d16f7d4b1a4ea1f9afd78eed306655f32f60470b20370d9e895f3c999619" && snapshot.FixtureRunnerSha256 = "85764d713331ae43b6093be8f2a9a2291c17c02251bce9804aee1e6855a17653"
    | CompleteCommandRoots -> censusCommands.Length = 54
    | CompleteSourcePopulation -> sources.Length = 62
    | StableUniqueOrdering -> censusCommands |> List.map _.Name = (censusCommands |> List.map _.Name |> List.sort)
    | ExactWriteClassification -> count "always" = 20 && count "conditional" = 6 && count "never" = 28
    | SourceIdentity -> sources |> List.forall (fun value -> value.Sha256.Length = 64)
    | SinkDisposition -> expected["mandatorySources"].AsObject().Count = 8
    | UnknownCommandRefusal -> generatedMutation UnknownCommandRefusal
    | DynamicWriterRefusal -> sourceByPath[mandatoryPath].Disposition = "remote-writer" && sourceByPath[mandatoryPath].SinkKinds = [ "dynamic-process" ]
    | NoFenceClaim -> generatedMutation NoFenceClaim
    | NoReceiverClaim -> generatedMutation NoReceiverClaim

let expectedControls = GitHubV1WriterCensusQualification.requiredControls |> List.map GitHubV1WriterCensusQualification.controlId
if texts expected "controls" <> expectedControls then failwith "independent control inventory differs"
let results operation =
    GitHubV1WriterCensusQualification.requiredControls
    |> List.map (fun control -> { Control = control; BaselineGreen = true; MutationRed = operation control })
match GitHubV1WriterCensusQualification.validateControls (results generatedMutation) (results independentMutation) with
| Error findings -> failwithf "v1 writer census qualification failed: %A" findings
| Ok () ->
    printfn "GITHUB_V1_WRITER_CENSUS_OK producer=%s roots=%d always=%d conditional=%d never=%d sources=%d controls=%d census=%s contract=%s"
        snapshot.ProducerRevision censusCommands.Length (count "always") (count "conditional") (count "never") sources.Length expectedControls.Length snapshot.CensusSha256 snapshot.CommandContractSha256
