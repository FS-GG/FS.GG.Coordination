#nowarn "9" // Fixed-layout Linux statx ABI used to bind filesystem identity.

namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type CapsuleCheckKind =
    | Import
    | Discovery

/// Owner-reviewed no-workload command. Results are observations, never caller-supplied readiness.
type CapsuleCheck =
    {
        Id: string
        Kind: CapsuleCheckKind
        Executable: string
        Arguments: string list
        ExpectedDiscoveries: string list
    }

/// Exact assembled input closure; scratch must be outside this immutable directory.
type CapsulePreparation =
    {
        Root: string
        Inputs: string list
        Checks: CapsuleCheck list
        Environment: Map<string, string>
        MaximumInputBytes: uint64
        MaximumOutputBytes: uint64
        MaximumCheckSeconds: int
    }

[<RequireQualifiedAccess>]
type PortablePrerequisiteRequirement =
    | CapsuleRequired of CapsulePreparation
    | CapsuleUnavailable

[<Struct; StructLayout(LayoutKind.Explicit, Size = 256)>]
type private CapsuleStat =
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

module private CapsuleNative =
    [<DllImport("libc", SetLastError = true, EntryPoint = "statx")>]
    extern int statx(int dirfd, string path, int flags, uint32 mask, CapsuleStat& result)

    [<DllImport("libc", SetLastError = true, EntryPoint = "kill")>]
    extern int kill(int pid, int signal)

    [<DllImport("libc", SetLastError = true, EntryPoint = "open")>]
    extern int openRead(string path, int flags)

    [<DllImport("libc", SetLastError = true, EntryPoint = "pidfd_open")>]
    extern int pidfdOpen(int pid, uint32 flags)

    [<DllImport("libc", SetLastError = true, EntryPoint = "pidfd_send_signal")>]
    extern int pidfdSignal(int descriptor, int signal, nativeint info, uint32 flags)

/// Bounded owned observations. Distinct identity accounting is separate from event/byte accounting.
type PreparationObservationLimits =
    {
        MaximumDistinct: int
        MaximumEvents: int
        MaximumBytes: int64
        Deadline: int64
    }

type PreparationObservationState =
    {
        Time: int64
        Identities: Set<string>
        Owned: Map<string, int64>
        Events: int
        Bytes: int64
        CleanupPending: Set<string>
        CleanupObserved: Set<string>
    }

type PreparationObservationDecision =
    {
        State: PreparationObservationState
        Effects: string list
        Refusal: string option
    }

[<RequireQualifiedAccess>]
module PreparationObservation =
    let initial =
        {
            Time = 0L
            Identities = Set.empty
            Owned = Map.empty
            Events = 0
            Bytes = 0L
            CleanupPending = Set.empty
            CleanupObserved = Set.empty
        }

    /// Invalid requests have no transition. Valid time advance retains retirement effects on refusal.
    let observe (limits: PreparationObservationLimits) now identity bytes (state: PreparationObservationState) =
        let refuse reason current effects =
            {
                State = current
                Effects = effects
                Refusal = Some reason
            }

        if
            String.IsNullOrWhiteSpace identity
            || bytes < 0L
            || now < state.Time
            || limits.MaximumDistinct < 1
            || limits.MaximumEvents < 1
            || limits.MaximumBytes < 1L
        then
            refuse "invalid" state []
        else
            let expired =
                state.Owned
                |> Map.toList
                |> List.filter (fun (_, deadline) -> now >= deadline)
                |> List.map fst

            let current =
                { state with
                    Time = now
                    Owned = expired |> List.fold (fun owned id -> Map.remove id owned) state.Owned
                    CleanupPending = Set.union state.CleanupPending (Set.ofList expired)
                }

            let effects =
                expired |> List.collect (fun id -> [ "retire:" + id; "cleanup:" + id ])

            if now >= limits.Deadline then
                refuse "deadline" current effects
            elif Set.contains identity current.CleanupObserved then
                refuse "closed" current effects
            elif
                not (Set.contains identity current.Identities)
                && current.Identities.Count >= limits.MaximumDistinct
            then
                refuse "distinct" current effects
            elif current.Events >= limits.MaximumEvents then
                refuse "events" current effects
            elif bytes > limits.MaximumBytes - current.Bytes then
                refuse "bytes" current effects
            else
                {
                    State =
                        { current with
                            Identities = Set.add identity current.Identities
                            Owned = Map.add identity limits.Deadline current.Owned
                            Events = current.Events + 1
                            Bytes = current.Bytes + bytes
                        }
                    Effects = effects @ [ "observe:" + identity ]
                    Refusal = None
                }

    let cleanup identity terminationObserved (state: PreparationObservationState) =
        if
            not terminationObserved
            || not (
                Map.containsKey identity state.Owned
                || Set.contains identity state.CleanupPending
            )
        then
            {
                State = state
                Effects = []
                Refusal = Some "cleanup-unobserved"
            }
        else
            {
                State =
                    { state with
                        Owned = Map.remove identity state.Owned
                        CleanupPending = Set.remove identity state.CleanupPending
                        CleanupObserved = Set.add identity state.CleanupObserved
                    }
                Effects = [ "cleaned:" + identity ]
                Refusal = None
            }

type PreparedArtifactState =
    {
        InputIdentity: string
        RequiredChecks: Set<string>
        ObservedChecks: Set<string>
        PreparedIdentity: string option
        EffectStarted: bool
        CleanupObserved: bool
    }

[<RequireQualifiedAccess>]
module PreparedArtifact =
    let initial identity required =
        {
            InputIdentity = identity
            RequiredChecks = required
            ObservedChecks = Set.empty
            PreparedIdentity = None
            EffectStarted = false
            CleanupObserved = false
        }

    let observe check state =
        if not (Set.contains check state.RequiredChecks) then
            Error "preparation-check-unknown"
        else
            let observed = Set.add check state.ObservedChecks

            Ok
                { state with
                    ObservedChecks = observed
                    PreparedIdentity =
                        if observed = state.RequiredChecks then
                            Some state.InputIdentity
                        else
                            None
                }

    let invalidate identity state =
        if identity = state.InputIdentity then
            state
        else
            { state with
                InputIdentity = identity
                ObservedChecks = Set.empty
                PreparedIdentity = None
            }

    let consume identity state =
        let current = invalidate identity state

        if current.PreparedIdentity <> Some identity then
            current, [], Some "preparation-input-invalidated"
        else
            { current with EffectStarted = true }, [ "effect" ], None

    let cleanup observed state =
        if observed && state.EffectStarted then
            { state with CleanupObserved = true }, [ "cleaned" ], None
        else
            state, [], Some "preparation-cleanup-unobserved"

/// Privately constructed in this assembly. JSON receipts cannot reconstruct this capability.
[<Sealed>]
type PreparedAttempt
    private
    (
        binding: string,
        deadline: DateTimeOffset,
        identities: (string * string) list,
        artifact: PreparedArtifactState option
    ) =
    member internal _.Binding = binding
    member internal _.Deadline = deadline
    member internal _.Identities = identities
    member internal _.Artifact = artifact

    static member internal Create(binding, deadline, identities, artifact) =
        PreparedAttempt(binding, deadline, identities, artifact)

[<RequireQualifiedAccess>]
module PreparedAttempt =
    // Existing reviewed runtimes already establish source/image/recipe custody in their runner.
    // Their validated contract still passes through an opaque command/deadline boundary.
    let internal prepareReviewedContract binding deadline =
        PreparedAttempt.Create(binding, deadline, [], None)

    let internal validateReviewedContract now binding (attempt: PreparedAttempt) =
        if now >= attempt.Deadline then
            Error "preparation-deadline-refused"
        elif binding <> attempt.Binding then
            Error "preparation-binding-invalidated"
        else
            Ok()

    let private digest bytes =
        SHA256.HashData(bytes: byte array) |> Convert.ToHexString

    let private statAt descriptor path flags =
        let mutable stat = Unchecked.defaultof<CapsuleStat>

        if
            CapsuleNative.statx (descriptor, path, flags, 0x7ffu, &stat) <> 0
            || stat.Mask &&& 0x100u = 0u
        then
            Error "preparation-file-identity-unavailable"
        else
            Ok stat

    let private nodeIdentity (stat: CapsuleStat) =
        $"{stat.DeviceMajor}:{stat.DeviceMinor}:{stat.Inode}"

    let private identity maximumBytes deadline path =
        if not (OperatingSystem.IsLinux()) then
            Error "preparation-file-identity-unavailable"
        elif DateTimeOffset.UtcNow >= deadline then
            Error "preparation-deadline-refused"
        else
            statAt -100 path 0x100
            |> Result.bind (fun stat ->
                match stat.Mode &&& 0xf000us with
                | 0xa000us -> Error "preparation-symlink-refused"
                | 0x4000us -> Ok(nodeIdentity stat)
                | 0x8000us ->
                    let descriptor = CapsuleNative.openRead (path, 0xa0800) // CLOEXEC | NOFOLLOW | NONBLOCK

                    if descriptor < 0 then
                        raise (IOException "bounded-input-open-refused")

                    use handle =
                        new Microsoft.Win32.SafeHandles.SafeFileHandle(nativeint descriptor, true)

                    use stream = new FileStream(handle, FileAccess.Read)
                    // Bind the held descriptor, then compare the current pathname to that same inode.
                    statAt (stream.SafeFileHandle.DangerousGetHandle().ToInt32()) "" 0x1000
                    |> Result.bind (fun held ->
                        if held.Mode &&& 0xf000us <> 0x8000us || nodeIdentity held <> nodeIdentity stat then
                            Error "preparation-input-invalidated"
                        elif uint64 stream.Length > maximumBytes then
                            Error "preparation-input-budget-refused"
                        else
                            use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                            let buffer = Array.zeroCreate<byte> 65536
                            let mutable total = 0UL
                            let mutable finished = false
                            let mutable refusal = None

                            while not finished && refusal.IsNone do
                                if DateTimeOffset.UtcNow >= deadline then
                                    refusal <- Some "preparation-deadline-refused"
                                else
                                    let count = stream.Read(buffer, 0, buffer.Length)

                                    if count = 0 then
                                        finished <- true
                                    elif uint64 count > maximumBytes - total then
                                        refusal <- Some "preparation-input-budget-refused"
                                    else
                                        total <- total + uint64 count
                                        hash.AppendData(buffer, 0, count)

                            match refusal with
                            | Some reason -> Error reason
                            | None ->
                                statAt -100 path 0x100
                                |> Result.bind (fun current ->
                                    if nodeIdentity current <> nodeIdentity held then
                                        Error "preparation-input-invalidated"
                                    else
                                        Ok(nodeIdentity held + ":" + Convert.ToHexString(hash.GetHashAndReset()))))
                | _ -> Error "preparation-special-file-refused")

    let private inventory (spec: CapsulePreparation) deadline =
        try
            let root = Path.GetFullPath spec.Root

            if not (Path.IsPathFullyQualified spec.Root) || not (Directory.Exists root) then
                Error "preparation-capsule-unavailable"
            elif
                spec.MaximumInputBytes = 0UL
                || spec.MaximumOutputBytes = 0UL
                || spec.MaximumOutputBytes > 1048576UL
                || spec.MaximumCheckSeconds < 1
                || spec.MaximumCheckSeconds > 60
                || spec.Inputs.IsEmpty
                || spec.Inputs.Length > 4096
            then
                Error "preparation-bounds-refused"
            else
                let rec collect (directory: string) (accumulated: string list) bytes =
                    let mutable all = accumulated
                    let mutable total = bytes

                    for path in Directory.EnumerateFileSystemEntries directory do
                        if all.Length >= 8192 then
                            invalidOp "input-count-bound"

                        match statAt -100 path 0x100 with
                        | Ok stat when stat.Mode &&& 0xf000us = 0x8000us || stat.Mode &&& 0xf000us = 0x4000us -> ()
                        | _ -> invalidOp "special-file-or-link-refused"

                        if
                            (File.GetAttributes path &&& FileAttributes.ReparsePoint)
                            <> enum<FileAttributes> 0
                        then
                            invalidOp "symlink-refused"

                        all <- path :: all

                        if Directory.Exists path then
                            let nested, size = collect path all total
                            all <- nested
                            total <- size
                        else
                            let length = uint64 (FileInfo(path).Length)

                            if length > spec.MaximumInputBytes - total then
                                invalidOp "input-byte-bound"

                            total <- total + length

                    all, total

                let paths, _ = collect root [] 0UL
                let entries = paths |> List.toArray |> Array.sort

                let files =
                    entries
                    |> Array.filter File.Exists
                    |> Array.map (fun p -> Path.GetRelativePath(root, p))
                    |> Set.ofArray

                if files <> Set.ofList spec.Inputs || files.Count <> spec.Inputs.Length then
                    let missing =
                        Set.difference (Set.ofList spec.Inputs) files |> Set.toList |> List.tryHead

                    match missing with
                    | Some name -> Error("preparation-input-missing:" + Path.GetFileName(name))
                    | None -> Error "preparation-input-closure-refused"
                elif
                    entries
                    |> Array.sumBy (fun p -> if File.Exists p then uint64 (FileInfo(p).Length) else 0UL)
                        >
                        spec.MaximumInputBytes
                then
                    Error "preparation-input-budget-refused"
                else
                    let executablePaths = "/usr/bin/setsid" :: (spec.Checks |> List.map _.Executable)

                    let ancestors =
                        let rec walk p =
                            if String.IsNullOrEmpty p then
                                []
                            else
                                p
                                :: (let parent = Path.GetDirectoryName p in if parent = p then [] else walk parent)

                        walk root

                    let paths = (ancestors @ Array.toList entries @ executablePaths) |> List.distinct

                    paths
                    |> List.fold
                        (fun state p ->
                            state
                            |> Result.bind (fun values ->
                                identity
                                    (if executablePaths |> List.contains p then
                                         67108864UL
                                     else
                                         spec.MaximumInputBytes)
                                    deadline
                                    p
                                |> Result.map (fun value -> (p, value) :: values)))
                        (Ok [])
        with _ ->
            Error "preparation-input-unavailable"

    let private specBinding (binding: string) (spec: CapsulePreparation) =
        use bytes = new MemoryStream()
        use writer = new BinaryWriter(bytes, Encoding.UTF8, true)
        writer.Write binding
        writer.Write spec.Root
        writer.Write spec.MaximumInputBytes
        writer.Write spec.MaximumOutputBytes
        writer.Write spec.MaximumCheckSeconds

        let strings (values: string list) =
            writer.Write values.Length
            values |> List.iter (fun value -> writer.Write value)

        strings spec.Inputs
        writer.Write spec.Environment.Count

        spec.Environment
        |> Map.iter (fun key value ->
            writer.Write key
            writer.Write value)

        writer.Write spec.Checks.Length

        for check in spec.Checks do
            writer.Write check.Id
            writer.Write(string check.Kind)
            writer.Write check.Executable
            strings check.Arguments
            strings check.ExpectedDiscoveries

        writer.Flush()
        digest (bytes.ToArray())

    let private readBounded (stream: Stream) maximum (token: CancellationToken) =
        task {
            use output = new MemoryStream()
            let buffer = Array.zeroCreate<byte> 4096
            let mutable ended = false
            let mutable exceeded = false

            while not ended && not exceeded do
                let! count = stream.ReadAsync(buffer.AsMemory(), token)

                if count = 0 then
                    ended <- true
                elif uint64 output.Length + uint64 count > maximum then
                    exceeded <- true
                else
                    output.Write(buffer, 0, count)

            return output.ToArray(), exceeded
        }

    let private checkOutput (check: CapsuleCheck) bytes =
        try
            use document = JsonDocument.Parse(bytes: byte array)
            let root = document.RootElement
            let fields = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList

            let found =
                root.GetProperty("discovered").EnumerateArray()
                |> Seq.map _.GetString()
                |> Seq.toList

            if
                fields.Length <> 3
                || Set.ofList fields <> set [ "schema"; "checkId"; "discovered" ]
                || root.GetProperty("schema").GetString() <> "fsgg.capsule-observation/1"
                || root.GetProperty("checkId").GetString() <> check.Id
                || found <> check.ExpectedDiscoveries
            then
                Error "preparation-discovery-refused"
            else
                Ok()
        with _ ->
            Error "preparation-observation-malformed"

    type private OwnedProbeIdentity =
        {
            Pid: int
            Parent: int
            Group: int
            Session: int
            Start: string
        }

    let private processIdentity pid =
        try
            use stream = File.OpenRead($"/proc/{pid}/stat")
            let buffer = Array.zeroCreate<byte> 8193
            let count = stream.Read(buffer, 0, buffer.Length)

            if count = buffer.Length || stream.ReadByte() <> -1 then
                Error "process-identity-incomplete"
            else
                let text = Encoding.UTF8.GetString(buffer, 0, count)
                let fields = text.Substring(text.LastIndexOf(')') + 2).Split(' ')

                if fields.Length < 20 then
                    Error "process-identity-malformed"
                else
                    Ok
                        {
                            Pid = pid
                            Parent = int fields[1]
                            Group = int fields[2]
                            Session = int fields[3]
                            Start = fields[19]
                        }
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Error "process-ended"
        | _ -> Error "process-identity-unavailable"

    let private cleanupProbe (root: OwnedProbeIdentity) =
        // The known live root anchors ancestry. A recycled/unjoined group never receives a signal.
        try
            let deadline = DateTimeOffset.UtcNow.AddSeconds 2.
            let mutable count = 0
            let mutable complete = true
            let mutable all = []

            for directory in Directory.EnumerateDirectories("/proc") do
                let mutable pid = 0

                if Int32.TryParse(Path.GetFileName directory, &pid) then
                    count <- count + 1

                    if count > 32768 || DateTimeOffset.UtcNow >= deadline then
                        complete <- false
                    elif complete then
                        match processIdentity pid with
                        | Ok observed -> all <- observed :: all
                        | Error "process-ended" -> ()
                        | Error _ -> complete <- false

            let currentRoot =
                all |> List.tryFind (fun p -> p.Pid = root.Pid && p.Start = root.Start)

            let candidates =
                all
                |> List.filter (fun p -> p.Group = root.Pid || p.Session = root.Pid || p.Pid = root.Pid)

            if not complete then
                false
            elif candidates.IsEmpty then
                true
            elif
                currentRoot
                |> Option.exists (fun p -> p.Group = root.Pid && p.Session = root.Pid)
                |> not
            then
                false
            else
                let rec descendants owned =
                    let next =
                        all
                        |> List.filter (fun p -> Set.contains p.Parent owned)
                        |> List.map _.Pid
                        |> Set.ofList
                        |> Set.union owned

                    if next = owned then owned else descendants next

                let owned = descendants (Set.singleton root.Pid)
                let members = all |> List.filter (fun p -> Set.contains p.Pid owned)
                // Escaped session/group members are unsupported; retain unknown cleanup.
                if
                    members |> List.exists (fun p -> p.Group <> root.Pid || p.Session <> root.Pid)
                    || candidates |> List.exists (fun p -> not (Set.contains p.Pid owned))
                then
                    false
                else
                    let mutable signalled = true
                    // Open pidfds and re-observe identity before each signal. Pidfds do not follow PID reuse.
                    for memberIdentity in members |> List.sortBy (fun p -> if p.Pid = root.Pid then 1 else 0) do
                        let descriptor = CapsuleNative.pidfdOpen (memberIdentity.Pid, 0u)

                        if descriptor < 0 then
                            if Marshal.GetLastPInvokeError() <> 3 then
                                signalled <- false
                        else
                            use handle =
                                new Microsoft.Win32.SafeHandles.SafeFileHandle(nativeint descriptor, true)

                            match processIdentity memberIdentity.Pid with
                            | Ok current when current = memberIdentity ->
                                if
                                    CapsuleNative.pidfdSignal (descriptor, 9, 0n, 0u) <> 0
                                    && Marshal.GetLastPInvokeError() <> 3
                                then
                                    signalled <- false
                            | Error "process-ended" -> ()
                            | _ -> signalled <- false

                    signalled
        with _ ->
            false

    let private runCheck
        (spec: CapsulePreparation)
        deadline
        (check: CapsuleCheck)
        (ledger: PreparationObservationState ref)
        (token: CancellationToken)
        =
        task {
            let scratch =
                Path.Combine(Path.GetTempPath(), "fsgg-preparation-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory scratch |> ignore
            use child = new Process()
            let mutable started = false
            let mutable ownedIdentity = ""
            let mutable ownedProcess = None

            let! outcome =
                task {
                    try
                        // A separate owned process group makes descendants cleanable after parent exit.
                        let info = ProcessStartInfo("/usr/bin/setsid")
                        info.WorkingDirectory <- spec.Root
                        info.UseShellExecute <- false
                        info.RedirectStandardOutput <- true
                        info.RedirectStandardError <- true
                        info.Environment.Clear()
                        spec.Environment |> Map.iter (fun k v -> info.Environment[k] <- v)
                        info.Environment["TMPDIR"] <- scratch
                        info.Environment["PYTHONDONTWRITEBYTECODE"] <- "1"
                        info.ArgumentList.Add check.Executable
                        check.Arguments |> List.iter info.ArgumentList.Add
                        child.StartInfo <- info

                        let milliseconds =
                            min
                                (float spec.MaximumCheckSeconds * 1000.)
                                (deadline - DateTimeOffset.UtcNow).TotalMilliseconds

                        if milliseconds <= 0. || token.IsCancellationRequested then
                            return Error "preparation-deadline-refused"
                        else
                            use budget = CancellationTokenSource.CreateLinkedTokenSource token
                            budget.CancelAfter(TimeSpan.FromMilliseconds milliseconds)
                            started <- child.Start()

                            if not started then
                                return Error "preparation-tool-unavailable"
                            else
                                let probeIdentity = processIdentity child.Id |> Result.defaultWith invalidOp
                                ownedProcess <- Some probeIdentity
                                let identity = $"{child.Id}:{probeIdentity.Start}:{check.Id}"
                                ownedIdentity <- identity

                                let limits =
                                    {
                                        MaximumDistinct = spec.Checks.Length
                                        MaximumEvents = spec.Checks.Length * 2
                                        MaximumBytes = int64 spec.MaximumOutputBytes
                                        Deadline = deadline.UtcTicks
                                    }

                                let admitted =
                                    PreparationObservation.observe
                                        limits
                                        DateTimeOffset.UtcNow.UtcTicks
                                        identity
                                        0L
                                        ledger.Value

                                ledger.Value <- admitted.State

                                if admitted.Refusal.IsSome then
                                    invalidOp "observation-admission-refused"

                                let stdout =
                                    readBounded child.StandardOutput.BaseStream spec.MaximumOutputBytes budget.Token

                                let stderr =
                                    readBounded child.StandardError.BaseStream spec.MaximumOutputBytes budget.Token

                                let exit = child.WaitForExitAsync(budget.Token)
                                let! first = Task.WhenAny(stdout :> Task, stderr :> Task, exit)

                                if
                                    first = (stdout :> Task) && (snd stdout.Result)
                                    || first = (stderr :> Task) && (snd stderr.Result)
                                then
                                    return Error "preparation-output-budget-refused"
                                else
                                    let! outBytes, outExceeded = stdout
                                    let! errBytes, errExceeded = stderr
                                    do! exit

                                    if
                                        outExceeded
                                        || errExceeded
                                        || uint64 (outBytes.Length + errBytes.Length) > spec.MaximumOutputBytes
                                    then
                                        return Error "preparation-output-budget-refused"
                                    elif child.ExitCode <> 0 then
                                        let diagnostic = Encoding.UTF8.GetString(errBytes)

                                        let missing =
                                            System.Text.RegularExpressions.Regex.Match(
                                                diagnostic,
                                                "ModuleNotFoundError: No module named '([A-Za-z0-9_.-]{1,128})'"
                                            )

                                        if missing.Success then
                                            return Error("preparation-import-missing:" + missing.Groups[1].Value)
                                        else
                                            return Error("preparation-check-failed:" + check.Id)
                                    elif errBytes.Length <> 0 then
                                        return Error "preparation-observation-malformed"
                                    else
                                        let observed =
                                            PreparationObservation.observe
                                                limits
                                                DateTimeOffset.UtcNow.UtcTicks
                                                identity
                                                (int64 (outBytes.Length + errBytes.Length))
                                                ledger.Value

                                        ledger.Value <- observed.State

                                        if observed.Refusal.IsSome then
                                            return Error "preparation-observation-budget-refused"
                                        else
                                            return checkOutput check outBytes
                    with
                    | :? OperationCanceledException -> return Error "preparation-check-timeout"
                    | _ -> return Error "preparation-tool-unavailable"
                }

            let mutable cleaned = true

            if started then
                match ownedProcess with
                | None -> cleaned <- false
                | Some identity ->
                    cleaned <- cleanupProbe identity

                    if cleaned && not child.HasExited && not (child.WaitForExit(2000)) then
                        cleaned <- false
                    // Surviving members or detached identities remain unknown; never signal by group number.
                    if cleaned && CapsuleNative.kill (-child.Id, 0) = 0 then
                        cleaned <- false

            try
                Directory.Delete(scratch, true)
            with _ ->
                cleaned <- false

            if started then
                let settlement = PreparationObservation.cleanup ownedIdentity cleaned ledger.Value
                ledger.Value <- settlement.State

                if settlement.Refusal.IsSome then
                    cleaned <- false

            if not cleaned then
                return Error "preparation-cleanup-unobserved"
            else
                return outcome
        }

    let private closureIdentity identities =
        identities
        |> List.map (fun (path, value) -> path + "\000" + value)
        |> String.concat "\n"
        |> Encoding.UTF8.GetBytes
        |> digest

    /// Performs bounded actual import/discovery without launching the product workload.
    let prepareAsync binding deadline (spec: CapsulePreparation) cancellationToken =
        task {
            let kinds = spec.Checks |> List.map _.Kind |> Set.ofList

            if
                spec.Checks.Length > 32
                || kinds <> set [ CapsuleCheckKind.Import; CapsuleCheckKind.Discovery ]
                || spec.Checks
                   |> List.exists (fun c ->
                       String.IsNullOrWhiteSpace c.Id
                       || not (Path.IsPathFullyQualified c.Executable)
                       || c.ExpectedDiscoveries.IsEmpty
                       || c.ExpectedDiscoveries |> List.exists String.IsNullOrWhiteSpace)
                || (spec.Checks |> List.map _.Id |> Set.ofList |> Set.count) <> spec.Checks.Length
            then
                return Error "preparation-required-check-unavailable"
            else
                match inventory spec deadline with
                | Error reason -> return Error reason
                | Ok before ->
                    let mutable result = Ok()
                    let ledger = ref PreparationObservation.initial

                    let mutable artifact =
                        PreparedArtifact.initial (closureIdentity before) (spec.Checks |> List.map _.Id |> Set.ofList)

                    for check in spec.Checks do
                        if Result.isOk result then
                            let! observed = runCheck spec deadline check ledger cancellationToken
                            result <- observed

                            if Result.isOk observed then
                                match PreparedArtifact.observe check.Id artifact with
                                | Ok next -> artifact <- next
                                | Error reason -> result <- Error reason

                    match result, inventory spec deadline with
                    | Error reason, _ -> return Error reason
                    | _, Error reason -> return Error reason
                    | Ok(), Ok after when before <> after -> return Error "preparation-input-invalidated"
                    | Ok(), Ok after ->
                        return Ok(PreparedAttempt.Create(specBinding binding spec, deadline, after, Some artifact))
        }

    /// Consume only against the same inputs/command/configuration and a current deadline.
    let validate now binding (spec: CapsulePreparation) (attempt: PreparedAttempt) =
        if now >= attempt.Deadline then
            Error "preparation-deadline-refused"
        elif specBinding binding spec <> attempt.Binding then
            Error "preparation-binding-invalidated"
        else
            inventory spec attempt.Deadline
            |> Result.bind (fun current ->
                match attempt.Artifact with
                | None -> Error "preparation-check-unknown"
                | Some artifact ->
                    let _, _, refusal = PreparedArtifact.consume (closureIdentity current) artifact

                    match refusal with
                    | Some reason -> Error reason
                    | None -> Ok())
