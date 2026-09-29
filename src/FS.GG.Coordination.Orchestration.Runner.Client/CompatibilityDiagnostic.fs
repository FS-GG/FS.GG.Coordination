namespace FS.GG.Coordination.Orchestration.Runner.Client

open System
open System.Buffers.Binary
open System.IO
open System.Security.Cryptography
open System.Threading
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Execution.Codex
open FS.GG.Coordination.Orchestration.Runner.Protocol

[<RequireQualifiedAccess>]
module CompatibilityDiagnostic =
    let private sha256 path =
        try
            use stream = File.OpenRead path
            SHA256.HashData stream |> Convert.ToHexString |> _.ToLowerInvariant() |> Some
        with _ ->
            None

    let run
        (executable: string)
        (expectedSha: string)
        (expectedVersion: string)
        (timeout: TimeSpan)
        (input: Stream)
        (output: Stream)
        =
        task {
            if sha256 executable <> Some expectedSha then
                return Error "compatibility-provider-pin-refused"
            else
                use deadline = new CancellationTokenSource(timeout)

                try
                    let header = Array.zeroCreate<byte> 4
                    do! input.ReadExactlyAsync(header, deadline.Token)
                    let size = BinaryPrimitives.ReadInt32BigEndian header

                    if size < 1 || size > CompatibilityDiagnosticWire.maximumBytes then
                        return Error "compatibility-diagnostic-frame-refused"
                    else
                        let bytes = Array.zeroCreate<byte> size
                        do! input.ReadExactlyAsync(bytes, deadline.Token)
                        let extra = Array.zeroCreate<byte> 1
                        let! extraCount = input.ReadAsync(extra.AsMemory(), deadline.Token)

                        if extraCount <> 0 then
                            return Error "compatibility-diagnostic-extra-frame-refused"
                        else
                            match CompatibilityDiagnosticWire.parseRequest bytes with
                            | Error reason -> return Error reason
                            | Ok request ->
                                let! observed =
                                    CodexReadinessProbe.observe
                                        {
                                            Executable = executable
                                            ExpectedVersion = "codex-cli " + expectedVersion
                                            StartupTimeout = timeout
                                            EnvironmentAllowList = CodexReadinessProbe.environmentAllowList
                                        }
                                        TimeProvider.System
                                        deadline.Token

                                let after = sha256 executable

                                if after <> Some expectedSha then
                                    return Error "compatibility-provider-changed"
                                else
                                    let authenticationState, authenticationProvenance =
                                        match observed.Authentication with
                                        | Authenticated provenance -> "authenticated", provenance
                                        | NotAuthenticated provenance -> "not-authenticated", provenance
                                        | AuthenticationUnknown provenance -> "unknown", provenance

                                    let response =
                                        {
                                            Schema = CompatibilityDiagnosticWire.responseSchema
                                            CorrelationId = request.CorrelationId
                                            Scope = CompatibilityDiagnosticWire.scope
                                            Provider = observed.Identity.Provider
                                            AdapterVersion = observed.Identity.AdapterVersion
                                            VersionState = observed.VersionState
                                            ObservedVersion = defaultArg observed.ObservedVersion ""
                                            AuthenticationState = authenticationState
                                            AuthenticationProvenance = authenticationProvenance
                                            SupportsResume = observed.SupportsResume
                                            ObservedAt = observed.ObservedAt
                                            ProviderExecutableSha256 = after.Value
                                        }

                                    let encoded = CompatibilityDiagnosticWire.encodeResponse response
                                    let responseHeader = Array.zeroCreate<byte> 4
                                    BinaryPrimitives.WriteInt32BigEndian(responseHeader, encoded.Length)
                                    do! output.WriteAsync(responseHeader, deadline.Token)
                                    do! output.WriteAsync(encoded, deadline.Token)
                                    do! output.FlushAsync(deadline.Token)
                                    return Ok()
                with
                | :? OperationCanceledException -> return Error "compatibility-diagnostic-timeout"
                | :? EndOfStreamException -> return Error "compatibility-diagnostic-truncated-frame"
                | _ -> return Error "compatibility-diagnostic-unavailable"
        }
