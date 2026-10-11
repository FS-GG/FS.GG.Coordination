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
    let stamp = { At = Some(now 100L); Scope = "fixture"; Provenance = evidence }
    let known value = Known(value,stamp)
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
    let generation = { Platform = "fixture"; ScopeId = "scope"; Pid = 42; BirthToken = "birth-1" }
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
        let state = state |> apply (CapabilitiesObtained [ { Capability = BoundedReturnWithRetainedCustody; Obtained = known evidence } ])
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
