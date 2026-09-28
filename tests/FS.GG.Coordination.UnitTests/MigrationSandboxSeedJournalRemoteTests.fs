module FS.GG.Coordination.MigrationSandboxSeedJournalRemoteTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open FS.GG.Coordination.Cli

let private raw (value: string) = Encoding.UTF8.GetBytes value

let private shaBytes (value: byte array) =
    SHA256.HashData value |> Convert.ToHexString |> _.ToLowerInvariant()

let private candidate = String.replicate 40 "a"
let private workflow = String.replicate 40 "b"

let private planBytes, corpusBytes, mintBytes =
    raw "seed-plan", raw "corpus", raw "mint"

let private installedBytes includeAdministration =
    let nonce = $"7-2-{candidate}"

    let write (fingerprint: string option) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteBoolean("activation", true)
        writer.WriteStartObject("artifacts")
        writer.WriteStartObject("corpus")
        writer.WriteString("sha256", shaBytes corpusBytes)
        writer.WriteEndObject()
        writer.WriteStartObject("seedPlan")
        writer.WriteString("sha256", shaBytes planBytes)
        writer.WriteEndObject()
        writer.WriteEndObject()
        writer.WriteString("authority", "installed-protected-workflow-verified")

        fingerprint
        |> Option.iter (fun value -> writer.WriteString("fingerprint", value))

        writer.WriteStartObject("journal")
        writer.WriteStartArray("allowedClosedEffectKinds")

        [
            "CreateNonceIssue"
            "AddProjectMembership"
            "RemoveProjectMembership"
            "DeleteNonceIssue"
        ]
        |> List.iter writer.WriteStringValue

        writer.WriteEndArray()
        writer.WriteString("identity", "protected-journal:gs2-09-7-q4-seed-v1")
        writer.WriteEndObject()
        writer.WriteStartObject("mint")
        writer.WriteNumber("appId", 4166418L)
        writer.WriteNumber("installationId", 143110413L)
        writer.WriteStartObject("permissions")

        if includeAdministration then
            writer.WriteString("administration", "write")

        writer.WriteString("contents", "write")
        writer.WriteString("issues", "write")
        writer.WriteString("metadata", "read")
        writer.WriteString("organization_projects", "write")
        writer.WriteEndObject()
        writer.WriteString("proofSha256", shaBytes mintBytes)
        writer.WriteEndObject()
        writer.WriteStartObject("sandbox")
        writer.WriteString("projectNodeId", "PVT_kwDOEYAWY84BiESo")
        writer.WriteNumber("repositoryId", 1353050537L)
        writer.WriteString("repositoryNodeId", "R_kgDOUKXpqQ")
        writer.WriteEndObject()
        writer.WriteString("schema", "fsgg.github-substrate-v2.sandbox-seed-execution-binding/1")
        writer.WriteString("schemaJoin", "coordination-isolated-cas-final")
        writer.WriteStartObject("source")
        writer.WriteString("approvedArtifactSourceSha256", String.replicate 64 "4")
        writer.WriteString("candidateSha", candidate)
        writer.WriteString("repository", "FS-GG/.github")
        writer.WriteNumber("runAttempt", 2)
        writer.WriteNumber("runId", 7L)
        writer.WriteString("runNonce", nonce)
        writer.WriteString("workflowPath", ".github/workflows/github-substrate-v2-sandbox-qualification.yml")
        writer.WriteString("workflowRef", "refs/heads/main")
        writer.WriteString("workflowSha", workflow)
        writer.WriteEndObject()
        writer.WriteString("status", "bound-isolated-cas-authority")
        writer.WriteEndObject()
        writer.Flush()
        Array.append (stream.ToArray()) [| byte '\n' |]

    let withoutFingerprint = write None
    write (Some(shaBytes withoutFingerprint))

let private state bindingBytes =
    let request =
        {
            CandidateSha = candidate
            WorkflowRunId = 7L
            WorkflowRunAttempt = 2
            RunNonce = $"7-2-{candidate}"
            CorpusSha256 = shaBytes corpusBytes
        }

    let draft: MigrationSandboxSeedExecutionBinding =
        {
            Request = request
            WorkflowPath = ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
            WorkflowRef = "refs/heads/main"
            WorkflowSha = workflow
            RepositoryId = 1353050537L
            RepositoryNodeId = "R_kgDOUKXpqQ"
            ProjectNodeId = "PVT_kwDOEYAWY84BiESo"
            MintProofSha256 = shaBytes mintBytes
            ProtectedHostReceiptSha256 = shaBytes bindingBytes
            SeedPlanSha256 = shaBytes planBytes
            CorpusSha256 = shaBytes corpusBytes
            Prestate =
                {
                    Complete = true
                    RepositoryId = 1353050537L
                    ProjectNodeId = "PVT_kwDOEYAWY84BiESo"
                    NonceIssueCount = 0
                    NonceProjectItemCount = 0
                    SnapshotSha256 = shaBytes (raw "empty")
                }
            AdmittedEffects =
                [
                    MigrationSandboxSeedEffectKind.CreateNonceIssue
                    MigrationSandboxSeedEffectKind.AddProjectMembership
                    MigrationSandboxSeedEffectKind.RemoveProjectMembership
                    MigrationSandboxSeedEffectKind.DeleteNonceIssue
                ]
            Seal = ""
        }

    let sealedBinding =
        MigrationSandboxSeedExecutor.sealBinding
            (ReadOnlyMemory mintBytes)
            (ReadOnlyMemory bindingBytes)
            (ReadOnlyMemory planBytes)
            (ReadOnlyMemory corpusBytes)
            draft
        |> Result.defaultWith (fun error -> failwithf "%A" error)

    MigrationSandboxSeedExecutor.create sealedBinding 10L (String.replicate 40 "c")
    |> Result.defaultWith (fun error -> failwithf "%A" error)

let private nextState head current =
    let effect = current.Effects[current.ActiveIndex]

    MigrationSandboxSeedExecutor.transition
        {
            ExpectedGeneration = current.Generation
            ExpectedHead = current.Head
            NextHead = head
        }
        (MigrationSandboxSeedAction.PersistIntent effect.EffectId)
        current
    |> Result.defaultWith (fun error -> failwithf "%A" error)
    |> fst

let private runGit path args (input: byte array option) =
    let start = ProcessStartInfo("git")
    start.WorkingDirectory <- path
    start.RedirectStandardInput <- input.IsSome
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.UseShellExecute <- false

    for argument in args do
        start.ArgumentList.Add argument

    use child = Process.Start start

    input
    |> Option.iter (fun bytes ->
        child.StandardInput.BaseStream.Write(bytes, 0, bytes.Length)
        child.StandardInput.Close())

    let output = child.StandardOutput.ReadToEnd().Trim()
    let error = child.StandardError.ReadToEnd().Trim()
    child.WaitForExit()
    child.ExitCode, output, error

let private withBare action =
    let path = Path.Combine(Path.GetTempPath(), $"gs2-q4-remote-{Guid.NewGuid():N}.git")
    Directory.CreateDirectory path |> ignore
    let code, _, error = runGit path [ "init"; "--bare"; "--quiet" ] None

    if code <> 0 then
        failwith error

    try
        action path
    finally
        Directory.Delete(path, true)

type private BareTransport(path: string, unknown: bool, applyWrite: bool) =
    let mutable pushes: MigrationSandboxSeedRemotePush list = []
    member _.Pushes = List.rev pushes

    interface IMigrationSandboxSeedJournalRemoteTransport with
        member _.PushExact request =
            pushes <- request :: pushes

            if not applyWrite then
                MigrationSandboxSeedRemotePushOutcome.ResponseUnknown
            else
                let mutable failure = None

                for item in request.Objects do
                    let code, oid, error =
                        runGit path [ "hash-object"; "-w"; "--stdin"; "-t"; item.Kind ] (Some item.Bytes)

                    if code <> 0 || oid <> item.Oid then
                        failure <- Some error

                match failure with
                | Some reason -> MigrationSandboxSeedRemotePushOutcome.DefiniteRefusal reason
                | None ->
                    let old =
                        request.ForceWithLease.Substring(request.ForceWithLease.LastIndexOf(':') + 1)

                    let oldOid = if old = "" then String.replicate 40 "0" else old

                    let code, _, _ =
                        runGit path [ "update-ref"; request.RefName; request.Objects[2].Oid; oldOid ] None

                    if code <> 0 then
                        MigrationSandboxSeedRemotePushOutcome.ParentConflict
                    elif unknown then
                        MigrationSandboxSeedRemotePushOutcome.ResponseUnknown
                    else
                        MigrationSandboxSeedRemotePushOutcome.Accepted

        member _.ReadFresh refName =
            MigrationSandboxSeedJournal.readLocalBare path refName

let private nativeCasBytes =
    raw
        $"{{\"schema\":\"fsgg.gs2-09-7.sandbox-nonce-ref-cas-readback/1\",\"refName\":\"refs/heads/gs2-09-7/7-2-{candidate}/seed-journal\",\"repositoryId\":1353050537,\"complete\":true}}"

let private evidence bindingBytes nativeCas =
    {
        BindingBytes = bindingBytes
        NativeCasReadbackBytes = nativeCas
        WorkflowRunId = 7L
        WorkflowRunAttempt = 2
        WorkflowSha = workflow
        ApprovedArtifactSourceSha256 = String.replicate 64 "4"
    }

type private ExactVerifier(expectedBinding: byte array, expectedNativeCas: byte array, accept: bool) =
    interface IMigrationSandboxSeedIsolatedProvenanceVerifier with
        member _.VerifyExact actual =
            accept
            && actual.BindingBytes = expectedBinding
            && actual.NativeCasReadbackBytes = expectedNativeCas
            && actual.WorkflowRunId = 7L
            && actual.WorkflowRunAttempt = 2
            && actual.WorkflowSha = workflow
            && actual.ApprovedArtifactSourceSha256 = String.replicate 64 "4"

let private authority bytes =
    MigrationSandboxSeedJournalRemote.establishIsolatedCasAuthority
        (ExactVerifier(bytes, nativeCasBytes, true))
        (evidence bytes nativeCasBytes)
    |> Result.defaultWith (fun error -> failwithf "%A" error)

let private writeRemote bytes previous proposal transport =
    MigrationSandboxSeedJournalRemote.writeAndRead (authority bytes) previous proposal transport

let private proposal bytes =
    MigrationSandboxSeedJournal.plan None (state bytes)
    |> Result.defaultWith (fun error -> failwithf "%A" error)

let private snapshot repository refName =
    match MigrationSandboxSeedJournal.readLocalBare repository refName with
    | MigrationSandboxSeedJournalRead.Complete value -> value
    | value -> failwithf "%A" value

[<Fact>]
let ``current source only S2 cannot construct isolated CAS authority`` () =
    let current =
        raw
            "{\"activation\":false,\"authority\":\"unavailable\",\"schema\":\"fsgg.github-substrate-v2.sandbox-seed-execution-binding/1\"}\n"

    Assert.Equal(
        Error MigrationSandboxSeedRemoteFailure.IsolatedProvenanceRejected,
        MigrationSandboxSeedJournalRemote.establishIsolatedCasAuthority
            (ExactVerifier(current, nativeCasBytes, false))
            (evidence current nativeCasBytes)
    )

    Assert.Equal(
        Error MigrationSandboxSeedRemoteFailure.InvalidIsolatedCasBinding,
        MigrationSandboxSeedJournalRemote.establishIsolatedCasAuthority
            (ExactVerifier(current, [||], true))
            (evidence current [||])
    )

[<Fact>]
let ``repository ruleset bytes cannot substitute for native CAS readback`` () =
    let candidatePolicy =
        raw "{\"authority\":\"unavailable\",\"installed\":false,\"schema\":\"fsgg.gs2-09-7.seed-journal-policy/1\"}\n"

    let binding = installedBytes false

    Assert.Equal(
        Error MigrationSandboxSeedRemoteFailure.IsolatedProvenanceRejected,
        MigrationSandboxSeedJournalRemote.establishIsolatedCasAuthority
            (ExactVerifier(binding, candidatePolicy, false))
            (evidence binding candidatePolicy)
    )

[<Fact>]
let ``well formed self asserted receipts cannot create isolated CAS authority`` () =
    let binding = installedBytes false

    Assert.Equal(
        Error MigrationSandboxSeedRemoteFailure.IsolatedProvenanceRejected,
        MigrationSandboxSeedJournalRemote.establishIsolatedCasAuthority
            (ExactVerifier(binding, nativeCasBytes, false))
            (evidence binding nativeCasBytes)
    )

[<Fact>]
let ``exact objects absent lease and fresh readback apply`` () =
    withBare (fun repository ->
        let bytes = installedBytes false
        let proposal = proposal bytes
        let transport = BareTransport(repository, false, true)
        let result = writeRemote bytes None proposal transport

        match result with
        | Ok(MigrationSandboxSeedRemoteResult.Applied restored) -> Assert.False(restored.RecoveryOnly)
        | value -> failwithf "%A" value

        let push = transport.Pushes.Head
        Assert.Equal($"{proposal.CommitOid}:{proposal.RefName}", push.Refspec)
        Assert.Equal($"--force-with-lease={proposal.RefName}:", push.ForceWithLease)
        Assert.Equal<string list>([ "blob"; "tree"; "commit" ], push.Objects |> List.map _.Kind))

[<Fact>]
let ``lost response always rereads applied or stays journal only`` () =
    withBare (fun repository ->
        let bytes = installedBytes false
        let proposal = proposal bytes

        let applied =
            writeRemote bytes None proposal (BareTransport(repository, true, true))

        match applied with
        | Ok(MigrationSandboxSeedRemoteResult.Applied _) -> ()
        | value -> failwithf "%A" value)

    withBare (fun repository ->
        let bytes = installedBytes false
        let proposal = proposal bytes

        Assert.Equal(
            Ok(MigrationSandboxSeedRemoteResult.JournalRetryOnly "response-unknown-old-head"),
            writeRemote bytes None proposal (BareTransport(repository, true, false))
        ))

    let bytes = installedBytes false
    let mutable reread = false

    let timeout =
        { new IMigrationSandboxSeedJournalRemoteTransport with
            member _.PushExact _ = raise (TimeoutException())

            member _.ReadFresh _ =
                reread <- true
                MigrationSandboxSeedJournalRead.Missing
        }

    Assert.Equal(
        Ok(MigrationSandboxSeedRemoteResult.JournalRetryOnly "response-unknown-old-head"),
        writeRemote bytes None (proposal bytes) timeout
    )

    Assert.True(reread)

[<Fact>]
let ``identical stale absent retry is journal only`` () =
    withBare (fun repository ->
        let bytes = installedBytes false
        let proposal = proposal bytes

        writeRemote bytes None proposal (BareTransport(repository, false, true))
        |> ignore

        Assert.Equal(
            Ok(MigrationSandboxSeedRemoteResult.JournalRetryOnly "parent-conflict-identical-object"),
            writeRemote bytes None proposal (BareTransport(repository, false, true))
        ))

[<Fact>]
let ``stale parent loses to a competing writer`` () =
    withBare (fun repository ->
        let bytes = installedBytes false
        let genesis = proposal bytes

        writeRemote bytes None genesis (BareTransport(repository, false, true))
        |> ignore

        let previous = snapshot repository genesis.RefName

        let left =
            MigrationSandboxSeedJournal.plan (Some previous) (nextState (String.replicate 40 "d") (state bytes))
            |> Result.defaultWith (fun error -> failwithf "%A" error)

        let right =
            MigrationSandboxSeedJournal.plan (Some previous) (nextState (String.replicate 40 "e") (state bytes))
            |> Result.defaultWith (fun error -> failwithf "%A" error)

        writeRemote bytes (Some previous) left (BareTransport(repository, false, true))
        |> ignore

        Assert.Equal(
            Ok MigrationSandboxSeedRemoteResult.Conflict,
            writeRemote bytes (Some previous) right (BareTransport(repository, false, true))
        ))

[<Fact>]
let ``altered readback and malformed installed binding refuse`` () =
    let bytes = installedBytes false
    let proposal = proposal bytes

    let altered =
        { new IMigrationSandboxSeedJournalRemoteTransport with
            member _.PushExact _ =
                MigrationSandboxSeedRemotePushOutcome.Accepted

            member _.ReadFresh _ =
                MigrationSandboxSeedJournalRead.Indeterminate "altered-object"
        }

    Assert.Equal(
        Ok(MigrationSandboxSeedRemoteResult.Indeterminate "altered-object"),
        writeRemote bytes None proposal altered
    )

    let alteredProposal =
        { proposal with
            CommitBytes = Array.append proposal.CommitBytes [| byte 'x' |]
        }

    Assert.Equal(
        Error MigrationSandboxSeedRemoteFailure.InvalidJournalProposal,
        writeRemote bytes None alteredProposal altered
    )

    let malformed = installedBytes false |> Array.map id
    malformed[malformed.Length - 2] <- byte ' '

    Assert.Equal(
        Error MigrationSandboxSeedRemoteFailure.IsolatedProvenanceRejected,
        MigrationSandboxSeedJournalRemote.establishIsolatedCasAuthority
            (ExactVerifier(malformed, nativeCasBytes, false))
            (evidence malformed nativeCasBytes)
    )
