namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json

type PortableToolchain = { Id: string; Version: string }

type PortableEntryPoints =
    {
        Build: string
        Test: string
        Lint: string option
        Artifact: string option
    }

type PortableComponent =
    {
        Id: string
        Language: string
        WorkingDirectory: string
        Toolchain: PortableToolchain
        EntryPoints: PortableEntryPoints
    }

type PortableWorkspaceProfile =
    {
        ProfileId: string
        Revision: uint64
        WorkspaceScope: string
        SourceRevision: string
        QualifiedImage: string
        Components: PortableComponent list
        ProductBuild: string
        ProductTest: string
        ProductJourney: string
        MaximumRuntimeSeconds: uint64
        MaximumOutputBytes: uint64
    }

type PortableWorkspaceCommand =
    {
        CommandId: Guid
        IdempotencyId: string
        WorkspaceScope: string
        ProfileId: string
        ProfileRevision: uint64
        SourceRevision: string
        ExpectedWorkflowRevision: uint64
        FenceGeneration: uint64
        CausationId: string option
        Deadline: DateTimeOffset
        Operation: string
        ComponentId: string option
    }

type PortableEvidence<'value> =
    | EvidenceKnown of 'value
    | EvidenceMissing of reason: string
    | EvidenceUnknown of reason: string

type PortableWorkspaceError =
    {
        Code: string
        Message: string
        Retryable: bool
        Details: Map<string, string>
    }

type PortableWorkspaceResult =
    {
        CommandId: Guid
        WorkflowRevision: uint64
        FenceGeneration: uint64
        CompletedAt: DateTimeOffset
        ExitCode: PortableEvidence<int>
        ArtifactReference: PortableEvidence<string>
        Error: PortableWorkspaceError option
    }

[<RequireQualifiedAccess>]
module PortableWorkspaceContract =
    let profileSchema = "fsgg.workspace.toolchain-profile/1"
    let commandSchema = "fsgg.workspace.command/1"
    let resultSchema = "fsgg.workspace.result/1"
    let maximumDocumentBytes = 256 * 1024

    let private identifier (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 128
        && (value
            |> Seq.mapi (fun index character ->
                Char.IsAsciiLetterOrDigit character
                || (index > 0
                    && (character = '.'
                        || character = '_'
                        || character = ':'
                        || character = '/'
                        || character = '-')))
            |> Seq.forall id)

    let private text maximum (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value = value.Trim()
        && value.Length <= maximum

    let private sourceRevision value =
        text 128 value
        && value
           |> Seq.forall (fun character -> Char.IsAsciiLetterOrDigit character || character = '-' || character = '_')

    let private relativePath (value: string) =
        text 256 value
        && value <> "."
        && not (Path.IsPathRooted value)
        && not (
            value.Split('/')
            |> Array.exists (fun segment -> segment = "" || segment = "." || segment = "..")
        )
        && not (value.Contains '\\')

    let private entryPoint value = identifier value

    let private qualifiedImage (value: string) =
        if not (text 256 value) then
            false
        else
            let marker = "@sha256:"
            let index = value.LastIndexOf(marker, StringComparison.Ordinal)

            index > 0
            && index + marker.Length + 64 = value.Length
            && value[(index + marker.Length) ..]
               |> Seq.forall (fun character -> Char.IsAsciiHexDigit character && not (Char.IsUpper character))

    let private instantText (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)

    let private portableInstant (value: DateTimeOffset) =
        value.Offset = TimeSpan.Zero && value.Ticks % 10L = 0L

    let private parseInstant value =
        match
            DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            )
        with
        | true, parsed when instantText parsed = value -> Ok parsed
        | _ -> Error "portable-timestamp-refused"

    let private counterText (value: uint64) =
        value.ToString(CultureInfo.InvariantCulture)

    let private parseCounter value =
        if String.IsNullOrEmpty value || (value.Length > 1 && value[0] = '0') then
            Error "portable-counter-refused"
        else
            match UInt64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, parsed -> Ok parsed
            | _ -> Error "portable-counter-refused"

    let private guidText (value: Guid) = value.ToString("D").ToLowerInvariant()

    let private parseGuid value =
        match Guid.TryParseExact(value, "D") with
        | true, parsed when parsed <> Guid.Empty && guidText parsed = value -> Ok parsed
        | _ -> Error "portable-command-id-refused"

    let private validProfile (profile: PortableWorkspaceProfile) =
        identifier profile.ProfileId
        && identifier profile.WorkspaceScope
        && sourceRevision profile.SourceRevision
        && qualifiedImage profile.QualifiedImage
        && profile.MaximumRuntimeSeconds > 0UL
        && profile.MaximumOutputBytes > 0UL
        && profile.Components.Length > 0
        && profile.Components.Length <= 32
        && (profile.Components |> List.map _.Id |> Set.ofList |> Set.count = profile.Components.Length)
        && profile.Components
           |> List.forall (fun part ->
               identifier part.Id
               && identifier part.Language
               && relativePath part.WorkingDirectory
               && identifier part.Toolchain.Id
               && text 128 part.Toolchain.Version
               && not (String.Equals(part.Toolchain.Version, "latest", StringComparison.OrdinalIgnoreCase))
               && part.Toolchain.Version <> "*"
               && entryPoint part.EntryPoints.Build
               && entryPoint part.EntryPoints.Test
               && (part.EntryPoints.Lint |> Option.forall entryPoint)
               && (part.EntryPoints.Artifact |> Option.forall entryPoint))
        && entryPoint profile.ProductBuild
        && entryPoint profile.ProductTest
        && entryPoint profile.ProductJourney

    let validateProfile profile =
        if validProfile profile then
            Ok profile
        else
            Error "portable-profile-refused"

    let private writeOptional (writer: Utf8JsonWriter) (name: string) (value: string option) =
        value |> Option.iter (fun item -> writer.WriteString(name, item))

    let profileBytes profile =
        validateProfile profile
        |> Result.map (fun value ->
            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream)
            writer.WriteStartObject()
            writer.WriteString("schema", profileSchema)
            writer.WriteString("profileId", value.ProfileId)
            writer.WriteString("revision", counterText value.Revision)
            writer.WriteString("workspaceScope", value.WorkspaceScope)
            writer.WriteString("sourceRevision", value.SourceRevision)
            writer.WriteString("qualifiedImage", value.QualifiedImage)
            writer.WritePropertyName("components")
            writer.WriteStartArray()

            for part in value.Components do
                writer.WriteStartObject()
                writer.WriteString("id", part.Id)
                writer.WriteString("language", part.Language)
                writer.WriteString("workingDirectory", part.WorkingDirectory)
                writer.WritePropertyName("toolchain")
                writer.WriteStartObject()
                writer.WriteString("id", part.Toolchain.Id)
                writer.WriteString("version", part.Toolchain.Version)
                writer.WriteEndObject()
                writer.WritePropertyName("entryPoints")
                writer.WriteStartObject()
                writer.WriteString("build", part.EntryPoints.Build)
                writer.WriteString("test", part.EntryPoints.Test)
                writeOptional writer "lint" part.EntryPoints.Lint
                writeOptional writer "artifact" part.EntryPoints.Artifact
                writer.WriteEndObject()
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WritePropertyName("product")
            writer.WriteStartObject()
            writer.WriteString("build", value.ProductBuild)
            writer.WriteString("test", value.ProductTest)
            writer.WriteString("journey", value.ProductJourney)
            writer.WriteEndObject()
            writer.WritePropertyName("limits")
            writer.WriteStartObject()
            writer.WriteString("maximumRuntimeSeconds", counterText value.MaximumRuntimeSeconds)
            writer.WriteString("maximumOutputBytes", counterText value.MaximumOutputBytes)
            writer.WriteEndObject()
            writer.WriteEndObject()
            writer.Flush()
            stream.ToArray())

    let private propertyNames (element: JsonElement) =
        element.EnumerateObject() |> Seq.map _.Name |> Seq.toList

    let private closed expected (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            false
        else
            let names = propertyNames element
            names.Length = (Set.ofList names).Count && Set.ofList names = expected

    let private closedOptional required optional (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            false
        else
            let names = propertyNames element
            let actual = Set.ofList names

            names.Length = actual.Count
            && Set.isSubset required actual
            && Set.isSubset actual (Set.union required optional)

    let private optionalString (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok None
        | true, property when property.ValueKind = JsonValueKind.String -> Ok(Some(property.GetString()))
        | _ -> Error "portable-null-refused"

    let private document (bytes: byte array) =
        if isNull bytes || bytes.Length = 0 || bytes.Length > maximumDocumentBytes then
            Error "portable-size-refused"
        else
            try
                Ok(JsonDocument.Parse(ReadOnlyMemory bytes, JsonDocumentOptions(MaxDepth = 12)))
            with :? JsonException ->
                Error "portable-json-refused"

    let parseProfile (bytes: byte array) =
        document bytes
        |> Result.bind (fun owner ->
            use document = owner
            let root = document.RootElement

            let rootFields =
                set
                    [
                        "schema"
                        "profileId"
                        "revision"
                        "workspaceScope"
                        "sourceRevision"
                        "qualifiedImage"
                        "components"
                        "product"
                        "limits"
                    ]

            if not (closed rootFields root) then
                Error "portable-profile-shape-refused"
            else
                try
                    let schema = root.GetProperty("schema").GetString()
                    let componentsElement = root.GetProperty("components")
                    let product = root.GetProperty("product")
                    let limits = root.GetProperty("limits")

                    if
                        schema <> profileSchema
                        || componentsElement.ValueKind <> JsonValueKind.Array
                        || not (closed (set [ "build"; "test"; "journey" ]) product)
                        || not (closed (set [ "maximumRuntimeSeconds"; "maximumOutputBytes" ]) limits)
                    then
                        Error "portable-profile-shape-refused"
                    else
                        let parseComponent (part: JsonElement) =
                            let required =
                                set [ "id"; "language"; "workingDirectory"; "toolchain"; "entryPoints" ]

                            if not (closed required part) then
                                Error "portable-component-shape-refused"
                            else
                                let toolchain = part.GetProperty("toolchain")
                                let entries = part.GetProperty("entryPoints")

                                if
                                    not (closed (set [ "id"; "version" ]) toolchain)
                                    || not (
                                        closedOptional (set [ "build"; "test" ]) (set [ "lint"; "artifact" ]) entries
                                    )
                                then
                                    Error "portable-component-shape-refused"
                                else
                                    match optionalString "lint" entries, optionalString "artifact" entries with
                                    | Ok lint, Ok artifact ->
                                        Ok
                                            {
                                                Id = part.GetProperty("id").GetString()
                                                Language = part.GetProperty("language").GetString()
                                                WorkingDirectory = part.GetProperty("workingDirectory").GetString()
                                                Toolchain =
                                                    {
                                                        Id = toolchain.GetProperty("id").GetString()
                                                        Version = toolchain.GetProperty("version").GetString()
                                                    }
                                                EntryPoints =
                                                    {
                                                        Build = entries.GetProperty("build").GetString()
                                                        Test = entries.GetProperty("test").GetString()
                                                        Lint = lint
                                                        Artifact = artifact
                                                    }
                                            }
                                    | Error reason, _
                                    | _, Error reason -> Error reason

                        let components =
                            componentsElement.EnumerateArray()
                            |> Seq.fold
                                (fun state item ->
                                    state
                                    |> Result.bind (fun found ->
                                        parseComponent item |> Result.map (fun value -> value :: found)))
                                (Ok [])
                            |> Result.map List.rev

                        match
                            components,
                            parseCounter (limits.GetProperty("maximumRuntimeSeconds").GetString()),
                            parseCounter (limits.GetProperty("maximumOutputBytes").GetString()),
                            parseCounter (root.GetProperty("revision").GetString())
                        with
                        | Ok parts, Ok runtime, Ok output, Ok revision ->
                            {
                                ProfileId = root.GetProperty("profileId").GetString()
                                Revision = revision
                                WorkspaceScope = root.GetProperty("workspaceScope").GetString()
                                SourceRevision = root.GetProperty("sourceRevision").GetString()
                                QualifiedImage = root.GetProperty("qualifiedImage").GetString()
                                Components = parts
                                ProductBuild = product.GetProperty("build").GetString()
                                ProductTest = product.GetProperty("test").GetString()
                                ProductJourney = product.GetProperty("journey").GetString()
                                MaximumRuntimeSeconds = runtime
                                MaximumOutputBytes = output
                            }
                            |> validateProfile
                            |> Result.bind (fun value ->
                                profileBytes value
                                |> Result.bind (fun canonical ->
                                    if canonical.AsSpan().SequenceEqual(bytes.AsSpan()) then
                                        Ok value
                                    else
                                        Error "portable-profile-noncanonical"))
                        | Error reason, _, _, _
                        | _, Error reason, _, _
                        | _, _, Error reason, _
                        | _, _, _, Error reason -> Error reason
                with :? InvalidOperationException ->
                    Error "portable-profile-shape-refused")

    let validateCommand (command: PortableWorkspaceCommand) =
        if
            command.CommandId <> Guid.Empty
            && identifier command.IdempotencyId
            && identifier command.WorkspaceScope
            && identifier command.ProfileId
            && sourceRevision command.SourceRevision
            && entryPoint command.Operation
            && (command.CausationId |> Option.forall identifier)
            && (command.ComponentId |> Option.forall identifier)
            && portableInstant command.Deadline
        then
            Ok command
        else
            Error "portable-command-refused"

    let commandBytes (command: PortableWorkspaceCommand) =
        validateCommand command
        |> Result.map (fun command ->
            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream)
            writer.WriteStartObject()
            writer.WriteString("schema", commandSchema)
            writer.WriteString("commandId", guidText command.CommandId)
            writer.WriteString("idempotencyId", command.IdempotencyId)
            writer.WriteString("workspaceScope", command.WorkspaceScope)
            writer.WriteString("profileId", command.ProfileId)
            writer.WriteString("profileRevision", counterText command.ProfileRevision)
            writer.WriteString("sourceRevision", command.SourceRevision)
            writer.WriteString("expectedWorkflowRevision", counterText command.ExpectedWorkflowRevision)
            writer.WriteString("fenceGeneration", counterText command.FenceGeneration)
            writeOptional writer "causationId" command.CausationId
            writer.WriteString("deadline", instantText command.Deadline)
            writer.WriteString("operation", command.Operation)
            writeOptional writer "componentId" command.ComponentId
            writer.WriteEndObject()
            writer.Flush()
            stream.ToArray())

    let parseCommand (bytes: byte array) =
        document bytes
        |> Result.bind (fun owner ->
            use document = owner
            let root = document.RootElement

            try
                let required =
                    set
                        [
                            "schema"
                            "commandId"
                            "idempotencyId"
                            "workspaceScope"
                            "profileId"
                            "profileRevision"
                            "sourceRevision"
                            "expectedWorkflowRevision"
                            "fenceGeneration"
                            "deadline"
                            "operation"
                        ]

                if
                    not (closedOptional required (set [ "causationId"; "componentId" ]) root)
                    || root.GetProperty("schema").GetString() <> commandSchema
                then
                    Error "portable-command-shape-refused"
                else
                    match
                        parseGuid (root.GetProperty("commandId").GetString()),
                        parseCounter (root.GetProperty("profileRevision").GetString()),
                        parseCounter (root.GetProperty("expectedWorkflowRevision").GetString()),
                        parseCounter (root.GetProperty("fenceGeneration").GetString()),
                        parseInstant (root.GetProperty("deadline").GetString()),
                        optionalString "causationId" root,
                        optionalString "componentId" root
                    with
                    | Ok commandId,
                      Ok profileRevision,
                      Ok workflowRevision,
                      Ok generation,
                      Ok deadline,
                      Ok causation,
                      Ok componentId ->
                        let value =
                            {
                                CommandId = commandId
                                IdempotencyId = root.GetProperty("idempotencyId").GetString()
                                WorkspaceScope = root.GetProperty("workspaceScope").GetString()
                                ProfileId = root.GetProperty("profileId").GetString()
                                ProfileRevision = profileRevision
                                SourceRevision = root.GetProperty("sourceRevision").GetString()
                                ExpectedWorkflowRevision = workflowRevision
                                FenceGeneration = generation
                                CausationId = causation
                                Deadline = deadline
                                Operation = root.GetProperty("operation").GetString()
                                ComponentId = componentId
                            }

                        commandBytes value
                        |> Result.bind (fun canonical ->
                            if canonical.AsSpan().SequenceEqual(bytes.AsSpan()) then
                                Ok value
                            else
                                Error "portable-command-noncanonical")
                    | Error reason, _, _, _, _, _, _
                    | _, Error reason, _, _, _, _, _
                    | _, _, Error reason, _, _, _, _
                    | _, _, _, Error reason, _, _, _
                    | _, _, _, _, Error reason, _, _
                    | _, _, _, _, _, Error reason, _
                    | _, _, _, _, _, _, Error reason -> Error reason
            with :? InvalidOperationException ->
                Error "portable-command-shape-refused")

    let private writeEvidence<'value>
        (writer: Utf8JsonWriter)
        (name: string)
        (writeValue: Utf8JsonWriter -> 'value -> unit)
        (evidence: PortableEvidence<'value>)
        =
        writer.WritePropertyName name
        writer.WriteStartObject()

        match evidence with
        | EvidenceKnown value ->
            writer.WriteString("state", "known")
            writeValue writer value
        | EvidenceMissing reason ->
            writer.WriteString("state", "missing")
            writer.WriteString("reason", reason)
        | EvidenceUnknown reason ->
            writer.WriteString("state", "unknown")
            writer.WriteString("reason", reason)

        writer.WriteEndObject()

    let validateResult (result: PortableWorkspaceResult) =
        let validReason value = text 256 value

        let evidenceValid =
            function
            | EvidenceKnown _ -> true
            | EvidenceMissing reason
            | EvidenceUnknown reason -> validReason reason

        let errorValid =
            result.Error
            |> Option.forall (fun error ->
                identifier error.Code
                && text 512 error.Message
                && error.Details.Count <= 32
                && error.Details |> Map.forall (fun key value -> identifier key && text 512 value))

        let artifactValid =
            match result.ArtifactReference with
            | EvidenceKnown value -> text 2048 value
            | _ -> true

        if
            result.CommandId <> Guid.Empty
            && portableInstant result.CompletedAt
            && evidenceValid result.ExitCode
            && evidenceValid result.ArtifactReference
            && artifactValid
            && errorValid
        then
            Ok result
        else
            Error "portable-result-refused"

    let resultBytes (result: PortableWorkspaceResult) =
        validateResult result
        |> Result.map (fun result ->
            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream)
            writer.WriteStartObject()
            writer.WriteString("schema", resultSchema)
            writer.WriteString("commandId", guidText result.CommandId)
            writer.WriteString("workflowRevision", counterText result.WorkflowRevision)
            writer.WriteString("fenceGeneration", counterText result.FenceGeneration)
            writer.WriteString("completedAt", instantText result.CompletedAt)

            writeEvidence
                writer
                "exitCode"
                (fun output (value: int) -> output.WriteNumber("value", value))
                result.ExitCode

            writeEvidence
                writer
                "artifactReference"
                (fun output (value: string) -> output.WriteString("value", value))
                result.ArtifactReference

            result.Error
            |> Option.iter (fun error ->
                writer.WritePropertyName("error")
                writer.WriteStartObject()
                writer.WriteString("code", error.Code)
                writer.WriteString("message", error.Message)
                writer.WriteBoolean("retryable", error.Retryable)
                writer.WritePropertyName("details")
                writer.WriteStartObject()

                for KeyValue(key, value) in error.Details do
                    writer.WriteString(key, value)

                writer.WriteEndObject()
                writer.WriteEndObject())

            writer.WriteEndObject()
            writer.Flush()
            stream.ToArray())

    let parseResult (bytes: byte array) =
        document bytes
        |> Result.bind (fun owner ->
            use document = owner
            let root = document.RootElement

            try
                let required =
                    set
                        [
                            "schema"
                            "commandId"
                            "workflowRevision"
                            "fenceGeneration"
                            "completedAt"
                            "exitCode"
                            "artifactReference"
                        ]

                let parseEvidence (name: string) (knownValue: JsonElement -> Result<'value, string>) =
                    let item = root.GetProperty name

                    if item.ValueKind <> JsonValueKind.Object then
                        Error "portable-evidence-shape-refused"
                    else
                        let state = item.GetProperty("state").GetString()

                        match state with
                        | "known" when closed (set [ "state"; "value" ]) item ->
                            knownValue (item.GetProperty("value")) |> Result.map EvidenceKnown
                        | "missing" when closed (set [ "state"; "reason" ]) item ->
                            Ok(EvidenceMissing(item.GetProperty("reason").GetString()))
                        | "unknown" when closed (set [ "state"; "reason" ]) item ->
                            Ok(EvidenceUnknown(item.GetProperty("reason").GetString()))
                        | _ -> Error "portable-evidence-shape-refused"

                if
                    not (closedOptional required (set [ "error" ]) root)
                    || root.GetProperty("schema").GetString() <> resultSchema
                then
                    Error "portable-result-shape-refused"
                else
                    let error =
                        match root.TryGetProperty "error" with
                        | false, _ -> Ok None
                        | true, item when
                            closed (set [ "code"; "message"; "retryable"; "details" ]) item
                            && item.GetProperty("details").ValueKind = JsonValueKind.Object
                            ->
                            let detailsElement = item.GetProperty("details")

                            let details =
                                detailsElement.EnumerateObject()
                                |> Seq.fold
                                    (fun state property ->
                                        state
                                        |> Result.bind (fun found ->
                                            if
                                                property.Value.ValueKind = JsonValueKind.String
                                                && not (Map.containsKey property.Name found)
                                            then
                                                Ok(Map.add property.Name (property.Value.GetString()) found)
                                            else
                                                Error "portable-error-details-refused"))
                                    (Ok Map.empty)

                            details
                            |> Result.map (fun values ->
                                Some
                                    {
                                        Code = item.GetProperty("code").GetString()
                                        Message = item.GetProperty("message").GetString()
                                        Retryable = item.GetProperty("retryable").GetBoolean()
                                        Details = values
                                    })
                        | _ -> Error "portable-error-shape-refused"

                    match
                        parseGuid (root.GetProperty("commandId").GetString()),
                        parseCounter (root.GetProperty("workflowRevision").GetString()),
                        parseCounter (root.GetProperty("fenceGeneration").GetString()),
                        parseInstant (root.GetProperty("completedAt").GetString()),
                        parseEvidence "exitCode" (fun item ->
                            if item.ValueKind = JsonValueKind.Number then
                                match item.TryGetInt32() with
                                | true, value -> Ok value
                                | _ -> Error "portable-exit-code-refused"
                            else
                                Error "portable-exit-code-refused"),
                        parseEvidence "artifactReference" (fun item ->
                            if item.ValueKind = JsonValueKind.String then
                                Ok(item.GetString())
                            else
                                Error "portable-artifact-reference-refused"),
                        error
                    with
                    | Ok commandId, Ok revision, Ok generation, Ok completedAt, Ok exitCode, Ok artifact, Ok errorValue ->
                        let value =
                            {
                                CommandId = commandId
                                WorkflowRevision = revision
                                FenceGeneration = generation
                                CompletedAt = completedAt
                                ExitCode = exitCode
                                ArtifactReference = artifact
                                Error = errorValue
                            }

                        resultBytes value
                        |> Result.bind (fun canonical ->
                            if canonical.AsSpan().SequenceEqual(bytes.AsSpan()) then
                                Ok value
                            else
                                Error "portable-result-noncanonical")
                    | Error reason, _, _, _, _, _, _
                    | _, Error reason, _, _, _, _, _
                    | _, _, Error reason, _, _, _, _
                    | _, _, _, Error reason, _, _, _
                    | _, _, _, _, Error reason, _, _
                    | _, _, _, _, _, Error reason, _
                    | _, _, _, _, _, _, Error reason -> Error reason
            with :? InvalidOperationException ->
                Error "portable-result-shape-refused")

    let digest (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
