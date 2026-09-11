namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Akka.Actor

type ProviderIdentity = { Provider: string; AdapterVersion: string }
type AuthenticationObservation =
    | Authenticated of provenance:string
    | NotAuthenticated of provenance:string
    | AuthenticationUnknown of provenance:string
type ProviderReadiness =
    { Identity: ProviderIdentity
      Authentication: AuthenticationObservation
      SupportsResume: bool
      ObservedAt: DateTimeOffset }

type ExecutionKey =
    { AssignmentId: Guid
      AttemptId: Guid
      Generation: int64 }
type RequestedSelection = { Model: string option; Effort: string option }
type ResolvedSelection = { Model: string option; Effort: string option }
type ExecutionLimits =
    { Deadline: DateTimeOffset
      MaximumRuntime: TimeSpan
      MaximumAttempts: int }
type LaunchIntent =
    { Schema: string
      Key: ExecutionKey
      InputDigest: string
      Workspace: string
      Requested: RequestedSelection
      Limits: ExecutionLimits
      RecordedAt: DateTimeOffset }

/// Opaque provider conversation/process identity. It is intentionally not a runner enrollment id.
type ProviderSessionReference = private ProviderSessionReference of string
[<RequireQualifiedAccess>]
module ProviderSessionReference =
    let create value =
        if String.IsNullOrWhiteSpace value || value.Length > 512 then Error "provider-session-reference-refused"
        else Ok(ProviderSessionReference value)
    let value (ProviderSessionReference value) = value

type MonetaryCost =
    | CostKnown of amount:decimal * currency:string * provenance:string
    | CostUnknown of provenance:string
    | CostNotApplicable of provenance:string
type UsageValue =
    | UsageKnown of value:int64 * unitName:string * provenance:string
    | UsageUnknown of provenance:string
    | UsageNotApplicable of provenance:string
type NormalizedUsage = { Values: Map<string,UsageValue>; Cost: MonetaryCost }
type OutputReference = { Kind:string; Reference:string; Digest:string option }
type CandidateReference = { CandidateId:Guid; HeadSha:string; TreeSha:string }
type SessionLifecycle = Starting | Running | Cancelling | Succeeded | Failed | Cancelled | DeadlineExceeded | OutcomeUnknown
type SessionObservation =
    { Provider: ProviderIdentity
      Session: ProviderSessionReference
      Resolved: ResolvedSelection
      Lifecycle: SessionLifecycle
      Output: OutputReference list
      LifecycleReferences: OutputReference list
      Usage: NormalizedUsage
      Candidate: CandidateReference option
      ObservedAt: DateTimeOffset }

type LaunchResult =
    | LaunchStarted of SessionObservation
    | LaunchRefused of string
    | LaunchAmbiguous of string
type ReconcileResult =
    | Reconciled of SessionObservation
    | ConfirmedAbsent
    | ReconcileUnknown of string
type CancelResult = CancelAccepted | CancelRefused of string | CancelUnknown of string

type IExecutionProvider =
    abstract member ObserveReadiness: CancellationToken -> Task<ProviderReadiness>
    abstract member Launch: LaunchIntent * CancellationToken -> Task<LaunchResult>
    abstract member Observe: ProviderSessionReference * CancellationToken -> Task<Result<SessionObservation,string>>
    abstract member Reconcile: LaunchIntent * CancellationToken -> Task<ReconcileResult>
    abstract member Cancel: ProviderSessionReference * CancellationToken -> Task<CancelResult>

type SessionEvent =
    | LaunchIntentRecorded of LaunchIntent
    | LaunchAttemptRecorded of attemptNumber:int * DateTimeOffset
    | StartObserved of SessionObservation
    | ObservationRecorded of SessionObservation
    | CancelRequested of DateTimeOffset
type StoredSession = { Revision:int64; Events:SessionEvent list }
type AppendResult = Appended | AppendConflict | DuplicateEvent
type IExecutionSessionJournal =
    abstract member ReadAttempt: assignmentId:Guid * attemptId:Guid * CancellationToken -> Task<StoredSession option>
    abstract member AppendAttempt: assignmentId:Guid * attemptId:Guid * int64 * SessionEvent * CancellationToken -> Task<AppendResult>

type SessionState =
    { Intent: LaunchIntent
      Observation: SessionObservation option
      CancelWasRequested: bool
      LaunchAttempts: int
      Revision: int64 }

[<RequireQualifiedAccess>]
module ExecutionProtocol =
    let launchSchema = "fsgg.orchestration.execution-launch/2"
    let supportedSchemas = set [ launchSchema ]
    let negotiate advertised =
        match advertised |> List.tryFind supportedSchemas.Contains with
        | Some schema -> Ok schema
        | None -> Error "execution-schema-not-supported"

    [<CLIMutable>]
    type LaunchWire =
        { Schema:string; AssignmentId:Guid; AttemptId:Guid; Generation:int64
          InputDigest:string; Workspace:string; RequestedModel:string; RequestedEffort:string
          Deadline:DateTimeOffset; MaximumRuntimeSeconds:int64; MaximumAttempts:int; RecordedAt:DateTimeOffset }

    let private options =
        let value=JsonSerializerOptions(PropertyNamingPolicy=JsonNamingPolicy.CamelCase,MaxDepth=8)
        value.PropertyNameCaseInsensitive <- false
        value.UnmappedMemberHandling <- JsonUnmappedMemberHandling.Disallow
        value
    let private properties = set ["schema";"assignmentId";"attemptId";"generation";"inputDigest";"workspace";"requestedModel";"requestedEffort";"deadline";"maximumRuntimeSeconds";"maximumAttempts";"recordedAt"]
    let encode (intent:LaunchIntent) =
        ({ Schema = intent.Schema
           AssignmentId = intent.Key.AssignmentId
           AttemptId = intent.Key.AttemptId
           Generation = intent.Key.Generation
           InputDigest = intent.InputDigest
           Workspace = intent.Workspace
           RequestedModel = defaultArg intent.Requested.Model null
           RequestedEffort = defaultArg intent.Requested.Effort null
           Deadline = intent.Limits.Deadline
           MaximumRuntimeSeconds = int64 intent.Limits.MaximumRuntime.TotalSeconds
           MaximumAttempts = intent.Limits.MaximumAttempts
           RecordedAt = intent.RecordedAt }:LaunchWire)
        |> fun wire -> JsonSerializer.SerializeToUtf8Bytes(wire,options)
    let decode (bytes:byte array) =
        try
            use document=JsonDocument.Parse(ReadOnlyMemory bytes,JsonDocumentOptions(MaxDepth=8))
            if document.RootElement.ValueKind<>JsonValueKind.Object then Error "execution-launch-object-required"
            else
                let names=document.RootElement.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
                if names<>properties then Error "execution-launch-shape-refused"
                else
                    let wire=JsonSerializer.Deserialize<LaunchWire>(ReadOnlySpan bytes,options)
                    if isNull(box wire) || wire.Schema<>launchSchema then Error "execution-launch-schema-refused"
                    else Ok { Schema=wire.Schema;Key={AssignmentId=wire.AssignmentId;AttemptId=wire.AttemptId;Generation=wire.Generation}
                              InputDigest=wire.InputDigest;Workspace=wire.Workspace
                              Requested={Model=Option.ofObj wire.RequestedModel;Effort=Option.ofObj wire.RequestedEffort}
                              Limits={Deadline=wire.Deadline;MaximumRuntime=TimeSpan.FromSeconds(float wire.MaximumRuntimeSeconds);MaximumAttempts=wire.MaximumAttempts}
                              RecordedAt=wire.RecordedAt }
        with :? JsonException -> Error "execution-launch-json-refused"

[<RequireQualifiedAccess>]
module SessionState =
    let replay events =
        events |> List.fold (fun state eventValue ->
            match state,eventValue with
            | None,LaunchIntentRecorded intent -> Some { Intent=intent;Observation=None;CancelWasRequested=false;LaunchAttempts=0;Revision=1L }
            | Some value,LaunchAttemptRecorded(attemptNumber,_) -> Some { value with LaunchAttempts=max value.LaunchAttempts attemptNumber;Revision=value.Revision+1L }
            | Some value,StartObserved observation
            | Some value,ObservationRecorded observation -> Some { value with Observation=Some observation;Revision=value.Revision+1L }
            | Some value,CancelRequested _ -> Some { value with CancelWasRequested=true;Revision=value.Revision+1L }
            | value,_ -> value) None

type CoordinationResult =
    | SessionAdvanced of SessionState
    | SessionDuplicate of SessionState
    | SessionRefused of string
    | SessionNeedsReconciliation of string

type ExecutionSessionCoordinator(provider:IExecutionProvider,journal:IExecutionSessionJournal,clock:TimeProvider) =
    let validDigest (value:string) = value.Length=64 && value |> Seq.forall Char.IsAsciiHexDigit
    let nonEmpty value = not(String.IsNullOrWhiteSpace value)
    let validUsageValue = function
        | UsageKnown(value,unitName,provenance) -> value>=0L && nonEmpty unitName && nonEmpty provenance
        | UsageUnknown provenance | UsageNotApplicable provenance -> nonEmpty provenance
    let validCost = function
        | CostKnown(amount,currency,provenance) -> amount>=0M && nonEmpty currency && nonEmpty provenance
        | CostUnknown provenance | CostNotApplicable provenance -> nonEmpty provenance
    let boundedReferences (observation:SessionObservation) =
        nonEmpty observation.Provider.Provider && nonEmpty observation.Provider.AdapterVersion
        && observation.Output.Length<=64 && observation.LifecycleReferences.Length<=64
        && (observation.Output @ observation.LifecycleReferences |> List.forall(fun item -> nonEmpty item.Reference && item.Reference.Length<=2048 && nonEmpty item.Kind && item.Kind.Length<=64))
        && observation.Usage.Values.Count<=64 && observation.Usage.Values |> Map.forall(fun name value -> nonEmpty name && validUsageValue value)
        && validCost observation.Usage.Cost
        && observation.Candidate |> Option.forall(fun candidate -> nonEmpty candidate.HeadSha && candidate.HeadSha.Length<=128 && nonEmpty candidate.TreeSha && candidate.TreeSha.Length<=128)
    let append key revision eventValue cancellationToken = journal.AppendAttempt(key.AssignmentId,key.AttemptId,revision,eventValue,cancellationToken)
    let expired intent now =
        now>=intent.Limits.Deadline || now>=intent.RecordedAt.Add(intent.Limits.MaximumRuntime)
    let readState key cancellationToken = task {
        let! stored=journal.ReadAttempt(key.AssignmentId,key.AttemptId,cancellationToken)
        return stored |> Option.bind(fun value -> SessionState.replay value.Events) }
    let recordObservation state eventValue observation cancellationToken = task {
        if not(boundedReferences observation) then return SessionRefused "provider-observation-bounds-refused"
        else
            let! appended=append state.Intent.Key state.Revision eventValue cancellationToken
            match appended with
            | Appended -> return SessionAdvanced { state with Observation=Some observation;Revision=state.Revision+1L }
            | DuplicateEvent -> return SessionDuplicate state
            | AppendConflict -> return SessionNeedsReconciliation "journal-write-conflict" }
    let launchProvider state cancellationToken = task {
        if state.LaunchAttempts>=state.Intent.Limits.MaximumAttempts then return SessionRefused "execution-attempt-budget-exhausted"
        else
            let attemptNumber=state.LaunchAttempts+1
            let! recorded=append state.Intent.Key state.Revision (LaunchAttemptRecorded(attemptNumber,clock.GetUtcNow())) cancellationToken
            match recorded with
            | AppendConflict -> return SessionNeedsReconciliation "journal-write-conflict"
            | DuplicateEvent -> return SessionNeedsReconciliation "journal-attempt-ambiguity"
            | Appended ->
                let attempted={state with LaunchAttempts=attemptNumber;Revision=state.Revision+1L}
                let! launched=provider.Launch(state.Intent,cancellationToken)
                match launched with
                | LaunchStarted observation -> return! recordObservation attempted (StartObserved observation) observation cancellationToken
                | LaunchRefused reason -> return SessionRefused reason
                | LaunchAmbiguous reason -> return SessionNeedsReconciliation reason }
    member _.Launch(intent,cancellationToken) = task {
        let now=clock.GetUtcNow()
        if intent.Schema<>ExecutionProtocol.launchSchema then return SessionRefused "execution-schema-not-supported"
        elif intent.Key.Generation<0L || not(validDigest intent.InputDigest) || String.IsNullOrWhiteSpace intent.Workspace then return SessionRefused "execution-intent-invalid"
        elif intent.Limits.MaximumAttempts<1 || intent.Limits.MaximumRuntime<=TimeSpan.Zero || expired intent now then return SessionRefused "execution-budget-expired"
        else
            let! existing=readState intent.Key cancellationToken
            match existing with
            | Some state when state.Intent<>intent -> return SessionRefused "execution-key-conflict"
            // A durable intent with no launch-attempt event has never crossed the
            // provider boundary. This is the normal Main-owned persist-before-
            // visibility path, so the first launch is safe and still records its
            // attempt before spawning. Once an attempt exists, only reconcile.
            | Some state when state.LaunchAttempts=0 && state.Observation.IsNone ->
                return! launchProvider state cancellationToken
            | Some state ->
                let! reconciled=provider.Reconcile(intent,cancellationToken)
                match reconciled with
                | Reconciled observation -> return! recordObservation state (ObservationRecorded observation) observation cancellationToken
                | ConfirmedAbsent when not(expired intent now) -> return! launchProvider state cancellationToken
                | ConfirmedAbsent -> return SessionRefused "execution-budget-expired"
                | ReconcileUnknown reason -> return SessionNeedsReconciliation reason
            | None ->
                let! persisted=append intent.Key 0L (LaunchIntentRecorded intent) cancellationToken
                match persisted with
                | AppendConflict -> return SessionNeedsReconciliation "journal-write-conflict"
                | DuplicateEvent -> return SessionNeedsReconciliation "journal-duplicate-without-readable-state"
                | Appended ->
                    let state={Intent=intent;Observation=None;CancelWasRequested=false;LaunchAttempts=0;Revision=1L}
                    return! launchProvider state cancellationToken }
    member _.Observe(key,cancellationToken) = task {
        let! state=readState key cancellationToken
        match state with
        | None -> return SessionRefused "execution-session-not-found"
        | Some value when expired value.Intent (clock.GetUtcNow()) && value.Observation.IsNone -> return SessionRefused "execution-budget-expired"
        | Some value when expired value.Intent (clock.GetUtcNow()) && not value.CancelWasRequested ->
            let! persisted=append key value.Revision (CancelRequested(clock.GetUtcNow())) cancellationToken
            match persisted,value.Observation with
            | Appended,Some observation ->
                let! _=provider.Cancel(observation.Session,cancellationToken)
                return SessionAdvanced {value with CancelWasRequested=true;Revision=value.Revision+1L}
            | Appended,None -> return SessionNeedsReconciliation "deadline-cancel-recorded-before-provider-session-observed"
            | DuplicateEvent,_ -> return SessionDuplicate value
            | AppendConflict,_ -> return SessionNeedsReconciliation "journal-write-conflict"
        | Some value ->
            match value.Observation with
            | None ->
                let! reconciled=provider.Reconcile(value.Intent,cancellationToken)
                match reconciled with
                | Reconciled observation -> return! recordObservation value (ObservationRecorded observation) observation cancellationToken
                | ConfirmedAbsent -> return SessionNeedsReconciliation "provider-session-confirmed-absent"
                | ReconcileUnknown reason -> return SessionNeedsReconciliation reason
            | Some observation ->
                let! observed=provider.Observe(observation.Session,cancellationToken)
                match observed with
                | Ok next -> return! recordObservation value (ObservationRecorded next) next cancellationToken
                | Error reason -> return SessionNeedsReconciliation reason }
    member _.Cancel(key,cancellationToken) = task {
        let! state=readState key cancellationToken
        match state with
        | None -> return SessionRefused "execution-session-not-found"
        | Some value when value.CancelWasRequested -> return SessionDuplicate value
        | Some value ->
            let! persisted=append key value.Revision (CancelRequested(clock.GetUtcNow())) cancellationToken
            match persisted with
            | AppendConflict -> return SessionNeedsReconciliation "journal-write-conflict"
            | DuplicateEvent -> return SessionDuplicate value
            | Appended ->
                let requested={value with CancelWasRequested=true;Revision=value.Revision+1L}
                match value.Observation with
                | None -> return SessionNeedsReconciliation "cancel-recorded-before-provider-session-observed"
                | Some observation ->
                    let! cancelled=provider.Cancel(observation.Session,cancellationToken)
                    match cancelled with
                    | CancelAccepted -> return SessionAdvanced requested
                    | CancelRefused reason -> return SessionRefused reason
                    | CancelUnknown reason -> return SessionNeedsReconciliation reason }

type ExecutionSessionCommand = Launch of LaunchIntent | Observe of ExecutionKey | Reconnect of ExecutionKey | Cancel of ExecutionKey

/// Thin Akka boundary: supervision/mailbox ownership stays here; lifecycle policy stays in the neutral coordinator.
type ExecutionSessionActor(coordinator:ExecutionSessionCoordinator) =
    inherit UntypedActor()
    override this.OnReceive(message) =
        let replyTo=this.Sender
        let self=this.Self
        let operation =
            match message with
            | :? ExecutionSessionCommand as command ->
                match command with
                | Launch intent -> coordinator.Launch(intent,CancellationToken.None)
                | Observe key | Reconnect key -> coordinator.Observe(key,CancellationToken.None)
                | Cancel key -> coordinator.Cancel(key,CancellationToken.None)
            | _ -> Task.FromResult(SessionRefused "execution-command-refused")
        // Never synchronously block the Akka dispatcher while PostgreSQL or a
        // remote provider awaits. Akka's dispatcher synchronization context is
        // also where that continuation resumes, so GetResult would deadlock the
        // real durable composition even though synchronous test journals pass.
        operation.ContinueWith(fun (completed:Task<CoordinationResult>) ->
            if completed.IsCompletedSuccessfully then replyTo.Tell(completed.Result,self)
            else
                let detail =
                    if isNull completed.Exception then "cancelled"
                    else completed.Exception.GetBaseException().Message
                replyTo.Tell(SessionNeedsReconciliation($"execution-actor-operation-failed:{detail}"),self))
        |>ignore
    static member Props(coordinator) = Akka.Actor.Props.Create(fun () -> ExecutionSessionActor(coordinator))
