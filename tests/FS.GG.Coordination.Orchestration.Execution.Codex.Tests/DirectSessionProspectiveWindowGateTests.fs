namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type DirectSessionProspectiveWindowGateTests() =
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

    let challenge = String.replicate 64 "d"
    let sourceId = "trusted-current-session-source-v1"
    let issuedAt = DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero)
    let now = issuedAt.AddMinutes 1.
    let assignment =
        { Scope = scope
          NativeSessionId = "native-session-current"
          WindowChallenge = challenge }
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
                {
                    NativeSessionId = assignment.NativeSessionId
                    WindowChallenge = challenge
                    NativeThreadId = scope.ThreadId
                    CompletedTurn =
                        { SourceBinding = scope
                          Usage = usage
                          CounterProvenance = "native-current-session-fixture" }
                }
        }

    let issuer result =
        { new IDirectSessionProspectiveWindowIssuer with
            member _.ReadIssuedWindow() = result }

    let source result =
        { new IDirectSessionWindowTurnSource with
            member _.ReadCurrentWindowTurn() = result }

    let evaluate ledger issuance observed =
        DirectSessionProspectiveWindowGate.evaluate scope sourceId now ledger
            (issuer issuance) (source observed)

    let refused (expected: string) result =
        match result with
        | Error actual -> Assert.Equal(expected, actual)
        | Ok _ -> failwithf "wanted refusal %s" expected

    [<Fact>]
    member _.``fresh exact prospective window prepares one fact and consumes challenge``() =
        match evaluate DirectSessionWindowLedger.empty (Ok issued) (Ok observation) with
        | Error code -> failwithf "unexpected refusal %s" code
        | Ok (next, prepared) ->
            Assert.Equal(scope.WorkspaceId, prepared.WorkspaceId)
            Assert.Equal(scope.ItemId, prepared.ItemId)
            Assert.Equal(usage.TurnId.Value, prepared.TurnId)
            refused "direct-session-window-replayed"
                (evaluate next (Ok issued) (Ok observation))

    [<Fact>]
    member _.``stale and premature windows refuse before current-source read``() =
        for time in [ issuedAt.AddSeconds -1.; issued.ExpiresAt ] do
            let mutable sourceRead = false
            let reader =
                { new IDirectSessionWindowTurnSource with
                    member _.ReadCurrentWindowTurn() =
                        sourceRead <- true
                        Ok observation }
            refused "direct-session-window-stale"
                (DirectSessionProspectiveWindowGate.evaluate scope sourceId time
                    DirectSessionWindowLedger.empty (issuer (Ok issued)) reader)
            Assert.False(sourceRead)

    [<Fact>]
    member _.``foreign challenge and borrowed native session refuse``() =
        let otherChallenge = String.replicate 64 "e"
        let foreignTurn =
            { observation.CurrentTurn with WindowChallenge = otherChallenge }
        refused "direct-session-window-mismatch"
            (evaluate DirectSessionWindowLedger.empty (Ok issued)
                (Ok { observation with CurrentTurn = foreignTurn }))
        let borrowedTurn =
            { observation.CurrentTurn with NativeSessionId = "borrowed-session" }
        refused "direct-session-borrowed-session"
            (evaluate DirectSessionWindowLedger.empty (Ok issued)
                (Ok { observation with CurrentTurn = borrowedTurn }))

    [<Fact>]
    member _.``wrong workspace item or selected source refuses``() =
        for wrongScope in
            [ { scope with WorkspaceId = "other-workspace" }
              { scope with ItemId = "other-item" } ] do
            let foreign = { issued with Assignment = { assignment with Scope = wrongScope } }
            refused "direct-session-window-foreign-assignment"
                (evaluate DirectSessionWindowLedger.empty (Ok foreign) (Ok observation))
        refused "direct-session-window-foreign-source"
            (evaluate DirectSessionWindowLedger.empty
                (Ok { issued with AuthorizedSourceAdapterId = "future-child-observer" })
                (Ok observation))
        refused "direct-session-window-source-substitution"
            (evaluate DirectSessionWindowLedger.empty (Ok issued)
                (Ok { observation with SourceAdapterId = "future-child-observer" }))

    [<Fact>]
    member _.``equal malformed scopes refuse before current session source read``() =
        for malformed in
            [ { scope with Repository = "FS-GG"; IssueRef = "FS-GG#123" }
              { scope with IssueRef = "FS-GG/other#123" }
              { scope with BindingDigest = "not-a-digest" } ] do
            let mutable sourceReads = 0
            let native =
                { new IDirectSessionWindowTurnSource with
                    member _.ReadCurrentWindowTurn() =
                        sourceReads <- sourceReads + 1
                        Ok observation }
            let malformedIssue =
                { issued with Assignment = { assignment with Scope = malformed } }
            refused "direct-session-window-input-invalid"
                (DirectSessionProspectiveWindowGate.evaluate malformed sourceId now
                    DirectSessionWindowLedger.empty (issuer (Ok malformedIssue)) native)
            Assert.Equal(0, sourceReads)

    [<Fact>]
    member _.``missing source and nonprospective evidence cannot prepare a fact``() =
        refused "direct-session-window-source-unavailable"
            (evaluate DirectSessionWindowLedger.empty (Ok issued)
                (Error "no-current-session-hook"))
        refused "direct-session-window-issuer-unavailable"
            (evaluate DirectSessionWindowLedger.empty (Error "no-protected-issuer")
                (Ok observation))
        refused "direct-session-window-observation-outside"
            (evaluate DirectSessionWindowLedger.empty (Ok issued)
                (Ok { observation with ObservedAt = issuedAt.AddSeconds -1. }))
        refused "direct-session-window-issue-invalid"
            (evaluate DirectSessionWindowLedger.empty
                (Ok { issued with ExpiresAt = issuedAt.AddMinutes 6. })
                (Ok observation))

    [<Fact>]
    member _.``observation tied with challenge issuance cannot prove prospective order``() =
        let tied = { observation with ObservedAt = issuedAt }
        refused "direct-session-window-observation-outside"
            (evaluate DirectSessionWindowLedger.empty (Ok issued) (Ok tied))

    [<Fact>]
    member _.``failed correlation leaves challenge available for one valid attempt``() =
        let mismatchedTurn =
            { observation.CurrentTurn with WindowChallenge = String.replicate 64 "e" }
        let wrong =
            { observation with CurrentTurn = mismatchedTurn }
        refused "direct-session-window-mismatch"
            (evaluate DirectSessionWindowLedger.empty (Ok issued) (Ok wrong))
        match evaluate DirectSessionWindowLedger.empty (Ok issued) (Ok observation) with
        | Ok _ -> ()
        | Error code -> failwithf "unexpected refusal after failed mapping %s" code
