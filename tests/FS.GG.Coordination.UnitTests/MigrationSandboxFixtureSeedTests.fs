module FS.GG.Coordination.MigrationSandboxFixtureSeedTests

open Xunit
open FS.GG.Coordination.Cli

let private candidate = String.replicate 40 "a"
let private request =
    { CandidateSha=candidate; WorkflowRunId=36086215835L; WorkflowRunAttempt=1
      RunNonce=$"36086215835-1-{candidate}"; CorpusSha256=String.replicate 64 "b" }

let private scope =
    { Complete=true; ActorLogin="fs-gg-cross-repo-dispatch[bot]"; ActorDatabaseId=297630107L
      RepositoryId=1353050537L; RepositoryNodeId="R_kgDOUKXpqQ"
      RepositoryFullName="FS-GG/FS.GG.GitHub.Substrate.Sandbox"; RepositoryPrivate=true
      RepositoryDescription="fsgg-sandbox-gs2-04-9 disposable qualification target; never production"
      ProjectOrganization="FS-GG"; ProjectNumber=2; ProjectNodeId="PVT_kwDOEYAWY84BiESo"
      ProjectTitle="fsgg-sandbox-gs2-04-9"; ProjectPrivate=true; ProjectClosed=false
      GrantedPermissions=set [ "issues:write"; "organization_projects:write" ] }

let private originalIssue =
    { RepositoryId=1353050537L; Number=1; DatabaseId=101L; NodeId="ISSUE_1"
      Title="fsgg-sandbox-gs2-04-9 fixture primary"; Body=Some "original body"
      State="open"; Labels=[ "baseline" ]; Revision="etag-1" }

type private FakePort() =
    let mutable observedScope = scope
    let mutable issue = originalIssue
    let mutable item: string option = None
    let mutable intent: MigrationSandboxSeedIntent option = None
    let mutable scopeReads = 0
    let mutable effects: string list = []
    let mutable persistFails = false
    let mutable secondPersistFails = false
    let mutable persistCount = 0
    let mutable issuePatchFailsAfterWrite = false
    let mutable addFailsAfterWrite = false
    let mutable deleteFailsAfterWrite = false
    let mutable incomplete = false
    let mutable extraNonceIssue = false
    let mutable residueAfterRestore = false
    let mutable scopeDriftsAfterIssuePatch = false
    let mutable unownedIssueDigest = String.replicate 64 "c"
    let mutable fixtureProjectNodeId = "PVT_kwDOEYAWY84BiESo"
    member _.Scope with get() = observedScope and set value = observedScope <- value
    member _.Issue with get() = issue and set value = issue <- value
    member _.Item with get() = item and set value = item <- value
    member _.ScopeReads = scopeReads
    member _.Effects = List.rev effects
    member _.HasIntent = intent.IsSome
    member _.RecordedItemId = intent |> Option.bind _.OwnedProjectItemId
    member _.TamperIntent edit = intent <- intent |> Option.map edit
    member _.PersistFails with get() = persistFails and set value = persistFails <- value
    member _.SecondPersistFails with get() = secondPersistFails and set value = secondPersistFails <- value
    member _.PersistCount = persistCount
    member _.IssuePatchFailsAfterWrite with get() = issuePatchFailsAfterWrite and set value = issuePatchFailsAfterWrite <- value
    member _.AddFailsAfterWrite with get() = addFailsAfterWrite and set value = addFailsAfterWrite <- value
    member _.DeleteFailsAfterWrite with get() = deleteFailsAfterWrite and set value = deleteFailsAfterWrite <- value
    member _.Incomplete with get() = incomplete and set value = incomplete <- value
    member _.ExtraNonceIssue with get() = extraNonceIssue and set value = extraNonceIssue <- value
    member _.ResidueAfterRestore with get() = residueAfterRestore and set value = residueAfterRestore <- value
    member _.ScopeDriftsAfterIssuePatch with get() = scopeDriftsAfterIssuePatch and set value = scopeDriftsAfterIssuePatch <- value
    member _.UnownedIssueDigest with get() = unownedIssueDigest and set value = unownedIssueDigest <- value
    member _.FixtureProjectNodeId with get() = fixtureProjectNodeId and set value = fixtureProjectNodeId <- value
    interface IMigrationSandboxFixtureSeedPort with
        member _.ObserveScope() =
            scopeReads <- scopeReads + 1
            Ok observedScope
        member _.ObserveFixture nonce =
            if nonce <> request.RunNonce then Error "wrong-nonce"
            else
                let owned =
                    if issue.Title.Contains($"[fsgg:gs2-09-7:{nonce}]") then [ issue.NodeId ] else []
                let owned = if extraNonceIssue then List.sort (owned @ [ "FOREIGN_ISSUE" ]) else owned
                Ok { Complete=not incomplete; ProjectNodeId=fixtureProjectNodeId; Issue=issue
                     ProjectItemIdsForIssue=item |> Option.toList
                     NonceIssueNodeIds=owned
                     UnownedIssuesSha256=unownedIssueDigest
                     UnownedProjectItemsSha256=String.replicate 64 "d" }
        member _.PersistIntent value =
            persistCount <- persistCount + 1
            if persistFails || (secondPersistFails && persistCount = 2) then Error "storage-unavailable"
            else intent <- Some value; Ok()
        member _.LoadIntent() = Ok intent
        member _.PatchIssue(repositoryId, nodeId, expectedRevision, title, body, state, labels) =
            if repositoryId <> issue.RepositoryId || nodeId <> issue.NodeId
               || expectedRevision <> issue.Revision then Error "stale-issue"
            else
                effects <- (if title = originalIssue.Title then "restore-issue" else "seed-issue") :: effects
                issue <- { issue with Title=title; Body=body; State=state; Labels=labels;
                                    Revision=issue.Revision + "x" }
                if title = originalIssue.Title && residueAfterRestore then extraNonceIssue <- true
                if title <> originalIssue.Title && scopeDriftsAfterIssuePatch then
                    observedScope <- { observedScope with ProjectNodeId="FOREIGN_PROJECT" }
                if issuePatchFailsAfterWrite then
                    issuePatchFailsAfterWrite <- false
                    Error "lost-patch-response"
                else Ok()
        member _.AddProjectItem(projectNodeId, issueNodeId) =
            if projectNodeId <> scope.ProjectNodeId || issueNodeId <> issue.NodeId || item.IsSome then
                Error "wrong-project-or-duplicate"
            else
                effects <- "add-project-item" :: effects
                item <- Some "ITEM_1"
                if addFailsAfterWrite then
                    addFailsAfterWrite <- false
                    Error "lost-add-response"
                else Ok "ITEM_1"
        member _.DeleteProjectItem(projectNodeId, itemNodeId) =
            if projectNodeId <> scope.ProjectNodeId || item <> Some itemNodeId then Error "wrong-item"
            else
                effects <- "delete-project-item" :: effects
                item <- None
                if deleteFailsAfterWrite then
                    deleteFailsAfterWrite <- false
                    Error "lost-delete-response"
                else Ok()

[<Fact>]
let ``seed persists exact prestate before writes and cleanup reverses to zero residue`` () =
    let fake = FakePort()
    let port = fake :> IMigrationSandboxFixtureSeedPort
    match MigrationSandboxFixtureSeed.seed request port with
    | Error failure -> failwithf "safe seed refused: %A" failure
    | Ok seeded ->
        Assert.True(fake.HasIntent)
        Assert.Equal(2, fake.PersistCount)
        Assert.Equal(Some "ITEM_1", fake.RecordedItemId)
        Assert.Equal("ISSUE_1", seeded.IssueNodeId)
        Assert.Equal("ITEM_1", seeded.ProjectItemId)
        Assert.Equal([ "seed-issue"; "add-project-item" ], fake.Effects)
        Assert.True(fake.ScopeReads >= 3)
        match MigrationSandboxFixtureSeed.cleanup request port with
        | Error failure -> failwithf "safe cleanup refused: %A" failure
        | Ok cleaned ->
            Assert.True(cleaned.ZeroResidue)
            Assert.Equal(seeded.IntentSha256, cleaned.IntentSha256)
            Assert.Equal([ "seed-issue"; "add-project-item"; "delete-project-item"; "restore-issue" ], fake.Effects)
            Assert.Equal(originalIssue.Title, fake.Issue.Title)
            Assert.Equal(originalIssue.Body, fake.Issue.Body)
            Assert.Equal(originalIssue.Labels, fake.Issue.Labels)
            Assert.True(fake.Item.IsNone)
            Assert.Equal(Ok cleaned, MigrationSandboxFixtureSeed.cleanup request port)
        let beforeRerun = fake.Effects
        Assert.Equal(Error MigrationSandboxSeedFailure.ExistingIntent,
                     MigrationSandboxFixtureSeed.seed request port)
        Assert.Equal(beforeRerun, fake.Effects)

[<Fact>]
let ``wrong workflow nonce actor repository Project or purpose refuses before effects`` () =
    let badNonce = { request with RunNonce="other-run" }
    let invalid = FakePort()
    Assert.Equal(Error MigrationSandboxSeedFailure.InvalidRequest,
                 MigrationSandboxFixtureSeed.seed badNonce invalid)
    Assert.Empty(invalid.Effects)
    Assert.Equal(0, invalid.ScopeReads)
    for changed in
        [ { scope with ActorDatabaseId=1L }
          { scope with ActorLogin="foreign[bot]" }
          { scope with RepositoryId=1L }
          { scope with RepositoryNodeId="FOREIGN" }
          { scope with RepositoryFullName="FS-GG/production" }
          { scope with ProjectNumber=3 }
          { scope with ProjectNodeId="FOREIGN_PROJECT" }
          { scope with ProjectTitle="foreign-purpose" }
          { scope with ProjectPrivate=false }
          { scope with RepositoryDescription="production" } ] do
        let fake = FakePort()
        fake.Scope <- changed
        Assert.Equal(Error MigrationSandboxSeedFailure.ScopeRefused,
                     MigrationSandboxFixtureSeed.seed request fake)
        Assert.Empty(fake.Effects)
        Assert.False(fake.HasIntent)

[<Fact>]
let ``missing grant incomplete census and failed intent persistence refuse before writes`` () =
    for permissions in [ set [ "issues:write" ]; set [ "organization_projects:write" ]; Set.empty ] do
        let fake = FakePort()
        fake.Scope <- { scope with GrantedPermissions=permissions }
        Assert.Equal(Error MigrationSandboxSeedFailure.MissingGrant,
                     MigrationSandboxFixtureSeed.seed request fake)
        Assert.Empty(fake.Effects)
    let incomplete = FakePort()
    incomplete.Incomplete <- true
    Assert.Equal(Error MigrationSandboxSeedFailure.IncompleteObservation,
                 MigrationSandboxFixtureSeed.seed request incomplete)
    Assert.Empty(incomplete.Effects)
    let foreignProjectRead = FakePort()
    foreignProjectRead.FixtureProjectNodeId <- "FOREIGN_PROJECT"
    Assert.Equal(Error MigrationSandboxSeedFailure.IncompleteObservation,
                 MigrationSandboxFixtureSeed.seed request foreignProjectRead)
    Assert.Empty(foreignProjectRead.Effects)
    let persist = FakePort()
    persist.PersistFails <- true
    match MigrationSandboxFixtureSeed.seed request persist with
    | Error(MigrationSandboxSeedFailure.EffectFailed "persist-intent:storage-unavailable") -> ()
    | outcome -> failwithf "failed durable intent did not refuse: %A" outcome
    Assert.Empty(persist.Effects)
    let occupied = FakePort()
    occupied.Item <- Some "EXISTING_ITEM"
    Assert.Equal(Error MigrationSandboxSeedFailure.UnsafePrestate,
                 MigrationSandboxFixtureSeed.seed request occupied)
    Assert.Empty(occupied.Effects)
    let markedBody = FakePort()
    markedBody.Issue <- { originalIssue with Body=Some($"old [fsgg:gs2-09-7:{request.RunNonce}]") }
    Assert.Equal(Error MigrationSandboxSeedFailure.UnsafePrestate,
                 MigrationSandboxFixtureSeed.seed request markedBody)
    Assert.Empty(markedBody.Effects)

[<Fact>]
let ``scope drift between seed effects refuses Project write and remains cleanable`` () =
    let fake = FakePort()
    fake.ScopeDriftsAfterIssuePatch <- true
    let port = fake :> IMigrationSandboxFixtureSeedPort
    Assert.Equal(Error MigrationSandboxSeedFailure.ScopeRefused,
                 MigrationSandboxFixtureSeed.seed request port)
    Assert.Equal([ "seed-issue" ], fake.Effects)
    fake.Scope <- scope
    Assert.True((MigrationSandboxFixtureSeed.cleanup request port).IsOk)
    Assert.Equal([ "seed-issue"; "restore-issue" ], fake.Effects)

[<Fact>]
let ``lost issue response remains cleanable but unknown Project item ownership refuses`` () =
    let issue = FakePort()
    issue.IssuePatchFailsAfterWrite <- true
    let issuePort = issue :> IMigrationSandboxFixtureSeedPort
    match MigrationSandboxFixtureSeed.seed request issuePort with
    | Error(MigrationSandboxSeedFailure.EffectFailed _) -> ()
    | outcome -> failwithf "lost issue response passed: %A" outcome
    Assert.Equal([ "seed-issue" ], issue.Effects)
    Assert.True(issue.HasIntent)
    Assert.True((MigrationSandboxFixtureSeed.cleanup request issuePort).IsOk)
    Assert.Equal([ "seed-issue"; "restore-issue" ], issue.Effects)

    let project = FakePort()
    project.AddFailsAfterWrite <- true
    let projectPort = project :> IMigrationSandboxFixtureSeedPort
    match MigrationSandboxFixtureSeed.seed request projectPort with
    | Error(MigrationSandboxSeedFailure.EffectFailed _) -> ()
    | outcome -> failwithf "lost Project response passed: %A" outcome
    Assert.Equal(Some "ITEM_1", project.Item)
    Assert.True(project.RecordedItemId.IsNone)
    Assert.Equal(Error(MigrationSandboxSeedFailure.IndeterminateProjectItem "ITEM_1"),
                 MigrationSandboxFixtureSeed.cleanup request projectPort)
    Assert.Equal([ "seed-issue"; "add-project-item" ], project.Effects)

    let lostPersistence = FakePort()
    lostPersistence.SecondPersistFails <- true
    let lostPort = lostPersistence :> IMigrationSandboxFixtureSeedPort
    match MigrationSandboxFixtureSeed.seed request lostPort with
    | Error(MigrationSandboxSeedFailure.EffectFailed "persist-item-provenance:storage-unavailable") -> ()
    | outcome -> failwithf "lost item provenance passed: %A" outcome
    Assert.Equal(Error(MigrationSandboxSeedFailure.IndeterminateProjectItem "ITEM_1"),
                 MigrationSandboxFixtureSeed.cleanup request lostPort)
    Assert.True(lostPersistence.RecordedItemId.IsNone)
    Assert.Equal([ "seed-issue"; "add-project-item" ], lostPersistence.Effects)

[<Fact>]
let ``cleanup refuses changed authority or foreign nonce then retries partial delete`` () =
    let fake = FakePort()
    let port = fake :> IMigrationSandboxFixtureSeedPort
    Assert.True((MigrationSandboxFixtureSeed.seed request port).IsOk)
    let before = fake.Effects
    let otherCandidate = String.replicate 40 "f"
    let otherRequest =
        { request with CandidateSha=otherCandidate
                       RunNonce=$"{request.WorkflowRunId}-{request.WorkflowRunAttempt}-{otherCandidate}" }
    Assert.Equal(Error MigrationSandboxSeedFailure.ChangedTarget,
                 MigrationSandboxFixtureSeed.cleanup otherRequest port)
    Assert.Equal(before, fake.Effects)
    fake.Item <- Some "FOREIGN_ITEM"
    Assert.Equal(Error MigrationSandboxSeedFailure.ChangedTarget,
                 MigrationSandboxFixtureSeed.cleanup request port)
    Assert.Equal(before, fake.Effects)
    fake.Item <- Some "ITEM_1"
    fake.Scope <- { scope with ProjectNodeId="FOREIGN" }
    Assert.Equal(Error MigrationSandboxSeedFailure.ScopeRefused,
                 MigrationSandboxFixtureSeed.cleanup request port)
    Assert.Equal(before, fake.Effects)
    fake.Scope <- scope
    fake.Scope <- { scope with GrantedPermissions=set [ "issues:write" ] }
    Assert.Equal(Error MigrationSandboxSeedFailure.MissingGrant,
                 MigrationSandboxFixtureSeed.cleanup request port)
    Assert.Equal(before, fake.Effects)
    fake.Scope <- scope
    fake.ExtraNonceIssue <- true
    Assert.Equal(Error MigrationSandboxSeedFailure.ChangedTarget,
                 MigrationSandboxFixtureSeed.cleanup request port)
    Assert.Equal(before, fake.Effects)
    fake.ExtraNonceIssue <- false
    fake.DeleteFailsAfterWrite <- true
    match MigrationSandboxFixtureSeed.cleanup request port with
    | Error(MigrationSandboxSeedFailure.EffectFailed "cleanup-project:lost-delete-response") -> ()
    | outcome -> failwithf "lost delete response passed: %A" outcome
    Assert.True(fake.Item.IsNone)
    Assert.True((MigrationSandboxFixtureSeed.cleanup request port).IsOk)
    Assert.Equal([ "seed-issue"; "add-project-item"; "delete-project-item"; "restore-issue" ], fake.Effects)

[<Fact>]
let ``cleanup refuses unowned drift and residual nonce after restoration`` () =
    let noIntent = FakePort()
    Assert.Equal(Error MigrationSandboxSeedFailure.MissingIntent,
                 MigrationSandboxFixtureSeed.cleanup request noIntent)
    Assert.Empty(noIntent.Effects)
    let tampered = FakePort()
    let tamperedPort = tampered :> IMigrationSandboxFixtureSeedPort
    Assert.True((MigrationSandboxFixtureSeed.seed request tamperedPort).IsOk)
    tampered.TamperIntent(fun value -> { value with OwnedProjectItemId=Some "FOREIGN_ITEM" })
    Assert.Equal(Error MigrationSandboxSeedFailure.ChangedTarget,
                 MigrationSandboxFixtureSeed.cleanup request tamperedPort)
    Assert.Equal([ "seed-issue"; "add-project-item" ], tampered.Effects)
    let drift = FakePort()
    let driftPort = drift :> IMigrationSandboxFixtureSeedPort
    Assert.True((MigrationSandboxFixtureSeed.seed request driftPort).IsOk)
    drift.UnownedIssueDigest <- String.replicate 64 "e"
    Assert.Equal(Error MigrationSandboxSeedFailure.ChangedTarget,
                 MigrationSandboxFixtureSeed.cleanup request driftPort)
    Assert.Equal([ "seed-issue"; "add-project-item" ], drift.Effects)

    let residue = FakePort()
    let residuePort = residue :> IMigrationSandboxFixtureSeedPort
    Assert.True((MigrationSandboxFixtureSeed.seed request residuePort).IsOk)
    residue.ResidueAfterRestore <- true
    Assert.Equal(Error MigrationSandboxSeedFailure.CleanupResidue,
                 MigrationSandboxFixtureSeed.cleanup request residuePort)
    Assert.Equal([ "seed-issue"; "add-project-item"; "delete-project-item"; "restore-issue" ], residue.Effects)
