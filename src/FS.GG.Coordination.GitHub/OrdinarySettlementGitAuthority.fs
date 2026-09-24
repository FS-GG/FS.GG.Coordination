#nowarn "3391"

namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

type OrdinarySettlementAuthorityDocument =
    {
        Address: AggregateAddress
        Revision: string option
        Entries: Map<string, OrdinarySettlementEntry>
        Effects: Map<string, string>
    }

type OrdinarySettlementDocumentRead =
    | SettlementDocumentAbsent
    | SettlementDocumentKnownEmpty of revision: string
    | SettlementDocumentPresent of revision: string * bytes: byte array
    | SettlementDocumentReadUnknown of string

type IOrdinarySettlementGitAuthorityTransport =
    abstract ReadDocument: AggregateAddress -> OrdinarySettlementDocumentRead
    abstract CompareExchangeDocument:
        address: AggregateAddress * expectedParent: string option * canonicalDocument: byte array -> OrdinarySettlementCasOutcome
    abstract VerifyCurrentProtection: AggregateAddress * appId: int64 -> Result<unit, string>

type OrdinarySettlementGitHubAuthorityOptions =
    {
        ApiBase: Uri
        Token: string
        UserAgent: string
        Repository: string
        RepositoryId: int64
        ExpectedAppId: int64
        WriterRulesetId: int64
        WriterRulesetName: string
        IntegrityRulesetId: int64
        IntegrityRulesetName: string
        RetainedWriterAppIds: int64 list
        WriterIncludes: Set<string>
        WriterExcludes: Set<string>
        IntegrityIncludes: Set<string>
        IntegrityExcludes: Set<string>
        WriterUpdatedAt: string
        IntegrityUpdatedAt: string
        ExpectedEpochCommit: string
        ExpectedEpochGeneration: int64
        EpochAggregateId: string
        EpochFleetId: string
        EpochRef: string
    }

type OrdinarySettlementAuthorityProfile =
    {
        Name: string
        Repository: string
        RepositoryId: int64
        Environment: string
        PolicyId: string
        WriterRulesetId: int64
        WriterRulesetName: string
        IntegrityRulesetId: int64
        IntegrityRulesetName: string
        RetainedWriterAppIds: int64 list
        WriterExcludes: Set<string>
        EpochAggregateId: string
        EpochFleetId: string
        EpochRef: string
        Production: bool
    }

type OrdinarySettlementRulesetAnchor =
    {
        Id: int64
        Name: string
        Enforcement: string
        UpdatedAt: string
        Includes: Set<string>
        Excludes: Set<string>
        Rules: Set<string>
        BypassActors: Set<int64 * string * string>
    }

type OrdinarySettlementPublicAnchor =
    {
        Trust: OrdinarySettlementTrustAnchor
        PolicyId: string
        OperationClass: string
        Repository: string
        SourceCommit: string
        WriterRuleset: OrdinarySettlementRulesetAnchor
        IntegrityRuleset: OrdinarySettlementRulesetAnchor
        EffectiveRules: Set<string * int64 * string * string>
    }

[<RequireQualifiedAccess>]
module OrdinarySettlementAuthorityProfiles =
    let production =
        { Name = "production"
          Repository = "FS-GG/FS.GG.Coordination.Authority"
          RepositoryId = 1351660651L
          Environment = "ordinary-v2"
          PolicyId = "v2-ci-i1-ordinary-settlement-v1"
          WriterRulesetId = 21872113L
          WriterRulesetName = "v2-journal-writer"
          IntegrityRulesetId = 21872115L
          IntegrityRulesetName = "v2-journal-integrity"
          RetainedWriterAppIds = [ 4882140L ]
          WriterExcludes = Set [ "refs/heads/fsgg/v2/journal/cutover/d5" ]
          EpochAggregateId = "fleet-cutover:fs-gg-production"
          EpochFleetId = "fs-gg-production"
          EpochRef = "refs/heads/fsgg/v2/journal/cutover/d5"
          Production = true }

    let rehearsal =
        { Name = "rehearsal"
          Repository = "FS-GG/FS.GG.Coordination.Authority.Sandbox"
          RepositoryId = 1385801070L
          Environment = "ordinary-v2-rehearsal"
          PolicyId = "v2-ci-i1-ordinary-settlement-rehearsal-v1"
          WriterRulesetId = 23947019L
          WriterRulesetName = "v2-journal-writer-rehearsal"
          IntegrityRulesetId = 23947025L
          IntegrityRulesetName = "v2-journal-integrity-rehearsal"
          RetainedWriterAppIds = []
          WriterExcludes = Set.empty
          EpochAggregateId = "fleet-cutover:fs-gg-v2-rehearsal"
          EpochFleetId = "fs-gg-v2-rehearsal"
          EpochRef = "refs/heads/ordinary-v2-rehearsal-epoch"
          Production = false }

    let requireProduction profile =
        if profile = production then Ok production
        else Error "production-command-refuses-rehearsal-profile"

[<RequireQualifiedAccess>]
module OrdinarySettlementPublicAnchor =
    let private fields (value: JsonElement) =
        value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

    let private exact expected value = fields value = Set.ofList expected

    let private strings (value: JsonElement) =
        value.EnumerateArray() |> Seq.map _.GetString() |> Set.ofSeq

    let private ruleset (value: JsonElement) =
        let conditions = value.GetProperty("conditions").GetProperty("ref_name")
        { Id = value.GetProperty("id").GetInt64()
          Name = value.GetProperty("name").GetString()
          Enforcement = value.GetProperty("enforcement").GetString()
          UpdatedAt = value.GetProperty("updatedAt").GetString()
          Includes = strings (conditions.GetProperty("include"))
          Excludes = strings (conditions.GetProperty("exclude"))
          Rules = value.GetProperty("rules").EnumerateArray() |> Seq.map (fun item -> item.GetProperty("type").GetString()) |> Set.ofSeq
          BypassActors =
            value.GetProperty("bypassActors").EnumerateArray()
            |> Seq.map (fun item ->
                item.GetProperty("actorId").GetInt64(),
                item.GetProperty("actorType").GetString(),
                item.GetProperty("bypassMode").GetString())
            |> Set.ofSeq }

    let parse (profile: OrdinarySettlementAuthorityProfile) (bytes: ReadOnlyMemory<byte>) =
        try
            use document = JsonDocument.Parse bytes
            let root = document.RootElement
            let authorizer = root.GetProperty("authorizer")
            let writer = root.GetProperty("writer")
            let rulesets = root.GetProperty("rulesets")
            let permissions =
                writer.GetProperty("permissions").EnumerateObject()
                |> Seq.map (fun item -> item.Name, item.Value.GetString())
                |> Map.ofSeq
            let writerRulesetElement = rulesets.GetProperty("writer")
            let integrityRulesetElement = rulesets.GetProperty("integrity")
            let writerRuleset = ruleset writerRulesetElement
            let integrityRuleset = ruleset integrityRulesetElement
            let effectiveRules =
                root.GetProperty("effectiveRules").EnumerateArray()
                |> Seq.map (fun item ->
                    item.GetProperty("type").GetString(), item.GetProperty("rulesetId").GetInt64(),
                    item.GetProperty("rulesetSourceType").GetString(), item.GetProperty("rulesetSource").GetString())
                |> Set.ofSeq
            let value =
                { Trust =
                    { KeyId = authorizer.GetProperty("keyId").GetString()
                      PublicKeySpkiSha256 = authorizer.GetProperty("publicKeySpkiSha256").GetString()
                      AppId = writer.GetProperty("appId").GetInt64()
                      InstallationId = writer.GetProperty("installationId").GetInt64()
                      RepositoryId = writer.GetProperty("repositoryId").GetInt64()
                      Permissions = permissions }
                  PolicyId = root.GetProperty("policyId").GetString()
                  OperationClass = root.GetProperty("operationClass").GetString()
                  Repository = writer.GetProperty("repository").GetString()
                  SourceCommit = root.GetProperty("sourceCommit").GetString()
                  WriterRuleset = writerRuleset
                  IntegrityRuleset = integrityRuleset
                  EffectiveRules = effectiveRules }
            let expectedEffective =
                Set
                    [ "creation", profile.WriterRulesetId, "Repository", profile.Repository
                      "update", profile.WriterRulesetId, "Repository", profile.Repository
                      "deletion", profile.IntegrityRulesetId, "Repository", profile.Repository
                      "non_fast_forward", profile.IntegrityRulesetId, "Repository", profile.Repository ]
            let validDigest (text: string) =
                not (isNull text) && text.Length = 64 && text |> Seq.forall (fun item -> Uri.IsHexDigit item && not (Char.IsUpper item))
            let validCommit (text: string) =
                not (isNull text) && text.Length = 40 && text |> Seq.forall Uri.IsHexDigit
            let expectedIncludes = Set [ "refs/heads/fsgg/v2/journal/**/*" ]
            let writerActors = value.WriterRuleset.BypassActors |> Set.map (fun (id, actorType, mode) -> id, actorType, mode)
            let expectedWriterActors =
                Set.ofList ((value.Trust.AppId :: profile.RetainedWriterAppIds) |> List.map (fun id -> id, "Integration", "always"))
            let mutable accepted = DateTimeOffset.MinValue
            let mutable writerUpdated = DateTimeOffset.MinValue
            let mutable integrityUpdated = DateTimeOffset.MinValue
            let acceptedAt = root.GetProperty("acceptedAt").GetString()
            let exactRuleset (element: JsonElement) =
                exact [ "id"; "name"; "enforcement"; "updatedAt"; "conditions"; "rules"; "bypassActors" ] element
                && exact [ "ref_name" ] (element.GetProperty("conditions"))
                && exact [ "include"; "exclude" ] (element.GetProperty("conditions").GetProperty("ref_name"))
                && element.GetProperty("rules").EnumerateArray() |> Seq.forall (exact [ "type" ])
                && element.GetProperty("bypassActors").EnumerateArray()
                   |> Seq.forall (exact [ "actorId"; "actorType"; "bypassMode" ])
            if not (exact [ "schema"; "policyId"; "operationClass"; "authorizer"; "writer"; "rulesets"; "effectiveRules"; "acceptedAt"; "sourceCommit" ] root)
               || not (exact [ "keyId"; "algorithm"; "publicKeySpkiSha256" ] authorizer)
               || not (exact [ "appId"; "installationId"; "repository"; "repositoryId"; "permissions" ] writer)
               || not (exact [ "contents"; "metadata" ] (writer.GetProperty("permissions")))
               || not (exact [ "writer"; "integrity" ] rulesets)
               || not (exactRuleset writerRulesetElement && exactRuleset integrityRulesetElement)
               || (root.GetProperty("effectiveRules").EnumerateArray()
                   |> Seq.forall (exact [ "type"; "rulesetId"; "rulesetSourceType"; "rulesetSource" ])
                   |> not)
               || not (DateTimeOffset.TryParse(acceptedAt, &accepted))
               || root.GetProperty("schema").GetString() <> "fsgg.github.v2-ci-ordinary-settlement-anchor/1"
               || authorizer.GetProperty("algorithm").GetString() <> "RSA-PSS-SHA256"
               || value.PolicyId <> profile.PolicyId
               || value.OperationClass <> "ordinary-post-merge-delivery-settlement"
               || value.Repository <> profile.Repository
               || value.Trust.RepositoryId <> profile.RepositoryId
               || value.Trust.AppId <= 0L || value.Trust.InstallationId <= 0L
               || value.Trust.Permissions <> Map [ "contents", "write"; "metadata", "read" ]
               || String.IsNullOrWhiteSpace value.Trust.KeyId || not (validDigest value.Trust.PublicKeySpkiSha256)
               || not (validCommit value.SourceCommit)
               || value.WriterRuleset.Id <> profile.WriterRulesetId
               || value.WriterRuleset.Name <> profile.WriterRulesetName
               || value.WriterRuleset.Enforcement <> "active"
               || not (DateTimeOffset.TryParse(value.WriterRuleset.UpdatedAt, &writerUpdated))
               || value.WriterRuleset.Includes <> expectedIncludes
               || value.WriterRuleset.Excludes <> profile.WriterExcludes
               || value.WriterRuleset.Rules <> Set [ "creation"; "update" ]
               || writerActors <> expectedWriterActors
               || value.IntegrityRuleset.Id <> profile.IntegrityRulesetId
               || value.IntegrityRuleset.Name <> profile.IntegrityRulesetName
               || value.IntegrityRuleset.Enforcement <> "active"
               || not (DateTimeOffset.TryParse(value.IntegrityRuleset.UpdatedAt, &integrityUpdated))
               || value.IntegrityRuleset.Includes <> expectedIncludes
               || not value.IntegrityRuleset.Excludes.IsEmpty
               || value.IntegrityRuleset.Rules <> Set [ "deletion"; "non_fast_forward" ]
               || not value.IntegrityRuleset.BypassActors.IsEmpty
               || value.EffectiveRules <> expectedEffective
               || accepted.ToUniversalTime() < writerUpdated.ToUniversalTime().AddSeconds 60.0
               || accepted.ToUniversalTime() < integrityUpdated.ToUniversalTime().AddSeconds 60.0 then Error "ordinary-settlement-anchor-binding"
            else Ok value
        with _ -> Error "ordinary-settlement-anchor-json"

    let transportOptions apiBase token userAgent (profile: OrdinarySettlementAuthorityProfile) (anchor: OrdinarySettlementPublicAnchor)
        expectedEpochCommit expectedEpochGeneration =
        { ApiBase = apiBase
          Token = token
          UserAgent = userAgent
          Repository = profile.Repository
          RepositoryId = profile.RepositoryId
          ExpectedAppId = anchor.Trust.AppId
          WriterRulesetId = anchor.WriterRuleset.Id
          WriterRulesetName = profile.WriterRulesetName
          IntegrityRulesetId = anchor.IntegrityRuleset.Id
          IntegrityRulesetName = profile.IntegrityRulesetName
          RetainedWriterAppIds =
            anchor.WriterRuleset.BypassActors
            |> Seq.map (fun (id, _, _) -> id)
            |> Seq.filter ((<>) anchor.Trust.AppId)
            |> Seq.sort
            |> Seq.toList
          WriterIncludes = anchor.WriterRuleset.Includes
          WriterExcludes = anchor.WriterRuleset.Excludes
          IntegrityIncludes = anchor.IntegrityRuleset.Includes
          IntegrityExcludes = anchor.IntegrityRuleset.Excludes
          WriterUpdatedAt = anchor.WriterRuleset.UpdatedAt
          IntegrityUpdatedAt = anchor.IntegrityRuleset.UpdatedAt
          ExpectedEpochCommit = expectedEpochCommit
          ExpectedEpochGeneration = expectedEpochGeneration
          EpochAggregateId = profile.EpochAggregateId
          EpochFleetId = profile.EpochFleetId
          EpochRef = profile.EpochRef }

[<RequireQualifiedAccess>]
module OrdinarySettlementAuthorityDocument =
    let private schema = "fsgg.coordination.ordinary-settlement-authority-document/1"

    let private stageText =
        function
        | SettlementIntentPersisted -> "intent-persisted"
        | SettlementEffectPending -> "effect-pending"
        | SettlementComplete -> "complete"

    let private parseStage =
        function
        | "intent-persisted" -> Some SettlementIntentPersisted
        | "effect-pending" -> Some SettlementEffectPending
        | "complete" -> Some SettlementComplete
        | _ -> None

    let private validText (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private validDigest (value: string) =
        validText value && value.Length = 64 && value |> Seq.forall Uri.IsHexDigit

    let private canonical (root: JsonNode) =
        root.ToJsonString(JsonSerializerOptions(WriteIndented = false))
        |> ShardedJournalAdapter.canonicalJson
        |> Result.defaultWith invalidOp

    let encode (document: OrdinarySettlementAuthorityDocument) =
        let root = JsonObject()
        root.Add("schema", schema)
        root.Add("journalRef", document.Address.Ref)
        let entries = JsonObject()
        for KeyValue(key, value) in document.Entries do
            let item = JsonObject()
            item.Add("attemptId", value.AttemptId)
            item.Add("generation", value.Generation)
            item.Add("operationId", value.OperationId)
            item.Add("planDigest", value.PlanDigest)
            match value.ReceiptDigest with Some receipt -> item.Add("receiptDigest", receipt) | None -> ()
            item.Add("stage", stageText value.Stage)
            entries.Add(key, item)
        root.Add("entries", entries)
        let effects = JsonObject()
        for KeyValue(key, value) in document.Effects do effects.Add(key, value)
        root.Add("effects", effects)
        canonical root

    let decode (address: AggregateAddress) (revision: string option) (bytes: ReadOnlyMemory<byte>) =
        try
            use parsed = JsonDocument.Parse bytes
            let root = parsed.RootElement
            let required = Set [ "schema"; "journalRef"; "entries"; "effects" ]
            if root.ValueKind <> JsonValueKind.Object
               || (root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq) <> required
               || root.GetProperty("schema").GetString() <> schema
               || root.GetProperty("journalRef").GetString() <> address.Ref then
                Error "authority-document-envelope"
            else
                let entriesElement = root.GetProperty("entries")
                let effectsElement = root.GetProperty("effects")
                if entriesElement.ValueKind <> JsonValueKind.Object || effectsElement.ValueKind <> JsonValueKind.Object then
                    Error "authority-document-shape"
                else
                    let mutable error = None
                    let mutable entries = Map.empty
                    for property in entriesElement.EnumerateObject() do
                        let value = property.Value
                        let fields = Set [ "attemptId"; "generation"; "operationId"; "planDigest"; "receiptDigest"; "stage" ]
                        let actual = value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
                        let requiredEntry = Set.remove "receiptDigest" fields
                        if value.ValueKind <> JsonValueKind.Object || not (actual = fields || actual = requiredEntry) then
                            error <- Some "authority-document-entry-shape"
                        else
                            let operation = value.GetProperty("operationId").GetString()
                            let attempt = value.GetProperty("attemptId").GetString()
                            let digest = value.GetProperty("planDigest").GetString()
                            let generation = value.GetProperty("generation").GetInt64()
                            let stage = value.GetProperty("stage").GetString() |> parseStage
                            let receipt =
                                let mutable receiptElement = Unchecked.defaultof<JsonElement>
                                if value.TryGetProperty("receiptDigest", &receiptElement) then
                                    Some(receiptElement.GetString())
                                else None
                            if property.Name <> operation || not (validText operation) || not (validText attempt)
                               || not (validDigest digest) || generation < 1L || stage.IsNone
                               || (receipt |> Option.exists (validDigest >> not)) then
                                error <- Some "authority-document-entry-binding"
                            else
                                entries <- Map.add property.Name
                                    { OperationId = operation; AttemptId = attempt; PlanDigest = digest
                                      Generation = generation; Stage = stage.Value; ReceiptDigest = receipt } entries
                    let mutable effects = Map.empty
                    for property in effectsElement.EnumerateObject() do
                        let receipt = property.Value.GetString()
                        if not (validText property.Name) || not (validDigest receipt) then
                            error <- Some "authority-document-effect-binding"
                        else effects <- Map.add property.Name receipt effects
                    match error with
                    | Some reason -> Error reason
                    | None -> Ok { Address = address; Revision = revision; Entries = entries; Effects = effects }
        with _ -> Error "authority-document-json"

[<RequireQualifiedAccess>]
module OrdinarySettlementGitAuthority =
    let private empty (address: AggregateAddress) : OrdinarySettlementAuthorityDocument =
        { Address = address; Revision = None; Entries = Map.empty; Effects = Map.empty }

    let private receipt (plan: OrdinarySettlementPlan) =
        Encoding.UTF8.GetBytes(
            String.concat "\n"
                [ "fsgg.coordination.ordinary-settlement-effect-receipt/1"
                  plan.OperationId; plan.AttemptId; plan.Seal ]
        )
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    type Runtime(expectedAppId: int64, transport: IOrdinarySettlementGitAuthorityTransport) =
        let mutable observed = Map.empty<string * string option, OrdinarySettlementAuthorityDocument>

        let read (address: AggregateAddress) =
            match transport.ReadDocument address with
            | SettlementDocumentReadUnknown reason -> Error reason
            | SettlementDocumentAbsent ->
                let value = empty address
                observed <- Map.add (address.Ref, None) value observed
                Ok value
            | SettlementDocumentKnownEmpty revision ->
                let value = { empty address with Revision = Some revision }
                observed <- Map.add (address.Ref, Some revision) value observed
                Ok value
            | SettlementDocumentPresent(revision, bytes) ->
                match OrdinarySettlementAuthorityDocument.decode address (Some revision) (ReadOnlyMemory bytes) with
                | Error reason -> Error reason
                | Ok value ->
                    observed <- Map.add (address.Ref, Some revision) value observed
                    Ok value

        interface IOrdinaryPostMergeSettlementRuntime with
            member _.ReadShard address =
                match read address with
                | Error reason -> SettlementShardUnknown reason
                | Ok value ->
                    SettlementShardObserved
                        { Address = value.Address; Revision = value.Revision; Entries = value.Entries }

            member _.CompareExchangeShard(expectedParent, proposed) =
                match Map.tryFind (proposed.Address.Ref, expectedParent) observed with
                | None -> SettlementCasConflict
                | Some basis when basis.Address <> proposed.Address -> SettlementCasConflict
                | Some basis ->
                    match transport.VerifyCurrentProtection(proposed.Address, expectedAppId) with
                    | Error _ -> SettlementCasConflict
                    | Ok () ->
                        let document =
                            { basis with Revision = expectedParent; Entries = proposed.Entries }
                        transport.CompareExchangeDocument(
                            proposed.Address,
                            expectedParent,
                            OrdinarySettlementAuthorityDocument.encode document
                        )

            member _.ObserveEffect plan =
                match read plan.JournalAddress with
                | Error reason -> Error reason
                | Ok value ->
                    match Map.tryFind plan.OperationId value.Effects with
                    | None -> Ok SettlementEffectAbsent
                    | Some value when value = receipt plan -> Ok(SettlementEffectApplied value)
                    | Some _ -> Error "authority-effect-conflict"

            member _.ApplyEffect(plan, binding) =
                if binding.AppId <> expectedAppId then SettlementEffectRejected "authority-app-mismatch"
                else
                    match transport.VerifyCurrentProtection(plan.JournalAddress, binding.AppId), read plan.JournalAddress with
                    | Error reason, _ -> SettlementEffectRejected reason
                    | _, Error _ -> SettlementEffectResponseUnknown
                    | Ok(), Ok current ->
                        let expectedReceipt = receipt plan
                        match Map.tryFind plan.OperationId current.Effects with
                        | Some value when value = expectedReceipt -> SettlementEffectAccepted value
                        | Some _ -> SettlementEffectRejected "authority-effect-conflict"
                        | None ->
                            let proposed = { current with Effects = Map.add plan.OperationId expectedReceipt current.Effects }
                            match transport.CompareExchangeDocument(
                                plan.JournalAddress,
                                current.Revision,
                                OrdinarySettlementAuthorityDocument.encode proposed
                            ) with
                            | SettlementCasAccepted -> SettlementEffectAccepted expectedReceipt
                            | SettlementCasConflict -> SettlementEffectRejected "authority-effect-ref-conflict"
                            | SettlementCasUnknown -> SettlementEffectResponseUnknown

            member _.ReadBack plan =
                match read plan.JournalAddress with
                | Error reason -> Error reason
                | Ok value -> Ok(Map.tryFind plan.OperationId value.Effects)

[<RequireQualifiedAccess>]
module OrdinarySettlementGitHubAuthority =
    let private oid (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length = 40
        && value |> Seq.forall Uri.IsHexDigit

    let private repositoryPath (value: string) =
        value.Split('/') |> Array.map Uri.EscapeDataString |> String.concat "/"

    let private refPath (address: AggregateAddress) =
        address.Ref.Substring("refs/".Length).Split('/')
        |> Array.map Uri.EscapeDataString
        |> String.concat "/"

    let private documentPath (address: AggregateAddress) =
        $"ordinary-v2/{address.Digest}.json"

    let private contentPath (address: AggregateAddress) =
        documentPath address
        |> _.Split('/')
        |> Array.map Uri.EscapeDataString
        |> String.concat "/"

    let private branchPath (address: AggregateAddress) =
        address.Ref.Substring("refs/heads/".Length)
        |> Uri.EscapeDataString

    let private combine (root: Uri) (path: string) = Uri(root, path)

    type Transport(options: OrdinarySettlementGitHubAuthorityOptions, transport: IOrdinaryGitHubTransport) =
        let repository = repositoryPath options.Repository

        let request (methodValue: RestMethod) (path: string) (body: string option) (idempotency: IdempotencyClass) =
            let headers =
                Map
                    [ "Accept", "application/vnd.github+json"
                      "Authorization", "Bearer " + options.Token
                      "User-Agent", options.UserAgent
                      "X-GitHub-Api-Version", ApiVersion.value ApiVersion.required ]
            transport.Send(
                Rest
                    { Method = methodValue; Uri = combine options.ApiBase path; Headers = headers
                      Body = body; ApiVersion = ApiVersion.required; Idempotency = idempotency }
            )

        let jsonObject (body: string) =
            try
                use document = JsonDocument.Parse body
                if document.RootElement.ValueKind = JsonValueKind.Object then Ok(document.RootElement.Clone())
                else Error "authority-response-shape"
            with _ -> Error "authority-response-json"

        let response (expected: int list) (outcome: TransportOutcome) =
            match outcome with
            | Response value when List.contains value.StatusCode expected -> Ok value
            | Response value -> Error $"authority-http-{value.StatusCode}"
            | NetworkFailure -> Error "authority-network"
            | TimedOut -> Error "authority-timeout"

        let getObject (path: string) =
            request Get path None ReplaySafe
            |> response [ 200 ]
            |> Result.bind (fun value -> jsonObject value.Body)

        let getArray (path: string) =
            request Get path None ReplaySafe
            |> response [ 200 ]
            |> Result.bind (fun value ->
                try
                    use document = JsonDocument.Parse value.Body
                    if document.RootElement.ValueKind = JsonValueKind.Array then Ok(document.RootElement.Clone())
                    else Error "authority-response-shape"
                with _ -> Error "authority-response-json")

        let postObject (path: string) (body: JsonObject) (key: string) =
            request Post path (Some(body.ToJsonString())) (ReplayWithKey key)
            |> response [ 200; 201 ]
            |> Result.bind (fun value -> jsonObject value.Body)

        let readRef (address: AggregateAddress) =
            match request Get $"repos/{repository}/git/ref/{refPath address}" None ReplaySafe with
            | Response value when value.StatusCode = 404 -> Ok None
            | Response value when value.StatusCode = 200 ->
                jsonObject value.Body
                |> Result.bind (fun root ->
                    let target = root.GetProperty("object")
                    let sha = target.GetProperty("sha").GetString()
                    if target.GetProperty("type").GetString() = "commit" && oid sha then Ok(Some sha)
                    else Error "authority-ref-target")
            | Response value -> Error $"authority-ref-http-{value.StatusCode}"
            | NetworkFailure -> Error "authority-ref-network"
            | TimedOut -> Error "authority-ref-timeout"

        let readDocumentAt (address: AggregateAddress) (revision: string) =
            let path = contentPath address
            match request Get $"repos/{repository}/contents/{path}?ref={Uri.EscapeDataString revision}" None ReplaySafe with
            | Response value when value.StatusCode = 404 -> Ok None
            | outcome ->
                outcome
                |> response [ 200 ]
                |> Result.bind (fun value -> jsonObject value.Body)
                |> Result.bind (fun root ->
                    let encoding = root.GetProperty("encoding").GetString()
                    let content = root.GetProperty("content").GetString()
                    try
                        let bytes = Convert.FromBase64String(content.Replace("\n", ""))
                        if encoding = "base64" && bytes.Length <= 1_000_000 then Ok(Some bytes)
                        else Error "authority-document-size"
                    with _ -> Error "authority-document-base64")

        let createBlob (address: AggregateAddress) (bytes: byte array) =
            let body = JsonObject()
            body.Add("content", Convert.ToBase64String bytes)
            body.Add("encoding", "base64")
            postObject $"repos/{repository}/git/blobs" body ("blob-" + address.Digest)
            |> Result.bind (fun root ->
                let value = root.GetProperty("sha").GetString()
                if oid value then Ok value else Error "authority-blob-oid")

        let commitTree (revision: string) =
            getObject $"repos/{repository}/git/commits/{revision}"
            |> Result.bind (fun root ->
                let value = root.GetProperty("tree").GetProperty("sha").GetString()
                if oid value then Ok value else Error "authority-tree-oid")

        let createTree (address: AggregateAddress) (parent: string option) (blob: string) =
            let body = JsonObject()
            match parent with Some value -> body.Add("base_tree", value) | None -> ()
            let entry = JsonObject()
            entry.Add("mode", "100644")
            entry.Add("path", documentPath address)
            entry.Add("sha", blob)
            entry.Add("type", "blob")
            let tree = JsonArray()
            tree.Add entry
            body.Add("tree", tree)
            postObject $"repos/{repository}/git/trees" body ("tree-" + address.Digest)
            |> Result.bind (fun root ->
                let value = root.GetProperty("sha").GetString()
                if oid value then Ok value else Error "authority-tree-oid")

        let createCommit (address: AggregateAddress) (expected: string option) (tree: string) =
            let body = JsonObject()
            body.Add("message", $"ordinary v2 settlement shard {address.Digest}")
            body.Add("tree", tree)
            let parents = JsonArray()
            match expected with Some value -> parents.Add value | None -> ()
            body.Add("parents", parents)
            postObject $"repos/{repository}/git/commits" body ("commit-" + address.Digest)
            |> Result.bind (fun root ->
                let value = root.GetProperty("sha").GetString()
                let actualTree = root.GetProperty("tree").GetProperty("sha").GetString()
                let actualParents =
                    root.GetProperty("parents").EnumerateArray()
                    |> Seq.map (fun item -> item.GetProperty("sha").GetString())
                    |> Seq.toList
                if oid value && actualTree = tree && actualParents = (expected |> Option.toList) then Ok value
                else Error "authority-commit-parent-binding")

        let updateRef (address: AggregateAddress) (expected: string option) (proposed: string) =
            let body = JsonObject()
            body.Add("sha", proposed)
            match expected with
            | None ->
                body.Add("ref", address.Ref)
                request Post $"repos/{repository}/git/refs" (Some(body.ToJsonString())) NeverReplay
            | Some _ ->
                body.Add("force", false)
                request Patch $"repos/{repository}/git/refs/{refPath address}" (Some(body.ToJsonString())) NeverReplay

        let responseTargets proposed (value: ResponseEnvelope) =
            try
                use document = JsonDocument.Parse value.Body
                document.RootElement.GetProperty("object").GetProperty("type").GetString() = "commit"
                && document.RootElement.GetProperty("object").GetProperty("sha").GetString() = proposed
            with _ -> false

        let ruleTypes (root: JsonElement) =
            root.GetProperty("rules").EnumerateArray()
            |> Seq.map (fun value -> value.GetProperty("type").GetString())
            |> Set.ofSeq

        let bypassAppIds (root: JsonElement) =
            let mutable actors = Unchecked.defaultof<JsonElement>
            if root.TryGetProperty("bypass_actors", &actors) then
                actors.EnumerateArray()
                |> Seq.map (fun value ->
                    value.GetProperty("actor_id").GetInt64(),
                    value.GetProperty("actor_type").GetString(),
                    value.GetProperty("bypass_mode").GetString())
                |> Set.ofSeq
                |> Some
            else None

        let readRuleset (identifier: int64) = getObject $"repos/{repository}/rulesets/{identifier}"

        let readEffectiveRules (address: AggregateAddress) =
            getArray $"repos/{repository}/rules/branches/{branchPath address}"

        let conditions (root: JsonElement) =
            let value = root.GetProperty("conditions").GetProperty("ref_name")
            let strings (name: string) = value.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Set.ofSeq
            strings "include", strings "exclude"

        let effectiveRuleBindings (root: JsonElement) =
            root.EnumerateArray()
            |> Seq.map (fun value ->
                value.GetProperty("type").GetString(),
                value.GetProperty("ruleset_id").GetInt64(),
                value.GetProperty("ruleset_source_type").GetString(),
                value.GetProperty("ruleset_source").GetString())
            |> Set.ofSeq

        let sameInstant (expected: string) (actual: string) =
            let mutable expectedValue = DateTimeOffset.MinValue
            let mutable actualValue = DateTimeOffset.MinValue
            DateTimeOffset.TryParse(expected, &expectedValue)
            && DateTimeOffset.TryParse(actual, &actualValue)
            && expectedValue.ToUniversalTime() = actualValue.ToUniversalTime()

        let verifyRepositoryScope () =
            match getObject $"repos/{repository}", getObject "installation/repositories?per_page=100" with
            | Ok repositoryValue, Ok installation ->
                let repositories = installation.GetProperty("repositories").EnumerateArray() |> Seq.toList
                let expectedName = options.Repository.ToLowerInvariant()
                if repositoryValue.GetProperty("id").GetInt64() <> options.RepositoryId
                   || repositoryValue.GetProperty("full_name").GetString().ToLowerInvariant() <> expectedName
                   || installation.GetProperty("total_count").GetInt32() <> 1
                   || repositories.Length <> 1
                   || repositories[0].GetProperty("id").GetInt64() <> options.RepositoryId
                   || repositories[0].GetProperty("full_name").GetString().ToLowerInvariant() <> expectedName then
                    Error "authority-token-repository-scope"
                else Ok()
            | Error reason, _
            | _, Error reason -> Error reason

        let verifyEpochFence () =
            let epochAddress =
                ShardedJournalAdapter.address Cutover options.EpochAggregateId
                |> Result.defaultWith (fun _ -> invalidOp "invalid pinned epoch aggregate")
            let epochRef = options.EpochRef.Substring("refs/".Length)
            let refValue () =
                getObject $"repos/{repository}/git/ref/{epochRef}"
                |> Result.bind (fun root ->
                    let value = root.GetProperty("object").GetProperty("sha").GetString()
                    if oid value then Ok value else Error "authority-epoch-ref")
            let epochFile (revision: string) (path: string) =
                getObject $"repos/{repository}/contents/{path}?ref={Uri.EscapeDataString revision}"
                |> Result.bind (fun root ->
                    try
                        if root.GetProperty("encoding").GetString() <> "base64" then Error "authority-epoch-encoding"
                        else root.GetProperty("content").GetString().Replace("\n", "") |> Convert.FromBase64String |> Ok
                    with _ -> Error "authority-epoch-content")
            match refValue () with
            | Error reason -> Error reason
            | Ok before when before <> options.ExpectedEpochCommit -> Error "authority-epoch-moved"
            | Ok before ->
                match epochFile before "head.json", epochFile before "event.json", refValue () with
                | Ok headBytes, Ok eventBytes, Ok after when before = after ->
                    try
                        use head = JsonDocument.Parse headBytes
                        use event = JsonDocument.Parse eventBytes
                        if head.RootElement.GetProperty("generation").GetInt64() <> options.ExpectedEpochGeneration
                           || head.RootElement.GetProperty("eventDigest").GetString()
                              <> (SHA256.HashData eventBytes |> Convert.ToHexString |> _.ToLowerInvariant())
                           || event.RootElement.GetProperty("schema").GetString() <> "fsgg.github-substrate.epoch-event/1"
                           || head.RootElement.GetProperty("aggregateId").GetString() <> epochAddress.CanonicalId
                           || head.RootElement.GetProperty("aggregateDigest").GetString() <> epochAddress.Digest
                           || head.RootElement.GetProperty("journalKind").GetString() <> "cutover"
                           || head.RootElement.GetProperty("shard").GetString() <> epochAddress.Shard
                           || event.RootElement.GetProperty("fleetId").GetString() <> options.EpochFleetId
                           || event.RootElement.GetProperty("phase").GetString() <> "OpenV2" then
                            Error "authority-epoch-moved"
                        else Ok()
                    with _ -> Error "authority-epoch-document"
                | _, _, Ok _ -> Error "authority-epoch-moved"
                | Error reason, _, _
                | _, Error reason, _
                | _, _, Error reason -> Error reason

        let verifyCurrentProtection (address: AggregateAddress) appId =
            let read () =
                match readRuleset options.WriterRulesetId, readRuleset options.IntegrityRulesetId, readEffectiveRules address with
                | Ok writer, Ok integrity, Ok effective -> Ok(writer, integrity, effective)
                | Error reason, _, _
                | _, Error reason, _
                | _, _, Error reason -> Error reason
            match verifyRepositoryScope (), verifyEpochFence (), read (), read () with
            | Error reason, _, _, _
            | _, Error reason, _, _ -> Error reason
            | Ok(), Ok(), Ok(firstWriter, firstIntegrity, firstEffective), Ok(secondWriter, secondIntegrity, secondEffective)
                when firstWriter.GetRawText() = secondWriter.GetRawText()
                     && firstIntegrity.GetRawText() = secondIntegrity.GetRawText()
                     && firstEffective.GetRawText() = secondEffective.GetRawText() ->
                let expectedWriters =
                    Set.ofList ((appId :: options.RetainedWriterAppIds) |> List.map (fun id -> id, "Integration", "always"))
                let writerIncludes, writerExcludes = conditions firstWriter
                let integrityIncludes, integrityExcludes = conditions firstIntegrity
                let expectedEffective =
                    Set
                        [ "creation", options.WriterRulesetId, "Repository", options.Repository
                          "update", options.WriterRulesetId, "Repository", options.Repository
                          "deletion", options.IntegrityRulesetId, "Repository", options.Repository
                          "non_fast_forward", options.IntegrityRulesetId, "Repository", options.Repository ]
                if firstWriter.GetProperty("id").GetInt64() <> options.WriterRulesetId
                   || firstWriter.GetProperty("name").GetString() <> options.WriterRulesetName
                   || firstWriter.GetProperty("enforcement").GetString() <> "active"
                   || not (sameInstant options.WriterUpdatedAt (firstWriter.GetProperty("updated_at").GetString()))
                   || writerIncludes <> options.WriterIncludes
                   || writerExcludes <> options.WriterExcludes
                   || ruleTypes firstWriter <> Set [ "creation"; "update" ]
                   || (bypassAppIds firstWriter |> Option.exists ((<>) expectedWriters))
                   || firstIntegrity.GetProperty("id").GetInt64() <> options.IntegrityRulesetId
                   || firstIntegrity.GetProperty("name").GetString() <> options.IntegrityRulesetName
                   || firstIntegrity.GetProperty("enforcement").GetString() <> "active"
                   || not (sameInstant options.IntegrityUpdatedAt (firstIntegrity.GetProperty("updated_at").GetString()))
                   || integrityIncludes <> options.IntegrityIncludes
                   || integrityExcludes <> options.IntegrityExcludes
                   || ruleTypes firstIntegrity <> Set [ "deletion"; "non_fast_forward" ]
                   || (bypassAppIds firstIntegrity |> Option.exists (Set.isEmpty >> not))
                   || effectiveRuleBindings firstEffective <> expectedEffective
                   || not (address.Ref.StartsWith("refs/heads/fsgg/v2/journal/operation/", StringComparison.Ordinal)) then
                    Error "authority-ruleset-drift"
                else Ok()
            | Ok (), Ok (), Ok _, Ok _ -> Error "authority-rules-moved"
            | Ok (), Ok (), Error reason, _
            | Ok (), Ok (), _, Error reason -> Error reason

        interface IOrdinarySettlementGitAuthorityTransport with
            member _.ReadDocument address =
                match readRef address with
                | Error reason -> SettlementDocumentReadUnknown reason
                | Ok None -> SettlementDocumentAbsent
                | Ok(Some revision) ->
                    match readDocumentAt address revision with
                    | Ok None -> SettlementDocumentKnownEmpty revision
                    | Ok(Some bytes) -> SettlementDocumentPresent(revision, bytes)
                    | Error reason -> SettlementDocumentReadUnknown reason

            member _.CompareExchangeDocument(address, expectedParent, canonicalDocument) =
                match readRef address with
                | Error _ -> SettlementCasUnknown
                | Ok actual when actual <> expectedParent -> SettlementCasConflict
                | Ok _ ->
                    let result =
                        createBlob address canonicalDocument
                        |> Result.bind (fun blob ->
                            match expectedParent with
                            | None -> createTree address None blob
                            | Some parent -> commitTree parent |> Result.bind (fun tree -> createTree address (Some tree) blob))
                        |> Result.bind (createCommit address expectedParent)
                    match result with
                    | Error _ -> SettlementCasUnknown
                    | Ok proposed ->
                        match verifyCurrentProtection address options.ExpectedAppId with
                        | Error _ -> SettlementCasConflict
                        | Ok () ->
                            match updateRef address expectedParent proposed with
                            | Response value when (value.StatusCode = 200 || value.StatusCode = 201) && responseTargets proposed value -> SettlementCasAccepted
                            | Response value when value.StatusCode = 200 || value.StatusCode = 201 -> SettlementCasUnknown
                            | Response value when value.StatusCode = 409 || value.StatusCode = 422 -> SettlementCasConflict
                            | _ -> SettlementCasUnknown

            member _.VerifyCurrentProtection(address, appId) =
                if appId <> options.ExpectedAppId then Error "authority-app-mismatch"
                else verifyCurrentProtection address appId
