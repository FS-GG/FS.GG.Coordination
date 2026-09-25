namespace FS.GG.Coordination.Cli

open System
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type MigrationReceiverTwoPass =
    { CohortSha256: string
      First: MigrationReceiverSnapshot list
      Second: MigrationReceiverSnapshot list }

type MigrationReceiverPinTwoPass =
    { CohortSha256: string
      InventoryBound: bool
      First: MigrationReceiverPinSnapshot list
      Second: MigrationReceiverPinSnapshot list }

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

    let capturePinBytesTwoPass (cohort: GitHubMigrationCopyCohort)
                               (pinsByReceiver: Map<string, MigrationReceiverPinDeclaration list>)
                               (template: MigrationGitHubReadOptions)
                               (transport: IMigrationGitHubReadTransport) =
        let receivers = cohort.Receivers |> List.sortBy _.Receiver
        let names = receivers |> List.map _.Receiver |> Set.ofList
        if not (GitHubMigrationInspect.validCohort cohort)
           || (pinsByReceiver |> Map.toSeq |> Seq.map fst |> Set.ofSeq) <> names
           || pinsByReceiver |> Map.exists (fun _ pins -> isNull (box pins) || pins.IsEmpty) then
            Error "invalid:receiver-pin-declaration"
        else
            let repositories = cohort.Repositories |> List.map (fun item -> item.Id, item) |> Map.ofList
            let readOne (receiver: GitHubMigrationCopyReceiver) =
                match Map.tryFind receiver.RepositoryId repositories with
                | None -> Error "missing:receiver-repository"
                | Some repository ->
                    match repository.FullName.Split('/') with
                    | [| owner; name |] when not (String.IsNullOrWhiteSpace owner)
                                             && not (String.IsNullOrWhiteSpace name) ->
                        let options =
                            { template with Owner=owner; Repository=name; ExpectedRepositoryId=repository.Id }
                        MigrationGitHubRead.readReceiverPinSnapshot options receiver.Receiver repository.NodeId
                            receiver.RefName receiver.ExpectedHead pinsByReceiver.[receiver.Receiver] transport
                        |> Result.mapError (fun failure -> $"receiver-pin:{receiver.Receiver}:{failure}")
                    | _ -> Error "invalid:receiver-repository-name"
            let readPass () =
                receivers
                |> List.fold (fun result receiver ->
                    result |> Result.bind (fun previous ->
                        readOne receiver |> Result.map (fun snapshot -> snapshot :: previous))) (Ok [])
                |> Result.map List.rev
            readPass ()
            |> Result.bind (fun first ->
                readPass ()
                |> Result.bind (fun second ->
                    if List.map _.PinSnapshotSha256 first <> List.map _.PinSnapshotSha256 second then
                        Error "changed:receiver-pin-snapshot"
                    else
                        Ok { CohortSha256=GitHubMigrationInspect.cohortSha256 cohort
                             InventoryBound=false; First=first; Second=second }))
