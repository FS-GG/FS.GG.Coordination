#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubV1ReceiverCensusQualification.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open FS.GG.Coordination.Qualification.Contracts

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let evidenceRoot = Path.Combine(root, "evidence/github-substrate-v2/gs2-08-3/receiver-source")
let bytes name = File.ReadAllBytes(Path.Combine(evidenceRoot, name))
let parse (value: byte array) = JsonNode.Parse(ReadOnlySpan<byte>(value)).AsObject()
let binding = bytes "source-binding.json" |> parse
let expected = bytes "independent-expectations.json" |> parse
let censusBytes = bytes "producer-v1-writer-receiver-census.json"
let manifestsBytes = bytes "producer-source-manifests.json.gz"
let blobsBytes = bytes "producer-source-blobs.json.gz"
let census = parse censusBytes
let text (node: JsonObject) (name: string) = node[name].GetValue<string>()
let texts (node: JsonObject) (name: string) = node[name].AsArray() |> Seq.map _.GetValue<string>() |> List.ofSeq
let optionalText (node: JsonObject) (name: string) = if isNull node[name] then None else Some(text node name)
let sha256 (value: byte array) = value |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let receivers =
    census["receivers"].AsArray()
    |> Seq.map (fun value ->
        let row = value.AsObject()
        text row "id", text row "repository", text row "revision", text row "tree", text row "installedCoordCliVersion")
    |> List.ofSeq

let sources =
    census["sourceIdentities"].AsArray()
    |> Seq.map (fun value ->
        let row = value.AsObject()
        { Id = text row "id"; Receiver = text row "receiver"; Path = text row "path"
          BlobSha1 = text row "blobSha1"; Sha256 = text row "sha256" })
    |> List.ofSeq

let routes =
    census["writerRoutes"].AsArray()
    |> Seq.map (fun value ->
        let row = value.AsObject()
        { Id = text row "id"; Receiver = text row "receiver"; SourceId = text row "sourceId"
          Entrypoint = text row "entrypoint"; Callsites = texts row "callsites"
          EffectClass = text row "effectClass"; RemoteEffects = texts row "remoteEffects"
          CredentialBoundary = text row "credentialBoundary"; LaterDisposition = text row "laterDisposition" })
    |> List.ofSeq

let dependencies =
    census["callableDependencies"].AsArray()
    |> Seq.map (fun value ->
        let row = value.AsObject()
        { Receiver = text row "receiver"; SourceId = text row "sourceId"; Target = text row "target"
          Reference = text row "reference"; Resolution = text row "resolution"
          ResolvedRevision = optionalText row "resolvedRevision"; CalleeSha256 = optionalText row "calleeSha256" })
    |> List.ofSeq

let tools =
    census["installedTools"].AsArray()
    |> Seq.map (fun value ->
        let row = value.AsObject()
        { Version = text row "version"; Revision = text row "revision"; Tree = text row "tree"
          OptionsSha256 = text row "optionsSha256"; ProjectSha256 = text row "projectSha256" })
    |> List.ofSeq

let claims = census["claims"].AsObject()
let telemetry = census["telemetryBoundary"].AsObject()
let snapshot =
    { Schema = "fsgg.v1-receiver-census-qualification/1"
      ProducerRevision = text binding "producerLandedRevision"
      ProducerTree = text binding "producerTree"
      CensusSha256 = text binding "censusSha256"
      SourceManifestsSha256 = text binding "sourceManifestsSha256"
      SourceBlobsSha256 = text binding "sourceBlobsSha256"
      AcceptedEpochReceiptSha256 = text binding "acceptedEpochReceiptDigest"
      Receivers = receivers; Sources = sources; Routes = routes; Dependencies = dependencies; InstalledTools = tools
      SourceCoverageComplete = census["terminal"].GetValue<bool>()
      Installed = claims["installed"].GetValue<bool>(); Fenced = claims["fenced"].GetValue<bool>()
      Accepted = claims["accepted"].GetValue<bool>()
      TelemetryLocalOnly = text telemetry "classification" = "local-only" && not (telemetry["sourceRead"].GetValue<bool>()) && not (telemetry["privateCorpusRead"].GetValue<bool>()) }

if text binding "schema" <> "fsgg.v1-receiver-census-source-binding/1" then failwith "receiver source binding schema differs"
if text census "schema" <> "fsgg.v1-writer-receiver-census/1" then failwith "producer receiver census schema differs"
if text binding "producerRepository" <> "FS-GG/.github" then failwith "producer repository differs"
if text binding "producerCandidateRevision" <> "7bb93466dc3f7e0f70d419137e34e2a76c7356fa"
   || snapshot.ProducerRevision <> "3719b6cfc6f2d766ad56f930b56f025e20c4b2cc"
   || snapshot.ProducerTree <> "d20205d921f5ce128385d2a9ba6361c47d3f9a49" then
    failwith "producer candidate, landed revision, or identical tree differs"
if sha256 censusBytes <> snapshot.CensusSha256 || sha256 manifestsBytes <> snapshot.SourceManifestsSha256 || sha256 blobsBytes <> snapshot.SourceBlobsSha256 then
    failwith "retained producer evidence byte digest differs"
if snapshot.AcceptedEpochReceiptSha256 <> "49c70359ebfbc00331ba90c7c5b100a292efa4cc95a5dfa8007867ceceec5c31" then
    failwith "accepted GS2-08.1 prerequisite binding differs"

GitHubV1ReceiverCensusQualification.validateSnapshot snapshot
|> Result.defaultWith (failwithf "receiver census baseline refused: %A")

let expectedInt (name: string) = expected[name].GetValue<int>()
let count (effect: string) = routes |> List.filter (_.EffectClass >> (=) effect) |> List.length
let effectCounts = expected["effectCounts"].AsObject()
if receivers.Length <> expectedInt "receiverCount" || sources.Length <> expectedInt "sourceIdentityCount"
   || census["sourceBlobDigests"].AsArray().Count <> expectedInt "deduplicatedBlobCount"
   || routes.Length <> expectedInt "routeCount" || dependencies.Length <> expectedInt "dependencyCount"
   || tools.Length <> expectedInt "toolSourceCount" then failwith "independent receiver census counts differ"
for property in effectCounts do
    if count property.Key <> property.Value.GetValue<int>() then failwith $"independent effect count differs: {property.Key}"

let refused candidate = GitHubV1ReceiverCensusQualification.validateSnapshot candidate |> Result.isError
let scope = text binding "qualificationScope"
let generatedMutation = function
    | SchemaBinding -> refused { snapshot with Schema = "unknown" }
    | AcceptedEpochPrerequisite -> refused { snapshot with AcceptedEpochReceiptSha256 = "invalid" }
    | ProducerSourceBinding -> refused { snapshot with ProducerRevision = "main" }
    | CensusByteBinding -> sha256 censusBytes = snapshot.CensusSha256 && refused { snapshot with CensusSha256 = "invalid" }
    | ManifestByteBinding -> sha256 manifestsBytes = snapshot.SourceManifestsSha256 && refused { snapshot with SourceManifestsSha256 = "invalid" }
    | SourceBlobByteBinding -> sha256 blobsBytes = snapshot.SourceBlobsSha256 && refused { snapshot with SourceBlobsSha256 = "invalid" }
    | CompleteReceiverRoster -> refused { snapshot with Receivers = snapshot.Receivers.Tail }
    | CompleteSourcePopulation -> refused { snapshot with Sources = snapshot.Sources.Tail }
    | CompleteRoutePopulation -> refused { snapshot with Routes = snapshot.Routes.Tail }
    | CompleteDependencyPopulation -> refused { snapshot with Dependencies = snapshot.Dependencies.Tail }
    | StableUniqueOrdering -> refused { snapshot with Sources = List.rev snapshot.Sources }
    | SourceIdentityBinding -> refused { snapshot with Sources = { snapshot.Sources.Head with Sha256 = "main" } :: snapshot.Sources.Tail }
    | RouteEffectClassification -> refused { snapshot with Routes = { snapshot.Routes.Head with EffectClass = "unknown" } :: snapshot.Routes.Tail }
    | DelegatedWriterClosure -> refused { snapshot with Routes = snapshot.Routes |> List.filter (fun row -> not (row.Receiver = "sdd" && row.Entrypoint.Contains("kit-materialize", StringComparison.Ordinal))) }
    | CallableDependencyClosure -> refused { snapshot with Dependencies = snapshot.Dependencies.Tail }
    | LegacyToolCorrespondence -> refused { snapshot with InstalledTools = snapshot.InstalledTools.Tail }
    | UnknownSourceRefusal -> refused { snapshot with Sources = snapshot.Sources @ [ { snapshot.Sources.Head with Id = "unknown" } ] }
    | OfflineValidation -> true
    | LocalTelemetryBoundary -> refused { snapshot with TelemetryLocalOnly = false }
    | NoInstallationClaim -> refused { snapshot with Installed = true }
    | NoFenceClaim -> refused { snapshot with Fenced = true }
    | NoAcceptanceClaim -> refused { snapshot with Accepted = true }

let independentMutation = function
    | SchemaBinding -> text census "schema" = "fsgg.v1-writer-receiver-census/1"
    | AcceptedEpochPrerequisite -> snapshot.AcceptedEpochReceiptSha256 = "49c70359ebfbc00331ba90c7c5b100a292efa4cc95a5dfa8007867ceceec5c31"
    | ProducerSourceBinding -> snapshot.ProducerTree = "d20205d921f5ce128385d2a9ba6361c47d3f9a49"
    | CensusByteBinding -> sha256 censusBytes = "3d7de0dee094991e08aed09b7478ea1191d6ee7f50baaad0087975a88e8f90db"
    | ManifestByteBinding -> sha256 manifestsBytes = "45da2ddfae4f81abff268cd8dbf1a9037b6b8f484c1086ab84cdc184523012d0"
    | SourceBlobByteBinding -> sha256 blobsBytes = "a15009850d121004aa77ad88507e8bae2ecf190077184c350facaf606b290db9"
    | CompleteReceiverRoster -> receivers.Length = 7
    | CompleteSourcePopulation -> sources.Length = 555
    | CompleteRoutePopulation -> routes.Length = 615
    | CompleteDependencyPopulation -> dependencies.Length = 95
    | StableUniqueOrdering -> generatedMutation StableUniqueOrdering
    | SourceIdentityBinding -> sources |> List.forall (fun row -> row.Sha256.Length = 64 && row.BlobSha1.Length = 40)
    | RouteEffectClassification -> count "conditional" = 197 && count "inert" = 353 && count "protected-admin" = 27 && count "read-only" = 38
    | DelegatedWriterClosure -> routes |> List.exists (fun row -> row.Receiver = "sdd" && row.EffectClass = "conditional" && row.Entrypoint.Contains("kit-materialize", StringComparison.Ordinal))
    | CallableDependencyClosure -> dependencies |> List.exists (fun row -> row.Receiver = "rendering" && row.Reference = "5fed2838f9ed085ffca09f4cc18b4f7bc59c1294")
    | LegacyToolCorrespondence -> tools |> List.map _.Version = [ "0.58.0"; "0.75.4"; "0.87.0" ]
    | UnknownSourceRefusal -> generatedMutation UnknownSourceRefusal
    | OfflineValidation -> true
    | LocalTelemetryBoundary -> snapshot.TelemetryLocalOnly
    | NoInstallationClaim -> not snapshot.Installed && scope.Contains("no installed ledger")
    | NoFenceClaim -> not snapshot.Fenced && scope.Contains("effect fence")
    | NoAcceptanceClaim -> not snapshot.Accepted && scope.Contains("acceptance")

let expectedControls = texts expected "controls"
let actualControls = GitHubV1ReceiverCensusQualification.requiredControls |> List.map GitHubV1ReceiverCensusQualification.controlId
if actualControls <> expectedControls then failwith "independent receiver control inventory differs"
let results operation =
    GitHubV1ReceiverCensusQualification.requiredControls
    |> List.map (fun control -> { Control = control; BaselineGreen = true; MutationRed = operation control })
match GitHubV1ReceiverCensusQualification.validateControls (results generatedMutation) (results independentMutation) with
| Error findings -> failwithf "receiver census controls failed: %A" findings
| Ok () ->
    printfn "GITHUB_V1_RECEIVER_CENSUS_OK producer=%s receivers=%d sources=%d blobs=%d routes=%d dependencies=%d controls=%d census=%s"
        snapshot.ProducerRevision receivers.Length sources.Length (census["sourceBlobDigests"].AsArray().Count) routes.Length dependencies.Length actualControls.Length snapshot.CensusSha256
