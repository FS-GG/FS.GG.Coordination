namespace FS.GG.Coordination.Orchestration.Host

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Execution.Codex

[<CLIMutable>]
type FixedNativeCapabilityProfile =
    {
        Schema: string
        Operation: string
        Revision: string
        HostExecutableSha256: string
        ProviderExecutable: string
        ProviderExecutableSha256: string
        ExpectedAdapterVersion: string
        ExpectedCodexVersion: string
        EnvironmentAllowList: string array
        CredentialScope: string
        MaximumRuntimeSeconds: int
        MaximumStreamBytes: int
        ExpiresAt: DateTimeOffset
        DisposableWorkspace: string
        Cleanup: string
    }

[<CLIMutable>]
type FixedNativeCapabilityCleanup =
    {
        ProcessTreeTerminationRequired: bool
        ProcessTreeTerminated: bool
        WorkspaceRemovalAttempted: bool
        WorkspaceRemoved: bool
    }

[<CLIMutable>]
type FixedNativeCapabilityResult =
    {
        Schema: string
        Operation: string
        ProfileRevision: string
        ProfileSha256: string
        HostExecutableSha256: string
        ProviderExecutableSha256: string
        AdapterVersion: string
        CredentialScope: string
        EnvironmentAllowList: string array
        MaximumRuntimeSeconds: int
        MaximumStreamBytes: int
        RequestedModel: string
        RequestedEffort: string
        StartedAt: DateTimeOffset
        CompletedAt: DateTimeOffset
        Disposition: string
        Detail: string
        AuthenticationState: string
        AuthenticationProvenance: string
        EvidenceSchema: string
        EvidenceProvenance: string
        EvidenceObservedAt: Nullable<DateTimeOffset>
        EvidenceExpiresAt: Nullable<DateTimeOffset>
        ModelSessionStarts: int
        Cleanup: FixedNativeCapabilityCleanup
    }

type private ClosedCapabilityInput() =
    interface ICodexExecutionInput with
        member _.ReadUtf8(_, _) =
            Task.FromResult(Error "fixed-native-capability-input-forbidden")

type private ClosedCapabilityCandidateInspector() =
    interface ICodexCandidateInspector with
        member _.CandidateId = Guid.Empty

        member _.Verify(_, _, _) =
            Task.FromResult(Error "fixed-native-capability-candidate-forbidden")

        member _.CreateCandidate(_, _) =
            Task.FromResult(Error "fixed-native-capability-candidate-forbidden")

[<RequireQualifiedAccess>]
module FixedNativeCapabilityDiagnostic =
    let profileSchema = "fsgg.orchestration.host-fixed-native-capability/1"
    let resultSchema = "fsgg.orchestration.host-fixed-native-capability-result/1"
    let operation = "codex-native-capability/1"
    let adapterVersion = "codex-subscription-exec/1"
    let credentialScope = "codex-native-current-account-read-only"
    let cleanupKind = "delete-owned-workspace/1"
    let requestedModel = "gpt-5.6-sol"
    let requestedEffort = "medium"

    let private options =
        let value = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 8)
        value.PropertyNameCaseInsensitive <- false
        value.UnmappedMemberHandling <- JsonUnmappedMemberHandling.Disallow
        value.DefaultIgnoreCondition <- JsonIgnoreCondition.Never
        value

    let private sha256Bytes (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private sha256File (path: string) =
        try
            use stream = File.OpenRead path
            Some(SHA256.HashData stream |> Convert.ToHexString |> _.ToLowerInvariant())
        with _ ->
            None

    let private lowercaseSha (value: string) =
        not (String.IsNullOrEmpty value)
        && value.Length = 64
        && value |> Seq.forall Char.IsAsciiHexDigitLower

    let private fixedPath (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && Path.IsPathFullyQualified value
        && Path.GetFullPath value = value

    let private noReparsePoint (path: string) =
        try
            not (File.GetAttributes(path).HasFlag FileAttributes.ReparsePoint)
        with _ ->
            false

    let private exactVersion (value: string) =
        let components = if isNull value then Array.empty else value.Split('.')
        components.Length = 3
        && components
           |> Array.forall (fun part ->
               part.Length > 0
               && (part = "0" || not (part.StartsWith '0'))
               && part |> Seq.forall Char.IsAsciiDigit)

    let private exactProperties (bytes: byte array) =
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

        try
            use document = JsonDocument.Parse(ReadOnlyMemory bytes, JsonDocumentOptions(MaxDepth = 8))
            let properties = document.RootElement.EnumerateObject() |> Seq.map _.Name |> Seq.toArray
            document.RootElement.ValueKind = JsonValueKind.Object
            && properties.Length = expected.Count
            && Set.ofArray properties = expected
        with :? JsonException ->
            false

    let private validateProfile now (profile: FixedNativeCapabilityProfile) =
        let allowList =
            if isNull profile.EnvironmentAllowList then Array.empty else profile.EnvironmentAllowList

        if profile.Schema <> profileSchema || profile.Operation <> operation then
            Error "fixed-native-capability-operation-refused"
        elif String.IsNullOrWhiteSpace profile.Revision || profile.Revision <> profile.Revision.Trim() then
            Error "fixed-native-capability-revision-refused"
        elif not (lowercaseSha profile.HostExecutableSha256 && lowercaseSha profile.ProviderExecutableSha256) then
            Error "fixed-native-capability-pin-refused"
        elif not (fixedPath profile.ProviderExecutable && fixedPath profile.DisposableWorkspace) then
            Error "fixed-native-capability-path-refused"
        elif profile.ExpectedAdapterVersion <> adapterVersion || not (exactVersion profile.ExpectedCodexVersion) then
            Error "fixed-native-capability-version-refused"
        elif profile.CredentialScope <> credentialScope then
            Error "fixed-native-capability-credential-scope-refused"
        elif profile.Cleanup <> cleanupKind then
            Error "fixed-native-capability-cleanup-refused"
        elif profile.MaximumRuntimeSeconds < 1 || profile.MaximumRuntimeSeconds > 30 then
            Error "fixed-native-capability-runtime-refused"
        elif profile.MaximumStreamBytes < 4096 || profile.MaximumStreamBytes > 8 * 1024 * 1024 then
            Error "fixed-native-capability-byte-limit-refused"
        elif profile.ExpiresAt <= now then
            Error "fixed-native-capability-profile-expired"
        elif
            allowList.Length = 0
            || allowList.Length <> (allowList |> Array.distinct |> Array.length)
            || allowList |> Array.exists (CodexReadinessProbe.environmentAllowList.Contains >> not)
        then
            Error "fixed-native-capability-environment-refused"
        elif Directory.Exists profile.DisposableWorkspace || File.Exists profile.DisposableWorkspace then
            Error "fixed-native-capability-workspace-not-disposable"
        elif
            not (Directory.Exists(Path.GetDirectoryName profile.DisposableWorkspace))
            || not (noReparsePoint (Path.GetDirectoryName profile.DisposableWorkspace))
        then
            Error "fixed-native-capability-workspace-parent-refused"
        else
            Ok(profile, Set.ofArray allowList)

    let parseProfile now (bytes: byte array) =
        try
            if isNull bytes || bytes.Length < 1 || bytes.Length > 65536 || not (exactProperties bytes) then
                Error "fixed-native-capability-profile-shape-refused"
            else
                JsonSerializer.Deserialize<FixedNativeCapabilityProfile>(bytes, options)
                |> validateProfile now
        with
        | :? JsonException
        | :? ArgumentException -> Error "fixed-native-capability-profile-shape-refused"

    let private emptyCleanup =
        {
            ProcessTreeTerminationRequired = false
            ProcessTreeTerminated = false
            WorkspaceRemovalAttempted = false
            WorkspaceRemoved = false
        }

    let private refusal digest started detail =
        {
            Schema = resultSchema
            Operation = operation
            ProfileRevision = ""
            ProfileSha256 = digest
            HostExecutableSha256 = ""
            ProviderExecutableSha256 = ""
            AdapterVersion = adapterVersion
            CredentialScope = credentialScope
            EnvironmentAllowList = Array.empty
            MaximumRuntimeSeconds = 0
            MaximumStreamBytes = 0
            RequestedModel = requestedModel
            RequestedEffort = requestedEffort
            StartedAt = started
            CompletedAt = DateTimeOffset.UtcNow
            Disposition = "refused"
            Detail = detail
            AuthenticationState = "unavailable"
            AuthenticationProvenance = ""
            EvidenceSchema = ""
            EvidenceProvenance = ""
            EvidenceObservedAt = Nullable()
            EvidenceExpiresAt = Nullable()
            ModelSessionStarts = 0
            Cleanup = emptyCleanup
        }

    let execute profileBytes =
        task {
            let started = DateTimeOffset.UtcNow
            let digest = if isNull profileBytes then "" else sha256Bytes profileBytes

            match parseProfile started profileBytes with
            | Error reason -> return refusal digest started reason
            | Ok(profile, allowList) ->
                let hostHash = Environment.ProcessPath |> Option.ofObj |> Option.bind sha256File
                let preflight =
                    if hostHash <> Some profile.HostExecutableSha256 then Error "fixed-native-capability-host-pin-refused"
                    elif sha256File profile.ProviderExecutable <> Some profile.ProviderExecutableSha256 then
                        Error "fixed-native-capability-provider-pin-refused"
                    else Ok()

                match preflight with
                | Error reason ->
                    return
                        { refusal digest started reason with
                            ProfileRevision = profile.Revision
                            HostExecutableSha256 = profile.HostExecutableSha256
                            ProviderExecutableSha256 = profile.ProviderExecutableSha256
                            EnvironmentAllowList = profile.EnvironmentAllowList
                            MaximumRuntimeSeconds = profile.MaximumRuntimeSeconds
                            MaximumStreamBytes = profile.MaximumStreamBytes
                        }
                | Ok() ->
                    let mutable disposition = "unavailable"
                    let mutable detail = "fixed-native-capability-unavailable"
                    let mutable authenticationState = "unavailable"
                    let mutable authenticationProvenance = ""
                    let mutable evidence: LearningSelectionEvidence option = None
                    let mutable processTerminated = false

                    try
                        Directory.CreateDirectory profile.DisposableWorkspace |> ignore
                        File.SetUnixFileMode(
                            profile.DisposableWorkspace,
                            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                        )

                        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds(float profile.MaximumRuntimeSeconds))
                        let providerOptions =
                            { CodexExecutionProviderOptions.create profile.ProviderExecutable profile.DisposableWorkspace with
                                ExpectedVersion = "codex-cli " + profile.ExpectedCodexVersion
                                MaximumStreamBytes = profile.MaximumStreamBytes
                                StartupTimeout = TimeSpan.FromSeconds(float profile.MaximumRuntimeSeconds)
                                EnvironmentAllowList = allowList }

                        let provider =
                            CodexExecution.provider
                                providerOptions
                                (ClosedCapabilityInput())
                                (ClosedCapabilityCandidateInspector())
                                TimeProvider.System

                        let! readiness = provider.ObserveReadiness deadline.Token

                        match readiness.Authentication with
                        | Authenticated provenance ->
                            authenticationState <- "authenticated"
                            authenticationProvenance <- provenance
                            let learning = provider :?> ILearningExecutionProvider
                            let! observed =
                                learning.ObserveLearningSelection(
                                    { Model = Some requestedModel; Effort = Some requestedEffort },
                                    deadline.Token
                                )
                            evidence <- Some observed

                            match observed.Status with
                            | LearningCapabilityStatus.Supported ->
                                disposition <- "advertised-supported"
                                detail <- "requested-selection-advertised"
                            | LearningCapabilityStatus.Unsupported code ->
                                disposition <- "advertised-unsupported"
                                detail <- code
                            | LearningCapabilityStatus.Unknown code ->
                                disposition <- "unavailable"
                                detail <-
                                    if code = "codex-model-list-cancelled" && deadline.IsCancellationRequested then
                                        "codex-model-list-deadline-exceeded"
                                    else
                                        code
                        | NotAuthenticated provenance ->
                            authenticationState <- "not-authenticated"
                            authenticationProvenance <- provenance
                            disposition <- "unavailable-authentication"
                            detail <- "codex-authentication-not-authenticated"
                        | AuthenticationUnknown provenance ->
                            authenticationState <- "unknown"
                            authenticationProvenance <- provenance
                            disposition <- "unavailable-authentication"
                            detail <- "codex-authentication-unknown"

                        processTerminated <- true
                    with
                    | :? OperationCanceledException ->
                        detail <- "codex-model-list-deadline-exceeded"
                        processTerminated <- true
                    | _ ->
                        detail <- "fixed-native-capability-unavailable"
                        processTerminated <- true

                    if sha256File profile.ProviderExecutable <> Some profile.ProviderExecutableSha256 then
                        disposition <- "unavailable"
                        detail <- "codex-model-list-executable-changed"

                    let mutable removed = false
                    try
                        if Directory.Exists profile.DisposableWorkspace then
                            Directory.Delete(profile.DisposableWorkspace, true)
                        removed <- not (Directory.Exists profile.DisposableWorkspace || File.Exists profile.DisposableWorkspace)
                    with _ ->
                        removed <- false

                    let cleanup =
                        {
                            ProcessTreeTerminationRequired = true
                            ProcessTreeTerminated = processTerminated
                            WorkspaceRemovalAttempted = true
                            WorkspaceRemoved = removed
                        }

                    if not removed || not processTerminated then
                        disposition <- "refused"
                        detail <- "fixed-native-capability-cleanup-refused"

                    let observedAt, expiresAt, evidenceSchema, evidenceProvenance =
                        match evidence with
                        | Some value -> Nullable value.ObservedAt, Nullable value.ExpiresAt, value.Schema, value.Provenance
                        | None -> Nullable(), Nullable(), "", ""

                    return
                        {
                            Schema = resultSchema
                            Operation = operation
                            ProfileRevision = profile.Revision
                            ProfileSha256 = digest
                            HostExecutableSha256 = profile.HostExecutableSha256
                            ProviderExecutableSha256 = profile.ProviderExecutableSha256
                            AdapterVersion = profile.ExpectedAdapterVersion
                            CredentialScope = profile.CredentialScope
                            EnvironmentAllowList = profile.EnvironmentAllowList
                            MaximumRuntimeSeconds = profile.MaximumRuntimeSeconds
                            MaximumStreamBytes = profile.MaximumStreamBytes
                            RequestedModel = requestedModel
                            RequestedEffort = requestedEffort
                            StartedAt = started
                            CompletedAt = DateTimeOffset.UtcNow
                            Disposition = disposition
                            Detail = detail
                            AuthenticationState = authenticationState
                            AuthenticationProvenance = authenticationProvenance
                            EvidenceSchema = evidenceSchema
                            EvidenceProvenance = evidenceProvenance
                            EvidenceObservedAt = observedAt
                            EvidenceExpiresAt = expiresAt
                            ModelSessionStarts = 0
                            Cleanup = cleanup
                        }
        }

    let readAndExecute path =
        task {
            try
                let info = FileInfo path
                if not info.Exists || info.Length < 1L || info.Length > 65536L || not (noReparsePoint path) then
                    return! execute null
                else
                    let! bytes = File.ReadAllBytesAsync path
                    return! execute bytes
            with _ ->
                return! execute null
        }

    let serialize result = JsonSerializer.SerializeToUtf8Bytes(result, options)
