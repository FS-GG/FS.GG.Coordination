namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type private AtomicChallengeFakeStore() =
    let reserved = ConcurrentDictionary<string, DirectSessionChallengeReservationRequest>()

    interface IDirectSessionChallengeReservationStore with
        member _.TryReserveOnce request =
            if reserved.TryAdd(request.Challenge, request) then
                Ok(ChallengeReserved { Request = request; ReservationId = "fake-reservation-1" })
            else
                Ok ChallengeAlreadyReserved

type DirectSessionChallengeReservationTests() =
    let scope: DirectSessionTurnScope =
        {
            WorkspaceId = "main-fsharp-dev"
            Repository = "FS-GG/.github"
            ItemId = "work-item-v1-" + String.replicate 64 "b"
            IssueRef = "FS-GG/.github#123"
            AttemptId = "attempt-1"
            InvocationId = "invocation-" + String.replicate 64 "a"
            ThreadId = "native-thread"
            SourceIdentity = "coordination"
            ProducerId = "fsharp-dev-main"
            BindingDigest = String.replicate 64 "c"
        }

    let sourceId = "trusted-current-session-source-v1"
    let challenge = String.replicate 64 "d"
    let issuedAt = DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero)
    let now = issuedAt.AddMinutes 1.
    let assignment =
        { Scope = scope; NativeSessionId = "native-session-current"; WindowChallenge = challenge }
    let issued =
        { Assignment = assignment
          AuthorizedSourceAdapterId = sourceId
          IssuedAt = issuedAt
          ExpiresAt = issuedAt.AddMinutes 2. }
    let usage: CodexTurnUsage =
        {
            ThreadId = scope.ThreadId
            TurnId = Some "native-turn-1"
            TurnSequence = 1L
            Provider = Some "openai"
            ObservedModel = Some "gpt-6-sol"
            ObservedEffort = Some "high"
            Backend = None
            Input = 17L
            CachedInput = 4L
            Output = 9L
            Reasoning = Some 3L
            Total = 26L
        }
    let observation =
        {
            SourceAdapterId = sourceId
            ObservedAt = issuedAt.AddSeconds 30.
            CurrentTurn =
                { NativeSessionId = assignment.NativeSessionId
                  WindowChallenge = challenge
                  NativeThreadId = scope.ThreadId
                  CompletedTurn =
                    { SourceBinding = scope
                      Usage = usage
                      CounterProvenance = "native-current-session-fixture" } }
        }

    let issuer result =
        { new IDirectSessionProspectiveWindowIssuer with
            member _.ReadIssuedWindow() = result }

    let source result =
        { new IDirectSessionWindowTurnSource with
            member _.ReadCurrentWindowTurn() = result }

    let clock time =
        { new IDirectSessionWindowClock with
            member _.ReadUtcNow() = Ok time }

    let run expected time issuedResult sourceResult store =
        DirectSessionChallengeReservation.prepareOnce expected sourceId (clock time)
            (issuer issuedResult) (source sourceResult) store

    [<Fact>]
    member _.``one atomic fake-store winner prepares a turn and all racers refuse replay``() =
        let store = AtomicChallengeFakeStore() :> IDirectSessionChallengeReservationStore
        let sourceReads = ConcurrentBag<int>()
        let nativeSource =
            { new IDirectSessionWindowTurnSource with
                member _.ReadCurrentWindowTurn() =
                    sourceReads.Add 1
                    Ok observation }
        let jobs =
            [| for _ in 1 .. 32 ->
                Task.Run(fun () ->
                    DirectSessionChallengeReservation.prepareOnce scope sourceId (clock now)
                        (issuer (Ok issued)) nativeSource store) |]
        let outcomes = Task.WhenAll(jobs).GetAwaiter().GetResult()
        Assert.Equal(1, outcomes |> Array.filter Result.isOk |> Array.length)
        Assert.Equal(31, outcomes |> Array.filter ((=) (Error AlreadyReserved)) |> Array.length)
        Assert.Equal(1, sourceReads.Count)
        match outcomes |> Array.pick (function Ok prepared -> Some prepared | Error _ -> None) with
        | prepared ->
            Assert.Equal(challenge, prepared.Reservation.Request.Challenge)
            Assert.Equal(scope.ItemId, prepared.Reservation.Request.Scope.ItemId)
            Assert.Equal(scope.ItemId, prepared.PreparedTurn.ItemId)

    [<Fact>]
    member _.``same challenge is globally one-use across a different workspace and item``() =
        let store = AtomicChallengeFakeStore() :> IDirectSessionChallengeReservationStore
        Assert.True(run scope now (Ok issued) (Ok observation) store |> Result.isOk)
        let foreignScope = { scope with WorkspaceId = "other-workspace"; ItemId = "other-item" }
        let foreignAssignment = { assignment with Scope = foreignScope }
        let foreignIssue = { issued with Assignment = foreignAssignment }
        Assert.Equal(
            Error AlreadyReserved,
            run foreignScope now (Ok foreignIssue) (Error "must-not-read-source") store
        )

    [<Fact>]
    member _.``stale or foreign issue never reaches reservation or source``() =
        let storeCalls = ConcurrentBag<int>()
        let sourceCalls = ConcurrentBag<int>()
        let store =
            { new IDirectSessionChallengeReservationStore with
                member _.TryReserveOnce _ =
                    storeCalls.Add 1
                    Error "unexpected-store-call" }
        let nativeSource =
            { new IDirectSessionWindowTurnSource with
                member _.ReadCurrentWindowTurn() =
                    sourceCalls.Add 1
                    Ok observation }
        let evaluate expected time candidate =
            DirectSessionChallengeReservation.prepareOnce expected sourceId (clock time)
                (issuer (Ok candidate)) nativeSource store
        Assert.Equal(Error(Rejected "direct-session-window-stale"), evaluate scope issued.ExpiresAt issued)
        let wrong = { issued with Assignment = { assignment with Scope = { scope with ItemId = "other-item" } } }
        Assert.Equal(Error(Rejected "direct-session-window-foreign-assignment"), evaluate scope now wrong)
        Assert.Empty storeCalls
        Assert.Empty sourceCalls

    [<Fact>]
    member _.``unknown or inconsistent store outcome never reads current source``() =
        let sourceCalls = ConcurrentBag<int>()
        let nativeSource =
            { new IDirectSessionWindowTurnSource with
                member _.ReadCurrentWindowTurn() =
                    sourceCalls.Add 1
                    Ok observation }
        let unknown =
            { new IDirectSessionChallengeReservationStore with
                member _.TryReserveOnce _ = Error "timeout-after-possible-commit" }
        let inconsistent =
            { new IDirectSessionChallengeReservationStore with
                member _.TryReserveOnce request =
                    let wrong = { request with Scope = { scope with ItemId = "other-item" } }
                    Ok(ChallengeReserved { Request = wrong; ReservationId = "fake-reservation" }) }
        let evaluate store =
            DirectSessionChallengeReservation.prepareOnce scope sourceId (clock now)
                (issuer (Ok issued)) nativeSource store
        Assert.Equal(
            Error(StoreOutcomeUnknown "direct-session-reservation-store-unavailable"),
            evaluate unknown
        )
        Assert.Equal(
            Error(StoreOutcomeUnknown "direct-session-reservation-receipt-invalid"),
            evaluate inconsistent
        )
        Assert.Empty sourceCalls

    [<Fact>]
    member _.``source gap after successful reservation burns the challenge``() =
        let store = AtomicChallengeFakeStore() :> IDirectSessionChallengeReservationStore
        match run scope now (Ok issued) (Error "unsupported-current-session-hook") store with
        | Error(ReservedGap(receipt, "direct-session-window-source-unavailable")) ->
            Assert.Equal(challenge, receipt.Request.Challenge)
        | other -> failwithf "wanted reserved gap, received %A" other
        Assert.Equal(
            Error AlreadyReserved,
            run scope now (Ok issued) (Ok observation) store
        )

    [<Fact>]
    member _.``substituted source after reservation is a retained gap``() =
        let store = AtomicChallengeFakeStore() :> IDirectSessionChallengeReservationStore
        let foreign = { observation with SourceAdapterId = "future-child-observer" }
        match run scope now (Ok issued) (Ok foreign) store with
        | Error(ReservedGap(receipt, "direct-session-window-source-substitution")) ->
            Assert.Equal(scope.WorkspaceId, receipt.Request.Scope.WorkspaceId)
        | other -> failwithf "wanted source-substitution gap, received %A" other

    [<Fact>]
    member _.``post-observation clock read permits a later prospective turn``() =
        let reservationNow = issuedAt.AddSeconds 10.
        let observationNow = issuedAt.AddSeconds 40.
        let laterObservation = { observation with ObservedAt = issuedAt.AddSeconds 30. }
        let times = ConcurrentQueue<Result<DateTimeOffset, string>>([ Ok reservationNow; Ok observationNow ])
        let order = ConcurrentQueue<string>()
        let trustedClock =
            { new IDirectSessionWindowClock with
                member _.ReadUtcNow() =
                    order.Enqueue "clock"
                    match times.TryDequeue() with
                    | true, value -> value
                    | _ -> Error "clock-exhausted" }
        let nativeSource =
            { new IDirectSessionWindowTurnSource with
                member _.ReadCurrentWindowTurn() =
                    order.Enqueue "source"
                    Ok laterObservation }
        let store = AtomicChallengeFakeStore() :> IDirectSessionChallengeReservationStore
        let result =
            DirectSessionChallengeReservation.prepareOnce scope sourceId trustedClock
                (issuer (Ok issued)) nativeSource store
        Assert.True(Result.isOk result)
        Assert.Equal<string>([| "clock"; "source"; "clock" |], order.ToArray())

    [<Fact>]
    member _.``clock loss after reservation retains a burned challenge``() =
        let store = AtomicChallengeFakeStore() :> IDirectSessionChallengeReservationStore
        let times = ConcurrentQueue<Result<DateTimeOffset, string>>([ Ok now; Error "clock-lost" ])
        let trustedClock =
            { new IDirectSessionWindowClock with
                member _.ReadUtcNow() =
                    match times.TryDequeue() with
                    | true, value -> value
                    | _ -> Error "clock-exhausted" }
        match DirectSessionChallengeReservation.prepareOnce scope sourceId trustedClock
                  (issuer (Ok issued)) (source (Ok observation)) store with
        | Error(ReservedGap(receipt, "direct-session-window-clock-unavailable")) ->
            Assert.Equal(challenge, receipt.Request.Challenge)
        | other -> failwithf "wanted retained clock gap, received %A" other
        Assert.Equal(Error AlreadyReserved, run scope now (Ok issued) (Ok observation) store)

    [<Fact>]
    member _.``window expiry during observation retains a burned challenge``() =
        let store = AtomicChallengeFakeStore() :> IDirectSessionChallengeReservationStore
        let times = ConcurrentQueue<Result<DateTimeOffset, string>>([ Ok now; Ok issued.ExpiresAt ])
        let trustedClock =
            { new IDirectSessionWindowClock with
                member _.ReadUtcNow() =
                    match times.TryDequeue() with
                    | true, value -> value
                    | _ -> Error "clock-exhausted" }
        match DirectSessionChallengeReservation.prepareOnce scope sourceId trustedClock
                  (issuer (Ok issued)) (source (Ok observation)) store with
        | Error(ReservedGap(receipt, "direct-session-window-stale")) ->
            Assert.Equal(challenge, receipt.Request.Challenge)
        | other -> failwithf "wanted stale reserved gap, received %A" other
        Assert.Equal(Error AlreadyReserved, run scope now (Ok issued) (Ok observation) store)
