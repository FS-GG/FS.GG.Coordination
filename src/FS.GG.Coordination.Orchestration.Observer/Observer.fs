namespace FS.GG.Coordination.Orchestration.Observer

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Text.Json.Serialization
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.GitHub

type ProposalId = private ProposalId of Guid

[<RequireQualifiedAccess>]
module ProposalId =
    let create value = ProposalId value
    let value (ProposalId value) = value

type PlanningBudget =
    { TokenLimit: int64
      RuntimeSecondsLimit: int64
      CostMicrosLimit: int64
      Deadline: DateTimeOffset }

type PlanningUse =
    { Tokens: int64
      RuntimeSeconds: int64
      CostMicros: int64 }

type ObservationProvenance =
    { Provider: string
      QuerySha256: string
      EvidenceSha256: string
      CapturedAt: DateTimeOffset }

type ObservedWorkItem =
    { Identity: WorkItemId
      MembershipItemId: string
      Archived: bool }

type ProjectObservationSnapshot =
    { ProjectId: ProjectId
      SourceRevision: string
      WorkflowRevision: WorkflowRevision
      Generation: Generation
      ObservationSha256: string
      Provenance: ObservationProvenance
      WorkItems: ObservedWorkItem list
      NonWorkItemCount: int }

type PlanningAttemptStatus =
    | PlanningActive
    | PlanningOutcomeUnknown of string
    | PlanningCompleted of PlanningUse
    | PlanningReservationConsumed of string

type PlanningAttempt =
    { AttemptId: AttemptId
      ObservationSha256: string
      WorkflowRevision: WorkflowRevision
      Generation: Generation
      Reserved: PlanningUse
      StartedAt: DateTimeOffset
      Status: PlanningAttemptStatus }

type ProposedActionKind = InspectWorkItem | CompareStatus | RecommendRoutineWork

type ProposedAction =
    { Kind: ProposedActionKind
      WorkItem: WorkItemId
      ParametersSha256: string }

type PlanProposal =
    { ProposalId: ProposalId
      AttemptId: AttemptId
      ObservationSha256: string
      WorkflowRevision: WorkflowRevision
      Generation: Generation
      Scope: string
      NarrativeSha256: string
      Actions: ProposedAction list
      PlanSha256: string
      ProposedAt: DateTimeOffset }

type ApprovalKind = ExplicitApproval | RoutinePreauthorization of policyId: string

type ProposalApproval =
    { ProposalId: ProposalId
      PlanSha256: string
      PlanningBudgetSha256: string
      PrincipalId: string
      Scope: string
      ExpectedWorkflowRevision: WorkflowRevision
      ExpectedGeneration: Generation
      CommandId: CommandId
      CommandBodySha256: string
      Kind: ApprovalKind
      ApprovalSha256: string
      ApprovedAt: DateTimeOffset }

type ReadbackProvenance =
    { Provider: string
      SourceRevision: string
      EvidenceSha256: string
      ObservedAt: DateTimeOffset }

type DurableCommandAcceptance =
    { ProposalId: ProposalId
      ApprovalSha256: string
      Receipt: CommandReceipt
      Provenance: ReadbackProvenance
      AcceptedAt: DateTimeOffset }

type KnownEffectResult = EffectApplied of providerRevision: string | EffectProvenAbsent | EffectRefused of reason: string

type DurableEffectCompletion =
    { CommandId: CommandId
      OperationId: OperationId
      Result: KnownEffectResult
      Provenance: ReadbackProvenance
      CompletedAt: DateTimeOffset }

type ConversationRole = Operator | PlanningAgent

type ConversationEntry =
    { EntryId: Guid
      Role: ConversationRole
      BodySha256: string
      RecordedAt: DateTimeOffset }

type ObserverState =
    { SessionId: SessionId option
      ProjectId: ProjectId option
      Sequence: int64
      Budget: PlanningBudget option
      Used: PlanningUse
      Reserved: PlanningUse
      Observation: ProjectObservationSnapshot option
      Attempts: Map<AttemptId, PlanningAttempt>
      Proposals: Map<ProposalId, PlanProposal>
      CurrentProposal: ProposalId option
      Approvals: Map<ProposalId, ProposalApproval>
      Acceptances: Map<CommandId, DurableCommandAcceptance>
      Effects: Map<OperationId, DurableEffectCompletion>
      Conversation: ConversationEntry list }

type ObserverEvent =
    | SessionOpened of SessionId * ProjectId * PlanningBudget
    | ConversationRecorded of ConversationEntry
    | ProjectObservationRecorded of ProjectObservationSnapshot
    | PlanningAttemptStarted of PlanningAttempt
    | PlanningAttemptBecameUnknown of AttemptId * string
    | PlanningAttemptCompleted of AttemptId * PlanningUse
    | UnknownPlanningReservationConsumed of AttemptId * string
    | ProposalRecorded of PlanProposal
    | ProposalApproved of ProposalApproval
    | CommandAcceptanceRecorded of DurableCommandAcceptance
    | EffectCompletionRecorded of DurableEffectCompletion

type ProposalInput =
    { ProposalId: ProposalId
      AttemptId: AttemptId
      ObservationSha256: string
      WorkflowRevision: WorkflowRevision
      Generation: Generation
      Scope: string
      NarrativeSha256: string
      Actions: ProposedAction list
      ProposedAt: DateTimeOffset }

type ApprovalInput =
    { ProposalId: ProposalId
      PlanSha256: string
      PlanningBudgetSha256: string
      PrincipalId: string
      Scope: string
      ExpectedWorkflowRevision: WorkflowRevision
      ExpectedGeneration: Generation
      CommandId: CommandId
      CommandBodySha256: string
      Kind: ApprovalKind
      ApprovedAt: DateTimeOffset }

type ObserverCommand =
    | OpenSession of SessionId * ProjectId * PlanningBudget
    | RecordConversation of ConversationEntry
    | RecordProjectObservation of ProjectObservationSnapshot
    | StartPlanningAttempt of AttemptId * PlanningUse * DateTimeOffset
    | MarkPlanningAttemptUnknown of AttemptId * string
    | CompletePlanningAttempt of AttemptId * PlanningUse * ProposalInput
    | ConsumeUnknownPlanningReservation of AttemptId * string
    | ApproveProposal of ApprovalInput
    | RecordCommandAcceptance of DurableCommandAcceptance
    | RecordEffectCompletion of DurableEffectCompletion

type ObserverCommandEnvelope =
    { CommandId: CommandId
      ExpectedSequence: int64
      PrincipalId: string
      IssuedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset
      Command: ObserverCommand }

type ObserverReceiptDisposition = ObserverAccepted | ObserverRejected

type ObserverReceipt =
    { CommandId: CommandId
      BodySha256: string
      Disposition: ObserverReceiptDisposition
      Sequence: int64
      Detail: string }

type ObserverDecision = { Events: ObserverEvent list; Receipt: ObserverReceipt }

[<RequireQualifiedAccess>]
module Observer =
    let initial =
        { SessionId = None
          ProjectId = None
          Sequence = 0L
          Budget = None
          Used = { Tokens = 0L; RuntimeSeconds = 0L; CostMicros = 0L }
          Reserved = { Tokens = 0L; RuntimeSeconds = 0L; CostMicros = 0L }
          Observation = None
          Attempts = Map.empty
          Proposals = Map.empty
          CurrentProposal = None
          Approvals = Map.empty
          Acceptances = Map.empty
          Effects = Map.empty
          Conversation = [] }

    let private digest (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let private validSha value =
        not (String.IsNullOrWhiteSpace value) && value.Length = 64 && value |> Seq.forall Uri.IsHexDigit

    let private validText value = not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private validReadback now value =
        validText value.Provider && validText value.SourceRevision && validSha value.EvidenceSha256 && value.ObservedAt <= now

    let private validEffectResult = function
        | EffectApplied revision -> validText revision
        | EffectProvenAbsent -> true
        | EffectRefused reason -> validText reason

    let private add left right =
        if left < 0L || right < 0L || left > Int64.MaxValue - right then None else Some(left + right)

    let private addUse left right =
        match add left.Tokens right.Tokens, add left.RuntimeSeconds right.RuntimeSeconds, add left.CostMicros right.CostMicros with
        | Some tokens, Some runtime, Some cost -> Some { Tokens = tokens; RuntimeSeconds = runtime; CostMicros = cost }
        | _ -> None

    let private subtractUse left right =
        if right.Tokens < 0L || right.RuntimeSeconds < 0L || right.CostMicros < 0L
           || right.Tokens > left.Tokens || right.RuntimeSeconds > left.RuntimeSeconds || right.CostMicros > left.CostMicros then None
        else Some { Tokens = left.Tokens - right.Tokens; RuntimeSeconds = left.RuntimeSeconds - right.RuntimeSeconds; CostMicros = left.CostMicros - right.CostMicros }

    let private within budget used reserved now =
        match addUse used reserved with
        | Some committed -> now <= budget.Deadline && committed.Tokens <= budget.TokenLimit && committed.RuntimeSeconds <= budget.RuntimeSecondsLimit && committed.CostMicros <= budget.CostMicrosLimit
        | None -> false

    let private validUse value = value.Tokens >= 0L && value.RuntimeSeconds >= 0L && value.CostMicros >= 0L

    let private positiveUse value = validUse value && (value.Tokens > 0L || value.RuntimeSeconds > 0L || value.CostMicros > 0L)

    let private validBudget now value =
        value.TokenLimit >= 0L && value.RuntimeSecondsLimit >= 0L && value.CostMicrosLimit >= 0L && value.Deadline > now
        && (value.TokenLimit > 0L || value.RuntimeSecondsLimit > 0L || value.CostMicrosLimit > 0L)

    let internal tryAddRuntime (startedAt: DateTimeOffset) runtimeSeconds =
        let availableTicks = (DateTimeOffset.MaxValue - startedAt).Ticks
        if runtimeSeconds < 0L || runtimeSeconds > availableTicks / TimeSpan.TicksPerSecond then None
        else Some(startedAt.AddTicks(runtimeSeconds * TimeSpan.TicksPerSecond))

    let private canonicalFields (fields: string list) =
        use stream = new IO.MemoryStream()
        use writer = new IO.BinaryWriter(stream, Encoding.UTF8, true)
        for field in fields do
            if isNull field then writer.Write -1
            else
                let bytes = Encoding.UTF8.GetBytes field
                writer.Write bytes.Length
                writer.Write bytes
        writer.Flush()
        stream.ToArray()

    let budgetSha256 (budget: PlanningBudget) =
        canonicalFields
            [ string budget.TokenLimit
              string budget.RuntimeSecondsLimit
              string budget.CostMicrosLimit
              budget.Deadline.ToUniversalTime().ToString("O") ]
        |> digest

    let observationSha256 (observation: ProjectObservationSnapshot) =
        let workItems = observation.WorkItems |> List.sortBy (fun item -> WorkItemIdentity.persistenceId item.Identity, item.MembershipItemId)
        let itemFields =
            workItems
            |> List.collect (fun item -> [ WorkItemIdentity.persistenceId item.Identity; item.MembershipItemId; string item.Archived ])
        canonicalFields
            ([ string (Id.projectValue observation.ProjectId)
               observation.SourceRevision
               string (Id.revisionValue observation.WorkflowRevision)
               string (Id.generationValue observation.Generation)
               observation.Provenance.Provider
               observation.Provenance.QuerySha256.ToLowerInvariant()
               observation.Provenance.EvidenceSha256.ToLowerInvariant()
               observation.Provenance.CapturedAt.ToUniversalTime().ToString("O")
               string workItems.Length
               string observation.NonWorkItemCount ] @ itemFields)
        |> digest

    let proposalSha256 (input: ProposalInput) =
        let actionFields =
            input.Actions
            |> List.collect (fun action -> [ string action.Kind; WorkItemIdentity.persistenceId action.WorkItem; action.ParametersSha256.ToLowerInvariant() ])
        canonicalFields
            ([ string (ProposalId.value input.ProposalId)
               string (Id.attemptValue input.AttemptId)
               input.ObservationSha256.ToLowerInvariant()
               string (Id.revisionValue input.WorkflowRevision)
               string (Id.generationValue input.Generation)
               input.Scope
               input.NarrativeSha256.ToLowerInvariant()
               string input.Actions.Length ] @ actionFields)
        |> digest

    let approvalSha256 (input: ApprovalInput) =
        let kind = match input.Kind with ExplicitApproval -> "explicit" | RoutinePreauthorization policy -> $"routine:{policy}"
        canonicalFields
            [ string (ProposalId.value input.ProposalId)
              input.PlanSha256.ToLowerInvariant()
              input.PlanningBudgetSha256.ToLowerInvariant()
              input.PrincipalId
              input.Scope
              string (Id.revisionValue input.ExpectedWorkflowRevision)
              string (Id.generationValue input.ExpectedGeneration)
              string (Id.commandValue input.CommandId)
              input.CommandBodySha256.ToLowerInvariant()
              kind ]
        |> digest

    let evolve (state: ObserverState) (eventValue: ObserverEvent) : ObserverState =
        let next = state.Sequence + 1L
        match eventValue with
        | SessionOpened(sessionId, projectId, budget) -> { state with SessionId = Some sessionId; ProjectId = Some projectId; Budget = Some budget; Sequence = next }
        | ConversationRecorded entry -> { state with Conversation = state.Conversation @ [ entry ]; Sequence = next }
        | ProjectObservationRecorded observation ->
            let current = state.Observation |> Option.map _.ObservationSha256
            { state with Observation = Some observation; CurrentProposal = (if current = Some observation.ObservationSha256 then state.CurrentProposal else None); Sequence = next }
        | PlanningAttemptStarted attempt ->
            let reserved = addUse state.Reserved attempt.Reserved |> Option.defaultWith (fun () -> invalidOp "planning reservation overflow")
            { state with Attempts = Map.add attempt.AttemptId attempt state.Attempts; Reserved = reserved; Sequence = next }
        | PlanningAttemptBecameUnknown(attemptId, reason) ->
            let attempt = Map.find attemptId state.Attempts
            { state with Attempts = Map.add attemptId { attempt with Status = PlanningOutcomeUnknown reason } state.Attempts; Sequence = next }
        | PlanningAttemptCompleted(attemptId, used) ->
            let attempt = Map.find attemptId state.Attempts
            let reserved = subtractUse state.Reserved attempt.Reserved |> Option.defaultWith (fun () -> invalidOp "planning reservation underflow")
            let committed = addUse state.Used used |> Option.defaultWith (fun () -> invalidOp "planning use overflow")
            { state with Attempts = Map.add attemptId { attempt with Status = PlanningCompleted used } state.Attempts; Reserved = reserved; Used = committed; Sequence = next }
        | UnknownPlanningReservationConsumed(attemptId, reason) ->
            let attempt = Map.find attemptId state.Attempts
            let reserved = subtractUse state.Reserved attempt.Reserved |> Option.defaultWith (fun () -> invalidOp "planning reservation underflow")
            let committed = addUse state.Used attempt.Reserved |> Option.defaultWith (fun () -> invalidOp "planning use overflow")
            { state with Attempts = Map.add attemptId { attempt with Status = PlanningReservationConsumed reason } state.Attempts; Reserved = reserved; Used = committed; Sequence = next }
        | ProposalRecorded proposal -> { state with Proposals = Map.add proposal.ProposalId proposal state.Proposals; CurrentProposal = Some proposal.ProposalId; Sequence = next }
        | ProposalApproved approval -> { state with Approvals = Map.add approval.ProposalId approval state.Approvals; Sequence = next }
        | CommandAcceptanceRecorded acceptance -> { state with Acceptances = Map.add acceptance.Receipt.CommandId acceptance state.Acceptances; Sequence = next }
        | EffectCompletionRecorded completion -> { state with Effects = Map.add completion.OperationId completion state.Effects; Sequence = next }

    let private reject (envelope: ObserverCommandEnvelope) body (state: ObserverState) detail : ObserverDecision =
        { Events = []
          Receipt = { CommandId = envelope.CommandId; BodySha256 = body; Disposition = ObserverRejected; Sequence = state.Sequence; Detail = detail } }

    let private accept (envelope: ObserverCommandEnvelope) body (state: ObserverState) (events: ObserverEvent list) detail : ObserverDecision =
        { Events = events
          Receipt = { CommandId = envelope.CommandId; BodySha256 = body; Disposition = ObserverAccepted; Sequence = state.Sequence + int64 events.Length; Detail = detail } }

    let private options =
        let value = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
        value.Converters.Add(JsonFSharpConverter())
        value

    let commandSha256 (envelope: ObserverCommandEnvelope) =
        let commandBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Command, options)
        canonicalFields
            [ string (Id.commandValue envelope.CommandId)
              string envelope.ExpectedSequence
              envelope.PrincipalId
              envelope.IssuedAt.ToUniversalTime().ToString("O")
              envelope.ExpiresAt.ToUniversalTime().ToString("O")
              Convert.ToBase64String commandBytes ]
        |> digest

    let decide now (state: ObserverState) (envelope: ObserverCommandEnvelope) =
        let body = commandSha256 envelope
        if envelope.ExpectedSequence <> state.Sequence then reject envelope body state "stale-observer-sequence"
        elif String.IsNullOrWhiteSpace envelope.PrincipalId || envelope.IssuedAt > now || envelope.ExpiresAt < now || envelope.ExpiresAt < envelope.IssuedAt then reject envelope body state "invalid-or-expired-observer-command"
        else
            match envelope.Command with
            | OpenSession(sessionId, projectId, budget) ->
                if state.SessionId.IsSome then reject envelope body state "session-already-open"
                elif not (validBudget now budget) then reject envelope body state "invalid-planning-budget"
                else accept envelope body state [ SessionOpened(sessionId, projectId, budget) ] "session-opened"
            | _ when state.SessionId.IsNone -> reject envelope body state "session-not-open"
            | RecordConversation entry ->
                if entry.EntryId = Guid.Empty || not (validSha entry.BodySha256) || entry.RecordedAt > now then reject envelope body state "invalid-conversation-entry"
                elif state.Conversation |> List.exists (fun prior -> prior.EntryId = entry.EntryId) then reject envelope body state "duplicate-conversation-entry"
                else accept envelope body state [ ConversationRecorded { entry with BodySha256 = entry.BodySha256.ToLowerInvariant() } ] "conversation-recorded"
            | RecordProjectObservation observation ->
                let canonicalObservation =
                    try Some(observationSha256 observation)
                    with _ -> None
                let regressed =
                    state.Observation
                    |> Option.exists (fun prior ->
                        observation.Provenance.CapturedAt < prior.Provenance.CapturedAt
                        || Id.generationValue observation.Generation < Id.generationValue prior.Generation
                        || Id.revisionValue observation.WorkflowRevision < Id.revisionValue prior.WorkflowRevision)
                if state.ProjectId <> Some observation.ProjectId || not (validText observation.SourceRevision) || not (validSha observation.ObservationSha256)
                   || not (validText observation.Provenance.Provider) || not (validSha observation.Provenance.QuerySha256) || not (validSha observation.Provenance.EvidenceSha256)
                   || observation.Provenance.CapturedAt > now || observation.NonWorkItemCount < 0
                   || observation.WorkItems |> List.exists (fun item -> not (validText item.MembershipItemId))
                   || (observation.WorkItems |> List.groupBy _.MembershipItemId |> List.exists (fun (_, values) -> values.Length <> 1))
                   || canonicalObservation <> Some(observation.ObservationSha256.ToLowerInvariant()) then
                    reject envelope body state "invalid-project-observation"
                elif regressed then reject envelope body state "stale-project-observation"
                else accept envelope body state [ ProjectObservationRecorded { observation with ObservationSha256 = canonicalObservation.Value } ] "project-observation-recorded"
            | StartPlanningAttempt(attemptId, reservation, startedAt) ->
                match state.Budget, state.Observation with
                | Some budget, Some observation when positiveUse reservation && within budget state.Used (addUse state.Reserved reservation |> Option.defaultValue { Tokens = Int64.MaxValue; RuntimeSeconds = Int64.MaxValue; CostMicros = Int64.MaxValue }) now
                                                     && startedAt <= now
                                                     && tryAddRuntime startedAt reservation.RuntimeSeconds |> Option.isSome
                                                     && not (Map.containsKey attemptId state.Attempts)
                                                     && not (state.Attempts |> Map.exists (fun _ attempt -> match attempt.Status with PlanningActive | PlanningOutcomeUnknown _ -> true | _ -> false)) ->
                    let attempt = { AttemptId = attemptId; ObservationSha256 = observation.ObservationSha256; WorkflowRevision = observation.WorkflowRevision; Generation = observation.Generation; Reserved = reservation; StartedAt = startedAt; Status = PlanningActive }
                    accept envelope body state [ PlanningAttemptStarted attempt ] "planning-attempt-reserved"
                | _ -> reject envelope body state "planning-attempt-not-authorized"
            | MarkPlanningAttemptUnknown(attemptId, reason) ->
                match Map.tryFind attemptId state.Attempts with
                | Some attempt when attempt.Status = PlanningActive && not (String.IsNullOrWhiteSpace reason) -> accept envelope body state [ PlanningAttemptBecameUnknown(attemptId, reason) ] "planning-outcome-unknown"
                | _ -> reject envelope body state "planning-attempt-not-active"
            | CompletePlanningAttempt(attemptId, actual, input) ->
                match Map.tryFind attemptId state.Attempts, state.Observation, state.Budget with
                | Some attempt, Some observation, Some budget when attempt.Status = PlanningActive && input.AttemptId = attemptId && validUse actual
                                                          && subtractUse attempt.Reserved actual |> Option.isSome
                                                          && now <= budget.Deadline
                                                          && (tryAddRuntime attempt.StartedAt attempt.Reserved.RuntimeSeconds |> Option.exists (fun deadline -> now <= deadline))
                                                          && input.ObservationSha256 = observation.ObservationSha256
                                                          && input.WorkflowRevision = observation.WorkflowRevision
                                                          && input.Generation = observation.Generation
                                                          && input.ObservationSha256 = attempt.ObservationSha256
                                                          && input.WorkflowRevision = attempt.WorkflowRevision
                                                          && input.Generation = attempt.Generation
                                                          && validText input.Scope
                                                          && validSha input.NarrativeSha256
                                                          && input.ProposedAt >= attempt.StartedAt && input.ProposedAt <= now
                                                          && not input.Actions.IsEmpty
                                                          && input.Actions |> List.forall (fun action ->
                                                              validSha action.ParametersSha256
                                                              && observation.WorkItems |> List.exists (fun item -> WorkItemIdentity.persistenceId item.Identity = WorkItemIdentity.persistenceId action.WorkItem))
                                                          && not (Map.containsKey input.ProposalId state.Proposals) ->
                    let proposal = { ProposalId = input.ProposalId; AttemptId = input.AttemptId; ObservationSha256 = input.ObservationSha256.ToLowerInvariant(); WorkflowRevision = input.WorkflowRevision; Generation = input.Generation; Scope = input.Scope; NarrativeSha256 = input.NarrativeSha256.ToLowerInvariant(); Actions = input.Actions; PlanSha256 = proposalSha256 input; ProposedAt = input.ProposedAt }
                    accept envelope body state [ ProposalRecorded proposal; PlanningAttemptCompleted(attemptId, actual) ] "proposal-recorded"
                | _ -> reject envelope body state "stale-or-invalid-proposal"
            | ConsumeUnknownPlanningReservation(attemptId, reason) ->
                match Map.tryFind attemptId state.Attempts with
                | Some attempt when (match attempt.Status with PlanningOutcomeUnknown _ -> true | _ -> false) && not (String.IsNullOrWhiteSpace reason) -> accept envelope body state [ UnknownPlanningReservationConsumed(attemptId, reason) ] "unknown-reservation-consumed"
                | _ -> reject envelope body state "planning-attempt-not-unknown"
            | ApproveProposal input ->
                match state.Proposals |> Map.tryFind input.ProposalId, state.Budget, state.Observation with
                | Some proposal, Some budget, Some observation when state.CurrentProposal = Some input.ProposalId
                                                                  && String.Equals(input.PlanSha256, proposal.PlanSha256, StringComparison.OrdinalIgnoreCase)
                                                                  && String.Equals(input.PlanningBudgetSha256, budgetSha256 budget, StringComparison.OrdinalIgnoreCase)
                                                                  && input.Scope = proposal.Scope && validText input.PrincipalId
                                                                  && input.PrincipalId = envelope.PrincipalId
                                                                  && proposal.WorkflowRevision = observation.WorkflowRevision
                                                                  && proposal.Generation = observation.Generation
                                                                  && input.ExpectedWorkflowRevision = observation.WorkflowRevision
                                                                  && input.ExpectedGeneration = observation.Generation
                                                                  && now <= budget.Deadline
                                                                  && validSha input.CommandBodySha256
                                                                  && input.ApprovedAt <= now
                                                                  && not (Map.containsKey input.ProposalId state.Approvals)
                                                                  && (match input.Kind with ExplicitApproval -> true | RoutinePreauthorization policy -> not (String.IsNullOrWhiteSpace policy)) ->
                    let approval = { ProposalId = input.ProposalId; PlanSha256 = proposal.PlanSha256; PlanningBudgetSha256 = budgetSha256 budget; PrincipalId = input.PrincipalId; Scope = input.Scope; ExpectedWorkflowRevision = input.ExpectedWorkflowRevision; ExpectedGeneration = input.ExpectedGeneration; CommandId = input.CommandId; CommandBodySha256 = input.CommandBodySha256.ToLowerInvariant(); Kind = input.Kind; ApprovalSha256 = approvalSha256 input; ApprovedAt = input.ApprovedAt }
                    accept envelope body state [ ProposalApproved approval ] "proposal-approved"
                | _ -> reject envelope body state "stale-or-unbound-approval"
            | RecordCommandAcceptance acceptance ->
                match Map.tryFind acceptance.ProposalId state.Approvals with
                | Some approval when String.Equals(acceptance.ApprovalSha256, approval.ApprovalSha256, StringComparison.OrdinalIgnoreCase)
                                     && acceptance.Receipt.CommandId = approval.CommandId
                                     && String.Equals(acceptance.Receipt.BodySha256, approval.CommandBodySha256, StringComparison.OrdinalIgnoreCase)
                                     && (acceptance.Receipt.Disposition = ReceiptDisposition.Accepted || acceptance.Receipt.Disposition = ReceiptDisposition.Duplicate)
                                     && validReadback now acceptance.Provenance
                                     && acceptance.AcceptedAt <= now
                                     && not (Map.containsKey acceptance.Receipt.CommandId state.Acceptances) ->
                    accept envelope body state [ CommandAcceptanceRecorded acceptance ] "durable-command-acceptance-recorded"
                | _ -> reject envelope body state "unbound-command-acceptance"
            | RecordEffectCompletion completion ->
                if not (Map.containsKey completion.CommandId state.Acceptances) || Map.containsKey completion.OperationId state.Effects || completion.CompletedAt > now || not (validReadback now completion.Provenance) || not (validEffectResult completion.Result) then reject envelope body state "unbound-or-duplicate-effect-completion"
                else accept envelope body state [ EffectCompletionRecorded completion ] "effect-completion-recorded"

    let replay (events: ObserverEvent list) = events |> List.fold evolve initial

[<RequireQualifiedAccess>]
module ObserverEventCodec =
    let schemaVersion = 1
    let serializerVersion = "fsgg.orchestration.observer-event-json/1"
    let private options =
        let value = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
        value.PropertyNameCaseInsensitive <- false
        value.Converters.Add(JsonFSharpConverter())
        value
    let encode (eventValue: ObserverEvent) = JsonSerializer.SerializeToUtf8Bytes(eventValue, options)
    let tryDecode (bytes: byte array) =
        try
            let value = JsonSerializer.Deserialize<ObserverEvent>(ReadOnlySpan<byte>(bytes), options)
            if isNull (box value) then Error "observer-event-json-null" else Ok value
        with
        | :? JsonException -> Error "observer-event-json-invalid"
        | :? NotSupportedException -> Error "observer-event-json-unsupported"

type ObserverStoredEvent =
    { ObserverId: string
      Sequence: int64
      EventId: Guid
      SchemaVersion: int
      SerializerVersion: string
      Event: ObserverEvent
      RecordedAt: DateTimeOffset }

type ObserverAppendRequest =
    { ObserverId: string
      Command: ObserverCommandEnvelope
      ReceivedAt: DateTimeOffset
      Events: ObserverStoredEvent list }

type ObserverAppendOutcome = ObserverAppended of int64 | ObserverDuplicate of int64 | ObserverConflict | ObserverWrongExpectedSequence of int64 | ObserverInvalidAppend of string | ObserverAppendUnavailable of string

type ObserverRecoveryFailure =
    | ObserverStoreUnavailable of string
    | ObserverStoreReadOnly
    | ObserverCapacityUnavailable
    | ObserverCorruptRecord of string * int64
    | ObserverUnknownEventVersion of string * int64 * int
    | ObserverUnknownSerializerVersion of string
    | ObserverMigrationInterrupted of string
    | ObserverIncompatibleDowngrade of databaseVersion: int * runtimeVersion: int
    | ObserverBackupRequiresReconciliation of string

type ObserverRecovery = { Events: ObserverStoredEvent list; State: ObserverState }

type IObserverJournalStore =
    abstract AppendObserver: ObserverAppendRequest * CancellationToken -> Task<ObserverAppendOutcome>
    abstract RecoverObserver: observerId: string * CancellationToken -> Task<Result<ObserverRecovery, ObserverRecoveryFailure list>>

[<RequireQualifiedAccess>]
module ObserverJournal =
    let observerId sessionId =
        let value = (Id.sessionValue sessionId).ToString("N")
        $"observer-session-v1-{value}"

    let appendRequest observerId receivedAt (envelope: ObserverCommandEnvelope) (decision: ObserverDecision) =
        { ObserverId = observerId
          Command = envelope
          ReceivedAt = receivedAt
          Events =
            decision.Events
            |> List.mapi (fun index eventValue ->
                { ObserverId = observerId
                  Sequence = envelope.ExpectedSequence + int64 index + 1L
                  EventId = Guid.NewGuid()
                  SchemaVersion = ObserverEventCodec.schemaVersion
                  SerializerVersion = ObserverEventCodec.serializerVersion
                  Event = eventValue
                  RecordedAt = receivedAt }) }

type ImmutableIssueIdentityFact =
    { Repository: RepositoryCoordinates
      RepositoryNodeId: string
      RepositoryDatabaseId: int64
      ContentNodeId: LiveId
      IssueNodeId: string
      IssueNumber: int64 }

type ObservationBridgeFailure =
    | ProjectReadRefused of ProjectReadFailure
    | InvalidObservationProvenance
    | MissingImmutableIssueIdentity of LiveId
    | DuplicateImmutableIssueIdentity of LiveId
    | ImmutableIssueIdentityMismatch of LiveId

[<RequireQualifiedAccess>]
module ProjectObservationBridge =
    let private digest (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
    let private validSha value = not (String.IsNullOrWhiteSpace value) && value.Length = 64 && value |> Seq.forall Uri.IsHexDigit

    let observe (projectId: ProjectId) (workflowRevision: WorkflowRevision) (generation: Generation) (provenance: ObservationProvenance) (facts: ImmutableIssueIdentityFact list) (observation: ProjectObservation) =
        match ProjectAdapter.readProject observation with
        | Error failure -> Error(ProjectReadRefused failure)
        | Ok snapshot when String.IsNullOrWhiteSpace provenance.Provider || not (validSha provenance.QuerySha256) || not (validSha provenance.EvidenceSha256) -> Error InvalidObservationProvenance
        | Ok snapshot ->
            let byContent = facts |> List.groupBy _.ContentNodeId |> Map.ofList
            let mutable failure = None
            let workItems = ResizeArray<ObservedWorkItem>()
            let mutable nonWork = 0
            for item in snapshot.Items do
                match item.Content with
                | RepositoryIssue(repository, number, contentId) ->
                    match Map.tryFind contentId byContent with
                    | None -> failure <- Some(MissingImmutableIssueIdentity contentId)
                    | Some [ fact ] when fact.Repository = repository && fact.IssueNumber = int64 number && fact.ContentNodeId = contentId ->
                        try
                            workItems.Add
                                { Identity = WorkItemIdentity.create fact.RepositoryNodeId fact.RepositoryDatabaseId fact.IssueNodeId fact.IssueNumber
                                  MembershipItemId = LiveId.value item.ItemId
                                  Archived = item.Archived }
                        with _ -> failure <- Some(ImmutableIssueIdentityMismatch contentId)
                    | Some [ _ ] -> failure <- Some(ImmutableIssueIdentityMismatch contentId)
                    | Some _ -> failure <- Some(DuplicateImmutableIssueIdentity contentId)
                | _ -> nonWork <- nonWork + 1
            match failure with
            | Some value -> Error value
            | None ->
                let sorted = workItems |> Seq.sortBy (fun item -> WorkItemIdentity.persistenceId item.Identity) |> Seq.toList
                let draft =
                    { ProjectId = projectId
                      SourceRevision = snapshot.Revision
                      WorkflowRevision = workflowRevision
                      Generation = generation
                      ObservationSha256 = String.replicate 64 "0"
                      Provenance = provenance
                      WorkItems = sorted
                      NonWorkItemCount = nonWork }
                Ok { draft with ObservationSha256 = Observer.observationSha256 draft }

type ProjectObservationRequest = { ProjectId: ProjectId; WorkflowRevision: WorkflowRevision; Generation: Generation }
type PlanningRequest = { SessionId: SessionId; AttemptId: AttemptId; Observation: ProjectObservationSnapshot; Reservation: PlanningUse }
type PlanningResult = { Proposal: ProposalInput; ActualUse: PlanningUse }

type IProjectReadCapability =
    abstract ObserveProject: ProjectObservationRequest * CancellationToken -> Task<Result<ProjectObservationSnapshot, ObservationBridgeFailure>>

type IBoundedPlanningCapability =
    abstract CreateProposal: PlanningRequest * CancellationToken -> Task<Result<PlanningResult, string>>

type ICommandReadbackCapability =
    abstract ReadCommandAcceptance: ProposalApproval * CancellationToken -> Task<Result<DurableCommandAcceptance, string>>
    abstract ReadEffectCompletions: DurableCommandAcceptance * CancellationToken -> Task<Result<DurableEffectCompletion list, string>>

type ObserverComposition = private ObserverComposition of IProjectReadCapability * IBoundedPlanningCapability * ICommandReadbackCapability * IObserverJournalStore

[<RequireQualifiedAccess>]
module ObserverComposition =
    let create projectRead boundedPlanning commandReadback journal = ObserverComposition(projectRead, boundedPlanning, commandReadback, journal)
    let capabilities (ObserverComposition _) = [ "project-read"; "bounded-planning"; "command-readback"; "observer-journal" ]

type ObservationRefreshRequest =
    { ObserverId: string
      State: ObserverState
      ProjectRequest: ProjectObservationRequest
      CommandId: CommandId
      PrincipalId: string
      IssuedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset }

type ObservationRefreshOutcome =
    | ObservationRefreshed of ObserverState
    | ObservationReadRefused of ObservationBridgeFailure
    | ObservationCommandRefused of ObserverReceipt
    | ObservationPersistenceRefused of ObserverAppendOutcome

type PlanningExecutionRequest =
    { ObserverId: string
      State: ObserverState
      AttemptId: AttemptId
      Reservation: PlanningUse
      StartCommandId: CommandId
      CompletionCommandId: CommandId
      UnknownCommandId: CommandId
      PrincipalId: string
      IssuedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset }

type PlanningExecutionOutcome =
    | PlanningResultPersisted of ObserverState * PlanProposal
    | PlanningLaunchRefused of ObserverReceipt
    | PlanningAlreadyRecorded of terminalSequence: int64
    | PlanningPersistenceRefused of ObserverAppendOutcome
    | PlanningOutcomePersistedUnknown of ObserverState * string
    | PlanningOutcomePersistenceUncertain of ObserverState * string * ObserverAppendOutcome

type CommandReadbackRequest =
    { ObserverId: string
      State: ObserverState
      ProposalId: ProposalId
      PersistenceCommandId: CommandId
      PrincipalId: string
      IssuedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset }

type CommandReadbackOutcome =
    | CommandAcceptanceRefreshed of ObserverState
    | CommandReadbackRefused of string
    | CommandReadbackCommandRefused of ObserverReceipt
    | CommandReadbackPersistenceRefused of ObserverAppendOutcome

type EffectReadbackRequest =
    { ObserverId: string
      State: ObserverState
      AcceptedCommandId: CommandId
      OperationId: OperationId
      PersistenceCommandId: CommandId
      PrincipalId: string
      IssuedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset }

type EffectReadbackOutcome =
    | EffectCompletionRefreshed of ObserverState
    | EffectReadbackRefused of string
    | EffectReadbackCommandRefused of ObserverReceipt
    | EffectReadbackPersistenceRefused of ObserverAppendOutcome

[<RequireQualifiedAccess>]
module ObserverRuntime =
    let private evolveDecision (state: ObserverState) (decision: ObserverDecision) = decision.Events |> List.fold Observer.evolve state

    let private append (journal: IObserverJournalStore) observerId receivedAt (envelope: ObserverCommandEnvelope) (decision: ObserverDecision) cancellationToken =
        journal.AppendObserver(ObserverJournal.appendRequest observerId receivedAt envelope decision, cancellationToken)

    let refreshObservation (clock: TimeProvider) (ObserverComposition(projectRead, _, _, journal)) (request: ObservationRefreshRequest) cancellationToken =
        task {
            let! read = projectRead.ObserveProject(request.ProjectRequest, cancellationToken)
            match read with
            | Error failure -> return ObservationReadRefused failure
            | Ok observation ->
                let now = clock.GetUtcNow()
                let envelope =
                    { CommandId = request.CommandId
                      ExpectedSequence = request.State.Sequence
                      PrincipalId = request.PrincipalId
                      IssuedAt = request.IssuedAt
                      ExpiresAt = request.ExpiresAt
                      Command = RecordProjectObservation observation }
                let decision = Observer.decide now request.State envelope
                if decision.Receipt.Disposition = ObserverRejected then return ObservationCommandRefused decision.Receipt
                else
                    let! stored = append journal request.ObserverId now envelope decision cancellationToken
                    match stored with
                    | ObserverAppended _ -> return ObservationRefreshed(evolveDecision request.State decision)
                    | other -> return ObservationPersistenceRefused other
        }

    let private markUnknown (clock: TimeProvider) (journal: IObserverJournalStore) (request: PlanningExecutionRequest) (state: ObserverState) reason cancellationToken =
        task {
            let now = clock.GetUtcNow()
            let envelope =
                { CommandId = request.UnknownCommandId
                  ExpectedSequence = state.Sequence
                  PrincipalId = request.PrincipalId
                  IssuedAt = now
                  ExpiresAt = now.AddMinutes 1.
                  Command = MarkPlanningAttemptUnknown(request.AttemptId, reason) }
            let decision = Observer.decide now state envelope
            if decision.Receipt.Disposition = ObserverRejected then return PlanningOutcomePersistenceUncertain(state, reason, ObserverInvalidAppend decision.Receipt.Detail)
            else
                let! stored = append journal request.ObserverId now envelope decision cancellationToken
                match stored with
                | ObserverAppended _ -> return PlanningOutcomePersistedUnknown(evolveDecision state decision, reason)
                | other -> return PlanningOutcomePersistenceUncertain(state, reason, other)
        }

    let executePlanning (clock: TimeProvider) (ObserverComposition(_, planner, _, journal)) (request: PlanningExecutionRequest) cancellationToken =
        task {
            let launchNow = clock.GetUtcNow()
            let startEnvelope =
                { CommandId = request.StartCommandId
                  ExpectedSequence = request.State.Sequence
                  PrincipalId = request.PrincipalId
                  IssuedAt = request.IssuedAt
                  ExpiresAt = request.ExpiresAt
                  Command = StartPlanningAttempt(request.AttemptId, request.Reservation, launchNow) }
            let startDecision = Observer.decide launchNow request.State startEnvelope
            if startDecision.Receipt.Disposition = ObserverRejected then return PlanningLaunchRefused startDecision.Receipt
            else
                let! startStored = append journal request.ObserverId launchNow startEnvelope startDecision cancellationToken
                match startStored with
                | ObserverDuplicate sequence -> return PlanningAlreadyRecorded sequence
                | ObserverAppended _ ->
                    let startedState = evolveDecision request.State startDecision
                    let planningRequest =
                        { SessionId = startedState.SessionId.Value
                          AttemptId = request.AttemptId
                          Observation = startedState.Observation.Value
                          Reservation = request.Reservation }
                    let budgetDeadline = startedState.Budget.Value.Deadline
                    // The reducer has already proved this exact integer-second addition safe.
                    let runtimeDeadline = Observer.tryAddRuntime launchNow request.Reservation.RuntimeSeconds |> Option.get
                    let hardDeadline = [ budgetDeadline; request.ExpiresAt; runtimeDeadline ] |> List.min
                    let afterReservation = clock.GetUtcNow()
                    let wait = hardDeadline - afterReservation
                    use plannerCancellation = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                    let! planned =
                        task {
                            if wait <= TimeSpan.Zero then return Error "planning-window-expired"
                            else
                                try
                                    // A capability may do synchronous work before returning its Task. Start it
                                    // away from the observer caller so the same deadline bounds that work too.
                                    let invocation =
                                        Task.Factory
                                            .StartNew<Task<Result<PlanningResult, string>>>(
                                                (fun () -> planner.CreateProposal(planningRequest, plannerCancellation.Token)),
                                                CancellationToken.None,
                                                TaskCreationOptions.DenyChildAttach,
                                                TaskScheduler.Default)
                                            .Unwrap()
                                    return! invocation.WaitAsync(wait, clock, cancellationToken)
                                with
                                | :? TimeoutException -> plannerCancellation.Cancel(); return Error "planning-window-expired"
                                | :? OperationCanceledException when not cancellationToken.IsCancellationRequested -> return Error "planning-window-expired"
                                | exceptionValue -> return Error $"planning-agent-exception:{exceptionValue.GetType().Name}"
                        }
                    match planned with
                    | Error reason -> return! markUnknown clock journal request startedState reason cancellationToken
                    | Ok result ->
                        let completionNow = clock.GetUtcNow()
                        if completionNow > hardDeadline then
                            return! markUnknown clock journal request startedState "planning-window-expired" cancellationToken
                        else
                            let completionEnvelope =
                                { CommandId = request.CompletionCommandId
                                  ExpectedSequence = startedState.Sequence
                                  PrincipalId = request.PrincipalId
                                  IssuedAt = completionNow
                                  ExpiresAt = request.ExpiresAt
                                  Command = CompletePlanningAttempt(request.AttemptId, result.ActualUse, result.Proposal) }
                            let completionDecision = Observer.decide completionNow startedState completionEnvelope
                            if completionDecision.Receipt.Disposition = ObserverRejected then
                                return! markUnknown clock journal request startedState $"planning-output-refused:{completionDecision.Receipt.Detail}" cancellationToken
                            else
                                let! completionStored = append journal request.ObserverId completionNow completionEnvelope completionDecision cancellationToken
                                match completionStored with
                                | ObserverAppended _ ->
                                    let completed = evolveDecision startedState completionDecision
                                    return PlanningResultPersisted(completed, completed.Proposals[result.Proposal.ProposalId])
                                | other -> return PlanningOutcomePersistenceUncertain(startedState, "planning-result-append-unconfirmed", other)
                | other -> return PlanningPersistenceRefused other
        }

    let refreshCommandAcceptance (clock: TimeProvider) (ObserverComposition(_, _, readback, journal)) (request: CommandReadbackRequest) cancellationToken =
        task {
            match Map.tryFind request.ProposalId request.State.Approvals with
            | None -> return CommandReadbackRefused "proposal-not-approved"
            | Some approval ->
                let! observed = readback.ReadCommandAcceptance(approval, cancellationToken)
                match observed with
                | Error reason -> return CommandReadbackRefused reason
                | Ok acceptance ->
                    let now = clock.GetUtcNow()
                    let envelope =
                        { CommandId = request.PersistenceCommandId; ExpectedSequence = request.State.Sequence; PrincipalId = request.PrincipalId
                          IssuedAt = request.IssuedAt; ExpiresAt = request.ExpiresAt; Command = RecordCommandAcceptance acceptance }
                    let decision = Observer.decide now request.State envelope
                    if decision.Receipt.Disposition = ObserverRejected then return CommandReadbackCommandRefused decision.Receipt
                    else
                        let! stored = append journal request.ObserverId now envelope decision cancellationToken
                        match stored with
                        | ObserverAppended _ -> return CommandAcceptanceRefreshed(evolveDecision request.State decision)
                        | other -> return CommandReadbackPersistenceRefused other
        }

    let refreshEffectCompletion (clock: TimeProvider) (ObserverComposition(_, _, readback, journal)) (request: EffectReadbackRequest) cancellationToken =
        task {
            match Map.tryFind request.AcceptedCommandId request.State.Acceptances with
            | None -> return EffectReadbackRefused "command-not-durably-accepted"
            | Some acceptance ->
                let! observed = readback.ReadEffectCompletions(acceptance, cancellationToken)
                match observed with
                | Error reason -> return EffectReadbackRefused reason
                | Ok completions ->
                    let now = clock.GetUtcNow()
                    match completions |> List.filter (fun completion -> completion.CommandId = request.AcceptedCommandId && completion.OperationId = request.OperationId) with
                    | [ completion ] ->
                        let envelope =
                            { CommandId = request.PersistenceCommandId; ExpectedSequence = request.State.Sequence; PrincipalId = request.PrincipalId
                              IssuedAt = request.IssuedAt; ExpiresAt = request.ExpiresAt; Command = RecordEffectCompletion completion }
                        let decision = Observer.decide now request.State envelope
                        if decision.Receipt.Disposition = ObserverRejected then return EffectReadbackCommandRefused decision.Receipt
                        else
                            let! stored = append journal request.ObserverId now envelope decision cancellationToken
                            match stored with
                            | ObserverAppended _ -> return EffectCompletionRefreshed(evolveDecision request.State decision)
                            | other -> return EffectReadbackPersistenceRefused other
                    | [] -> return EffectReadbackRefused "operation-completion-not-observed"
                    | _ -> return EffectReadbackRefused "duplicate-operation-completion-observation"
        }

type ProjectionStage = Conversation | Proposed | Approved | DurableAcceptance | EffectComplete
type ProjectionRow = { Stage: ProjectionStage; Identity: string; Detail: string; RecordedAt: DateTimeOffset }
type ObserverProjection = { SessionId: string option; Sequence: int64; ObservationRevision: string option; Rows: ProjectionRow list; Remaining: PlanningUse option; Reserved: PlanningUse }

[<RequireQualifiedAccess>]
module ObserverProjection =
    let private remaining (budget: PlanningBudget) (used: PlanningUse) (reserved: PlanningUse) =
        Some { Tokens = budget.TokenLimit - used.Tokens - reserved.Tokens; RuntimeSeconds = budget.RuntimeSecondsLimit - used.RuntimeSeconds - reserved.RuntimeSeconds; CostMicros = budget.CostMicrosLimit - used.CostMicros - reserved.CostMicros }

    let render (state: ObserverState) =
        let conversations = state.Conversation |> List.map (fun value -> { Stage = Conversation; Identity = string value.EntryId; Detail = string value.Role; RecordedAt = value.RecordedAt })
        let proposals = state.Proposals |> Map.toList |> List.map (fun (_, value) -> { Stage = Proposed; Identity = string (ProposalId.value value.ProposalId); Detail = value.PlanSha256; RecordedAt = value.ProposedAt })
        let approvals = state.Approvals |> Map.toList |> List.map (fun (_, value) -> { Stage = Approved; Identity = value.ApprovalSha256; Detail = value.Scope; RecordedAt = value.ApprovedAt })
        let acceptances = state.Acceptances |> Map.toList |> List.map (fun (_, value) -> { Stage = DurableAcceptance; Identity = string (Id.commandValue value.Receipt.CommandId); Detail = string value.Receipt.Disposition; RecordedAt = value.AcceptedAt })
        let effects = state.Effects |> Map.toList |> List.map (fun (_, value) -> { Stage = EffectComplete; Identity = string (Id.operationValue value.OperationId); Detail = string value.Result; RecordedAt = value.CompletedAt })
        { SessionId = state.SessionId |> Option.map (Id.sessionValue >> string)
          Sequence = state.Sequence
          ObservationRevision = state.Observation |> Option.map _.SourceRevision
          Rows = [ conversations; proposals; approvals; acceptances; effects ] |> List.concat |> List.sortBy _.RecordedAt
          Remaining = state.Budget |> Option.bind (fun budget -> remaining budget state.Used state.Reserved)
          Reserved = state.Reserved }

    /// Projects only a recovery already validated by the durable journal store.
    let fromRecovery (recovery: ObserverRecovery) = render recovery.State
