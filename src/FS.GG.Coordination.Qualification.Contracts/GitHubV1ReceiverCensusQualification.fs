namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Text.RegularExpressions

type ReceiverSourceIdentity =
    { Id: string
      Receiver: string
      Path: string
      BlobSha1: string
      Sha256: string }

type WriterRoute =
    { Id: string
      Receiver: string
      SourceId: string
      Entrypoint: string
      Callsites: string list
      EffectClass: string
      RemoteEffects: string list
      CredentialBoundary: string
      LaterDisposition: string }

type CallableDependency =
    { Receiver: string
      SourceId: string
      Target: string
      Reference: string
      Resolution: string
      ResolvedRevision: string option
      CalleeSha256: string option }

type InstalledToolIdentity =
    { Version: string
      Revision: string
      Tree: string
      OptionsSha256: string
      ProjectSha256: string }

type ReceiverCensusSnapshot =
    { Schema: string
      ProducerRevision: string
      ProducerTree: string
      CensusSha256: string
      SourceManifestsSha256: string
      SourceBlobsSha256: string
      AcceptedEpochReceiptSha256: string
      Receivers: (string * string * string * string * string) list
      Sources: ReceiverSourceIdentity list
      Routes: WriterRoute list
      Dependencies: CallableDependency list
      InstalledTools: InstalledToolIdentity list
      SourceCoverageComplete: bool
      Installed: bool
      Fenced: bool
      Accepted: bool
      TelemetryLocalOnly: bool }

type ReceiverCensusFinding =
    { Code: string
      Subject: string
      Message: string }

type ReceiverCensusControl =
    | SchemaBinding | AcceptedEpochPrerequisite | ProducerSourceBinding | CensusByteBinding
    | ManifestByteBinding | SourceBlobByteBinding | CompleteReceiverRoster | CompleteSourcePopulation
    | CompleteRoutePopulation | CompleteDependencyPopulation | StableUniqueOrdering | SourceIdentityBinding
    | RouteEffectClassification | DelegatedWriterClosure | CallableDependencyClosure
    | LegacyToolCorrespondence | UnknownSourceRefusal | OfflineValidation | LocalTelemetryBoundary
    | NoInstallationClaim | NoFenceClaim | NoAcceptanceClaim

type ReceiverCensusControlResult =
    { Control: ReceiverCensusControl
      BaselineGreen: bool
      MutationRed: bool }

module GitHubV1ReceiverCensusQualification =
    let requiredControls =
        [ SchemaBinding; AcceptedEpochPrerequisite; ProducerSourceBinding; CensusByteBinding
          ManifestByteBinding; SourceBlobByteBinding; CompleteReceiverRoster; CompleteSourcePopulation
          CompleteRoutePopulation; CompleteDependencyPopulation; StableUniqueOrdering; SourceIdentityBinding
          RouteEffectClassification; DelegatedWriterClosure; CallableDependencyClosure
          LegacyToolCorrespondence; UnknownSourceRefusal; OfflineValidation; LocalTelemetryBoundary
          NoInstallationClaim; NoFenceClaim; NoAcceptanceClaim ]

    let controlId = function
        | SchemaBinding -> "schema-binding"
        | AcceptedEpochPrerequisite -> "accepted-epoch-prerequisite"
        | ProducerSourceBinding -> "producer-source-binding"
        | CensusByteBinding -> "census-byte-binding"
        | ManifestByteBinding -> "manifest-byte-binding"
        | SourceBlobByteBinding -> "source-blob-byte-binding"
        | CompleteReceiverRoster -> "complete-receiver-roster"
        | CompleteSourcePopulation -> "complete-source-population"
        | CompleteRoutePopulation -> "complete-route-population"
        | CompleteDependencyPopulation -> "complete-dependency-population"
        | StableUniqueOrdering -> "stable-unique-ordering"
        | SourceIdentityBinding -> "source-identity-binding"
        | RouteEffectClassification -> "route-effect-classification"
        | DelegatedWriterClosure -> "delegated-writer-closure"
        | CallableDependencyClosure -> "callable-dependency-closure"
        | LegacyToolCorrespondence -> "legacy-tool-correspondence"
        | UnknownSourceRefusal -> "unknown-source-refusal"
        | OfflineValidation -> "offline-validation"
        | LocalTelemetryBoundary -> "local-telemetry-boundary"
        | NoInstallationClaim -> "no-installation-claim"
        | NoFenceClaim -> "no-fence-claim"
        | NoAcceptanceClaim -> "no-acceptance-claim"

    let private matches pattern value =
        not (isNull value) && Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant)
    let private sha1 = matches "^[0-9a-f]{40}$"
    let private sha256 = matches "^[0-9a-f]{64}$"
    let private finding code subject message = { Code = code; Subject = subject; Message = message }

    let validateSnapshot snapshot =
        let findings = ResizeArray<ReceiverCensusFinding>()
        if snapshot.Schema <> "fsgg.v1-receiver-census-qualification/1" then
            findings.Add(finding "RC-SCHEMA" "snapshot" "unsupported receiver-census qualification schema")
        if not (sha1 snapshot.ProducerRevision && sha1 snapshot.ProducerTree) then
            findings.Add(finding "RC-PRODUCER" "producer" "landed producer revision and tree must be immutable")
        for label, value in [ "census", snapshot.CensusSha256; "manifests", snapshot.SourceManifestsSha256; "blobs", snapshot.SourceBlobsSha256; "acceptedEpoch", snapshot.AcceptedEpochReceiptSha256 ] do
            if not (sha256 value) then findings.Add(finding "RC-DIGEST" label "expected a lowercase SHA-256")
        let expectedReceivers =
            [ "sdd", "FS-GG/FS.GG.SDD", "8d648c8deaf1edc16b942d0cfccee722c3a0a24c", "9d1486b51dc3b0b62c6a3ef0272b00f0a1421829", "0.87.0"
              "rendering", "FS-GG/FS.GG.Rendering", "86102999e7f60a494bed74e825e36b284fef6d62", "41299fe02be434366da6d876af1955765cbeee2a", "0.75.4"
              "governance", "FS-GG/FS.GG.Governance", "e0580e402c07f1c0576183eb8c6433bf6b1185bd", "78fee3a3c372dbeb13be34b3e130d3f2dfd67f54", "0.58.0"
              "templates", "FS-GG/FS.GG.Templates", "61091078337689c6ab1aac139bc03f6a07ca8f99", "2fd555ff979b1200a0e03f2f8417b59c953bdc31", "0.75.4"
              "game", "FS-GG/FS.GG.Game", "24f79084fdd289f34387f91b1d4398c78fde16eb", "e3dec5593102975031fc4d4a4bf053659db2e2b3", "0.75.4"
              "audio", "FS-GG/FS.GG.Audio", "04ca17810c2a00efa20a689405f943a20c06769a", "e24658403dca6a0ac29bc3f5ae854953be88bd66", "0.75.4"
              "net", "FS-GG/FS.GG.Net", "e66d38d7ed7fb5cc36984f4d33434bd9c6eaa802", "670a54df53405c8e025f1f0cf1ba0a2e3ba87b48", "0.75.4" ]
        if snapshot.Receivers <> expectedReceivers then
            findings.Add(finding "RC-RECEIVERS" "receivers" "receiver roster or order differs")
        for id, repository, revision, tree, version in snapshot.Receivers do
            if String.IsNullOrWhiteSpace id || not (repository.StartsWith("FS-GG/", StringComparison.Ordinal)) || not (sha1 revision && sha1 tree) || String.IsNullOrWhiteSpace version then
                findings.Add(finding "RC-RECEIVER-IDENTITY" id "receiver repository, revision, tree, or installed tool is malformed")
        let sourceIds = snapshot.Sources |> List.map _.Id
        if snapshot.Sources.Length <> 555 then findings.Add(finding "RC-SOURCE-COUNT" "sources" "expected all 555 retained source identities")
        if snapshot.Sources <> List.sortBy (fun row -> row.Receiver, row.Path, row.Id) snapshot.Sources || sourceIds.Length <> (sourceIds |> List.distinct |> List.length) then
            findings.Add(finding "RC-SOURCE-ORDER" "sources" "source identities must be unique and ordinally sorted")
        for source in snapshot.Sources do
            if String.IsNullOrWhiteSpace source.Id || String.IsNullOrWhiteSpace source.Receiver || String.IsNullOrWhiteSpace source.Path || source.Path.StartsWith("/", StringComparison.Ordinal) || source.Path.Contains("..") || not (sha1 source.BlobSha1 && sha256 source.Sha256) then
                findings.Add(finding "RC-SOURCE" source.Id "source identity is incomplete")
        let sourceSet = Set.ofList sourceIds
        let routeIds = snapshot.Routes |> List.map _.Id
        if snapshot.Routes.Length <> 615 then findings.Add(finding "RC-ROUTE-COUNT" "routes" "expected all 615 invocation-preserving routes")
        if snapshot.Routes <> List.sortBy (fun row -> row.Receiver, row.Entrypoint, row.Id) snapshot.Routes || routeIds.Length <> (routeIds |> List.distinct |> List.length) then
            findings.Add(finding "RC-ROUTE-ORDER" "routes" "route identities must be unique and ordinally sorted")
        let allowedEffects = Set [ "always"; "conditional"; "read-only"; "protected-admin"; "local-only"; "inert" ]
        for route in snapshot.Routes do
            if not (Set.contains route.SourceId sourceSet) || not (Set.contains route.EffectClass allowedEffects) || route.Callsites.IsEmpty || route.Callsites.Length <> (route.Callsites |> List.distinct |> List.length) then
                findings.Add(finding "RC-ROUTE" route.Id "route source, effect, or callsite closure is malformed")
            if not route.RemoteEffects.IsEmpty && Set.contains route.EffectClass (Set [ "read-only"; "local-only"; "inert" ]) then
                findings.Add(finding "RC-LAUNDER" route.Id "remote effect cannot be classified as non-writer")
        let count effect = snapshot.Routes |> List.filter (_.EffectClass >> (=) effect) |> List.length
        if count "conditional" <> 197 || count "inert" <> 353 || count "protected-admin" <> 27 || count "read-only" <> 38 then
            findings.Add(finding "RC-EFFECT-COUNTS" "routes" "exact route-effect population differs")
        if not (snapshot.Routes |> List.exists (fun row -> row.Receiver = "sdd" && row.EffectClass = "conditional" && row.Entrypoint.Contains("kit-materialize", StringComparison.Ordinal))) then
            findings.Add(finding "RC-DELEGATED-WRITER" "sdd" "kit-materialize delegated writer route is missing")
        if snapshot.Dependencies.Length <> 95 then findings.Add(finding "RC-DEPENDENCY-COUNT" "dependencies" "expected all 95 callable dependencies")
        for dependency in snapshot.Dependencies do
            if not (Set.contains dependency.SourceId sourceSet) || String.IsNullOrWhiteSpace dependency.Target || String.IsNullOrWhiteSpace dependency.Reference then
                findings.Add(finding "RC-DEPENDENCY" dependency.Target "callable dependency is incomplete")
            if dependency.Target.StartsWith("FS-GG/", StringComparison.Ordinal) && dependency.Resolution = "mutable-source-reference" then
                findings.Add(finding "RC-MUTABLE" dependency.Target "FS-GG dependency remains unresolved")
            if dependency.Resolution = "observed-mutable" && (dependency.ResolvedRevision |> Option.exists sha1 |> not || dependency.CalleeSha256 |> Option.exists sha256 |> not) then
                findings.Add(finding "RC-CALLEE" dependency.Target "observed mutable callee lacks exact revision and bytes")
        if not (snapshot.Dependencies |> List.exists (fun row -> row.Receiver = "rendering" && row.Target = "FS-GG/.github/.github/workflows/dispatch-sender.yml" && row.Reference = "5fed2838f9ed085ffca09f4cc18b4f7bc59c1294" && row.Resolution = "immutable")) then
            findings.Add(finding "RC-HISTORICAL-CALLEE" "rendering" "historical dispatch callee is missing")
        if snapshot.InstalledTools |> List.map _.Version <> [ "0.58.0"; "0.75.4"; "0.87.0" ] then
            findings.Add(finding "RC-TOOLS" "installedTools" "legacy and current tool-source identities differ")
        for tool in snapshot.InstalledTools do
            if not (sha1 tool.Revision && sha1 tool.Tree && sha256 tool.OptionsSha256 && sha256 tool.ProjectSha256) then
                findings.Add(finding "RC-TOOL-IDENTITY" tool.Version "tool-source identity is incomplete")
        if not snapshot.SourceCoverageComplete then findings.Add(finding "RC-COVERAGE" "snapshot" "source census is not terminal and complete")
        if snapshot.Installed then findings.Add(finding "RC-INSTALLED" "claims" "source census must not claim installed behavior")
        if snapshot.Fenced then findings.Add(finding "RC-FENCED" "claims" "source census must not claim the effect fence")
        if snapshot.Accepted then findings.Add(finding "RC-ACCEPTED" "claims" "source census must not claim native acceptance")
        if not snapshot.TelemetryLocalOnly then findings.Add(finding "RC-TELEMETRY" "telemetry" "telemetry boundary must remain local-only")
        if findings.Count = 0 then Ok() else Error(List.ofSeq findings)

    let validateControls generated independent =
        let expected = requiredControls |> List.map controlId |> Set.ofList
        let findingsFor source values =
            let groups = values |> List.groupBy (fun value -> controlId value.Control)
            [ for missing in Set.difference expected (groups |> List.map fst |> Set.ofList) do
                  finding "RC-CONTROL-MISSING" missing $"{source} omitted the required control"
              for control, results in groups do
                  if results.Length <> 1 then finding "RC-CONTROL-DUPLICATE" control $"{source} supplied the control more than once"
                  else
                      if not results.Head.BaselineGreen then finding "RC-BASELINE-RED" control $"{source} baseline is not green"
                      if not results.Head.MutationRed then finding "RC-MUTATION-SURVIVED" control $"{source} mutation did not fail" ]
        let findings = findingsFor "generated" generated @ findingsFor "independent" independent
        if findings.IsEmpty then Ok() else Error findings
