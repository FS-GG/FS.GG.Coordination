namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System

/// A future native source must attest the currently attached session and transport. This record
/// alone cannot prove current-session custody or that the first turn notification was retained.
type CodexAppServerCurrentSubscriptionObservation =
    {
        SourceAdapterId: string
        NativeSessionId: string
        WindowChallenge: string
        NativeThreadId: string
        SubscribedAt: DateTimeOffset
        ObservedAt: DateTimeOffset
        Binding: CodexAppServerSubscriptionBinding
    }

/// Future authenticated current-session App Server source, not a transcript or child process.
/// It must bind the prospective challenge to the attached native session and connection.
type ICodexAppServerCurrentSubscriptionSource =
    abstract member ReadCurrentSubscription:
        unit -> Result<CodexAppServerCurrentSubscriptionObservation, string>

/// A structural handoff only. No native source, journal, or Host producer is activated here.
type CodexAppServerReservedSubscription =
    {
        Reservation: DirectSessionChallengeReservationReceipt
        Binding: CodexAppServerSubscriptionBinding
        SubscribedAt: DateTimeOffset
        ValidatedAt: DateTimeOffset
    }

[<RequireQualifiedAccess>]
module CodexAppServerCurrentSubscription =
    let private read reader =
        try reader ()
        with _ -> Error "adapter-threw"

    let private boundedText (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 256
        && (value |> Seq.forall (Char.IsControl >> not))

    let private utc (value: DateTimeOffset) = value.Offset = TimeSpan.Zero

    /// Reserve the issued challenge before reading the current-session source. A confirmed
    /// reservation is never released after a source, clock or binding gap.
    let prepare
        (expectedScope: DirectSessionTurnScope)
        (expectedSourceAdapterId: string)
        (expectedTransport: string)
        (clock: IDirectSessionWindowClock)
        (issuer: IDirectSessionProspectiveWindowIssuer)
        (source: ICodexAppServerCurrentSubscriptionSource)
        (store: IDirectSessionChallengeReservationStore)
        : Result<CodexAppServerReservedSubscription, DirectSessionReservationFailure> =
        if isNull (box clock)
           || isNull (box issuer)
           || isNull (box source)
           || isNull (box store)
           || not (boundedText expectedTransport) then
            Error(Rejected "app-server-current-subscription-adapter-missing")
        else
            match read clock.ReadUtcNow with
            | Error _ -> Error(Rejected "app-server-current-subscription-clock-unavailable")
            | Ok reservationNow ->
                match read issuer.ReadIssuedWindow with
                | Error _ -> Error(Rejected "app-server-current-subscription-issuer-unavailable")
                | Ok issued ->
                    match DirectSessionProspectiveWindowGate.validateIssue
                              expectedScope expectedSourceAdapterId reservationNow issued with
                    | Error code -> Error(Rejected code)
                    | Ok challenge ->
                        let request =
                            { Challenge = challenge
                              Scope = issued.Assignment.Scope
                              NativeSessionId = issued.Assignment.NativeSessionId
                              AuthorizedSourceAdapterId = issued.AuthorizedSourceAdapterId
                              IssuedAt = issued.IssuedAt
                              ExpiresAt = issued.ExpiresAt }
                        match read (fun () -> store.TryReserveOnce request) with
                        | Error _ ->
                            Error(StoreOutcomeUnknown "app-server-current-subscription-store-unavailable")
                        | Ok ChallengeAlreadyReserved -> Error AlreadyReserved
                        | Ok (ChallengeReserved receipt) when
                            isNull (box receipt)
                            || isNull (box receipt.Request)
                            || receipt.Request <> request
                            || not (boundedText receipt.ReservationId) ->
                            Error(StoreOutcomeUnknown "app-server-current-subscription-receipt-invalid")
                        | Ok (ChallengeReserved receipt) ->
                            let gap code = Error(ReservedGap(receipt, code))
                            match read source.ReadCurrentSubscription with
                            | Error _ -> gap "app-server-current-subscription-source-unavailable"
                            | Ok observation when
                                isNull (box observation)
                                || isNull (box observation.Binding)
                                || isNull (box observation.Binding.Scope) ->
                                gap "app-server-current-subscription-observation-invalid"
                            | Ok observation ->
                                match read clock.ReadUtcNow with
                                | Error _ -> gap "app-server-current-subscription-clock-unavailable"
                                | Ok observedNow when
                                    not (utc observedNow)
                                    || not (utc observation.SubscribedAt)
                                    || not (utc observation.ObservedAt)
                                    || observation.SubscribedAt < issued.IssuedAt
                                    || observation.SubscribedAt < reservationNow
                                    || observation.SubscribedAt >= observation.ObservedAt
                                    || observation.ObservedAt > observedNow
                                    || observedNow >= issued.ExpiresAt
                                    || observation.ObservedAt >= issued.ExpiresAt ->
                                    gap "app-server-current-subscription-outside-window"
                                | Ok _ when observation.SourceAdapterId <> expectedSourceAdapterId ->
                                    gap "app-server-current-subscription-source-substitution"
                                | Ok _ when observation.NativeSessionId <> issued.Assignment.NativeSessionId ->
                                    gap "app-server-current-subscription-borrowed-session"
                                | Ok _ when observation.WindowChallenge <> challenge ->
                                    gap "app-server-current-subscription-challenge-mismatch"
                                | Ok _ when observation.NativeThreadId <> expectedScope.ThreadId ->
                                    gap "app-server-current-subscription-thread-mismatch"
                                | Ok observedNow ->
                                    let bindingAdapter =
                                        { new ICodexAppServerSubscriptionAuthenticator with
                                            member _.ReadBoundSubscription() = Ok observation.Binding }
                                    match CodexAppServerContinuity.beginWindow
                                              expectedScope observation.Binding.TurnId expectedTransport
                                              bindingAdapter with
                                    | Error code -> gap code
                                    | Ok _ ->
                                        Ok
                                            { Reservation = receipt
                                              Binding = observation.Binding
                                              SubscribedAt = observation.SubscribedAt
                                              ValidatedAt = observedNow }
