namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type private CorrelatedChallengeFake() =
    let reserved = ConcurrentDictionary<string, DirectSessionChallengeReservationRequest>()
    interface IDirectSessionChallengeReservationStore with
        member _.TryReserveOnce request =
            if reserved.TryAdd(request.Challenge, request) then
                Ok(ChallengeReserved { Request = request; ReservationId = "reservation-1" })
            else Ok ChallengeAlreadyReserved

type CodexAppServerCorrelatedTerminalTests() =
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
    let frameEvent ordinal (bytes: byte array) =
        let digest = SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
        ObservedFrame(ordinal, binding.TransportIdentity, binding.ConnectionId,
                      Convert.ToBase64String bytes, digest)
    let receipt ordinal prior event =
        { Append = { Binding = binding; PreviousEntryId = prior; Event = event }
          EntryId = sprintf "entry-%d" ordinal }
    let first = receipt 1L None (frameEvent 1L (fixture "turn-started.json"))
    let second = receipt 2L (Some first.EntryId) (frameEvent 2L (fixture "usage-updated.json"))
    let third = receipt 3L (Some second.EntryId) (frameEvent 3L (fixture "turn-completed.json"))
    let snapshot entries =
        { Seal =
            { Binding = binding
              EntryCount = List.length entries
              HeadEntryId = (List.last entries).EntryId
              SealId = "sealed-store-head-1" }
          Entries = entries }
    let firstObservation =
        { NativeSessionId = assignment.NativeSessionId
          WindowChallenge = challenge
          ObservedAt = issuedAt.AddSeconds 95.
          Receipt = first }
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
    let currentSource =
        { new ICodexAppServerCurrentSubscriptionSource with
            member _.ReadCurrentSubscription() = Ok subscription }
    let firstSource =
        { new ICodexAppServerFirstStartSource with
            member _.ReadFirstStartReceipt _ = Ok firstObservation }
    let sealedSource result =
        { new ICodexAppServerJournalRecoverySource with
            member _.ReadSealedSnapshot _ = result }
    let correlate candidate store =
        CodexAppServerCorrelatedTerminal.correlate scope sourceId binding.TransportIdentity
            (clock ()) issuer currentSource store firstSource (sealedSource candidate)
    let gapCode result =
        match result with
        | Error(ReservedGap(_, code)) -> code
        | other -> failwithf "expected retained gap, got %A" other

    [<Fact>]
    member _.``exact sealed chain correlates item and turn without token usage``() =
        let store = CorrelatedChallengeFake() :> IDirectSessionChallengeReservationStore
        match correlate (Ok(snapshot [ first; second; third ])) store with
        | Ok result ->
            Assert.Equal(scope.ItemId, result.Binding.Scope.ItemId)
            Assert.Equal(binding.TurnId, result.Binding.TurnId)
            Assert.Equal(first.EntryId, result.FirstEntryId)
            Assert.Equal(third.EntryId, result.SealedHeadEntryId)
            Assert.Equal("completed", result.TerminalStatus)
            Assert.Equal(1, result.UsageUpdateCount)
            let usageDigest =
                SHA256.HashData(fixture "usage-updated.json")
                |> Convert.ToHexString
                |> fun value -> value.ToLowerInvariant()
            Assert.Equal([ usageDigest ], result.UsageWireSha256s)
        | other -> failwithf "unexpected correlation refusal %A" other

    [<Fact>]
    member _.``different first receipt cannot borrow prospective start``() =
        let fakeFirst = { first with EntryId = "borrowed-entry-1" }
        let store = CorrelatedChallengeFake() :> IDirectSessionChallengeReservationStore
        Assert.Equal(
            "app-server-correlated-first-receipt-mismatch",
            gapCode (correlate (Ok(snapshot [ fakeFirst; second; third ])) store)
        )
        Assert.Equal(Error AlreadyReserved, correlate (Ok(snapshot [ first; second; third ])) store)

    [<Fact>]
    member _.``omitted tail and missing terminal cannot produce correlated terminal``() =
        let complete = snapshot [ first; second; third ]
        let truncated = { complete with Entries = [ first; second ] }
        let store1 = CorrelatedChallengeFake() :> IDirectSessionChallengeReservationStore
        Assert.Equal("app-server-recovery-count-mismatch", gapCode (correlate (Ok truncated) store1))
        let store2 = CorrelatedChallengeFake() :> IDirectSessionChallengeReservationStore
        Assert.Equal(
            "app-server-recovery-terminal-missing",
            gapCode (correlate (Ok(snapshot [ first; second ])) store2)
        )

    [<Fact>]
    member _.``foreign seal and broken predecessor refuse``() =
        let complete = snapshot [ first; second; third ]
        let foreign =
            { complete with Seal = { complete.Seal with Binding = { binding with Scope = { scope with ItemId = "other-item" } } } }
        let store1 = CorrelatedChallengeFake() :> IDirectSessionChallengeReservationStore
        Assert.Equal("app-server-recovery-seal-invalid", gapCode (correlate (Ok foreign) store1))
        let broken = { second with Append = { second.Append with PreviousEntryId = None } }
        let store2 = CorrelatedChallengeFake() :> IDirectSessionChallengeReservationStore
        Assert.Equal(
            "app-server-recovery-chain-invalid",
            gapCode (correlate (Ok(snapshot [ first; broken; third ])) store2)
        )

    [<Fact>]
    member _.``malformed usage and disconnect remain gaps``() =
        let malformed = receipt 2L (Some first.EntryId) (frameEvent 2L (System.Text.Encoding.UTF8.GetBytes "{bad"))
        let following = receipt 3L (Some malformed.EntryId) (frameEvent 3L (fixture "turn-completed.json"))
        let store1 = CorrelatedChallengeFake() :> IDirectSessionChallengeReservationStore
        Assert.Equal(
            "app-server-continuity-json-invalid",
            gapCode (correlate (Ok(snapshot [ first; malformed ])) store1)
        )
        let storeAfterGap = CorrelatedChallengeFake() :> IDirectSessionChallengeReservationStore
        Assert.Equal(
            "app-server-recovery-after-gap",
            gapCode (correlate (Ok(snapshot [ first; malformed; following ])) storeAfterGap)
        )
        let disconnected = receipt 2L (Some first.EntryId) (ObservedDisconnect 2L)
        let store2 = CorrelatedChallengeFake() :> IDirectSessionChallengeReservationStore
        Assert.Equal(
            "app-server-continuity-disconnected",
            gapCode (correlate (Ok(snapshot [ first; disconnected ])) store2)
        )

    [<Fact>]
    member _.``unavailable or throwing sealed source burns one-use reservation``() =
        let store = CorrelatedChallengeFake() :> IDirectSessionChallengeReservationStore
        Assert.Equal(
            "app-server-correlated-sealed-source-unavailable",
            gapCode (correlate (Error "store-unavailable") store)
        )
        Assert.Equal(Error AlreadyReserved, correlate (Ok(snapshot [ first; second; third ])) store)
        let throwing =
            { new ICodexAppServerJournalRecoverySource with
                member _.ReadSealedSnapshot _ = failwith "store-threw" }
        let store2 = CorrelatedChallengeFake() :> IDirectSessionChallengeReservationStore
        let result =
            CodexAppServerCorrelatedTerminal.correlate scope sourceId binding.TransportIdentity
                (clock ()) issuer currentSource store2 firstSource throwing
        Assert.Equal("app-server-correlated-sealed-source-unavailable", gapCode result)
