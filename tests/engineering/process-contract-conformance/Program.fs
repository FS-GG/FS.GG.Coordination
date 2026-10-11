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
      Cleanup: string; Custody: string; LocalReleased: bool; ObserverErrors: string array }

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
    let private runCase (python: string) (fixture: string) (work: string) (source: string) (profile: string) (profileDigest: string) (owner: OwnerReference) (case: string) =
        let identity: OperationIdentity = { InvocationId = Guid.NewGuid(); Execution = None; LaunchOrdinal = 0UL
                                           SourceDigest = source; ProfileId = profile; ProfileDigest = profileDigest }
        // Session remains reachable by this caller across bounded Run and finite original cleanup.
        let lease = FixtureSession(command python fixture work case,identity,owner,case = "failed-sink")
        let session = lease :> IProcessSession
        use cancellation = new CancellationTokenSource()
        if case = "prestart-cancel" then cancellation.Cancel()
        elif case = "caller-cancel" then cancellation.CancelAfter(TimeSpan.FromMilliseconds 100.0)
        let initial = session.RunAsync(cancellation.Token).GetAwaiter().GetResult()
        lease.RetireWithinOriginalBudgetAsync().GetAwaiter().GetResult()
        let released = match session.TryRelease() with ResourcesReleased -> true | ReleaseRefused _ -> false
        let final = session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
        let checks = ResizeArray<string>()
        let failures = ResizeArray<string>()
        let check condition name = if condition then checks.Add name else failures.Add name
        check released "local-release-established"
        check (lease.ObserverErrors.Length = 0) "all-contract-observations-admitted"
        check (final.Cleanup.Disposition <> Complete) "no-fabricated-whole-workload-completion"
        check (final.FirstFailure = initial.FirstFailure) "original-first-cause-message-phase-time-preserved"
        check (lease.MetadataBytes <= int64 Validation.limits.ResultMetadataBytes) "actual-metadata-bound"
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
              LocalReleased = released; ObserverErrors = lease.ObserverErrors }
        receipt
    [<EntryPoint>]
    let main (argv: string array) =
        let receipts = ResizeArray<CaseReceipt>()
        let mutable originalFailure = ""
        let mutable reportFailure = ""
        let mutable output: string option = None
        let mutable stage = "preflight"
        try
            Support.require (argv.Length = 2) "descriptor-and-digest-required"
            Support.require (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("HOME") = "/home/developer") "literal-home-linux-profile"
            let descriptorPath = Path.GetFullPath argv[0]
            Support.require (FileInfo(descriptorPath).Length <= 2097152L && hash descriptorPath = argv[1]) "descriptor-digest-bound"
            use document = JsonDocument.Parse(File.ReadAllBytes descriptorPath)
            let root = document.RootElement
            Support.require (text root "schema" = "fsgg.proc.dot2-C-native/1" && flag root "operationAdmitted" && flag root "sourceAccepted") "native-source-admission-required"
            let selected = root.GetProperty("cases").EnumerateArray() |> Seq.map (fun value -> value.GetString() |> Option.ofObj |> Option.defaultWith(fun () -> invalidOp "null-case")) |> Seq.toList
            Support.require (not selected.IsEmpty && selected.Length <= cases.Length && (Set.ofList selected).Count = selected.Length) "case-population"
            Support.require (selected = (cases |> List.filter (fun value -> List.contains value selected))) "frozen-case-order"
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
            let source = text root "sourceDigest"
            let profileDigest = hash qualification
            let mutable continueCases = true
            for case in selected do
                if continueCases then
                    stage <- case
                    let result = runCase python fixture work source owner.OwnerId profileDigest owner case
                    receipts.Add result
                    // No dependent continuation after unknown custody. Ordinary semantic failure
                    // can be retained while later independent cases run only after proven local release.
                    if not result.LocalReleased || result.ObserverErrors.Length > 0 then continueCases <- false
            Support.require (receipts.Count = selected.Length) "remaining-cases-prerequisite-blocked"
        with error ->
            originalFailure <- stage + ": " + (Support.exceptionDiagnostic "caller-original" error).Message
        let allAccepted = originalFailure = "" && receipts.Count > 0 && (receipts |> Seq.forall(fun result -> result.Accepted))
        let body = {| Schema = "fsgg.proc.dot2-C-case-results/1"; Accepted = allAccepted; Cases = receipts.ToArray(); OriginalFailure = originalFailure
                      Scope = "Test-only actual Contracts observations; external owner custody is separate; no whole-workload containment claim." |}
        try
            let raw = JsonSerializer.SerializeToUtf8Bytes(body)
            Support.require (raw.Length <= 65536) "finite-case-report"
            match output with Some path -> File.WriteAllBytes(path,raw) | None -> ()
            Console.Out.WriteLine(Encoding.UTF8.GetString raw)
        with error ->
            reportFailure <- error.GetType().Name + ": " + error.Message
            try Console.Error.WriteLine("original=" + originalFailure + "; reporting=" + reportFailure) with _ -> ()
        if allAccepted && reportFailure = "" then 0 else 2
