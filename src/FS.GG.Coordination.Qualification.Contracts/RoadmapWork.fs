namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

type RoadmapWorkFinding =
    { Code: string
      Path: string
      Message: string }

type RoadmapUnit =
    { Id: string
      Title: string
      Owner: string
      Prerequisites: string list
      PermissionCeiling: string list
      ExitGate: string
      QGates: string list
      GateCommands: string list
      ContractSha256: string }

type RoadmapInspection =
    { RoadmapRevision: string
      RoadmapSha256: string
      Unit: RoadmapUnit }

type PrerequisiteStatus =
    { UnitId: string
      Ready: bool
      AcceptedReceiptDigests: string list }

type RoadmapArtifactInput =
    { Name: string
      Path: string
      Bytes: ReadOnlyMemory<byte> }

type RoadmapCandidate =
    { Commit: string
      Tree: string }

[<RequireQualifiedAccess>]
module RoadmapWork =
    [<Literal>]
    let private IndexSchema = "fsgg.coordination.roadmap-index/1"

    [<Literal>]
    let private ReceiptSchema = "fsgg.coordination.unit-acceptance/1"

    [<Literal>]
    let private ManifestSchema = "fsgg.coordination.unit-evidence/1"

    let private sha256 (bytes: ReadOnlyMemory<byte>) =
        SHA256.HashData(bytes.Span) |> Convert.ToHexString |> _.ToLowerInvariant()

    let private finding code path message =
        { Code = code; Path = path; Message = message }

    let private isSha (value: string) length =
        not (isNull value) && value.Length = length && value |> Seq.forall Uri.IsHexDigit

    let private strictObject path allowed (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            Error [ finding "RW-JSON-TYPE" path "expected an object" ]
        else
            let names = HashSet<string>(StringComparer.Ordinal)
            let properties = element.EnumerateObject() |> Seq.toList
            let errors =
                [ for property in properties do
                      if not (names.Add property.Name) then
                          yield finding "RW-JSON-DUPLICATE" $"{path}/{property.Name}" "duplicate member"
                      if not (Set.contains property.Name allowed) then
                          yield finding "RW-JSON-UNKNOWN" $"{path}/{property.Name}" "unknown member" ]
            if List.isEmpty errors then Ok properties else Error errors

    let private property (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) then Some value else None

    let private requiredString path name element =
        match property name element with
        | Some value when value.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(value.GetString())) ->
            Ok(value.GetString())
        | _ -> Error [ finding "RW-JSON-REQUIRED" $"{path}/{name}" "required non-empty string" ]

    let private stringList path name allowEmpty element =
        match property name element with
        | Some value when value.ValueKind = JsonValueKind.Array ->
            let values = value.EnumerateArray() |> Seq.toList
            let errors =
                [ if not allowEmpty && List.isEmpty values then
                      yield finding "RW-JSON-REQUIRED" $"{path}/{name}" "array must not be empty"
                  for index, item in values |> List.indexed do
                      if item.ValueKind <> JsonValueKind.String || String.IsNullOrWhiteSpace(item.GetString()) then
                          yield finding "RW-JSON-TYPE" $"{path}/{name}/{index}" "expected a non-empty string" ]
            if List.isEmpty errors then
                let result = values |> List.map _.GetString()
                if result.Length <> (result |> List.distinct).Length then
                    Error [ finding "RW-JSON-DUPLICATE" $"{path}/{name}" "duplicate array member" ]
                else Ok result
            else Error errors
        | _ -> Error [ finding "RW-JSON-REQUIRED" $"{path}/{name}" "required string array" ]

    let private combine results =
        let errors = results |> List.collect (function Error values -> values | Ok _ -> [])
        if List.isEmpty errors then Ok() else Error errors

    let private canonicalBytesOmitting omittedRootMember (element: JsonElement) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))

        let rec write isRoot (value: JsonElement) =
            match value.ValueKind with
            | JsonValueKind.Object ->
                writer.WriteStartObject()
                value.EnumerateObject()
                |> Seq.filter (fun memberValue -> not (isRoot && memberValue.Name = omittedRootMember))
                |> Seq.sortBy _.Name
                |> Seq.iter (fun memberValue ->
                    writer.WritePropertyName(memberValue.Name)
                    write false memberValue.Value)
                writer.WriteEndObject()
            | JsonValueKind.Array ->
                writer.WriteStartArray()
                value.EnumerateArray() |> Seq.iter (write false)
                writer.WriteEndArray()
            | JsonValueKind.String -> writer.WriteStringValue(value.GetString())
            | JsonValueKind.Number -> writer.WriteRawValue(value.GetRawText(), true)
            | JsonValueKind.True -> writer.WriteBooleanValue(true)
            | JsonValueKind.False -> writer.WriteBooleanValue(false)
            | JsonValueKind.Null -> writer.WriteNullValue()
            | _ -> invalidOp "unsupported JSON token"

        write true element
        writer.Flush()
        stream.ToArray()

    type private Index =
        { Revision: string
          RoadmapPath: string
          RoadmapSha: string
          Units: RoadmapUnit list }

    let private parseUnit index (element: JsonElement) =
        let path = $"/units/{index}"
        let allowed =
            Set.ofList [ "id"; "title"; "owner"; "prerequisites"; "permissionCeiling"; "exitGate"; "qGates"; "gateCommands"; "contractSha256" ]
        match strictObject path allowed element with
        | Error errors -> Error errors
        | Ok _ ->
            let id = requiredString path "id" element
            let title = requiredString path "title" element
            let owner = requiredString path "owner" element
            let prerequisites = stringList path "prerequisites" true element
            let permissionCeiling = stringList path "permissionCeiling" false element
            let exitGate = requiredString path "exitGate" element
            let qGates = stringList path "qGates" true element
            let gateCommands = stringList path "gateCommands" true element
            let contractSha = requiredString path "contractSha256" element
            match combine [ id |> Result.map ignore; title |> Result.map ignore; owner |> Result.map ignore
                            prerequisites |> Result.map ignore; permissionCeiling |> Result.map ignore
                            exitGate |> Result.map ignore; qGates |> Result.map ignore; gateCommands |> Result.map ignore
                            contractSha |> Result.map ignore ] with
            | Error errors -> Error errors
            | Ok _ ->
                let unitValue =
                    { Id = Result.defaultValue "" id
                      Title = Result.defaultValue "" title
                      Owner = Result.defaultValue "" owner
                      Prerequisites = Result.defaultValue [] prerequisites
                      PermissionCeiling = Result.defaultValue [] permissionCeiling
                      ExitGate = Result.defaultValue "" exitGate
                      QGates = Result.defaultValue [] qGates
                      GateCommands = Result.defaultValue [] gateCommands
                      ContractSha256 = Result.defaultValue "" contractSha }
                let errors =
                    [ if not (Regex.IsMatch(unitValue.Id, "^GS2-[0-9]{2}\\.[0-9]+$", RegexOptions.CultureInvariant)) then
                          yield finding "RW-UNIT-ID" $"{path}/id" "unit id must use the stable GS2-NN.N form"
                      let calculated = sha256 (ReadOnlyMemory<byte>(canonicalBytesOmitting "contractSha256" element))
                      if unitValue.ContractSha256 <> calculated then
                          yield finding "RW-UNIT-CONTRACT" $"{path}/contractSha256" $"expected canonical digest {calculated}"
                      for gate in unitValue.QGates do
                          if not (Regex.IsMatch(gate, "^Q(?:[0-9]|10)$", RegexOptions.CultureInvariant)) then
                              yield finding "RW-Q-GATE" $"{path}/qGates" $"unknown qualification gate: {gate}" ]
                if List.isEmpty errors then Ok unitValue else Error errors

    let private parseIndex (bytes: ReadOnlyMemory<byte>) =
        try
            use document = JsonDocument.Parse bytes
            let root = document.RootElement
            let allowed = Set.ofList [ "schema"; "roadmap"; "units" ]
            match strictObject "" allowed root with
            | Error errors -> Error errors
            | Ok _ ->
                let schema = requiredString "" "schema" root
                let roadmap = property "roadmap" root
                let units = property "units" root
                let mutable errors =
                    [ match schema with
                      | Ok value when value = IndexSchema -> ()
                      | Ok value -> yield finding "RW-INDEX-SCHEMA" "/schema" $"expected {IndexSchema}, observed {value}"
                      | Error values -> yield! values ]

                let roadmapResult =
                    match roadmap with
                    | Some value ->
                        let allowedRoadmap = Set.ofList [ "repository"; "revision"; "path"; "sha256" ]
                        match strictObject "/roadmap" allowedRoadmap value with
                        | Error values -> Error values
                        | Ok _ ->
                            let repository = requiredString "/roadmap" "repository" value
                            let revision = requiredString "/roadmap" "revision" value
                            let path = requiredString "/roadmap" "path" value
                            let digest = requiredString "/roadmap" "sha256" value
                            match combine [ repository |> Result.map ignore; revision |> Result.map ignore; path |> Result.map ignore; digest |> Result.map ignore ] with
                            | Error values -> Error values
                            | Ok _ ->
                                let repo = Result.defaultValue "" repository
                                let rev = Result.defaultValue "" revision
                                let roadmapPath = Result.defaultValue "" path
                                let sha = Result.defaultValue "" digest
                                let validation =
                                    [ if repo <> "FS-GG/.github" then yield finding "RW-ROADMAP-REPOSITORY" "/roadmap/repository" "expected FS-GG/.github"
                                      if not (isSha rev 40) then yield finding "RW-ROADMAP-REVISION" "/roadmap/revision" "expected exact 40-hex revision"
                                      if Path.IsPathRooted roadmapPath || roadmapPath.Split('/') |> Array.contains ".." then
                                          yield finding "RW-PATH" "/roadmap/path" "roadmap path must be repository-relative"
                                      if not (isSha sha 64) then yield finding "RW-SHA256" "/roadmap/sha256" "expected lowercase 64-hex SHA-256" ]
                                if List.isEmpty validation then Ok(rev, roadmapPath, sha) else Error validation
                    | None -> Error [ finding "RW-JSON-REQUIRED" "/roadmap" "required object" ]

                let unitResults =
                    match units with
                    | Some value when value.ValueKind = JsonValueKind.Array && value.GetArrayLength() > 0 ->
                        value.EnumerateArray() |> Seq.mapi parseUnit |> Seq.toList
                    | _ -> [ Error [ finding "RW-JSON-REQUIRED" "/units" "required non-empty array" ] ]
                errors <- errors @ (unitResults |> List.collect (function Error values -> values | Ok _ -> []))
                match roadmapResult with Error values -> errors <- errors @ values | Ok _ -> ()
                let parsedUnits = unitResults |> List.choose Result.toOption
                let duplicateIds =
                    parsedUnits |> List.countBy _.Id |> List.choose (fun (id, count) -> if count > 1 then Some id else None)
                for id in duplicateIds do errors <- errors @ [ finding "RW-UNIT-DUPLICATE" "/units" $"duplicate unit id: {id}" ]
                let known = parsedUnits |> List.map _.Id |> Set.ofList
                for unitValue in parsedUnits do
                    for prerequisite in unitValue.Prerequisites do
                        if not (Set.contains prerequisite known) then
                            errors <- errors @ [ finding "RW-PREREQUISITE-UNKNOWN" $"/units/{unitValue.Id}/prerequisites" prerequisite ]
                    if unitValue.QGates.IsEmpty <> unitValue.GateCommands.IsEmpty then
                        errors <- errors @ [ finding "RW-GATE-INCOMPLETE" $"/units/{unitValue.Id}" "qGates and gateCommands must both be empty or both be populated" ]
                if not (List.isEmpty errors) then Error errors
                else
                    let revision, roadmapPath, digest = Result.defaultValue ("", "", "") roadmapResult
                    Ok { Revision = revision; RoadmapPath = roadmapPath; RoadmapSha = digest; Units = parsedUnits }
        with :? JsonException as error ->
            Error [ finding "RW-INDEX-JSON" "/" error.Message ]

    let private validateRoadmap (index: Index) (bytes: ReadOnlyMemory<byte>) =
        let digest = sha256 bytes
        let errors =
            [ if digest <> index.RoadmapSha then
                  yield finding "RW-ROADMAP-DIGEST" index.RoadmapPath $"expected {index.RoadmapSha}, observed {digest}" ]
        if not (List.isEmpty errors) then Error errors
        else
            try
                let text = UTF8Encoding(false, true).GetString(bytes.Span)
                let missing =
                    index.Units
                    |> List.choose (fun unitValue ->
                        let marker = $"**{unitValue.Id} — {unitValue.Title}.**"
                        if text.Contains(marker, StringComparison.Ordinal) then None
                        else Some(finding "RW-ROADMAP-UNIT" index.RoadmapPath $"roadmap is missing exact unit heading: {marker}"))
                if List.isEmpty missing then Ok() else Error missing
            with :? DecoderFallbackException as error ->
                Error [ finding "RW-ROADMAP-UTF8" index.RoadmapPath error.Message ]

    let private validated indexBytes roadmapBytes unitId =
        match parseIndex indexBytes with
        | Error errors -> Error errors
        | Ok index ->
            match validateRoadmap index roadmapBytes with
            | Error errors -> Error errors
            | Ok _ ->
                match index.Units |> List.tryFind (fun unitValue -> unitValue.Id = unitId) with
                | None -> Error [ finding "RW-UNIT-UNKNOWN" "/unit" $"unit is not registered: {unitId}" ]
                | Some unitValue -> Ok(index, unitValue)

    let inspect index roadmap unitId =
        validated index roadmap unitId
        |> Result.map (fun (indexValue, unitValue) ->
            { RoadmapRevision = indexValue.Revision
              RoadmapSha256 = indexValue.RoadmapSha
              Unit = unitValue })

    type private Receipt =
        { UnitId: string
          UnitContractSha256: string
          Digest: string }

    let private parseReceipt (bytes: ReadOnlyMemory<byte>) =
        try
            use document = JsonDocument.Parse bytes
            let root = document.RootElement
            let allowed = Set.ofList [ "schema"; "unitId"; "state"; "unitContractSha256"; "sourceRevision"; "artifacts"; "acceptedAt"; "digest" ]
            match strictObject "" allowed root with
            | Error errors -> Error errors
            | Ok _ ->
                let schema = requiredString "" "schema" root
                let unitId = requiredString "" "unitId" root
                let state = requiredString "" "state" root
                let unitContractSha = requiredString "" "unitContractSha256" root
                let sourceRevision = requiredString "" "sourceRevision" root
                let acceptedAt = requiredString "" "acceptedAt" root
                let digest = requiredString "" "digest" root
                let artifacts = property "artifacts" root
                let mutable errors =
                    [ match schema with Ok value when value = ReceiptSchema -> () | Ok value -> yield finding "RW-RECEIPT-SCHEMA" "/schema" value | Error values -> yield! values
                      match state with Ok "accepted" -> () | Ok value -> yield finding "RW-RECEIPT-STATE" "/state" $"expected accepted, observed {value}" | Error values -> yield! values
                      match unitContractSha with Ok value when isSha value 64 -> () | Ok _ -> yield finding "RW-RECEIPT-CONTRACT" "/unitContractSha256" "expected lowercase 64-hex SHA-256" | Error values -> yield! values
                      match sourceRevision with Ok value when isSha value 40 -> () | Ok _ -> yield finding "RW-RECEIPT-SOURCE" "/sourceRevision" "expected exact 40-hex revision" | Error values -> yield! values
                      match acceptedAt with
                      | Ok value -> match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with true, _ -> () | _ -> yield finding "RW-RECEIPT-TIME" "/acceptedAt" "expected ISO-8601 instant"
                      | Error values -> yield! values
                      match digest with Ok value when isSha value 64 -> () | Ok _ -> yield finding "RW-RECEIPT-DIGEST" "/digest" "expected lowercase 64-hex SHA-256" | Error values -> yield! values ]
                match artifacts with
                | Some value when value.ValueKind = JsonValueKind.Array && value.GetArrayLength() > 0 ->
                    for index, artifact in value.EnumerateArray() |> Seq.indexed do
                        match strictObject $"/artifacts/{index}" (Set.ofList [ "name"; "sha256" ]) artifact with
                        | Error values -> errors <- errors @ values
                        | Ok _ ->
                            match requiredString $"/artifacts/{index}" "name" artifact with Error values -> errors <- errors @ values | Ok _ -> ()
                            match requiredString $"/artifacts/{index}" "sha256" artifact with
                            | Ok value when isSha value 64 -> ()
                            | Ok _ -> errors <- errors @ [ finding "RW-SHA256" $"/artifacts/{index}/sha256" "expected lowercase 64-hex SHA-256" ]
                            | Error values -> errors <- errors @ values
                | _ -> errors <- errors @ [ finding "RW-JSON-REQUIRED" "/artifacts" "required non-empty array" ]
                let calculated = sha256 (ReadOnlyMemory<byte>(canonicalBytesOmitting "digest" root))
                match digest with
                | Ok value when value <> calculated -> errors <- errors @ [ finding "RW-RECEIPT-TAMPERED" "/digest" $"expected canonical digest {calculated}" ]
                | _ -> ()
                if List.isEmpty errors then
                    Ok { UnitId = Result.defaultValue "" unitId
                         UnitContractSha256 = Result.defaultValue "" unitContractSha
                         Digest = Result.defaultValue "" digest }
                else Error errors
        with :? JsonException as error -> Error [ finding "RW-RECEIPT-JSON" "/" error.Message ]

    let private prerequisites index unitValue receiptDocuments =
        let parsed = receiptDocuments |> List.map parseReceipt
        let errors = parsed |> List.collect (function Error values -> values | Ok _ -> [])
        if not (List.isEmpty errors) then Error errors
        else
            let receipts = parsed |> List.choose Result.toOption
            let duplicateIds = receipts |> List.countBy _.UnitId |> List.filter (fun (_, count) -> count > 1)
            let mutable findings =
                duplicateIds |> List.map (fun (id, _) -> finding "RW-RECEIPT-DUPLICATE" "/receipts" $"duplicate receipt for {id}")
            for receipt in receipts do
                match index.Units |> List.tryFind (fun unitValue -> unitValue.Id = receipt.UnitId) with
                | None -> findings <- findings @ [ finding "RW-RECEIPT-UNIT" "/receipts" $"receipt unit is not registered: {receipt.UnitId}" ]
                | Some unitValue when unitValue.ContractSha256 <> receipt.UnitContractSha256 ->
                    findings <- findings @ [ finding "RW-RECEIPT-STALE" "/receipts" $"receipt contract for {receipt.UnitId} does not match the current unit definition" ]
                | Some _ -> ()
            for prerequisite in unitValue.Prerequisites do
                if receipts |> List.exists (fun receipt -> receipt.UnitId = prerequisite) |> not then
                    findings <- findings @ [ finding "RW-PREREQUISITE-MISSING" "/receipts" $"accepted receipt missing for {prerequisite}" ]
            if not (List.isEmpty findings) then Error findings
            else
                Ok(
                    unitValue.Prerequisites
                    |> List.map (fun prerequisite -> receipts |> List.find (fun receipt -> receipt.UnitId = prerequisite))
                )

    let checkPrerequisites indexBytes roadmapBytes receiptDocuments unitId =
        match validated indexBytes roadmapBytes unitId with
        | Error errors -> Error errors
        | Ok(index, unitValue) ->
            prerequisites index unitValue receiptDocuments
            |> Result.map (fun receipts ->
                { UnitId = unitId
                  Ready = true
                  AcceptedReceiptDigests = receipts |> List.map _.Digest })

    let private validArtifact (artifact: RoadmapArtifactInput) =
        let path = artifact.Path.Replace('\\', '/')
        let segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
        [ if String.IsNullOrWhiteSpace artifact.Name then yield finding "RW-ARTIFACT-NAME" "/artifacts" "artifact name is required"
          if String.IsNullOrWhiteSpace path || Path.IsPathRooted path || segments |> Array.contains ".." then
              yield finding "RW-PATH" $"/artifacts/{artifact.Name}/path" "artifact path must be repository-relative and contained"
          if artifact.Bytes.IsEmpty then yield finding "RW-ARTIFACT-EMPTY" $"/artifacts/{artifact.Name}" "artifact must not be empty" ]

    let private manifestNode (indexSha: string) (index: Index) (unitValue: RoadmapUnit) (receipts: Receipt list) (candidate: RoadmapCandidate) (createdAt: string) (artifacts: RoadmapArtifactInput list) =
        let array (values: string list) = JsonArray(values |> Seq.map (fun value -> JsonValue.Create(value) :> JsonNode) |> Seq.toArray)
        let artifactNodes =
            artifacts
            |> List.sortBy _.Name
            |> List.map (fun artifact ->
                let node = JsonObject()
                node.Add("name", artifact.Name)
                node.Add("path", artifact.Path.Replace('\\', '/'))
                node.Add("sha256", sha256 artifact.Bytes)
                node :> JsonNode)
            |> List.toArray
        let root = JsonObject()
        root.Add("schema", ManifestSchema)
        root.Add("state", "candidate")
        root.Add("unitId", unitValue.Id)
        root.Add("indexSha256", indexSha)
        root.Add("roadmapRevision", index.Revision)
        root.Add("roadmapSha256", index.RoadmapSha)
        root.Add("candidateCommit", candidate.Commit)
        root.Add("candidateTree", candidate.Tree)
        root.Add("prerequisiteReceiptDigests", array (receipts |> List.map _.Digest))
        root.Add("qGates", array unitValue.QGates)
        root.Add("gateCommands", array unitValue.GateCommands)
        root.Add("artifacts", JsonArray(artifactNodes))
        root.Add("generator", "FS.GG.Coordination.RoadmapWork/1")
        root.Add("createdAt", createdAt)
        root

    let createManifest indexBytes roadmapBytes receiptDocuments unitId (candidate: RoadmapCandidate) (createdAt: string) (artifacts: RoadmapArtifactInput list) =
        match validated indexBytes roadmapBytes unitId with
        | Error errors -> Error errors
        | Ok(index, unitValue) ->
            let validation =
                [ if not (isSha candidate.Commit 40) then yield finding "RW-CANDIDATE-COMMIT" "/candidateCommit" "expected exact 40-hex commit"
                  if not (isSha candidate.Tree 40) then yield finding "RW-CANDIDATE-TREE" "/candidateTree" "expected exact 40-hex tree"
                  match DateTimeOffset.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
                  | true, _ -> ()
                  | _ -> yield finding "RW-CREATED-AT" "/createdAt" "expected explicit ISO-8601 instant"
                  if List.isEmpty artifacts then yield finding "RW-ARTIFACTS" "/artifacts" "at least one artifact is required"
                  yield! artifacts |> List.collect validArtifact
                  if artifacts.Length <> (artifacts |> List.map _.Name |> List.distinct).Length then
                      yield finding "RW-ARTIFACT-DUPLICATE" "/artifacts" "artifact names must be unique" ]
            if not (List.isEmpty validation) then Error validation
            else
                match prerequisites index unitValue receiptDocuments with
                | Error errors -> Error errors
                | Ok receipts ->
                    let options = JsonSerializerOptions(WriteIndented = false)
                    let bytes = manifestNode (sha256 indexBytes) index unitValue receipts candidate createdAt artifacts
                                |> _.ToJsonString(options)
                                |> Encoding.UTF8.GetBytes
                    Ok bytes

    let validateManifest indexBytes roadmapBytes receiptDocuments unitId (candidate: RoadmapCandidate) (manifest: ReadOnlyMemory<byte>) =
        match validated indexBytes roadmapBytes unitId with
        | Error errors -> Error errors
        | Ok(index, unitValue) ->
            match prerequisites index unitValue receiptDocuments with
            | Error errors -> Error errors
            | Ok receipts ->
                try
                    use document = JsonDocument.Parse manifest
                    let root = document.RootElement
                    let allowed = Set.ofList [ "schema"; "state"; "unitId"; "indexSha256"; "roadmapRevision"; "roadmapSha256"; "candidateCommit"; "candidateTree"; "prerequisiteReceiptDigests"; "qGates"; "gateCommands"; "artifacts"; "generator"; "createdAt" ]
                    match strictObject "" allowed root with
                    | Error errors -> Error errors
                    | Ok _ ->
                        let expectedStrings =
                            [ "schema", ManifestSchema; "state", "candidate"; "unitId", unitId
                              "indexSha256", sha256 indexBytes
                              "roadmapRevision", index.Revision; "roadmapSha256", index.RoadmapSha
                              "candidateCommit", candidate.Commit; "candidateTree", candidate.Tree
                              "generator", "FS.GG.Coordination.RoadmapWork/1" ]
                        let mutable errors =
                            [ for name, expected in expectedStrings do
                                  match requiredString "" name root with
                                  | Ok actual when actual = expected -> ()
                                  | Ok actual -> yield finding "RW-MANIFEST-MISMATCH" $"/{name}" $"expected {expected}, observed {actual}"
                                  | Error values -> yield! values ]
                        let checkList name expected =
                            match stringList "" name true root with
                            | Ok actual when actual = expected -> ()
                            | Ok _ -> errors <- errors @ [ finding "RW-MANIFEST-MISMATCH" $"/{name}" "ordered values differ from the selected unit" ]
                            | Error values -> errors <- errors @ values
                        checkList "prerequisiteReceiptDigests" (receipts |> List.map _.Digest)
                        checkList "qGates" unitValue.QGates
                        checkList "gateCommands" unitValue.GateCommands
                        match requiredString "" "createdAt" root with
                        | Ok value -> match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with true, _ -> () | _ -> errors <- errors @ [ finding "RW-CREATED-AT" "/createdAt" "expected ISO-8601 instant" ]
                        | Error values -> errors <- errors @ values
                        match property "artifacts" root with
                        | Some value when value.ValueKind = JsonValueKind.Array && value.GetArrayLength() > 0 ->
                            for index, artifact in value.EnumerateArray() |> Seq.indexed do
                                match strictObject $"/artifacts/{index}" (Set.ofList [ "name"; "path"; "sha256" ]) artifact with
                                | Error values -> errors <- errors @ values
                                | Ok _ ->
                                    match requiredString $"/artifacts/{index}" "sha256" artifact with Ok value when isSha value 64 -> () | Ok _ -> errors <- errors @ [ finding "RW-SHA256" $"/artifacts/{index}/sha256" "expected SHA-256" ] | Error values -> errors <- errors @ values
                                    match requiredString $"/artifacts/{index}" "path" artifact with
                                    | Ok path when not (Path.IsPathRooted path) && not (path.Split('/') |> Array.contains "..") -> ()
                                    | Ok _ -> errors <- errors @ [ finding "RW-PATH" $"/artifacts/{index}/path" "path escapes repository" ]
                                    | Error values -> errors <- errors @ values
                        | _ -> errors <- errors @ [ finding "RW-ARTIFACTS" "/artifacts" "required non-empty artifact array" ]
                        if List.isEmpty errors then Ok unitValue.GateCommands else Error errors
                with :? JsonException as error -> Error [ finding "RW-MANIFEST-JSON" "/" error.Message ]

    let commandIds index roadmap unitId =
        inspect index roadmap unitId |> Result.map (fun value -> value.Unit.GateCommands)
