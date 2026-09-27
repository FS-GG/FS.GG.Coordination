namespace FS.GG.Coordination.Cli

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Runtime.CompilerServices
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

[<assembly: InternalsVisibleTo("FS.GG.Coordination.UnitTests")>]
do ()

type MigrationReceiverCopyAcceptedEvidence =
    { Gs2083ReceiptBytes: ReadOnlyMemory<byte>
      ReceiverSourceBindingBytes: ReadOnlyMemory<byte>
      ReceiverCensusBytes: ReadOnlyMemory<byte>
      ReceiverSourceManifestsGzipBytes: ReadOnlyMemory<byte>
      ReceiverSourceBlobsGzipBytes: ReadOnlyMemory<byte> }

type MigrationReceiverCopyMapping =
    { ReceiverCopyId: string
      ReceiverCopyRepository: string
      ReceiverCopySourceRevision: string
      ReceiverCopySourceTree: string
      ReceiverCopyPlannedRef: string
      ReceiverCopyRequiredEntries: MigrationReceiverTreeEntry list
      ReceiverCopyMissingBlobSha1s: string list }

type MigrationReceiverCopyPlanResult =
    { ReceiverCopyRunIdentity: MigrationSandboxSeedRequest
      ReceiverCopyReceiptVerification: AcceptanceReceiptDigestVerification
      ReceiverCopyCensus: ReceiverCensusSnapshot
      ReceiverCopyMappings: MigrationReceiverCopyMapping list
      ReceiverCopyRetainedBlobSha256BySha1: Map<string, string>
      ReceiverCopyFingerprint: string }

type private ReceiverDescription =
    { Id: string; Repository: string; Revision: string; Tree: string
      Version: string; SourceManifestSha256: string }

type private ReceiverManifest =
    { Description: ReceiverDescription
      Entries: MigrationReceiverTreeEntry list }

type private BlobDeclaration =
    { Repository: string
      Revision: string
      Path: string
      BlobSha1: string
      Sha256: string }

type private InstalledToolDeclaration =
    { Version: string
      Repository: string
      Revision: string
      Tree: string
      OptionsPath: string
      OptionsSha256: string
      ProjectPath: string
      ProjectSha256: string }

type private CensusCorrespondence =
    { BlobDigests: string list
      ReviewedCallees: BlobDeclaration list
      InstalledTools: InstalledToolDeclaration list }

[<RequireQualifiedAccess>]
module MigrationReceiverCopyPlan =
    [<Literal>]
    let private sandboxRepositoryId = 1353050537L
    [<Literal>]
    let private sandboxRepositoryNodeId = "R_kgDOUKXpqQ"
    [<Literal>]
    let private sandboxRepositoryName = "FS-GG/FS.GG.GitHub.Substrate.Sandbox"

    let private strictUtf8 = UTF8Encoding(false, true)
    let private sha256 (bytes: ReadOnlyMemory<byte>) =
        SHA256.HashData(bytes.Span) |> Convert.ToHexString |> _.ToLowerInvariant()
    let private sha256Array (bytes: byte array) =
        SHA256.HashData(bytes) |> Convert.ToHexString |> _.ToLowerInvariant()
    let private sha1Array (bytes: byte array) =
        SHA1.HashData(bytes) |> Convert.ToHexString |> _.ToLowerInvariant()
    let private isHex length (value: string) =
        not (isNull value) && value.Length = length
        && (value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
    let private require (condition: bool) (code: string) = if not condition then failwith code

    let private rejectDuplicateMembers (root: JsonElement) =
        let rec visit (path: string) (element: JsonElement) =
            match element.ValueKind with
            | JsonValueKind.Object ->
                let names = HashSet<string>(StringComparer.Ordinal)
                for property in element.EnumerateObject() do
                    require (names.Add property.Name) $"duplicate-json-member:{path}/{property.Name}"
                    visit $"{path}/{property.Name}" property.Value
            | JsonValueKind.Array ->
                element.EnumerateArray() |> Seq.iteri (fun index value -> visit $"{path}/{index}" value)
            | _ -> ()
        visit "$" root

    let private parseJson (label: string) (bytes: ReadOnlyMemory<byte>) (maxBytes: int) =
        require (bytes.Length > 0 && bytes.Length <= maxBytes) $"{label}-size"
        let document = JsonDocument.Parse(strictUtf8.GetString(bytes.Span))
        rejectDuplicateMembers document.RootElement
        document

    let private decompress (label: string) (bytes: ReadOnlyMemory<byte>) (maxCompressed: int) (maxExpanded: int) =
        require (bytes.Length > 0 && bytes.Length <= maxCompressed) $"{label}-compressed-size"
        use source = new MemoryStream(bytes.ToArray(), false)
        use gzip = new GZipStream(source, CompressionMode.Decompress, false)
        use target = new MemoryStream()
        let buffer = Array.zeroCreate<byte> 81920
        let mutable total = 0
        let mutable reading = true
        while reading do
            let count = gzip.Read(buffer, 0, buffer.Length)
            if count = 0 then reading <- false
            else
                total <- total + count
                require (total <= maxExpanded) $"{label}-expanded-size"
                target.Write(buffer, 0, count)
        target.ToArray()

    let private property (name: string) (element: JsonElement) : JsonElement =
        match element.TryGetProperty name with
        | true, value -> value
        | _ -> failwith $"missing-json-member:{name}"
    let private stringProperty (name: string) (element: JsonElement) : string =
        let value = property name element
        require (value.ValueKind = JsonValueKind.String) $"json-string:{name}"
        let result = value.GetString()
        require (not (isNull result)) $"json-string:{name}"
        result
    let private boolProperty (name: string) (element: JsonElement) : bool =
        let value = property name element
        require (value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False) $"json-bool:{name}"
        value.GetBoolean()
    let private stringList (name: string) (element: JsonElement) : string list =
        let value = property name element
        require (value.ValueKind = JsonValueKind.Array) $"json-array:{name}"
        [ for item in value.EnumerateArray() do
              require (item.ValueKind = JsonValueKind.String) $"json-string:{name}"
              let text = item.GetString()
              require (not (isNull text)) $"json-string:{name}"
              yield text ]
    let private optionalStringProperty (name: string) (element: JsonElement) : string option =
        match element.TryGetProperty name with
        | false, _ -> None
        | true, value when value.ValueKind = JsonValueKind.Null -> None
        | true, value when value.ValueKind = JsonValueKind.String ->
            let text = value.GetString()
            require (not (isNull text)) $"json-string:{name}"
            Some text
        | _ -> failwith $"json-string:{name}"
    let private exactMemberNames (expected: string list) (element: JsonElement) (code: string) =
        let actual = element.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
        require (actual = Set.ofList expected) code

    let private compactJsonBytes (element: JsonElement) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
        element.WriteTo writer
        writer.Flush()
        stream.ToArray()

    let private artifactMap (receipt: JsonElement) =
        let artifacts = property "artifacts" receipt
        require (artifacts.ValueKind = JsonValueKind.Array) "receipt-artifacts"
        let rows =
            [ for artifact in artifacts.EnumerateArray() do
                  exactMemberNames [ "name"; "sha256" ] artifact "receipt-artifact-shape"
                  let name, digest = stringProperty "name" artifact, stringProperty "sha256" artifact
                  require (isHex 64 digest) "receipt-artifact-digest"
                  yield name, digest ]
        require (rows.Length = (rows |> List.map fst |> Set.ofList |> Set.count)) "receipt-artifact-duplicate"
        Map.ofList rows

    let private validateReceipt (evidence: MigrationReceiverCopyAcceptedEvidence) =
        require (sha256 evidence.Gs2083ReceiptBytes = AcceptanceReceiptDigest.Gs2083RawReceiptSha256) "receipt-raw-sha256"
        use receipt = parseJson "receipt" evidence.Gs2083ReceiptBytes (256 * 1024)
        let root = receipt.RootElement
        require (stringProperty "schema" root = "fsgg.coordination.unit-acceptance/1") "receipt-schema"
        require (stringProperty "unitId" root = AcceptanceReceiptDigest.Gs2083UnitId) "receipt-unit"
        require (stringProperty "state" root = "accepted") "receipt-state"
        let storedDigest = stringProperty "digest" root
        let verification =
            match AcceptanceReceiptDigest.verify evidence.Gs2083ReceiptBytes AcceptanceReceiptDigest.Gs2083UnitId storedDigest root with
            | Ok value -> value
            | Error message -> failwith $"receipt-digest:{message}"
        let artifacts = artifactMap root
        let bind (name: string) (bytes: ReadOnlyMemory<byte>) =
            let expected = artifacts |> Map.tryFind name |> Option.defaultWith (fun () -> failwith $"receipt-artifact-missing:{name}")
            require (sha256 bytes = expected) $"receipt-artifact-sha256:{name}"
        bind "receiver-source-binding" evidence.ReceiverSourceBindingBytes
        bind "receiver-v1-writer-census" evidence.ReceiverCensusBytes
        bind "receiver-source-manifests" evidence.ReceiverSourceManifestsGzipBytes
        bind "receiver-source-blobs" evidence.ReceiverSourceBlobsGzipBytes
        verification

    let private validateBinding (evidence: MigrationReceiverCopyAcceptedEvidence) =
        use document = parseJson "source-binding" evidence.ReceiverSourceBindingBytes (64 * 1024)
        let root = document.RootElement
        exactMemberNames
            [ "schema"; "producerRepository"; "producerCandidateRevision"; "producerLandedRevision"; "producerTree"
              "censusSha256"; "sourceManifestsSha256"; "sourceBlobsSha256"; "collectorSha256"; "checkerSha256"
              "fixtureRunnerSha256"; "subroadmapSha256"; "nativeRoadmapSha256"; "unifiedRoadmapSha256"
              "acceptedEpochReceiptDigest"; "qualificationScope" ] root "source-binding-shape"
        require (stringProperty "schema" root = "fsgg.v1-receiver-census-source-binding/1") "source-binding-schema"
        require (stringProperty "producerRepository" root = "FS-GG/.github") "source-binding-producer"
        require (stringProperty "producerLandedRevision" root = "3719b6cfc6f2d766ad56f930b56f025e20c4b2cc") "source-binding-revision"
        require (stringProperty "producerTree" root = "d20205d921f5ce128385d2a9ba6361c47d3f9a49") "source-binding-tree"
        for name, bytes in
            [ "censusSha256", evidence.ReceiverCensusBytes
              "sourceManifestsSha256", evidence.ReceiverSourceManifestsGzipBytes
              "sourceBlobsSha256", evidence.ReceiverSourceBlobsGzipBytes ] do
            require (stringProperty name root = sha256 bytes) $"source-binding-sha256:{name}"
        stringProperty "producerLandedRevision" root, stringProperty "producerTree" root,
        stringProperty "acceptedEpochReceiptDigest" root

    let private parseCensus (producerRevision: string) (producerTree: string) (acceptedEpoch: string) (evidence: MigrationReceiverCopyAcceptedEvidence) =
        use document = parseJson "receiver-census" evidence.ReceiverCensusBytes (2 * 1024 * 1024)
        let root = document.RootElement
        require (stringProperty "schema" root = "fsgg.v1-writer-receiver-census/1") "receiver-census-schema"
        require (stringProperty "captureSource" root = "fixed-github-rest-get/1") "receiver-census-source"
        require (boolProperty "terminal" root) "receiver-census-terminal"
        let descriptions: ReceiverDescription list =
            [ for row in (property "receivers" root).EnumerateArray() do
                  yield { Id = stringProperty "id" row; Repository = stringProperty "repository" row
                          Revision = stringProperty "revision" row; Tree = stringProperty "tree" row
                          Version = stringProperty "installedCoordCliVersion" row
                          SourceManifestSha256 = stringProperty "sourceManifestSha256" row } ]
        let sources: ReceiverSourceIdentity list =
            [ for row in (property "sourceIdentities" root).EnumerateArray() do
                  yield { Id = stringProperty "id" row; Receiver = stringProperty "receiver" row
                          Path = stringProperty "path" row; BlobSha1 = stringProperty "blobSha1" row
                          Sha256 = stringProperty "sha256" row } ]
        let routes: WriterRoute list =
            [ for row in (property "writerRoutes" root).EnumerateArray() do
                  yield { Id = stringProperty "id" row; Receiver = stringProperty "receiver" row
                          SourceId = stringProperty "sourceId" row; Entrypoint = stringProperty "entrypoint" row
                          Callsites = stringList "callsites" row; EffectClass = stringProperty "effectClass" row
                          RemoteEffects = stringList "remoteEffects" row
                          CredentialBoundary = stringProperty "credentialBoundary" row
                          LaterDisposition = stringProperty "laterDisposition" row } ]
        let dependencies: CallableDependency list =
            [ for row in (property "callableDependencies" root).EnumerateArray() do
                  yield { Receiver = stringProperty "receiver" row; SourceId = stringProperty "sourceId" row
                          Target = stringProperty "target" row; Reference = stringProperty "reference" row
                          Resolution = stringProperty "resolution" row
                          ResolvedRevision = optionalStringProperty "resolvedRevision" row
                          CalleeSha256 = optionalStringProperty "calleeSha256" row } ]
        let installedTools: InstalledToolIdentity list =
            [ for row in (property "installedTools" root).EnumerateArray() do
                  yield { Version = stringProperty "version" row; Revision = stringProperty "revision" row
                          Tree = stringProperty "tree" row; OptionsSha256 = stringProperty "optionsSha256" row
                          ProjectSha256 = stringProperty "projectSha256" row } ]
        let reviewedCallees =
            [ for row in (property "reviewedCallees" root).EnumerateArray() do
                  yield { Repository = stringProperty "repository" row
                          Revision = stringProperty "revision" row
                          Path = stringProperty "path" row
                          BlobSha1 = stringProperty "blobSha1" row
                          Sha256 = stringProperty "sha256" row } ]
        let installedToolDeclarations =
            [ for row in (property "installedTools" root).EnumerateArray() do
                  yield { Version = stringProperty "version" row
                          Repository = stringProperty "repository" row
                          Revision = stringProperty "revision" row
                          Tree = stringProperty "tree" row
                          OptionsPath = stringProperty "optionsPath" row
                          OptionsSha256 = stringProperty "optionsSha256" row
                          ProjectPath = stringProperty "projectPath" row
                          ProjectSha256 = stringProperty "projectSha256" row } ]
        let claims, telemetry = property "claims" root, property "telemetryBoundary" root
        let snapshot: ReceiverCensusSnapshot =
            { Schema = "fsgg.v1-receiver-census-qualification/1"; ProducerRevision = producerRevision; ProducerTree = producerTree
              CensusSha256 = sha256 evidence.ReceiverCensusBytes
              SourceManifestsSha256 = sha256 evidence.ReceiverSourceManifestsGzipBytes
              SourceBlobsSha256 = sha256 evidence.ReceiverSourceBlobsGzipBytes
              AcceptedEpochReceiptSha256 = acceptedEpoch
              Receivers = descriptions |> List.map (fun row -> row.Id, row.Repository, row.Revision, row.Tree, row.Version)
              Sources = sources; Routes = routes; Dependencies = dependencies; InstalledTools = installedTools
              SourceCoverageComplete = true; Installed = boolProperty "installed" claims
              Fenced = boolProperty "fenced" claims; Accepted = boolProperty "accepted" claims
              TelemetryLocalOnly = stringProperty "classification" telemetry = "local-only"
                                   && not (boolProperty "privateCorpusRead" telemetry)
                                   && not (boolProperty "sourceRead" telemetry) }
        match GitHubV1ReceiverCensusQualification.validateSnapshot snapshot with
        | Error findings ->
            findings |> List.map _.Code |> String.concat "," |> fun codes -> failwith $"receiver-census-qualification:{codes}"
        | Ok() -> ()
        let correspondence =
            { BlobDigests = stringList "sourceBlobDigests" root
              ReviewedCallees = reviewedCallees
              InstalledTools = installedToolDeclarations }
        snapshot, descriptions, correspondence

    let private safePath (path: string) =
        if String.IsNullOrWhiteSpace path || path <> path.Trim() || path.StartsWith('/') || path.EndsWith('/')
           || path.Contains('\\') || (path |> Seq.exists Char.IsControl) then false
        else
            path.Split('/')
            |> Array.forall (fun part ->
                part <> ""
                && part <> "."
                && part <> ".."
                && not (part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
    let private pathParent (path: string) =
        let index = path.LastIndexOf('/')
        if index < 0 then "" else path.Substring(0, index)
    let private pathName (path: string) =
        let index = path.LastIndexOf('/')
        if index < 0 then path else path.Substring(index + 1)

    let private gitTreeSha (children: MigrationReceiverTreeEntry list) (resolvedTreeSha: string -> string) =
        let sortKey (entry: MigrationReceiverTreeEntry) =
            strictUtf8.GetBytes(pathName entry.EntryPath + (if entry.EntryKind = "tree" then "/" else ""))
        let compareBytes (left: byte array) (right: byte array) =
            let mutable index, result = 0, 0
            while result = 0 && index < min left.Length right.Length do
                result <- compare left[index] right[index]; index <- index + 1
            if result <> 0 then result else compare left.Length right.Length
        let ordered = children |> List.sortWith (fun left right -> compareBytes (sortKey left) (sortKey right))
        use body = new MemoryStream()
        for child in ordered do
            let mode = if child.EntryKind = "tree" then "40000" else child.EntryMode
            let prefix = strictUtf8.GetBytes($"{mode} {pathName child.EntryPath}\000")
            body.Write(prefix, 0, prefix.Length)
            let objectSha = if child.EntryKind = "tree" then resolvedTreeSha child.EntryPath else child.EntrySha
            let rawSha = Convert.FromHexString objectSha
            body.Write(rawSha, 0, rawSha.Length)
        let bodyBytes = body.ToArray()
        let header = strictUtf8.GetBytes($"tree {bodyBytes.Length}\000")
        let complete = Array.append header bodyBytes
        sha1Array complete

    let private validateManifestEntries (expectedRoot: string) (entries: MigrationReceiverTreeEntry list) =
        require (not entries.IsEmpty) "manifest-empty"
        let paths = entries |> List.map _.EntryPath
        require (paths.Length = (paths |> Set.ofList |> Set.count)) "manifest-path-duplicate"
        let destinationKeys = paths |> List.map (fun path -> path.Normalize(NormalizationForm.FormC).ToUpperInvariant())
        require (destinationKeys.Length = (destinationKeys |> Set.ofList |> Set.count)) "manifest-destination-collision"
        let byPath = entries |> List.map (fun entry -> entry.EntryPath, entry) |> Map.ofList
        for entry in entries do
            require (safePath entry.EntryPath) "manifest-unsafe-path"
            require (isHex 40 entry.EntrySha) "manifest-object-sha1"
            match entry.EntryKind, entry.EntryMode, entry.EntrySize with
            | "tree", "040000", None -> ()
            | "blob", ("100644" | "100755"), Some size when size >= 0L -> ()
            | _ -> failwith "manifest-entry-shape"
            let parent = pathParent entry.EntryPath
            if parent <> "" then
                match Map.tryFind parent byPath with
                | Some value when value.EntryKind = "tree" -> ()
                | _ -> failwith "manifest-parent-missing"
        let declaredTrees = entries |> List.choose (fun e -> if e.EntryKind = "tree" then Some(e.EntryPath, e.EntrySha) else None) |> Map.ofList
        let computed = Dictionary<string, string>(StringComparer.Ordinal)
        let treePaths = "" :: (declaredTrees |> Map.toList |> List.map fst)
        for treePath in treePaths |> List.sortByDescending (fun path -> path.Split('/').Length, path.Length) do
            let children = entries |> List.filter (fun entry -> pathParent entry.EntryPath = treePath)
            let digest = gitTreeSha children (fun child -> computed[child])
            computed[treePath] <- digest
            if treePath <> "" then require (digest = declaredTrees[treePath]) "manifest-tree-sha1"
        require (computed[""] = expectedRoot) "manifest-root-tree-sha1"

    let private parseManifests
        (census: ReceiverCensusSnapshot)
        (correspondence: CensusCorrespondence)
        (descriptions: ReceiverDescription list)
        (evidence: MigrationReceiverCopyAcceptedEvidence) =
        let expanded = decompress "source-manifests" evidence.ReceiverSourceManifestsGzipBytes (1024 * 1024) (4 * 1024 * 1024)
        use document = parseJson "source-manifests" (ReadOnlyMemory<byte>(expanded)) (4 * 1024 * 1024)
        let root = document.RootElement
        require (stringProperty "schema" root = "fsgg.v1-receiver-source-manifests/1") "manifests-schema"
        require (stringProperty "source" root = "fixed-github-rest-get/1" && boolProperty "terminal" root) "manifests-source"
        let descriptionById = descriptions |> List.map (fun row -> row.Id, row) |> Map.ofList
        let relevantSources = ResizeArray<string * string * string * string>()
        let manifests: ReceiverManifest list =
            [ for row in (property "receivers" root).EnumerateArray() do
                  let id = stringProperty "id" row
                  let expected = descriptionById |> Map.tryFind id |> Option.defaultWith (fun () -> failwith "manifest-unknown-receiver")
                  require (stringProperty "repository" row = expected.Repository && stringProperty "revision" row = expected.Revision
                           && stringProperty "tree" row = expected.Tree && stringProperty "installedCoordCliVersion" row = expected.Version
                           && stringProperty "sourceManifestSha256" row = expected.SourceManifestSha256) "manifest-receiver-binding"
                  let manifestElement = property "manifest" row
                  let computedManifestSha256 = sha256Array (compactJsonBytes manifestElement)
                  require (computedManifestSha256 = expected.SourceManifestSha256) $"manifest-content-sha256:{id}:{computedManifestSha256}"
                  let entries =
                      [ for entry in manifestElement.EnumerateArray() do
                            let size =
                                match entry.TryGetProperty "size" with
                                | true, value -> match value.TryGetInt64() with | true, number -> Some number | _ -> failwith "manifest-size"
                                | _ -> None
                            yield ({ EntryPath = stringProperty "path" entry; EntryMode = stringProperty "mode" entry
                                     EntryKind = stringProperty "type" entry; EntrySha = stringProperty "sha" entry
                                     EntrySize = size }: MigrationReceiverTreeEntry) ]
                      |> List.sortBy _.EntryPath
                  for source in (property "relevantSources" row).EnumerateArray() do
                      let path = stringProperty "path" source
                      let blobSha1 = stringProperty "blobSha1" source
                      let bytesSha256 = stringProperty "sha256" source
                      require (safePath path && isHex 40 blobSha1 && isHex 64 bytesSha256) "manifest-relevant-source"
                      relevantSources.Add(id, path, blobSha1, bytesSha256)
                  validateManifestEntries expected.Tree entries
                  yield { Description = expected; Entries = entries } ]
        require (manifests.Length = 7) "manifest-receiver-count"
        require ((manifests |> List.map (_.Description.Id)) = (descriptions |> List.map _.Id)) "manifest-receiver-order"
        let relevantRows = List.ofSeq relevantSources
        require (relevantRows.Length = (relevantRows |> Set.ofList |> Set.count)) "manifest-relevant-source-duplicate"
        let relevantSet = Set.ofList relevantRows
        let censusSourceSet = census.Sources |> List.map (fun source -> source.Receiver, source.Path, source.BlobSha1, source.Sha256) |> Set.ofList
        require (Set.isSubset censusSourceSet relevantSet) "manifest-relevant-source-join"
        require (censusSourceSet.Count = 555) "manifest-relevant-source-count"

        let parseBlobDeclaration row =
            let declaration =
                { Repository = stringProperty "repository" row
                  Revision = stringProperty "revision" row
                  Path = stringProperty "path" row
                  BlobSha1 = stringProperty "blobSha1" row
                  Sha256 = stringProperty "sha256" row }
            require (declaration.Repository = "FS-GG/.github" && isHex 40 declaration.Revision
                     && safePath declaration.Path && isHex 40 declaration.BlobSha1 && isHex 64 declaration.Sha256)
                    "manifest-external-declaration"
            declaration

        let reviewedCallees =
            [ for row in (property "reviewedCallees" root).EnumerateArray() do yield parseBlobDeclaration row ]
        require (Set.ofList reviewedCallees = Set.ofList correspondence.ReviewedCallees
                 && reviewedCallees.Length = correspondence.ReviewedCallees.Length)
                "manifest-reviewed-callee-join"

        let toolRows = ResizeArray<InstalledToolDeclaration>()
        let toolBlobs = ResizeArray<BlobDeclaration>()
        for row in (property "toolSources" root).EnumerateArray() do
            let options = parseBlobDeclaration (property "options" row)
            let project = parseBlobDeclaration (property "project" row)
            let declaration =
                { Version = stringProperty "version" row
                  Repository = stringProperty "repository" row
                  Revision = stringProperty "revision" row
                  Tree = stringProperty "tree" row
                  OptionsPath = options.Path
                  OptionsSha256 = options.Sha256
                  ProjectPath = project.Path
                  ProjectSha256 = project.Sha256 }
            require (options.Repository = declaration.Repository && project.Repository = declaration.Repository
                     && options.Revision = declaration.Revision && project.Revision = declaration.Revision)
                    "manifest-tool-source-binding"
            toolRows.Add declaration
            toolBlobs.Add options
            toolBlobs.Add project
        require (Set.ofSeq toolRows = Set.ofList correspondence.InstalledTools
                 && toolRows.Count = correspondence.InstalledTools.Length)
                "manifest-tool-source-join"
        let external = reviewedCallees @ List.ofSeq toolBlobs
        require (external.Length = 13
                 && external.Length = (external |> List.map _.Sha256 |> Set.ofList |> Set.count)
                 && external.Length = (external |> List.map _.BlobSha1 |> Set.ofList |> Set.count))
                "manifest-external-count"
        manifests, (external |> List.map (fun row -> row.Sha256, row.BlobSha1) |> Map.ofList)

    let private gitBlobSha (bytes: byte array) =
        let header = strictUtf8.GetBytes($"blob {bytes.Length}\000")
        sha1Array (Array.append header bytes)
    let private parseRetainedBlobRows
        (census: ReceiverCensusSnapshot)
        (censusDigests: string list)
        (manifests: ReceiverManifest list)
        (external: Map<string, string>)
        (evidence: MigrationReceiverCopyAcceptedEvidence) =
        let expanded = decompress "source-blobs" evidence.ReceiverSourceBlobsGzipBytes (3 * 1024 * 1024) (8 * 1024 * 1024)
        use document = parseJson "source-blobs" (ReadOnlyMemory<byte>(expanded)) (8 * 1024 * 1024)
        let root = document.RootElement
        require (stringProperty "schema" root = "fsgg.v1-receiver-source-blobs/1") "blobs-schema"
        let manifestBlobs = manifests |> List.collect _.Entries |> List.filter (fun e -> e.EntryKind = "blob") |> List.groupBy _.EntrySha |> Map.ofList
        let censusSources = census.Sources |> List.groupBy _.Sha256 |> Map.ofList
        let declaredDigests = Set.union (censusSources |> Map.keys |> Set.ofSeq) (external |> Map.keys |> Set.ofSeq)
        require (declaredDigests = Set.ofList censusDigests && declaredDigests.Count = 460) "blob-declaration-census-join"
        let retained =
            [ for row in (property "blobs" root).EnumerateArray() do
                  exactMemberNames [ "bytesBase64"; "sha256" ] row "blob-shape"
                  let expectedSha256 = stringProperty "sha256" row
                  require (isHex 64 expectedSha256) "blob-sha256"
                  let bytes = Convert.FromBase64String(stringProperty "bytesBase64" row)
                  require (sha256Array bytes = expectedSha256) "blob-bytes-sha256"
                  let blobSha1 = gitBlobSha bytes
                  let expectedSha1s =
                      match Map.tryFind expectedSha256 censusSources, Map.tryFind expectedSha256 external with
                      | Some sources, None -> sources |> List.map _.BlobSha1
                      | None, Some externalSha1 -> [ externalSha1 ]
                      | _ -> failwith "blob-declaration-ambiguous"
                  require (expectedSha1s |> List.forall ((=) blobSha1)) "blob-git-sha1"
                  match Map.tryFind blobSha1 manifestBlobs with
                  | Some uses -> require (uses |> List.forall (fun entry -> entry.EntrySize = Some(int64 bytes.Length))) "blob-length"
                  | None -> ()
                  yield blobSha1, (expectedSha256, bytes) ]
        require (retained.Length = (retained |> List.map fst |> Set.ofList |> Set.count)) "blob-sha1-duplicate"
        require (retained.Length = (retained |> List.map (snd >> fst) |> Set.ofList |> Set.count)) "blob-sha256-duplicate"
        require (List.sort censusDigests = (retained |> List.map (snd >> fst) |> List.sort)) "blob-census-binding"
        Map.ofList retained

    let private parseRetainedBlobs census censusDigests manifests external evidence =
        parseRetainedBlobRows census censusDigests manifests external evidence
        |> Map.map (fun _ (digest, _) -> digest)

    let private validRunIdentity (request: MigrationSandboxSeedRequest) =
        isHex 40 request.CandidateSha && isHex 64 request.CorpusSha256 && request.WorkflowRunId > 0L
        && request.WorkflowRunAttempt > 0
        && request.RunNonce = $"{request.WorkflowRunId}-{request.WorkflowRunAttempt}-{request.CandidateSha}"
        && request.RunNonce.Length <= 160

    let private buildMappings
        (runIdentity: MigrationSandboxSeedRequest)
        (manifests: ReceiverManifest list)
        (retained: Map<string, string>) =
        manifests |> List.map (fun manifest ->
            let missing = manifest.Entries |> List.filter (fun e -> e.EntryKind = "blob" && not (retained.ContainsKey e.EntrySha))
                          |> List.map _.EntrySha |> List.distinct |> List.sort
            { ReceiverCopyId = manifest.Description.Id; ReceiverCopyRepository = sandboxRepositoryName
              ReceiverCopySourceRevision = manifest.Description.Revision; ReceiverCopySourceTree = manifest.Description.Tree
              ReceiverCopyPlannedRef = $"refs/heads/gs2-09-7/{runIdentity.RunNonce}/receivers/{manifest.Description.Id}"
              ReceiverCopyRequiredEntries = manifest.Entries; ReceiverCopyMissingBlobSha1s = missing })

    let internal deriveUnpinnedForTests
        (receiverCensusBytes: ReadOnlyMemory<byte>)
        (receiverSourceManifestsGzipBytes: ReadOnlyMemory<byte>)
        (receiverSourceBlobsGzipBytes: ReadOnlyMemory<byte>)
        (runIdentity: MigrationSandboxSeedRequest) =
        try
            require (validRunIdentity runIdentity) "invalid-run-identity"
            let evidence =
                { Gs2083ReceiptBytes = ReadOnlyMemory<byte>.Empty
                  ReceiverSourceBindingBytes = ReadOnlyMemory<byte>.Empty
                  ReceiverCensusBytes = receiverCensusBytes
                  ReceiverSourceManifestsGzipBytes = receiverSourceManifestsGzipBytes
                  ReceiverSourceBlobsGzipBytes = receiverSourceBlobsGzipBytes }
            let census, descriptions, correspondence =
                parseCensus
                    "3719b6cfc6f2d766ad56f930b56f025e20c4b2cc"
                    "d20205d921f5ce128385d2a9ba6361c47d3f9a49"
                    "49c70359ebfbc00331ba90c7c5b100a292efa4cc95a5dfa8007867ceceec5c31"
                    evidence
            let manifests, external = parseManifests census correspondence descriptions evidence
            let retained = parseRetainedBlobs census correspondence.BlobDigests manifests external evidence
            Ok(buildMappings runIdentity manifests retained, retained)
        with ex -> Error ex.Message

    let private fingerprint
        (evidence: MigrationReceiverCopyAcceptedEvidence)
        (run: MigrationSandboxSeedRequest)
        (receiptVerification: AcceptanceReceiptDigestVerification)
        (census: ReceiverCensusSnapshot)
        (mappings: MigrationReceiverCopyMapping list)
        (retained: Map<string, string>) =
        use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        let feedBytes (bytes: byte array) =
            let length = Array.zeroCreate<byte> 8
            BinaryPrimitives.WriteInt64BigEndian(length, int64 bytes.Length)
            hash.AppendData length; hash.AppendData bytes
        let feedString (value: string) = feedBytes (strictUtf8.GetBytes value)
        for bytes in [ evidence.Gs2083ReceiptBytes; evidence.ReceiverSourceBindingBytes; evidence.ReceiverCensusBytes
                       evidence.ReceiverSourceManifestsGzipBytes; evidence.ReceiverSourceBlobsGzipBytes ] do feedBytes (bytes.ToArray())
        for value in [ run.CandidateSha; string run.WorkflowRunId; string run.WorkflowRunAttempt; run.RunNonce; run.CorpusSha256
                       string sandboxRepositoryId; sandboxRepositoryNodeId; sandboxRepositoryName
                       receiptVerification.StoredDigest; receiptVerification.CanonicalDigest; string receiptVerification.CompatibilityApplied
                       census.Schema; census.ProducerRevision; census.ProducerTree ] do feedString value
        for mapping in mappings do
            for value in [ mapping.ReceiverCopyId; mapping.ReceiverCopyRepository; mapping.ReceiverCopySourceRevision
                           mapping.ReceiverCopySourceTree; mapping.ReceiverCopyPlannedRef ] do feedString value
            for entry in mapping.ReceiverCopyRequiredEntries do
                for value in [ entry.EntryPath; entry.EntryMode; entry.EntryKind; entry.EntrySha
                               entry.EntrySize |> Option.map string |> Option.defaultValue "-" ] do feedString value
            mapping.ReceiverCopyMissingBlobSha1s |> List.iter feedString
        for KeyValue(blobSha1, bytesSha256) in retained do feedString blobSha1; feedString bytesSha256
        hash.GetHashAndReset() |> Convert.ToHexString |> _.ToLowerInvariant()

    let derive (acceptedEvidence: MigrationReceiverCopyAcceptedEvidence) (runIdentity: MigrationSandboxSeedRequest) =
        try
            require (validRunIdentity runIdentity) "invalid-run-identity"
            let receiptVerification = validateReceipt acceptedEvidence
            let producerRevision, producerTree, acceptedEpoch = validateBinding acceptedEvidence
            let census, descriptions, correspondence = parseCensus producerRevision producerTree acceptedEpoch acceptedEvidence
            let manifests, external = parseManifests census correspondence descriptions acceptedEvidence
            let retained = parseRetainedBlobs census correspondence.BlobDigests manifests external acceptedEvidence
            let mappings = buildMappings runIdentity manifests retained
            let digest = fingerprint acceptedEvidence runIdentity receiptVerification census mappings retained
            Ok { ReceiverCopyRunIdentity = runIdentity; ReceiverCopyReceiptVerification = receiptVerification
                 ReceiverCopyCensus = census; ReceiverCopyMappings = mappings
                 ReceiverCopyRetainedBlobSha256BySha1 = retained; ReceiverCopyFingerprint = digest }
        with ex -> Error ex.Message

    let verify
        (acceptedEvidence: MigrationReceiverCopyAcceptedEvidence)
        (runIdentity: MigrationSandboxSeedRequest)
        (observed: MigrationReceiverCopyPlanResult) =
        derive acceptedEvidence runIdentity
        |> Result.bind (fun expected -> if expected = observed then Ok expected else Error "receiver-copy-plan-drift")

    let internal readRetainedBlobBytes acceptedEvidence runIdentity observed =
        verify acceptedEvidence runIdentity observed
        |> Result.bind (fun _ ->
            try
                let producerRevision, producerTree, acceptedEpoch = validateBinding acceptedEvidence
                let census, descriptions, correspondence = parseCensus producerRevision producerTree acceptedEpoch acceptedEvidence
                let manifests, external = parseManifests census correspondence descriptions acceptedEvidence
                let rows = parseRetainedBlobRows census correspondence.BlobDigests manifests external acceptedEvidence
                require
                    (rows |> Map.map (fun _ (digest, _) -> digest) = observed.ReceiverCopyRetainedBlobSha256BySha1)
                    "receiver-copy-retained-bytes-drift"
                Ok(rows |> Map.map (fun _ (_, bytes) -> bytes))
            with ex -> Error ex.Message)
