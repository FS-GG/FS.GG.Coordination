namespace FS.GG.Coordination.Cli

open System
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type MigrationReceiverTwoPass =
    { CohortSha256: string
      First: MigrationReceiverSnapshot list
      Second: MigrationReceiverSnapshot list }

[<RequireQualifiedAccess>]
module MigrationReceiverCapture =
    let captureTwoPass (cohort: GitHubMigrationCopyCohort) (template: MigrationGitHubReadOptions)
                       (transport: IMigrationGitHubReadTransport) =
        let repositories = cohort.Repositories |> List.map (fun (item: GitHubMigrationCopyRepository) -> item.Id, item) |> Map.ofList
        let receivers: GitHubMigrationCopyReceiver list = cohort.Receivers |> List.sortBy _.Receiver
        if not (GitHubMigrationInspect.validCohort cohort) then
            Error "invalid:receiver-cohort"
        else
            let readOne (receiver: GitHubMigrationCopyReceiver) =
                match Map.tryFind receiver.RepositoryId repositories with
                | None -> Error "missing:receiver-repository"
                | Some repository ->
                    match repository.FullName.Split('/') with
                    | [| owner; name |] when not (String.IsNullOrWhiteSpace owner)
                                             && not (String.IsNullOrWhiteSpace name) ->
                        let options =
                            { template with Owner=owner; Repository=name; ExpectedRepositoryId=repository.Id }
                        MigrationGitHubRead.readReceiverSnapshot options receiver.Receiver repository.NodeId
                            receiver.RefName receiver.ExpectedHead transport
                        |> Result.mapError (fun failure -> $"receiver:{receiver.Receiver}:{failure}")
                    | _ -> Error "invalid:receiver-repository-name"
            let readPass () =
                receivers
                |> List.fold (fun result receiver ->
                    result
                    |> Result.bind (fun previous ->
                        readOne receiver |> Result.map (fun snapshot -> snapshot :: previous))) (Ok [])
                |> Result.map List.rev
            readPass ()
            |> Result.bind (fun first ->
                readPass ()
                |> Result.bind (fun second ->
                    if List.map _.SnapshotSha256 first <> List.map _.SnapshotSha256 second then
                        Error "changed:receiver-snapshot"
                    else
                        Ok { CohortSha256=GitHubMigrationInspect.cohortSha256 cohort
                             First=first; Second=second }))
