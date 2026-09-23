namespace FS.GG.Coordination.GitHub

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

type GenesisAuthorizationIntent =
    {
        SourceCommit: GitObjectId
        SourceTree: GitObjectId
        WorkflowRevision: GitObjectId
        WorkflowSha256: Sha256Digest
    }

type GenesisSignature =
    {
        KeyId: string
        PublicKeyPem: string
        ProtectedRunId: int64
        AuthorizedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        Signature: byte array
    }

type private VerifiedSignatureData =
    {
        IntentSha256: Sha256Digest
        ProtectedRunId: int64
    }

type VerifiedGenesisSignature = private VerifiedGenesisSignature of VerifiedSignatureData

[<RequireQualifiedAccess>]
module V1AdmissionGenesisAuthorization =
    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private json (fields: (string * JsonNode) list) =
        let value = JsonObject()
        fields |> List.iter (fun (name, entry) -> value.Add(name, entry))

        value.ToJsonString(JsonSerializerOptions(WriteIndented = false))
        |> ShardedJournalAdapter.canonicalJson
        |> Result.defaultWith invalidOp

    let private iso (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)

    let canonicalIntent plan intent =
        let objects = V1AdmissionRegistry.genesisObjects plan
        let commit = V1AdmissionRegistry.genesisCommit plan

        json
            [
                "authorityCommit", JsonValue.Create(V1AdmissionRegistry.genesisAuthorityCommit plan |> V1AdmissionRegistry.gitObjectIdValue)
                "eventOid", JsonValue.Create(V1AdmissionRegistry.gitObjectIdValue objects.EventObjectId)
                "eventSha256", JsonValue.Create(sha256 objects.EventBytes)
                "genesisCommitOid", JsonValue.Create(commit.CommitOid)
                "genesisCommitSha256", JsonValue.Create(sha256 objects.CommitBytes)
                "headOid", JsonValue.Create(V1AdmissionRegistry.gitObjectIdValue objects.HeadObjectId)
                "headSha256", JsonValue.Create(sha256 objects.HeadBytes)
                "manifestSha256", JsonValue.Create(V1AdmissionRegistry.genesisManifest plan |> V1AdmissionRegistry.sha256Value)
                "operationId", JsonValue.Create(commit.OperationId)
                "ref", JsonValue.Create((V1AdmissionRegistry.genesisAddress plan).Ref)
                "repository", JsonValue.Create("FS-GG/FS.GG.Coordination.Authority")
                "repositoryId", JsonValue.Create(1351660651L)
                "schema", JsonValue.Create("fsgg.github-substrate.v1-admission-genesis-intent/1")
                "sourceCommit", JsonValue.Create(V1AdmissionRegistry.gitObjectIdValue intent.SourceCommit)
                "sourceTree", JsonValue.Create(V1AdmissionRegistry.gitObjectIdValue intent.SourceTree)
                "treeOid", JsonValue.Create(V1AdmissionRegistry.gitObjectIdValue objects.TreeObjectId)
                "treeSha256", JsonValue.Create(sha256 objects.TreeBytes)
                "trustAnchorSha256", JsonValue.Create(V1AdmissionRegistry.genesisTrustDigest plan |> V1AdmissionRegistry.sha256Value)
                "workflowRevision", JsonValue.Create(V1AdmissionRegistry.gitObjectIdValue intent.WorkflowRevision)
                "workflowSha256", JsonValue.Create(V1AdmissionRegistry.sha256Value intent.WorkflowSha256)
            ]

    let canonicalSignaturePayload plan intent signature =
        json
            [
                "authorizedAt", JsonValue.Create(iso signature.AuthorizedAt)
                "expiresAt", JsonValue.Create(iso signature.ExpiresAt)
                "intentSha256", JsonValue.Create(sha256 (canonicalIntent plan intent))
                "keyId", JsonValue.Create(signature.KeyId)
                "protectedRunId", JsonValue.Create(signature.ProtectedRunId)
                "schema", JsonValue.Create("fsgg.github-substrate.v1-admission-genesis-signature/1")
            ]

    let private exactProperties expected (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then
            false
        else
            let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            names.Length = (names |> List.distinct |> List.length)
            && Set.ofList names = Set.ofList expected

    let decodeEnvelope plan intent (raw: ReadOnlyMemory<byte>) =
        try
            if raw.Length = 0 || raw.Length > 8192 then
                Error [ "genesis-signature-envelope-size" ]
            else
                let _ = UTF8Encoding(false, true).GetString(raw.Span)
                use document = JsonDocument.Parse raw
                let root = document.RootElement
                let fields =
                    [ "schema"; "intentSha256"; "keyId"; "protectedRunId"
                      "authorizedAt"; "expiresAt"; "publicKeyPem"; "signatureBase64" ]
                if not (exactProperties fields root) then
                    Error [ "genesis-signature-envelope-shape" ]
                else
                    let value (name: string) = root.GetProperty(name).GetString()
                    let authorizedAt =
                        DateTimeOffset.ParseExact(value "authorizedAt", "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    let expiresAt =
                        DateTimeOffset.ParseExact(value "expiresAt", "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    let keyId = value "keyId"
                    let publicKeyPem = value "publicKeyPem"
                    let encoded = value "signatureBase64"
                    let signature = Convert.FromBase64String encoded
                    let expectedIntent = sha256 (canonicalIntent plan intent)
                    let pemLines = publicKeyPem.TrimEnd('\n').Split('\n')
                    if value "schema" <> "fsgg.github-substrate.v1-admission-genesis-signature-envelope/1"
                       || value "intentSha256" <> expectedIntent
                       || String.IsNullOrWhiteSpace keyId
                       || keyId.Length > 128
                       || (keyId |> Seq.exists (fun ch -> ch < '!' || ch > '~'))
                       || root.GetProperty("protectedRunId").GetInt64() < 1L
                       || iso authorizedAt <> value "authorizedAt"
                       || iso expiresAt <> value "expiresAt"
                       || publicKeyPem.Length > 2048
                       || pemLines.Length < 3
                       || pemLines[0] <> "-----BEGIN PUBLIC KEY-----"
                       || pemLines[pemLines.Length - 1] <> "-----END PUBLIC KEY-----"
                       || signature.Length < 256
                       || signature.Length > 512
                       || Convert.ToBase64String(signature) <> encoded then
                        Error [ "genesis-signature-envelope-binding" ]
                    else
                        use rsa = RSA.Create()
                        rsa.ImportFromPem publicKeyPem
                        if rsa.KeySize / 8 <> signature.Length then
                            Error [ "genesis-signature-envelope-binding" ]
                        else
                            Ok
                                { KeyId = keyId
                                  PublicKeyPem = publicKeyPem
                                  ProtectedRunId = root.GetProperty("protectedRunId").GetInt64()
                                  AuthorizedAt = authorizedAt
                                  ExpiresAt = expiresAt
                                  Signature = signature }
        with _ ->
            Error [ "genesis-signature-envelope-invalid" ]

    let private trustSigner plan (trustAnchorBytes: byte array) =
        try
            if obj.ReferenceEquals(trustAnchorBytes, null) || trustAnchorBytes.Length > 8192 then
                Error [ "genesis-trust-bytes" ]
            elif sha256 trustAnchorBytes <> (V1AdmissionRegistry.genesisTrustDigest plan |> V1AdmissionRegistry.sha256Value) then
                Error [ "genesis-trust-digest" ]
            else
                let utf8 = UTF8Encoding(false, true)
                let content = utf8.GetString trustAnchorBytes
                let expected =
                    ShardedJournalAdapter.canonicalJson content
                    |> Result.defaultWith invalidOp
                    |> fun bytes -> Array.append bytes [| 10uy |]

                if expected <> trustAnchorBytes then
                    Error [ "genesis-trust-canonical" ]
                else
                    use document = JsonDocument.Parse trustAnchorBytes
                    let root = document.RootElement
                    let authorizer = root.GetProperty("authorizer")

                    if not (exactProperties [ "schema"; "manifestSha256"; "acceptedGenesisReceiptDigest"; "authorizer" ] root)
                       || not (exactProperties [ "keyId"; "algorithm"; "publicKeySpkiSha256" ] authorizer)
                       || root.GetProperty("schema").GetString() <> "fsgg.github-ledger-initial-trust/1"
                       || root.GetProperty("manifestSha256").GetString()
                          <> (V1AdmissionRegistry.genesisManifest plan |> V1AdmissionRegistry.sha256Value)
                       || authorizer.GetProperty("algorithm").GetString() <> "RSA-PSS-SHA256" then
                        Error [ "genesis-trust-shape" ]
                    else
                        match
                            V1AdmissionRegistry.sha256Digest (root.GetProperty("acceptedGenesisReceiptDigest").GetString()),
                            V1AdmissionRegistry.sha256Digest (authorizer.GetProperty("publicKeySpkiSha256").GetString())
                        with
                        | Ok _, Ok _ ->
                            Ok(authorizer.GetProperty("keyId").GetString(), authorizer.GetProperty("publicKeySpkiSha256").GetString())
                        | _ -> Error [ "genesis-trust-shape" ]
        with _ ->
            Error [ "genesis-trust-invalid" ]

    let verify asOf trustAnchorBytes plan intent signature =
        trustSigner plan trustAnchorBytes
        |> Result.bind (fun (keyId, expectedSpki) ->
            let errors =
                [
                    if String.IsNullOrWhiteSpace keyId || signature.KeyId <> keyId then
                        "genesis-signer-key-id"
                    if signature.ProtectedRunId < 1L then
                        "genesis-protected-run-id"
                    if signature.AuthorizedAt > asOf
                       || asOf >= signature.ExpiresAt
                       || signature.ExpiresAt - signature.AuthorizedAt > TimeSpan.FromHours 2.
                       || signature.ExpiresAt <= signature.AuthorizedAt then
                        "genesis-signature-expiry"
                    if obj.ReferenceEquals(signature.Signature, null) || signature.Signature.Length = 0 then
                        "genesis-signature-missing"
                ]

            if not errors.IsEmpty then
                Error errors
            else
                try
                    use rsa = RSA.Create()
                    rsa.ImportFromPem(signature.PublicKeyPem)

                    if sha256 (rsa.ExportSubjectPublicKeyInfo()) <> expectedSpki then
                        Error [ "genesis-signer-trust-mismatch" ]
                    elif
                        not (
                            rsa.VerifyData(
                                canonicalSignaturePayload plan intent signature,
                                signature.Signature,
                                HashAlgorithmName.SHA256,
                                RSASignaturePadding.Pss
                            )
                        )
                    then
                        Error [ "genesis-signature-invalid" ]
                    else
                        canonicalIntent plan intent
                        |> sha256
                        |> V1AdmissionRegistry.sha256Digest
                        |> Result.mapError List.singleton
                        |> Result.map (fun digest ->
                            VerifiedGenesisSignature { IntentSha256 = digest; ProtectedRunId = signature.ProtectedRunId })
                with _ ->
                    Error [ "genesis-signature-invalid" ])

    let intentSha256 (VerifiedGenesisSignature value) = value.IntentSha256
    let protectedRunId (VerifiedGenesisSignature value) = value.ProtectedRunId
