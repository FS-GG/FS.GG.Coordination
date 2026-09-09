module FS.GG.Coordination.GitHubV1ReceiverCensusTests

open FS.GG.Coordination.Qualification.Contracts
open Xunit

let private sha1 = String.replicate 40 "a"
let private sha256 = String.replicate 64 "b"
let private receivers =
    [ "sdd", "FS-GG/FS.GG.SDD", "8d648c8deaf1edc16b942d0cfccee722c3a0a24c", "9d1486b51dc3b0b62c6a3ef0272b00f0a1421829", "0.87.0"
      "rendering", "FS-GG/FS.GG.Rendering", "86102999e7f60a494bed74e825e36b284fef6d62", "41299fe02be434366da6d876af1955765cbeee2a", "0.75.4"
      "governance", "FS-GG/FS.GG.Governance", "e0580e402c07f1c0576183eb8c6433bf6b1185bd", "78fee3a3c372dbeb13be34b3e130d3f2dfd67f54", "0.58.0"
      "templates", "FS-GG/FS.GG.Templates", "61091078337689c6ab1aac139bc03f6a07ca8f99", "2fd555ff979b1200a0e03f2f8417b59c953bdc31", "0.75.4"
      "game", "FS-GG/FS.GG.Game", "24f79084fdd289f34387f91b1d4398c78fde16eb", "e3dec5593102975031fc4d4a4bf053659db2e2b3", "0.75.4"
      "audio", "FS-GG/FS.GG.Audio", "04ca17810c2a00efa20a689405f943a20c06769a", "e24658403dca6a0ac29bc3f5ae854953be88bd66", "0.75.4"
      "net", "FS-GG/FS.GG.Net", "e66d38d7ed7fb5cc36984f4d33434bd9c6eaa802", "670a54df53405c8e025f1f0cf1ba0a2e3ba87b48", "0.75.4" ]

let private sources =
    [ for index in 0 .. 554 ->
        { Id = $"source-{index:D3}"; Receiver = "audio"; Path = $"source/{index:D3}.fs"
          BlobSha1 = sha1; Sha256 = sha256 } ]

let private route index receiver entrypoint effect =
    { Id = $"route-{index:D3}"; Receiver = receiver; SourceId = sources[index % sources.Length].Id
      Entrypoint = entrypoint; Callsites = [ $"{entrypoint}:1" ]; EffectClass = effect
      RemoteEffects = if effect = "conditional" || effect = "protected-admin" then [ "effect" ] else []
      CredentialBoundary = "none-observed"; LaterDisposition = "GS2-08.4-fence" }

let private routes =
    [ yield route 0 "sdd" "scripts/kit-materialize.sh" "conditional"
      for index in 1 .. 196 do yield route index "audio" $"conditional/{index:D3}" "conditional"
      for index in 197 .. 549 do yield route index "audio" $"inert/{index:D3}" "inert"
      for index in 550 .. 576 do yield route index "audio" $"admin/{index:D3}" "protected-admin"
      for index in 577 .. 614 do yield route index "audio" $"read/{index:D3}" "read-only" ]
    |> List.sortBy (fun row -> row.Receiver, row.Entrypoint, row.Id)

let private dependencies =
    [ yield { Receiver = "rendering"; SourceId = sources[0].Id
              Target = "FS-GG/.github/.github/workflows/dispatch-sender.yml"
              Reference = "5fed2838f9ed085ffca09f4cc18b4f7bc59c1294"; Resolution = "immutable"
              ResolvedRevision = None; CalleeSha256 = None }
      for index in 1 .. 94 do
          yield { Receiver = "audio"; SourceId = sources[index].Id; Target = $"./action/{index:D3}"
                  Reference = sha1; Resolution = "receiver-tree"; ResolvedRevision = None; CalleeSha256 = None } ]

let private tools =
    [ for version in [ "0.58.0"; "0.75.4"; "0.87.0" ] ->
        { Version = version; Revision = sha1; Tree = sha1; OptionsSha256 = sha256; ProjectSha256 = sha256 } ]

let private snapshot =
    { Schema = "fsgg.v1-receiver-census-qualification/1"; ProducerRevision = sha1; ProducerTree = sha1
      CensusSha256 = sha256; SourceManifestsSha256 = sha256; SourceBlobsSha256 = sha256
      AcceptedEpochReceiptSha256 = sha256; Receivers = receivers; Sources = sources; Routes = routes
      Dependencies = dependencies; InstalledTools = tools; SourceCoverageComplete = true
      Installed = false; Fenced = false; Accepted = false; TelemetryLocalOnly = true }

[<Fact>]
let ``complete receiver source census qualifies without installation claims`` () =
    Assert.Equal(Ok(), GitHubV1ReceiverCensusQualification.validateSnapshot snapshot)

[<Fact>]
let ``omitted route and nonwriter laundering refuse`` () =
    Assert.True(GitHubV1ReceiverCensusQualification.validateSnapshot { snapshot with Routes = routes.Tail } |> Result.isError)
    let writer = routes |> List.find (fun row -> row.EffectClass = "conditional")
    let laundered = { writer with EffectClass = "read-only" }
    Assert.True(GitHubV1ReceiverCensusQualification.validateSnapshot { snapshot with Routes = laundered :: (routes |> List.filter (_.Id >> (<>) writer.Id)) } |> Result.isError)

[<Fact>]
let ``mutable FS-GG callee without exact resolution refuses`` () =
    let unknown = { dependencies.Head with Reference = "main"; Resolution = "mutable-source-reference" }
    Assert.True(GitHubV1ReceiverCensusQualification.validateSnapshot { snapshot with Dependencies = unknown :: dependencies.Tail } |> Result.isError)

[<Fact>]
let ``source census cannot claim installed fenced or accepted state`` () =
    Assert.True(GitHubV1ReceiverCensusQualification.validateSnapshot { snapshot with Installed = true } |> Result.isError)
    Assert.True(GitHubV1ReceiverCensusQualification.validateSnapshot { snapshot with Fenced = true } |> Result.isError)
    Assert.True(GitHubV1ReceiverCensusQualification.validateSnapshot { snapshot with Accepted = true } |> Result.isError)

[<Fact>]
let ``generated and independent controls must both cover every mutation`` () =
    let passing: ReceiverCensusControlResult list =
        GitHubV1ReceiverCensusQualification.requiredControls
        |> List.map (fun control -> { Control = control; BaselineGreen = true; MutationRed = true })
    Assert.Equal(Ok(), GitHubV1ReceiverCensusQualification.validateControls passing passing)
    Assert.True(GitHubV1ReceiverCensusQualification.validateControls passing passing.Tail |> Result.isError)
