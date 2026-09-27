namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.GitHub

type MigrationClaimJournalNamespace =
    | ClaimJournalNamespace
    | OperationJournalNamespace

type MigrationClaimJournalSchemaFamily =
    | ClaimSchemaFamily
    | AdmissionSchemaFamily
    | OrdinarySchemaFamily
    | ReviewSchemaFamily

type MigrationClaimNativeSubjectKind =
    | NativeIssue
    | NativePullRequest

type MigrationClaimNativeStreamKind =
    | NativeIssueComments
    | NativeIssueEvents
    | NativeIssueTimeline

type MigrationClaimJournalRef =
    { ClaimRefName: string
      ClaimHeadSha: string }

type MigrationClaimJournalDecodedRecord =
    { Namespace: MigrationClaimJournalNamespace
      Schema: string
      Family: MigrationClaimJournalSchemaFamily
      CanonicalId: string
      OperationId: string option
      Generation: int64 }

type MigrationClaimJournalHistoryEntry =
    { ClaimCommitSha: string
      ClaimParentSha: string option
      ClaimTreeSha: string
      ClaimReads: MigrationReviewDeliveryRead list
      ClaimRecord: MigrationClaimJournalDecodedRecord }

type MigrationClaimJournalHistory =
    { ClaimHistoryRefName: string
      ClaimHistoryHeadSha: string
      ClaimEntries: MigrationClaimJournalHistoryEntry list }

type MigrationClaimJournalNamespaceCensus =
    { ClaimNamespace: MigrationClaimJournalNamespace
      ClaimPrefix: string
      ClaimNamespaceReads: MigrationReviewDeliveryRead list
      ClaimRefs: MigrationClaimJournalRef list }

type MigrationClaimJournalPass =
    { ClaimRepository: MigrationReviewDeliveryRepository
      ClaimNamespaces: MigrationClaimJournalNamespaceCensus list
      ClaimHistories: MigrationClaimJournalHistory list
      ClaimFingerprint: string }

type MigrationClaimJournalTwoPass =
    { ClaimFirst: MigrationClaimJournalPass
      ClaimSecond: MigrationClaimJournalPass }

type MigrationLegacyReceiptSource =
    { ProducerId: string
      ProducerRevision: string
      SourceIdentity: string
      SchemaFamily: string
      SourceKind: MigrationLegacyReceiptSourceKind }

and MigrationLegacyReceiptSourceKind =
    | ProtectedProducer
    | ProtectedParserOnly
    | LocalCacheOnly

type MigrationLegacyReceiptInventory =
    { ProducerReads: MigrationReviewDeliveryRead list
      Sources: MigrationLegacyReceiptSource list
      Fingerprint: string }

[<RequireQualifiedAccess>]
module MigrationClaimEventCaptureContract =
    let claimRefPrefix = "refs/heads/fsgg/v2/journal/claim/"
    let operationRefPrefix = "refs/heads/fsgg/v2/journal/operation/"
    let journalRefPrefixes = [ claimRefPrefix; operationRefPrefix ]

    let namespacePrefix = function
        | ClaimJournalNamespace -> claimRefPrefix
        | OperationJournalNamespace -> operationRefPrefix

    let requiredNativeStreams (_: MigrationClaimNativeSubjectKind) =
        [ NativeIssueComments; NativeIssueEvents; NativeIssueTimeline ]

    let requiredLegacySchemaFamilies =
        [ "claim-marker"
          "review-decision"
          "review-wait"
          "delivery-obligation"
          "delivery-receipt"
          "intake-marker"
          "intake-receipt"
          "typed-completion"
          "completion-correction"
          "legacy-done-receipt" ]

    let schemaFamilyAllowed namespace' family =
        match namespace', family with
        | ClaimJournalNamespace, ClaimSchemaFamily -> true
        | OperationJournalNamespace, (AdmissionSchemaFamily | OrdinarySchemaFamily | ReviewSchemaFamily) -> true
        | _ -> false

    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private oid value =
        not (String.IsNullOrWhiteSpace value) && value.Length = 40
        && value |> Seq.forall Uri.IsHexDigit

    let private readParts (read: MigrationReviewDeliveryRead) =
        [ MigrationReviewDeliveryCaptureContract.requestSha256 read.Request
          read.RequestSha256; string read.StatusCode; read.RawSha256
          defaultArg read.NextRequestUri "" ]

    let private sourceKindText = function
        | ProtectedProducer -> "protected-producer"
        | ProtectedParserOnly -> "protected-parser-only"
        | LocalCacheOnly -> "local-cache-only"

    let legacyInventoryFingerprint (inventory: MigrationLegacyReceiptInventory) =
        (inventory.ProducerReads |> List.collect readParts)
        @ (inventory.Sources
           |> List.collect (fun source ->
               [ source.ProducerId; source.ProducerRevision; source.SourceIdentity
                 source.SchemaFamily; sourceKindText source.SourceKind ]))
        |> List.map frame |> String.concat "" |> sha

    let private trySourceIdentity (read: MigrationReviewDeliveryRead) =
        try
            let uri = Uri read.Request.Uri
            let query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            let revision =
                query
                |> Array.tryPick (fun part ->
                    let pair = part.Split('=', 2)
                    if pair.Length = 2 && pair.[0] = "ref" then Some(Uri.UnescapeDataString pair.[1]) else None)
            use response = JsonDocument.Parse read.RawBody
            let root = response.RootElement
            let mutable shaProperty = Unchecked.defaultof<JsonElement>
            let mutable encodingProperty = Unchecked.defaultof<JsonElement>
            let mutable contentProperty = Unchecked.defaultof<JsonElement>
            let propertyNames =
                if root.ValueKind = JsonValueKind.Object then
                    root.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                else []
            if read.Request.Kind <> "rest" || read.Request.Method <> "Get" || read.Request.Body.IsSome
               || not read.Request.Variables.IsEmpty || read.NextRequestUri.IsSome
               || read.StatusCode <> 200
               || read.RequestSha256 <> MigrationReviewDeliveryCaptureContract.requestSha256 read.Request
               || read.RawSha256 <> sha read.RawBody
               || uri.Host <> "api.github.com"
               || query.Length <> 1
               || not (String.IsNullOrEmpty uri.Fragment)
               || not (uri.AbsolutePath.StartsWith("/repos/FS-GG/.github/contents/", StringComparison.Ordinal))
               || propertyNames.Length <> (propertyNames |> Set.ofList |> Set.count)
               || not (root.TryGetProperty("sha", &shaProperty))
               || not (root.TryGetProperty("encoding", &encodingProperty))
               || not (root.TryGetProperty("content", &contentProperty))
               || shaProperty.ValueKind <> JsonValueKind.String
               || encodingProperty.ValueKind <> JsonValueKind.String
               || contentProperty.ValueKind <> JsonValueKind.String
               || encodingProperty.GetString() <> "base64" then None
            else
                let bytes = Convert.FromBase64String(contentProperty.GetString().Replace("\n", ""))
                let gitBytes = Array.append (Encoding.ASCII.GetBytes($"blob {bytes.LongLength}\u0000")) bytes
                let gitSha = gitBytes |> SHA1.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
                if shaProperty.GetString() <> gitSha then None
                else Some(read.Request.Uri + "#sha256:" + (bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()), revision)
        with
        | :? JsonException | :? FormatException | :? UriFormatException -> None

    let validateLegacyInventory inventory =
        let decoded = inventory.ProducerReads |> List.map (fun read -> read, trySourceIdentity read)
        let identities =
            decoded
            |> List.choose (fun (_, value) -> value |> Option.map fst)
        let referencedIdentities = inventory.Sources |> List.map _.SourceIdentity |> Set.ofList
        let sourceKeys =
            inventory.Sources
            |> List.map (fun source -> source.SchemaFamily, source.ProducerId, source.SourceIdentity)
        let families = inventory.Sources |> List.map _.SchemaFamily |> Set.ofList
        let required = requiredLegacySchemaFamilies |> Set.ofList
        let revisionsValid =
            inventory.Sources
            |> List.forall (fun source ->
                oid source.ProducerRevision
                && source.ProducerRevision = source.ProducerRevision.ToLowerInvariant()
                && not (String.IsNullOrWhiteSpace source.ProducerId)
                && Set.contains source.SchemaFamily required
                && (decoded
                    |> List.exists (fun (_, decodedIdentity) ->
                        decodedIdentity = Some(source.SourceIdentity, Some source.ProducerRevision))))
        if inventory.ProducerReads.IsEmpty || decoded |> List.exists (snd >> Option.isNone) then
            Error "legacy-inventory-source-read"
        elif identities.Length <> (identities |> Set.ofList |> Set.count) then
            Error "legacy-inventory-duplicate-read"
        elif Set.ofList identities <> referencedIdentities then
            Error "legacy-inventory-source-roster"
        elif sourceKeys.Length <> (sourceKeys |> Set.ofList |> Set.count) then
            Error "legacy-inventory-duplicate-source"
        elif families <> required then
            Error "legacy-inventory-family-roster"
        elif not revisionsValid then
            Error "legacy-inventory-source-identity"
        elif inventory.Fingerprint <> legacyInventoryFingerprint inventory then
            Error "legacy-inventory-fingerprint"
        else Ok inventory

    let qualifyLegacyInventory inventory =
        validateLegacyInventory inventory
        |> Result.bind (fun valid ->
            // The pinned source audit found only consumers for these receipt markers, or a
            // process-local cache. A caller-authored SourceKind must not upgrade that evidence.
            let sourceAuditGaps =
                Set.ofList [ "delivery-receipt"; "intake-receipt"; "legacy-done-receipt" ]
            let unresolved =
                requiredLegacySchemaFamilies
                |> List.filter (fun family ->
                    Set.contains family sourceAuditGaps
                    || (valid.Sources
                        |> List.exists (fun source ->
                            source.SchemaFamily = family && source.SourceKind = ProtectedProducer)
                        |> not))
            match unresolved with
            | [] -> Ok valid
            | values -> Error("legacy-inventory-producer-unavailable:" + String.concat "," values))

    let private namespaceText = function
        | ClaimJournalNamespace -> "claim"
        | OperationJournalNamespace -> "operation"

    let private familyText = function
        | ClaimSchemaFamily -> "claim"
        | AdmissionSchemaFamily -> "admission"
        | OrdinarySchemaFamily -> "ordinary"
        | ReviewSchemaFamily -> "review"

    let passFingerprint (pass: MigrationClaimJournalPass) =
        let historyParts =
            pass.ClaimHistories
            |> List.collect (fun history ->
                [ history.ClaimHistoryRefName; history.ClaimHistoryHeadSha ]
                @ (history.ClaimEntries
                   |> List.collect (fun entry ->
                       [ entry.ClaimCommitSha; defaultArg entry.ClaimParentSha ""; entry.ClaimTreeSha
                         namespaceText entry.ClaimRecord.Namespace; entry.ClaimRecord.Schema
                         familyText entry.ClaimRecord.Family; entry.ClaimRecord.CanonicalId
                         defaultArg entry.ClaimRecord.OperationId ""; string entry.ClaimRecord.Generation ]
                       @ (entry.ClaimReads |> List.collect readParts))))
        [ string pass.ClaimRepository.RepositoryId; pass.ClaimRepository.NodeId; pass.ClaimRepository.FullName ]
        @ readParts pass.ClaimRepository.Read
        @ (pass.ClaimNamespaces |> List.collect (fun census ->
            [ namespaceText census.ClaimNamespace; census.ClaimPrefix ]
            @ (census.ClaimNamespaceReads |> List.collect readParts)
            @ (census.ClaimRefs |> List.collect (fun item -> [ item.ClaimRefName; item.ClaimHeadSha ]))))
        @ historyParts
        |> List.map frame |> String.concat "" |> sha

    let private validRead (read: MigrationReviewDeliveryRead) =
        read.StatusCode = 200
        && read.RequestSha256 = MigrationReviewDeliveryCaptureContract.requestSha256 read.Request
        && read.RawSha256 = sha read.RawBody

    let private rawTypedEntry (entry: MigrationClaimJournalHistoryEntry) =
        let property (name: string) (value: JsonElement) =
            let mutable found = Unchecked.defaultof<JsonElement>
            if value.ValueKind <> JsonValueKind.Object || not (value.TryGetProperty(name, &found)) then
                invalidOp $"missing:{name}"
            found
        let text name value =
            let found = property name value
            if found.ValueKind <> JsonValueKind.String then invalidOp $"invalid:{name}"
            found.GetString()
        let number name value =
            let found = property name value
            let mutable parsed = 0L
            if found.ValueKind <> JsonValueKind.Number || not (found.TryGetInt64(&parsed)) then
                invalidOp $"invalid:{name}"
            parsed
        let decodeBlob (read: MigrationReviewDeliveryRead) =
            use response = JsonDocument.Parse read.RawBody
            let root = response.RootElement
            let oid = text "sha" root
            let bytes = Convert.FromBase64String((text "content" root).Replace("\n", ""))
            let gitBytes = Array.append (Encoding.ASCII.GetBytes($"blob {bytes.LongLength}\u0000")) bytes
            let actualOid = gitBytes |> SHA1.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
            let requestUri = Uri read.Request.Uri
            if text "encoding" root <> "base64" || oid <> actualOid
               || not (requestUri.AbsolutePath.EndsWith($"/git/blobs/{oid}", StringComparison.Ordinal)) then
                invalidOp "blob-identity"
            bytes
        let canonicalIds family (event: JsonElement) =
            match family with
            | ClaimSchemaFamily ->
                let subject = text "subject" event
                let primary = ClaimTouchSetAdapter.claimAddress subject |> Result.toOption |> Option.map _.CanonicalId
                let conflicts =
                    property "touches" event |> _.EnumerateArray()
                    |> Seq.choose (fun touch ->
                        ClaimTouchSetAdapter.conflictAddress
                            { Repository=text "repository" touch; Path=text "path" touch }
                        |> Result.toOption |> Option.map _.CanonicalId)
                    |> Seq.toList
                primary |> Option.toList |> List.append conflicts
            | AdmissionSchemaFamily -> [ "fleet-v1-admission:fs-gg-production" ]
            | OrdinarySchemaFamily -> [ entry.ClaimRecord.CanonicalId ]
            | ReviewSchemaFamily ->
                ReviewDeliveryAdapter.deliveryAddress (text "subject" event)
                |> Result.toOption |> Option.map _.CanonicalId |> Option.toList
        try
            if entry.ClaimReads.Length <> 4 && entry.ClaimReads.Length <> 5 then false
            else
                let eventBytes = decodeBlob entry.ClaimReads.[2]
                let headBytes = decodeBlob entry.ClaimReads.[3]
                use eventDocument = JsonDocument.Parse eventBytes
                use headDocument = JsonDocument.Parse headBytes
                let event, head = eventDocument.RootElement, headDocument.RootElement
                let familyMatches =
                    match entry.ClaimRecord.Family with
                    | ClaimSchemaFamily ->
                        number "schemaVersion" event = 1L
                        && entry.ClaimRecord.Schema = "fsgg.coordination.claim-authority/1"
                        && entry.ClaimRecord.OperationId = Some(text "operationId" event)
                    | AdmissionSchemaFamily ->
                        text "schema" event = entry.ClaimRecord.Schema
                        && entry.ClaimRecord.OperationId = Some(text "commandId" event)
                    | OrdinarySchemaFamily ->
                        text "schema" event = entry.ClaimRecord.Schema
                        && entry.ClaimRecord.OperationId = Some(text "operationId" event)
                        && number "generation" event = entry.ClaimRecord.Generation
                    | ReviewSchemaFamily ->
                        number "schemaVersion" event = 1L
                        && entry.ClaimRecord.Schema = "fsgg.coordination.delivery-authority/1"
                        && entry.ClaimRecord.OperationId = Some(text "operationId" event)
                let expectedKind =
                    match entry.ClaimRecord.Namespace with
                    | ClaimJournalNamespace -> "claim"
                    | OperationJournalNamespace -> "operation"
                familyMatches
                && text "journalKind" head = expectedKind
                && text "aggregateId" head = entry.ClaimRecord.CanonicalId
                && number "generation" head = entry.ClaimRecord.Generation
                && text "eventDigest" head =
                    (eventBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant())
                && List.contains entry.ClaimRecord.CanonicalId (canonicalIds entry.ClaimRecord.Family event)
        with
        | :? InvalidOperationException | :? JsonException | :? FormatException | :? UriFormatException -> false

    let private validNamespaceRead (repository: MigrationReviewDeliveryRepository) census =
        match census.ClaimNamespaceReads with
        | [ read ] ->
            let suffix =
                let segments = repository.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries)
                if segments.Length <> 2 then ""
                else
                    let relative = namespacePrefix census.ClaimNamespace |> fun value -> value.Substring("refs/".Length)
                    $"/repos/{Uri.EscapeDataString segments.[0]}/{Uri.EscapeDataString segments.[1]}/git/matching-refs/{relative}"
            match Uri.TryCreate(read.Request.Uri, UriKind.Absolute) with
            | true, uri ->
                not (String.IsNullOrEmpty suffix)
                && uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal)
                && String.IsNullOrEmpty uri.Query
                && read.Request.Kind = "rest" && read.Request.Method = "Get"
                && read.Request.Body.IsNone && read.Request.Variables.IsEmpty
                && read.NextRequestUri.IsNone && validRead read
            | _ -> false
        | _ -> false

    let private validatePass (pass: MigrationClaimJournalPass) =
        let expectedNamespaces = [ ClaimJournalNamespace; OperationJournalNamespace ]
        let actualNamespaces = pass.ClaimNamespaces |> List.map _.ClaimNamespace
        let refs = pass.ClaimNamespaces |> List.collect _.ClaimRefs
        let refNames = refs |> List.map _.ClaimRefName
        let historyNames = pass.ClaimHistories |> List.map _.ClaimHistoryRefName
        let namespacesValid =
            actualNamespaces = expectedNamespaces
            && pass.ClaimNamespaces |> List.forall (fun census ->
                census.ClaimPrefix = namespacePrefix census.ClaimNamespace
                && validNamespaceRead pass.ClaimRepository census
                && census.ClaimRefs |> List.forall (fun item ->
                    item.ClaimRefName.StartsWith(census.ClaimPrefix, StringComparison.Ordinal)
                    && item.ClaimRefName.Length > census.ClaimPrefix.Length && oid item.ClaimHeadSha))
        let uniqueRefs = refNames.Length = (refNames |> Set.ofList |> Set.count)
        let completeHistories = List.sort refNames = List.sort historyNames
        let historiesValid =
            pass.ClaimHistories |> List.forall (fun history ->
                let historyNamespace =
                    if history.ClaimHistoryRefName.StartsWith(claimRefPrefix, StringComparison.Ordinal) then Some ClaimJournalNamespace
                    elif history.ClaimHistoryRefName.StartsWith(operationRefPrefix, StringComparison.Ordinal) then Some OperationJournalNamespace
                    else None
                match history.ClaimEntries with
                | [] -> false
                | entries ->
                    history.ClaimHistoryHeadSha = entries.Head.ClaimCommitSha && oid history.ClaimHistoryHeadSha
                    && entries |> List.forall (fun entry ->
                        oid entry.ClaimCommitSha && oid entry.ClaimTreeSha
                        && entry.ClaimReads |> List.forall validRead
                        && entry.ClaimRecord.Generation >= 0L
                        && not (String.IsNullOrWhiteSpace entry.ClaimRecord.Schema)
                        && not (String.IsNullOrWhiteSpace entry.ClaimRecord.CanonicalId)
                        && Some entry.ClaimRecord.Namespace = historyNamespace
                        && schemaFamilyAllowed entry.ClaimRecord.Namespace entry.ClaimRecord.Family
                        && rawTypedEntry entry)
                    && entries
                       |> List.pairwise
                       |> List.forall (fun (current, next) ->
                           current.ClaimParentSha = Some next.ClaimCommitSha
                           && current.ClaimRecord.Generation > next.ClaimRecord.Generation)
                    && entries |> List.last |> _.ClaimParentSha |> Option.isNone)
        if not namespacesValid then Error "claim-journal-namespace-census"
        elif not uniqueRefs then Error "claim-journal-duplicate-ref"
        elif not completeHistories then Error "claim-journal-history-roster"
        elif not historiesValid then Error "claim-journal-history"
        elif pass.ClaimFingerprint <> passFingerprint pass then Error "claim-journal-fingerprint"
        else Ok pass

    let validateTwoPass value =
        validatePass value.ClaimFirst
        |> Result.bind (fun _ -> validatePass value.ClaimSecond)
        |> Result.bind (fun _ ->
            if value.ClaimFirst <> value.ClaimSecond then Error "claim-journal-pass-drift"
            else Ok value)
