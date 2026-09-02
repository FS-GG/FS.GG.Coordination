namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type RepositoryRole = Authority | Framework | NonParticipant
type AdministrationBoundary = OrganizationAdministered | ExternalObserveOnly
type RepositoryRosterRow =
    { Id: string
      FullName: string
      Role: RepositoryRole
      Capabilities: string list
      KitDelivery: string option
      AbsenceCover: string option
      Reason: string option }

type RepositoryRosterSnapshot =
    { SchemaVersion: int
      SourceRevision: string
      SourceArtifactSha256: string
      CanonicalRosterSha256: string
      ReviewedAt: DateTimeOffset
      Complete: bool
      Rows: RepositoryRosterRow list }

type NativeCustomProperty = { Name: string; Value: string }
type RepositoryProfile =
    { Id: string
      FullName: string
      Role: RepositoryRole
      Administration: AdministrationBoundary
      Capabilities: string list
      KitDelivery: string option
      AbsenceCover: string option
      Reason: string option
      NativeProperties: NativeCustomProperty list
      PropertyMutationPermitted: bool }

type RepositoryProfileReport =
    { SourceRevision: string
      SourceArtifactSha256: string
      CanonicalRosterSha256: string
      Profiles: RepositoryProfile list
      Seal: string }

type RepositoryProfileFinding =
    | UnsupportedRosterSchema of int
    | IncompleteRoster
    | StaleRoster
    | InvalidSourceBinding of string
    | InvalidRepositoryIdentity of string
    | DuplicateRepositoryId of string
    | DuplicateRepositoryName of string
    | UnsupportedRepositoryRole of string
    | UnsupportedRepositoryCapability of string * string
    | InvalidRepositoryMetadata of string
    | MissingAuthority
    | MultipleAuthorities
    | LossyRepositoryProfile of string
    | NativePropertyOverflow of string * string
    | AlteredRepositoryProfileSeal

module RepositoryProfileAdapter =
    let allowedCapabilities =
        [ "build-config"; "contract-coherence"; "coordination-kit"; "labels"; "lockfile-sync"; "skill-union" ]

    let private sha256 (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private opt = Option.defaultValue ""
    let private roleText = function Authority -> "authority" | Framework -> "framework" | NonParticipant -> "non-participant"
    let private adminText = function OrganizationAdministered -> "organization" | ExternalObserveOnly -> "external"

    let private canonicalRow (row: RepositoryRosterRow) =
        [ row.Id.Trim().ToLowerInvariant()
          row.FullName.Trim()
          roleText row.Role
          row.Capabilities |> List.map (_.Trim().ToLowerInvariant()) |> List.sort |> String.concat ","
          row.KitDelivery |> Option.map (_.Trim().ToLowerInvariant()) |> opt
          row.AbsenceCover |> Option.map (_.Trim().ToLowerInvariant()) |> opt
          row.Reason |> Option.map _.Trim() |> opt ]
        |> List.map frame
        |> String.concat ""

    let canonicalRosterDigest (rows: RepositoryRosterRow list) =
        rows
        |> List.sortBy (fun row -> row.FullName.ToLowerInvariant())
        |> List.map canonicalRow
        |> String.concat ""
        |> sha256

    let private digestLike (value: string) =
        value.Length = 64 && value |> Seq.forall Uri.IsHexDigit

    let private repositoryOwner (fullName: string) =
        match fullName.Split('/') with
        | [| owner; repository |] when owner <> "" && repository <> "" && owner.Trim() = owner && repository.Trim() = repository -> Some owner
        | _ -> None

    let private duplicates projection rows finding =
        rows
        |> List.groupBy projection
        |> List.choose (fun (key, values) -> if List.length values > 1 then Some(finding key) else None)

    let private validateRow (row: RepositoryRosterRow) =
        let owner = repositoryOwner row.FullName
        let capabilities = row.Capabilities |> List.map (_.Trim().ToLowerInvariant())
        let unsupported = capabilities |> List.filter (fun capability -> not (List.contains capability allowedCapabilities))
        let duplicateCapabilities = capabilities |> List.countBy id |> List.exists (fun (_, count) -> count > 1)
        let hasKit = List.contains "coordination-kit" capabilities
        [ if row.Id.Trim() = "" || row.Id.Trim() <> row.Id || owner.IsNone then yield InvalidRepositoryIdentity row.FullName
          for capability in unsupported do yield UnsupportedRepositoryCapability(row.FullName, capability)
          if duplicateCapabilities then yield InvalidRepositoryMetadata row.FullName
          match row.Role, owner with
          | Authority, Some "FS-GG" when row.FullName = "FS-GG/.github" && row.Reason.IsNone -> ()
          | Authority, _ -> yield UnsupportedRepositoryRole row.FullName
          | Framework, Some "FS-GG" when not capabilities.IsEmpty && row.Reason.IsNone -> ()
          | Framework, _ -> yield UnsupportedRepositoryRole row.FullName
          | NonParticipant, Some _ when capabilities.IsEmpty && row.Reason |> Option.exists (String.IsNullOrWhiteSpace >> not) -> ()
          | NonParticipant, _ -> yield InvalidRepositoryMetadata row.FullName
          match row.KitDelivery, row.AbsenceCover, hasKit with
          | Some "package", Some "required", true -> ()
          | None, None, false -> ()
          | _ -> yield InvalidRepositoryMetadata row.FullName ]

    let private nativeProperties role administration =
        let mode =
            match role, administration with
            | _, ExternalObserveOnly -> "observe-only"
            | Authority, _ -> "source"
            | Framework, _ -> "receiver"
            | NonParticipant, _ -> "inert"
        [ { Name = "fsgg_role"; Value = roleText role }
          { Name = "fsgg_owner_scope"; Value = adminText administration }
          { Name = "fsgg_coordination_mode"; Value = mode } ]

    let private profileOf (row: RepositoryRosterRow) : RepositoryProfile =
        let owner = repositoryOwner row.FullName |> Option.defaultValue ""
        let administration = if owner.Equals("FS-GG", StringComparison.OrdinalIgnoreCase) then OrganizationAdministered else ExternalObserveOnly
        let capabilities = row.Capabilities |> List.map (_.Trim().ToLowerInvariant()) |> List.sort
        let properties = if administration = OrganizationAdministered then nativeProperties row.Role administration else []
        { Id = row.Id.Trim().ToLowerInvariant()
          FullName = row.FullName
          Role = row.Role
          Administration = administration
          Capabilities = capabilities
          KitDelivery = row.KitDelivery
          AbsenceCover = row.AbsenceCover
          Reason = row.Reason
          NativeProperties = properties
          PropertyMutationPermitted = administration = OrganizationAdministered }

    let private profileSeal (snapshot: RepositoryRosterSnapshot) (profiles: RepositoryProfile list) =
        let profileText (profile: RepositoryProfile) =
            [ profile.Id; profile.FullName; roleText profile.Role; adminText profile.Administration
              String.concat "," profile.Capabilities; opt profile.KitDelivery; opt profile.AbsenceCover; opt profile.Reason
              profile.NativeProperties |> List.map (fun property -> $"{property.Name}={property.Value}") |> String.concat ","
              string profile.PropertyMutationPermitted ]
            |> List.map frame
            |> String.concat ""
        [ snapshot.SourceRevision; snapshot.SourceArtifactSha256; snapshot.CanonicalRosterSha256
          profiles |> List.map profileText |> String.concat "" ]
        |> List.map frame
        |> String.concat ""
        |> sha256

    let compile (asOf: DateTimeOffset) (maxAge: TimeSpan) (snapshot: RepositoryRosterSnapshot) =
        let rows = snapshot.Rows
        let computedRosterDigest = canonicalRosterDigest rows
        let findings =
            [ if snapshot.SchemaVersion <> 1 then UnsupportedRosterSchema snapshot.SchemaVersion
              if not snapshot.Complete || rows.IsEmpty then IncompleteRoster
              if snapshot.ReviewedAt > asOf || asOf - snapshot.ReviewedAt > maxAge then StaleRoster
              if String.IsNullOrWhiteSpace snapshot.SourceRevision then InvalidSourceBinding "sourceRevision"
              if not (digestLike snapshot.SourceArtifactSha256) then InvalidSourceBinding "sourceArtifactSha256"
              if not (digestLike snapshot.CanonicalRosterSha256) || snapshot.CanonicalRosterSha256 <> computedRosterDigest then InvalidSourceBinding "canonicalRosterSha256"
              yield! duplicates (fun (row: RepositoryRosterRow) -> row.Id.ToLowerInvariant()) rows DuplicateRepositoryId
              yield! duplicates (fun (row: RepositoryRosterRow) -> row.FullName.ToLowerInvariant()) rows DuplicateRepositoryName
              yield! rows |> List.collect validateRow
              match rows |> List.filter (fun row -> row.Role = Authority) |> List.length with
              | 0 -> MissingAuthority
              | 1 -> ()
              | _ -> MultipleAuthorities ]
        if not findings.IsEmpty then Error findings
        else
            let profiles = rows |> List.map profileOf |> List.sortBy (fun (profile: RepositoryProfile) -> profile.FullName.ToLowerInvariant())
            let retentionFindings =
                List.zip (rows |> List.sortBy (fun (row: RepositoryRosterRow) -> row.FullName.ToLowerInvariant())) profiles
                |> List.choose (fun ((row: RepositoryRosterRow), (profile: RepositoryProfile)) ->
                    if row.Capabilities |> List.map (_.Trim().ToLowerInvariant()) |> List.sort <> profile.Capabilities
                       || row.Reason <> profile.Reason || row.KitDelivery <> profile.KitDelivery || row.AbsenceCover <> profile.AbsenceCover
                    then Some(LossyRepositoryProfile row.FullName) else None)
            let propertyFindings =
                profiles
                |> List.collect (fun (profile: RepositoryProfile) ->
                    profile.NativeProperties
                    |> List.choose (fun property -> if property.Name.Length > 75 || property.Value.Length > 200 then Some(NativePropertyOverflow(profile.FullName, property.Name)) else None))
            let externalFindings =
                profiles
                |> List.choose (fun (profile: RepositoryProfile) ->
                    if profile.Administration = ExternalObserveOnly && (profile.PropertyMutationPermitted || not profile.NativeProperties.IsEmpty)
                    then Some(LossyRepositoryProfile profile.FullName) else None)
            let finalFindings = retentionFindings @ propertyFindings @ externalFindings
            if not finalFindings.IsEmpty then Error finalFindings
            else
                let report: RepositoryProfileReport =
                    { SourceRevision = snapshot.SourceRevision
                      SourceArtifactSha256 = snapshot.SourceArtifactSha256.ToLowerInvariant()
                      CanonicalRosterSha256 = computedRosterDigest
                      Profiles = profiles
                      Seal = profileSeal snapshot profiles }
                Ok report

    let verify expectedSeal asOf maxAge snapshot =
        match compile asOf maxAge snapshot with
        | Ok report when report.Seal = expectedSeal -> Ok report
        | Ok _ -> Error [ AlteredRepositoryProfileSeal ]
        | Error findings -> Error findings
