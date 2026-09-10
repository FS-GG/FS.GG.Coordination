namespace FS.GG.Coordination.Core

open System
open System.Security.Cryptography
open System.Text
open System.Globalization

/// Runtime state refining the generated protocol's command/event/effect vocabulary.
module Orchestration =
    type ProjectId = private ProjectId of Guid
    type OperationId = private OperationId of Guid
    type CommandId = private CommandId of Guid
    type AttemptId = private AttemptId of Guid
    type SessionId = private SessionId of Guid
    type ReservationId = private ReservationId of Guid
    type CandidateId = private CandidateId of Guid
    type RunnerId = private RunnerId of Guid
    type Generation = private Generation of int64
    type WorkflowRevision = private WorkflowRevision of int64

    [<RequireQualifiedAccess>]
    module Id =
        let project value = ProjectId value
        let operation value = OperationId value
        let command value = CommandId value
        let attempt value = AttemptId value
        let session value = SessionId value
        let reservation value = ReservationId value
        let candidate value = CandidateId value
        let runner value = RunnerId value
        let generation value = if value < 0L then invalidArg (nameof value) "negative generation" else Generation value
        let revision value = if value < 0L then invalidArg (nameof value) "negative revision" else WorkflowRevision value
        let generationValue (Generation value) = value
        let revisionValue (WorkflowRevision value) = value
        let projectValue (ProjectId value) = value
        let operationValue (OperationId value) = value
        let commandValue (CommandId value) = value
        let attemptValue (AttemptId value) = value
        let sessionValue (SessionId value) = value
        let reservationValue (ReservationId value) = value
        let candidateValue (CandidateId value) = value
        let runnerValue (RunnerId value) = value

    type RepositoryIdentity = private { NodeId: string; DatabaseId: int64 }
    type WorkItemId = private { Repository: RepositoryIdentity; IssueNodeId: string; IssueNumber: int64 }

    [<RequireQualifiedAccess>]
    module WorkItemIdentity =
        let private node value =
            if String.IsNullOrWhiteSpace value then invalidArg (nameof value) "immutable node id required"
            let normalized=value.Trim()
            if normalized.Length>128 || not(normalized |> Seq.forall(fun c -> Char.IsAsciiLetterOrDigit c || c='_' || c='-' || c='.')) then invalidArg (nameof value) "invalid immutable node id"
            normalized
        let create repositoryNodeId repositoryDatabaseId issueNodeId issueNumber =
            if repositoryDatabaseId <= 0L || issueNumber <= 0L then invalidArg "identity" "numeric identities must be positive"
            { Repository = { NodeId = node repositoryNodeId; DatabaseId = repositoryDatabaseId }
              IssueNodeId = node issueNodeId; IssueNumber = issueNumber }
        let canonicalBytes item =
            // Names, aliases, URLs, clone paths and board membership are projections, not identity.
            $"fsgg.work-item/v1\nrepository-node:{item.Repository.NodeId}\nrepository-id:{item.Repository.DatabaseId}\nissue-node:{item.IssueNodeId}\nissue-number:{item.IssueNumber}\n"
            |> Encoding.UTF8.GetBytes
        let persistenceId item =
            canonicalBytes item |> SHA256.HashData |> Convert.ToHexString
            |> fun digest -> $"work-item-v1-{digest.ToLowerInvariant()}"

    type Budget = { TokenLimit: int64; RuntimeSecondsLimit: int64; CostMicrosLimit: int64; Deadline: DateTimeOffset }
    type BudgetUse = { Tokens: int64; RuntimeSeconds: int64; CostMicros: int64 }
    type RunnerEnrollment =
        { RunnerId: RunnerId; PrincipalId: string; FingerprintSha256: string
          Generation: Generation; ExpiresAt: DateTimeOffset }
    type PlanningSnapshot =
        { ProjectId: ProjectId; WorkItemId: WorkItemId; WorkflowRevision: WorkflowRevision
          CanonicalSha256: string; BoardMembershipIds: string list; CapturedAt: DateTimeOffset }
    type DurableCandidateLocation =
        | ContentAddressedObject of objectKey: string
        | ImmutableRemoteGitRef of repositoryNodeId: string * commitSha: string * qualifiedRef: string
    type CandidateArtifact =
        { CandidateId: CandidateId; BaselineSha: string; HeadSha: string; TreeSha: string
          ManifestSha256: string; ContentSha256: string; MediaType: string; SizeBytes: int64
          RetainUntil: DateTimeOffset; Location: DurableCandidateLocation }
    type CandidateStorageReceipt =
        { CandidateId: CandidateId; ContentSha256: string; ManifestSha256: string; SizeBytes: int64
          Location: DurableCandidateLocation; StoreId: string; StoreSchemaVersion: int
          StorageReceiptSha256: string; VerifiedAt: DateTimeOffset }
    type ExternalClaim =
        { ClaimId: string; Generation: Generation; WorkflowRevision: WorkflowRevision; ObservedAt: DateTimeOffset }
    type EffectKind = AcquireExternalClaim | ReleaseExternalClaim | DispatchRunner | CancelRunner | InspectExternalOperation
    type EffectIntent =
        { OperationId: OperationId; Kind: EffectKind; Generation: Generation
          WorkflowRevision: WorkflowRevision; ResourceId: string; PayloadSha256: string }
    type EffectOutcome = Applied of providerRevision: string | ProvenAbsent | Refused of string | Unknown of string
    type ControlState = Running | Paused of string | CancelPending of string | Cancelled of string | Revoked of string
    type Reservation =
        { ReservationId: ReservationId; Generation: Generation; ExpiresAt: DateTimeOffset
          RequiredClaimIds: Set<string> }
    type AttemptStatus = Active | OutcomeUnknown of string | Completed | CancelledByRunner | ReconciledAbsent of string
    type Attempt =
        { AttemptId: AttemptId; SessionId: SessionId; Runner: RunnerEnrollment
          Generation: Generation; StartedAt: DateTimeOffset; Status: AttemptStatus }
    type OperationState =
        | IntentRecorded of EffectIntent | Dispatching of EffectIntent
        | NeedsObservation of EffectIntent * string | Settled of EffectIntent * EffectOutcome
    type ReceiptDisposition = Accepted | Duplicate | Conflict | Rejected
    type CommandReceipt =
        { CommandId: CommandId; BodySha256: string; Disposition: ReceiptDisposition
          Revision: WorkflowRevision; Detail: string }
    type State =
        { WorkItemId: WorkItemId option; Snapshot: PlanningSnapshot option; Revision: WorkflowRevision
          Generation: Generation; Control: ControlState; Budget: Budget option; Used: BudgetUse
          Reservation: Reservation option; ExternalClaims: Map<string,ExternalClaim>
          RecoveryObligations: Set<string>; CompensationFailures: Map<string,string>
          Attempts: Map<AttemptId, Attempt>; Candidates: Map<CandidateId, CandidateArtifact>
          Operations: Map<OperationId, OperationState>; CommandReceipts: Map<CommandId, CommandReceipt> }
    type Command =
        | Admit of PlanningSnapshot * Budget | Reserve of ReservationId * DateTimeOffset * requiredClaimIds:Set<string>
        | ObserveClaim of ExternalClaim | ReleaseReservation of string | ObserveClaimReleased of string
        | RecordCompensationFailure of claimId:string * reason:string
        | StartAttempt of AttemptId * SessionId * RunnerEnrollment
        | ObserveAttempt of AttemptId * AttemptStatus
        | ChargeBudget of BudgetUse | Pause of string | Resume | RequestCancel of string
        | ConfirmCancelled of string | Revoke of string | RecordCandidate of CandidateArtifact * CandidateStorageReceipt
        | RecordEffectIntent of EffectIntent | MarkEffectDispatching of OperationId
        | ObserveEffect of OperationId * EffectOutcome | AuthorizeEffectRetry of OperationId
    type Event =
        | WorkAdmitted of PlanningSnapshot * Budget | GenerationAdvanced of Generation
        | ReservationCreated of Reservation | ReservationReleased of ReservationId * string * claimsToCompensate:Set<string>
        | ClaimObserved of ExternalClaim | ClaimReleased of string | CompensationFailed of string * string
        | AttemptStarted of Attempt | AttemptObserved of AttemptId * AttemptStatus | BudgetCharged of BudgetUse
        | PausedEvent of string | ResumedEvent | CancelRequestedEvent of string | CancelledEvent of string
        | RevokedEvent of string | CandidateAccepted of CandidateArtifact | EffectIntentRecorded of EffectIntent
        | EffectDispatchStarted of OperationId | EffectObservationRequired of OperationId * string
        | EffectSettled of OperationId * EffectOutcome | EffectRetryAuthorized of OperationId
        | CommandRecorded of CommandReceipt
    type Decision = { Events: Event list; Effects: EffectIntent list; Receipt: CommandReceipt }

    let initial =
        { WorkItemId=None; Snapshot=None; Revision=WorkflowRevision 0L; Generation=Generation 0L
          Control=Paused "not-admitted"; Budget=None; Used={Tokens=0L;RuntimeSeconds=0L;CostMicros=0L}
          Reservation=None; ExternalClaims=Map.empty; RecoveryObligations=Set.empty;CompensationFailures=Map.empty
          Attempts=Map.empty; Candidates=Map.empty
          Operations=Map.empty; CommandReceipts=Map.empty }
    let private nextRevision (WorkflowRevision value) = WorkflowRevision(value + 1L)
    let private nextGeneration (Generation value) = Generation(value + 1L)
    let private sameGeneration (Generation left) (Generation right) = left = right
    let private validSha value = not(String.IsNullOrWhiteSpace value) && value.Length=64 && Seq.forall Uri.IsHexDigit value
    let private validGitObject value = not(String.IsNullOrWhiteSpace value) && (value.Length=40 || value.Length=64) && Seq.forall Uri.IsHexDigit value
    let private tryAdd left right =
        if left < 0L || right < 0L || left > Int64.MaxValue-right then None else Some(left+right)
    let private tryAddUse a b =
        match tryAdd a.Tokens b.Tokens,tryAdd a.RuntimeSeconds b.RuntimeSeconds,tryAdd a.CostMicros b.CostMicros with
        | Some tokens,Some seconds,Some cost -> Some {Tokens=tokens;RuntimeSeconds=seconds;CostMicros=cost}
        | _ -> None
    let private within now budget used =
        now <= budget.Deadline && used.Tokens <= budget.TokenLimit
        && used.RuntimeSeconds <= budget.RuntimeSecondsLimit && used.CostMicros <= budget.CostMicrosLimit
    let private hasCurrentClaims state reservation =
        reservation.RequiredClaimIds
        |> Set.forall(fun claimId ->
            state.ExternalClaims
            |> Map.tryFind claimId
            |> Option.exists(fun claim ->
                sameGeneration claim.Generation state.Generation
                && state.Snapshot |> Option.exists(fun snapshot -> claim.WorkflowRevision=snapshot.WorkflowRevision)))
    let private effectAuthorized now state intent =
        let currentRevision = state.Snapshot |> Option.exists(fun snapshot -> snapshot.WorkflowRevision=intent.WorkflowRevision)
        let currentGeneration = sameGeneration intent.Generation state.Generation
        let budgetAvailable = match state.Budget with | Some budget -> within now budget state.Used | None -> false
        match intent.Kind with
        | DispatchRunner ->
            state.Control=Running && currentGeneration && currentRevision && budgetAvailable
            && Set.isEmpty state.RecoveryObligations
            && (state.Attempts |> Map.exists(fun _ attempt -> attempt.Status=Active && sameGeneration attempt.Generation state.Generation))
            && (state.Reservation |> Option.exists(fun reservation -> reservation.ExpiresAt>now && sameGeneration reservation.Generation state.Generation && hasCurrentClaims state reservation))
        | AcquireExternalClaim ->
            state.Control=Running && currentGeneration && currentRevision && budgetAvailable
            && Set.isEmpty state.RecoveryObligations
            && (state.Reservation |> Option.exists(fun reservation -> reservation.ExpiresAt>now && sameGeneration reservation.Generation state.Generation))
        | ReleaseExternalClaim -> currentRevision && (Set.contains intent.ResourceId state.RecoveryObligations || Map.containsKey intent.ResourceId state.ExternalClaims)
        | CancelRunner -> currentRevision && (state.Attempts |> Map.exists(fun _ attempt -> Id.runnerValue attempt.Runner.RunnerId |> string = intent.ResourceId && (match attempt.Status with Active|OutcomeUnknown _ -> true | _ -> false))) && (match state.Control with CancelPending _|Cancelled _|Revoked _ -> true | _ -> false)
        | InspectExternalOperation -> currentRevision

    let evolve state event =
        let revision = nextRevision state.Revision
        match event with
        | WorkAdmitted(s,b) -> {state with WorkItemId=Some s.WorkItemId;Snapshot=Some s;Budget=Some b;Control=Running;Revision=revision}
        | GenerationAdvanced g -> {state with Generation=g;Revision=revision}
        | ReservationCreated r -> {state with Reservation=Some r;Revision=revision}
        | ReservationReleased(_,_,claims) -> {state with Reservation=None;RecoveryObligations=Set.union state.RecoveryObligations claims;Revision=revision}
        | ClaimObserved c -> {state with ExternalClaims=Map.add c.ClaimId c state.ExternalClaims;Revision=revision}
        | ClaimReleased claimId -> {state with ExternalClaims=Map.remove claimId state.ExternalClaims;RecoveryObligations=Set.remove claimId state.RecoveryObligations;CompensationFailures=Map.remove claimId state.CompensationFailures;Revision=revision}
        | CompensationFailed(claimId,reason) -> {state with RecoveryObligations=Set.add claimId state.RecoveryObligations;CompensationFailures=Map.add claimId reason state.CompensationFailures;Revision=revision}
        | AttemptStarted a -> {state with Attempts=Map.add a.AttemptId a state.Attempts;Revision=revision}
        | AttemptObserved(id,status) ->
            match Map.tryFind id state.Attempts with
            | Some attempt -> {state with Attempts=Map.add id {attempt with Status=status} state.Attempts;Revision=revision}
            | None -> {state with Revision=revision}
        | BudgetCharged b ->
            match tryAddUse state.Used b with
            | Some used -> {state with Used=used;Revision=revision}
            | None -> invalidOp "persisted budget event overflows"
        | PausedEvent r -> {state with Control=Paused r;Revision=revision}
        | ResumedEvent -> {state with Control=Running;Revision=revision}
        | CancelRequestedEvent r -> {state with Control=CancelPending r;Revision=revision}
        | CancelledEvent r -> {state with Control=Cancelled r;Reservation=None;RecoveryObligations=Set.union state.RecoveryObligations (state.ExternalClaims |> Map.keys |> Set.ofSeq);Revision=revision}
        | RevokedEvent r -> {state with Control=Revoked r;Reservation=None;RecoveryObligations=Set.union state.RecoveryObligations (state.ExternalClaims |> Map.keys |> Set.ofSeq);Revision=revision}
        | CandidateAccepted c -> {state with Candidates=Map.add c.CandidateId c state.Candidates;Revision=revision}
        | EffectIntentRecorded i -> {state with Operations=Map.add i.OperationId (IntentRecorded i) state.Operations;Revision=revision}
        | EffectDispatchStarted id ->
            match Map.tryFind id state.Operations with
            | Some(IntentRecorded i) -> {state with Operations=Map.add id (Dispatching i) state.Operations;Revision=revision}
            | _ -> {state with Revision=revision}
        | EffectObservationRequired(id,r) ->
            match Map.tryFind id state.Operations with
            | Some(IntentRecorded i)|Some(Dispatching i)|Some(NeedsObservation(i,_)) -> {state with Operations=Map.add id (NeedsObservation(i,r)) state.Operations;Revision=revision}
            | _ -> {state with Revision=revision}
        | EffectSettled(id,o) ->
            match Map.tryFind id state.Operations with
            | Some(IntentRecorded i)|Some(Dispatching i)|Some(NeedsObservation(i,_)) -> {state with Operations=Map.add id (Settled(i,o)) state.Operations;Revision=revision}
            | _ -> {state with Revision=revision}
        | EffectRetryAuthorized id ->
            match Map.tryFind id state.Operations with
            | Some(Settled(intent,ProvenAbsent)) -> {state with Operations=Map.add id (IntentRecorded intent) state.Operations;Revision=revision}
            | _ -> {state with Revision=revision}
        | CommandRecorded r -> {state with CommandReceipts=Map.add r.CommandId r state.CommandReceipts}

    let replay events = List.fold evolve initial events
    let private mkReceipt state id digest disposition detail =
        {CommandId=id;BodySha256=digest;Disposition=disposition;Revision=state.Revision;Detail=detail}
    let private decideNew now state id digest command =
        let accept events effects detail =
            let projected=List.fold evolve state events
            let receipt=mkReceipt projected id digest Accepted detail
            {Events=events@[CommandRecorded receipt];Effects=effects;Receipt=receipt}
        let reject detail =
            let receipt=mkReceipt state id digest Rejected detail
            {Events=[CommandRecorded receipt];Effects=[];Receipt=receipt}
        match command with
        | Admit(s,b) when state.WorkItemId.IsNone && within now b state.Used -> accept [WorkAdmitted(s,b);GenerationAdvanced(nextGeneration state.Generation)] [] "admitted"
        | Admit _ -> reject "already-admitted-or-invalid-budget"
        | Reserve(r,e,claims) when state.Control=Running && state.Reservation.IsNone && e>now && not(Set.isEmpty claims) && Set.forall (String.IsNullOrWhiteSpace >> not) claims -> accept [ReservationCreated{ReservationId=r;Generation=state.Generation;ExpiresAt=e;RequiredClaimIds=claims}] [] "reserved"
        | Reserve _ -> reject "reservation-not-available"
        | ObserveClaim c when sameGeneration c.Generation state.Generation -> accept [ClaimObserved c] [] "claim-observed"
        | ObserveClaim _ -> reject "stale-claim-generation"
        | ReleaseReservation reason ->
            match state.Reservation with
            | Some reservation ->
                let held=Set.intersect reservation.RequiredClaimIds (state.ExternalClaims |> Map.keys |> Set.ofSeq)
                accept [ReservationReleased(reservation.ReservationId,reason,held)] [] "reservation-released-compensation-required"
            | None -> reject "no-reservation"
        | ObserveClaimReleased claimId when Map.containsKey claimId state.ExternalClaims -> accept [ClaimReleased claimId] [] "claim-release-observed"
        | ObserveClaimReleased _ -> reject "claim-not-held"
        | RecordCompensationFailure(claimId,reason) when Set.contains claimId state.RecoveryObligations -> accept [CompensationFailed(claimId,reason)] [] "compensation-pending"
        | RecordCompensationFailure _ -> reject "no-compensation-obligation"
        | StartAttempt(a,s,r) ->
            match state.Control,state.Budget,state.Reservation with
            | Running,Some b,Some reservation when reservation.ExpiresAt>now && r.ExpiresAt>now && within now b state.Used && sameGeneration reservation.Generation state.Generation && sameGeneration r.Generation state.Generation && not(Map.containsKey a state.Attempts) && (state.Attempts |> Map.forall(fun _ attempt -> match attempt.Status with Completed|CancelledByRunner|ReconciledAbsent _ -> true | _ -> false)) && hasCurrentClaims state reservation ->
                accept [AttemptStarted{AttemptId=a;SessionId=s;Runner=r;Generation=state.Generation;StartedAt=now;Status=Active}] [] "attempt-started"
            | _ -> reject "dispatch-requires-current-reservation-claim-runner-and-budget"
        | ObserveAttempt(attemptId,status) ->
            match Map.tryFind attemptId state.Attempts,status with
            | Some _,Active -> reject "observation-cannot-create-active-attempt"
            | Some _,_ -> accept [AttemptObserved(attemptId,status)] [] (match status with OutcomeUnknown _ -> "attempt-awaits-reconciliation" | _ -> "attempt-terminal-observed")
            | None,_ -> reject "unknown-attempt"
        | ChargeBudget delta ->
            match state.Budget,tryAddUse state.Used delta with
            | Some b,Some used when within now b used -> accept [BudgetCharged delta] [] "budget-charged"
            | _ -> reject "budget-exceeded-or-expired"
        | Pause r when state.Control=Running -> accept [PausedEvent r] [] "paused"
        | Pause _ -> reject "not-running"
        | Resume -> match state.Control,state.Budget with | Paused _,Some b when within now b state.Used -> accept [ResumedEvent] [] "resumed" | _ -> reject "resume-refused"
        | RequestCancel r -> match state.Control with | Running|Paused _ -> accept [CancelRequestedEvent r] [] "cancel-requested" | _ -> reject "cancel-refused"
        | ConfirmCancelled r -> match state.Control with | CancelPending _ -> accept [CancelledEvent r;GenerationAdvanced(nextGeneration state.Generation)] [] "cancelled" | _ -> reject "cancel-not-pending"
        | Revoke r -> accept [RevokedEvent r;GenerationAdvanced(nextGeneration state.Generation)] [] "revoked"
        | RecordCandidate(c,proof) when proof.CandidateId=c.CandidateId && proof.ContentSha256.Equals(c.ContentSha256,StringComparison.OrdinalIgnoreCase) && proof.ManifestSha256.Equals(c.ManifestSha256,StringComparison.OrdinalIgnoreCase) && proof.SizeBytes=c.SizeBytes && proof.Location=c.Location && proof.StoreSchemaVersion=1 && not(String.IsNullOrWhiteSpace proof.StoreId) && validSha proof.StorageReceiptSha256 && proof.VerifiedAt<=now && c.SizeBytes>=0L && c.SizeBytes<=104857600L && c.RetainUntil>now && c.RetainUntil<=now.AddDays 90. && validSha c.ContentSha256 && validSha c.ManifestSha256 && validGitObject c.BaselineSha && validGitObject c.HeadSha && validGitObject c.TreeSha && Set.contains c.MediaType (Set.ofList ["application/vnd.git.bundle";"application/zip";"application/zstd"]) && (match c.Location with | ContentAddressedObject key -> key=$"sha256/{c.ContentSha256.ToLowerInvariant()}" | ImmutableRemoteGitRef(repository,commit,qualifiedRef) -> not(String.IsNullOrWhiteSpace repository) && validGitObject commit && commit.Equals(c.HeadSha,StringComparison.OrdinalIgnoreCase) && qualifiedRef.StartsWith("refs/fsgg/candidates/",StringComparison.Ordinal)) ->
            match Map.tryFind c.CandidateId state.Candidates with | Some x when x=c -> accept [] [] "candidate-already-accepted" | Some _ -> reject "candidate-identity-conflict" | None -> accept [CandidateAccepted c] [] "candidate-durably-accepted"
        | RecordCandidate _ -> reject "candidate-not-recoverable-or-invalid"
        | RecordEffectIntent i when sameGeneration i.Generation state.Generation && not(Map.containsKey i.OperationId state.Operations) -> accept [EffectIntentRecorded i] [] "effect-intent-recorded"
        | RecordEffectIntent _ -> reject "effect-identity-conflict-or-stale-generation"
        | MarkEffectDispatching operationId ->
            match Map.tryFind operationId state.Operations with | Some(IntentRecorded i) when effectAuthorized now state i -> accept [EffectDispatchStarted operationId] [i] "effect-dispatching" | Some(NeedsObservation _) -> reject "observe-before-retry" | Some _ -> reject "effect-not-authorized" | _ -> reject "effect-not-dispatchable"
        | ObserveEffect(operationId,Unknown reason) ->
            match Map.tryFind operationId state.Operations with | Some(IntentRecorded _)|Some(Dispatching _)|Some(NeedsObservation _) -> accept [EffectObservationRequired(operationId,reason)] [] "observe-before-retry" | _ -> reject "unknown-operation"
        | ObserveEffect(operationId,outcome) ->
            match Map.tryFind operationId state.Operations with | Some(IntentRecorded _)|Some(Dispatching _)|Some(NeedsObservation _) -> accept [EffectSettled(operationId,outcome)] [] "effect-settled" | _ -> reject "unknown-operation"
        | AuthorizeEffectRetry operationId ->
            match Map.tryFind operationId state.Operations with | Some(Settled(_,ProvenAbsent)) -> accept [EffectRetryAuthorized operationId] [] "same-operation-retry-authorized" | _ -> reject "retry-requires-proven-absent"

    let private invariant (value:int64) = value.ToString(CultureInfo.InvariantCulture)
    let private frame values =
        values |> Seq.map(fun value -> let v=if isNull value then "" else value in $"{v.Length}:{v};") |> String.concat ""
    let private generationText generation = generation |> Id.generationValue |> invariant
    let private revisionText revision = revision |> Id.revisionValue |> invariant
    let private timeText (value:DateTimeOffset) = value.ToUniversalTime().Ticks |> invariant
    let private candidateParts (candidate:CandidateArtifact) =
        let location = match candidate.Location with | ContentAddressedObject key -> frame["object";key] | ImmutableRemoteGitRef(repo,commit,reference) -> frame["git";repo;commit;reference]
        [Id.candidateValue candidate.CandidateId |> string;candidate.BaselineSha;candidate.HeadSha;candidate.TreeSha;candidate.ManifestSha256;candidate.ContentSha256;candidate.MediaType;invariant candidate.SizeBytes;timeText candidate.RetainUntil;location]
    let canonicalCommandBytes command =
        let parts =
            match command with
            | Admit(s,b) -> ["admit";Convert.ToBase64String(WorkItemIdentity.canonicalBytes s.WorkItemId);Id.projectValue s.ProjectId |> string;revisionText s.WorkflowRevision;s.CanonicalSha256;frame(List.sort s.BoardMembershipIds);timeText s.CapturedAt;invariant b.TokenLimit;invariant b.RuntimeSecondsLimit;invariant b.CostMicrosLimit;timeText b.Deadline]
            | Reserve(id,expires,claims) -> ["reserve";Id.reservationValue id |> string;timeText expires;frame(Set.toList claims)]
            | ObserveClaim c -> ["claim";c.ClaimId;generationText c.Generation;revisionText c.WorkflowRevision;timeText c.ObservedAt]
            | ReleaseReservation reason -> ["release-reservation";reason]
            | ObserveClaimReleased claim -> ["claim-released";claim]
            | RecordCompensationFailure(claim,reason) -> ["compensation-failed";claim;reason]
            | StartAttempt(a,s,r) -> ["start-attempt";Id.attemptValue a |> string;Id.sessionValue s |> string;Id.runnerValue r.RunnerId |> string;r.PrincipalId;r.FingerprintSha256;generationText r.Generation;timeText r.ExpiresAt]
            | ObserveAttempt(a,status) -> ["observe-attempt";Id.attemptValue a |> string;sprintf "%A" status]
            | ChargeBudget b -> ["charge";invariant b.Tokens;invariant b.RuntimeSeconds;invariant b.CostMicros]
            | Pause reason -> ["pause";reason] | Resume -> ["resume"] | RequestCancel reason -> ["request-cancel";reason]
            | ConfirmCancelled reason -> ["confirm-cancel";reason] | Revoke reason -> ["revoke";reason]
            | RecordCandidate(c,proof) -> "candidate"::candidateParts c @ [Id.candidateValue proof.CandidateId |> string;proof.ContentSha256;proof.ManifestSha256;invariant proof.SizeBytes;sprintf "%A" proof.Location;proof.StoreId;string proof.StoreSchemaVersion;proof.StorageReceiptSha256;timeText proof.VerifiedAt]
            | RecordEffectIntent i -> ["effect";Id.operationValue i.OperationId |> string;string i.Kind;generationText i.Generation;revisionText i.WorkflowRevision;i.ResourceId;i.PayloadSha256]
            | MarkEffectDispatching id -> ["dispatch-effect";Id.operationValue id |> string]
            | ObserveEffect(id,outcome) -> ["observe-effect";Id.operationValue id |> string;sprintf "%A" outcome]
            | AuthorizeEffectRetry id -> ["authorize-effect-retry";Id.operationValue id |> string]
        frame parts |> Encoding.UTF8.GetBytes
    let canonicalCommandSha256 command = canonicalCommandBytes command |> SHA256.HashData |> Convert.ToHexString |> fun x -> x.ToLowerInvariant()

    let decide now state commandId bodySha256 command =
        let canonicalDigest=canonicalCommandSha256 command
        if not(validSha bodySha256) || not(bodySha256.Equals(canonicalDigest,StringComparison.OrdinalIgnoreCase)) then
            {Events=[];Effects=[];Receipt=mkReceipt state commandId bodySha256 Rejected "invalid-command-digest"}
        else
            match Map.tryFind commandId state.CommandReceipts with
            | Some prior when prior.BodySha256=bodySha256 -> {Events=[];Effects=[];Receipt={prior with Disposition=Duplicate;Detail="duplicate-command"}}
            | Some prior -> {Events=[];Effects=[];Receipt={prior with Disposition=Conflict;Detail="command-identity-conflict"}}
            | None -> decideNew now state commandId bodySha256 command

    [<RequireQualifiedAccess>]
    module CandidateArtifact =
        let archiveEntryIsSafe entry =
            if String.IsNullOrWhiteSpace entry || IO.Path.IsPathRooted entry then false
            else entry.Replace('\\','/').Split('/',StringSplitOptions.RemoveEmptyEntries) |> Array.forall(fun s -> s<>"." && s<>".." && not(s.Contains(':')))

    type ProjectState =
        { ProjectId: ProjectId; Generation: Generation; Paused: bool; Capacity: int
          RecoveryCapacity: int; Reservations: Map<ReservationId,WorkItemId> }
    type ProjectCommand =
        | Allocate of ReservationId * WorkItemId * recoveryWork:bool
        | Release of ReservationId | PauseProject | ResumeProject | AdvanceOwnershipGeneration
    type ProjectEvent =
        | Allocated of ReservationId * WorkItemId | Released of ReservationId
        | ProjectPaused | ProjectResumed | OwnershipGenerationAdvanced of Generation

    [<RequireQualifiedAccess>]
    module ProjectOrchestrator =
        let initial projectId capacity recoveryCapacity =
            if capacity<=0 || recoveryCapacity<=0 || recoveryCapacity>=capacity then invalidArg "capacity" "reserve at least one recovery slot below total capacity"
            {ProjectId=projectId;Generation=Generation 0L;Paused=true;Capacity=capacity;RecoveryCapacity=recoveryCapacity;Reservations=Map.empty}
        let evolve state event =
            match event with
            | Allocated(id,item) -> {state with Reservations=Map.add id item state.Reservations}
            | Released id -> {state with Reservations=Map.remove id state.Reservations}
            | ProjectPaused -> {state with Paused=true} | ProjectResumed -> {state with Paused=false}
            | OwnershipGenerationAdvanced generation -> {state with Generation=generation;Paused=true}
        let decide state command =
            match command with
            | Allocate(id,item,recovery) when not state.Paused && not(Map.containsKey id state.Reservations) ->
                let used=Map.count state.Reservations
                let limit=if recovery then state.Capacity else state.Capacity-state.RecoveryCapacity
                if used<limit then Ok [Allocated(id,item)] else Error "capacity-reserved-for-recovery"
            | Release id when Map.containsKey id state.Reservations -> Ok [Released id]
            | PauseProject when not state.Paused -> Ok [ProjectPaused]
            | ResumeProject when state.Paused -> Ok [ProjectResumed]
            | AdvanceOwnershipGeneration -> Ok [OwnershipGenerationAdvanced(nextGeneration state.Generation)]
            | _ -> Error "project-command-refused"

    [<RequireQualifiedAccess>]
    module WorkItem =
        let evolve = evolve
        let decide = decide

    [<RequireQualifiedAccess>]
    module SchedulerReservation =
        let available state = state.Capacity - Map.count state.Reservations
        let recoveryCapacityPreserved state =
            state.Paused || Map.count state.Reservations <= state.Capacity-state.RecoveryCapacity

    [<RequireQualifiedAccess>]
    module OperationSaga =
        let pending state =
            state.Operations
            |> Map.toList
            |> List.choose(fun (id,status) -> match status with Settled _ -> None | _ -> Some(id,status))
        let requiresObservation state =
            state.Operations |> Map.exists(fun _ status -> match status with NeedsObservation _ -> true | _ -> false)

    type SessionState =
        { SessionId: SessionId; RunnerId: RunnerId; Generation: Generation
          LastClientSequence: int64; LastServerSequence: int64; Closed: bool }
    type SessionInput = ClientMessage of sequence:int64 | ServerMessage | CloseSession

    [<RequireQualifiedAccess>]
    module Session =
        let accept state input =
            match input with
            | _ when state.Closed -> Error "session-closed"
            | ClientMessage sequence when sequence=state.LastClientSequence+1L -> Ok {state with LastClientSequence=sequence}
            | ClientMessage sequence when sequence<=state.LastClientSequence -> Error "duplicate-client-message"
            | ClientMessage _ -> Error "client-sequence-gap"
            | ServerMessage when state.LastServerSequence<Int64.MaxValue -> Ok {state with LastServerSequence=state.LastServerSequence+1L}
            | ServerMessage -> Error "server-sequence-overflow"
            | CloseSession -> Ok {state with Closed=true}

    [<RequireQualifiedAccess>]
    module AgentAttempt =
        let isTerminal attempt = match attempt.Status with Completed|CancelledByRunner|ReconciledAbsent _ -> true | _ -> false
        let replacementAllowed attempts = attempts |> Map.forall(fun _ attempt -> isTerminal attempt)
