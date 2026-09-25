namespace FS.GG.Coordination.GitHub

open System
open System.Collections.Generic
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json

type MigrationWorkflowPolicyEvidence =
    { RequestUri: string; PayloadJson: string; PayloadSha256: string }

type MigrationWorkflowDefaultPermission =
    | WorkflowRead
    | WorkflowWrite

type MigrationWorkflowAccessLevel =
    | AccessNone
    | AccessUser
    | AccessOrganization

type MigrationPrivateForkWorkflowPolicy =
    { RunWorkflowsFromForkPullRequests: bool
      SendWriteTokensToWorkflows: bool
      SendSecretsAndVariables: bool
      RequireApprovalForForkPrWorkflows: bool }

type MigrationWorkflowPolicyPass =
    { DefaultPermission: MigrationWorkflowDefaultPermission
      CanApprovePullRequestReviews: bool
      ForkPullRequests: MigrationPrivateForkWorkflowPolicy
      AccessLevel: MigrationWorkflowAccessLevel
      WorkflowEvidence: MigrationWorkflowPolicyEvidence
      ForkEvidence: MigrationWorkflowPolicyEvidence
      AccessEvidence: MigrationWorkflowPolicyEvidence }

type MigrationRepositoryWorkflowPolicy =
    { RepositoryId: int64; RepositoryNodeId: string; RepositoryFullName: string
      RepositoryUpdatedAt: DateTimeOffset; Visibility: string
      InitialIdentity: MigrationWorkflowPolicyEvidence
      MiddleIdentity: MigrationWorkflowPolicyEvidence
      TerminalIdentity: MigrationWorkflowPolicyEvidence
      FirstPass: MigrationWorkflowPolicyPass
      SecondPass: MigrationWorkflowPolicyPass }

[<RequireQualifiedAccess>]
module MigrationRepositoryWorkflowPolicyRead =
    let private failure reason = Error(MigrationReadFailure.MalformedResponse reason)
    let private digest (body: string) =
        body |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let rec private uniqueMembers (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object ->
            let properties = value.EnumerateObject() |> Seq.toList
            let seen = HashSet<string>(StringComparer.Ordinal)
            match properties |> List.tryPick (fun property ->
                if seen.Add property.Name then None else Some property.Name) with
            | Some name -> Error(MigrationReadFailure.DuplicateIdentity $"json-member:{name}")
            | None ->
                properties
                |> List.fold (fun state property ->
                    state |> Result.bind (fun () -> uniqueMembers property.Value)) (Ok())
        | JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.fold (fun state item ->
                state |> Result.bind (fun () -> uniqueMembers item)) (Ok())
        | _ -> Ok()

    let private parse (body: string) =
        try
            use document = JsonDocument.Parse body
            let root = document.RootElement.Clone()
            uniqueMembers root |> Result.map (fun () -> root)
        with :? JsonException -> failure "invalid-json"

    let private property (name: string) (root: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if root.ValueKind = JsonValueKind.Object && root.TryGetProperty(name, &value) then Ok value
        else failure $"missing:{name}"

    let private stringValue name root =
        property name root |> Result.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then
                let text = value.GetString()
                if not (String.IsNullOrWhiteSpace text) && text = text.Trim() then Ok text
                else failure $"invalid:{name}"
            else failure $"invalid:{name}")

    let private positive name root =
        property name root |> Result.bind (fun value ->
            let mutable number = 0L
            if value.ValueKind = JsonValueKind.Number && value.TryGetInt64(&number) && number > 0L then Ok number
            else failure $"invalid:{name}")

    let private boolean name root =
        property name root |> Result.bind (fun value ->
            match value.ValueKind with
            | JsonValueKind.True -> Ok true
            | JsonValueKind.False -> Ok false
            | _ -> failure $"invalid:{name}")

    let private timestamp name root =
        stringValue name root |> Result.bind (fun text ->
            let mutable value = DateTimeOffset.MinValue
            if text.EndsWith("Z", StringComparison.Ordinal)
               && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                                               DateTimeStyles.AssumeUniversal, &value) then Ok value
            else failure $"invalid:{name}")

    let private exactFields allowed (root: JsonElement) =
        if root.ValueKind <> JsonValueKind.Object then failure "invalid:policy-object"
        else
            let actual = root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
            if actual = Set.ofList allowed then Ok()
            else failure "missing-or-unknown:policy-field"

    let private validSegment (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()
        && value <> "." && value <> ".."
        && value.IndexOfAny([| '/'; '?'; '#'; '\\'; '%'; '\r'; '\n' |]) < 0

    let private validOptions (options: MigrationGitHubReadOptions) =
        not (isNull options.ApiBase) && options.ApiBase.IsAbsoluteUri
        && options.ApiBase.Scheme = Uri.UriSchemeHttps
        && options.ApiBase.AbsolutePath = "/"
        && String.IsNullOrEmpty options.ApiBase.Query
        && String.IsNullOrEmpty options.ApiBase.Fragment
        && String.IsNullOrEmpty options.ApiBase.UserInfo
        && not (isNull options.GraphQLUri) && options.GraphQLUri.IsAbsoluteUri
        && options.GraphQLUri.Scheme = Uri.UriSchemeHttps
        && options.ExpectedRepositoryId > 0L
        && validSegment options.Owner && validSegment options.Repository
        && not (String.IsNullOrWhiteSpace options.UserAgent)
        && options.UserAgent = options.UserAgent.Trim()
        && options.UserAgent.IndexOfAny([| '\r'; '\n' |]) < 0
        && not (String.IsNullOrWhiteSpace options.Token)
        && options.Token = options.Token.Trim()
        && options.Token.IndexOfAny([| '\r'; '\n' |]) < 0

    let private headers (options: MigrationGitHubReadOptions) =
        Map.ofList
            [ "accept", "application/vnd.github+json"
              "x-github-api-version", ApiVersion.value ApiVersion.required
              "user-agent", options.UserAgent
              "authorization", $"Bearer {options.Token}" ]

    let private get (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) (uri: Uri) =
        let request =
            Rest { Method=Get; Uri=uri; Headers=headers options; Body=None
                   ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
        match transport.Send request with
        | Response value when value.StatusCode <> 200 ->
            Error(MigrationReadFailure.HttpRefused value.StatusCode)
        | Response value when value.Headers
                              |> Map.exists (fun name _ -> name.Equals("link", StringComparison.OrdinalIgnoreCase)) ->
            Error(MigrationReadFailure.PaginationRefused "unexpected-singleton-link")
        | Response value ->
            Ok { RequestUri=uri.AbsoluteUri; PayloadJson=value.Body; PayloadSha256=digest value.Body }
        | NetworkFailure | TimedOut -> Error MigrationReadFailure.TransportUnavailable

    let private identity (options: MigrationGitHubReadOptions) (evidence: MigrationWorkflowPolicyEvidence) =
        parse evidence.PayloadJson |> Result.bind (fun root ->
            match positive "id" root, stringValue "node_id" root,
                  stringValue "full_name" root, stringValue "visibility" root,
                  boolean "private" root, timestamp "updated_at" root,
                  stringValue "url" root with
            | Ok id, Ok nodeId, Ok fullName, Ok "private", Ok true, Ok updated, Ok url when
                id = options.ExpectedRepositoryId
                && fullName = $"{options.Owner}/{options.Repository}"
                && url = Uri(options.ApiBase,
                             $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}").AbsoluteUri ->
                Ok(id, nodeId, fullName, updated)
            | Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _ -> Error MigrationReadFailure.IdentityDrift
            | Error error, _, _, _, _, _, _ | _, Error error, _, _, _, _, _
            | _, _, Error error, _, _, _, _ | _, _, _, Error error, _, _, _
            | _, _, _, _, Error error, _, _ | _, _, _, _, _, Error error, _
            | _, _, _, _, _, _, Error error -> Error error)

    let private workflow (evidence: MigrationWorkflowPolicyEvidence) =
        parse evidence.PayloadJson |> Result.bind (fun root ->
            exactFields [ "default_workflow_permissions"; "can_approve_pull_request_reviews" ] root
            |> Result.bind (fun () ->
                match stringValue "default_workflow_permissions" root,
                      boolean "can_approve_pull_request_reviews" root with
                | Ok "read", Ok approval -> Ok(WorkflowRead, approval)
                | Ok "write", Ok approval -> Ok(WorkflowWrite, approval)
                | Ok _, Ok _ -> failure "unsupported:default-workflow-permission"
                | Error error, _ | _, Error error -> Error error))

    let private fork (evidence: MigrationWorkflowPolicyEvidence) =
        parse evidence.PayloadJson |> Result.bind (fun root ->
            exactFields [ "run_workflows_from_fork_pull_requests"; "send_write_tokens_to_workflows"
                          "send_secrets_and_variables"; "require_approval_for_fork_pr_workflows" ] root
            |> Result.bind (fun () ->
                match boolean "run_workflows_from_fork_pull_requests" root,
                      boolean "send_write_tokens_to_workflows" root,
                      boolean "send_secrets_and_variables" root,
                      boolean "require_approval_for_fork_pr_workflows" root with
                | Ok run, Ok write, Ok secrets, Ok approval ->
                    Ok { RunWorkflowsFromForkPullRequests=run
                         SendWriteTokensToWorkflows=write
                         SendSecretsAndVariables=secrets
                         RequireApprovalForForkPrWorkflows=approval }
                | Error error, _, _, _ | _, Error error, _, _
                | _, _, Error error, _ | _, _, _, Error error -> Error error))

    let private access (evidence: MigrationWorkflowPolicyEvidence) =
        parse evidence.PayloadJson |> Result.bind (fun root ->
            exactFields [ "access_level" ] root |> Result.bind (fun () ->
                stringValue "access_level" root |> Result.bind (function
                    | "none" -> Ok AccessNone
                    | "user" -> Ok AccessUser
                    | "organization" -> Ok AccessOrganization
                    | _ -> failure "unsupported:access-level")))

    let private pass options transport workflowUri forkUri accessUri =
        get options transport workflowUri |> Result.bind (fun workflowEvidence ->
            workflow workflowEvidence |> Result.bind (fun (permission, approval) ->
                get options transport forkUri |> Result.bind (fun forkEvidence ->
                    fork forkEvidence |> Result.bind (fun forkPolicy ->
                        get options transport accessUri |> Result.bind (fun accessEvidence ->
                            access accessEvidence |> Result.map (fun accessLevel ->
                                { DefaultPermission=permission
                                  CanApprovePullRequestReviews=approval
                                  ForkPullRequests=forkPolicy; AccessLevel=accessLevel
                                  WorkflowEvidence=workflowEvidence
                                  ForkEvidence=forkEvidence; AccessEvidence=accessEvidence }))))))

    let read (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (validOptions options) then Error MigrationReadFailure.InvalidOptions
        else
            let repositoryPath =
                $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}"
            let repositoryUri = Uri(options.ApiBase, repositoryPath)
            let workflowUri = Uri(options.ApiBase, $"{repositoryPath}/actions/permissions/workflow")
            let forkUri = Uri(options.ApiBase, $"{repositoryPath}/actions/permissions/fork-pr-workflows-private-repos")
            let accessUri = Uri(options.ApiBase, $"{repositoryPath}/actions/permissions/access")
            let readIdentity () =
                get options transport repositoryUri
                |> Result.bind (fun evidence -> identity options evidence |> Result.map (fun parsed -> evidence, parsed))
            readIdentity () |> Result.bind (fun (initialEvidence, initial) ->
                pass options transport workflowUri forkUri accessUri |> Result.bind (fun first ->
                    readIdentity () |> Result.bind (fun (middleEvidence, middle) ->
                        if middle <> initial || middleEvidence.PayloadSha256 <> initialEvidence.PayloadSha256 then
                            Error MigrationReadFailure.IdentityDrift
                        else
                            pass options transport workflowUri forkUri accessUri |> Result.bind (fun second ->
                                if second <> first then Error MigrationReadFailure.PopulationDrift
                                else
                                    readIdentity () |> Result.bind (fun (terminalEvidence, terminal) ->
                                        if terminal <> initial
                                           || terminalEvidence.PayloadSha256 <> initialEvidence.PayloadSha256 then
                                            Error MigrationReadFailure.IdentityDrift
                                        else
                                            let repoId, repoNode, fullName, revision = initial
                                            Ok { RepositoryId=repoId; RepositoryNodeId=repoNode
                                                 RepositoryFullName=fullName; RepositoryUpdatedAt=revision
                                                 Visibility="private"; InitialIdentity=initialEvidence
                                                 MiddleIdentity=middleEvidence; TerminalIdentity=terminalEvidence
                                                 FirstPass=first; SecondPass=second })))))
