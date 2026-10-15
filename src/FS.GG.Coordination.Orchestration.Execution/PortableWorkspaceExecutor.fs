namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

type PortableReviewedOperation =
    {
        EntryPoint: string
        OperationIdentity: string
        ComponentId: string option
        WorkingDirectory: string
        QualifiedImage: string
        RequiredToolchains: (string * string) list
        Executable: string
        Arguments: string list
        VerificationIdentity: string
        VerificationPath: string
        VerificationSha256: string
        RecipeSha256: string
    }

type PortableRuntimePolicy =
    {
        GitExecutable: string
        TarExecutable: string
        PodmanExecutable: string
        PodmanGlobalArguments: string list
        StateRoot: string
        ContainerPath: string
        ContainerUser: string
        HostEnvironment: Map<string, string>
        ContainerEnvironment: Map<string, string>
        MaximumSnapshotBytes: uint64
        TerminationGrace: TimeSpan
    }

type PortableExecutorPolicy =
    {
        WorkspaceRoot: string
        WorkspaceScope: string
        SourceRevision: string
        QualifiedImage: string
        MaximumRuntimeSeconds: uint64
        MaximumOutputBytes: uint64
        Operations: PortableReviewedOperation list
        Runtime: PortableRuntimePolicy
    }

type PortableProcessRequest =
    {
        Executable: string
        Arguments: string list
        WorkingDirectory: string
        Deadline: DateTimeOffset
        MaximumOutputBytes: uint64
        WorkspaceScope: string
        SourceRepository: string
        SourceRevision: string
        QualifiedImage: string
        ContainerName: string
        ContainerPath: string
        ContainerEnvironment: Map<string, string>
        VerificationPath: string
        VerificationSha256: string
        RecipeSha256: string
    }

[<RequireQualifiedAccess>]
type PortableProcessRefusalStage =
    | SourceSnapshot
    | RuntimeVersion
    | ImageInspection
    | ImageIdentity
    | OutputOwnership
    | ContainerCreation
    | ContainerInspection

type PortableProcessRefusal =
    {
        Stage: PortableProcessRefusalStage
        Reason: string
        HelperTimedOut: bool
        HelperOutputComplete: bool
        HelperReadStatus: string
        HelperReadFailure: string option
    }

type PortableProcessObservation =
    {
        ExecutionStarted: bool
        ExitCode: int option
        StandardOutput: byte array
        StandardError: byte array
        CancellationRequested: bool
        TerminationObserved: bool
        Interrupted: bool
        OutputLimitExceeded: bool
        OutputComplete: bool
        SourceTree: string option
        SnapshotSha256: string option
        RuntimeIdentity: string option
        ContainerIdentity: string option
        VerificationObserved: bool
        VerificationOutput: byte array option
        VerificationCustodyLimitExceeded: bool
        Refusal: PortableProcessRefusal option
    }

type IPortableProcessRunner =
    abstract RunAsync:
        request: PortableProcessRequest * cancellationToken: CancellationToken -> Task<PortableProcessObservation>

    abstract RecoverAsync:
        request: PortableProcessRequest * cancellationToken: CancellationToken -> Task<PortableProcessObservation>

    abstract CleanupAsync: request: PortableProcessRequest * cancellationToken: CancellationToken -> Task<bool>

type PortableExecutionReceipt =
    {
        Result: PortableWorkspaceResult
        WorkspaceScope: string
        SourceRevision: string
        QualifiedImage: string
        OperationIdentity: string
        EntryPoint: string
        WorkingDirectory: string
        VerificationIdentity: string
        VerificationSha256: string
        RecipeSha256: string
        SourceTree: string option
        SnapshotSha256: string option
        RuntimeIdentity: string option
        ContainerIdentity: string option
        VerificationOutput: byte array option
        OutputSha256: string
        OutputBytes: uint64
        ExecutionStarted: bool
        CleanupCompleted: bool
        CancellationRequested: bool
        TerminationObserved: bool
    }

type PortableExecutionOutcome =
    | Completed of PortableExecutionReceipt
    | Duplicate of PortableExecutionReceipt
    | PendingDuplicate of commandId: Guid
    | Refused of reason: string

type PortableReconciliationObservation =
    {
        CommandId: Guid
        SourceRevision: string
        QualifiedImage: string
        VerificationIdentity: string
        ObservedAt: DateTimeOffset
        ExitCode: int option
        TerminationObserved: bool
    }

/// Host mode is explicitly selected by an authenticated adapter; these values do not authenticate evidence.
type PortableHostExecutionState =
    {
        Reservation: PortableHostReservation
        Lifecycle: PortableHostLifecycle
        Receipt: PortableExecutionReceipt option
    }

type private PortableHostJournalEntry =
    { BindingDigest: string; HostContainerName: string; State: PortableHostExecutionState }

type private PortableJournalEntry =
    | PortableRunning of commandId: Guid * bindingDigest: string * containerName: string
    | PortableSettled of bindingDigest: string * receipt: PortableExecutionReceipt
    | PortableHost of PortableHostJournalEntry

type private PortableLaunchDisposition =
    | UsePortableOutcome of PortableExecutionOutcome
    | LaunchPortableOperation

[<RequireQualifiedAccess>]
module PortableWorkspaceExecutor =
    let private sha256 (bytes: byte array) =
        bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private validSha256 (value: string) =
        value.Length = 64
        && value
           |> Seq.forall (fun character ->
               (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))

    let private absolutePath (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && Path.IsPathFullyQualified value
        && not (value.Split(Path.DirectorySeparatorChar) |> Array.contains "..")

    let private relativeOutputPath (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && not (Path.IsPathRooted value)
        && not (value.Contains '\\')
        && value.Split('/')
           |> Array.forall (fun segment -> segment <> "" && segment <> "." && segment <> "..")

    let private runtimePolicyValid (runtime: PortableRuntimePolicy) =
        let allowedEnvironment =
            set
                [
                    "HOME"
                    "PATH"
                    "LANG"
                    "LC_ALL"
                    "CONTAINERS_CONF"
                    "CONTAINERS_STORAGE_CONF"
                    "PYTHONDONTWRITEBYTECODE"
                    "PYTHONPYCACHEPREFIX"
                ]

        let hostKeys = runtime.HostEnvironment |> Map.toSeq |> Seq.map fst |> Set.ofSeq

        let containerKeys =
            runtime.ContainerEnvironment |> Map.toSeq |> Seq.map fst |> Set.ofSeq

        let containerPathValid =
            runtime.ContainerPath.Split(':') |> Array.forall absolutePath

        let containerUserValid =
            match runtime.ContainerUser.Split(':') with
            | [| user; group |] ->
                user <> "0"
                && group <> "0"
                && user |> Seq.forall Char.IsAsciiDigit
                && group |> Seq.forall Char.IsAsciiDigit
            | _ -> false

        let podmanArgumentsValid =
            match runtime.PodmanGlobalArguments with
            | [] -> true
            | [ "--storage-driver=vfs"; "--root"; root; "--runroot"; runRoot ] ->
                absolutePath root && absolutePath runRoot && root <> runRoot
            | _ -> false

        absolutePath runtime.GitExecutable
        && absolutePath runtime.TarExecutable
        && absolutePath runtime.PodmanExecutable
        && absolutePath runtime.StateRoot
        && containerPathValid
        && containerUserValid
        && podmanArgumentsValid
        && runtime.MaximumSnapshotBytes > 0UL
        && runtime.TerminationGrace > TimeSpan.Zero
        && runtime.TerminationGrace <= TimeSpan.FromSeconds 30.0
        && runtime.ContainerEnvironment.ContainsKey "HOME"
        && runtime.ContainerEnvironment.ContainsKey "PATH"
        && runtime.ContainerEnvironment["PATH"] = runtime.ContainerPath
        && runtime.HostEnvironment.ContainsKey "HOME"
        && runtime.HostEnvironment.ContainsKey "PATH"
        && Set.isSubset hostKeys allowedEnvironment
        && Set.isSubset containerKeys allowedEnvironment

    let private bindingDigest
        (profile: PortableWorkspaceProfile)
        (command: PortableWorkspaceCommand)
        (operation: PortableReviewedOperation)
        =
        let profileDigest =
            PortableWorkspaceContract.profileBytes profile
            |> Result.map sha256
            |> Result.defaultWith invalidOp

        let commandDigest =
            PortableWorkspaceContract.commandBytes command
            |> Result.map sha256
            |> Result.defaultWith invalidOp

        String.concat "\n" [ profileDigest; commandDigest; operation.RecipeSha256 ]
        |> Encoding.UTF8.GetBytes
        |> sha256

    let private outputDigest (stdout: byte array) (stderr: byte array) =
        Array.concat [ stdout; [| byte '\n' |]; stderr ] |> sha256

    let private writeOptionalString (writer: BinaryWriter) (value: string option) =
        match value with
        | Some text ->
            writer.Write true
            writer.Write text
        | None -> writer.Write false

    let private readOptionalString (reader: BinaryReader) =
        if reader.ReadBoolean() then
            Some(reader.ReadString())
        else
            None

    let private writeOptionalBytes (writer: BinaryWriter) (value: byte array option) =
        match value with
        | Some bytes ->
            writer.Write bytes.Length
            writer.Write bytes
        | None -> writer.Write -1

    let private readOptionalBytes (reader: BinaryReader) =
        let length = reader.ReadInt32()

        if length < 0 then
            None
        elif length > PortableWorkspaceContract.maximumDocumentBytes * 4 then
            invalidOp "portable-journal-verification-size-refused"
        else
            let bytes = reader.ReadBytes length

            if bytes.Length <> length then
                invalidOp "portable-journal-verification-truncated"

            Some bytes

    let private writeReceipt (writer: BinaryWriter) (receipt: PortableExecutionReceipt) =
        let resultBytes =
            PortableWorkspaceContract.resultBytes receipt.Result
            |> Result.defaultWith (fun reason -> invalidOp reason)

        writer.Write(Convert.ToBase64String resultBytes)
        writer.Write receipt.WorkspaceScope
        writer.Write receipt.SourceRevision
        writer.Write receipt.QualifiedImage
        writer.Write receipt.OperationIdentity
        writer.Write receipt.EntryPoint
        writer.Write receipt.WorkingDirectory
        writer.Write receipt.VerificationIdentity
        writer.Write receipt.VerificationSha256
        writer.Write receipt.RecipeSha256
        writeOptionalString writer receipt.SourceTree
        writeOptionalString writer receipt.SnapshotSha256
        writeOptionalString writer receipt.RuntimeIdentity
        writeOptionalString writer receipt.ContainerIdentity
        writeOptionalBytes writer receipt.VerificationOutput
        writer.Write receipt.OutputSha256
        writer.Write receipt.OutputBytes
        writer.Write receipt.ExecutionStarted
        writer.Write receipt.CleanupCompleted
        writer.Write receipt.CancellationRequested
        writer.Write receipt.TerminationObserved

    let private readReceipt (reader: BinaryReader) =
        let result =
            reader.ReadString()
            |> Convert.FromBase64String
            |> PortableWorkspaceContract.parseResult
            |> Result.defaultWith (fun reason -> invalidOp reason)

        {
            Result = result
            WorkspaceScope = reader.ReadString()
            SourceRevision = reader.ReadString()
            QualifiedImage = reader.ReadString()
            OperationIdentity = reader.ReadString()
            EntryPoint = reader.ReadString()
            WorkingDirectory = reader.ReadString()
            VerificationIdentity = reader.ReadString()
            VerificationSha256 = reader.ReadString()
            RecipeSha256 = reader.ReadString()
            SourceTree = readOptionalString reader
            SnapshotSha256 = readOptionalString reader
            RuntimeIdentity = readOptionalString reader
            ContainerIdentity = readOptionalString reader
            VerificationOutput = readOptionalBytes reader
            OutputSha256 = reader.ReadString()
            OutputBytes = reader.ReadUInt64()
            ExecutionStarted = reader.ReadBoolean()
            CleanupCompleted = reader.ReadBoolean()
            CancellationRequested = reader.ReadBoolean()
            TerminationObserved = reader.ReadBoolean()
        }

    // Host records use a distinct schema under the same authoritative key and lock.
    let private writeHostBinding (writer: BinaryWriter) (binding: PortableHostBinding) =
        let command = PortableWorkspaceContract.commandBytes binding.Command |> Result.defaultWith invalidOp
        writer.Write(Convert.ToBase64String command)
        writer.Write binding.QualifiedImage
        writer.Write binding.AllocationId
        writer.Write binding.WorkerId
        writer.Write binding.AllocationReceiptDigest

    let private readHostBinding (reader: BinaryReader) : PortableHostBinding =
        { Command = reader.ReadString() |> Convert.FromBase64String |> PortableWorkspaceContract.parseCommand |> Result.defaultWith invalidOp
          QualifiedImage = reader.ReadString()
          AllocationId = reader.ReadString()
          WorkerId = reader.ReadString()
          AllocationReceiptDigest = reader.ReadString() }

    let private writeDate (writer: BinaryWriter) (value: DateTimeOffset) =
        writer.Write value.Ticks
        writer.Write value.Offset.Ticks

    let private readDate (reader: BinaryReader) =
        let ticks = reader.ReadInt64()
        let offset = reader.ReadInt64()
        DateTimeOffset(ticks, TimeSpan.FromTicks offset)

    let private writeManifest (writer: BinaryWriter) (value: PortableHostExportManifest) =
        writer.Write value.Digest
        writer.Write value.MemberCount
        writer.Write value.TotalBytes

    let private readManifest (reader: BinaryReader) : PortableHostExportManifest =
        { Digest = reader.ReadString(); MemberCount = reader.ReadUInt64(); TotalBytes = reader.ReadUInt64() }

    let private writeHostEvidence (writeValue: BinaryWriter -> 'value -> unit) (writer: BinaryWriter) (evidence: PortableEvidence<PortableHostObservation<'value>>) =
        match evidence with
        | EvidenceMissing reason -> writer.Write 0uy; writer.Write(reason: string)
        | EvidenceUnknown reason -> writer.Write 1uy; writer.Write(reason: string)
        | EvidenceKnown observation ->
            writer.Write 2uy
            writeHostBinding writer observation.Binding
            writeDate writer observation.CollectedAt
            writer.Write observation.ReceiptDigest
            writeValue writer observation.Value

    let private readHostEvidence (readValue: BinaryReader -> 'value) (reader: BinaryReader) =
        match reader.ReadByte() with
        | 0uy -> EvidenceMissing(reader.ReadString())
        | 1uy -> EvidenceUnknown(reader.ReadString())
        | 2uy ->
            let binding = readHostBinding reader
            let collectedAt = readDate reader
            let digest = reader.ReadString()
            let value = readValue reader
            EvidenceKnown { Binding = binding; CollectedAt = collectedAt; ReceiptDigest = digest; Value = value }
        | _ -> invalidOp "portable-host-evidence-schema-refused"

    let private writeHost (writer: BinaryWriter) (entry: PortableHostJournalEntry) =
        writer.Write entry.BindingDigest
        writer.Write entry.HostContainerName
        let reservation = entry.State.Reservation
        writeHostBinding writer reservation.Binding
        writeDate writer reservation.AdmittedAt
        writeDate writer reservation.HostLeaseExpiresAt
        writer.Write reservation.MaximumRuntime.Ticks
        writer.Write reservation.ExportReserve.Ticks
        writer.Write reservation.CleanupReserve.Ticks
        match reservation.Selection with
        | NoSelectedOutputs -> writer.Write 0uy
        | SelectedOutputs manifest -> writer.Write 1uy; writeManifest writer manifest
        let lifecycle = entry.State.Lifecycle
        writeHostEvidence (fun (w: BinaryWriter) (v: int) -> w.Write v) writer lifecycle.WorkloadExit
        writeHostEvidence (fun _ () -> ()) writer lifecycle.Termination
        writeHostEvidence (fun _ () -> ()) writer lifecycle.InnerCleanup
        writeHostEvidence (fun w value ->
            match value with
            | HostCensusConfirmsZeroSelection -> w.Write 0uy
            | DurableManifestReadback manifest -> w.Write 1uy; writeManifest w manifest) writer lifecycle.Export
        writeHostEvidence (fun _ () -> ()) writer lifecycle.OuterRetirement
        writeOptionalString writer lifecycle.FirstFailure
        if lifecycle.ReconciliationConflicts.Count > 1024 then invalidOp "portable-host-conflicts-size-refused"
        writer.Write lifecycle.ReconciliationConflicts.Count
        for conflict in lifecycle.ReconciliationConflicts do writer.Write conflict
        writer.Write entry.State.Receipt.IsSome
        entry.State.Receipt |> Option.iter (writeReceipt writer)

    let private readHost (reader: BinaryReader) =
        let digest = reader.ReadString()
        let containerName = reader.ReadString()
        let binding = readHostBinding reader
        let admittedAt = readDate reader
        let expiresAt = readDate reader
        let runtime = TimeSpan.FromTicks(reader.ReadInt64())
        let export = TimeSpan.FromTicks(reader.ReadInt64())
        let cleanup = TimeSpan.FromTicks(reader.ReadInt64())
        let selection =
            match reader.ReadByte() with
            | 0uy -> NoSelectedOutputs
            | 1uy -> SelectedOutputs(readManifest reader)
            | _ -> invalidOp "portable-host-selection-schema-refused"
        let reservation =
            { Binding = binding; AdmittedAt = admittedAt; HostLeaseExpiresAt = expiresAt
              MaximumRuntime = runtime; ExportReserve = export; CleanupReserve = cleanup; Selection = selection }
        let exit = readHostEvidence (fun r -> r.ReadInt32()) reader
        let termination = readHostEvidence (fun _ -> ()) reader
        let inner = readHostEvidence (fun _ -> ()) reader
        let exported = readHostEvidence (fun r ->
            match r.ReadByte() with
            | 0uy -> HostCensusConfirmsZeroSelection
            | 1uy -> DurableManifestReadback(readManifest r)
            | _ -> invalidOp "portable-host-export-schema-refused") reader
        let outer = readHostEvidence (fun _ -> ()) reader
        let failure = readOptionalString reader
        let count = reader.ReadInt32()
        if count < 0 || count > 1024 then invalidOp "portable-host-conflicts-size-refused"
        let conflicts = [ for _ in 1 .. count -> reader.ReadString() ] |> Set.ofList
        let receipt = if reader.ReadBoolean() then Some(readReceipt reader) else None
        { BindingDigest = digest; HostContainerName = containerName
          State = { Reservation = reservation; Receipt = receipt
                    Lifecycle = { WorkloadExit = exit; Termination = termination; InnerCleanup = inner
                                  Export = exported; OuterRetirement = outer; FirstFailure = failure
                                  ReconciliationConflicts = conflicts } } }

    type private DurableJournal(stateRoot: string) =
        let journalRoot = Path.Combine(stateRoot, "journal-v1")

        let ensureRoot () =
            Directory.CreateDirectory journalRoot |> ignore

            if File.GetAttributes(journalRoot) &&& FileAttributes.ReparsePoint = FileAttributes.ReparsePoint then
                invalidOp "portable-journal-root-refused"

            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(
                    journalRoot,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                )

        let keyPath workspaceScope idempotencyId =
            let key = Encoding.UTF8.GetBytes(workspaceScope + "\n" + idempotencyId) |> sha256
            Path.Combine(journalRoot, key + ".bin"), Path.Combine(journalRoot, key + ".lock")

        let read path =
            if not (File.Exists path) then
                None
            else
                let info = FileInfo path

                if
                    info.Length <= 0L
                    || info.Length > int64 PortableWorkspaceContract.maximumDocumentBytes * 4L
                then
                    invalidOp "portable-journal-size-refused"

                use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                use reader = new BinaryReader(stream, Encoding.UTF8, false)

                let schema = reader.ReadString()
                let entry =
                    match schema with
                    | "fsgg.portable-execution-host-journal/1" -> PortableHost(readHost reader)
                    | "fsgg.portable-execution-journal/1" ->
                      match reader.ReadByte() with
                      | 1uy ->
                          PortableRunning(
                              Guid.ParseExact(reader.ReadString(), "D"),
                              reader.ReadString(),
                              reader.ReadString()
                          )
                      | 2uy -> PortableSettled(reader.ReadString(), readReceipt reader)
                      | _ -> invalidOp "portable-journal-state-refused"
                    | _ -> invalidOp "portable-journal-schema-refused"

                if stream.Position <> stream.Length then
                    invalidOp "portable-journal-trailing-bytes-refused"

                Some entry

        let write path entry =
            let temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"

            try
                use stream =
                    new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                use writer = new BinaryWriter(stream, Encoding.UTF8, true)
                match entry with
                | PortableHost host ->
                    writer.Write "fsgg.portable-execution-host-journal/1"
                    writeHost writer host
                | _ ->
                  writer.Write "fsgg.portable-execution-journal/1"
                  match entry with
                  | PortableRunning(commandId, binding, containerName) ->
                      writer.Write 1uy
                      writer.Write(commandId.ToString("D"))
                      writer.Write binding
                      writer.Write containerName
                  | PortableSettled(binding, receipt) ->
                      writer.Write 2uy
                      writer.Write binding
                      writeReceipt writer receipt
                  | PortableHost _ -> invalidOp "portable-journal-state-refused"

                writer.Flush()
                stream.Flush true
                if stream.Length > int64 PortableWorkspaceContract.maximumDocumentBytes * 4L then
                    invalidOp "portable-journal-size-refused"
                File.Move(temporary, path, true)
            finally
                if File.Exists temporary then
                    File.Delete temporary

        let locked workspaceScope idempotencyId action =
            try
                ensureRoot ()
                let path, lockPath = keyPath workspaceScope idempotencyId

                use lockStream =
                    new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

                Ok(action path)
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? InvalidDataException
            | :? InvalidOperationException
            | :? FormatException
            | :? ArgumentException -> Error "portable-journal-unavailable"

        member _.Read(workspaceScope, idempotencyId) =
            locked workspaceScope idempotencyId read

        member _.Reserve(workspaceScope, idempotencyId, commandId, binding, containerName) =
            locked workspaceScope idempotencyId (fun path ->
                match read path with
                | Some existing -> Choice1Of2 existing
                | None ->
                    write path (PortableRunning(commandId, binding, containerName))
                    Choice2Of2())

        member _.ReserveHost(workspaceScope, idempotencyId, entry) =
            locked workspaceScope idempotencyId (fun path ->
                match read path with
                | Some existing -> Choice1Of2 existing
                | None -> write path (PortableHost entry); Choice2Of2())

        member _.ChangeHost(workspaceScope, idempotencyId, digest, change) =
            locked workspaceScope idempotencyId (fun path ->
                match read path with
                | Some(PortableHost entry) when entry.BindingDigest = digest ->
                    let updated, result = change entry
                    write path (PortableHost updated)
                    Some result
                | _ -> None)

        member _.Abandon(workspaceScope, idempotencyId, binding) =
            locked workspaceScope idempotencyId (fun path ->
                match read path with
                | Some(PortableRunning(_, existing, _)) when existing = binding -> File.Delete path; true
                | _ -> false)

        member _.Settle(workspaceScope, idempotencyId, binding, receipt) =
            locked workspaceScope idempotencyId (fun path ->
                match read path with
                | Some(PortableRunning(_, existing, _)) when existing = binding ->
                    write path (PortableSettled(binding, receipt))
                    true
                | _ -> false)

        member _.UpdateSettled(workspaceScope, idempotencyId, binding, receipt) =
            locked workspaceScope idempotencyId (fun path ->
                match read path with
                | Some(PortableSettled(existing, _)) when existing = binding ->
                    write path (PortableSettled(binding, receipt))
                    true
                | _ -> false)

    let private error code message retryable details =
        Some
            {
                Code = code
                Message = message
                Retryable = retryable
                Details = Map details
            }

    let private result now (prepared: PreparedWorkspaceOperation) exitCode artifactReference executionError =
        {
            CommandId = prepared.CommandId
            WorkflowRevision = prepared.WorkflowRevision
            FenceGeneration = prepared.FenceGeneration
            CompletedAt = now
            ExitCode = exitCode
            ArtifactReference = artifactReference
            Error = executionError
        }

    let private toolchainsMatch (profile: PortableWorkspaceProfile) (operation: PortableReviewedOperation) =
        match operation.ComponentId with
        | Some componentId ->
            profile.Components
            |> List.tryFind (fun part -> part.Id = componentId)
            |> Option.exists (fun part -> operation.RequiredToolchains = [ part.Toolchain.Id, part.Toolchain.Version ])
        | None ->
            let declared =
                profile.Components
                |> List.map (fun part -> part.Toolchain.Id, part.Toolchain.Version)
                |> Set.ofList

            operation.RequiredToolchains |> Set.ofList = declared

    let private runtimeDeadline
        (now: DateTimeOffset)
        (requestedDeadline: DateTimeOffset)
        (maximumRuntimeSeconds: uint64)
        =
        let availableTicks = DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks
        let maximumSafeSeconds = uint64 availableTicks / uint64 TimeSpan.TicksPerSecond

        let bounded =
            if maximumRuntimeSeconds >= maximumSafeSeconds then
                DateTimeOffset.MaxValue
            else
                now.AddTicks(int64 maximumRuntimeSeconds * TimeSpan.TicksPerSecond)

        min requestedDeadline bounded

    let private deadlineAfter (now: DateTimeOffset) (duration: TimeSpan) =
        let availableTicks = DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks

        if duration.Ticks >= availableTicks then
            DateTimeOffset.MaxValue
        else
            now.AddTicks duration.Ticks

    type Executor(policy: PortableExecutorPolicy, runner: IPortableProcessRunner, ?clock: unit -> DateTimeOffset,
                  ?prerequisites: Map<string, PortablePrerequisiteRequirement>,
                  ?hostReservation: PortableHostReservation,
                  ?hostEvidenceCurrent: unit -> bool) =
        let clock = defaultArg clock (fun () ->
            let now = DateTimeOffset.UtcNow
            DateTimeOffset(now.Ticks - now.Ticks % 10L, TimeSpan.Zero))
        let journal = DurableJournal policy.Runtime.StateRoot
        let prerequisites = defaultArg prerequisites Map.empty
        let evidenceCurrent () =
            try (defaultArg hostEvidenceCurrent (fun () -> true)) ()
            with _ -> false

        let attemptBinding profile command (operation: PortableReviewedOperation) =
            let contract = bindingDigest profile command operation
            // Bind the exact selected runner arguments and all execution configuration, not only recipe ID.
            System.Text.Json.JsonSerializer.Serialize({| contract = contract; operation = operation; policy = policy |})
            |> Encoding.UTF8.GetBytes |> sha256

        let prepareAttemptAsync profile command operation deadline cancellationToken =
            let binding = attemptBinding profile command operation
            match Map.tryFind operation.EntryPoint prerequisites with
            | Some PortablePrerequisiteRequirement.CapsuleUnavailable ->
                Task.FromResult(Error "preparation-required-check-unavailable")
            | Some(PortablePrerequisiteRequirement.CapsuleRequired spec) ->
                PreparedAttempt.prepareAsync binding deadline spec cancellationToken
            | None -> Task.FromResult(Ok(PreparedAttempt.prepareReviewedContract binding deadline))

        let consumeAttempt profile command operation attempt =
            let binding = attemptBinding profile command operation
            match Map.tryFind operation.EntryPoint prerequisites with
            | Some PortablePrerequisiteRequirement.CapsuleUnavailable -> Error "preparation-required-check-unavailable"
            | Some(PortablePrerequisiteRequirement.CapsuleRequired spec) -> PreparedAttempt.validate (clock ()) binding spec attempt
            | None -> PreparedAttempt.validateReviewedContract (clock ()) binding attempt

        let selectReviewedOperation (profile: PortableWorkspaceProfile) (command: PortableWorkspaceCommand) =
            PortableWorkspaceContract.validateProfile profile
            |> Result.bind (fun _ -> PortableWorkspaceContract.validateCommand command)
            |> Result.bind (fun _ ->
                if
                    command.WorkspaceScope <> profile.WorkspaceScope
                    || command.WorkspaceScope <> policy.WorkspaceScope
                then
                    Error "portable-workspace-scope-refused"
                elif
                    command.ProfileId <> profile.ProfileId
                    || command.ProfileRevision <> profile.Revision
                    || command.SourceRevision <> profile.SourceRevision
                    || command.SourceRevision <> policy.SourceRevision
                then
                    Error "portable-executor-source-binding-refused"
                elif profile.QualifiedImage <> policy.QualifiedImage then
                    Error "portable-executor-image-binding-refused"
                elif
                    profile.MaximumRuntimeSeconds > policy.MaximumRuntimeSeconds
                    || profile.MaximumOutputBytes > policy.MaximumOutputBytes
                then
                    Error "portable-executor-limit-refused"
                else
                    let selected =
                        match command.ComponentId with
                        | Some componentId ->
                            profile.Components
                            |> List.tryFind (fun part -> part.Id = componentId)
                            |> Option.bind (fun part ->
                                match command.Operation with
                                | "build" -> Some(part.WorkingDirectory, part.EntryPoints.Build)
                                | "test" -> Some(part.WorkingDirectory, part.EntryPoints.Test)
                                | "lint" ->
                                    part.EntryPoints.Lint |> Option.map (fun value -> part.WorkingDirectory, value)
                                | "artifact" ->
                                    part.EntryPoints.Artifact
                                    |> Option.map (fun value -> part.WorkingDirectory, value)
                                | _ -> None)
                        | None ->
                            match command.Operation with
                            | "build" -> Some("product", profile.ProductBuild)
                            | "test" -> Some("product", profile.ProductTest)
                            | "journey" -> Some("product", profile.ProductJourney)
                            | _ -> None

                    match selected with
                    | None -> Error "portable-executor-operation-refused"
                    | Some(workingDirectory, entryPoint) ->
                        policy.Operations
                        |> List.tryFind (fun operation ->
                            operation.EntryPoint = entryPoint
                            && operation.OperationIdentity = command.Operation
                            && operation.ComponentId = command.ComponentId
                            && operation.WorkingDirectory = workingDirectory)
                        |> function
                            | None -> Error "portable-executor-operation-refused"
                            | Some operation when operation.QualifiedImage <> profile.QualifiedImage ->
                                Error "portable-executor-image-binding-refused"
                            | Some operation when not (toolchainsMatch profile operation) ->
                                Error "portable-executor-toolchain-refused"
                            | Some operation when
                                String.IsNullOrWhiteSpace operation.VerificationIdentity
                                || not (absolutePath operation.Executable)
                                || not (relativeOutputPath operation.VerificationPath)
                                || not (validSha256 operation.VerificationSha256)
                                || not (validSha256 operation.RecipeSha256)
                                || not (runtimePolicyValid policy.Runtime)
                                ->
                                Error "portable-executor-verification-refused"
                            | Some operation -> Ok operation)

        let refuseBeforeLaunch
            (authority: PortableWorkspaceAuthority)
            (profile: PortableWorkspaceProfile)
            (command: PortableWorkspaceCommand)
            =
            selectReviewedOperation profile command
            |> Result.bind (fun operation ->
                PortableWorkspaceAdapter.prepare (clock ()) authority profile command
                |> Result.map (fun prepared -> prepared, operation, operation.WorkingDirectory))

        let receiptFromObservation
            (prepared: PreparedWorkspaceOperation)
            (operation: PortableReviewedOperation)
            (observed: PortableProcessObservation)
            =
            let completedAt = clock ()

            let outputBytes =
                uint64 observed.StandardOutput.LongLength
                + uint64 observed.StandardError.LongLength

            let refusalDetails =
                match observed.Refusal with
                | None -> [ "verification", operation.VerificationIdentity ]
                | Some refusal ->
                    let maximumDiagnosticBytes = 512
                    let diagnosticBytes = observed.StandardError |> Array.truncate maximumDiagnosticBytes
                    let diagnostic = Encoding.UTF8.GetString(diagnosticBytes).Trim()

                    let sourceOutput =
                        if refusal.Stage = PortableProcessRefusalStage.SourceSnapshot then
                            observed.StandardOutput
                            |> Array.truncate maximumDiagnosticBytes
                            |> Encoding.UTF8.GetString
                            |> _.Trim()
                        else
                            ""

                    [
                        "verification", operation.VerificationIdentity
                        "refusalStage", string refusal.Stage
                        "refusalReason", refusal.Reason
                        "helperExitCode", observed.ExitCode |> Option.map string |> Option.defaultValue "unavailable"
                        "helperTimedOut", string refusal.HelperTimedOut
                        "helperOutputComplete", string refusal.HelperOutputComplete
                        "helperOutputLimitExceeded", string observed.OutputLimitExceeded
                        "helperReadStatus", refusal.HelperReadStatus
                        "helperStderrSha256", sha256 observed.StandardError
                        "helperStderrTruncated", string (observed.StandardError.Length > maximumDiagnosticBytes)
                    ]
                    @ if String.IsNullOrWhiteSpace diagnostic then
                          []
                      else
                          [ "helperStderr", diagnostic ]
                    @ (refusal.HelperReadFailure
                       |> Option.map (fun value -> [ "helperReadFailure", value ])
                       |> Option.defaultValue [])
                    @ if String.IsNullOrWhiteSpace sourceOutput then
                          []
                      else
                          [
                              "helperStdout", sourceOutput
                              "helperStdoutSha256", sha256 observed.StandardOutput
                              "helperStdoutTruncated", string (observed.StandardOutput.Length > maximumDiagnosticBytes)
                          ]

            let workspaceResult =
                if not observed.ExecutionStarted then
                    result
                        completedAt
                        prepared
                        (EvidenceMissing "execution-not-started")
                        (EvidenceMissing "artifact-not-created")
                        (error
                            "execution-refused-before-start"
                            "The fixed operation was refused before execution started."
                            false
                            refusalDetails)
                elif observed.Interrupted || not observed.TerminationObserved then
                    result
                        completedAt
                        prepared
                        (EvidenceUnknown "process-termination-not-observed")
                        (EvidenceUnknown "artifact-state-requires-reconciliation")
                        (error
                            "execution-outcome-unknown"
                            "The fixed operation was interrupted before termination was observed."
                            true
                            [ "verification", operation.VerificationIdentity ])
                elif observed.VerificationCustodyLimitExceeded then
                    result
                        completedAt
                        prepared
                        (observed.ExitCode
                         |> Option.map EvidenceKnown
                         |> Option.defaultValue (EvidenceMissing "process-exit-code-unavailable"))
                        (EvidenceMissing "verification-output-exceeds-custody-limit")
                        (error
                            "execution-verification-custody-limit"
                            "The reviewed verification output exceeds the durable custody limit."
                            false
                            [ "verification", operation.VerificationIdentity ])
                elif
                    observed.OutputLimitExceeded
                    || not observed.OutputComplete
                    || outputBytes > prepared.MaximumOutputBytes
                then
                    result
                        completedAt
                        prepared
                        (EvidenceUnknown "output-limit-exceeded-before-complete-readback")
                        (EvidenceMissing "no-verified-artifact-observed")
                        (error
                            "execution-output-limit"
                            "The fixed operation exceeded its reviewed output limit."
                            false
                            [ "verification", operation.VerificationIdentity ])
                elif observed.CancellationRequested then
                    result
                        completedAt
                        prepared
                        (observed.ExitCode
                         |> Option.map EvidenceKnown
                         |> Option.defaultValue (EvidenceMissing "process-exit-code-unavailable"))
                        (EvidenceMissing "cancelled-before-verification")
                        (error
                            "execution-cancelled"
                            "Cancellation was requested and process termination was observed."
                            false
                            [ "verification", operation.VerificationIdentity ])
                else
                    match observed.ExitCode with
                    | Some 0 when observed.VerificationObserved ->
                        result
                            completedAt
                            prepared
                            (EvidenceKnown 0)
                            (EvidenceKnown operation.VerificationIdentity)
                            None
                    | Some 0 ->
                        result
                            completedAt
                            prepared
                            (EvidenceKnown 0)
                            (EvidenceMissing "reviewed-verification-not-observed")
                            (error
                                "execution-verification-missing"
                                "The operation exited successfully without its reviewed verification output."
                                false
                                [ "verification", operation.VerificationIdentity ])
                    | Some exitCode ->
                        result
                            completedAt
                            prepared
                            (EvidenceKnown exitCode)
                            (EvidenceMissing "operation-failed-before-verification")
                            (error
                                "execution-failed"
                                "The fixed operation returned a non-zero exit code."
                                false
                                [ "verification", operation.VerificationIdentity ])
                    | None ->
                        result
                            completedAt
                            prepared
                            (EvidenceUnknown "process-exit-code-unavailable")
                            (EvidenceUnknown "artifact-state-requires-reconciliation")
                            (error
                                "execution-outcome-unknown"
                                "The fixed operation outcome could not be established."
                                true
                                [ "verification", operation.VerificationIdentity ])

            {
                Result = workspaceResult
                WorkspaceScope = prepared.WorkspaceScope
                SourceRevision = prepared.SourceRevision
                QualifiedImage = prepared.QualifiedImage
                OperationIdentity = prepared.OperationIdentity
                EntryPoint = prepared.EntryPoint
                WorkingDirectory = prepared.WorkingDirectory
                VerificationIdentity = operation.VerificationIdentity
                VerificationSha256 = operation.VerificationSha256
                RecipeSha256 = operation.RecipeSha256
                SourceTree = observed.SourceTree
                SnapshotSha256 = observed.SnapshotSha256
                RuntimeIdentity = observed.RuntimeIdentity
                ContainerIdentity = observed.ContainerIdentity
                VerificationOutput = observed.VerificationOutput
                OutputSha256 = outputDigest observed.StandardOutput observed.StandardError
                OutputBytes = outputBytes
                ExecutionStarted = observed.ExecutionStarted
                CleanupCompleted = false
                CancellationRequested = observed.CancellationRequested
                TerminationObserved = observed.TerminationObserved
            }

        let preparedForRecovery
            (profile: PortableWorkspaceProfile)
            (command: PortableWorkspaceCommand)
            (operation: PortableReviewedOperation)
            =
            {
                CommandId = command.CommandId
                IdempotencyId = command.IdempotencyId
                WorkspaceScope = command.WorkspaceScope
                ProfileId = command.ProfileId
                ProfileRevision = command.ProfileRevision
                SourceRevision = command.SourceRevision
                QualifiedImage = profile.QualifiedImage
                WorkflowRevision = command.ExpectedWorkflowRevision
                FenceGeneration = command.FenceGeneration
                ComponentId = command.ComponentId
                OperationIdentity = command.Operation
                WorkingDirectory = operation.WorkingDirectory
                EntryPoint = operation.EntryPoint
                Deadline = command.Deadline
                MaximumRuntimeSeconds = profile.MaximumRuntimeSeconds
                MaximumOutputBytes = profile.MaximumOutputBytes
            }

        let requestFor
            (prepared: PreparedWorkspaceOperation)
            (operation: PortableReviewedOperation)
            containerName
            deadline
            =
            {
                Executable = operation.Executable
                Arguments = operation.Arguments
                WorkingDirectory = operation.WorkingDirectory
                Deadline = deadline
                MaximumOutputBytes = prepared.MaximumOutputBytes
                WorkspaceScope = prepared.WorkspaceScope
                SourceRepository = policy.WorkspaceRoot
                SourceRevision = prepared.SourceRevision
                QualifiedImage = prepared.QualifiedImage
                ContainerName = containerName
                ContainerPath = policy.Runtime.ContainerPath
                ContainerEnvironment = policy.Runtime.ContainerEnvironment
                VerificationPath = operation.VerificationPath
                VerificationSha256 = operation.VerificationSha256
                RecipeSha256 = operation.RecipeSha256
            }

        let cleanupReceipt
            (profile: PortableWorkspaceProfile)
            (command: PortableWorkspaceCommand)
            (operation: PortableReviewedOperation)
            (digest: string)
            (receipt: PortableExecutionReceipt)
            (cancellationToken: CancellationToken)
            =
            task {
                if receipt.CleanupCompleted then
                    return receipt
                else
                    let observedAt = clock ()
                    let prepared = preparedForRecovery profile command operation
                    let containerName = "fsgg-portable-" + digest[..23]

                    let request =
                        requestFor
                            prepared
                            operation
                            containerName
                            (deadlineAfter observedAt policy.Runtime.TerminationGrace)

                    let! cleaned =
                        task {
                            try
                                return! runner.CleanupAsync(request, cancellationToken)
                            with _ ->
                                return false
                        }

                    if not cleaned then
                        return receipt
                    else
                        let updated = { receipt with CleanupCompleted = true }

                        match journal.UpdateSettled(command.WorkspaceScope, command.IdempotencyId, digest, updated) with
                        | Ok true -> return updated
                        | _ -> return receipt
            }

        let hostDigest (profile: PortableWorkspaceProfile) (command: PortableWorkspaceCommand) (operation: PortableReviewedOperation) (reservation: PortableHostReservation) =
            use stream = new MemoryStream()
            use writer = new BinaryWriter(stream, Encoding.UTF8, true)
            writer.Write(bindingDigest profile command operation)
            writeHost writer
                { BindingDigest = ""; HostContainerName = ""
                  State = { Reservation = reservation; Lifecycle = PortableHostAllocation.emptyLifecycle; Receipt = None } }
            writer.Flush()
            sha256 (stream.ToArray())

        let selectHost (profile: PortableWorkspaceProfile) (command: PortableWorkspaceCommand) (reservation: PortableHostReservation) =
            selectReviewedOperation profile command
            |> Result.bind (fun operation ->
                if reservation.Binding.Command <> command
                   || reservation.Binding.QualifiedImage <> operation.QualifiedImage
                   || reservation.MaximumRuntime <= TimeSpan.Zero
                   || reservation.MaximumRuntime.TotalSeconds > float profile.MaximumRuntimeSeconds then
                    Error "portable-host-binding-refused"
                else Ok(operation, hostDigest profile command operation reservation))

        let readHostEntry (profile: PortableWorkspaceProfile) (command: PortableWorkspaceCommand) (reservation: PortableHostReservation) =
            selectHost profile command reservation
            |> Result.bind (fun (operation, digest) ->
                journal.Read(command.WorkspaceScope, command.IdempotencyId)
                |> Result.bind (function
                    | Some(PortableHost entry) when entry.BindingDigest = digest && entry.State.Reservation = reservation ->
                        Ok(operation, digest, Some entry)
                    | Some _ -> Error "portable-executor-idempotency-conflict"
                    | None -> Ok(operation, digest, None)))

        // Receipt cleanup is a projection of durable lifecycle evidence, independent of workload outcome
        // and outer retirement. Reconciliation can confirm a lost acknowledgement or expose a conflict.
        let projectHostReceipt now (state: PortableHostExecutionState) =
            let reservation = state.Reservation
            let lifecycle = state.Lifecycle
            let validObservation (observation: PortableHostObservation<'value>) =
                observation.Binding = reservation.Binding
                && not (String.IsNullOrWhiteSpace observation.ReceiptDigest)
                && observation.CollectedAt >= reservation.AdmittedAt
                && observation.CollectedAt <= now
            let cleanupCompleted =
                match lifecycle.Export, lifecycle.Termination, lifecycle.InnerCleanup with
                | EvidenceKnown exported, EvidenceKnown termination, EvidenceKnown inner
                    when lifecycle.ReconciliationConflicts.IsEmpty
                         && validObservation exported && validObservation termination && validObservation inner
                         && exported.CollectedAt < min reservation.HostLeaseExpiresAt reservation.Binding.Command.Deadline
                         && inner.CollectedAt >= exported.CollectedAt
                         && inner.CollectedAt >= termination.CollectedAt
                         && Result.isOk (PortableHostAllocation.executionDeadline reservation.AdmittedAt reservation reservation) ->
                    match reservation.Selection, exported.Value with
                    | SelectedOutputs expected, DurableManifestReadback actual -> expected = actual
                    | NoSelectedOutputs, HostCensusConfirmsZeroSelection -> true
                    | _ -> false
                | _ -> false
            { state with Receipt = state.Receipt |> Option.map (fun receipt -> { receipt with CleanupCompleted = cleanupCompleted }) }

        let cleanupHost (profile: PortableWorkspaceProfile) (command: PortableWorkspaceCommand) (operation: PortableReviewedOperation) digest (cancellationToken: CancellationToken) =
            task {
                // The same durable lock commits Unknown before control crosses into a destructive port.
                // A reentrant call sees Unknown; neither false nor an exception clears that original intent.
                let intent =
                    journal.ChangeHost(command.WorkspaceScope, command.IdempotencyId, digest, fun entry ->
                        let decision = PortableHostAllocation.retirement (clock ()) entry.State.Reservation entry.State.Lifecycle
                        // A lost workload settlement does not erase independent termination/export evidence.
                        // Receipt absence keeps the workload outcome pending; it cannot veto reducer-authorized cleanup.
                        if evidenceCurrent () && not cancellationToken.IsCancellationRequested
                           && decision.OutputDeletion = HostEligible then
                            let lifecycle = { entry.State.Lifecycle with InnerCleanup = EvidenceUnknown "inner cleanup intent persisted; completion not observed" }
                            let updated = { entry with State = { entry.State with Lifecycle = lifecycle } }
                            updated, Some updated
                        else entry, None)
                match intent with
                | Error reason -> return Refused reason
                | Ok None -> return Refused "portable-host-journal-conflict"
                | Ok(Some None) | Ok(Some(Some _)) ->
                    match intent with
                    | Ok(Some(Some entry)) ->
                        let reservation = entry.State.Reservation
                        let request = requestFor (preparedForRecovery profile command operation) operation entry.HostContainerName
                                          (min command.Deadline reservation.HostLeaseExpiresAt)
                        let! cleaned = task {
                            try
                                if evidenceCurrent () then return! runner.CleanupAsync(request, cancellationToken)
                                else return false
                            with _ -> return false
                        }
                        if not cleaned then
                            journal.ChangeHost(command.WorkspaceScope, command.IdempotencyId, digest, fun current ->
                                let lifecycle = { current.State.Lifecycle with
                                                    FirstFailure = current.State.Lifecycle.FirstFailure |> Option.orElse (Some "inner-cleanup-unconfirmed") }
                                { current with State = { current.State with Lifecycle = lifecycle } }, ()) |> ignore
                        else
                            // Inner callback success says nothing about independently observed outer retirement.
                            let observedAt = clock ()
                            let observed =
                                { Binding = reservation.Binding; CollectedAt = observedAt
                                  ReceiptDigest = "executor-inner-cleanup-" + digest; Value = () }
                            journal.ChangeHost(command.WorkspaceScope, command.IdempotencyId, digest, fun current ->
                                let incoming = { PortableHostAllocation.emptyLifecycle with InnerCleanup = EvidenceKnown observed }
                                let lifecycle = PortableHostAllocation.reconcileLifecycle current.State.Lifecycle incoming
                                let state = projectHostReceipt observedAt { current.State with Lifecycle = lifecycle }
                                { current with State = state }, ()) |> ignore
                    | _ -> ()
                    match journal.Read(command.WorkspaceScope, command.IdempotencyId) with
                    | Ok(Some(PortableHost entry)) when entry.BindingDigest = digest ->
                        return entry.State.Receipt |> Option.map Duplicate |> Option.defaultValue (PendingDuplicate command.CommandId)
                    | _ -> return Refused "portable-journal-unavailable"
            }

        let executeHost (authority: PortableWorkspaceAuthority) (profile: PortableWorkspaceProfile) (command: PortableWorkspaceCommand) (reservation: PortableHostReservation) (cancellationToken: CancellationToken) =
            task {
                match readHostEntry profile command reservation with
                | Error reason -> return Refused reason
                | Ok(operation, digest, Some _) ->
                    return! cleanupHost profile command operation digest cancellationToken
                | Ok(operation, digest, None) ->
                    match refuseBeforeLaunch authority profile command,
                          PortableHostAllocation.executionDeadline (clock ()) reservation reservation with
                    | Error reason, _ | _, Error reason -> return Refused reason
                    | Ok(prepared, _, _), Ok deadline ->
                        let! attempt = prepareAttemptAsync profile command operation deadline cancellationToken
                        match attempt with
                        | Error reason -> return Refused reason
                        | Ok attempt ->
                            match consumeAttempt profile command operation attempt,
                                  PortableHostAllocation.executionDeadline (clock ()) reservation reservation with
                            | Error reason, _ | _, Error reason -> return Refused reason
                            | Ok (), Ok originalDeadline when not cancellationToken.IsCancellationRequested ->
                                let containerName = "fsgg-portable-" + digest[..23]
                                let entry =
                                    { BindingDigest = digest; HostContainerName = containerName
                                      State = { Reservation = reservation; Lifecycle = PortableHostAllocation.emptyLifecycle; Receipt = None } }
                                match journal.ReserveHost(command.WorkspaceScope, command.IdempotencyId, entry) with
                                | Error reason -> return Refused reason
                                | Ok(Choice1Of2(PortableHost prior)) when prior.BindingDigest = digest ->
                                    return! cleanupHost profile command operation digest cancellationToken
                                | Ok(Choice1Of2 _) -> return Refused "portable-executor-idempotency-conflict"
                                | Ok(Choice2Of2()) ->
                                    let request = requestFor prepared operation containerName originalDeadline
                                    // Recheck the original window after durable reservation and before crossing RunAsync.
                                    match PortableHostAllocation.executionDeadline (clock ()) reservation reservation with
                                    | Error reason -> return Refused reason
                                    | Ok _ when cancellationToken.IsCancellationRequested -> return Refused "portable-executor-deadline-refused"
                                    | Ok _ when not (evidenceCurrent ()) -> return Refused "portable-host-supplier-evidence-expired"
                                    | Ok _ ->
                                        let! observation = task {
                                            try return! runner.RunAsync(request, cancellationToken)
                                            with _ ->
                                                return { ExecutionStarted = true; ExitCode = None; StandardOutput = Array.empty
                                                         StandardError = Array.empty; CancellationRequested = cancellationToken.IsCancellationRequested
                                                         TerminationObserved = false; Interrupted = true; OutputLimitExceeded = false
                                                         OutputComplete = false; SourceTree = None; SnapshotSha256 = None
                                                         RuntimeIdentity = None; ContainerIdentity = Some containerName
                                                         VerificationObserved = false; VerificationOutput = None
                                                         VerificationCustodyLimitExceeded = false; Refusal = None }
                                        }
                                        let receipt = receiptFromObservation prepared operation observation
                                        let observedAt = clock ()
                                        let observed value =
                                            { Binding = reservation.Binding; CollectedAt = observedAt
                                              ReceiptDigest = "executor-workload-" + digest; Value = value }
                                        let lifecycle =
                                            { PortableHostAllocation.emptyLifecycle with
                                                WorkloadExit = observation.ExitCode |> Option.map (observed >> EvidenceKnown) |> Option.defaultValue (EvidenceUnknown "workload exit unavailable")
                                                Termination = if observation.TerminationObserved then EvidenceKnown(observed ()) else EvidenceUnknown "termination not observed"
                                                FirstFailure = receipt.Result.Error |> Option.map (fun failure -> failure.Code) }
                                        match journal.ChangeHost(command.WorkspaceScope, command.IdempotencyId, digest, fun current ->
                                            let merged = PortableHostAllocation.reconcileLifecycle current.State.Lifecycle lifecycle
                                            let state = projectHostReceipt observedAt { current.State with Lifecycle = merged; Receipt = Some receipt }
                                            { current with State = state }, ()) with
                                        | Ok(Some ()) ->
                                            let! outcome = cleanupHost profile command operation digest cancellationToken
                                            match outcome with
                                            | Duplicate updated -> return Completed updated
                                            | other -> return other
                                        | _ -> return Refused "portable-journal-settlement-refused"
                            | _ -> return Refused "portable-executor-deadline-refused"
            }

        member this.ExecuteAsync(authority, profile, command, cancellationToken) =
            match hostReservation with
            | None -> this.ExecuteLegacyAsync(authority, profile, command, cancellationToken)
            | Some reservation -> executeHost authority profile command reservation cancellationToken

        member this.RecoverAsync(profile, command, cancellationToken) =
            match hostReservation with
            | None -> this.RecoverLegacyAsync(profile, command, cancellationToken)
            | Some reservation ->
                task {
                    match readHostEntry profile command reservation with
                    | Error reason -> return Refused reason
                    | Ok(_, _, None) -> return Refused "portable-reconciliation-unknown-refused"
                    | Ok(operation, digest, Some _) -> return! cleanupHost profile command operation digest cancellationToken
                }

        /// Reads the original durable state. This does not authenticate synthetic adapter observations.
        member _.ReadHostState(profile, command) =
            match hostReservation with
            | None -> Error "portable-host-mode-required"
            | Some reservation ->
                readHostEntry profile command reservation
                |> Result.bind (function
                    | _, _, Some entry -> Ok entry.State
                    | _ -> Error "portable-reconciliation-unknown-refused")

        /// Bounded observation only: never calls RunAsync, RecoverAsync, CleanupAsync or an outer transport.
        member _.ReconcileHost(profile, command, incoming: PortableHostLifecycle) =
            match hostReservation with
            | None -> Error "portable-host-mode-required"
            | Some reservation ->
                readHostEntry profile command reservation
                |> Result.bind (fun (_, digest, entry) ->
                    match entry with
                    | None -> Error "portable-reconciliation-unknown-refused"
                    | Some _ ->
                        journal.ChangeHost(command.WorkspaceScope, command.IdempotencyId, digest, fun current ->
                            let lifecycle = PortableHostAllocation.reconcileLifecycle current.State.Lifecycle incoming
                            let state = projectHostReceipt (clock ()) { current.State with Lifecycle = lifecycle }
                            { current with State = state }, state)
                        |> Result.bind (Option.map Ok >> Option.defaultValue (Error "portable-host-journal-conflict")))

        member private _.ExecuteLegacyAsync
            (
                authority: PortableWorkspaceAuthority,
                profile: PortableWorkspaceProfile,
                command: PortableWorkspaceCommand,
                cancellationToken: CancellationToken
            ) =
            task {
                let prior =
                    selectReviewedOperation profile command
                    |> Result.bind (fun operation ->
                        let digest = bindingDigest profile command operation

                        journal.Read(command.WorkspaceScope, command.IdempotencyId)
                        |> Result.map (function
                            | Some(PortableRunning(commandId, existingDigest, _)) when existingDigest = digest ->
                                Some(PendingDuplicate commandId)
                            | Some(PortableSettled(existingDigest, receipt)) when existingDigest = digest ->
                                Some(Duplicate receipt)
                            | Some _ -> Some(Refused "portable-executor-idempotency-conflict")
                            | None -> None))

                let! preparation = task {
                    match prior with
                    | Error reason -> return Error(Refused reason)
                    | Ok(Some outcome) -> return Error outcome
                    | Ok None ->
                        match refuseBeforeLaunch authority profile command with
                        | Error reason -> return Error(Refused reason)
                        | Ok(prepared, operation, workingDirectory) ->
                            let! attempt = prepareAttemptAsync profile command operation prepared.Deadline cancellationToken
                            match attempt with
                            | Error reason -> return Error(Refused reason)
                            | Ok attempt ->
                                let digest = bindingDigest profile command operation
                                let containerName = "fsgg-portable-" + digest[..23]
                                match consumeAttempt profile command operation attempt |> Result.mapError (fun reason -> if reason = "preparation-deadline-refused" then "portable-executor-deadline-refused" else reason) with
                                | Error reason -> return Error(Refused reason)
                                | Ok () ->
                                    let launchObservedAt = clock ()
                                    if cancellationToken.IsCancellationRequested || prepared.Deadline <= launchObservedAt then
                                        return Error(Refused "portable-executor-deadline-refused")
                                    else
                                        match journal.Reserve(prepared.WorkspaceScope, prepared.IdempotencyId, prepared.CommandId, digest, containerName) with
                                        | Error _ -> return Error(Refused "portable-journal-unavailable")
                                        | Ok(Choice1Of2(PortableRunning(commandId, existingDigest, _))) when existingDigest = digest ->
                                            return Error(PendingDuplicate commandId)
                                        | Ok(Choice1Of2(PortableSettled(existingDigest, receipt))) when existingDigest = digest ->
                                            return Error(Duplicate receipt)
                                        | Ok(Choice1Of2 _) -> return Error(Refused "portable-executor-idempotency-conflict")
                                        | Ok(Choice2Of2()) -> return Ok(prepared, operation, workingDirectory, digest, containerName, launchObservedAt, attempt)
                }

                match preparation with
                | Error(Duplicate receipt) when not receipt.CleanupCompleted ->
                    match selectReviewedOperation profile command with
                    | Error reason -> return Refused reason
                    | Ok operation ->
                        let digest = bindingDigest profile command operation

                        let! updated =
                            cleanupReceipt profile command operation digest receipt cancellationToken

                        return Duplicate updated
                | Error outcome -> return outcome
                | Ok(prepared, operation, workingDirectory, digest, containerName, launchObservedAt, attempt) ->
                    let request: PortableProcessRequest =
                        {
                            Executable = operation.Executable
                            Arguments = operation.Arguments
                            WorkingDirectory = workingDirectory
                            Deadline = runtimeDeadline launchObservedAt prepared.Deadline prepared.MaximumRuntimeSeconds
                            MaximumOutputBytes = prepared.MaximumOutputBytes
                            WorkspaceScope = prepared.WorkspaceScope
                            SourceRepository = policy.WorkspaceRoot
                            SourceRevision = prepared.SourceRevision
                            QualifiedImage = prepared.QualifiedImage
                            ContainerName = containerName
                            ContainerPath = policy.Runtime.ContainerPath
                            ContainerEnvironment = policy.Runtime.ContainerEnvironment
                            VerificationPath = operation.VerificationPath
                            VerificationSha256 = operation.VerificationSha256
                            RecipeSha256 = operation.RecipeSha256
                        }

                    match consumeAttempt profile command operation attempt with
                    | Error reason ->
                        journal.Abandon(prepared.WorkspaceScope, prepared.IdempotencyId, digest) |> ignore
                        return Refused reason
                    | Ok () ->
                        let! observed =
                            task {
                                try
                                    return! runner.RunAsync(request, cancellationToken)
                                with _ ->
                                    return
                                        {
                                            ExecutionStarted = true
                                            ExitCode = None
                                            StandardOutput = Array.empty
                                            StandardError = Array.empty
                                            CancellationRequested = cancellationToken.IsCancellationRequested
                                            TerminationObserved = false
                                            Interrupted = true
                                            OutputLimitExceeded = false
                                            OutputComplete = false
                                            SourceTree = None
                                            SnapshotSha256 = None
                                            RuntimeIdentity = None
                                            ContainerIdentity = None
                                            VerificationObserved = false
                                            VerificationOutput = None
                                            VerificationCustodyLimitExceeded = false
                                            Refusal = None
                                        }
                            }

                        let receipt = receiptFromObservation prepared operation observed

                        if observed.ExecutionStarted && not observed.TerminationObserved then
                            return Completed receipt
                        else
                            match journal.Settle(prepared.WorkspaceScope, prepared.IdempotencyId, digest, receipt) with
                            | Ok true ->
                                let! updated =
                                    cleanupReceipt profile command operation digest receipt cancellationToken

                                return Completed updated
                            | _ -> return Refused "portable-journal-settlement-refused"
                }

        member private _.RecoverLegacyAsync
            (profile: PortableWorkspaceProfile, command: PortableWorkspaceCommand, cancellationToken: CancellationToken)
            =
            task {
                match selectReviewedOperation profile command with
                | Error reason -> return Refused reason
                | Ok operation ->
                    let digest = bindingDigest profile command operation

                    match journal.Read(command.WorkspaceScope, command.IdempotencyId) with
                    | Error _ -> return Refused "portable-journal-unavailable"
                    | Ok(Some(PortableSettled(existing, receipt))) when existing = digest ->
                        let! updated =
                            cleanupReceipt profile command operation digest receipt cancellationToken

                        return Duplicate updated
                    | Ok(Some(PortableRunning(_, existing, containerName))) when existing = digest ->
                        let prepared = preparedForRecovery profile command operation
                        let observedAt = clock ()

                        let request =
                            requestFor
                                prepared
                                operation
                                containerName
                                (deadlineAfter observedAt policy.Runtime.TerminationGrace)

                        let! observed =
                            task {
                                try
                                    return! runner.RecoverAsync(request, cancellationToken)
                                with _ ->
                                    return
                                        {
                                            ExecutionStarted = true
                                            ExitCode = None
                                            StandardOutput = Array.empty
                                            StandardError = Array.empty
                                            CancellationRequested = cancellationToken.IsCancellationRequested
                                            TerminationObserved = false
                                            Interrupted = true
                                            OutputLimitExceeded = false
                                            OutputComplete = false
                                            SourceTree = None
                                            SnapshotSha256 = None
                                            RuntimeIdentity = None
                                            ContainerIdentity = Some containerName
                                            VerificationObserved = false
                                            VerificationOutput = None
                                            VerificationCustodyLimitExceeded = false
                                            Refusal = None
                                        }
                            }

                        if not observed.TerminationObserved then
                            return PendingDuplicate command.CommandId
                        else
                            let receipt = receiptFromObservation prepared operation observed

                            match journal.Settle(command.WorkspaceScope, command.IdempotencyId, digest, receipt) with
                            | Ok true ->
                                let! updated =
                                    cleanupReceipt profile command operation digest receipt cancellationToken

                                return Completed updated
                            | _ -> return Refused "portable-journal-settlement-refused"
                    | Ok(Some _) -> return Refused "portable-executor-idempotency-conflict"
                    | Ok None -> return Refused "portable-reconciliation-unknown-refused"
            }

    /// Concrete host-mode caller for independently authenticated supplier evidence. The underlying
    /// Executor and journal remain the only execution/recovery path; no supplier effect is invoked.
    /// Restarts require the original independently retained supplier pins through ingress. The
    /// executor journal cannot establish issuer/CID/inode trust from a current status document.
    type SupplierExecutor private (policy: PortableExecutorPolicy, runner: IPortableProcessRunner,
                                   initial: PortableAuthenticatedHostEvidence, clock: unit -> DateTimeOffset) =
        let original = initial.Evidence
        let mutable latest = initial
        let executor = Executor(policy, runner, clock = clock, hostReservation = original.Reservation,
                                hostEvidenceCurrent = (fun () -> latest.IsCurrent(clock ())))
        let command = original.Reservation.Binding.Command
        let synchronize profile allowFresh =
            match executor.ReadHostState(profile, command) with
            | Ok _ -> executor.ReconcileHost(profile, command, latest.Evidence.Lifecycle) |> Result.map ignore
            | Error "portable-reconciliation-unknown-refused" when allowFresh
                                                                    && latest.Evidence.Lifecycle = PortableHostAllocation.emptyLifecycle -> Ok ()
            | Error reason -> Error reason

        static member Create(policy, runner, evidence: PortableAuthenticatedHostEvidence, clock) =
            if isNull (box evidence) || not (evidence.IsCurrent(clock ())) then
                Error "portable-host-supplier-evidence-expired"
            else Ok(SupplierExecutor(policy, runner, evidence, clock))

        member _.ExecuteAsync(authority, profile, cancellationToken) =
            if not (latest.IsCurrent(clock ())) then
                Task.FromResult(Refused "portable-host-supplier-evidence-expired")
            else
                match synchronize profile true with
                | Error reason -> Task.FromResult(Refused reason)
                | Ok () -> executor.ExecuteAsync(authority, profile, command, cancellationToken)

        member _.RecoverAsync(profile, cancellationToken) =
            if not (latest.IsCurrent(clock ())) then
                Task.FromResult(Refused "portable-host-supplier-evidence-expired")
            else
                match synchronize profile false with
                | Error reason -> Task.FromResult(Refused reason)
                | Ok () -> executor.RecoverAsync(profile, command, cancellationToken)

        member _.ReadHostState(profile) = executor.ReadHostState(profile, command)

        /// Observation only. It cannot start a workload, repeat cleanup or retire the supplier.
        member _.Reconcile(profile, evidence: PortableAuthenticatedHostEvidence) =
            if isNull (box evidence) || not (evidence.IsCurrent(clock ())) then
                Error "portable-host-supplier-evidence-expired"
            elif evidence.Evidence.Identity <> original.Identity
                 || evidence.Evidence.Reservation <> original.Reservation then
                Error "portable-host-supplier-binding-refused"
            else
                executor.ReconcileHost(profile, command, evidence.Evidence.Lifecycle)
                |> Result.map (fun state -> latest <- evidence; state)
