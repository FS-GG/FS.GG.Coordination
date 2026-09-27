namespace FS.GG.Coordination.GitHub

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text

type MigrationRepositorySettingsPageEvidence =
    { SettingsRequestedUri: string
      SettingsPayloadJson: string
      SettingsPayloadSha256: string
      SettingsNextUri: string option }

type MigrationRepositorySettingsSurfaceRead =
    { RepositoryIdentity: RepositoryIdentity
      RepositoryRevision: string
      Surface: SettingsSurface
      Complete: bool
      Pages: MigrationRepositorySettingsPageEvidence list
      Settings: RepositorySetting list }

[<RequireQualifiedAccess>]
type MigrationRepositorySettingsSurfaceRefusal =
    | Unsupported of reason:string
    | Conditional of reason:string
    | Partial of reason:string
    | Unauthorized of reason:string
    | Unavailable of reason:string
    | Unreadable of reason:string

type IMigrationRepositorySettingsSurfaceProvider =
    abstract Read:
        identity:RepositoryIdentity * repositoryRevision:string * surface:SettingsSurface ->
            Result<MigrationRepositorySettingsSurfaceRead, MigrationRepositorySettingsSurfaceRefusal>

type MigrationRepositorySettingsCapture =
    { RepositoryIdentity: RepositoryIdentity
      RepositoryRevision: string
      First: Map<SettingsSurface, MigrationRepositorySettingsSurfaceRead>
      Second: Map<SettingsSurface, MigrationRepositorySettingsSurfaceRead>
      CaptureFingerprint: string }

[<RequireQualifiedAccess>]
type MigrationRepositorySettingsReadFailure =
    | InvalidIdentity
    | InvalidRevision
    | ProviderRefused of surface:SettingsSurface * refusal:MigrationRepositorySettingsSurfaceRefusal
    | IdentityDrift of surface:SettingsSurface
    | RevisionDrift of surface:SettingsSurface
    | SurfaceDrift of expected:SettingsSurface * actual:SettingsSurface
    | PartialSurface of surface:SettingsSurface * reason:string
    | EvidenceInvalid of surface:SettingsSurface * reason:string
    | SnapshotDrift of surface:SettingsSurface
    | CaptureFingerprintDrift
    | ObservationRefused of SettingsFailure

[<RequireQualifiedAccess>]
module MigrationRepositorySettingsRead =
    let private hashText (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private validText (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private validIdentity (identity: RepositoryIdentity) =
        identity.DatabaseId > 0L
        && [ identity.NodeId; identity.Owner; identity.Name; identity.DefaultBranch ]
           |> List.forall validText

    let private pageText (page: MigrationRepositorySettingsPageEvidence) =
        String.concat "|"
            [ Convert.ToBase64String(Encoding.UTF8.GetBytes page.SettingsRequestedUri)
              page.SettingsPayloadSha256
              page.SettingsNextUri
              |> Option.map (Encoding.UTF8.GetBytes >> Convert.ToBase64String)
              |> Option.defaultValue "-" ]

    let private settingValueText =
        function
        | Boolean value -> if value then "b:1" else "b:0"
        | Integer value -> $"i:{value}"
        | Text value -> "t:" + Convert.ToBase64String(Encoding.UTF8.GetBytes value)
        | TextList values ->
            values
            |> List.sort
            |> List.map (Encoding.UTF8.GetBytes >> Convert.ToBase64String)
            |> String.concat ","
            |> (+) "l:"

    let private settingText (setting: RepositorySetting) =
        String.concat "|"
            [ RepositorySettingsAdapter.surfaceId setting.Surface
              Convert.ToBase64String(Encoding.UTF8.GetBytes setting.Subject)
              Convert.ToBase64String(Encoding.UTF8.GetBytes setting.Name)
              settingValueText setting.Value ]

    let private readText (read: MigrationRepositorySettingsSurfaceRead) =
        String.concat "\n"
            [ RepositorySettingsAdapter.identityDigest read.RepositoryIdentity
              read.RepositoryRevision
              RepositorySettingsAdapter.surfaceId read.Surface
              string read.Complete
              read.Pages |> List.map pageText |> String.concat ";"
              read.Settings |> List.sortBy settingText |> List.map settingText |> String.concat ";" ]

    let private captureFingerprint
        (identity: RepositoryIdentity)
        (revision: string)
        (reads: Map<SettingsSurface, MigrationRepositorySettingsSurfaceRead>)
        : string =
        let surfaceDigests =
            RepositorySettingsAdapter.surfaces
            |> List.map (fun surface -> reads[surface] |> readText |> hashText)

        [ "fsgg.gs2-09.7.repository-settings-capture/v1"
          RepositorySettingsAdapter.identityDigest identity
          revision
          yield! surfaceDigests ]
        |> String.concat "\n"
        |> hashText

    let private validatePages
        (surface: SettingsSurface)
        (pages: MigrationRepositorySettingsPageEvidence list)
        =
        if List.isEmpty pages then
            Error(MigrationRepositorySettingsReadFailure.EvidenceInvalid(surface, "missing-pages"))
        else
            let rec loop
                (seen: HashSet<string>)
                (remaining: MigrationRepositorySettingsPageEvidence list)
                =
                match remaining with
                | [] -> Ok()
                | page :: tail ->
                    if not (validText page.SettingsRequestedUri) then
                        Error(MigrationRepositorySettingsReadFailure.EvidenceInvalid(surface, "invalid-request-uri"))
                    elif not (seen.Add page.SettingsRequestedUri) then
                        Error(MigrationRepositorySettingsReadFailure.EvidenceInvalid(surface, "duplicate-request-uri"))
                    elif isNull page.SettingsPayloadJson
                         || hashText page.SettingsPayloadJson <> page.SettingsPayloadSha256 then
                        Error(MigrationRepositorySettingsReadFailure.EvidenceInvalid(surface, "payload-hash-drift"))
                    else
                        match page.SettingsNextUri, tail with
                        | None, [] -> Ok()
                        | Some next, following :: _ when
                            validText next && next = following.SettingsRequestedUri ->
                            loop seen tail
                        | Some _, [] ->
                            Error(MigrationRepositorySettingsReadFailure.PartialSurface(surface, "unterminated-pagination"))
                        | None, _ :: _ ->
                            Error(MigrationRepositorySettingsReadFailure.EvidenceInvalid(surface, "page-after-terminal"))
                        | Some _, _ ->
                            Error(MigrationRepositorySettingsReadFailure.EvidenceInvalid(surface, "pagination-chain-drift"))

            loop (HashSet<string>(StringComparer.Ordinal)) pages

    let private validateSettings (surface: SettingsSurface) (settings: RepositorySetting list) =
        if settings |> List.exists (fun setting -> setting.Surface <> surface) then
            Error(MigrationRepositorySettingsReadFailure.EvidenceInvalid(surface, "setting-surface-drift"))
        elif
            settings
            |> List.exists (fun setting -> not (validText setting.Subject) || not (validText setting.Name))
        then
            Error(MigrationRepositorySettingsReadFailure.EvidenceInvalid(surface, "invalid-setting-identity"))
        else
            match
                settings
                |> List.groupBy (fun setting -> setting.Subject, setting.Name)
                |> List.tryFind (fun (_, values) -> values.Length > 1)
            with
            | Some _ -> Error(MigrationRepositorySettingsReadFailure.EvidenceInvalid(surface, "duplicate-setting"))
            | None -> Ok()

    let private validateRead
        (identity: RepositoryIdentity)
        (revision: string)
        (surface: SettingsSurface)
        (read: MigrationRepositorySettingsSurfaceRead)
        =
        if read.RepositoryIdentity <> identity then
            Error(MigrationRepositorySettingsReadFailure.IdentityDrift surface)
        elif read.RepositoryRevision <> revision then
            Error(MigrationRepositorySettingsReadFailure.RevisionDrift surface)
        elif read.Surface <> surface then
            Error(MigrationRepositorySettingsReadFailure.SurfaceDrift(surface, read.Surface))
        elif not read.Complete then
            Error(MigrationRepositorySettingsReadFailure.PartialSurface(surface, "provider-marked-incomplete"))
        else
            validatePages surface read.Pages
            |> Result.bind (fun () -> validateSettings surface read.Settings)

    let private validateMap
        (identity: RepositoryIdentity)
        (revision: string)
        (reads: Map<SettingsSurface, MigrationRepositorySettingsSurfaceRead>)
        =
        if reads.Count <> RepositorySettingsAdapter.surfaces.Length then
            let missing =
                RepositorySettingsAdapter.surfaces
                |> List.tryFind (fun surface -> not (Map.containsKey surface reads))
                |> Option.defaultValue SettingsSurface.Repository
            Error(MigrationRepositorySettingsReadFailure.PartialSurface(missing, "surface-roster-incomplete"))
        else
            RepositorySettingsAdapter.surfaces
            |> List.fold (fun state surface ->
                state
                |> Result.bind (fun () ->
                    match Map.tryFind surface reads with
                    | None ->
                        Error(MigrationRepositorySettingsReadFailure.PartialSurface(surface, "surface-missing"))
                    | Some read -> validateRead identity revision surface read)) (Ok())

    let private readPass
        (identity: RepositoryIdentity)
        (revision: string)
        (provider: IMigrationRepositorySettingsSurfaceProvider)
        : Result<Map<SettingsSurface, MigrationRepositorySettingsSurfaceRead>, MigrationRepositorySettingsReadFailure> =
        RepositorySettingsAdapter.surfaces
        |> List.fold (fun state surface ->
            state
            |> Result.bind (fun reads ->
                match provider.Read(identity, revision, surface) with
                | Error refusal ->
                    Error(MigrationRepositorySettingsReadFailure.ProviderRefused(surface, refusal))
                | Ok read ->
                    validateRead identity revision surface read
                    |> Result.map (fun () -> Map.add surface read reads))) (Ok Map.empty)

    let captureTwoPass
        (identity: RepositoryIdentity)
        (repositoryRevision: string)
        (provider: IMigrationRepositorySettingsSurfaceProvider)
        =
        if not (validIdentity identity) then
            Error MigrationRepositorySettingsReadFailure.InvalidIdentity
        elif not (validText repositoryRevision) then
            Error MigrationRepositorySettingsReadFailure.InvalidRevision
        elif isNull (box provider) then
            Error(MigrationRepositorySettingsReadFailure.ProviderRefused(
                SettingsSurface.Repository, MigrationRepositorySettingsSurfaceRefusal.Unavailable "provider-missing"))
        else
            readPass identity repositoryRevision provider
            |> Result.bind (fun first ->
                readPass identity repositoryRevision provider
                |> Result.bind (fun second ->
                    match
                        RepositorySettingsAdapter.surfaces
                        |> List.tryFind (fun surface -> first[surface] <> second[surface])
                    with
                    | Some surface -> Error(MigrationRepositorySettingsReadFailure.SnapshotDrift surface)
                    | None ->
                        Ok
                            { RepositoryIdentity=identity
                              RepositoryRevision=repositoryRevision
                              First=first
                              Second=second
                              CaptureFingerprint=captureFingerprint identity repositoryRevision first }))

    let validateCapture (captured: MigrationRepositorySettingsCapture) =
        if not (validIdentity captured.RepositoryIdentity) then
            Error MigrationRepositorySettingsReadFailure.InvalidIdentity
        elif not (validText captured.RepositoryRevision) then
            Error MigrationRepositorySettingsReadFailure.InvalidRevision
        else
            validateMap captured.RepositoryIdentity captured.RepositoryRevision captured.First
            |> Result.bind (fun () -> validateMap captured.RepositoryIdentity captured.RepositoryRevision captured.Second)
            |> Result.bind (fun () ->
                match
                    RepositorySettingsAdapter.surfaces
                    |> List.tryFind (fun surface -> captured.First[surface] <> captured.Second[surface])
                with
                | Some surface -> Error(MigrationRepositorySettingsReadFailure.SnapshotDrift surface)
                | None when
                    captureFingerprint captured.RepositoryIdentity captured.RepositoryRevision captured.First
                    <> captured.CaptureFingerprint ->
                    Error MigrationRepositorySettingsReadFailure.CaptureFingerprintDrift
                | None -> Ok captured)

    let composeComplete (captured: MigrationRepositorySettingsCapture) =
        validateCapture captured
        |> Result.bind (fun valid ->
            let surfaces =
                RepositorySettingsAdapter.surfaces
                |> List.map (fun surface ->
                    surface,
                    Supported(
                        valid.RepositoryRevision,
                        true,
                        valid.First[surface].Settings))
                |> Map.ofList

            let observation =
                { Identity=valid.RepositoryIdentity
                  CapturedRevision=valid.RepositoryRevision
                  Surfaces=surfaces
                  Digest=
                    RepositorySettingsAdapter.observationDigest
                        valid.RepositoryIdentity valid.RepositoryRevision surfaces }

            RepositorySettingsAdapter.validate observation
            |> Result.mapError MigrationRepositorySettingsReadFailure.ObservationRefused)
