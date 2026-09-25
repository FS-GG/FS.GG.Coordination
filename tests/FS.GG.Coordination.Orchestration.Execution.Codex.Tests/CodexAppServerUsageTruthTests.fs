namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open System.IO
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type CodexAppServerUsageTruthTests() =
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
    let challenge = String.replicate 64 "d"
    let request =
        { Challenge = challenge
          Scope = scope
          NativeSessionId = "native-session-current"
          AuthorizedSourceAdapterId = "trusted-current-session-source-v1"
          IssuedAt = DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero)
          ExpiresAt = DateTimeOffset(2026, 9, 25, 12, 2, 0, TimeSpan.Zero) }
    let binding =
        { Scope = scope
          TurnId = "native-turn"
          TransportIdentity = "authenticated-local-app-server"
          ConnectionId = "connection-1"
          SubscriptionDigest = String.replicate 64 "e"
          ProtocolVersion = "codex-app-server-v2/0.156.1" }
    let terminal =
        { Reservation = { Request = request; ReservationId = "reservation-1" }
          Binding = binding
          FirstEntryId = "entry-1"
          SealedHeadEntryId = "entry-3"
          TerminalStatus = "completed"
          UsageUpdateCount = 1 }
    let snapshot =
        let bytes =
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "app-server", "usage-updated.json")
            |> File.ReadAllBytes
        match CodexAppServerUsageProjection.parse scope.ThreadId binding.TurnId bytes with
        | Ok update -> update
        | Error code -> failwithf "fixture refused: %s" code
    let execChild: CodexTurnUsage =
        { ThreadId = scope.ThreadId
          TurnId = Some binding.TurnId
          TurnSequence = 1L
          Provider = Some "openai"
          ObservedModel = Some "gpt-6-sol"
          ObservedEffort = Some "high"
          Backend = None
          Input = 100L
          CachedInput = 20L
          Output = 40L
          Reasoning = Some 10L
          Total = 140L }
    let assess evidence = CodexAppServerUsageTruth.assess terminal evidence
    let noVerdict result =
        match result with
        | Ok value ->
            Assert.Equal("native-completed-turn-usage-not-established", value.Reason)
            value
        | Error code -> failwithf "unexpected refusal: %s" code

    [<Fact>]
    member _.``canonical correlation without native turn usage has explicit no verdict``() =
        let result = noVerdict (assess [])
        Assert.Equal(scope.WorkspaceId, result.Correlation.Scope.WorkspaceId)
        Assert.Equal(scope.ItemId, result.Correlation.Scope.ItemId)
        Assert.Equal(request.NativeSessionId, result.Correlation.NativeSessionId)
        Assert.Equal(binding.TurnId, result.Correlation.NativeTurnId)
        Assert.Equal(challenge, result.Correlation.WindowChallenge)
        Assert.Equal(terminal.Reservation.ReservationId, result.Correlation.ReservationId)
        Assert.Equal(request.AuthorizedSourceAdapterId, result.Correlation.AuthorizedSourceAdapterId)
        Assert.Equal(request.IssuedAt, result.Correlation.IssuedAt)
        Assert.Equal(request.ExpiresAt, result.Correlation.ExpiresAt)
        Assert.Equal(binding.TransportIdentity, result.Correlation.TransportIdentity)
        Assert.Equal(binding.ConnectionId, result.Correlation.ConnectionId)
        Assert.Equal(binding.ProtocolVersion, result.Correlation.ProtocolVersion)
        Assert.Equal(terminal.SealedHeadEntryId, result.Correlation.SealedHeadEntryId)
        Assert.Empty result.ObservedEvidenceClasses

    [<Fact>]
    member _.``last and cumulative snapshots never become completed-turn usage``() =
        let exactLooking =
            { snapshot with Last = snapshot.Cumulative }
        let result = noVerdict (assess [ ThreadSnapshot exactLooking ])
        Assert.Equal([ "thread-last-total-snapshot" ], result.ObservedEvidenceClasses)
        Assert.Equal(binding.TurnId, result.Correlation.NativeTurnId)

    [<Fact>]
    member _.``thread snapshot evidence cannot outnumber sealed usage updates``() =
        let noUpdates = { terminal with UsageUpdateCount = 0 }
        Assert.Equal(
            Error "app-server-usage-snapshot-count-mismatch",
            CodexAppServerUsageTruth.assess noUpdates [ ThreadSnapshot snapshot ]
        )
        let bytes =
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "app-server", "usage-updated.json")
            |> File.ReadAllBytes
        let secondSnapshot =
            match CodexAppServerUsageProjection.parse scope.ThreadId binding.TurnId
                      (Array.append bytes [| byte '\n' |]) with
            | Ok update -> update
            | Error code -> failwithf "copied-live whitespace variant refused: %s" code
        Assert.Equal(
            Error "app-server-usage-snapshot-count-mismatch",
            assess [ ThreadSnapshot snapshot; ThreadSnapshot secondSnapshot ]
        )

    [<Fact>]
    member _.``exec child completed frame with matching IDs remains a different provenance``() =
        let raw =
            """{"type":"turn.completed","thread_id":"native-thread","turn_id":"native-turn","usage":{"input_tokens":100,"cached_input_tokens":20,"output_tokens":40,"reasoning_output_tokens":10}}"""
        let parsed = CodexTurnProjection.project None 1L raw
        match parsed with
        | Some(Ok usage) ->
            let result = noVerdict (assess [ ExecChildCompleted usage ])
            Assert.Equal([ "exec-child-turn-completed" ], result.ObservedEvidenceClasses)
        | other -> failwithf "unexpected authored exec fixture result %A" other
        let direct = noVerdict (assess [ ExecChildCompleted execChild ])
        Assert.Equal([ "exec-child-turn-completed" ], direct.ObservedEvidenceClasses)

    [<Fact>]
    member _.``one upstream response and all nearby evidence still have no turn usage verdict``() =
        let counts = snapshot.Last
        let result =
            noVerdict
                (assess
                    [ ThreadSnapshot snapshot
                      ExecChildCompleted execChild
                      UpstreamResponseCompleted(scope.ThreadId, binding.TurnId, "response-1", counts) ])
        Assert.Equal(
            [ "exec-child-turn-completed"; "one-upstream-response"; "thread-last-total-snapshot" ],
            result.ObservedEvidenceClasses
        )

    [<Fact>]
    member _.``typed snapshot with invalid counters or digest cannot be observed evidence``() =
        let invalidLast = { snapshot with Last = { snapshot.Last with Input = -1L } }
        let regressedTotal =
            { snapshot with
                Cumulative =
                    { snapshot.Last with
                        Input = snapshot.Last.Input - 1L
                        Total = snapshot.Last.Total - 1L } }
        let invalidDigest = { snapshot with WireSha256 = "not-a-wire-digest" }
        for candidate in [ invalidLast; regressedTotal; invalidDigest ] do
            Assert.Equal(
                Error "app-server-usage-candidate-invalid",
                assess [ ThreadSnapshot candidate ]
            )

    [<Fact>]
    member _.``typed exec child with invalid turn counters cannot be observed evidence``() =
        for candidate in
            [ { execChild with Total = execChild.Total + 1L }
              { execChild with TurnSequence = 0L } ] do
            Assert.Equal(
                Error "app-server-usage-candidate-invalid",
                assess [ ExecChildCompleted candidate ]
            )

    [<Fact>]
    member _.``typed upstream response with invalid counters cannot be observed evidence``() =
        let invalidCached = { snapshot.Last with CachedInput = snapshot.Last.Input + 1L }
        let overflow =
            { snapshot.Last with Input = Int64.MaxValue; Output = 1L; Total = Int64.MaxValue }
        for counts in [ invalidCached; overflow ] do
            Assert.Equal(
                Error "app-server-usage-candidate-invalid",
                assess [ UpstreamResponseCompleted(scope.ThreadId, binding.TurnId, "response-1", counts) ]
            )

    [<Fact>]
    member _.``foreign candidate thread or turn refuses canonical correlation``() =
        let wrongSnapshot = { snapshot with TurnId = "other-turn" }
        let wrongExec = { execChild with ThreadId = "other-thread" }
        for evidence in
            [ ThreadSnapshot wrongSnapshot
              ExecChildCompleted wrongExec
              UpstreamResponseCompleted(scope.ThreadId, "other-turn", "response-1", snapshot.Last) ] do
            Assert.Equal(
                Error "app-server-usage-candidate-identity-mismatch",
                assess [ evidence ]
            )

    [<Fact>]
    member _.``foreign or malformed terminal correlation refuses``() =
        let foreign = { terminal with Binding = { binding with Scope = { scope with ItemId = "other-item" } } }
        let badStatus = { terminal with TerminalStatus = "inProgress" }
        let badDigest = { terminal with Binding = { binding with SubscriptionDigest = "not-a-digest" } }
        for candidate in [ foreign; badStatus; badDigest ] do
            Assert.Equal(
                Error "app-server-usage-correlation-invalid",
                CodexAppServerUsageTruth.assess candidate []
            )

    [<Fact>]
    member _.``one entry cannot be both the first start and a terminal seal head``() =
        let impossible = { terminal with SealedHeadEntryId = terminal.FirstEntryId }
        Assert.Equal(
            Error "app-server-usage-correlation-invalid",
            CodexAppServerUsageTruth.assess impossible []
        )

    [<Fact>]
    member _.``usage-update count cannot exceed closed journal entry capacity``() =
        let impossible = { terminal with UsageUpdateCount = 9999 }
        Assert.Equal(
            Error "app-server-usage-correlation-invalid",
            CodexAppServerUsageTruth.assess impossible []
        )

    [<Fact>]
    member _.``null or foreign response candidate refuses``() =
        Assert.Equal(
            Error "app-server-usage-candidates-invalid",
            CodexAppServerUsageTruth.assess terminal
                (Unchecked.defaultof<CodexAppServerUsageCandidate list>)
        )
        Assert.Equal(
            Error "app-server-usage-candidate-identity-mismatch",
            assess [ UpstreamResponseCompleted(scope.ThreadId, binding.TurnId, "", snapshot.Last) ]
        )

    [<Fact>]
    member _.``equal but invalid repository and issue scope cannot be canonical``() =
        let invalid = { scope with Repository = "FS-GG"; IssueRef = "FS-GG#123" }
        let candidate =
            { terminal with
                Reservation = { terminal.Reservation with Request = { request with Scope = invalid } }
                Binding = { binding with Scope = invalid } }
        Assert.Equal(
            Error "app-server-usage-correlation-invalid",
            CodexAppServerUsageTruth.assess candidate []
        )

    [<Fact>]
    member _.``equal but malformed binding digest cannot be canonical``() =
        let invalid = { scope with BindingDigest = "not-a-digest" }
        let candidate =
            { terminal with
                Reservation = { terminal.Reservation with Request = { request with Scope = invalid } }
                Binding = { binding with Scope = invalid } }
        Assert.Equal(
            Error "app-server-usage-correlation-invalid",
            CodexAppServerUsageTruth.assess candidate []
        )

    [<Fact>]
    member _.``equal but blank workspace cannot be canonical``() =
        let invalid = { scope with WorkspaceId = " " }
        let candidate =
            { terminal with
                Reservation = { terminal.Reservation with Request = { request with Scope = invalid } }
                Binding = { binding with Scope = invalid } }
        Assert.Equal(
            Error "app-server-usage-correlation-invalid",
            CodexAppServerUsageTruth.assess candidate []
        )

    [<Fact>]
    member _.``unsupported transport protocol cannot be canonical``() =
        let candidate = { terminal with Binding = { binding with ProtocolVersion = "foreign-v1" } }
        Assert.Equal(
            Error "app-server-usage-correlation-invalid",
            CodexAppServerUsageTruth.assess candidate []
        )

    [<Fact>]
    member _.``blank transport connection cannot be canonical``() =
        let candidate = { terminal with Binding = { binding with ConnectionId = "" } }
        Assert.Equal(
            Error "app-server-usage-correlation-invalid",
            CodexAppServerUsageTruth.assess candidate []
        )

    [<Fact>]
    member _.``unattributed reservation cannot be canonical``() =
        let candidate =
            { terminal with
                Reservation =
                    { Request = { request with AuthorizedSourceAdapterId = "" }
                      ReservationId = "" } }
        Assert.Equal(
            Error "app-server-usage-correlation-invalid",
            CodexAppServerUsageTruth.assess candidate []
        )

    [<Fact>]
    member _.``non UTC reservation window cannot be canonical``() =
        let candidate =
            { terminal with
                Reservation =
                    { terminal.Reservation with
                        Request = { request with IssuedAt = request.IssuedAt.ToOffset(TimeSpan.FromHours 1.) } } }
        Assert.Equal(
            Error "app-server-usage-correlation-invalid",
            CodexAppServerUsageTruth.assess candidate []
        )

    [<Fact>]
    member _.``zero or overlong reservation window cannot be canonical``() =
        for expiresAt in [ request.IssuedAt; request.IssuedAt.AddMinutes 6. ] do
            let candidate =
                { terminal with
                    Reservation =
                        { terminal.Reservation with
                            Request = { request with ExpiresAt = expiresAt } } }
            Assert.Equal(
                Error "app-server-usage-correlation-invalid",
                CodexAppServerUsageTruth.assess candidate []
            )
