module FS.GG.Coordination.MigrationSandboxSeedJournalTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Cli

let private bytes (value: string) =
    ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes value)

let private sha (value: string) =
    SHA256.HashData((bytes value).Span)
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let private candidate = String.replicate 40 "a"

let private mint, host, seedPlan, corpus =
    bytes "mint-v1", bytes "protected-host", bytes "seed-plan", bytes "corpus"

let private request =
    {
        CandidateSha = candidate
        WorkflowRunId = 7L
        WorkflowRunAttempt = 2
        RunNonce = $"7-2-{candidate}"
        CorpusSha256 = sha "corpus"
    }

let private draft: MigrationSandboxSeedExecutionBinding =
    {
        Request = request
        WorkflowPath = ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
        WorkflowRef = "refs/heads/main"
        WorkflowSha = String.replicate 40 "b"
        RepositoryId = 1353050537L
        RepositoryNodeId = "R_kgDOUKXpqQ"
        ProjectNodeId = "PVT_kwDOEYAWY84BiESo"
        MintProofSha256 = sha "mint-v1"
        ProtectedHostReceiptSha256 = sha "protected-host"
        SeedPlanSha256 = sha "seed-plan"
        CorpusSha256 = sha "corpus"
        Prestate =
            {
                Complete = true
                RepositoryId = 1353050537L
                ProjectNodeId = "PVT_kwDOEYAWY84BiESo"
                NonceIssueCount = 0
                NonceProjectItemCount = 0
                SnapshotSha256 = sha "empty"
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

let private binding () =
    MigrationSandboxSeedExecutor.sealBinding mint host seedPlan corpus draft
    |> Result.defaultWith (fun e -> failwithf "%A" e)

let private initial () =
    MigrationSandboxSeedExecutor.create (binding ()) 10L (String.replicate 40 "c")
    |> Result.defaultWith (fun e -> failwithf "%A" e)

let private nextWith head state =
    let effect = state.Effects[state.ActiveIndex]

    MigrationSandboxSeedExecutor.transition
        {
            ExpectedGeneration = state.Generation
            ExpectedHead = state.Head
            NextHead = head
        }
        (MigrationSandboxSeedAction.PersistIntent effect.EffectId)
        state
    |> Result.defaultWith (fun e -> failwithf "%A" e)
    |> fst

let private advance head action (state: MigrationSandboxSeedExecution) =
    MigrationSandboxSeedExecutor.transition
        {
            ExpectedGeneration = state.Generation
            ExpectedHead = state.Head
            NextHead = head
        }
        action
        state
    |> Result.defaultWith (fun e -> failwithf "%A" e)
    |> fst

let private runGitBytes (path: string) (args: string list) (input: byte array option) =
    let start = ProcessStartInfo("git")
    start.WorkingDirectory <- path
    start.RedirectStandardInput <- input.IsSome
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.UseShellExecute <- false

    for arg in args do
        start.ArgumentList.Add arg

    use child = Process.Start start

    input
    |> Option.iter (fun value ->
        child.StandardInput.BaseStream.Write(value, 0, value.Length)
        child.StandardInput.Close())

    let output = child.StandardOutput.ReadToEnd().Trim()
    let error = child.StandardError.ReadToEnd().Trim()
    child.WaitForExit()
    child.ExitCode, output, error

let private git (path: string) (args: string list) =
    let code, output, error = runGitBytes path args None

    if code <> 0 then
        failwithf "git %A: %s" args error

    output

let private writeObject (path: string) (kind: string) (expected: string) (data: byte array) =
    let code, actual, error =
        runGitBytes path [ "hash-object"; "-w"; "--stdin"; "-t"; kind ] (Some data)

    if code <> 0 then
        failwith error

    Assert.Equal(expected, actual)

let private apply path (plan: MigrationSandboxSeedJournalPlan) =
    writeObject path "blob" plan.BlobOid plan.StateBytes
    writeObject path "tree" plan.TreeOid plan.TreeBytes
    writeObject path "commit" plan.CommitOid plan.CommitBytes
    let old = plan.ExpectedParent |> Option.defaultValue (String.replicate 40 "0")

    runGitBytes path [ "update-ref"; plan.RefName; plan.CommitOid; old ] None
    |> fun (code, _, _) -> code

let private withBare (action: string -> unit) =
    let path =
        Path.Combine(Path.GetTempPath(), $"gs2-q4-seed-journal-{Guid.NewGuid():N}.git")

    Directory.CreateDirectory path |> ignore

    try
        git path [ "init"; "--bare"; "--quiet" ] |> ignore
        action path
    finally
        Directory.Delete(path, true)

let private getSnapshot =
    function
    | MigrationSandboxSeedJournalRead.Complete value -> value
    | value -> failwithf "%A" value

[<Fact>]
let ``genesis persists exact executor state and survives a fresh reader`` () =
    withBare (fun repository ->
        let plan =
            MigrationSandboxSeedJournal.plan None (initial ())
            |> Result.defaultWith (fun e -> failwithf "%A" e)

        Assert.Equal(0, apply repository plan)

        let first =
            MigrationSandboxSeedJournal.readLocalBare repository plan.RefName |> getSnapshot

        let restarted =
            MigrationSandboxSeedJournal.readLocalBare repository plan.RefName |> getSnapshot

        Assert.Equal(plan.CommitOid, first.CommitOid)
        Assert.Equal(first, restarted)
        Assert.Contains("\"kind\":\"create-nonce-issue\"", Encoding.UTF8.GetString first.StateBytes)
        Assert.Contains("\"stage\":\"planned\"", Encoding.UTF8.GetString first.StateBytes))

[<Fact>]
let ``expected parent CAS rejects a concurrent writer`` () =
    withBare (fun repository ->
        let genesis =
            MigrationSandboxSeedJournal.plan None (initial ())
            |> Result.defaultWith (fun e -> failwithf "%A" e)

        Assert.Equal(0, apply repository genesis)

        let previous =
            MigrationSandboxSeedJournal.readLocalBare repository genesis.RefName
            |> getSnapshot

        let left = nextWith (String.replicate 40 "d") (initial ())
        let right = nextWith (String.replicate 40 "e") (initial ())

        let leftPlan =
            MigrationSandboxSeedJournal.plan (Some previous) left
            |> Result.defaultWith (fun e -> failwithf "%A" e)

        let rightPlan =
            MigrationSandboxSeedJournal.plan (Some previous) right
            |> Result.defaultWith (fun e -> failwithf "%A" e)

        Assert.Equal(0, apply repository leftPlan)
        Assert.NotEqual(0, apply repository rightPlan)

        Assert.Equal(
            leftPlan.CommitOid,
            (MigrationSandboxSeedJournal.readLocalBare repository leftPlan.RefName
             |> getSnapshot)
                .CommitOid
        ))

[<Fact>]
let ``lost response reconciles only from exact reread`` () =
    withBare (fun repository ->
        let proposal =
            MigrationSandboxSeedJournal.plan None (initial ())
            |> Result.defaultWith (fun e -> failwithf "%A" e)

        Assert.Equal(
            MigrationSandboxSeedJournalReconciliation.ProvenAbsent,
            MigrationSandboxSeedJournal.reconcile
                proposal
                (MigrationSandboxSeedJournal.readLocalBare repository proposal.RefName)
        )

        Assert.Equal(0, apply repository proposal) // discard the write result as a simulated lost response
        let reread = MigrationSandboxSeedJournal.readLocalBare repository proposal.RefName

        Assert.Equal(
            MigrationSandboxSeedJournalReconciliation.Applied,
            MigrationSandboxSeedJournal.reconcile proposal reread
        ))

[<Fact>]
let ``restart append refuses stale executor generation`` () =
    withBare (fun repository ->
        let state = initial ()

        let genesis =
            MigrationSandboxSeedJournal.plan None state
            |> Result.defaultWith (fun e -> failwithf "%A" e)

        Assert.Equal(0, apply repository genesis)

        let previous =
            MigrationSandboxSeedJournal.readLocalBare repository genesis.RefName
            |> getSnapshot

        Assert.Equal(
            Error MigrationSandboxSeedJournalFailure.StaleGeneration,
            MigrationSandboxSeedJournal.plan (Some previous) state
        ))

[<Fact>]
let ``tampered state object is indeterminate and cannot confirm proposal`` () =
    withBare (fun repository ->
        let proposal =
            MigrationSandboxSeedJournal.plan None (initial ())
            |> Result.defaultWith (fun e -> failwithf "%A" e)

        let changed = Array.copy proposal.StateBytes
        changed[changed.Length - 2] <- byte 'x'

        let code, changedBlob, error =
            runGitBytes repository [ "hash-object"; "-w"; "--stdin" ] (Some changed)

        if code <> 0 then
            failwith error

        let treeLine = Encoding.UTF8.GetBytes($"100644 blob {changedBlob}\tstate.json\n")
        let code, changedTree, error = runGitBytes repository [ "mktree" ] (Some treeLine)

        if code <> 0 then
            failwith error

        let code, changedCommit, error =
            runGitBytes
                repository
                [
                    "-c"
                    "user.name=test"
                    "-c"
                    "user.email=test@example.invalid"
                    "commit-tree"
                    changedTree
                    "-m"
                    "tampered"
                ]
                None

        if code <> 0 then
            failwith error

        let old = String.replicate 40 "0"

        Assert.Equal(
            0,
            runGitBytes repository [ "update-ref"; proposal.RefName; changedCommit; old ] None
            |> fun (exitCode, _, _) -> exitCode
        )

        let reread = MigrationSandboxSeedJournal.readLocalBare repository proposal.RefName

        match reread with
        | MigrationSandboxSeedJournalRead.Indeterminate _ -> ()
        | value -> failwithf "tamper accepted: %A" value

        match MigrationSandboxSeedJournal.reconcile proposal reread with
        | MigrationSandboxSeedJournalReconciliation.Indeterminate _ -> ()
        | value -> failwithf "tamper reconciled: %A" value)

[<Fact>]
let ``journal retains in flight recovery result and reverse compensation custody`` () =
    withBare (fun repository ->
        let mutable state = initial ()

        let mutable proposal =
            MigrationSandboxSeedJournal.plan None state
            |> Result.defaultWith (fun e -> failwithf "%A" e)

        Assert.Equal(0, apply repository proposal)

        let mutable snapshot =
            MigrationSandboxSeedJournal.readLocalBare repository proposal.RefName
            |> getSnapshot

        let persist nextState =
            state <- nextState

            proposal <-
                MigrationSandboxSeedJournal.plan (Some snapshot) state
                |> Result.defaultWith (fun e -> failwithf "%A" e)

            Assert.Equal(0, apply repository proposal)

            snapshot <-
                MigrationSandboxSeedJournal.readLocalBare repository proposal.RefName
                |> getSnapshot

        let issue = state.Effects.Head
        persist (advance (String.replicate 40 "d") (MigrationSandboxSeedAction.PersistIntent issue.EffectId) state)
        persist (advance (String.replicate 40 "e") (MigrationSandboxSeedAction.MarkInFlight issue.EffectId) state)
        Assert.Contains("\"stage\":\"in-flight\"", Encoding.UTF8.GetString snapshot.StateBytes)

        persist (
            advance (String.replicate 40 "f") (MigrationSandboxSeedAction.RecordResponseUnknown issue.EffectId) state
        )

        Assert.Contains("\"stage\":\"recovery-pending\"", Encoding.UTF8.GetString snapshot.StateBytes)

        let issueOwned =
            {
                EffectId = issue.EffectId
                Kind = "issue"
                ResourceId = "ISSUE_NONCE"
                ParentResourceId = None
                RunNonce = request.RunNonce
                ReadbackSha256 = sha "issue-readback"
            }

        persist (
            advance
                (String.replicate 40 "1")
                (MigrationSandboxSeedAction.SettleApplied(issue.EffectId, issueOwned))
                state
        )

        let item = state.Effects[1]
        persist (advance (String.replicate 40 "2") (MigrationSandboxSeedAction.PersistIntent item.EffectId) state)
        persist (advance (String.replicate 40 "3") (MigrationSandboxSeedAction.MarkInFlight item.EffectId) state)

        let itemOwned =
            {
                EffectId = item.EffectId
                Kind = "project-item"
                ResourceId = "ITEM_NONCE"
                ParentResourceId = Some "ISSUE_NONCE"
                RunNonce = request.RunNonce
                ReadbackSha256 = sha "item-readback"
            }

        persist (
            advance
                (String.replicate 40 "4")
                (MigrationSandboxSeedAction.SettleApplied(item.EffectId, itemOwned))
                state
        )

        let compensation =
            MigrationSandboxSeedExecutor.beginCompensation
                {
                    ExpectedGeneration = state.Generation
                    ExpectedHead = state.Head
                    NextHead = String.replicate 40 "5"
                }
                state
            |> Result.defaultWith (fun e -> failwithf "%A" e)

        persist compensation
        let retained = Encoding.UTF8.GetString snapshot.StateBytes
        Assert.Contains("\"kind\":\"remove-project-membership\"", retained)
        Assert.Contains("\"kind\":\"delete-nonce-issue\"", retained)
        Assert.Contains("\"resourceId\":\"ITEM_NONCE\"", retained)
        Assert.Contains("\"resourceId\":\"ISSUE_NONCE\"", retained))
