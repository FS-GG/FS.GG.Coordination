namespace FS.GG.Coordination.Cli

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

// Types duplicated from the signature.
type MigrationSandboxSeedJournalPlan =
    {
        RefName: string
        JournalGeneration: int64
        StateGeneration: int64
        RunNonce: string
        BindingSeal: string
        ExpectedParent: string option
        StateSha256: string
        StateBytes: byte array
        BlobOid: string
        TreeOid: string
        TreeBytes: byte array
        CommitOid: string
        CommitBytes: byte array
    }

type MigrationSandboxSeedJournalSnapshot =
    {
        RefName: string
        JournalGeneration: int64
        StateGeneration: int64
        RunNonce: string
        BindingSeal: string
        CommitOid: string
        ParentOid: string option
        StateSha256: string
        StateBytes: byte array
        BlobOid: string
        TreeOid: string
        TreeBytes: byte array
        CommitBytes: byte array
    }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedJournalRead =
    | Missing
    | Complete of MigrationSandboxSeedJournalSnapshot
    | Indeterminate of reason: string

[<RequireQualifiedAccess>]
type MigrationSandboxSeedJournalReconciliation =
    | Applied
    | ProvenAbsent
    | Conflict
    | Indeterminate of reason: string

[<RequireQualifiedAccess>]
type MigrationSandboxSeedJournalFailure =
    | InvalidState
    | InvalidPrevious
    | StaleGeneration
    | GitUnavailable of reason: string

[<RequireQualifiedAccess>]
module MigrationSandboxSeedJournal =
    let private utf8 = UTF8Encoding(false, true)

    let private hex length (value: string) =
        not (isNull value)
        && value.Length = length
        && value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f')

    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private gitOid kind (bytes: byte array) =
        let header = utf8.GetBytes($"{kind} {bytes.Length}\000")

        Array.append header bytes
        |> SHA1.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private effectKind =
        function
        | MigrationSandboxSeedEffectKind.CreateNonceIssue -> "create-nonce-issue"
        | MigrationSandboxSeedEffectKind.AddProjectMembership -> "add-project-membership"
        | MigrationSandboxSeedEffectKind.RemoveProjectMembership -> "remove-project-membership"
        | MigrationSandboxSeedEffectKind.DeleteNonceIssue -> "delete-nonce-issue"

    let private stage =
        function
        | MigrationSandboxSeedEffectStage.Planned -> "planned"
        | MigrationSandboxSeedEffectStage.IntentPersisted -> "intent-persisted"
        | MigrationSandboxSeedEffectStage.InFlight -> "in-flight"
        | MigrationSandboxSeedEffectStage.RecoveryPending -> "recovery-pending"
        | MigrationSandboxSeedEffectStage.Settled -> "settled"

    let private mode =
        function
        | MigrationSandboxSeedExecutionMode.Forward -> "forward"
        | MigrationSandboxSeedExecutionMode.Compensation -> "compensation"
        | MigrationSandboxSeedExecutionMode.Complete -> "complete"
        | MigrationSandboxSeedExecutionMode.Compensated -> "compensated"

    let private writeOption (writer: Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some value -> writer.WriteString(name, value)
        | None -> writer.WriteNull(name)

    let private encodeState (state: MigrationSandboxSeedExecution) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        let b = state.Binding
        writer.WriteStartObject()
        writer.WriteString("schema", "fsgg.gs2-09-7.sandbox-seed-execution/1")
        writer.WriteNumber("stateGeneration", state.Generation)
        writer.WriteString("stateHead", state.Head)
        writer.WriteString("mode", mode state.Mode)
        writer.WriteNumber("activeIndex", state.ActiveIndex)
        writer.WriteStartObject("binding")
        writer.WriteString("candidateSha", b.Request.CandidateSha)
        writer.WriteNumber("workflowRunId", b.Request.WorkflowRunId)
        writer.WriteNumber("workflowRunAttempt", b.Request.WorkflowRunAttempt)
        writer.WriteString("runNonce", b.Request.RunNonce)
        writer.WriteString("requestCorpusSha256", b.Request.CorpusSha256)
        writer.WriteString("workflowPath", b.WorkflowPath)
        writer.WriteString("workflowRef", b.WorkflowRef)
        writer.WriteString("workflowSha", b.WorkflowSha)
        writer.WriteNumber("repositoryId", b.RepositoryId)
        writer.WriteString("repositoryNodeId", b.RepositoryNodeId)
        writer.WriteString("projectNodeId", b.ProjectNodeId)
        writer.WriteString("mintProofSha256", b.MintProofSha256)
        writer.WriteString("protectedHostReceiptSha256", b.ProtectedHostReceiptSha256)
        writer.WriteString("seedPlanSha256", b.SeedPlanSha256)
        writer.WriteString("corpusSha256", b.CorpusSha256)
        writer.WriteStartObject("prestate")
        writer.WriteBoolean("complete", b.Prestate.Complete)
        writer.WriteNumber("repositoryId", b.Prestate.RepositoryId)
        writer.WriteString("projectNodeId", b.Prestate.ProjectNodeId)
        writer.WriteNumber("nonceIssueCount", b.Prestate.NonceIssueCount)
        writer.WriteNumber("nonceProjectItemCount", b.Prestate.NonceProjectItemCount)
        writer.WriteString("snapshotSha256", b.Prestate.SnapshotSha256)
        writer.WriteEndObject()
        writer.WriteStartArray("admittedEffects")
        b.AdmittedEffects |> List.iter (effectKind >> writer.WriteStringValue)
        writer.WriteEndArray()
        writer.WriteString("seal", b.Seal)
        writer.WriteEndObject()
        writer.WriteStartArray("effects")

        for effect in state.Effects do
            writer.WriteStartObject()
            writer.WriteString("effectId", effect.EffectId)
            writer.WriteString("idempotencyKey", effect.IdempotencyKey)
            writer.WriteString("kind", effectKind effect.Kind)
            writeOption writer "originalEffectId" effect.OriginalEffectId
            writer.WriteString("stage", stage effect.Stage)

            match effect.Ownership with
            | None -> writer.WriteNull("ownership")
            | Some owned ->
                writer.WriteStartObject("ownership")
                writer.WriteString("effectId", owned.EffectId)
                writer.WriteString("kind", owned.Kind)
                writer.WriteString("resourceId", owned.ResourceId)
                writeOption writer "parentResourceId" owned.ParentResourceId
                writer.WriteString("runNonce", owned.RunNonce)
                writer.WriteString("readbackSha256", owned.ReadbackSha256)
                writer.WriteEndObject()

            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()

    let private refName nonce =
        $"refs/heads/gs2-09-7/{nonce}/seed-journal"

    let private stateShape (state: MigrationSandboxSeedExecution) =
        let b = state.Binding

        let expectedKinds =
            match state.Mode with
            | MigrationSandboxSeedExecutionMode.Forward
            | MigrationSandboxSeedExecutionMode.Complete ->
                [
                    MigrationSandboxSeedEffectKind.CreateNonceIssue
                    MigrationSandboxSeedEffectKind.AddProjectMembership
                ]
            | MigrationSandboxSeedExecutionMode.Compensation
            | MigrationSandboxSeedExecutionMode.Compensated ->
                [
                    MigrationSandboxSeedEffectKind.RemoveProjectMembership
                    MigrationSandboxSeedEffectKind.DeleteNonceIssue
                ]

        state.Generation >= 0L
        && hex 40 state.Head
        && hex 64 b.Seal
        && hex 40 b.Request.CandidateSha
        && hex 64 b.Request.CorpusSha256
        && b.Request.RunNonce = $"{b.Request.WorkflowRunId}-{b.Request.WorkflowRunAttempt}-{b.Request.CandidateSha}"
        && b.Request.RunNonce.Length <= 160
        && b.WorkflowPath = ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
        && b.WorkflowRef = "refs/heads/main"
        && hex 40 b.WorkflowSha
        && b.RepositoryId = 1353050537L
        && b.RepositoryNodeId = "R_kgDOUKXpqQ"
        && b.ProjectNodeId = "PVT_kwDOEYAWY84BiESo"
        && ([
                b.MintProofSha256
                b.ProtectedHostReceiptSha256
                b.SeedPlanSha256
                b.CorpusSha256
            ]
            |> List.forall (hex 64))
        && b.CorpusSha256 = b.Request.CorpusSha256
        && b.Prestate.Complete
        && b.Prestate.RepositoryId = b.RepositoryId
        && b.Prestate.ProjectNodeId = b.ProjectNodeId
        && b.Prestate.NonceIssueCount = 0
        && b.Prestate.NonceProjectItemCount = 0
        && hex 64 b.Prestate.SnapshotSha256
        && b.AdmittedEffects =
            [
                MigrationSandboxSeedEffectKind.CreateNonceIssue
                MigrationSandboxSeedEffectKind.AddProjectMembership
                MigrationSandboxSeedEffectKind.RemoveProjectMembership
                MigrationSandboxSeedEffectKind.DeleteNonceIssue
            ]
        && state.Effects.Length = 2
        && (state.Effects |> List.map _.Kind) = expectedKinds
        && (state.Effects
            |> List.forall (fun e -> hex 64 e.EffectId && hex 64 e.IdempotencyKey))

    let private genesisState (state: MigrationSandboxSeedExecution) =
        state.Mode = MigrationSandboxSeedExecutionMode.Forward
        && state.ActiveIndex = 0
        && state.Effects
           |> List.forall (fun effect ->
               effect.Stage = MigrationSandboxSeedEffectStage.Planned
               && effect.OriginalEffectId.IsNone
               && effect.Ownership.IsNone)

    let private treeBytes (blob: string) =
        let prefix = utf8.GetBytes("100644 state.json\000")
        let raw = Convert.FromHexString blob
        Array.append prefix raw

    let private commitBytes (generation: int64) (tree: string) (parent: string option) (stateSha: string) =
        let parentLine =
            parent |> Option.map (fun oid -> $"parent {oid}\n") |> Option.defaultValue ""

        utf8.GetBytes(
            $"tree {tree}\n{parentLine}author FS.GG Q4 Seed Journal <q4-seed-journal@fs.gg> {generation} +0000\ncommitter FS.GG Q4 Seed Journal <q4-seed-journal@fs.gg> {generation} +0000\n\nfsgg Q4 seed journal generation {generation}\nstate-sha256 {stateSha}\n"
        )

    let private metadata (bytes: byte array) =
        try
            use document = JsonDocument.Parse bytes
            let root = document.RootElement
            let binding = root.GetProperty("binding")

            if
                root.GetProperty("schema").GetString()
                <> "fsgg.gs2-09-7.sandbox-seed-execution/1"
            then
                None
            else
                Some(
                    root.GetProperty("stateGeneration").GetInt64(),
                    binding.GetProperty("runNonce").GetString(),
                    binding.GetProperty("seal").GetString()
                )
        with _ ->
            None

    let private snapshotValid (snapshot: MigrationSandboxSeedJournalSnapshot) =
        snapshot.RefName = refName snapshot.RunNonce
        && snapshot.JournalGeneration >= 0L
        && snapshot.StateGeneration >= 0L
        && hex 40 snapshot.CommitOid
        && (snapshot.ParentOid |> Option.forall (hex 40))
        && hex 64 snapshot.BindingSeal
        && snapshot.StateSha256 = sha256 snapshot.StateBytes
        && snapshot.BlobOid = gitOid "blob" snapshot.StateBytes
        && snapshot.TreeOid = gitOid "tree" snapshot.TreeBytes
        && snapshot.CommitOid = gitOid "commit" snapshot.CommitBytes
        && snapshot.TreeBytes = treeBytes snapshot.BlobOid
        && snapshot.CommitBytes =
            commitBytes snapshot.JournalGeneration snapshot.TreeOid snapshot.ParentOid snapshot.StateSha256
        && metadata snapshot.StateBytes = Some(snapshot.StateGeneration, snapshot.RunNonce, snapshot.BindingSeal)

    let plan previous state =
        if not (stateShape state) then
            Error MigrationSandboxSeedJournalFailure.InvalidState
        else
            let next =
                match previous with
                | None when genesisState state -> Ok(0L, None)
                | None -> Error MigrationSandboxSeedJournalFailure.InvalidState
                | Some value when
                    not (snapshotValid value)
                    || value.RefName <> refName state.Binding.Request.RunNonce
                    || value.RunNonce <> state.Binding.Request.RunNonce
                    || value.BindingSeal <> state.Binding.Seal
                    ->
                    Error MigrationSandboxSeedJournalFailure.InvalidPrevious
                | Some value when state.Generation <> value.StateGeneration + 1L ->
                    Error MigrationSandboxSeedJournalFailure.StaleGeneration
                | Some value -> Ok(value.JournalGeneration + 1L, Some value.CommitOid)

            match next with
            | Error failure -> Error failure
            | Ok(generation, parent) ->
                let stateBytes = encodeState state
                let stateSha = sha256 stateBytes
                let blob = gitOid "blob" stateBytes
                let treeData = treeBytes blob
                let tree = gitOid "tree" treeData
                let commitData = commitBytes generation tree parent stateSha
                let commit = gitOid "commit" commitData

                Ok
                    {
                        RefName = refName state.Binding.Request.RunNonce
                        JournalGeneration = generation
                        StateGeneration = state.Generation
                        RunNonce = state.Binding.Request.RunNonce
                        BindingSeal = state.Binding.Seal
                        ExpectedParent = parent
                        StateSha256 = stateSha
                        StateBytes = stateBytes
                        BlobOid = blob
                        TreeOid = tree
                        TreeBytes = treeData
                        CommitOid = commit
                        CommitBytes = commitData
                    }

    let private runGit (path: string) (args: string list) =
        try
            let start = ProcessStartInfo("git")
            start.WorkingDirectory <- path
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true
            start.UseShellExecute <- false

            for arg in args do
                start.ArgumentList.Add arg

            use child = Process.Start start
            use output = new MemoryStream()
            child.StandardOutput.BaseStream.CopyTo output
            let error = child.StandardError.ReadToEnd()
            child.WaitForExit()

            if child.ExitCode = 0 then
                Ok(output.ToArray())
            else
                Error(
                    if String.IsNullOrWhiteSpace error then
                        $"git-exit-{child.ExitCode}"
                    else
                        error.Trim()
                )
        with ex ->
            Error ex.Message

    let private textBytes (bytes: byte array) = utf8.GetString bytes

    let private parseCommit (bytes: byte array) =
        let lines = (textBytes bytes).Split('\n')

        let field (name: string) =
            lines
            |> Array.tryPick (fun line ->
                if line.StartsWith(name + " ", StringComparison.Ordinal) then
                    Some(line.Substring(name.Length + 1))
                else
                    None)

        match field "tree", field "parent" with
        | Some tree, parent when hex 40 tree && parent |> Option.forall (hex 40) -> Some(tree, parent)
        | _ -> None

    let private parseTree (bytes: byte array) =
        let prefix = utf8.GetBytes("100644 state.json\000")

        if
            bytes.Length = prefix.Length + 20
            && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix)
        then
            Some(Convert.ToHexString(bytes.AsSpan(prefix.Length, 20)).ToLowerInvariant())
        else
            None

    let readLocalBare (repositoryPath: string) (requestedRef: string) =
        if
            String.IsNullOrWhiteSpace repositoryPath
            || not (requestedRef.StartsWith("refs/heads/gs2-09-7/", StringComparison.Ordinal))
            || not (requestedRef.EndsWith("/seed-journal", StringComparison.Ordinal))
        then
            MigrationSandboxSeedJournalRead.Indeterminate "invalid-read-scope"
        else
            match runGit repositoryPath [ "rev-parse"; "--verify"; requestedRef ] with
            | Error reason when
                reason.Contains("Needed a single revision", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("unknown revision", StringComparison.OrdinalIgnoreCase)
                ->
                MigrationSandboxSeedJournalRead.Missing
            | Error reason -> MigrationSandboxSeedJournalRead.Indeterminate reason
            | Ok commitOutput ->
                let commit = textBytes commitOutput |> _.Trim()

                match runGit repositoryPath [ "cat-file"; "commit"; commit ] with
                | Error reason -> MigrationSandboxSeedJournalRead.Indeterminate reason
                | Ok commitData ->
                    match parseCommit commitData with
                    | None -> MigrationSandboxSeedJournalRead.Indeterminate "invalid-commit"
                    | Some(tree, parent) ->
                        match runGit repositoryPath [ "cat-file"; "tree"; tree ] with
                        | Error reason -> MigrationSandboxSeedJournalRead.Indeterminate reason
                        | Ok treeData ->
                            match parseTree treeData with
                            | None -> MigrationSandboxSeedJournalRead.Indeterminate "invalid-tree"
                            | Some blob ->
                                match runGit repositoryPath [ "cat-file"; "blob"; blob ] with
                                | Error reason -> MigrationSandboxSeedJournalRead.Indeterminate reason
                                | Ok stateBytes ->
                                    match metadata stateBytes with
                                    | None -> MigrationSandboxSeedJournalRead.Indeterminate "invalid-state"
                                    | Some(stateGeneration, nonce, seal) ->
                                        let stateSha = sha256 stateBytes
                                        let message = textBytes commitData
                                        let marker = "fsgg Q4 seed journal generation "

                                        let line =
                                            message.Split('\n')
                                            |> Array.tryFind (fun value ->
                                                value.StartsWith(marker, StringComparison.Ordinal))

                                        let mutable generation = 0L

                                        match line with
                                        | Some value when
                                            Int64.TryParse(
                                                value.Substring(marker.Length),
                                                NumberStyles.None,
                                                CultureInfo.InvariantCulture,
                                                &generation
                                            )
                                            ->
                                            let snapshot =
                                                {
                                                    RefName = requestedRef
                                                    JournalGeneration = generation
                                                    StateGeneration = stateGeneration
                                                    RunNonce = nonce
                                                    BindingSeal = seal
                                                    CommitOid = commit
                                                    ParentOid = parent
                                                    StateSha256 = stateSha
                                                    StateBytes = stateBytes
                                                    BlobOid = blob
                                                    TreeOid = tree
                                                    TreeBytes = treeData
                                                    CommitBytes = commitData
                                                }

                                            if snapshotValid snapshot then
                                                MigrationSandboxSeedJournalRead.Complete snapshot
                                            else
                                                MigrationSandboxSeedJournalRead.Indeterminate "snapshot-integrity"
                                        | _ -> MigrationSandboxSeedJournalRead.Indeterminate "invalid-generation"

    let private exact (plan: MigrationSandboxSeedJournalPlan) (snapshot: MigrationSandboxSeedJournalSnapshot) =
        snapshot.RefName = plan.RefName
        && snapshot.JournalGeneration = plan.JournalGeneration
        && snapshot.StateGeneration = plan.StateGeneration
        && snapshot.RunNonce = plan.RunNonce
        && snapshot.BindingSeal = plan.BindingSeal
        && snapshot.CommitOid = plan.CommitOid
        && snapshot.ParentOid = plan.ExpectedParent
        && snapshot.StateSha256 = plan.StateSha256
        && snapshot.StateBytes = plan.StateBytes
        && snapshot.BlobOid = plan.BlobOid
        && snapshot.TreeOid = plan.TreeOid
        && snapshot.TreeBytes = plan.TreeBytes
        && snapshot.CommitBytes = plan.CommitBytes

    let reconcile (proposal: MigrationSandboxSeedJournalPlan) (reread: MigrationSandboxSeedJournalRead) =
        match reread with
        | MigrationSandboxSeedJournalRead.Indeterminate reason ->
            MigrationSandboxSeedJournalReconciliation.Indeterminate reason
        | MigrationSandboxSeedJournalRead.Missing when proposal.ExpectedParent.IsNone ->
            MigrationSandboxSeedJournalReconciliation.ProvenAbsent
        | MigrationSandboxSeedJournalRead.Missing -> MigrationSandboxSeedJournalReconciliation.Conflict
        | MigrationSandboxSeedJournalRead.Complete snapshot when exact proposal snapshot ->
            MigrationSandboxSeedJournalReconciliation.Applied
        | MigrationSandboxSeedJournalRead.Complete snapshot when Some snapshot.CommitOid = proposal.ExpectedParent ->
            MigrationSandboxSeedJournalReconciliation.ProvenAbsent
        | MigrationSandboxSeedJournalRead.Complete _ -> MigrationSandboxSeedJournalReconciliation.Conflict
