namespace FS.GG.Coordination.Orchestration.Pilot

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Protocol

[<RequireQualifiedAccess>]
type PilotPhase = StableOwned | TransferIntended | PilotOwned | OutcomeUnknown | Paused | Revoked | ReturnIntended

type PilotPermit =
    { SchemaVersion: int
      PermitId: Guid
      SubjectId: WorkItemId
      JobClass: string
      StableOwnerId: string
      PilotOwnerId: string
      Generation: Generation
      AttemptLimit: int64
      TokenLimit: int64
      RuntimeSecondsLimit: int64
      CostMicrosLimit: int64
      ExpiresAt: DateTimeOffset
      Capacity: int
      RecoveryCapacity: int
      StartupPolicy: string
      AutoResume: bool }

type StableRouteEvidence =
    { SubjectId: WorkItemId
      Generation: Generation
      EvidenceSha256: string
      Quiesced: bool
      Excluded: bool
      ObservedAt: DateTimeOffset }

type ReadbackPurpose = TransferAcknowledgement | AssignmentAdmission | AssignmentReconciliation | AssignmentSettlement | ReturnIntent | ReturnAcknowledgement | Reconnect

type ReadbackEvidenceInput =
    { SubjectId: WorkItemId
      Generation: Generation
      Purpose: ReadbackPurpose
      OperationId: OperationId option
      Provider: string
      SourceRevision: string
      EvidenceSha256: string
      ObservedAt: DateTimeOffset }

type ReadbackEvidence = private ReadbackEvidence of ReadbackEvidenceInput

type PilotAllocation =
    { O0ReservationId: ReservationId
      O0Deadline: DateTimeOffset
      PlanningBudgetSha256: string option
      Tokens: int64
      RuntimeSeconds: int64
      CostMicros: int64 }

type PilotState =
    { Sequence: int64
      Permit: PilotPermit option
      Phase: PilotPhase
      AssignedOwnerId: string
      TransferEvidence: StableRouteEvidence option
      TransferAcknowledgement: ReadbackEvidence option
      ReturnAcknowledgement: ReadbackEvidence option
      ReadbackCurrent: bool
      AttemptsUsed: int64
      TokensReserved: int64
      RuntimeSecondsReserved: int64
      CostMicrosReserved: int64
      ActiveAssignments: Map<OperationId, AttemptId>
      UnknownOperations: Set<OperationId>
      KnownOperations: Set<OperationId> }

type PilotEvent =
    | PermitIssued of PilotPermit
    | TransferIntentPersisted of StableRouteEvidence
    | TransferAcknowledged of ReadbackEvidence
    | AssignmentStarted of OperationId * AttemptId * PilotAllocation * ReadbackEvidence
    | AssignmentOutcomeUnknown of OperationId * string
    | AssignmentReconciled of OperationId * ReadbackEvidence
    | AssignmentSettled of OperationId * ReadbackEvidence
    | PilotPaused of string
    | PilotRevoked of string
    | ReturnIntentPersisted of ReadbackEvidence
    | ReturnAcknowledged of ReadbackEvidence
    | ReadbackDisconnected
    | ReadbackReconnected of ReadbackEvidence

type PilotCommand =
    | IssuePermit of PilotPermit
    | RecordTransferIntent of StableRouteEvidence
    | AcknowledgeTransfer of ReadbackEvidence
    | StartAssignment of OperationId * AttemptId * PilotAllocation * ReadbackEvidence
    | MarkOutcomeUnknown of OperationId * string
    | ReconcileOutcome of OperationId * ReadbackEvidence
    | SettleAssignment of OperationId * ReadbackEvidence
    | Pause of string
    | Revoke of string
    | RecordReturnIntent of ReadbackEvidence
    | AcknowledgeReturn of ReadbackEvidence
    | DisconnectReadback
    | ReconnectReadback of ReadbackEvidence

type PilotCommandEnvelope =
    { CommandId: Guid
      ExpectedSequence: int64
      PrincipalId: string
      IssuedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset
      Command: PilotCommand }

type PilotDecision = Result<PilotEvent list, string>

type PilotStoredEvent =
    { PermitId: Guid; Sequence: int64; EventId: Guid; SchemaVersion: int
      SerializerVersion: string; Event: PilotEvent; RecordedAt: DateTimeOffset }
type PilotAppendRequest =
    { PermitId: Guid; Command: PilotCommandEnvelope; ReceivedAt: DateTimeOffset
      Events: PilotStoredEvent list }
type PilotAppendOutcome = PilotAppended of int64 | PilotDuplicate of int64 | PilotConflict | PilotWrongExpectedSequence of int64 | PilotInvalidAppend of string | PilotAppendUnavailable of string
type PilotRecoveryFailure = PilotStoreUnavailable of string | PilotStoreReadOnly | PilotCorruptRecord of Guid * int64 | PilotUnknownEventVersion of Guid * int64 * int | PilotUnknownSerializerVersion of string | PilotMigrationInterrupted of string | PilotIncompatibleDowngrade of int * int | PilotBackupRequiresReconciliation of string
type PilotRecovery = { Events: PilotStoredEvent list; State: PilotState }
type IPilotJournalStore =
    abstract AppendPilot: PilotAppendRequest * CancellationToken -> Task<PilotAppendOutcome>
    abstract RecoverPilot: permitId: Guid * CancellationToken -> Task<Result<PilotRecovery, PilotRecoveryFailure list>>

type PilotReadbackRequest =
    { Permit: PilotPermit
      Purpose: ReadbackPurpose
      OperationId: OperationId option
      NotBefore: DateTimeOffset }

type IPilotReadbackCapability =
    abstract ReadCurrent: PilotReadbackRequest * CancellationToken -> Task<Result<ReadbackEvidenceInput, string>>

[<RequireQualifiedAccess>]
module PilotReadback =
    let private validText value = not (String.IsNullOrWhiteSpace value) && value = value.Trim() && value.Length <= 256
    let private validSha value = validText value && value.Length = 64 && value |> Seq.forall Uri.IsHexDigit
    let private validate now request (input: ReadbackEvidenceInput) =
        if input.SubjectId = request.Permit.SubjectId && input.Generation = request.Permit.Generation
           && input.Purpose = request.Purpose && input.OperationId = request.OperationId
           && validText input.Provider && validText input.SourceRevision && validSha input.EvidenceSha256
           && input.ObservedAt >= request.NotBefore && input.ObservedAt <= now then Ok(ReadbackEvidence input)
        else Error "readback-evidence-untrusted-stale-or-unbound"
    let read (clock: TimeProvider) (capability: IPilotReadbackCapability) request cancellationToken = task {
        let! result = capability.ReadCurrent(request, cancellationToken)
        return result |> Result.bind (validate (clock.GetUtcNow()) request) }
    let value (ReadbackEvidence input) = input

[<RequireQualifiedAccess>]
module Pilot =
    let schema = "fsgg.coordination.pilot-permit/1"
    let initial =
        { Sequence = 0L; Permit = None; Phase = PilotPhase.StableOwned; AssignedOwnerId = ""
          TransferEvidence = None; TransferAcknowledgement = None; ReturnAcknowledgement = None
          ReadbackCurrent = false; AttemptsUsed = 0L; TokensReserved = 0L; RuntimeSecondsReserved = 0L
          CostMicrosReserved = 0L; ActiveAssignments = Map.empty; UnknownOperations = Set.empty; KnownOperations = Set.empty }

    let private validText value = not (String.IsNullOrWhiteSpace value) && value.Length <= 128
    let private validSha value = validText value && value.Length = 64 && value |> Seq.forall Uri.IsHexDigit
    let private sameStableAuthority (permit: PilotPermit) (evidence: StableRouteEvidence) = evidence.SubjectId = permit.SubjectId && evidence.Generation = permit.Generation && validSha evidence.EvidenceSha256
    let private evidenceValue = PilotReadback.value
    let private sameReadbackAuthority now notBefore purpose operationId (permit: PilotPermit) (evidence: ReadbackEvidence) =
        let value = evidenceValue evidence
        value.SubjectId = permit.SubjectId && value.Generation = permit.Generation
        && value.Purpose = purpose && value.OperationId = operationId
        && validText value.Provider && validText value.SourceRevision && validSha value.EvidenceSha256
        && value.ObservedAt >= notBefore && value.ObservedAt <= now
    let private addWithin current added limit = added > 0L && current >= 0L && current <= Int64.MaxValue - added && current + added <= limit
    let private validAllocation now (permit: PilotPermit) (state: PilotState) (allocation: PilotAllocation) =
        Id.reservationValue allocation.O0ReservationId <> Guid.Empty
        && allocation.O0Deadline > now && allocation.O0Deadline <= permit.ExpiresAt
        && (allocation.PlanningBudgetSha256 |> Option.forall validSha)
        && addWithin state.TokensReserved allocation.Tokens permit.TokenLimit
        && addWithin state.RuntimeSecondsReserved allocation.RuntimeSeconds permit.RuntimeSecondsLimit
        && addWithin state.CostMicrosReserved allocation.CostMicros permit.CostMicrosLimit
    let validatePermit (permit: PilotPermit) =
        let generated = CoordinationProtocolGenerated.Catalogue |> List.exists (fun row -> row.Id = "PILOT-PermitV1")
        generated && permit.SchemaVersion = 1 && permit.PermitId <> Guid.Empty
        && permit.JobClass = "routine-implementation" && validText permit.StableOwnerId && validText permit.PilotOwnerId
        && permit.StableOwnerId <> permit.PilotOwnerId && Id.generationValue permit.Generation > 0L
        && permit.AttemptLimit > 0L && permit.AttemptLimit <= 100L
        && permit.TokenLimit > 0L && permit.RuntimeSecondsLimit > 0L && permit.CostMicrosLimit > 0L
        && permit.Capacity > 1 && permit.Capacity <= 4 && permit.RecoveryCapacity > 0 && permit.RecoveryCapacity < permit.Capacity
        && permit.StartupPolicy = "manual" && not permit.AutoResume

    let evolve (state: PilotState) (eventValue: PilotEvent) =
        let next = state.Sequence + 1L
        match eventValue with
        | PermitIssued permit -> { initial with Sequence = next; Permit = Some permit; AssignedOwnerId = permit.StableOwnerId; ReadbackCurrent = true }
        | TransferIntentPersisted evidence -> { state with Sequence = next; Phase = PilotPhase.TransferIntended; TransferEvidence = Some evidence }
        | TransferAcknowledged evidence ->
            let permit = state.Permit.Value
            { state with Sequence = next; Phase = PilotPhase.PilotOwned; AssignedOwnerId = permit.PilotOwnerId; TransferAcknowledgement = Some evidence; ReadbackCurrent = true }
        | AssignmentStarted(operationId, attemptId, allocation, _) ->
            { state with Sequence = next; AttemptsUsed = state.AttemptsUsed + 1L
                         TokensReserved = state.TokensReserved + allocation.Tokens
                         RuntimeSecondsReserved = state.RuntimeSecondsReserved + allocation.RuntimeSeconds
                         CostMicrosReserved = state.CostMicrosReserved + allocation.CostMicros
                         ActiveAssignments = Map.add operationId attemptId state.ActiveAssignments
                         KnownOperations = Set.add operationId state.KnownOperations }
        | AssignmentOutcomeUnknown(operationId, _) ->
            let phase = if state.Phase = PilotPhase.PilotOwned then PilotPhase.OutcomeUnknown else state.Phase
            { state with Sequence = next; Phase = phase; UnknownOperations = Set.add operationId state.UnknownOperations }
        | AssignmentReconciled(operationId, _) ->
            let remaining = Set.remove operationId state.UnknownOperations
            let phase = if state.Phase = PilotPhase.OutcomeUnknown && remaining.IsEmpty then PilotPhase.PilotOwned else state.Phase
            { state with Sequence = next; Phase = phase; ActiveAssignments = Map.remove operationId state.ActiveAssignments; UnknownOperations = remaining; ReadbackCurrent = true }
        | AssignmentSettled(operationId, _) -> { state with Sequence = next; ActiveAssignments = Map.remove operationId state.ActiveAssignments; UnknownOperations = Set.remove operationId state.UnknownOperations; ReadbackCurrent = true }
        | PilotPaused _ -> { state with Sequence = next; Phase = PilotPhase.Paused }
        | PilotRevoked _ -> { state with Sequence = next; Phase = PilotPhase.Revoked }
        | ReturnIntentPersisted _ -> { state with Sequence = next; Phase = PilotPhase.ReturnIntended }
        | ReturnAcknowledged evidence ->
            let permit = state.Permit.Value
            { state with Sequence = next; Phase = PilotPhase.StableOwned; AssignedOwnerId = permit.StableOwnerId; ReturnAcknowledgement = Some evidence; ReadbackCurrent = true }
        | ReadbackDisconnected -> { state with Sequence = next; ReadbackCurrent = false }
        | ReadbackReconnected _ -> { state with Sequence = next; ReadbackCurrent = true }

    let replay (events: PilotEvent list) = List.fold evolve initial events

    let validateReplay permitId (events: PilotEvent list) =
        let structuralReadback (permit: PilotPermit) (purpose: ReadbackPurpose) (operationId: OperationId option) (evidence: ReadbackEvidence) =
            let value = evidenceValue evidence
            value.SubjectId = permit.SubjectId && value.Generation = permit.Generation
            && value.Purpose = purpose && value.OperationId = operationId
            && validText value.Provider && validText value.SourceRevision
            && validSha value.EvidenceSha256
        let folder (result: Result<PilotState, string>) (eventValue: PilotEvent) =
            result |> Result.bind (fun state ->
                let allowed =
                    match eventValue, state.Permit with
                    | PermitIssued permit, None -> permit.PermitId = permitId && validatePermit permit
                    | PermitIssued _, Some _ -> false
                    | _, None -> false
                    | TransferIntentPersisted evidence, Some permit ->
                        state.Phase = PilotPhase.StableOwned && sameStableAuthority permit evidence
                        && evidence.Quiesced && evidence.Excluded && state.ActiveAssignments.IsEmpty && state.UnknownOperations.IsEmpty
                    | TransferAcknowledged evidence, Some permit ->
                        state.Phase = PilotPhase.TransferIntended && state.TransferEvidence.IsSome && structuralReadback permit TransferAcknowledgement None evidence
                    | AssignmentStarted(operationId, attemptId, allocation, admission), Some permit ->
                        let capacity = permit.Capacity - permit.RecoveryCapacity
                        state.Phase = PilotPhase.PilotOwned && state.ReadbackCurrent && state.UnknownOperations.IsEmpty
                        && state.AttemptsUsed < permit.AttemptLimit && state.ActiveAssignments.Count < capacity
                        && not (state.KnownOperations.Contains operationId)
                        && not (state.ActiveAssignments |> Seq.exists (fun pair -> pair.Value = attemptId))
                        && structuralReadback permit AssignmentAdmission (Some operationId) admission
                        && Id.reservationValue allocation.O0ReservationId <> Guid.Empty
                        && allocation.O0Deadline <= permit.ExpiresAt
                        && allocation.PlanningBudgetSha256 |> Option.forall validSha
                        && addWithin state.TokensReserved allocation.Tokens permit.TokenLimit
                        && addWithin state.RuntimeSecondsReserved allocation.RuntimeSeconds permit.RuntimeSecondsLimit
                        && addWithin state.CostMicrosReserved allocation.CostMicros permit.CostMicrosLimit
                    | AssignmentOutcomeUnknown(operationId, reason), Some _ -> Map.containsKey operationId state.ActiveAssignments && validText reason
                    | AssignmentReconciled(operationId, evidence), Some permit -> state.UnknownOperations.Contains operationId && structuralReadback permit AssignmentReconciliation (Some operationId) evidence
                    | AssignmentSettled(operationId, evidence), Some permit -> Map.containsKey operationId state.ActiveAssignments && not (state.UnknownOperations.Contains operationId) && structuralReadback permit AssignmentSettlement (Some operationId) evidence
                    | PilotPaused reason, Some _ -> (state.Phase = PilotPhase.PilotOwned || state.Phase = PilotPhase.OutcomeUnknown) && validText reason
                    | PilotRevoked reason, Some permit -> state.AssignedOwnerId = permit.PilotOwnerId && state.Phase <> PilotPhase.ReturnIntended && validText reason
                    | ReturnIntentPersisted evidence, Some permit -> state.AssignedOwnerId = permit.PilotOwnerId && state.ActiveAssignments.IsEmpty && state.UnknownOperations.IsEmpty && structuralReadback permit ReturnIntent None evidence
                    | ReturnAcknowledged evidence, Some permit -> state.Phase = PilotPhase.ReturnIntended && structuralReadback permit ReturnAcknowledgement None evidence
                    | ReadbackDisconnected, Some permit -> state.AssignedOwnerId = permit.PilotOwnerId && state.ReadbackCurrent
                    | ReadbackReconnected evidence, Some permit -> state.AssignedOwnerId = permit.PilotOwnerId && structuralReadback permit Reconnect None evidence
                if allowed then Ok(evolve state eventValue) else Error "illegal-pilot-event-history")
        List.fold folder (Ok initial) events

    let decide (now: DateTimeOffset) (state: PilotState) (envelope: PilotCommandEnvelope) : PilotDecision =
        let refuse reason = Error reason
        if envelope.CommandId = Guid.Empty || String.IsNullOrWhiteSpace envelope.PrincipalId then refuse "invalid-command-envelope"
        elif envelope.ExpectedSequence <> state.Sequence then refuse "wrong-expected-sequence"
        elif now < envelope.IssuedAt || now >= envelope.ExpiresAt then refuse "command-expired"
        else
            match envelope.Command, state.Permit with
            | IssuePermit permit, None when envelope.PrincipalId = permit.StableOwnerId && validatePermit permit && now < permit.ExpiresAt && envelope.ExpiresAt <= permit.ExpiresAt -> Ok [ PermitIssued permit ]
            | IssuePermit _, _ -> refuse "permit-invalid-or-already-issued"
            | _, None -> refuse "permit-required"
            | command, Some permit ->
                let current = Id.generationValue permit.Generation
                let activeCapacity = permit.Capacity - permit.RecoveryCapacity
                match command with
                | RecordTransferIntent evidence when envelope.PrincipalId = permit.StableOwnerId && state.Phase = PilotPhase.StableOwned && sameStableAuthority permit evidence && evidence.Quiesced && evidence.Excluded && evidence.ObservedAt >= envelope.IssuedAt && evidence.ObservedAt <= now && state.ActiveAssignments.IsEmpty && state.UnknownOperations.IsEmpty -> Ok [ TransferIntentPersisted evidence ]
                | AcknowledgeTransfer evidence when envelope.PrincipalId = permit.PilotOwnerId && state.Phase = PilotPhase.TransferIntended && sameReadbackAuthority now envelope.IssuedAt TransferAcknowledgement None permit evidence && state.TransferEvidence.IsSome -> Ok [ TransferAcknowledged evidence ]
                | StartAssignment(operationId, attemptId, allocation, admission) when envelope.PrincipalId = permit.PilotOwnerId && state.Phase = PilotPhase.PilotOwned && state.ReadbackCurrent && state.UnknownOperations.IsEmpty && now < permit.ExpiresAt && state.AttemptsUsed < permit.AttemptLimit && state.ActiveAssignments.Count < activeCapacity && not (state.KnownOperations.Contains operationId) && not (state.ActiveAssignments |> Seq.exists (fun pair -> pair.Value = attemptId)) && validAllocation now permit state allocation && sameReadbackAuthority now envelope.IssuedAt AssignmentAdmission (Some operationId) permit admission -> Ok [ AssignmentStarted(operationId, attemptId, allocation, admission) ]
                | MarkOutcomeUnknown(operationId, reason) when envelope.PrincipalId = permit.PilotOwnerId && Map.containsKey operationId state.ActiveAssignments && validText reason -> Ok [ AssignmentOutcomeUnknown(operationId, reason) ]
                | ReconcileOutcome(operationId, evidence) when envelope.PrincipalId = permit.PilotOwnerId && state.UnknownOperations.Contains operationId && sameReadbackAuthority now envelope.IssuedAt AssignmentReconciliation (Some operationId) permit evidence -> Ok [ AssignmentReconciled(operationId, evidence) ]
                | SettleAssignment(operationId, evidence) when envelope.PrincipalId = permit.PilotOwnerId && Map.containsKey operationId state.ActiveAssignments && not (state.UnknownOperations.Contains operationId) && sameReadbackAuthority now envelope.IssuedAt AssignmentSettlement (Some operationId) permit evidence -> Ok [ AssignmentSettled(operationId, evidence) ]
                | Pause reason when envelope.PrincipalId = permit.PilotOwnerId && (state.Phase = PilotPhase.PilotOwned || state.Phase = PilotPhase.OutcomeUnknown) && validText reason -> Ok [ PilotPaused reason ]
                | Revoke reason when envelope.PrincipalId = permit.PilotOwnerId && state.AssignedOwnerId = permit.PilotOwnerId && state.Phase <> PilotPhase.ReturnIntended && validText reason -> Ok [ PilotRevoked reason ]
                | RecordReturnIntent evidence when envelope.PrincipalId = permit.PilotOwnerId && state.AssignedOwnerId = permit.PilotOwnerId && state.ActiveAssignments.IsEmpty && state.UnknownOperations.IsEmpty && sameReadbackAuthority now envelope.IssuedAt ReturnIntent None permit evidence -> Ok [ ReturnIntentPersisted evidence ]
                | AcknowledgeReturn evidence when envelope.PrincipalId = permit.StableOwnerId && state.Phase = PilotPhase.ReturnIntended && sameReadbackAuthority now envelope.IssuedAt ReturnAcknowledgement None permit evidence -> Ok [ ReturnAcknowledged evidence ]
                | DisconnectReadback when envelope.PrincipalId = permit.PilotOwnerId && state.AssignedOwnerId = permit.PilotOwnerId && state.ReadbackCurrent -> Ok [ ReadbackDisconnected ]
                | ReconnectReadback evidence when envelope.PrincipalId = permit.PilotOwnerId && not state.ReadbackCurrent && sameReadbackAuthority now envelope.IssuedAt Reconnect None permit evidence && Id.generationValue (evidenceValue evidence).Generation = current -> Ok [ ReadbackReconnected evidence ]
                | _ -> refuse "pilot-command-refused"

[<RequireQualifiedAccess>]
module PilotCodec =
    let schemaVersion = 1
    let serializerVersion = "fsgg.coordination.pilot-event-json/1"
    let private options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    do options.Converters.Add(JsonFSharpConverter())
    let encode eventValue = JsonSerializer.SerializeToUtf8Bytes(eventValue, options)
    let tryDecode (bytes: byte array) =
        try
            let value = JsonSerializer.Deserialize<PilotEvent>(ReadOnlySpan<byte>(bytes), options)
            if isNull (box value) then Error "pilot-event-null" else Ok value
        with :? JsonException -> Error "pilot-event-json-invalid"
    let commandSha256 envelope =
        JsonSerializer.SerializeToUtf8Bytes(envelope, options) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

[<RequireQualifiedAccess>]
module PilotJournal =
    let appendRequest (permitId: Guid) (receivedAt: DateTimeOffset) (envelope: PilotCommandEnvelope) (events: PilotEvent list) =
        { PermitId = permitId; Command = envelope; ReceivedAt = receivedAt
          Events = events |> List.mapi (fun index eventValue ->
              { PermitId = permitId; Sequence = envelope.ExpectedSequence + int64 index + 1L
                EventId = Guid.NewGuid(); SchemaVersion = PilotCodec.schemaVersion
                SerializerVersion = PilotCodec.serializerVersion; Event = eventValue; RecordedAt = receivedAt }) }
