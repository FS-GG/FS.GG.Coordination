namespace FS.GG.Coordination.GitHub

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

type JournalKind = Claim | Review | Operation | Cutover
type AggregateAddress = { CanonicalId: string; Digest: string; Shard: string; Kind: JournalKind; Ref: string }
type JournalHead = { SchemaVersion: int; Address: AggregateAddress; Generation: int64; EventDigest: string; SnapshotDigest: string option; Terminal: bool; PriorHeadDigest: string option; HeadDigest: string }
type CanonicalBlob = { Bytes: byte array; Digest: string }
type JournalCheckpoint = { Blob: CanonicalBlob; HighWaterGeneration: int64; EventDigests: string list; AggregateDigest: string; ReplayAggregateDigest: string }
type JournalCommit = { CommitOid: string; ParentOid: string option; TreeOid: string; OperationId: string; Head: JournalHead; HeadBytes: byte array; Event: CanonicalBlob; Checkpoint: JournalCheckpoint option }
type JournalObservation = JournalComplete of revision: string * commits: JournalCommit list | JournalIncomplete of reason: string | JournalUnsupported of reason: string | JournalUnauthorized of reason: string | JournalUnreadable of reason: string | JournalDeleted | JournalDivergent of reason: string
type JournalFailure = InvalidAggregateId | InvalidJournalRef | IncompleteJournal of string | UnsupportedJournal of string | UnauthorizedJournal of string | UnreadableJournal of string | DeletedJournal | DivergentJournal of string | UnknownJournalSchema of int | WrongJournalShard | InvalidDigest of string | MissingJournalParent | DuplicateJournalGeneration of int64 | NonMonotonicJournalGeneration | TerminalJournalAppend | InvalidJournalCommit
type JournalSnapshot = { Revision: string; Current: JournalCommit; Commits: JournalCommit list }
type CasProposal = { Address: AggregateAddress; ObservedObjectId: string; ProposedCommit: JournalCommit; OperationId: string; Refspec: string; ForceWithLease: string }
type ReceivePackOutcome = ReceiveAccepted | ReceiveParentConflict | ReceiveDefiniteRefusal of string | ReceiveResponseUnknown
type ReconcileOutcome = Accepted | ParentConflict | DefiniteRefusal of string | ResponseUnknownRequiresReread
type FencedGrant = { Address: AggregateAddress; JournalCommit: string; Generation: int64 }
type EffectRefusal = StaleFence | TerminalAuthority | EffectAuthorityUnavailable of JournalFailure
type SagaTouch = { Address: AggregateAddress; ExpectedGeneration: int64 }
type SagaPlan = { OperationId: string; PersistBeforeEffects: SagaTouch list; AcquisitionOrder: SagaTouch list }
type Compensation = { OperationId: string; Address: AggregateAddress; Generation: int64; OriginalResultRetained: bool }
type SagaConflictPlan = { ReleaseUnconsumed: SagaTouch list; CompensateApplied: Compensation list }
type EffectiveBranchRule = CreationRestricted | UpdateRestricted | DeletionRejected | NonFastForwardRejected
type Ruleset = { Id: int64; Name: string; Active: bool; Target: string; BypassAppIds: int64 list; RestrictsCreationAndUpdate: bool; RejectsDeletionAndNonFastForward: bool }
type ProtectionObservation = { Complete: bool; RepositoryId: int64; Writer: Ruleset; Integrity: Ruleset; EffectiveRulesComplete: bool; EffectiveRules: EffectiveBranchRule list }
type ProtectionFailure = IncompleteProtectionObservation | AuthorityRepositoryDrift | WriterRulesetDrift | IntegrityRulesetDrift | TargetPatternDrift | BypassDrift | EffectiveRulesDrift

[<RequireQualifiedAccess>]
module ShardedJournalAdapter =
    let journalKind = function Claim -> "claim" | Review -> "review" | Operation -> "operation" | Cutover -> "cutover"
    let sha256 (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
    let private validText (value: string) = not (String.IsNullOrWhiteSpace value) && value = value.Trim()
    let private validOid (value: string) = validText value && (value.Length = 40 || value.Length = 64) && value |> Seq.forall Uri.IsHexDigit
    let private validDigest (value: string) = validText value && value.Length = 64 && value |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

    let address kind aggregateId =
        if not (validText aggregateId) then Error InvalidAggregateId else
        let canonical = aggregateId.ToLowerInvariant()
        let payload = Encoding.UTF8.GetBytes canonical
        let prefix = Encoding.ASCII.GetBytes(payload.Length.ToString(CultureInfo.InvariantCulture) + ":")
        let digest = Array.append prefix payload |> sha256
        let shard = digest.Substring(0, 2)
        let refName = $"refs/heads/fsgg/v2/journal/{journalKind kind}/{shard}"
        Ok { CanonicalId = canonical; Digest = digest; Shard = shard; Kind = kind; Ref = refName }

    let canonicalJson (json: string) =
        let rec write (writer: Utf8JsonWriter) (value: JsonElement) =
            match value.ValueKind with
            | JsonValueKind.Object ->
                writer.WriteStartObject()
                value.EnumerateObject() |> Seq.sortBy _.Name |> Seq.iter (fun property -> writer.WritePropertyName property.Name; write writer property.Value)
                writer.WriteEndObject()
            | JsonValueKind.Array -> writer.WriteStartArray(); value.EnumerateArray() |> Seq.iter (write writer); writer.WriteEndArray()
            | JsonValueKind.String -> writer.WriteStringValue(value.GetString())
            | JsonValueKind.Number ->
                let raw = value.GetRawText()
                match Int64.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, number -> writer.WriteNumberValue number
                | _ -> writer.WriteRawValue(raw, true)
            | JsonValueKind.True -> writer.WriteBooleanValue true
            | JsonValueKind.False -> writer.WriteBooleanValue false
            | JsonValueKind.Null -> writer.WriteNullValue()
            | _ -> invalidArg "json" "unsupported JSON token"
        try
            use document = JsonDocument.Parse json
            use stream = new IO.MemoryStream()
            use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))
            write writer document.RootElement
            writer.Flush()
            Ok(Array.append (stream.ToArray()) [| byte '\n' |])
        with ex -> Error ex.Message

    let journalHeadBytes (head: JournalHead) =
        let root = JsonObject()
        root.Add("aggregateDigest", head.Address.Digest)
        root.Add("aggregateId", head.Address.CanonicalId)
        root.Add("eventDigest", head.EventDigest)
        root.Add("generation", head.Generation)
        root.Add("journalKind", journalKind head.Address.Kind)
        match head.PriorHeadDigest with Some value -> root.Add("priorHeadDigest", value) | None -> root.Add("priorHeadDigest", null)
        root.Add("schemaVersion", head.SchemaVersion)
        root.Add("shard", head.Address.Shard)
        match head.SnapshotDigest with Some value -> root.Add("snapshotDigest", value) | None -> root.Add("snapshotDigest", null)
        root.Add("terminal", head.Terminal)
        root.ToJsonString(JsonSerializerOptions(WriteIndented = false)) |> canonicalJson |> Result.defaultWith invalidOp

    let private validBlob (blob: CanonicalBlob) =
        not (obj.ReferenceEquals(blob, null))
        && not (obj.ReferenceEquals(blob.Bytes, null))
        && validDigest blob.Digest
        && sha256 blob.Bytes = blob.Digest
        && (try Encoding.UTF8.GetString blob.Bytes |> canonicalJson = Ok blob.Bytes with _ -> false)

    let private validateCommit address index previous eventDigests (commit: JournalCommit) =
        let head = commit.Head
        if obj.ReferenceEquals(commit, null) || obj.ReferenceEquals(head, null) || not (validOid commit.CommitOid) || not (validOid commit.TreeOid) || not (validText commit.OperationId) then Error InvalidJournalCommit
        elif head.SchemaVersion <> 1 then Error(UnknownJournalSchema head.SchemaVersion)
        elif head.Address <> address || head.Address.Shard <> address.Shard then Error WrongJournalShard
        elif not (validDigest head.EventDigest) || not (validDigest head.HeadDigest) || (head.SnapshotDigest |> Option.exists (validDigest >> not)) || (head.PriorHeadDigest |> Option.exists (validDigest >> not)) then Error(InvalidDigest head.HeadDigest)
        elif not (validBlob commit.Event) || commit.Event.Digest <> head.EventDigest then Error(InvalidDigest head.EventDigest)
        elif obj.ReferenceEquals(commit.HeadBytes, null) || commit.HeadBytes <> journalHeadBytes head || sha256 commit.HeadBytes <> head.HeadDigest then Error(InvalidDigest head.HeadDigest)
        elif
            match head.SnapshotDigest, commit.Checkpoint with
            | None, None -> false
            | Some expected, Some checkpoint ->
                not (validBlob checkpoint.Blob)
                || checkpoint.Blob.Digest <> expected
                || checkpoint.HighWaterGeneration <> head.Generation
                || checkpoint.EventDigests <> eventDigests @ [ head.EventDigest ]
                || not (validDigest checkpoint.AggregateDigest)
                || checkpoint.AggregateDigest <> checkpoint.ReplayAggregateDigest
            | _ -> true
            then Error(InvalidDigest(head.SnapshotDigest |> Option.defaultValue "missing-checkpoint"))
        elif head.Generation <> int64 (index + 1) then Error NonMonotonicJournalGeneration
        else
            match previous, commit.ParentOid with
            | None, None when index = 0 -> Ok ()
            | Some prior, Some parent when parent = prior.CommitOid && head.PriorHeadDigest = Some prior.Head.HeadDigest && not prior.Head.Terminal -> Ok ()
            | Some prior, _ when prior.Head.Terminal -> Error TerminalJournalAppend
            | _ -> Error MissingJournalParent

    let validate address observation =
        match observation with
        | JournalIncomplete reason -> Error(IncompleteJournal reason)
        | JournalUnsupported reason -> Error(UnsupportedJournal reason)
        | JournalUnauthorized reason -> Error(UnauthorizedJournal reason)
        | JournalUnreadable reason -> Error(UnreadableJournal reason)
        | JournalDeleted -> Error DeletedJournal
        | JournalDivergent reason -> Error(DivergentJournal reason)
        | JournalComplete(revision, commits) when not (validText revision) || obj.ReferenceEquals(commits, null) || List.isEmpty commits -> Error MissingJournalParent
        | JournalComplete(revision, commits) ->
            match commits |> List.groupBy (fun commit -> commit.Head.Generation) |> List.tryFind (fun (_, values) -> values.Length > 1) with
            | Some(generation, _) -> Error(DuplicateJournalGeneration generation)
            | None ->
                let folder state (index, commit) =
                    state |> Result.bind (fun (previous, eventDigests) ->
                        validateCommit address index previous eventDigests commit
                        |> Result.map (fun () -> Some commit, eventDigests @ [ commit.Head.EventDigest ]))
                match commits |> List.indexed |> List.fold folder (Ok(None, [])) with
                | Error failure -> Error failure
                | Ok _ -> Ok { Revision = revision; Current = List.last commits; Commits = commits }

    let planCas (operationId: string) (snapshot: JournalSnapshot) (proposed: JournalCommit) =
        if not (validText operationId) || proposed.OperationId <> operationId || proposed.ParentOid <> Some snapshot.Current.CommitOid || proposed.Head.Generation <> snapshot.Current.Head.Generation + 1L || proposed.Head.Address <> snapshot.Current.Head.Address then Error InvalidJournalCommit
        elif snapshot.Current.Head.Terminal then Error TerminalJournalAppend
        else
            let refName = snapshot.Current.Head.Address.Ref
            Ok { Address = snapshot.Current.Head.Address; ObservedObjectId = snapshot.Current.CommitOid; ProposedCommit = proposed; OperationId = operationId; Refspec = $"{proposed.CommitOid}:{refName}"; ForceWithLease = $"--force-with-lease={refName}:{snapshot.Current.CommitOid}" }

    let private exactProposal (proposal: CasProposal) (observation: JournalObservation) =
        match validate proposal.Address observation with
        | Ok snapshot ->
            let current = snapshot.Current
            current.CommitOid = proposal.ProposedCommit.CommitOid && current.TreeOid = proposal.ProposedCommit.TreeOid && current.OperationId = proposal.OperationId && current.Head.HeadDigest = proposal.ProposedCommit.Head.HeadDigest && current.Head.Generation = proposal.ProposedCommit.Head.Generation
        | Error _ -> false

    let reconcile (proposal: CasProposal) (outcome: ReceivePackOutcome) (reread: JournalObservation) =
        match outcome with
        | ReceiveParentConflict -> ParentConflict
        | ReceiveDefiniteRefusal reason -> DefiniteRefusal reason
        | ReceiveAccepted when exactProposal proposal reread -> Accepted
        | ReceiveAccepted -> ResponseUnknownRequiresReread
        | ReceiveResponseUnknown when exactProposal proposal reread -> Accepted
        | ReceiveResponseUnknown -> ResponseUnknownRequiresReread

    let authorizeEffect (grant: FencedGrant) (observation: JournalObservation) =
        match validate grant.Address observation with
        | Error failure -> Error(EffectAuthorityUnavailable failure)
        | Ok snapshot when snapshot.Current.Head.Terminal -> Error TerminalAuthority
        | Ok snapshot when snapshot.Current.CommitOid <> grant.JournalCommit || snapshot.Current.Head.Generation <> grant.Generation || snapshot.Current.Head.Address <> grant.Address -> Error StaleFence
        | Ok snapshot -> Ok snapshot.Current

    let private touchKey (touch: SagaTouch) = journalKind touch.Address.Kind, touch.Address.Shard, touch.Address.Digest
    let planSaga (operationId: string) (touches: SagaTouch list) =
        if not (validText operationId) || obj.ReferenceEquals(touches, null) || List.isEmpty touches || touches |> List.exists (fun touch -> touch.ExpectedGeneration < 0L) then Error "invalid saga"
        elif touches |> List.groupBy (fun touch -> touch.Address.Kind, touch.Address.Digest) |> List.exists (fun (_, values) -> values.Length > 1) then Error "duplicate aggregate"
        else let ordered = List.sortBy touchKey touches in Ok { OperationId = operationId; PersistBeforeEffects = ordered; AcquisitionOrder = ordered }
    let planConflict (plan: SagaPlan) (acquired: SagaTouch list) (applied: SagaTouch list) =
        let prefix length values = plan.AcquisitionOrder |> List.truncate length = values
        if obj.ReferenceEquals(plan, null) || not (prefix acquired.Length acquired) || not (prefix applied.Length applied) || applied.Length > acquired.Length then Error "conflict state is not a persisted acquisition prefix"
        else
            let release = plan.AcquisitionOrder |> List.skip acquired.Length
            let compensation =
                applied
                |> List.rev
                |> List.map (fun touch ->
                    { OperationId = $"{plan.OperationId}:compensate:{journalKind touch.Address.Kind}:{touch.Address.Digest}:{touch.ExpectedGeneration}"
                      Address = touch.Address
                      Generation = touch.ExpectedGeneration
                      OriginalResultRetained = true })
            Ok { ReleaseUnconsumed = release; CompensateApplied = compensation }

    let validateProtection (observation: ProtectionObservation) =
        if obj.ReferenceEquals(observation, null) || not observation.Complete then Error IncompleteProtectionObservation
        elif observation.RepositoryId <> 1351660651L then Error AuthorityRepositoryDrift
        elif observation.Writer.Id <> 21872113L || observation.Writer.Name <> "v2-journal-writer" || not observation.Writer.Active || not observation.Writer.RestrictsCreationAndUpdate then Error WriterRulesetDrift
        elif observation.Integrity.Id <> 21872115L || observation.Integrity.Name <> "v2-journal-integrity" || not observation.Integrity.Active || not observation.Integrity.RejectsDeletionAndNonFastForward then Error IntegrityRulesetDrift
        elif observation.Writer.Target <> "refs/heads/fsgg/v2/journal/**/*" || observation.Integrity.Target <> "refs/heads/fsgg/v2/journal/**/*" then Error TargetPatternDrift
        elif observation.Writer.BypassAppIds <> [ 4166418L ] || not (List.isEmpty observation.Integrity.BypassAppIds) then Error BypassDrift
        elif not observation.EffectiveRulesComplete || observation.EffectiveRules <> [ CreationRestricted; UpdateRestricted; DeletionRejected; NonFastForwardRejected ] then Error EffectiveRulesDrift
        else Ok ()
