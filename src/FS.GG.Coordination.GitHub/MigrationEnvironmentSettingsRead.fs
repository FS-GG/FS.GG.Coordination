namespace FS.GG.Coordination.GitHub

open System
open System.Collections.Generic
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json

type MigrationEnvironmentReviewer =
    { Kind: string; DatabaseId: int64; NodeId: string; Name: string }

type MigrationEnvironmentProtectionRule =
    { RuleId: int64; RuleNodeId: string; Kind: string; WaitMinutes: int option
      PreventSelfReview: bool option; Reviewers: MigrationEnvironmentReviewer list
      PayloadJson: string; PayloadSha256: string }

type MigrationEnvironmentBranchPolicy =
    { PolicyId: int64; PolicyNodeId: string; Name: string; Kind: string
      PayloadJson: string; PayloadSha256: string }

type MigrationEnvironmentCustomRule =
    { RuleId: int64; RuleNodeId: string; Enabled: bool; AppId: int64
      AppNodeId: string; AppSlug: string; PayloadJson: string; PayloadSha256: string }

type MigrationEnvironmentPageEvidence =
    { RequestedUri: string; PayloadJson: string; PayloadSha256: string; NextUri: string option }

type MigrationEnvironmentObservation =
    { EnvironmentId: int64; EnvironmentNodeId: string; Name: string; UpdatedAt: DateTimeOffset
      ProtectedBranches: bool; CustomBranchPolicies: bool
      ProtectionRules: MigrationEnvironmentProtectionRule list
      BranchPolicyPages: MigrationEnvironmentPageEvidence list
      BranchPolicies: MigrationEnvironmentBranchPolicy list
      CustomRulesUri: string; CustomRulesPayloadJson: string; CustomRulesPayloadSha256: string
      CustomRules: MigrationEnvironmentCustomRule list
      ListPayloadJson: string; ListPayloadSha256: string
      DetailUri: string; DetailPayloadJson: string; DetailPayloadSha256: string }

type MigrationEnvironmentSettings =
    { RepositoryId: int64; RepositoryNodeId: string; RepositoryFullName: string
      RepositoryUpdatedAt: DateTimeOffset; IdentityUri: string
      IdentityPayloadJson: string; IdentityPayloadSha256: string
      TerminalIdentityPayloadJson: string; TerminalIdentityPayloadSha256: string
      Pages: MigrationEnvironmentPageEvidence list; Terminal: bool; TotalCount: int
      Environments: MigrationEnvironmentObservation list }

[<RequireQualifiedAccess>]
module MigrationEnvironmentSettingsRead =
    let private fail reason = Error(MigrationReadFailure.MalformedResponse reason)
    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private sequence (items: Result<'a, MigrationReadFailure> list) =
        List.fold (fun state item ->
            state |> Result.bind (fun values -> item |> Result.map (fun value -> value :: values)))
            (Ok []) items
        |> Result.map List.rev

    let private unique key (items: 'a list) =
        let seen = HashSet<string>(StringComparer.Ordinal)
        match items |> List.tryPick (fun item -> let value = key item in if seen.Add value then None else Some value) with
        | Some value -> Error(MigrationReadFailure.DuplicateIdentity value)
        | None -> Ok items

    let rec private noDuplicateMembers (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object ->
            let properties = value.EnumerateObject() |> Seq.toList
            let names = HashSet<string>(StringComparer.Ordinal)
            match properties |> List.tryPick (fun p -> if names.Add p.Name then None else Some p.Name) with
            | Some name -> Error(MigrationReadFailure.DuplicateIdentity $"json-member:{name}")
            | None -> properties |> List.map (fun p -> noDuplicateMembers p.Value) |> sequence |> Result.map ignore
        | JsonValueKind.Array ->
            value.EnumerateArray() |> Seq.map noDuplicateMembers |> Seq.toList |> sequence |> Result.map ignore
        | _ -> Ok ()

    let private parse (body: string) =
        try
            use document = JsonDocument.Parse body
            let root = document.RootElement.Clone()
            noDuplicateMembers root |> Result.map (fun () -> root)
        with :? JsonException -> fail "invalid-json"

    let private prop (name: string) (value: JsonElement) =
        let mutable memberValue = Unchecked.defaultof<JsonElement>
        if value.ValueKind = JsonValueKind.Object && value.TryGetProperty(name, &memberValue) then Ok memberValue
        else fail $"missing:{name}"

    let private str name value =
        prop name value |> Result.bind (fun item ->
            if item.ValueKind = JsonValueKind.String then
                let text = item.GetString()
                if not (String.IsNullOrWhiteSpace text) && text = text.Trim() then Ok text
                else fail $"invalid:{name}"
            else fail $"invalid:{name}")

    let private positive name value =
        prop name value |> Result.bind (fun item ->
            let mutable parsed = 0L
            if item.ValueKind = JsonValueKind.Number && item.TryGetInt64(&parsed) && parsed > 0L then Ok parsed
            else fail $"invalid:{name}")

    let private count name value =
        prop name value |> Result.bind (fun item ->
            let mutable parsed = 0
            if item.ValueKind = JsonValueKind.Number && item.TryGetInt32(&parsed) && parsed >= 0 then Ok parsed
            else fail $"invalid:{name}")

    let private flag name value =
        prop name value |> Result.bind (fun item ->
            match item.ValueKind with
            | JsonValueKind.True -> Ok true
            | JsonValueKind.False -> Ok false
            | _ -> fail $"invalid:{name}")

    let private timestamp name value =
        str name value |> Result.bind (fun text ->
            let mutable parsed = DateTimeOffset.MinValue
            if text.EndsWith("Z", StringComparison.Ordinal)
               && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                                               DateTimeStyles.AssumeUniversal, &parsed) then Ok parsed
            else fail $"invalid:{name}")

    let private array name value =
        prop name value |> Result.bind (fun item ->
            if item.ValueKind = JsonValueKind.Array then Ok(item.EnumerateArray() |> Seq.toList)
            else fail $"invalid:{name}")

    let private validOptions (options: MigrationGitHubReadOptions) =
        not (isNull options.ApiBase) && options.ApiBase.IsAbsoluteUri
        && options.ApiBase.Scheme = Uri.UriSchemeHttps
        && options.ApiBase.AbsolutePath = "/"
        && not (isNull options.GraphQLUri) && options.GraphQLUri.IsAbsoluteUri
        && options.GraphQLUri.Scheme = Uri.UriSchemeHttps
        && options.ExpectedRepositoryId > 0L
        && ([ options.Owner; options.Repository; options.UserAgent ]
            |> List.forall (fun value -> not (String.IsNullOrWhiteSpace value) && value = value.Trim()
                                         && value.IndexOfAny([| '/'; '?'; '#'; '\\' |]) < 0))

    let private headers (options: MigrationGitHubReadOptions) =
        [ "accept", "application/vnd.github+json"
          "x-github-api-version", ApiVersion.value ApiVersion.required
          "user-agent", options.UserAgent
          if not (String.IsNullOrWhiteSpace options.Token) then "authorization", $"Bearer {options.Token}" ]
        |> Map.ofList

    let private get (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) (uri: Uri) =
        let request = Rest { Method=Get; Uri=uri; Headers=headers options; Body=None
                             ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
        match transport.Send request with
        | Response value when value.StatusCode = 200 -> Ok value
        | Response value -> Error(MigrationReadFailure.HttpRefused value.StatusCode)
        | NetworkFailure | TimedOut -> Error MigrationReadFailure.TransportUnavailable

    let private repoIdentity (options: MigrationGitHubReadOptions) transport uri =
        get options transport uri |> Result.bind (fun response ->
            parse response.Body |> Result.bind (fun root ->
                match positive "id" root, str "node_id" root, str "full_name" root,
                      timestamp "updated_at" root with
                | Ok id, Ok nodeId, Ok fullName, Ok updated when
                    id = options.ExpectedRepositoryId
                    && fullName = $"{options.Owner}/{options.Repository}" ->
                    Ok(id, nodeId, fullName, updated, response.Body)
                | Ok _, Ok _, Ok _, Ok _ -> Error MigrationReadFailure.IdentityDrift
                | Error error, _, _, _ | _, Error error, _, _
                | _, _, Error error, _ | _, _, _, Error error -> Error error))

    let private reviewer (value: JsonElement) =
        str "type" value |> Result.bind (fun kind ->
            if kind <> "User" && kind <> "Team" then fail "unsupported:reviewer-type"
            else prop "reviewer" value |> Result.bind (fun actor ->
                match positive "id" actor, str "node_id" actor,
                      str (if kind = "User" then "login" else "slug") actor with
                | Ok id, Ok nodeId, Ok name ->
                    Ok { Kind=kind; DatabaseId=id; NodeId=nodeId; Name=name }
                | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error))

    let private rule (value: JsonElement) =
        match positive "id" value, str "node_id" value, str "type" value with
        | Ok id, Ok nodeId, Ok kind ->
            let payload = value.GetRawText()
            let mk wait prevent reviewers =
                Ok { RuleId=id; RuleNodeId=nodeId; Kind=kind; WaitMinutes=wait
                     PreventSelfReview=prevent; Reviewers=reviewers
                     PayloadJson=payload; PayloadSha256=sha payload }
            match kind with
            | "wait_timer" ->
                count "wait_timer" value |> Result.bind (fun minutes ->
                    if minutes > 43200 then fail "invalid:wait-timer"
                    else mk (Some minutes) None [])
            | "required_reviewers" ->
                flag "prevent_self_review" value |> Result.bind (fun prevent ->
                    array "reviewers" value |> Result.bind (fun entries ->
                        if List.isEmpty entries || entries.Length > 6 then fail "invalid:reviewers"
                        else entries |> List.map reviewer |> sequence
                             |> Result.bind (unique (fun actor -> $"{actor.Kind}:{actor.DatabaseId}"))
                             |> Result.bind (mk None (Some prevent))))
            | "branch_policy" -> mk None None []
            | _ -> fail $"unsupported:environment-rule:{kind}"
        | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error

    let private branchMode (value: JsonElement) =
        prop "deployment_branch_policy" value |> Result.bind (fun item ->
            if item.ValueKind = JsonValueKind.Null then Ok(false, false)
            else
                match flag "protected_branches" item, flag "custom_branch_policies" item with
                | Ok protectedBranches, Ok custom when not (protectedBranches && custom) ->
                    Ok(protectedBranches, custom)
                | Ok _, Ok _ -> fail "invalid:deployment-branch-policy"
                | Error error, _ | _, Error error -> Error error)

    let private environment (options: MigrationGitHubReadOptions) (value: JsonElement) =
        match positive "id" value, str "node_id" value, str "name" value,
              timestamp "updated_at" value, branchMode value,
              array "protection_rules" value, str "url" value with
        | Ok id, Ok nodeId, Ok name, Ok updated, Ok(protectedBranches, custom), Ok entries, Ok url ->
            let expectedUrl =
                Uri(options.ApiBase,
                    $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}/environments/{Uri.EscapeDataString(name: string)}").AbsoluteUri
            if url <> expectedUrl then Error MigrationReadFailure.IdentityDrift
            else
                entries |> List.map rule |> sequence
                |> Result.bind (unique (fun item -> string item.RuleId))
                |> Result.bind (unique (fun item -> item.Kind))
                |> Result.bind (fun rules ->
                    let hasBranch = rules |> List.exists (fun item -> item.Kind = "branch_policy")
                    if hasBranch <> (protectedBranches || custom) then fail "invalid:branch-rule-consistency"
                    else Ok(id, nodeId, name, updated, protectedBranches, custom, rules))
        | Error error, _, _, _, _, _, _ | _, Error error, _, _, _, _, _
        | _, _, Error error, _, _, _, _ | _, _, _, Error error, _, _, _
        | _, _, _, _, Error error, _, _ | _, _, _, _, _, Error error, _
        | _, _, _, _, _, _, Error error -> Error error

    let private pageNumber (baseUri: Uri) expectedPage (candidate: Uri) =
        if candidate.Scheme <> baseUri.Scheme || candidate.Authority <> baseUri.Authority
           || candidate.AbsolutePath <> baseUri.AbsolutePath || not (String.IsNullOrEmpty candidate.Fragment) then
            Error(MigrationReadFailure.PaginationRefused "escaped-next-uri")
        else
            let parts = candidate.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            let values = parts |> Array.map (fun part -> part.Split('=', 2))
            if values.Length <> 2 || values |> Array.exists (fun part -> part.Length <> 2)
               || (values |> Array.map (fun part -> part.[0]) |> Set.ofArray |> Set.count) <> 2 then
                Error(MigrationReadFailure.PaginationRefused "invalid-next-query")
            else
                let parsed = values |> Array.map (fun part -> part.[0], part.[1]) |> Map.ofArray
                if Map.tryFind "per_page" parsed = Some "100"
                   && Map.tryFind "page" parsed = Some(string expectedPage) then Ok()
                else Error(MigrationReadFailure.PaginationRefused "invalid-next-page")

    let private nextUri (response: ResponseEnvelope) =
        let links =
            response.Headers
            |> Map.toList
            |> List.choose (fun (name, value) ->
                if name.Equals("link", StringComparison.OrdinalIgnoreCase) then Some value else None)
        match links with
        | [] -> Ok None
        | [ header ] ->
            let entries = header.Split(',', StringSplitOptions.RemoveEmptyEntries) |> Array.map _.Trim()
            let parsed =
                entries |> Array.map (fun entry ->
                    let parts = entry.Split(';', StringSplitOptions.RemoveEmptyEntries) |> Array.map _.Trim()
                    if parts.Length = 2
                       && parts.[0].StartsWith("<", StringComparison.Ordinal)
                       && parts.[0].EndsWith(">", StringComparison.Ordinal)
                       && Set.contains parts.[1] (set [ "rel=\"next\""; "rel=\"last\"";
                                                         "rel=\"first\""; "rel=\"prev\"" ]) then
                        Some(parts.[1], parts.[0].[1 .. parts.[0].Length - 2])
                    else None)
            if parsed |> Array.exists Option.isNone then
                Error(MigrationReadFailure.PaginationRefused "malformed-link")
            else
                let next = parsed |> Array.choose id |> Array.choose (fun (rel, url) ->
                    if rel = "rel=\"next\"" then Some url else None)
                if next.Length > 1 then Error(MigrationReadFailure.PaginationRefused "duplicate-next")
                elif next.Length = 1 then
                    let mutable uri = Unchecked.defaultof<Uri>
                    if Uri.TryCreate(next.[0], UriKind.Absolute, &uri) then Ok(Some uri)
                    else Error(MigrationReadFailure.PaginationRefused "invalid-next-uri")
                else Ok None
        | _ -> Error(MigrationReadFailure.PaginationRefused "duplicate-link-header")

    let private readPages (options: MigrationGitHubReadOptions) transport (initialUri: Uri)
                          (itemName: string) parseItem =
        let rec loop pageNumberValue (currentUri: Uri) seen pages values expectedTotal =
            if pageNumberValue > 1000 || Set.contains currentUri.AbsoluteUri seen then
                Error(MigrationReadFailure.PaginationRefused "repeated-or-excessive-page")
            else
                get options transport currentUri |> Result.bind (fun response ->
                    parse response.Body |> Result.bind (fun root ->
                        match count "total_count" root, array itemName root, nextUri response with
                        | Ok total, Ok entries, Ok next ->
                            if expectedTotal |> Option.exists ((<>) total) then
                                Error MigrationReadFailure.PopulationDrift
                            else
                                entries |> List.map (parseItem response.Body) |> sequence
                                |> Result.bind (fun parsed ->
                                    let evidence =
                                        { RequestedUri=currentUri.AbsoluteUri; PayloadJson=response.Body
                                          PayloadSha256=sha response.Body
                                          NextUri=next |> Option.map _.AbsoluteUri }
                                    let all = values @ parsed
                                    if all.Length > total then
                                        Error(MigrationReadFailure.PaginationRefused "count-exceeded")
                                    else
                                        match next with
                                        | Some uri ->
                                            pageNumber initialUri (pageNumberValue + 1) uri
                                            |> Result.bind (fun () ->
                                                if List.isEmpty entries then
                                                    Error(MigrationReadFailure.PaginationRefused "empty-nonterminal-page")
                                                else loop (pageNumberValue + 1) uri
                                                          (Set.add currentUri.AbsoluteUri seen)
                                                          (pages @ [ evidence ]) all (Some total))
                                        | None when all.Length = total -> Ok(pages @ [ evidence ], all, total)
                                        | None -> Error(MigrationReadFailure.PaginationRefused "incomplete-terminal-page"))
                        | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error))
        loop 1 initialUri Set.empty [] [] None

    let private branchPolicy (_: string) (value: JsonElement) =
        match positive "id" value, str "node_id" value, str "name" value, str "type" value with
        | Ok id, Ok nodeId, Ok name, Ok kind when kind = "branch" || kind = "tag" ->
            let payload = value.GetRawText()
            Ok { PolicyId=id; PolicyNodeId=nodeId; Name=name; Kind=kind
                 PayloadJson=payload; PayloadSha256=sha payload }
        | Ok _, Ok _, Ok _, Ok _ -> fail "unsupported:branch-policy-type"
        | Error error, _, _, _ | _, Error error, _, _
        | _, _, Error error, _ | _, _, _, Error error -> Error error

    let private customRule (value: JsonElement) =
        match positive "id" value, str "node_id" value, flag "enabled" value, prop "app" value with
        | Ok id, Ok nodeId, Ok enabled, Ok app ->
            match positive "id" app, str "node_id" app, str "slug" app with
            | Ok appId, Ok appNodeId, Ok slug ->
                let payload = value.GetRawText()
                Ok { RuleId=id; RuleNodeId=nodeId; Enabled=enabled; AppId=appId
                     AppNodeId=appNodeId; AppSlug=slug; PayloadJson=payload; PayloadSha256=sha payload }
            | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error
        | Error error, _, _, _ | _, Error error, _, _
        | _, _, Error error, _ | _, _, _, Error error -> Error error

    let private readCustom (options: MigrationGitHubReadOptions) transport uri =
        get options transport uri |> Result.bind (fun response ->
            parse response.Body |> Result.bind (fun root ->
                match count "total_count" root, array "custom_deployment_protection_rules" root with
                | Ok total, Ok values when total = values.Length ->
                    values |> List.map customRule |> sequence
                    |> Result.bind (unique (fun item -> string item.RuleId))
                    |> Result.map (fun rules -> response.Body, rules)
                | Ok _, Ok _ -> Error(MigrationReadFailure.PaginationRefused "custom-rule-count")
                | Error error, _ | _, Error error -> Error error))

    let read (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (validOptions options) then Error MigrationReadFailure.InvalidOptions
        else
            let repoPath = $"repos/{Uri.EscapeDataString options.Owner}/{Uri.EscapeDataString options.Repository}"
            let repoUri = Uri(options.ApiBase, repoPath)
            let listUri = Uri(options.ApiBase, $"{repoPath}/environments?per_page=100&page=1")
            repoIdentity options transport repoUri |> Result.bind (fun (repoId, repoNode, fullName, revision, identityBody) ->
                readPages options transport listUri "environments" (fun _ item ->
                    environment options item |> Result.map (fun parsed -> parsed, item.GetRawText()))
                |> Result.bind (fun (pages, summaries, total) ->
                    summaries |> List.map (fun ((id, node, name, updated, protectedBranches, custom, rules), payload) ->
                        id, node, name, updated, protectedBranches, custom, rules, payload)
                    |> unique (fun (id, _, _, _, _, _, _, _) -> string id)
                    |> Result.bind (unique (fun (_, node, _, _, _, _, _, _) -> node))
                    |> Result.bind (unique (fun (_, _, name, _, _, _, _, _) -> name))
                    |> Result.bind (fun summaries ->
                        let rec details remaining accumulated =
                            match remaining with
                            | [] -> Ok(List.rev accumulated)
                            | (id, node, name, updated, protectedBranches, custom, rules, payload) :: tail ->
                                let escaped = Uri.EscapeDataString(name: string)
                                let detailUri = Uri(options.ApiBase, $"{repoPath}/environments/{escaped}")
                                get options transport detailUri |> Result.bind (fun detailResponse ->
                                    parse detailResponse.Body |> Result.bind (fun detailRoot ->
                                        environment options detailRoot |> Result.bind (fun (detailId, detailNode, detailName, detailUpdated,
                                                                                   detailProtected, detailCustom, detailRules) ->
                                            if (id, node, name, updated, protectedBranches, custom, rules)
                                               <> (detailId, detailNode, detailName, detailUpdated,
                                                   detailProtected, detailCustom, detailRules) then
                                                Error MigrationReadFailure.PopulationDrift
                                            else
                                                let branchUri =
                                                    Uri(options.ApiBase, $"{repoPath}/environments/{escaped}/deployment-branch-policies?per_page=100&page=1")
                                                let branchRead =
                                                    if custom then
                                                        readPages options transport branchUri "branch_policies" branchPolicy
                                                        |> Result.bind (fun (branchPages, policies, _) ->
                                                            policies
                                                            |> unique (fun item -> string item.PolicyId)
                                                            |> Result.bind (unique (fun item -> item.PolicyNodeId))
                                                            |> Result.bind (unique (fun item -> $"{item.Kind}:{item.Name}"))
                                                            |> Result.map (fun values -> branchPages, values))
                                                    else Ok([], [])
                                                branchRead |> Result.bind (fun (branchPages, branchPolicies) ->
                                                    let customUri =
                                                        Uri(options.ApiBase, $"{repoPath}/environments/{escaped}/deployment_protection_rules")
                                                    readCustom options transport customUri |> Result.bind (fun (customBody, customRules) ->
                                                        let observed =
                                                            { EnvironmentId=id; EnvironmentNodeId=node; Name=name; UpdatedAt=updated
                                                              ProtectedBranches=protectedBranches; CustomBranchPolicies=custom
                                                              ProtectionRules=rules; BranchPolicyPages=branchPages
                                                              BranchPolicies=branchPolicies; CustomRulesUri=customUri.AbsoluteUri
                                                              CustomRulesPayloadJson=customBody; CustomRulesPayloadSha256=sha customBody
                                                              CustomRules=customRules; ListPayloadJson=payload; ListPayloadSha256=sha payload
                                                              DetailUri=detailUri.AbsoluteUri; DetailPayloadJson=detailResponse.Body
                                                              DetailPayloadSha256=sha detailResponse.Body }
                                                        details tail (observed :: accumulated))))))
                        details summaries [] |> Result.bind (fun observed ->
                            repoIdentity options transport repoUri |> Result.bind (fun (lastId, lastNode, lastName, lastRevision, terminalBody) ->
                                if (lastId, lastNode, lastName, lastRevision) <> (repoId, repoNode, fullName, revision) then
                                    Error MigrationReadFailure.IdentityDrift
                                else
                                    Ok { RepositoryId=repoId; RepositoryNodeId=repoNode
                                         RepositoryFullName=fullName; RepositoryUpdatedAt=revision
                                         IdentityUri=repoUri.AbsoluteUri; IdentityPayloadJson=identityBody
                                         IdentityPayloadSha256=sha identityBody
                                         TerminalIdentityPayloadJson=terminalBody
                                         TerminalIdentityPayloadSha256=sha terminalBody
                                         Pages=pages; Terminal=true; TotalCount=total; Environments=observed })))))
