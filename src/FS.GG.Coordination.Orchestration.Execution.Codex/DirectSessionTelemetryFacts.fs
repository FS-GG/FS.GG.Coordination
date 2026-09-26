namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

/// Supplied assignment fields. Authentication and current-session binding are external obligations.
type DirectSessionTurnScope =
    {
        WorkspaceId: string
        Repository: string
        ItemId: string
        IssueRef: string
        AttemptId: string
        InvocationId: string
        ThreadId: string
        SourceIdentity: string
        ProducerId: string
        BindingDigest: string
    }

/// A native completed-turn fact carrying the independently supplied source binding.
/// This record does not prove that a current-session event hook exists.
type DirectSessionCompletedTurn =
    {
        SourceBinding: DirectSessionTurnScope
        Usage: CodexTurnUsage
        CounterProvenance: string
    }

/// Bytes and submit scope prepared by pure code. No publication or Host application is implied.
type PreparedDirectSessionTurn =
    {
        WorkspaceId: string
        Repository: string
        ItemId: string
        IssueRef: string
        AttemptId: string
        InvocationId: string
        ThreadId: string
        TurnId: string
        TurnSequence: int64
        SourceIdentity: string
        ProducerId: string
        BindingDigest: string
        IngestId: string
        EventIdentity: string
        PayloadSha256: string
        Payload: byte array
    }

[<RequireQualifiedAccess>]
module DirectSessionTelemetryFacts =
    let private repositoryName =
        Regex("^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)
    let private hexDigest = Regex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)
    let private nonTurnProvenance =
        set [ "thread-last-total-snapshot"; "exec-child-turn-completed"; "one-upstream-response" ]

    let private text maximum (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= maximum
        && (value |> Seq.forall (Char.IsControl >> not))

    let private hashBytes (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let private hashText (value: string) =
        Encoding.UTF8.GetBytes value |> hashBytes

    let private optional (target: JsonObject) (name: string) (value: string option) =
        target[name] <- value |> Option.map JsonValue.Create |> Option.defaultValue null

    let internal validScope (scope: DirectSessionTurnScope) =
        text 128 scope.WorkspaceId
        && text 128 scope.Repository
        && repositoryName.IsMatch scope.Repository
        && text 256 scope.ItemId
        && text 256 scope.IssueRef
        && Regex.IsMatch(scope.IssueRef, "^" + Regex.Escape(scope.Repository) + "#[1-9][0-9]*$")
        && text 128 scope.AttemptId
        && text 256 scope.InvocationId
        && text 256 scope.ThreadId
        && text 128 scope.SourceIdentity
        && text 128 scope.ProducerId
        && not (isNull scope.BindingDigest)
        && hexDigest.IsMatch scope.BindingDigest

    let internal validUsage (turn: CodexTurnUsage) =
        not (isNull (box turn))
        && turn.TurnSequence > 0L
        && ([ turn.Provider; turn.ObservedModel; turn.ObservedEffort; turn.Backend ]
            |> List.forall (Option.forall (text 128)))
        && turn.Input >= 0L
        && turn.CachedInput >= 0L
        && turn.CachedInput <= turn.Input
        && turn.Output >= 0L
        && (turn.Reasoning |> Option.forall (fun value -> value >= 0L && value <= turn.Output))
        && turn.Total >= 0L
        && turn.Input <= Int64.MaxValue - turn.Output
        && turn.Total = turn.Input + turn.Output

    /// Map one already observed native turn into the existing ingest/1 usage shape.
    /// A caller must separately prove the native hook, assignment, starts, submission and applied receipt.
    let prepareCompletedTurn (assignment: DirectSessionTurnScope) (observed: DirectSessionCompletedTurn)
        : Result<PreparedDirectSessionTurn, string> =
        if not (validScope assignment) then Error "direct-session-assignment-invalid"
        elif not (validScope observed.SourceBinding) then Error "direct-session-source-binding-invalid"
        elif assignment <> observed.SourceBinding then Error "direct-session-scope-mismatch"
        elif observed.Usage.ThreadId <> assignment.ThreadId then Error "direct-session-thread-mismatch"
        elif observed.Usage.TurnId |> Option.forall (text 256 >> not) then
            Error "direct-session-native-turn-id-missing"
        elif not (text 256 observed.CounterProvenance) then Error "direct-session-provenance-missing"
        elif Set.contains observed.CounterProvenance nonTurnProvenance then
            Error "direct-session-provenance-not-completed-turn"
        elif not (validUsage observed.Usage) then Error "direct-session-usage-invalid"
        else
            let turn = observed.Usage
            let nativeId = turn.TurnId.Value
            let identity =
                "runtime-usage-"
                + hashText (assignment.InvocationId + "\u001f" + turn.ThreadId + "\u001f" + nativeId)
            let cursor = hashText identity
            let ingestId = "batch-" + cursor
            let usage = JsonObject()
            usage["kind"] <- "runtime-turn-usage"
            usage["identity"] <- identity
            usage["itemId"] <- assignment.ItemId
            usage["revision"] <- 0
            usage["invocationId"] <- assignment.InvocationId
            usage["threadId"] <- turn.ThreadId
            usage["turnId"] <- nativeId
            usage["turnSequence"] <- turn.TurnSequence
            optional usage "provider" turn.Provider
            usage["requestedModel"] <- null
            optional usage "observedModel" turn.ObservedModel
            usage["requestedEffort"] <- null
            optional usage "observedEffort" turn.ObservedEffort
            optional usage "backend" turn.Backend
            usage["scope"] <- "completed-turn"
            usage["provenance"] <- observed.CounterProvenance
            usage["input"] <- turn.Input
            usage["cachedInput"] <- turn.CachedInput
            usage["output"] <- turn.Output
            usage["reasoning"] <- turn.Reasoning |> Option.map JsonValue.Create |> Option.defaultValue null
            usage["total"] <- turn.Total

            let envelope = JsonObject()
            envelope["schema"] <- "fsgg.telemetry.ingest/1"
            envelope["ingestId"] <- ingestId
            envelope["sourceIdentity"] <- assignment.SourceIdentity
            envelope["generation"] <- assignment.InvocationId
            envelope["cursor"] <- cursor
            envelope["eventCount"] <- 1
            let events = JsonArray()
            events.Add usage
            envelope["events"] <- events
            let bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString(JsonSerializerOptions(WriteIndented = false)))

            Ok
                {
                    WorkspaceId = assignment.WorkspaceId
                    Repository = assignment.Repository
                    ItemId = assignment.ItemId
                    IssueRef = assignment.IssueRef
                    AttemptId = assignment.AttemptId
                    InvocationId = assignment.InvocationId
                    ThreadId = turn.ThreadId
                    TurnId = nativeId
                    TurnSequence = turn.TurnSequence
                    SourceIdentity = assignment.SourceIdentity
                    ProducerId = assignment.ProducerId
                    BindingDigest = assignment.BindingDigest.ToLowerInvariant()
                    IngestId = ingestId
                    EventIdentity = identity
                    PayloadSha256 = hashBytes bytes
                    Payload = bytes
                }
