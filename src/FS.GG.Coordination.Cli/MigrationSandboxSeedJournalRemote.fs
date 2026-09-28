namespace FS.GG.Coordination.Cli

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

type MigrationSandboxSeedInstalledS2Binding = private MigrationSandboxSeedInstalledS2Binding of byte array
type MigrationSandboxSeedInstalledJournalPolicy = private MigrationSandboxSeedInstalledJournalPolicy of byte array

[<RequireQualifiedAccess>]
type MigrationSandboxSeedRemotePushOutcome =
    | Accepted
    | ParentConflict
    | DefiniteRefusal of reason: string
    | ResponseUnknown

type MigrationSandboxSeedRemoteObject =
    {
        Kind: string
        Oid: string
        Bytes: byte array
    }

type MigrationSandboxSeedRemotePush =
    {
        RefName: string
        Refspec: string
        ForceWithLease: string
        Objects: MigrationSandboxSeedRemoteObject list
    }

type IMigrationSandboxSeedJournalRemoteTransport =
    abstract PushExact: MigrationSandboxSeedRemotePush -> MigrationSandboxSeedRemotePushOutcome
    abstract ReadFresh: refName: string -> MigrationSandboxSeedJournalRead

[<RequireQualifiedAccess>]
type MigrationSandboxSeedRemoteResult =
    | Applied of MigrationSandboxSeedJournalRestore
    | JournalRetryOnly of reason: string
    | Conflict
    | Refused of reason: string
    | Indeterminate of reason: string

[<RequireQualifiedAccess>]
type MigrationSandboxSeedRemoteFailure =
    | InvalidInstalledBinding
    | InvalidInstalledPolicy
    | InstalledBindingMismatch
    | InvalidJournalProposal

[<RequireQualifiedAccess>]
module MigrationSandboxSeedJournalRemote =
    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private hex length (value: string) =
        not (isNull value)
        && value.Length = length
        && value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f')

    let private unique (value: JsonElement) =
        let rec visit (item: JsonElement) =
            match item.ValueKind with
            | JsonValueKind.Object ->
                let names = item.EnumerateObject() |> Seq.map _.Name |> Seq.toList

                if names.Length <> (names |> Set.ofList |> Set.count) then
                    invalidOp "duplicate-json-member"

                item.EnumerateObject() |> Seq.iter (fun property -> visit property.Value)
            | JsonValueKind.Array -> item.EnumerateArray() |> Seq.iter visit
            | _ -> ()

        visit value

    let private canonical (omitFingerprint: bool) (value: JsonElement) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)

        let rec write atRoot (item: JsonElement) =
            match item.ValueKind with
            | JsonValueKind.Object ->
                writer.WriteStartObject()

                item.EnumerateObject()
                |> Seq.filter (fun property -> not (atRoot && omitFingerprint && property.Name = "fingerprint"))
                |> Seq.sortBy _.Name
                |> Seq.iter (fun property ->
                    writer.WritePropertyName property.Name
                    write false property.Value)

                writer.WriteEndObject()
            | JsonValueKind.Array ->
                writer.WriteStartArray()
                item.EnumerateArray() |> Seq.iter (write false)
                writer.WriteEndArray()
            | JsonValueKind.String -> writer.WriteStringValue(item.GetString())
            | JsonValueKind.Number -> writer.WriteRawValue(item.GetRawText())
            | JsonValueKind.True -> writer.WriteBooleanValue true
            | JsonValueKind.False -> writer.WriteBooleanValue false
            | JsonValueKind.Null -> writer.WriteNullValue()
            | _ -> invalidOp "json-kind"

        write true value
        writer.Flush()
        Array.append (stream.ToArray()) [| byte '\n' |]

    let private text (name: string) (value: JsonElement) =
        let property = value.GetProperty name

        if property.ValueKind <> JsonValueKind.String || isNull (property.GetString()) then
            invalidOp name

        property.GetString()

    let private exactNames (expected: string list) (value: JsonElement) =
        value.ValueKind = JsonValueKind.Object
        && (value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq) = Set.ofList expected

    let private installedShape (bytes: byte array) =
        try
            let utf8 = UTF8Encoding(false, true)
            let json = utf8.GetString bytes

            if not (json.EndsWith("\n", StringComparison.Ordinal)) then
                false
            else
                use document = JsonDocument.Parse bytes
                let root = document.RootElement
                unique root
                let source = root.GetProperty "source"
                let sandbox = root.GetProperty "sandbox"
                let mint = root.GetProperty "mint"
                let artifacts = root.GetProperty "artifacts"
                let journal = root.GetProperty "journal"
                let permissions = mint.GetProperty "permissions"

                let effects =
                    journal.GetProperty("allowedClosedEffectKinds").EnumerateArray()
                    |> Seq.map _.GetString()
                    |> Seq.toList

                let runId = source.GetProperty("runId").GetInt64()
                let runAttempt = source.GetProperty("runAttempt").GetInt32()
                let candidate = text "candidateSha" source
                let expectedNonce = $"{runId}-{runAttempt}-{candidate}"

                bytes = canonical false root
                && exactNames
                    [
                        "activation"
                        "artifacts"
                        "authority"
                        "fingerprint"
                        "journal"
                        "mint"
                        "sandbox"
                        "schema"
                        "schemaJoin"
                        "source"
                        "status"
                    ]
                    root
                && exactNames [ "corpus"; "seedPlan" ] artifacts
                && exactNames [ "sha256" ] (artifacts.GetProperty "corpus")
                && exactNames [ "sha256" ] (artifacts.GetProperty "seedPlan")
                && exactNames [ "allowedClosedEffectKinds"; "identity" ] journal
                && exactNames [ "appId"; "installationId"; "permissions"; "proofSha256" ] mint
                && exactNames [ "projectNodeId"; "repositoryId"; "repositoryNodeId" ] sandbox
                && exactNames
                    [
                        "candidateSha"
                        "repository"
                        "runAttempt"
                        "runId"
                        "runNonce"
                        "workflowPath"
                        "workflowRef"
                        "workflowSha"
                    ]
                    source
                && text "fingerprint" root = sha256 (canonical true root)
                && text "schema" root = "fsgg.github-substrate-v2.sandbox-seed-execution-binding/1"
                && text "status" root = "bound-write-authority"
                && root.GetProperty("activation").GetBoolean()
                && text "authority" root = "installed-protected-workflow-verified"
                && text "schemaJoin" root = "coordination-s1-final"
                && text "repository" source = "FS-GG/.github"
                && text "workflowPath" source = ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
                && text "workflowRef" source = "refs/heads/main"
                && hex 40 (text "workflowSha" source)
                && runId > 0L
                && runAttempt > 0
                && hex 40 candidate
                && text "runNonce" source = expectedNonce
                && sandbox.GetProperty("repositoryId").GetInt64() = 1353050537L
                && text "repositoryNodeId" sandbox = "R_kgDOUKXpqQ"
                && text "projectNodeId" sandbox = "PVT_kwDOEYAWY84BiESo"
                && mint.GetProperty("appId").GetInt64() = 4166418L
                && mint.GetProperty("installationId").GetInt64() = 143110413L
                && hex 64 (text "proofSha256" mint)
                && exactNames [ "contents"; "issues"; "metadata"; "organization_projects" ] permissions
                && text "contents" permissions = "write"
                && text "issues" permissions = "write"
                && text "metadata" permissions = "read"
                && text "organization_projects" permissions = "write"
                && [ "seedPlan"; "corpus" ]
                   |> List.forall (fun name -> hex 64 (text "sha256" (artifacts.GetProperty name)))
                && not (String.IsNullOrWhiteSpace(text "identity" journal))
                && effects =
                    [
                        "CreateNonceIssue"
                        "AddProjectMembership"
                        "RemoveProjectMembership"
                        "DeleteNonceIssue"
                    ]
                && hex 64 (text "fingerprint" root)
        with
        | :? JsonException
        | :? InvalidOperationException
        | :? KeyNotFoundException
        | :? FormatException
        | :? DecoderFallbackException -> false

    let authenticateInstalledS2 (bindingBytes: ReadOnlyMemory<byte>) =
        let bytes = bindingBytes.ToArray()

        if bytes.Length = 0 || bytes.Length > 1024 * 1024 || not (installedShape bytes) then
            Error MigrationSandboxSeedRemoteFailure.InvalidInstalledBinding
        else
            Ok(MigrationSandboxSeedInstalledS2Binding bytes)

    let private installedPolicyShape (bytes: byte array) =
        try
            let utf8 = UTF8Encoding(false, true)
            let json = utf8.GetString bytes

            if not (json.EndsWith("\n", StringComparison.Ordinal)) then
                false
            else
                use document = JsonDocument.Parse bytes
                let root = document.RootElement
                unique root
                let installation = root.GetProperty "installation"
                let repository = root.GetProperty "repository"
                let rulesets = root.GetProperty "rulesets"

                bytes = canonical false root
                && exactNames
                    [
                        "authority"
                        "candidateSha256"
                        "classicProtection"
                        "fingerprint"
                        "installation"
                        "installed"
                        "repository"
                        "rulesets"
                        "schema"
                        "selector"
                    ]
                    root
                && exactNames [ "actorId"; "appId"; "installationId"; "permission" ] installation
                && exactNames [ "fullName"; "id" ] repository
                && exactNames [ "integrityId"; "integritySha256"; "writerId"; "writerSha256" ] rulesets
                && text "fingerprint" root = sha256 (canonical true root)
                && text "schema" root = "fsgg.gs2-09-7.seed-journal-policy-installed/1"
                && root.GetProperty("installed").GetBoolean()
                && text "authority" root = "authenticated-protected-readback"
                && text "selector" root = "refs/heads/gs2-09-7/*/seed-journal"
                && text "classicProtection" root = "proven-absent"
                && repository.GetProperty("id").GetInt64() = 1353050537L
                && text "fullName" repository = "FS-GG/FS.GG.GitHub.Substrate.Sandbox"
                && installation.GetProperty("appId").GetInt64() = 4166418L
                && installation.GetProperty("installationId").GetInt64() = 143110413L
                && installation.GetProperty("actorId").GetInt64() = 297630107L
                && text "permission" installation = "administration:write-or-custom-role-edit-rules"
                && rulesets.GetProperty("writerId").GetInt64() > 0L
                && rulesets.GetProperty("integrityId").GetInt64() > 0L
                && hex 64 (text "writerSha256" rulesets)
                && hex 64 (text "integritySha256" rulesets)
                && hex 64 (text "candidateSha256" root)
                && hex 64 (text "fingerprint" root)
        with
        | :? JsonException
        | :? InvalidOperationException
        | :? KeyNotFoundException
        | :? FormatException
        | :? DecoderFallbackException -> false

    let authenticateInstalledPolicy (policyBytes: ReadOnlyMemory<byte>) =
        let bytes = policyBytes.ToArray()

        if
            bytes.Length = 0
            || bytes.Length > 1024 * 1024
            || not (installedPolicyShape bytes)
        then
            Error MigrationSandboxSeedRemoteFailure.InvalidInstalledPolicy
        else
            Ok(MigrationSandboxSeedInstalledJournalPolicy bytes)

    let private proposalSnapshot (proposal: MigrationSandboxSeedJournalPlan) =
        {
            RefName = proposal.RefName
            JournalGeneration = proposal.JournalGeneration
            StateGeneration = proposal.StateGeneration
            RunNonce = proposal.RunNonce
            BindingSeal = proposal.BindingSeal
            CommitOid = proposal.CommitOid
            ParentOid = proposal.ExpectedParent
            StateSha256 = proposal.StateSha256
            StateBytes = proposal.StateBytes
            BlobOid = proposal.BlobOid
            TreeOid = proposal.TreeOid
            TreeBytes = proposal.TreeBytes
            CommitBytes = proposal.CommitBytes
        }

    let private bindingMatches (bytes: byte array) (restore: MigrationSandboxSeedJournalRestore) =
        try
            use document = JsonDocument.Parse bytes
            let root = document.RootElement
            let source = root.GetProperty "source"
            let sandbox = root.GetProperty "sandbox"
            let mint = root.GetProperty "mint"
            let artifacts = root.GetProperty "artifacts"
            let state = restore.State.Binding

            sha256 bytes = state.ProtectedHostReceiptSha256
            && text "candidateSha" source = state.Request.CandidateSha
            && source.GetProperty("runId").GetInt64() = state.Request.WorkflowRunId
            && source.GetProperty("runAttempt").GetInt32() = state.Request.WorkflowRunAttempt
            && text "runNonce" source = state.Request.RunNonce
            && text "workflowPath" source = state.WorkflowPath
            && text "workflowRef" source = state.WorkflowRef
            && text "workflowSha" source = state.WorkflowSha
            && sandbox.GetProperty("repositoryId").GetInt64() = state.RepositoryId
            && text "repositoryNodeId" sandbox = state.RepositoryNodeId
            && text "projectNodeId" sandbox = state.ProjectNodeId
            && text "proofSha256" mint = state.MintProofSha256
            && text "sha256" (artifacts.GetProperty "seedPlan") = state.SeedPlanSha256
            && text "sha256" (artifacts.GetProperty "corpus") = state.CorpusSha256
        with _ ->
            false

    let writeAndRead
        (MigrationSandboxSeedInstalledS2Binding bindingBytes)
        (MigrationSandboxSeedInstalledJournalPolicy _)
        previous
        proposal
        (transport: IMigrationSandboxSeedJournalRemoteTransport)
        =
        let current = proposalSnapshot proposal

        match MigrationSandboxSeedJournal.restore previous current with
        | Error _ -> Error MigrationSandboxSeedRemoteFailure.InvalidJournalProposal
        | Ok restore when not (bindingMatches bindingBytes restore) ->
            Error MigrationSandboxSeedRemoteFailure.InstalledBindingMismatch
        | Ok restore ->
            let old = proposal.ExpectedParent |> Option.defaultValue ""

            let push =
                {
                    RefName = proposal.RefName
                    Refspec = $"{proposal.CommitOid}:{proposal.RefName}"
                    ForceWithLease = $"--force-with-lease={proposal.RefName}:{old}"
                    Objects =
                        [
                            {
                                Kind = "blob"
                                Oid = proposal.BlobOid
                                Bytes = proposal.StateBytes
                            }
                            {
                                Kind = "tree"
                                Oid = proposal.TreeOid
                                Bytes = proposal.TreeBytes
                            }
                            {
                                Kind = "commit"
                                Oid = proposal.CommitOid
                                Bytes = proposal.CommitBytes
                            }
                        ]
                }

            let outcome =
                try
                    transport.PushExact push
                with _ ->
                    MigrationSandboxSeedRemotePushOutcome.ResponseUnknown

            let reread =
                try
                    transport.ReadFresh proposal.RefName
                with _ ->
                    MigrationSandboxSeedJournalRead.Indeterminate "fresh-read-unavailable"

            match outcome, MigrationSandboxSeedJournal.reconcile proposal reread with
            | MigrationSandboxSeedRemotePushOutcome.ParentConflict, MigrationSandboxSeedJournalReconciliation.Applied ->
                Ok(MigrationSandboxSeedRemoteResult.JournalRetryOnly "parent-conflict-identical-object")
            | (MigrationSandboxSeedRemotePushOutcome.Accepted | MigrationSandboxSeedRemotePushOutcome.ResponseUnknown),
              MigrationSandboxSeedJournalReconciliation.Applied ->
                match reread with
                | MigrationSandboxSeedJournalRead.Complete snapshot ->
                    match MigrationSandboxSeedJournal.restore previous snapshot with
                    | Ok applied -> Ok(MigrationSandboxSeedRemoteResult.Applied applied)
                    | Error _ -> Ok(MigrationSandboxSeedRemoteResult.Indeterminate "restore-after-readback")
                | _ -> Ok(MigrationSandboxSeedRemoteResult.Indeterminate "readback-shape")
            | MigrationSandboxSeedRemotePushOutcome.ParentConflict, _ -> Ok MigrationSandboxSeedRemoteResult.Conflict
            | MigrationSandboxSeedRemotePushOutcome.DefiniteRefusal reason, _ ->
                Ok(MigrationSandboxSeedRemoteResult.Refused reason)
            | MigrationSandboxSeedRemotePushOutcome.ResponseUnknown,
              MigrationSandboxSeedJournalReconciliation.ProvenAbsent ->
                Ok(MigrationSandboxSeedRemoteResult.JournalRetryOnly "response-unknown-old-head")
            | MigrationSandboxSeedRemotePushOutcome.Accepted, MigrationSandboxSeedJournalReconciliation.ProvenAbsent ->
                Ok(MigrationSandboxSeedRemoteResult.Indeterminate "accepted-without-readback")
            | _, MigrationSandboxSeedJournalReconciliation.Conflict -> Ok MigrationSandboxSeedRemoteResult.Conflict
            | _, MigrationSandboxSeedJournalReconciliation.Indeterminate reason ->
                Ok(MigrationSandboxSeedRemoteResult.Indeterminate reason)
