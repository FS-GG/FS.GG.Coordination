namespace FS.GG.Execution.Contracts.Tests
open System
open System.Threading.Tasks
open Xunit
open FS.GG.Execution.Contracts

module Samples =
    let domain = { Id = Guid.Parse "164cae00-e9aa-4699-9a20-6bb155e750b7"; Frequency = 1000L }
    let now ticks = { Domain = domain; Ticks = ticks }
    let ok = function Ok value -> value | Error error -> failwithf "Unexpected refusal: %A" error
    let diagnostic code = { Code = code; Message = "Finite diagnostic."; Evidence = None }
    let evidence = { Kind = "fixture"; Reference = "fixture:contract-control"; Digest = None }
    let capture =
        { TotalBytes = 64L; TerminalReserveBytes = 8; StdoutPrefixBytes = 4; StdoutTailBytes = 4
          StderrPrefixBytes = 4; StderrTailBytes = 4; ReadChunkBytes = 32; MaximumFrameBytes = 32; MaximumIncompleteLineBytes = 16 }
    let resources = { MemoryBytes = None; CpuQuota = None; CpuAffinity = None; MaxKernelTasks = None; MaxOwnedProcesses = None; MaxClrProcesses = None }
    let command =
        { Executable = "/fixture/command"; Arguments = [ "literal argument" ]; WorkingDirectory = "/fixture"
          Environment = Replace(Map.ofList [ "FIXTURE", "opaque" ]); Input = Closed
          Capture = capture; Capabilities = []; Resources = resources }
    let identity = { InvocationId = Guid.Parse "c8fc952a-486f-48c5-9d7b-c1cecb22f954"; Execution = None; LaunchOrdinal = 0UL
                     SourceDigest = String.replicate 64 "a"; ProfileId = "ordinary-fixture"; ProfileDigest = String.replicate 64 "b" }
    let budget = Budget.create (now 100L) (TimeSpan.FromSeconds 10.) (TimeSpan.FromSeconds 2.) |> ok
    let generation = { Platform = "fixture"; ScopeId = identity.InvocationId.ToString("D"); Pid = 42; BirthToken = "birth-1" }
    let stamp = { At = Some(now 100L); Scope = identity.InvocationId.ToString("D"); Generation = Some generation; Provenance = evidence }
    let known value = Known(value,stamp)
    let beforeStamp = { stamp with Generation = None }
    let knownBefore value = Known(value,beforeStamp)
    let prepared () = Lifecycle.prepare command identity budget |> ok
    let apply event state = Lifecycle.apply (now 100L) event state |> ok
    let launched () = prepared() |> apply BeginLaunch |> apply TargetReleaseRequested |> apply (LaunchObserved(Started generation))
    let memory bytes = ReadOnlyMemory<byte>(bytes)

module ContractTests =
    open Samples

    [<Fact>]
    let ``budget reserves cleanup and never renews phases`` () =
        let work = Budget.clip (now 8100L) Work (TimeSpan.FromDays 1.) budget
        Assert.Equal(Error BudgetExpired, work)
        let cleanup = Budget.clip (now 9100L) Cleanup (TimeSpan.FromDays 1.) budget |> ok
        Assert.Equal(TimeSpan.FromSeconds 1., cleanup)
        Assert.Equal(10100L, (Budget.describe budget).TotalExpiresAt)

    [<Fact>]
    let ``domain mismatch backwards time and overflowing expiry refuse`` () =
        Assert.Equal(Error ClockDomainMismatch, Budget.remaining { (now 100L) with Domain = { domain with Id = Guid.NewGuid() } } budget)
        Assert.Equal(Error ClockMovedBackwards, Budget.remaining (now 99L) budget)
        Assert.Equal(Error InvalidBudget, Budget.create (now Int64.MaxValue) (TimeSpan.FromSeconds 1.) TimeSpan.Zero)
        Assert.Equal(Error InvalidBudget, Budget.create (now 1L) TimeSpan.Zero TimeSpan.Zero)
        Assert.Equal(Error InvalidBudget, Budget.create (now 1L) (TimeSpan.FromSeconds 1.) (TimeSpan.FromSeconds 1.))

    [<Fact>]
    let ``integer duration rounding cannot extend expiry`` () =
        let b = Budget.create { Domain = { domain with Frequency = 3L }; Ticks = 0L } (TimeSpan.FromMilliseconds 999.) TimeSpan.Zero |> ok
        Assert.Equal(2L, (Budget.describe b).TotalExpiresAt)

    [<Fact>]
    let ``literal command validation refuses shell resolution NUL and oversized input`` () =
        Assert.NotEmpty(Validation.command { command with Executable = "sh" })
        Assert.NotEmpty(Validation.command { command with Arguments = [ "x\000y" ] })
        Assert.NotEmpty(Validation.command { command with Input = Bytes(memory (Array.zeroCreate (Validation.limits.RequestBytes + 1))) })
        Assert.NotEmpty(Validation.command { command with Environment = Allowlist([ "PATH"; "path" ], Map.empty) })
        Assert.Empty(Validation.command { command with Arguments = [ "$(literal)"; "a b"; "" ] })

    [<Fact>]
    let ``capture rejects unchecked storage before allocation`` () =
        Assert.True(BoundedOutput.create { capture with StdoutPrefixBytes = Int32.MaxValue } |> Result.isError)
        Assert.True(BoundedOutput.create { capture with TerminalReserveBytes = 64 } |> Result.isError)

    [<Fact>]
    let ``prefix and ring tail do not overlap for short or long binary input`` () =
        let sink = BoundedOutput.create capture |> ok
        sink.Append(Stdout,memory [| 0uy; 255uy; 10uy |]) |> ignore
        let short = sink.Snapshot().Stdout
        Assert.Equal(3L, short.Totals.Retained)
        Assert.Empty(short.Tail.ToArray())
        sink.Append(Stdout,memory [| 1uy; 2uy; 3uy; 4uy; 5uy; 6uy; 7uy; 8uy |]) |> ignore
        let long = sink.Snapshot().Stdout
        Assert.Equal<byte>([| 0uy; 255uy; 10uy; 1uy |], long.Prefix.ToArray())
        Assert.Equal<byte>([| 5uy; 6uy; 7uy; 8uy |], long.Tail.ToArray())
        Assert.Equal(11L, long.Totals.Observed)
        Assert.Equal(3L, long.Totals.Omitted)

    [<Fact>]
    let ``shared stdout stderr ledger refuses exact ordinary overflow without retention`` () =
        let sink = BoundedOutput.create capture |> ok
        Assert.Equal(Accepted, sink.Append(Stdout,memory (Array.zeroCreate 28)))
        Assert.Equal(Accepted, sink.Append(Stderr,memory (Array.zeroCreate 28)))
        let before = sink.Snapshot()
        match sink.Append(Stdout,memory [| 1uy |]) with RetireRequired _ -> () | _ -> failwith "Overflow accepted"
        let after = sink.Snapshot()
        Assert.Equal(57L, after.ChargedTotal)
        Assert.Equal(before.Stdout.Totals.Retained, after.Stdout.Totals.Retained)
        Assert.Equal(29L, after.Stdout.Totals.Charged)

    [<Fact>]
    let ``terminal reserve is carved from total and protocol cannot spend it`` () =
        let sink = BoundedOutput.create capture |> ok
        sink.Append(SupervisorEvidence,memory (Array.zeroCreate 28)) |> ignore
        sink.Append(Protocol,memory (Array.zeroCreate 28)) |> ignore
        Assert.Equal(Accepted, sink.Append(TerminalEvidence,memory (Array.zeroCreate 8)))
        match sink.Append(TerminalEvidence,memory [| 1uy |]) with RetireRequired _ -> () | _ -> failwith "Total cap ignored"
        Assert.Equal(65L, sink.Snapshot().EvidenceBytes)

    [<Fact>]
    let ``concurrent channels charge once before any capture mutation`` () =
        let sink = BoundedOutput.create { capture with TotalBytes = 10000L } |> ok
        Parallel.For(0, 100, fun i -> sink.Append((if i % 2 = 0 then Stdout else Stderr),memory [| byte i |]) |> ignore) |> ignore
        let s = sink.Snapshot()
        Assert.Equal(100L,s.ChargedTotal)
        Assert.Equal(50L,s.Stdout.Totals.Charged)
        Assert.Equal(50L,s.Stderr.Totals.Charged)

    [<Fact>]
    let ``oversized read chunk still charges observed bytes and does not retain`` () =
        let sink = BoundedOutput.create capture |> ok
        Assert.True(match sink.Append(Stdout,memory (Array.zeroCreate 33)) with RetireRequired _ -> true | _ -> false)
        let s = sink.Snapshot()
        Assert.Equal(33L,s.ChargedTotal)
        Assert.Equal(0L,s.Stdout.Totals.Retained)
        Assert.Equal(33L,s.Stdout.Totals.Omitted)

    [<Fact>]
    let ``snapshot bytes are copies and raw split UTF8 is not recoded`` () =
        let sink = BoundedOutput.create capture |> ok
        sink.Append(Stdout,memory [| 0xe2uy; 0x82uy |]) |> ignore
        sink.Append(Stdout,memory [| 0xacuy |]) |> ignore
        Assert.Equal<byte>([| 0xe2uy; 0x82uy; 0xacuy |],sink.Snapshot().Stdout.Prefix.ToArray())

    [<Fact>]
    let ``start consumed once and ambiguous launch is not retried`` () =
        let state = prepared() |> apply BeginLaunch |> apply (LaunchObserved(Ambiguous(diagnostic "launch-unknown")))
        Assert.True(Lifecycle.apply (now 100L) BeginLaunch state |> Result.isError)
        Assert.Equal(Retiring,Lifecycle.snapshot state |> _.Phase)

    [<Fact>]
    let ``cancel before launch refuses subsequent launch without claiming termination`` () =
        let state = prepared() |> apply (StopRequested CallerCancellation)
        Assert.True(Lifecycle.apply (now 100L) BeginLaunch state |> Result.isError)
        match (Lifecycle.snapshot state).Exit with Pending _ -> () | _ -> failwith "Synthetic exit"

    [<Fact>]
    let ``exit zero does not supply EOF or cleanup proof`` () =
        let state = launched() |> apply (ExitObserved(known(Exited 0)))
        let result = Lifecycle.snapshot state
        Assert.Equal(Retiring,result.Phase)
        Assert.Equal(CleanupUnknown,result.Cleanup.Disposition)
        match result.Stdout.Reader with Pending _ -> () | _ -> failwith "EOF fabricated"

    [<Fact>]
    let ``nonzero exit is exit rather than launch failure`` () =
        let result = launched() |> apply (ExitObserved(known(Exited 7))) |> Lifecycle.snapshot
        Assert.Equal(None,result.FirstFailure)
        Assert.Equal(known(Exited 7),result.Exit)

    [<Fact>]
    let ``required capabilities and retained owner gate target release`` () =
        let command = { command with Capabilities = [ { Capability = BoundedReturnWithRetainedCustody; Required = true } ] }
        let state = Lifecycle.prepare command identity budget |> ok |> apply BeginLaunch
        Assert.True(Lifecycle.apply (now 100L) TargetReleaseRequested state |> Result.isError)
        let state = state |> apply (CapabilitiesObtained [ { Capability = BoundedReturnWithRetainedCustody; Obtained = knownBefore evidence } ])
        Assert.True(Lifecycle.apply (now 100L) TargetReleaseRequested state |> Result.isError)
        let cleanup = { (Lifecycle.snapshot state).Cleanup with Custody = RetainedByOwner { OwnerId = "fixture-owner"; Evidence = evidence } }
        let state = state |> apply (CleanupObserved cleanup) |> apply TargetReleaseRequested
        Assert.Equal(Starting,(Lifecycle.snapshot state).Phase)

    [<Fact>]
    let ``known exit and EOF resist unknown regression`` () =
        let state = launched() |> apply (ExitObserved(known(Exited 0))) |> apply (ExitObserved(Unknown(diagnostic "read-race"))) |> apply (ReaderObserved(Stdout,known Eof))
        Assert.Equal(known(Exited 0),(Lifecycle.snapshot state).Exit)
        Assert.True(Lifecycle.apply (now 100L) (ReaderObserved(Stdout,known Reading)) state |> Result.isError)

    [<Fact>]
    let ``last accepted tick is monotonic even above original start`` () =
        let state = Lifecycle.apply (now 200L) ReturnRequested (prepared()) |> ok
        Assert.True(Lifecycle.apply (now 150L) ReturnRequested state |> Result.isError)
        Assert.True(Lifecycle.apply (now 201L) BeginLaunch state |> Result.isError)

    [<Fact>]
    let ``total expiry allows only bounded snapshot return`` () =
        let state = Lifecycle.apply (now 10100L) ReturnRequested (launched()) |> ok
        Assert.Equal(Retiring,(Lifecycle.snapshot state).Phase)
        Assert.True(Lifecycle.apply (now 10100L) (StopRequested WorkDeadline) state |> Result.isError)
        Assert.Equal(10100L,(Lifecycle.snapshot state |> _.OriginalBudget |> Budget.describe).TotalExpiresAt)

    [<Fact>]
    let ``original sink failure survives later reporting and cleanup failures`` () =
        let failure code = { Phase = Running; Diagnostic = diagnostic code; At = Some(now 100L) }
        let result = launched() |> apply (FailureObserved(failure "sink-failure")) |> apply (FailureObserved(failure "report-failure")) |> apply (FailureObserved(failure "cleanup-failure")) |> Lifecycle.snapshot
        Assert.Equal("sink-failure",result.FirstFailure.Value.Diagnostic.Code)
        Assert.Equal<string>([ "report-failure"; "cleanup-failure" ],result.AdditionalFailures |> List.map _.Diagnostic.Code)

    [<Fact>]
    let ``failure collection is finite and omissions are counted`` () =
        let mutable state = launched()
        for _ in 0 .. Validation.limits.CollectionEntries + 5 do state <- state |> apply (FailureObserved { Phase = Running; Diagnostic = diagnostic "same-failure"; At = Some(now 100L) })
        let result = Lifecycle.snapshot state
        Assert.Equal(Validation.limits.CollectionEntries,result.AdditionalFailures.Length)
        Assert.Equal(5L,result.OmittedFailures)

    [<Fact>]
    let ``unknown tasks or unresolved process cannot report complete`` () =
        let state = launched()
        let cleanup = { (Lifecycle.snapshot state).Cleanup with Disposition = Complete; LeaderTermination = known true; LocalTasks = known Settled; Unresolved = [ generation ] }
        Assert.True(Lifecycle.apply (now 100L) (CleanupObserved cleanup) state |> Result.isError)
        let cleanup = { cleanup with Unresolved = []; LocalTasks = known Outstanding }
        Assert.True(Lifecycle.apply (now 100L) (CleanupObserved cleanup) state |> Result.isError)

    [<Fact>]
    let ``requested enforced memory cannot be downgraded to sampling`` () =
        let resources = { resources with MemoryBytes = Some { Value = 1024L; Assurance = Enforced } }
        let limit = { Name = "memory"; Unit = "bytes"; Requested = "1024"; Actual = known "1024"; Assurance = known(Sampled(TimeSpan.FromMilliseconds 10.)) }
        Assert.NotEmpty(Validation.obtainedLimit resources limit)
        Assert.NotEmpty(Validation.obtainedLimit resources { limit with Unit = "processes" })
        Assert.Empty(Validation.obtainedLimit resources { limit with Assurance = known Enforced })

    [<Fact>]
    let ``output observations reject contradictory retained and charged counts`` () =
        let sink = BoundedOutput.create capture |> ok
        sink.Append(Stdout,memory [| 1uy; 2uy |]) |> ignore
        let output = sink.Snapshot()
        let bad = { output with Stdout = { output.Stdout with Totals = { output.Stdout.Totals with Charged = 0L } } }
        Assert.True(Lifecycle.apply (now 100L) (OutputObserved bad) (launched()) |> Result.isError)

    [<Fact>]
    let ``large secondary diagnostics cannot replace the original cause`` () =
        let original = { Phase = Running; Diagnostic = diagnostic "original-cause"; At = Some(now 100L) }
        let mutable state = launched() |> apply (FailureObserved original)
        for _ in 0 .. 10 do
            let large = { original with Diagnostic = { (diagnostic "reporting-failure") with Message = String.replicate 4000 "x" } }
            state <- state |> apply (FailureObserved large)
        Assert.Equal("original-cause",(Lifecycle.snapshot state).FirstFailure.Value.Diagnostic.Code)
        Assert.True((Lifecycle.snapshot state).OmittedFailures > 0L)

    [<Fact>]
    let ``capture snapshot cannot mutate the retained lifecycle observation`` () =
        let bytes = [| 1uy; 2uy |]
        let sink = BoundedOutput.create capture |> ok
        sink.Append(Stdout,memory bytes) |> ignore
        let state = launched() |> apply (OutputObserved(sink.Snapshot()))
        bytes[0] <- 9uy
        Assert.Equal<byte>([| 1uy; 2uy |],(Lifecycle.snapshot state).Stdout.Prefix.ToArray())

    [<Fact>]
    let ``obtained display amount must join typed resource request`` () =
        let resources = { resources with MemoryBytes = Some { Value = 1024L; Assurance = Enforced } }
        let limit = { Name = "memory"; Unit = "bytes"; Requested = "1024"; Actual = known "garbage"; Assurance = known Enforced }
        Assert.NotEmpty(Validation.obtainedLimit resources limit)
        Assert.NotEmpty(Validation.obtainedLimit resources { limit with Actual = known "2048" })
        Assert.Empty(Validation.obtainedLimit resources { limit with Actual = known "512" })

    [<Fact>]
    let ``known argument and environment values are redacted without replacing first cause code`` () =
        let command = { command with Arguments = [ "argument-secret" ]; Environment = Replace(Map.ofList [ "TOKEN", "environment-secret" ]) }
        let state = Lifecycle.prepare command identity budget |> ok
        let failure = { Phase = Prepared; Diagnostic = { Code = "launch-failure"; Message = "argument-secret environment-secret"; Evidence = None }; At = Some(now 100L) }
        let result = state |> apply (FailureObserved failure) |> Lifecycle.snapshot
        Assert.Equal("launch-failure",result.FirstFailure.Value.Diagnostic.Code)
        Assert.DoesNotContain("argument-secret",result.FirstFailure.Value.Diagnostic.Message)
        Assert.DoesNotContain("environment-secret",result.FirstFailure.Value.Diagnostic.Message)

    [<Fact>]
    let ``ordinary overflow retains first cause while affordable terminal evidence is admitted`` () =
        let sink = BoundedOutput.create capture |> ok
        sink.Append(Stdout,memory (Array.zeroCreate 28)) |> ignore
        sink.Append(Stderr,memory (Array.zeroCreate 28)) |> ignore
        let refusal = sink.Append(Stdout,memory [| 1uy |])
        Assert.True(match refusal with RetireRequired _ -> true | _ -> false)
        let original = sink.Snapshot().FirstFailure
        Assert.Equal(Accepted,sink.Append(TerminalEvidence,memory (Array.zeroCreate 7)))
        Assert.Equal(original,sink.Snapshot().FirstFailure)
        Assert.Equal(7L,sink.Snapshot().TerminalEvidenceBytes)
        Assert.Equal(64L,sink.Snapshot().ChargedTotal)
        Assert.True(match sink.Append(TerminalEvidence,memory [| 1uy |]) with RetireRequired _ -> true | _ -> false)
        Assert.Equal(original,sink.Snapshot().FirstFailure)

    [<Fact>]
    let ``oversized chunk failure still permits a finite terminal report`` () =
        let sink = BoundedOutput.create capture |> ok
        sink.Append(Stdout,memory (Array.zeroCreate 33)) |> ignore
        let first = sink.Snapshot().FirstFailure
        Assert.Equal(Accepted,sink.Append(TerminalEvidence,memory [| 1uy; 2uy |]))
        Assert.Equal(first,sink.Snapshot().FirstFailure)
        Assert.True(match sink.Append(Protocol,memory [| 1uy |]) with RetireRequired _ -> true | _ -> false)

    [<Fact>]
    let ``forged ordinary subtotal or terminal category cannot bypass carved reserve`` () =
        let sink = BoundedOutput.create capture |> ok
        sink.Append(Stdout,memory (Array.zeroCreate 28)) |> ignore
        sink.Append(Stderr,memory (Array.zeroCreate 28)) |> ignore
        sink.Append(Stdout,memory [| 1uy |]) |> ignore
        let bad = { (sink.Snapshot()) with FirstFailure = None }
        Assert.NotEmpty(Validation.output capture bad)
        Assert.NotEmpty(Validation.output capture { bad with TerminalEvidenceBytes = 1L })

    [<Theory>]
    [<InlineData("unavailable")>]
    [<InlineData("unknown")>]
    [<InlineData("limit-exceeded")>]
    let ``new mutable capability uncertainty or violation invalidates release proof`` (kind: string) =
        let command = { command with Capabilities = [ { Capability = StableLeaderIdentity; Required = true } ] }
        let state = Lifecycle.prepare command identity budget |> ok |> apply BeginLaunch
        let state = state |> apply (CapabilitiesObtained [ { Capability = StableLeaderIdentity; Obtained = knownBefore evidence } ])
        let observation = match kind with "unavailable" -> Unavailable(diagnostic "cap-lost") | "unknown" -> Unknown(diagnostic "cap-unknown") | _ -> LimitExceeded(diagnostic "cap-violation")
        let state = state |> apply (CapabilitiesObtained [ { Capability = StableLeaderIdentity; Obtained = observation } ])
        Assert.True(Lifecycle.apply (now 100L) TargetReleaseRequested state |> Result.isError)
        if kind = "limit-exceeded" then
            let recovered = state |> apply (CapabilitiesObtained [ { Capability = StableLeaderIdentity; Obtained = knownBefore evidence } ])
            Assert.True(Lifecycle.apply (now 100L) TargetReleaseRequested recovered |> Result.isError)
            Assert.Equal("cap-violation",(Lifecycle.snapshot recovered).FirstFailure.Value.Diagnostic.Code)
            Assert.NotEmpty((Lifecycle.snapshot recovered).Violations)

    [<Fact>]
    let ``incomplete or split limit bundle cannot inherit older amount proof`` () =
        let command = { command with Resources = { resources with MemoryBytes = Some { Value = 1024L; Assurance = Enforced } } }
        let state = Lifecycle.prepare command identity budget |> ok |> apply BeginLaunch
        let limit = { Name = "memory"; Unit = "bytes"; Requested = "1024"; Actual = knownBefore "1024"; Assurance = knownBefore Enforced }
        let state = state |> apply (LimitObserved limit)
        let state = state |> apply (LimitObserved { limit with Actual = Unknown(diagnostic "amount-unavailable") })
        Assert.True(Lifecycle.apply (now 100L) TargetReleaseRequested state |> Result.isError)
        let split = { limit with Assurance = Known(Enforced,{ beforeStamp with At = Some(now 101L) }) }
        Assert.True(Lifecycle.apply (now 101L) (LimitObserved split) state |> Result.isError)

    [<Fact>]
    let ``old empty scope is not merged into new incomplete cleanup proof`` () =
        let command = { command with Capabilities = [ { Capability = WorkloadEmptyObservation; Required = true } ] }
        let state = Lifecycle.prepare command identity budget |> ok |> apply BeginLaunch |> apply (CapabilitiesObtained [ { Capability = WorkloadEmptyObservation; Obtained = knownBefore evidence } ]) |> apply TargetReleaseRequested |> apply (LaunchObserved(Started generation))
        let state = state |> apply (ReaderObserved(Stdout,known Eof)) |> apply (ReaderObserved(Stderr,known Eof))
        let cleanup = { (Lifecycle.snapshot state).Cleanup with Disposition = Incomplete; LeaderTermination = known true; WorkloadEmpty = known true; LocalTasks = known Outstanding }
        let state = state |> apply (CleanupObserved cleanup)
        let partial = { cleanup with Disposition = Complete; WorkloadEmpty = Unknown(diagnostic "empty-unknown"); LocalTasks = known Settled }
        Assert.True(Lifecycle.apply (now 100L) (CleanupObserved partial) state |> Result.isError)
        let partial = { partial with Disposition = Incomplete }
        let state = state |> apply (CleanupObserved partial)
        Assert.True(match (Lifecycle.snapshot state).Cleanup.WorkloadEmpty with Unknown _ -> true | _ -> false)

    [<Theory>]
    [<InlineData("cancel")>]
    [<InlineData("return")>]
    let ``late start after cancel or return remains retiring`` (kind: string) =
        let state = prepared() |> apply BeginLaunch |> apply TargetReleaseRequested
        let state = if kind = "cancel" then state |> apply (StopRequested CallerCancellation) else state |> apply ReturnRequested
        let state = state |> apply (LaunchObserved(Started generation))
        Assert.Equal(Retiring,(Lifecycle.snapshot state).Phase)
        Assert.Equal(Started generation,(Lifecycle.snapshot state).Launch)
        Assert.True(Lifecycle.apply (now 100L) BeginLaunch state |> Result.isError)

    [<Fact>]
    let ``terminal duplicates do not reopen phase and reader failure cannot become EOF`` () =
        let failed = launched() |> apply (ReaderObserved(Stdout,known(ReadFailed(diagnostic "read-original"))))
        Assert.Equal("read-original",(Lifecycle.snapshot failed).FirstFailure.Value.Diagnostic.Code)
        Assert.True(Lifecycle.apply (now 100L) (ReaderObserved(Stdout,known Eof)) failed |> Result.isError)
        Assert.True(Lifecycle.apply (now 100L) (ReaderObserved(Stdout,known Reading)) failed |> Result.isError)
        let state = launched() |> apply (ExitObserved(known(Exited 0))) |> apply (ReaderObserved(Stdout,known Eof)) |> apply (ReaderObserved(Stderr,known Eof))
        let cleanup = { (Lifecycle.snapshot state).Cleanup with Disposition = Complete; LeaderTermination = known true; LocalTasks = known Settled }
        let state = state |> apply (CleanupObserved cleanup) |> apply (ExitObserved(known(Exited 0))) |> apply (StopRequested CallerCancellation)
        Assert.Equal(Terminal,(Lifecycle.snapshot state).Phase)

    [<Fact>]
    let ``confirmed missing executable is no effect after release request but ambiguity is not`` () =
        let state = prepared() |> apply BeginLaunch |> apply TargetReleaseRequested
        let refused = state |> apply (LaunchObserved(Refused(diagnostic "missing-executable")))
        let cleanup = { (Lifecycle.snapshot refused).Cleanup with Disposition = Complete; LocalTasks = knownBefore Settled; Custody = NoEffect }
        Assert.Equal(Terminal,(refused |> apply (CleanupObserved cleanup) |> Lifecycle.snapshot).Phase)
        let ambiguous = state |> apply (LaunchObserved(Ambiguous(diagnostic "launch-unknown")))
        Assert.True(Lifecycle.apply (now 100L) (CleanupObserved cleanup) ambiguous |> Result.isError)

    [<Theory>]
    [<InlineData("scope")>]
    [<InlineData("invocation")>]
    [<InlineData("pre-start")>]
    let ``foreign operation scope or pre-start capability proof cannot release target`` (kind: string) =
        let command = { command with Capabilities = [ { Capability = StableLeaderIdentity; Required = true } ] }
        let state = Lifecycle.prepare command identity budget |> ok |> apply BeginLaunch
        let bad = match kind with "scope" -> { beforeStamp with Scope = "foreign-scope" } | "invocation" -> { beforeStamp with Scope = Guid.NewGuid().ToString("D") } | _ -> { beforeStamp with At = Some(now 99L) }
        Assert.True(Lifecycle.apply (now 100L) (CapabilitiesObtained [ { Capability = StableLeaderIdentity; Obtained = Known(evidence,bad) } ]) state |> Result.isError)
        Assert.True(Lifecycle.apply (now 100L) TargetReleaseRequested state |> Result.isError)

    [<Theory>]
    [<InlineData("birth")>]
    [<InlineData("scope")>]
    [<InlineData("pre-start")>]
    [<InlineData("missing-generation")>]
    let ``foreign or old terminal evidence cannot supply exit or cleanup completion`` (kind: string) =
        let state = launched() |> apply (ReaderObserved(Stdout,known Eof)) |> apply (ReaderObserved(Stderr,known Eof))
        let bad = match kind with "birth" -> { stamp with Generation = Some { generation with BirthToken = "other-birth" } } | "scope" -> { stamp with Scope = "foreign-scope" } | "pre-start" -> { stamp with At = Some(now 99L) } | _ -> { stamp with Generation = None }
        Assert.True(Lifecycle.apply (now 100L) (ExitObserved(Known(Exited 0,bad))) state |> Result.isError)
        let cleanup = { (Lifecycle.snapshot state).Cleanup with Disposition = Complete; LeaderTermination = Known(true,bad); LocalTasks = known Settled }
        Assert.True(Lifecycle.apply (now 100L) (CleanupObserved cleanup) state |> Result.isError)

    let private metadataPressure () =
        let mutable state = launched()
        let detail = { (diagnostic "retirement-action") with Message = String.replicate 128 "x" }
        for _ in 1 .. Validation.limits.CollectionEntries do
            match Lifecycle.apply (now 100L) (RetirementActionRequested detail) state with Ok next -> state <- next | Error _ -> ()
        Assert.True(Lifecycle.metadataBytes state > 50000L)
        Assert.True(Lifecycle.metadataBytes state <= int64 (Validation.limits.ResultMetadataBytes - Validation.limits.FirstCauseReserveBytes))
        state

    [<Fact>]
    let ``first explicit cause survives preexisting metadata pressure with truthful omissions`` () =
        let state = metadataPressure()
        let original = { Phase = Retiring; Diagnostic = { (diagnostic "first-original-cause") with Message = String.replicate 4096 "y" }; At = Some(now 100L) }
        let state = state |> apply (FailureObserved original)
        let result = Lifecycle.snapshot state
        Assert.Equal("first-original-cause",result.FirstFailure.Value.Diagnostic.Code)
        Assert.Equal(original.At,result.FirstFailure.Value.At)
        Assert.Equal(0L,result.OmittedFailures)
        Assert.Equal(1L,result.OmittedDiagnosticDetails)
        Assert.True(Lifecycle.metadataBytes state <= int64 Validation.limits.ResultMetadataBytes)
        let state = state |> apply (FailureObserved { original with Diagnostic = { original.Diagnostic with Code = "reporting-secondary" } })
        Assert.Equal("first-original-cause",(Lifecycle.snapshot state).FirstFailure.Value.Diagnostic.Code)
        Assert.Equal(1L,(Lifecycle.snapshot state).OmittedFailures)

    [<Fact>]
    let ``actual output overflow retains original cause under preexisting metadata pressure`` () =
        let state = metadataPressure()
        let sink = BoundedOutput.create capture |> ok
        sink.Append(Stdout,memory (Array.zeroCreate 28)) |> ignore
        sink.Append(Stderr,memory (Array.zeroCreate 28)) |> ignore
        sink.Append(Stdout,memory [| 1uy |]) |> ignore
        let state = state |> apply (OutputObserved(sink.Snapshot()))
        Assert.Equal("output-byte-limit",(Lifecycle.snapshot state).FirstFailure.Value.Diagnostic.Code)
        Assert.Equal(Some(now 100L),(Lifecycle.snapshot state).FirstFailure.Value.At)
        Assert.True(Lifecycle.metadataBytes state <= int64 Validation.limits.ResultMetadataBytes)

    [<Fact>]
    let ``reader failure under metadata pressure retains cause and terminal outcome even if detail is omitted`` () =
        let state = metadataPressure()
        let proof = { stamp with Provenance = { evidence with Reference = String.replicate 2048 "z" } }
        let failed = { (diagnostic "reader-original-cause") with Message = String.replicate 4096 "y" }
        let state = state |> apply (ReaderObserved(Stdout,Known(ReadFailed failed,proof)))
        Assert.Equal("reader-original-cause",(Lifecycle.snapshot state).FirstFailure.Value.Diagnostic.Code)
        Assert.True((Lifecycle.snapshot state).OmittedDiagnosticDetails > 0L)
        Assert.True(Lifecycle.metadataBytes state <= int64 Validation.limits.ResultMetadataBytes)
        Assert.True(Lifecycle.apply (now 100L) (ReaderObserved(Stdout,known Eof)) state |> Result.isError)

    [<Fact>]
    let ``mutable violation under metadata pressure blocks release and remains latched after new known proof`` () =
        let state = metadataPressure()
        let original = { (diagnostic "resource-original-cause") with Message = String.replicate 4096 "y" }
        let state = state |> apply (CapabilitiesObtained [ { Capability = StableLeaderIdentity; Obtained = LimitExceeded original } ])
        Assert.Equal("resource-original-cause",(Lifecycle.snapshot state).FirstFailure.Value.Diagnostic.Code)
        let state = state |> apply (CapabilitiesObtained [ { Capability = StableLeaderIdentity; Obtained = known evidence } ])
        Assert.Equal("resource-original-cause",(Lifecycle.snapshot state).FirstFailure.Value.Diagnostic.Code)
        Assert.NotEmpty((Lifecycle.snapshot state).Violations)
        Assert.True(Lifecycle.apply (now 100L) TargetReleaseRequested state |> Result.isError)

    [<Fact>]
    let ``valid operation proof must be revalidated at release and process birth cannot change`` () =
        let command = { command with Capabilities = [ { Capability = StableLeaderIdentity; Required = true } ] }
        let state = Lifecycle.prepare command identity budget |> ok |> apply BeginLaunch |> apply (CapabilitiesObtained [ { Capability = StableLeaderIdentity; Obtained = knownBefore evidence } ])
        Assert.True(Lifecycle.apply (now 101L) TargetReleaseRequested state |> Result.isError)
        let fresh = Known(evidence,{ beforeStamp with At = Some(now 101L) })
        let state = Lifecycle.apply (now 101L) (CapabilitiesObtained [ { Capability = StableLeaderIdentity; Obtained = fresh } ]) state |> ok
        let state = Lifecycle.apply (now 101L) TargetReleaseRequested state |> ok
        let state = Lifecycle.apply (now 101L) (LaunchObserved(Started generation)) state |> ok
        Assert.True(Lifecycle.apply (now 101L) (LaunchObserved(Started { generation with BirthToken = "other-birth" })) state |> Result.isError)
        Assert.True(Lifecycle.apply (now 101L) (LaunchObserved(Started { generation with Pid = 0 })) state |> Result.isError)

    [<Fact>]
    let ``terminal proof before original target-release window is refused`` () =
        let state = prepared() |> apply BeginLaunch
        let state = Lifecycle.apply (now 110L) TargetReleaseRequested state |> ok
        let state = Lifecycle.apply (now 120L) (LaunchObserved(Started generation)) state |> ok
        Assert.True(Lifecycle.apply (now 120L) (ExitObserved(known(Exited 0))) state |> Result.isError)
        let valid = Known(Exited 0,{ stamp with At = Some(now 115L) })
        Assert.True(Lifecycle.apply (now 120L) (ExitObserved valid) state |> Result.isOk)

    let private nonActionMetadataPressure () =
        let proof = { stamp with Provenance = { evidence with Reference = String.replicate 2048 "p" } }
        let value = { evidence with Reference = String.replicate 2048 "v" }
        let state = launched() |> apply (CapabilitiesObtained [ { Capability = StableLeaderIdentity; Obtained = Known(value,proof) }; { Capability = WorkloadEmptyObservation; Obtained = Known(value,proof) } ])
        Assert.Empty((Lifecycle.snapshot state).Cleanup.RequestedActions)
        Assert.True(Lifecycle.metadataBytes state > 50000L)
        Assert.True(Lifecycle.metadataBytes state <= int64 (Validation.limits.ResultMetadataBytes - Validation.limits.FirstCauseReserveBytes))
        state

    [<Fact>]
    let ``cleanup violation keeps original first cause under no action evidence pressure`` () =
        let state = nonActionMetadataPressure()
        let failure = { (diagnostic "cleanup-original") with Message = String.replicate 4096 "x" }
        let cleanup = { (Lifecycle.snapshot state).Cleanup with LeaderTermination = LimitExceeded failure }
        let state = state |> apply (CleanupObserved cleanup)
        let result = Lifecycle.snapshot state
        Assert.Equal("cleanup-original",result.FirstFailure.Value.Diagnostic.Code)
        Assert.Equal(Running,result.FirstFailure.Value.Phase)
        Assert.Equal(Some(now 100L),result.FirstFailure.Value.At)
        Assert.Equal(Retiring,result.Phase)
        Assert.Equal(CleanupUnknown,result.Cleanup.Disposition)
        Assert.True(match result.Cleanup.LeaderTermination with LimitExceeded d -> d.Code = "cleanup-original" | _ -> false)
        Assert.True(result.OmittedDiagnosticDetails > 0L)
        Assert.Equal(0L,result.OmittedFailures)
        Assert.True(Lifecycle.metadataBytes state <= int64 Validation.limits.ResultMetadataBytes)
        Assert.True(Lifecycle.apply (now 100L) TargetReleaseRequested state |> Result.isError)

    [<Fact>]
    let ``simultaneous cleanup violations preserve maximum codes and negative facts within metadata cap`` () =
        let state = nonActionMetadataPressure()
        let failure letter = { (diagnostic (String.replicate Validation.limits.CodeBytes letter)) with Message = String.replicate Validation.limits.MessageBytes "x" }
        let cleanup = { (Lifecycle.snapshot state).Cleanup with LeaderTermination = LimitExceeded(failure "l"); WorkloadEmpty = LimitExceeded(failure "e"); OwnedChildrenReaped = LimitExceeded(failure "r"); LocalTasks = LimitExceeded(failure "t") }
        let state = state |> apply (CleanupObserved cleanup)
        let result = Lifecycle.snapshot state
        Assert.Equal(String.replicate 64 "l",result.FirstFailure.Value.Diagnostic.Code)
        Assert.Equal(Running,result.FirstFailure.Value.Phase)
        Assert.Equal(Some(now 100L),result.FirstFailure.Value.At)
        Assert.Equal(Retiring,result.Phase)
        Assert.Equal(CleanupUnknown,result.Cleanup.Disposition)
        Assert.True(match result.Cleanup.LeaderTermination with LimitExceeded d -> d.Code = String.replicate 64 "l" | _ -> false)
        Assert.True(match result.Cleanup.WorkloadEmpty with LimitExceeded d -> d.Code = String.replicate 64 "e" | _ -> false)
        Assert.True(match result.Cleanup.OwnedChildrenReaped with LimitExceeded d -> d.Code = String.replicate 64 "r" | _ -> false)
        Assert.True(match result.Cleanup.LocalTasks with LimitExceeded d -> d.Code = String.replicate 64 "t" | _ -> false)
        Assert.True(result.OmittedDiagnosticDetails > 0L)
        Assert.True(Lifecycle.metadataBytes state <= int64 Validation.limits.ResultMetadataBytes)
        Assert.True(Lifecycle.apply (now 100L) (CleanupObserved { cleanup with Disposition = Complete }) state |> Result.isError)

    [<Fact>]
    let ``oversized complete cleanup projects uncertainty and keeps actual completion history`` () =
        let generation = { generation with BirthToken = String.replicate 2048 "b" }
        let stamp = { stamp with Generation = Some generation }
        let known value = Known(value,stamp)
        let proof = { stamp with Provenance = { evidence with Reference = String.replicate 2048 "p" } }
        let state = prepared() |> apply BeginLaunch |> apply TargetReleaseRequested |> apply (LaunchObserved(Started generation))
        let state = state |> apply (ReaderObserved(Stdout,known Eof)) |> apply (ReaderObserved(Stderr,known Eof))
        let failure = { (diagnostic "cleanup-large-proof-original") with Message = String.replicate 4096 "x" }
        let cleanup = { (Lifecycle.snapshot state).Cleanup with Disposition = Complete; LeaderTermination = Known(true,proof); LocalTasks = Known(Settled,proof); WorkloadEmpty = LimitExceeded failure; Custody = Released proof }
        let state = state |> apply (CleanupObserved cleanup)
        let result = Lifecycle.snapshot state
        Assert.Equal(Terminal,result.Phase)
        Assert.Equal("cleanup-large-proof-original",result.FirstFailure.Value.Diagnostic.Code)
        Assert.Equal(Running,result.FirstFailure.Value.Phase)
        Assert.Equal(Some(now 100L),result.FirstFailure.Value.At)
        Assert.Equal(Incomplete,result.Cleanup.Disposition)
        Assert.True(match result.Cleanup.LeaderTermination with Unknown _ -> true | _ -> false)
        Assert.True(match result.Cleanup.LocalTasks with Unknown _ -> true | _ -> false)
        Assert.True(match result.Cleanup.Custody with AuthorityUnavailable _ -> true | _ -> false)
        Assert.True(match result.Cleanup.WorkloadEmpty with LimitExceeded d -> d.Code = "cleanup-large-proof-original" | _ -> false)
        Assert.True(result.OmittedDiagnosticDetails > 0L)
        Assert.True(Lifecycle.metadataBytes state <= int64 Validation.limits.ResultMetadataBytes)
        Assert.True(Lifecycle.apply (now 100L) (CleanupObserved { cleanup with Disposition = Incomplete }) state |> Result.isError)
        let returned = state |> apply ReturnRequested |> Lifecycle.snapshot
        Assert.Equal(Terminal,returned.Phase)
        Assert.Equal(Incomplete,returned.Cleanup.Disposition)
        Assert.True(match returned.Cleanup.LeaderTermination with Unknown _ -> true | _ -> false)

    [<Fact>]
    let ``oversized unresolved and custody evidence preserve unknowns with truthful omissions`` () =
        let state = launched()
        let unknowns = [ for i in 1 .. Validation.limits.CollectionEntries -> { generation with Pid = 100 + i; BirthToken = String.replicate 2048 (string (char (65 + i % 26))) } ]
        let failure = { (diagnostic "cleanup-large-unresolved-original") with Message = String.replicate 4096 "x" }
        let retained = { OwnerId = String.replicate 64 "o"; Evidence = { evidence with Reference = String.replicate 2048 "p" } }
        let cleanup = { (Lifecycle.snapshot state).Cleanup with Disposition = Incomplete; LeaderTermination = LimitExceeded failure; Unresolved = unknowns; OmittedUnresolved = Int64.MaxValue - 1L; Custody = RetainedByOwner retained }
        let state = state |> apply (CleanupObserved cleanup)
        let result = Lifecycle.snapshot state
        Assert.Equal("cleanup-large-unresolved-original",result.FirstFailure.Value.Diagnostic.Code)
        Assert.Equal(Running,result.FirstFailure.Value.Phase)
        Assert.Equal(Some(now 100L),result.FirstFailure.Value.At)
        Assert.Equal(Incomplete,result.Cleanup.Disposition)
        Assert.Empty(result.Cleanup.Unresolved)
        Assert.Equal(Int64.MaxValue,result.Cleanup.OmittedUnresolved)
        Assert.True(match result.Cleanup.Custody with AuthorityUnavailable _ -> true | _ -> false)
        Assert.True(match result.Cleanup.WorkloadEmpty with Unknown _ -> true | _ -> false)
        Assert.True(match result.Cleanup.LeaderTermination with LimitExceeded d -> d.Code = "cleanup-large-unresolved-original" | _ -> false)
        Assert.True(result.OmittedDiagnosticDetails > 0L)
        Assert.True(Lifecycle.metadataBytes state <= int64 Validation.limits.ResultMetadataBytes)

    [<Fact>]
    let ``maximal immutable and mutable metadata still retains an original capability failure`` () =
        let affinity = [ for i in 0 .. 63 -> 1000000000 + i ]
        let maximum value = { Value = value; Assurance = Enforced }
        let resources = { MemoryBytes = Some(maximum Int64.MaxValue); CpuQuota = Some(maximum { Quota = TimeSpan.MaxValue; Period = TimeSpan.MaxValue }); CpuAffinity = Some affinity
                          MaxKernelTasks = Some(maximum Int32.MaxValue); MaxOwnedProcesses = Some(maximum Int32.MaxValue); MaxClrProcesses = Some(maximum Int32.MaxValue) }
        let command = { command with Resources = resources }
        let identity = { identity with ProfileId = String.replicate 64 "i" }
        let generation = { generation with Platform = String.replicate 64 "p"; BirthToken = String.replicate 2048 "b" }
        let small = { beforeStamp with Scope = identity.InvocationId.ToString("D") }
        let tuples = [ "memory","bytes",string Int64.MaxValue; "cpuQuota","ticks-per-period",$"{TimeSpan.MaxValue.Ticks}/{TimeSpan.MaxValue.Ticks}"; "cpuAffinity","cpu-ids",String.Join(",",affinity)
                       "kernelTasks","kernel-tasks",string Int32.MaxValue; "ownedProcesses","process-generations",string Int32.MaxValue; "clrProcesses","classified-clr-hosts",string Int32.MaxValue ]
        let mutable state = Lifecycle.prepare command identity budget |> ok |> apply BeginLaunch
        for name,unit,requested in tuples do
            state <- state |> apply (LimitObserved { Name = name; Unit = unit; Requested = requested; Actual = Known(requested,small); Assurance = Known(Enforced,small) })
        state <- state |> apply TargetReleaseRequested |> apply (LaunchObserved(Started generation))
        let uncertain = diagnostic (String.replicate 64 "u") |> fun d -> { d with Message = "Omitted." }
        for name,unit,requested in tuples do
            state <- state |> apply (LimitObserved { Name = name; Unit = unit; Requested = requested; Actual = Unknown uncertain; Assurance = Unknown uncertain })
        let proof = { stamp with Generation = Some generation; Provenance = { Kind = String.replicate 64 "k"; Reference = String.replicate 2048 "r"; Digest = Some(String.replicate 64 "d") } }
        state <- state |> apply (ExitObserved(Known(Exited 0,proof)))
        Assert.Empty((Lifecycle.snapshot state).Capabilities)
        Assert.Empty((Lifecycle.snapshot state).Cleanup.RequestedActions)
        Assert.True(Lifecycle.metadataBytes state > 59000L)
        Assert.True(Lifecycle.metadataBytes state <= int64 (Validation.limits.ResultMetadataBytes - Validation.limits.FirstCauseReserveBytes))
        let capabilities = [ StableLeaderIdentity; OwnedDescendantObservation; ContainmentBeforeTarget; PidNamespaceIsolation; UserNamespaceIsolation; MountNamespaceIsolation; NetworkIsolation
                             WorkloadEmptyObservation; OwnedChildReaping; AggregateMemoryLimit; AggregateCpuQuota; KernelTaskLimit; OwnerSurvivalCleanup; BoundedReturnWithRetainedCustody ]
        let batch = capabilities |> List.mapi (fun index capability -> { Capability = capability; Obtained = LimitExceeded { Code = String.replicate 63 "v" + string (char (65 + index)); Message = "Omitted."; Evidence = None } })
        state <- state |> apply (CapabilitiesObtained batch)
        let result = Lifecycle.snapshot state
        Assert.Equal(String.replicate 63 "v" + "A",result.FirstFailure.Value.Diagnostic.Code)
        Assert.Equal(Retiring,result.FirstFailure.Value.Phase)
        Assert.Equal(Some(now 100L),result.FirstFailure.Value.At)
        Assert.Equal(Retiring,result.Phase)
        Assert.Equal(14,result.Capabilities.Length)
        for item in result.Capabilities do Assert.True(match item.Obtained with LimitExceeded d -> d.Code.Length = 64 | _ -> false)
        for item in result.Limits do
            Assert.True(match item.Actual with Unknown d -> d.Code = String.replicate 64 "u" | _ -> false)
            Assert.True(match item.Assurance with Unknown d -> d.Code = String.replicate 64 "u" | _ -> false)
        Assert.True(match result.Exit with Unknown _ -> true | _ -> false)
        Assert.True(result.OmittedDiagnosticDetails > 0L)
        Assert.True(Lifecycle.metadataBytes state <= int64 Validation.limits.ResultMetadataBytes)
        Assert.True(Lifecycle.apply (now 100L) (ExitObserved(Known(Exited 1,proof))) state |> Result.isError)
        let uncertainExit = state |> apply (ExitObserved(Unknown uncertain)) |> Lifecycle.snapshot
        Assert.True(match uncertainExit.Exit with Unknown _ -> true | _ -> false)
        Assert.True(Lifecycle.apply (now 100L) TargetReleaseRequested state |> Result.isError)
