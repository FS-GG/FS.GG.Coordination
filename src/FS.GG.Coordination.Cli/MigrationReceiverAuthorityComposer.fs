namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type MigrationReceiverAuthorityCompositionRequest =
    { Options: MigrationInspectProviderOptions
      RosterOptions: MigrationReceiverRosterReadOptions
      RosterCapture: MigrationReceiverRosterCapture
      AcceptedEvidence: MigrationReceiverCopyAcceptedEvidence
      RunIdentity: MigrationSandboxSeedRequest
      CopyPlan: MigrationReceiverCopyPlanResult
      BlobBatches: MigrationReceiverCopyBlobBatch list
      BlobArtifacts: MigrationReceiverCopyBlobBatchArtifact list
      BlobCoverage: MigrationReceiverCopyBlobCoverage
      PinCapture: MigrationReceiverPinTwoPass }

type MigrationReceiverAuthorityComposition =
    { ReceiverIdentities: GitHubMigrationInspectAuthority * GitHubMigrationInspectAuthority
      WorkflowPins: GitHubMigrationInspectAuthority * GitHubMigrationInspectAuthority
      CopyPlanFingerprint: string
      BlobCoverageFingerprint: string
      ScopedSettingsSha256: string
      RepositoryRosterSha256: string }

type internal MigrationReceiverAuthorityBinding =
    { ReceiverName: string
      RepositoryId: int64
      RepositoryNodeId: string
      RepositoryFullName: string
      RefName: string
      CommitSha: string
      TreeSha: string }

type internal MigrationReceiverAuthorityVerifiedFacts =
    { Cohort: GitHubMigrationCopyCohort
      CopyPlanFingerprint: string
      BlobCoverageFingerprint: string
      BlobObjectCount: int
      Mappings: MigrationReceiverCopyMapping list
      Bindings: MigrationReceiverAuthorityBinding list
      SignedHeadReceiverNames: string list
      RosterRepositories: MigrationReceiverRosterRepository list
      ScopedSettingsSha256: string
      RepositoryRosterSha256: string
      RosterPages: GitHubMigrationInspectPage list
      ReceiverProof: GitHubMigrationInspectAuthority
      WorkflowProof: GitHubMigrationInspectAuthority }

[<RequireQualifiedAccess>]
module MigrationReceiverAuthorityComposer =
    let private utf8 = UTF8Encoding(false, true)
    let private unavailable authority reason = Error $"authority-adapter-unavailable:{authority}:{reason}"
    let private sha256Bytes (bytes: byte array) =
        bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private sha256 (value: string) = value |> utf8.GetBytes |> sha256Bytes
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private fingerprint values = values |> Seq.map frame |> String.concat "" |> sha256
    let private isHex length (value: string) =
        not (isNull value) && value.Length = length
        && (value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))

    let private uniqueMembers (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then false
        else
            let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            names.Length = (names |> Set.ofList |> Set.count)

    let private stringProperty (name: string) (value: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        if value.TryGetProperty(name, &property) && property.ValueKind = JsonValueKind.String then
            let text = property.GetString()
            if String.IsNullOrWhiteSpace text then failwith $"roster-string:{name}" else text
        else failwith $"roster-string:{name}"

    let private int64Property (name: string) (value: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        let mutable result = 0L
        if value.TryGetProperty(name, &property) && property.TryGetInt64(&result) then result
        else failwith $"roster-int64:{name}"

    let private intProperty (name: string) (value: JsonElement) =
        let result = int64Property name value
        if result < 0L || result > int64 Int32.MaxValue then failwith $"roster-int:{name}"
        int result

    let private boolProperty (name: string) (value: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        if not (value.TryGetProperty(name, &property)) then failwith $"roster-bool:{name}"
        match property.ValueKind with
        | JsonValueKind.True -> true
        | JsonValueKind.False -> false
        | _ -> failwith $"roster-bool:{name}"

    let private stringMap (name: string) (value: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        if not (value.TryGetProperty(name, &property)) || not (uniqueMembers property) then
            failwith $"roster-map:{name}"
        property.EnumerateObject()
        |> Seq.map (fun item ->
            if item.Value.ValueKind <> JsonValueKind.String then failwith $"roster-map:{name}"
            item.Name, item.Value.GetString())
        |> Map.ofSeq

    let private boolMap (name: string) (value: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        if not (value.TryGetProperty(name, &property)) || not (uniqueMembers property) then
            failwith $"roster-map:{name}"
        property.EnumerateObject()
        |> Seq.map (fun item ->
            let enabled =
                match item.Value.ValueKind with
                | JsonValueKind.True -> true
                | JsonValueKind.False -> false
                | _ -> failwith $"roster-map:{name}"
            item.Name, enabled)
        |> Map.ofSeq

    let private optionalBoolMap (name: string) (value: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        if value.TryGetProperty(name, &property) then boolMap name value
        else Map.empty

    let private scopeFingerprint (settings: MigrationReceiverScopeSettings) =
        seq {
            string settings.InstallationId
            settings.AccountLogin.ToLowerInvariant()
            string settings.AccountId
            settings.AccountNodeId
            settings.RepositorySelection
            settings.RepositoriesUrl
            for KeyValue(name, level) in settings.Permissions do
                name
                level
        }
        |> fingerprint

    let private rosterFingerprint (repositories: MigrationReceiverRosterRepository list) =
        seq {
            for repository in repositories do
                string repository.RosterRepositoryId
                repository.RosterRepositoryNodeId
                repository.RosterRepositoryFullName.ToLowerInvariant()
                string repository.RosterPrivate
                string repository.RosterArchived
                string repository.RosterDisabled
                for KeyValue(name, enabled) in repository.RosterPermissions do
                    name
                    string enabled
        }
        |> fingerprint

    let private passFingerprint settings total repositories pages =
        seq {
            scopeFingerprint settings
            string total
            rosterFingerprint repositories
            for page: MigrationReceiverRosterRawPage in pages do
                page.RosterRequestedUri
                page.RosterRequestIdentitySha256
                page.RosterRawSha256
                page.RosterNextUri |> Option.defaultValue ""
        }
        |> fingerprint

    let private parseRepository (expectedOwner: string) (value: JsonElement) =
        if not (uniqueMembers value) then failwith "roster-repository-members"
        let repository =
            { RosterRepositoryId=int64Property "id" value
              RosterRepositoryNodeId=stringProperty "node_id" value
              RosterRepositoryFullName=stringProperty "full_name" value
              RosterPrivate=boolProperty "private" value
              RosterArchived=boolProperty "archived" value
              RosterDisabled=boolProperty "disabled" value
              RosterPermissions=optionalBoolMap "permissions" value }
        let pieces = repository.RosterRepositoryFullName.Split('/')
        if pieces.Length <> 2
           || not (String.Equals(pieces.[0], expectedOwner, StringComparison.OrdinalIgnoreCase))
           || Map.tryFind "pull" repository.RosterPermissions = Some false then
            failwith "roster-repository-scope"
        repository

    let private parseRosterPass
        (options: MigrationReceiverRosterReadOptions)
        (pass: MigrationReceiverRosterPass) =
        try
            if pass.Pages.Length < 2 then failwith "roster-pages"
            for page in pass.Pages do
                if page.RosterRequestIdentitySha256 <> sha256 page.RosterRequestedUri
                   || page.RosterRawSha256 <> sha256 page.RosterRawBody then
                    failwith "roster-page-digest"

            let installation = pass.Pages.Head
            let expectedInstallation = Uri(options.ApiBase, $"app/installations/{options.InstallationId}").AbsoluteUri
            if installation.RosterRequestedUri <> expectedInstallation || installation.RosterNextUri.IsSome then
                failwith "roster-installation-uri"
            use installationDocument = JsonDocument.Parse installation.RosterRawBody
            let root = installationDocument.RootElement
            if not (uniqueMembers root) then failwith "roster-installation-members"
            let mutable account = Unchecked.defaultof<JsonElement>
            let mutable suspended = Unchecked.defaultof<JsonElement>
            if not (root.TryGetProperty("account", &account)) || not (uniqueMembers account)
               || not (root.TryGetProperty("suspended_at", &suspended)) then
                failwith "roster-installation-shape"
            let settings =
                { InstallationId=int64Property "id" root
                  AccountLogin=stringProperty "login" account
                  AccountId=int64Property "id" account
                  AccountNodeId=stringProperty "node_id" account
                  RepositorySelection=stringProperty "repository_selection" root
                  Permissions=stringMap "permissions" root
                  RepositoriesUrl=stringProperty "repositories_url" root }
            if int64Property "target_id" root <> options.AccountId
               || stringProperty "target_type" root <> "Organization"
               || settings.InstallationId <> options.InstallationId
               || settings.AccountId <> options.AccountId
               || settings.AccountNodeId <> options.AccountNodeId
               || not (String.Equals(settings.AccountLogin, options.AccountLogin, StringComparison.OrdinalIgnoreCase))
               || settings.RepositorySelection <> "selected"
               || suspended.ValueKind <> JsonValueKind.Null
               || settings.RepositoriesUrl <> Uri(options.ApiBase, "installation/repositories").AbsoluteUri
               || options.RequiredPermissions |> Map.exists (fun name level -> Map.tryFind name settings.Permissions <> Some level)
               || Map.tryFind "contents" options.RequiredPermissions <> Some "read"
               || Map.tryFind "metadata" options.RequiredPermissions <> Some "read" then
                failwith "roster-scoped-settings"

            let repositories = ResizeArray<MigrationReceiverRosterRepository>()
            let mutable expectedTotal: int option = None
            let repositoryPages = pass.Pages.Tail
            for index, page in repositoryPages |> List.indexed do
                let expectedUri =
                    Uri(options.ApiBase, $"installation/repositories?per_page=100&page={index + 1}").AbsoluteUri
                if page.RosterRequestedUri <> expectedUri then failwith "roster-repository-uri"
                let expectedNext =
                    if index + 1 < repositoryPages.Length then Some repositoryPages.[index + 1].RosterRequestedUri
                    else None
                if page.RosterNextUri <> expectedNext then failwith "roster-page-chain"
                use document = JsonDocument.Parse page.RosterRawBody
                let pageRoot = document.RootElement
                if not (uniqueMembers pageRoot) then failwith "roster-page-members"
                let total = intProperty "total_count" pageRoot
                if expectedTotal |> Option.exists ((<>) total) then failwith "roster-total-drift"
                expectedTotal <- Some total
                let mutable rows = Unchecked.defaultof<JsonElement>
                if not (pageRoot.TryGetProperty("repositories", &rows)) || rows.ValueKind <> JsonValueKind.Array then
                    failwith "roster-page-shape"
                for row in rows.EnumerateArray() do repositories.Add(parseRepository options.AccountLogin row)
            let sorted = repositories |> Seq.sortBy _.RosterRepositoryFullName |> List.ofSeq
            let total = expectedTotal |> Option.defaultWith (fun () -> failwith "roster-total")
            if total <> sorted.Length || total <> pass.RepositoryTotalCount || sorted <> pass.Repositories
               || sorted |> List.map _.RosterRepositoryId |> Set.ofList |> Set.count <> sorted.Length
               || sorted |> List.map _.RosterRepositoryNodeId |> Set.ofList |> Set.count <> sorted.Length
               || sorted |> List.map (fun item -> item.RosterRepositoryFullName.ToLowerInvariant()) |> Set.ofList |> Set.count <> sorted.Length
               || settings <> pass.ScopeSettings
               || pass.PassFingerprint <> passFingerprint settings total sorted pass.Pages then
                failwith "roster-typed-raw-drift"
            Ok(settings, sorted)
        with
        | :? JsonException -> Error "roster-json"
        | :? DecoderFallbackException -> Error "roster-utf8"
        | failure -> Error failure.Message

    let private rosterPages (capture: MigrationReceiverRosterCapture) =
        capture.First.Pages
        |> List.mapi (fun index page ->
            let subjects =
                if index = 0 then
                    [ { Identity=$"receiver-scope:installation:{capture.First.ScopeSettings.InstallationId}"
                        Revision=scopeFingerprint capture.First.ScopeSettings
                        PayloadSha256=page.RosterRawSha256 } ]
                else
                    use document = JsonDocument.Parse page.RosterRawBody
                    let ids =
                        document.RootElement.GetProperty("repositories").EnumerateArray()
                        |> Seq.map (int64Property "id")
                        |> Set.ofSeq
                    capture.First.Repositories
                    |> List.filter (fun repository -> Set.contains repository.RosterRepositoryId ids)
                    |> List.map (fun repository ->
                        { Identity=$"receiver-scope:repository:{repository.RosterRepositoryId}"
                          Revision=repository.RosterRepositoryNodeId
                          PayloadSha256=page.RosterRawSha256 })
            { RequestedUri=page.RosterRequestedUri
              RequestIdentitySha256=page.RosterRequestIdentitySha256
              RawBody=page.RosterRawBody
              PayloadSha256=page.RosterRawSha256
              NextRequestIdentitySha256=None
              Subjects=subjects })

    let private consistentPageIdentities (pages: GitHubMigrationInspectPage list) =
        pages
        |> List.groupBy _.RequestIdentitySha256
        |> List.forall (fun (_, group) ->
            let first = group.Head
            group
            |> List.forall (fun page ->
                page.RequestedUri = first.RequestedUri
                && page.RawBody = first.RawBody
                && page.PayloadSha256 = first.PayloadSha256))

    let private linkedPages (pages: GitHubMigrationInspectPage list) =
        pages
        |> List.fold (fun accumulated page ->
            match accumulated |> List.tryFindIndex (fun item -> item.RequestIdentitySha256 = page.RequestIdentitySha256) with
            | None -> accumulated @ [ page ]
            | Some index ->
                accumulated
                |> List.mapi (fun current existing ->
                    if current <> index then existing
                    else { existing with Subjects=(existing.Subjects @ page.Subjects) |> List.distinct |> List.sortBy _.Identity })) []
        |> List.mapi (fun index page ->
            let next =
                if index + 1 < pages.Length then Some pages.[index + 1].RequestIdentitySha256
                else None
            { page with NextRequestIdentitySha256=next })

    let private canonical (authority: string) (plan: string) (coverage: string) (settings: string) (roster: string)
                          (rosterEvidence: GitHubMigrationInspectPage list)
                          (proof: GitHubMigrationInspectAuthority) =
        let pages = linkedPages (rosterEvidence @ proof.Pages)
        let subjects = pages |> List.collect _.Subjects |> List.sortBy _.Identity
        let highWater =
            seq {
                yield! pages |> Seq.map _.PayloadSha256
                yield plan
                yield coverage
                yield settings
                yield roster
            }
            |> fingerprint
        { proof with
            Pages=pages
            Read=
                { proof.Read with
                    Authority=authority
                    PageCount=pages.Length
                    ItemCount=subjects.Length
                    Terminal=true
                    NextCursor=None
                    HighWaterMark=highWater
                    Subjects=subjects } }

    let internal composeVerifiedForTests (facts: MigrationReceiverAuthorityVerifiedFacts) =
        let receiverIds = [ "sdd"; "rendering"; "governance"; "templates"; "game"; "audio"; "net" ]
        let cohortSha = GitHubMigrationInspect.cohortSha256 facts.Cohort
        let mappings: MigrationReceiverCopyMapping list = facts.Mappings |> List.sortBy _.ReceiverCopyId
        let bindings: MigrationReceiverAuthorityBinding list = facts.Bindings |> List.sortBy _.ReceiverName
        let mappingNames = mappings |> List.map _.ReceiverCopyId
        let bindingNames = bindings |> List.map _.ReceiverName
        let cohortReceivers: GitHubMigrationCopyReceiver list = facts.Cohort.Receivers |> List.sortBy _.Receiver
        let cohortRepositories: GitHubMigrationCopyRepository list = facts.Cohort.Repositories |> List.sortBy _.Id
        let rosterRepositories: MigrationReceiverRosterRepository list = facts.RosterRepositories |> List.sortBy _.RosterRepositoryId
        if not (GitHubMigrationInspect.validCohort facts.Cohort) then
            unavailable "receiver-identities" "invalid-cohort"
        elif mappingNames <> List.sort receiverIds || bindingNames <> List.sort receiverIds
             || (facts.SignedHeadReceiverNames |> List.sort) <> List.sort receiverIds then
            unavailable "receiver-identities" "receiver-population"
        elif facts.BlobObjectCount <> 8999
             || not (isHex 64 facts.CopyPlanFingerprint)
             || not (isHex 64 facts.BlobCoverageFingerprint)
             || not (isHex 64 facts.ScopedSettingsSha256)
             || not (isHex 64 facts.RepositoryRosterSha256) then
            unavailable "receiver-identities" "copy-custody"
        elif cohortRepositories.Length <> rosterRepositories.Length
             || not (List.forall2 (fun (cohort: GitHubMigrationCopyRepository) (repository: MigrationReceiverRosterRepository) ->
                    cohort.Id = repository.RosterRepositoryId
                    && cohort.NodeId = repository.RosterRepositoryNodeId
                    && String.Equals(cohort.FullName, repository.RosterRepositoryFullName, StringComparison.OrdinalIgnoreCase)
                    && repository.RosterPrivate && not repository.RosterArchived && not repository.RosterDisabled
                    && Map.tryFind "pull" repository.RosterPermissions <> Some false)
                    cohortRepositories rosterRepositories) then
            unavailable "receiver-identities" "provider-roster-mismatch"
        else
            let mappingByName = mappings |> List.map (fun item -> item.ReceiverCopyId, item) |> Map.ofList
            let cohortByName = cohortReceivers |> List.map (fun item -> item.Receiver, item) |> Map.ofList
            let bindingMismatch =
                bindings
                |> List.exists (fun (binding: MigrationReceiverAuthorityBinding) ->
                    match Map.tryFind binding.ReceiverName mappingByName,
                          Map.tryFind binding.ReceiverName cohortByName with
                    | Some mapping, Some receiver ->
                        mapping.ReceiverCopyRepository <> binding.RepositoryFullName
                        || mapping.ReceiverCopySourceRevision = binding.CommitSha
                        || mapping.ReceiverCopySourceTree <> binding.TreeSha
                        || mapping.ReceiverCopyPlannedRef <> binding.RefName
                        || receiver.RepositoryId <> binding.RepositoryId
                        || receiver.RefName <> binding.RefName
                        || receiver.ExpectedHead <> binding.CommitSha
                        || not (cohortRepositories |> List.exists (fun repository ->
                            repository.Id = binding.RepositoryId
                            && repository.NodeId = binding.RepositoryNodeId
                            && repository.FullName = binding.RepositoryFullName))
                    | _ -> true)
            if bindingMismatch then unavailable "receiver-identities" "copy-plan-provider-mismatch"
            elif facts.ReceiverProof.CohortSha256 <> cohortSha
                 || not facts.ReceiverProof.ScopeVerified || not facts.ReceiverProof.SubjectsParsedFromRaw
                 || facts.ReceiverProof.Read.Authority <> "receiver-identities/declared" then
                unavailable "receiver-identities" "partial-proof"
            elif facts.WorkflowProof.CohortSha256 <> cohortSha
                 || not facts.WorkflowProof.ScopeVerified || not facts.WorkflowProof.SubjectsParsedFromRaw
                 || facts.WorkflowProof.Read.Authority <> "workflow-pins/provider-tree-signed-tools" then
                unavailable "workflow-pins" "partial-proof"
            elif not (consistentPageIdentities (facts.RosterPages @ facts.ReceiverProof.Pages @ facts.WorkflowProof.Pages)) then
                unavailable "receiver-identities" "contradictory-provider-page"
            elif facts.RosterPages.IsEmpty then unavailable "receiver-identities" "roster-pages"
            else
                let receivers =
                    canonical "receiver-identities" facts.CopyPlanFingerprint facts.BlobCoverageFingerprint
                              facts.ScopedSettingsSha256 facts.RepositoryRosterSha256 facts.RosterPages facts.ReceiverProof
                let workflows =
                    canonical "workflow-pins" facts.CopyPlanFingerprint facts.BlobCoverageFingerprint
                              facts.ScopedSettingsSha256 facts.RepositoryRosterSha256 facts.RosterPages facts.WorkflowProof
                Ok
                    { ReceiverIdentities=(receivers, receivers)
                      WorkflowPins=(workflows, workflows)
                      CopyPlanFingerprint=facts.CopyPlanFingerprint
                      BlobCoverageFingerprint=facts.BlobCoverageFingerprint
                      ScopedSettingsSha256=facts.ScopedSettingsSha256
                      RepositoryRosterSha256=facts.RepositoryRosterSha256 }

    let compose (request: MigrationReceiverAuthorityCompositionRequest) =
        if not (GitHubMigrationInspect.validCohort request.Options.Cohort) then
            unavailable "receiver-identities" "invalid-cohort"
        elif request.RosterCapture.First <> request.RosterCapture.Second then
            unavailable "receiver-identities" "roster-pass-drift"
        elif not (isNull (box request.PinCapture))
             && request.PinCapture.WorkflowTools |> List.exists _.RequiresMigration then
            unavailable "workflow-pins" "mutable-legacy-reference-requires-migration"
        else
            match parseRosterPass request.RosterOptions request.RosterCapture.First,
                  parseRosterPass request.RosterOptions request.RosterCapture.Second with
            | Error reason, _ | _, Error reason -> unavailable "receiver-identities" reason
            | Ok(settings, repositories), Ok(secondSettings, secondRepositories) ->
                let expectedCaptureFingerprint =
                    sha256 (request.RosterCapture.First.PassFingerprint + "\n" + request.RosterCapture.Second.PassFingerprint)
                if settings <> secondSettings || repositories <> secondRepositories
                   || request.RosterCapture.CaptureFingerprint <> expectedCaptureFingerprint then
                    unavailable "receiver-identities" "roster-capture-drift"
                else
                    MigrationReceiverCopyPlan.verify request.AcceptedEvidence request.RunIdentity request.CopyPlan
                    |> Result.mapError (fun reason -> $"authority-adapter-unavailable:receiver-identities:copy-plan:{reason}")
                    |> Result.bind (fun plan ->
                        MigrationReceiverCopyBlobCapture.verifyCoverage
                            request.AcceptedEvidence request.RunIdentity plan request.BlobBatches request.BlobArtifacts
                        |> Result.mapError (fun reason -> $"authority-adapter-unavailable:receiver-identities:blob-coverage:{reason}")
                        |> Result.bind (fun coverage ->
                            if coverage <> request.BlobCoverage then
                                unavailable "receiver-identities" "blob-coverage-drift"
                            else
                                MigrationInspectProviderAdapter.bindDeclaredReceiverIdentities
                                    request.Options
                                    (request.PinCapture.First |> List.map _.Receiver)
                                    (request.PinCapture.Second |> List.map _.Receiver)
                                |> Result.mapError (fun reason -> $"authority-adapter-unavailable:receiver-identities:{reason}")
                                |> Result.bind (fun receiverProof ->
                                    MigrationInspectProviderAdapter.bindProviderWorkflowPins request.Options request.PinCapture
                                    |> Result.mapError (fun reason -> $"authority-adapter-unavailable:workflow-pins:{reason}")
                                    |> Result.bind (fun workflowProof ->
                                        let snapshots = request.PinCapture.First |> List.map _.Receiver
                                        let mappingByName = plan.ReceiverCopyMappings |> List.map (fun item -> item.ReceiverCopyId, item) |> Map.ofList
                                        let treeMismatch =
                                            snapshots
                                            |> List.exists (fun snapshot ->
                                                match Map.tryFind snapshot.ReceiverName mappingByName with
                                                | None -> true
                                                | Some mapping -> mapping.ReceiverCopyRequiredEntries <> snapshot.TreeEntries)
                                        if treeMismatch then unavailable "receiver-identities" "copy-tree-mismatch"
                                        else
                                            let bindings =
                                                snapshots
                                                |> List.map (fun snapshot ->
                                                    let receiver =
                                                        request.Options.Cohort.Receivers
                                                        |> List.find (fun item -> item.Receiver = snapshot.ReceiverName)
                                                    { ReceiverName=snapshot.ReceiverName
                                                      RepositoryId=receiver.RepositoryId
                                                      RepositoryNodeId=snapshot.RepositoryNodeId
                                                      RepositoryFullName=snapshot.RepositoryFullName
                                                      RefName=snapshot.RefName
                                                      CommitSha=snapshot.CommitSha
                                                      TreeSha=snapshot.TreeSha })
                                            composeVerifiedForTests
                                                { Cohort=request.Options.Cohort
                                                  CopyPlanFingerprint=plan.ReceiverCopyFingerprint
                                                  BlobCoverageFingerprint=coverage.ReceiverCopyBlobCoverageFingerprint
                                                  BlobObjectCount=coverage.ReceiverCopyBlobCoverageSha256BySha1.Count
                                                  Mappings=plan.ReceiverCopyMappings
                                                  Bindings=bindings
                                                  SignedHeadReceiverNames=request.PinCapture.SignedHeads |> List.map _.ReceiverName
                                                  RosterRepositories=repositories
                                                  ScopedSettingsSha256=scopeFingerprint settings
                                                  RepositoryRosterSha256=rosterFingerprint repositories
                                                  RosterPages=rosterPages request.RosterCapture
                                                  ReceiverProof=receiverProof
                                                  WorkflowProof=workflowProof }))))
