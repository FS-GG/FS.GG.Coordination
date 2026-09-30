namespace FS.GG.Coordination.Orchestration.Execution.AgentFramework

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Execution.Codex

[<CLIMutable>]
type AgentFrameworkUsageProjection =
    {
        Name: string
        Kind: string
        Value: Nullable<int64>
        UnitName: string
        Provenance: string
    }

[<CLIMutable>]
type AgentFrameworkExecutionProjection =
    {
        Lifecycle: string
        CandidateId: string
        HeadSha: string
        TreeSha: string
        Usage: AgentFrameworkUsageProjection array
        DeliveryClaimed: bool
    }

type AgentFrameworkTrialProfile =
    {
        AgentId: string
        ExactInput: string
        ExactKey: ExecutionKey
        ExactWorkspace: string
        ExactModel: string option
        ExactEffort: string option
        ExactDeadline: DateTimeOffset
        ExactMaximumRuntime: TimeSpan
        ExactMaximumAttempts: int
        ExactRecordedAt: DateTimeOffset
    }

type BoundAgentFrameworkLaunch internal (profile: AgentFrameworkTrialProfile, intent: LaunchIntent) =
    member _.Profile = profile
    member _.Intent = intent

[<RequireQualifiedAccess>]
module BoundAgentFrameworkLaunch =
    let private digest (value: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes value)
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let create profile intent =
        if
            isNull (box profile)
            || String.IsNullOrWhiteSpace profile.AgentId
            || profile.AgentId.Length > 128
            || String.IsNullOrWhiteSpace profile.ExactInput
            || profile.ExactInput.Length > 8192
        then
            Error "agent-framework-profile-invalid"
        elif
            intent.Schema <> ExecutionProtocol.launchSchema
            || intent.InputDigest <> digest profile.ExactInput
            || intent.Key <> profile.ExactKey
            || intent.Workspace <> profile.ExactWorkspace
            || intent.Requested.Model <> profile.ExactModel
            || intent.Requested.Effort <> profile.ExactEffort
            || intent.Limits.Deadline <> profile.ExactDeadline
            || intent.Limits.MaximumRuntime <> profile.ExactMaximumRuntime
            || intent.Limits.MaximumAttempts <> profile.ExactMaximumAttempts
            || intent.RecordedAt <> profile.ExactRecordedAt
            || profile.ExactMaximumAttempts <> 1
        then
            Error "agent-framework-launch-binding-mismatch"
        else
            Ok(BoundAgentFrameworkLaunch(profile, intent))

[<RequireQualifiedAccess>]
module AgentFrameworkExecutionProjection =
    let private lifecycle =
        function
        | Starting -> "starting"
        | Running -> "running"
        | Cancelling -> "cancelling"
        | Succeeded -> "succeeded"
        | Failed -> "failed"
        | Cancelled -> "cancelled"
        | DeadlineExceeded -> "deadline-exceeded"
        | OutcomeUnknown -> "outcome-unknown"

    let private usageValue name =
        function
        | UsageKnown(value, unitName, provenance) ->
            {
                Name = name
                Kind = "known"
                Value = Nullable value
                UnitName = unitName
                Provenance = provenance
            }
        | UsageUnknown provenance ->
            {
                Name = name
                Kind = "unknown"
                Value = Nullable()
                UnitName = null
                Provenance = provenance
            }
        | UsageNotApplicable provenance ->
            {
                Name = name
                Kind = "not-applicable"
                Value = Nullable()
                UnitName = null
                Provenance = provenance
            }

    let ofCoordinationResult result =
        let state =
            match result with
            | SessionAdvanced state
            | SessionDuplicate state -> Ok state
            | SessionRefused reason -> Error reason
            | SessionNeedsReconciliation reason -> Error reason

        match state with
        | Error reason -> Error reason
        | Ok state ->
            match state.Observation with
            | None -> Error "agent-framework-observation-required"
            | Some observation ->
                let candidateId, headSha, treeSha =
                    match observation.Candidate with
                    | Some candidate -> candidate.CandidateId.ToString("D"), candidate.HeadSha, candidate.TreeSha
                    | None -> null, null, null

                Ok
                    {
                        Lifecycle = lifecycle observation.Lifecycle
                        CandidateId = candidateId
                        HeadSha = headSha
                        TreeSha = treeSha
                        Usage =
                            observation.Usage.Values
                            |> Seq.map (fun item -> usageValue item.Key item.Value)
                            |> Seq.sortBy _.Name
                            |> Seq.toArray
                        DeliveryClaimed = false
                    }

    let encode projection = JsonSerializer.Serialize projection

    let decode (value: string) =
        try
            let projection = JsonSerializer.Deserialize<AgentFrameworkExecutionProjection> value

            if isNull (box projection) || projection.DeliveryClaimed then
                Error "agent-framework-projection-invalid"
            else
                Ok projection
        with :? JsonException ->
            Error "agent-framework-projection-invalid"

type private BoundAgentFrameworkSession(ownerId: Guid) =
    inherit AgentSession()
    member _.OwnerId = ownerId

type private SingleUpdate(factory: CancellationToken -> Task<AgentResponseUpdate>) =
    interface IAsyncEnumerable<AgentResponseUpdate> with
        member _.GetAsyncEnumerator(cancellationToken) =
            let mutable current = Unchecked.defaultof<AgentResponseUpdate>
            let mutable moved = false

            { new IAsyncEnumerator<AgentResponseUpdate> with
                member _.Current = current

                member _.MoveNextAsync() =
                    ValueTask<bool>(
                        task {
                            if moved then
                                return false
                            else
                                moved <- true
                                let! value = factory cancellationToken
                                current <- value
                                return true
                        }
                    )

                member _.DisposeAsync() = ValueTask() }

/// Optional Microsoft Agent Framework facade over one already-bound neutral launch.
/// It does not construct provider requests, authorize effects, or claim delivery.
type BoundExecutionAgent(coordinator: ExecutionSessionCoordinator, binding: BoundAgentFrameworkLaunch) =
    inherit AIAgent()

    let ownerId = Guid.NewGuid()
    let mutable invoked = 0

    let validate (messages: IEnumerable<ChatMessage>) (session: AgentSession) (options: AgentRunOptions) =
        let supplied = messages |> Seq.toArray

        if isNull (box session) || not (session :? BoundAgentFrameworkSession) then
            Error "agent-framework-session-required"
        else
            let actual = session :?> BoundAgentFrameworkSession

            if actual.OwnerId <> ownerId then
                Error "agent-framework-session-identity-mismatch"
            elif not (isNull options) then
                Error "agent-framework-options-refused"
            elif supplied.Length <> 1 || supplied[0].Role <> ChatRole.User || supplied[0].Text <> binding.Profile.ExactInput then
                Error "agent-framework-input-mismatch"
            else
                Ok()

    let invoke messages session options cancellationToken =
        task {
            match validate messages session options with
            | Error reason -> return raise (InvalidOperationException reason)
            | Ok() ->
                if Interlocked.CompareExchange(&invoked, 1, 0) <> 0 then
                    return raise (InvalidOperationException "agent-framework-duplicate-invocation-refused")
                else
                    let! result = coordinator.Launch(binding.Intent, cancellationToken)

                    match AgentFrameworkExecutionProjection.ofCoordinationResult result with
                    | Error reason -> return raise (InvalidOperationException reason)
                    | Ok projection -> return projection
        }

    override _.IdCore = binding.Profile.AgentId
    override _.Name = "FS.GG bounded execution adapter"
    override _.Description = "Projects one previously bound FS.GG execution through Microsoft Agent Framework."

    override _.CreateSessionCoreAsync(_) = ValueTask<AgentSession>(BoundAgentFrameworkSession(ownerId))

    override _.SerializeSessionCoreAsync(_, _, _) =
        ValueTask<JsonElement>(Task.FromException<JsonElement>(NotSupportedException "agent-framework-session-serialization-refused"))

    override _.DeserializeSessionCoreAsync(_, _, _) =
        ValueTask<AgentSession>(Task.FromException<AgentSession>(NotSupportedException "agent-framework-session-deserialization-refused"))

    override _.RunCoreAsync(messages, session, options, cancellationToken) =
        task {
            let! projection = invoke messages session options cancellationToken
            let response = AgentResponse(ChatMessage(ChatRole.Assistant, AgentFrameworkExecutionProjection.encode projection))
            response.AgentId <- binding.Profile.AgentId
            return response
        }

    override _.RunCoreStreamingAsync(messages, session, options, cancellationToken) =
        SingleUpdate(fun enumerationToken ->
            task {
                use linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, enumerationToken)
                let! projection = invoke messages session options linked.Token
                let update = AgentResponseUpdate(ChatRole.Assistant, AgentFrameworkExecutionProjection.encode projection)
                update.AgentId <- binding.Profile.AgentId
                return update
            })

        :> IAsyncEnumerable<AgentResponseUpdate>

[<RequireQualifiedAccess>]
module AgentFrameworkCodexExecution =
    /// Composes the accepted concrete provider path without changing its authority:
    /// CodexExecutionProvider -> ExecutionSessionCoordinator -> bounded AIAgent facade.
    let createAgent options input candidateInspector journal clock profile intent =
        BoundAgentFrameworkLaunch.create profile intent
        |> Result.map (fun binding ->
            let coordinator = CodexExecution.coordinator options input candidateInspector journal clock
            BoundExecutionAgent(coordinator, binding))
