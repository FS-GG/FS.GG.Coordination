namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type DirectSessionCorrelationHandoffTests() =
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

    let challenge = String.replicate 64 "a"
    let assigned = { Scope = scope; NativeSessionId = "session-current"; WindowChallenge = challenge }
    let current =
        {
            NativeSessionId = assigned.NativeSessionId
            WindowChallenge = challenge
            NativeThreadId = scope.ThreadId
            CompletedTurn =
                { SourceBinding = scope
                  Usage = usage
                  CounterProvenance = "native-current-session-fixture" }
        }

    let auth result =
        { new IDirectSessionAssignmentAuthenticator with
            member _.ReadAuthorizedAssignment() = result }

    let source result =
        { new IDirectSessionCurrentTurnSource with
            member _.ReadCurrentTurn() = result }

    let run expected assignment turn =
        DirectSessionCorrelationHandoff.prepare expected (auth assignment) (source turn)

    let refused (expectedCode: string) result =
        match result with
        | Error actual -> Assert.Equal(expectedCode, actual)
        | Ok _ -> failwithf "wanted refusal %s" expectedCode

    [<Fact>]
    member _.``exact independent assignment and native session prepare a scoped turn``() =
        match run scope (Ok assigned) (Ok current) with
        | Error code -> failwithf "unexpected refusal %s" code
        | Ok prepared ->
            Assert.Equal(scope.WorkspaceId, prepared.WorkspaceId)
            Assert.Equal(scope.Repository, prepared.Repository)
            Assert.Equal(scope.ItemId, prepared.ItemId)
            Assert.Equal(scope.AttemptId, prepared.AttemptId)
            Assert.Equal(scope.ThreadId, prepared.ThreadId)
            Assert.Equal(usage.TurnId.Value, prepared.TurnId)

    [<Fact>]
    member _.``borrowed native session id refuses even when thread and turn match``() =
        refused "direct-session-borrowed-session"
            (run scope (Ok assigned) (Ok { current with NativeSessionId = "other-session" }))

    [<Fact>]
    member _.``wrong workspace item and attempt from assignment refuse before source read``() =
        for wrong in
            [ { scope with WorkspaceId = "other-workspace" }
              { scope with ItemId = "other-item" }
              { scope with AttemptId = "other-attempt" } ] do
            let mutable sourceRead = false
            let observer =
                { new IDirectSessionCurrentTurnSource with
                    member _.ReadCurrentTurn() =
                        sourceRead <- true
                        Ok current }
            refused "direct-session-assignment-scope-mismatch"
                (DirectSessionCorrelationHandoff.prepare scope
                    (auth (Ok { assigned with Scope = wrong })) observer)
            Assert.False(sourceRead)

    [<Fact>]
    member _.``window challenge and native thread cannot be borrowed``() =
        refused "direct-session-window-mismatch"
            (run scope (Ok assigned)
                (Ok { current with WindowChallenge = String.replicate 64 "b" }))
        refused "direct-session-current-thread-mismatch"
            (run scope (Ok assigned)
                (Ok { current with NativeThreadId = "other-thread" }))
        let wrongUsage = { usage with ThreadId = "other-thread" }
        let wrongCompleted = { current.CompletedTurn with Usage = wrongUsage }
        refused "direct-session-turn-thread-mismatch"
            (run scope (Ok assigned)
                (Ok { current with CompletedTurn = wrongCompleted }))

    [<Fact>]
    member _.``unavailable and malformed boundaries refuse without a mapped fact``() =
        refused "direct-session-assignment-unavailable"
            (run scope (Error "private-auth-failure") (Ok current))
        refused "direct-session-current-source-unavailable"
            (run scope (Ok assigned) (Error "unsupported-current-thread-hook"))
        refused "direct-session-assignment-session-invalid"
            (run scope (Ok { assigned with WindowChallenge = "caller-controlled" }) (Ok current))
        refused "direct-session-source-session-invalid"
            (run scope (Ok assigned) (Ok { current with NativeSessionId = " " }))
        let missingSourceBinding =
            { current.CompletedTurn with SourceBinding = Unchecked.defaultof<DirectSessionTurnScope> }
        refused "direct-session-source-session-invalid"
            (run scope (Ok assigned) (Ok { current with CompletedTurn = missingSourceBinding }))
        refused "direct-session-adapter-missing"
            (DirectSessionCorrelationHandoff.prepare scope null (source (Ok current)))
        let brokenSource =
            { new IDirectSessionCurrentTurnSource with
                member _.ReadCurrentTurn() = failwith "untrusted adapter failure" }
        refused "direct-session-current-source-unavailable"
            (DirectSessionCorrelationHandoff.prepare scope (auth (Ok assigned)) brokenSource)

    [<Fact>]
    member _.``source binding must match the authenticated assignment``() =
        let wrongBinding = { scope with ItemId = "borrowed-item" }
        let wrongCompleted = { current.CompletedTurn with SourceBinding = wrongBinding }
        refused "direct-session-scope-mismatch"
            (run scope (Ok assigned)
                (Ok { current with CompletedTurn = wrongCompleted }))
