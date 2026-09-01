namespace FS.GG.Coordination.GitHub

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes

type ClaimTouch = { Repository: string; Path: string }
type ClaimAuthorityRecord = { SchemaVersion: int; Subject: string; Owner: string; Touches: ClaimTouch list; LeaseExpiresAt: int64; OperationId: string }
type ClaimAuthorityObservation = { Complete: bool; Journal: JournalObservation; Current: ClaimAuthorityRecord }
type ClaimProjectionHints = { FieldOwner: string option; CommentOwner: string option; LeaseLooksActive: bool; WebhookSequence: int64 option }
type ClaimAcquireIntent = { Subject: string; Owner: string; Touches: ClaimTouch list; Now: int64; LeaseExpiresAt: int64 }
type ClaimCommitMaterial = { CommitOid: string; TreeOid: string }
type ClaimCost = { AuthorityReads: int; MaximumEffects: int }
type ClaimGrant = { Address: AggregateAddress; Subject: string; Owner: string; Touches: ClaimTouch list; JournalCommit: string; Generation: int64 }
type ClaimAcquirePlan = { OperationId: string; ProposedAuthority: ClaimAuthorityRecord; Proposal: CasProposal; Grant: ClaimGrant; Seal: string; Cost: ClaimCost }
type SuccessorEligibility = CurrentOwner | EligibleAfterExpiry | BlockedUntil of int64
type ClaimRefusal =
    | InvalidSubject
    | InvalidOwner
    | InvalidTouch of string
    | DuplicateTouch of string
    | IncompleteClaimObservation
    | ClaimJournalFailure of JournalFailure
    | ClaimPayloadMismatch
    | ActiveForeignClaim of owner: string * leaseExpiresAt: int64
    | InvalidLease
    | InvalidCommitMaterial
    | AlteredClaimPlan
    | PersistedPlanMissing
    | WrongClaimOwner
    | WrongClaimTouches
    | ClaimEffectRefused of EffectRefusal
    | MissingDomainProof of string
    | DuplicateDomainProof of string
    | WrongDomainGeneration of touch: string * expected: int64 * observed: int64
    | ClaimDomainEffectRefused of touch: string * refusal: EffectRefusal
type ClaimAcquireResult = ClaimAcquired of ClaimGrant | ClaimParentConflict | ClaimDefiniteRefusal of string | ClaimResponseUnknownRequiresReread | ClaimAcquireRefused of ClaimRefusal
type ClaimDomainObservation = { Touch: ClaimTouch; ExpectedGeneration: int64; ActiveGrant: ClaimGrant option }
type ClaimDomainExpectation = { Touch: ClaimTouch; Address: AggregateAddress; ExpectedGeneration: int64 }
type ClaimDomainEffectProof = { Touch: ClaimTouch; JournalCommit: string; Generation: int64 }
type ClaimMultiTouchPlan = { OperationId: string; Owner: string; Touches: ClaimTouch list; Domains: ClaimDomainExpectation list; Saga: SagaPlan; Seal: string; Cost: ClaimCost }
type ClaimPersistedPlan = { OperationId: string; PlanSeal: string; Touches: ClaimTouch list; ExpectedGenerations: int64 list; Domains: ClaimDomainExpectation list }

[<RequireQualifiedAccess>]
module ClaimTouchSetAdapter =
    let private sha256Text (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private validIdentity (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private normalizeTouch (touch: ClaimTouch) =
        if obj.ReferenceEquals(touch, null) || not (validIdentity touch.Repository) || not (validIdentity touch.Path) then
            Error(InvalidTouch(if obj.ReferenceEquals(touch, null) then "null" else touch.Path))
        else
            let repository = touch.Repository.ToLowerInvariant()
            let path = touch.Path
            let parts = path.Split('/', StringSplitOptions.None)
            let unsafeSyntax =
                path.StartsWith("/", StringComparison.Ordinal)
                || path.EndsWith("/", StringComparison.Ordinal)
                || path.Contains('\\')
                || path.IndexOfAny([| '*'; '?'; '['; ']' |]) >= 0
                || parts.Length = 0
                || parts |> Array.exists (fun part -> part = "" || part = "." || part = ".." || part <> part.Trim())
                || path = "."

            if unsafeSyntax then Error(InvalidTouch path) else Ok { Repository = repository; Path = path }

    let normalizeTouches (touches: ClaimTouch list) =
        if obj.ReferenceEquals(touches, null) || List.isEmpty touches then
            Error [ InvalidTouch "empty" ]
        else
            let normalized = touches |> List.map normalizeTouch
            let failures = normalized |> List.choose (function Error failure -> Some failure | Ok _ -> None)
            let values = normalized |> List.choose Result.toOption |> List.sortBy (fun touch -> touch.Repository, touch.Path)
            let duplicates =
                values
                |> List.countBy (fun touch -> touch.Repository, touch.Path)
                |> List.choose (fun ((repository, path), count) -> if count > 1 then Some(DuplicateTouch $"{repository}:{path}") else None)
            let allFailures = failures @ duplicates
            if List.isEmpty allFailures then Ok values else Error allFailures

    let private pathContains (parent: string) (child: string) =
        child = parent || child.StartsWith(parent + "/", StringComparison.Ordinal)

    let touchesConflict (left: ClaimTouch list) (right: ClaimTouch list) =
        match normalizeTouches left, normalizeTouches right with
        | Ok canonicalLeft, Ok canonicalRight ->
            canonicalLeft
            |> List.exists (fun a ->
                canonicalRight
                |> List.exists (fun b ->
                    a.Repository = b.Repository && (pathContains a.Path b.Path || pathContains b.Path a.Path)))
        | _ -> false

    let private canonicalSubject (subject: string) =
        if validIdentity subject then Ok(subject.ToLowerInvariant()) else Error InvalidSubject

    let private canonicalOwner (owner: string) =
        if validIdentity owner then Ok(owner.ToLowerInvariant()) else Error InvalidOwner

    let claimAddress (subject: string) =
        canonicalSubject subject
        |> Result.bind (fun canonical ->
            ShardedJournalAdapter.address JournalKind.Claim $"claim:{canonical}"
            |> Result.mapError ClaimJournalFailure)

    let conflictAddress (touch: ClaimTouch) =
        normalizeTouch touch
        |> Result.bind (fun canonical ->
            ShardedJournalAdapter.address JournalKind.Claim $"conflict:{canonical.Repository}:{canonical.Path}"
            |> Result.mapError ClaimJournalFailure)

    let authorityBytes (authority: ClaimAuthorityRecord) =
        let failures =
            [ if authority.SchemaVersion <> 1 then yield ClaimPayloadMismatch
              if canonicalSubject authority.Subject |> Result.isError then yield InvalidSubject
              if canonicalOwner authority.Owner |> Result.isError then yield InvalidOwner
              if authority.LeaseExpiresAt < 0L then yield InvalidLease
              if not (validIdentity authority.OperationId) then yield ClaimPayloadMismatch ]
        match normalizeTouches authority.Touches with
        | Error touchFailures -> Error(failures @ touchFailures)
        | Ok touches when not (List.isEmpty failures) -> Error failures
        | Ok touches ->
            let root = JsonObject()
            root.Add("leaseExpiresAt", authority.LeaseExpiresAt)
            root.Add("operationId", authority.OperationId)
            root.Add("owner", authority.Owner.ToLowerInvariant())
            root.Add("schemaVersion", authority.SchemaVersion)
            root.Add("subject", authority.Subject.ToLowerInvariant())
            let values = JsonArray()
            for touch in touches do
                let value = JsonObject()
                value.Add("path", touch.Path)
                value.Add("repository", touch.Repository)
                values.Add(value)
            root.Add("touches", values)
            root.ToJsonString() |> ShardedJournalAdapter.canonicalJson |> Result.mapError (fun _ -> [ ClaimPayloadMismatch ])

    let private validateObservation (observation: ClaimAuthorityObservation) =
        if obj.ReferenceEquals(observation, null) || not observation.Complete then
            Error IncompleteClaimObservation
        else
            claimAddress observation.Current.Subject
            |> Result.bind (fun address ->
                ShardedJournalAdapter.validate address observation.Journal
                |> Result.mapError ClaimJournalFailure
                |> Result.bind (fun snapshot ->
                    authorityBytes observation.Current
                    |> Result.mapError List.head
                    |> Result.bind (fun bytes ->
                        if snapshot.Current.Event.Bytes = bytes
                           && snapshot.Current.OperationId = observation.Current.OperationId
                           && snapshot.Current.Head.Address = address then Ok snapshot
                        else Error ClaimPayloadMismatch)))

    let successorEligibility (now: int64) (candidateOwner: string) (observation: ClaimAuthorityObservation) =
        match canonicalOwner candidateOwner, validateObservation observation with
        | Error failure, _ -> Error failure
        | _, Error failure -> Error failure
        | Ok candidate, Ok _ when candidate = observation.Current.Owner.ToLowerInvariant() -> Ok CurrentOwner
        | Ok _, Ok _ when now >= observation.Current.LeaseExpiresAt -> Ok EligibleAfterExpiry
        | Ok _, Ok _ -> Ok(BlockedUntil observation.Current.LeaseExpiresAt)

    let private validOid (value: string) =
        validIdentity value && (value.Length = 40 || value.Length = 64) && value |> Seq.forall Uri.IsHexDigit

    let private sealPlan (operationId: string) (authority: ClaimAuthorityRecord) (proposal: CasProposal) =
        let touches = authority.Touches |> List.map (fun touch -> $"{touch.Repository}:{touch.Path}") |> String.concat "|"
        sha256Text $"{operationId}|{authority.Subject}|{authority.Owner}|{touches}|{authority.LeaseExpiresAt}|{proposal.ObservedObjectId}|{proposal.ProposedCommit.CommitOid}|{proposal.ProposedCommit.Head.Generation}"

    let planAcquire (intent: ClaimAcquireIntent) (observation: ClaimAuthorityObservation) (material: ClaimCommitMaterial) =
        let normalizedSubject = canonicalSubject intent.Subject
        let normalizedOwner = canonicalOwner intent.Owner
        let normalizedTouches = normalizeTouches intent.Touches
        let snapshot = validateObservation observation
        let failures =
            [ match normalizedSubject with Error failure -> yield failure | _ -> ()
              match normalizedOwner with Error failure -> yield failure | _ -> ()
              match normalizedTouches with Error values -> yield! values | _ -> ()
              match snapshot with Error failure -> yield failure | _ -> ()
              if intent.LeaseExpiresAt <= intent.Now then yield InvalidLease
              if not (validOid material.CommitOid && validOid material.TreeOid) then yield InvalidCommitMaterial ]
        if not (List.isEmpty failures) then Error failures else
        let subject = Result.defaultValue "" normalizedSubject
        let owner = Result.defaultValue "" normalizedOwner
        let touches = Result.defaultValue [] normalizedTouches
        let current = observation.Current
        if current.Subject.ToLowerInvariant() <> subject then Error [ ClaimPayloadMismatch ]
        elif owner <> current.Owner.ToLowerInvariant() && intent.Now < current.LeaseExpiresAt then
            Error [ ActiveForeignClaim(current.Owner, current.LeaseExpiresAt) ]
        else
            let journal = Result.defaultWith (fun _ -> invalidOp "validated above") snapshot
            let touchIdentity =
                touches
                |> List.map (fun touch -> touch.Repository + ":" + touch.Path)
                |> String.concat "|"
            let identityInput =
                $"{subject}|{owner}|{intent.LeaseExpiresAt.ToString(CultureInfo.InvariantCulture)}|{journal.Current.CommitOid}|{touchIdentity}"
            let operationId = "claim:" + sha256Text identityInput
            let authority = { SchemaVersion = 1; Subject = subject; Owner = owner; Touches = touches; LeaseExpiresAt = intent.LeaseExpiresAt; OperationId = operationId }
            match authorityBytes authority with
            | Error values -> Error values
            | Ok bytes ->
                let event = { Bytes = bytes; Digest = ShardedJournalAdapter.sha256 bytes }
                let provisionalHead =
                    { SchemaVersion = 1
                      Address = journal.Current.Head.Address
                      Generation = journal.Current.Head.Generation + 1L
                      EventDigest = event.Digest
                      SnapshotDigest = None
                      Terminal = false
                      PriorHeadDigest = Some journal.Current.Head.HeadDigest
                      HeadDigest = String.replicate 64 "0" }
                let headBytes = ShardedJournalAdapter.journalHeadBytes provisionalHead
                let head = { provisionalHead with HeadDigest = ShardedJournalAdapter.sha256 headBytes }
                let proposed =
                    { CommitOid = material.CommitOid
                      ParentOid = Some journal.Current.CommitOid
                      TreeOid = material.TreeOid
                      OperationId = operationId
                      Head = head
                      HeadBytes = ShardedJournalAdapter.journalHeadBytes head
                      Event = event
                      Checkpoint = None }
                match ShardedJournalAdapter.planCas operationId journal proposed with
                | Error failure -> Error [ ClaimJournalFailure failure ]
                | Ok proposal ->
                    let grant = { Address = head.Address; Subject = subject; Owner = owner; Touches = touches; JournalCommit = material.CommitOid; Generation = head.Generation }
                    Ok { OperationId = operationId; ProposedAuthority = authority; Proposal = proposal; Grant = grant; Seal = sealPlan operationId authority proposal; Cost = { AuthorityReads = 2; MaximumEffects = 1 } }

    let confirmAcquire (plan: ClaimAcquirePlan) (outcome: ReceivePackOutcome) (reread: ClaimAuthorityObservation) =
        let expectedSeal = sealPlan plan.OperationId plan.ProposedAuthority plan.Proposal
        if expectedSeal <> plan.Seal then ClaimAcquireRefused AlteredClaimPlan else
        match ShardedJournalAdapter.reconcile plan.Proposal outcome reread.Journal with
        | ReconcileOutcome.ParentConflict -> ClaimParentConflict
        | ReconcileOutcome.DefiniteRefusal reason -> ClaimDefiniteRefusal reason
        | ReconcileOutcome.ResponseUnknownRequiresReread -> ClaimResponseUnknownRequiresReread
        | ReconcileOutcome.Accepted ->
            match validateObservation reread with
            | Error failure -> ClaimAcquireRefused failure
            | Ok _ when reread.Current <> plan.ProposedAuthority -> ClaimAcquireRefused ClaimPayloadMismatch
            | Ok _ -> ClaimAcquired plan.Grant

    let authorizeEffect (grant: ClaimGrant) (observation: ClaimAuthorityObservation) =
        match validateObservation observation with
        | Error failure -> Error failure
        | Ok _ when observation.Current.Subject.ToLowerInvariant() <> grant.Subject -> Error ClaimPayloadMismatch
        | Ok _ when observation.Current.Owner.ToLowerInvariant() <> grant.Owner -> Error WrongClaimOwner
        | Ok _ ->
            match normalizeTouches observation.Current.Touches, normalizeTouches grant.Touches with
            | Ok observed, Ok expected when observed <> expected -> Error WrongClaimTouches
            | Error _, _ | _, Error _ -> Error WrongClaimTouches
            | _ ->
                ShardedJournalAdapter.authorizeEffect
                    { Address = grant.Address; JournalCommit = grant.JournalCommit; Generation = grant.Generation }
                    observation.Journal
                |> Result.mapError ClaimEffectRefused

    let private domainKey (touch: ClaimTouch) = touch.Repository + ":" + touch.Path

    let private multiSeal (operationId: string) (owner: string) (domains: ClaimDomainExpectation list) =
        let domainText =
            domains
            |> List.map (fun domain ->
                $"{domainKey domain.Touch}:{ShardedJournalAdapter.journalKind domain.Address.Kind}:{domain.Address.Shard}:{domain.Address.Digest}:{domain.ExpectedGeneration.ToString(CultureInfo.InvariantCulture)}")
            |> String.concat "|"
        sha256Text $"{operationId}|{owner}|{domainText}"

    let planMultiTouch (operationId: string) (owner: string) (touches: ClaimTouch list) (domains: ClaimDomainObservation list) =
        let canonicalOwnerResult = canonicalOwner owner
        let canonicalTouchesResult = normalizeTouches touches
        let failures =
            [ if not (validIdentity operationId) then yield AlteredClaimPlan
              match canonicalOwnerResult with Error failure -> yield failure | _ -> ()
              match canonicalTouchesResult with Error values -> yield! values | _ -> () ]
        if not (List.isEmpty failures) then Error failures else
        let canonicalOwner = Result.defaultValue "" canonicalOwnerResult
        let canonicalTouches = Result.defaultValue [] canonicalTouchesResult
        let normalizedDomains =
            domains
            |> List.map (fun domain -> normalizeTouch domain.Touch |> Result.map (fun touch -> touch, domain.ExpectedGeneration, domain.ActiveGrant))
        let domainFailures = normalizedDomains |> List.choose (function Error failure -> Some failure | _ -> None)
        let domainValues = normalizedDomains |> List.choose Result.toOption |> List.sortBy (fun (touch, _, _) -> touch.Repository, touch.Path)
        let domainTouches = domainValues |> List.map (fun (touch, _, _) -> touch
        )
        let generationFailures = domainValues |> List.choose (fun (_, generation, _) -> if generation < 1L then Some AlteredClaimPlan else None)
        let conflictFailures =
            domainValues
            |> List.choose (fun (_, _, active) ->
                match active with
                | Some grant when grant.Owner <> canonicalOwner && touchesConflict canonicalTouches grant.Touches ->
                    Some(ActiveForeignClaim(grant.Owner, Int64.MaxValue))
                | _ -> None)
        let allFailures = domainFailures @ generationFailures @ conflictFailures
        if domainTouches <> canonicalTouches then Error(AlteredClaimPlan :: allFailures)
        elif not (List.isEmpty allFailures) then Error allFailures
        else
            let sagaTouchesResult =
                domainValues
                |> List.map (fun (touch, generation, _) ->
                    conflictAddress touch |> Result.map (fun address -> { Address = address; ExpectedGeneration = generation }))
            let addressFailures = sagaTouchesResult |> List.choose (function Error failure -> Some failure | _ -> None)
            if not (List.isEmpty addressFailures) then Error addressFailures else
            let sagaTouches = sagaTouchesResult |> List.choose Result.toOption
            match ShardedJournalAdapter.planSaga operationId sagaTouches with
            | Error _ -> Error [ AlteredClaimPlan ]
            | Ok saga ->
                let touchByAddress = List.zip sagaTouches domainTouches |> Map.ofList
                let expectations =
                    saga.AcquisitionOrder
                    |> List.map (fun sagaTouch ->
                        { Touch = touchByAddress[sagaTouch]
                          Address = sagaTouch.Address
                          ExpectedGeneration = sagaTouch.ExpectedGeneration })
                Ok { OperationId = operationId; Owner = canonicalOwner; Touches = canonicalTouches; Domains = expectations; Saga = saga; Seal = multiSeal operationId canonicalOwner expectations; Cost = { AuthorityReads = canonicalTouches.Length + 1; MaximumEffects = canonicalTouches.Length * 2 + 1 } }

    let persistPlan (plan: ClaimMultiTouchPlan) =
        { OperationId = plan.OperationId
          PlanSeal = plan.Seal
          Touches = plan.Touches
          ExpectedGenerations = plan.Saga.AcquisitionOrder |> List.map _.ExpectedGeneration
          Domains = plan.Domains }

    let authorizeMultiTouchEffects (plan: ClaimMultiTouchPlan) (persisted: ClaimPersistedPlan option) (primary: ClaimGrant * ClaimAuthorityObservation) (domains: (ClaimDomainEffectProof * JournalObservation) list) =
        let primaryGrant, primaryObservation = primary
        let domainProofs = domains
        match persisted with
        | None -> Error [ PersistedPlanMissing ]
        | Some receipt ->
            let expected = persistPlan plan
            if receipt <> expected || multiSeal plan.OperationId plan.Owner receipt.Domains <> plan.Seal then
                Error [ AlteredClaimPlan ]
            else
                let primaryResult =
                    match normalizeTouches primaryGrant.Touches with
                    | Error _ -> Error WrongClaimTouches
                    | Ok touches when primaryGrant.Owner <> plan.Owner -> Error WrongClaimOwner
                    | Ok touches when touches <> plan.Touches -> Error WrongClaimTouches
                    | Ok _ -> authorizeEffect primaryGrant primaryObservation

                let normalizedProofs =
                    domainProofs
                    |> List.map (fun (proof, observation) ->
                        normalizeTouch proof.Touch
                        |> Result.map (fun touch -> touch, proof, observation))
                let normalizationFailures = normalizedProofs |> List.choose (function Error failure -> Some failure | _ -> None)
                let proofValues = normalizedProofs |> List.choose Result.toOption
                let duplicateFailures =
                    proofValues
                    |> List.countBy (fun (touch, _, _) -> domainKey touch)
                    |> List.choose (fun (key, count) -> if count > 1 then Some(DuplicateDomainProof key) else None)
                let domainResults =
                    plan.Domains
                    |> List.map (fun expectedDomain ->
                        let key = domainKey expectedDomain.Touch
                        match proofValues |> List.filter (fun (touch, _, _) -> touch = expectedDomain.Touch) with
                        | [] -> Error(MissingDomainProof key)
                        | [ _, proof, observation ] when proof.Generation <> expectedDomain.ExpectedGeneration ->
                            Error(WrongDomainGeneration(key, expectedDomain.ExpectedGeneration, proof.Generation))
                        | [ _, proof, observation ] ->
                            ShardedJournalAdapter.authorizeEffect
                                { Address = expectedDomain.Address
                                  JournalCommit = proof.JournalCommit
                                  Generation = proof.Generation }
                                observation
                            |> Result.mapError (fun refusal -> ClaimDomainEffectRefused(key, refusal))
                        | _ -> Error(DuplicateDomainProof key))
                let extraFailures =
                    proofValues
                    |> List.choose (fun (touch, _, _) ->
                        if plan.Domains |> List.exists (fun expectedDomain -> expectedDomain.Touch = touch) then None
                        else Some(WrongClaimTouches))
                let results = primaryResult :: domainResults
                let failures = normalizationFailures @ duplicateFailures @ extraFailures @ (results |> List.choose (function Error failure -> Some failure | _ -> None))
                if List.isEmpty failures then Ok(results |> List.choose Result.toOption) else Error failures

    let planConflict (plan: ClaimMultiTouchPlan) (acquired: SagaTouch list) (applied: SagaTouch list) =
        ShardedJournalAdapter.planConflict plan.Saga acquired applied
