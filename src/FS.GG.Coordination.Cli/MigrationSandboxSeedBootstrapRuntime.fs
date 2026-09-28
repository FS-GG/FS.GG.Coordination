namespace FS.GG.Coordination.Cli

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

type internal MigrationSandboxSeedBootstrapRuntimeInput =
    {
        BootstrapDirectory: string
        SourceManifestPath: string
        MintProofPath: string
        FinalAdmissionPath: string
        InitialPrestatePath: string
        InitialPrestateEvidencePath: string
        FinalPrestatePath: string
        FinalPrestateEvidencePath: string
        RunResponsePath: string
        WorkflowBlobPath: string
        BindingBuilderBlobPath: string
        RepositoryResponsePath: string
        ProjectResponsePath: string
        MintResponsePath: string
        ViewerResponsePath: string
        TokenPath: string
        OutputPath: string
    }

type private RuntimeEvidenceRead
    (
        binding: byte array,
        sourceManifest: byte array,
        mintProof: byte array,
        plan: byte array,
        corpus: byte array,
        admission: byte array,
        initialPrestate: byte array,
        initialEvidence: byte array,
        finalEvidence: byte array,
        runResponse: byte array,
        workflowBlob: byte array,
        builderBlob: byte array,
        repositoryResponse: byte array,
        projectResponse: byte array,
        mintResponse: byte array,
        viewerResponse: byte array,
        tokenSha256: string
    ) =
    interface IMigrationSandboxSeedNativeProvenanceRead with
        member _.ReadRunAttempt(_, _) = Ok(Array.copy runResponse)

        member _.ReadGitBlob(_, path) =
            match path with
            | ".github/workflows/github-substrate-v2-sandbox-qualification.yml" -> Ok(Array.copy workflowBlob)
            | "scripts/gs2-09-7-seed-execution-binding.py" -> Ok(Array.copy builderBlob)
            | _ -> Error "unsupported-protected-blob"

        member _.ReadRetained name =
            match name with
            | "mint-proof" -> Ok(Array.copy mintProof)
            | "seed-plan" -> Ok(Array.copy plan)
            | "corpus" -> Ok(Array.copy corpus)
            | "approved-source" -> Ok(Array.copy sourceManifest)
            | "bootstrap-final-admission" -> Ok(Array.copy admission)
            | "bootstrap-prestate" -> Ok(Array.copy initialPrestate)
            | "bootstrap-prestate-evidence" -> Ok(Array.copy initialEvidence)
            | _ -> Error "unsupported-retained-evidence"

        member _.ReadPrivateEphemeral name =
            match name with
            | "mint-response" -> Ok(Array.copy mintResponse)
            | "viewer-response" -> Ok(Array.copy viewerResponse)
            | _ -> Error "unsupported-private-evidence"

        member _.CurrentTokenSha256() = Ok tokenSha256
        member _.ReadSandboxRepository() = Ok(Array.copy repositoryResponse)
        member _.ReadSandboxProject() = Ok(Array.copy projectResponse)
        member _.ReadBootstrapPrestateEvidence() = Ok(Array.copy finalEvidence)

        member _.ReadNativeCasReadback _ =
            Error "bootstrap-runtime-has-no-prior-cas-readback"

type internal ExactGenesisGitTransport(remote: string, gitExecutable: string, token: string) =
    let run (workingDirectory: string) (arguments: string list) (input: byte array option) =
        let start = ProcessStartInfo(gitExecutable)
        start.WorkingDirectory <- workingDirectory
        start.RedirectStandardInput <- input.IsSome
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.UseShellExecute <- false
        start.CreateNoWindow <- true
        start.Environment["GIT_TERMINAL_PROMPT"] <- "0"
        start.Environment["GIT_CONFIG_COUNT"] <- "3"
        start.Environment["GIT_CONFIG_KEY_0"] <- "http.https://github.com/.extraHeader"
        start.Environment["GIT_CONFIG_VALUE_0"] <- "Authorization: Bearer " + token
        start.Environment["GIT_CONFIG_KEY_1"] <- "credential.helper"
        start.Environment["GIT_CONFIG_VALUE_1"] <- ""
        start.Environment["GIT_CONFIG_KEY_2"] <- "http.followRedirects"
        start.Environment["GIT_CONFIG_VALUE_2"] <- "false"

        for argument in arguments do
            start.ArgumentList.Add argument

        use child = Process.Start start

        input
        |> Option.iter (fun (bytes: byte array) ->
            child.StandardInput.BaseStream.Write(bytes, 0, bytes.Length)
            child.StandardInput.Close())

        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()

        if not (child.WaitForExit(60_000)) then
            try
                child.Kill(true)
            with _ ->
                ()

            child.WaitForExit()
            output.GetAwaiter().GetResult() |> ignore
            error.GetAwaiter().GetResult() |> ignore
            124, ""
        else
            let value = output.GetAwaiter().GetResult()
            // Drain stderr so the child cannot block. Provider bytes never leave this process.
            error.GetAwaiter().GetResult() |> ignore
            child.ExitCode, value.Trim()

    let temporary action =
        let path = Path.Combine(Path.GetTempPath(), $"fsgg-q4-bootstrap-{Guid.NewGuid():N}")
        Directory.CreateDirectory path |> ignore

        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

        try
            action path
        finally
            try
                Directory.Delete(path, true)
            with _ ->
                ()

    let init path =
        let code, _ = run path [ "init"; "--bare"; "--quiet" ] None
        code = 0

    interface IMigrationSandboxSeedJournalRemoteTransport with
        member _.PushExact push =
            let expectedLease = $"--force-with-lease={push.RefName}:"

            if
                push.Objects.Length <> 3
                || (push.Objects |> List.map _.Kind) <> [ "blob"; "tree"; "commit" ]
                || push.ForceWithLease <> expectedLease
                || not (push.RefName.StartsWith("refs/heads/gs2-09-7/", StringComparison.Ordinal))
                || not (push.RefName.EndsWith("/seed-journal", StringComparison.Ordinal))
            then
                MigrationSandboxSeedRemotePushOutcome.DefiniteRefusal "exact-genesis-push-shape"
            else
                let expectedRefspec = $"{push.Objects[2].Oid}:{push.RefName}"

                if push.Refspec <> expectedRefspec then
                    MigrationSandboxSeedRemotePushOutcome.DefiniteRefusal "exact-genesis-push-shape"
                else
                    temporary (fun path ->
                        if not (init path) then
                            MigrationSandboxSeedRemotePushOutcome.DefiniteRefusal "git-init"
                        else
                            let objectsExact =
                                push.Objects
                                |> List.forall (fun item ->
                                    let code, oid =
                                        run path [ "hash-object"; "-w"; "--stdin"; "-t"; item.Kind ] (Some item.Bytes)

                                    code = 0 && oid = item.Oid)

                            if not objectsExact then
                                MigrationSandboxSeedRemotePushOutcome.DefiniteRefusal "git-object-identity"
                            else
                                let code, _ =
                                    run path [ "push"; "--porcelain"; push.ForceWithLease; remote; push.Refspec ] None

                                if code = 0 then
                                    MigrationSandboxSeedRemotePushOutcome.Accepted
                                else
                                    // Authentication, transport and lease failures are deliberately not guessed
                                    // from provider text. The mandatory fresh read determines what is knowable.
                                    MigrationSandboxSeedRemotePushOutcome.ResponseUnknown)

        member _.ReadFresh refName =
            temporary (fun path ->
                if not (init path) then
                    MigrationSandboxSeedJournalRead.Indeterminate "fresh-git-init"
                else
                    let code, _ =
                        run path [ "fetch"; "--no-tags"; remote; $"+{refName}:{refName}" ] None

                    if code <> 0 then
                        MigrationSandboxSeedJournalRead.Indeterminate "fresh-git-fetch"
                    else
                        MigrationSandboxSeedJournal.readLocalBare path refName)

[<RequireQualifiedAccess>]
module internal MigrationSandboxSeedBootstrapRuntime =
    let private remote = "https://github.com/FS-GG/FS.GG.GitHub.Substrate.Sandbox.git"

    let private sha (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private regular privateFile limit path =
        let full = Path.GetFullPath path
        let info = FileInfo full

        if
            not info.Exists
            || not (isNull info.LinkTarget)
            || info.Length <= 0L
            || info.Length > int64 limit
        then
            invalidOp "input-file"

        if privateFile && not (OperatingSystem.IsWindows()) then
            let mode = File.GetUnixFileMode full

            if mode <> (UnixFileMode.UserRead ||| UnixFileMode.UserWrite) then
                invalidOp "private-file-mode"

        File.ReadAllBytes full

    let private parsePrestate (bytes: byte array) =
        use document = JsonDocument.Parse bytes
        let root = document.RootElement
        let names = root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

        if
            names
            <> set
                [
                    "schema"
                    "complete"
                    "repositoryId"
                    "projectNodeId"
                    "nonceIssueCount"
                    "nonceProjectItemCount"
                    "snapshotSha256"
                ]
            || root.GetProperty("schema").GetString()
               <> "fsgg.gs2-09-7.sandbox-seed-prestate/1"
        then
            invalidOp "prestate-shape"

        {
            Complete = root.GetProperty("complete").GetBoolean()
            RepositoryId = root.GetProperty("repositoryId").GetInt64()
            ProjectNodeId = root.GetProperty("projectNodeId").GetString()
            NonceIssueCount = root.GetProperty("nonceIssueCount").GetInt32()
            NonceProjectItemCount = root.GetProperty("nonceProjectItemCount").GetInt32()
            SnapshotSha256 = root.GetProperty("snapshotSha256").GetString()
        }

    let private bootstrapPath root relative =
        let root = Path.GetFullPath root
        let path = Path.GetFullPath(Path.Combine(root, relative))

        if not (path.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.Ordinal)) then
            invalidOp "bootstrap-path"

        path

    let private requireExactArtifacts root (expected: MigrationSandboxSeedBootstrapArtifactSet) =
        let linkedDirectory =
            Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            |> Seq.exists (fun path -> not (isNull (DirectoryInfo(path).LinkTarget)))

        if linkedDirectory then
            invalidOp "bootstrap-linked-directory"

        let names =
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'))
            |> Set.ofSeq

        let expectedNames =
            "bootstrap-manifest.json" :: (expected.Files |> List.map fst) |> Set.ofList

        if names <> expectedNames then
            invalidOp "bootstrap-file-set"

        let exact relative bytes =
            let actual = regular false (64 * 1024 * 1024) (bootstrapPath root relative)

            if actual <> bytes then
                invalidOp "bootstrap-byte-drift"

        exact "bootstrap-manifest.json" expected.ManifestBytes
        expected.Files |> List.iter (fun (relative, bytes) -> exact relative bytes)

    let private validateOutput path =
        let full = Path.GetFullPath path

        if File.Exists full || Directory.Exists full then
            invalidOp "output-exists"

        let parent = Path.GetDirectoryName full

        if String.IsNullOrEmpty parent || not (Directory.Exists parent) then
            invalidOp "output-parent"

        if not (isNull (DirectoryInfo(parent).LinkTarget)) then
            invalidOp "output-parent-link"

        full

    let private writeReceipt
        (path: string)
        (candidateSha: string)
        (workflowSha: string)
        (workflowRunId: int64)
        (workflowRunAttempt: int)
        (sourceManifestSha256: string)
        (bindingSha256: string)
        (admissionSha256: string)
        (finalEvidenceSha256: string)
        (proposal: MigrationSandboxSeedJournalPlan)
        =
        let full = validateOutput path

        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteString("schema", "fsgg.gs2-09-7.trusted-bootstrap-runtime-receipt/1")
        writer.WriteString("status", "genesis-journal-applied-and-read-back")
        writer.WriteBoolean("complete", true)
        writer.WriteString("workflowRepository", "FS-GG/.github")
        writer.WriteString("workflowSha", workflowSha)
        writer.WriteNumber("workflowRunId", workflowRunId)
        writer.WriteNumber("workflowRunAttempt", workflowRunAttempt)
        writer.WriteString("candidateSha", candidateSha)
        writer.WriteString("runNonce", proposal.RunNonce)
        writer.WriteNumber("sandboxRepositoryId", 1353050537L)
        writer.WriteString("sandboxRepositoryNodeId", "R_kgDOUKXpqQ")
        writer.WriteString("projectNodeId", "PVT_kwDOEYAWY84BiESo")
        writer.WriteString("refName", proposal.RefName)
        writer.WriteNull("expectedOldOid")
        writer.WriteString("commitOid", proposal.CommitOid)
        writer.WriteString("treeOid", proposal.TreeOid)
        writer.WriteString("blobOid", proposal.BlobOid)
        writer.WriteString("stateSha256", proposal.StateSha256)
        writer.WriteNumber("journalGeneration", proposal.JournalGeneration)
        writer.WriteNumber("stateGeneration", proposal.StateGeneration)
        writer.WriteString("sourceManifestSha256", sourceManifestSha256)
        writer.WriteString("s2DeclarationSha256", bindingSha256)
        writer.WriteString("finalAdmissionSha256", admissionSha256)
        writer.WriteString("finalPrestateEvidenceSha256", finalEvidenceSha256)
        writer.WriteBoolean("providerEffectsAuthorized", false)
        writer.WriteEndObject()
        writer.Flush()

        use output =
            new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None)

        let bytes = stream.ToArray()
        output.Write(bytes, 0, bytes.Length)
        output.Flush(true)

        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(full, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    let internal establishAndWrite verifier evidence proposal transport =
        MigrationSandboxSeedJournalRemote.establishBootstrapAdmission verifier evidence proposal
        |> Result.bind (fun admission ->
            MigrationSandboxSeedJournalRemote.writeGenesisAndRead admission proposal transport)

    let internal executeWith gitExecutable (input: MigrationSandboxSeedBootstrapRuntimeInput) =
        try
            let bootstrapDirectory = Path.GetFullPath input.BootstrapDirectory
            let directory = DirectoryInfo bootstrapDirectory

            if not directory.Exists || not (isNull directory.LinkTarget) then
                invalidOp "bootstrap-directory"

            let sourceManifest = regular false (1024 * 1024) input.SourceManifestPath
            let mintProof = regular false (1024 * 1024) input.MintProofPath
            let admission = regular true (1024 * 1024) input.FinalAdmissionPath
            let initialPrestate = regular true (1024 * 1024) input.InitialPrestatePath

            let initialEvidence =
                regular true (64 * 1024 * 1024) input.InitialPrestateEvidencePath

            let finalPrestate = regular true (1024 * 1024) input.FinalPrestatePath
            let finalEvidence = regular true (64 * 1024 * 1024) input.FinalPrestateEvidencePath
            let runResponse = regular true (1024 * 1024) input.RunResponsePath
            let workflowBlob = regular true (1024 * 1024) input.WorkflowBlobPath
            let builderBlob = regular true (1024 * 1024) input.BindingBuilderBlobPath
            let repositoryResponse = regular true (1024 * 1024) input.RepositoryResponsePath
            let projectResponse = regular true (1024 * 1024) input.ProjectResponsePath
            let mintResponse = regular true (1024 * 1024) input.MintResponsePath
            let viewerResponse = regular true (1024 * 1024) input.ViewerResponsePath
            let tokenBytes = regular true 4096 input.TokenPath
            let token = Encoding.UTF8.GetString tokenBytes

            // Refuse every local custody error before an opaque admission can be created or
            // the exact Git transport can run.
            validateOutput input.OutputPath |> ignore

            if
                token.Length < 20
                || token.Length > 2048
                || token |> Seq.exists Char.IsWhiteSpace
            then
                invalidOp "token-shape"

            let prestate = parsePrestate initialPrestate

            if parsePrestate finalPrestate <> prestate then
                invalidOp "final-prestate-drift"

            use manifestDocument =
                JsonDocument.Parse(
                    regular false (1024 * 1024) (bootstrapPath bootstrapDirectory "bootstrap-manifest.json")
                )

            let manifest = manifestDocument.RootElement
            let candidateSha = manifest.GetProperty("candidateSha").GetString()
            let workflowRunId = manifest.GetProperty("workflowRunId").GetInt64()
            let workflowRunAttempt = manifest.GetProperty("workflowRunAttempt").GetInt32()
            let workflowSha = manifest.GetProperty("workflowSha").GetString()

            let binding =
                regular false (1024 * 1024) (bootstrapPath bootstrapDirectory "s2-declaration.json")

            let plan =
                regular false (64 * 1024 * 1024) (bootstrapPath bootstrapDirectory "seed-plan.json")

            let corpus =
                regular false (64 * 1024 * 1024) (bootstrapPath bootstrapDirectory "corpus.json")

            let request: MigrationSandboxSeedBootstrapArtifactRequest =
                {
                    CandidateSha = candidateSha
                    WorkflowRunId = workflowRunId
                    WorkflowRunAttempt = workflowRunAttempt
                    WorkflowSha = workflowSha
                    SourceManifestBytes = ReadOnlyMemory sourceManifest
                    S2DeclarationBytes = ReadOnlyMemory binding
                    MintProofBytes = ReadOnlyMemory mintProof
                    SeedPlanBytes = ReadOnlyMemory plan
                    CorpusBytes = ReadOnlyMemory corpus
                    Prestate = prestate
                }

            let prepared =
                MigrationSandboxSeedBootstrapArtifact.prepare request
                |> Result.defaultWith (fun error -> invalidOp $"bootstrap-prepare-{error}")

            requireExactArtifacts bootstrapDirectory prepared

            let evidence =
                {
                    BindingBytes = binding
                    NativeCasReadbackBytes = [||]
                    BootstrapAdmissionBytes = admission
                    BootstrapPrestateBytes = initialPrestate
                    WorkflowRunId = workflowRunId
                    WorkflowRunAttempt = workflowRunAttempt
                    WorkflowSha = workflowSha
                    ApprovedArtifactSourceSha256 = sha sourceManifest
                }

            let reads =
                RuntimeEvidenceRead(
                    binding,
                    sourceManifest,
                    mintProof,
                    plan,
                    corpus,
                    admission,
                    initialPrestate,
                    initialEvidence,
                    finalEvidence,
                    runResponse,
                    workflowBlob,
                    builderBlob,
                    repositoryResponse,
                    projectResponse,
                    mintResponse,
                    viewerResponse,
                    sha (Encoding.UTF8.GetBytes token)
                )
                :> IMigrationSandboxSeedNativeProvenanceRead

            let verifier =
                MigrationSandboxSeedNativeProvenanceVerifier(reads) :> IMigrationSandboxSeedIsolatedProvenanceVerifier

            let transport =
                ExactGenesisGitTransport(remote, gitExecutable, token) :> IMigrationSandboxSeedJournalRemoteTransport

            match establishAndWrite verifier evidence prepared.JournalPlan transport with
            | Ok(MigrationSandboxSeedRemoteResult.Applied _) ->
                writeReceipt
                    input.OutputPath
                    candidateSha
                    workflowSha
                    workflowRunId
                    workflowRunAttempt
                    (sha sourceManifest)
                    (sha binding)
                    (sha admission)
                    (sha finalEvidence)
                    prepared.JournalPlan

                Ok()
            | Ok MigrationSandboxSeedRemoteResult.Conflict -> Error "genesis-conflict"
            | Ok(MigrationSandboxSeedRemoteResult.Refused _) -> Error "genesis-refused"
            | Ok(MigrationSandboxSeedRemoteResult.Indeterminate _) -> Error "genesis-indeterminate"
            | Ok(MigrationSandboxSeedRemoteResult.JournalRetryOnly _) -> Error "genesis-retry-only"
            | Error error -> Error $"bootstrap-admission-{error}"
        with _ ->
            Error "trusted-bootstrap-runtime-refused"

    let execute input =
        let gitExecutable =
            if OperatingSystem.IsWindows() then
                "git.exe"
            else
                "/usr/bin/git"

        executeWith gitExecutable input
