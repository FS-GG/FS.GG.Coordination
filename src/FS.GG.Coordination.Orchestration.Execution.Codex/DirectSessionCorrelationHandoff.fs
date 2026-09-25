namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System
open System.Text.RegularExpressions

/// A prospective assignment returned by an independently trusted assignment adapter.
/// The challenge must be issued before the observation window; this type cannot prove that fact.
type DirectSessionAuthorizedAssignment =
    {
        Scope: DirectSessionTurnScope
        NativeSessionId: string
        WindowChallenge: string
    }

/// One native turn returned by a supported observer of the current interactive session.
/// The observer, its authentication, and its continuity journal are not implemented here.
type DirectSessionCurrentTurn =
    {
        NativeSessionId: string
        WindowChallenge: string
        NativeThreadId: string
        CompletedTurn: DirectSessionCompletedTurn
    }

/// Security boundary for an owner-approved prospective assignment, not a caller-supplied echo.
type IDirectSessionAssignmentAuthenticator =
    abstract member ReadAuthorizedAssignment: unit -> Result<DirectSessionAuthorizedAssignment, string>

/// Security boundary for the *current* native session, not a transcript or future child process.
type IDirectSessionCurrentTurnSource =
    abstract member ReadCurrentTurn: unit -> Result<DirectSessionCurrentTurn, string>

[<RequireQualifiedAccess>]
module DirectSessionCorrelationHandoff =
    let private challengePattern = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private read failure reader =
        try reader ()
        with _ -> Error failure

    let private boundedText maximum (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= maximum
        && (value |> Seq.forall (Char.IsControl >> not))

    /// Prepare only structurally correlated facts. Neither interface has an installed trusted
    /// implementation, so an Ok result is not evidence of current-session capture or authority.
    let prepare
        (expectedScope: DirectSessionTurnScope)
        (assignmentAuthenticator: IDirectSessionAssignmentAuthenticator)
        (currentTurnSource: IDirectSessionCurrentTurnSource)
        : Result<PreparedDirectSessionTurn, string> =
        if isNull (box expectedScope)
           || isNull (box assignmentAuthenticator)
           || isNull (box currentTurnSource) then
            Error "direct-session-adapter-missing"
        elif not (DirectSessionTelemetryFacts.validScope expectedScope) then
            Error "direct-session-assignment-scope-invalid"
        else
            match read "direct-session-assignment-unavailable" assignmentAuthenticator.ReadAuthorizedAssignment with
            | Error _ -> Error "direct-session-assignment-unavailable"
            | Ok assignment when isNull (box assignment) || isNull (box assignment.Scope) ->
                Error "direct-session-assignment-session-invalid"
            | Ok assignment when
                not (boundedText 256 assignment.NativeSessionId)
                || isNull assignment.WindowChallenge
                || not (challengePattern.IsMatch assignment.WindowChallenge)
                ->
                Error "direct-session-assignment-session-invalid"
            | Ok assignment when assignment.Scope <> expectedScope ->
                Error "direct-session-assignment-scope-mismatch"
            | Ok assignment ->
                match read "direct-session-current-source-unavailable" currentTurnSource.ReadCurrentTurn with
                | Error _ -> Error "direct-session-current-source-unavailable"
                | Ok current when
                    isNull (box current)
                    || isNull (box current.CompletedTurn)
                    || isNull (box current.CompletedTurn.Usage)
                    || isNull (box current.CompletedTurn.SourceBinding)
                    ->
                    Error "direct-session-source-session-invalid"
                | Ok current when not (boundedText 256 current.NativeSessionId) ->
                    Error "direct-session-source-session-invalid"
                | Ok current when current.NativeSessionId <> assignment.NativeSessionId ->
                    Error "direct-session-borrowed-session"
                | Ok current when current.WindowChallenge <> assignment.WindowChallenge ->
                    Error "direct-session-window-mismatch"
                | Ok current when current.NativeThreadId <> assignment.Scope.ThreadId ->
                    Error "direct-session-current-thread-mismatch"
                | Ok current when current.CompletedTurn.Usage.ThreadId <> current.NativeThreadId ->
                    Error "direct-session-turn-thread-mismatch"
                | Ok current ->
                    DirectSessionTelemetryFacts.prepareCompletedTurn
                        assignment.Scope current.CompletedTurn
