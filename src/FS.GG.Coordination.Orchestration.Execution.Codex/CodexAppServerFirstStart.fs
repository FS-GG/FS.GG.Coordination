namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System
open System.Security.Cryptography
open System.Text.RegularExpressions

/// Future trusted journal read: the first native notification and store receipt must be from the
/// currently subscribed session. These supplied facts alone cannot authenticate store custody.
type CodexAppServerFirstStartObservation =
    {
        NativeSessionId: string
        WindowChallenge: string
        ObservedAt: DateTimeOffset
        Receipt: CodexAppServerJournalReceipt
    }

/// Future authenticated read of an append receipt from the durable current-session journal.
/// No implementation is installed; a caller-supplied receipt is not accepted as authority.
type ICodexAppServerFirstStartSource =
    abstract member ReadFirstStartReceipt:
        CodexAppServerSubscriptionBinding -> Result<CodexAppServerFirstStartObservation, string>

/// Structural link between a reserved subscription and one native turn/started journal entry.
type CodexAppServerBoundStart =
    {
        Reservation: DirectSessionChallengeReservationReceipt
        Binding: CodexAppServerSubscriptionBinding
        StartReceipt: CodexAppServerJournalReceipt
    }

[<RequireQualifiedAccess>]
module CodexAppServerFirstStart =
    let private shaPattern = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private boundedText (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 256
        && (value |> Seq.forall (Char.IsControl >> not))

    let private read reader =
        try reader ()
        with _ -> Error "adapter-threw"

    let private decodeFrame (encoded: string) (digest: string) =
        if isNull encoded || encoded.Length = 0 || encoded.Length > 87384
           || isNull digest || not (shaPattern.IsMatch digest) then
            Error "app-server-first-start-frame-invalid"
        else
            try
                let bytes = Convert.FromBase64String encoded
                if bytes.Length = 0 || bytes.Length > 65536
                   || Convert.ToBase64String bytes <> encoded then
                    Error "app-server-first-start-frame-invalid"
                else
                    let actual =
                        SHA256.HashData bytes
                        |> Convert.ToHexString
                        |> fun value -> value.ToLowerInvariant()
                    if actual <> digest then Error "app-server-first-start-digest-mismatch"
                    else Ok bytes
            with :? FormatException ->
                Error "app-server-first-start-frame-invalid"

    /// Reserve and authenticate the prospective subscription before reading the first journal
    /// receipt. Any later source, clock or frame gap burns the already confirmed challenge.
    let bind
        (expectedScope: DirectSessionTurnScope)
        (expectedSourceAdapterId: string)
        (expectedTransport: string)
        (clock: IDirectSessionWindowClock)
        (issuer: IDirectSessionProspectiveWindowIssuer)
        (subscriptionSource: ICodexAppServerCurrentSubscriptionSource)
        (store: IDirectSessionChallengeReservationStore)
        (firstStartSource: ICodexAppServerFirstStartSource)
        : Result<CodexAppServerBoundStart, DirectSessionReservationFailure> =
        if isNull (box firstStartSource) then
            Error(Rejected "app-server-first-start-source-missing")
        else
            match CodexAppServerCurrentSubscription.prepare expectedScope expectedSourceAdapterId
                      expectedTransport clock issuer subscriptionSource store with
            | Error failure -> Error failure
            | Ok subscription ->
                let gap code = Error(ReservedGap(subscription.Reservation, code))
                match read (fun () -> firstStartSource.ReadFirstStartReceipt subscription.Binding) with
                | Error _ -> gap "app-server-first-start-source-unavailable"
                | Ok observed when
                    isNull (box observed)
                    || isNull (box observed.Receipt)
                    || isNull (box observed.Receipt.Append) ->
                    gap "app-server-first-start-observation-invalid"
                | Ok observed ->
                    match read clock.ReadUtcNow with
                    | Error _ -> gap "app-server-first-start-clock-unavailable"
                    | Ok now when now < subscription.ValidatedAt ->
                        gap "app-server-first-start-clock-regressed"
                    | Ok now when
                        now.Offset <> TimeSpan.Zero
                        || observed.ObservedAt.Offset <> TimeSpan.Zero
                        || observed.ObservedAt <= subscription.SubscribedAt
                        || observed.ObservedAt > now
                        || now >= subscription.Reservation.Request.ExpiresAt ->
                        gap "app-server-first-start-outside-window"
                    | Ok _ when observed.NativeSessionId <> subscription.Reservation.Request.NativeSessionId ->
                        gap "app-server-first-start-borrowed-session"
                    | Ok _ when observed.WindowChallenge <> subscription.Reservation.Request.Challenge ->
                        gap "app-server-first-start-challenge-mismatch"
                    | Ok _ when
                        observed.Receipt.Append.Binding <> subscription.Binding
                        || observed.Receipt.Append.PreviousEntryId.IsSome
                        || isNull (box observed.Receipt.Append.Event)
                        || not (boundedText observed.Receipt.EntryId) ->
                        gap "app-server-first-start-receipt-mismatch"
                    | Ok _ ->
                        match observed.Receipt.Append.Event with
                        | ObservedFrame(1L, transport, connection, encoded, digest) when
                            transport = subscription.Binding.TransportIdentity
                            && connection = subscription.Binding.ConnectionId ->
                            match decodeFrame encoded digest with
                            | Error code -> gap code
                            | Ok bytes ->
                                let authenticator =
                                    { new ICodexAppServerSubscriptionAuthenticator with
                                        member _.ReadBoundSubscription() = Ok subscription.Binding }
                                match CodexAppServerContinuity.beginWindow
                                          expectedScope subscription.Binding.TurnId expectedTransport
                                          authenticator with
                                | Error code -> gap code
                                | Ok initial ->
                                    let frame =
                                        { Ordinal = 1L
                                          TransportIdentity = transport
                                          ConnectionId = connection
                                          Payload = bytes }
                                    let next = CodexAppServerContinuity.apply initial frame
                                    match CodexAppServerContinuity.status next with
                                    | InTurn 0 ->
                                        Ok
                                            { Reservation = subscription.Reservation
                                              Binding = subscription.Binding
                                              StartReceipt = observed.Receipt }
                                    | _ -> gap "app-server-first-start-native-event-invalid"
                        | _ -> gap "app-server-first-start-receipt-mismatch"
