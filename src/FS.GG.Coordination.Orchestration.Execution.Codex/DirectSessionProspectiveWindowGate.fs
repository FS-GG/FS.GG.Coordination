namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System
open System.Text.RegularExpressions

/// A prospective window from an independently trusted issuer. No issuer is installed here.
type DirectSessionIssuedWindow =
    {
        Assignment: DirectSessionAuthorizedAssignment
        AuthorizedSourceAdapterId: string
        IssuedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
    }

/// A current-session observation from the source selected by the issued window.
type DirectSessionWindowObservation =
    {
        SourceAdapterId: string
        ObservedAt: DateTimeOffset
        CurrentTurn: DirectSessionCurrentTurn
    }

/// Future protected custody boundary: issue a fresh challenge before the observation starts.
type IDirectSessionProspectiveWindowIssuer =
    abstract member ReadIssuedWindow: unit -> Result<DirectSessionIssuedWindow, string>

/// Future authenticated current-session source. A transcript or future child is not sufficient.
type IDirectSessionWindowTurnSource =
    abstract member ReadCurrentWindowTurn: unit -> Result<DirectSessionWindowObservation, string>

/// Immutable test model only; a production one-use decision needs durable atomic custody.
type DirectSessionWindowLedger = private DirectSessionWindowLedger of Set<string>

[<RequireQualifiedAccess>]
module DirectSessionWindowLedger =
    let empty = DirectSessionWindowLedger Set.empty

    let internal contains challenge (DirectSessionWindowLedger used) = Set.contains challenge used

    let internal consume challenge (DirectSessionWindowLedger used) =
        DirectSessionWindowLedger(Set.add challenge used)

[<RequireQualifiedAccess>]
module DirectSessionProspectiveWindowGate =
    let private challengePattern = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let private maxWindow = TimeSpan.FromMinutes 5.

    let private boundedText maximum (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= maximum
        && (value |> Seq.forall (Char.IsControl >> not))

    let private read reader =
        try reader ()
        with _ -> Error "adapter-threw"

    let private utc (value: DateTimeOffset) = value.Offset = TimeSpan.Zero

    /// Shared pre-source validation for both the immutable model and a future CAS boundary.
    let internal validateIssue expectedScope expectedSourceAdapterId now issued =
        if isNull (box expectedScope)
           || not (DirectSessionTelemetryFacts.validScope expectedScope)
           || not (boundedText 128 expectedSourceAdapterId)
           || not (utc now) then
            Error "direct-session-window-input-invalid"
        elif isNull (box issued)
             || isNull (box issued.Assignment)
             || isNull (box issued.Assignment.Scope) then
            Error "direct-session-window-issue-invalid"
        elif issued.Assignment.Scope <> expectedScope then
            Error "direct-session-window-foreign-assignment"
        elif issued.AuthorizedSourceAdapterId <> expectedSourceAdapterId then
            Error "direct-session-window-foreign-source"
        elif isNull issued.Assignment.WindowChallenge
             || not (challengePattern.IsMatch issued.Assignment.WindowChallenge)
             || not (boundedText 256 issued.Assignment.NativeSessionId)
             || not (utc issued.IssuedAt)
             || not (utc issued.ExpiresAt)
             || issued.ExpiresAt <= issued.IssuedAt
             || issued.ExpiresAt - issued.IssuedAt > maxWindow then
            Error "direct-session-window-issue-invalid"
        elif now < issued.IssuedAt || now >= issued.ExpiresAt then
            Error "direct-session-window-stale"
        else
            Ok issued.Assignment.WindowChallenge

    /// Return prepared bytes and a consumed challenge only after independent records agree.
    /// This function does not persist the ledger, authenticate adapters or submit telemetry.
    let evaluate
        (expectedScope: DirectSessionTurnScope)
        (expectedSourceAdapterId: string)
        (now: DateTimeOffset)
        (ledger: DirectSessionWindowLedger)
        (issuer: IDirectSessionProspectiveWindowIssuer)
        (source: IDirectSessionWindowTurnSource)
        : Result<DirectSessionWindowLedger * PreparedDirectSessionTurn, string> =
        if isNull (box expectedScope)
           || isNull (box ledger)
           || isNull (box issuer)
           || isNull (box source)
           || not (boundedText 128 expectedSourceAdapterId)
           || not (utc now) then
            Error "direct-session-window-input-invalid"
        else
            match read issuer.ReadIssuedWindow with
            | Error _ -> Error "direct-session-window-issuer-unavailable"
            | Ok issued ->
                match validateIssue expectedScope expectedSourceAdapterId now issued with
                | Error code -> Error code
                | Ok challenge when DirectSessionWindowLedger.contains challenge ledger ->
                    Error "direct-session-window-replayed"
                | Ok challenge ->
                    match read source.ReadCurrentWindowTurn with
                    | Error _ -> Error "direct-session-window-source-unavailable"
                    | Ok observation when isNull (box observation) || isNull (box observation.CurrentTurn) ->
                        Error "direct-session-window-observation-invalid"
                    | Ok observation when observation.SourceAdapterId <> expectedSourceAdapterId ->
                        Error "direct-session-window-source-substitution"
                    | Ok observation when
                        not (utc observation.ObservedAt)
                        || observation.ObservedAt <= issued.IssuedAt
                        || observation.ObservedAt > now
                        || observation.ObservedAt >= issued.ExpiresAt
                        ->
                        Error "direct-session-window-observation-outside"
                    | Ok observation ->
                        let assignmentAdapter =
                            { new IDirectSessionAssignmentAuthenticator with
                                member _.ReadAuthorizedAssignment() = Ok issued.Assignment }
                        let sourceAdapter =
                            { new IDirectSessionCurrentTurnSource with
                                member _.ReadCurrentTurn() = Ok observation.CurrentTurn }
                        DirectSessionCorrelationHandoff.prepare expectedScope assignmentAdapter sourceAdapter
                        |> Result.map (fun prepared ->
                            DirectSessionWindowLedger.consume challenge ledger,
                            prepared)
