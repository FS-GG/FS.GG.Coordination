namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type private AtomicSubscriptionChallengeFake() =
    let reserved = ConcurrentDictionary<string, DirectSessionChallengeReservationRequest>()
    interface IDirectSessionChallengeReservationStore with
        member _.TryReserveOnce request =
            if reserved.TryAdd(request.Challenge, request) then
                Ok(ChallengeReserved { Request = request; ReservationId = "reservation-1" })
            else Ok ChallengeAlreadyReserved

type CodexAppServerCurrentSubscriptionTests() =
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
    let reservationNow = issuedAt.AddMinutes 1.
    let postNow = issuedAt.AddSeconds 90.
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
    let observation =
        { SourceAdapterId = sourceId
          NativeSessionId = assignment.NativeSessionId
          WindowChallenge = challenge
          NativeThreadId = scope.ThreadId
          SubscribedAt = issuedAt.AddSeconds 70.
          ObservedAt = issuedAt.AddSeconds 80.
          Binding = binding }
    let clock () =
        let times = ConcurrentQueue<Result<DateTimeOffset, string>>([ Ok reservationNow; Ok postNow ])
        { new IDirectSessionWindowClock with
            member _.ReadUtcNow() =
                match times.TryDequeue() with
                | true, value -> value
                | _ -> Error "clock-exhausted" }
    let issuer result =
        { new IDirectSessionProspectiveWindowIssuer with
            member _.ReadIssuedWindow() = result }
    let source result =
        { new ICodexAppServerCurrentSubscriptionSource with
            member _.ReadCurrentSubscription() = result }
    let prepare expected issue observed store =
        CodexAppServerCurrentSubscription.prepare expected sourceId binding.TransportIdentity
            (clock ()) (issuer issue) (source observed) store
    let gapCode result =
        match result with
        | Error(ReservedGap(_, code)) -> code
        | other -> failwithf "expected reserved gap, got %A" other

    [<Fact>]
    member _.``exact prospective source returns only reserved structural subscription``() =
        let store = AtomicSubscriptionChallengeFake() :> IDirectSessionChallengeReservationStore
        match prepare scope (Ok issued) (Ok observation) store with
        | Ok selected ->
            Assert.Equal(challenge, selected.Reservation.Request.Challenge)
            Assert.Equal(scope.ItemId, selected.Binding.Scope.ItemId)
            Assert.Equal(binding.ConnectionId, selected.Binding.ConnectionId)
            Assert.Equal(postNow, selected.ValidatedAt)
        | other -> failwithf "unexpected subscription refusal %A" other

    [<Fact>]
    member _.``stale and foreign issues refuse before reservation and source read``() =
        let storeReads = ConcurrentBag<int>()
        let sourceReads = ConcurrentBag<int>()
        let store =
            { new IDirectSessionChallengeReservationStore with
                member _.TryReserveOnce _ = storeReads.Add 1; Error "should-not-read" }
        let native =
            { new ICodexAppServerCurrentSubscriptionSource with
                member _.ReadCurrentSubscription() = sourceReads.Add 1; Ok observation }
        let run expected issue now =
            let singleClock =
                { new IDirectSessionWindowClock with member _.ReadUtcNow() = Ok now }
            CodexAppServerCurrentSubscription.prepare expected sourceId binding.TransportIdentity
                singleClock (issuer (Ok issue)) native store
        Assert.Equal(Error(Rejected "direct-session-window-stale"), run scope issued issued.ExpiresAt)
        let foreign = { issued with Assignment = { assignment with Scope = { scope with ItemId = "other-item" } } }
        Assert.Equal(Error(Rejected "direct-session-window-foreign-assignment"), run scope foreign reservationNow)
        Assert.Empty storeReads
        Assert.Empty sourceReads

    [<Fact>]
    member _.``borrowed session challenge source and thread burn the reservation``() =
        let cases =
            [ { observation with NativeSessionId = "borrowed-session" }, "app-server-current-subscription-borrowed-session"
              { observation with WindowChallenge = String.replicate 64 "f" }, "app-server-current-subscription-challenge-mismatch"
              { observation with SourceAdapterId = "future-child-source" }, "app-server-current-subscription-source-substitution"
              { observation with NativeThreadId = "other-thread" }, "app-server-current-subscription-thread-mismatch" ]
        for candidate, expectedCode in cases do
            let store = AtomicSubscriptionChallengeFake() :> IDirectSessionChallengeReservationStore
            Assert.Equal(expectedCode, gapCode (prepare scope (Ok issued) (Ok candidate) store))
            Assert.Equal(Error AlreadyReserved, prepare scope (Ok issued) (Ok observation) store)

    [<Fact>]
    member _.``pre-reservation subscription and stale or future observation burn challenge``() =
        let cases =
            [ { observation with SubscribedAt = issuedAt.AddSeconds 50. }
              { observation with ObservedAt = issued.ExpiresAt }
              { observation with ObservedAt = postNow.AddSeconds 1. } ]
        for candidate in cases do
            let store = AtomicSubscriptionChallengeFake() :> IDirectSessionChallengeReservationStore
            Assert.Equal(
                "app-server-current-subscription-outside-window",
                gapCode (prepare scope (Ok issued) (Ok candidate) store)
            )
            Assert.Equal(Error AlreadyReserved, prepare scope (Ok issued) (Ok observation) store)
        let expiredClock =
            let times = ConcurrentQueue<Result<DateTimeOffset, string>>([ Ok reservationNow; Ok issued.ExpiresAt ])
            { new IDirectSessionWindowClock with
                member _.ReadUtcNow() =
                    match times.TryDequeue() with
                    | true, time -> time
                    | _ -> Error "clock-exhausted" }
        let expiredStore = AtomicSubscriptionChallengeFake() :> IDirectSessionChallengeReservationStore
        let expired =
            CodexAppServerCurrentSubscription.prepare scope sourceId binding.TransportIdentity
                expiredClock (issuer (Ok issued)) (source (Ok observation)) expiredStore
        Assert.Equal("app-server-current-subscription-outside-window", gapCode expired)
        Assert.Equal(Error AlreadyReserved, prepare scope (Ok issued) (Ok observation) expiredStore)

    [<Fact>]
    member _.``wrong scope transport protocol or digest cannot bind the subscription``() =
        let cases =
            [ { observation with Binding = { binding with Scope = { scope with WorkspaceId = "other-workspace" } } },
              "app-server-subscription-binding-mismatch"
              { observation with Binding = { binding with TransportIdentity = "borrowed-transport" } },
              "app-server-subscription-binding-mismatch"
              { observation with Binding = { binding with ProtocolVersion = "codex-app-server-v1" } },
              "app-server-subscription-binding-invalid"
              { observation with Binding = { binding with SubscriptionDigest = "bad-digest" } },
              "app-server-subscription-binding-invalid" ]
        for candidate, expectedCode in cases do
            let store = AtomicSubscriptionChallengeFake() :> IDirectSessionChallengeReservationStore
            Assert.Equal(expectedCode, gapCode (prepare scope (Ok issued) (Ok candidate) store))

    [<Fact>]
    member _.``uncertain or inconsistent reservation never reads native source``() =
        let sourceReads = ConcurrentBag<int>()
        let native =
            { new ICodexAppServerCurrentSubscriptionSource with
                member _.ReadCurrentSubscription() = sourceReads.Add 1; Ok observation }
        let evaluate store =
            CodexAppServerCurrentSubscription.prepare scope sourceId binding.TransportIdentity
                (clock ()) (issuer (Ok issued)) native store
        let uncertain =
            { new IDirectSessionChallengeReservationStore with
                member _.TryReserveOnce _ = Error "timeout-after-possible-commit" }
        Assert.Equal(
            Error(StoreOutcomeUnknown "app-server-current-subscription-store-unavailable"),
            evaluate uncertain
        )
        let inconsistent =
            { new IDirectSessionChallengeReservationStore with
                member _.TryReserveOnce request =
                    let wrong = { request with Scope = { scope with ItemId = "other-item" } }
                    Ok(ChallengeReserved { Request = wrong; ReservationId = "reservation-1" }) }
        Assert.Equal(
            Error(StoreOutcomeUnknown "app-server-current-subscription-receipt-invalid"),
            evaluate inconsistent
        )
        Assert.Empty sourceReads

    [<Fact>]
    member _.``one atomic challenge winner may read source under concurrent attempts``() =
        let store = AtomicSubscriptionChallengeFake() :> IDirectSessionChallengeReservationStore
        let sourceReads = ConcurrentBag<int>()
        let native =
            { new ICodexAppServerCurrentSubscriptionSource with
                member _.ReadCurrentSubscription() = sourceReads.Add 1; Ok observation }
        let jobs =
            [| for _ in 1 .. 32 ->
                Task.Run(fun () ->
                    let stableClock =
                        let times = ConcurrentQueue<Result<DateTimeOffset, string>>([ Ok reservationNow; Ok postNow ])
                        { new IDirectSessionWindowClock with
                            member _.ReadUtcNow() =
                                match times.TryDequeue() with
                                | true, time -> time
                                | _ -> Error "clock-exhausted" }
                    CodexAppServerCurrentSubscription.prepare scope sourceId binding.TransportIdentity
                        stableClock (issuer (Ok issued)) native store) |]
        let outcomes = Task.WhenAll(jobs).GetAwaiter().GetResult()
        Assert.Equal(1, outcomes |> Array.filter Result.isOk |> Array.length)
        Assert.Equal(31, outcomes |> Array.filter ((=) (Error AlreadyReserved)) |> Array.length)
        Assert.Equal(1, sourceReads.Count)

    [<Fact>]
    member _.``missing current-session source burns a confirmed reservation``() =
        let store = AtomicSubscriptionChallengeFake() :> IDirectSessionChallengeReservationStore
        Assert.Equal(
            "app-server-current-subscription-source-unavailable",
            gapCode (prepare scope (Ok issued) (Error "no-native-current-session-hook") store)
        )
        Assert.Equal(Error AlreadyReserved, prepare scope (Ok issued) (Ok observation) store)
