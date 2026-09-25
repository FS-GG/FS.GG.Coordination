namespace FS.GG.Coordination.Cli

open System
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type MigrationWorkflowPinKind =
    | Workflow
    | PackageToolPin

type MigrationWorkflowPinDeclaration =
    { Kind: MigrationWorkflowPinKind
      Receiver: string
      RepositoryId: int64
      RepositoryNodeId: string
      RepositoryFullName: string
      RefName: string
      ExpectedHead: string
      Path: string
      ExpectedMode: string
      ExpectedBlobSha: string
      ExpectedBytesSha256: string }

type MigrationWorkflowPinBlob =
    { Declaration: MigrationWorkflowPinDeclaration
      RequestUri: string
      RawBody: string
      RawBodySha256: string
      Bytes: byte array
      BytesSha256: string
      GitBlobSha: string }

type MigrationWorkflowPinTwoPass =
    { CohortSha256: string
      First: MigrationWorkflowPinBlob list
      Second: MigrationWorkflowPinBlob list }

[<RequireQualifiedAccess>]
module MigrationWorkflowPinCapture =
    let private text (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private hex length (value: string) =
        not (isNull value) && value.Length = length
        && (value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))

    let private packageNames =
        set [ "global.json"; "Directory.Packages.props"; "packages.lock.json"; "package.json"
              "package-lock.json"; "pnpm-lock.yaml"; "yarn.lock"; "nuget.config" ]

    let private validPath (value: string) =
        text value && not (value.StartsWith('/')) && not (value.Contains('\\'))
        && not (value.Contains("..", StringComparison.Ordinal))
        && (value.Split('/') |> Array.forall (fun part -> text part && part <> "."))

    let private validDeclaration (value: MigrationWorkflowPinDeclaration) =
        text value.Receiver && value.RepositoryId > 0L && text value.RepositoryNodeId
        && text value.RepositoryFullName && text value.RefName && hex 40 value.ExpectedHead
        && validPath value.Path && Set.contains value.ExpectedMode (set [ "100644"; "100755" ])
        && hex 40 value.ExpectedBlobSha && hex 64 value.ExpectedBytesSha256
        && (match value.Kind with
            | MigrationWorkflowPinKind.Workflow ->
                value.Path.StartsWith(".github/workflows/", StringComparison.Ordinal)
                && (value.Path.EndsWith(".yml", StringComparison.Ordinal)
                    || value.Path.EndsWith(".yaml", StringComparison.Ordinal))
                && not (value.Path.Substring(".github/workflows/".Length).Contains('/'))
            | MigrationWorkflowPinKind.PackageToolPin ->
                not (value.Path.StartsWith(".github/workflows/", StringComparison.Ordinal))
                && packageNames.Contains(value.Path.Split('/') |> Array.last))

    let private receiverMap (cohort: GitHubMigrationCopyCohort) =
        cohort.Receivers |> List.map (fun value -> value.Receiver, value) |> Map.ofList

    let private repositoryMap (cohort: GitHubMigrationCopyCohort) =
        cohort.Repositories |> List.map (fun value -> value.Id, value) |> Map.ofList

    let private validSnapshots (cohort: GitHubMigrationCopyCohort) snapshots =
        let byReceiver = receiverMap cohort
        let byRepository = repositoryMap cohort
        let expected = cohort.Receivers |> List.map _.Receiver |> List.sort
        let actual = snapshots |> List.map _.ReceiverName |> List.sort
        expected = actual
        && (snapshots |> List.forall (fun snapshot ->
            match Map.tryFind snapshot.ReceiverName byReceiver,
                  Map.tryFind snapshot.RepositoryId byRepository with
            | Some receiver, Some repository ->
                receiver.RepositoryId = snapshot.RepositoryId
                && receiver.RefName = snapshot.RefName
                && receiver.ExpectedHead = snapshot.CommitSha
                && repository.NodeId = snapshot.RepositoryNodeId
                && repository.FullName = snapshot.RepositoryFullName
                && hex 64 snapshot.SnapshotSha256
            | _ -> false))

    let private validBinding (cohort: GitHubMigrationCopyCohort)
                             (declaration: MigrationWorkflowPinDeclaration) =
        match Map.tryFind declaration.Receiver (receiverMap cohort),
              Map.tryFind declaration.RepositoryId (repositoryMap cohort) with
        | Some receiver, Some repository ->
            receiver.RepositoryId = declaration.RepositoryId
            && receiver.RefName = declaration.RefName
            && receiver.ExpectedHead = declaration.ExpectedHead
            && repository.NodeId = declaration.RepositoryNodeId
            && repository.FullName = declaration.RepositoryFullName
            && (match repository.FullName.Split('/') with
                | [| owner; name |] -> text owner && text name
                | _ -> false)
        | _ -> false

    let private providerKind = function
        | MigrationWorkflowPinKind.Workflow -> "workflow"
        | MigrationWorkflowPinKind.PackageToolPin -> "package"

    let captureTwoPass (cohort: GitHubMigrationCopyCohort)
                       (declarations: MigrationWorkflowPinDeclaration list)
                       (receivers: MigrationReceiverTwoPass)
                       (template: MigrationGitHubReadOptions)
                       (transport: IMigrationGitHubReadTransport) =
        let cohortSha = GitHubMigrationInspect.cohortSha256 cohort
        let sorted = declarations |> List.sortBy (fun value -> value.Receiver, value.Path)
        let uniquePaths = sorted |> List.map (fun value -> value.Receiver, value.Path) |> Set.ofList |> Set.count
        let validOptions =
            not (isNull template.ApiBase) && template.ApiBase.IsAbsoluteUri
            && template.ApiBase.Scheme = Uri.UriSchemeHttps
            && not (isNull template.GraphQLUri) && template.GraphQLUri.IsAbsoluteUri
            && template.GraphQLUri.Scheme = Uri.UriSchemeHttps && text template.UserAgent
        if not (GitHubMigrationInspect.validCohort cohort) then Error "invalid:pin-cohort"
        elif not validOptions then Error "invalid:pin-options"
        elif declarations.IsEmpty || uniquePaths <> declarations.Length
             || not (List.forall validDeclaration declarations) then Error "invalid:pin-declarations"
        elif not (List.forall (validBinding cohort) declarations) then Error "foreign:pin-declaration"
        elif receivers.CohortSha256 <> cohortSha
             || not (validSnapshots cohort receivers.First)
             || not (validSnapshots cohort receivers.Second)
             || (receivers.First |> List.sortBy _.ReceiverName |> List.map _.SnapshotSha256)
                <> (receivers.Second |> List.sortBy _.ReceiverName |> List.map _.SnapshotSha256) then
            Error "changed:pin-receivers"
        else
            let readPass (snapshots: MigrationReceiverSnapshot list) =
                let byReceiver = snapshots |> List.map (fun value -> value.ReceiverName, value) |> Map.ofList
                cohort.Receivers
                |> List.sortBy _.Receiver
                |> List.fold (fun state receiver ->
                    state
                    |> Result.bind (fun captured ->
                        let declared = sorted |> List.filter (fun value -> value.Receiver = receiver.Receiver)
                        let snapshot = Map.find receiver.Receiver byReceiver
                        if declared.IsEmpty then Error $"missing:pin-declarations:{receiver.Receiver}"
                        else
                            let repository = Map.find receiver.RepositoryId (repositoryMap cohort)
                            let parts = repository.FullName.Split('/')
                            let options =
                                { template with Owner=parts.[0]; Repository=parts.[1]
                                                ExpectedRepositoryId=repository.Id }
                            let pins =
                                declared
                                |> List.map (fun value ->
                                    { EntryPath=value.Path; PinKind=providerKind value.Kind })
                            MigrationGitHubRead.readReceiverPinSnapshot options receiver.Receiver repository.NodeId
                                receiver.RefName receiver.ExpectedHead pins transport
                            |> Result.mapError (fun failure -> $"receiver:{receiver.Receiver}:{failure}")
                            |> Result.bind (fun observed ->
                                if observed.Receiver.SnapshotSha256 <> snapshot.SnapshotSha256 then
                                    Error $"changed:pin-receiver-snapshot:{receiver.Receiver}"
                                else
                                    let byPath = declared |> List.map (fun value -> value.Path, value) |> Map.ofList
                                    let bind (pin: MigrationReceiverPinBlob) =
                                        let declaration = Map.find pin.EntryPath byPath
                                        if pin.EntryMode <> declaration.ExpectedMode
                                           || pin.EntrySha <> declaration.ExpectedBlobSha
                                           || pin.BytesSha256 <> declaration.ExpectedBytesSha256 then
                                            Error $"changed:pin-content:{receiver.Receiver}:{pin.EntryPath}"
                                        else
                                            Ok { Declaration=declaration; RequestUri=pin.RequestUri
                                                 RawBody=pin.RawBody; RawBodySha256=pin.RawSha256
                                                 Bytes=pin.Bytes; BytesSha256=pin.BytesSha256
                                                 GitBlobSha=pin.EntrySha }
                                    observed.Pins
                                    |> List.fold (fun result pin ->
                                        result
                                        |> Result.bind (fun previous ->
                                            bind pin |> Result.map (fun item -> item :: previous))) (Ok [])
                                    |> Result.map (fun items -> List.rev items @ captured)))) (Ok [])
            readPass receivers.First
            |> Result.bind (fun first ->
                readPass receivers.Second
                |> Result.bind (fun second ->
                    let first = first |> List.sortBy (fun value -> value.Declaration.Receiver, value.Declaration.Path)
                    let second = second |> List.sortBy (fun value -> value.Declaration.Receiver, value.Declaration.Path)
                    if List.map _.Bytes first <> List.map _.Bytes second then Error "changed:pin-bytes"
                    else Ok { CohortSha256=cohortSha; First=first; Second=second }))
