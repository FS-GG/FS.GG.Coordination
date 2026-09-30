namespace FS.GG.Coordination.Orchestration.Host

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Serialization

[<CLIMutable>]
type FixedQualificationProfile =
    {
        Schema: string
        Operation: string
        Revision: string
        HostExecutableSha256: string
        RunnerExecutable: string
        RunnerExecutableSha256: string
        ProviderExecutable: string
        ProviderExecutableSha256: string
        ExpectedRunnerProtocol: string
        ExpectedAdapterVersion: string
        ExpectedCodexVersion: string
        EnvironmentAllowList: string array
        CredentialScope: string
        MaximumRuntimeSeconds: int
        ExpiresAt: DateTimeOffset
        DisposableWorkspace: string
        Cleanup: string
    }

[<CLIMutable>]
type FixedQualificationCleanup =
    {
        ProcessTreeTerminationRequired: bool
        ProcessTreeTerminated: bool
        WorkspaceRemovalAttempted: bool
        WorkspaceRemoved: bool
    }

[<CLIMutable>]
type FixedQualificationResult =
    {
        Schema: string
        Operation: string
        ProfileRevision: string
        ProfileSha256: string
        HostExecutableSha256: string
        RunnerExecutableSha256: string
        ProviderExecutableSha256: string
        RunnerProtocol: string
        AdapterVersion: string
        CredentialScope: string
        EnvironmentAllowList: string array
        MaximumRuntimeSeconds: int
        StartedAt: DateTimeOffset
        CompletedAt: DateTimeOffset
        Disposition: string
        Detail: string
        Diagnostic: ServedCompatibilityDiagnostic
        Cleanup: FixedQualificationCleanup
    }

[<RequireQualifiedAccess>]
module FixedQualificationOperation =
    let profileSchema = "fsgg.orchestration.host-fixed-qualification/1"
    let resultSchema = "fsgg.orchestration.host-fixed-qualification-result/1"
    let operation = "executor-compatibility/1"
    let runnerProtocol = "fsgg.orchestration.compatibility-diagnostic-response/1"
    let adapterVersion = "codex-subscription-exec/1"
    let cleanupKind = "delete-owned-workspace/1"
    let credentialScope = "codex-subscription-login-status-read-only"

    let private options =
        let value =
            JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 8)

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

    let private exactProperties (bytes: byte array) =
        let expected =
            set
                [
                    "schema"
                    "operation"
                    "revision"
                    "hostExecutableSha256"
                    "runnerExecutable"
                    "runnerExecutableSha256"
                    "providerExecutable"
                    "providerExecutableSha256"
                    "expectedRunnerProtocol"
                    "expectedAdapterVersion"
                    "expectedCodexVersion"
                    "environmentAllowList"
                    "credentialScope"
                    "maximumRuntimeSeconds"
                    "expiresAt"
                    "disposableWorkspace"
                    "cleanup"
                ]

        try
            use document =
                JsonDocument.Parse(ReadOnlyMemory bytes, JsonDocumentOptions(MaxDepth = 8))

            let properties =
                document.RootElement.EnumerateObject() |> Seq.map _.Name |> Seq.toArray

            document.RootElement.ValueKind = JsonValueKind.Object
            && properties.Length = expected.Count
            && Set.ofArray properties = expected
        with :? JsonException ->
            false

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

    let private validateProfile now (profile: FixedQualificationProfile) =
        let allowList =
            if isNull profile.EnvironmentAllowList then
                Array.empty
            else
                profile.EnvironmentAllowList

        let allowed = HostConfiguration.compatibilityDiagnosticEnvironmentAllowList

        if profile.Schema <> profileSchema || profile.Operation <> operation then
            Error "fixed-profile-operation-refused"
        elif
            String.IsNullOrWhiteSpace profile.Revision
            || profile.Revision <> profile.Revision.Trim()
        then
            Error "fixed-profile-revision-refused"
        elif
            not (
                lowercaseSha profile.HostExecutableSha256
                && lowercaseSha profile.RunnerExecutableSha256
                && lowercaseSha profile.ProviderExecutableSha256
            )
        then
            Error "fixed-profile-pin-refused"
        elif
            not (
                fixedPath profile.RunnerExecutable
                && fixedPath profile.ProviderExecutable
                && fixedPath profile.DisposableWorkspace
            )
        then
            Error "fixed-profile-path-refused"
        elif
            profile.ExpectedRunnerProtocol <> runnerProtocol
            || profile.ExpectedAdapterVersion <> adapterVersion
        then
            Error "fixed-profile-protocol-refused"
        elif not (exactVersion profile.ExpectedCodexVersion) then
            Error "fixed-profile-version-refused"
        elif profile.CredentialScope <> credentialScope then
            Error "fixed-profile-credential-scope-refused"
        elif profile.Cleanup <> cleanupKind then
            Error "fixed-profile-cleanup-refused"
        elif profile.MaximumRuntimeSeconds < 1 || profile.MaximumRuntimeSeconds > 30 then
            Error "fixed-profile-runtime-refused"
        elif profile.ExpiresAt <= now then
            Error "fixed-profile-expired"
        elif
            allowList.Length = 0
            || allowList.Length <> (allowList |> Array.distinct |> Array.length)
            || allowList |> Array.exists (allowed.Contains >> not)
        then
            Error "fixed-profile-environment-refused"
        elif
            Directory.Exists profile.DisposableWorkspace
            || File.Exists profile.DisposableWorkspace
        then
            Error "fixed-profile-workspace-not-disposable"
        elif
            not (Directory.Exists(Path.GetDirectoryName profile.DisposableWorkspace))
            || not (noReparsePoint (Path.GetDirectoryName profile.DisposableWorkspace))
        then
            Error "fixed-profile-workspace-parent-refused"
        else
            Ok(profile, Set.ofArray allowList)

    let parseProfile (now: DateTimeOffset) (bytes: byte array) =
        try
            if
                isNull bytes
                || bytes.Length < 1
                || bytes.Length > 64 * 1024
                || not (exactProperties bytes)
            then
                Error "fixed-profile-shape-refused"
            else
                let profile =
                    JsonSerializer.Deserialize<FixedQualificationProfile>(ReadOnlySpan bytes, options)

                if isNull (box profile) then
                    Error "fixed-profile-shape-refused"
                else
                    validateProfile now profile
        with
        | :? JsonException
        | :? NotSupportedException
        | :? ArgumentException -> Error "fixed-profile-shape-refused"

    let private emptyCleanup =
        {
            ProcessTreeTerminationRequired = false
            ProcessTreeTerminated = true
            WorkspaceRemovalAttempted = false
            WorkspaceRemoved = true
        }

    let private refusal (profileDigest: string) (started: DateTimeOffset) (detail: string) =
        {
            Schema = resultSchema
            Operation = operation
            ProfileRevision = ""
            ProfileSha256 = profileDigest
            HostExecutableSha256 = ""
            RunnerExecutableSha256 = ""
            ProviderExecutableSha256 = ""
            RunnerProtocol = runnerProtocol
            AdapterVersion = adapterVersion
            CredentialScope = credentialScope
            EnvironmentAllowList = Array.empty
            MaximumRuntimeSeconds = 0
            StartedAt = started
            CompletedAt = DateTimeOffset.UtcNow
            Disposition = "refused"
            Detail = detail
            Diagnostic = Unchecked.defaultof<_>
            Cleanup = emptyCleanup
        }

    let execute (profileBytes: byte array) =
        task {
            let started = DateTimeOffset.UtcNow
            let profileDigest = if isNull profileBytes then "" else sha256Bytes profileBytes

            match parseProfile started profileBytes with
            | Error reason -> return refusal profileDigest started reason
            | Ok(profile, allowList) ->
                let hostHash = Environment.ProcessPath |> Option.ofObj |> Option.bind sha256File

                let preflight =
                    if hostHash <> Some profile.HostExecutableSha256 then
                        Error "fixed-profile-host-pin-refused"
                    elif sha256File profile.RunnerExecutable <> Some profile.RunnerExecutableSha256 then
                        Error "fixed-profile-runner-pin-refused"
                    elif sha256File profile.ProviderExecutable <> Some profile.ProviderExecutableSha256 then
                        Error "fixed-profile-provider-pin-refused"
                    else
                        Ok()

                match preflight with
                | Error reason ->
                    return
                        { refusal profileDigest started reason with
                            ProfileRevision = profile.Revision
                            HostExecutableSha256 = profile.HostExecutableSha256
                            RunnerExecutableSha256 = profile.RunnerExecutableSha256
                            ProviderExecutableSha256 = profile.ProviderExecutableSha256
                            EnvironmentAllowList = profile.EnvironmentAllowList
                            MaximumRuntimeSeconds = profile.MaximumRuntimeSeconds
                        }
                | Ok() ->
                    let mutable diagnostic = Unchecked.defaultof<ServedCompatibilityDiagnostic>
                    let mutable detail = "fixed-qualification-unavailable"
                    let mutable passed = false
                    let mutable processTerminated = false

                    try
                        Directory.CreateDirectory profile.DisposableWorkspace |> ignore

                        File.SetUnixFileMode(
                            profile.DisposableWorkspace,
                            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                        )

                        let! outcome =
                            ServedCompatibilityDiagnostic.run
                                {
                                    RunnerExecutable = profile.RunnerExecutable
                                    RunnerSha256 = profile.RunnerExecutableSha256
                                    ProviderExecutable = profile.ProviderExecutable
                                    ProviderSha256 = profile.ProviderExecutableSha256
                                    WorkingDirectory = profile.DisposableWorkspace
                                    ExpectedCodexVersion = profile.ExpectedCodexVersion
                                    Timeout = TimeSpan.FromSeconds(float profile.MaximumRuntimeSeconds)
                                    EnvironmentAllowList = allowList
                                }

                        processTerminated <- outcome <> Error "compatibility-process-cleanup-refused"

                        match outcome with
                        | Error reason -> detail <- reason
                        | Ok value when value.AdapterVersion <> profile.ExpectedAdapterVersion ->
                            detail <- "fixed-qualification-adapter-refused"
                        | Ok value when value.VersionState <> "matched" ->
                            detail <- "fixed-qualification-version-mismatch"
                        | Ok value when value.AuthenticationState <> "authenticated" ->
                            detail <- "fixed-qualification-authentication-refused"
                        | Ok value when not value.SupportsResume -> detail <- "fixed-qualification-resume-refused"
                        | Ok value ->
                            diagnostic <- value
                            detail <- "passed"
                            passed <- true
                    with _ ->
                        detail <- "fixed-qualification-unavailable"

                    let mutable removed = false

                    try
                        if Directory.Exists profile.DisposableWorkspace then
                            Directory.Delete(profile.DisposableWorkspace, true)

                        removed <-
                            not (
                                Directory.Exists profile.DisposableWorkspace
                                || File.Exists profile.DisposableWorkspace
                            )
                    with _ ->
                        removed <- false

                    let cleanup =
                        {
                            ProcessTreeTerminationRequired = true
                            ProcessTreeTerminated = processTerminated
                            WorkspaceRemovalAttempted = true
                            WorkspaceRemoved = removed
                        }

                    let disposition, finalDetail =
                        if not removed || not processTerminated then
                            "refused", "fixed-qualification-cleanup-refused"
                        elif passed then
                            "passed", detail
                        else
                            "refused", detail

                    return
                        {
                            Schema = resultSchema
                            Operation = operation
                            ProfileRevision = profile.Revision
                            ProfileSha256 = profileDigest
                            HostExecutableSha256 = profile.HostExecutableSha256
                            RunnerExecutableSha256 = profile.RunnerExecutableSha256
                            ProviderExecutableSha256 = profile.ProviderExecutableSha256
                            RunnerProtocol = profile.ExpectedRunnerProtocol
                            AdapterVersion = profile.ExpectedAdapterVersion
                            CredentialScope = profile.CredentialScope
                            EnvironmentAllowList = profile.EnvironmentAllowList
                            MaximumRuntimeSeconds = profile.MaximumRuntimeSeconds
                            StartedAt = started
                            CompletedAt = DateTimeOffset.UtcNow
                            Disposition = disposition
                            Detail = finalDetail
                            Diagnostic = diagnostic
                            Cleanup = cleanup
                        }
        }

    let readAndExecute path =
        task {
            try
                let info = FileInfo path

                if
                    not info.Exists
                    || info.Length < 1L
                    || info.Length > 65536L
                    || not (noReparsePoint path)
                then
                    return! execute null
                else
                    let! bytes = File.ReadAllBytesAsync path
                    return! execute bytes
            with _ ->
                return! execute null
        }

    let serialize result =
        JsonSerializer.SerializeToUtf8Bytes(result, options)
