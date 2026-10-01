#nowarn "9"

namespace FS.GG.Coordination.Orchestration.Host

open System
open System.IO
open System.Runtime.InteropServices
open System.Runtime.CompilerServices
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Win32.SafeHandles
open FS.GG.Coordination.Orchestration.Execution

[<assembly: InternalsVisibleTo("FS.GG.Coordination.Orchestration.Host.Tests")>]
do ()

type LearningInstalledReadinessOptions =
    {
        Enabled: bool
        HostConfigPath: string
        ExpectedOwnerUid: uint32
        ExpectedExecutableOwnerUid: uint32
        MaximumCapabilityAge: TimeSpan
    }

type LearningInstalledProducerReceiptQuery =
    {
        WorkspaceId: string
        ProducerId: string
        StreamId: string
        Role: string
        GrantId: string
        GrantGeneration: int64
        ManagerReceiptSha256: string
        CapabilityProfileSha256: string
        CapabilityResultSha256: string
        NativeCaptureSha256: string
        NativeVerificationSha256: string
    }

type LearningInstalledProducerReceipt =
    {
        Source: LearningOperationalProducerIdentity
        WorkspaceId: string
        ProducerId: string
        StreamId: string
        Role: string
        GrantId: string
        GrantGeneration: int64
        ManagerReceiptSha256: string
        CapabilityProfileSha256: string
        CapabilityResultSha256: string
        NativeCaptureSha256: string
        NativeVerificationSha256: string
        CapabilityObservedAt: DateTimeOffset
        CapabilityExpiresAt: DateTimeOffset
    }

type ILearningInstalledProducerReceiptSource =
    abstract ReadLearningInstalledProducerReceipt:
        LearningInstalledProducerReceiptQuery * CancellationToken ->
            Task<Result<LearningInstalledProducerReceipt, string>>

type LearningInstalledReadinessSnapshot =
    {
        InstalledCustody: LearningOperationalProducerIdentity
        ProviderCapability: LearningOperationalProducerIdentity
        NativeCapture: LearningOperationalProducerIdentity
        CapabilityEvidence: LearningSelectionEvidence
        NativeSelection: LearningNativeSelection
        ExpiresAt: DateTimeOffset
    }

type ILearningInstalledReadinessSource =
    abstract ReadLearningInstalledReadiness:
        LearningSelectionQuery * CancellationToken -> Task<Result<LearningInstalledReadinessSnapshot, string>>

[<StructLayout(LayoutKind.Explicit, Size = 256)>]
type internal LinuxStatx =
    struct
        [<FieldOffset(0)>]
        val mutable Mask: uint32

        [<FieldOffset(16)>]
        val mutable LinkCount: uint32

        [<FieldOffset(20)>]
        val mutable OwnerUid: uint32

        [<FieldOffset(24)>]
        val mutable OwnerGid: uint32

        [<FieldOffset(28)>]
        val mutable Mode: uint16

        [<FieldOffset(32)>]
        val mutable Inode: uint64

        [<FieldOffset(40)>]
        val mutable Size: uint64

        [<FieldOffset(96)>]
        val mutable ChangedSeconds: int64

        [<FieldOffset(104)>]
        val mutable ChangedNanoseconds: uint32

        [<FieldOffset(112)>]
        val mutable ModifiedSeconds: int64

        [<FieldOffset(120)>]
        val mutable ModifiedNanoseconds: uint32

        [<FieldOffset(136)>]
        val mutable DeviceMajor: uint32

        [<FieldOffset(140)>]
        val mutable DeviceMinor: uint32
    end

type internal PrivateFileIdentity =
    {
        DeviceMajor: uint32
        DeviceMinor: uint32
        Inode: uint64
        Size: uint64
        OwnerUid: uint32
        FileType: uint16
        Mode: uint16
        LinkCount: uint32
    }

type internal ExecutableFileObservation =
    {
        Identity: PrivateFileIdentity
        ChangedSeconds: int64
        ChangedNanoseconds: uint32
        ModifiedSeconds: int64
        ModifiedNanoseconds: uint32
    }

[<RequireQualifiedAccess>]
module internal LinuxFiles =
    [<Literal>]
    let private readOnly = 0

    [<Literal>]
    let private closeOnExec = 0x80000

    [<Literal>]
    let private noFollow = 0x20000

    [<Literal>]
    let private directory = 0x10000

    [<Literal>]
    let private atEmptyPath = 0x1000

    [<Literal>]
    let private statxBasicStats = 0x7ffu

    [<Literal>]
    let executableStatxMask = 0x3cfu

    [<Literal>]
    let private regularFile = 0x8000us

    [<Literal>]
    let private directoryFile = 0x4000us

    [<Literal>]
    let private fileType = 0xf000us

    [<DllImport("libc", SetLastError = true, EntryPoint = "open")>]
    extern int private openFile(string path, int flags)

    [<DllImport("libc", SetLastError = true, EntryPoint = "statx")>]
    extern int private statx(int descriptor, string path, int flags, uint32 mask, LinuxStatx& value)

    [<DllImport("libc", EntryPoint = "geteuid")>]
    extern uint32 effectiveUserId()

    let private stat (handle: SafeFileHandle) =
        let mutable value = LinuxStatx()
        let descriptor = handle.DangerousGetHandle().ToInt32()

        if statx (descriptor, "", atEmptyPath, statxBasicStats, &value) <> 0 then
            Error "learning-installed-file-stat-refused"
        else
            Ok value

    let identityFromStatx (value: LinuxStatx) =
        {
            DeviceMajor = value.DeviceMajor
            DeviceMinor = value.DeviceMinor
            Inode = value.Inode
            Size = value.Size
            OwnerUid = value.OwnerUid
            FileType = value.Mode &&& fileType
            Mode = value.Mode &&& 0x1ffus
            LinkCount = value.LinkCount
        }

    let executableObservationFromStatx (value: LinuxStatx) =
        if value.Mask &&& executableStatxMask <> executableStatxMask then
            Error "learning-installed-file-stat-refused"
        else
            Ok
                {
                    Identity = identityFromStatx value
                    ChangedSeconds = value.ChangedSeconds
                    ChangedNanoseconds = value.ChangedNanoseconds
                    ModifiedSeconds = value.ModifiedSeconds
                    ModifiedNanoseconds = value.ModifiedNanoseconds
                }

    let private identity handle =
        stat handle |> Result.map identityFromStatx

    let private executableObservation handle =
        stat handle |> Result.bind executableObservationFromStatx

    let private canonicalAbsolutePath (path: string) =
        not (String.IsNullOrWhiteSpace path)
        && Path.IsPathFullyQualified path
        && Path.GetFullPath path = path

    let readPrivate maximumBytes expectedUid allowedModes path =
        try
            if not (canonicalAbsolutePath path) then
                Error "learning-installed-path-refused"
            else
                let descriptor = openFile (path, readOnly ||| closeOnExec ||| noFollow)

                if descriptor < 0 then
                    Error "learning-installed-file-unavailable"
                else
                    use handle = new SafeFileHandle(nativeint descriptor, true)

                    match identity handle with
                    | Error reason -> Error reason
                    | Ok before when
                        before.FileType <> regularFile
                        || before.LinkCount <> 1u
                        || before.OwnerUid <> expectedUid
                        || not (Set.contains before.Mode allowedModes)
                        || before.Size < 1UL
                        || before.Size > uint64 maximumBytes
                        ->
                        Error "learning-installed-file-custody-refused"
                    | Ok before ->
                        use stream = new FileStream(handle, FileAccess.Read, 65536, false)
                        let bytes = Array.zeroCreate<byte>(int before.Size)
                        let mutable offset = 0

                        while offset < bytes.Length do
                            let count = stream.Read(bytes, offset, bytes.Length - offset)

                            if count = 0 then
                                offset <- bytes.Length + 1
                            else
                                offset <- offset + count

                        if offset <> bytes.Length then
                            Error "learning-installed-file-truncated"
                        else
                            match identity handle with
                            | Ok after when after = before -> Ok bytes
                            | _ -> Error "learning-installed-file-changed"
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? ArgumentException
        | :? NotSupportedException -> Error "learning-installed-file-unavailable"

    let readExecutable maximumBytes expectedUid path =
        readPrivate maximumBytes expectedUid (set [ 0o500us; 0o700us; 0o555us; 0o755us ]) path

    let hashExecutable maximumBytes expectedUid (token: CancellationToken) path =
        try
            if not (canonicalAbsolutePath path) then
                Error "learning-installed-path-refused"
            else
                let descriptor = openFile (path, readOnly ||| closeOnExec ||| noFollow)

                if descriptor < 0 then
                    Error "learning-installed-file-unavailable"
                else
                    use handle = new SafeFileHandle(nativeint descriptor, true)

                    match executableObservation handle with
                    | Error reason -> Error reason
                    | Ok before when
                        before.Identity.FileType <> regularFile
                        || before.Identity.LinkCount <> 1u
                        || before.Identity.OwnerUid <> expectedUid
                        || not (
                            Set.contains
                                before.Identity.Mode
                                (set [ 0o500us; 0o700us; 0o555us; 0o755us ])
                        )
                        || before.Identity.Size < 1UL
                        || before.Identity.Size > uint64 maximumBytes
                        ->
                        Error "learning-installed-file-custody-refused"
                    | Ok before ->
                        use stream = new FileStream(handle, FileAccess.Read, 1048576, false)
                        use digest = IncrementalHash.CreateHash HashAlgorithmName.SHA256
                        let buffer = Array.zeroCreate<byte> 1048576
                        let mutable remaining = before.Identity.Size
                        let mutable failure = None

                        while remaining > 0UL && Option.isNone failure do
                            if token.IsCancellationRequested then
                                failure <- Some "learning-installed-readiness-cancelled"
                            else
                                let requested = int (min remaining (uint64 buffer.Length))
                                let count = stream.Read(buffer, 0, requested)

                                if count = 0 then
                                    failure <- Some "learning-installed-file-truncated"
                                else
                                    digest.AppendData(buffer, 0, count)
                                    remaining <- remaining - uint64 count

                        match failure with
                        | Some reason -> Error reason
                        | None ->
                            match executableObservation handle with
                            | Ok after when after = before ->
                                let currentDescriptor = openFile (path, readOnly ||| closeOnExec ||| noFollow)

                                if currentDescriptor < 0 then
                                    Error "learning-installed-file-changed"
                                else
                                    use currentHandle = new SafeFileHandle(nativeint currentDescriptor, true)

                                    match executableObservation currentHandle with
                                    | Ok current when current = before ->
                                        digest.GetHashAndReset()
                                        |> Convert.ToHexString
                                        |> _.ToLowerInvariant()
                                        |> Ok
                                    | _ -> Error "learning-installed-file-changed"
                            | _ -> Error "learning-installed-file-changed"
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? ArgumentException
        | :? NotSupportedException -> Error "learning-installed-file-unavailable"

    let validatePrivateDirectory expectedUid allowedModes path =
        try
            if not (canonicalAbsolutePath path) then
                Error "learning-installed-path-refused"
            else
                let descriptor =
                    openFile (path, readOnly ||| directory ||| closeOnExec ||| noFollow)

                if descriptor < 0 then
                    Error "learning-installed-directory-unavailable"
                else
                    use handle = new SafeFileHandle(nativeint descriptor, true)

                    match identity handle with
                    | Ok value when
                        value.FileType = directoryFile
                        && value.OwnerUid = expectedUid
                        && Set.contains value.Mode allowedModes
                        ->
                        Ok()
                    | _ -> Error "learning-installed-directory-custody-refused"
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? ArgumentException
        | :? NotSupportedException -> Error "learning-installed-directory-unavailable"

[<RequireQualifiedAccess>]
module private ClosedJson =
    let parse maximumBytes (bytes: byte array) =
        try
            if isNull bytes || bytes.Length < 1 || bytes.Length > maximumBytes then
                Error "learning-installed-json-size-refused"
            else
                let document =
                    JsonDocument.Parse(ReadOnlyMemory bytes, JsonDocumentOptions(MaxDepth = 32))

                Ok document
        with :? JsonException ->
            Error "learning-installed-json-shape-refused"

    let exactProperties (expected: Set<string>) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            false
        else
            let properties = element.EnumerateObject() |> Seq.map _.Name |> Seq.toArray
            properties.Length = expected.Count && Set.ofArray properties = expected

    let string (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String ->
            let result = value.GetString()
            if isNull result then None else Some result
        | _ -> None

    let int64 (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value ->
            match value.TryGetInt64() with
            | true, result -> Some result
            | _ -> None
        | _ -> None

    let uint32 (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value ->
            match value.TryGetUInt32() with
            | true, result -> Some result
            | _ -> None
        | _ -> None

    let bool (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False ->
            Some(value.GetBoolean())
        | _ -> None

    let timestamp (name: string) (element: JsonElement) =
        match string name element with
        | Some value ->
            match DateTimeOffset.TryParse value with
            | true, result when result.Offset = TimeSpan.Zero -> Some result
            | _ -> None
        | None -> None

[<RequireQualifiedAccess>]
module private LearningInstalledReadinessCodec =
    let sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let lowercaseSha value =
        not (String.IsNullOrEmpty value)
        && value.Length = 64
        && value |> Seq.forall Char.IsAsciiHexDigitLower

    let boundedIdentity value =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 128
        && Char.IsLetterOrDigit value[0]
        && value
           |> Seq.forall (fun character -> Char.IsLetterOrDigit character || "._-".Contains character)

    let fixedEvidencePath root name =
        if
            String.IsNullOrWhiteSpace root
            || not (Path.IsPathFullyQualified root)
            || Path.GetFullPath root <> root
        then
            None
        else
            Some(Path.Combine(root, name))

type LearningInstalledReadinessSource
    (
        clock: TimeProvider,
        options: LearningInstalledReadinessOptions,
        producerReceipts: ILearningInstalledProducerReceiptSource
    ) =

    let privateModes = set [ 0o400us; 0o600us ]

    let readPrivate bound path =
        LinuxFiles.readPrivate bound options.ExpectedOwnerUid privateModes path

    let sha = LearningInstalledReadinessCodec.sha256

    let parseConfig bytes =
        match ClosedJson.parse 1048576 bytes with
        | Error reason -> Error reason
        | Ok document ->
            use document = document
            let root = document.RootElement

            let expected =
                set
                    [
                        "Schema"
                        "ListenUrl"
                        "CertificatePath"
                        "CertificatePasswordFile"
                        "ServiceLockPath"
                        "Stores"
                        "Credentials"
                        "BrowserPrincipals"
                        "BrowserSession"
                    ]

            if
                not (ClosedJson.exactProperties expected root)
                || ClosedJson.string "Schema" root <> Some "fsgg.telemetry.host-config/2"
            then
                Error "learning-installed-config-refused"
            else
                match root.TryGetProperty "Credentials" with
                | false, _ -> Error "learning-installed-grant-refused"
                | true, credentials when credentials.ValueKind = JsonValueKind.Array ->
                    let rows = credentials.EnumerateArray() |> Seq.toArray

                    let matches =
                        rows
                        |> Array.filter (fun row ->
                            ClosedJson.exactProperties
                                (set
                                    [
                                        "Reference"
                                        "SecretFile"
                                        "WorkspaceId"
                                        "ProducerId"
                                        "StreamId"
                                        "Role"
                                        "GrantId"
                                        "GrantGeneration"
                                        "Revoked"
                                    ])
                                row
                            && ClosedJson.string "Role" row = Some "native-collector")

                    if matches.Length <> 1 then
                        Error "learning-installed-grant-refused"
                    else
                        let row = matches[0]

                        match
                            ClosedJson.string "Reference" row,
                            ClosedJson.string "WorkspaceId" row,
                            ClosedJson.string "ProducerId" row,
                            ClosedJson.string "StreamId" row,
                            ClosedJson.string "GrantId" row,
                            ClosedJson.int64 "GrantGeneration" row,
                            ClosedJson.bool "Revoked" row
                        with
                        | Some reference,
                          Some workspace,
                          Some producer,
                          Some stream,
                          Some grant,
                          Some generation,
                          Some false when
                            generation > 0L
                            && [ reference; workspace; producer; stream; grant ]
                               |> List.forall LearningInstalledReadinessCodec.boundedIdentity
                            ->
                            Ok(reference, workspace, producer, stream, grant, generation)
                        | _ -> Error "learning-installed-grant-refused"
                | _ -> Error "learning-installed-grant-refused"

    let parseSidecar bytes =
        match ClosedJson.parse 65536 bytes with
        | Error reason -> Error reason
        | Ok document ->
            use document = document
            let root = document.RootElement

            let expected =
                set
                    [
                        "Schema"
                        "CredentialReference"
                        "ExecutablePath"
                        "CodexHome"
                        "EvidenceRoot"
                        "Provider"
                        "Model"
                        "Effort"
                        "ExecutableSha256"
                    ]

            if
                not (ClosedJson.exactProperties expected root)
                || ClosedJson.string "Schema" root
                   <> Some "fsgg.telemetry.native-collector-installation/2"
            then
                Error "learning-installed-sidecar-refused"
            else
                match
                    ClosedJson.string "CredentialReference" root,
                    ClosedJson.string "ExecutablePath" root,
                    ClosedJson.string "CodexHome" root,
                    ClosedJson.string "EvidenceRoot" root,
                    ClosedJson.string "Provider" root,
                    ClosedJson.string "Model" root,
                    ClosedJson.string "Effort" root,
                    ClosedJson.string "ExecutableSha256" root
                with
                | Some reference,
                  Some executable,
                  Some sourceRoot,
                  Some evidenceRoot,
                  Some provider,
                  Some model,
                  Some effort,
                  Some executableSha when LearningInstalledReadinessCodec.lowercaseSha executableSha ->
                    Ok(reference, executable, sourceRoot, evidenceRoot, provider, model, effort, executableSha)
                | _ -> Error "learning-installed-sidecar-refused"

    let parseManagerReceipt bytes =
        match ClosedJson.parse 65536 bytes with
        | Error reason -> Error reason
        | Ok document ->
            use document = document
            let root = document.RootElement

            let expected =
                set
                    [
                        "schema"
                        "status"
                        "ownerUid"
                        "hostConfigSha256"
                        "sidecarSha256"
                        "executableSha256"
                        "credentialReference"
                        "workspaceId"
                        "producerId"
                        "streamId"
                        "grantId"
                        "grantGeneration"
                        "sourceVerification"
                        "snapshotOrigin"
                        "sharedCostCompleteness"
                        "activationAuthorized"
                    ]

            if
                not (ClosedJson.exactProperties expected root)
                || ClosedJson.string "schema" root
                   <> Some "fsgg.telemetry.native-collector-installation-receipt/2"
                || ClosedJson.string "status" root <> Some "installed"
                || ClosedJson.bool "activationAuthorized" root <> Some false
            then
                Error "learning-installed-manager-receipt-refused"
            else
                Ok(root.Clone())

    let parseSourceReference bytes sourceRoot =
        match ClosedJson.parse 65536 bytes with
        | Error reason -> Error reason
        | Ok document ->
            use document = document
            let root = document.RootElement

            let expected =
                set
                    [
                        "schema"
                        "profileSha256"
                        "nativeSourceVolume"
                        "developmentTarget"
                        "collectorReadOnlyTarget"
                        "readerProfileSha256"
                        "captureQualified"
                    ]

            if
                not (ClosedJson.exactProperties expected root)
                || ClosedJson.string "schema" root
                   <> Some "fsgg.telemetry.persistent-source-references/2"
                || ClosedJson.string "collectorReadOnlyTarget" root <> Some sourceRoot
                || ClosedJson.bool "captureQualified" root <> Some true
            then
                Error "learning-installed-source-reference-refused"
            else
                match ClosedJson.string "readerProfileSha256" root with
                | Some value when LearningInstalledReadinessCodec.lowercaseSha value -> Ok value
                | _ -> Error "learning-installed-source-reference-refused"

    let parseCapabilityProfile bytes =
        match ClosedJson.parse 65536 bytes with
        | Error reason -> Error reason
        | Ok document ->
            use document = document
            let root = document.RootElement

            let expected =
                set
                    [
                        "schema"
                        "operation"
                        "revision"
                        "hostExecutableSha256"
                        "providerExecutable"
                        "providerExecutableSha256"
                        "expectedAdapterVersion"
                        "expectedCodexVersion"
                        "environmentAllowList"
                        "credentialScope"
                        "maximumRuntimeSeconds"
                        "maximumStreamBytes"
                        "expiresAt"
                        "disposableWorkspace"
                        "cleanup"
                    ]

            if
                not (ClosedJson.exactProperties expected root)
                || ClosedJson.string "schema" root
                   <> Some FixedNativeCapabilityDiagnostic.profileSchema
                || ClosedJson.string "operation" root
                   <> Some FixedNativeCapabilityDiagnostic.operation
            then
                Error "learning-installed-capability-profile-refused"
            else
                Ok(root.Clone())

    let parseCapabilityResult bytes =
        match ClosedJson.parse 65536 bytes with
        | Error reason -> Error reason
        | Ok document ->
            use document = document
            let root = document.RootElement

            let expected =
                set
                    [
                        "schema"
                        "operation"
                        "profileRevision"
                        "profileSha256"
                        "hostExecutableSha256"
                        "providerExecutableSha256"
                        "adapterVersion"
                        "credentialScope"
                        "environmentAllowList"
                        "maximumRuntimeSeconds"
                        "maximumStreamBytes"
                        "requestedModel"
                        "requestedEffort"
                        "startedAt"
                        "completedAt"
                        "disposition"
                        "detail"
                        "authenticationState"
                        "authenticationProvenance"
                        "evidenceSchema"
                        "evidenceProvenance"
                        "evidenceObservedAt"
                        "evidenceExpiresAt"
                        "modelSessionStarts"
                        "cleanup"
                    ]

            if
                not (ClosedJson.exactProperties expected root)
                || ClosedJson.string "schema" root
                   <> Some FixedNativeCapabilityDiagnostic.resultSchema
                || ClosedJson.string "operation" root
                   <> Some FixedNativeCapabilityDiagnostic.operation
            then
                Error "learning-installed-capability-result-refused"
            else
                Ok(root.Clone())

    let parseCapture bytes =
        match ClosedJson.parse (64 * 1024 * 1024) bytes with
        | Error reason -> Error reason
        | Ok document ->
            use document = document
            let root = document.RootElement

            let expected =
                set
                    [
                        "schema"
                        "outcome"
                        "rootThreadId"
                        "limits"
                        "initialExchanges"
                        "confirmationExchanges"
                        "rollouts"
                        "projection"
                        "captureDigest"
                    ]

            if
                not (ClosedJson.exactProperties expected root)
                || ClosedJson.string "schema" root <> Some "fsgg.learn.native-source-capture/1"
                || ClosedJson.string "outcome" root
                   <> Some "native-census-and-usage-reconciled-at-capture"
            then
                Error "learning-installed-native-capture-refused"
            else
                match root.TryGetProperty "projection" with
                | true, projection when ClosedJson.exactProperties (set [ "threads"; "turnUsage" ]) projection ->
                    Ok(root.Clone(), projection.Clone())
                | _ -> Error "learning-installed-native-capture-refused"

    let parseVerification bytes =
        match ClosedJson.parse 1048576 bytes with
        | Error reason -> Error reason
        | Ok document ->
            use document = document
            let root = document.RootElement

            let expected =
                set
                    [
                        "schema"
                        "status"
                        "outcome"
                        "captureDigest"
                        "missingDescendants"
                        "foreignDescendants"
                        "mismatchedThreads"
                        "missingTurns"
                        "foreignTurns"
                        "missingUsage"
                        "foreignUsage"
                        "mismatchedUsage"
                    ]

            if
                not (ClosedJson.exactProperties expected root)
                || ClosedJson.string "schema" root
                   <> Some "fsgg.learn.native-source-verification/1"
                || ClosedJson.string "status" root <> Some "verified"
                || ClosedJson.string "outcome" root
                   <> Some "native-census-and-usage-reconciled-at-capture"
            then
                Error "learning-installed-native-verification-refused"
            else
                Ok(root.Clone())

    let read (query: LearningSelectionQuery) (token: CancellationToken) =
        task {
            if not options.Enabled then
                return Error "learning-installed-readiness-disabled"
            elif
                options.MaximumCapabilityAge <= TimeSpan.Zero
                || LinuxFiles.effectiveUserId () <> options.ExpectedOwnerUid
            then
                return Error "learning-installed-reader-identity-refused"
            elif token.IsCancellationRequested then
                return Error "learning-installed-readiness-cancelled"
            else
                let sidecarPath = options.HostConfigPath + ".native-collector.json"
                let managerReceiptPath = options.HostConfigPath + ".native-collector.receipt.json"

                let sourceReferencePath =
                    Path.Combine(Path.GetDirectoryName options.HostConfigPath, "source-reference.json")

                match
                    readPrivate 1048576 options.HostConfigPath,
                    readPrivate 65536 sidecarPath,
                    readPrivate 65536 managerReceiptPath
                with
                | Ok configBytes, Ok sidecarBytes, Ok managerReceiptBytes ->
                    match
                        parseConfig configBytes, parseSidecar sidecarBytes, parseManagerReceipt managerReceiptBytes
                    with
                    | Ok(reference, workspace, producer, stream, grant, generation),
                      Ok(sidecarReference, executable, sourceRoot, evidenceRoot, provider, model, effort, executableSha),
                      Ok managerReceipt ->
                        let receiptMatches =
                            sidecarReference = reference
                            && ClosedJson.uint32 "ownerUid" managerReceipt = Some options.ExpectedOwnerUid
                            && ClosedJson.string "hostConfigSha256" managerReceipt = Some(sha configBytes)
                            && ClosedJson.string "sidecarSha256" managerReceipt = Some(sha sidecarBytes)
                            && ClosedJson.string "executableSha256" managerReceipt = Some executableSha
                            && ClosedJson.string "credentialReference" managerReceipt = Some reference
                            && ClosedJson.string "workspaceId" managerReceipt = Some workspace
                            && ClosedJson.string "producerId" managerReceipt = Some producer
                            && ClosedJson.string "streamId" managerReceipt = Some stream
                            && ClosedJson.string "grantId" managerReceipt = Some grant
                            && ClosedJson.int64 "grantGeneration" managerReceipt = Some generation

                        if not receiptMatches then
                            return Error "learning-installed-manager-receipt-refused"
                        elif
                            query.Provider.Provider <> provider
                            || query.Requested
                               <> {
                                      Model = Some model
                                      Effort = Some effort
                                  }
                            || query.Executable.Path <> executable
                            || query.Executable.Sha256 <> Some executableSha
                        then
                            return Error "learning-installed-selection-refused"
                        else
                            match
                                LinuxFiles.hashExecutable
                                    (512 * 1024 * 1024)
                                    options.ExpectedExecutableOwnerUid
                                    token
                                    executable
                            with
                            | Error reason -> return Error reason
                            | Ok observedExecutableSha when observedExecutableSha <> executableSha ->
                                return Error "learning-installed-executable-changed"
                            | Ok _ ->
                                match readPrivate 65536 sourceReferencePath with
                                | Error reason -> return Error reason
                                | Ok sourceReferenceBytes ->
                                    match parseSourceReference sourceReferenceBytes sourceRoot with
                                    | Error reason -> return Error reason
                                    | Ok expectedProfileSha ->
                                        let paths =
                                            [
                                                "fixed-native-capability-profile.json", 65536
                                                "fixed-native-capability-result.json", 65536
                                                "native-source-capture.json", 64 * 1024 * 1024
                                                "native-source-verification.json", 1048576
                                            ]
                                            |> List.map (fun (name, bound) ->
                                                LearningInstalledReadinessCodec.fixedEvidencePath evidenceRoot name
                                                |> Option.map (fun path -> readPrivate bound path))

                                        match
                                            LinuxFiles.validatePrivateDirectory
                                                options.ExpectedOwnerUid
                                                (set [ 0o500us; 0o700us ])
                                                sourceRoot,
                                            LinuxFiles.validatePrivateDirectory
                                                options.ExpectedOwnerUid
                                                (set [ 0o700us ])
                                                evidenceRoot,
                                            paths
                                        with
                                        | Ok(),
                                          Ok(),
                                          [ Some(Ok profileBytes)
                                            Some(Ok resultBytes)
                                            Some(Ok captureBytes)
                                            Some(Ok verificationBytes) ] ->
                                            match
                                                parseCapabilityProfile profileBytes,
                                                parseCapabilityResult resultBytes,
                                                parseCapture captureBytes,
                                                parseVerification verificationBytes
                                            with
                                            | Ok profile, Ok result, Ok(capture, projection), Ok verification ->
                                                let profileSha = sha profileBytes
                                                let resultSha = sha resultBytes
                                                let captureSha = sha captureBytes
                                                let verificationSha = sha verificationBytes
                                                let captureDigest = ClosedJson.string "captureDigest" capture
                                                let observedAt = ClosedJson.timestamp "evidenceObservedAt" result
                                                let expiresAt = ClosedJson.timestamp "evidenceExpiresAt" result

                                                let cleanupValid =
                                                    match result.TryGetProperty "cleanup" with
                                                    | true, cleanup ->
                                                        ClosedJson.exactProperties
                                                            (set
                                                                [
                                                                    "processTreeTerminationRequired"
                                                                    "processTreeTerminated"
                                                                    "workspaceRemovalAttempted"
                                                                    "workspaceRemoved"
                                                                ])
                                                            cleanup
                                                        && ClosedJson.bool "processTreeTerminationRequired" cleanup =
                                                            Some true
                                                        && ClosedJson.bool "processTreeTerminated" cleanup = Some true
                                                        && ClosedJson.bool "workspaceRemovalAttempted" cleanup =
                                                            Some true
                                                        && ClosedJson.bool "workspaceRemoved" cleanup = Some true
                                                    | _ -> false

                                                let profileContractValid =
                                                    match
                                                        FixedNativeCapabilityDiagnostic.parseProfile
                                                            (clock.GetUtcNow())
                                                            profileBytes
                                                    with
                                                    | Ok _ -> true
                                                    | Error _ -> false

                                                let profileMatches =
                                                    profileContractValid
                                                    && profileSha = expectedProfileSha
                                                    && ClosedJson.string "providerExecutable" profile = Some executable
                                                    && ClosedJson.string "providerExecutableSha256" profile =
                                                        Some executableSha
                                                    && ClosedJson.string "expectedAdapterVersion" profile =
                                                        Some query.Provider.AdapterVersion
                                                    && ClosedJson.string "expectedCodexVersion" profile =
                                                        query.Executable.Version
                                                    && ClosedJson.string "credentialScope" profile =
                                                        Some FixedNativeCapabilityDiagnostic.credentialScope

                                                let resultMatches =
                                                    ClosedJson.string "profileRevision" result =
                                                        ClosedJson.string "revision" profile
                                                    && ClosedJson.string "profileSha256" result = Some profileSha
                                                    && ClosedJson.string "hostExecutableSha256" result =
                                                        ClosedJson.string "hostExecutableSha256" profile
                                                    && ClosedJson.string "providerExecutableSha256" result =
                                                        Some executableSha
                                                    && ClosedJson.string "adapterVersion" result =
                                                        Some query.Provider.AdapterVersion
                                                    && ClosedJson.string "credentialScope" result =
                                                        Some FixedNativeCapabilityDiagnostic.credentialScope
                                                    && ClosedJson.string "requestedModel" result = Some model
                                                    && ClosedJson.string "requestedEffort" result = Some effort
                                                    && ClosedJson.string "disposition" result =
                                                        Some "advertised-supported"
                                                    && ClosedJson.string "authenticationState" result =
                                                        Some "authenticated"
                                                    && ClosedJson.string "evidenceSchema" result =
                                                        Some LearningSelectionEvidence.schema
                                                    && ClosedJson.int64 "modelSessionStarts" result = Some 0L
                                                    && cleanupValid

                                                let captureMatches =
                                                    captureDigest
                                                    |> Option.exists LearningInstalledReadinessCodec.lowercaseSha
                                                    && ClosedJson.string "captureDigest" verification = captureDigest
                                                    && match projection.TryGetProperty "threads" with
                                                       | true, threads when threads.ValueKind = JsonValueKind.Array ->
                                                           threads.EnumerateArray()
                                                           |> Seq.exists (fun thread ->
                                                               ClosedJson.string "provider" thread = Some provider
                                                               && ClosedJson.string "model" thread = Some model
                                                               && ClosedJson.string "effort" thread = Some effort)
                                                       | _ -> false

                                                match observedAt, expiresAt with
                                                | Some observed, Some expires when
                                                    profileMatches
                                                    && resultMatches
                                                    && captureMatches
                                                    && observed <= clock.GetUtcNow()
                                                    && expires > observed
                                                    && clock.GetUtcNow() < expires
                                                    && clock.GetUtcNow() - observed <= options.MaximumCapabilityAge
                                                    ->
                                                    let receiptQuery =
                                                        {
                                                            WorkspaceId = workspace
                                                            ProducerId = producer
                                                            StreamId = stream
                                                            Role = "native-collector"
                                                            GrantId = grant
                                                            GrantGeneration = generation
                                                            ManagerReceiptSha256 = sha managerReceiptBytes
                                                            CapabilityProfileSha256 = profileSha
                                                            CapabilityResultSha256 = resultSha
                                                            NativeCaptureSha256 = captureSha
                                                            NativeVerificationSha256 = verificationSha
                                                        }

                                                    let! producerReceipt =
                                                        producerReceipts.ReadLearningInstalledProducerReceipt(
                                                            receiptQuery,
                                                            token
                                                        )

                                                    match producerReceipt with
                                                    | Error _ ->
                                                        return Error "learning-installed-producer-receipt-unavailable"
                                                    | Ok receipt when
                                                        receipt.WorkspaceId = receiptQuery.WorkspaceId
                                                        && receipt.ProducerId = receiptQuery.ProducerId
                                                        && receipt.StreamId = receiptQuery.StreamId
                                                        && receipt.Role = receiptQuery.Role
                                                        && receipt.GrantId = receiptQuery.GrantId
                                                        && receipt.GrantGeneration = receiptQuery.GrantGeneration
                                                        && receipt.ManagerReceiptSha256 =
                                                            receiptQuery.ManagerReceiptSha256
                                                        && receipt.CapabilityProfileSha256 =
                                                            receiptQuery.CapabilityProfileSha256
                                                        && receipt.CapabilityResultSha256 =
                                                            receiptQuery.CapabilityResultSha256
                                                        && receipt.NativeCaptureSha256 =
                                                            receiptQuery.NativeCaptureSha256
                                                        && receipt.NativeVerificationSha256 =
                                                            receiptQuery.NativeVerificationSha256
                                                        && receipt.CapabilityObservedAt = observed
                                                        && receipt.CapabilityExpiresAt = expires
                                                        && receipt.Source.ProducerId = producer
                                                        && receipt.Source.ObservedAt = observed
                                                        && LearningInstalledReadinessCodec.boundedIdentity
                                                            receipt.Source.ProducerId
                                                        && LearningInstalledReadinessCodec.boundedIdentity
                                                            receipt.Source.Revision
                                                        && LearningInstalledReadinessCodec.boundedIdentity
                                                            receipt.Source.RecordId
                                                        ->
                                                        let revision = receipt.Source.Revision

                                                        let identity digest =
                                                            { receipt.Source with
                                                                Revision = revision
                                                                RecordId = digest
                                                            }

                                                        let evidence =
                                                            {
                                                                Schema = LearningSelectionEvidence.schema
                                                                Provider = query.Provider
                                                                Executable = query.Executable
                                                                Requested = query.Requested
                                                                Status = LearningCapabilityStatus.Supported
                                                                Provenance =
                                                                    ClosedJson.string "evidenceProvenance" result
                                                                    |> Option.defaultValue ""
                                                                ObservedAt = observed
                                                                ExpiresAt = expires
                                                            }

                                                        match
                                                            LearningSelectionEvidence.authorize
                                                                (clock.GetUtcNow())
                                                                query
                                                                evidence
                                                        with
                                                        | Error _ ->
                                                            return Error "learning-installed-capability-refused"
                                                        | Ok() ->
                                                            return
                                                                Ok
                                                                    {
                                                                        InstalledCustody =
                                                                            identity receiptQuery.ManagerReceiptSha256
                                                                        ProviderCapability =
                                                                            identity receiptQuery.CapabilityResultSha256
                                                                        NativeCapture =
                                                                            identity
                                                                                receiptQuery.NativeVerificationSha256
                                                                        CapabilityEvidence = evidence
                                                                        NativeSelection =
                                                                            {
                                                                                Provider = Some provider
                                                                                Model = Some model
                                                                                Effort = Some effort
                                                                                Backend = None
                                                                            }
                                                                        ExpiresAt = expires
                                                                    }
                                                    | Ok _ -> return Error "learning-installed-producer-receipt-refused"
                                                | _ -> return Error "learning-installed-capability-refused"
                                            | _ -> return Error "learning-installed-evidence-refused"
                                        | _ -> return Error "learning-installed-evidence-unavailable"
                    | _ -> return Error "learning-installed-custody-refused"
                | _ -> return Error "learning-installed-custody-unavailable"
        }

    interface ILearningInstalledReadinessSource with
        member _.ReadLearningInstalledReadiness(query, token) = read query token
