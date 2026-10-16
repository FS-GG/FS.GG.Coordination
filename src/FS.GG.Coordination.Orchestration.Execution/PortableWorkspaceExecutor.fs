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

exception private SupplierSourceConflictException

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
    | PortableSupplierAdmissionDraft of PortableSupplierAdmissionState

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

    // This v2 record is an inert source draft. It cannot become authenticated host execution.
    let private writeSupplier (writer: BinaryWriter) (state: PortableSupplierAdmissionState) =
        PortableSupplierAdmission.validate state |> Result.defaultWith invalidOp |> ignore
        let text maximum (value: string) =
            if isNull value then invalidOp "supplier-journal-null"
            let bytes = Encoding.UTF8.GetBytes value
            if bytes.Length > maximum then invalidOp "supplier-journal-field-size"
            writer.Write bytes.Length
            writer.Write bytes
        let optional write value = writer.Write(Option.isSome value); value |> Option.iter write
        let phase = function
            | SupplierIntentStored -> 0uy | SupplierCreationPinned -> 1uy | SupplierAwaitingGenesis -> 2uy
            | SupplierGenesisAccepted -> 3uy | SupplierAckPrepared -> 4uy | SupplierAwaitingActivation -> 5uy | SupplierHostExecution -> 6uy
        let reservation value =
            use memory = new MemoryStream()
            use nested = new BinaryWriter(memory, Encoding.UTF8, true)
            writeHost nested { BindingDigest = "supplier-source-only"; HostContainerName = "source-only"
                               State = { Reservation = value; Lifecycle = PortableHostAllocation.emptyLifecycle; Receipt = None } }
            nested.Flush()
            if memory.Length > 524288L then invalidOp "supplier-host-section-size"
            writer.Write(int memory.Length)
            writer.Write(memory.ToArray())
        let observation (value: PortableSupplierTranscriptObservation) =
            writer.Write(match value.Kind with SupplierCreation -> 0uy | SupplierGenesis -> 1uy | SupplierAppend -> 2uy | SupplierUnchanged -> 3uy)
            for value in [value.IntentDigest; value.TrustPolicyDigest; value.ChallengeDigest; value.Purpose] do text 64 value
            reservation value.OriginalReservation
            text 4096 value.Identity.Issuer; text 4096 value.Identity.ContainerId
            writer.Write value.Identity.AllocationDevice; writer.Write value.Identity.AllocationInode
            text 64 value.OuterImageId; text 4096 value.SupplierSourceRevision
            for value in [value.CreationBase64; value.CreationReceiptBase64; value.ReservationBase64; value.ResponseBase64] do text 87384 value
            writer.Write value.EventsBase64.Length
            value.EventsBase64 |> List.iter (text 35684)
            optional (text 64) value.FromHead; optional (text 64) value.ToHead
            writer.Write value.Sequence; writeDate writer value.CollectedAt
        text 64 "source-only-unqualified-authentication-and-durability"
        writer.Write state.Revision; writer.Write(phase state.Phase)
        let command = PortableWorkspaceContract.commandBytes state.Original.Command |> Result.defaultWith invalidOp
        text 349528 (Convert.ToBase64String command)
        text 4096 state.Original.QualifiedImage; text 60000 state.Original.IntentBase64
        text 64 state.Original.IntentDigest; text 64 state.Original.TrustPolicyDigest
        text 4096 state.Original.SupplierSourceRevision; text 36 (state.Original.ReceiverEpoch.ToString("D"))
        let wire (challenge: PortableSupplierAdmissionChallenge) =
            text 36 (challenge.ChallengeId.ToString("D")); text 64 challenge.Digest; text 60000 challenge.BytesBase64
            text 64 challenge.Purpose; text 36 (challenge.ReceiverEpoch.ToString("D"))
            writeDate writer challenge.IssuedAt; writeDate writer challenge.NotAfter
            writer.Write challenge.IssuedMonotonic; writer.Write challenge.DeadlineMonotonic
            optional (text 64) challenge.ExpectedHead
        let native (challenge: PortableSupplierNativeProofChallenge) =
            text 36 (challenge.Context.OperationId.ToString("D"))
            text 64 challenge.Context.ReservationDigest; text 64 challenge.Context.ProofEventHead
            text 64 challenge.Context.NativeReceiptSha256; writeDate writer challenge.Context.HostLeaseNotAfter
            text 36 (challenge.ChallengeId.ToString("D")); text 64 challenge.Digest; text 60000 challenge.BytesBase64
            text 36 (challenge.ReceiverEpoch.ToString("D"))
            writeDate writer challenge.IssuedAt; writeDate writer challenge.NotAfter
            writer.Write challenge.IssuedMonotonic; writer.Write challenge.DeadlineMonotonic
        let selectedNative (challenge: PortableSupplierSelectedNativeProofChallenge) =
            if state.JournalVersion <> SupplierJournalV4 then invalidOp "supplier-selected-native-generation"
            text 36 (challenge.Context.OperationId.ToString("D"))
            text 64 challenge.Context.ReservationDigest; text 64 challenge.Context.ProofEventHead
            writeDate writer challenge.Context.HostLeaseNotAfter
            text 64 challenge.Context.ExpectedCurrentHead; writer.Write challenge.Context.ExpectedCurrentSequence
            text 36 (challenge.ChallengeId.ToString("D")); text 64 challenge.Digest; text 60000 challenge.BytesBase64
            text 36 (challenge.ReceiverEpoch.ToString("D"))
            writeDate writer challenge.IssuedAt; writeDate writer challenge.NotAfter
            writer.Write challenge.IssuedMonotonic; writer.Write challenge.DeadlineMonotonic
        let pending pending =
            if state.JournalVersion <> SupplierJournalV2 then writer.Write(match pending with WireV1 _ -> 0uy | NativeProofV1 _ -> 1uy | NativeSelectedProofV2 _ -> 2uy)
            match pending with WireV1 challenge -> wire challenge | NativeProofV1 challenge -> native challenge | NativeSelectedProofV2 challenge -> selectedNative challenge
        let anchor (value: PortableSupplierNativeIssuanceAnchor) =
            writer.Write(match value.ChallengeKind with SupplierWireV1 -> invalidOp "supplier-native-anchor-kind" | SupplierNativeProofV1 -> 1uy | SupplierSelectedNativeProofV2 -> 2uy)
            text 36 (value.ChallengeId.ToString("D")); text 64 value.ChallengeDigest; text 64 value.Head; writer.Write value.Sequence
        optional pending state.Challenge
        writer.Write state.ChallengeLedger.Length
        for kind, id, digest, disposition in state.ChallengeLedger do
            if state.JournalVersion <> SupplierJournalV2 then writer.Write(match kind with SupplierWireV1 -> 0uy | SupplierNativeProofV1 -> 1uy | SupplierSelectedNativeProofV2 -> 2uy)
            text 36 (id.ToString("D")); text 64 digest; text 16 disposition
        optional observation state.Pinned
        optional (text 64) state.Head; writer.Write state.Sequence
        for values in [state.EventsBase64; state.ResponsesBase64] do
            writer.Write values.Length
            values |> List.iter (text 87384)
        optional (text 524288) state.CheckpointBase64; optional (text 60000) state.AckDraftBase64
        optional (text 4096) state.FirstFailure
        writer.Write state.Conflicts.Count
        state.Conflicts |> Seq.iter (text 4096)
        if state.JournalVersion = SupplierJournalV4 then
            let c = state.CheckpointCustody |> Option.defaultWith (fun () -> invalidOp "supplier-v4-custody-missing")
            text 36 (c.OriginalReceiverEpoch.ToString("D")); writer.Write c.StoreFence
            text 87384 c.OriginalIntentEnvelopeBase64
            let config = c.NativeConfiguration
            let intentDigest,signaturePolicy,initialEpoch,configRevision = PortableSupplierOriginalNativeConfiguration.metadata config
            let scopeHash,clockHash,destinationHash = PortableSupplierOriginalNativeConfiguration.expectedHashes config
            text 64 intentDigest; text 64 signaturePolicy; text 36 (initialEpoch.ToString("D")); writer.Write configRevision
            text 10924 (PortableSupplierOriginalNativeConfiguration.scopeBytes config |> Convert.ToBase64String); text 64 scopeHash
            text 5464 (PortableSupplierOriginalNativeConfiguration.clockBytes config |> Convert.ToBase64String); text 64 clockHash
            optional (text 2732) (PortableSupplierOriginalNativeConfiguration.destinationBytes config |> Option.map Convert.ToBase64String)
            optional (text 64) destinationHash
            optional anchor c.PendingNativeAnchor
            let exchange (e: PortableSupplierRetainedWireExchange) =
                writer.Write e.AcceptedRevision; writer.Write e.AcceptedStoreFence
                wire e.Challenge
                text 87384 e.RequestBase64; text 87384 e.ResultBase64; text 87384 e.EvidenceBase64
                text 43692 e.ReceiverBundleBase64; text 10924 e.ControllerBundleBase64
            optional exchange c.CreationExchange
            writer.Write c.WireExchanges.Length; c.WireExchanges |> List.iter exchange
            writer.Write c.NativeAcquisitions.Length
            for e in c.NativeAcquisitions do
                writer.Write e.AcceptedRevision; writer.Write e.AcceptedStoreFence
                pending e.Challenge; anchor e.IssuanceAnchor
                text 87384 e.RequestBase64; text 87384 e.ResultBase64; text 87384 e.ProofEnvelopeBase64
                text 43692 e.ReceiptBase64; text 10924 e.ControllerBundleBase64
            writer.Write c.RejectedExchanges.Length
            for e in c.RejectedExchanges do
                writer.Write e.AcceptedRevision; writer.Write e.AcceptedStoreFence
                pending e.Challenge; optional anchor e.NativeAnchor; text 87384 e.RequestBase64
                writer.Write e.FramesBase64.Length; e.FramesBase64 |> List.iter (text 87384)
                optional (text 43692) e.ReceiverBundleBase64; optional (text 10924) e.ControllerBundleBase64
                text 4096 e.Reason
            writer.Write c.AbandonedChallenges.Length; c.AbandonedChallenges |> List.iter pending
            optional (text 21848) c.GenesisCheckpointBase64
            optional (text 21848) c.OutputCheckpointBase64

    let private readSupplier version (reader: BinaryReader) =
        let text maximum =
            let length = reader.ReadInt32()
            if length < 0 || length > maximum || int64 length > reader.BaseStream.Length - reader.BaseStream.Position then
                invalidOp "supplier-journal-field-size"
            let bytes = reader.ReadBytes length
            let value = UTF8Encoding(false, true).GetString bytes
            if Encoding.UTF8.GetBytes(value) <> bytes then invalidOp "supplier-journal-text"
            value
        let optional read =
            let present = reader.ReadByte()
            if version <> SupplierJournalV2 && present > 1uy then invalidOp "supplier-journal-optional-tag"
            if present <> 0uy then Some(read()) else None
        let guid () =
            let value = text 36
            let parsed = Guid.ParseExact(value, "D")
            if version <> SupplierJournalV2 && parsed.ToString("D") <> value then invalidOp "supplier-journal-uuid"
            parsed
        let count maximum =
            let n = reader.ReadInt32()
            if n < 0 || n > maximum then invalidOp "supplier-journal-count"
            n
        let texts maximum fieldMaximum = [ for _ in 1 .. count maximum -> text fieldMaximum ]
        let reservation () =
            let length = reader.ReadInt32()
            if length <= 0 || length > 524288 || int64 length > reader.BaseStream.Length - reader.BaseStream.Position then
                invalidOp "supplier-host-section-size"
            use memory = new MemoryStream(reader.ReadBytes length, false)
            use nested = new BinaryReader(memory, Encoding.UTF8, false)
            let value = readHost nested
            if memory.Position <> memory.Length then invalidOp "supplier-host-trailing-bytes"
            value.State.Reservation
        let observation () : PortableSupplierTranscriptObservation =
            let kind = match reader.ReadByte() with 0uy -> SupplierCreation | 1uy -> SupplierGenesis | 2uy -> SupplierAppend | 3uy -> SupplierUnchanged | _ -> invalidOp "supplier-transcript-kind"
            let intent = text 64
            let policy = text 64
            let challenge = text 64
            let purpose = text 64
            let original = reservation ()
            let issuer = text 4096
            let cid = text 4096
            let device = reader.ReadUInt64()
            let inode = reader.ReadUInt64()
            let image = text 64
            let source = text 4096
            let creation = text 87384
            let receipt = text 87384
            let reserved = text 87384
            let response = text 87384
            let events = texts 16 35684
            let previous = optional (fun () -> text 64)
            let head = optional (fun () -> text 64)
            let sequence = reader.ReadUInt64()
            let collected = readDate reader
            { Kind = kind; IntentDigest = intent; TrustPolicyDigest = policy; ChallengeDigest = challenge; Purpose = purpose
              OriginalReservation = original; Identity = { Issuer = issuer; ContainerId = cid; AllocationDevice = device; AllocationInode = inode }
              OuterImageId = image; SupplierSourceRevision = source; CreationBase64 = creation; CreationReceiptBase64 = receipt
              ReservationBase64 = reserved; ResponseBase64 = response; EventsBase64 = events; FromHead = previous
              ToHead = head; Sequence = sequence; CollectedAt = collected }
        if text 64 <> "source-only-unqualified-authentication-and-durability" then invalidOp "supplier-journal-provenance"
        let revision = reader.ReadUInt64()
        let phase = match reader.ReadByte() with 0uy -> SupplierIntentStored | 1uy -> SupplierCreationPinned | 2uy -> SupplierAwaitingGenesis | 3uy -> SupplierGenesisAccepted | 4uy -> SupplierAckPrepared | 5uy -> SupplierAwaitingActivation | 6uy -> SupplierHostExecution | _ -> invalidOp "supplier-phase"
        let command = text 349528 |> Convert.FromBase64String |> PortableWorkspaceContract.parseCommand |> Result.defaultWith invalidOp
        let image = text 4096
        let intent = text 60000
        let intentDigest = text 64
        let policy = text 64
        let source = text 4096
        let epoch = guid ()
        let original: PortableSupplierAdmissionOriginal =
            { Command = command
              QualifiedImage = image
              IntentBase64 = intent
              IntentDigest = intentDigest
              TrustPolicyDigest = policy
              SupplierSourceRevision = source
              ReceiverEpoch = epoch }
        let kind () =
            if version = SupplierJournalV2 then SupplierWireV1
            else match reader.ReadByte() with
                 | 0uy -> SupplierWireV1 | 1uy -> SupplierNativeProofV1
                 | 2uy when version = SupplierJournalV4 -> SupplierSelectedNativeProofV2
                 | _ -> invalidOp "supplier-challenge-kind"
        let wire () : PortableSupplierAdmissionChallenge =
            let id = guid ()
            let digest = text 64
            let bytes = text 60000
            let purpose = text 64
            let epoch = guid ()
            let issued = readDate reader
            let deadline = readDate reader
            let monotonic = reader.ReadInt64()
            let bound = reader.ReadInt64()
            let head = optional (fun () -> text 64)
            { ChallengeId = id; Digest = digest; BytesBase64 = bytes; Purpose = purpose; ReceiverEpoch = epoch
              IssuedAt = issued; NotAfter = deadline; IssuedMonotonic = monotonic; DeadlineMonotonic = bound; ExpectedHead = head }
        let native () : PortableSupplierNativeProofChallenge =
            let operation = guid ()
            let reservation = text 64
            let proofHead = text 64
            let receipt = text 64
            let lease = readDate reader
            let id = guid ()
            let digest = text 64
            let bytes = text 60000
            let epoch = guid ()
            let issued = readDate reader
            let deadline = readDate reader
            let monotonic = reader.ReadInt64()
            let bound = reader.ReadInt64()
            let context =
                { Original = { original with ReceiverEpoch = epoch }; OperationId = operation; ReservationDigest = reservation
                  ProofEventHead = proofHead; NativeReceiptSha256 = receipt; HostLeaseNotAfter = lease }
            { Context = context; ChallengeId = id; Digest = digest; BytesBase64 = bytes; ReceiverEpoch = epoch
              IssuedAt = issued; NotAfter = deadline; IssuedMonotonic = monotonic; DeadlineMonotonic = bound }
        let selectedNative () : PortableSupplierSelectedNativeProofChallenge =
            if version <> SupplierJournalV4 then invalidOp "supplier-selected-native-generation"
            let operation = guid ()
            let reservation = text 64
            let proofHead = text 64
            let lease = readDate reader
            let currentHead = text 64
            let currentSequence = reader.ReadUInt64()
            let id = guid ()
            let digest = text 64
            let bytes = text 60000
            let epoch = guid ()
            let issued = readDate reader
            let deadline = readDate reader
            let monotonic = reader.ReadInt64()
            let bound = reader.ReadInt64()
            let context =
                { Original = { original with ReceiverEpoch = epoch }; OperationId = operation; ReservationDigest = reservation
                  ProofEventHead = proofHead; HostLeaseNotAfter = lease
                  ExpectedCurrentHead = currentHead; ExpectedCurrentSequence = currentSequence }
            { Context = context; ChallengeId = id; Digest = digest; BytesBase64 = bytes; ReceiverEpoch = epoch
              IssuedAt = issued; NotAfter = deadline; IssuedMonotonic = monotonic; DeadlineMonotonic = bound }
        let pending () =
            match kind () with
            | SupplierWireV1 -> WireV1(wire ())
            | SupplierNativeProofV1 -> NativeProofV1(native ())
            | SupplierSelectedNativeProofV2 -> NativeSelectedProofV2(selectedNative ())
        let anchor () : PortableSupplierNativeIssuanceAnchor =
            if version <> SupplierJournalV4 then invalidOp "supplier-native-anchor-generation"
            let kind = kind ()
            if kind = SupplierWireV1 then invalidOp "supplier-native-anchor-kind"
            let id = guid ()
            let digest = text 64
            let head = text 64
            let sequence = reader.ReadUInt64()
            { ChallengeKind = kind; ChallengeId = id; ChallengeDigest = digest; Head = head; Sequence = sequence }
        let challenge = optional pending
        let ledger = [ for _ in 1 .. count 128 do
                           let kind = kind ()
                           let id = guid ()
                           let digest = text 64
                           let disposition = text 16
                           if disposition <> "consumed" && disposition <> "abandoned" then invalidOp "supplier-challenge-disposition"
                           yield kind, id, digest, disposition ]
        let pinned = optional observation
        let head = optional (fun () -> text 64)
        let sequence = reader.ReadUInt64()
        let events = texts 1024 87384
        let responses = texts 128 87384
        let checkpoint = optional (fun () -> text 524288)
        let ack = optional (fun () -> text 60000)
        let failure = optional (fun () -> text 4096)
        let conflicts = texts 1024 4096 |> Set.ofList
        let custody =
            if version <> SupplierJournalV4 then None
            else
                let initialEpoch = guid ()
                let fence = reader.ReadUInt64()
                let signedIntent = text 87384
                let configIntent = text 64
                let configSignaturePolicy = text 64
                let configInitialEpoch = guid ()
                let configRevision = reader.ReadUInt64()
                let configBytes maximum =
                    let encoded = text maximum
                    let bytes = Convert.FromBase64String encoded
                    if Convert.ToBase64String bytes <> encoded then invalidOp "supplier-native-configuration-base64"
                    bytes
                let scope = configBytes 10924
                let scopeHash = text 64
                let clock = configBytes 5464
                let clockHash = text 64
                let destination = optional (fun () -> configBytes 2732)
                let destinationHash = optional (fun () -> text 64)
                if configIntent <> original.IntentDigest || configSignaturePolicy <> original.TrustPolicyDigest
                   || configInitialEpoch <> initialEpoch then invalidOp "supplier-native-configuration-original"
                let destinationInput =
                    match destination,destinationHash with
                    | None,None -> None
                    | Some bytes,Some hash -> Some(bytes,hash)
                    | _ -> invalidOp "supplier-native-configuration-destination"
                let configuration =
                    PortableSupplierOriginalNativeConfiguration.create { original with ReceiverEpoch = initialEpoch }
                        configRevision scope scopeHash clock clockHash destinationInput |> Result.defaultWith invalidOp
                let pendingAnchor = optional anchor
                let exchange () : PortableSupplierRetainedWireExchange =
                    let accepted = reader.ReadUInt64()
                    let acceptedFence = reader.ReadUInt64()
                    let challenge = wire ()
                    let request = text 87384
                    let result = text 87384
                    let evidence = text 87384
                    let receiver = text 43692
                    let controller = text 10924
                    { AcceptedRevision = accepted; AcceptedStoreFence = acceptedFence
                      Challenge = challenge; RequestBase64 = request; ResultBase64 = result; EvidenceBase64 = evidence
                      ReceiverBundleBase64 = receiver; ControllerBundleBase64 = controller }
                let creation = optional exchange
                let exchanges = [ for _ in 1 .. count 128 -> exchange () ]
                let acquisitions =
                    [ for _ in 1 .. count 128 do
                        let accepted = reader.ReadUInt64()
                        let acceptedFence = reader.ReadUInt64()
                        let challenge = pending ()
                        match challenge with WireV1 _ -> invalidOp "supplier-native-acquisition-kind" | _ -> ()
                        let issuanceAnchor = anchor ()
                        let request = text 87384
                        let result = text 87384
                        let proof = text 87384
                        let receipt = text 43692
                        let controller = text 10924
                        yield { AcceptedRevision = accepted; AcceptedStoreFence = acceptedFence
                                Challenge = challenge; IssuanceAnchor = issuanceAnchor; RequestBase64 = request; ResultBase64 = result
                                ProofEnvelopeBase64 = proof; ReceiptBase64 = receipt; ControllerBundleBase64 = controller } ]
                let rejected =
                    [ for _ in 1 .. count 1 do
                        let accepted = reader.ReadUInt64()
                        let acceptedFence = reader.ReadUInt64()
                        let challenge = pending ()
                        let nativeAnchor = optional anchor
                        let request = text 87384
                        let frames = texts 2 87384
                        let receiver = optional (fun () -> text 43692)
                        let controller = optional (fun () -> text 10924)
                        let reason = text 4096
                        yield { AcceptedRevision = accepted; AcceptedStoreFence = acceptedFence; Challenge = challenge; NativeAnchor = nativeAnchor
                                RequestBase64 = request; FramesBase64 = frames; ReceiverBundleBase64 = receiver
                                ControllerBundleBase64 = controller; Reason = reason } ]
                let abandoned = [ for _ in 1 .. count 128 -> pending () ]
                let genesis = optional (fun () -> text 21848)
                let output = optional (fun () -> text 21848)
                Some { OriginalReceiverEpoch = initialEpoch; StoreFence = fence; OriginalIntentEnvelopeBase64 = signedIntent
                       NativeConfiguration = configuration; PendingNativeAnchor = pendingAnchor
                       CreationExchange = creation; WireExchanges = exchanges; NativeAcquisitions = acquisitions; RejectedExchanges = rejected
                       AbandonedChallenges = abandoned; GenesisCheckpointBase64 = genesis; OutputCheckpointBase64 = output }
        { JournalVersion = version; Revision = revision; CheckpointCustody = custody; Original = original
          Phase = phase; Challenge = challenge; ChallengeLedger = ledger; Pinned = pinned; Head = head
          Sequence = sequence; EventsBase64 = events; ResponsesBase64 = responses; CheckpointBase64 = checkpoint
          AckDraftBase64 = ack; FirstFailure = failure; Conflicts = conflicts }
        |> PortableSupplierAdmission.validate |> Result.defaultWith invalidOp

    let private supplierSourceBytes (state: PortableSupplierAdmissionState) =
        use memory = new MemoryStream()
        use writer = new BinaryWriter(memory, Encoding.UTF8, true)
        writer.Write(match state.JournalVersion with
                     | SupplierJournalV2 -> "fsgg.portable-execution-supplier-journal/2"
                     | SupplierJournalV3 -> "fsgg.portable-execution-supplier-journal/3"
                     | SupplierJournalV4 -> "fsgg.portable-execution-supplier-journal/4")
        writeSupplier writer state
        writer.Flush()
        if memory.Length > int64 PortableSupplierAdmission.MaximumSupplierBytes then invalidOp "supplier-journal-size"
        memory.ToArray()

    let private supplierV4Transition (previous: PortableSupplierAdmissionState) (current: PortableSupplierAdmissionState) =
        let before = previous.CheckpointCustody.Value
        let after = current.CheckpointCustody.Value
        let created =
            match before.CreationExchange,after.CreationExchange with
            | None,Some e -> [WireV1 e.Challenge,e.AcceptedRevision,e.AcceptedStoreFence,None]
            | _ -> []
        let additions =
            created
            @ (after.WireExchanges |> List.skip before.WireExchanges.Length |> List.map (fun e -> WireV1 e.Challenge,e.AcceptedRevision,e.AcceptedStoreFence,None))
            @ (after.NativeAcquisitions |> List.skip before.NativeAcquisitions.Length |> List.map (fun e -> e.Challenge,e.AcceptedRevision,e.AcceptedStoreFence,Some e.IssuanceAnchor))
        let rejected = after.RejectedExchanges |> List.skip before.RejectedExchanges.Length
        let abandoned = after.AbandonedChallenges |> List.skip before.AbandonedChallenges.Length
        let originalPinned = previous.Pinned |> Option.forall (fun pinned -> current.Pinned = Some pinned)
        let previouslyReady = previous.FirstFailure.IsNone && previous.Conflicts.IsEmpty
        match previous.Challenge,current.Challenge with
        | None,Some pending ->
            let phase =
                match pending,previous.Phase with
                | WireV1 _,SupplierCreationPinned -> SupplierAwaitingGenesis
                | _ -> previous.Phase
            let expectedCustody = { before with StoreFence = after.StoreFence; PendingNativeAnchor = after.PendingNativeAnchor }
            let expected = { previous with Revision = current.Revision; Challenge = Some pending; Phase = phase; CheckpointCustody = Some expectedCustody }
            previouslyReady && additions.IsEmpty && abandoned.IsEmpty && current = expected
        | Some pending,Some retained when retained = pending ->
            let exactRejected =
                match rejected with
                | [row] -> row.Challenge = pending && row.AcceptedRevision = current.Revision && row.AcceptedStoreFence = after.StoreFence
                           && row.NativeAnchor = before.PendingNativeAnchor
                | _ -> false
            let expectedCustody = { before with StoreFence = after.StoreFence; RejectedExchanges = after.RejectedExchanges }
            let expected = { previous with Revision = current.Revision; FirstFailure = current.FirstFailure; Conflicts = current.Conflicts; CheckpointCustody = Some expectedCustody }
            previouslyReady && current.FirstFailure.IsSome && not current.Conflicts.IsEmpty
            && additions.IsEmpty && abandoned.IsEmpty && exactRejected && current = expected
        | Some pending,None ->
            match additions,abandoned,current.ChallengeLedger with
            | [(challenge,revision,fence,anchor)],[],(kind,id,digest,"consumed")::ledger ->
                previouslyReady && current.FirstFailure.IsNone && current.Conflicts.IsEmpty
                && originalPinned && challenge = pending && revision = current.Revision && fence = after.StoreFence
                && (kind,id,digest) = (pending.Kind,pending.ChallengeId,pending.Digest)
                && ledger = previous.ChallengeLedger && current.Original = previous.Original
                && after.PendingNativeAnchor.IsNone && rejected.IsEmpty
                && (match pending with WireV1 _ -> anchor.IsNone | _ -> anchor = before.PendingNativeAnchor)
            | [],[abandoned],(kind,id,digest,"abandoned")::ledger ->
                let expectedCustody = { before with StoreFence = after.StoreFence; PendingNativeAnchor = None; AbandonedChallenges = before.AbandonedChallenges @ [pending] }
                let expected = { previous with Revision = current.Revision; Challenge = None; Original = current.Original; ChallengeLedger = current.ChallengeLedger; CheckpointCustody = Some expectedCustody }
                abandoned = pending && current.Original.ReceiverEpoch <> previous.Original.ReceiverEpoch
                && { previous.Original with ReceiverEpoch = current.Original.ReceiverEpoch } = current.Original
                && (kind,id,digest) = (pending.Kind,pending.ChallengeId,pending.Digest)
                && ledger = previous.ChallengeLedger && current = expected
            | _ -> false
        | _ -> false

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
                    | "fsgg.portable-execution-supplier-journal/2"
                    | "fsgg.portable-execution-supplier-journal/3"
                    | "fsgg.portable-execution-supplier-journal/4" ->
                        if info.Length > int64 PortableSupplierAdmission.MaximumSupplierBytes then invalidOp "supplier-journal-size"
                        PortableSupplierAdmissionDraft(readSupplier (if schema.EndsWith("/2", StringComparison.Ordinal) then SupplierJournalV2 elif schema.EndsWith("/3", StringComparison.Ordinal) then SupplierJournalV3 else SupplierJournalV4) reader)
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
            // /4 checks the complete candidate size before any temporary-file effect.
            let prepared = match entry with
                           | PortableSupplierAdmissionDraft state when state.JournalVersion = SupplierJournalV4 -> Some(supplierSourceBytes state)
                           | _ -> None
            let temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"

            try
                use stream =
                    new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                use writer = new BinaryWriter(stream, Encoding.UTF8, true)
                match entry with
                | PortableSupplierAdmissionDraft state when state.JournalVersion = SupplierJournalV4 ->
                    writer.Write(prepared.Value: byte array)
                | PortableSupplierAdmissionDraft state ->
                    writer.Write(match state.JournalVersion with SupplierJournalV2 -> "fsgg.portable-execution-supplier-journal/2" | SupplierJournalV3 -> "fsgg.portable-execution-supplier-journal/3" | SupplierJournalV4 -> "fsgg.portable-execution-supplier-journal/4")
                    writeSupplier writer state
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
                  | PortableHost _ | PortableSupplierAdmissionDraft _ -> invalidOp "portable-journal-state-refused"

                writer.Flush()
                stream.Flush true
                if stream.Length > int64 PortableWorkspaceContract.maximumDocumentBytes * 4L then
                    invalidOp "portable-journal-size-refused"
                match entry with
                | PortableSupplierAdmissionDraft _ when stream.Length > int64 PortableSupplierAdmission.MaximumSupplierBytes ->
                    invalidOp "supplier-journal-size"
                | _ -> ()
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
            | SupplierSourceConflictException -> Error "supplier-source-conflict"
            | :? IOException
            | :? UnauthorizedAccessException
            | :? InvalidDataException
            | :? InvalidOperationException
            | :? FormatException
            | :? ArgumentException -> Error "portable-journal-unavailable"

        member _.Read(workspaceScope, idempotencyId) =
            locked workspaceScope idempotencyId read

        member _.StageSupplier(state: PortableSupplierAdmissionState, expectedRevision: uint64 option, expectedFence: uint64 option) =
            locked state.Original.Command.WorkspaceScope state.Original.Command.IdempotencyId (fun path ->
                let eligible =
                    match read path, expectedRevision with
                    | None, None -> state.Revision = 1UL && expectedFence.IsNone
                    | Some(PortableSupplierAdmissionDraft previous), Some expected ->
                        previous.JournalVersion = state.JournalVersion
                        && (match previous.CheckpointCustody, state.CheckpointCustody with
                            | None, None -> expectedFence.IsNone
                            | Some before, Some after when state.JournalVersion = SupplierJournalV4 ->
                                expectedFence = Some before.StoreFence && before.StoreFence <> UInt64.MaxValue
                                && after.StoreFence = before.StoreFence + 1UL
                                && (previous.Original.ReceiverEpoch = state.Original.ReceiverEpoch
                                    || (previous.Challenge |> Option.exists (fun challenge ->
                                        state.Challenge.IsNone
                                        && List.contains (challenge.Kind, challenge.ChallengeId, challenge.Digest, "abandoned") state.ChallengeLedger
                                        && List.contains challenge after.AbandonedChallenges)))
                                && before.OriginalReceiverEpoch = after.OriginalReceiverEpoch
                                && before.OriginalIntentEnvelopeBase64 = after.OriginalIntentEnvelopeBase64
                                && before.NativeConfiguration = after.NativeConfiguration
                                && (before.CreationExchange |> Option.forall (fun e -> after.CreationExchange = Some e))
                                && (after.WireExchanges |> List.truncate before.WireExchanges.Length) = before.WireExchanges
                                && (after.NativeAcquisitions |> List.truncate before.NativeAcquisitions.Length) = before.NativeAcquisitions
                                && (after.RejectedExchanges |> List.truncate before.RejectedExchanges.Length) = before.RejectedExchanges
                                && (after.AbandonedChallenges |> List.truncate before.AbandonedChallenges.Length) = before.AbandonedChallenges
                                && (before.GenesisCheckpointBase64 |> Option.forall (fun raw -> after.GenesisCheckpointBase64 = Some raw))
                                && (before.OutputCheckpointBase64 |> Option.forall (fun raw -> after.OutputCheckpointBase64 = Some raw))
                            | _ -> false)
                        && previous.Revision = expected && expected <> UInt64.MaxValue && state.Revision = expected + 1UL
                        && { previous.Original with ReceiverEpoch = state.Original.ReceiverEpoch } = state.Original
                        && (previous.FirstFailure |> Option.forall (fun failure -> state.FirstFailure = Some failure))
                        && Set.isSubset previous.Conflicts state.Conflicts
                        && (previous.ChallengeLedger |> List.forall (fun entry -> List.contains entry state.ChallengeLedger))
                        && (previous.Challenge |> Option.forall (fun challenge ->
                            state.Challenge = Some challenge
                            || (state.ChallengeLedger |> List.exists (fun (kind, id, digest, disposition) ->
                                kind = challenge.Kind && id = challenge.ChallengeId && digest = challenge.Digest
                                && (disposition = "consumed" || disposition = "abandoned")))))
                        && (state.EventsBase64 |> List.truncate previous.EventsBase64.Length) = previous.EventsBase64
                        && (state.ResponsesBase64 |> List.truncate previous.ResponsesBase64.Length) = previous.ResponsesBase64
                        && (previous.Pinned |> Option.forall (fun pinned ->
                            state.Pinned |> Option.exists (fun current ->
                                current.Identity = pinned.Identity && current.OuterImageId = pinned.OuterImageId
                                && current.SupplierSourceRevision = pinned.SupplierSourceRevision
                                && current.OriginalReservation = pinned.OriginalReservation
                                && current.CreationBase64 = pinned.CreationBase64
                                && current.CreationReceiptBase64 = pinned.CreationReceiptBase64
                                && current.ReservationBase64 = pinned.ReservationBase64)))
                        && (state.JournalVersion <> SupplierJournalV4 || supplierV4Transition previous state)
                    | _ -> false
                if not eligible then
                    if state.JournalVersion = SupplierJournalV4 then raise SupplierSourceConflictException
                    else invalidOp "supplier-journal-original-or-revision-conflict"
                match state.Phase with
                | SupplierIntentStored | SupplierCreationPinned | SupplierAwaitingGenesis | SupplierGenesisAccepted -> ()
                | _ -> invalidOp "supplier-journal-effect-phase-unavailable"
                let expectedBytes = if state.JournalVersion = SupplierJournalV4 then Some(supplierSourceBytes state) else None
                write path (PortableSupplierAdmissionDraft state)
                expectedBytes |> Option.iter (fun expected ->
                    // New descriptor, bounded exact bytes, still under the same owner lock.
                    use fresh = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                    if fresh.Length <> int64 expected.Length then invalidOp "supplier-source-readback-size"
                    let actual = Array.zeroCreate<byte> expected.Length
                    fresh.ReadExactly(actual.AsSpan())
                    if fresh.ReadByte() <> -1 || actual <> expected then invalidOp "supplier-source-readback-mismatch"
                    // Cold decoding invokes all structural checkpoint/reference checks again.
                    match read path with
                    | Some(PortableSupplierAdmissionDraft decoded) when decoded = state -> ()
                    | _ -> invalidOp "supplier-source-cold-readback-mismatch")
                // Flush+rename is NOT a qualified directory-durable commit. No ACK/runner receipt.
                "supplier-admission-commit-unknown: directory durability is unavailable")

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

    /// Inactive source journal only: shares the existing key/lock, never a runner or transport.
    /// Every successful write is still durability-Unknown; no production ACK/handoff API exists.
    type SupplierAdmissionJournal(stateRoot: string) =
        let journal = DurableJournal(stateRoot)
        member _.Read(command: PortableWorkspaceCommand) : Result<PortableSupplierAdmissionState * PortableEvidence<unit>, string> =
            journal.Read(command.WorkspaceScope, command.IdempotencyId)
            |> Result.bind (function
                | Some(PortableSupplierAdmissionDraft state) -> Ok(state, EvidenceUnknown "supplier directory durability unqualified")
                | None -> Error "supplier-admission-journal-missing"
                | _ -> Error "supplier-admission-journal-conflict")
        member _.Stage(state: PortableSupplierAdmissionState, expectedRevision: uint64 option) =
            if state.JournalVersion = SupplierJournalV4 then Error "supplier-v4-explicit-fence-required"
            else
                PortableSupplierAdmission.validate state
                |> Result.bind (fun validated -> journal.StageSupplier(validated, expectedRevision, None))
        /// Same-store source CAS/readback. No qualified directory barrier or capability is produced.
        member _.StageV4(state: PortableSupplierAdmissionState, expectedRevision: uint64 option, expectedFence: uint64 option) =
            if state.JournalVersion <> SupplierJournalV4 || (PortableSupplierAdmission.validate state |> Result.isError) then
                SupplierSourceConflict
            else
                match journal.StageSupplier(state, expectedRevision, expectedFence) with
                | Ok _ -> SupplierSourceReadback state
                | Error "supplier-source-conflict" -> SupplierSourceConflict
                | Error _ -> SupplierSourceCommitUnknown

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
