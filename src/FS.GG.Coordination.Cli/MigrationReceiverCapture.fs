namespace FS.GG.Coordination.Cli

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type MigrationReceiverTwoPass =
    { CohortSha256: string
      First: MigrationReceiverSnapshot list
      Second: MigrationReceiverSnapshot list }

type MigrationReceiverSignedHeadIdentity =
    { ReceiverName: string
      RepositoryFullName: string
      RefName: string
      CommitSha: string
      EvidenceRequestUri: string
      VerificationReason: string
      SignatureSha256: string
      SignedPayloadSha256: string
      VerifiedAtUtc: DateTimeOffset }

type MigrationReceiverWorkflowToolIdentity =
    { ReceiverName: string
      WorkflowPath: string
      LineNumber: int
      EvidenceRequestUri: string
      Literal: string
      TargetRepository: string
      TargetPath: string option
      Revision: string
      Kind: ImmutableExecutionReferenceKind }

type MigrationReceiverPinEvidenceOrigin =
    private
    | CallerDeclared
    | ProviderTreeSignedTools
    static member internal CreateCallerDeclared() = CallerDeclared
    static member internal CreateProviderTreeSignedTools() = ProviderTreeSignedTools

type MigrationReceiverPinTwoPass =
    { CohortSha256: string
      EvidenceOrigin: MigrationReceiverPinEvidenceOrigin
      SignedHeads: MigrationReceiverSignedHeadIdentity list
      WorkflowTools: MigrationReceiverWorkflowToolIdentity list
      First: MigrationReceiverPinSnapshot list
      Second: MigrationReceiverPinSnapshot list }
    member this.InventoryBound = this.EvidenceOrigin.IsProviderTreeSignedTools
    member this.SignedToolIdentitiesBound = this.EvidenceOrigin.IsProviderTreeSignedTools

[<RequireQualifiedAccess>]
module MigrationReceiverCapture =
    let private sha256Bytes (bytes: byte array) =
        bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private sha256Text (value: string) = Encoding.UTF8.GetBytes value |> sha256Bytes

    let private uniqueMembers (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then false
        else
            let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            names.Length = (names |> Set.ofList |> Set.count)

    let private requiredString (name: string) (value: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        if value.TryGetProperty(name, &property) && property.ValueKind = JsonValueKind.String then
            let text = property.GetString()
            if String.IsNullOrWhiteSpace text then None else Some text
        else None

    let private signedHead (snapshot: MigrationReceiverSnapshot) =
        try
            use document = JsonDocument.Parse snapshot.CommitEvidence.RawBody
            let root = document.RootElement
            let mutable verification = Unchecked.defaultof<JsonElement>
            if not (uniqueMembers root)
               || not (root.TryGetProperty("verification", &verification))
               || not (uniqueMembers verification) then
                Error "missing:receiver-signed-head"
            else
                let mutable verified = Unchecked.defaultof<JsonElement>
                match requiredString "sha" root, requiredString "reason" verification,
                      requiredString "signature" verification, requiredString "payload" verification,
                      requiredString "verified_at" verification with
                | Some sha, Some "valid", Some signature, Some payload, Some verifiedAt when
                    verification.TryGetProperty("verified", &verified)
                    && verified.ValueKind = JsonValueKind.True
                    && sha = snapshot.CommitSha ->
                    match DateTimeOffset.TryParse(verifiedAt, CultureInfo.InvariantCulture,
                                                  DateTimeStyles.RoundtripKind) with
                    | true, instant when instant.Offset = TimeSpan.Zero ->
                        Ok { ReceiverName=snapshot.ReceiverName
                             RepositoryFullName=snapshot.RepositoryFullName
                             RefName=snapshot.RefName; CommitSha=snapshot.CommitSha
                             EvidenceRequestUri=snapshot.CommitEvidence.RequestUri
                             VerificationReason="valid"
                             SignatureSha256=sha256Text signature
                             SignedPayloadSha256=sha256Text payload
                             VerifiedAtUtc=instant }
                    | _ -> Error "invalid:receiver-signed-head-time"
                | _ -> Error "invalid:receiver-signed-head"
        with :? JsonException -> Error "invalid:receiver-signed-head-json"

    let private usesLine =
        Regex("^(?:-\\s*)?uses\\s*:\\s*(.*?)\\s*$", RegexOptions.CultureInvariant)

    let private suspiciousUsesLine =
        Regex("(?:^|[\\s{,\\-])(?:uses|['\"]uses['\"])\\s*:", RegexOptions.CultureInvariant)

    let private unquote (value: string) =
        if value.Length >= 2 && value.[0] = '\'' && value.[value.Length - 1] = '\'' then
            Some(value.Substring(1, value.Length - 2).Replace("''", "'"))
        elif value.Length >= 2 && value.[0] = '"' && value.[value.Length - 1] = '"' then
            let inner = value.Substring(1, value.Length - 2)
            if inner.Contains('\\') then None else Some inner
        elif value.StartsWith("'", StringComparison.Ordinal)
             || value.StartsWith("\"", StringComparison.Ordinal)
             || value.EndsWith("'", StringComparison.Ordinal)
             || value.EndsWith("\"", StringComparison.Ordinal) then None
        else Some value

    let private workflowTools (snapshot: MigrationReceiverPinSnapshot) =
        let utf8 = UTF8Encoding(false, true)
        let parsePin (pin: MigrationReceiverPinBlob) =
            if pin.PinKind <> "workflow" then Ok []
            else
                try
                    let text = utf8.GetString pin.Bytes
                    let lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                    lines
                    |> Array.indexed
                    |> Array.fold (fun state (lineNumber, line) ->
                        state |> Result.bind (fun previous ->
                            let candidate = line.Trim()
                            if candidate = "" || candidate.StartsWith("#", StringComparison.Ordinal) then Ok previous
                            else
                                let matched = usesLine.Match candidate
                                if not matched.Success then
                                    if suspiciousUsesLine.IsMatch candidate then
                                        Error $"ambiguous:workflow-tool-identity:{pin.EntryPath}:{lineNumber + 1}"
                                    else Ok previous
                                else
                                    let raw = matched.Groups[1].Value.Trim()
                                    let withoutComment =
                                        if raw.StartsWith("'", StringComparison.Ordinal)
                                           || raw.StartsWith("\"", StringComparison.Ordinal) then raw
                                        else raw.Split('#').[0].TrimEnd()
                                    match unquote withoutComment with
                                    | None -> Error $"ambiguous:workflow-tool-identity:{pin.EntryPath}:{lineNumber + 1}"
                                    | Some literal ->
                                        match GitHubImmutableExecutionPinsQualification.classifyReferenceLiteral literal with
                                        | Error _ -> Error $"mutable-or-invalid:workflow-tool-identity:{pin.EntryPath}:{lineNumber + 1}"
                                        | Ok(kind, repository, path, revision) ->
                                            Ok ({ ReceiverName=snapshot.Receiver.ReceiverName
                                                  WorkflowPath=pin.EntryPath
                                                  LineNumber=lineNumber + 1
                                                  EvidenceRequestUri=pin.RequestUri
                                                  Literal=literal; TargetRepository=repository
                                                  TargetPath=path; Revision=revision; Kind=kind } :: previous))) (Ok [])
                    |> Result.bind (fun identities ->
                        let identities = identities |> List.rev
                        let keys = identities |> List.map (fun item -> item.WorkflowPath, item.LineNumber)
                        if keys.Length <> (keys |> Set.ofList |> Set.count) then
                            Error $"duplicate:workflow-tool-identity:{pin.EntryPath}"
                        else Ok identities)
                with :? DecoderFallbackException -> Error $"invalid:workflow-utf8:{pin.EntryPath}"
        snapshot.Pins
        |> List.fold (fun state pin ->
            state |> Result.bind (fun previous ->
                parsePin pin |> Result.map (fun current -> previous @ current))) (Ok [])

    let private deriveSignedToolIdentities (snapshots: MigrationReceiverPinSnapshot list) =
        let signed =
            snapshots
            |> List.fold (fun state snapshot ->
                state |> Result.bind (fun previous ->
                    signedHead snapshot.Receiver |> Result.map (fun value -> value :: previous))) (Ok [])
            |> Result.map (List.sortBy (fun value -> value.ReceiverName))
        let tools =
            snapshots
            |> List.fold (fun state snapshot ->
                state |> Result.bind (fun previous ->
                    workflowTools snapshot |> Result.map (fun values -> previous @ values))) (Ok [])
            |> Result.map (List.sortBy (fun value -> value.ReceiverName, value.WorkflowPath, value.LineNumber))
        match signed, tools with
        | Ok signedHeads, Ok workflowToolIdentities -> Ok(signedHeads, workflowToolIdentities)
        | Error reason, _ | _, Error reason -> Error reason

    let private pinDeclaration (entry: MigrationReceiverTreeEntry) =
        let workflow = entry.EntryPath.StartsWith(".github/workflows/", StringComparison.Ordinal)
        let packageNames =
            set [ "global.json"; "Directory.Packages.props"; "packages.lock.json"
                  "package.json"; "package-lock.json"; "pnpm-lock.yaml"
                  "yarn.lock"; "nuget.config" ]
        let package = entry.EntryPath.Split('/') |> Array.last |> packageNames.Contains
        if workflow then Some { EntryPath=entry.EntryPath; PinKind="workflow" }
        elif package then Some { EntryPath=entry.EntryPath; PinKind="package" }
        else None

    let captureTwoPass (cohort: GitHubMigrationCopyCohort) (template: MigrationGitHubReadOptions)
                       (transport: IMigrationGitHubReadTransport) =
        let repositories = cohort.Repositories |> List.map (fun (item: GitHubMigrationCopyRepository) -> item.Id, item) |> Map.ofList
        let receivers: GitHubMigrationCopyReceiver list = cohort.Receivers |> List.sortBy _.Receiver
        if not (GitHubMigrationInspect.validCohort cohort) then
            Error "invalid:receiver-cohort"
        else
            let readOne (receiver: GitHubMigrationCopyReceiver) =
                match Map.tryFind receiver.RepositoryId repositories with
                | None -> Error "missing:receiver-repository"
                | Some repository ->
                    match repository.FullName.Split('/') with
                    | [| owner; name |] when not (String.IsNullOrWhiteSpace owner)
                                             && not (String.IsNullOrWhiteSpace name) ->
                        let options =
                            { template with Owner=owner; Repository=name; ExpectedRepositoryId=repository.Id }
                        MigrationGitHubRead.readReceiverSnapshot options receiver.Receiver repository.NodeId
                            receiver.RefName receiver.ExpectedHead transport
                        |> Result.mapError (fun failure -> $"receiver:{receiver.Receiver}:{failure}")
                    | _ -> Error "invalid:receiver-repository-name"
            let readPass () =
                receivers
                |> List.fold (fun result receiver ->
                    result
                    |> Result.bind (fun previous ->
                        readOne receiver |> Result.map (fun snapshot -> snapshot :: previous))) (Ok [])
                |> Result.map List.rev
            readPass ()
            |> Result.bind (fun first ->
                readPass ()
                |> Result.bind (fun second ->
                    if List.map _.SnapshotSha256 first <> List.map _.SnapshotSha256 second then
                        Error "changed:receiver-snapshot"
                    else
                        Ok { CohortSha256=GitHubMigrationInspect.cohortSha256 cohort
                             First=first; Second=second }))

    let capturePinBytesTwoPass (cohort: GitHubMigrationCopyCohort)
                               (pinsByReceiver: Map<string, MigrationReceiverPinDeclaration list>)
                               (template: MigrationGitHubReadOptions)
                               (transport: IMigrationGitHubReadTransport) =
        let receivers = cohort.Receivers |> List.sortBy _.Receiver
        let names = receivers |> List.map _.Receiver |> Set.ofList
        if not (GitHubMigrationInspect.validCohort cohort)
           || (pinsByReceiver |> Map.toSeq |> Seq.map fst |> Set.ofSeq) <> names
           || pinsByReceiver |> Map.exists (fun _ pins -> isNull (box pins) || pins.IsEmpty) then
            Error "invalid:receiver-pin-declaration"
        else
            let repositories = cohort.Repositories |> List.map (fun item -> item.Id, item) |> Map.ofList
            let readOne (receiver: GitHubMigrationCopyReceiver) =
                match Map.tryFind receiver.RepositoryId repositories with
                | None -> Error "missing:receiver-repository"
                | Some repository ->
                    match repository.FullName.Split('/') with
                    | [| owner; name |] when not (String.IsNullOrWhiteSpace owner)
                                             && not (String.IsNullOrWhiteSpace name) ->
                        let options =
                            { template with Owner=owner; Repository=name; ExpectedRepositoryId=repository.Id }
                        MigrationGitHubRead.readReceiverPinSnapshot options receiver.Receiver repository.NodeId
                            receiver.RefName receiver.ExpectedHead pinsByReceiver.[receiver.Receiver] transport
                        |> Result.mapError (fun failure -> $"receiver-pin:{receiver.Receiver}:{failure}")
                    | _ -> Error "invalid:receiver-repository-name"
            let readPass () =
                receivers
                |> List.fold (fun result receiver ->
                    result |> Result.bind (fun previous ->
                        readOne receiver |> Result.map (fun snapshot -> snapshot :: previous))) (Ok [])
                |> Result.map List.rev
            readPass ()
            |> Result.bind (fun first ->
                readPass ()
                |> Result.bind (fun second ->
                    if List.map _.PinSnapshotSha256 first <> List.map _.PinSnapshotSha256 second then
                        Error "changed:receiver-pin-snapshot"
                    else
                        Ok
                            { CohortSha256=GitHubMigrationInspect.cohortSha256 cohort
                              EvidenceOrigin=MigrationReceiverPinEvidenceOrigin.CreateCallerDeclared()
                              SignedHeads=[]; WorkflowTools=[]; First=first; Second=second }))

    let captureWorkflowPinsTwoPass (cohort: GitHubMigrationCopyCohort)
                                   (template: MigrationGitHubReadOptions)
                                   (transport: IMigrationGitHubReadTransport) =
        let inventory (snapshots: MigrationReceiverSnapshot list) =
            snapshots
            |> List.map (fun snapshot ->
                snapshot.ReceiverName,
                (snapshot.TreeEntries
                 |> List.choose pinDeclaration
                 |> List.sortBy _.EntryPath))
            |> Map.ofList
        captureTwoPass cohort template transport
        |> Result.bind (fun census ->
            let firstInventory = inventory census.First
            let secondInventory = inventory census.Second
            if firstInventory <> secondInventory then
                Error "changed:receiver-pin-inventory"
            elif firstInventory |> Map.exists (fun _ pins -> pins.IsEmpty) then
                Error "missing:receiver-pin-inventory"
            else
                capturePinBytesTwoPass cohort firstInventory template transport
                |> Result.bind (fun captured ->
                    let firstReceivers = captured.First |> List.map _.Receiver
                    let secondReceivers = captured.Second |> List.map _.Receiver
                    if firstReceivers <> census.First || secondReceivers <> census.Second then
                        Error "changed:receiver-pin-provider-tree"
                    else
                        match deriveSignedToolIdentities captured.First,
                              deriveSignedToolIdentities captured.Second with
                        | Ok(firstSigned, firstTools), Ok(secondSigned, secondTools) when
                            firstSigned = secondSigned && firstTools = secondTools ->
                            Ok
                                { captured with EvidenceOrigin=MigrationReceiverPinEvidenceOrigin.CreateProviderTreeSignedTools()
                                                SignedHeads=firstSigned; WorkflowTools=firstTools }
                        | Ok _, Ok _ -> Error "changed:receiver-signed-tool-identities"
                        | Error reason, _ | _, Error reason -> Error reason))

    let validateSignedToolIdentityEvidence (captured: MigrationReceiverPinTwoPass) =
        if not captured.SignedToolIdentitiesBound then Error "workflow-pins-signed-tools-unbound"
        else
            match deriveSignedToolIdentities captured.First,
                  deriveSignedToolIdentities captured.Second with
            | Ok(firstSigned, firstTools), Ok(secondSigned, secondTools) when
                firstSigned = secondSigned && firstTools = secondTools
                && captured.SignedHeads = firstSigned && captured.WorkflowTools = firstTools -> Ok()
            | Ok _, Ok _ -> Error "workflow-pins-signed-tools-mismatch"
            | Error reason, _ | _, Error reason -> Error reason
