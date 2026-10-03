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

    [<DllImport("libc", SetLastError = true, EntryPoint = "open")>]
    extern int openRead(string path, int flags)

/// Pure custody decisions are replayable observations; they cannot construct PreparedAttempt.
type PreparationCustodyState =
    {
        Identity: string
        Bound: bool
        Permitted: bool
        Filtered: bool
        ResultObserved: bool
        LeaderExited: bool
        GroupLive: bool
        GroupTerminated: bool
        CleanupObserved: bool
        Pending: bool
        Refused: bool
        Admitted: bool
    }

type PreparationCustodyDecision =
    {
        State: PreparationCustodyState
        Effects: string list
        Refusal: string option
    }

[<RequireQualifiedAccess>]
module PreparationCustody =
    let initial =
        {
            Identity = ""
            Bound = false
            Permitted = false
            Filtered = false
            ResultObserved = false
            LeaderExited = false
            GroupLive = false
            GroupTerminated = false
            CleanupObserved = false
            Pending = false
            Refused = false
            Admitted = false
        }

    let ready s =
        s.Bound
        && s.Permitted
        && s.Filtered
        && s.ResultObserved
        && s.GroupTerminated
        && not s.GroupLive
        && s.CleanupObserved
        && not s.Pending
        && not s.Refused

    let step event identity s =
        let accept state effects =
            {
                State = state
                Effects = effects
                Refusal = None
            }

        let refuse reason =
            {
                State =
                    { s with
                        Refused = true
                        Admitted = false
                    }
                Effects = []
                Refusal = Some reason
            }

        match event with
        | "bind" when not s.Bound && not (String.IsNullOrWhiteSpace identity) ->
            accept
                { s with
                    Identity = identity
                    Bound = true
                    GroupLive = true
                    Pending = true
                }
                [ "bind-owned-group" ]
        | "permit" when s.Bound && not s.Permitted && not s.Refused ->
            accept { s with Permitted = true } [ "ack-bootstrap" ]
        | "filtered" when s.Bound && s.Permitted && not s.Filtered && not s.Refused ->
            accept { s with Filtered = true } [ "observe-fixed-filter" ]
        | "result" when s.Filtered && not s.ResultObserved && not s.Refused ->
            accept { s with ResultObserved = true } [ "observe-check" ]
        | "leaderExit" when s.Bound && not s.GroupTerminated ->
            accept { s with LeaderExited = true } [ "observe-leader-exit" ]
        | "terminated" when s.Bound && not s.GroupTerminated ->
            accept
                { s with
                    GroupTerminated = true
                    GroupLive = false
                }
                [ "observe-group-termination" ]
        | "timeout" when s.Bound ->
            accept
                { s with
                    Refused = true
                    Admitted = false
                }
                [ "signal-owned-group"; "await-group-termination" ]
        | "cleanup" when s.GroupTerminated && s.Pending ->
            accept
                { s with
                    CleanupObserved = true
                    Pending = false
                }
                [ "remove-scratch"; "observe-cleanup" ]
        | "admit" when ready s && not s.Admitted -> accept { s with Admitted = true } [ "admit-prepared-check" ]
        | "unavailable" -> refuse "preparation-custody-unavailable"
        | _ -> refuse "preparation-custody-order-refused"

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
                            | None when DateTimeOffset.UtcNow >= deadline -> Error "preparation-deadline-refused"
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
                        if DateTimeOffset.UtcNow >= deadline then
                            invalidOp "input-deadline-bound"

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
                    let executablePaths = spec.Checks |> List.map _.Executable

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
        writer.Write CustodyBootstrap.ProfileId
        writer.Write CustodyBootstrap.BootstrapSha256
        writer.Write CustodyBootstrap.FilterSha256
        writer.Write CustodyBootstrap.SourceSha256
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

    let private readCustodyLine (stream: Stream) (token: CancellationToken) =
        task {
            use bytes = new MemoryStream()
            let one = Array.zeroCreate<byte> 1
            let mutable ended = false

            while not ended do
                let! count = stream.ReadAsync(one.AsMemory(), token)

                if count <> 1 || bytes.Length >= 128L then
                    invalidOp "custody-handshake-refused"
                elif one[0] = 10uy then
                    ended <- true
                else
                    bytes.WriteByte one[0]

            return Encoding.ASCII.GetString(bytes.ToArray())
        }

    let private runCheck
        (spec: CapsulePreparation)
        deadline
        (check: CapsuleCheck)
        (ledger: PreparationObservationState ref)
        (token: CancellationToken)
        =
        task {
            match CustodyBootstrap.tryOpen () with
            | Error reason -> return Error reason
            | Ok resources ->
                use resources = resources

                let scratch =
                    Path.Combine(Path.GetTempPath(), "fsgg-preparation-" + Guid.NewGuid().ToString("N"))

                Directory.CreateDirectory scratch |> ignore
                use child = new Process()
                let mutable started = false
                let mutable ownedIdentity = ""
                let mutable ownedGroup: CustodyProcessLease option = None
                let mutable outputTasks: Task list = []
                let mutable checkerPermitted = false
                let mutable custody = PreparationCustody.initial

                let transition event identity =
                    let decision = PreparationCustody.step event identity custody
                    custody <- decision.State

                    if decision.Refusal.IsSome then
                        invalidOp decision.Refusal.Value

                let! outcome =
                    task {
                        try
                            let milliseconds =
                                min
                                    (float spec.MaximumCheckSeconds * 1000.)
                                    (deadline - DateTimeOffset.UtcNow).TotalMilliseconds

                            if milliseconds <= 0. || token.IsCancellationRequested then
                                return Error "preparation-deadline-refused"
                            else
                                use budget = CancellationTokenSource.CreateLinkedTokenSource token
                                budget.CancelAfter(TimeSpan.FromMilliseconds milliseconds)
                                let nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes 16)

                                let environment =
                                    spec.Environment
                                    |> Map.add "TMPDIR" scratch
                                    |> Map.add "PYTHONDONTWRITEBYTECODE" "1"
                                    |> Map.toList
                                    |> List.map (fun (k, v) -> k + "=" + v)

                                let arguments = check.Executable :: check.Arguments

                                if
                                    environment.Length > 128
                                    || arguments.Length > 128
                                    || (environment @ arguments
                                        |> List.sumBy (fun value -> Encoding.UTF8.GetByteCount(value) + 1))
                                        >
                                        65536
                                then
                                    return Error "preparation-custody-arguments-unavailable"
                                else
                                    let info = ProcessStartInfo(resources.ExecutablePath)
                                    info.WorkingDirectory <- spec.Root
                                    info.UseShellExecute <- false
                                    info.RedirectStandardInput <- true
                                    info.RedirectStandardOutput <- true
                                    info.RedirectStandardError <- true
                                    // No owner/caller loader or startup hooks execute in the trusted bootstrap.
                                    info.Environment.Clear()
                                    info.Environment["LANG"] <- "C"

                                    for value in
                                        [
                                            "--bootstrap"
                                            nonce
                                            string (min 60000 (max 1 (int milliseconds)))
                                            string environment.Length
                                            string arguments.Length
                                        ]
                                        @ environment
                                        @ arguments do
                                        info.ArgumentList.Add value

                                    child.StartInfo <- info

                                    if not (resources.Revalidate()) then
                                        return Error "preparation-custody-resource-unavailable"
                                    else
                                        started <- child.Start()

                                        if not started then
                                            return Error "preparation-custody-bootstrap-unavailable"
                                        else
                                            let stderr =
                                                readBounded
                                                    child.StandardError.BaseStream
                                                    spec.MaximumOutputBytes
                                                    budget.Token

                                            outputTasks <- [ stderr :> Task ]
                                            let! hello = readCustodyLine child.StandardOutput.BaseStream budget.Token

                                            if hello <> $"FSGG-CUSTODY/1 {child.Id} {nonce}" then
                                                invalidOp "custody-handshake-refused"

                                            let bootstrapIdentity =
                                                processIdentity child.Id |> Result.defaultWith invalidOp

                                            if bootstrapIdentity.Parent <> Environment.ProcessId then
                                                invalidOp "custody-direct-child-refused"

                                            match CustodyProcessLease.BindHeld child.Id with
                                            | Error reason -> return Error reason
                                            | Ok group ->
                                                ownedGroup <- Some group
                                                let rejoined = processIdentity child.Id |> Result.defaultWith invalidOp

                                                if
                                                    rejoined <> bootstrapIdentity
                                                    || group.Status <> CustodyProcessStatus.Live
                                                    || not (resources.Revalidate())
                                                then
                                                    invalidOp "custody-held-child-refused"

                                                let identity =
                                                    $"{child.Id}:{bootstrapIdentity.Start}:{check.Id}:{CustodyBootstrap.ProfileId}"

                                                ownedIdentity <- identity
                                                transition "bind" identity

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

                                                budget.Token.ThrowIfCancellationRequested()

                                                if DateTimeOffset.UtcNow >= deadline then
                                                    invalidOp "custody-deadline-refused"

                                                transition "permit" identity
                                                // Held bootstrap cannot execute checker code before this exact owned ACK.
                                                do!
                                                    child.StandardInput.BaseStream.WriteAsync(
                                                        ReadOnlyMemory<byte>([| 65uy |]),
                                                        budget.Token
                                                    )

                                                do! child.StandardInput.BaseStream.FlushAsync(budget.Token)
                                                child.StandardInput.Close()
                                                checkerPermitted <- true

                                                let! filtered =
                                                    readCustodyLine child.StandardOutput.BaseStream budget.Token

                                                if filtered <> $"FSGG-FILTERED/1 {nonce}" then
                                                    invalidOp "custody-filter-refused"

                                                transition "filtered" identity

                                                let stdout =
                                                    readBounded
                                                        child.StandardOutput.BaseStream
                                                        spec.MaximumOutputBytes
                                                        budget.Token

                                                outputTasks <- [ stdout :> Task; stderr :> Task ]
                                                let exit = child.WaitForExitAsync(budget.Token)
                                                let mutable outputExceeded = false

                                                while group.Status <> CustodyProcessStatus.Terminated
                                                      && not outputExceeded do
                                                    budget.Token.ThrowIfCancellationRequested()

                                                    if group.Status = CustodyProcessStatus.Unknown then
                                                        invalidOp "custody-group-unknown"

                                                    if not custody.LeaderExited then
                                                        try
                                                            let stat = File.ReadAllText($"/proc/{child.Id}/stat")

                                                            if
                                                                stat
                                                                    .Substring(stat.LastIndexOf(')') + 2)
                                                                    .StartsWith("Z ")
                                                                && group.Status = CustodyProcessStatus.Live
                                                            then
                                                                transition "leaderExit" identity
                                                        with
                                                        | :? FileNotFoundException
                                                        | :? DirectoryNotFoundException -> ()

                                                    if stdout.IsFaulted || stdout.IsCanceled then
                                                        let! _ = stdout in ()

                                                    if stderr.IsFaulted || stderr.IsCanceled then
                                                        let! _ = stderr in ()

                                                    outputExceeded <-
                                                        (stdout.IsCompletedSuccessfully && snd stdout.Result)
                                                        || (stderr.IsCompletedSuccessfully && snd stderr.Result)

                                                    if not outputExceeded then
                                                        do! Task.Delay(10, budget.Token)

                                                if outputExceeded then
                                                    custody <- { custody with Refused = true }
                                                    return Error "preparation-output-budget-refused"
                                                else
                                                    transition "terminated" identity
                                                    let! outBytes, outExceeded = stdout
                                                    let! errBytes, errExceeded = stderr
                                                    do! exit

                                                    if
                                                        outExceeded
                                                        || errExceeded
                                                        || uint64 (outBytes.Length + errBytes.Length) >
                                                            spec.MaximumOutputBytes
                                                    then
                                                        return Error "preparation-output-budget-refused"
                                                    elif not group.HasTerminated then
                                                        return Error "preparation-cleanup-unobserved"
                                                    elif child.ExitCode <> 0 then
                                                        let diagnostic = Encoding.UTF8.GetString errBytes

                                                        let missing =
                                                            System.Text.RegularExpressions.Regex.Match(
                                                                diagnostic,
                                                                "ModuleNotFoundError: No module named '([A-Za-z0-9_.-]{1,128})'"
                                                            )

                                                        if missing.Success then
                                                            return
                                                                Error(
                                                                    "preparation-import-missing:"
                                                                    + missing.Groups[1].Value
                                                                )
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
                                                            let result = checkOutput check outBytes

                                                            if Result.isOk result then
                                                                transition "result" identity
                                                            else
                                                                custody <- { custody with Refused = true }

                                                            return result
                        with
                        | :? OperationCanceledException ->
                            if custody.Bound then
                                transition "timeout" ownedIdentity
                            else
                                custody <- { custody with Refused = true }

                            return Error "preparation-check-timeout"
                        | _ ->
                            custody <- { custody with Refused = true }

                            return
                                Error(
                                    if checkerPermitted then
                                        "preparation-custody-check-unavailable"
                                    else
                                        "preparation-custody-setup-unavailable"
                                )
                    }
                // Cleanup has a separate bounded grace and uses only the already held process pidfd.
                let mutable cleaned = not started

                if started then
                    try
                        child.StandardInput.Close()
                    with _ ->
                        ()

                    match ownedGroup with
                    | None ->
                        // Trusted pre-ACK bootstrap sees EOF and exits; absence of group custody is unknown.
                        try
                            child.WaitForExit(2000) |> ignore
                        with _ ->
                            ()

                        cleaned <- false
                    | Some group ->
                        if not group.HasTerminated then
                            group.Terminate() |> ignore

                        let! terminated = group.WaitTerminatedAsync 2000
                        cleaned <- terminated

                        if terminated && custody.Bound && not custody.GroupTerminated then
                            transition "terminated" ownedIdentity

                        if terminated then
                            try
                                cleaned <- child.WaitForExit(2000)
                            with _ ->
                                cleaned <- false
                    // Reader faults/cancellation are settled observations, never abandoned background work.
                    for output in outputTasks do
                        if not output.IsCompleted then
                            let! settled = Task.WhenAny(output, Task.Delay 2000)

                            if settled <> output then
                                cleaned <- false

                        if output.IsFaulted then
                            output.Exception |> ignore

                try
                    Directory.Delete(scratch, true)
                with _ ->
                    cleaned <- false

                if cleaned && custody.Bound then
                    transition "cleanup" ownedIdentity

                if not (String.IsNullOrEmpty ownedIdentity) then
                    let settlement = PreparationObservation.cleanup ownedIdentity cleaned ledger.Value
                    ledger.Value <- settlement.State

                    if settlement.Refusal.IsSome then
                        cleaned <- false

                ownedGroup |> Option.iter (fun group -> (group :> IDisposable).Dispose())

                if not cleaned then
                    return Error "preparation-cleanup-unobserved"
                elif Result.isOk outcome && not (PreparationCustody.ready custody) then
                    return Error "preparation-custody-order-refused"
                else
                    if Result.isOk outcome then
                        transition "admit" ownedIdentity

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
                | _ when DateTimeOffset.UtcNow >= attempt.Deadline -> Error "preparation-deadline-refused"
                | None -> Error "preparation-check-unknown"
                | Some artifact ->
                    let _, _, refusal = PreparedArtifact.consume (closureIdentity current) artifact

                    match refusal with
                    | Some reason -> Error reason
                    | None -> Ok())
