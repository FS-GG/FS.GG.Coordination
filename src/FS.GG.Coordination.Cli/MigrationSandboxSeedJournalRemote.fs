namespace FS.GG.Coordination.Cli

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

type MigrationSandboxSeedIsolatedCasAuthority = private MigrationSandboxSeedIsolatedCasAuthority of byte array
type MigrationSandboxSeedBootstrapAdmission =
    private
    | MigrationSandboxSeedBootstrapAdmission of bindingBytes: byte array * admissionBytes: byte array

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
    | InvalidIsolatedCasBinding
    | IsolatedProvenanceRejected
    | IsolatedBindingMismatch
    | InvalidJournalProposal

[<RequireQualifiedAccess>]
module MigrationSandboxSeedJournalRemote =
    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private text (name: string) (value: JsonElement) =
        let property = value.GetProperty name

        if property.ValueKind <> JsonValueKind.String || isNull (property.GetString()) then
            invalidOp name

        property.GetString()

    let private evidenceBytes evidence =
        let bindingBytes =
            if isNull evidence.BindingBytes then
                [||]
            else
                Array.copy evidence.BindingBytes

        let nativeReadbackBytes =
            if isNull evidence.NativeCasReadbackBytes then
                [||]
            else
                Array.copy evidence.NativeCasReadbackBytes

        bindingBytes, nativeReadbackBytes

    let establishBootstrapAdmission
        (verifier: IMigrationSandboxSeedIsolatedProvenanceVerifier)
        (evidence: MigrationSandboxSeedIsolatedProvenanceEvidence)
        (proposal: MigrationSandboxSeedJournalPlan)
        =
        let bindingBytes, nativeReadbackBytes = evidenceBytes evidence

        if
            bindingBytes.Length = 0
            || bindingBytes.Length > 1024 * 1024
            || nativeReadbackBytes.Length <> 0
            || isNull evidence.BootstrapAdmissionBytes
            || evidence.BootstrapAdmissionBytes.Length = 0
            || evidence.BootstrapAdmissionBytes.Length > 1024 * 1024
            || isNull evidence.BootstrapPrestateBytes
            || evidence.BootstrapPrestateBytes.Length = 0
            || evidence.BootstrapPrestateBytes.Length > 1024 * 1024
        then
            Error MigrationSandboxSeedRemoteFailure.InvalidIsolatedCasBinding
        else
            try
                let exact =
                    { evidence with
                        BindingBytes = bindingBytes
                        NativeCasReadbackBytes = [||]
                    }

                if verifier.VerifyBootstrapExact(exact, proposal) then
                    Ok(
                        MigrationSandboxSeedBootstrapAdmission(
                            bindingBytes,
                            Array.copy evidence.BootstrapAdmissionBytes
                        )
                    )
                else
                    Error MigrationSandboxSeedRemoteFailure.IsolatedProvenanceRejected
            with _ ->
                Error MigrationSandboxSeedRemoteFailure.IsolatedProvenanceRejected

    let establishIsolatedCasAuthority
        (verifier: IMigrationSandboxSeedIsolatedProvenanceVerifier)
        (evidence: MigrationSandboxSeedIsolatedProvenanceEvidence)
        =
        let bindingBytes, nativeReadbackBytes = evidenceBytes evidence

        if
            bindingBytes.Length = 0
            || bindingBytes.Length > 1024 * 1024
            || nativeReadbackBytes.Length = 0
            || nativeReadbackBytes.Length > 1024 * 1024
            || (not (isNull evidence.BootstrapAdmissionBytes)
                && evidence.BootstrapAdmissionBytes.Length <> 0)
            || (not (isNull evidence.BootstrapPrestateBytes)
                && evidence.BootstrapPrestateBytes.Length <> 0)
        then
            Error MigrationSandboxSeedRemoteFailure.InvalidIsolatedCasBinding
        else
            try
                let verifierEvidence =
                    { evidence with
                        BindingBytes = bindingBytes
                        NativeCasReadbackBytes = nativeReadbackBytes
                    }

                if verifier.VerifyExact verifierEvidence then
                    Ok(MigrationSandboxSeedIsolatedCasAuthority bindingBytes)
                else
                    Error MigrationSandboxSeedRemoteFailure.IsolatedProvenanceRejected
            with _ ->
                Error MigrationSandboxSeedRemoteFailure.IsolatedProvenanceRejected

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

    let private transact bindingBytes previous proposal (transport: IMigrationSandboxSeedJournalRemoteTransport) =
        let current = proposalSnapshot proposal

        match MigrationSandboxSeedJournal.restore previous current with
        | Error _ -> Error MigrationSandboxSeedRemoteFailure.InvalidJournalProposal
        | Ok restore when not (bindingMatches bindingBytes restore) ->
            Error MigrationSandboxSeedRemoteFailure.IsolatedBindingMismatch
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

    let private admissionMatches (bytes: byte array) (proposal: MigrationSandboxSeedJournalPlan) =
        try
            use document = JsonDocument.Parse bytes
            let root = document.RootElement
            let subject = root.GetProperty "subject"

            root.GetProperty("schema").GetString() = "fsgg.gs2-09-7.seed-admission-request/1"
            && root.GetProperty("phase").GetString() = "final"
            && subject.GetProperty("refName").GetString() = proposal.RefName
            && subject.GetProperty("blobOid").GetString() = proposal.BlobOid
            && subject.GetProperty("treeOid").GetString() = proposal.TreeOid
            && subject.GetProperty("commitOid").GetString() = proposal.CommitOid
            && subject.GetProperty("expectedOldOid").ValueKind = JsonValueKind.Null
            && subject.GetProperty("expectedRefAbsent").GetBoolean()
            && subject.GetProperty("operation").GetString() = "genesis-nonce-seed-journal"
        with _ ->
            false

    let writeGenesisAndRead
        (MigrationSandboxSeedBootstrapAdmission(bindingBytes, admissionBytes))
        proposal
        transport
        =
        if
            proposal.ExpectedParent.IsSome
            || proposal.JournalGeneration <> 0L
            || proposal.StateGeneration <> 0L
            || not (admissionMatches admissionBytes proposal)
        then
            Error MigrationSandboxSeedRemoteFailure.InvalidJournalProposal
        else
            transact bindingBytes None proposal transport

    let writeAndRead (MigrationSandboxSeedIsolatedCasAuthority bindingBytes) previous proposal transport =
        if
            proposal.ExpectedParent <> Some previous.CommitOid
            || proposal.JournalGeneration <= 0L
            || proposal.StateGeneration <= 0L
        then
            Error MigrationSandboxSeedRemoteFailure.InvalidJournalProposal
        else
            transact bindingBytes (Some previous) proposal transport
