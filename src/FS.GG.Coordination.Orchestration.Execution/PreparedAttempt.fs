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

/// Explicit owner declarations; absence never establishes independence.
type CapsuleCheckDependencies =
    {
        CheckId: string
        DependsOn: string list
        SharedStateScopes: string list
    }

[<RequireQualifiedAccess>]
type PreparationCheckOutcome =
    | Passed
    | Failed
    | Blocked
    | Unknown
    | NotRunBound

/// Bounded per-check observations. Exit, cleanup and reporting are distinct from the first cause.
type PreparationCheckFinding =
    {
        CheckId: string
        Stage: string
        Dependencies: string list
        SharedStateScopes: string list
        Required: bool
        Outcome: PreparationCheckOutcome
        Cause: string option
        EvidenceReferences: string list
        ExitCode: int option
        CleanupObserved: bool option
        ReportingFailure: string option
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
        artifact: PreparedArtifactState option,
        dependenciesBinding: string option
    ) =
    member internal _.Binding = binding
    member internal _.Deadline = deadline
    member internal _.Identities = identities
    member internal _.Artifact = artifact
    member internal _.DependenciesBinding = dependenciesBinding

    static member internal Create(binding, deadline, identities, artifact) =
        PreparedAttempt(binding, deadline, identities, artifact, None)

    static member internal CreateDetailed(binding, deadline, identities, artifact, dependenciesBinding) =
        PreparedAttempt(binding, deadline, identities, artifact, Some dependenciesBinding)

/// A report is data; only its assembly-created prepared value can admit consumption.
type PreparationReport =
    {
        CandidateBinding: string
        ClosureIdentity: string option
        DependenciesBinding: string
        Prepared: PreparedAttempt option
        FirstFailure: string option
        Findings: PreparationCheckFinding list
        AdditionalFailures: string list
        OmittedChecks: int
        Truncated: bool
    }

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
            if DateTimeOffset.UtcNow >= deadline then Error "preparation-deadline-refused"
            else Error "preparation-input-unavailable"

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
            | Error reason -> return Error reason, None, Some true, None
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
                let mutable observedExit = None
                let mutable reportingFailure = None
                let remainingOutput = spec.MaximumOutputBytes - uint64 ledger.Value.Bytes
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
                                                    remainingOutput
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
                                                        remainingOutput
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

                                                    observedExit <- Some child.ExitCode
                                                    // Failed checks consume the same cumulative observation budget as successes.
                                                    let observed =
                                                        PreparationObservation.observe
                                                            limits
                                                            DateTimeOffset.UtcNow.UtcTicks
                                                            identity
                                                            (int64 (outBytes.Length + errBytes.Length))
                                                            ledger.Value

                                                    ledger.Value <- observed.State
                                                    let checkerResult =
                                                        if child.ExitCode <> 0 then
                                                            let diagnostic = Encoding.UTF8.GetString errBytes
                                                            let missing =
                                                                System.Text.RegularExpressions.Regex.Match(
                                                                    diagnostic,
                                                                    "ModuleNotFoundError: No module named '([A-Za-z0-9_.-]{1,128})'"
                                                                )
                                                            if missing.Success then
                                                                Error("preparation-import-missing:" + missing.Groups[1].Value)
                                                            else
                                                                Error("preparation-check-failed:" + check.Id)
                                                        elif errBytes.Length <> 0 then
                                                            Error "preparation-observation-malformed"
                                                        else
                                                            checkOutput check outBytes

                                                    let boundFailure =
                                                        if outExceeded || errExceeded
                                                           || uint64 (outBytes.Length + errBytes.Length) > remainingOutput then
                                                            Some "preparation-output-budget-refused"
                                                        elif observed.Refusal.IsSome then
                                                            Some "preparation-observation-budget-refused"
                                                        else None

                                                    reportingFailure <- boundFailure
                                                    match checkerResult, boundFailure with
                                                    | Error reason, _ ->
                                                        custody <- { custody with Refused = true }
                                                        return Error reason
                                                    | Ok (), Some reason ->
                                                        custody <- { custody with Refused = true }
                                                        return Error reason
                                                    | Ok (), None ->
                                                        transition "result" identity
                                                        return Ok ()
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

                if cleaned && started && observedExit.IsNone then
                    try
                        if child.HasExited then observedExit <- Some child.ExitCode
                    with _ -> ()

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

                let finalOutcome =
                    if not cleaned then
                        match outcome with
                        | Error reason -> Error reason
                        | Ok () -> Error "preparation-cleanup-unobserved"
                    elif Result.isOk outcome && not (PreparationCustody.ready custody) then
                        Error "preparation-custody-order-refused"
                    else outcome

                if Result.isOk finalOutcome then
                    transition "admit" ownedIdentity

                return finalOutcome, observedExit, Some cleaned, reportingFailure
        }

    let private closureIdentity identities =
        identities
        |> List.map (fun (path, value) -> path + "\000" + value)
        |> String.concat "\n"
        |> Encoding.UTF8.GetBytes
        |> digest

    let private dependenciesBinding (declarations: CapsuleCheckDependencies list) =
        use bytes = new MemoryStream()
        use writer = new BinaryWriter(bytes, Encoding.UTF8, true)
        for declaration in declarations do
            writer.Write declaration.CheckId
            writer.Write declaration.DependsOn.Length
            for dependency in declaration.DependsOn do writer.Write dependency
            writer.Write declaration.SharedStateScopes.Length
            for scope in declaration.SharedStateScopes do writer.Write scope
        writer.Flush()
        digest (bytes.ToArray())

    let private validChecks (spec: CapsulePreparation) =
        let kinds = spec.Checks |> List.map _.Kind |> Set.ofList
        let boundedStrings maximum values =
            List.length values <= maximum
            && (values |> List.forall (fun (value: string) -> not (isNull value) && value.Length <= 4096))
        not (isNull spec.Root) && spec.Root.Length <= 4096
        && spec.Checks.Length <= 32
        && boundedStrings 4096 spec.Inputs
        && spec.Environment.Count <= 128
        && (spec.Environment |> Map.toList |> List.forall (fun (key, value) ->
            not (isNull key) && not (isNull value) && key.Length <= 4096 && value.Length <= 4096))
        && kinds = set [ CapsuleCheckKind.Import; CapsuleCheckKind.Discovery ]
        && not (spec.Checks |> List.exists (fun c ->
            String.IsNullOrWhiteSpace c.Id || c.Id.Length > 128
            || isNull c.Executable || c.Executable.Length > 4096
            || not (Path.IsPathFullyQualified c.Executable)
            || not (boundedStrings 128 c.Arguments)
            || not (boundedStrings 4096 c.ExpectedDiscoveries)
            || c.ExpectedDiscoveries.IsEmpty
            || c.ExpectedDiscoveries |> List.exists String.IsNullOrWhiteSpace))
        && (spec.Checks |> List.map _.Id |> Set.ofList |> Set.count) = spec.Checks.Length

    let private validCandidate (binding: string) (spec: CapsulePreparation) =
        // Bound every serialized string before encoding, then cap their cumulative UTF-8 population.
        not (isNull binding) && binding.Length <= 4096
        && validChecks spec
        && (seq {
                yield binding
                yield spec.Root
                yield! spec.Inputs
                for KeyValue(key, value) in spec.Environment do
                    yield key
                    yield value
                for check in spec.Checks do
                    yield check.Id
                    yield check.Executable
                    yield! check.Arguments
                    yield! check.ExpectedDiscoveries
            }
            |> Seq.sumBy (fun value -> int64 (Encoding.UTF8.GetByteCount value))) <= 1048576L

    let private validDeclarations (spec: CapsulePreparation) (declarations: CapsuleCheckDependencies list) =
        let ids = spec.Checks |> List.map _.Id |> Set.ofList
        let mutable prior = Set.empty
        let mutable valid = List.length declarations <= 32
        for declaration in declarations do
            valid <- valid
                     && ids.Contains declaration.CheckId
                     && not (prior.Contains declaration.CheckId)
                     && declaration.DependsOn.Length <= 32
                     && declaration.SharedStateScopes.Length <= 32
                     && (declaration.DependsOn @ declaration.SharedStateScopes
                         |> List.forall (fun value -> not (String.IsNullOrWhiteSpace value) && value.Length <= 128))
                     && (declaration.DependsOn |> Set.ofList |> Set.count) = declaration.DependsOn.Length
                     && (declaration.SharedStateScopes |> Set.ofList |> Set.count) = declaration.SharedStateScopes.Length
            prior <- prior.Add declaration.CheckId
        // Unknown references are retained as blocked outcomes; cycles likewise never execute.
        valid

    let private hardStop (reason: string) =
        not (reason.StartsWith("preparation-check-failed:", StringComparison.Ordinal)
             || reason.StartsWith("preparation-import-missing:", StringComparison.Ordinal)
             || reason.StartsWith("preparation-discovery-", StringComparison.Ordinal))

    /// Runs the existing actual checks once, collecting only explicitly independent observations.
    /// Declarations select continuation, never readiness; every original check remains required.
    let prepareDetailedAsync binding deadline (spec: CapsulePreparation) (declarations: CapsuleCheckDependencies list) (cancellationToken: CancellationToken) =
        task {
            // Immutable F# records/lists/maps are snapped before any checker effects.
            let declarations = declarations |> List.map (fun d -> { d with DependsOn = List.ofSeq d.DependsOn; SharedStateScopes = List.ofSeq d.SharedStateScopes })
            let mutable firstFailure: string option = None
            let mutable additional: string list = []
            let mutable findings: PreparationCheckFinding list = []
            let mutable omitted = 0
            let mutable truncated = false
            let remember reason =
                match firstFailure with
                | None -> firstFailure <- Some reason
                | Some first when first <> reason && not (List.contains reason additional) ->
                    if additional.Length < 32 then additional <- additional @ [reason]
                    else truncated <- true
                | _ -> ()
            let add finding =
                if findings.Length < 32 then findings <- findings @ [finding]
                else omitted <- omitted + 1; truncated <- true
            let finding id stage (declaration: CapsuleCheckDependencies option) outcome cause exit cleanup reporting =
                { CheckId = id; Stage = stage
                  Dependencies = declaration |> Option.map _.DependsOn |> Option.defaultValue []
                  SharedStateScopes = declaration |> Option.map _.SharedStateScopes |> Option.defaultValue []
                  Required = true; Outcome = outcome; Cause = cause
                  EvidenceReferences = ["capsule-check:" + id]
                  ExitCode = exit; CleanupObserved = cleanup; ReportingFailure = reporting }
            let valid = validCandidate binding spec && validDeclarations spec declarations
            // Invalid input never reaches the potentially unbounded binding serializer.
            let candidate = if valid then specBinding binding spec else "unavailable"
            let selection = if valid then dependenciesBinding declarations else "unavailable"
            let mutable prepared = None
            let mutable closure = None
            if not valid then
                omitted <- spec.Checks.Length
                truncated <- omitted > 0
                remember "preparation-required-check-unavailable"
            else
                match inventory spec deadline with
                | Error reason ->
                    remember reason
                    // Structural missing inputs are independently observable without running any probe.
                    if reason.StartsWith("preparation-input-missing:", StringComparison.Ordinal) then
                        for input in spec.Inputs do
                            if DateTimeOffset.UtcNow >= deadline then truncated <- true
                            else
                                // Malformed relative declarations never authorize reads outside the capsule.
                                let missingInput =
                                    try
                                        let root = Path.GetFullPath(spec.Root).TrimEnd(Path.DirectorySeparatorChar)
                                        let path = Path.GetFullPath(Path.Combine(root, input))
                                        not (path.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.Ordinal))
                                        || not (File.Exists path)
                                    with _ -> true
                                if missingInput then
                                    let missing = "preparation-input-missing:" + Path.GetFileName input
                                    remember missing
                                    add (finding (Path.GetFileName input) "closure" None PreparationCheckOutcome.Failed (Some missing) None None None)
                    for check in spec.Checks do
                        let declaration = declarations |> List.tryFind (fun d -> d.CheckId = check.Id)
                        add (finding check.Id "check" declaration (if reason.Contains("deadline") || reason.Contains("budget") then PreparationCheckOutcome.NotRunBound else PreparationCheckOutcome.Blocked) (Some reason) None None None)
                | Ok before ->
                    closure <- Some(closureIdentity before)
                    let ledger = ref PreparationObservation.initial
                    let mutable artifact = PreparedArtifact.initial (closureIdentity before) (spec.Checks |> List.map _.Id |> Set.ofList)
                    let mutable observed = Map.empty<string, PreparationCheckOutcome>
                    let mutable invalidScopes = Set.empty<string>
                    let mutable stopped = None
                    for check in spec.Checks do
                        let declaration = declarations |> List.tryFind (fun d -> d.CheckId = check.Id)
                        let blocked =
                            match declaration with
                            | None when firstFailure.IsSome -> Some "preparation-independence-unknown"
                            | None -> None
                            | Some d when d.DependsOn |> List.exists (fun id -> Map.tryFind id observed <> Some PreparationCheckOutcome.Passed) ->
                                Some "preparation-dependency-not-passed"
                            | Some d when d.SharedStateScopes |> List.exists invalidScopes.Contains ->
                                Some "preparation-shared-state-invalidated"
                            | Some _ -> None
                        if stopped.IsSome || DateTimeOffset.UtcNow >= deadline || cancellationToken.IsCancellationRequested then
                            let reason = stopped |> Option.defaultValue "preparation-deadline-refused"
                            remember reason
                            let outcome = if reason.Contains("budget") || reason.Contains("deadline") || reason.Contains("timeout") then PreparationCheckOutcome.NotRunBound else PreparationCheckOutcome.Blocked
                            add (finding check.Id "check" declaration outcome (Some reason) None None None)
                            observed <- observed.Add(check.Id, outcome)
                        elif blocked.IsSome then
                            remember blocked.Value
                            add (finding check.Id "check" declaration PreparationCheckOutcome.Blocked blocked None None None)
                            observed <- observed.Add(check.Id, PreparationCheckOutcome.Blocked)
                        else
                            let! result, exit, cleanup, reporting = runCheck spec deadline check ledger cancellationToken
                            let outcome, cause =
                                match result with
                                | Ok () -> PreparationCheckOutcome.Passed, None
                                | Error reason ->
                                    remember reason
                                    (if hardStop reason then PreparationCheckOutcome.Unknown else PreparationCheckOutcome.Failed), Some reason
                            add (finding check.Id "check" declaration outcome cause exit cleanup reporting)
                            observed <- observed.Add(check.Id, outcome)
                            match reporting with Some reason -> remember reason; stopped <- Some reason | None -> ()
                            if cleanup <> Some true then
                                remember "preparation-cleanup-unobserved"
                                stopped <- Some "preparation-cleanup-unobserved"
                            match cause with
                            | Some reason ->
                                if hardStop reason then stopped <- Some reason
                                match declaration with
                                | None -> stopped <- Some "preparation-independence-unknown"
                                | Some d -> invalidScopes <- Set.union invalidScopes (Set.ofList d.SharedStateScopes)
                            | None ->
                                match PreparedArtifact.observe check.Id artifact with
                                | Ok next -> artifact <- next
                                | Error reason -> remember reason; stopped <- Some reason
                            // A failed checker may have modified inputs. Never use old closure validity to continue.
                            match inventory spec deadline with
                            | Error reason -> remember reason; stopped <- Some reason
                            | Ok current when current <> before ->
                                remember "preparation-input-invalidated"
                                stopped <- Some "preparation-input-invalidated"
                            | Ok _ -> ()
                    if firstFailure.IsNone then
                        match inventory spec deadline with
                        | Error reason -> remember reason
                        | Ok after when after <> before -> remember "preparation-input-invalidated"
                        | Ok after -> prepared <- Some(PreparedAttempt.CreateDetailed(candidate, deadline, after, Some artifact, selection))
            return
                { CandidateBinding = candidate; ClosureIdentity = closure; DependenciesBinding = selection; Prepared = prepared
                  FirstFailure = firstFailure; Findings = findings; AdditionalFailures = additional
                  OmittedChecks = omitted; Truncated = truncated }
        }

    /// Legacy strict projection shares the same implementation and conservative continuation policy.
    let prepareAsync binding deadline (spec: CapsulePreparation) cancellationToken =
        task {
            let! report = prepareDetailedAsync binding deadline spec [] cancellationToken
            match report.Prepared, report.FirstFailure with
            | Some prepared, None -> return Ok prepared
            | _, Some reason -> return Error reason
            | _ -> return Error "preparation-check-unknown"
        }

    /// Consume only against the same inputs/command/configuration and a current deadline.
    let validate now binding (spec: CapsulePreparation) (attempt: PreparedAttempt) =
        if now >= attempt.Deadline then
            Error "preparation-deadline-refused"
        elif not (validCandidate binding spec) || specBinding binding spec <> attempt.Binding then
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

    /// Consume a detailed preparation only with the same frozen continuation declarations.
    let validateDetailed now binding spec declarations (attempt: PreparedAttempt) =
        if not (validCandidate binding spec) || not (validDeclarations spec declarations)
           || attempt.DependenciesBinding <> Some(dependenciesBinding declarations) then
            Error "preparation-dependencies-invalidated"
        else validate now binding spec attempt
