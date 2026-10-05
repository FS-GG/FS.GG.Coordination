#nowarn "9" // Fixed-layout Linux statx ABI for the private measurement directory.
open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

[<Struct; StructLayout(LayoutKind.Explicit, Size = 256)>]
type private CalibrationDirectoryStat =
    [<FieldOffset(0)>]
    val mutable Mask: uint32

    [<FieldOffset(28)>]
    val mutable Mode: uint16

    [<FieldOffset(32)>]
    val mutable Inode: uint64

    [<FieldOffset(136)>]
    val mutable DeviceMajor: uint32

    [<FieldOffset(140)>]
    val mutable DeviceMinor: uint32

module private CalibrationDirectoryNative =
    [<DllImport("libc", SetLastError = true, EntryPoint = "statx")>]
    extern int statx(int dirfd, string path, int flags, uint32 mask, CalibrationDirectoryStat& result)

    [<DllImport("libc", SetLastError = true, EntryPoint = "open")>]
    extern int openDirectory(string path, int flags)

    [<DllImport("libc", SetLastError = true, EntryPoint = "openat")>]
    extern int openChild(int descriptor, string path, int flags)

    [<DllImport("libc", SetLastError = true, EntryPoint = "mkdirat")>]
    extern int createChild(int descriptor, string path, uint32 mode)

type private MeasurementDirectory =
    {
        NamedPath: string
        HeldPath: string
        Handle: SafeFileHandle
        Identity: string
        ParentPath: string
        ParentHandle: SafeFileHandle
        ParentIdentity: string
    }

module private MeasurementDirectory =
    let directoryIdentity descriptor path flags =
        let mutable stat = Unchecked.defaultof<CalibrationDirectoryStat>

        if
            CalibrationDirectoryNative.statx (descriptor, path, flags, 0x7ffu, &stat) <> 0
            || stat.Mask &&& 0x100u = 0u
            || stat.Mode &&& 0xf000us <> 0x4000us
        then
            invalidOp "MEASUREMENT-DIRECTORY-IDENTITY: unavailable"

        $"{stat.DeviceMajor}:{stat.DeviceMinor}:{stat.Inode}"

    let rec rejectLinkedAncestors (path: string) =
        let directory = DirectoryInfo path

        if
            not directory.Exists
            || not (isNull directory.LinkTarget)
            || (File.GetAttributes path &&& FileAttributes.ReparsePoint)
               <> enum<FileAttributes> 0
        then
            invalidOp "MEASUREMENT-DIRECTORY-LINK: refused"

        if not (isNull directory.Parent) then
            rejectLinkedAncestors directory.Parent.FullName

    let descriptor (handle: SafeFileHandle) = handle.DangerousGetHandle().ToInt32()

    let verify custody =
        rejectLinkedAncestors custody.ParentPath

        if
            directoryIdentity -100 custody.ParentPath 0x100 <> custody.ParentIdentity
            || directoryIdentity -100 custody.NamedPath 0x100 <> custody.Identity
        then
            invalidOp "MEASUREMENT-DIRECTORY-INVALIDATED: refused"

    let create root path =
        if not (OperatingSystem.IsLinux()) then
            invalidOp "MEASUREMENT-DIRECTORY-PLATFORM: unavailable"

        let parent = Path.GetDirectoryName(path: string)
        let name = Path.GetFileName path
        rejectLinkedAncestors parent
        let parentFd = CalibrationDirectoryNative.openDirectory (parent, 0xb0000) // CLOEXEC | NOFOLLOW | DIRECTORY

        if parentFd < 0 then
            invalidOp "MEASUREMENT-DIRECTORY-PARENT: unavailable"

        let parentHandle = new SafeFileHandle(nativeint parentFd, true)

        try
            let parentIdentity = directoryIdentity parentFd "" 0x1000

            if directoryIdentity -100 parent 0x100 <> parentIdentity then
                invalidOp "MEASUREMENT-DIRECTORY-PARENT: changed"

            rejectLinkedAncestors parent

            let actualParent =
                File.ResolveLinkTarget($"/proc/self/fd/{parentFd}", true).FullName

            let actualRelative = Path.GetRelativePath(root, actualParent)

            if
                actualRelative = "."
                || not (actualRelative.StartsWith(".." + string Path.DirectorySeparatorChar, StringComparison.Ordinal))
            then
                invalidOp "MEASUREMENT-DIRECTORY-PARENT: inside-source"

            if CalibrationDirectoryNative.createChild (parentFd, name, 0x1c0u) <> 0 then
                invalidOp "MEASUREMENT-DIRECTORY-CREATE: requires-exclusive-fresh-output"

            let outputFd = CalibrationDirectoryNative.openChild (parentFd, name, 0xb0000)

            if outputFd < 0 then
                invalidOp "MEASUREMENT-DIRECTORY-OPEN: unavailable"

            let handle = new SafeFileHandle(nativeint outputFd, true)

            try
                let custody =
                    {
                        NamedPath = path
                        HeldPath = $"/proc/self/fd/{outputFd}"
                        Handle = handle
                        Identity = directoryIdentity outputFd "" 0x1000
                        ParentPath = parent
                        ParentHandle = parentHandle
                        ParentIdentity = parentIdentity
                    }

                verify custody
                custody
            with _ ->
                handle.Dispose()
                reraise ()
        with _ ->
            parentHandle.Dispose()
            reraise ()

type FormalMeasurement =
    {
        mutable StateCount: int
        mutable TransitionCount: int
        mutable SampleCount: int
        mutable ElapsedMs: int64
        mutable AllAttemptElapsedMs: int64
        mutable PeakMiB: int
        mutable ArtifactBytes: int64
    }

let expectedPackage = "FS.GG.SDD.Artifacts/2.1.0"
let expectedProfile = "fsgg-quint-profile/2"

let expectedToolchain =
    "79b32dacc5bb150e23c4017eef16f3f688cde062441583d5ea1ffa5cc9e62486"

let expectedQuint =
    "939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f"

let expectedLmt = "37e0b0365c2641edce40b48605471f61fa12e97c3e2376152f0e849abdc31f10"

let expectedSource =
    "ab114cbfd7738dd1568ce2da3250b7b141b7d5759169bd9d9fb23d3165bdd354"

let expectedContract =
    "791c65eacbc4ef93484673ed6c40d3e8ca21fd75f34371e58c9f954f757c1a64"

let expectedBehavior =
    "9d2581c99badfb89acdb359acceb0a1ba560322939d47944e58aa1fdcf10e05b"

let expectedSourceVersion = "fsgg.quint.literate-source/1"
let expectedExtractorVersion = "quint-specification-v1@FS.GG.SDD.Artifacts/2.1.0"
let expectedQuintVersion = "sha256:" + expectedQuint
let expectedSchemaVersion = "fsgg.quint.compiled-contract/v2"

let expectedApalacheJar =
    "4753c0ebb2cbb266e2c6ac19ab5ca3827d726cc80fd1fc5d7c1eeb64736cd60b"

let qualificationClock = Stopwatch.StartNew()
let mutable externalProcessCount = 0
let mutable quintProcessCount = 0
let mutable quintRejectedProcessCount = 0
let mutable apalacheVerifyInvocationCount = 0
let mutable verifiedPositiveInvariantCount = 0
let mutable q1Outcome = "not-run"
let mutable q2Outcome = "not-run"
let mutable currentPhase = "q1"
let mutable preparationDurationMs = 0L
let mutable preparationDigest: string option = None
let mutable failureReceiptWriter: (string -> string -> unit) option = None
let formalCounterexampleReceipts = ResizeArray<string * string * string * string>()

let actualInvocationInventory =
    System.Collections.Concurrent.ConcurrentDictionary<string, int>()

let expectedInvocationInventory =
    System.Collections.Generic.Dictionary<string, int>()

let mutable formalSafeSteps = Set.empty<string * string>
let mutable formalInvalidSteps = Set.empty<string * string>
let mutable formalRemovedSteps = Set.empty<string * string>
let mutable formalInventoryReady = false
let mutable apalacheEndpointOrdinal = 0
let mutable apalacheStartupRetryCount = 0
let mutable apalacheVerifyStartupRetryCount = 0
let mutable apalacheReflectionRetryCount = 0
let mutable apalacheEarlyLifecycleRetryCount = 0
// Hosted Apalache has produced the recognized parser-only lifecycle exit twice in
// succession. Keep the recovery finite while allowing one further isolated start.
let maxApalacheStartupRetries = 2

let apalacheEndpointBase =
    match Environment.GetEnvironmentVariable "FSGG_APALACHE_PORT_BASE" with
    | null
    | "" -> 18820
    | value ->
        match Int32.TryParse value with
        | true, port when port >= 1024 && port <= 65400 -> port
        | _ -> failwith "FSGG_APALACHE_PORT_BASE must be an integer from 1024 through 65400"

let apalacheEndpoints =
    System.Collections.Concurrent.ConcurrentDictionary<int, byte>()

let incrementInvocation label =
    actualInvocationInventory.AddOrUpdate(label, 1, fun _ count -> count + 1)
    |> ignore

let argumentValue name arguments =
    arguments
    |> List.tryFindIndex ((=) name)
    |> Option.bind (fun index -> arguments |> List.tryItem (index + 1))

let classifyInvocation isQuint arguments =
    if
        isQuint
        && (argumentValue "--out-itf" arguments
            |> Option.exists (fun path -> path.Contains("preflight-interaction", StringComparison.Ordinal)))
    then
        "quint/preflight-interaction"
    elif
        isQuint
        && (argumentValue "--main" arguments
            |> Option.exists (fun main -> main.StartsWith("Preflight", StringComparison.Ordinal)))
    then
        "quint/preflight-sampling"
    elif not isQuint then
        "external/base"
    elif not formalInventoryReady then
        if List.tryHead arguments = Some "verify" then
            "quint/base-verify"
        else
            "quint/base-nonverify"
    else
        let command = List.tryHead arguments |> Option.defaultValue "missing-command"
        let main = argumentValue "--main" arguments |> Option.defaultValue ""
        let step = argumentValue "--step" arguments

        let isFormal memberSet =
            step |> Option.exists (fun value -> Set.contains (main, value) memberSet)

        // Measurement state roots emit --out-itf; test roots and ordinary roots emit --out.
        // Require one actual root-artifacts directory component, not a path substring.
        let rootOutput =
            [ "--out"; "--out-itf" ]
            |> List.collect (fun flag ->
                arguments
                |> List.indexed
                |> List.choose (fun (index, value) ->
                    if value = flag then List.tryItem (index + 1) arguments else None))
            |> function
                | [ path ] ->
                    try
                        Path.IsPathRooted path
                        && Path.GetFileName(Path.GetDirectoryName path) = "root-artifacts"
                        && Path.GetExtension path = ".json"
                    with :? ArgumentException -> false
                | _ -> false

        if (command = "run" || command = "test") && rootOutput then
            "quint/selected-root"
        elif command = "verify" && isFormal formalSafeSteps then
            "quint/formal-temporal"
        elif command = "verify" && isFormal formalRemovedSteps then
            "quint/formal-counterexample-temporal"
        elif command = "run" && isFormal formalInvalidSteps then
            "quint/formal-safety-mutant"
        elif command = "run" && isFormal formalRemovedSteps then
            "quint/formal-counterexample-projection"
        elif command = "run" && isFormal formalSafeSteps then
            "quint/formal-simulation"
        elif command = "verify" then
            "quint/base-verify"
        else
            "quint/base-nonverify"

let positiveInvariants =
    [
        "acceptedVocabularyIsQualified"
        "acceptedAuthoritiesAreQualified"
        "humanIntentIsObservationIndependent"
        "lifecycleStatusIsDerived"
        "nativeRelationEdgesAreValid"
        "relationChangesPreserveUnrelatedEdges"
        "protocolEnvelopesAreValidAndOrdered"
        "durableProtocolCheckpointsArePreserved"
    ]

let fail code detail =
    eprintfn "CANONICAL_QUINT_PROTOCOL_RED code=%s detail=%s" code detail

    match failureReceiptWriter with
    | Some writer ->
        try
            writer code detail
        with exceptionValue ->
            eprintfn "CANONICAL_QUINT_RECEIPT_RED code=WRITE detail=%s" exceptionValue.Message
    | None -> ()

    exit 1

let sha256 path =
    File.ReadAllBytes path
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let sha256Text (value: string) =
    value
    |> Encoding.UTF8.GetBytes
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let normalizeSupportedAuthoring (value: string) =
    let lf =
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal)

    let normalizedFences =
        lf.Split('\n')
        |> Array.map (fun line ->
            let leadingSpaces = line.Length - line.TrimStart(' ').Length

            if
                leadingSpaces > 0
                && leadingSpaces <= 3
                && line.Substring(leadingSpaces).StartsWith("```", StringComparison.Ordinal)
            then
                line.Substring(leadingSpaces)
            else
                line)
        |> String.concat "\n"

    normalizedFences.Replace("\n```\n\n```quint protocol.qnt +=\n", "\n", StringComparison.Ordinal)

let requireFile code path =
    if not (File.Exists path) then
        fail code path

let isolateApalacheEndpoint isQuint arguments =
    let command = List.tryHead arguments

    let usesApalacheServer =
        isQuint
        && (command = Some "verify"
            || (command = Some "compile" && argumentValue "--target" arguments = Some "tlaplus"))

    if not usesApalacheServer || List.contains "--server-endpoint" arguments then
        arguments, None
    else
        let ordinal = Interlocked.Increment(&apalacheEndpointOrdinal) - 1
        let port = apalacheEndpointBase + ordinal

        if port > 65535 then
            fail "APALACHE-ENDPOINT-RANGE" (string port)

        if not (apalacheEndpoints.TryAdd(port, 0uy)) then
            fail "APALACHE-ENDPOINT-DUPLICATE" (string port)

        arguments @ [ "--server-endpoint"; $"localhost:%d{port}" ], Some port

let classifyTransientApalacheStartupFailure exitCode (output: string) (error: string) =
    let diagnostic = output + "\n" + error

    let reflectionDeadline =
        diagnostic.Contains("Error querying reflection endpoint", StringComparison.Ordinal)
        && diagnostic.Contains("DEADLINE_EXCEEDED", StringComparison.Ordinal)

    let launchedAndStopped =
        diagnostic.Contains("No running Apalache server found, launching", StringComparison.Ordinal)
        && diagnostic.Contains("Started Apalache server on pid=", StringComparison.Ordinal)
        && diagnostic.Contains("Shutting down Apalache server", StringComparison.Ordinal)

    let successfulExitAfterParserWithoutResult =
        exitCode = 0
        && launchedAndStopped
        && diagnostic.Contains("PASS #0: SanyParser", StringComparison.Ordinal)
        && not (diagnostic.Contains("states generated", StringComparison.Ordinal))
        && not (diagnostic.Contains("Invariant violated", StringComparison.Ordinal))

    let executionTimeout =
        exitCode = 124
        && diagnostic.Contains("APALACHE_EXECUTION_TIMEOUT", StringComparison.Ordinal)

    let earlyLifecycleExit =
        executionTimeout
        || (launchedAndStopped
            && (Regex.IsMatch(diagnostic, "(?m)^error: error\\s*$")
                || successfulExitAfterParserWithoutResult))

    if reflectionDeadline then Some "reflection-deadline"
    elif earlyLifecycleExit then Some "early-lifecycle-exit"
    else None

let recordApalacheStartupRetry commandKind failureClass =
    match commandKind with
    | "verify" -> Interlocked.Increment(&apalacheVerifyStartupRetryCount) |> ignore
    | "compile" -> ()
    | _ -> fail "APALACHE-STARTUP-RETRY-COMMAND" commandKind

    let retryCount = Interlocked.Increment(&apalacheStartupRetryCount)

    match failureClass with
    | "reflection-deadline" -> Interlocked.Increment(&apalacheReflectionRetryCount) |> ignore
    | "early-lifecycle-exit" -> Interlocked.Increment(&apalacheEarlyLifecycleRetryCount) |> ignore
    | _ -> fail "APALACHE-STARTUP-RETRY-CLASS" failureClass

    eprintfn "APALACHE_STARTUP_RETRY count=%d command=%s class=%s" retryCount commandKind failureClass

let private processTreeRssBytes rootPid =
    let rec collect visited pid =
        if Set.contains pid visited then
            0L, visited
        else
            let visited' = Set.add pid visited
            let status = $"/proc/%d{pid}/status"

            let rss =
                try
                    File.ReadLines status
                    |> Seq.tryFind (fun line -> line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    |> Option.map (fun line ->
                        let fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        Int64.Parse(fields[1]) * 1024L)
                    |> Option.defaultValue 0L
                with _ ->
                    0L

            let childrenPath = $"/proc/%d{pid}/task/%d{pid}/children"

            let children =
                try
                    File.ReadAllText(childrenPath).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map Int32.Parse
                    |> Array.toList
                with _ ->
                    []

            children
            |> List.fold
                (fun (total, seen) childPid ->
                    let childTotal, nextSeen = collect seen childPid
                    total + childTotal, nextSeen)
                (rss, visited')

    collect Set.empty rootPid |> fst

// Nested FSI calls use the host and FSI bytes actually running this validator.
// This avoids a second SDK resolution through repository global.json.
let private observedDotnetHost = Environment.ProcessPath
let private observedFsiDll = System.Reflection.Assembly.GetEntryAssembly().Location
let private observedFsharpDirectory = Path.GetDirectoryName observedFsiDll

let private compilerFiles =
    [
        observedDotnetHost
        observedFsiDll
        typeof<option<int>>.Assembly.Location
        Path.Combine(observedFsharpDirectory, "FSharp.Compiler.Service.dll")
        Path.Combine(observedFsharpDirectory, "FSharp.Compiler.Interactive.Settings.dll")
        Path.Combine(Directory.GetParent(observedFsharpDirectory).FullName, "dotnet.dll")
        typeof<obj>.Assembly.Location
    ]
    |> List.distinct
    |> List.map (fun path -> {| path = path; sha256 = sha256 path |})

let private compilerIdentitySha256 =
    compilerFiles
    |> List.map (fun file -> file.path + "|" + file.sha256)
    |> String.concat "\n"
    |> sha256Text

let private resolveFsiInvocation executable arguments =
    match executable, arguments with
    | "dotnet", "fsi" :: tail -> observedDotnetHost, observedFsiDll :: tail
    | _ -> executable, arguments

type private PhysicalAttemptObservation =
    {
        LogicalId: string
        Ordinal: int
        Scope: string option
        Label: string
        Classification: string option
        ElapsedMs: int64 option
        PeakMiB: int option
        ExitCode: int option
        TimedOut: bool option
        Terminal: bool option
        Completed: bool
        Pid: int option
        TimeoutMs: int option
        OutputBytes: int64 option
        OutputSha256: string option
        SemanticOutcome: string option
    }

let mutable private physicalScope: string option = None
let mutable private logicalSequence = 0
let private physicalAttempts = ResizeArray<PhysicalAttemptObservation>()

let mutable private physicalObserver: (PhysicalAttemptObservation -> unit) option =
    None

let private observePhysical row =
    match physicalObserver with
    | Some observer -> observer row
    | None -> ()

// Terminal work and all physical work are different accounting projections.
// Retrying never discards failed-attempt duration or memory.
let private terminalSemantics
    (label: string)
    (scope: string option)
    code
    timedOut
    (classification: string option)
    (output: string)
    (error: string)
    =
    if timedOut || classification.IsSome then
        None
    elif code = 0 then
        Some "positive-complete"
    elif code = 1 then
        let text = output + "\n" + error

        if
            label = "quint/formal-counterexample-temporal"
            && text.Contains("Temporal properties were violated", StringComparison.Ordinal)
            && text.Contains("Error: The following behavior constitutes a counter-example:", StringComparison.Ordinal)
            && text.Contains("Finished checking temporal properties", StringComparison.Ordinal)
        then
            Some "temporal-counterexample"
        elif
            (label = "quint/formal-safety-mutant"
             || label = "quint/formal-counterexample-projection"
             || (scope
                 |> Option.exists (fun value ->
                     value.StartsWith("sampling/", StringComparison.Ordinal) && value.Contains(":"))))
            && text.Contains("Invariant violated", StringComparison.Ordinal)
        then
            Some "invariant-counterexample"
        else
            None
    else
        None

let private retryObserved eligible logicalId scope label timeoutMs invoke =
    let mutable ordinal = 0
    let mutable greatestPeak = 0

    let rec attempt () =
        ordinal <- ordinal + 1

        if physicalAttempts.Count >= 4096 then
            fail "PHYSICAL-INVENTORY-BOUND" logicalId

        observePhysical
            {
                LogicalId = logicalId
                Ordinal = ordinal
                Scope = scope
                Label = label
                Classification = None
                ElapsedMs = None
                PeakMiB = None
                ExitCode = None
                TimedOut = None
                Terminal = None
                Completed = false
                Pid = None
                TimeoutMs = timeoutMs
                OutputBytes = None
                OutputSha256 = None
                SemanticOutcome = None
            }

        let code, output, error, elapsed, peak, timedOut, pid = invoke ordinal

        let classification =
            if eligible then
                classifyTransientApalacheStartupFailure code output error
            else
                None

        let retry = classification.IsSome && ordinal <= maxApalacheStartupRetries
        greatestPeak <- max greatestPeak peak

        let row =
            {
                LogicalId = logicalId
                Ordinal = ordinal
                Scope = scope
                Label = label
                Classification = classification
                ElapsedMs = Some elapsed
                PeakMiB = (if peak > 0 then Some peak else None)
                ExitCode = Some code
                TimedOut = Some timedOut
                Terminal = Some(not retry)
                Completed = true
                Pid = Some pid
                TimeoutMs = timeoutMs
                OutputBytes = Some(int64 (Encoding.UTF8.GetByteCount(output) + Encoding.UTF8.GetByteCount(error)))
                OutputSha256 = Some(sha256Text (output + "\n" + error))
                SemanticOutcome = terminalSemantics label scope code timedOut classification output error
            }

        physicalAttempts.Add row
        observePhysical row

        if retry then
            recordApalacheStartupRetry "verify" classification.Value
            attempt ()
        else
            code, output, error, elapsed, greatestPeak

    attempt ()

let private runWithBudget timeoutMs workingDirectory (executable: string) arguments environment =
    Interlocked.Increment(&externalProcessCount) |> ignore
    let isQuint = executable.EndsWith(expectedQuint, StringComparison.Ordinal)
    let label = classifyInvocation isQuint arguments
    incrementInvocation label
    let logicalId = $"call-{Interlocked.Increment(&logicalSequence):D6}"

    if isQuint then
        Interlocked.Increment(&quintProcessCount) |> ignore

        if List.tryHead arguments = Some "verify" then
            Interlocked.Increment(&apalacheVerifyInvocationCount) |> ignore

    let invoke _ =
        let executable, arguments = resolveFsiInvocation executable arguments
        let clock = Stopwatch.StartNew()
        let isolatedArguments, _ = isolateApalacheEndpoint isQuint arguments
        let info = ProcessStartInfo(executable)
        info.WorkingDirectory <- workingDirectory
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true

        for argument in isolatedArguments do
            info.ArgumentList.Add argument

        for name, value in environment do
            info.Environment[name] <- value

        use child = Process.Start info
        let pid = child.Id
        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()
        let mutable peakBytes = processTreeRssBytes pid
        let mutable timedOut = false

        while not child.HasExited && not timedOut do
            peakBytes <- max peakBytes (processTreeRssBytes pid)

            if
                timeoutMs
                |> Option.exists (fun bound -> clock.ElapsedMilliseconds > int64 bound)
            then
                timedOut <- true

                try
                    child.Kill(true)
                with _ ->
                    ()
            else
                Thread.Sleep 10

        child.WaitForExit()
        peakBytes <- max peakBytes (processTreeRssBytes pid)
        // Waiting for reaping and both redirected streams is actual cleanup work.
        let stdout = output.GetAwaiter().GetResult().Trim()
        let stderr = error.GetAwaiter().GetResult().Trim()
        let actualExit = if timedOut then 124 else child.ExitCode
        child.Dispose()
        let elapsed = clock.ElapsedMilliseconds

        let diagnostic =
            if timedOut then
                $"APALACHE_EXECUTION_TIMEOUT elapsedMs={elapsed} budgetMs={timeoutMs.Value}"
            else
                ""

        actualExit,
        stdout,
        (String.concat "\n" [ stderr; diagnostic ]).Trim(),
        elapsed,
        int (Math.Ceiling(float peakBytes / 1048576.0)),
        timedOut,
        pid

    let result =
        retryObserved (isQuint && List.tryHead arguments = Some "verify") logicalId physicalScope label timeoutMs invoke

    let code, _, _, _, _ = result

    if isQuint && code <> 0 then
        Interlocked.Increment(&quintRejectedProcessCount) |> ignore

    result

let runMeasured timeoutMs workingDirectory executable arguments environment =
    runWithBudget (Some timeoutMs) workingDirectory executable arguments environment

let run workingDirectory (executable: string) arguments environment =
    let timeoutMs =
        if
            executable.EndsWith(expectedQuint, StringComparison.Ordinal)
            && List.tryHead arguments = Some "verify"
        then
            Some 150000
        else
            None

    let code, output, error, _, _ =
        runWithBudget timeoutMs workingDirectory executable arguments environment

    code, output, error

let private acceptedFormalTerminal row =
    let expected =
        match row.Label with
        | "quint/formal-simulation"
        | "quint/formal-temporal" -> Some(0, "positive-complete")
        | "quint/formal-safety-mutant"
        | "quint/formal-counterexample-projection" -> Some(1, "invariant-counterexample")
        | "quint/formal-counterexample-temporal" -> Some(1, "temporal-counterexample")
        | _ -> None

    expected
    |> Option.exists (fun (code, outcome) ->
        row.ExitCode = Some code
        && row.SemanticOutcome = Some outcome
        && row.TimedOut = Some false
        && row.Classification.IsNone)

let private summarizeFormalPhysical allAttemptBudget (physical: PhysicalAttemptObservation array) =
    let groups = physical |> Array.groupBy _.LogicalId

    if groups.Length <> 7 || physical.Length < 7 || physical.Length > 13 then
        Error "inventory"
    elif
        groups
        |> Array.exists (fun (_, rows) ->
            (rows
             |> Array.mapi (fun index row ->
                 row.Ordinal = index + 1
                 && row.Completed
                 && (row.ElapsedMs |> Option.exists (fun value -> value > 0L))
                 && (row.PeakMiB |> Option.exists (fun value -> value > 0))
                 && row.ExitCode.IsSome
                 && row.Terminal.IsSome)
             |> Array.exists not)
            || (rows |> Array.filter (fun row -> row.Terminal = Some true) |> Array.length)
               <> 1
            || rows[rows.Length - 1].Terminal <> Some true
            || (rows
                |> Array.take (rows.Length - 1)
                |> Array.exists (fun row -> row.Classification.IsNone)))
    then
        Error "incomplete-or-unclassified"
    elif
        (physical
         |> Array.filter (fun row -> row.Terminal = Some true)
         |> Array.countBy _.Label
         |> Map.ofArray)
        <> Map.ofList
            [
                ("quint/formal-simulation", 1)
                ("quint/formal-temporal", 1)
                ("quint/formal-safety-mutant", 1)
                ("quint/formal-counterexample-temporal", 2)
                ("quint/formal-counterexample-projection", 2)
            ]
    then
        Error "declared-logical-roles"
    elif
        physical
        |> Array.filter (fun row -> row.Terminal = Some true)
        |> Array.exists (fun row -> not (acceptedFormalTerminal row))
    then
        Error "unexpected-terminal-outcome"
    else
        let terminal =
            physical
            |> Array.filter (fun row -> row.Terminal = Some true)
            |> Array.sumBy (fun row -> row.ElapsedMs.Value)

        let busy = physical |> Array.sumBy (fun row -> row.ElapsedMs.Value)
        let peak = physical |> Array.map (fun row -> row.PeakMiB.Value) |> Array.max

        if terminal > 300000L || busy > allAttemptBudget || peak > 6144 then
            Error "budget"
        else
            Ok(terminal, busy, peak)

// Controlled subprocess fixtures exercise the production wrappers, not native proof.
if fsi.CommandLineArgs |> Array.contains "--exercise-accounting-controls" then
    if fsi.CommandLineArgs |> Array.skip 1 <> [| "--exercise-accounting-controls" |] then
        fail "ACCOUNTING-CONTROL-MIXTURE" "requires only focused switch"

    let scratch = Directory.CreateTempSubdirectory("fsgg-physical-accounting-controls-")

    let check name condition =
        if not condition then
            fail "ACCOUNTING-CONTROL" name

    try
        let executable = Path.Combine(scratch.FullName, expectedQuint)
        File.CreateSymbolicLink(executable, "/usr/bin/python3") |> ignore

        File.WriteAllText(
            Path.Combine(scratch.FullName, "verify"),
            """import sys,time,pathlib
p=pathlib.Path(sys.argv[1]); n=int(p.read_text()) if p.exists() else 0; p.write_text(str(n+1))
mode=sys.argv[2]
if mode=='timeout' and n==0 or mode=='exhaust': time.sleep(2)
if mode=='unknown': time.sleep(.06);sys.exit(7)
if mode=='memory' and n==0:
 x=bytearray(80*1024*1024);time.sleep(.08);print('Error querying reflection endpoint DEADLINE_EXCEEDED',file=sys.stderr);sys.exit(1)
time.sleep(.06)
        """
        )

        let invoke mode timeout =
            let before = physicalAttempts.Count

            let result =
                runMeasured
                    timeout
                    scratch.FullName
                    executable
                    [ "verify"; Path.Combine(scratch.FullName, mode); mode ]
                    []

            let rows = physicalAttempts |> Seq.skip before |> Seq.toArray

            if rows.Length > 3 then
                fail "ACCOUNTING-CONTROL" "physical-row-bound"

            // Fixed fixture metadata only: retain the actual row before an assertion fails.
            // Never print child stdout/stderr, process arguments or environment.
            let observed value =
                value
                |> Option.map (fun item -> (string item).ToLowerInvariant())
                |> Option.defaultValue "unobserved"

            for row in rows do
                printfn
                    "ACCOUNTING_CONTROL_ATTEMPT mode=%s ordinal=%d exit=%s timedOut=%s classification=%s completed=%b terminal=%s elapsedMs=%s peakMiB=%s"
                    mode
                    row.Ordinal
                    (observed row.ExitCode)
                    (observed row.TimedOut)
                    (row.Classification |> Option.defaultValue "unclassified")
                    row.Completed
                    (observed row.Terminal)
                    (observed row.ElapsedMs)
                    (observed row.PeakMiB)

            result, rows

        let (code, _, _, elapsed, peak), timeoutRows = invoke "timeout" 180

        check
            "timeout-success"
            (code = 0
             && timeoutRows.Length = 2
             && timeoutRows[0].TimedOut = Some true
             && timeoutRows[0].Classification = Some "early-lifecycle-exit"
             && timeoutRows[0].Terminal = Some false
             && elapsed = timeoutRows[1].ElapsedMs.Value
             && peak = (timeoutRows |> Array.map (fun row -> row.PeakMiB.Value) |> Array.max))

        let (code, _, _, _, _), exhausted = invoke "exhaust" 180
        check "two-retries-exhausted" (code = 124 && exhausted.Length = 3 && exhausted[2].Terminal = Some true)
        // This control exercises an observed ordinary exit, not the separate 180ms
        // timeout controls. Allow bounded process startup/scheduling headroom.
        let (code, _, _, _, _), unknown = invoke "unknown" 3000

        check
            "unclassified-no-retry"
            (code = 7
             && unknown.Length = 1
             && unknown[0].Classification.IsNone
             && unknown[0].TimedOut = Some false
             && unknown[0].Completed
             && unknown[0].Terminal = Some true
             && (unknown[0].ElapsedMs |> Option.exists (fun value -> value > 0L))
             && (unknown[0].PeakMiB |> Option.exists (fun value -> value > 0)))
        let (code, _, _, _, peak), memory = invoke "memory" 1000

        check
            "failed-high-rss-preserved"
            (code = 0
             && memory.Length = 2
             && memory[0].PeakMiB.Value > memory[1].PeakMiB.Value
             && peak = memory[0].PeakMiB.Value)

        let beforeRun = physicalAttempts.Count

        let code, _, _ =
            run scratch.FullName executable [ "verify"; Path.Combine(scratch.FullName, "run-memory"); "memory" ] []

        let runRows = physicalAttempts |> Seq.skip beforeRun |> Seq.toArray

        check
            "run-wrapper-preserves-failed-peak"
            (code = 0
             && runRows.Length = 2
             && runRows[0].PeakMiB.Value > runRows[1].PeakMiB.Value)

        let row index elapsed terminal ordinal classification peak =
            { timeoutRows[1] with
                LogicalId = string index
                Label =
                    [|
                        "quint/formal-simulation"
                        "quint/formal-temporal"
                        "quint/formal-safety-mutant"
                        "quint/formal-counterexample-temporal"
                        "quint/formal-counterexample-temporal"
                        "quint/formal-counterexample-projection"
                        "quint/formal-counterexample-projection"
                    |][index - 1]
                ExitCode = Some(if index <= 2 then 0 else 1)
                SemanticOutcome =
                    Some(
                        if index <= 2 then
                            "positive-complete"
                        elif index <= 3 || index >= 6 then
                            "invariant-counterexample"
                        else
                            "temporal-counterexample"
                    )
                ElapsedMs = Some elapsed
                Ordinal = ordinal
                Terminal = Some terminal
                Classification = classification
                PeakMiB = Some peak
            }

        let exact =
            [|
                for index in 1..7 -> row index (if index = 7 then 299994L else 1L) true 1 None 10
                for index in 1..3 do
                    for ordinal in 1..2 -> row index 300000L false ordinal (Some "early-lifecycle-exit") 6144
            |]

        let exact =
            exact
            |> Array.groupBy _.LogicalId
            |> Array.collect (fun (_, rows) ->
                rows
                |> Array.sortBy (fun row -> if row.Terminal = Some true then 4 else row.Ordinal)
                |> Array.mapi (fun index row -> { row with Ordinal = index + 1 }))

        check "exact-total-and-terminal" (summarizeFormalPhysical 2100000L exact = Ok(300000L, 2100000L, 6144))
        check "over-total-includes-failed-cost" (summarizeFormalPhysical 2099999L exact = Error "budget")

        check
            "failed-high-rss-gate"
            (summarizeFormalPhysical
                2100000L
                (exact
                 |> Array.mapi (fun index row -> if index = 0 then { row with PeakMiB = Some 6145 } else row))
                =
                Error "budget")

        check "omitted" (summarizeFormalPhysical 2100000L exact[1..] |> Result.isError)

        check
            "duplicate"
            (summarizeFormalPhysical 2100000L (Array.append exact [| exact[0] |])
             |> Result.isError)

        check
            "incomplete"
            (summarizeFormalPhysical
                2100000L
                (exact
                 |> Array.mapi (fun index row ->
                     if index = 0 then
                         { row with
                             Completed = false
                             ElapsedMs = None
                         }
                     else
                         row))
             |> Result.isError)

        check
            "unexpected-terminal-crash"
            (summarizeFormalPhysical
                2100000L
                (exact
                 |> Array.mapi (fun index row -> if index = 2 then { row with ExitCode = Some 137 } else row))
             |> Result.isError)

        check
            "unexpected-terminal-timeout"
            (summarizeFormalPhysical
                2100000L
                (exact
                 |> Array.mapi (fun index row -> if index = 2 then { row with TimedOut = Some true } else row))
             |> Result.isError)

        check
            "unexpected-nonsemantic-negative"
            (terminalSemantics "quint/formal-safety-mutant" (Some "fixture") 1 false None "" "Traceback" = None)

        check
            "expected-semantic-negative"
            (terminalSemantics "quint/formal-safety-mutant" (Some "fixture") 1 false None "Invariant violated" "" =
                Some "invariant-counterexample")

        printfn "ACCOUNTING_CONTROLS_OK controls=15 disposition=controlled-subprocess-not-native-qualification"
    finally
        scratch.Delete true

    exit 0

let requireGreen code workingDirectory executable arguments environment =
    let exitCode, output, error = run workingDirectory executable arguments environment

    if exitCode <> 0 then
        fail code ($"exit={exitCode}; stdout={output}; stderr={error}")

    output, error

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList

let rec parse root staticOnly compilerOnly output receiptFailurePhase measurementOutput preflightOutput remaining =
    match remaining with
    | [] -> root, staticOnly, compilerOnly, output, receiptFailurePhase, measurementOutput, preflightOutput
    | "--root" :: value :: tail ->
        parse
            (Path.GetFullPath value)
            staticOnly
            compilerOnly
            output
            receiptFailurePhase
            measurementOutput
            preflightOutput
            tail
    | "--static-only" :: tail ->
        parse root true compilerOnly output receiptFailurePhase measurementOutput preflightOutput tail
    | "--compiler-only" :: tail ->
        parse root staticOnly true output receiptFailurePhase measurementOutput preflightOutput tail
    | "--output" :: value :: tail ->
        parse root staticOnly compilerOnly (Some value) receiptFailurePhase measurementOutput preflightOutput tail
    | "--exercise-failure-receipt" :: value :: tail ->
        parse root staticOnly compilerOnly output (Some value) measurementOutput preflightOutput tail
    | "--measure-only" :: value :: tail ->
        parse
            root
            staticOnly
            compilerOnly
            output
            receiptFailurePhase
            (Some(Path.GetFullPath value))
            preflightOutput
            tail
    | "--preflight-only" :: value :: tail ->
        parse
            root
            staticOnly
            compilerOnly
            output
            receiptFailurePhase
            measurementOutput
            (Some(Path.GetFullPath value))
            tail
    | value :: _ -> fail "ARGUMENT" value

let root, staticOnly, compilerOnly, outputOption, receiptFailurePhase, measurementOutput, preflightOutput =
    parse (Path.GetFullPath ".") false false None None None None arguments

let measurementOnly = Option.isSome measurementOutput
let preflightOnly = Option.isSome preflightOutput
// Measurement admission requires the qualified Core bytes actually loaded, not a
// symlinked SDK path that the loader may resolve into another runtime closure.
if measurementOnly || preflightOnly then
    let core = typeof<option<int>>.Assembly.Location

    if
        sha256 core
        <> "7516a966abc789eab916e95d429bf3a49e996257069d97f7f3eb5d47373fa72b"
    then
        fail "FSI-CORE-BINDING" "loaded Core bytes are outside the qualified profile"

if measurementOnly && preflightOnly then
    fail "MEASUREMENT-MIXTURE" "measurement and focused observation are incompatible"

let private measurementCustody =
    (if preflightOnly then preflightOutput else measurementOutput)
    |> Option.map (fun path ->
        let relative = Path.GetRelativePath(root, path)

        if
            staticOnly
            || compilerOnly
            || Option.isSome outputOption
            || Option.isSome receiptFailurePhase
            || relative = "."
            || not (relative.StartsWith(".." + string Path.DirectorySeparatorChar, StringComparison.Ordinal))
        then
            fail "MEASUREMENT-OUTPUT" "requires distinct directory outside source and full native execution"

        if
            not (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable "FSGG_QUINT_RECEIPT"))
            || Environment.GetEnvironmentVariable "FSGG_REFRESH_FORMAL_EVIDENCE" = "1"
        then
            fail "MEASUREMENT-MIXTURE" "ordinary receipt and source-refresh options are incompatible"

        MeasurementDirectory.create root path)

let measurementAttemptId = Guid.NewGuid().ToString("N")
let measurementDirectory = measurementCustody |> Option.map _.HeldPath

let verifyMeasurementDirectory () =
    measurementCustody |> Option.iter MeasurementDirectory.verify

let refreshFormalEvidence =
    Environment.GetEnvironmentVariable("FSGG_REFRESH_FORMAL_EVIDENCE") = "1"

let qualificationOutput =
    (if measurementOnly || preflightOnly then
         measurementDirectory |> Option.map (fun p -> Path.Combine(p, "attempt.json"))
     else
         outputOption)
    |> Option.map Path.GetFullPath
    |> Option.defaultValue (Path.Combine(root, "artifacts/canonical-quint/qualification.json"))

let writeQualificationReceipt failure =
    verifyMeasurementDirectory ()
    let totalDurationMs = qualificationClock.ElapsedMilliseconds
    let q2DurationMs = Math.Max(0L, totalDurationMs - preparationDurationMs)
    let preparationValue = preparationDigest |> Option.defaultValue "none"

    let failureCode, failureDetailSha256 =
        match failure with
        | Some(code, detail) -> code, sha256Text detail
        | None -> "none", "none"

    let formalEvidenceIdentity =
        formalCounterexampleReceipts
        |> Seq.sortBy (fun (id, _, _, _) -> id)
        |> Seq.map (fun (id, manifest, trace, itf) -> $"%s{id}|%s{manifest}|%s{trace}|%s{itf}")
        |> String.concat ";"

    let resultSha256 =
        sha256Text (
            $"%s{q1Outcome}|%s{q2Outcome}|%d{verifiedPositiveInvariantCount}|%d{quintRejectedProcessCount}|%d{externalProcessCount}|%d{quintProcessCount}|%d{apalacheVerifyInvocationCount}|%d{apalacheStartupRetryCount}|%d{apalacheVerifyStartupRetryCount}|%d{apalacheReflectionRetryCount}|%d{apalacheEarlyLifecycleRetryCount}|%s{preparationValue}|%s{formalEvidenceIdentity}|%s{failureCode}|%s{failureDetailSha256}"
        )

    let outputDirectory = Path.GetDirectoryName qualificationOutput

    if not (String.IsNullOrWhiteSpace outputDirectory) then
        Directory.CreateDirectory outputDirectory |> ignore

    let temporaryOutput =
        qualificationOutput + "." + Guid.NewGuid().ToString("N") + ".tmp"

    do
        use outputStream =
            if measurementOnly || preflightOnly then
                new FileStream(temporaryOutput, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            else
                File.Create temporaryOutput

        use writer = new Utf8JsonWriter(outputStream, JsonWriterOptions(Indented = true))
        writer.WriteStartObject()

        writer.WriteString(
            "schema",
            if measurementOnly || preflightOnly then
                "fsgg.coordination.canonical-quint-measurement-attempt/1"
            else
                "fsgg.coordination.canonical-quint-qualification/1"
        )

        writer.WriteString("q1Outcome", q1Outcome)
        writer.WriteString("q2Outcome", q2Outcome)
        writer.WriteNumber("positiveInvariantCount", verifiedPositiveInvariantCount)
        writer.WriteNumber("negativeControlCount", quintRejectedProcessCount)
        writer.WriteNumber("preparationDurationMs", preparationDurationMs)
        writer.WriteNumber("q2DurationMs", q2DurationMs)
        writer.WriteNumber("totalDurationMs", totalDurationMs)
        writer.WriteStartObject("processCounts")
        writer.WriteNumber("external", externalProcessCount)
        writer.WriteNumber("quintCli", quintProcessCount)
        writer.WriteNumber("apalacheVerify", apalacheVerifyInvocationCount)
        writer.WriteEndObject()

        writer.WriteString(
            "processAccounting",
            if measurementOnly then
                "logical-invocations-plus-all-physical-observations/v2"
            else
                "logical-invocations-plus-explicit-startup-retries/v1"
        )

        if measurementOnly then
            writer.WriteString("attemptId", measurementAttemptId)
            writer.WriteString("compilerSha256", compilerIdentitySha256)

            writer.WriteString(
                "validatorSha256",
                sha256 (Path.Combine(root, "eng/validate-canonical-quint-protocol.fsx"))
            )

            writer.WriteString("configurationSha256", sha256 (Path.Combine(root, "eng/quint-qualification.json")))
            writer.WriteNumber("allPhysicalElapsedMs", physicalAttempts |> Seq.sumBy (fun row -> row.ElapsedMs.Value))
            writer.WriteNumber("observedPhysicalCount", physicalAttempts.Count)

        writer.WriteStartObject("physicalProcessCounts")
        writer.WriteNumber("external", externalProcessCount + apalacheStartupRetryCount)
        writer.WriteNumber("quintCli", quintProcessCount + apalacheStartupRetryCount)
        writer.WriteNumber("apalacheVerify", apalacheVerifyInvocationCount + apalacheVerifyStartupRetryCount)
        writer.WriteEndObject()
        writer.WriteStartObject("startupRetries")
        writer.WriteNumber("total", apalacheStartupRetryCount)
        writer.WriteNumber("verify", apalacheVerifyStartupRetryCount)
        writer.WriteNumber("reflectionDeadline", apalacheReflectionRetryCount)
        writer.WriteNumber("earlyLifecycleExit", apalacheEarlyLifecycleRetryCount)
        writer.WriteEndObject()
        writer.WriteStartArray("formalCounterexamples")

        for id, manifestSha256, traceSha256, itfSha256 in
            formalCounterexampleReceipts |> Seq.sortBy (fun (id, _, _, _) -> id) do
            writer.WriteStartObject()
            writer.WriteString("id", id)
            writer.WriteString("manifestSha256", manifestSha256)
            writer.WriteString("traceSha256", traceSha256)
            writer.WriteString("itfSha256", itfSha256)
            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteStartObject("tools")
        writer.WriteString("toolchainSha256", expectedToolchain)
        writer.WriteString("quintSha256", expectedQuint)
        writer.WriteString("apalacheJarSha256", expectedApalacheJar)
        writer.WriteEndObject()
        writer.WriteStartObject("inputs")
        writer.WriteString("sourceSha256", expectedSource)
        writer.WriteString("contractSha256", expectedContract)
        writer.WriteEndObject()

        match preparationDigest with
        | Some digest -> writer.WriteString("preparationSha256", digest)
        | None -> writer.WriteNull("preparationSha256")

        match failure with
        | Some(code, _) ->
            writer.WriteStartObject("failure")
            writer.WriteString("code", code)
            writer.WriteString("detailSha256", failureDetailSha256)
            writer.WriteEndObject()
        | None -> writer.WriteNull("failure")

        writer.WriteString("resultSha256", resultSha256)
        writer.WriteEndObject()
        writer.Flush()
        outputStream.Flush(true)

    verifyMeasurementDirectory ()
    File.Move(temporaryOutput, qualificationOutput, not (measurementOnly || preflightOnly))

failureReceiptWriter <-
    Some(fun code detail ->
        if currentPhase = "q1" then
            q1Outcome <- "failed"
            q2Outcome <- "not-run"
        else
            q2Outcome <- "failed"

        writeQualificationReceipt (Some(code, detail)))

let requireCompletedProcessInventory () =
    let expected =
        expectedInvocationInventory
        |> Seq.map (fun row -> row.Key, row.Value)
        |> Map.ofSeq

    let actual =
        actualInvocationInventory
        |> Seq.map (fun row -> row.Key, row.Value)
        |> Map.ofSeq

    if actual <> expected then
        fail "PROCESS-INVENTORY-COVERAGE" ($"expected=%A{expected}; actual=%A{actual}")

let exerciseFormalShardProcessInventory mutation =
    let expected =
        [
            "external/base", 31
            "quint/preflight-sampling", 13
            "quint/preflight-interaction", 2
            "quint/base-nonverify", 5
            "quint/base-verify", 0
            "quint/selected-root", 7
            "quint/formal-simulation", 1
            "quint/formal-temporal", 1
            "quint/formal-safety-mutant", 1
            "quint/formal-counterexample-temporal", 2
            "quint/formal-counterexample-projection", 2
        ]

    for label, count in expected do
        expectedInvocationInventory[label] <- count
        actualInvocationInventory[label] <- count

    match mutation with
    | "missing" -> actualInvocationInventory.TryRemove("quint/formal-temporal") |> ignore
    | "wrong" -> actualInvocationInventory["quint/formal-temporal"] <- 2
    | "extra" -> actualInvocationInventory["quint/unplanned"] <- 1
    | value -> fail "ARGUMENT" value

    q1Outcome <- "passed"
    currentPhase <- "q2"
    preparationDurationMs <- qualificationClock.ElapsedMilliseconds
    preparationDigest <- Some(String.replicate 64 "0")
    requireCompletedProcessInventory ()

match receiptFailurePhase with
| Some "q1" -> fail "RECEIPT-SELF-TEST-Q1" "exercise q1 failure receipt"
| Some "q2" ->
    q1Outcome <- "passed"
    currentPhase <- "q2"
    preparationDurationMs <- qualificationClock.ElapsedMilliseconds
    preparationDigest <- Some(String.replicate 64 "0")
    fail "RECEIPT-SELF-TEST-Q2" "exercise q2 failure receipt"
| Some "process-inventory" ->
    q1Outcome <- "passed"
    currentPhase <- "q2"
    verifiedPositiveInvariantCount <- 8
    quintRejectedProcessCount <- 100
    expectedInvocationInventory["exercise-required"] <- 1
    preparationDurationMs <- qualificationClock.ElapsedMilliseconds
    preparationDigest <- Some(String.replicate 64 "0")
    requireCompletedProcessInventory ()
| Some "formal-process-inventory-missing" -> exerciseFormalShardProcessInventory "missing"
| Some "formal-process-inventory-wrong" -> exerciseFormalShardProcessInventory "wrong"
| Some "formal-process-inventory-extra" -> exerciseFormalShardProcessInventory "extra"
| Some value -> fail "ARGUMENT" ($"invalid failure receipt phase: %s{value}")
| None -> ()

let source = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")

let selectors =
    Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.bindings.json")

let retained = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Generated")
let authority = Path.Combine(retained, "typed-authority.json")
let contract = Path.Combine(retained, "contract.json")
let binding = Path.Combine(retained, "Protocol.Generated.fs")
let sourceMap = Path.Combine(retained, "source-map.json")
let receipt = Path.Combine(retained, "receipt.json")

let outputGenerator =
    Path.Combine(root, "eng/generate-compiled-contract-outputs.fsx")

let qualificationValidator =
    Path.Combine(root, "eng/validate-quint-qualification.fsx")

let qualificationConfiguration = Path.Combine(root, "eng/quint-qualification.json")

let measurementConfigurationDigest = sha256 qualificationConfiguration

let measurementValidatorDigest =
    sha256 (Path.Combine(root, "eng/validate-canonical-quint-protocol.fsx"))

let writeMeasurementJson (name: string) value =
    if measurementOnly then
        verifyMeasurementDirectory ()

        if Path.GetFileName name <> name then
            fail "MEASUREMENT-OUTPUT-PATH" name

        let options =
            JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true)

        let bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, options) + "\n")

        use stream =
            new FileStream(
                Path.Combine(measurementDirectory.Value, name),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            )

        stream.Write(bytes, 0, bytes.Length)
        stream.Flush(true)
        verifyMeasurementDirectory ()

if measurementOnly then
    physicalObserver <-
        Some(fun row ->
            let state = if row.Completed then "complete" else "active"

            let boxOption value =
                value |> Option.map box |> Option.defaultValue null

            writeMeasurementJson
                ($"physical-{row.LogicalId}-{row.Ordinal}-{state}.json")
                {|
                    schema = "fsgg.quint-physical-attempt/2"
                    attemptId = measurementAttemptId
                    sourceSha256 = expectedSource
                    configurationSha256 = measurementConfigurationDigest
                    toolchainSha256 = expectedToolchain
                    validatorSha256 = measurementValidatorDigest
                    compilerSha256 = compilerIdentitySha256
                    logicalId = row.LogicalId
                    ordinal = row.Ordinal
                    scope = boxOption row.Scope
                    label = row.Label
                    classification = boxOption row.Classification
                    elapsedMs = boxOption row.ElapsedMs
                    peakMiB = boxOption row.PeakMiB
                    exitCode = boxOption row.ExitCode
                    timedOut = boxOption row.TimedOut
                    terminal = boxOption row.Terminal
                    completed = row.Completed
                    pid = boxOption row.Pid
                    timeoutMs = boxOption row.TimeoutMs
                    outputBytes = boxOption row.OutputBytes
                    outputSha256 = boxOption row.OutputSha256
                    semanticOutcome = boxOption row.SemanticOutcome
                |})

let qualificationBaseline =
    Path.Combine(root, "eng/quint-qualification-baseline.json")

let compiledOutputs = Path.Combine(retained, "compiled-outputs")
let compiledOutputManifest = Path.Combine(compiledOutputs, "manifest.json")

for code, path in
    [
        "SOURCE-MISSING", source
        "SELECTOR-MISSING", selectors
        "AUTHORITY-MISSING", authority
        "CONTRACT-MISSING", contract
        "BINDING-MISSING", binding
        "SOURCE-MAP-MISSING", sourceMap
        "RECEIPT-MISSING", receipt
        "OUTPUT-GENERATOR-MISSING", outputGenerator
        "QUALIFICATION-VALIDATOR-MISSING", qualificationValidator
        "QUALIFICATION-CONFIG-MISSING", qualificationConfiguration
        "QUALIFICATION-BASELINE-MISSING", qualificationBaseline
        "COMPILED-OUTPUT-MANIFEST-MISSING", compiledOutputManifest
    ] do
    requireFile code path

if sha256 source <> expectedSource then
    fail "SOURCE-DIGEST" (sha256 source)

if sha256 contract <> expectedContract then
    fail "CONTRACT-DIGEST" (sha256 contract)

let authorityDocument = JsonDocument.Parse(File.ReadAllBytes authority)
let authorityRoot = authorityDocument.RootElement

if authorityRoot.GetProperty("schemaVersion").GetInt32() <> 2 then
    fail "AUTHORITY-SCHEMA" "not-v2"

if authorityRoot.GetProperty("backend").GetString() <> "quint-specification-v1" then
    fail "BACKEND" "wrong"

if authorityRoot.GetProperty("profileIdentity").GetString() <> expectedProfile then
    fail "PROFILE" "wrong"

if authorityRoot.GetProperty("packageIdentity").GetString() <> expectedPackage then
    fail "PACKAGE" "wrong"

if authorityRoot.GetProperty("toolchainIdentity").GetString() <> expectedToolchain then
    fail "TOOLCHAIN" "wrong"

let contractDocument = JsonDocument.Parse(File.ReadAllBytes contract)
let contractRoot = contractDocument.RootElement

if
    contractRoot.GetProperty("schema").GetString()
    <> "fsgg.quint.compiled-contract/v2"
then
    fail "CONTRACT-SCHEMA" "wrong"

if contractRoot.GetProperty("profile").GetString() <> expectedProfile then
    fail "CONTRACT-PROFILE" "wrong"

if contractRoot.GetProperty("catalogue").GetArrayLength() <> 136 then
    fail "CATALOGUE" "wrong-cardinality"

if contractRoot.GetProperty("relationships").GetArrayLength() <> 17 then
    fail "RELATIONSHIPS" "wrong-cardinality"

if contractRoot.GetProperty("actionEffects").GetArrayLength() <> 14 then
    fail "ACTIONS" "wrong-cardinality"

let mutationIds =
    contractRoot.GetProperty("catalogue").EnumerateArray()
    |> Seq.map (fun entry -> entry.GetProperty("id").GetString())
    |> Seq.filter (fun id ->
        id.StartsWith("MUT-", StringComparison.Ordinal)
        || id.StartsWith("MOUT-", StringComparison.Ordinal))
    |> Set.ofSeq

let expectedMutationIds =
    Set
        [
            "MUT-Create"
            "MUT-Append"
            "MUT-AddEdge"
            "MUT-RemoveEdge"
            "MUT-Set"
            "MUT-Clear"
            "MUT-Transition"
            "MUT-Compensate"
            "MOUT-Applied"
            "MOUT-Idempotent"
            "MOUT-Rejected"
            "MOUT-RevisionConflict"
            "MOUT-RateLimited"
            "MOUT-Unavailable"
            "MOUT-TimedOut"
            "MOUT-Incomplete"
        ]

if mutationIds <> expectedMutationIds then
    fail "MUTATION-CATALOGUES" (String.concat "," mutationIds)

let durablePlanDispositionIds =
    contractRoot.GetProperty("catalogue").EnumerateArray()
    |> Seq.map (fun entry -> entry.GetProperty("id").GetString())
    |> Seq.filter (fun id -> id.StartsWith("PDISP-", StringComparison.Ordinal))
    |> Set.ofSeq

let expectedDurablePlanDispositionIds =
    Set [ "PDISP-Advance"; "PDISP-ReceiptReread"; "PDISP-Replan"; "PDISP-Compensate" ]

if durablePlanDispositionIds <> expectedDurablePlanDispositionIds then
    fail "DURABLE-PLAN-DISPOSITIONS" (String.concat "," durablePlanDispositionIds)

let desiredStateEntries =
    contractRoot.GetProperty("catalogue").EnumerateArray()
    |> Seq.filter (fun entry -> entry.GetProperty("id").GetString() = "DSTATE-Specification")
    |> Seq.toList

match desiredStateEntries with
| [ desiredState ] ->
    let field name =
        desiredState.GetProperty("value").GetProperty("fields").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("name").GetString() = name)
        |> _.GetProperty("value")
        |> _.GetProperty("value")

    let expectedFields =
        [
            "authorityClass", "revision-bound"
            "executionClass", "pure-intent-no-writer"
            "issueSchemaContract", "issue-type|issue-field|field-type|allowed-value"
            "repositoryPropertiesContract", "property-schema|property-value"
            "projectsContract",
            "project-field|project-view|project-workflow|project-visibility|project-membership-policy"
            "repositoryProfileContract", "ruleset|merge-queue|merge-policy|actions-policy|branch-deletion-policy"
            "workflowPinsContract", "reusable-workflow-pin|action-pin"
            "releasesContract", "release-environment|immutable-release|tag-protection|trusted-publisher"
            "permissionsContract", "repository-visibility|team-access|workflow-permission|environment-protection"
            "securitySupplyChainContract",
            "vulnerability-policy|secret-policy|dependency-policy|sbom-policy|attestation-policy"
            "phaseContract", "DSPH-Inspect>DSPH-Plan>(DSPH-Apply|DSPH-Verify)>DSPH-Verify"
            "phaseAuthorityContract", "subject|profile|family|content|authority-revision|plan-outcome|apply-receipt"
            "refusalContract", "unsupported|unauthorized|incomplete|stale|identity-mismatch"
        ]

    for name, expected in expectedFields do
        if (field name).GetString() <> expected then
            fail "DESIRED-STATE-SUMMARY" name
| _ -> fail "DESIRED-STATE-SUMMARY" "expected-one"

let compiledOutputEntries =
    contractRoot.GetProperty("catalogue").EnumerateArray()
    |> Seq.filter (fun entry -> entry.GetProperty("id").GetString() = "COUT-Specification")
    |> Seq.toList

match compiledOutputEntries with
| [ compiledOutput ] ->
    let field name =
        compiledOutput.GetProperty("value").GetProperty("fields").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("name").GetString() = name)
        |> _.GetProperty("value")
        |> _.GetProperty("value")
        |> _.GetString()

    let expectedFields =
        [
            "familyContract",
            "1:schemas|2:command-metadata|3:permission-census|4:mutation-census|5:settings-plans|6:projection-views|7:semantic-diff|8:diagrams|9:model-test-inventory"
            "identityContract",
            "family|ordinal|source|behavior|source-version|extractor-version|quint-version|profile-version|schema-version|contract|content"
            "qualificationContract",
            "supported|complete|fresh|qualification-manifest:candidate|input-set|environment|results|reviewers|independent-cases|independent-review"
            "projectionViewFormats", "markdown|json"
            "refusalContract", "missing|duplicate|substituted|unsupported|incomplete|reordered|stale"
            "normalizationAuthority", "typed-effect-json"
            "versionContract",
            String.concat
                "|"
                [
                    expectedSourceVersion
                    expectedExtractorVersion
                    expectedQuintVersion
                    expectedProfile
                    expectedSchemaVersion
                ]
            "semanticDiffContract", "ordinal|json-pointer|value-sha256"
        ]

    for name, expected in expectedFields do
        if field name <> expected then
            fail "COMPILED-OUTPUT-SUMMARY" name
| _ -> fail "COMPILED-OUTPUT-SUMMARY" "expected-one"

let expectedCompiledOutputs =
    [
        ("COUT-Schemas", 1, [ "schemas.json" ])
        ("COUT-CommandMetadata", 2, [ "command-metadata.json" ])
        ("COUT-PermissionCensus", 3, [ "permission-census.json" ])
        ("COUT-MutationCensus", 4, [ "mutation-census.json" ])
        ("COUT-SettingsPlans", 5, [ "settings-plans.json" ])
        ("COUT-ProjectionViews", 6, [ "projection-view.json"; "projection-view.md" ])
        ("COUT-SemanticDiff", 7, [ "semantic-diff.json" ])
        ("COUT-Diagrams", 8, [ "diagrams.md" ])
        ("COUT-ModelTestInventory", 9, [ "model-test-inventory.json" ])
    ]

let compiledOutputDocument =
    JsonDocument.Parse(File.ReadAllBytes compiledOutputManifest)

let compiledOutputRoot = compiledOutputDocument.RootElement

let receiptDocument = JsonDocument.Parse(File.ReadAllBytes receipt)
let receiptRoot = receiptDocument.RootElement

if receiptRoot.GetProperty("sourceSha256").GetString() <> expectedSource then
    fail "RECEIPT-SOURCE" "stale"

if receiptRoot.GetProperty("contractSha256").GetString() <> expectedContract then
    fail "RECEIPT-CONTRACT" "stale"

if receiptRoot.GetProperty("typedEffectSha256").GetString() <> expectedBehavior then
    fail "RECEIPT-BEHAVIOR" "stale"

let validateDeterministicIdentity code (identity: JsonElement) =
    if
        identity.GetProperty("schema").GetString()
        <> "fsgg.quint.deterministic-identity/1"
    then
        fail code "schema"

    if identity.GetProperty("sourceSha256").GetString() <> expectedSource then
        fail code "source"

    if identity.GetProperty("behavioralSha256").GetString() <> expectedBehavior then
        fail code "behavior"

    if identity.GetProperty("contractSha256").GetString() <> expectedContract then
        fail code "contract"

    if
        identity.GetProperty("normalizationAuthority").GetString()
        <> "typed-effect-json"
    then
        fail code "normalization"

    let versions = identity.GetProperty("versions")

    if versions.GetProperty("source").GetString() <> expectedSourceVersion then
        fail "SOURCE-VERSION" "unsupported"

    if versions.GetProperty("extractor").GetString() <> expectedExtractorVersion then
        fail "EXTRACTOR-VERSION" "unsupported"

    if versions.GetProperty("quint").GetString() <> expectedQuintVersion then
        fail "QUINT-VERSION" "unsupported"

    if versions.GetProperty("profile").GetString() <> expectedProfile then
        fail "PROFILE-VERSION" "unsupported"

    if versions.GetProperty("schema").GetString() <> expectedSchemaVersion then
        fail "SCHEMA-VERSION" "unsupported"

    if not (identity.GetProperty("supported").GetBoolean()) then
        fail code "unsupported"

    if not (identity.GetProperty("complete").GetBoolean()) then
        fail code "incomplete"

    if not (identity.GetProperty("fresh").GetBoolean()) then
        fail code "stale"

if
    compiledOutputRoot.GetProperty("schema").GetString()
    <> "fsgg.quint.compiled-output-manifest/1"
then
    fail "COMPILED-OUTPUT-SCHEMA" "wrong"

if compiledOutputRoot.GetProperty("sourceSha256").GetString() <> expectedSource then
    fail "COMPILED-OUTPUT-SOURCE" "stale"

if
    compiledOutputRoot.GetProperty("behavioralSha256").GetString()
    <> expectedBehavior
then
    fail "COMPILED-OUTPUT-BEHAVIOR" "stale"

if compiledOutputRoot.GetProperty("profile").GetString() <> expectedProfile then
    fail "COMPILED-OUTPUT-PROFILE" "wrong"

if compiledOutputRoot.GetProperty("contractSha256").GetString() <> expectedContract then
    fail "COMPILED-OUTPUT-CONTRACT" "stale"

validateDeterministicIdentity "COMPILED-OUTPUT-IDENTITY" (compiledOutputRoot.GetProperty("identity"))

let actualCompiledOutputs =
    compiledOutputRoot.GetProperty("outputs").EnumerateArray() |> Seq.toList

if actualCompiledOutputs.Length <> expectedCompiledOutputs.Length then
    fail "COMPILED-OUTPUT-COUNT" (string actualCompiledOutputs.Length)

let expectedFiles =
    expectedCompiledOutputs
    |> List.collect (fun (_, _, files) -> files)
    |> Set.ofList
    |> Set.add "manifest.json"

let actualFiles =
    Directory.EnumerateFiles(compiledOutputs, "*", SearchOption.TopDirectoryOnly)
    |> Seq.map Path.GetFileName
    |> Set.ofSeq

if actualFiles <> expectedFiles then
    fail "COMPILED-OUTPUT-FILES" (String.concat "," actualFiles)

for expected, actual in List.zip expectedCompiledOutputs actualCompiledOutputs do
    let expectedFamily, expectedOrdinal, expectedFamilyFiles = expected

    if actual.GetProperty("family").GetString() <> expectedFamily then
        fail "COMPILED-OUTPUT-ORDER" expectedFamily

    if actual.GetProperty("ordinal").GetInt32() <> expectedOrdinal then
        fail "COMPILED-OUTPUT-ORDER" expectedFamily

    if actual.GetProperty("sourceSha256").GetString() <> expectedSource then
        fail "COMPILED-OUTPUT-IDENTITY" expectedFamily

    if actual.GetProperty("behavioralSha256").GetString() <> expectedBehavior then
        fail "COMPILED-OUTPUT-IDENTITY" expectedFamily

    if actual.GetProperty("profile").GetString() <> expectedProfile then
        fail "COMPILED-OUTPUT-IDENTITY" expectedFamily

    if actual.GetProperty("contractSha256").GetString() <> expectedContract then
        fail "COMPILED-OUTPUT-IDENTITY" expectedFamily

    validateDeterministicIdentity "COMPILED-OUTPUT-IDENTITY" (actual.GetProperty("identity"))

    if not (actual.GetProperty("supported").GetBoolean()) then
        fail "COMPILED-OUTPUT-UNSUPPORTED" expectedFamily

    if not (actual.GetProperty("complete").GetBoolean()) then
        fail "COMPILED-OUTPUT-INCOMPLETE" expectedFamily

    if not (actual.GetProperty("fresh").GetBoolean()) then
        fail "COMPILED-OUTPUT-STALE" expectedFamily

    let actualFamilyFiles =
        actual.GetProperty("files").EnumerateArray()
        |> Seq.map (fun file -> file.GetProperty("path").GetString())
        |> Seq.toList

    if actualFamilyFiles <> expectedFamilyFiles then
        fail "COMPILED-OUTPUT-FAMILY-FILES" expectedFamily

    for file in actual.GetProperty("files").EnumerateArray() do
        let relative = file.GetProperty("path").GetString()
        let path = Path.Combine(compiledOutputs, relative)
        requireFile "COMPILED-OUTPUT-FILE-MISSING" path

        if file.GetProperty("contentSha256").GetString() <> sha256 path then
            fail "COMPILED-OUTPUT-CONTENT" relative

let trackedExit, trackedQnt, trackedError =
    run root "git" [ "ls-files"; "*.qnt" ] []

if trackedExit <> 0 then
    fail "GIT-INVENTORY" trackedError

if not (String.IsNullOrWhiteSpace trackedQnt) then
    fail "GENERATED-QNT-TRACKED" trackedQnt

let selectionPlanPath =
    Path.Combine(Path.GetTempPath(), $"fsgg-quint-selection-{Guid.NewGuid():N}.json")

let qualificationMode =
    match (measurementOnly || preflightOnly), Environment.GetEnvironmentVariable "FSGG_QUINT_QUALIFICATION_MODE" with
    | true, _ -> "measurement"
    | false, null
    | false, "" -> "protected"
    | false, value -> value

let protectedMode =
    match Environment.GetEnvironmentVariable "FSGG_QUINT_PROTECTED_MODE" with
    | null
    | "" -> "main"
    | value -> value

let changedModules =
    match Environment.GetEnvironmentVariable "FSGG_QUINT_CHANGED_MODULES" with
    | null
    | "" -> []
    | value ->
        value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.toList

let changedPaths =
    match Environment.GetEnvironmentVariable "FSGG_QUINT_CHANGED_PATHS" with
    | null
    | "" -> []
    | value ->
        value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.toList

let changedSurfaces =
    match Environment.GetEnvironmentVariable "FSGG_QUINT_CHANGED_SURFACES" with
    | null
    | "" -> []
    | value ->
        value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.toList

let reuseSource =
    Environment.GetEnvironmentVariable "FSGG_QUINT_REUSE_SOURCE_SHA256"

let reuseReceipt = Environment.GetEnvironmentVariable "FSGG_QUINT_REUSE_RECEIPT"
let proposalPath = Environment.GetEnvironmentVariable "FSGG_QUINT_FUTURE_PROPOSAL"

let selectionArguments =
    [
        "--mode"
        qualificationMode
        "--protected-mode"
        protectedMode
        "--plan-out"
        selectionPlanPath
    ]
    @ (changedModules
       |> List.collect (fun moduleId -> [ "--changed-module"; moduleId ]))
    @ (changedPaths |> List.collect (fun path -> [ "--changed-path"; path ]))
    @ (changedSurfaces
       |> List.collect (fun surface -> [ "--changed-surface"; surface ]))
    @ (if String.IsNullOrWhiteSpace reuseSource then
           []
       else
           [ "--reuse-source-sha256"; reuseSource ])
    @ (if String.IsNullOrWhiteSpace reuseReceipt then
           []
       else
           [ "--reuse-receipt"; reuseReceipt ])
    @ (if String.IsNullOrWhiteSpace proposalPath then
           []
       else
           [ "--proposal"; proposalPath ])

requireGreen
    "QUINT-QUALIFICATION-CONTRACT"
    root
    "dotnet"
    ([
        "fsi"
        qualificationValidator
        "--"
        "--self-test"
        "--root"
        root
        "--config"
        qualificationConfiguration
     ]
     @ selectionArguments)
    []
|> ignore

let selectionDocument = JsonDocument.Parse(File.ReadAllBytes selectionPlanPath)

let selectedRootIds =
    selectionDocument.RootElement.GetProperty("roots").EnumerateArray()
    |> Seq.map _.GetString()
    |> Set.ofSeq

File.Delete selectionPlanPath

let processInventoryConfiguration =
    JsonDocument.Parse(File.ReadAllBytes qualificationConfiguration)

let formalShardId =
    match Environment.GetEnvironmentVariable "FSGG_QUINT_FORMAL_SHARD" with
    | null
    | "" -> None
    | "base" -> Some "base"
    | value -> Some value

if (measurementOnly || preflightOnly) && Option.isSome formalShardId then
    fail "MEASUREMENT-SHARD" "requires one full inventory pass"

let declaredFormalTestCount =
    match formalShardId with
    | None -> processInventoryConfiguration.RootElement.GetProperty("formalTests").GetArrayLength()
    | Some "base" -> 0
    | Some value ->
        let matches =
            processInventoryConfiguration.RootElement.GetProperty("formalTests").EnumerateArray()
            |> Seq.filter (fun item -> item.GetProperty("id").GetString() = value)
            |> Seq.length

        if matches <> 1 then
            fail "QUINT-FORMAL-SHARD" value

        1

if staticOnly then
    printfn "CANONICAL_QUINT_PROTOCOL_STATIC_OK contract=%s profile=%s" expectedContract expectedProfile
    exit 0

let cacheValue = Environment.GetEnvironmentVariable "FSGG_QUINT_CACHE"

if String.IsNullOrWhiteSpace cacheValue then
    fail "CACHE" "FSGG_QUINT_CACHE is required"

let cache = Path.GetFullPath cacheValue
let quint = Path.Combine(cache, "objects", expectedQuint)
let lmt = Path.Combine(cache, "objects", expectedLmt)
requireFile "QUINT-MISSING" quint
requireFile "LMT-MISSING" lmt

if sha256 quint <> expectedQuint then
    fail "QUINT-DIGEST" (sha256 quint)

if sha256 lmt <> expectedLmt then
    fail "LMT-DIGEST" (sha256 lmt)

let cli =
    match Environment.GetEnvironmentVariable "FSGG_SDD_CLI" with
    | null
    | "" -> "fsgg-sdd"
    | value -> value

let version, _ = requireGreen "CLI-VERSION" root cli [ "--version" ] []

if version.Trim() <> "2.1.0" then
    fail "CLI-VERSION" version

let scratch =
    Path.Combine(Path.GetTempPath(), $"fsgg-canonical-quint-{Guid.NewGuid():N}")

try
    let scratchSource =
        Path.Combine(scratch, "src/FS.GG.Coordination.Protocol/Protocol.md")

    let scratchSelectors =
        Path.Combine(scratch, "src/FS.GG.Coordination.Protocol/Protocol.bindings.json")

    Directory.CreateDirectory(Path.GetDirectoryName scratchSource) |> ignore
    File.Copy(source, scratchSource)
    File.Copy(selectors, scratchSelectors)

    requireGreen
        "AUTHOR"
        root
        cli
        [
            "typed-sdd"
            "author"
            "--root"
            scratch
            "--work"
            "66-gs2-02-11-deterministic-identity"
            "--title"
            "GS2-02.11 deterministic identity"
            "--agent"
            "swift-5db0"
            "--session"
            "gs2-02-11-profile2-r1"
            "--backend"
            "quint-specification-v1"
            "--profile"
            expectedProfile
            "--source"
            "src/FS.GG.Coordination.Protocol/Protocol.md"
            "--bindings"
            "src/FS.GG.Coordination.Protocol/Protocol.bindings.json"
            "--cache"
            cache
        ]
        []
    |> ignore

    requireGreen
        "INSPECT"
        root
        cli
        [
            "typed-sdd"
            "inspect"
            "--root"
            scratch
            "--work"
            "66-gs2-02-11-deterministic-identity"
        ]
        []
    |> ignore

    let generatedRoot =
        Path.Combine(scratch, "readiness/66-gs2-02-11-deterministic-identity")

    let generatedBinding = Path.Combine(generatedRoot, "quint/bindings.fs")
    requireFile "REGEN-MISSING" generatedBinding

    let packages = Environment.GetEnvironmentVariable "NUGET_PACKAGES"

    let packageRoot =
        if String.IsNullOrWhiteSpace packages then
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages")
        else
            packages

    let formatter =
        Path.Combine(packageRoot, "fantomas/8.0.0/tools/net10.0/any/fantomas.dll")

    requireFile "FANTOMAS-MISSING" formatter

    if
        sha256 formatter
        <> "1bb5742abd5fd194575cea1a56f898dd6049bc9c88bf96d60568f66c0d336c70"
    then
        fail "FANTOMAS-DIGEST" "unexpected bytes"

    let info = ProcessStartInfo(observedDotnetHost)
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.ArgumentList.Add formatter
    info.ArgumentList.Add generatedBinding
    use formatterProcess = Process.Start info
    let formatterOutput = formatterProcess.StandardOutput.ReadToEndAsync()
    let formatterError = formatterProcess.StandardError.ReadToEndAsync()
    formatterProcess.WaitForExit()

    if formatterProcess.ExitCode <> 0 then
        fail "FANTOMAS-NORMALIZATION" (formatterOutput.Result + formatterError.Result)

    let comparisons =
        [
            authority, Path.Combine(generatedRoot, "typed-authority.json")
            contract, Path.Combine(generatedRoot, "quint/contract.json")
            binding, generatedBinding
            sourceMap, Path.Combine(generatedRoot, "quint/source-map.json")
            receipt, Path.Combine(generatedRoot, "quint/receipt.json")
        ]

    for expected, actual in comparisons do
        requireFile "REGEN-MISSING" actual

        if not (File.ReadAllBytes(expected).AsSpan().SequenceEqual(File.ReadAllBytes(actual))) then
            fail "STALE-PROJECTION" (Path.GetFileName expected)

    let scratchRetained =
        Path.Combine(scratch, "src/FS.GG.Coordination.Protocol/Generated")

    Directory.CreateDirectory(scratchRetained) |> ignore
    File.Copy(Path.Combine(generatedRoot, "quint/contract.json"), Path.Combine(scratchRetained, "contract.json"), true)

    File.Copy(
        Path.Combine(generatedRoot, "typed-authority.json"),
        Path.Combine(scratchRetained, "typed-authority.json"),
        true
    )

    File.Copy(Path.Combine(generatedRoot, "quint/receipt.json"), Path.Combine(scratchRetained, "receipt.json"), true)

    let scratchCompiledOutputs = Path.Combine(scratch, "compiled-outputs")

    requireGreen
        "COMPILED-OUTPUT-GENERATOR"
        root
        "dotnet"
        [
            "fsi"
            outputGenerator
            "--"
            "--root"
            scratch
            "--output"
            scratchCompiledOutputs
        ]
        []
    |> ignore

    for relative in expectedFiles do
        let expected = Path.Combine(compiledOutputs, relative)
        let actual = Path.Combine(scratchCompiledOutputs, relative)
        requireFile "COMPILED-OUTPUT-REGEN-MISSING" actual

        if not (File.ReadAllBytes(expected).AsSpan().SequenceEqual(File.ReadAllBytes(actual))) then
            fail "COMPILED-OUTPUT-STALE-PROJECTION" relative

    let canonicalReceipt =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(generatedRoot, "quint/receipt.json"))).RootElement

    let canonicalBehavior =
        canonicalReceipt.GetProperty("typedEffectSha256").GetString()

    let canonicalSource = canonicalReceipt.GetProperty("sourceSha256").GetString()
    let canonicalContract = canonicalReceipt.GetProperty("contractSha256").GetString()
    let canonicalSemanticDiffPath = Path.Combine(compiledOutputs, "semantic-diff.json")

    let canonicalSemanticContent =
        JsonDocument.Parse(File.ReadAllBytes canonicalSemanticDiffPath).RootElement.GetProperty("content").GetRawText()

    let authorVariant name (sourceText: string) =
        let variantRoot = Path.Combine(scratch, name)

        let variantSource =
            Path.Combine(variantRoot, "src/FS.GG.Coordination.Protocol/Protocol.md")

        let variantSelectors =
            Path.Combine(variantRoot, "src/FS.GG.Coordination.Protocol/Protocol.bindings.json")

        Directory.CreateDirectory(Path.GetDirectoryName variantSource) |> ignore
        let normalizedSourceText = normalizeSupportedAuthoring sourceText
        File.WriteAllText(variantSource, normalizedSourceText, UTF8Encoding(false))
        File.Copy(selectors, variantSelectors)
        let work = $"66-gs2-02-11-{name}"

        requireGreen
            ($"AUTHOR-{name.ToUpperInvariant()}")
            root
            cli
            [
                "typed-sdd"
                "author"
                "--root"
                variantRoot
                "--work"
                work
                "--title"
                $"GS2-02.11 {name}"
                "--agent"
                "swift-5db0"
                "--session"
                $"gs2-02-11-{name}"
                "--backend"
                "quint-specification-v1"
                "--profile"
                expectedProfile
                "--source"
                "src/FS.GG.Coordination.Protocol/Protocol.md"
                "--bindings"
                "src/FS.GG.Coordination.Protocol/Protocol.bindings.json"
                "--cache"
                cache
            ]
            []
        |> ignore

        let variantReadiness = Path.Combine(variantRoot, "readiness", work)
        let variantGenerated = Path.Combine(variantReadiness, "quint")
        let variantReceiptPath = Path.Combine(variantGenerated, "receipt.json")
        requireFile "VARIANT-RECEIPT" variantReceiptPath

        let variantReceipt =
            JsonDocument.Parse(File.ReadAllBytes variantReceiptPath).RootElement

        if variantReceipt.GetProperty("sourceSha256").GetString() <> sha256 variantSource then
            fail "VARIANT-NORMALIZED-SOURCE" name

        let identity =
            sha256Text sourceText,
            variantReceipt.GetProperty("typedEffectSha256").GetString(),
            variantReceipt.GetProperty("contractSha256").GetString()

        let variantRetained =
            Path.Combine(variantRoot, "src/FS.GG.Coordination.Protocol/Generated")

        Directory.CreateDirectory(variantRetained) |> ignore

        File.Copy(
            Path.Combine(variantReadiness, "typed-authority.json"),
            Path.Combine(variantRetained, "typed-authority.json"),
            true
        )

        File.Copy(Path.Combine(variantGenerated, "contract.json"), Path.Combine(variantRetained, "contract.json"), true)
        File.Copy(Path.Combine(variantGenerated, "receipt.json"), Path.Combine(variantRetained, "receipt.json"), true)
        let variantOutputs = Path.Combine(variantRoot, "compiled-outputs")

        requireGreen
            ($"COMPILED-OUTPUT-{name.ToUpperInvariant()}")
            root
            "dotnet"
            [
                "fsi"
                outputGenerator
                "--"
                "--root"
                variantRoot
                "--output"
                variantOutputs
            ]
            []
        |> ignore

        let semanticDiffPath = Path.Combine(variantOutputs, "semantic-diff.json")
        requireFile "VARIANT-SEMANTIC-DIFF" semanticDiffPath

        let semanticContent =
            JsonDocument.Parse(File.ReadAllBytes semanticDiffPath).RootElement.GetProperty("content").GetRawText()

        let rawTypedEffect = Path.Combine(variantGenerated, "typed-effect.json")

        if File.Exists rawTypedEffect then
            File.Delete rawTypedEffect

        identity, semanticContent, variantOutputs

    let executedEquivalentVariants = ResizeArray<string>()

    let requireEquivalentVariant name sourceText =
        let (variantSource, variantBehavior, variantContract), semanticContent, _ =
            authorVariant name sourceText

        if variantSource = canonicalSource then
            fail "EQUIVALENT-AUTHORING" $"{name}: raw source did not change"

        if variantBehavior <> canonicalBehavior then
            fail "EQUIVALENT-AUTHORING" $"{name}: behavior changed"

        if variantContract <> canonicalContract then
            fail "EQUIVALENT-AUTHORING" $"{name}: public contract changed"

        if semanticContent <> canonicalSemanticContent then
            fail "EQUIVALENT-AUTHORING" $"{name}: semantic diff changed"

        executedEquivalentVariants.Add name

    let canonicalText = File.ReadAllText(source, Encoding.UTF8)
    let triviaFixture = "}\n```\n\n### Prospective trusted pilot permit"

    if not (canonicalText.Contains(triviaFixture, StringComparison.Ordinal)) then
        fail "EQUIVALENT-AUTHORING" "trivia fixture absent"

    let equivalentText =
        canonicalText.Replace(
            triviaFixture,
            "}\n// semantically inert authoring trivia\n```\n\n### Prospective trusted pilot permit"
        )

    requireEquivalentVariant "equivalent-quint-trivia" equivalentText

    let partitionFixture = "\n}\n```\n\n### Prospective trusted pilot permit"

    if not (canonicalText.Contains(partitionFixture, StringComparison.Ordinal)) then
        fail "EQUIVALENT-AUTHORING" "partition fixture absent"

    let partitionedText =
        canonicalText.Replace(
            partitionFixture,
            "\n```\n\n```quint protocol.qnt +=\n}\n```\n\n### Prospective trusted pilot permit"
        )

    requireEquivalentVariant "equivalent-named-block-partition" partitionedText

    let openingFence = "```quint protocol.qnt +=\n"

    if
        not (
            canonicalText.StartsWith("# GS2-03.1", StringComparison.Ordinal)
            && canonicalText.Contains(openingFence, StringComparison.Ordinal)
        )
    then
        fail "EQUIVALENT-AUTHORING" "fence indentation fixture absent"

    let indentedFenceText =
        canonicalText
            .Replace(openingFence, "  ```quint protocol.qnt +=\n", StringComparison.Ordinal)
            .Replace(partitionFixture, "\n}\n  ```\n\n### Prospective trusted pilot permit", StringComparison.Ordinal)

    requireEquivalentVariant "equivalent-fence-indentation" indentedFenceText

    let crlfText = canonicalText.Replace("\n", "\r\n", StringComparison.Ordinal)
    requireEquivalentVariant "equivalent-crlf" crlfText

    let requiredEquivalentVariants =
        [
            "equivalent-quint-trivia"
            "equivalent-named-block-partition"
            "equivalent-fence-indentation"
            "equivalent-crlf"
        ]

    if List.ofSeq executedEquivalentVariants <> requiredEquivalentVariants then
        fail "EQUIVALENT-AUTHORING-COVERAGE" (String.concat "," executedEquivalentVariants)

    let proseFixture =
        "This document is the sole authored source for the coordination protocol baseline."

    if not (canonicalText.Contains(proseFixture, StringComparison.Ordinal)) then
        fail "PROSE-ONLY" "fixture absent"

    let proseText =
        canonicalText.Replace(proseFixture, proseFixture + " Review prose may evolve independently.")

    let (proseSource, proseBehavior, proseContract), proseSemanticContent, _ =
        authorVariant "prose-only" proseText

    if proseSource = canonicalSource then
        fail "PROSE-ONLY" "raw source did not change"

    if proseBehavior <> canonicalBehavior then
        fail "PROSE-ONLY" "behavior changed"

    if proseContract <> canonicalContract then
        fail "PROSE-ONLY" "public contract changed"

    if proseSemanticContent <> canonicalSemanticContent then
        fail "PROSE-ONLY" "semantic diff changed"

    let semanticFixture =
        "  action observeProtocolEvidence: bool = all {\n    evidenceObserved' = true,"

    if not (canonicalText.Contains(semanticFixture, StringComparison.Ordinal)) then
        fail "SEMANTIC-CHANGE" "fixture absent"

    let semanticText =
        canonicalText.Replace(
            semanticFixture,
            "  action observeProtocolEvidence: bool = all {\n    evidenceObserved' = lifecycleStatusCurrent,"
        )

    let (_, semanticBehavior, semanticContract), _, semanticOutputs =
        authorVariant "semantic-change" semanticText

    if semanticBehavior = canonicalBehavior then
        fail "SEMANTIC-CHANGE" "behavior did not change"

    if semanticContract = canonicalContract then
        fail "SEMANTIC-CHANGE" "public contract did not change"

    let readSemanticRows path =
        let document = JsonDocument.Parse(File.ReadAllBytes path)

        let rows =
            document.RootElement.GetProperty("content").GetProperty("rows").EnumerateArray()
            |> Seq.toList

        let paths = rows |> List.map (fun row -> row.GetProperty("path").GetString())

        if paths <> List.sort paths then
            fail "SEMANTIC-DIFF-ORDER" path

        for ordinal, row in rows |> List.indexed do
            if row.GetProperty("ordinal").GetInt32() <> ordinal + 1 then
                fail "SEMANTIC-DIFF-ORDINAL" path

        rows
        |> Seq.map (fun row -> row.GetProperty("path").GetString(), row.GetProperty("valueSha256").GetString())
        |> Map.ofSeq

    let canonicalRows = readSemanticRows canonicalSemanticDiffPath

    let changedRows =
        readSemanticRows (Path.Combine(semanticOutputs, "semantic-diff.json"))

    let changedPaths =
        Set.union (canonicalRows |> Map.keys |> Set.ofSeq) (changedRows |> Map.keys |> Set.ofSeq)
        |> Set.filter (fun path -> Map.tryFind path canonicalRows <> Map.tryFind path changedRows)

    if not (changedPaths.Contains "/behavioralSha256") then
        fail "SEMANTIC-DIFF-BEHAVIOR" "missing"

    if changedPaths.Count < 2 then
        fail "SEMANTIC-DIFF-CONTRACT" "no public contract row changed"

    let requireGeneratorRed (name: string) (fieldName: string) (replacement: string) (expectedCode: string) =
        let mutantRoot = Path.Combine(scratch, "generator-mutant-" + name)

        let mutantSource =
            Path.Combine(mutantRoot, "src/FS.GG.Coordination.Protocol/Protocol.md")

        let mutantRetained =
            Path.Combine(mutantRoot, "src/FS.GG.Coordination.Protocol/Generated")

        Directory.CreateDirectory(Path.GetDirectoryName mutantSource) |> ignore
        Directory.CreateDirectory(mutantRetained) |> ignore
        File.Copy(source, mutantSource)
        File.Copy(authority, Path.Combine(mutantRetained, "typed-authority.json"))
        File.Copy(receipt, Path.Combine(mutantRetained, "receipt.json"))

        let mutantContract = JsonNode.Parse(File.ReadAllText(contract))
        let catalogue = mutantContract["catalogue"].AsArray()

        let specification =
            catalogue
            |> Seq.find (fun entry -> entry["id"].GetValue<string>() = "COUT-Specification")

        let fields = (specification["value"]["fields"]).AsArray()

        let field =
            fields |> Seq.find (fun entry -> entry["name"].GetValue<string>() = fieldName)

        field["value"]["value"] <- JsonValue.Create(replacement)

        File.WriteAllText(
            Path.Combine(mutantRetained, "contract.json"),
            mutantContract.ToJsonString(),
            UTF8Encoding(false)
        )

        let exitCode, output, error =
            run root "dotnet" [ "fsi"; outputGenerator; "--"; "--root"; mutantRoot ] []

        if exitCode = 0 then
            fail "GENERATOR-NEGATIVE-CONTROL" ($"{name}: mutant passed")

        if not ((output + "\n" + error).Contains($"code={expectedCode}", StringComparison.Ordinal)) then
            fail "GENERATOR-NEGATIVE-CONTROL" ($"{name}: expected={expectedCode}; stdout={output}; stderr={error}")

    let supportedVersions =
        [
            expectedSourceVersion
            expectedExtractorVersion
            expectedQuintVersion
            expectedProfile
            expectedSchemaVersion
        ]

    let versionMutation index replacement =
        supportedVersions
        |> List.mapi (fun position value -> if position = index then replacement else value)
        |> String.concat "|"

    requireGeneratorRed
        "source-version"
        "versionContract"
        (versionMutation 0 "fsgg.quint.literate-source/999")
        "SOURCE-VERSION"

    requireGeneratorRed
        "extractor-version"
        "versionContract"
        (versionMutation 1 "quint-specification-v1@FS.GG.SDD.Artifacts/999")
        "EXTRACTOR-VERSION"

    requireGeneratorRed "quint-version" "versionContract" (versionMutation 2 "sha256:substituted") "QUINT-VERSION"

    requireGeneratorRed
        "profile-version"
        "versionContract"
        (versionMutation 3 "fsgg-quint-profile/999")
        "PROFILE-VERSION"

    requireGeneratorRed
        "schema-version"
        "versionContract"
        (versionMutation 4 "fsgg.quint.compiled-contract/v999")
        "SCHEMA-VERSION"

    requireGeneratorRed "normalization-authority" "normalizationAuthority" "raw-markdown" "NORMALIZATION-AUTHORITY"
    requireGeneratorRed "semantic-diff-contract" "semanticDiffContract" "unordered-raw-values" "SEMANTIC-DIFF-CONTRACT"

    let qnt = Path.Combine(generatedRoot, "quint/protocol.qnt")
    requireGreen "QUINT-TYPECHECK" scratch quint [ "typecheck"; qnt ] [] |> ignore

    let extractQuintTestFence (markdown: string) =
        let lines = File.ReadAllLines markdown
        let mutable inside = false
        let mutable fences = 0
        let body = ResizeArray<string>()

        for line in lines do
            if line.Trim() = "```quint-test" then
                if inside then
                    fail "QUINT-TEST-FENCE" "nested"

                inside <- true
                fences <- fences + 1
            elif inside && line.Trim() = "```" then
                inside <- false
            elif inside then
                body.Add line

        if inside then
            fail "QUINT-TEST-FENCE" "unterminated"

        if fences <> 1 then
            fail "QUINT-TEST-FENCE" ($"expected-one; actual=%d{fences}")

        String.Join(Environment.NewLine, body)

    let q2Qnt = Path.Combine(scratch, "protocol-q2.qnt")

    let q2Source =
        File.ReadAllText(qnt)
        + Environment.NewLine
        + extractQuintTestFence (source)
        + Environment.NewLine

    File.WriteAllText(q2Qnt, q2Source)

    requireGreen "QUINT-Q2-TYPECHECK" scratch quint [ "typecheck"; q2Qnt ] []
    |> ignore

    // Whole canonical modules are independent profile inputs. No IR rows are merged or removed.
    let preflightInputs = System.Collections.Generic.Dictionary<string, string>()
    let markdownLines = File.ReadAllLines source

    let testHeaders =
        markdownLines
        |> Array.mapi (fun i line -> i, line)
        |> Array.filter (fun (_, line) -> line.Trim() = "```quint-test")

    if testHeaders.Length <> 1 then
        fail "PREFLIGHT-PROJECTION-FENCE" "expected-one"

    let fenceStart = fst testHeaders[0]

    let fenceEnd =
        [ fenceStart + 1 .. markdownLines.Length - 1 ]
        |> List.find (fun i -> markdownLines[i].Trim() = "```")

    let moduleHeaders =
        [ fenceStart + 1 .. fenceEnd - 1 ]
        |> List.filter (fun i -> Regex.IsMatch(markdownLines[i], @"^module [A-Za-z0-9_]+ \{$"))

    let parentDigest = sha256 source
    let projectionReceipts = ResizeArray<string * string * string * string>()

    let writeProjected label names =
        let directory = Path.Combine(scratch, "preflight-" + label)
        Directory.CreateDirectory directory |> ignore

        let spans =
            names
            |> List.map (fun name ->
                let hits =
                    moduleHeaders
                    |> List.filter (fun i -> markdownLines[i] = "module " + name + " {")

                if hits.Length <> 1 then
                    fail "PREFLIGHT-PROJECTION-MODULE" name

                let first = hits.Head

                let mutable last =
                    moduleHeaders
                    |> List.tryFind (fun i -> i > first)
                    |> Option.defaultValue fenceEnd

                while last > first && String.IsNullOrWhiteSpace markdownLines[last - 1] do
                    last <- last - 1

                if markdownLines[last - 1] <> "}" then
                    fail "PREFLIGHT-PROJECTION-RANGE" name

                name, first, last)

        let selected = Set.ofList names

        for _, first, last in spans do
            for i in first .. last - 1 do
                let imported = Regex.Match(markdownLines[i], @"^  import ([A-Za-z0-9_]+)\.\*")

                if imported.Success && not (selected.Contains imported.Groups[1].Value) then
                    fail "PREFLIGHT-PROJECTION-IMPORT" imported.Groups[1].Value

        let projected = Array.create markdownLines.Length ""
        let first = spans |> List.map (fun (_, first, _) -> first) |> List.min
        let last = spans |> List.map (fun (_, _, last) -> last) |> List.max
        projected[first - 1] <- "```quint protocol.qnt +="
        projected[last] <- "```"

        for _, first, last in spans do
            Array.blit markdownLines first projected first (last - first)

        let projectedPath = Path.Combine(directory, "Projection.md")
        File.WriteAllLines(projectedPath, projected, UTF8Encoding(false))

        let spanReceipts =
            spans
            |> List.map (fun (name, first, last) ->
                {|
                    moduleName = name
                    startLine = first + 1
                    endLine = last
                    sha256 = sha256Text (String.Join(Environment.NewLine, markdownLines[first .. last - 1]))
                |})

        let actions =
            spans
            |> List.map (fun (name, first, last) ->
                let declaration =
                    if name.EndsWith("Replay", StringComparison.Ordinal) then
                        "replayInit"
                    else
                        "init"

                let matches =
                    [ first .. last - 1 ]
                    |> List.filter (fun i ->
                        markdownLines[i].StartsWith("  action " + declaration + " =", StringComparison.Ordinal))

                if matches.Length <> 1 then
                    fail "PREFLIGHT-PROJECTION-ACTION" name

                let line = matches.Head + 1
                // Published native range for the inline admission record includes its next line.
                // This selector is source-specific, retained from the independently qualified pilot.
                let endLine = if name = "PreflightAdmissionModel" then line + 1 else line

                {|
                    id = "ACT-" + name + "-" + declaration
                    ``module`` = name
                    declaration = declaration
                    source =
                        {|
                            path = "Projection.md"
                            start = {| line = line; column = 1 |}
                            ``end`` = {| line = endLine; column = 4 |}
                        |}
                |})

        let bindingsPath = Path.Combine(directory, "bindings.json")

        File.WriteAllText(
            bindingsPath,
            JsonSerializer.Serialize(
                {|
                    schema = "fsgg.quint.general-bindings/v1"
                    profile = expectedProfile
                    moduleName = "PreflightProjectionGenerated"
                    exports = Array.empty<string>
                    actions = actions
                |}
            ),
            UTF8Encoding(false)
        )

        let projectedDigest = sha256 projectedPath

        let authorCode, authorOutput, authorError, authorElapsed, authorPeak =
            runMeasured
                60000
                root
                cli
                [
                    "typed-sdd"
                    "author"
                    "--root"
                    directory
                    "--work"
                    "partition-profile2"
                    "--title"
                    "Canonical preflight projection"
                    "--agent"
                    "coordination"
                    "--session"
                    "v2-preflight-coordination-20261003"
                    "--backend"
                    "quint-specification-v1"
                    "--profile"
                    expectedProfile
                    "--source"
                    "Projection.md"
                    "--bindings"
                    "bindings.json"
                    "--cache"
                    cache
                ]
                []

        if authorCode <> 0 then
            fail "PREFLIGHT-PROJECTION-AUTHOR" (label + ":" + authorOutput + authorError)

        let inspectCode, inspectOutput, inspectError, inspectElapsed, inspectPeak =
            runMeasured
                60000
                root
                cli
                [ "typed-sdd"; "inspect"; "--root"; directory; "--work"; "partition-profile2" ]
                []

        if inspectCode <> 0 then
            fail "PREFLIGHT-PROJECTION-INSPECT" (label + ":" + inspectOutput + inspectError)

        if
            authorElapsed <= 0L
            || inspectElapsed <= 0L
            || authorPeak <= 0
            || inspectPeak <= 0
        then
            fail "PREFLIGHT-PROJECTION-METRIC" label

        let generated = Path.Combine(directory, "readiness/partition-profile2/quint")
        let projectedQnt = Path.Combine(generated, "protocol.qnt")

        use projectedReceipt =
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(generated, "receipt.json")))

        let receipt = projectedReceipt.RootElement

        if
            receipt.GetProperty("sourceSha256").GetString() <> projectedDigest
            || receipt.GetProperty("toolchainSha256").GetString() <> expectedToolchain
        then
            fail "PREFLIGHT-PROJECTION-RECEIPT" label

        use projectedAuthority =
            JsonDocument.Parse(
                File.ReadAllBytes(Path.Combine(directory, "readiness/partition-profile2/typed-authority.json"))
            )

        if
            projectedAuthority.RootElement.GetProperty("packageIdentity").GetString()
            <> expectedPackage
        then
            fail "PREFLIGHT-PROJECTION-PACKAGE" label

        use native =
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(generated, "typed-effect.json")))

        let keys (field: string) =
            native.RootElement.GetProperty(field).EnumerateObject()
            |> Seq.map _.Name
            |> Set.ofSeq

        if keys "types" <> keys "effects" then
            fail "PREFLIGHT-PROJECTION-NATIVE-KEYS" label

        if sha256 source <> parentDigest || sha256 projectedPath <> projectedDigest then
            fail "PREFLIGHT-PROJECTION-PARENT-DRIFT" label

        for _, first, last in spans do
            if projected[first .. last - 1] <> markdownLines[first .. last - 1] then
                fail "PREFLIGHT-PROJECTION-SPAN" label

        File.WriteAllText(
            Path.Combine(directory, "projection.json"),
            JsonSerializer.Serialize(
                {|
                    schema = "fsgg.canonical-module-projection/1"
                    parentPath = source
                    parentSha256 = parentDigest
                    derivedSha256 = projectedDigest
                    quintSha256 = expectedQuint
                    extractorSha256 = expectedLmt
                    compilerPackage = expectedPackage
                    toolchainSha256 = expectedToolchain
                    selected = names
                    spans = spanReceipts
                    tableRows = (keys "table" |> Set.count)
                    typeRows = (keys "types" |> Set.count)
                    effectRows = (keys "effects" |> Set.count)
                    authorElapsedMs = authorElapsed
                    authorPeakMiB = authorPeak
                    inspectElapsedMs = inspectElapsed
                    inspectPeakMiB = inspectPeak
                    typedEffectSha256 = sha256 (Path.Combine(generated, "typed-effect.json"))
                    retainsAllNativeRows = true
                    disposition = "independent-profile-input-not-aggregate-contract"
                |}
            ),
            UTF8Encoding(false)
        )

        projectionReceipts.Add(directory, projectedPath, projectedDigest, sha256 projectedQnt)

        for name in names do
            preflightInputs[name] <- projectedQnt

    writeProjected "artifact" [ "PreflightArtifactModel"; "PreflightArtifactReplay" ]
    writeProjected "admission" [ "PreflightAdmissionModel" ]
    writeProjected "observation" [ "PreflightObservationModel"; "PreflightObservationReplay" ]

    let projectionRefusal directory path digest qntDigest =
        try
            use manifest =
                JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "projection.json")))

            let m = manifest.RootElement
            let get (name: string) = m.GetProperty(name).GetString()

            if sha256 source <> parentDigest || get "parentSha256" <> parentDigest then
                Some "parent"
            elif get "derivedSha256" <> digest || sha256 path <> digest then
                Some "derived"
            elif get "quintSha256" <> expectedQuint || sha256 quint <> expectedQuint then
                Some "quint"
            elif get "extractorSha256" <> expectedLmt || sha256 lmt <> expectedLmt then
                Some "extractor"
            elif
                get "compilerPackage" <> expectedPackage
                || get "toolchainSha256" <> expectedToolchain
            then
                Some "compiler"
            elif
                not (m.GetProperty("retainsAllNativeRows").GetBoolean())
                || get "typedEffectSha256"
                   <> sha256 (Path.Combine(directory, "readiness/partition-profile2/quint/typed-effect.json"))
            then
                Some "native-rows"
            elif
                sha256 (Path.Combine(directory, "readiness/partition-profile2/quint/protocol.qnt"))
                <> qntDigest
            then
                Some "native-input"
            else
                let selected =
                    m.GetProperty("selected").EnumerateArray()
                    |> Seq.map _.GetString()
                    |> Seq.toArray

                let spans = m.GetProperty("spans").EnumerateArray() |> Seq.toArray

                if
                    selected.Length = 0
                    || Set.count (Set.ofArray selected) <> selected.Length
                    || spans.Length <> selected.Length
                then
                    Some "selection"
                else
                    let reconstructed = Array.create markdownLines.Length ""

                    let ranges =
                        spans
                        |> Array.map (fun span ->
                            let name = span.GetProperty("moduleName").GetString()
                            let first = span.GetProperty("startLine").GetInt32() - 1
                            let last = span.GetProperty("endLine").GetInt32()

                            let hits =
                                moduleHeaders
                                |> List.filter (fun i -> markdownLines[i] = "module " + name + " {")

                            let ownerEnd =
                                hits
                                |> List.tryHead
                                |> Option.map (fun start ->
                                    let mutable finish =
                                        moduleHeaders
                                        |> List.tryFind (fun i -> i > start)
                                        |> Option.defaultValue fenceEnd

                                    while finish > start && String.IsNullOrWhiteSpace markdownLines[finish - 1] do
                                        finish <- finish - 1

                                    finish)

                            if
                                not (Array.contains name selected)
                                || hits <> [ first ]
                                || ownerEnd <> Some last
                                || span.GetProperty("sha256").GetString()
                                   <> sha256Text (String.Join(Environment.NewLine, markdownLines[first .. last - 1]))
                            then
                                invalidOp "span"

                            Array.blit markdownLines first reconstructed first (last - first)
                            first, last)

                    let first = ranges |> Array.map fst |> Array.min
                    let last = ranges |> Array.map snd |> Array.max
                    reconstructed[first - 1] <- "```quint protocol.qnt +="
                    reconstructed[last] <- "```"

                    if File.ReadAllLines path <> reconstructed then
                        Some "reconstruction"
                    else
                        None
        with _ ->
            Some "malformed"

    // Independent provenance mutations exercise the actual pre-use refusal guard.
    let directory, path, digest, qntDigest = projectionReceipts[0]
    let manifestPath = Path.Combine(directory, "projection.json")
    let originalManifest = File.ReadAllBytes manifestPath
    let originalDerived = File.ReadAllBytes path

    for field in
        [
            "parentSha256"
            "derivedSha256"
            "quintSha256"
            "extractorSha256"
            "compilerPackage"
            "toolchainSha256"
            "typedEffectSha256"
        ] do
        try
            let mutant = JsonNode.Parse originalManifest
            mutant[field] <- JsonValue.Create("substituted")
            File.WriteAllText(manifestPath, mutant.ToJsonString())

            if projectionRefusal directory path digest qntDigest |> Option.isNone then
                fail "PREFLIGHT-PROJECTION-CONTROL" field
        finally
            File.WriteAllBytes(manifestPath, originalManifest)

    try
        let mutant = JsonNode.Parse originalManifest
        let spans = mutant["spans"].AsArray()
        let span = spans[0].AsObject()
        span["startLine"] <- JsonValue.Create(1)
        File.WriteAllText(manifestPath, mutant.ToJsonString())

        if projectionRefusal directory path digest qntDigest |> Option.isNone then
            fail "PREFLIGHT-PROJECTION-CONTROL" "span"
    finally
        File.WriteAllBytes(manifestPath, originalManifest)

    try
        File.AppendAllText(path, "substituted source")

        if projectionRefusal directory path digest qntDigest |> Option.isNone then
            fail "PREFLIGHT-PROJECTION-CONTROL" "reconstruction"
    finally
        File.WriteAllBytes(path, originalDerived)

    if projectionRefusal directory path digest qntDigest |> Option.isSome then
        fail "PREFLIGHT-PROJECTION-CONTROL" "restored"

    let validateProjectionUse main =
        for directory, path, digest, qntDigest in projectionReceipts do
            match projectionRefusal directory path digest qntDigest with
            | Some reason -> fail "PREFLIGHT-PROJECTION-USE" (main + ":" + reason)
            | None -> ()

        preflightInputs[main]

    let interactionMeasurements = ResizeArray<string * int64 * int * int64>()
    let samplingMeasurements = ResizeArray<string * int64 * int * int64>()

    let runPreflight label arguments =
        physicalScope <- Some("sampling/" + label)

        let code, output, error, elapsed, peak =
            runMeasured 30000 scratch quint arguments []

        let bytes =
            int64 (Encoding.UTF8.GetByteCount(output) + Encoding.UTF8.GetByteCount(error))

        if elapsed <= 0L || peak <= 0 || bytes <= 0L then
            fail "MEASUREMENT-SAMPLING-METRIC" label

        samplingMeasurements.Add(label, elapsed, peak, bytes)
        physicalScope <- None
        code, output, error

    // Consumer-owned, bounded preflight partitions use the published compiler's exact projection.
    // Sampling and causal counterexamples are explicitly separate from native exhaustive obligations.
    let preflightBound =
        [
            "--max-samples"
            "100"
            "--max-steps"
            "12"
            "--seed"
            "37"
            "--verbosity"
            "1"
        ]

    for main in
        [
            "PreflightArtifactModel"
            "PreflightAdmissionModel"
            "PreflightObservationModel"
        ] do
        let code, output, error =
            runPreflight
                main
                ([
                    "run"
                    validateProjectionUse main
                    "--main"
                    main
                    "--invariant"
                    "safety"
                    "--witnesses"
                    "done"
                 ]
                 @ preflightBound)

        if code <> 0 then
            fail "PREFLIGHT-SAMPLED-INVARIANT" (main + ":" + output + error)

        if not (Regex.IsMatch(output, @"done was witnessed in [1-9][0-9]* trace")) then
            fail "PREFLIGHT-REACHABILITY" main

    for main, step, invariant in
        [
            "PreflightArtifactModel", "mutantStep", "safety"
            "PreflightArtifactModel", "invalidationMutantStep", "safety"
            "PreflightAdmissionModel", "mutantStep", "safety"
            "PreflightObservationModel", "mutantStep", "cleanupSafety"
            "PreflightObservationModel", "filterMutantStep", "custodySafety"
            "PreflightObservationModel", "jsonMutantStep", "custodySafety"
            "PreflightObservationModel", "leaderMutantStep", "custodySafety"
            "PreflightObservationModel", "timeoutMutantStep", "custodySafety"
        ] do
        let code, output, error =
            runPreflight
                (main + ":" + step)
                ([
                    "run"
                    validateProjectionUse main
                    "--main"
                    main
                    "--step"
                    step
                    "--invariant"
                    invariant
                 ]
                 @ preflightBound)

        if
            code = 0
            || not ((output + error).Contains("Invariant violated", StringComparison.Ordinal))
        then
            fail "PREFLIGHT-CAUSAL-MUTANT" (main + ":" + step)

    for main, steps in [ "PreflightArtifactReplay", "8"; "PreflightObservationReplay", "18" ] do
        let actualTrace = Path.Combine(scratch, main + ".itf.json")

        let code, output, error =
            runPreflight
                main
                [
                    "run"
                    validateProjectionUse main
                    "--main"
                    main
                    "--init"
                    "replayInit"
                    "--step"
                    "replayStep"
                    "--invariant"
                    "safety"
                    "--max-samples"
                    "1"
                    "--max-steps"
                    steps
                    "--seed"
                    "37"
                    "--verbosity"
                    "1"
                    "--out-itf"
                    actualTrace
                ]

        if code <> 0 then
            fail "PREFLIGHT-REPLAY-TRACE" (main + ":" + output + error)

        use actual = JsonDocument.Parse(File.ReadAllBytes actualTrace)

        use retainedTrace =
            JsonDocument.Parse(
                File.ReadAllBytes(
                    Path.Combine(
                        root,
                        "tests/FS.GG.Coordination.Orchestration.Execution.Tests/Fixtures/preflight",
                        main + ".itf.json"
                    )
                )
            )

        let interactionTrace =
            Path.Combine(scratch, "preflight-interaction-" + main + ".itf.json")

        physicalScope <- Some("interaction/" + main)

        let interactionCode, interactionOutput, interactionError, interactionElapsed, interactionPeak =
            runMeasured
                30000
                scratch
                quint
                [
                    "run"
                    q2Qnt
                    "--main"
                    main
                    "--init"
                    "replayInit"
                    "--step"
                    "replayStep"
                    "--invariant"
                    "safety"
                    "--max-samples"
                    "1"
                    "--max-steps"
                    steps
                    "--seed"
                    "37"
                    "--verbosity"
                    "1"
                    "--out-itf"
                    interactionTrace
                ]
                []

        if interactionCode <> 0 then
            fail "PREFLIGHT-INTERACTION-TRACE" (main + ":" + interactionOutput + interactionError)

        let interactionBytes = int64 (FileInfo(interactionTrace).Length)

        if interactionElapsed <= 0L || interactionPeak <= 0 || interactionBytes <= 0L then
            fail "PREFLIGHT-INTERACTION-METRIC" main

        interactionMeasurements.Add(main, interactionElapsed, interactionPeak, interactionBytes)
        physicalScope <- None

        if measurementOnly then
            verifyMeasurementDirectory ()

            File.Copy(
                interactionTrace,
                Path.Combine(measurementDirectory.Value, "interaction-" + main + ".itf.json"),
                false
            )

        use interaction = JsonDocument.Parse(File.ReadAllBytes interactionTrace)
        // Isolated, full-core/test-fence, and retained production-driver traces agree causally.
        for key in [ "vars"; "states" ] do
            if
                actual.RootElement.GetProperty(key).GetRawText()
                <> interaction.RootElement.GetProperty(key).GetRawText()
            then
                fail "PREFLIGHT-INTERACTION-DRIFT" (main + ":" + key)
        // Tool timestamps are observations; causal states and their variables must match exactly.
        for key in [ "vars"; "states" ] do
            if
                actual.RootElement.GetProperty(key).GetRawText()
                <> retainedTrace.RootElement.GetProperty(key).GetRawText()
            then
                fail "PREFLIGHT-REPLAY-DRIFT" (main + ":" + key)

    if preflightOnly then
        if samplingMeasurements.Count <> 13 || interactionMeasurements.Count <> 2 then
            fail "PREFLIGHT-FOCUSED-COVERAGE" "missing observation"

        verifyMeasurementDirectory ()

        let options =
            JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true)

        let metrics rows =
            rows
            |> Seq.map (fun (id, elapsed, peak, bytes) ->
                {|
                    id = id
                    elapsedMs = elapsed
                    peakMiB = peak
                    artifactBytes = bytes
                |})
            |> Seq.toArray

        for directory, _, _, _ in projectionReceipts do
            File.Copy(
                Path.Combine(directory, "projection.json"),
                Path.Combine(measurementDirectory.Value, Path.GetFileName(directory) + ".json"),
                false
            )

        let report =
            {|
                schema = "fsgg.preflight-focused-observation/1"
                disposition = "bounded-sampling-and-native-replay-not-full-qualification"
                sourceSha256 = expectedSource
                compilerPackage = expectedPackage
                toolchainSha256 = expectedToolchain
                sampling = metrics samplingMeasurements
                interaction = metrics interactionMeasurements
            |}

        use stream =
            new FileStream(
                Path.Combine(measurementDirectory.Value, "preflight.json"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            )

        JsonSerializer.Serialize(stream, report, options)
        stream.Flush()

        printfn
            "PREFLIGHT_FOCUSED_OBSERVED disposition=not-full-qualification output=%s"
            measurementCustody.Value.NamedPath

        Directory.Delete(scratch, true)
        exit 0

    // The pinned Choreo modules are appended to the canonical Q2 source and are
    // compiled above as part of that source identity. Quint 0.32's TLC flattener,
    // however, resolves every later module while flattening an unrelated legacy
    // root and loses names imported through Choreo's parameterized module alias.
    // Select exact canonical regions for each root after the combined typecheck.
    // Legacy roots retain the pre-Choreo prefix; C5's two hosted-writer roots use
    // the pinned Choreo region, including qualification-only negative controls.
    let choreoBoundary = "// BEGIN PINNED quint-co/choreo spells/basicSpells.qnt"
    let choreoBoundaryIndex = q2Source.IndexOf(choreoBoundary, StringComparison.Ordinal)

    if choreoBoundaryIndex < 0 then
        fail "QUINT-CHOREO-BOUNDARY" "missing pinned basic-spells boundary"

    let qualificationQnt = Path.Combine(scratch, "protocol-q2-legacy-qualification.qnt")
    File.WriteAllText(qualificationQnt, q2Source.Substring(0, choreoBoundaryIndex), UTF8Encoding(false))

    let choreoTestsIndex =
        q2Source.IndexOf("module O2HostedWriterChoreoTests {", choreoBoundaryIndex, StringComparison.Ordinal)

    if choreoTestsIndex <= choreoBoundaryIndex then
        fail "QUINT-CHOREO-BOUNDARY" "missing Choreo tests boundary"

    let choreoQualificationQnt =
        Path.Combine(scratch, "protocol-q2-choreo-qualification.qnt")

    File.WriteAllText(
        choreoQualificationQnt,
        q2Source.Substring(choreoBoundaryIndex, choreoTestsIndex - choreoBoundaryIndex),
        UTF8Encoding(false)
    )

    let formalQnt main =
        match main with
        | "O2HostedWriterChoreoProgressQualification"
        | "O2HostedWriterChoreoFaultQualification" -> choreoQualificationQnt
        | _ -> qualificationQnt

    use qualificationConfigurationDocument =
        JsonDocument.Parse(File.ReadAllBytes qualificationConfiguration)

    let artifactBudgets =
        qualificationConfigurationDocument.RootElement.GetProperty("roots").EnumerateArray()
        |> Seq.map (fun item ->
            item.GetProperty("id").GetString(), item.GetProperty("budget").GetProperty("artifactBytes").GetInt32())
        |> Map.ofSeq

    let formalTests =
        qualificationConfigurationDocument.RootElement.GetProperty("formalTests").EnumerateArray()
        |> Seq.map (fun item ->
            item.GetProperty("id").GetString(),
            item.GetProperty("main").GetString(),
            item.GetProperty("init").GetString(),
            item.GetProperty("step").GetString(),
            item.GetProperty("invariant").GetString(),
            item.GetProperty("witness").GetString(),
            item.GetProperty("temporal").GetString(),
            item.GetProperty("invalid").GetString(),
            item.GetProperty("removedStep").GetString(),
            item.GetProperty("violatedTemporal").GetString(),
            item.GetProperty("blockedInvariant").GetString(),
            item.GetProperty("counterexample").GetString(),
            item.GetProperty("counterexampleTrace").GetString(),
            item.GetProperty("counterexampleManifest").GetString(),
            item.GetProperty("budget").GetProperty("depth").GetInt32(),
            item.GetProperty("budget").GetProperty("states").GetInt32(),
            item.GetProperty("budget").GetProperty("transitions").GetInt32(),
            item.GetProperty("budget").GetProperty("samples").GetInt32(),
            item.GetProperty("budget").GetProperty("elapsedMs").GetInt32(),
            item.GetProperty("budget").GetProperty("peakMiB").GetInt32(),
            item.GetProperty("budget").GetProperty("artifactBytes").GetInt32())
        |> Seq.filter (fun (id, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _) ->
            match formalShardId with
            | None -> true
            | Some "base" -> false
            | Some selected -> id = selected)
        |> Seq.toList

    formalSafeSteps <-
        formalTests
        |> List.map (fun (_, main, _, step, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _) -> main, step)
        |> Set.ofList

    formalInvalidSteps <-
        formalTests
        |> List.map (fun (_, main, _, _, _, _, _, invalid, _, _, _, _, _, _, _, _, _, _, _, _, _) -> main, invalid)
        |> Set.ofList

    formalRemovedSteps <-
        formalTests
        |> List.map (fun (_, main, _, _, _, _, _, _, removedStep, _, _, _, _, _, _, _, _, _, _, _, _) ->
            main, removedStep)
        |> Set.ofList

    formalInventoryReady <- true

    // This is the executable qualification plan. Each category corresponds to one named
    // orchestration obligation; completion compares the actual invocation multiset to this plan.
    // Adding a formal test extends five explicit categories rather than changing opaque totals.
    let expectedBaseNonverifyCount, expectedBaseVerifyCount =
        match formalShardId with
        | Some selected when selected <> "base" -> 5, 0
        | _ -> 63, 14

    for label, count in
        [
            "external/base", 31
            "quint/preflight-sampling", 13
            "quint/preflight-interaction", 2
            "quint/base-nonverify", expectedBaseNonverifyCount
            "quint/base-verify", expectedBaseVerifyCount
            "quint/selected-root", selectedRootIds.Count
            "quint/formal-simulation", declaredFormalTestCount
            "quint/formal-temporal", declaredFormalTestCount
            "quint/formal-safety-mutant", declaredFormalTestCount
            "quint/formal-counterexample-temporal", 2 * declaredFormalTestCount
            "quint/formal-counterexample-projection", 2 * declaredFormalTestCount
        ] do
        expectedInvocationInventory[label] <- count
        actualInvocationInventory.TryAdd(label, 0) |> ignore

    // The base qualification suite has 71 established red outcomes plus eight preflight mutants. Every formal scenario
    // contributes one safety mutant, two TLC reproductions, and two Rust projections.
    let expectedRejectedProcessCount = 79 + 5 * declaredFormalTestCount
    let rootArtifactDirectory = Path.Combine(scratch, "root-artifacts")
    Directory.CreateDirectory rootArtifactDirectory |> ignore
    let rootArtifactDigests = ResizeArray<string * string>()

    let rootMeasurements =
        System.Collections.Generic.Dictionary<string, int * int * int * int64 * int * int64>()

    let rootConfiguration =
        processInventoryConfiguration.RootElement.GetProperty("roots").EnumerateArray()
        |> Seq.map (fun r -> r.GetProperty("id").GetString(), r.Clone())
        |> Map.ofSeq

    let selectionImports =
        processInventoryConfiguration.RootElement.GetProperty("modules").EnumerateArray()
        |> Seq.map (fun m ->
            m.GetProperty("id").GetString(),
            m.GetProperty("selectionImports").EnumerateArray()
            |> Seq.map _.GetString()
            |> Seq.toList)
        |> Map.ofSeq

    let rec measuredDependencyDepth owner =
        match selectionImports[owner] with
        | [] -> 1
        | dependencies -> 1 + (dependencies |> List.map measuredDependencyDepth |> List.max)

    let runRoot rootId artifactPath arguments =
        physicalScope <- Some("root/" + rootId)

        if measurementOnly then
            let budget = rootConfiguration[rootId].GetProperty("budget")

            let actualArguments =
                if rootConfiguration[rootId].GetProperty("mode").GetString() = "state" then
                    arguments
                    |> List.map (fun argument -> if argument = "--out" then "--out-itf" else argument)
                else
                    arguments

            let code, output, error, elapsed, peak =
                runMeasured (budget.GetProperty("elapsedMs").GetInt32()) scratch quint actualArguments []

            if code <> 0 then
                fail "MEASUREMENT-ROOT-FAILED" (rootId + ":" + output + error)

            use artifact = JsonDocument.Parse(File.ReadAllBytes artifactPath)
            let mutable trace = Unchecked.defaultof<JsonElement>

            let states, samples =
                if artifact.RootElement.TryGetProperty("states", &trace) then
                    let observed = Regex.Match(output + error, @"out of (\d+) explored")

                    if not observed.Success then
                        fail "MEASUREMENT-ROOT-SAMPLES" rootId

                    trace.GetArrayLength(), Int32.Parse observed.Groups[1].Value
                else
                    let passed = artifact.RootElement.GetProperty("passed").GetArrayLength()

                    if
                        artifact.RootElement.GetProperty("failed").GetArrayLength() <> 0
                        || artifact.RootElement.GetProperty("ignored").GetArrayLength() <> 0
                    then
                        fail "MEASUREMENT-ROOT-PARTIAL" rootId

                    passed, passed

            let depth =
                measuredDependencyDepth (rootConfiguration[rootId].GetProperty("module").GetString())

            let bytes = FileInfo(artifactPath).Length

            if
                elapsed <= 0L
                || peak <= 0
                || bytes <= 0L
                || samples < 1
                || states < 1
                || depth > budget.GetProperty("depth").GetInt32()
                || states > budget.GetProperty("states").GetInt32()
                || samples > budget.GetProperty("samples").GetInt32()
                || peak > budget.GetProperty("peakMiB").GetInt32()
                || bytes > budget.GetProperty("artifactBytes").GetInt64()
            then
                fail "MEASUREMENT-ROOT-BUDGET" rootId

            if rootMeasurements.ContainsKey rootId then
                fail "MEASUREMENT-ROOT-DUPLICATE" rootId

            rootMeasurements.Add(rootId, (depth, states, samples, elapsed, peak, bytes))

            writeMeasurementJson
                ("root-" + rootId + ".json")
                {|
                    id = rootId
                    outcome = "passed"
                    attemptId = measurementAttemptId
                    sourceSha256 = expectedSource
                    configurationSha256 = measurementConfigurationDigest
                    toolchainSha256 = expectedToolchain
                    validatorSha256 = measurementValidatorDigest
                    compilerSha256 = compilerIdentitySha256
                    dependencyDepth = depth
                    stateCount = states
                    sampleCount = samples
                    elapsedMs = elapsed
                    peakMiB = peak
                    artifactBytes = bytes
                |}
        else
            requireGreen "QUINT-BOUNDED-ROOT" scratch quint arguments [] |> ignore

        physicalScope <- None

    let recordRootArtifact rootId path =
        requireFile "QUINT-ROOT-ARTIFACT-MISSING" path
        let length = FileInfo(path).Length

        if length <= 0L || length > int64 artifactBudgets[rootId] then
            fail "QUINT-ROOT-ARTIFACT-BUDGET" ($"%s{rootId}: bytes=%d{length}; budget=%d{artifactBudgets[rootId]}")

        let digest = sha256 path
        rootArtifactDigests.Add(rootId, digest)
        printfn "QUINT_ROOT_ARTIFACT root=%s bytes=%d sha256=%s" rootId length digest

    preparationDurationMs <- qualificationClock.ElapsedMilliseconds

    let preparationSha256 =
        sha256Text ($"%s{sha256 qnt}|%s{sha256 q2Qnt}|%s{expectedToolchain}")

    preparationDigest <- Some preparationSha256
    q1Outcome <- "passed"
    currentPhase <- "q2"

    printfn
        "CANONICAL_QUINT_COMPILER_OK contract=%s source=%s profile=%s preparation=%s durationMs=%d"
        expectedContract
        expectedSource
        expectedProfile
        preparationSha256
        preparationDurationMs

    if compilerOnly then
        Directory.Delete(scratch, true)
        exit 0

    let stateRoots =
        [
            "authority", "QualificationAuthorityRoot"
            "lifecycle", "QualificationLifecycleRoot"
            "relations", "QualificationRelationsRoot"
            "protocol-streams", "QualificationProtocolStreamsRoot"
        ]

    for rootId, rootModule in
        stateRoots
        |> List.filter (fun (rootId, _) -> Set.contains rootId selectedRootIds) do
        let artifactPath = Path.Combine(rootArtifactDirectory, $"%s{rootId}.json")

        runRoot
            rootId
            artifactPath
            [
                "run"
                qualificationQnt
                "--main"
                rootModule
                "--init"
                "init"
                "--step"
                "rootStep"
                "--invariant"
                "qualificationInvariant"
                "--witnesses"
                "positiveWitness"
                "adversarialWitness"
                "--max-steps"
                "8"
                "--max-samples"
                "100"
                "--seed"
                "1"
                "--verbosity"
                (if measurementOnly then "1" else "0")
                "--out"
                artifactPath
            ]


        recordRootArtifact rootId artifactPath

    let testRoots =
        [
            "mutation-saga", "^test(Mutation|DurablePlan)"
            "desired-state", "^testDesiredState"
            "qualification", "^test(QualificationManifest|CompiledOutputs|DeterministicIdentity)"
        ]

    for rootId, rootPattern in
        testRoots
        |> List.filter (fun (rootId, _) -> Set.contains rootId selectedRootIds) do
        let artifactPath = Path.Combine(rootArtifactDirectory, $"%s{rootId}.json")

        runRoot
            rootId
            artifactPath
            [
                "test"
                qualificationQnt
                "--main"
                "CoordinationProtocolTests"
                "--backend"
                "rust"
                "--match"
                rootPattern
                "--verbosity"
                (if measurementOnly then "1" else "0")
                "--out"
                artifactPath
            ]


        recordRootArtifact rootId artifactPath

    let duplicateRootArtifacts =
        rootArtifactDigests
        |> Seq.groupBy snd
        |> Seq.filter (fun (_, rows) -> Seq.length rows > 1)
        |> Seq.toList

    if not (List.isEmpty duplicateRootArtifacts) then
        fail "QUINT-ROOT-ARTIFACT-IDENTITY" ($"duplicates=%A{duplicateRootArtifacts |> List.map fst}")

    let formalArtifactDirectory = Path.Combine(scratch, "formal-test-artifacts")
    Directory.CreateDirectory formalArtifactDirectory |> ignore

    let formalMeasurements =
        System.Collections.Generic.Dictionary<string, FormalMeasurement>()

    let formalExternalProcessBaseline = externalProcessCount
    let formalQuintProcessBaseline = quintProcessCount
    let formalApalacheVerifyBaseline = apalacheVerifyInvocationCount
    let formalRejectedProcessBaseline = quintRejectedProcessCount
    let formalStartupRetryBaseline = apalacheStartupRetryCount
    let formalVerifyStartupRetryBaseline = apalacheVerifyStartupRetryCount
    let formalReflectionRetryBaseline = apalacheReflectionRetryCount
    let formalEarlyLifecycleRetryBaseline = apalacheEarlyLifecycleRetryCount

    for formalId,
        main,
        init,
        step,
        invariantName,
        witness,
        _,
        _,
        _,
        _,
        _,
        _,
        _,
        _,
        depth,
        _,
        _,
        samples,
        elapsedBudget,
        peakBudget,
        artifactBudget in formalTests do
        physicalScope <- Some formalId

        let artifactPath =
            Path.Combine(formalArtifactDirectory, $"%s{formalId}-simulation.json")

        let exitCode, output, error, elapsedMs, peakMiB =
            runMeasured
                elapsedBudget
                scratch
                quint
                [
                    "run"
                    (formalQnt main)
                    "--main"
                    main
                    "--init"
                    init
                    "--step"
                    step
                    "--invariant"
                    invariantName
                    "--witnesses"
                    witness
                    "--max-steps"
                    string depth
                    "--max-samples"
                    string samples
                    "--seed"
                    "1"
                    "--verbosity"
                    "1"
                ]
                []

        if exitCode <> 0 then
            fail "QUINT-FORMAL-SIMULATION" ($"%s{formalId}: exit=%d{exitCode}; stdout=%s{output}; stderr=%s{error}")

        let exploredMatch = Regex.Match(output + "\n" + error, @"out of (\d+) explored")

        if not exploredMatch.Success then
            fail
                "QUINT-FORMAL-SIMULATION-MEASUREMENT"
                ($"%s{formalId}: exit=%d{exitCode}; stdout=%s{output}; stderr=%s{error}")

        let observedSamples = Int32.Parse exploredMatch.Groups[1].Value

        let normalizedOutput =
            Regex.Replace((output + "\n" + error).Trim(), @"\d+ms at \d+ traces/second", "<measured runtime>")

        File.WriteAllText(artifactPath, normalizedOutput + "\n", UTF8Encoding(false))
        let length = FileInfo(artifactPath).Length

        if
            length <= 0L
            || length > int64 artifactBudget
            || elapsedMs > int64 elapsedBudget
            || peakMiB > peakBudget
        then
            fail "QUINT-FORMAL-ARTIFACT-BUDGET" ($"%s{formalId}: bytes=%d{length}; budget=%d{artifactBudget}")

        formalMeasurements[formalId] <-
            {
                StateCount = 0
                TransitionCount = 0
                SampleCount = observedSamples
                ElapsedMs = elapsedMs
                AllAttemptElapsedMs = 0L
                PeakMiB = peakMiB
                ArtifactBytes = length
            }

        printfn
            "QUINT_FORMAL_SIMULATION id=%s samples=%d elapsedMs=%d peakMiB=%d bytes=%d sha256=%s"
            formalId
            observedSamples
            elapsedMs
            peakMiB
            length
            (sha256 artifactPath)

    physicalScope <- None

    requireGreen
        "QUINT-INDEPENDENT-ORACLES"
        scratch
        quint
        [
            "test"
            qualificationQnt
            "--main"
            "CoordinationProtocolTests"
            "--backend"
            "rust"
            "--match"
            "^oracle"
            "--verbosity"
            "0"
        ]
        []
    |> ignore

    requireGreen
        "QUINT-RUN"
        scratch
        quint
        [
            "run"
            qnt
            "--main"
            "CoordinationProtocol"
            "--init"
            "init"
            "--step"
            "step"
            "--invariant"
            "acceptedVocabularyIsQualified"
            "--max-steps"
            "4"
            "--max-samples"
            "20"
            "--seed"
            "1"
            "--verbosity"
            "0"
        ]
        []
    |> ignore

    requireGreen
        "QUINT-TEST"
        scratch
        quint
        [
            "test"
            qualificationQnt
            "--main"
            "CoordinationProtocolTests"
            "--backend"
            "rust"
            "--match"
            "^test"
            "--verbosity"
            "0"
        ]
        []
    |> ignore

    let quintHome = Environment.GetEnvironmentVariable "FSGG_QUINT_HOME"
    let javaHome = Environment.GetEnvironmentVariable "JAVA_HOME"

    if String.IsNullOrWhiteSpace quintHome then
        fail "APALACHE-CACHE" "FSGG_QUINT_HOME is required"

    if String.IsNullOrWhiteSpace javaHome then
        fail "JAVA" "JAVA_HOME is required"

    let apalacheJar =
        Path.Combine(quintHome, "apalache-dist-0.56.1/apalache/lib/apalache.jar")

    let java = Path.Combine(javaHome, "bin/java")
    requireFile "APALACHE-MISSING" apalacheJar
    requireFile "JAVA-MISSING" java

    if sha256 apalacheJar <> expectedApalacheJar then
        fail "APALACHE-DIGEST" (sha256 apalacheJar)

    let environment =
        [
            "QUINT_HOME", quintHome
            "PATH",
            Path.GetDirectoryName(java)
            + string Path.PathSeparator
            + Environment.GetEnvironmentVariable("PATH")
        ]

    let normalizeItf path =
        let document = JsonNode.Parse(File.ReadAllBytes path).AsObject()
        let metadata = document["#meta"].AsObject()
        metadata.Remove("description") |> ignore
        metadata.Remove("timestamp") |> ignore
        metadata.Remove("source") |> ignore

        if
            metadata["format"].GetValue<string>() <> "ITF"
            || metadata["status"].GetValue<string>() <> "violation"
        then
            fail "QUINT-FORMAL-COUNTEREXAMPLE-SHAPE" path

        if document["states"].AsArray().Count < 2 then
            fail "QUINT-FORMAL-COUNTEREXAMPLE-TRACE" path

        document.ToJsonString(JsonSerializerOptions(WriteIndented = false))

    // TLC liveness verification is comparatively expensive and cannot emit ITF.  Preflight
    // one deterministic Rust projection for every declared removed-step model before entering
    // the TLC loop.  The result is retained as the first of the two reproducibility samples,
    // so this changes failure order without adding process work.
    let runProjection formalId main init removedStep blockedInvariant depth elapsedBudget ordinal =
        physicalScope <- Some formalId

        let pattern =
            Path.Combine(formalArtifactDirectory, $"%s{formalId}-counterexample-%d{ordinal}-{{seq}}.itf.json")

        let exitCode, output, error, elapsedMs, peakMiB =
            runMeasured
                elapsedBudget
                scratch
                quint
                [
                    "run"
                    (formalQnt main)
                    "--main"
                    main
                    "--init"
                    init
                    "--step"
                    removedStep
                    "--invariant"
                    blockedInvariant
                    "--max-steps"
                    string depth
                    "--max-samples"
                    "1"
                    "--seed"
                    "1"
                    "--verbosity"
                    "0"
                    "--out-itf"
                    pattern
                ]
                []

        if
            exitCode = 0
            || not ((output + "\n" + error).Contains("Invariant violated", StringComparison.Ordinal))
        then
            fail "QUINT-FORMAL-ITF-PROJECTION" ($"%s{formalId}: exit=%d{exitCode}; stdout=%s{output}; stderr=%s{error}")

        let path = pattern.Replace("{seq}", "0", StringComparison.Ordinal)
        requireFile "QUINT-FORMAL-COUNTEREXAMPLE-MISSING" path
        path, normalizeItf path, elapsedMs, peakMiB

    let projectionPreflights =
        formalTests
        |> List.map
            (fun
                (formalId,
                 main,
                 init,
                 _,
                 _,
                 _,
                 _,
                 _,
                 removedStep,
                 _,
                 blockedInvariant,
                 _,
                 _,
                 _,
                 depth,
                 _,
                 _,
                 _,
                 elapsedBudget,
                 _,
                 _) -> formalId, runProjection formalId main init removedStep blockedInvariant depth elapsedBudget 1)
        |> Map.ofList

    for formalId,
        main,
        init,
        step,
        invariantName,
        _,
        temporalName,
        invalid,
        removedStep,
        violatedTemporal,
        blockedInvariant,
        retainedCounterexample,
        retainedTrace,
        retainedManifest,
        depth,
        stateBudget,
        transitionBudget,
        _,
        elapsedBudget,
        peakBudget,
        artifactBudget in formalTests do
        physicalScope <- Some formalId

        let temporalExit, temporalOutput, temporalError, temporalElapsed, temporalPeak =
            runMeasured
                elapsedBudget
                scratch
                quint
                [
                    "verify"
                    (formalQnt main)
                    "--main"
                    main
                    "--init"
                    init
                    "--step"
                    step
                    "--invariant"
                    invariantName
                    "--temporal"
                    temporalName
                    "--max-steps"
                    string depth
                    "--backend"
                    "tlc"
                    "--verbosity"
                    "5"
                ]
                environment

        if temporalExit <> 0 then
            fail
                "QUINT-FORMAL-TEMPORAL"
                ($"%s{formalId}: exit=%d{temporalExit}; stdout=%s{temporalOutput}; stderr=%s{temporalError}")

        let stateMatches =
            Regex.Matches(temporalOutput + "\n" + temporalError, @"(\d+) states generated, (\d+) distinct states found")

        if stateMatches.Count = 0 then
            fail
                "QUINT-FORMAL-TLC-MEASUREMENT"
                ($"%s{formalId}: exit=%d{temporalExit}; stdout=%s{temporalOutput}; stderr=%s{temporalError}")

        let stateMatch = stateMatches[stateMatches.Count - 1]
        let generatedStates = Int32.Parse stateMatch.Groups[1].Value
        let distinctStates = Int32.Parse stateMatch.Groups[2].Value
        let transitions = Math.Max(0, generatedStates - 1)
        let measurement = formalMeasurements[formalId]
        measurement.StateCount <- distinctStates
        measurement.TransitionCount <- transitions
        measurement.ElapsedMs <- measurement.ElapsedMs + temporalElapsed
        measurement.PeakMiB <- Math.Max(measurement.PeakMiB, temporalPeak)

        let safetyExit, safetyOutput, safetyError, safetyElapsed, safetyPeak =
            runMeasured
                elapsedBudget
                scratch
                quint
                [
                    "run"
                    (formalQnt main)
                    "--main"
                    main
                    "--init"
                    init
                    "--step"
                    invalid
                    "--invariant"
                    invariantName
                    "--max-steps"
                    "2"
                    "--max-samples"
                    "1"
                    "--seed"
                    "1"
                    "--verbosity"
                    "0"
                ]
                []

        if
            safetyExit = 0
            || not ((safetyOutput + "\n" + safetyError).Contains("Invariant violated", StringComparison.Ordinal))
        then
            fail
                "QUINT-FORMAL-SAFETY-INVALID"
                ($"%s{formalId}: exit=%d{safetyExit}; stdout=%s{safetyOutput}; stderr=%s{safetyError}")

        measurement.ElapsedMs <- measurement.ElapsedMs + safetyElapsed
        measurement.PeakMiB <- Math.Max(measurement.PeakMiB, safetyPeak)

        let runCounterexample ordinal =
            let temporalExitCode, temporalOutput, temporalError, temporalElapsedMs, temporalPeakMiB =
                runMeasured
                    elapsedBudget
                    scratch
                    quint
                    [
                        "verify"
                        (formalQnt main)
                        "--main"
                        main
                        "--init"
                        init
                        "--step"
                        removedStep
                        "--temporal"
                        violatedTemporal
                        "--max-steps"
                        string depth
                        "--backend"
                        "tlc"
                        "--verbosity"
                        "5"
                    ]
                    environment

            let temporalDiagnostic = temporalOutput + "\n" + temporalError

            if
                temporalExitCode = 0
                || not (temporalDiagnostic.Contains("Temporal properties were violated", StringComparison.Ordinal))
            then
                fail
                    "QUINT-FORMAL-TEMPORAL-COUNTEREXAMPLE"
                    ($"%s{formalId}: transition-removal did not violate %s{violatedTemporal}; exit=%d{temporalExitCode}; stdout=%s{temporalOutput}; stderr=%s{temporalError}")

            let diagnosticStart =
                temporalDiagnostic.IndexOf(
                    "Error: The following behavior constitutes a counter-example:",
                    StringComparison.Ordinal
                )

            if diagnosticStart < 0 then
                fail
                    "QUINT-FORMAL-TEMPORAL-DIAGNOSTIC"
                    ($"%s{formalId}: counterexample start marker missing; exit=%d{temporalExitCode}; stdout=%s{temporalOutput}; stderr=%s{temporalError}")

            let diagnosticEnd =
                temporalDiagnostic.IndexOf(
                    "Finished checking temporal properties",
                    diagnosticStart,
                    StringComparison.Ordinal
                )

            if diagnosticEnd <= diagnosticStart then
                fail
                    "QUINT-FORMAL-TEMPORAL-DIAGNOSTIC"
                    ($"%s{formalId}: counterexample end marker missing; exit=%d{temporalExitCode}; stdout=%s{temporalOutput}; stderr=%s{temporalError}")

            let normalizedDiagnostic =
                temporalDiagnostic.Substring(diagnosticStart, diagnosticEnd - diagnosticStart).Trim()

            let path, projection, projectionElapsedMs, projectionPeakMiB =
                if ordinal = 1 then
                    projectionPreflights[formalId]
                else
                    runProjection formalId main init removedStep blockedInvariant depth elapsedBudget ordinal

            path,
            projection,
            normalizedDiagnostic,
            temporalElapsedMs + projectionElapsedMs,
            Math.Max(temporalPeakMiB, projectionPeakMiB)

        let firstPath, first, firstDiagnostic, firstElapsed, firstPeak = runCounterexample 1
        let _, second, secondDiagnostic, secondElapsed, secondPeak = runCounterexample 2

        if first <> second || firstDiagnostic <> secondDiagnostic then
            fail
                "QUINT-FORMAL-COUNTEREXAMPLE-NONDETERMINISTIC"
                ($"%s{formalId}: firstProjection=%s{sha256Text first}; secondProjection=%s{sha256Text second}; firstDiagnostic=%s{sha256Text firstDiagnostic}; secondDiagnostic=%s{sha256Text secondDiagnostic}")

        File.WriteAllText(firstPath, first, UTF8Encoding(false))
        let itfSha256 = sha256 firstPath
        let itfNode = JsonNode.Parse(first).AsObject()
        let traceNode = JsonObject()
        traceNode["schema"] <- JsonValue.Create("fsgg.coordination.quint-counterexample-trace/1")
        traceNode["temporalDiagnostic"] <- JsonValue.Create(firstDiagnostic)
        traceNode["states"] <- JsonNode.Parse(itfNode["states"].ToJsonString())
        let traceText = traceNode.ToJsonString(JsonSerializerOptions(WriteIndented = false))

        let tracePath =
            Path.Combine(formalArtifactDirectory, $"%s{formalId}.quint-trace.json")

        File.WriteAllText(tracePath, traceText, UTF8Encoding(false))
        let traceSha256 = sha256 tracePath

        let orderedStatesSha256 =
            sha256Text (itfNode["states"].ToJsonString(JsonSerializerOptions(WriteIndented = false)))

        let manifestNode = JsonObject()
        manifestNode["schema"] <- JsonValue.Create("fsgg.coordination.quint-counterexample-manifest/1")
        manifestNode["id"] <- JsonValue.Create(formalId)
        manifestNode["sourceSha256"] <- JsonValue.Create(expectedSource)
        manifestNode["assembledQuintSha256"] <- JsonValue.Create(sha256 q2Qnt)
        manifestNode["main"] <- JsonValue.Create(main)
        manifestNode["init"] <- JsonValue.Create(init)
        manifestNode["removedStep"] <- JsonValue.Create(removedStep)
        manifestNode["violatedTemporal"] <- JsonValue.Create(violatedTemporal)
        manifestNode["blockedInvariant"] <- JsonValue.Create(blockedInvariant)
        let boundsNode = JsonObject()
        boundsNode["maxSteps"] <- JsonValue.Create(depth)
        manifestNode["bounds"] <- boundsNode
        manifestNode["backend"] <- JsonValue.Create("tlc")
        manifestNode["toolchainSha256"] <- JsonValue.Create(expectedToolchain)
        manifestNode["normalization"] <- JsonValue.Create("itf-volatile-meta-v1")
        manifestNode["outcome"] <- JsonValue.Create("temporal-violation")
        manifestNode["orderedStatesSha256"] <- JsonValue.Create(orderedStatesSha256)
        manifestNode["traceSha256"] <- JsonValue.Create(traceSha256)
        manifestNode["itfSha256"] <- JsonValue.Create(itfSha256)

        let manifestText =
            manifestNode.ToJsonString(JsonSerializerOptions(WriteIndented = false))

        let manifestPath =
            Path.Combine(formalArtifactDirectory, $"%s{formalId}.manifest.json")

        File.WriteAllText(manifestPath, manifestText, UTF8Encoding(false))

        for code, retainedPath, actualText in
            [
                "QUINT-FORMAL-COUNTEREXAMPLE-STALE", retainedCounterexample, first
                "QUINT-FORMAL-TRACE-STALE", retainedTrace, traceText
                "QUINT-FORMAL-MANIFEST-STALE", retainedManifest, manifestText
            ] do
            let fullPath = Path.Combine(root, retainedPath)

            if measurementOnly then
                verifyMeasurementDirectory ()

                if Path.IsPathFullyQualified retainedPath || Path.GetFileName(formalId) <> formalId then
                    fail "MEASUREMENT-EVIDENCE-PATH" "outside-candidate"

                let candidatePath =
                    Path.Combine(
                        measurementDirectory.Value,
                        "counterexample-" + formalId + "-" + Path.GetFileName retainedPath
                    )

                use stream =
                    new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                let bytes = Encoding.UTF8.GetBytes actualText
                stream.Write(bytes, 0, bytes.Length)
            elif refreshFormalEvidence then
                Directory.CreateDirectory(Path.GetDirectoryName fullPath) |> ignore
                File.WriteAllText(fullPath, actualText, UTF8Encoding(false))
            else
                requireFile code fullPath

                if File.ReadAllText(fullPath, Encoding.UTF8) <> actualText then
                    fail code formalId

        let totalBytes =
            FileInfo(firstPath).Length
            + FileInfo(tracePath).Length
            + FileInfo(manifestPath).Length
            + measurement.ArtifactBytes

        measurement.ArtifactBytes <- totalBytes
        measurement.ElapsedMs <- measurement.ElapsedMs + firstElapsed + secondElapsed
        measurement.PeakMiB <- List.max [ measurement.PeakMiB; firstPeak; secondPeak ]

        let physical =
            physicalAttempts
            |> Seq.filter (fun row -> row.Scope = Some formalId)
            |> Seq.toArray

        let allAttemptBudget =
            processInventoryConfiguration.RootElement.GetProperty("formalTests").EnumerateArray()
            |> Seq.find (fun item -> item.GetProperty("id").GetString() = formalId)
            |> fun item -> item.GetProperty("budget").GetProperty("allAttemptElapsedMs").GetInt64()

        match summarizeFormalPhysical allAttemptBudget physical with
        | Error detail -> fail "PHYSICAL-FORMAL-ACCOUNTING" (formalId + ":" + detail)
        | Ok(terminal, busy, peak) ->
            if terminal <> measurement.ElapsedMs then
                fail "PHYSICAL-FORMAL-TERMINAL-WORK" formalId

            measurement.AllAttemptElapsedMs <- busy
            measurement.PeakMiB <- peak

        if
            distinctStates > stateBudget
            || transitions > transitionBudget
            || measurement.ElapsedMs > int64 elapsedBudget
            || measurement.PeakMiB > peakBudget
            || totalBytes > int64 artifactBudget
        then
            fail
                "QUINT-FORMAL-MEASUREMENT-BUDGET"
                ($"%s{formalId}: states=%d{distinctStates}; transitions=%d{transitions}; elapsedMs=%d{measurement.ElapsedMs}; peakMiB=%d{measurement.PeakMiB}; bytes=%d{totalBytes}")

        writeMeasurementJson
            ("formal-" + formalId + ".json")
            {|
                id = formalId
                outcome = "passed"
                attemptId = measurementAttemptId
                sourceSha256 = expectedSource
                configurationSha256 = measurementConfigurationDigest
                toolchainSha256 = expectedToolchain
                validatorSha256 = measurementValidatorDigest
                compilerSha256 = compilerIdentitySha256
                stateCount = measurement.StateCount
                transitionCount = measurement.TransitionCount
                sampleCount = measurement.SampleCount
                elapsedMs = measurement.ElapsedMs
                allAttemptElapsedMs = measurement.AllAttemptElapsedMs
                peakMiB = measurement.PeakMiB
                artifactBytes = measurement.ArtifactBytes
            |}

        formalCounterexampleReceipts.Add(formalId, sha256 manifestPath, traceSha256, itfSha256)

        printfn
            "QUINT_FORMAL_TEST id=%s backend=tlc temporal=%s removedStep=%s violatedTemporal=%s states=%d transitions=%d samples=%d elapsedMs=%d peakMiB=%d artifactBytes=%d manifestSha256=%s traceSha256=%s itfSha256=%s"
            formalId
            temporalName
            removedStep
            violatedTemporal
            distinctStates
            transitions
            measurement.SampleCount
            measurement.ElapsedMs
            measurement.PeakMiB
            totalBytes
            (sha256 manifestPath)
            traceSha256
            itfSha256

    use formalBaselineDocument =
        JsonDocument.Parse(File.ReadAllBytes qualificationBaseline)

    if
        not measurementOnly
        && formalBaselineDocument.RootElement.GetProperty("formalMeasurementMethod").GetString()
           <> "canonical-runner-observed-tlc-and-rust-v2"
    then
        fail "QUINT-FORMAL-BASELINE-METHOD" "unsupported"

    physicalScope <- None

    let retainedFormalMeasurements =
        formalBaselineDocument.RootElement.GetProperty("formalMeasurements").EnumerateArray()
        |> Seq.map (fun item -> item.GetProperty("id").GetString(), item.Clone())
        |> Map.ofSeq

    for KeyValue(formalId, observed) in formalMeasurements do
        if not (Map.containsKey formalId retainedFormalMeasurements) then
            fail "QUINT-FORMAL-BASELINE-MISSING" formalId

        let retainedMeasurement = retainedFormalMeasurements[formalId]

        let expectedStable =
            retainedMeasurement.GetProperty("stateCount").GetInt32(),
            retainedMeasurement.GetProperty("transitionCount").GetInt32(),
            retainedMeasurement.GetProperty("sampleCount").GetInt32(),
            retainedMeasurement.GetProperty("artifactBytes").GetInt64()

        let observedStable =
            observed.StateCount, observed.TransitionCount, observed.SampleCount, observed.ArtifactBytes

        if
            expectedStable <> observedStable
            && not refreshFormalEvidence
            && not measurementOnly
        then
            fail
                "QUINT-FORMAL-BASELINE-DRIFT"
                ($"%s{formalId}: expected=%A{expectedStable}; observed=%A{observedStable}")

        printfn
            "QUINT_FORMAL_MEASUREMENT id=%s stateCount=%d transitionCount=%d sampleCount=%d elapsedMs=%d peakMiB=%d artifactBytes=%d"
            formalId
            observed.StateCount
            observed.TransitionCount
            observed.SampleCount
            observed.ElapsedMs
            observed.PeakMiB
            observed.ArtifactBytes

    match formalShardId with
    | Some formalId when formalId <> "base" ->
        requireCompletedProcessInventory ()
        let observed = formalMeasurements[formalId]

        let _, manifestSha256, traceSha256, itfSha256 =
            formalCounterexampleReceipts |> Seq.exactlyOne

        let invocationCount label =
            match actualInvocationInventory.TryGetValue label with
            | true, count -> count
            | _ -> 0

        let formalQuintProcessCount =
            [
                "quint/formal-simulation"
                "quint/formal-temporal"
                "quint/formal-safety-mutant"
                "quint/formal-counterexample-temporal"
                "quint/formal-counterexample-projection"
            ]
            |> List.sumBy invocationCount

        let formalExternalProcessCount = formalQuintProcessCount

        let formalApalacheVerifyCount =
            invocationCount "quint/formal-temporal"
            + invocationCount "quint/formal-counterexample-temporal"

        let executedExternalProcessCount =
            externalProcessCount - formalExternalProcessBaseline

        let executedQuintProcessCount = quintProcessCount - formalQuintProcessBaseline

        let executedApalacheVerifyCount =
            apalacheVerifyInvocationCount - formalApalacheVerifyBaseline

        let formalRejectedProcessCount =
            quintRejectedProcessCount - formalRejectedProcessBaseline

        let startupRetryCount = apalacheStartupRetryCount - formalStartupRetryBaseline

        let verifyStartupRetryCount =
            apalacheVerifyStartupRetryCount - formalVerifyStartupRetryBaseline

        let reflectionRetryCount =
            apalacheReflectionRetryCount - formalReflectionRetryBaseline

        let earlyLifecycleRetryCount =
            apalacheEarlyLifecycleRetryCount - formalEarlyLifecycleRetryBaseline

        let shardQ2DurationMs =
            Math.Max(0L, qualificationClock.ElapsedMilliseconds - preparationDurationMs)

        let outputDirectory = Path.GetDirectoryName qualificationOutput

        if not (String.IsNullOrWhiteSpace outputDirectory) then
            Directory.CreateDirectory outputDirectory |> ignore

        use outputStream = File.Create qualificationOutput
        use writer = new Utf8JsonWriter(outputStream, JsonWriterOptions(Indented = false))
        writer.WriteStartObject()
        writer.WriteString("schema", "fsgg.coordination.canonical-quint-formal-shard/1")
        writer.WriteString("id", formalId)
        writer.WriteString("outcome", "passed")
        writer.WriteString("accountingMethod", "logical-formal-contribution-and-observed-execution/v1")
        writer.WriteNumber("negativeControlCount", formalRejectedProcessCount)
        writer.WriteNumber("q2DurationMs", shardQ2DurationMs)
        writer.WriteStartObject("processCounts")
        writer.WriteNumber("external", formalExternalProcessCount)
        writer.WriteNumber("quintCli", formalQuintProcessCount)
        writer.WriteNumber("apalacheVerify", formalApalacheVerifyCount)
        writer.WriteEndObject()
        writer.WriteStartObject("executedProcessCounts")
        writer.WriteNumber("external", executedExternalProcessCount + startupRetryCount)
        writer.WriteNumber("quintCli", executedQuintProcessCount + startupRetryCount)
        writer.WriteNumber("apalacheVerify", executedApalacheVerifyCount + verifyStartupRetryCount)
        writer.WriteEndObject()
        writer.WriteStartObject("startupRetries")
        writer.WriteNumber("total", startupRetryCount)
        writer.WriteNumber("verify", verifyStartupRetryCount)
        writer.WriteNumber("reflectionDeadline", reflectionRetryCount)
        writer.WriteNumber("earlyLifecycleExit", earlyLifecycleRetryCount)
        writer.WriteEndObject()
        writer.WriteNumber("elapsedMs", observed.ElapsedMs)
        writer.WriteNumber("peakMiB", observed.PeakMiB)
        writer.WriteNumber("artifactBytes", observed.ArtifactBytes)
        writer.WriteNumber("stateCount", observed.StateCount)
        writer.WriteNumber("transitionCount", observed.TransitionCount)
        writer.WriteNumber("sampleCount", observed.SampleCount)
        writer.WriteString("manifestSha256", manifestSha256)
        writer.WriteString("traceSha256", traceSha256)
        writer.WriteString("itfSha256", itfSha256)
        writer.WriteString("toolchainSha256", expectedToolchain)
        writer.WriteString("quintSha256", expectedQuint)
        writer.WriteString("apalacheJarSha256", expectedApalacheJar)
        writer.WriteString("sourceSha256", expectedSource)
        writer.WriteString("contractSha256", expectedContract)
        writer.WriteString("preparationSha256", preparationDigest |> Option.defaultValue "")
        writer.WriteEndObject()
        writer.Flush()
        printfn "CANONICAL_QUINT_FORMAL_SHARD_OK id=%s receipt=%s" formalId qualificationOutput
        exit 0
    | _ -> ()

    requireGreen
        "QUINT-POSITIVE-INVARIANTS-VERIFY"
        scratch
        quint
        ([
            "verify"
            qnt
            "--main"
            "CoordinationProtocol"
            "--init"
            "init"
            "--step"
            "step"
            "--invariants"
         ]
         @ positiveInvariants
         @ [ "--max-steps"; "4"; "--verbosity"; "1" ])
        environment
    |> ignore

    verifiedPositiveInvariantCount <- positiveInvariants.Length

    let mutatedQnt = Path.Combine(scratch, "protocol-missing-evidence-guard.qnt")
    let originalQnt = File.ReadAllText qualificationQnt
    let rustMutationChecks = ResizeArray<unit -> unit>()

    let enqueueRustMutation code filePrefix testMatch (name: string) (fixture: string) (replacement: string) =
        if not (originalQnt.Contains(fixture, StringComparison.Ordinal)) then
            fail code ($"%s{name}: fixture absent")

        let mutant = Path.Combine(scratch, $"%s{filePrefix}-%s{name}.qnt")
        File.WriteAllText(mutant, originalQnt.Replace(fixture, replacement))

        rustMutationChecks.Add(fun () ->
            let exitCode, output, error =
                run
                    scratch
                    quint
                    [
                        "test"
                        mutant
                        "--main"
                        "CoordinationProtocolTests"
                        "--backend"
                        "rust"
                        "--match"
                        testMatch
                        "--verbosity"
                        "0"
                    ]
                    []

            if exitCode = 0 then
                fail code ($"%s{name}: mutant passed")

            if not ((output + "\n" + error).Contains("failed", StringComparison.OrdinalIgnoreCase)) then
                fail code ($"%s{name}: no failed witness; %s{output}; %s{error}"))

    // Every independent oracle owns a focused mutation of the canonical subject it observes.
    // The mutation is run only against that oracle, so a broad generated test cannot mask a
    // disconnected or self-confirming oracle implementation.
    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleClaimExclusion$"
        "claim-exclusion"
        "    left != right,\n    left.operationId == right.operationId or left.idempotencyKey == right.idempotencyKey,"
        "    true,\n    left.operationId == right.operationId or left.idempotencyKey == right.idempotencyKey,"

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleStaleProjection$"
        "lifecycle-claim-status"
        "else if (facts.claimPresent) \"claimed\""
        "else if (facts.claimPresent) \"ready\""

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleDependencyConcurrency$"
        "dependency-concurrency"
        "if (expectedRevision == observedRevision) \"MOUT-Applied\" else \"MOUT-RevisionConflict\""
        "\"MOUT-Applied\""

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oraclePartialOperation$"
        "partial-operation"
        "else if (mutationOutcomeIsUncertain(receipt.outcomeId)) \"PDISP-ReceiptReread\""
        "else if (mutationOutcomeIsUncertain(receipt.outcomeId)) \"PDISP-Advance\""

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleOldClientFencing$"
        "old-client-fencing"
        "versions == supportedDeterministicVersions"
        "true"

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleLedgerTamper$"
        "ledger-tamper"
        "pure def retainedProtocolEnvelopeHasPredecessor(envelope: ProtocolEnvelope, events: Set[ProtocolEnvelope]): bool =\n    if (envelope.sequence == 1)"
        "pure def retainedProtocolEnvelopeHasPredecessor(envelope: ProtocolEnvelope, events: Set[ProtocolEnvelope]): bool =\n    if (true)"

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleExactHeadReview$"
        "exact-head-review"
        "    manifest.reviewCandidateSha == manifest.candidateSha,"
        "    true,"

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oraclePostMergeVerification$"
        "post-merge-verification"
        "    manifest.resultCandidateSha == manifest.candidateSha,"
        "    true,"

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleDualFeedRecovery$"
        "dual-feed-recovery"
        "if (desired.contentDigest == observed.contentDigest) \"DSPLAN-NoChange\" else \"DSPLAN-Ready\""
        "\"DSPLAN-NoChange\""

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleAbstractionEquivalence$"
        "abstraction-equivalence"
        "    left.behavioralSha256 == right.behavioralSha256,"
        "    true,"

    enqueueRustMutation
        "INDEPENDENT-ORACLE-NEGATIVE-CONTROL"
        "protocol-independent-oracle"
        "^oracleScaleEnvelope$"
        "scale-envelope"
        "{ id: \"BOUND-TraceSteps\", kind: \"bound\", minimum: 0, maximum: 4 }"
        "{ id: \"BOUND-TraceSteps\", kind: \"bound\", minimum: 5, maximum: 4 }"

    let enqueueInvalidParameterRed (rootId: string) (rootModule: string) =
        rustMutationChecks.Add(fun () ->
            let exitCode, output, error =
                run
                    scratch
                    quint
                    [
                        "run"
                        qualificationQnt
                        "--main"
                        rootModule
                        "--init"
                        "init"
                        "--step"
                        "invalidStep"
                        "--invariant"
                        "qualificationInvariant"
                        "--max-steps"
                        "8"
                        "--max-samples"
                        "100"
                        "--seed"
                        "1"
                        "--verbosity"
                        "0"
                    ]
                    []

            if exitCode = 0 then
                fail "ANTI-VACUITY-NEGATIVE-CONTROL" ($"%s{rootId}: invalid parameterization passed")

            if not ((output + "\n" + error).Contains("Invariant", StringComparison.OrdinalIgnoreCase)) then
                fail "ANTI-VACUITY-NEGATIVE-CONTROL" ($"%s{rootId}: no invariant violation; %s{output}; %s{error}"))

    enqueueInvalidParameterRed "authority" "QualificationAuthorityRoot"
    enqueueInvalidParameterRed "lifecycle" "QualificationLifecycleRoot"
    enqueueInvalidParameterRed "relations" "QualificationRelationsRoot"
    enqueueInvalidParameterRed "protocol-streams" "QualificationProtocolStreamsRoot"

    let requireMutationRed (name: string) (fixture: string) (replacement: string) =
        enqueueRustMutation "MUTATION-NEGATIVE-CONTROL" "protocol-mutation" "^testMutation" name fixture replacement

    requireMutationRed
        "idempotency-key-binding"
        "left.operationId == right.operationId or left.idempotencyKey == right.idempotencyKey"
        "left.operationId == right.operationId"

    requireMutationRed
        "operation-binding"
        "left.operationId == right.operationId or left.idempotencyKey == right.idempotencyKey"
        "left.idempotencyKey == right.idempotencyKey"

    requireMutationRed
        "target-kind-binding"
        "      mutationKind.targetKind == intent.targetKind,\n      mutationKind.payloadKind == intent.payloadKind,"
        "      mutationKind.payloadKind == intent.payloadKind,"

    requireMutationRed
        "payload-kind-binding"
        "      mutationKind.targetKind == intent.targetKind,\n      mutationKind.payloadKind == intent.payloadKind,"
        "      mutationKind.targetKind == intent.targetKind,"

    requireMutationRed
        "remove-edge-payload-classification"
        "targetKind: \"relation\", payloadKind: \"edge\", revisionRequirement: \"exact\" },\n    { id: \"MUT-Set\""
        "targetKind: \"relation\", payloadKind: \"scalar\", revisionRequirement: \"exact\" },\n    { id: \"MUT-Set\""

    requireMutationRed
        "rate-limit-uncertainty-classification"
        "id: \"MOUT-RateLimited\", kind: \"mutationOutcome\", finality: \"uncertain\", effectClass: \"unknown\""
        "id: \"MOUT-RateLimited\", kind: \"mutationOutcome\", finality: \"terminal\", effectClass: \"applied\""

    requireMutationRed
        "exact-replay-idempotent-outcome"
        "        current.outcomeId == \"MOUT-Idempotent\","
        "        current.outcomeId == \"MOUT-Applied\","

    requireMutationRed
        "stale-revision"
        "if (expectedRevision == observedRevision) \"MOUT-Applied\" else \"MOUT-RevisionConflict\""
        "\"MOUT-Applied\""

    requireMutationRed
        "compensation-outcome-binding"
        "    original.outcomeId == \"MOUT-Applied\",\n    original.resultingRevision == intent.expectedRevision,"
        "    original.resultingRevision == intent.expectedRevision,"

    requireMutationRed
        "compensation-predecessor-shape-binding"
        "    mutationIntentShapeIsValid(original.intent),\n    mutationResultOutcomeIsValid(original),"
        "    mutationResultOutcomeIsValid(original),"

    requireMutationRed "compensation-uniqueness-binding" "      existing != intent," "      existing == intent,"

    let requireDurablePlanRed (name: string) (fixture: string) (replacement: string) =
        enqueueRustMutation
            "DURABLE-PLAN-NEGATIVE-CONTROL"
            "protocol-durable-plan"
            "^testDurablePlan"
            name
            fixture
            replacement

    requireDurablePlanRed
        "predecessor-binding"
        "    current.sequence == previous.sequence + 1, current.predecessorStepId == previous.stepId,"
        "    current.sequence == previous.sequence + 1, true,"

    requireDurablePlanRed
        "causation-binding"
        "    current.causationId == previous.intent.operationId, current.stepId != previous.stepId,"
        "    true, current.stepId != previous.stepId,"

    requireDurablePlanRed
        "correlation-binding"
        "    previous.planId == current.planId, previous.correlationId == current.correlationId,"
        "    previous.planId == current.planId, true,"

    requireDurablePlanRed
        "receipt-intent-binding"
        "    checkpoint.receipt.intent == checkpoint.step.intent, mutationResultOutcomeIsValid(checkpoint.receipt),"
        "    true, mutationResultOutcomeIsValid(checkpoint.receipt),"

    requireDurablePlanRed
        "uncertain-receipt-reread"
        "    else if (mutationOutcomeIsUncertain(receipt.outcomeId)) \"PDISP-ReceiptReread\""
        "    else if (mutationOutcomeIsUncertain(receipt.outcomeId)) \"PDISP-Advance\""

    requireDurablePlanRed
        "compensation-boundary"
        "    compensation.compensationBoundaryId == original.compensationBoundaryId,"
        "    true,"

    requireDurablePlanRed
        "compensation-ordered-follow-relation"
        "    durablePlanStepMayFollow(original, compensation),"
        "    true,"

    requireDurablePlanRed "compensation-reverse-order" "      applied.sequence <= original.sequence," "      true,"

    requireDurablePlanRed
        "disposition-classification"
        "    if (receipt.outcomeId == \"MOUT-Applied\" or receipt.outcomeId == \"MOUT-Idempotent\") \"PDISP-Advance\""
        "    if (receipt.outcomeId == \"MOUT-Applied\" or receipt.outcomeId == \"MOUT-Idempotent\") \"PDISP-Replan\""

    requireDurablePlanRed
        "disposition-boundary-history"
        "    applied.step.compensationBoundaryId == current.compensationBoundaryId,"
        "    true,"

    let requireDesiredStateRed (name: string) (fixture: string) (replacement: string) =
        enqueueRustMutation
            "DESIRED-STATE-NEGATIVE-CONTROL"
            "protocol-desired-state"
            "^testDesiredState"
            name
            fixture
            replacement

    requireDesiredStateRed
        "family-completeness"
        "    facts.map(fact => fact.familyId) == desiredStateFamilyCatalogue.map(family => family.id),"
        "    true,"

    requireDesiredStateRed
        "subject-binding"
        "    desired.subjectId == observed.subjectId, desired.profileId == observed.profileId,"
        "    true, desired.profileId == observed.profileId,"

    requireDesiredStateRed
        "profile-binding"
        "    desired.subjectId == observed.subjectId, desired.profileId == observed.profileId,"
        "    desired.subjectId == observed.subjectId, true,"

    requireDesiredStateRed
        "surface-binding"
        "      family.requiredPermission == fact.requiredPermission, family.surfaceIds == fact.surfaceIds,"
        "      family.requiredPermission == fact.requiredPermission, true,"

    requireDesiredStateRed
        "unsupported-classification"
        "    else if (not(observed.supported) or observed.outcomeId == \"OBS-Unsupported\") \"DSPLAN-Unsupported\""
        "    else if (observed.outcomeId == \"OBS-Unsupported\") \"DSPLAN-Ready\""

    requireDesiredStateRed
        "permission-classification"
        "    else if (not(observed.permissionGranted) or observed.outcomeId == \"OBS-Unauthorized\") \"DSPLAN-Unauthorized\""
        "    else if (observed.outcomeId == \"OBS-Unauthorized\") \"DSPLAN-Ready\""

    requireDesiredStateRed
        "stale-classification"
        "    else if (observed.outcomeId == \"OBS-Stale\") \"DSPLAN-Stale\""
        "    else if (observed.outcomeId == \"OBS-Stale\") \"DSPLAN-Ready\""

    requireDesiredStateRed
        "policy-content-binding"
        "    desired.contentDigest == observed.contentDigest,\n  }\n\n  pure def desiredStateSpecificationIsComplete"
        "    true,\n  }\n\n  pure def desiredStateSpecificationIsComplete"

    requireDesiredStateRed
        "phase-authorization"
        "      desiredStateMayApply(desired, observed),\n    },\n    and {\n      fromPhaseId == \"DSPH-Plan\", toPhaseId == \"DSPH-Verify\""
        "      true,\n    },\n    and {\n      fromPhaseId == \"DSPH-Plan\", toPhaseId == \"DSPH-Verify\""

    requireDesiredStateRed
        "desired-qualification"
        "    desired.complete, desired.supported, desired.permissionGranted, desired.outcomeId == \"OBS-Observed\","
        "    true,"

    requireDesiredStateRed
        "apply-receipt"
        "      Set(\"MOUT-Applied\", \"MOUT-Idempotent\").contains(authority.applyReceiptOutcomeId),"
        "      true,"

    requireDesiredStateRed
        "phase-authority-binding"
        "      desiredStatePhaseAuthorityMatches(authority, desired, observed),"
        "      true,"

    let requireCompiledOutputRed (name: string) (fixture: string) (replacement: string) =
        enqueueRustMutation
            "COMPILED-OUTPUT-NEGATIVE-CONTROL"
            "protocol-compiled-output"
            "^testCompiledOutput"
            name
            fixture
            replacement

    requireCompiledOutputRed
        "duplicate-family"
        "    outputs.size() == compiledOutputFamilyCatalogue.size(),"
        "    true,"

    requireCompiledOutputRed
        "family-completeness"
        ("    outputs.size() == compiledOutputFamilyCatalogue.size(),\n"
         + "    outputs.map(output => output.familyId) == compiledOutputFamilyCatalogue.map(family => family.id),\n"
         + "    outputs.map(output => output.ordinal) == Set(1, 2, 3, 4, 5, 6, 7, 8, 9),")
        "    true,\n    true,\n    true,"

    requireCompiledOutputRed
        "family-order"
        "      family.id == output.familyId, family.ordinal == output.ordinal,"
        "      family.id == output.familyId, true,"

    requireCompiledOutputRed "source-identity" "    output.sourceIdentity == \"literate-quint-authority\"," "    true,"

    requireCompiledOutputRed
        "support-qualification"
        "    output.contentDigest != \"\", output.supported, output.complete, output.fresh,"
        "    output.contentDigest != \"\", true, output.complete, output.fresh,"

    requireCompiledOutputRed
        "completeness-qualification"
        "    output.contentDigest != \"\", output.supported, output.complete, output.fresh,"
        "    output.contentDigest != \"\", output.supported, true, output.fresh,"

    requireCompiledOutputRed
        "freshness-qualification"
        "    output.contentDigest != \"\", output.supported, output.complete, output.fresh,"
        "    output.contentDigest != \"\", output.supported, output.complete, true,"

    requireCompiledOutputRed
        "projection-formats"
        "      family.contentContract == output.contentContract, family.formats == output.formats,"
        "      family.contentContract == output.contentContract, true,"

    if rustMutationChecks.Count <> 56 then
        fail "RUST-MUTATION-INVENTORY" ($"expected=56; actual=%d{rustMutationChecks.Count}")

    let parallelOptions = ParallelOptions(MaxDegreeOfParallelism = 2)

    Parallel.ForEach(rustMutationChecks, parallelOptions, fun check -> check ())
    |> ignore

    let guard = "    evidenceObserved,\n    evidenceObserved' = evidenceObserved,"

    if not (originalQnt.Contains(guard, StringComparison.Ordinal)) then
        fail "NEGATIVE-CONTROL" "guard fixture absent"

    File.WriteAllText(mutatedQnt, originalQnt.Replace(guard, "    evidenceObserved' = evidenceObserved,"))

    let counterexample = Path.Combine(scratch, "counterexample.itf.json")

    let redExit, redOutput, redError =
        run
            scratch
            quint
            [
                "verify"
                mutatedQnt
                "--main"
                "CoordinationProtocol"
                "--init"
                "init"
                "--step"
                "step"
                "--invariant"
                "acceptedVocabularyIsQualified"
                "--max-steps"
                "1"
                "--out-itf"
                counterexample
                "--verbosity"
                "1"
            ]
            environment

    if redExit = 0 then
        fail "NEGATIVE-CONTROL" "missing evidence guard passed"

    if not (File.Exists counterexample) then
        fail "NEGATIVE-CONTROL" ($"missing ITF; {redOutput}; {redError}")

    let lifecycleMutant = Path.Combine(scratch, "protocol-intent-follows-claim.qnt")

    let preservedIntent =
        "    humanIntentId' = humanIntentId,\n    authorizedHumanIntentId' = authorizedHumanIntentId,\n    lifecycleFacts' = facts,"

    let collapsedIntent =
        "    humanIntentId' = if (facts.claimPresent) \"INTENT-Ready\" else humanIntentId,\n    authorizedHumanIntentId' = authorizedHumanIntentId,\n    lifecycleFacts' = facts,"

    if not (originalQnt.Contains(preservedIntent, StringComparison.Ordinal)) then
        fail "LIFECYCLE-NEGATIVE-CONTROL" "intent-preservation fixture absent"

    File.WriteAllText(lifecycleMutant, originalQnt.Replace(preservedIntent, collapsedIntent))

    let lifecycleCounterexample =
        Path.Combine(scratch, "counterexample-intent-follows-claim.itf.json")

    let lifecycleRedExit, lifecycleRedOutput, lifecycleRedError =
        run
            scratch
            quint
            [
                "verify"
                lifecycleMutant
                "--main"
                "CoordinationProtocol"
                "--init"
                "init"
                "--step"
                "step"
                "--invariant"
                "humanIntentIsObservationIndependent"
                "--max-steps"
                "1"
                "--out-itf"
                lifecycleCounterexample
                "--verbosity"
                "1"
            ]
            environment

    if lifecycleRedExit = 0 then
        fail "LIFECYCLE-NEGATIVE-CONTROL" "claim-to-intent collapse passed"

    if not (File.Exists lifecycleCounterexample) then
        fail "LIFECYCLE-NEGATIVE-CONTROL" ($"missing ITF; {lifecycleRedOutput}; {lifecycleRedError}")

    let replacementMutant =
        Path.Combine(scratch, "protocol-relation-whole-set-replacement.qnt")

    let edgeLocalAdd =
        "    nativeRelationEdges' = nativeRelationEdges.union(Set(edge)),"

    let wholeSetReplacement = "    nativeRelationEdges' = Set(edge),"

    if not (originalQnt.Contains(edgeLocalAdd, StringComparison.Ordinal)) then
        fail "RELATION-NEGATIVE-CONTROL" "edge-local add fixture absent"

    File.WriteAllText(replacementMutant, originalQnt.Replace(edgeLocalAdd, wholeSetReplacement))

    let replacementCounterexample =
        Path.Combine(scratch, "counterexample-relation-whole-set-replacement.itf.json")

    let replacementRedExit, replacementRedOutput, replacementRedError =
        run
            scratch
            quint
            [
                "verify"
                replacementMutant
                "--main"
                "CoordinationProtocol"
                "--init"
                "init"
                "--step"
                "step"
                "--invariant"
                "relationChangesPreserveUnrelatedEdges"
                "--max-steps"
                "2"
                "--out-itf"
                replacementCounterexample
                "--verbosity"
                "1"
            ]
            environment

    if replacementRedExit = 0 then
        fail "RELATION-NEGATIVE-CONTROL" "whole-set replacement passed"

    if not (File.Exists replacementCounterexample) then
        fail
            "RELATION-NEGATIVE-CONTROL"
            ($"whole-set replacement missing ITF; {replacementRedOutput}; {replacementRedError}")

    let selfEdgeMutant = Path.Combine(scratch, "protocol-relation-self-edge.qnt")
    let validRelationStep = "    addNativeRelation(parentChildEdge),"

    let invalidRelationStep =
        "    addNativeRelation({ ...parentChildEdge, targetId: parentChildEdge.sourceId }),"

    let relationGuard =
        "    nativeRelationEdgeIsValid(edge),\n    evidenceObserved' = evidenceObserved,"

    if not (originalQnt.Contains(validRelationStep, StringComparison.Ordinal)) then
        fail "RELATION-VALIDITY-NEGATIVE-CONTROL" "relation step fixture absent"

    if not (originalQnt.Contains(relationGuard, StringComparison.Ordinal)) then
        fail "RELATION-VALIDITY-NEGATIVE-CONTROL" "relation guard fixture absent"

    let withoutRelationGuard =
        originalQnt.Replace(relationGuard, "    evidenceObserved' = evidenceObserved,")

    File.WriteAllText(selfEdgeMutant, withoutRelationGuard.Replace(validRelationStep, invalidRelationStep))

    let selfEdgeCounterexample =
        Path.Combine(scratch, "counterexample-relation-self-edge.itf.json")

    let selfEdgeRedExit, selfEdgeRedOutput, selfEdgeRedError =
        run
            scratch
            quint
            [
                "verify"
                selfEdgeMutant
                "--main"
                "CoordinationProtocol"
                "--init"
                "init"
                "--step"
                "step"
                "--invariant"
                "nativeRelationEdgesAreValid"
                "--max-steps"
                "1"
                "--out-itf"
                selfEdgeCounterexample
                "--verbosity"
                "1"
            ]
            environment

    if selfEdgeRedExit = 0 then
        fail "RELATION-VALIDITY-NEGATIVE-CONTROL" "self edge passed"

    if not (File.Exists selfEdgeCounterexample) then
        fail "RELATION-VALIDITY-NEGATIVE-CONTROL" ($"self edge missing ITF; {selfEdgeRedOutput}; {selfEdgeRedError}")

    let orderingMutant = Path.Combine(scratch, "protocol-stream-ordering-gap.qnt")
    let orderedAppendGuard = "      protocolAppendHasPredecessor(envelope, events),"
    let gapStep = "    appendProtocolEnvelope(leaseEnvelope),"

    let invalidGapStep =
        "    appendProtocolEnvelope({ ...leaseEnvelope, sequence: 3 }),"

    if not (originalQnt.Contains(orderedAppendGuard, StringComparison.Ordinal)) then
        fail "PROTOCOL-STREAM-ORDERING-NEGATIVE-CONTROL" "append ordering guard absent"

    if not (originalQnt.Contains(gapStep, StringComparison.Ordinal)) then
        fail "PROTOCOL-STREAM-ORDERING-NEGATIVE-CONTROL" "gap step fixture absent"

    File.WriteAllText(
        orderingMutant,
        originalQnt.Replace(orderedAppendGuard, "      true,").Replace(gapStep, invalidGapStep)
    )

    let orderingCounterexample =
        Path.Combine(scratch, "counterexample-protocol-stream-ordering-gap.itf.json")

    let orderingRedExit, orderingRedOutput, orderingRedError =
        run
            scratch
            quint
            [
                "verify"
                orderingMutant
                "--main"
                "CoordinationProtocol"
                "--init"
                "init"
                "--step"
                "step"
                "--invariant"
                "protocolEnvelopesAreValidAndOrdered"
                "--max-steps"
                "2"
                "--out-itf"
                orderingCounterexample
                "--verbosity"
                "1"
            ]
            environment

    if orderingRedExit = 0 then
        fail "PROTOCOL-STREAM-ORDERING-NEGATIVE-CONTROL" "ordering gap passed"

    if not (File.Exists orderingCounterexample) then
        fail
            "PROTOCOL-STREAM-ORDERING-NEGATIVE-CONTROL"
            ($"ordering gap missing ITF; {orderingRedOutput}; {orderingRedError}")

    let retentionMutant = Path.Combine(scratch, "protocol-stream-retention-relabel.qnt")

    let appendAdmissionGuard =
        "    protocolAppendIsValid(envelope, protocolStreamEvents),"

    let validClaimRetention =
        "    payloadKindId: \"PAYLOAD-Claim\", retentionClass: \"ephemeral\", durableCheckpoint: false,"

    let relabeledClaimRetention =
        "    payloadKindId: \"PAYLOAD-Claim\", retentionClass: \"durable\", durableCheckpoint: true,"

    if not (originalQnt.Contains(appendAdmissionGuard, StringComparison.Ordinal)) then
        fail "PROTOCOL-STREAM-RETENTION-NEGATIVE-CONTROL" "append admission guard absent"

    if not (originalQnt.Contains(validClaimRetention, StringComparison.Ordinal)) then
        fail "PROTOCOL-STREAM-RETENTION-NEGATIVE-CONTROL" "claim retention fixture absent"

    File.WriteAllText(
        retentionMutant,
        originalQnt.Replace(appendAdmissionGuard, "    true,").Replace(validClaimRetention, relabeledClaimRetention)
    )

    let retentionCounterexample =
        Path.Combine(scratch, "counterexample-protocol-stream-retention-relabel.itf.json")

    let retentionRedExit, retentionRedOutput, retentionRedError =
        run
            scratch
            quint
            [
                "verify"
                retentionMutant
                "--main"
                "CoordinationProtocol"
                "--init"
                "init"
                "--step"
                "step"
                "--invariant"
                "protocolEnvelopesAreValidAndOrdered"
                "--max-steps"
                "1"
                "--out-itf"
                retentionCounterexample
                "--verbosity"
                "1"
            ]
            environment

    if retentionRedExit = 0 then
        fail "PROTOCOL-STREAM-RETENTION-NEGATIVE-CONTROL" "retention relabel passed"

    if not (File.Exists retentionCounterexample) then
        fail
            "PROTOCOL-STREAM-RETENTION-NEGATIVE-CONTROL"
            ($"retention relabel missing ITF; {retentionRedOutput}; {retentionRedError}")

    let checkpointMutant =
        Path.Combine(scratch, "protocol-stream-durable-compaction.qnt")

    let compactionGuard =
        "    ephemeralEnvelopeMayBeCompacted(envelope, protocolStreamEvents),"

    let compactEphemeralStep =
        "    compactEphemeralProtocolEnvelope(operationLockEnvelope),"

    let compactDurableStep =
        "    compactEphemeralProtocolEnvelope(reviewCheckpointEnvelope),"

    if not (originalQnt.Contains(compactionGuard, StringComparison.Ordinal)) then
        fail "PROTOCOL-STREAM-CHECKPOINT-NEGATIVE-CONTROL" "compaction guard absent"

    File.WriteAllText(
        checkpointMutant,
        originalQnt.Replace(compactionGuard, "    true,").Replace(compactEphemeralStep, compactDurableStep)
    )

    let checkpointCounterexample =
        Path.Combine(scratch, "counterexample-protocol-stream-durable-compaction.itf.json")

    let checkpointRedExit, checkpointRedOutput, checkpointRedError =
        run
            scratch
            quint
            [
                "verify"
                checkpointMutant
                "--main"
                "CoordinationProtocol"
                "--init"
                "init"
                "--step"
                "step"
                "--invariant"
                "durableProtocolCheckpointsArePreserved"
                "--max-steps"
                "2"
                "--out-itf"
                checkpointCounterexample
                "--verbosity"
                "1"
            ]
            environment

    if checkpointRedExit = 0 then
        fail "PROTOCOL-STREAM-CHECKPOINT-NEGATIVE-CONTROL" "durable checkpoint compaction passed"

    if not (File.Exists checkpointCounterexample) then
        fail
            "PROTOCOL-STREAM-CHECKPOINT-NEGATIVE-CONTROL"
            ($"durable checkpoint compaction missing ITF; {checkpointRedOutput}; {checkpointRedError}")

    let unrelatedCheckpointMutant =
        Path.Combine(scratch, "protocol-stream-unrelated-checkpoint.qnt")

    let streamBoundCompaction =
        "      checkpoint.streamKindId == envelope.streamKindId,\n"
        + "      checkpoint.streamId == envelope.streamId,\n"
        + "      checkpoint.subjectId == envelope.subjectId,\n"
        + "      checkpoint.generation == envelope.generation,\n"
        + "      checkpoint.sequence > envelope.sequence,\n"
        + "      checkpoint.durableCheckpoint,\n"
        + "      checkpoint.retentionClass == \"durable\","

    let subjectOnlyCompaction =
        "      checkpoint.subjectId == envelope.subjectId,\n"
        + "      checkpoint.durableCheckpoint,\n"
        + "      checkpoint.retentionClass == \"durable\","

    if not (originalQnt.Contains(streamBoundCompaction, StringComparison.Ordinal)) then
        fail "PROTOCOL-STREAM-CAUSAL-COMPACTION-NEGATIVE-CONTROL" "stream-bound compaction fixture absent"

    File.WriteAllText(unrelatedCheckpointMutant, originalQnt.Replace(streamBoundCompaction, subjectOnlyCompaction))

    let unrelatedRedExit, unrelatedRedOutput, unrelatedRedError =
        run
            scratch
            quint
            [
                "test"
                unrelatedCheckpointMutant
                "--main"
                "CoordinationProtocolTests"
                "--backend"
                "rust"
                "--match"
                "^testUnrelatedCheckpointCannotCompactEphemeralHistory$"
                "--verbosity"
                "0"
            ]
            []

    if unrelatedRedExit = 0 then
        fail "PROTOCOL-STREAM-CAUSAL-COMPACTION-NEGATIVE-CONTROL" "unrelated checkpoint authorized compaction"

    if not ((unrelatedRedOutput + "\n" + unrelatedRedError).Contains("failed", StringComparison.OrdinalIgnoreCase)) then
        fail
            "PROTOCOL-STREAM-CAUSAL-COMPACTION-NEGATIVE-CONTROL"
            ($"unrelated checkpoint mutant did not produce a failed test; {unrelatedRedOutput}; {unrelatedRedError}")

    let unrelatedPredecessorMutant =
        Path.Combine(scratch, "protocol-stream-unrelated-predecessor.qnt")

    let streamBoundPredecessor =
        "        checkpoint.streamKindId == envelope.streamKindId,\n"
        + "        checkpoint.streamId == envelope.streamId,\n"
        + "        checkpoint.subjectId == envelope.subjectId,\n"
        + "        checkpoint.generation == envelope.generation,\n"
        + "        checkpoint.sequence > envelope.sequence,\n"
        + "        checkpoint.durableCheckpoint,"

    let subjectOnlyPredecessor =
        "        checkpoint.subjectId == envelope.subjectId,\n"
        + "        checkpoint.durableCheckpoint,"

    if not (originalQnt.Contains(streamBoundPredecessor, StringComparison.Ordinal)) then
        fail "PROTOCOL-STREAM-RETAINED-ORDERING-NEGATIVE-CONTROL" "stream-bound predecessor fixture absent"

    File.WriteAllText(unrelatedPredecessorMutant, originalQnt.Replace(streamBoundPredecessor, subjectOnlyPredecessor))

    let predecessorRedExit, predecessorRedOutput, predecessorRedError =
        run
            scratch
            quint
            [
                "test"
                unrelatedPredecessorMutant
                "--main"
                "CoordinationProtocolTests"
                "--backend"
                "rust"
                "--match"
                "^testUnrelatedCheckpointCannotExcuseMissingPredecessor$"
                "--verbosity"
                "0"
            ]
            []

    if predecessorRedExit = 0 then
        fail "PROTOCOL-STREAM-RETAINED-ORDERING-NEGATIVE-CONTROL" "unrelated checkpoint excused a missing predecessor"

    if
        not ((predecessorRedOutput + "\n" + predecessorRedError).Contains("failed", StringComparison.OrdinalIgnoreCase))
    then
        fail
            "PROTOCOL-STREAM-RETAINED-ORDERING-NEGATIVE-CONTROL"
            ($"unrelated predecessor mutant did not produce a failed test; {predecessorRedOutput}; {predecessorRedError}")

    let requireAuthorityRed name (observationMutation: string -> string) (sourceMutation: string -> string) =
        let mutated = Path.Combine(scratch, $"protocol-%s{name}.qnt")

        let withoutQualificationGuard =
            originalQnt.Replace(
                "    authorityObservationIsQualified(authorityObservation),\n    evidenceObserved' = evidenceObserved,",
                "    evidenceObserved' = evidenceObserved,"
            )

        if withoutQualificationGuard = originalQnt then
            fail "AUTHORITY-NEGATIVE-CONTROL" "qualification guard fixture absent"

        let changedObservation = observationMutation withoutQualificationGuard
        let changedSource = sourceMutation changedObservation
        File.WriteAllText(mutated, changedSource)
        let outItf = Path.Combine(scratch, $"counterexample-%s{name}.itf.json")

        let exitCode, output, error =
            run
                scratch
                quint
                [
                    "verify"
                    mutated
                    "--main"
                    "CoordinationProtocol"
                    "--init"
                    "init"
                    "--step"
                    "step"
                    "--invariant"
                    "acceptedAuthoritiesAreQualified"
                    "--max-steps"
                    "2"
                    "--out-itf"
                    outItf
                    "--verbosity"
                    "1"
                ]
                environment

        if exitCode = 0 then
            fail "AUTHORITY-NEGATIVE-CONTROL" ($"%s{name} passed")

        if not (File.Exists outItf) then
            fail "AUTHORITY-NEGATIVE-CONTROL" ($"%s{name} missing ITF; %s{output}; %s{error}")

    let mutateStep replacement (text: string) =
        text.Replace("    observeAuthority(nativeGitHubObservation),", $"    observeAuthority(%s{replacement}),")

    let unchanged (text: string) = text
    requireAuthorityRed "incomplete" (mutateStep "{ ...nativeGitHubObservation, complete: false }") unchanged

    requireAuthorityRed
        "stale-revision"
        (mutateStep "{ ...nativeGitHubObservation, revisionValue: \"stale-revision\" }")
        unchanged

    requireAuthorityRed
        "wrong-revision-kind"
        (mutateStep "{ ...nativeGitHubObservation, revisionKind: \"wrong-kind\" }")
        unchanged

    requireAuthorityRed
        "wrong-authority"
        (mutateStep "{ ...nativeGitHubObservation, authorityId: \"AUTH-PackageFeed\" }")
        unchanged

    requireAuthorityRed "contradictory" (mutateStep "{ ...nativeGitHubObservation, contradictory: true }") unchanged

    let omittedFamilyRow =
        "    { id: \"AUTH-ClassifiedExternal\", kind: \"authorityBinding\", family: \"classified-external\", revisionKind: \"classified-external-revision\", revisionValue: \"declared-source-revision\", completenessContract: \"complete-required-fields\", evidenceRelationship: \"REL-AUTH-ClassifiedExternal-Evidence\" }"

    requireAuthorityRed "omitted-family" (mutateStep "nativeGitHubObservation") (fun text ->
        text.Replace(omittedFamilyRow, ""))

    if verifiedPositiveInvariantCount <> 8 then
        fail "POSITIVE-INVARIANT-COVERAGE" ($"expected=8; actual=%d{verifiedPositiveInvariantCount}")

    if quintRejectedProcessCount <> expectedRejectedProcessCount then
        fail
            "NEGATIVE-CONTROL-COVERAGE"
            ($"expected=%d{expectedRejectedProcessCount}; actual=%d{quintRejectedProcessCount}")

    requireCompletedProcessInventory ()

    if measurementOnly then
        let expectedRoots = rootConfiguration |> Map.keys |> Set.ofSeq

        let expectedFormal =
            formalTests
            |> List.map (fun (id, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _) -> id)
            |> Set.ofList

        if
            Set.ofSeq rootMeasurements.Keys <> expectedRoots
            || Set.ofSeq formalMeasurements.Keys <> expectedFormal
            || samplingMeasurements.Count <> 13
            || (samplingMeasurements
                |> Seq.map (fun (id, _, _, _) -> id)
                |> Set.ofSeq
                |> Set.count)
               <> 13
        then
            fail "MEASUREMENT-COVERAGE" "missing-or-duplicate-observations"

        for KeyValue(id, m) in formalMeasurements do
            if
                m.SampleCount < 1
                || m.ElapsedMs <= 0L
                || m.PeakMiB <= 0
                || m.ArtifactBytes <= 0L
            then
                fail "MEASUREMENT-FORMAL-METRIC" id

        verifyMeasurementDirectory ()
        let candidate = measurementDirectory.Value

        for directory, _, _, _ in projectionReceipts do
            verifyMeasurementDirectory ()

            File.Copy(
                Path.Combine(directory, "projection.json"),
                Path.Combine(candidate, Path.GetFileName(directory) + ".json"),
                false
            )

        Directory.CreateDirectory candidate |> ignore
        let writeJson = writeMeasurementJson
        let configDigest = measurementConfigurationDigest
        let measuredAt = DateTimeOffset.UtcNow.ToString("O")

        let journal =
            Directory.GetFiles(candidate, "physical-*.json")
            |> Array.sort
            |> Array.map (fun path ->
                {|
                    name = Path.GetFileName path
                    sha256 = sha256 path
                |})

        if journal.Length <> physicalAttempts.Count * 2 then
            fail "MEASUREMENT-PHYSICAL-COVERAGE" "active-or-completed-missing"

        writeJson
            "physical-inventory.json"
            {|
                schema = "fsgg.quint-physical-inventory/2"
                attemptId = measurementAttemptId
                sourceSha256 = expectedSource
                configurationSha256 = configDigest
                toolchainSha256 = expectedToolchain
                validatorSha256 = measurementValidatorDigest
                compilerSha256 = compilerIdentitySha256
                logicalCount = logicalSequence
                physicalCount = physicalAttempts.Count
                allAttemptElapsedMs = (physicalAttempts |> Seq.sumBy (fun row -> row.ElapsedMs.Value))
                files = journal
            |}

        let invocationMetrics =
            samplingMeasurements
            |> Seq.map (fun (id, elapsed, peak, bytes) ->
                {|
                    id = id
                    elapsedMs = elapsed
                    peakMiB = peak
                    artifactBytes = bytes
                    logicalId =
                        (physicalAttempts
                         |> Seq.filter (fun row -> row.Scope = Some("sampling/" + id))
                         |> Seq.exactlyOne)
                            .LogicalId
                |})
            |> Seq.toArray

        writeJson
            "preflight-sampling.json"
            {|
                schema = "fsgg.preflight-sampling-observation/2"
                attemptId = measurementAttemptId
                validatorSha256 = measurementValidatorDigest
                compilerSha256 = compilerIdentitySha256
                toolchainSha256 = expectedToolchain
                disposition = "bounded-sampling-not-proof"
                sourceSha256 = expectedSource
                configurationSha256 = configDigest
                invocations = invocationMetrics
            |}

        if interactionMeasurements.Count <> 2 then
            fail "PREFLIGHT-INTERACTION-COVERAGE" "expected-two"

        writeJson
            "preflight-interaction.json"
            {|
                schema = "fsgg.preflight-interaction-observation/2"
                attemptId = measurementAttemptId
                validatorSha256 = measurementValidatorDigest
                compilerSha256 = compilerIdentitySha256
                toolchainSha256 = expectedToolchain
                configurationSha256 = configDigest
                disposition = "native-causal-equivalence-not-aggregate-profile"
                sourceSha256 = expectedSource
                invocations =
                    (interactionMeasurements
                     |> Seq.map (fun (id, elapsed, peak, bytes) ->
                         {|
                             id = id
                             elapsedMs = elapsed
                             peakMiB = peak
                             artifactBytes = bytes
                             logicalId =
                                 (physicalAttempts
                                  |> Seq.filter (fun row -> row.Scope = Some("interaction/" + id))
                                  |> Seq.exactlyOne)
                                     .LogicalId
                             artifactSha256 = sha256 (Path.Combine(candidate, "interaction-" + id + ".itf.json"))
                         |})
                     |> Seq.toArray)
            |}

        writeJson
            "closure.json"
            {|
                schema = "fsgg.coordination.quint-calibration-observation/2"
                attemptId = measurementAttemptId
                validatorSha256 = measurementValidatorDigest
                compilerSha256 = compilerIdentitySha256
                compilerFiles = compilerFiles
                physicalInventorySha256 = sha256 (Path.Combine(candidate, "physical-inventory.json"))
                outcome = "observed-complete-unadmitted"
                sourceSha256 = expectedSource
                configurationSha256 = configDigest
                toolchainSha256 = expectedToolchain
                contractSha256 = expectedContract
                preparationSha256 = preparationSha256
                assembledQuintSha256 = sha256 q2Qnt
                measuredAt = measuredAt
                runnerClass = "linux-x64-local-exact-sdk-single-compiler-observed"
                memoryMethod = "process-tree-proc-rss-sampled-10ms"
                rootArtifactMethod = "state-native-itf-with-observed-witness-samples-and-test-native-passed-array"
                observedIds = Set.union expectedRoots expectedFormal |> Set.toArray
            |}

        q2Outcome <- "measured-unadmitted"
        writeQualificationReceipt None

        printfn
            "CANONICAL_QUINT_MEASUREMENT_OBSERVED disposition=unadmitted output=%s source=%s"
            measurementCustody.Value.NamedPath
            expectedSource

        exit 0

    q2Outcome <- "passed"
    writeQualificationReceipt None
    let totalDurationMs = qualificationClock.ElapsedMilliseconds

    printfn
        "CANONICAL_QUINT_PROTOCOL_OK contract=%s source=%s profile=%s receipt=%s durationMs=%d"
        expectedContract
        expectedSource
        expectedProfile
        qualificationOutput
        totalDurationMs
finally
    if Directory.Exists scratch then
        Directory.Delete(scratch, true)
