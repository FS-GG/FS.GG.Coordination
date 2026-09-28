namespace FS.GG.Coordination.Cli

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

type MigrationSandboxSeedProviderRequest =
    { Method: string
      Uri: string
      ApiVersion: string
      Body: byte array option
      EffectId: string
      IdempotencyKey: string }

type MigrationSandboxSeedProviderEvidence =
    { PreviousSnapshot: MigrationSandboxSeedJournalSnapshot option
      CurrentSnapshot: MigrationSandboxSeedJournalSnapshot
      MintProofBytes: ReadOnlyMemory<byte>
      ProtectedHostReceiptBytes: ReadOnlyMemory<byte>
      SeedPlanBytes: ReadOnlyMemory<byte>
      CorpusBytes: ReadOnlyMemory<byte> }

type MigrationSandboxSeedProviderPage =
    { Request: MigrationSandboxSeedProviderRequest
      StatusCode: int
      ResponseBody: ReadOnlyMemory<byte> }

type MigrationSandboxSeedProviderPass =
    { IssuePages: MigrationSandboxSeedProviderPage list
      ProjectPages: MigrationSandboxSeedProviderPage list }

type MigrationSandboxSeedProviderCapture =
    { MutationResponse: MigrationSandboxSeedProviderPage option
      FirstPass: MigrationSandboxSeedProviderPass
      SecondPass: MigrationSandboxSeedProviderPass }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedProviderAuthority =
    | SourceOnlyUnavailable

type MigrationSandboxSeedProviderPlan =
    { MutationTemplate: MigrationSandboxSeedProviderRequest
      DispatchRequest: MigrationSandboxSeedProviderRequest option
      FirstIssueRead: MigrationSandboxSeedProviderRequest
      FirstProjectRead: MigrationSandboxSeedProviderRequest
      UnknownResponseRequiresRecoveryPending: bool
      DispatchAuthority: MigrationSandboxSeedProviderAuthority }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedProviderDisposition =
    | Applied of MigrationSandboxOwnedResource
    | ProvenAbsent
    | RecoveryPending of reason: string
    | Conflict of reason: string
    | Indeterminate of reason: string

[<RequireQualifiedAccess>]
type MigrationSandboxSeedProviderFailure =
    | InvalidSealedExecution
    | ProtectedHostEvidenceInvalid
    | WrongEffect
    | EffectNotDispatchable
    | InvalidOwnership
    | CaptureIncomplete
    | CaptureDrift
    | ForeignRead
    | MalformedResponse

[<RequireQualifiedAccess>]
module MigrationSandboxSeedProvider =
    [<Literal>]
    let private apiVersion = "2022-11-28"
    [<Literal>]
    let private graphqlUri = "/graphql"
    [<Literal>]
    let private repositoryId = 1353050537L
    [<Literal>]
    let private repositoryNode = "R_kgDOUKXpqQ"
    [<Literal>]
    let private projectNode = "PVT_kwDOEYAWY84BiESo"
    [<Literal>]
    let private actorLogin = "fs-gg-cross-repo-dispatch[bot]"
    [<Literal>]
    let private actorId = 297630107L

    let private utf8 = UTF8Encoding(false, true)

    let private sha (bytes: ReadOnlyMemory<byte>) =
        SHA256.HashData(bytes.Span) |> Convert.ToHexString |> _.ToLowerInvariant()

    let private hex length (value: string) =
        not (isNull value) && value.Length = length
        && value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f')

    let private sameBytes (left: byte array option) (right: byte array option) =
        match left, right with
        | None, None -> true
        | Some a, Some b -> a.AsSpan().SequenceEqual(b)
        | _ -> false

    let private sameRequest (left: MigrationSandboxSeedProviderRequest) (right: MigrationSandboxSeedProviderRequest) =
        left.Method = right.Method && left.Uri = right.Uri && left.ApiVersion = right.ApiVersion
        && left.EffectId = right.EffectId && left.IdempotencyKey = right.IdempotencyKey
        && sameBytes left.Body right.Body

    let private jsonBytes (write: Utf8JsonWriter -> unit) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        write writer
        writer.Flush()
        stream.ToArray()

    let private request (effect: MigrationSandboxSeedEffectState) (body: byte array) =
        { Method = "POST"; Uri = graphqlUri; ApiVersion = apiVersion; Body = Some body
          EffectId = effect.EffectId; IdempotencyKey = effect.IdempotencyKey }

    let private queryBody (query: string) (variables: Utf8JsonWriter -> unit) =
        jsonBytes (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("query", query)
            writer.WritePropertyName("variables")
            variables writer
            writer.WriteEndObject())

    let private createMutation =
        "mutation($repository:ID!,$title:String!,$body:String!,$clientMutationId:String!){createIssue(input:{repositoryId:$repository,title:$title,body:$body,clientMutationId:$clientMutationId}){clientMutationId issue{id databaseId number}}}"

    let private addMutation =
        "mutation($project:ID!,$content:ID!,$clientMutationId:String!){addProjectV2ItemById(input:{projectId:$project,contentId:$content,clientMutationId:$clientMutationId}){clientMutationId item{id}}}"

    let private removeMutation =
        "mutation($project:ID!,$item:ID!,$clientMutationId:String!){deleteProjectV2Item(input:{projectId:$project,itemId:$item,clientMutationId:$clientMutationId}){clientMutationId deletedItemId}}"

    let private deleteMutation =
        "mutation($issue:ID!,$clientMutationId:String!){deleteIssue(input:{issueId:$issue,clientMutationId:$clientMutationId}){clientMutationId repository{id databaseId}}}"

    let private issueQuery =
        "query($repository:ID!,$cursor:String){node(id:$repository){... on Repository{id databaseId issues(first:100,after:$cursor,states:[OPEN,CLOSED]){nodes{id databaseId number title body state author{login databaseId}}pageInfo{hasNextPage endCursor}}}}}"

    let private projectQuery =
        "query($project:ID!,$cursor:String){node(id:$project){... on ProjectV2{id items(first:100,after:$cursor){nodes{id content{... on Issue{id databaseId number repository{id databaseId}}}}pageInfo{hasNextPage endCursor}}}}}"

    let private issueTitle (nonce: string) = $"GS2-09.7 seed {nonce}"

    let private issueBody (binding: MigrationSandboxSeedExecutionBinding) (effect: MigrationSandboxSeedEffectState) =
        $"<!-- fsgg:gs2-09-7-seed/v1 effect={effect.EffectId} idempotency={effect.IdempotencyKey} nonce={binding.Request.RunNonce} binding={binding.Seal} host={binding.ProtectedHostReceiptSha256} -->"

    let private pageRequest (effect: MigrationSandboxSeedEffectState) (query: string) (variable: string) (cursor: string option) =
        let body =
            queryBody query (fun writer ->
                writer.WriteStartObject()
                writer.WriteString(variable, if variable = "repository" then repositoryNode else projectNode)
                match cursor with
                | Some value -> writer.WriteString("cursor", value)
                | None -> writer.WriteNull("cursor")
                writer.WriteEndObject())
        request effect body

    let private uniqueJson (root: JsonElement) =
        let rec visit (value: JsonElement) =
            match value.ValueKind with
            | JsonValueKind.Object ->
                let properties = value.EnumerateObject() |> Seq.toList
                if properties.Length <> (properties |> List.map _.Name |> Set.ofList |> Set.count) then
                    invalidOp "duplicate-json-member"
                properties |> List.iter (fun property -> visit property.Value)
            | JsonValueKind.Array -> value.EnumerateArray() |> Seq.iter visit
            | _ -> ()
        visit root

    let private exactProperties expected (value: JsonElement) =
        value.ValueKind = JsonValueKind.Object
        && (value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq) = Set.ofList expected

    let private stringList (value: JsonElement) =
        value.EnumerateArray() |> Seq.map _.GetString() |> Seq.toList

    let private validateHostReceipt (binding: MigrationSandboxSeedExecutionBinding) (bytes: ReadOnlyMemory<byte>) =
        try
            use document = JsonDocument.Parse bytes
            let root = document.RootElement
            uniqueJson root
            let source = root.GetProperty "source"
            let sandbox = root.GetProperty "sandbox"
            let mint = root.GetProperty "mint"
            let artifacts = root.GetProperty "artifacts"
            let plan = artifacts.GetProperty "seedPlan"
            let corpus = artifacts.GetProperty "corpus"
            let journal = root.GetProperty "journal"
            let protectedCheckout = source.GetProperty "protectedCheckout"
            let protectedBuilder = protectedCheckout.GetProperty "builder"
            let protectedWorkflow = protectedCheckout.GetProperty "workflow"
            exactProperties [ "activation"; "artifacts"; "authority"; "fingerprint"; "journal"; "mint";
                              "sandbox"; "schema"; "schemaJoin"; "source"; "status" ] root
            && root.GetProperty("schema").GetString() = "fsgg.github-substrate-v2.sandbox-seed-execution-binding/1"
            && root.GetProperty("activation").GetBoolean() = false
            && root.GetProperty("status").GetString() = "bound-no-write-authority"
            && root.GetProperty("authority").GetString() = "unavailable-without-protected-workflow-verification"
            && root.GetProperty("schemaJoin").GetString() = "pending-coordination-s1-final"
            && hex 64 (root.GetProperty("fingerprint").GetString())
            && source.GetProperty("workflowPath").GetString() = binding.WorkflowPath
            && source.GetProperty("workflowRef").GetString() = binding.WorkflowRef
            && source.GetProperty("workflowSha").GetString() = binding.WorkflowSha
            && source.GetProperty("providerWorkflowSha").GetString() = binding.WorkflowSha
            && source.GetProperty("candidateSha").GetString() = binding.Request.CandidateSha
            && source.GetProperty("runId").GetInt64() = binding.Request.WorkflowRunId
            && source.GetProperty("runAttempt").GetInt32() = binding.Request.WorkflowRunAttempt
            && source.GetProperty("runNonce").GetString() = binding.Request.RunNonce
            && source.GetProperty("repository").GetString() = "FS-GG/.github"
            && source.GetProperty("workflowRepository").GetString() = "FS-GG/.github"
            && source.GetProperty("builderPath").GetString() = "scripts/gs2-09-7-seed-execution-binding.py"
            && hex 64 (source.GetProperty("builderSha256").GetString())
            && protectedCheckout.GetProperty("checkoutHead").GetString() = binding.WorkflowSha
            && protectedBuilder.GetProperty("path").GetString() = "scripts/gs2-09-7-seed-execution-binding.py"
            && hex 64 (protectedBuilder.GetProperty("sha256").GetString())
            && protectedWorkflow.GetProperty("path").GetString() = binding.WorkflowPath
            && hex 64 (protectedWorkflow.GetProperty("sha256").GetString())
            && sandbox.GetProperty("repositoryId").GetInt64() = repositoryId
            && sandbox.GetProperty("repositoryNodeId").GetString() = repositoryNode
            && sandbox.GetProperty("projectNodeId").GetString() = projectNode
            && mint.GetProperty("proofSha256").GetString() = binding.MintProofSha256
            && mint.GetProperty("appId").GetInt64() = 4166418L
            && mint.GetProperty("installationId").GetInt64() = 143110413L
            && hex 64 (mint.GetProperty("tokenSha256").GetString())
            && plan.GetProperty("sha256").GetString() = binding.SeedPlanSha256
            && corpus.GetProperty("sha256").GetString() = binding.CorpusSha256
            && plan.GetProperty("byteLength").GetInt64() > 0L
            && corpus.GetProperty("byteLength").GetInt64() > 0L
            && not (String.IsNullOrWhiteSpace(journal.GetProperty("identity").GetString()))
            && stringList (journal.GetProperty "allowedClosedEffectKinds") =
                [ "CreateNonceIssue"; "AddProjectMembership"; "RemoveProjectMembership"; "DeleteNonceIssue" ]
        with
        | :? JsonException | :? InvalidOperationException | :? KeyNotFoundException
        | :? FormatException | :? DecoderFallbackException -> false

    let private validateEvidence (evidence: MigrationSandboxSeedProviderEvidence) =
        match MigrationSandboxSeedJournal.restore evidence.PreviousSnapshot evidence.CurrentSnapshot with
        | Error _ -> Error MigrationSandboxSeedProviderFailure.InvalidSealedExecution
        | Ok restored ->
            let state = restored.State
            let binding = state.Binding
            let active = state.Effects |> List.tryItem state.ActiveIndex
            match active with
            | None -> Error MigrationSandboxSeedProviderFailure.WrongEffect
            | Some effect when restored.ActiveEffectId <> Some effect.EffectId
                               || not restored.RecoveryOnly
                               || not (hex 64 effect.EffectId)
                               || not (hex 64 effect.IdempotencyKey)
                               || binding.RepositoryId <> repositoryId
                               || binding.RepositoryNodeId <> repositoryNode
                               || binding.ProjectNodeId <> projectNode
                               || sha evidence.ProtectedHostReceiptBytes <> binding.ProtectedHostReceiptSha256 ->
                Error MigrationSandboxSeedProviderFailure.InvalidSealedExecution
            | Some effect ->
                match MigrationSandboxSeedExecutor.sealBinding evidence.MintProofBytes evidence.ProtectedHostReceiptBytes evidence.SeedPlanBytes evidence.CorpusBytes binding with
                | Error _ -> Error MigrationSandboxSeedProviderFailure.InvalidSealedExecution
                | Ok sealedBinding when sealedBinding <> binding -> Error MigrationSandboxSeedProviderFailure.InvalidSealedExecution
                | Ok _ when not (validateHostReceipt binding evidence.ProtectedHostReceiptBytes) ->
                    Error MigrationSandboxSeedProviderFailure.ProtectedHostEvidenceInvalid
                | Ok _ -> Ok(restored, effect)

    let private mutation (state: MigrationSandboxSeedExecution) (effect: MigrationSandboxSeedEffectState) =
        let binding = state.Binding
        let gql query variables = request effect (queryBody query variables)
        match effect.Kind with
        | MigrationSandboxSeedEffectKind.CreateNonceIssue ->
            Ok(gql createMutation (fun writer ->
                writer.WriteStartObject()
                writer.WriteString("repository", repositoryNode)
                writer.WriteString("title", issueTitle binding.Request.RunNonce)
                writer.WriteString("body", issueBody binding effect)
                writer.WriteString("clientMutationId", effect.IdempotencyKey)
                writer.WriteEndObject()))
        | MigrationSandboxSeedEffectKind.AddProjectMembership ->
            match state.Effects |> List.tryHead |> Option.bind _.Ownership with
            | Some issue when issue.Kind = "issue" && issue.RunNonce = binding.Request.RunNonce ->
                Ok(gql addMutation (fun writer ->
                    writer.WriteStartObject()
                    writer.WriteString("project", projectNode)
                    writer.WriteString("content", issue.ResourceId)
                    writer.WriteString("clientMutationId", effect.IdempotencyKey)
                    writer.WriteEndObject()))
            | _ -> Error MigrationSandboxSeedProviderFailure.InvalidOwnership
        | MigrationSandboxSeedEffectKind.RemoveProjectMembership ->
            match effect.OriginalEffectId, effect.Ownership with
            | Some original, Some item when original = item.EffectId && item.Kind = "project-item"
                                             && item.RunNonce = binding.Request.RunNonce ->
                Ok(gql removeMutation (fun writer ->
                    writer.WriteStartObject()
                    writer.WriteString("project", projectNode)
                    writer.WriteString("item", item.ResourceId)
                    writer.WriteString("clientMutationId", effect.IdempotencyKey)
                    writer.WriteEndObject()))
            | _ -> Error MigrationSandboxSeedProviderFailure.InvalidOwnership
        | MigrationSandboxSeedEffectKind.DeleteNonceIssue ->
            match effect.OriginalEffectId, effect.Ownership with
            | Some original, Some issue when original = issue.EffectId && issue.Kind = "issue"
                                              && issue.RunNonce = binding.Request.RunNonce ->
                Ok(gql deleteMutation (fun writer ->
                    writer.WriteStartObject()
                    writer.WriteString("issue", issue.ResourceId)
                    writer.WriteString("clientMutationId", effect.IdempotencyKey)
                    writer.WriteEndObject()))
            | _ -> Error MigrationSandboxSeedProviderFailure.InvalidOwnership

    let plan (evidence: MigrationSandboxSeedProviderEvidence) =
        match validateEvidence evidence with
        | Error failure -> Error failure
        | Ok(restored, effect) ->
            let state = restored.State
            match effect.Stage with
            | MigrationSandboxSeedEffectStage.InFlight
            | MigrationSandboxSeedEffectStage.RecoveryPending ->
                match mutation state effect with
                | Error failure -> Error failure
                | Ok mutationRequest ->
                    Ok
                        { MutationTemplate = mutationRequest
                          DispatchRequest = None
                          FirstIssueRead = pageRequest effect issueQuery "repository" None
                          FirstProjectRead = pageRequest effect projectQuery "project" None
                          UnknownResponseRequiresRecoveryPending = true
                          DispatchAuthority = MigrationSandboxSeedProviderAuthority.SourceOnlyUnavailable }
            | _ -> Error MigrationSandboxSeedProviderFailure.EffectNotDispatchable

    type private Issue =
        { NodeId: string; DatabaseId: int64; Number: int; Title: string; Body: string
          State: string; AuthorLogin: string; AuthorId: int64 }

    type private ProjectItem =
        { ItemId: string; IssueNodeId: string; IssueDatabaseId: int64; IssueNumber: int }

    let private parsePages<'a>
        (effect: MigrationSandboxSeedEffectState)
        (query: string)
        (variable: string)
        (select: JsonElement -> 'a list * bool * string option)
        (pages: MigrationSandboxSeedProviderPage list)
        =
        let rec loop (cursor: string option) (accumulated: 'a list) (remaining: MigrationSandboxSeedProviderPage list) =
            match remaining with
            | [] -> Error MigrationSandboxSeedProviderFailure.CaptureIncomplete
            | page :: tail ->
                let expected = pageRequest effect query variable cursor
                if not (sameRequest page.Request expected) then Error MigrationSandboxSeedProviderFailure.ForeignRead
                elif page.StatusCode <> 200 || page.ResponseBody.IsEmpty then Error MigrationSandboxSeedProviderFailure.CaptureIncomplete
                else
                    try
                        use document = JsonDocument.Parse page.ResponseBody
                        uniqueJson document.RootElement
                        if document.RootElement.TryGetProperty("errors") |> fst then
                            Error MigrationSandboxSeedProviderFailure.MalformedResponse
                        else
                            let nodes, hasNext, endCursor = select document.RootElement
                            let values = List.append accumulated nodes
                            if hasNext then
                                match endCursor, tail with
                                | Some next, _ when not (String.IsNullOrWhiteSpace next) -> loop (Some next) values tail
                                | _ -> Error MigrationSandboxSeedProviderFailure.CaptureIncomplete
                            elif not tail.IsEmpty then
                                Error MigrationSandboxSeedProviderFailure.CaptureIncomplete
                            else Ok values
                    with
                    | :? JsonException | :? InvalidOperationException | :? KeyNotFoundException
                    | :? FormatException | :? DecoderFallbackException ->
                        Error MigrationSandboxSeedProviderFailure.MalformedResponse
        loop None [] pages

    let private pageInfo (connection: JsonElement) =
        let page = connection.GetProperty "pageInfo"
        let hasNext = page.GetProperty("hasNextPage").GetBoolean()
        let cursor = page.GetProperty "endCursor"
        let endCursor = if cursor.ValueKind = JsonValueKind.Null then None else Some(cursor.GetString())
        hasNext, endCursor

    let private parseIssues (effect: MigrationSandboxSeedEffectState) pages =
        parsePages effect issueQuery "repository" (fun root ->
            let repository = root.GetProperty("data").GetProperty("node")
            if repository.GetProperty("id").GetString() <> repositoryNode
               || repository.GetProperty("databaseId").GetInt64() <> repositoryId then invalidOp "foreign-repository"
            let connection = repository.GetProperty "issues"
            let values =
                connection.GetProperty("nodes").EnumerateArray()
                |> Seq.map (fun node ->
                    let author = node.GetProperty "author"
                    { NodeId=node.GetProperty("id").GetString(); DatabaseId=node.GetProperty("databaseId").GetInt64()
                      Number=node.GetProperty("number").GetInt32(); Title=node.GetProperty("title").GetString()
                      Body=node.GetProperty("body").GetString(); State=node.GetProperty("state").GetString()
                      AuthorLogin=author.GetProperty("login").GetString(); AuthorId=author.GetProperty("databaseId").GetInt64() })
                |> Seq.toList
            let hasNext, cursor = pageInfo connection
            values, hasNext, cursor) pages

    let private parseProjectItems (effect: MigrationSandboxSeedEffectState) pages =
        parsePages effect projectQuery "project" (fun root ->
            let project = root.GetProperty("data").GetProperty("node")
            if project.GetProperty("id").GetString() <> projectNode then invalidOp "foreign-project"
            let connection = project.GetProperty "items"
            let values =
                connection.GetProperty("nodes").EnumerateArray()
                |> Seq.map (fun node ->
                    let content = node.GetProperty "content"
                    let repository = content.GetProperty "repository"
                    if repository.GetProperty("id").GetString() <> repositoryNode
                       || repository.GetProperty("databaseId").GetInt64() <> repositoryId then invalidOp "foreign-repository"
                    { ItemId=node.GetProperty("id").GetString(); IssueNodeId=content.GetProperty("id").GetString()
                      IssueDatabaseId=content.GetProperty("databaseId").GetInt64()
                      IssueNumber=content.GetProperty("number").GetInt32() })
                |> Seq.toList
            let hasNext, cursor = pageInfo connection
            values, hasNext, cursor) pages

    let private passBytes (pass: MigrationSandboxSeedProviderPass) =
        [ yield! pass.IssuePages; yield! pass.ProjectPages ]
        |> List.collect (fun page ->
            [ utf8.GetBytes page.Request.Method; utf8.GetBytes page.Request.Uri
              defaultArg page.Request.Body Array.empty; page.ResponseBody.ToArray() ])
        |> List.map (fun bytes -> Array.append (BitConverter.GetBytes bytes.Length) bytes)
        |> Array.concat

    let private parsePass (effect: MigrationSandboxSeedEffectState) (pass: MigrationSandboxSeedProviderPass) =
        match parseIssues effect pass.IssuePages, parseProjectItems effect pass.ProjectPages with
        | Ok issues, Ok items when
            issues |> List.forall (fun issue ->
                not (String.IsNullOrWhiteSpace issue.NodeId) && issue.DatabaseId > 0L && issue.Number > 0
                && not (isNull issue.Title) && not (isNull issue.Body) && not (isNull issue.State)
                && not (String.IsNullOrWhiteSpace issue.AuthorLogin) && issue.AuthorId > 0L)
            && issues.Length = (issues |> List.map _.NodeId |> Set.ofList |> Set.count)
            && issues.Length = (issues |> List.map _.DatabaseId |> Set.ofList |> Set.count)
            && issues.Length = (issues |> List.map _.Number |> Set.ofList |> Set.count)
            && items |> List.forall (fun item ->
                not (String.IsNullOrWhiteSpace item.ItemId) && not (String.IsNullOrWhiteSpace item.IssueNodeId)
                && item.IssueDatabaseId > 0L && item.IssueNumber > 0)
            && items.Length = (items |> List.map _.ItemId |> Set.ofList |> Set.count) ->
            Ok(issues, items, sha (ReadOnlyMemory(passBytes pass)))
        | Ok _, Ok _ -> Error MigrationSandboxSeedProviderFailure.MalformedResponse
        | Error failure, _ | _, Error failure -> Error failure

    let private ownedReceipt (state: MigrationSandboxSeedExecution) (effect: MigrationSandboxSeedEffectState) (kind: string) (resource: string) (parent: string option) (readback: string) =
        { EffectId=effect.EffectId; Kind=kind; ResourceId=resource
          ParentResourceId=parent; RunNonce=state.Binding.Request.RunNonce
          ReadbackSha256=readback }

    let reconcile (evidence: MigrationSandboxSeedProviderEvidence) (capture: MigrationSandboxSeedProviderCapture) =
        match plan evidence, validateEvidence evidence with
        | Error failure, _ -> Error failure
        | _, Error failure -> Error failure
        | Ok providerPlan, Ok(restored, effect) ->
            let state = restored.State
            match parsePass effect capture.FirstPass, parsePass effect capture.SecondPass with
            | Error failure, _ | _, Error failure -> Error failure
            | Ok(firstIssues, firstItems, firstSha), Ok(secondIssues, secondItems, secondSha) ->
                if firstIssues <> secondIssues || firstItems <> secondItems || firstSha <> secondSha then
                    Error MigrationSandboxSeedProviderFailure.CaptureDrift
                else
                    let binding = state.Binding
                    let title = issueTitle binding.Request.RunNonce
                    let body = issueBody binding effect
                    let exactIssues = firstIssues |> List.filter (fun issue -> issue.Title = title && issue.Body = body)
                    let nonceIssues = firstIssues |> List.filter (fun issue -> issue.Title = title || issue.Body.Contains(binding.Request.RunNonce, StringComparison.Ordinal))
                    let exactOwned = exactIssues |> List.filter (fun issue -> issue.AuthorLogin = actorLogin && issue.AuthorId = actorId)
                    match effect.Kind with
                    | MigrationSandboxSeedEffectKind.CreateNonceIssue ->
                        match exactOwned, nonceIssues with
                        | [ issue ], [ same ] when issue = same && not (String.IsNullOrWhiteSpace issue.NodeId)
                                                    && issue.DatabaseId > 0L && issue.Number > 0
                                                    && issue.State = "OPEN" ->
                            Ok(MigrationSandboxSeedProviderDisposition.Applied(ownedReceipt state effect "issue" issue.NodeId None firstSha))
                        | [], [] -> Ok(MigrationSandboxSeedProviderDisposition.RecoveryPending "forward-create-not-observed")
                        | _ -> Ok(MigrationSandboxSeedProviderDisposition.Conflict "nonce-issue-ownership-conflict")
                    | MigrationSandboxSeedEffectKind.AddProjectMembership ->
                        match state.Effects |> List.tryHead |> Option.bind _.Ownership with
                        | None -> Error MigrationSandboxSeedProviderFailure.InvalidOwnership
                        | Some issue ->
                            let items = firstItems |> List.filter (fun item -> item.IssueNodeId = issue.ResourceId)
                            match items, capture.MutationResponse with
                            | [], _ -> Ok(MigrationSandboxSeedProviderDisposition.RecoveryPending "forward-membership-not-observed")
                            | [ item ], None -> Ok(MigrationSandboxSeedProviderDisposition.RecoveryPending "membership-response-unknown")
                            | [ item ], Some response when
                                sameRequest response.Request providerPlan.MutationTemplate
                                && response.StatusCode = 200
                                && not response.ResponseBody.IsEmpty ->
                                try
                                    use document = JsonDocument.Parse response.ResponseBody
                                    uniqueJson document.RootElement
                                    let payload = document.RootElement.GetProperty("data").GetProperty("addProjectV2ItemById")
                                    if payload.GetProperty("clientMutationId").GetString() <> effect.IdempotencyKey
                                       || payload.GetProperty("item").GetProperty("id").GetString() <> item.ItemId then
                                        Ok(MigrationSandboxSeedProviderDisposition.Conflict "membership-response-conflict")
                                    else
                                        Ok(MigrationSandboxSeedProviderDisposition.Applied(ownedReceipt state effect "project-item" item.ItemId (Some issue.ResourceId) firstSha))
                                with _ -> Error MigrationSandboxSeedProviderFailure.MalformedResponse
                            | [ _ ], Some _ -> Error MigrationSandboxSeedProviderFailure.ForeignRead
                            | _ -> Ok(MigrationSandboxSeedProviderDisposition.Conflict "project-membership-conflict")
                    | MigrationSandboxSeedEffectKind.RemoveProjectMembership ->
                        match effect.Ownership with
                        | None -> Error MigrationSandboxSeedProviderFailure.InvalidOwnership
                        | Some owned ->
                            let sameIssue = firstItems |> List.filter (fun item -> Some item.IssueNodeId = owned.ParentResourceId)
                            if sameIssue.IsEmpty then Ok MigrationSandboxSeedProviderDisposition.ProvenAbsent
                            else Ok(MigrationSandboxSeedProviderDisposition.Conflict "project-membership-still-present")
                    | MigrationSandboxSeedEffectKind.DeleteNonceIssue ->
                        match effect.Ownership with
                        | None -> Error MigrationSandboxSeedProviderFailure.InvalidOwnership
                        | Some owned ->
                            let sameIssue = firstIssues |> List.filter (fun issue -> issue.NodeId = owned.ResourceId)
                            let sameItems = firstItems |> List.filter (fun item -> item.IssueNodeId = owned.ResourceId)
                            if sameIssue.IsEmpty && sameItems.IsEmpty then Ok MigrationSandboxSeedProviderDisposition.ProvenAbsent
                            elif sameIssue.Length = 1 && sameItems.IsEmpty then
                                Ok(MigrationSandboxSeedProviderDisposition.RecoveryPending "nonce-issue-still-present")
                            else Ok(MigrationSandboxSeedProviderDisposition.Conflict "nonce-cleanup-conflict")
