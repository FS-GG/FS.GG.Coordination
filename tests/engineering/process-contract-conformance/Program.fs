namespace FS.GG.Execution.Contracts.Conformance

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open FS.GG.Execution.Contracts

type CaseReceipt =
    { Case: string; InvocationId: string; Accepted: bool; Checks: string array; Failures: string array
      InitialExit: string; InitialStdoutReader: string; InitialStderrReader: string; InitialPhase: string
      FinalExit: string; FinalStdoutReader: string; FinalStderrReader: string; FirstCause: string; FirstCauseMessage: string; FirstCausePhase: string; FirstCauseAtTicks: int64
      StdoutCharged: int64; StdoutRetained: int64; StderrCharged: int64; StderrRetained: int64
      StdoutPrefixBase64: string; StderrPrefixBase64: string; MetadataBytesBound: int
      Cleanup: string; Custody: string; LocalReleased: bool; ObserverErrors: string array
      ClockDomain: string; ClockFrequency: int64; StartedAtTicks: int64; WorkExpiresAtTicks: int64; TotalExpiresAtTicks: int64
      RunReturnAtTicks: int64; CallerObservedRunAtTicks: int64; RetirementReturnedAtTicks: int64; ReleaseDecisionAtTicks: int64; FinalObservedAtTicks: int64
      HelperStartGateAtTicks: int64; HelperStartedAtTicks: int64; TargetReleaseGateAtTicks: int64; TargetReleasedAtTicks: int64; TargetWasUncancelled: bool
      LeaderPid: int; LeaderBirth: string; LeaderScope: string; InitialLocalPending: bool
      EarlyReleaseProbed: bool; EarlyDecisionAtTicks: int64; EarlyLocalPending: bool; EarlyReleaseRefused: bool
      WorkStopAtTicks: int64; RunAfterWorkTicks: int64 }

module Program =
    let private hash (path: string) = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes path))
    let private text (node: JsonElement) (name: string) = node.GetProperty(name).GetString() |> fun s -> if isNull s then invalidOp "null-field" else s
    let private flag (node: JsonElement) (name: string) = node.GetProperty(name).GetBoolean()
    let private pin (node: JsonElement) =
        let path = text node "path"
        Support.require (Path.IsPathFullyQualified path && File.Exists path && not (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))) "input-pin-path"
        let length = node.GetProperty("bytes").GetInt64()
        Support.require (FileInfo(path).Length = length && hash path = text node "sha256") "input-pin-currentness"
        path
    let private label (value: CleanupDisposition) = match value with Complete -> "complete" | Incomplete -> "incomplete" | CleanupUnknown -> "unknown"
    let private custodyLabel (value: CustodyState) = match value with NoEffect -> "no-effect" | RetainedByOwner _ -> "retained" | Released _ -> "released-local" | AuthorityUnavailable _ -> "unavailable"
    let private code (value: Failure option) = match value with Some f -> f.Diagnostic.Code | None -> ""
    let private literal = [ "literal space"; "quote\"and\\slash"; "café" ]
    let private cases = [ "literal-normal"; "missing"; "prestart-cancel"; "flood"; "failed-sink"; "held-writer"; "deadline"; "caller-cancel" ]
    let private command (python: string) (fixture: string) (work: string) (case: string) : CommandSpec =
        let mode = match case with "flood" -> "ordinary-overflow" | "failed-sink" -> "ordinary-dual" | "held-writer" -> "ordinary-heldpipe"
                                  | "deadline" -> "ordinary-deadline" | "caller-cancel" -> "ordinary-cancel" | _ -> "literal-normal"
        let arguments = [ "-I"; "-S"; "-B"; fixture; mode ] @ (if mode = "literal-normal" then literal else [])
        let capture: CapturePolicy =
            { TotalBytes = 4096L; TerminalReserveBytes = 256
              StdoutPrefixBytes = if case = "failed-sink" then 0 else 512
              StdoutTailBytes = if case = "failed-sink" then 0 else 128
              StderrPrefixBytes = 512; StderrTailBytes = 128
              ReadChunkBytes = 512; MaximumFrameBytes = 1024; MaximumIncompleteLineBytes = 1024 }
        { Executable = if case = "missing" then Path.Combine(work,"intentionally-absent-python") else python
          Arguments = arguments; WorkingDirectory = work
          Environment = Replace(Map.ofList [ "HOME","/home/developer"; "FSGG_LITERAL","literal env é"; "LC_ALL","C.UTF-8" ])
          Input = Bytes(ReadOnlyMemory<byte>([|byte 'G'|])); Capture = capture
          Capabilities = [ { Capability = BoundedReturnWithRetainedCustody; Required = true } ]
          Resources = { MemoryBytes = None; CpuQuota = None; CpuAffinity = None; MaxKernelTasks = None; MaxOwnedProcesses = None; MaxClrProcesses = None } }
    let private sourceControls (python: string) (fixture: string) (work: string) (source: string) (profile: string) (profileDigest: string) (owner: OwnerReference) =
        // Finite reducer/gate controls, no native effect or authority acquisition.
        let names = ResizeArray<string>()
        let check condition name = Support.require condition name; names.Add name
        let identity: OperationIdentity =
            { InvocationId = Guid.NewGuid()
              Execution = None
              LaunchOrdinal = 0UL
              SourceDigest = source
              ProfileId = profile
              ProfileDigest = profileDigest }
        let domain: ClockDomain = { Id = Guid.NewGuid(); Frequency = 1000L }
        let at (ticks: int64) : ClockStamp = { Domain = domain; Ticks = ticks }
        let original = Budget.create (at 1000L) (TimeSpan.FromSeconds 3.0) (TimeSpan.FromSeconds 2.5) |> Support.ok
        let description = Budget.describe original
        let generation: ProcessGeneration = { Platform = "linux"; ScopeId = identity.InvocationId.ToString("D"); Pid = 1; BirthToken = "synthetic-reducer-control" }
        let stamp (tick: int64) (leader: ProcessGeneration option) : ObservationStamp =
            { At = Some(at tick); Scope = identity.InvocationId.ToString("D"); Generation = leader; Provenance = owner.Evidence }
        let apply (tick: int64) (event: LifecycleEvent) (current: LifecycleState) = Lifecycle.apply (at tick) event current |> Support.ok
        let prepare () = Lifecycle.prepare (command python fixture work "literal-normal") identity original |> Support.ok
        let begun = prepare() |> apply 1000L BeginLaunch
        let retained = Support.currentCleanup (Lifecycle.snapshot begun).Cleanup (stamp 1100L None) None Outstanding (RetainedByOwner owner)
        let capability: CapabilityObservation = { Capability = BoundedReturnWithRetainedCustody; Obtained = Known(owner.Evidence,stamp 1100L None) }
        let running = begun |> apply 1100L (CleanupObserved retained) |> apply 1100L (CapabilitiesObtained [capability])
                            |> apply 1100L TargetReleaseRequested |> apply 1101L (LaunchObserved(Started generation))
                            |> apply 1102L (ReaderObserved(Stdout,Known(Eof,stamp 1102L (Some generation))))
                            |> apply 1103L (ReaderObserved(Stderr,Known(Eof,stamp 1103L (Some generation))))
        let settled = Support.currentCleanup (Lifecycle.snapshot running).Cleanup (stamp 1104L (Some generation)) (Some true) Settled (RetainedByOwner owner)
        check (Lifecycle.apply (at 1105L) (CleanupObserved settled) running |> Result.isError) "C1-stale-settled-bundle-refused"
        let settledState = running |> apply 1104L (CleanupObserved settled)
        check ((Lifecycle.snapshot settledState).Cleanup.LocalTasks = Known(Settled,stamp 1104L (Some generation))) "C1-fresh-settled-bundle-admitted"
        let releaseStamp = stamp 1105L (Some generation)
        let released = Support.currentCleanup (Lifecycle.snapshot settledState).Cleanup releaseStamp (Some true) Settled (Released releaseStamp)
        check (Lifecycle.apply (at 1106L) (CleanupObserved released) settledState |> Result.isError) "C1-stale-released-bundle-refused"
        let releasedState = settledState |> apply 1105L (CleanupObserved released)
        check ((Lifecycle.snapshot releasedState).Cleanup.Custody = Released releaseStamp) "C1-fresh-release-admitted"
        let missing = prepare() |> apply 1000L BeginLaunch |> apply 1101L (LaunchObserved(Refused(Support.diagnostic "missing-executable")))
        let noEffect = Support.currentCleanup (Lifecycle.snapshot missing).Cleanup (stamp 1102L None) None Settled NoEffect
        check (Lifecycle.apply (at 1103L) (CleanupObserved noEffect) missing |> Result.isError) "C1-stale-no-effect-bundle-refused"
        let missingState = missing |> apply 1102L (CleanupObserved noEffect)
        check ((Lifecycle.snapshot missingState).Cleanup.Custody = NoEffect) "C1-fresh-no-effect-admitted"
        check (Support.workReleaseDecision (Budget.remaining (at 1100L) original) false = Ok ()) "C2-current-uncancelled-release"
        check (Support.workReleaseDecision (Budget.remaining (at 1600L) original) false = Error "work-deadline") "C2-startup-elapsed-release-refused"
        check (Support.workReleaseDecision (Budget.remaining (at 1100L) original) true = Error "caller-cancelled") "C2-startup-cancelled-release-refused"
        check (Support.workReleaseDecision (Budget.remaining (at 1600L) original) true = Error "caller-cancelled") "C2-elapsed-and-cancelled-release-refused"
        check (Support.workReleaseDecision (Error ClockDomainMismatch) false = Error "startup-budget-refused") "C2-invalid-clock-release-refused"
        check (Budget.describe original = description) "C2-original-expiries-unchanged"
        names.ToArray()
    let private runCase (python: string) (fixture: string) (work: string) (source: string) (profile: string) (profileDigest: string) (owner: OwnerReference) (case: string) =
        let identity: OperationIdentity =
            { InvocationId = Guid.NewGuid()
              Execution = None
              LaunchOrdinal = 0UL
              SourceDigest = source
              ProfileId = profile
              ProfileDigest = profileDigest }
        // Session remains reachable by this caller across bounded Run and finite original cleanup.
        let lease: FixtureSession = FixtureSession(command python fixture work case,identity,owner,(case = "failed-sink"))
        let session = lease :> IProcessSession
        use cancellation = new CancellationTokenSource()
        if case = "prestart-cancel" then cancellation.Cancel()
        elif case = "caller-cancel" then cancellation.CancelAfter(TimeSpan.FromMilliseconds 100.0)
        let original = Budget.describe lease.Budget
        let initial = session.RunAsync(cancellation.Token).GetAwaiter().GetResult()
        let callerObservedRunAt = lease.Now()
        let early = if List.contains case [ "held-writer"; "deadline"; "caller-cancel" ] then Some(lease.ProbeRelease()) else None
        lease.RetireWithinOriginalBudgetAsync().GetAwaiter().GetResult()
        let retirementAt = lease.Now()
        let releaseDecision,releaseAt,_ = lease.ProbeRelease()
        let released = match releaseDecision with ResourcesReleased -> true | ReleaseRefused _ -> false
        let final = session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
        let finalAt = lease.Now()
        let earlyAt,earlyPending,earlyRefused =
            match early with
            | Some(decision,at,pending) -> at.Ticks,pending,(match decision with ReleaseRefused _ -> true | ResourcesReleased -> false)
            | None -> -1L,false,false
        let checks = ResizeArray<string>()
        let failures = ResizeArray<string>()
        let check condition name = if condition then checks.Add name else failures.Add name
        check released "local-release-established"
        check (lease.ObserverErrors.Length = 0) "all-contract-observations-admitted"
        check (final.Cleanup.Disposition <> Complete) "no-fabricated-whole-workload-completion"
        check (final.FirstFailure = initial.FirstFailure) "original-first-cause-message-phase-time-preserved"
        check (lease.MetadataBytes <= int64 Validation.limits.ResultMetadataBytes) "actual-metadata-bound"
        check (callerObservedRunAt.Domain = original.Domain && retirementAt.Domain = original.Domain && releaseAt.Domain = original.Domain && finalAt.Domain = original.Domain) "same-original-clock-domain"
        check (original.StartedAt <= lease.InitialReturnTick && lease.InitialReturnTick <= callerObservedRunAt.Ticks
               && callerObservedRunAt.Ticks <= retirementAt.Ticks && retirementAt.Ticks <= releaseAt.Ticks && releaseAt.Ticks <= finalAt.Ticks
               && finalAt.Ticks < original.TotalExpiresAt) "actual-run-cleanup-release-inside-original-total-window"
        if early.IsSome then
            check (lease.InitialReturnPending && earlyPending && earlyRefused) "observed-pending-lease-refuses-early-release"
            check (callerObservedRunAt.Ticks <= earlyAt && earlyAt <= retirementAt.Ticks) "actual-early-decision-within-original-order"
        let finalEof = Support.readerLabel final.Stdout.Reader = "eof" && Support.readerLabel final.Stderr.Reader = "eof"
        match case with
        | "missing" ->
            check (not lease.Started && (match final.Launch with Refused d -> d.Code = "missing-executable" | _ -> false)) "confirmed-missing-no-workload"
            check (final.Cleanup.Custody = NoEffect) "missing-no-effect-custody"
        | "prestart-cancel" ->
            check (not lease.Started && final.Launch = NotStarted) "prestart-cancellation-no-launch"
            check (match final.Stop with Requested(CallerCancellation,_) -> true | _ -> false) "prestart-original-stop-request"
            check (final.Cleanup.Custody = NoEffect) "prestart-no-effect-custody"
        | _ ->
            check (original.StartedAt <= lease.HelperStartGateTick && lease.HelperStartGateTick < original.WorkExpiresAt
                   && lease.HelperStartGateTick <= lease.HelperStartedTick) "helper-current-work-gate"
            check (lease.TargetWasUncancelled && lease.HelperStartedTick <= lease.TargetReleaseGateTick
                   && lease.TargetReleaseGateTick <= lease.TargetReleasedTick && lease.TargetReleasedTick < original.WorkExpiresAt) "actual-G-release-current-uncancelled-work-window"
            check (match lease.Generation with Some leader -> leader.ScopeId = identity.InvocationId.ToString("D") && leader.Pid > 0 && leader.BirthToken <> "" | None -> false) "original-observed-leader-generation"
            check (Support.exitLabel final.Exit = "exited:0") "real-leader-exit-zero"
            check finalEof "both-real-readers-eof"
            check (custodyLabel final.Cleanup.Custody = "released-local") "local-custody-separated-from-whole-workload"
            match case with
            | "literal-normal" ->
                let expectedOut = Array.append (Encoding.ASCII.GetBytes("argv/env-ok\n")) [|for i in 0 .. 255 -> byte i|]
                let expectedErr = [|for i in 255 .. -1 .. 0 -> byte i|]
                check (final.Stdout.Prefix.ToArray() = expectedOut && final.Stderr.Prefix.ToArray() = expectedErr) "literal-argv-environment-binary-identity"
                check (final.ChargedTotalBytes = 524L && code final.FirstFailure = "") "normal-byte-ledger-no-failure"
            | "flood" ->
                check (final.Stdout.Totals.Charged = 32768L && final.Stderr.Totals.Charged = 32768L) "dual-flood-charged-once"
                check (final.Stdout.Totals.Retained + final.Stderr.Totals.Retained <= 1280L) "bounded-prefix-tail-capture"
                check (code final.FirstFailure = "output-byte-limit") "actual-overflow-first-cause"
            | "failed-sink" ->
                check (code final.FirstFailure = "sink-write-failed") "actual-disposed-consumer-sink-failure"
                check (final.Stdout.Totals.Charged = 256L && final.Stdout.Totals.Retained = 0L) "failed-stdout-charged-not-retained"
                check (final.Stderr.Totals.Charged = 256L && final.Stderr.Totals.Retained = 256L) "independent-other-stream-full-result"
            | "held-writer" ->
                check (Support.exitLabel initial.Exit = "exited:0") "exit-before-pending-pipe-boundary"
                check (Support.readerLabel initial.Stdout.Reader <> "eof" && Support.readerLabel initial.Stderr.Reader <> "eof") "real-inherited-writer-pending-eof"
                check (code initial.FirstFailure = "") "held-pipe-not-mislabeled-child-deadline"
            | "deadline" ->
                check (Support.exitLabel initial.Exit = "unknown" && code initial.FirstFailure = "work-deadline") "original-deadline-without-exit-proof"
            | "caller-cancel" ->
                check (Support.exitLabel initial.Exit = "unknown" && code initial.FirstFailure = "caller-cancelled") "original-caller-cancellation-without-exit-proof"
            | _ -> invalidOp "case-not-selected"
        let receipt: CaseReceipt =
            { Case = case; InvocationId = identity.InvocationId.ToString("D"); Accepted = failures.Count = 0
              Checks = checks.ToArray(); Failures = failures.ToArray()
              InitialExit = Support.exitLabel initial.Exit; InitialStdoutReader = Support.readerLabel initial.Stdout.Reader; InitialStderrReader = Support.readerLabel initial.Stderr.Reader; InitialPhase = string initial.Phase
              FinalExit = Support.exitLabel final.Exit; FinalStdoutReader = Support.readerLabel final.Stdout.Reader; FinalStderrReader = Support.readerLabel final.Stderr.Reader; FirstCause = code final.FirstFailure
              FirstCauseMessage = final.FirstFailure |> Option.map(fun f -> f.Diagnostic.Message) |> Option.defaultValue ""
              FirstCausePhase = final.FirstFailure |> Option.map(fun f -> string f.Phase) |> Option.defaultValue ""
              FirstCauseAtTicks = final.FirstFailure |> Option.bind(fun f -> f.At) |> Option.map _.Ticks |> Option.defaultValue -1L
              StdoutCharged = final.Stdout.Totals.Charged; StdoutRetained = final.Stdout.Totals.Retained; StderrCharged = final.Stderr.Totals.Charged; StderrRetained = final.Stderr.Totals.Retained
              StdoutPrefixBase64 = Convert.ToBase64String(final.Stdout.Prefix.Span); StderrPrefixBase64 = Convert.ToBase64String(final.Stderr.Prefix.Span)
              MetadataBytesBound = int lease.MetadataBytes; Cleanup = label final.Cleanup.Disposition; Custody = custodyLabel final.Cleanup.Custody
              LocalReleased = released; ObserverErrors = lease.ObserverErrors
              ClockDomain = original.Domain.Id.ToString("D"); ClockFrequency = original.Domain.Frequency
              StartedAtTicks = original.StartedAt; WorkExpiresAtTicks = original.WorkExpiresAt; TotalExpiresAtTicks = original.TotalExpiresAt
              RunReturnAtTicks = lease.InitialReturnTick; CallerObservedRunAtTicks = callerObservedRunAt.Ticks; RetirementReturnedAtTicks = retirementAt.Ticks
              ReleaseDecisionAtTicks = releaseAt.Ticks; FinalObservedAtTicks = finalAt.Ticks
              HelperStartGateAtTicks = lease.HelperStartGateTick; HelperStartedAtTicks = lease.HelperStartedTick
              TargetReleaseGateAtTicks = lease.TargetReleaseGateTick; TargetReleasedAtTicks = lease.TargetReleasedTick; TargetWasUncancelled = lease.TargetWasUncancelled
              LeaderPid = lease.Generation |> Option.map(fun leader -> leader.Pid) |> Option.defaultValue 0
              LeaderBirth = lease.Generation |> Option.map(fun leader -> leader.BirthToken) |> Option.defaultValue ""
              LeaderScope = lease.Generation |> Option.map(fun leader -> leader.ScopeId) |> Option.defaultValue ""
              InitialLocalPending = lease.InitialReturnPending; EarlyReleaseProbed = early.IsSome; EarlyDecisionAtTicks = earlyAt
              EarlyLocalPending = earlyPending; EarlyReleaseRefused = earlyRefused
              WorkStopAtTicks = match initial.Stop with Requested(_,at) -> at.Ticks | _ -> -1L
              RunAfterWorkTicks = max 0L (lease.InitialReturnTick - original.WorkExpiresAt) }
        receipt

    let private runProductionCase python fixture work source profile profileDigest owner total reserve case =
        let identity: OperationIdentity =
            { InvocationId = Guid.NewGuid(); Execution = None; LaunchOrdinal = 0UL
              SourceDigest = source; ProfileId = profile; ProfileDigest = profileDigest }
        let session,clock,budget = ProductionFixture.prepare (command python fixture work case) identity owner total reserve
        use cancellation = new CancellationTokenSource()
        if case = "prestart-cancel" then cancellation.Cancel()
        elif case = "caller-cancel" then cancellation.CancelAfter(TimeSpan.FromMilliseconds 100.)
        let initial = session.RunAsync(cancellation.Token).GetAwaiter().GetResult()
        let runAt = clock.Now()
        let earlyRelease =
            if case = "held-writer" then Some(session.TryRelease()) else None
        // Observe is a current snapshot, not a hidden cleanup wait. The existing owner
        // explicitly retains the session and spends only this original remaining total.
        let mutable observed = session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
        let mutable settled = match observed.Cleanup.LocalTasks with Known(Settled,_) -> true | _ -> false
        while not settled do
            let delay = Budget.clip (clock.Now()) Cleanup (TimeSpan.FromMilliseconds 5.) budget |> Support.ok
            Support.require (delay > TimeSpan.Zero) "production-original-cleanup-budget-expired"
            System.Threading.Tasks.Task.Delay(delay).GetAwaiter().GetResult()
            observed <- session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
            settled <- match observed.Cleanup.LocalTasks with Known(Settled,_) -> true | _ -> false
        let observedAt = clock.Now()
        let decision = session.TryRelease()
        let releaseAt = clock.Now()
        let released = match decision with ResourcesReleased -> true | _ -> false
        let final = session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
        let finalAt = clock.Now()
        let original = Budget.describe budget
        let checks = ResizeArray<string>()
        let failures = ResizeArray<string>()
        let check condition name = if condition then checks.Add name else failures.Add name
        let eof = Support.readerLabel final.Stdout.Reader = "eof" && Support.readerLabel final.Stderr.Reader = "eof"
        check released "actual-production-local-release"
        check (initial.FirstFailure = observed.FirstFailure && observed.FirstFailure = final.FirstFailure) "actual-production-first-cause-preserved"
        check (Budget.describe final.OriginalBudget = original) "actual-production-original-budget-preserved"
        check (runAt.Domain = original.Domain && observedAt.Domain = original.Domain && releaseAt.Domain = original.Domain && finalAt.Domain = original.Domain
               && original.StartedAt <= runAt.Ticks && runAt.Ticks <= observedAt.Ticks && observedAt.Ticks <= releaseAt.Ticks
               && releaseAt.Ticks <= finalAt.Ticks && finalAt.Ticks < original.TotalExpiresAt) "actual-production-fixed-total-window"
        match case with
        | "missing" ->
            check (match final.Launch with Refused diagnostic -> diagnostic.Code = "missing-executable" | _ -> false) "actual-production-confirmed-missing"
            check (final.Cleanup.Custody = NoEffect) "actual-production-missing-no-effect"
        | "prestart-cancel" ->
            check (final.Launch = NotStarted && final.Cleanup.Custody = NoEffect) "actual-production-pre-cancel-zero-launch"
            check (match final.Stop with Requested(CallerCancellation,_) -> true | _ -> false) "actual-production-pre-cancel-stop"
        | _ ->
            check (match final.Launch with Started generation -> generation.Pid > 0 && generation.ScopeId = identity.InvocationId.ToString("D") | _ -> false) "actual-production-retained-process-generation"
            check (match final.Exit with Known(Exited _,_) -> true | _ -> false) "actual-production-leader-exit-observed"
            check eof "actual-production-independent-stream-eof"
            check (final.Cleanup.Disposition = Incomplete && (match final.Cleanup.WorkloadEmpty with Unknown _ -> true | _ -> false)
                   && (match final.Cleanup.OwnedChildrenReaped with Unknown _ -> true | _ -> false)) "actual-production-no-descendant-proof"
            match case with
            | "literal-normal" ->
                let expectedOut = Array.append (Encoding.ASCII.GetBytes("argv/env-ok\n")) [|for i in 0 .. 255 -> byte i|]
                let expectedErr = [|for i in 255 .. -1 .. 0 -> byte i|]
                check (final.Stdout.Prefix.ToArray() = expectedOut && final.Stderr.Prefix.ToArray() = expectedErr) "actual-production-literal-binary-output"
                check (final.FirstFailure.IsNone && Support.exitLabel final.Exit = "exited:0") "actual-production-normal-success"
            | "flood" ->
                check (initial.FirstFailure.IsSome && final.ChargedTotalBytes > 3840L
                       && final.Stdout.Totals.Retained <= 640L && final.Stderr.Totals.Retained <= 640L) "actual-production-overflow-charge-before-retention"
                check (match final.Stop with Requested(OutputLimit,_) -> true | _ -> false) "actual-production-overflow-stop"
            | "held-writer" ->
                check (Support.exitLabel initial.Exit = "exited:0" && (Support.readerLabel initial.Stdout.Reader <> "eof" || Support.readerLabel initial.Stderr.Reader <> "eof")) "actual-production-exit-distinct-from-held-writer"
                check (match earlyRelease with Some(ReleaseRefused _) -> true | _ -> false) "actual-production-held-reader-release-refused"
            | "deadline" -> check (match initial.Stop with Requested(WorkDeadline,_) -> true | _ -> false) "actual-production-deadline-stop"
            | "caller-cancel" -> check (match initial.Stop with Requested(CallerCancellation,_) -> true | _ -> false) "actual-production-caller-stop"
            | _ -> invalidOp "production-case-unselected"
        {| Case = case; InvocationId = identity.InvocationId.ToString("D"); Accepted = failures.Count = 0
           Checks = checks.ToArray(); Failures = failures.ToArray(); LocalReleased = released
           InitialExit = Support.exitLabel initial.Exit; FinalExit = Support.exitLabel final.Exit
           InitialStdoutReader = Support.readerLabel initial.Stdout.Reader; InitialStderrReader = Support.readerLabel initial.Stderr.Reader
           FinalStdoutReader = Support.readerLabel final.Stdout.Reader; FinalStderrReader = Support.readerLabel final.Stderr.Reader
           FirstCause = code final.FirstFailure; ChargedTotalBytes = final.ChargedTotalBytes
           StdoutRetainedBytes = final.Stdout.Totals.Retained; StderrRetainedBytes = final.Stderr.Totals.Retained
           Cleanup = label final.Cleanup.Disposition; Custody = custodyLabel final.Cleanup.Custody
           ClockDomain = original.Domain.Id.ToString("D"); ClockFrequency = original.Domain.Frequency
           StartedAtTicks = original.StartedAt; WorkExpiresAtTicks = original.WorkExpiresAt; TotalExpiresAtTicks = original.TotalExpiresAt
           RunReturnedAtTicks = runAt.Ticks; ObservedAtTicks = observedAt.Ticks; ReleaseDecisionAtTicks = releaseAt.Ticks; FinalObservedAtTicks = finalAt.Ticks |}

    // Separate expected-negative route. The CLR caller remains the reachable
    // owner while the existing independent stage observes this finite receipt.
    // It never signals descendants or mistakes outer cleanup for inner retirement.
    let private runDirectChildNegative (shell: string) (sleep: string) (work: string) (source: string) (profile: string) (profileDigest: string) (owner: OwnerReference) =
        let body = "sleep 30 &\necho $! > child.pid\nwait\n"
        let command: CommandSpec =
            { Executable = shell; Arguments = [ "-c"; body ]; WorkingDirectory = work
              Environment = Replace(Map.ofList [ "HOME","/home/developer"; "PATH",Path.GetDirectoryName sleep; "LC_ALL","C.UTF-8" ])
              Input = Closed
              Capture = { TotalBytes = 4096L; TerminalReserveBytes = 256
                          StdoutPrefixBytes = 512; StdoutTailBytes = 128
                          StderrPrefixBytes = 512; StderrTailBytes = 128
                          ReadChunkBytes = 512; MaximumFrameBytes = 1024; MaximumIncompleteLineBytes = 1024 }
              Capabilities = [ { Capability = BoundedReturnWithRetainedCustody; Required = true } ]
              Resources = { MemoryBytes = None; CpuQuota = None; CpuAffinity = None; MaxKernelTasks = None; MaxOwnedProcesses = None; MaxClrProcesses = None } }
        let identity: OperationIdentity =
            { InvocationId = Guid.NewGuid(); Execution = None; LaunchOrdinal = 0UL
              SourceDigest = source; ProfileId = profile; ProfileDigest = profileDigest }
        let session,clock,budget = ProductionFixture.prepareWithRetirement FS.GG.Execution.Processes.RetirementPolicy.DirectChild command identity owner (TimeSpan.FromMilliseconds 5200.) (TimeSpan.FromMilliseconds 5000.)
        let stat pid =
            let raw = File.ReadAllText(sprintf "/proc/%d/stat" pid)
            let fields = raw.Substring(raw.LastIndexOf(')') + 2).Split(' ',StringSplitOptions.RemoveEmptyEntries)
            Int32.Parse fields[1],Int32.Parse fields[2],Int64.Parse fields[19],fields[0]
        let callerPid = Environment.ProcessId
        let _,callerGroup,callerBirth,_ = stat callerPid
        Support.require (callerGroup = callerPid) "negative-caller-owned-stage-group"
        let mutable running: System.Threading.Tasks.Task<ExecutionResult> option = None
        let mutable childWitness: (int * int64 * int * int64) option = None
        let childFile = Path.Combine(work,"child.pid")
        Support.require (not (File.Exists childFile || Directory.Exists childFile)) "negative-one-use-child-witness-file"
        try
            let actualRun = session.RunAsync(CancellationToken.None)
            running <- Some actualRun
            while childWitness.IsNone do
                let remaining = Budget.remaining (clock.Now()) budget |> Support.ok
                Support.require (not remaining.WorkExpired) "negative-child-witness-before-work-expiry"
                if File.Exists childFile then
                    Support.require (FileInfo(childFile).Length <= 32L && not (File.GetAttributes(childFile).HasFlag(FileAttributes.ReparsePoint))) "negative-child-pid-byte-cap"
                    let childPid = File.ReadAllText(childFile).Trim() |> Int32.Parse
                    let childParent,childGroup,childBirth,state = stat childPid
                    let actual = session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
                    let leaderPid = match actual.Launch with Started generation -> generation.Pid | _ -> invalidOp "negative-leader-not-started"
                    let _,leaderGroup,leaderBirth,_ = stat leaderPid
                    Support.require (childParent = leaderPid && childGroup = callerGroup && leaderGroup = callerGroup && state <> "Z" && state <> "X") "negative-original-child-parent-group"
                    Support.require (Path.GetFullPath(File.ResolveLinkTarget(sprintf "/proc/%d/exe" childPid,true).FullName) = sleep) "negative-original-sleep-executable"
                    childWitness <- Some(childPid,childBirth,leaderPid,leaderBirth)
                else
                    let delay = Budget.clip (clock.Now()) Work (TimeSpan.FromMilliseconds 1.) budget |> Support.ok
                    Support.require (delay > TimeSpan.Zero) "negative-original-work-window"
                    System.Threading.Tasks.Task.Delay(delay).GetAwaiter().GetResult()
            let initial = running.Value.GetAwaiter().GetResult()
            let mutable leaderObserved = session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
            let mutable leaderExited = match leaderObserved.Exit with Known(Exited _,_) -> true | _ -> false
            while not leaderExited do
                let delay = Budget.clip (clock.Now()) Cleanup (TimeSpan.FromMilliseconds 5.) budget |> Support.ok
                Support.require (delay > TimeSpan.Zero) "negative-leader-exit-not-observed-in-original-total"
                System.Threading.Tasks.Task.Delay(delay).GetAwaiter().GetResult()
                leaderObserved <- session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
                leaderExited <- match leaderObserved.Exit with Known(Exited _,_) -> true | _ -> false
            let childPid,childBirth,leaderPid,leaderBirth = childWitness.Value
            let _,childGroup,observedBirth,state = stat childPid
            let release = session.TryRelease()
            let observed = session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
            let original = Budget.describe budget
            let at = clock.Now()
            Support.require (observedBirth = childBirth && childGroup = callerGroup && state <> "Z" && state <> "X") "negative-child-still-executing-before-outer-cleanup"
            Support.require (match observed.Exit with Known(Exited _,_) -> true | _ -> false) "negative-leader-exit-observed"
            Support.require (match initial.Stop with Requested(WorkDeadline,_) -> true | _ -> false) "negative-original-work-deadline"
            Support.require (match release with ReleaseRefused _ -> true | _ -> false) "negative-local-release-refused"
            Support.require (match observed.Cleanup.LocalTasks with Known(Outstanding,_) | Unknown _ -> true | _ -> false) "negative-local-tasks-not-settled"
            Support.require (observed.Cleanup.Disposition <> Complete && (match observed.Cleanup.WorkloadEmpty with Unknown _ -> true | _ -> false)) "negative-no-inner-workload-proof"
            Support.require (initial.FirstFailure = observed.FirstFailure && at.Domain = original.Domain && at.Ticks < original.TotalExpiresAt) "negative-first-cause-original-total"
            Support.require (initial.Stop = observed.Stop && Budget.describe observed.OriginalBudget = original) "negative-stop-and-original-budget-preserved"
            let receipt =
                {| Schema = "fsgg.proc.direct-child-expected-pending/1"; Case = "direct-child-negative"
                   ExpectedDescendantRetirementFailed = true; InnerLocalReleased = false; InnerWorkloadEmpty = "unknown"
                   InnerLocalTasks = (match observed.Cleanup.LocalTasks with Known(Outstanding,_) -> "outstanding" | _ -> "unknown")
                   InnerCleanup = label observed.Cleanup.Disposition; InnerCustody = custodyLabel observed.Cleanup.Custody
                   InvocationId = identity.InvocationId.ToString("D"); SourceDigest = source; ProfileDigest = profileDigest
                   CallerPid = callerPid; CallerBirth = callerBirth; OwnedGroup = callerGroup
                   ChildPid = childPid; ChildBirth = childBirth; ChildObservedState = state
                   LeaderPid = leaderPid; LeaderBirth = leaderBirth; OriginalChildParent = leaderPid
                   InitialExit = Support.exitLabel initial.Exit; ObservedLeaderExit = Support.exitLabel observed.Exit
                   InitialFirstCause = code initial.FirstFailure; FirstCause = code observed.FirstFailure
                   FirstFailurePreserved = (initial.FirstFailure = observed.FirstFailure)
                   InitialStopReason = (match initial.Stop with Requested(WorkDeadline,_) -> "work-deadline" | _ -> "unexpected")
                   ObservedStopReason = (match observed.Stop with Requested(WorkDeadline,_) -> "work-deadline" | _ -> "unexpected")
                   InitialStopAtTicks = (match initial.Stop with Requested(_,stamp) -> stamp.Ticks | _ -> -1L)
                   ObservedStopAtTicks = (match observed.Stop with Requested(_,stamp) -> stamp.Ticks | _ -> -1L)
                   InitialStopClockDomain = (match initial.Stop with Requested(_,stamp) -> stamp.Domain.Id.ToString("D") | _ -> "")
                   ObservedStopClockDomain = (match observed.Stop with Requested(_,stamp) -> stamp.Domain.Id.ToString("D") | _ -> "")
                   StartedAtTicks = original.StartedAt
                   WorkExpiresAtTicks = original.WorkExpiresAt; TotalExpiresAtTicks = original.TotalExpiresAt; ReceiptAtTicks = at.Ticks
                   ClockDomain = original.Domain.Id.ToString("D"); ClockFrequency = original.Domain.Frequency
                   Scope = "Inner DirectChild expected nonretirement only; caller retains session/task. Outer retirement receipt required separately." |}
            let raw = JsonSerializer.SerializeToUtf8Bytes receipt
            Support.require (raw.Length <= 8192) "negative-finite-pending-receipt"
            let pending = Path.Combine(work,"expected-pending.json")
            Support.require (not (File.Exists pending)) "negative-one-use-pending-receipt"
            let temporary = pending + ".tmp"
            Support.require (not (File.Exists temporary)) "negative-one-use-temporary-receipt"
            use output = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)
            output.Write raw
            output.Flush(true)
            output.Dispose()
            File.Move(temporary,pending)
            // The existing stage owner consumes the receipt while this caller is alive.
            // If it does not, fail at the original total; never await natural sleep30.
            while true do
                let delay = Budget.clip (clock.Now()) Cleanup (TimeSpan.FromMilliseconds 5.) budget |> Support.ok
                Support.require (delay > TimeSpan.Zero) "negative-outer-owner-did-not-retire-within-original-total"
                session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult() |> ignore
                GC.KeepAlive session
                GC.KeepAlive running
                System.Threading.Tasks.Task.Delay(delay).GetAwaiter().GetResult()
        with originalError ->
            // Preserve failures without dropping the live session/task. The outer
            // poll hook consumes a failure receipt or its original stage cutoff.
            originalError.Data["RetainedNegativeSession"] <- session
            originalError.Data["RetainedNegativeRunTask"] <- running
            try
                let original = Budget.describe budget
                let raw = JsonSerializer.SerializeToUtf8Bytes
                              {| Schema = "fsgg.proc.direct-child-negative-failure/1"
                                 CallerPid = callerPid; CallerBirth = callerBirth; OwnedGroup = callerGroup
                                 SourceDigest = source; ProfileDigest = profileDigest
                                 OriginalFailure = originalError.GetType().Name + ": " + originalError.Message
                                 StartedAtTicks = original.StartedAt; TotalExpiresAtTicks = original.TotalExpiresAt
                                 ChildWitness = childWitness |}
                Support.require (raw.Length <= 8192) "negative-finite-failure-receipt"
                let target = Path.Combine(work,"negative-failure.json")
                let temporary = target + ".tmp"
                File.WriteAllBytes(temporary,raw)
                File.Move(temporary,target)
            with _ -> () // Missing receipt never qualifies; original outer cutoff still owns retirement.
            let mutable remaining = Budget.remaining (clock.Now()) budget |> Support.ok
            while not remaining.TotalExpired do
                let delay = Budget.clip (clock.Now()) Cleanup (TimeSpan.FromMilliseconds 5.) budget |> Support.ok
                if delay > TimeSpan.Zero then System.Threading.Tasks.Task.Delay(delay).GetAwaiter().GetResult()
                GC.KeepAlive session
                GC.KeepAlive running
                remaining <- Budget.remaining (clock.Now()) budget |> Support.ok
            raise originalError

    [<EntryPoint>]
    let main (argv: string array) =
        let receipts = ResizeArray<CaseReceipt>()
        let productionReceipts = ResizeArray<_>()
        let mutable production = false
        let mutable originalFailure = ""
        let mutable reportFailure = ""
        let mutable output: string option = None
        let mutable stage = "preflight"
        let mutable controls: string array = [||]
        try
            Support.require (argv.Length = 2) "descriptor-and-digest-required"
            Support.require (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("HOME") = "/home/developer") "literal-home-linux-profile"
            let descriptorPath = Path.GetFullPath argv[0]
            Support.require (FileInfo(descriptorPath).Length <= 2097152L && hash descriptorPath = argv[1]) "descriptor-digest-bound"
            use document = JsonDocument.Parse(File.ReadAllBytes descriptorPath)
            let root = document.RootElement
            let schema = text root "schema"
            production <- schema = "fsgg.proc.dot3-B-native/1"
            Support.require ((production || schema = "fsgg.proc.dot2-C-native/1") && flag root "operationAdmitted" && flag root "sourceAccepted") "native-source-admission-required"
            let selected = root.GetProperty("cases").EnumerateArray() |> Seq.map (fun value -> value.GetString() |> Option.ofObj |> Option.defaultWith(fun () -> invalidOp "null-case")) |> Seq.toList
            Support.require (not selected.IsEmpty && selected.Length <= cases.Length && (Set.ofList selected).Count = selected.Length) "case-population"
            let separateNegative = production && selected = [ "direct-child-negative" ]
            Support.require (separateNegative || selected = (cases |> List.filter (fun value -> List.contains value selected))) "frozen-case-order"
            if production then Support.require (not (List.contains "failed-sink" selected)) "production-has-no-test-sink-injection"
            let profile = root.GetProperty("ownerProfile")
            Support.require (flag profile "qualified" && flag profile "retainsAfterCallerExit") "independent-owner-route-unqualified"
            if List.contains "held-writer" selected then Support.require (flag profile "inheritedWriterQualified") "inherited-writer-owner-route-unqualified"
            let qualification = pin (profile.GetProperty("qualificationPin"))
            use accepted = JsonDocument.Parse(File.ReadAllBytes qualification)
            Support.require (flag accepted.RootElement "accepted" && text accepted.RootElement "profileId" = text profile "profileId") "owner-profile-acceptance-join"
            let selfRaw = File.ReadAllText("/proc/self/stat")
            let selfFields = selfRaw.Substring(selfRaw.LastIndexOf(')') + 2).Split(' ',StringSplitOptions.RemoveEmptyEntries)
            let actualParent = Int32.Parse selfFields[1]
            Support.require (actualParent = root.GetProperty("ownerPid").GetInt32() && Support.birth actualParent = text root "ownerBirth") "direct-independent-owner-birth"
            let python = pin(root.GetProperty("pythonPin"))
            let fixture = pin(root.GetProperty("fixturePin"))
            let contracts = pin(root.GetProperty("contractsPin"))
            Support.require (Path.GetFullPath(typeof<ExecutionResult>.Assembly.Location) = contracts) "loaded-contracts-assembly"
            if production then
                let producer = pin(root.GetProperty("producerPin"))
                Support.require (Path.GetFullPath(FS.GG.Execution.Processes.ProcessSessions.createFactory().GetType().Assembly.Location) = producer) "loaded-production-assembly"
            let caller = pin(root.GetProperty("callerPin"))
            Support.require (Path.GetFullPath(Assembly.GetExecutingAssembly().Location) = caller) "loaded-caller-assembly"
            let work = Path.GetFullPath(text root "operationDirectory")
            Support.require (Directory.Exists work && not (File.GetAttributes(work).HasFlag(FileAttributes.ReparsePoint))) "private-operation-directory"
            let resultPath = Path.Combine(work,"case-results.json")
            Support.require (not (File.Exists resultPath)) "one-use-output-absent"
            output <- Some resultPath
            let absent = Path.Combine(work,"intentionally-absent-python")
            Support.require (not (File.Exists absent || Directory.Exists absent)) "missing-fixture-path-not-absent"
            let owner: OwnerReference = { OwnerId = text profile "profileId"; Evidence = { Kind = "qualified-independent-owner"; Reference = qualification; Digest = Some(hash qualification) } }
            let sourceManifest = pin(root.GetProperty("sourceManifestPin"))
            Support.require (FileInfo(sourceManifest).Length <= 2097152L) "source-manifest-bound"
            use sourceDocument = JsonDocument.Parse(File.ReadAllBytes sourceManifest)
            let source = hash sourceManifest
            Support.require (source = text root "sourceDigest" && text sourceDocument.RootElement "revision" = text root "sourceRevision") "selected-source-SHA256-and-revision-join"
            let sourceFiles = sourceDocument.RootElement.GetProperty("files").EnumerateArray() |> Seq.toArray
            if production then
                let expected = root.GetProperty("sourceFileCount").GetInt32()
                Support.require (expected >= 68 && expected <= 128 && sourceFiles.Length = expected) "production-selected-source-population"
            else Support.require (sourceFiles.Length = 68) "selected-source-population"
            for sourceFile in sourceFiles do pin sourceFile |> ignore
            let profileDigest = hash qualification
            stage <- "source-controls"
            controls <- sourceControls python fixture work source owner.OwnerId profileDigest owner
            let mutable continueCases = true
            let total,reserve =
                if production then
                    let caseBudget = root.GetProperty("caseBudget")
                    let totalMs = caseBudget.GetProperty("totalMilliseconds").GetInt32()
                    let reserveMs = caseBudget.GetProperty("cleanupReserveMilliseconds").GetInt32()
                    Support.require (totalMs > reserveMs && totalMs <= 10000 && reserveMs > 0) "production-case-budget-bound"
                    TimeSpan.FromMilliseconds(float totalMs),TimeSpan.FromMilliseconds(float reserveMs)
                else TimeSpan.Zero,TimeSpan.Zero
            if separateNegative then
                Support.require (flag root "expectedPendingNegative" && total = TimeSpan.FromMilliseconds 5200. && reserve = TimeSpan.FromMilliseconds 5000.) "negative-explicit-fixed-profile"
                let shell = pin(root.GetProperty("shellPin"))
                let sleep = pin(root.GetProperty("sleepPin"))
                runDirectChildNegative shell sleep work source owner.OwnerId profileDigest owner
            for case in selected do
                if continueCases then
                    stage <- case
                    if production then
                        let result = runProductionCase python fixture work source owner.OwnerId profileDigest owner total reserve case
                        productionReceipts.Add result
                        if not result.LocalReleased then continueCases <- false
                    else
                        let result = runCase python fixture work source owner.OwnerId profileDigest owner case
                        receipts.Add result
                        // No dependent continuation after unknown custody. Ordinary semantic failure
                        // can be retained while later independent cases run only after proven local release.
                        if not result.LocalReleased || result.ObserverErrors.Length > 0 then continueCases <- false
            Support.require ((if production then productionReceipts.Count else receipts.Count) = selected.Length) "remaining-cases-prerequisite-blocked"
        with error ->
            originalFailure <- stage + ": " + (Support.exceptionDiagnostic "caller-original" error).Message
        let allAccepted =
            originalFailure = "" && (if production then productionReceipts.Count > 0 && (productionReceipts |> Seq.forall(fun result -> result.Accepted))
                                    else receipts.Count > 0 && (receipts |> Seq.forall(fun result -> result.Accepted)))
        try
            let raw =
                if production then
                    JsonSerializer.SerializeToUtf8Bytes {| Schema = "fsgg.proc.dot3-B-case-results/1"; Accepted = allAccepted; Cases = productionReceipts.ToArray(); OriginalFailure = originalFailure; SourceControls = controls
                                                           Scope = "Actual production BCL sessions; caller retains custody; qualified outer owner remains independent; no containment/reaping proof." |}
                else
                    JsonSerializer.SerializeToUtf8Bytes {| Schema = "fsgg.proc.dot2-C-case-results/1"; Accepted = allAccepted; Cases = receipts.ToArray(); OriginalFailure = originalFailure; SourceControls = controls
                                                           Scope = "Test-only actual Contracts observations; external owner custody is separate; no whole-workload containment claim." |}
            Support.require (raw.Length <= 65536) "finite-case-report"
            match output with Some path -> File.WriteAllBytes(path,raw) | None -> ()
            Console.Out.WriteLine(Encoding.UTF8.GetString raw)
        with error ->
            reportFailure <- error.GetType().Name + ": " + error.Message
            try Console.Error.WriteLine("original=" + originalFailure + "; reporting=" + reportFailure) with _ -> ()
        if allAccepted && reportFailure = "" then 0 else 2
