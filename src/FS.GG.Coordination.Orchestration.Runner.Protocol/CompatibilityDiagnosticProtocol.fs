namespace FS.GG.Coordination.Orchestration.Runner.Protocol

open System
open System.Text.Json
open System.Text.Json.Serialization

[<CLIMutable>]
type CompatibilityDiagnosticRequest = { Schema: string; CorrelationId: Guid }

[<CLIMutable>]
type CompatibilityDiagnosticResponse =
    {
        Schema: string
        CorrelationId: Guid
        Scope: string
        Provider: string
        AdapterVersion: string
        VersionState: string
        ObservedVersion: string
        AuthenticationState: string
        AuthenticationProvenance: string
        SupportsResume: bool
        ObservedAt: DateTimeOffset
        ProviderExecutableSha256: string
    }

[<RequireQualifiedAccess>]
module CompatibilityDiagnosticWire =
    let requestSchema = "fsgg.orchestration.compatibility-diagnostic-request/1"
    let responseSchema = "fsgg.orchestration.compatibility-diagnostic-response/1"
    let scope = "compatibility-diagnostic-only"
    let provider = "Codex"
    let adapterVersion = "codex-subscription-exec/1"
    let maximumBytes = 16 * 1024

    let private options =
        let value =
            JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 8)

        value.PropertyNameCaseInsensitive <- false
        value.UnmappedMemberHandling <- JsonUnmappedMemberHandling.Disallow
        value.DefaultIgnoreCondition <- JsonIgnoreCondition.Never
        value

    let private exactProperties expected (bytes: byte array) =
        try
            use document =
                JsonDocument.Parse(ReadOnlyMemory bytes, JsonDocumentOptions(MaxDepth = 8))

            if document.RootElement.ValueKind <> JsonValueKind.Object then
                false
            else
                let properties =
                    document.RootElement.EnumerateObject() |> Seq.map _.Name |> Seq.toArray

                properties.Length = Set.count expected && Set.ofArray properties = expected
        with :? JsonException ->
            false

    let encodeRequest value =
        JsonSerializer.SerializeToUtf8Bytes(value, options)

    let encodeResponse value =
        JsonSerializer.SerializeToUtf8Bytes(value, options)

    let parseRequest (bytes: byte array) =
        try
            if
                isNull bytes
                || bytes.Length < 1
                || bytes.Length > maximumBytes
                || not (exactProperties (set [ "schema"; "correlationId" ]) bytes)
            then
                Error "compatibility-diagnostic-request-refused"
            else
                let value =
                    JsonSerializer.Deserialize<CompatibilityDiagnosticRequest>(ReadOnlySpan bytes, options)

                if
                    isNull (box value)
                    || value.Schema <> requestSchema
                    || value.CorrelationId = Guid.Empty
                then
                    Error "compatibility-diagnostic-request-refused"
                else
                    Ok value
        with
        | :? JsonException
        | :? NotSupportedException -> Error "compatibility-diagnostic-request-refused"

    let parseResponse (bytes: byte array) =
        try
            let expected =
                set
                    [
                        "schema"
                        "correlationId"
                        "scope"
                        "provider"
                        "adapterVersion"
                        "versionState"
                        "observedVersion"
                        "authenticationState"
                        "authenticationProvenance"
                        "supportsResume"
                        "observedAt"
                        "providerExecutableSha256"
                    ]

            if
                isNull bytes
                || bytes.Length < 1
                || bytes.Length > maximumBytes
                || not (exactProperties expected bytes)
            then
                Error "compatibility-diagnostic-response-refused"
            else
                let value =
                    JsonSerializer.Deserialize<CompatibilityDiagnosticResponse>(ReadOnlySpan bytes, options)

                let textValid value =
                    not (String.IsNullOrWhiteSpace value) && value = value.Trim()

                if
                    isNull (box value)
                    || value.Schema <> responseSchema
                    || value.CorrelationId = Guid.Empty
                    || value.Scope <> scope
                    || value.Provider <> provider
                    || value.AdapterVersion <> adapterVersion
                    || not (Set.contains value.VersionState (set [ "matched"; "mismatch"; "unavailable" ]))
                    || not (
                        Set.contains value.AuthenticationState (set [ "authenticated"; "not-authenticated"; "unknown" ])
                    )
                    || not (textValid value.AuthenticationProvenance)
                    || (value.ObservedVersion <> "" && not (textValid value.ObservedVersion))
                    || value.ObservedAt = DateTimeOffset.MinValue
                    || isNull value.ProviderExecutableSha256
                    || value.ProviderExecutableSha256.Length <> 64
                    || (value.ProviderExecutableSha256 |> Seq.exists (Char.IsAsciiHexDigitLower >> not))
                then
                    Error "compatibility-diagnostic-response-refused"
                else
                    Ok value
        with
        | :? JsonException
        | :? NotSupportedException -> Error "compatibility-diagnostic-response-refused"
