namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System

/// Exact facts to reserve. A store must enforce global uniqueness on Challenge, across scopes.
type DirectSessionChallengeReservationRequest =
    {
        Challenge: string
        Scope: DirectSessionTurnScope
        NativeSessionId: string
        AuthorizedSourceAdapterId: string
        IssuedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
    }

type DirectSessionChallengeReservationReceipt =
    {
        Request: DirectSessionChallengeReservationRequest
        ReservationId: string
    }

type DirectSessionChallengeReserveOutcome =
    | ChallengeReserved of DirectSessionChallengeReservationReceipt
    | ChallengeAlreadyReserved

/// Future durable atomic insert-if-absent boundary. An Error means the effect is unknown.
/// No production implementation is installed; this port does not issue challenges.
type IDirectSessionChallengeReservationStore =
    abstract member TryReserveOnce:
        DirectSessionChallengeReservationRequest -> Result<DirectSessionChallengeReserveOutcome, string>

/// Future trusted UTC clock. The second read must follow the native source observation.
type IDirectSessionWindowClock =
    abstract member ReadUtcNow: unit -> Result<DateTimeOffset, string>

type DirectSessionReservedTurn =
    {
        Reservation: DirectSessionChallengeReservationReceipt
        PreparedTurn: PreparedDirectSessionTurn
    }

/// ReservedGap burns the challenge. StoreOutcomeUnknown must not be retried blindly.
type DirectSessionReservationFailure =
    | Rejected of string
    | AlreadyReserved
    | StoreOutcomeUnknown of string
    | ReservedGap of DirectSessionChallengeReservationReceipt * string

[<RequireQualifiedAccess>]
module DirectSessionChallengeReservation =
    let private boundedText maximum (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= maximum
        && (value |> Seq.forall (Char.IsControl >> not))

    let private read reader =
        try reader ()
        with _ -> Error "adapter-threw"

    /// Reserve before reading the source. A successful CAS is never released on a later gap.
    /// This remains source-only until issuer, clock, source and durable store are trusted.
    let prepareOnce
        (expectedScope: DirectSessionTurnScope)
        (expectedSourceAdapterId: string)
        (clock: IDirectSessionWindowClock)
        (issuer: IDirectSessionProspectiveWindowIssuer)
        (source: IDirectSessionWindowTurnSource)
        (store: IDirectSessionChallengeReservationStore)
        : Result<DirectSessionReservedTurn, DirectSessionReservationFailure> =
        if isNull (box clock)
           || isNull (box issuer)
           || isNull (box source)
           || isNull (box store) then
            Error(Rejected "direct-session-reservation-adapter-missing")
        else
            match read clock.ReadUtcNow with
            | Error _ -> Error(Rejected "direct-session-window-clock-unavailable")
            | Ok reservationNow ->
                match read issuer.ReadIssuedWindow with
                | Error _ -> Error(Rejected "direct-session-window-issuer-unavailable")
                | Ok issued ->
                    match DirectSessionProspectiveWindowGate.validateIssue
                              expectedScope expectedSourceAdapterId reservationNow issued with
                    | Error code -> Error(Rejected code)
                    | Ok challenge ->
                        let request =
                            {
                                Challenge = challenge
                                Scope = issued.Assignment.Scope
                                NativeSessionId = issued.Assignment.NativeSessionId
                                AuthorizedSourceAdapterId = issued.AuthorizedSourceAdapterId
                                IssuedAt = issued.IssuedAt
                                ExpiresAt = issued.ExpiresAt
                            }
                        match read (fun () -> store.TryReserveOnce request) with
                        | Error _ -> Error(StoreOutcomeUnknown "direct-session-reservation-store-unavailable")
                        | Ok ChallengeAlreadyReserved -> Error AlreadyReserved
                        | Ok (ChallengeReserved receipt) when
                            isNull (box receipt)
                            || isNull (box receipt.Request)
                            || receipt.Request <> request
                            || not (boundedText 256 receipt.ReservationId)
                            ->
                            Error(StoreOutcomeUnknown "direct-session-reservation-receipt-invalid")
                        | Ok (ChallengeReserved receipt) ->
                            match read source.ReadCurrentWindowTurn with
                            | Error _ ->
                                Error(ReservedGap(receipt, "direct-session-window-source-unavailable"))
                            | Ok observation ->
                                match read clock.ReadUtcNow with
                                | Error _ ->
                                    Error(ReservedGap(receipt, "direct-session-window-clock-unavailable"))
                                | Ok observedNow when observedNow < reservationNow ->
                                    Error(ReservedGap(receipt, "direct-session-reservation-clock-regressed"))
                                | Ok _ when
                                    not (isNull (box observation))
                                    && observation.ObservedAt <= reservationNow ->
                                    Error(ReservedGap(receipt, "direct-session-reservation-observation-not-prospective"))
                                | Ok observedNow ->
                                    let issuerReadback =
                                        { new IDirectSessionProspectiveWindowIssuer with
                                            member _.ReadIssuedWindow() = Ok issued }
                                    let sourceReadback =
                                        { new IDirectSessionWindowTurnSource with
                                            member _.ReadCurrentWindowTurn() = Ok observation }
                                    match DirectSessionProspectiveWindowGate.evaluate
                                              expectedScope expectedSourceAdapterId observedNow
                                              DirectSessionWindowLedger.empty issuerReadback sourceReadback with
                                    | Error code -> Error(ReservedGap(receipt, code))
                                    | Ok (_, prepared) ->
                                        Ok { Reservation = receipt; PreparedTurn = prepared }
