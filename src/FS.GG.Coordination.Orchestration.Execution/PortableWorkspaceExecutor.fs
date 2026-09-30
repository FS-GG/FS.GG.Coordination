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

type PortableProcessObservation =
    {
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
    }

type IPortableProcessRunner =
    abstract RunAsync:
        request: PortableProcessRequest * cancellationToken: CancellationToken -> Task<PortableProcessObservation>

    abstract RecoverAsync:
        request: PortableProcessRequest * cancellationToken: CancellationToken -> Task<PortableProcessObservation>

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
        OutputSha256: string
        OutputBytes: uint64
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

type private PortableJournalEntry =
    | PortableRunning of commandId: Guid * bindingDigest: string * containerName: string
    | PortableSettled of bindingDigest: string * receipt: PortableExecutionReceipt

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
        writer.Write receipt.OutputSha256
        writer.Write receipt.OutputBytes
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
            OutputSha256 = reader.ReadString()
            OutputBytes = reader.ReadUInt64()
            CancellationRequested = reader.ReadBoolean()
            TerminationObserved = reader.ReadBoolean()
        }

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

                if reader.ReadString() <> "fsgg.portable-execution-journal/1" then
                    invalidOp "portable-journal-schema-refused"

                let entry =
                    match reader.ReadByte() with
                    | 1uy ->
                        PortableRunning(
                            Guid.ParseExact(reader.ReadString(), "D"),
                            reader.ReadString(),
                            reader.ReadString()
                        )
                    | 2uy -> PortableSettled(reader.ReadString(), readReceipt reader)
                    | _ -> invalidOp "portable-journal-state-refused"

                if stream.Position <> stream.Length then
                    invalidOp "portable-journal-trailing-bytes-refused"

                Some entry

        let write path entry =
            let temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"

            try
                use stream =
                    new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                use writer = new BinaryWriter(stream, Encoding.UTF8, true)
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

                writer.Flush()
                stream.Flush true
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
            | :? FormatException -> Error "portable-journal-unavailable"

        member _.Read(workspaceScope, idempotencyId) =
            locked workspaceScope idempotencyId read

        member _.Reserve(workspaceScope, idempotencyId, commandId, binding, containerName) =
            locked workspaceScope idempotencyId (fun path ->
                match read path with
                | Some existing -> Choice1Of2 existing
                | None ->
                    write path (PortableRunning(commandId, binding, containerName))
                    Choice2Of2())

        member _.Settle(workspaceScope, idempotencyId, binding, receipt) =
            locked workspaceScope idempotencyId (fun path ->
                match read path with
                | Some(PortableRunning(_, existing, _)) when existing = binding ->
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

    type Executor(policy: PortableExecutorPolicy, runner: IPortableProcessRunner, ?clock: unit -> DateTimeOffset) =
        let clock = defaultArg clock (fun () -> DateTimeOffset.UtcNow)
        let journal = DurableJournal policy.Runtime.StateRoot

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

            let workspaceResult =
                if observed.Interrupted || not observed.TerminationObserved then
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
                OutputSha256 = outputDigest observed.StandardOutput observed.StandardError
                OutputBytes = outputBytes
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

        member _.ExecuteAsync
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

                let preparation =
                    match prior with
                    | Error reason -> Error(Refused reason)
                    | Ok(Some outcome) -> Error outcome
                    | Ok None ->
                        refuseBeforeLaunch authority profile command
                        |> Result.mapError Refused
                        |> Result.bind (fun (prepared, operation, workingDirectory) ->
                            let digest = bindingDigest profile command operation
                            let containerName = "fsgg-portable-" + digest[..23]
                            let launchObservedAt = clock ()

                            if
                                cancellationToken.IsCancellationRequested
                                || prepared.Deadline <= launchObservedAt
                            then
                                Error(Refused "portable-executor-deadline-refused")
                            else
                                match
                                    journal.Reserve(
                                        prepared.WorkspaceScope,
                                        prepared.IdempotencyId,
                                        prepared.CommandId,
                                        digest,
                                        containerName
                                    )
                                with
                                | Error _ -> Error(Refused "portable-journal-unavailable")
                                | Ok(Choice1Of2(PortableRunning(commandId, existingDigest, _))) when
                                    existingDigest = digest
                                    ->
                                    Error(PendingDuplicate commandId)
                                | Ok(Choice1Of2(PortableSettled(existingDigest, receipt))) when existingDigest = digest ->
                                    Error(Duplicate receipt)
                                | Ok(Choice1Of2 _) -> Error(Refused "portable-executor-idempotency-conflict")
                                | Ok(Choice2Of2()) ->
                                    Ok(prepared, operation, workingDirectory, digest, containerName, launchObservedAt))

                match preparation with
                | Error outcome -> return outcome
                | Ok(prepared, operation, workingDirectory, digest, containerName, launchObservedAt) ->
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

                    let! observed =
                        task {
                            try
                                return! runner.RunAsync(request, cancellationToken)
                            with _ ->
                                return
                                    {
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
                                    }
                        }

                    let receipt = receiptFromObservation prepared operation observed

                    if not observed.TerminationObserved then
                        return Completed receipt
                    else
                        match journal.Settle(prepared.WorkspaceScope, prepared.IdempotencyId, digest, receipt) with
                        | Ok true -> return Completed receipt
                        | _ -> return Refused "portable-journal-settlement-refused"
            }

        member _.RecoverAsync
            (profile: PortableWorkspaceProfile, command: PortableWorkspaceCommand, cancellationToken: CancellationToken)
            =
            task {
                match selectReviewedOperation profile command with
                | Error reason -> return Refused reason
                | Ok operation ->
                    let digest = bindingDigest profile command operation

                    match journal.Read(command.WorkspaceScope, command.IdempotencyId) with
                    | Error _ -> return Refused "portable-journal-unavailable"
                    | Ok(Some(PortableSettled(existing, receipt))) when existing = digest -> return Duplicate receipt
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
                                        }
                            }

                        if not observed.TerminationObserved then
                            return PendingDuplicate command.CommandId
                        else
                            let receipt = receiptFromObservation prepared operation observed

                            match journal.Settle(command.WorkspaceScope, command.IdempotencyId, digest, receipt) with
                            | Ok true -> return Completed receipt
                            | _ -> return Refused "portable-journal-settlement-refused"
                    | Ok(Some _) -> return Refused "portable-executor-idempotency-conflict"
                    | Ok None -> return Refused "portable-reconciliation-unknown-refused"
            }
