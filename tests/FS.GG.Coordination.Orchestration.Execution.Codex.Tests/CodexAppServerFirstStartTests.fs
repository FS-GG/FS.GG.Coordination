namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type private FirstStartChallengeFake() =
    let reserved = ConcurrentDictionary<string, DirectSessionChallengeReservationRequest>()
    interface IDirectSessionChallengeReservationStore with
        member _.TryReserveOnce request =
            if reserved.TryAdd(request.Challenge, request) then
                Ok(ChallengeReserved { Request = request; ReservationId = "reservation-1" })
            else Ok ChallengeAlreadyReserved

type CodexAppServerFirstStartTests() =
    let scope: DirectSessionTurnScope =
        { WorkspaceId = "main-fsharp-dev"
          Repository = "FS-GG/.github"
          ItemId = "work-item-v1-" + String.replicate 64 "b"
          IssueRef = "FS-GG/.github#123"
          AttemptId = "attempt-1"
          InvocationId = "invocation-" + String.replicate 64 "a"
          ThreadId = "native-thread"
          SourceIdentity = "coordination"
          ProducerId = "fsharp-dev-main"
          BindingDigest = String.replicate 64 "c" }
    let sourceId = "trusted-current-session-source-v1"
    let challenge = String.replicate 64 "d"
    let issuedAt = DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero)
    let assignment =
        { Scope = scope; NativeSessionId = "native-session-current"; WindowChallenge = challenge }
    let issued =
        { Assignment = assignment
          AuthorizedSourceAdapterId = sourceId
          IssuedAt = issuedAt
          ExpiresAt = issuedAt.AddMinutes 2. }
    let binding =
        { Scope = scope
          TurnId = "native-turn"
          TransportIdentity = "authenticated-local-app-server"
          ConnectionId = "connection-1"
          SubscriptionDigest = String.replicate 64 "e"
          ProtocolVersion = "codex-app-server-v2/0.156.1" }
    let subscription =
        { SourceAdapterId = sourceId
          NativeSessionId = assignment.NativeSessionId
          WindowChallenge = challenge
          NativeThreadId = scope.ThreadId
          SubscribedAt = issuedAt.AddSeconds 70.
          ObservedAt = issuedAt.AddSeconds 80.
          Binding = binding }
    let fixture name =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "app-server", name)
        |> File.ReadAllBytes
    let started = fixture "turn-started.json"
    let frameEvent ordinal (bytes: byte array) =
        let digest = SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
        ObservedFrame(ordinal, binding.TransportIdentity, binding.ConnectionId,
                      Convert.ToBase64String bytes, digest)
    let receipt =
        { Append =
            { Binding = binding
              PreviousEntryId = None
              Event = frameEvent 1L started }
          EntryId = "entry-1" }
    let first =
        { NativeSessionId = assignment.NativeSessionId
          WindowChallenge = challenge
          ObservedAt = issuedAt.AddSeconds 95.
          Receipt = receipt }
    let clock () =
        let times =
            ConcurrentQueue<Result<DateTimeOffset, string>>
                ([ Ok(issuedAt.AddSeconds 60.)
                   Ok(issuedAt.AddSeconds 90.)
                   Ok(issuedAt.AddSeconds 100.) ])
        { new IDirectSessionWindowClock with
            member _.ReadUtcNow() =
                match times.TryDequeue() with
                | true, time -> time
                | _ -> Error "clock-exhausted" }
    let issuer =
        { new IDirectSessionProspectiveWindowIssuer with
            member _.ReadIssuedWindow() = Ok issued }
    let source =
        { new ICodexAppServerCurrentSubscriptionSource with
            member _.ReadCurrentSubscription() = Ok subscription }
    let firstSource result =
        { new ICodexAppServerFirstStartSource with
            member _.ReadFirstStartReceipt _ = result }
    let bind candidate store =
        CodexAppServerFirstStart.bind scope sourceId binding.TransportIdentity
            (clock ()) issuer source store (firstSource candidate)
    let gapCode result =
        match result with
        | Error(ReservedGap(_, code)) -> code
        | other -> failwithf "expected reserved gap, got %A" other

    [<Fact>]
    member _.``first retained native start binds prospective reservation and turn``() =
        let store = FirstStartChallengeFake() :> IDirectSessionChallengeReservationStore
        match bind (Ok first) store with
        | Ok selected ->
            Assert.Equal(challenge, selected.Reservation.Request.Challenge)
            Assert.Equal(binding.TurnId, selected.Binding.TurnId)
            Assert.Equal(receipt.EntryId, selected.StartReceipt.EntryId)
        | other -> failwithf "unexpected first-start refusal %A" other

    [<Fact>]
    member _.``wrong first ordinal predecessor binding or connection burns challenge``() =
        let wrongConnection =
            match receipt.Append.Event with
            | ObservedFrame(ordinal, transport, _, encoded, digest) ->
                ObservedFrame(ordinal, transport, "other-connection", encoded, digest)
            | event -> event
        let candidates =
            [ { first with Receipt = { receipt with Append = { receipt.Append with Event = frameEvent 2L started } } }
              { first with Receipt = { receipt with Append = { receipt.Append with PreviousEntryId = Some "entry-0" } } }
              { first with Receipt = { receipt with Append = { receipt.Append with Binding = { binding with Scope = { scope with ItemId = "other-item" } } } } }
              { first with Receipt = { receipt with Append = { receipt.Append with Event = Unchecked.defaultof<CodexAppServerJournalEvent> } } }
              { first with Receipt = { receipt with Append = { receipt.Append with Event = wrongConnection } } } ]
        for candidate in candidates do
            let store = FirstStartChallengeFake() :> IDirectSessionChallengeReservationStore
            Assert.Equal("app-server-first-start-receipt-mismatch", gapCode (bind (Ok candidate) store))
            Assert.Equal(Error AlreadyReserved, bind (Ok first) store)

    [<Fact>]
    member _.``usage or completed frame and wrong native IDs cannot impersonate first start``() =
        let cases =
            [ frameEvent 1L (fixture "usage-updated.json")
              frameEvent 1L (fixture "turn-completed.json")
              Encoding.UTF8.GetString(started).Replace("native-thread", "other-thread", StringComparison.Ordinal)
              |> Encoding.UTF8.GetBytes |> frameEvent 1L
              Encoding.UTF8.GetString(started).Replace("native-turn", "other-turn", StringComparison.Ordinal)
              |> Encoding.UTF8.GetBytes |> frameEvent 1L ]
        for event in cases do
            let store = FirstStartChallengeFake() :> IDirectSessionChallengeReservationStore
            let candidate = { first with Receipt = { receipt with Append = { receipt.Append with Event = event } } }
            Assert.Equal("app-server-first-start-native-event-invalid", gapCode (bind (Ok candidate) store))

    [<Fact>]
    member _.``changed bytes, malformed JSON and duplicate native key refuse``() =
        let tampered =
            match receipt.Append.Event with
            | ObservedFrame(ordinal, transport, connection, _, digest) ->
                ObservedFrame(ordinal, transport, connection,
                              Convert.ToBase64String(Array.append started [| byte ' ' |]), digest)
            | _ -> failwith "expected start event"
        let duplicateKey =
            Encoding.UTF8.GetString(started).Replace("\"id\":\"native-turn\"",
                "\"id\":\"native-turn\",\"id\":\"native-turn\"", StringComparison.Ordinal)
            |> Encoding.UTF8.GetBytes |> frameEvent 1L
        for event, expected in
            [ tampered, "app-server-first-start-digest-mismatch"
              frameEvent 1L (Encoding.UTF8.GetBytes "{bad-json"), "app-server-first-start-native-event-invalid"
              duplicateKey, "app-server-first-start-native-event-invalid" ] do
            let store = FirstStartChallengeFake() :> IDirectSessionChallengeReservationStore
            let candidate = { first with Receipt = { receipt with Append = { receipt.Append with Event = event } } }
            Assert.Equal(expected, gapCode (bind (Ok candidate) store))

    [<Fact>]
    member _.``borrowed session challenge and nonprospective timing burn challenge``() =
        let cases =
            [ { first with NativeSessionId = "borrowed-session" }, "app-server-first-start-borrowed-session"
              { first with WindowChallenge = String.replicate 64 "f" }, "app-server-first-start-challenge-mismatch"
              { first with ObservedAt = subscription.SubscribedAt }, "app-server-first-start-outside-window"
              { first with ObservedAt = issued.ExpiresAt }, "app-server-first-start-outside-window" ]
        for candidate, expected in cases do
            let store = FirstStartChallengeFake() :> IDirectSessionChallengeReservationStore
            Assert.Equal(expected, gapCode (bind (Ok candidate) store))

    [<Fact>]
    member _.``missing journal source burns reservation and replay reads no journal``() =
        let store = FirstStartChallengeFake() :> IDirectSessionChallengeReservationStore
        Assert.Equal("app-server-first-start-source-unavailable", gapCode (bind (Error "no-journal-reader") store))
        let mutable calls = 0
        let reader =
            { new ICodexAppServerFirstStartSource with
                member _.ReadFirstStartReceipt _ = calls <- calls + 1; Ok first }
        let replay =
            CodexAppServerFirstStart.bind scope sourceId binding.TransportIdentity
                (clock ()) issuer source store reader
        Assert.Equal(Error AlreadyReserved, replay)
        Assert.Equal(0, calls)

    [<Fact>]
    member _.``expired post-read clock burns first-start reservation``() =
        let times =
            ConcurrentQueue<Result<DateTimeOffset, string>>
                ([ Ok(issuedAt.AddSeconds 60.)
                   Ok(issuedAt.AddSeconds 90.)
                   Ok issued.ExpiresAt ])
        let expiredClock =
            { new IDirectSessionWindowClock with
                member _.ReadUtcNow() =
                    match times.TryDequeue() with
                    | true, time -> time
                    | _ -> Error "clock-exhausted" }
        let store = FirstStartChallengeFake() :> IDirectSessionChallengeReservationStore
        let result =
            CodexAppServerFirstStart.bind scope sourceId binding.TransportIdentity
                expiredClock issuer source store (firstSource (Ok first))
        Assert.Equal("app-server-first-start-outside-window", gapCode result)
