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
    }

type PortableProcessRequest =
    {
        Executable: string
        Arguments: string list
        WorkingDirectory: string
        Deadline: DateTimeOffset
        MaximumOutputBytes: uint64
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
    }

type IPortableProcessRunner =
    abstract RunAsync:
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
    | PortableRunning of commandId: Guid * bindingDigest: string
    | PortableSettled of bindingDigest: string * receipt: PortableExecutionReceipt

type private PortableLaunchDisposition =
    | UsePortableOutcome of PortableExecutionOutcome
    | LaunchPortableOperation

[<RequireQualifiedAccess>]
module PortableWorkspaceExecutor =
    let private sha256 (bytes: byte array) =
        bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private bindingDigest (prepared: PreparedWorkspaceOperation) =
        String.concat
            "\n"
            [
                prepared.CommandId.ToString("D")
                prepared.WorkspaceScope
                prepared.ProfileId
                string prepared.ProfileRevision
                prepared.SourceRevision
                prepared.QualifiedImage
                string prepared.WorkflowRevision
                string prepared.FenceGeneration
                defaultArg prepared.ComponentId ""
                prepared.OperationIdentity
                prepared.WorkingDirectory
                prepared.EntryPoint
                prepared.Deadline.ToString("O")
                string prepared.MaximumRuntimeSeconds
                string prepared.MaximumOutputBytes
            ]
        |> Encoding.UTF8.GetBytes
        |> sha256

    let private outputDigest (stdout: byte array) (stderr: byte array) =
        Array.concat [ stdout; [| byte '\n' |]; stderr ] |> sha256

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
        let declared =
            profile.Components
            |> List.map (fun part -> part.Toolchain.Id, part.Toolchain.Version)
            |> Set.ofList

        operation.RequiredToolchains
        |> List.forall (fun required -> Set.contains required declared)

    let private fullWorkingDirectory root relative =
        try
            let canonicalRoot = Path.GetFullPath root
            let candidate = Path.GetFullPath(Path.Combine(canonicalRoot, relative))

            let prefix =
                canonicalRoot.TrimEnd(Path.DirectorySeparatorChar)
                + string Path.DirectorySeparatorChar

            let containsLink =
                relative.Split('/')
                |> Array.scan (fun parent segment -> Path.Combine(parent, segment)) canonicalRoot
                |> Array.exists (fun path ->
                    File.GetAttributes(path) &&& FileAttributes.ReparsePoint = FileAttributes.ReparsePoint)

            if
                candidate.StartsWith(prefix, StringComparison.Ordinal)
                && Directory.Exists candidate
                && not containsLink
            then
                Ok candidate
            else
                Error "portable-working-directory-refused"
        with
        | :? ArgumentException
        | :? IOException
        | :? UnauthorizedAccessException
        | :? NotSupportedException -> Error "portable-working-directory-refused"

    type Executor(policy: PortableExecutorPolicy, runner: IPortableProcessRunner, ?clock: unit -> DateTimeOffset) =
        let clock = defaultArg clock (fun () -> DateTimeOffset.UtcNow)
        let journal = Dictionary<string, PortableJournalEntry>(StringComparer.Ordinal)
        let gate = obj ()

        let refuseBeforeLaunch
            (authority: PortableWorkspaceAuthority)
            (profile: PortableWorkspaceProfile)
            (command: PortableWorkspaceCommand)
            =
            PortableWorkspaceAdapter.prepare (clock ()) authority profile command
            |> Result.bind (fun prepared ->
                if
                    prepared.WorkspaceScope <> policy.WorkspaceScope
                    || prepared.SourceRevision <> policy.SourceRevision
                then
                    Error "portable-executor-source-binding-refused"
                elif prepared.QualifiedImage <> policy.QualifiedImage then
                    Error "portable-executor-image-binding-refused"
                elif
                    prepared.MaximumRuntimeSeconds > policy.MaximumRuntimeSeconds
                    || prepared.MaximumOutputBytes > policy.MaximumOutputBytes
                then
                    Error "portable-executor-limit-refused"
                else
                    match
                        policy.Operations
                        |> List.tryFind (fun operation ->
                            operation.EntryPoint = prepared.EntryPoint
                            && operation.OperationIdentity = prepared.OperationIdentity
                            && operation.ComponentId = prepared.ComponentId
                            && operation.WorkingDirectory = prepared.WorkingDirectory)
                    with
                    | None -> Error "portable-executor-operation-refused"
                    | Some(operation: PortableReviewedOperation) ->
                        if operation.QualifiedImage <> prepared.QualifiedImage then
                            Error "portable-executor-image-binding-refused"
                        elif not (toolchainsMatch profile operation) then
                            Error "portable-executor-toolchain-refused"
                        elif String.IsNullOrWhiteSpace operation.VerificationIdentity then
                            Error "portable-executor-verification-refused"
                        else
                            fullWorkingDirectory policy.WorkspaceRoot operation.WorkingDirectory
                            |> Result.map (fun workingDirectory -> prepared, operation, workingDirectory))

        member _.ExecuteAsync
            (
                authority: PortableWorkspaceAuthority,
                profile: PortableWorkspaceProfile,
                command: PortableWorkspaceCommand,
                cancellationToken: CancellationToken
            ) =
            task {
                match refuseBeforeLaunch authority profile command with
                | Error reason -> return Refused reason
                | Ok(prepared, operation, workingDirectory) ->
                    let digest = bindingDigest prepared

                    let disposition =
                        lock gate (fun () ->
                            match journal.TryGetValue prepared.IdempotencyId with
                            | true, PortableRunning(commandId, existingDigest) when existingDigest = digest ->
                                UsePortableOutcome(PendingDuplicate commandId)
                            | true, PortableSettled(existingDigest, receipt) when existingDigest = digest ->
                                UsePortableOutcome(Duplicate receipt)
                            | true, _ -> UsePortableOutcome(Refused "portable-executor-idempotency-conflict")
                            | false, _ ->
                                journal.Add(prepared.IdempotencyId, PortableRunning(prepared.CommandId, digest))
                                LaunchPortableOperation)

                    match disposition with
                    | UsePortableOutcome outcome -> return outcome
                    | LaunchPortableOperation ->
                        let request: PortableProcessRequest =
                            {
                                Executable = operation.Executable
                                Arguments = operation.Arguments
                                WorkingDirectory = workingDirectory
                                Deadline =
                                    min prepared.Deadline ((clock ()).AddSeconds(float prepared.MaximumRuntimeSeconds))
                                MaximumOutputBytes = prepared.MaximumOutputBytes
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
                                        }
                            }

                        let completedAt = clock ()

                        let outputBytes =
                            uint64 observed.StandardOutput.LongLength
                            + uint64 observed.StandardError.LongLength

                        let digestOfOutput = outputDigest observed.StandardOutput observed.StandardError

                        let workspaceResult =
                            if observed.OutputLimitExceeded || outputBytes > prepared.MaximumOutputBytes then
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
                                | Some 0 ->
                                    result
                                        completedAt
                                        prepared
                                        (EvidenceKnown 0)
                                        (EvidenceKnown operation.VerificationIdentity)
                                        None
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

                        let receipt =
                            {
                                Result = workspaceResult
                                WorkspaceScope = prepared.WorkspaceScope
                                SourceRevision = prepared.SourceRevision
                                QualifiedImage = prepared.QualifiedImage
                                OperationIdentity = prepared.OperationIdentity
                                EntryPoint = prepared.EntryPoint
                                WorkingDirectory = prepared.WorkingDirectory
                                VerificationIdentity = operation.VerificationIdentity
                                OutputSha256 = digestOfOutput
                                OutputBytes = outputBytes
                                CancellationRequested = observed.CancellationRequested
                                TerminationObserved = observed.TerminationObserved
                            }

                        lock gate (fun () -> journal[prepared.IdempotencyId] <- PortableSettled(digest, receipt))
                        return Completed receipt
            }

        member _.Reconcile(idempotencyId: string, observation: PortableReconciliationObservation) =
            lock gate (fun () ->
                match journal.TryGetValue idempotencyId with
                | true, PortableSettled(binding, receipt) when receipt.Result.CommandId = observation.CommandId ->
                    if
                        receipt.SourceRevision <> observation.SourceRevision
                        || receipt.QualifiedImage <> observation.QualifiedImage
                        || receipt.VerificationIdentity <> observation.VerificationIdentity
                    then
                        Refused "portable-reconciliation-binding-refused"
                    elif not observation.TerminationObserved then
                        Duplicate receipt
                    elif
                        receipt.Result.Error
                        |> Option.exists (fun observed -> observed.Code <> "execution-outcome-unknown")
                    then
                        Duplicate receipt
                    else
                        let reconciledResult =
                            { receipt.Result with
                                CompletedAt = observation.ObservedAt
                                ExitCode =
                                    observation.ExitCode
                                    |> Option.map EvidenceKnown
                                    |> Option.defaultValue (EvidenceMissing "process-exit-code-unavailable")
                                ArtifactReference =
                                    match observation.ExitCode with
                                    | Some 0 -> EvidenceKnown receipt.VerificationIdentity
                                    | _ -> EvidenceMissing "operation-failed-before-verification"
                                Error =
                                    match observation.ExitCode with
                                    | Some 0 -> None
                                    | Some _ ->
                                        error
                                            "execution-failed"
                                            "The reconciled fixed operation returned a non-zero exit code."
                                            false
                                            [ "verification", receipt.VerificationIdentity ]
                                    | None ->
                                        error
                                            "execution-outcome-unknown"
                                            "Termination was observed without an exit code."
                                            true
                                            [ "verification", receipt.VerificationIdentity ]
                            }

                        let reconciled =
                            { receipt with
                                Result = reconciledResult
                                TerminationObserved = true
                            }

                        journal[idempotencyId] <- PortableSettled(binding, reconciled)
                        Completed reconciled
                | true, PortableSettled(_, _) -> Refused "portable-reconciliation-command-refused"
                | true, PortableRunning _ -> Refused "portable-reconciliation-running-refused"
                | false, _ -> Refused "portable-reconciliation-unknown-refused")

    type SystemProcessRunner() =
        interface IPortableProcessRunner with
            member _.RunAsync(request, cancellationToken) =
                task {
                    use child = new Process()

                    child.StartInfo <-
                        ProcessStartInfo(
                            request.Executable,
                            WorkingDirectory = request.WorkingDirectory,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false
                        )

                    for argument in request.Arguments do
                        child.StartInfo.ArgumentList.Add argument

                    if not (child.Start()) then
                        return
                            {
                                ExitCode = None
                                StandardOutput = Array.empty
                                StandardError = Array.empty
                                CancellationRequested = false
                                TerminationObserved = false
                                Interrupted = true
                                OutputLimitExceeded = false
                            }
                    else
                        use deadline = new CancellationTokenSource()
                        let remaining = request.Deadline - DateTimeOffset.UtcNow

                        deadline.CancelAfter(
                            if remaining <= TimeSpan.Zero then
                                TimeSpan.Zero
                            else
                                remaining
                        )

                        use combined =
                            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token)

                        let outputGate = obj ()
                        let mutable capturedBytes = 0UL
                        let mutable outputLimitExceeded = false

                        let readBounded (source: Stream) =
                            task {
                                use captured = new MemoryStream()
                                let buffer = Array.zeroCreate<byte> 4096
                                let mutable reading = true

                                while reading do
                                    let! count = source.ReadAsync(buffer.AsMemory(0, buffer.Length))

                                    if count = 0 then
                                        reading <- false
                                    else
                                        let allowed =
                                            lock outputGate (fun () ->
                                                let remaining =
                                                    if capturedBytes >= request.MaximumOutputBytes then
                                                        0
                                                    else
                                                        int (
                                                            min
                                                                (uint64 count)
                                                                (request.MaximumOutputBytes - capturedBytes)
                                                        )

                                                capturedBytes <- capturedBytes + uint64 remaining

                                                if remaining < count then
                                                    outputLimitExceeded <- true

                                                remaining)

                                        if allowed > 0 then
                                            captured.Write(buffer, 0, allowed)

                                        if outputLimitExceeded && not child.HasExited then
                                            try
                                                child.Kill(true)
                                            with _ ->
                                                ()

                                return captured.ToArray()
                            }

                        let stdout = readBounded child.StandardOutput.BaseStream
                        let stderr = readBounded child.StandardError.BaseStream
                        let mutable cancelled = false

                        try
                            do! child.WaitForExitAsync(combined.Token)
                        with :? OperationCanceledException ->
                            cancelled <- true

                            try
                                child.Kill(true)
                                do! child.WaitForExitAsync()
                            with _ ->
                                ()

                        let! outputBytes = stdout
                        let! errorBytes = stderr

                        return
                            {
                                ExitCode = if child.HasExited then Some child.ExitCode else None
                                StandardOutput = outputBytes
                                StandardError = errorBytes
                                CancellationRequested = cancelled
                                TerminationObserved = child.HasExited
                                Interrupted = not child.HasExited
                                OutputLimitExceeded = outputLimitExceeded
                            }
                }
