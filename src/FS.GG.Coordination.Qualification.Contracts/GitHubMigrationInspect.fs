namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text

type GitHubMigrationCopyRepository =
    { Id: int64
      NodeId: string
      FullName: string
      SourceHead: string
      TargetHead: string }

type GitHubMigrationCopyCohort =
    { Repositories: GitHubMigrationCopyRepository list
      ProjectOrganization: string
      ProjectNumber: int
      ProjectNodeId: string
      SourceRevision: string
      Isolated: bool }

type GitHubMigrationInspectPage =
    { RequestedUri: string
      RequestIdentitySha256: string
      RawBody: string
      PayloadSha256: string
      NextRequestIdentitySha256: string option
      Subjects: GitHubDiscoverySubject list }

type GitHubMigrationInspectAuthority =
    { CohortSha256: string
      ScopeVerified: bool
      SubjectsParsedFromRaw: bool
      Read: GitHubDiscoveryAuthorityRead
      Pages: GitHubMigrationInspectPage list }

type GitHubMigrationInspectWindow =
    { StartedAt: DateTimeOffset
      CompletedAt: DateTimeOffset }

type GitHubMigrationInspectRequest =
    { Cohort: GitHubMigrationCopyCohort
      RoadmapRevision: string
      RoadmapSha256: string
      UnitContractSha256: string
      ReceiptDigests: string list
      First: GitHubMigrationInspectWindow
      Second: GitHubMigrationInspectWindow }

type IGitHubMigrationInspectSource =
    abstract ReadAuthority:
        passOrdinal:int * authority:string -> Result<GitHubMigrationInspectAuthority, string>

type IGitHubMigrationInspectStages =
    abstract BuildManifest:
        discovery:GitHubCompleteDiscovery -> Result<GitHubImmutableManifest, string>
    abstract BuildTransforms:
        manifest:GitHubImmutableManifest -> Result<GitHubTypedTransformQualification, string>
    abstract BuildOperations:
        transforms:GitHubTypedTransformQualification -> Result<GitHubLiveOperationQualification, string>

type GitHubMigrationInspectResult =
    { CohortSha256: string
      Discovery: GitHubCompleteDiscovery
      Manifest: GitHubImmutableManifest
      Transforms: GitHubTypedTransformQualification
      Operations: GitHubLiveOperationQualification }

[<RequireQualifiedAccess>]
type GitHubMigrationInspectFailure =
    | InvalidCohort
    | InvalidWindow
    | SourceRefused of passOrdinal:int * authority:string * reason:string
    | InvalidProviderEvidence of passOrdinal:int * authority:string * reason:string
    | ChangedProviderPages of authority:string
    | DiscoveryRefused of GitHubCompleteDiscoveryFinding list
    | MissingStageInput of stage:string * reason:string
    | InvalidStageBinding of stage:string
    | InvalidManifest of GitHubImmutableManifestFinding list
    | InvalidTransforms of GitHubTypedTransformFinding list
    | InvalidOperations of GitHubLiveOperationFinding list

[<RequireQualifiedAccess>]
module GitHubMigrationInspect =
    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()

    let private framed (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private validSha length (value: string) =
        not (String.IsNullOrWhiteSpace value) && value.Length = length
        && (value |> Seq.forall (fun character ->
            (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))

    let private unique (values: 'a list) = values.Length = (values |> Set.ofList |> Set.count)

    let cohortSha256 (cohort: GitHubMigrationCopyCohort) =
        [ yield string cohort.Isolated
          yield cohort.SourceRevision
          yield cohort.ProjectOrganization
          yield string cohort.ProjectNumber
          yield cohort.ProjectNodeId
          for repository in cohort.Repositories |> List.sortBy _.Id do
              yield string repository.Id
              yield repository.NodeId
              yield repository.FullName
              yield repository.SourceHead
              yield repository.TargetHead ]
        |> List.map framed |> String.concat "" |> sha

    let private validCohort cohort =
        cohort.Isolated && validSha 40 cohort.SourceRevision
        && not (String.IsNullOrWhiteSpace cohort.ProjectOrganization)
        && cohort.ProjectNumber > 0
        && not (String.IsNullOrWhiteSpace cohort.ProjectNodeId)
        && not cohort.Repositories.IsEmpty
        && (cohort.Repositories |> List.forall (fun repository ->
            repository.Id > 0L
            && not (String.IsNullOrWhiteSpace repository.NodeId)
            && not (String.IsNullOrWhiteSpace repository.FullName)
            && repository.FullName.Contains '/'
            && validSha 40 repository.SourceHead
            && validSha 40 repository.TargetHead))
        && unique (cohort.Repositories |> List.map _.Id)
        && unique (cohort.Repositories |> List.map _.NodeId)
        && unique (cohort.Repositories |> List.map _.FullName)

    let private validateAuthority passOrdinal expected cohortDigest (value: GitHubMigrationInspectAuthority) =
        let fail reason =
            Error(GitHubMigrationInspectFailure.InvalidProviderEvidence(passOrdinal, expected, reason))
        let read = value.Read
        let pages = value.Pages
        let validUri (address: string) =
            let mutable uri = Unchecked.defaultof<Uri>
            Uri.TryCreate(address, UriKind.Absolute, &uri) && uri.Scheme = Uri.UriSchemeHttps
        let rec linked remaining =
            match remaining with
            | [] -> false
            | [ last ] -> last.NextRequestIdentitySha256.IsNone
            | first :: ((next :: _) as rest) ->
                first.NextRequestIdentitySha256 = Some next.RequestIdentitySha256 && linked rest
        if value.CohortSha256 <> cohortDigest || read.Authority <> expected then
            fail "cohort-or-authority"
        elif not value.ScopeVerified || not value.SubjectsParsedFromRaw then
            fail "unqualified-adapter"
        elif not read.Terminal || read.NextCursor.IsSome || read.PageCount < 1
             || read.PageCount <> pages.Length || read.ItemCount <> read.Subjects.Length then
            fail "incomplete-pages"
        elif not (unique (pages |> List.map _.RequestIdentitySha256)) || not (linked pages) then
            fail "page-chain"
        elif pages |> List.exists (fun page ->
            not (validUri page.RequestedUri)
            || not (validSha 64 page.RequestIdentitySha256)
            || (page.NextRequestIdentitySha256 |> Option.exists (validSha 64 >> not))
            || not (validSha 64 page.PayloadSha256)
            || sha page.RawBody <> page.PayloadSha256) then
            fail "raw-page"
        elif (pages |> List.collect _.Subjects |> List.sortBy _.Identity) <> read.Subjects then
            fail "page-subjects"
        else Ok value

    let private readPass passOrdinal cohortDigest (source: IGitHubMigrationInspectSource) =
        let read authority =
            source.ReadAuthority(passOrdinal, authority)
            |> Result.mapError (fun reason ->
                GitHubMigrationInspectFailure.SourceRefused(passOrdinal, authority, reason))
            |> Result.bind (validateAuthority passOrdinal authority cohortDigest)
        GitHubCompleteDiscoveryQualification.expectedAuthorities
        |> List.fold (fun result authority ->
            result |> Result.bind (fun values ->
                read authority |> Result.map (fun value -> value :: values))) (Ok [])
        |> Result.map List.rev

    let private samePageEvidence first second =
        List.forall2 (fun (left: GitHubMigrationInspectAuthority) (right: GitHubMigrationInspectAuthority) ->
            left.Read.Authority = right.Read.Authority
            && (left.Pages |> List.map (fun page -> page.RequestedUri, page.RequestIdentitySha256, page.PayloadSha256, page.NextRequestIdentitySha256))
               = (right.Pages |> List.map (fun page -> page.RequestedUri, page.RequestIdentitySha256, page.PayloadSha256, page.NextRequestIdentitySha256))) first second

    let private discoveredSubjectIds (discovery: GitHubCompleteDiscovery) =
        discovery.First.Authorities
        |> List.collect (fun authority -> authority.Subjects |> List.map _.Identity)
        |> List.distinct |> List.sort

    type private ResultBuilder() =
        member _.Bind(value, continuation) = Result.bind continuation value
        member _.Return(value) = Ok value
        member _.ReturnFrom(value) = value

    let private result = ResultBuilder()

    let inspect request (source: IGitHubMigrationInspectSource) (stages: IGitHubMigrationInspectStages) =
        if not (validCohort request.Cohort) then Error GitHubMigrationInspectFailure.InvalidCohort
        elif request.First.StartedAt > request.First.CompletedAt
             || request.Second.StartedAt > request.Second.CompletedAt
             || request.Second.StartedAt < request.First.CompletedAt then
            Error GitHubMigrationInspectFailure.InvalidWindow
        else
            result {
                let cohortDigest = cohortSha256 request.Cohort
                let! first = readPass 1 cohortDigest source
                let! second = readPass 2 cohortDigest source
                let! _ =
                    if samePageEvidence first second then Ok ()
                    else
                        let changed =
                            List.zip first second
                            |> List.find (fun (left, right) ->
                                (left.Pages |> List.map (fun page -> page.RequestedUri, page.RequestIdentitySha256, page.PayloadSha256, page.NextRequestIdentitySha256))
                                <> (right.Pages |> List.map (fun page -> page.RequestedUri, page.RequestIdentitySha256, page.PayloadSha256, page.NextRequestIdentitySha256)))
                            |> fst
                        Error(GitHubMigrationInspectFailure.ChangedProviderPages changed.Read.Authority)
                let pass window observations =
                    { SourceRevision=request.Cohort.SourceRevision
                      StartedAt=window.StartedAt; CompletedAt=window.CompletedAt
                      Authorities=observations |> List.map _.Read }
                let! discovery =
                    GitHubCompleteDiscoveryQualification.qualify
                        request.RoadmapRevision request.RoadmapSha256 request.UnitContractSha256
                        request.ReceiptDigests (pass request.First first) (pass request.Second second)
                    |> Result.mapError GitHubMigrationInspectFailure.DiscoveryRefused
                let! manifest =
                    stages.BuildManifest discovery
                    |> Result.mapError (fun reason -> GitHubMigrationInspectFailure.MissingStageInput("manifest", reason))
                let! _ =
                    if manifest.DiscoverySourceRevision <> request.Cohort.SourceRevision
                       || manifest.DiscoveryNormalizedDigest <> discovery.NormalizedDigest
                       || manifest.DiscoverySeal <> discovery.Seal
                       || manifest.RoadmapRevision <> request.RoadmapRevision
                       || manifest.RoadmapSha256 <> request.RoadmapSha256
                       || manifest.UnitContractSha256 <> request.UnitContractSha256 then
                        Error(GitHubMigrationInspectFailure.InvalidStageBinding "manifest")
                    else Ok ()
                let! _ =
                    GitHubImmutableManifestQualification.verify
                        (discoveredSubjectIds discovery) manifest.Seal manifest
                    |> Result.mapError GitHubMigrationInspectFailure.InvalidManifest
                let! transforms =
                    stages.BuildTransforms manifest
                    |> Result.mapError (fun reason -> GitHubMigrationInspectFailure.MissingStageInput("transforms", reason))
                let! _ =
                    if transforms.ManifestNormalizedDigest <> manifest.NormalizedDigest
                       || transforms.ManifestSeal <> manifest.Seal
                       || transforms.RoadmapRevision <> request.RoadmapRevision
                       || transforms.RoadmapSha256 <> request.RoadmapSha256
                       || transforms.UnitContractSha256 <> request.UnitContractSha256 then
                        Error(GitHubMigrationInspectFailure.InvalidStageBinding "transforms")
                    else Ok ()
                let! _ =
                    GitHubTypedTransformQualification.verify
                        transforms.Obligations transforms.Seal transforms
                    |> Result.mapError GitHubMigrationInspectFailure.InvalidTransforms
                let! operations =
                    stages.BuildOperations transforms
                    |> Result.mapError (fun reason -> GitHubMigrationInspectFailure.MissingStageInput("operations", reason))
                let! _ =
                    if operations.ManifestNormalizedDigest <> manifest.NormalizedDigest
                       || operations.ManifestSeal <> manifest.Seal
                       || operations.TransformNormalizedDigest <> transforms.NormalizedDigest
                       || operations.TransformSeal <> transforms.Seal
                       || operations.RoadmapRevision <> request.RoadmapRevision
                       || operations.RoadmapSha256 <> request.RoadmapSha256
                       || operations.UnitContractSha256 <> request.UnitContractSha256 then
                        Error(GitHubMigrationInspectFailure.InvalidStageBinding "operations")
                    else Ok ()
                let! _ =
                    GitHubLiveOperationQualification.verify
                        operations.Obligations operations.Seal operations
                    |> Result.mapError GitHubMigrationInspectFailure.InvalidOperations
                return
                    { CohortSha256=cohortDigest
                      Discovery=discovery; Manifest=manifest
                      Transforms=transforms; Operations=operations }
            }
