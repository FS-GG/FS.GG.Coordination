namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text

type MigrationSandboxSeedRequest =
    { CandidateSha: string
      WorkflowRunId: int64
      WorkflowRunAttempt: int
      RunNonce: string
      CorpusSha256: string }

type MigrationSandboxScopeObservation =
    { Complete: bool
      ActorLogin: string
      ActorDatabaseId: int64
      RepositoryId: int64
      RepositoryNodeId: string
      RepositoryFullName: string
      RepositoryPrivate: bool
      RepositoryDescription: string
      ProjectOrganization: string
      ProjectNumber: int
      ProjectNodeId: string
      ProjectTitle: string
      ProjectPrivate: bool
      ProjectClosed: bool
      GrantedPermissions: Set<string> }

type MigrationSandboxIssueObservation =
    { RepositoryId: int64
      Number: int
      DatabaseId: int64
      NodeId: string
      Title: string
      Body: string option
      State: string
      Labels: string list
      Revision: string }

type MigrationSandboxFixtureObservation =
    { Complete: bool
      ProjectNodeId: string
      Issue: MigrationSandboxIssueObservation
      ProjectItemIdsForIssue: string list
      NonceIssueNodeIds: string list
      UnownedIssuesSha256: string
      UnownedProjectItemsSha256: string }

type MigrationSandboxSeedIntent =
    { Request: MigrationSandboxSeedRequest
      Prestate: MigrationSandboxFixtureObservation
      SeedTitle: string
      SeedBody: string option
      OwnedProjectItemId: string option
      Seal: string }

type MigrationSandboxSeedResult =
    { IntentSha256: string
      IssueNodeId: string
      ProjectItemId: string }

type MigrationSandboxCleanupResult =
    { IntentSha256: string
      ZeroResidue: bool }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedFailure =
    | InvalidRequest
    | ScopeRefused
    | MissingGrant
    | IncompleteObservation
    | UnsafePrestate
    | ExistingIntent
    | MissingIntent
    | IndeterminateProjectItem of itemNodeId:string
    | ChangedTarget
    | EffectFailed of string
    | CleanupResidue

type IMigrationSandboxFixtureSeedPort =
    abstract ObserveScope: unit -> Result<MigrationSandboxScopeObservation, string>
    abstract ObserveFixture: nonce:string -> Result<MigrationSandboxFixtureObservation, string>
    abstract PersistIntent: MigrationSandboxSeedIntent -> Result<unit, string>
    abstract LoadIntent: unit -> Result<MigrationSandboxSeedIntent option, string>
    abstract PatchIssue:
        repositoryId:int64 * issueNodeId:string * expectedRevision:string *
        title:string * body:string option * state:string * labels:string list -> Result<unit, string>
    abstract AddProjectItem: projectNodeId:string * issueNodeId:string -> Result<string, string>
    abstract DeleteProjectItem: projectNodeId:string * itemNodeId:string -> Result<unit, string>

[<RequireQualifiedAccess>]
module MigrationSandboxFixtureSeed =
    [<Literal>]
    let private repositoryId = 1353050537L
    [<Literal>]
    let private repositoryNodeId = "R_kgDOUKXpqQ"
    [<Literal>]
    let private repositoryName = "FS-GG/FS.GG.GitHub.Substrate.Sandbox"
    [<Literal>]
    let private projectNodeId = "PVT_kwDOEYAWY84BiESo"
    [<Literal>]
    let private purpose = "fsgg-sandbox-gs2-04-9"
    [<Literal>]
    let private description = "fsgg-sandbox-gs2-04-9 disposable qualification target; never production"
    [<Literal>]
    let private issueTitle = "fsgg-sandbox-gs2-04-9 fixture primary"

    let private isSha length (value: string) =
        not (isNull value) && value.Length = length
        && (value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))

    let private text (value: string) = not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private validRequest (request: MigrationSandboxSeedRequest) =
        isSha 40 request.CandidateSha && isSha 64 request.CorpusSha256
        && request.WorkflowRunId > 0L && request.WorkflowRunAttempt > 0
        && request.RunNonce = $"{request.WorkflowRunId}-{request.WorkflowRunAttempt}-{request.CandidateSha}"
        && request.RunNonce.Length <= 160

    let private scope (port: IMigrationSandboxFixtureSeedPort) =
        port.ObserveScope()
        |> Result.mapError (fun _ -> MigrationSandboxSeedFailure.ScopeRefused)
        |> Result.bind (fun observed ->
            if not observed.Complete
               || observed.ActorLogin <> "fs-gg-cross-repo-dispatch[bot]"
               || observed.ActorDatabaseId <> 297630107L
               || observed.RepositoryId <> repositoryId
               || observed.RepositoryNodeId <> repositoryNodeId
               || observed.RepositoryFullName <> repositoryName
               || not observed.RepositoryPrivate
               || observed.RepositoryDescription <> description
               || observed.ProjectOrganization <> "FS-GG"
               || observed.ProjectNumber <> 2
               || observed.ProjectNodeId <> projectNodeId
               || observed.ProjectTitle <> purpose
               || not observed.ProjectPrivate || observed.ProjectClosed then
                Error MigrationSandboxSeedFailure.ScopeRefused
            elif not (observed.GrantedPermissions.Contains "issues:write")
                 || not (observed.GrantedPermissions.Contains "organization_projects:write") then
                Error MigrationSandboxSeedFailure.MissingGrant
            else Ok())

    let private sortedDistinct values =
        values = List.sort values && values.Length = (values |> Set.ofList |> Set.count)

    let private fixture request (port: IMigrationSandboxFixtureSeedPort) =
        port.ObserveFixture request.RunNonce
        |> Result.mapError (fun _ -> MigrationSandboxSeedFailure.IncompleteObservation)
        |> Result.bind (fun observed ->
            let issue = observed.Issue
            if not observed.Complete || observed.ProjectNodeId <> projectNodeId
               || issue.RepositoryId <> repositoryId
               || issue.Number <> 1 || issue.DatabaseId <= 0L || not (text issue.NodeId)
               || not (text issue.Title) || not (text issue.Revision)
               || issue.State <> "open" || not (sortedDistinct issue.Labels)
               || not (issue.Labels |> List.forall text)
               || (issue.Body |> Option.exists isNull)
               || not (sortedDistinct observed.ProjectItemIdsForIssue)
               || not (sortedDistinct observed.NonceIssueNodeIds)
               || not (observed.ProjectItemIdsForIssue |> List.forall text)
               || not (observed.NonceIssueNodeIds |> List.forall text)
               || not (isSha 64 observed.UnownedIssuesSha256)
               || not (isSha 64 observed.UnownedProjectItemsSha256) then
                Error MigrationSandboxSeedFailure.IncompleteObservation
            else Ok observed)

    let private sameIdentity (expected: MigrationSandboxIssueObservation) (actual: MigrationSandboxIssueObservation) =
        actual.RepositoryId = expected.RepositoryId && actual.Number = expected.Number
        && actual.DatabaseId = expected.DatabaseId && actual.NodeId = expected.NodeId

    let private sameValues (expected: MigrationSandboxIssueObservation) (actual: MigrationSandboxIssueObservation) =
        actual.Title = expected.Title && actual.Body = expected.Body
        && actual.State = expected.State && actual.Labels = expected.Labels

    let private sameUnowned (pre: MigrationSandboxFixtureObservation) (actual: MigrationSandboxFixtureObservation) =
        pre.UnownedIssuesSha256 = actual.UnownedIssuesSha256
        && pre.UnownedProjectItemsSha256 = actual.UnownedProjectItemsSha256

    let private validInitialPrestate (request: MigrationSandboxSeedRequest)
                                    (pre: MigrationSandboxFixtureObservation) =
        let issue = pre.Issue
        let marker = $"[fsgg:gs2-09-7:{request.RunNonce}]"
        pre.Complete && pre.ProjectNodeId = projectNodeId
        && issue.RepositoryId = repositoryId && issue.Number = 1
        && issue.DatabaseId > 0L && text issue.NodeId && text issue.Revision
        && issue.Title = issueTitle && issue.State = "open"
        && sortedDistinct issue.Labels && (issue.Labels |> List.forall text)
        && not (issue.Body |> Option.exists isNull)
        && not (issue.Body |> Option.exists (fun body -> body.Contains(marker, StringComparison.Ordinal)))
        && pre.ProjectItemIdsForIssue.IsEmpty && pre.NonceIssueNodeIds.IsEmpty
        && isSha 64 pre.UnownedIssuesSha256 && isSha 64 pre.UnownedProjectItemsSha256

    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()

    let private seal (request: MigrationSandboxSeedRequest) (pre: MigrationSandboxFixtureObservation)
                     title body ownedItem =
        let frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
        let issue = pre.Issue
        [ yield request.CandidateSha
          yield string request.WorkflowRunId
          yield string request.WorkflowRunAttempt
          yield request.RunNonce
          yield request.CorpusSha256
          yield string pre.Complete
          yield pre.ProjectNodeId
          yield string issue.RepositoryId
          yield string issue.Number
          yield string issue.DatabaseId
          yield issue.NodeId
          yield issue.Title
          yield match issue.Body with None -> "body:none" | Some value -> "body:some:" + value
          yield issue.State
          yield issue.Revision
          yield string issue.Labels.Length
          for label in issue.Labels do yield label
          yield pre.UnownedIssuesSha256
          yield pre.UnownedProjectItemsSha256
          yield title
          yield match body with None -> "seed-body:none" | Some value -> "seed-body:some:" + value
          yield match ownedItem with None -> "owned-project:none" | Some value -> "owned-project:some:" + value ]
        |> List.map frame |> String.concat "" |> sha

    let private seededValues intent actual =
        let issue = actual.Issue
        sameIdentity intent.Prestate.Issue issue
        && issue.Title = intent.SeedTitle && issue.Body = intent.SeedBody
        && issue.State = intent.Prestate.Issue.State
        && issue.Labels = intent.Prestate.Issue.Labels
        && actual.NonceIssueNodeIds = [ issue.NodeId ]
        && sameUnowned intent.Prestate actual

    let private validateIntent request intent =
        intent.Request = request
        && intent.Seal = seal intent.Request intent.Prestate intent.SeedTitle intent.SeedBody intent.OwnedProjectItemId
        && (intent.OwnedProjectItemId |> Option.forall text)
        && validInitialPrestate request intent.Prestate

    let seed request (port: IMigrationSandboxFixtureSeedPort) =
        if not (validRequest request) then Error MigrationSandboxSeedFailure.InvalidRequest
        else
            port.LoadIntent()
            |> Result.mapError (fun reason -> MigrationSandboxSeedFailure.EffectFailed $"load-intent:{reason}")
            |> Result.bind (function
                | Some _ -> Error MigrationSandboxSeedFailure.ExistingIntent
                | None -> Ok())
            |> Result.bind (fun () -> scope port)
            |> Result.bind (fun () -> fixture request port)
            |> Result.bind (fun pre ->
                if not (validInitialPrestate request pre) then
                    Error MigrationSandboxSeedFailure.UnsafePrestate
                else
                    let marker = $"[fsgg:gs2-09-7:{request.RunNonce}]"
                    let title = $"{issueTitle} {marker}"
                    let baselineBody = pre.Issue.Body |> Option.defaultValue ""
                    let body = Some($"{baselineBody}\n\n{marker}\ncorpus-sha256:{request.CorpusSha256}")
                    let intent =
                        { Request=request; Prestate=pre; SeedTitle=title; SeedBody=body
                          OwnedProjectItemId=None; Seal=seal request pre title body None }
                    port.PersistIntent intent
                    |> Result.mapError (fun reason -> MigrationSandboxSeedFailure.EffectFailed $"persist-intent:{reason}")
                    |> Result.bind (fun () ->
                        scope port
                        |> Result.bind (fun () -> fixture request port)
                        |> Result.bind (fun fresh ->
                            if fresh <> pre then Error MigrationSandboxSeedFailure.ChangedTarget
                            else
                                port.PatchIssue(repositoryId, pre.Issue.NodeId, pre.Issue.Revision,
                                                title, body, pre.Issue.State, pre.Issue.Labels)
                                |> Result.mapError (fun reason -> MigrationSandboxSeedFailure.EffectFailed $"seed-issue:{reason}"))
                        |> Result.bind (fun () -> fixture request port)
                        |> Result.bind (fun afterIssue ->
                            if not (seededValues intent afterIssue) || not afterIssue.ProjectItemIdsForIssue.IsEmpty then
                                Error MigrationSandboxSeedFailure.ChangedTarget
                            else
                                scope port
                                |> Result.bind (fun () -> fixture request port)
                                |> Result.bind (fun fresh ->
                                    if fresh <> afterIssue then Error MigrationSandboxSeedFailure.ChangedTarget
                                    else
                                        port.AddProjectItem(projectNodeId, pre.Issue.NodeId)
                                        |> Result.mapError (fun reason -> MigrationSandboxSeedFailure.EffectFailed $"seed-project:{reason}"))
                                |> Result.bind (fun itemId ->
                                    if not (text itemId) then Error(MigrationSandboxSeedFailure.EffectFailed "invalid-project-item-id")
                                    else
                                        let owned =
                                            { intent with OwnedProjectItemId=Some itemId
                                                          Seal=seal request pre title body (Some itemId) }
                                        port.PersistIntent owned
                                        |> Result.mapError (fun reason -> MigrationSandboxSeedFailure.EffectFailed $"persist-item-provenance:{reason}")
                                        |> Result.bind (fun () -> fixture request port)
                                        |> Result.bind (fun final ->
                                            if seededValues owned final && final.ProjectItemIdsForIssue = [ itemId ] then
                                                Ok { IntentSha256=owned.Seal; IssueNodeId=pre.Issue.NodeId
                                                     ProjectItemId=itemId }
                                            else Error MigrationSandboxSeedFailure.ChangedTarget)))))

    let cleanup request (port: IMigrationSandboxFixtureSeedPort) =
        if not (validRequest request) then Error MigrationSandboxSeedFailure.InvalidRequest
        else
            port.LoadIntent()
            |> Result.mapError (fun reason -> MigrationSandboxSeedFailure.EffectFailed $"load-intent:{reason}")
            |> Result.bind (function None -> Error MigrationSandboxSeedFailure.MissingIntent | Some intent -> Ok intent)
            |> Result.bind (fun intent ->
                if not (validateIntent request intent) then Error MigrationSandboxSeedFailure.ChangedTarget
                else
                    let pre = intent.Prestate
                    let acceptable current =
                        sameIdentity pre.Issue current.Issue
                        && sameUnowned pre current
                        && current.ProjectItemIdsForIssue.Length <= 1
                        && (current.NonceIssueNodeIds.IsEmpty
                            || current.NonceIssueNodeIds = [ pre.Issue.NodeId ])
                    let baseline current =
                        sameValues pre.Issue current.Issue && current.NonceIssueNodeIds.IsEmpty
                    let recheck expected =
                        scope port
                        |> Result.bind (fun () -> fixture request port)
                        |> Result.bind (fun fresh ->
                            if fresh = expected then Ok()
                            else Error MigrationSandboxSeedFailure.ChangedTarget)
                    scope port
                    |> Result.bind (fun () -> fixture request port)
                    |> Result.bind (fun current ->
                        if not (acceptable current) then Error MigrationSandboxSeedFailure.ChangedTarget
                        elif not current.ProjectItemIdsForIssue.IsEmpty
                             && intent.OwnedProjectItemId.IsNone then
                            Error(MigrationSandboxSeedFailure.IndeterminateProjectItem current.ProjectItemIdsForIssue.Head)
                        elif not current.ProjectItemIdsForIssue.IsEmpty
                             && (not (seededValues intent current)
                                 || intent.OwnedProjectItemId <> Some current.ProjectItemIdsForIssue.Head) then
                            Error MigrationSandboxSeedFailure.ChangedTarget
                        elif not (baseline current || seededValues intent current) then
                            Error MigrationSandboxSeedFailure.ChangedTarget
                        else
                            let removeItem =
                                match current.ProjectItemIdsForIssue with
                                | [] -> Ok()
                                | [ itemId ] ->
                                    recheck current
                                    |> Result.bind (fun () ->
                                        port.DeleteProjectItem(projectNodeId, itemId)
                                        |> Result.mapError (fun reason -> MigrationSandboxSeedFailure.EffectFailed $"cleanup-project:{reason}"))
                                | _ -> Error MigrationSandboxSeedFailure.ChangedTarget
                            removeItem
                            |> Result.bind (fun () -> fixture request port)
                            |> Result.bind (fun afterItem ->
                                if not (acceptable afterItem) || not afterItem.ProjectItemIdsForIssue.IsEmpty then
                                    Error MigrationSandboxSeedFailure.ChangedTarget
                                elif baseline afterItem then Ok()
                                elif seededValues intent afterItem then
                                    recheck afterItem
                                    |> Result.bind (fun () ->
                                        port.PatchIssue(repositoryId, pre.Issue.NodeId, afterItem.Issue.Revision,
                                                        pre.Issue.Title, pre.Issue.Body, pre.Issue.State, pre.Issue.Labels)
                                        |> Result.mapError (fun reason -> MigrationSandboxSeedFailure.EffectFailed $"cleanup-issue:{reason}"))
                                else Error MigrationSandboxSeedFailure.ChangedTarget)
                            |> Result.bind (fun () -> scope port)
                            |> Result.bind (fun () -> fixture request port)
                            |> Result.bind (fun final ->
                                if acceptable final && baseline final && final.ProjectItemIdsForIssue.IsEmpty then
                                    Ok { IntentSha256=intent.Seal; ZeroResidue=true }
                                else Error MigrationSandboxSeedFailure.CleanupResidue)))
