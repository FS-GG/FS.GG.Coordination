namespace FS.GG.Coordination.GitHub

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes

type ReviewSnapshot = { Complete: bool; Subject: string; BaseCommit: string; HeadCommit: string; ChangedFiles: string list; RequiredChecks: string list }
type ReviewVerdict = ReviewPending | ReviewPass | ReviewChangesRequired
type ReviewAuthorityRecord = { SchemaVersion: int; ChainId: string; EpochKey: string; SnapshotDigest: string; AccountableAuthority: string; PhaseSeat: string; SeatOrdinal: int64; Verdict: ReviewVerdict; OperationId: string }
type ReviewAuthorityObservation = { Complete: bool; Journal: JournalObservation; Current: ReviewAuthorityRecord }
type ReviewCommitMaterial = { CommitOid: string; TreeOid: string }
type ReviewDeliveryCost = { AuthorityReads: int; MaximumEffects: int }
type ReviewGrant = { Address: AggregateAddress; ChainId: string; EpochKey: string; SnapshotDigest: string; AccountableAuthority: string; PhaseSeat: string; JournalCommit: string; Generation: int64 }
type ReviewPlan = { ProposedAuthority: ReviewAuthorityRecord; Proposal: CasProposal; Grant: ReviewGrant; Seal: string; Cost: ReviewDeliveryCost }
type ReviewRefusal = InvalidReviewSubject | IncompleteReviewSnapshot | InvalidReviewSnapshot of string | InvalidAccountableAuthority | InvalidSeatOrdinal | ReusedPhaseSeat | ReviewObservationIncomplete | ReviewJournalFailure of JournalFailure | ReviewPayloadMismatch | WrongReviewChain | WrongReviewEpoch | WrongReviewSnapshot | WrongReviewSeat | ReviewNotPassed | ReviewEffectRefused of EffectRefusal | InvalidReviewCommitMaterial
type DeliveryState = NotMerged | Merged of mergeCommit: string | ProtectedVerified of mergeCommit: string * runId: int64 * runCommit: string * conclusion: string
type DeliveryReceiptKind = DeliveryGenesis | DeliveryReceipt | DoneReceipt
type DeliveryAuthorityRecord = { SchemaVersion: int; Subject: string; Kind: DeliveryReceiptKind; ReviewChainId: string; ReviewEpochKey: string; ReviewSeat: string; MergeCommit: string; ProtectedRunId: int64 option; ProtectedRunCommit: string option; ProtectedRunConclusion: string option; OperationId: string }
type DeliveryAuthorityObservation = { Complete: bool; Journal: JournalObservation; Current: DeliveryAuthorityRecord }
type DeliveryReceipt = { Address: AggregateAddress; Record: DeliveryAuthorityRecord; JournalCommit: string; Generation: int64; Digest: string }
type DeliveryPlan = { ProposedAuthority: DeliveryAuthorityRecord; Proposal: CasProposal; Receipt: DeliveryReceipt; Seal: string; Cost: ReviewDeliveryCost }
type DeliveryPlanResult = DeliveryPlanned of DeliveryPlan | DeliveryReplayed of DeliveryReceipt
type DeliveryRefusal = ReviewAuthorizationRefused of ReviewRefusal | DeliveryObservationIncomplete | DeliveryJournalFailure of JournalFailure | DeliveryPayloadMismatch | MergeRequired | ProtectedVerificationRequired | InvalidMergeCommit | InvalidProtectedRun | ProtectedRunCommitMismatch | ProtectedRunNotSuccessful | InvalidDeliveryKind | DeliveryReceiptRequired | DeliveryAlreadyCompleted | DeliveryPredecessorMismatch | DivergentDeliveryReplay | InvalidDeliveryCommitMaterial

[<RequireQualifiedAccess>]
module ReviewDeliveryAdapter =
    let private shaText (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private validText (value: string) = not (String.IsNullOrWhiteSpace value) && value = value.Trim()
    let private validOid (value: string) = validText value && (value.Length = 40 || value.Length = 64) && value |> Seq.forall Uri.IsHexDigit
    let private verdictText = function ReviewPending -> "pending" | ReviewPass -> "pass" | ReviewChangesRequired -> "changes-required"
    let private kindText = function DeliveryGenesis -> "genesis" | DeliveryReceipt -> "delivery" | DoneReceipt -> "done"
    let private validDigest (value: string) = validText value && value.Length = 64 && value |> Seq.forall Uri.IsHexDigit

    let chainId (subject: string) =
        if validText subject then Ok("review-chain:" + shaText (subject.ToLowerInvariant())) else Error InvalidReviewSubject

    let snapshotBytes (snapshot: ReviewSnapshot) =
        let valuesValid values = not (obj.ReferenceEquals(values, null)) && values |> List.forall validText && values = (values |> List.distinct |> List.sort)
        let failures =
            [ if obj.ReferenceEquals(snapshot, null) || not snapshot.Complete then yield IncompleteReviewSnapshot
              if not (obj.ReferenceEquals(snapshot, null)) then
                  if chainId snapshot.Subject |> Result.isError then yield InvalidReviewSubject
                  if not (validOid snapshot.BaseCommit) then yield InvalidReviewSnapshot "baseCommit"
                  if not (validOid snapshot.HeadCommit) then yield InvalidReviewSnapshot "headCommit"
                  if not (valuesValid snapshot.ChangedFiles) then yield InvalidReviewSnapshot "changedFiles"
                  if not (valuesValid snapshot.RequiredChecks) then yield InvalidReviewSnapshot "requiredChecks" ]
        if not (List.isEmpty failures) then Error failures else
        let root = JsonObject()
        root.Add("baseCommit", snapshot.BaseCommit.ToLowerInvariant())
        let files = JsonArray()
        snapshot.ChangedFiles |> List.iter (fun value -> files.Add(value))
        root.Add("changedFiles", files)
        root.Add("headCommit", snapshot.HeadCommit.ToLowerInvariant())
        let checks = JsonArray()
        snapshot.RequiredChecks |> List.iter (fun value -> checks.Add(value))
        root.Add("requiredChecks", checks)
        root.Add("subject", snapshot.Subject.ToLowerInvariant())
        root.ToJsonString() |> ShardedJournalAdapter.canonicalJson |> Result.mapError (fun reason -> [ InvalidReviewSnapshot reason ])

    let epochKey (chainValue: string) (snapshot: ReviewSnapshot) =
        match chainId snapshot.Subject, snapshotBytes snapshot with
        | Ok expected, Ok bytes when expected = chainValue -> Ok("review-epoch:" + shaText (chainValue + "|" + ShardedJournalAdapter.sha256 bytes))
        | Ok _, Ok _ -> Error [ WrongReviewChain ]
        | Error failure, _ -> Error [ failure ]
        | _, Error failures -> Error failures

    let phaseSeat (epochValue: string) (ordinal: int64) =
        if not (validText epochValue) || ordinal < 1L then Error InvalidSeatOrdinal
        else Ok("review-seat:" + shaText (epochValue + "|" + ordinal.ToString(CultureInfo.InvariantCulture)))

    let reviewAddress (chainValue: string) =
        if not (validText chainValue) then Error WrongReviewChain
        else ShardedJournalAdapter.address JournalKind.Review chainValue |> Result.mapError ReviewJournalFailure

    let reviewAuthorityBytes (authority: ReviewAuthorityRecord) =
        let failures =
            [ if authority.SchemaVersion <> 1 || not (validText authority.OperationId) then yield ReviewPayloadMismatch
              if not (validText authority.ChainId) then yield WrongReviewChain
              if not (validDigest authority.SnapshotDigest) then yield WrongReviewSnapshot
              if authority.EpochKey <> "review-epoch:" + shaText (authority.ChainId + "|" + authority.SnapshotDigest) then yield WrongReviewEpoch
              if not (validText authority.AccountableAuthority) then yield InvalidAccountableAuthority
              if phaseSeat authority.EpochKey authority.SeatOrdinal <> Ok authority.PhaseSeat then yield WrongReviewSeat ]
        if not (List.isEmpty failures) then Error failures else
        let root = JsonObject()
        root.Add("accountableAuthority", authority.AccountableAuthority.ToLowerInvariant())
        root.Add("chainId", authority.ChainId); root.Add("epochKey", authority.EpochKey)
        root.Add("operationId", authority.OperationId); root.Add("phaseSeat", authority.PhaseSeat)
        root.Add("schemaVersion", authority.SchemaVersion); root.Add("seatOrdinal", authority.SeatOrdinal)
        root.Add("snapshotDigest", authority.SnapshotDigest); root.Add("verdict", verdictText authority.Verdict)
        root.ToJsonString() |> ShardedJournalAdapter.canonicalJson |> Result.mapError (fun _ -> [ ReviewPayloadMismatch ])

    let private validateReview (observation: ReviewAuthorityObservation) =
        if obj.ReferenceEquals(observation, null) || not observation.Complete then Error ReviewObservationIncomplete else
        reviewAddress observation.Current.ChainId
        |> Result.bind (fun address ->
            ShardedJournalAdapter.validate address observation.Journal |> Result.mapError ReviewJournalFailure
            |> Result.bind (fun snapshot ->
                reviewAuthorityBytes observation.Current |> Result.mapError List.head
                |> Result.bind (fun bytes -> if snapshot.Current.Event.Bytes = bytes && snapshot.Current.OperationId = observation.Current.OperationId then Ok snapshot else Error ReviewPayloadMismatch)))

    let private makeCommit (address: AggregateAddress) (generation: int64) (parent: string) (prior: string option) (operationId: string) (bytes: byte array) (material: ReviewCommitMaterial) : JournalCommit =
        let event = { Bytes = bytes; Digest = ShardedJournalAdapter.sha256 bytes }
        let unsigned = { SchemaVersion = 1; Address = address; Generation = generation; EventDigest = event.Digest; SnapshotDigest = None; Terminal = false; PriorHeadDigest = prior; HeadDigest = String.replicate 64 "0" }
        let head = { unsigned with HeadDigest = ShardedJournalAdapter.journalHeadBytes unsigned |> ShardedJournalAdapter.sha256 }
        { CommitOid = material.CommitOid; ParentOid = Some parent; TreeOid = material.TreeOid; OperationId = operationId; Head = head; HeadBytes = ShardedJournalAdapter.journalHeadBytes head; Event = event; Checkpoint = None }

    let planReview (subject: string) (accountableAuthority: string) (seatOrdinal: int64) (verdict: ReviewVerdict) (snapshot: ReviewSnapshot) (observation: ReviewAuthorityObservation) (material: ReviewCommitMaterial) =
        let current = validateReview observation
        let chain = chainId subject
        let bytes = snapshotBytes snapshot
        let epochResult =
            match chain with
            | Ok chainValue -> epochKey chainValue snapshot
            | Error failure -> Error [ failure ]
        let failures =
            [ match current with Error failure -> yield failure | _ -> ()
              match chain with Error failure -> yield failure | _ -> ()
              match bytes with Error values -> yield! values | _ -> ()
              match epochResult with Error values -> yield! values | _ -> ()
              if not (validText accountableAuthority) then yield InvalidAccountableAuthority
              if seatOrdinal < 1L then yield InvalidSeatOrdinal
              if not (validOid material.CommitOid && validOid material.TreeOid) then yield InvalidReviewCommitMaterial ]
        if not (List.isEmpty failures) then Error failures else
        let currentSnapshot = Result.defaultWith (fun _ -> invalidOp "validated") current
        let chainValue = Result.defaultValue "" chain
        let snapshotDigest = Result.defaultValue Array.empty bytes |> ShardedJournalAdapter.sha256
        let epoch = Result.defaultWith (fun _ -> invalidOp "validated") epochResult
        let seat = phaseSeat epoch seatOrdinal |> Result.defaultWith (fun _ -> invalidOp "validated")
        if observation.Current.ChainId <> chainValue then Error [ WrongReviewChain ]
        elif observation.Current.AccountableAuthority.ToLowerInvariant() <> accountableAuthority.ToLowerInvariant() then Error [ InvalidAccountableAuthority ]
        elif observation.Current.ChainId = chainValue && observation.Current.EpochKey = epoch && seatOrdinal <= observation.Current.SeatOrdinal then Error [ ReusedPhaseSeat ]
        else
            let operationId = "review:" + shaText ($"{chainValue}|{epoch}|{seat}|{verdictText verdict}|{currentSnapshot.Current.CommitOid}")
            let authority = { SchemaVersion = 1; ChainId = chainValue; EpochKey = epoch; SnapshotDigest = snapshotDigest; AccountableAuthority = accountableAuthority.ToLowerInvariant(); PhaseSeat = seat; SeatOrdinal = seatOrdinal; Verdict = verdict; OperationId = operationId }
            let authorityBytes = reviewAuthorityBytes authority |> Result.defaultWith (fun _ -> invalidOp "validated")
            let proposed = makeCommit currentSnapshot.Current.Head.Address (currentSnapshot.Current.Head.Generation + 1L) currentSnapshot.Current.CommitOid (Some currentSnapshot.Current.Head.HeadDigest) operationId authorityBytes material
            match ShardedJournalAdapter.planCas operationId currentSnapshot proposed with
            | Error failure -> Error [ ReviewJournalFailure failure ]
            | Ok proposal ->
                let grant = { Address = proposed.Head.Address; ChainId = chainValue; EpochKey = epoch; SnapshotDigest = snapshotDigest; AccountableAuthority = authority.AccountableAuthority; PhaseSeat = seat; JournalCommit = proposed.CommitOid; Generation = proposed.Head.Generation }
                let seal = shaText ($"{operationId}|{proposed.CommitOid}|{proposed.Head.Generation}")
                Ok { ProposedAuthority = authority; Proposal = proposal; Grant = grant; Seal = seal; Cost = { AuthorityReads = 2; MaximumEffects = 1 } }

    let authorizeReview (grant: ReviewGrant) (snapshot: ReviewSnapshot) (observation: ReviewAuthorityObservation) =
        match validateReview observation, chainId snapshot.Subject, snapshotBytes snapshot with
        | Error failure, _, _ -> Error failure
        | _, Error failure, _ -> Error failure
        | _, _, Error failures -> Error(List.head failures)
        | Ok _, Ok chain, Ok bytes when chain <> grant.ChainId || observation.Current.ChainId <> grant.ChainId -> Error WrongReviewChain
        | Ok _, Ok _, Ok bytes ->
            let digest = ShardedJournalAdapter.sha256 bytes
            if digest <> grant.SnapshotDigest || observation.Current.SnapshotDigest <> digest then Error WrongReviewSnapshot
            elif observation.Current.EpochKey <> grant.EpochKey then Error WrongReviewEpoch
            elif observation.Current.PhaseSeat <> grant.PhaseSeat then Error WrongReviewSeat
            elif observation.Current.AccountableAuthority <> grant.AccountableAuthority then Error InvalidAccountableAuthority
            elif observation.Current.Verdict <> ReviewPass then Error ReviewNotPassed
            else ShardedJournalAdapter.authorizeEffect { Address = grant.Address; JournalCommit = grant.JournalCommit; Generation = grant.Generation } observation.Journal |> Result.mapError ReviewEffectRefused

    let deliveryAddress (subject: string) =
        chainId subject |> Result.mapError ReviewAuthorizationRefused
        |> Result.bind (fun chain -> ShardedJournalAdapter.address JournalKind.Operation ("delivery:" + chain) |> Result.mapError DeliveryJournalFailure)

    let deliveryAuthorityBytes (record: DeliveryAuthorityRecord) =
        let failures =
            [ if record.SchemaVersion <> 1 || not (validText record.Subject) || not (validText record.OperationId) then yield DeliveryPayloadMismatch
              match record.Kind with
              | DeliveryGenesis ->
                  if record.ReviewChainId <> "" || record.ReviewEpochKey <> "" || record.ReviewSeat <> "" || record.MergeCommit <> "" || record.ProtectedRunId.IsSome || record.ProtectedRunCommit.IsSome || record.ProtectedRunConclusion.IsSome then yield DeliveryPayloadMismatch
              | DeliveryReceipt ->
                  if not (validText record.ReviewChainId && validText record.ReviewEpochKey && validText record.ReviewSeat) then yield DeliveryPayloadMismatch
                  if not (validOid record.MergeCommit) then yield InvalidMergeCommit
                  if record.ProtectedRunId.IsSome || record.ProtectedRunCommit.IsSome || record.ProtectedRunConclusion.IsSome then yield DeliveryPayloadMismatch
              | DoneReceipt ->
                  if not (validText record.ReviewChainId && validText record.ReviewEpochKey && validText record.ReviewSeat) then yield DeliveryPayloadMismatch
                  if not (validOid record.MergeCommit) then yield InvalidMergeCommit
                  if record.ProtectedRunId.IsNone then yield ProtectedVerificationRequired
                  match record.ProtectedRunCommit with
                  | Some value when validOid value && String.Equals(value, record.MergeCommit, StringComparison.OrdinalIgnoreCase) -> ()
                  | _ -> yield ProtectedRunCommitMismatch
                  match record.ProtectedRunConclusion with
                  | Some value when String.Equals(value, "success", StringComparison.OrdinalIgnoreCase) -> ()
                  | _ -> yield ProtectedRunNotSuccessful ]
        if not (List.isEmpty failures) then Error failures else
        let root = JsonObject()
        root.Add("kind", kindText record.Kind); root.Add("mergeCommit", record.MergeCommit.ToLowerInvariant())
        root.Add("operationId", record.OperationId)
        match record.ProtectedRunId with Some value -> root.Add("protectedRunId", value) | None -> root.Add("protectedRunId", null)
        match record.ProtectedRunCommit with Some value -> root.Add("protectedRunCommit", value.ToLowerInvariant()) | None -> root.Add("protectedRunCommit", null)
        match record.ProtectedRunConclusion with Some value -> root.Add("protectedRunConclusion", value.ToLowerInvariant()) | None -> root.Add("protectedRunConclusion", null)
        root.Add("reviewChainId", record.ReviewChainId); root.Add("reviewEpochKey", record.ReviewEpochKey); root.Add("reviewSeat", record.ReviewSeat)
        root.Add("schemaVersion", record.SchemaVersion); root.Add("subject", record.Subject.ToLowerInvariant())
        root.ToJsonString() |> ShardedJournalAdapter.canonicalJson |> Result.mapError (fun _ -> [ DeliveryPayloadMismatch ])

    let private validateDelivery (observation: DeliveryAuthorityObservation) =
        if obj.ReferenceEquals(observation, null) || not observation.Complete then Error DeliveryObservationIncomplete else
        match deliveryAddress observation.Current.Subject with
        | Error failure -> Error failure
        | Ok address ->
            match ShardedJournalAdapter.validate address observation.Journal with
            | Error failure -> Error(DeliveryJournalFailure failure)
            | Ok snapshot ->
                match deliveryAuthorityBytes observation.Current with
                | Error failures -> Error(List.head failures)
                | Ok bytes when snapshot.Current.Event.Bytes = bytes && snapshot.Current.OperationId = observation.Current.OperationId -> Ok snapshot
                | Ok _ -> Error DeliveryPayloadMismatch

    let planDelivery (kind: DeliveryReceiptKind) (grant: ReviewGrant) (snapshot: ReviewSnapshot) (reviewObservation: ReviewAuthorityObservation) (state: DeliveryState) (operationObservation: DeliveryAuthorityObservation) (material: ReviewCommitMaterial) =
        let review = authorizeReview grant snapshot reviewObservation
        let operation = validateDelivery operationObservation
        let stateResult =
            match kind, state with
            | DeliveryGenesis, _ -> Error InvalidDeliveryKind
            | _, NotMerged -> Error MergeRequired
            | DeliveryReceipt, Merged merge when validOid merge -> Ok(merge.ToLowerInvariant(), None, None, None)
            | DoneReceipt, Merged _ -> Error ProtectedVerificationRequired
            | DeliveryReceipt, ProtectedVerified(merge, runId, runCommit, conclusion) ->
                if not (validOid merge) then Error InvalidMergeCommit
                elif runId < 1L then Error InvalidProtectedRun
                elif not (validOid runCommit) || not (String.Equals(merge, runCommit, StringComparison.OrdinalIgnoreCase)) then Error ProtectedRunCommitMismatch
                elif not (String.Equals(conclusion, "success", StringComparison.OrdinalIgnoreCase)) then Error ProtectedRunNotSuccessful
                else Ok(merge.ToLowerInvariant(), None, None, None)
            | DoneReceipt, ProtectedVerified(merge, runId, runCommit, conclusion) ->
                if not (validOid merge) then Error InvalidMergeCommit
                elif runId < 1L then Error InvalidProtectedRun
                elif not (validOid runCommit) || not (String.Equals(merge, runCommit, StringComparison.OrdinalIgnoreCase)) then Error ProtectedRunCommitMismatch
                elif not (String.Equals(conclusion, "success", StringComparison.OrdinalIgnoreCase)) then Error ProtectedRunNotSuccessful
                else Ok(merge.ToLowerInvariant(), Some runId, Some(runCommit.ToLowerInvariant()), Some(conclusion.ToLowerInvariant()))
            | DeliveryReceipt, Merged _ -> Error InvalidMergeCommit
        let failures =
            [ match review with Error failure -> yield ReviewAuthorizationRefused failure | _ -> ()
              match operation with Error failure -> yield failure | _ -> ()
              match stateResult with Error failure -> yield failure | _ -> () ]
        if not (List.isEmpty failures) then Error failures else
        let journal: JournalSnapshot = Result.defaultWith (fun _ -> invalidOp "validated") operation
        let merge, runId, runCommit, runConclusion = Result.defaultWith (fun _ -> invalidOp "validated") stateResult
        let runCommitValue = defaultArg runCommit ""
        let runConclusionValue = defaultArg runConclusion ""
        let operationId = "delivery:" + shaText ($"{kindText kind}|{grant.ChainId}|{grant.EpochKey}|{grant.PhaseSeat}|{merge}|{defaultArg runId 0L}|{runCommitValue}|{runConclusionValue}")
        let record = { SchemaVersion = 1; Subject = snapshot.Subject.ToLowerInvariant(); Kind = kind; ReviewChainId = grant.ChainId; ReviewEpochKey = grant.EpochKey; ReviewSeat = grant.PhaseSeat; MergeCommit = merge; ProtectedRunId = runId; ProtectedRunCommit = runCommit; ProtectedRunConclusion = runConclusion; OperationId = operationId }
        let bytes = deliveryAuthorityBytes record |> Result.defaultWith (fun _ -> invalidOp "validated")
        let digest = ShardedJournalAdapter.sha256 bytes
        if operationObservation.Current.OperationId = operationId then
            if operationObservation.Current = record then Ok(DeliveryReplayed { Address = journal.Current.Head.Address; Record = record; JournalCommit = journal.Current.CommitOid; Generation = journal.Current.Head.Generation; Digest = digest })
            else Error [ DivergentDeliveryReplay ]
        elif kind = DeliveryReceipt && operationObservation.Current.Kind = DoneReceipt then Error [ DeliveryAlreadyCompleted ]
        elif kind = DeliveryReceipt && operationObservation.Current.Kind <> DeliveryGenesis then Error [ DeliveryPredecessorMismatch ]
        elif kind = DoneReceipt && operationObservation.Current.Kind = DeliveryGenesis then Error [ DeliveryReceiptRequired ]
        elif kind = DoneReceipt && operationObservation.Current.Kind = DoneReceipt then Error [ DeliveryAlreadyCompleted ]
        elif kind = DoneReceipt
             && (operationObservation.Current.ReviewChainId <> record.ReviewChainId
                 || operationObservation.Current.ReviewEpochKey <> record.ReviewEpochKey
                 || operationObservation.Current.ReviewSeat <> record.ReviewSeat
                 || operationObservation.Current.MergeCommit <> record.MergeCommit) then Error [ DeliveryPredecessorMismatch ]
        elif not (validOid material.CommitOid && validOid material.TreeOid) then Error [ InvalidDeliveryCommitMaterial ]
        else
            let proposed: JournalCommit = makeCommit journal.Current.Head.Address (journal.Current.Head.Generation + 1L) journal.Current.CommitOid (Some journal.Current.Head.HeadDigest) operationId bytes material
            match ShardedJournalAdapter.planCas operationId journal proposed with
            | Error failure -> Error [ DeliveryJournalFailure failure ]
            | Ok proposal ->
                let receipt = { Address = proposed.Head.Address; Record = record; JournalCommit = proposed.CommitOid; Generation = proposed.Head.Generation; Digest = digest }
                Ok(DeliveryPlanned { ProposedAuthority = record; Proposal = proposal; Receipt = receipt; Seal = shaText ($"{operationId}|{proposed.CommitOid}|{proposed.Head.Generation}|{digest}"); Cost = { AuthorityReads = 3; MaximumEffects = 1 } })
