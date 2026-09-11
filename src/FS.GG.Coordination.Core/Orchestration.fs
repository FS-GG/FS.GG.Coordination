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
    type ProtocolVersion = private ProtocolVersion of major:int * minor:int

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
        let protocolVersion major minor = if major<0 || minor<0 then invalidArg "version" "negative protocol version" else ProtocolVersion(major,minor)
        let generationValue (Generation value) = value
        let revisionValue (WorkflowRevision value) = value
        let protocolVersionValue (ProtocolVersion(major,minor)) = major,minor
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
            if normalized.Length>128 || not(normalized |> Seq.forall(fun c -> Char.IsAsciiLetterOrDigit c || c='_' || c='-' || c='.' || c='+' || c='/' || c='=')) then invalidArg (nameof value) "invalid immutable node id"
            normalized
        let create repositoryNodeId repositoryDatabaseId issueNodeId issueNumber =
            if repositoryDatabaseId <= 0L || issueNumber <= 0L then invalidArg "identity" "numeric identities must be positive"
            { Repository = { NodeId = node repositoryNodeId; DatabaseId = repositoryDatabaseId }
              IssueNodeId = node issueNodeId; IssueNumber = issueNumber }
        let canonicalBytes item =
            // Names, aliases, URLs, clone paths and board membership are projections, not identity.
            // Database identity plus repository-local issue number survives repository rename/transfer
            // and GitHub's legacy/new global-node-ID migration. Node IDs remain opaque readback facts.
            $"fsgg.work-item/v1\nrepository-id:{item.Repository.DatabaseId}\nissue-number:{item.IssueNumber}\n"
            |> Encoding.UTF8.GetBytes
        let persistenceId item =
            canonicalBytes item |> SHA256.HashData |> Convert.ToHexString
            |> fun digest -> $"work-item-v1-{digest.ToLowerInvariant()}"

    type Budget = { TokenLimit: int64; RuntimeSecondsLimit: int64; CostMicrosLimit: int64; Deadline: DateTimeOffset }
    type BudgetUse = { Tokens: int64; RuntimeSeconds: int64; CostMicros: int64 }
    /// Additive subscription accounting; legacy Budget and BudgetUse retain their /1 meaning.
    type SubscriptionUsage =
        | TokensObserved of int64 * provenance:string
        | TokensUnknown of provenance:string
    type SubscriptionCost =
        { InvocationState:string; InvocationProvenance:string
          BroaderAttributionState:string; BroaderAttributionProvenance:string }
    type SubscriptionExecutionBudget =
        { Schema:string; AttemptLimit:int; MaximumRuntime:TimeSpan; ExecutionDeadline:DateTimeOffset
          Usage:SubscriptionUsage; Cost:SubscriptionCost }
    type SubscriptionAccountingObservation =
        { ObservedAt:DateTimeOffset; RuntimeSeconds:int64; RuntimeWithinBound:bool
          Usage:SubscriptionUsage; Cost:SubscriptionCost }
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
    type EffectKind =
        | AcquireExternalClaim | ReleaseExternalClaim | DispatchRunner | CancelRunner | InspectExternalOperation
        | StoreCandidate | PublishCandidateBranch | CreatePullRequest | MergePullRequest | ReadNativeDelivery
    type EffectIntent =
        { OperationId: OperationId; Kind: EffectKind; Generation: Generation
          WorkflowRevision: WorkflowRevision; ResourceId: string; PayloadSha256: string }
    type EffectOutcome = Applied of providerRevision: string | ProvenAbsent | Refused of string | Unknown of string
    type NativeDeliveryReadback =
        { OperationId: OperationId; RouteId: Guid; AttemptId: AttemptId; CandidateId: CandidateId
          RepositoryNodeId: string; PullRequestNodeId: string
          CandidateHeadSha: string; ObservedPullRequestHeadSha: string; MergeCommitSha: string
          ProviderRevision: string; Generation: Generation; WorkflowRevision: WorkflowRevision
          ObservedAt: DateTimeOffset; Merged: bool }
    type HostedRoutePlan =
        { RouteId: Guid; WorkItemId: WorkItemId; JobClass: string; AttemptId: AttemptId
          CandidateId: CandidateId; RepositoryNodeId: string; BranchRef: string; ClaimResourceId: string
          ClaimOperationId: OperationId; ProcessOperationId: OperationId; CandidateOperationId: OperationId
          BranchOperationId: OperationId; PullRequestOperationId: OperationId; MergeOperationId: OperationId
          ReadbackOperationId: OperationId; Generation: Generation; WorkflowRevision: WorkflowRevision
          SelectedAt: DateTimeOffset }
    type HostedEffectReadback =
        { OperationId: OperationId; RouteId: Guid; AttemptId: AttemptId; CandidateId: CandidateId
          RepositoryNodeId: string; ProviderResourceId: string
          CandidateHeadSha: string option; ResultSha: string option; ProviderRevision: string
          Generation: Generation; WorkflowRevision: WorkflowRevision; ObservedAt: DateTimeOffset
          Exists: bool }
    type HostedRouteReadback =
        { RouteId: Guid; WorkItemId: WorkItemId; RepositoryNodeId: string
          ProviderRevision: string; EvidenceSha256: string
          Generation: Generation; WorkflowRevision: WorkflowRevision; ObservedAt: DateTimeOffset }
    type ControlState = Running | Paused of string | CancelPending of string | Cancelled of string | Revoked of string
    type Reservation =
        { ReservationId: ReservationId; Generation: Generation; ExpiresAt: DateTimeOffset
          RequiredClaimIds: Set<string> }
    type AttemptStatus = Active | OutcomeUnknown of string | Completed | CancelledByRunner | ReconciledAbsent of string
    type Attempt =
        { AttemptId: AttemptId; SessionId: SessionId; Runner: RunnerEnrollment
          Generation: Generation; StartedAt: DateTimeOffset; Status: AttemptStatus }
    type SessionState =
        { SessionId: SessionId; RunnerId: RunnerId; Generation: Generation
          LastClientSequence: int64; LastServerSequence: int64; Closed: bool }
    type OperationState =
        | IntentRecorded of EffectIntent | Dispatching of EffectIntent
        | NeedsObservation of EffectIntent * string | Settled of EffectIntent * EffectOutcome
    type ReceiptDisposition = Accepted | Duplicate | Conflict | Rejected
    type CommandReceipt =
        { CommandId: CommandId; BodySha256: string; Disposition: ReceiptDisposition
          Revision: WorkflowRevision; ProtocolVersion: ProtocolVersion; Detail: string }
    type State =
        { WorkItemId: WorkItemId option; Snapshot: PlanningSnapshot option; Revision: WorkflowRevision
          Generation: Generation; Control: ControlState; ReadbackCurrent: bool; Budget: Budget option; Used: BudgetUse
          SubscriptionBudget: SubscriptionExecutionBudget option; SubscriptionAccounting: SubscriptionAccountingObservation option
          Reservation: Reservation option; ExternalClaims: Map<string,ExternalClaim>
          RecoveryObligations: Set<string>; CompensationFailures: Map<string,string>
          Attempts: Map<AttemptId, Attempt>; Sessions: Map<SessionId, SessionState>
          Candidates: Map<CandidateId, CandidateArtifact>
          Operations: Map<OperationId, OperationState>; HostedRoute: HostedRoutePlan option
          HostedEffectReadbacks: Map<OperationId, HostedEffectReadback>
          NativeDeliveryReadbacks: Map<OperationId, NativeDeliveryReadback>
          CommandReceipts: Map<CommandId, CommandReceipt> }
    type Command =
        | Admit of PlanningSnapshot * Budget | AdmitSubscription of PlanningSnapshot * SubscriptionExecutionBudget
        | Reserve of ReservationId * DateTimeOffset * requiredClaimIds:Set<string>
        | ObserveClaim of ExternalClaim | ReleaseReservation of string | ObserveClaimReleased of string
        | RecordCompensationFailure of claimId:string * reason:string
        | SelectHostedRoute of HostedRoutePlan | StartAttempt of AttemptId * SessionId * RunnerEnrollment
        | ObserveAttempt of AttemptId * AttemptStatus
        | AcceptRunnerMessage of SessionId * clientSequence:int64 * emitServerMessage:bool * requestSha256:string
        | CloseRunnerSession of SessionId
        | ChargeBudget of BudgetUse | RecordSubscriptionAccounting of SubscriptionAccountingObservation
        | Pause of string | Resume | RequestCancel of string
        | RecordStartupPause of string | RecordHostedRouteReadback of HostedRouteReadback
        | ConfirmCancelled of string | Revoke of string | RecordCandidate of CandidateArtifact * CandidateStorageReceipt
        | RecordEffectIntent of EffectIntent | MarkEffectDispatching of OperationId
        | ObserveEffect of OperationId * EffectOutcome
        | RecordHostedEffectReadback of OperationId * HostedEffectReadback
        | RecordNativeDeliveryReadback of OperationId * NativeDeliveryReadback
        | AuthorizeEffectRetry of OperationId
    type CommandEnvelope =
        { CommandId: CommandId; ProtocolVersion: ProtocolVersion
          ExpectedRevision: WorkflowRevision; ExpectedGeneration: Generation
          PrincipalId: string; SessionId: SessionId option; IssuedAt: DateTimeOffset
          ExpiresAt: DateTimeOffset; Command: Command }
    type Event =
        | WorkAdmitted of PlanningSnapshot * Budget | SubscriptionWorkAdmitted of PlanningSnapshot * SubscriptionExecutionBudget
        | GenerationAdvanced of Generation
        | ReservationCreated of Reservation | ReservationReleased of ReservationId * string * claimsToCompensate:Set<string>
        | ClaimObserved of ExternalClaim | ClaimReleased of string | CompensationFailed of string * string
        | HostedRouteSelected of HostedRoutePlan | AttemptStarted of Attempt | AttemptObserved of AttemptId * AttemptStatus | BudgetCharged of BudgetUse
        | SubscriptionAccountingRecorded of SubscriptionAccountingObservation
        | RunnerSessionOpened of SessionState | RunnerClientSequenceAccepted of SessionId * int64
        | RunnerServerSequenceAdvanced of SessionId * int64 | RunnerSessionClosed of SessionId
        | PausedEvent of string | ResumedEvent | CancelRequestedEvent of string | CancelledEvent of string
        | StartupPausedEvent of string | HostedRouteReadbackAccepted of HostedRouteReadback
        | RevokedEvent of string | CandidateAccepted of CandidateArtifact | EffectIntentRecorded of EffectIntent
        | EffectDispatchStarted of OperationId | EffectObservationRequired of OperationId * string
        | EffectSettled of OperationId * EffectOutcome | HostedEffectReadbackAccepted of HostedEffectReadback
        | NativeDeliveryReadbackAccepted of NativeDeliveryReadback
        | EffectRetryAuthorized of OperationId
        | CommandRecorded of CommandReceipt
    type Decision = { Events: Event list; Effects: EffectIntent list; Receipt: CommandReceipt }

    let initial =
        { WorkItemId=None; Snapshot=None; Revision=WorkflowRevision 0L; Generation=Generation 0L
          Control=Paused "not-admitted"; ReadbackCurrent=true; Budget=None; Used={Tokens=0L;RuntimeSeconds=0L;CostMicros=0L}
          SubscriptionBudget=None;SubscriptionAccounting=None
          Reservation=None; ExternalClaims=Map.empty; RecoveryObligations=Set.empty;CompensationFailures=Map.empty
          Attempts=Map.empty; Sessions=Map.empty; Candidates=Map.empty
          Operations=Map.empty; HostedRoute=None; HostedEffectReadbacks=Map.empty
          NativeDeliveryReadbacks=Map.empty; CommandReceipts=Map.empty }
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
    let private validSubscriptionUsage = function
        | TokensObserved(value,provenance) -> value>=0L && not(String.IsNullOrWhiteSpace provenance)
        | TokensUnknown provenance -> not(String.IsNullOrWhiteSpace provenance)
    let private validSubscriptionCost cost =
        cost.InvocationState="not-applicable" && not(String.IsNullOrWhiteSpace cost.InvocationProvenance)
        && cost.BroaderAttributionState="unknown" && not(String.IsNullOrWhiteSpace cost.BroaderAttributionProvenance)
    let private validSubscriptionBudget now budget =
        budget.Schema="fsgg.coordination.subscription-execution-budget/1"
        && budget.AttemptLimit=1 && budget.MaximumRuntime=TimeSpan.FromMinutes 30.
        && budget.ExecutionDeadline>now && budget.ExecutionDeadline<=now.AddMinutes 30.
        && validSubscriptionUsage budget.Usage && validSubscriptionCost budget.Cost
    let private budgetAvailable now state =
        (state.Budget |> Option.exists(fun budget->within now budget state.Used))
        || (state.SubscriptionBudget |> Option.exists(fun budget->now<budget.ExecutionDeadline))
    let private hasCurrentClaims state reservation =
        reservation.RequiredClaimIds
        |> Set.forall(fun claimId ->
            state.ExternalClaims
            |> Map.tryFind claimId
            |> Option.exists(fun claim ->
                sameGeneration claim.Generation state.Generation
                && state.Snapshot |> Option.exists(fun snapshot -> claim.WorkflowRevision=snapshot.WorkflowRevision)))
    let private intentOf (operation: OperationState) : EffectIntent =
        match operation with
        | IntentRecorded intent | Dispatching intent | NeedsObservation(intent, _) | Settled(intent, _) -> intent
    let private routeOperations (route: HostedRoutePlan) =
        [ AcquireExternalClaim,route.ClaimOperationId
          DispatchRunner,route.ProcessOperationId
          StoreCandidate,route.CandidateOperationId
          PublishCandidateBranch,route.BranchOperationId
          CreatePullRequest,route.PullRequestOperationId
          MergePullRequest,route.MergeOperationId
          ReadNativeDelivery,route.ReadbackOperationId ]
    let private operationForKind kind (route: HostedRoutePlan) = routeOperations route |> List.tryFind(fst >> (=) kind) |> Option.map snd
    let private routeKind operationId (route: HostedRoutePlan) = routeOperations route |> List.tryFind(snd >> (=) operationId) |> Option.map fst
    let private routeIntentMatches (route: HostedRoutePlan) (intent: EffectIntent) =
        operationForKind intent.Kind route = Some intent.OperationId
        && intent.Generation=route.Generation && intent.WorkflowRevision=route.WorkflowRevision
        && match intent.Kind with
           | AcquireExternalClaim -> intent.ResourceId=route.ClaimResourceId
           | DispatchRunner -> intent.ResourceId=(Id.attemptValue route.AttemptId |> string)
           | StoreCandidate -> intent.ResourceId=(Id.candidateValue route.CandidateId |> string)
           | PublishCandidateBranch | CreatePullRequest | MergePullRequest | ReadNativeDelivery -> intent.ResourceId=route.BranchRef
           | _ -> false
    let private predecessorReadbackPresent kind (route: HostedRoutePlan) (state: State) =
        let present operationId =
            state.HostedEffectReadbacks
            |> Map.tryFind operationId
            |> Option.exists(fun readback ->
                readback.Exists && readback.RouteId=route.RouteId && readback.AttemptId=route.AttemptId
                && readback.CandidateId=route.CandidateId && readback.Generation=route.Generation
                && readback.WorkflowRevision=route.WorkflowRevision)
        match kind with
        | AcquireExternalClaim -> true
        | DispatchRunner -> present route.ClaimOperationId
        | StoreCandidate -> present route.ProcessOperationId
        | PublishCandidateBranch -> present route.CandidateOperationId && Map.containsKey route.CandidateId state.Candidates
        | CreatePullRequest -> present route.BranchOperationId
        | MergePullRequest -> present route.PullRequestOperationId
        | ReadNativeDelivery -> present route.MergeOperationId
        | _ -> false
    let private predecessorObservedAt kind (route: HostedRoutePlan) (state: State) =
        let operationId =
            match kind with
            | DispatchRunner -> Some route.ClaimOperationId
            | StoreCandidate -> Some route.ProcessOperationId
            | PublishCandidateBranch -> Some route.CandidateOperationId
            | CreatePullRequest -> Some route.BranchOperationId
            | MergePullRequest -> Some route.PullRequestOperationId
            | ReadNativeDelivery -> Some route.MergeOperationId
            | _ -> None
        operationId |> Option.bind(fun id -> Map.tryFind id state.HostedEffectReadbacks) |> Option.map _.ObservedAt
    let private hostedReadbackMatchesRoute (route:HostedRoutePlan) operationId (readback:HostedEffectReadback) =
        readback.OperationId=operationId && readback.RouteId=route.RouteId
        && readback.AttemptId=route.AttemptId && readback.CandidateId=route.CandidateId
        && readback.RepositoryNodeId=route.RepositoryNodeId
        && readback.Generation=route.Generation && readback.WorkflowRevision=route.WorkflowRevision
    let private nativeDeliveryProofMatches now (route:HostedRoutePlan) (readback:NativeDeliveryReadback) (state:State) =
        let candidateMatches =
            Map.tryFind route.CandidateId state.Candidates
            |> Option.exists(fun candidate ->
                candidate.HeadSha.Equals(readback.CandidateHeadSha,StringComparison.OrdinalIgnoreCase)
                && candidate.HeadSha.Equals(readback.ObservedPullRequestHeadSha,StringComparison.OrdinalIgnoreCase))
        let pullRequestMatches =
            Map.tryFind route.PullRequestOperationId state.HostedEffectReadbacks
            |> Option.exists(fun value ->
                hostedReadbackMatchesRoute route route.PullRequestOperationId value
                && value.Exists && value.ProviderResourceId=readback.PullRequestNodeId
                && value.CandidateHeadSha |> Option.exists(fun head -> head.Equals(readback.CandidateHeadSha,StringComparison.OrdinalIgnoreCase)))
        let mergeMatches =
            Map.tryFind route.MergeOperationId state.HostedEffectReadbacks
            |> Option.exists(fun value ->
                hostedReadbackMatchesRoute route route.MergeOperationId value
                && value.Exists && value.ProviderResourceId=readback.PullRequestNodeId
                && value.CandidateHeadSha |> Option.exists(fun head -> head.Equals(readback.CandidateHeadSha,StringComparison.OrdinalIgnoreCase))
                && value.ResultSha |> Option.exists(fun sha -> sha.Equals(readback.MergeCommitSha,StringComparison.OrdinalIgnoreCase))
                && readback.ObservedAt>=value.ObservedAt)
        readback.OperationId=route.ReadbackOperationId && readback.RouteId=route.RouteId
        && readback.AttemptId=route.AttemptId && readback.CandidateId=route.CandidateId
        && readback.RepositoryNodeId=route.RepositoryNodeId
        && readback.Generation=route.Generation && readback.WorkflowRevision=route.WorkflowRevision
        && readback.ObservedAt>=route.SelectedAt && readback.ObservedAt<=now && readback.Merged
        && validGitObject readback.CandidateHeadSha && validGitObject readback.ObservedPullRequestHeadSha
        && validGitObject readback.MergeCommitSha && candidateMatches && pullRequestMatches && mergeMatches
    let private conflictingUnsettledOperation currentOperation (state: State) =
        state.Operations
        |> Map.exists(fun operationId operation ->
            operationId<>currentOperation
            && match operation with IntentRecorded _|Dispatching _|NeedsObservation _ -> true | Settled _ -> false)
    let private currentRouteAuthorization now (state: State) (route: HostedRoutePlan) (intent: EffectIntent) requireAttempt =
        let currentRevision = state.Snapshot |> Option.exists(fun snapshot -> snapshot.WorkflowRevision=intent.WorkflowRevision)
        let currentGeneration = sameGeneration intent.Generation state.Generation
        let budgetAvailable = budgetAvailable now state
        let reservationReady =
            state.Reservation
            |> Option.exists(fun reservation ->
                let claimReady = intent.Kind=AcquireExternalClaim || hasCurrentClaims state reservation
                reservation.ExpiresAt>now && sameGeneration reservation.Generation route.Generation && claimReady)
        state.Control=Running && state.ReadbackCurrent && currentGeneration && currentRevision && budgetAvailable
        && state.WorkItemId=Some route.WorkItemId && routeIntentMatches route intent
        && Set.isEmpty state.RecoveryObligations && not(conflictingUnsettledOperation intent.OperationId state)
        && (not requireAttempt || state.Attempts |> Map.tryFind route.AttemptId |> Option.exists(fun attempt -> attempt.Status=Active && sameGeneration attempt.Generation route.Generation))
        && reservationReady
        && predecessorReadbackPresent intent.Kind route state
    let private effectAuthorized now state (intent: EffectIntent) =
        let currentRevision = state.Snapshot |> Option.exists(fun snapshot -> snapshot.WorkflowRevision=intent.WorkflowRevision)
        let currentGeneration = sameGeneration intent.Generation state.Generation
        let budgetAvailable = budgetAvailable now state
        match intent.Kind with
        | DispatchRunner ->
            match state.HostedRoute with
            | Some route -> currentRouteAuthorization now state route intent true
            | None ->
                state.Control=Running && currentGeneration && currentRevision && budgetAvailable
                && Set.isEmpty state.RecoveryObligations
                && (state.Attempts |> Map.exists(fun _ attempt -> attempt.Status=Active && sameGeneration attempt.Generation state.Generation))
                && (state.Reservation |> Option.exists(fun reservation -> reservation.ExpiresAt>now && sameGeneration reservation.Generation state.Generation && hasCurrentClaims state reservation))
        | AcquireExternalClaim ->
            match state.HostedRoute with
            | Some route -> currentRouteAuthorization now state route intent false
            | None ->
                state.Control=Running && currentGeneration && currentRevision && budgetAvailable
                && Set.isEmpty state.RecoveryObligations
                && (state.Reservation |> Option.exists(fun reservation -> reservation.ExpiresAt>now && sameGeneration reservation.Generation state.Generation))
        | ReleaseExternalClaim -> currentRevision && (Set.contains intent.ResourceId state.RecoveryObligations || Map.containsKey intent.ResourceId state.ExternalClaims)
        | CancelRunner -> currentRevision && (state.Attempts |> Map.exists(fun _ attempt -> Id.runnerValue attempt.Runner.RunnerId |> string = intent.ResourceId && (match attempt.Status with Active|OutcomeUnknown _ -> true | _ -> false))) && (match state.Control with CancelPending _|Cancelled _|Revoked _ -> true | _ -> false)
        | InspectExternalOperation -> currentRevision
        | StoreCandidate | PublishCandidateBranch | CreatePullRequest | MergePullRequest | ReadNativeDelivery ->
            state.HostedRoute |> Option.exists(fun route -> currentRouteAuthorization now state route intent true)

    let private currentRunnerSession now (state:State) (session:SessionState) =
        let attempt =
            state.Attempts
            |> Map.tryPick(fun _ attempt -> if attempt.SessionId=session.SessionId then Some attempt else None)
        match attempt with
        | Some attempt ->
            not session.Closed && attempt.Status=Active && attempt.Runner.RunnerId=session.RunnerId
            && attempt.Generation=session.Generation && session.Generation=state.Generation
            && attempt.Runner.Generation=state.Generation && attempt.Runner.ExpiresAt>now
            && state.Control=Running && state.ReadbackCurrent && budgetAvailable now state
            && Set.isEmpty state.RecoveryObligations
        | _ -> false

    let evolve state event =
        let revision = nextRevision state.Revision
        match event with
        | WorkAdmitted(s,b) -> {state with WorkItemId=Some s.WorkItemId;Snapshot=Some s;Budget=Some b;SubscriptionBudget=None;Control=Running;Revision=revision}
        | SubscriptionWorkAdmitted(s,b) -> {state with WorkItemId=Some s.WorkItemId;Snapshot=Some s;Budget=None;SubscriptionBudget=Some b;Control=Running;Revision=revision}
        | GenerationAdvanced g -> {state with Generation=g;Revision=revision}
        | ReservationCreated r -> {state with Reservation=Some r;Revision=revision}
        | ReservationReleased(_,_,claims) -> {state with Reservation=None;RecoveryObligations=Set.union state.RecoveryObligations claims;Revision=revision}
        | ClaimObserved c -> {state with ExternalClaims=Map.add c.ClaimId c state.ExternalClaims;Revision=revision}
        | ClaimReleased claimId -> {state with ExternalClaims=Map.remove claimId state.ExternalClaims;RecoveryObligations=Set.remove claimId state.RecoveryObligations;CompensationFailures=Map.remove claimId state.CompensationFailures;Revision=revision}
        | CompensationFailed(claimId,reason) -> {state with RecoveryObligations=Set.add claimId state.RecoveryObligations;CompensationFailures=Map.add claimId reason state.CompensationFailures;Revision=revision}
        | HostedRouteSelected route -> {state with HostedRoute=Some route;Revision=revision}
        | AttemptStarted a -> {state with Attempts=Map.add a.AttemptId a state.Attempts;Revision=revision}
        | RunnerSessionOpened session -> {state with Sessions=Map.add session.SessionId session state.Sessions;Revision=revision}
        | RunnerClientSequenceAccepted(sessionId,sequence) ->
            match Map.tryFind sessionId state.Sessions with
            | Some session -> {state with Sessions=Map.add sessionId {session with LastClientSequence=sequence} state.Sessions;Revision=revision}
            | None -> {state with Revision=revision}
        | RunnerServerSequenceAdvanced(sessionId,sequence) ->
            match Map.tryFind sessionId state.Sessions with
            | Some session -> {state with Sessions=Map.add sessionId {session with LastServerSequence=sequence} state.Sessions;Revision=revision}
            | None -> {state with Revision=revision}
        | RunnerSessionClosed sessionId ->
            match Map.tryFind sessionId state.Sessions with
            | Some session -> {state with Sessions=Map.add sessionId {session with Closed=true} state.Sessions;Revision=revision}
            | None -> {state with Revision=revision}
        | AttemptObserved(id,status) ->
            match Map.tryFind id state.Attempts with
            | Some attempt -> {state with Attempts=Map.add id {attempt with Status=status} state.Attempts;Revision=revision}
            | None -> {state with Revision=revision}
        | BudgetCharged b ->
            match tryAddUse state.Used b with
            | Some used -> {state with Used=used;Revision=revision}
            | None -> invalidOp "persisted budget event overflows"
        | SubscriptionAccountingRecorded accounting -> {state with SubscriptionAccounting=Some accounting;Revision=revision}
        | PausedEvent r -> {state with Control=Paused r;Revision=revision}
        | ResumedEvent -> {state with Control=Running;Revision=revision}
        | StartupPausedEvent r ->
            let control = match state.Control with Running | Paused _ -> Paused r | existing -> existing
            {state with Control=control;ReadbackCurrent=false;Revision=revision}
        | HostedRouteReadbackAccepted _ -> {state with ReadbackCurrent=true;Revision=revision}
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
        | HostedEffectReadbackAccepted readback ->
            {state with HostedEffectReadbacks=Map.add readback.OperationId readback state.HostedEffectReadbacks;Revision=revision}
        | NativeDeliveryReadbackAccepted readback ->
            {state with NativeDeliveryReadbacks=Map.add readback.OperationId readback state.NativeDeliveryReadbacks;Revision=revision}
        | EffectRetryAuthorized id ->
            match Map.tryFind id state.Operations with
            | Some(Settled(intent,ProvenAbsent)) -> {state with Operations=Map.add id (IntentRecorded intent) state.Operations;Revision=revision}
            | _ -> {state with Revision=revision}
        | CommandRecorded r -> {state with CommandReceipts=Map.add r.CommandId r state.CommandReceipts}

    let replay events = List.fold evolve initial events
    let private mkReceipt protocolVersion state id digest disposition detail =
        {CommandId=id;BodySha256=digest;Disposition=disposition;Revision=state.Revision;ProtocolVersion=protocolVersion;Detail=detail}
    let private decideNew now protocolVersion state id digest command =
        let accept events effects detail =
            let projected=List.fold evolve state events
            let receipt=mkReceipt protocolVersion projected id digest Accepted detail
            {Events=events@[CommandRecorded receipt];Effects=effects;Receipt=receipt}
        let reject detail =
            let receipt=mkReceipt protocolVersion state id digest Rejected detail
            {Events=[CommandRecorded receipt];Effects=[];Receipt=receipt}
        let conflict detail =
            let receipt=mkReceipt protocolVersion state id digest Conflict detail
            {Events=[CommandRecorded receipt];Effects=[];Receipt=receipt}
        match command with
        | Admit(s,b) when state.WorkItemId.IsNone && within now b state.Used -> accept [WorkAdmitted(s,b);GenerationAdvanced(nextGeneration state.Generation)] [] "admitted"
        | Admit _ -> reject "already-admitted-or-invalid-budget"
        | AdmitSubscription(s,b) when state.WorkItemId.IsNone && validSubscriptionBudget now b -> accept [SubscriptionWorkAdmitted(s,b);GenerationAdvanced(nextGeneration state.Generation)] [] "subscription-admitted"
        | AdmitSubscription _ -> reject "already-admitted-or-invalid-subscription-budget"
        | Reserve(r,e,claims) when state.Reservation |> Option.exists(fun existing -> existing.ReservationId=r && existing.ExpiresAt=e && existing.RequiredClaimIds=claims) -> accept [] [] "reservation-already-created"
        | Reserve(r,_,_) when state.Reservation |> Option.exists(fun existing -> existing.ReservationId=r) -> conflict "reservation-identity-conflict"
        | Reserve(r,e,claims) when state.Control=Running && state.Reservation.IsNone && e>now && not(Set.isEmpty claims) && Set.forall (String.IsNullOrWhiteSpace >> not) claims -> accept [ReservationCreated{ReservationId=r;Generation=state.Generation;ExpiresAt=e;RequiredClaimIds=claims}] [] "reserved"
        | Reserve _ -> reject "reservation-not-available"
        | ObserveClaim c when state.HostedRoute |> Option.exists(fun route -> c.ClaimId=route.ClaimResourceId)
                              && not(state.HostedRoute |> Option.exists(fun route -> predecessorReadbackPresent DispatchRunner route state)) ->
            reject "hosted-claim-readback-required"
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
        | SelectHostedRoute route ->
            let operations = routeOperations route |> List.map snd
            let validText maximum value = not(String.IsNullOrWhiteSpace value) && value=value.Trim() && value.Length<=maximum
            let valid =
                route.RouteId<>Guid.Empty && state.HostedRoute.IsNone && state.WorkItemId=Some route.WorkItemId
                && route.JobClass="routine-documentation-delivery" && route.Generation=state.Generation
                && state.Snapshot |> Option.exists(fun snapshot -> snapshot.WorkflowRevision=route.WorkflowRevision)
                && route.SelectedAt<=now && validText 128 route.RepositoryNodeId && validText 128 route.ClaimResourceId
                && route.BranchRef.StartsWith("refs/heads/fsgg/pilot/",StringComparison.Ordinal)
                && route.BranchRef.Length<=255 && Set.count(Set.ofList operations)=operations.Length
                && operations |> List.forall(fun operation -> Id.operationValue operation<>Guid.Empty)
                && state.Operations.IsEmpty && state.Attempts.IsEmpty
            if valid then accept [HostedRouteSelected route] [] "hosted-route-selected"
            else reject "invalid-hosted-route"
        | StartAttempt(a,s,r) ->
            match Map.tryFind a state.Attempts with
            | Some existing when existing.SessionId=s && existing.Runner=r -> accept [] [] "attempt-already-started"
            | Some _ -> conflict "attempt-identity-conflict"
            | None ->
                match state.Control,state.Reservation with
                | Running,Some reservation when reservation.ExpiresAt>now && r.ExpiresAt>now && budgetAvailable now state && sameGeneration reservation.Generation state.Generation && sameGeneration r.Generation state.Generation && (state.Attempts |> Map.forall(fun _ attempt -> match attempt.Status with Completed|CancelledByRunner|ReconciledAbsent _ -> true | _ -> false)) && hasCurrentClaims state reservation && (state.HostedRoute |> Option.forall(fun route -> route.AttemptId=a && predecessorReadbackPresent DispatchRunner route state)) ->
                    let attempt={AttemptId=a;SessionId=s;Runner=r;Generation=state.Generation;StartedAt=now;Status=Active}
                    let session:SessionState={SessionId=s;RunnerId=r.RunnerId;Generation=state.Generation;LastClientSequence=0L;LastServerSequence=0L;Closed=false}
                    accept [AttemptStarted attempt;RunnerSessionOpened session] [] "attempt-and-runner-session-started"
                | _ -> reject "dispatch-requires-current-reservation-claim-runner-and-budget"
        | AcceptRunnerMessage(sessionId,clientSequence,emitServerMessage,requestSha256) when validSha requestSha256 ->
            match Map.tryFind sessionId state.Sessions with
            | Some session when currentRunnerSession now state session && clientSequence=session.LastClientSequence+1L ->
                let events =
                    [ RunnerClientSequenceAccepted(sessionId,clientSequence)
                      if emitServerMessage && session.LastServerSequence<Int64.MaxValue then
                          RunnerServerSequenceAdvanced(sessionId,session.LastServerSequence+1L) ]
                if emitServerMessage && session.LastServerSequence=Int64.MaxValue then reject "runner-server-sequence-overflow"
                else accept events [] "runner-message-accepted"
            | Some session when clientSequence<=session.LastClientSequence -> reject "duplicate-runner-message"
            | Some _ -> reject "runner-client-sequence-gap-or-inactive-session"
            | None -> reject "unknown-runner-session"
        | AcceptRunnerMessage _ -> reject "runner-request-digest-invalid"
        | CloseRunnerSession sessionId ->
            match Map.tryFind sessionId state.Sessions with
            | Some session when not session.Closed -> accept [RunnerSessionClosed sessionId] [] "runner-session-closed"
            | Some _ -> accept [] [] "runner-session-already-closed"
            | None -> reject "unknown-runner-session"
        | ObserveAttempt(attemptId,status) ->
            match Map.tryFind attemptId state.Attempts,status with
            | Some _,Active -> reject "observation-cannot-create-active-attempt"
            | Some _,Completed when state.HostedRoute |> Option.exists(fun route ->
                route.AttemptId=attemptId
                && not(state.NativeDeliveryReadbacks |> Map.tryFind route.ReadbackOperationId |> Option.exists(fun readback ->
                    nativeDeliveryProofMatches now route readback state
                    && state.Operations |> Map.tryFind route.ReadbackOperationId |> Option.exists(function
                        | Settled(intent,Applied revision) -> routeIntentMatches route intent && revision=readback.ProviderRevision
                        | _ -> false)))) -> reject "native-delivery-readback-required"
            | Some _,_ -> accept [AttemptObserved(attemptId,status)] [] (match status with OutcomeUnknown _ -> "attempt-awaits-reconciliation" | _ -> "attempt-terminal-observed")
            | None,_ -> reject "unknown-attempt"
        | ChargeBudget delta ->
            match state.Budget,tryAddUse state.Used delta with
            | Some b,Some used when within now b used -> accept [BudgetCharged delta] [] "budget-charged"
            | _ -> reject "budget-exceeded-or-expired"
        | RecordSubscriptionAccounting accounting ->
            let valid = accounting.ObservedAt<=now && accounting.RuntimeSeconds>=0L
                        && accounting.RuntimeWithinBound=(accounting.RuntimeSeconds<=1800L)
                        && validSubscriptionUsage accounting.Usage && validSubscriptionCost accounting.Cost
            if state.SubscriptionBudget.IsSome && valid then accept [SubscriptionAccountingRecorded accounting] [] "subscription-accounting-recorded"
            else reject "subscription-accounting-refused"
        | Pause r when state.Control=Running -> accept [PausedEvent r] [] "paused"
        | Pause _ -> reject "not-running"
        | Resume -> match state.Control with | Paused _ when state.ReadbackCurrent && budgetAvailable now state -> accept [ResumedEvent] [] "resumed" | _ -> reject "resume-refused"
        | RecordStartupPause reason when not(String.IsNullOrWhiteSpace reason) && reason=reason.Trim() ->
            accept [StartupPausedEvent reason] [] "startup-paused-readback-invalidated"
        | RecordStartupPause _ -> reject "invalid-startup-pause"
        | RecordHostedRouteReadback readback ->
            let paused = match state.Control with Paused _ -> true | _ -> false
            let valid =
                paused && state.HostedRoute
                   |> Option.exists(fun route ->
                       readback.RouteId=route.RouteId && readback.WorkItemId=route.WorkItemId
                       && readback.RepositoryNodeId=route.RepositoryNodeId
                       && readback.Generation=route.Generation && readback.Generation=state.Generation
                       && readback.WorkflowRevision=route.WorkflowRevision
                       && readback.ObservedAt>=route.SelectedAt && readback.ObservedAt<=now
                       && not(String.IsNullOrWhiteSpace readback.ProviderRevision)
                       && validSha readback.EvidenceSha256)
            if valid then accept [HostedRouteReadbackAccepted readback] [] "hosted-route-readback-reconnected"
            else reject "hosted-route-readback-invalid-or-stale"
        | RequestCancel r -> match state.Control with | Running|Paused _ -> accept [CancelRequestedEvent r] [] "cancel-requested" | _ -> reject "cancel-refused"
        | ConfirmCancelled r -> match state.Control with | CancelPending _ -> accept [CancelledEvent r;GenerationAdvanced(nextGeneration state.Generation)] [] "cancelled" | _ -> reject "cancel-not-pending"
        | Revoke r -> accept [RevokedEvent r;GenerationAdvanced(nextGeneration state.Generation)] [] "revoked"
        | RecordCandidate(c,proof) when proof.CandidateId=c.CandidateId && proof.ContentSha256.Equals(c.ContentSha256,StringComparison.OrdinalIgnoreCase) && proof.ManifestSha256.Equals(c.ManifestSha256,StringComparison.OrdinalIgnoreCase) && proof.SizeBytes=c.SizeBytes && proof.Location=c.Location && Set.contains proof.StoreSchemaVersion (set[1;2]) && not(String.IsNullOrWhiteSpace proof.StoreId) && validSha proof.StorageReceiptSha256 && proof.VerifiedAt<=now && c.SizeBytes>=0L && c.SizeBytes<=104857600L && c.RetainUntil>now && c.RetainUntil<=now.AddDays 90. && validSha c.ContentSha256 && validSha c.ManifestSha256 && validGitObject c.BaselineSha && validGitObject c.HeadSha && validGitObject c.TreeSha && Set.contains c.MediaType (Set.singleton "application/vnd.fsgg.runner-candidate+zip") && (match c.Location with | ContentAddressedObject key -> key=$"sha256/{c.ContentSha256.ToLowerInvariant()}" | ImmutableRemoteGitRef(repository,commit,qualifiedRef) -> not(String.IsNullOrWhiteSpace repository) && validGitObject commit && commit.Equals(c.HeadSha,StringComparison.OrdinalIgnoreCase) && qualifiedRef.StartsWith("refs/fsgg/candidates/",StringComparison.Ordinal)) && (state.HostedRoute |> Option.forall(fun route -> route.CandidateId=c.CandidateId && state.HostedEffectReadbacks |> Map.tryFind route.CandidateOperationId |> Option.exists(fun readback -> readback.Exists && readback.CandidateHeadSha=Some c.HeadSha && readback.ResultSha=Some c.ContentSha256))) ->
            match Map.tryFind c.CandidateId state.Candidates with | Some x when x=c -> accept [] [] "candidate-already-accepted" | Some _ -> conflict "candidate-identity-conflict" | None -> accept [CandidateAccepted c] [] "candidate-durably-accepted"
        | RecordCandidate _ -> reject "candidate-not-recoverable-or-invalid"
        | RecordEffectIntent i when sameGeneration i.Generation state.Generation ->
            let routeMismatch =
                state.HostedRoute
                |> Option.exists(fun route ->
                    (operationForKind i.Kind route).IsSome && not(routeIntentMatches route i))
            let routeOrderViolation =
                state.HostedRoute
                |> Option.exists(fun route ->
                    operationForKind i.Kind route=Some i.OperationId
                    && (not(predecessorReadbackPresent i.Kind route state)
                        || conflictingUnsettledOperation i.OperationId state))
            let resourceConflict =
                state.Operations
                |> Map.exists(fun operationId operation ->
                    let existing = intentOf operation
                    operationId <> i.OperationId
                    && existing.Kind = i.Kind && existing.ResourceId = i.ResourceId
                    && (existing.Generation=i.Generation || match operation with Settled _ -> false | _ -> true))
            match Map.tryFind i.OperationId state.Operations, routeMismatch, routeOrderViolation, resourceConflict with
            | None, true, _, _ -> reject "effect-outside-hosted-route"
            | None, false, true, _ -> reject "hosted-effect-predecessor-required"
            | None, false, false, true -> conflict "effect-resource-identity-conflict"
            | None, false, false, false -> accept [EffectIntentRecorded i] [] "effect-intent-recorded"
            | Some(IntentRecorded existing),_,_,_|Some(Dispatching existing),_,_,_|Some(NeedsObservation(existing,_)),_,_,_|Some(Settled(existing,_)),_,_,_ when existing=i -> accept [] [] "effect-intent-already-recorded"
            | Some _,_,_,_ -> conflict "operation-identity-conflict"
        | RecordEffectIntent _ -> reject "stale-effect-generation"
        | MarkEffectDispatching operationId ->
            match Map.tryFind operationId state.Operations with | Some(IntentRecorded i) when effectAuthorized now state i -> accept [EffectDispatchStarted operationId] [i] "effect-dispatching" | Some(NeedsObservation _) -> reject "observe-before-retry" | Some _ -> reject "effect-not-authorized" | _ -> reject "effect-not-dispatchable"
        | ObserveEffect(operationId,Unknown reason) ->
            match Map.tryFind operationId state.Operations with | Some(IntentRecorded _)|Some(Dispatching _)|Some(NeedsObservation _) -> accept [EffectObservationRequired(operationId,reason)] [] "observe-before-retry" | _ -> reject "unknown-operation"
        | ObserveEffect(operationId,(Applied _|ProvenAbsent)) when state.HostedRoute |> Option.exists(fun route -> routeKind operationId route |> Option.isSome) -> reject "hosted-effect-readback-required"
        | ObserveEffect(operationId,outcome) ->
            match Map.tryFind operationId state.Operations with | Some(IntentRecorded _)|Some(Dispatching _)|Some(NeedsObservation _) -> accept [EffectSettled(operationId,outcome)] [] "effect-settled" | _ -> reject "unknown-operation"
        | RecordHostedEffectReadback(operationId,readback) ->
            let validText maximum value = not(String.IsNullOrWhiteSpace value) && value=value.Trim() && value.Length<=maximum
            let candidate (route:HostedRoutePlan) = Map.tryFind route.CandidateId state.Candidates
            let common (route:HostedRoutePlan) intent =
                readback.OperationId=operationId && routeIntentMatches route intent
                && readback.RouteId=route.RouteId && readback.AttemptId=route.AttemptId
                && readback.CandidateId=route.CandidateId
                && readback.RepositoryNodeId=route.RepositoryNodeId
                && readback.Generation=route.Generation && readback.Generation=state.Generation
                && readback.WorkflowRevision=route.WorkflowRevision
                && readback.ObservedAt>=route.SelectedAt && readback.ObservedAt<=now
                && (predecessorObservedAt intent.Kind route state |> Option.forall(fun observedAt -> readback.ObservedAt>=observedAt))
                && validText 128 readback.ProviderResourceId && validText 256 readback.ProviderRevision
            let appliedShape (route:HostedRoutePlan) kind =
                match kind with
                | AcquireExternalClaim -> readback.ProviderResourceId=route.ClaimResourceId && readback.CandidateHeadSha.IsNone && readback.ResultSha.IsNone
                | DispatchRunner -> readback.ProviderResourceId=(Id.attemptValue route.AttemptId |> string) && readback.CandidateHeadSha.IsNone && readback.ResultSha.IsNone
                // Storage readback precedes RecordCandidate: it binds the provider's
                // immutable bytes; the following command independently proves the
                // Main-owned ICandidateStore receipt/readback. Requiring Candidates here
                // would make that two-step durable transition circular.
                | StoreCandidate -> readback.ProviderResourceId=(Id.candidateValue route.CandidateId |> string) && readback.CandidateHeadSha |> Option.exists validGitObject && readback.ResultSha |> Option.exists validSha
                | PublishCandidateBranch ->
                    readback.ProviderResourceId=route.BranchRef && candidate route |> Option.exists(fun value -> readback.CandidateHeadSha=Some value.HeadSha && readback.ResultSha=Some value.HeadSha)
                | CreatePullRequest -> candidate route |> Option.exists(fun value -> readback.CandidateHeadSha=Some value.HeadSha) && readback.ResultSha.IsNone
                | MergePullRequest ->
                    let pullRequest = Map.tryFind route.PullRequestOperationId state.HostedEffectReadbacks
                    candidate route |> Option.exists(fun value -> readback.CandidateHeadSha=Some value.HeadSha)
                    && pullRequest |> Option.exists(fun value -> value.Exists && value.ProviderResourceId=readback.ProviderResourceId)
                    && readback.ResultSha |> Option.exists validGitObject
                | _ -> false
            match state.HostedRoute,Map.tryFind operationId state.Operations with
            | Some route,(Some(Dispatching intent)|Some(NeedsObservation(intent,_))) when operationId<>route.ReadbackOperationId && common route intent && predecessorReadbackPresent intent.Kind route state ->
                if readback.Exists && appliedShape route intent.Kind then
                    accept [HostedEffectReadbackAccepted readback;EffectSettled(operationId,Applied readback.ProviderRevision)] [] "hosted-effect-readback-accepted"
                elif not readback.Exists && readback.CandidateHeadSha.IsNone && readback.ResultSha.IsNone && readback.ProviderResourceId=intent.ResourceId then
                    accept [HostedEffectReadbackAccepted readback;EffectSettled(operationId,ProvenAbsent)] [] "hosted-effect-absence-accepted"
                else reject "hosted-effect-readback-invalid-or-stale"
            | _ -> reject "hosted-effect-readback-invalid-or-stale"
        | RecordNativeDeliveryReadback(operationId,readback) ->
            let validNode value = not(String.IsNullOrWhiteSpace value) && value=value.Trim() && value.Length<=128
            match state.HostedRoute,Map.tryFind operationId state.Operations with
            | Some route,(Some(Dispatching intent)|Some(NeedsObservation(intent,_)))
                when intent.Kind=ReadNativeDelivery && operationId=route.ReadbackOperationId
                     && routeIntentMatches route intent && readback.OperationId=operationId
                     && nativeDeliveryProofMatches now route readback state
                     && readback.Generation=state.Generation && validNode readback.RepositoryNodeId
                     && validNode readback.PullRequestNodeId && validNode readback.ProviderRevision ->
                accept [NativeDeliveryReadbackAccepted readback;EffectSettled(operationId,Applied readback.ProviderRevision)] [] "native-delivery-readback-accepted"
            | _ -> reject "native-delivery-readback-invalid-or-stale"
        | AuthorizeEffectRetry operationId ->
            match Map.tryFind operationId state.Operations with | Some(Settled(_,ProvenAbsent)) -> accept [EffectRetryAuthorized operationId] [] "same-operation-retry-authorized" | _ -> reject "retry-requires-proven-absent"

    let private invariant (value:int64) = value.ToString(CultureInfo.InvariantCulture)
    let private frame values =
        values |> Seq.map(fun value -> let v=if isNull value then "" else value in $"{v.Length}:{v};") |> String.concat ""
    let private generationText generation = generation |> Id.generationValue |> invariant
    let private revisionText revision = revision |> Id.revisionValue |> invariant
    let private timeText (value:DateTimeOffset) = value.ToUniversalTime().Ticks |> invariant
    let private subscriptionUsageParts = function
        | TokensObserved(value,provenance) -> ["observed";invariant value;provenance]
        | TokensUnknown provenance -> ["unknown";"";provenance]
    let private subscriptionCostParts cost =
        [cost.InvocationState;cost.InvocationProvenance;cost.BroaderAttributionState;cost.BroaderAttributionProvenance]
    let private subscriptionBudgetParts budget =
        [budget.Schema;invariant budget.AttemptLimit;invariant (int64 budget.MaximumRuntime.TotalSeconds);timeText budget.ExecutionDeadline]
        @ subscriptionUsageParts budget.Usage @ subscriptionCostParts budget.Cost
    let private candidateParts (candidate:CandidateArtifact) =
        let location = match candidate.Location with | ContentAddressedObject key -> frame["object";key] | ImmutableRemoteGitRef(repo,commit,reference) -> frame["git";repo;commit;reference]
        [Id.candidateValue candidate.CandidateId |> string;candidate.BaselineSha;candidate.HeadSha;candidate.TreeSha;candidate.ManifestSha256;candidate.ContentSha256;candidate.MediaType;invariant candidate.SizeBytes;timeText candidate.RetainUntil;location]
    let private routeParts (route:HostedRoutePlan) =
        [string route.RouteId;Convert.ToBase64String(WorkItemIdentity.canonicalBytes route.WorkItemId);route.JobClass
         Id.attemptValue route.AttemptId |> string;Id.candidateValue route.CandidateId |> string
         route.RepositoryNodeId;route.BranchRef;route.ClaimResourceId
         Id.operationValue route.ClaimOperationId |> string;Id.operationValue route.ProcessOperationId |> string
         Id.operationValue route.CandidateOperationId |> string;Id.operationValue route.BranchOperationId |> string
         Id.operationValue route.PullRequestOperationId |> string;Id.operationValue route.MergeOperationId |> string
         Id.operationValue route.ReadbackOperationId |> string;generationText route.Generation
         revisionText route.WorkflowRevision;timeText route.SelectedAt]
    let canonicalCommandBytes command =
        let parts =
            match command with
            | Admit(s,b) -> ["admit";Convert.ToBase64String(WorkItemIdentity.canonicalBytes s.WorkItemId);Id.projectValue s.ProjectId |> string;revisionText s.WorkflowRevision;s.CanonicalSha256;frame(List.sort s.BoardMembershipIds);timeText s.CapturedAt;invariant b.TokenLimit;invariant b.RuntimeSecondsLimit;invariant b.CostMicrosLimit;timeText b.Deadline]
            | AdmitSubscription(s,b) -> ["admit-subscription";Convert.ToBase64String(WorkItemIdentity.canonicalBytes s.WorkItemId);Id.projectValue s.ProjectId |> string;revisionText s.WorkflowRevision;s.CanonicalSha256;frame(List.sort s.BoardMembershipIds);timeText s.CapturedAt] @ subscriptionBudgetParts b
            | Reserve(id,expires,claims) -> ["reserve";Id.reservationValue id |> string;timeText expires;frame(Set.toList claims)]
            | ObserveClaim c -> ["claim";c.ClaimId;generationText c.Generation;revisionText c.WorkflowRevision;timeText c.ObservedAt]
            | ReleaseReservation reason -> ["release-reservation";reason]
            | ObserveClaimReleased claim -> ["claim-released";claim]
            | RecordCompensationFailure(claim,reason) -> ["compensation-failed";claim;reason]
            | SelectHostedRoute route -> "select-hosted-route"::routeParts route
            | StartAttempt(a,s,r) -> ["start-attempt";Id.attemptValue a |> string;Id.sessionValue s |> string;Id.runnerValue r.RunnerId |> string;r.PrincipalId;r.FingerprintSha256;generationText r.Generation;timeText r.ExpiresAt]
            | ObserveAttempt(a,status) -> ["observe-attempt";Id.attemptValue a |> string;sprintf "%A" status]
            | AcceptRunnerMessage(sessionId,sequence,emitServer,requestSha256) -> ["accept-runner-message";Id.sessionValue sessionId |> string;invariant sequence;string emitServer;requestSha256]
            | CloseRunnerSession sessionId -> ["close-runner-session";Id.sessionValue sessionId |> string]
            | ChargeBudget b -> ["charge";invariant b.Tokens;invariant b.RuntimeSeconds;invariant b.CostMicros]
            | RecordSubscriptionAccounting accounting -> ["subscription-accounting";timeText accounting.ObservedAt;invariant accounting.RuntimeSeconds;string accounting.RuntimeWithinBound] @ subscriptionUsageParts accounting.Usage @ subscriptionCostParts accounting.Cost
            | Pause reason -> ["pause";reason] | Resume -> ["resume"] | RequestCancel reason -> ["request-cancel";reason]
            | RecordStartupPause reason -> ["startup-pause";reason]
            | RecordHostedRouteReadback readback ->
                ["hosted-route-readback";string readback.RouteId;Convert.ToBase64String(WorkItemIdentity.canonicalBytes readback.WorkItemId)
                 readback.RepositoryNodeId;readback.ProviderRevision;readback.EvidenceSha256
                 generationText readback.Generation;revisionText readback.WorkflowRevision;timeText readback.ObservedAt]
            | ConfirmCancelled reason -> ["confirm-cancel";reason] | Revoke reason -> ["revoke";reason]
            | RecordCandidate(c,proof) -> "candidate"::candidateParts c @ [Id.candidateValue proof.CandidateId |> string;proof.ContentSha256;proof.ManifestSha256;invariant proof.SizeBytes;sprintf "%A" proof.Location;proof.StoreId;string proof.StoreSchemaVersion;proof.StorageReceiptSha256;timeText proof.VerifiedAt]
            | RecordEffectIntent i -> ["effect";Id.operationValue i.OperationId |> string;string i.Kind;generationText i.Generation;revisionText i.WorkflowRevision;i.ResourceId;i.PayloadSha256]
            | MarkEffectDispatching id -> ["dispatch-effect";Id.operationValue id |> string]
            | ObserveEffect(id,outcome) -> ["observe-effect";Id.operationValue id |> string;sprintf "%A" outcome]
            | RecordHostedEffectReadback(id,readback) ->
                ["hosted-effect-readback";Id.operationValue id |> string;Id.operationValue readback.OperationId |> string
                 string readback.RouteId;Id.attemptValue readback.AttemptId |> string;Id.candidateValue readback.CandidateId |> string
                 readback.RepositoryNodeId;readback.ProviderResourceId;Option.defaultValue "" readback.CandidateHeadSha
                 Option.defaultValue "" readback.ResultSha;readback.ProviderRevision;generationText readback.Generation
                 revisionText readback.WorkflowRevision;timeText readback.ObservedAt;string readback.Exists]
            | RecordNativeDeliveryReadback(id,readback) ->
                ["native-delivery-readback";Id.operationValue id |> string;Id.operationValue readback.OperationId |> string
                 string readback.RouteId;Id.attemptValue readback.AttemptId |> string;Id.candidateValue readback.CandidateId |> string
                 readback.RepositoryNodeId;readback.PullRequestNodeId;readback.CandidateHeadSha
                 readback.ObservedPullRequestHeadSha;readback.MergeCommitSha;readback.ProviderRevision
                 generationText readback.Generation;revisionText readback.WorkflowRevision;timeText readback.ObservedAt;string readback.Merged]
            | AuthorizeEffectRetry id -> ["authorize-effect-retry";Id.operationValue id |> string]
        frame parts |> Encoding.UTF8.GetBytes
    let canonicalCommandSha256 command = canonicalCommandBytes command |> SHA256.HashData |> Convert.ToHexString |> fun x -> x.ToLowerInvariant()
    let canonicalEnvelopeBytes envelope =
        let major,minor=Id.protocolVersionValue envelope.ProtocolVersion
        frame
            [ Id.commandValue envelope.CommandId |> string; string major; string minor; revisionText envelope.ExpectedRevision
              generationText envelope.ExpectedGeneration; envelope.PrincipalId
              envelope.SessionId |> Option.map(Id.sessionValue >> string) |> Option.defaultValue ""
              timeText envelope.IssuedAt; timeText envelope.ExpiresAt
              Convert.ToBase64String(canonicalCommandBytes envelope.Command) ]
        |> Encoding.UTF8.GetBytes
    let canonicalEnvelopeSha256 envelope =
        canonicalEnvelopeBytes envelope |> SHA256.HashData |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let decide now state envelope =
        let digest=canonicalEnvelopeSha256 envelope
        let requiredProtocol =
            match envelope.Command with
            | AdmitSubscription _ | RecordSubscriptionAccounting _ -> ProtocolVersion(2,0)
            | _ -> ProtocolVersion(1,0)
        match Map.tryFind envelope.CommandId state.CommandReceipts with
        | Some prior when prior.BodySha256=digest -> {Events=[];Effects=[];Receipt={prior with Disposition=Duplicate;Detail="duplicate-command"}}
        | Some prior -> {Events=[];Effects=[];Receipt={prior with Disposition=Conflict;Detail="command-identity-conflict"}}
        | None when envelope.ProtocolVersion<>requiredProtocol ->
            {Events=[];Effects=[];Receipt=mkReceipt envelope.ProtocolVersion state envelope.CommandId digest Rejected "unsupported-command-version"}
        | None when String.IsNullOrWhiteSpace envelope.PrincipalId || envelope.ExpiresAt<now || envelope.IssuedAt>now ->
            {Events=[];Effects=[];Receipt=mkReceipt envelope.ProtocolVersion state envelope.CommandId digest Rejected "invalid-or-expired-command-envelope"}
        | None when envelope.ExpectedRevision<>state.Revision ->
            {Events=[];Effects=[];Receipt=mkReceipt envelope.ProtocolVersion state envelope.CommandId digest Rejected "stale-workflow-revision"}
        | None when envelope.ExpectedGeneration<>state.Generation ->
            {Events=[];Effects=[];Receipt=mkReceipt envelope.ProtocolVersion state envelope.CommandId digest Rejected "stale-generation"}
        | None -> decideNew now envelope.ProtocolVersion state envelope.CommandId digest envelope.Command

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
