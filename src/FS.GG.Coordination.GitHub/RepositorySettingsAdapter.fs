namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type RepositoryIdentity = { NodeId: string; DatabaseId: int64; Owner: string; Name: string; DefaultBranch: string; SourceRepositoryNodeId: string option }
type SettingsSurface = Repository | CustomProperties | BranchRulesets | TagRulesets | MergePolicy | ActionsPolicy | Environments | ReleasesAndTags | CodeSecurity | DependencyControls | ImmutableReleases
type SettingValue = Boolean of bool | Integer of int64 | Text of string | TextList of string list
type RepositorySetting = { Surface: SettingsSurface; Subject: string; Name: string; Value: SettingValue }
type SurfaceObservation = Supported of Revision: string * Complete: bool * Settings: RepositorySetting list | Unsupported of reason: string | Unauthorized of reason: string | Unavailable of reason: string | Incomplete of reason: string | Unreadable of reason: string
type RepositorySettingsObservation = { Identity: RepositoryIdentity; CapturedRevision: string; Surfaces: Map<SettingsSurface, SurfaceObservation>; Digest: string }
type DesiredRepositorySettings = { Identity: RepositoryIdentity; Settings: RepositorySetting list; Digest: string }
type SettingsFailure = InvalidIdentity | IdentityDrift | MissingSurface of SettingsSurface | PartialSurface of SettingsSurface * string | UnsupportedDesiredSurface of SettingsSurface | ContradictorySetting of SettingsSurface * string * string | SecretValueForbidden of string | InvalidObservationDigest | InvalidDesiredDigest | StaleObservation of expected: string * actual: string
type SettingsOperation = { OperationId: string; Surface: SettingsSurface; Subject: string; Name: string; Before: SettingValue option; After: SettingValue option; RequiredPermission: string; ObservationDigest: string; DesiredDigest: string }
type RepositorySettingsPlan = { Identity: RepositoryIdentity; ObservationRevision: string; ObservationDigest: string; DesiredDigest: string; Operations: SettingsOperation list }
type SettingsTransportOutcome = SettingsAccepted | SettingsDefiniteRefusal of string | SettingsResponseUnknown | SettingsPartiallyApplied of operationIds: string list
type SettingsReconcileOutcome = SettingsVerified | SettingsRereadAndReplan | SettingsRollback of SettingsOperation list | SettingsForwardRepair of SettingsOperation list | SettingsRefused of string | SettingsIndeterminate of SettingsFailure

[<RequireQualifiedAccess>]
module RepositorySettingsAdapter =
    let surfaces = [ Repository; CustomProperties; BranchRulesets; TagRulesets; MergePolicy; ActionsPolicy; Environments; ReleasesAndTags; CodeSecurity; DependencyControls; ImmutableReleases ]
    let surfaceId = function Repository -> "repository" | CustomProperties -> "custom-properties" | BranchRulesets -> "branch-rulesets" | TagRulesets -> "tag-rulesets" | MergePolicy -> "merge-policy" | ActionsPolicy -> "actions-policy" | Environments -> "environments" | ReleasesAndTags -> "releases-tags" | CodeSecurity -> "code-security" | DependencyControls -> "dependency-controls" | ImmutableReleases -> "immutable-releases"
    let sha256 (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
    let private hashText (value: string) = value |> Encoding.UTF8.GetBytes |> sha256
    let private validText (value: string) = not (String.IsNullOrWhiteSpace value) && value = value.Trim()
    let private escape (value: string) = Convert.ToBase64String(Encoding.UTF8.GetBytes value)
    let private identityText (identity: RepositoryIdentity) = String.concat "|" [ escape identity.NodeId; string identity.DatabaseId; escape identity.Owner; escape identity.Name; escape identity.DefaultBranch; identity.SourceRepositoryNodeId |> Option.map escape |> Option.defaultValue "-" ]
    let identityDigest (identity: RepositoryIdentity) = identityText identity |> hashText
    let private valueText = function
        | Boolean value -> if value then "b:1" else "b:0"
        | Integer value -> $"i:{value}"
        | Text value -> "t:" + escape value
        | TextList values -> "l:" + (values |> List.sort |> List.map escape |> String.concat ",")
    let private settingKey (setting: RepositorySetting) = setting.Surface, setting.Subject, setting.Name
    let private settingText (setting: RepositorySetting) =
        let surface, subject, name = settingKey setting
        $"{surfaceId surface}|{escape subject}|{escape name}|{valueText setting.Value}"
    let private observationText = function
        | Supported(revision, complete, settings) ->
            let encodedSettings = settings |> List.sortBy settingKey |> List.map settingText |> String.concat ";"
            $"supported|{escape revision}|{complete}|{encodedSettings}"
        | Unsupported reason -> $"unsupported|{escape reason}" | Unauthorized reason -> $"unauthorized|{escape reason}"
        | Unavailable reason -> $"unavailable|{escape reason}" | Incomplete reason -> $"incomplete|{escape reason}" | Unreadable reason -> $"unreadable|{escape reason}"
    let observationDigest (identity: RepositoryIdentity) (capturedRevision: string) (surfacesMap: Map<SettingsSurface, SurfaceObservation>) =
        let surfaceText =
            surfaces
            |> List.map (fun surface ->
                let encoded = surfacesMap |> Map.tryFind surface |> Option.map observationText |> Option.defaultValue "missing"
                $"{surfaceId surface}={encoded}")
            |> String.concat "\n"
        hashText $"{identityText identity}\n{escape capturedRevision}\n{surfaceText}\n"
    let desiredDigest (identity: RepositoryIdentity) (settings: RepositorySetting list) =
        let encodedSettings = settings |> List.sortBy settingKey |> List.map settingText |> String.concat "\n"
        hashText $"{identityText identity}\n{encodedSettings}\n"
    let private validIdentity (identity: RepositoryIdentity) = identity.DatabaseId > 0L && [ identity.NodeId; identity.Owner; identity.Name; identity.DefaultBranch ] |> List.forall validText
    let private isSecretName (name: string) =
        name.Equals("secret", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("-secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase)
    let private validateSettings (settings: RepositorySetting list) =
        match settings |> List.tryFind (fun setting -> not (validText setting.Subject) || not (validText setting.Name) || isSecretName setting.Name) with
        | Some setting when isSecretName setting.Name -> Error(SecretValueForbidden setting.Name)
        | Some setting -> Error(ContradictorySetting(setting.Surface, setting.Subject, setting.Name))
        | None ->
            match settings |> List.groupBy settingKey |> List.tryFind (fun (_, values) -> values.Length > 1) with
            | Some((surface, subject, name), _) -> Error(ContradictorySetting(surface, subject, name))
            | None -> Ok ()
    let validate (observation: RepositorySettingsObservation) =
        if not (validIdentity observation.Identity) || not (validText observation.CapturedRevision) then Error InvalidIdentity
        else
            let folder state surface =
                state |> Result.bind (fun () ->
                    match Map.tryFind surface observation.Surfaces with
                    | None -> Error(MissingSurface surface)
                    | Some(Supported(revision, true, settings)) when revision = observation.CapturedRevision && settings |> List.forall (fun item -> item.Surface = surface) -> validateSettings settings
                    | Some(Supported(_, false, _)) -> Error(PartialSurface(surface, "pagination incomplete"))
                    | Some(Supported(_, true, _)) -> Error(PartialSurface(surface, "revision or surface identity drift"))
                    | Some(Unsupported _) -> Ok ()
                    | Some(Unauthorized reason) -> Error(PartialSurface(surface, reason))
                    | Some(Unavailable reason) -> Error(PartialSurface(surface, reason))
                    | Some(Incomplete reason) -> Error(PartialSurface(surface, reason))
                    | Some(Unreadable reason) -> Error(PartialSurface(surface, reason)))
            match surfaces |> List.fold folder (Ok ()) with
            | Error failure -> Error failure
            | Ok () when observation.Digest <> observationDigest observation.Identity observation.CapturedRevision observation.Surfaces -> Error InvalidObservationDigest
            | Ok () -> Ok observation
    let private permission = function ActionsPolicy -> "actions:write" | Environments -> "environments:write" | CodeSecurity | DependencyControls -> "security_events:write" | ReleasesAndTags | ImmutableReleases -> "contents:write" | _ -> "administration:write"
    let private operationId observation desired (setting: RepositorySetting) after =
        let encodedAfter = after |> Option.map valueText |> Option.defaultValue "delete"
        hashText $"{observation}|{desired}|{settingText setting}|{encodedAfter}"
    let plan (expectedRevision: string) (observation: RepositorySettingsObservation) (desired: DesiredRepositorySettings) =
        validate observation |> Result.bind (fun valid ->
            if valid.Identity <> desired.Identity then Error IdentityDrift
            elif valid.CapturedRevision <> expectedRevision then Error(StaleObservation(expectedRevision, valid.CapturedRevision))
            elif desired.Digest <> desiredDigest desired.Identity desired.Settings then Error InvalidDesiredDigest
            else validateSettings desired.Settings |> Result.bind (fun () ->
                match desired.Settings |> List.tryFind (fun setting -> match Map.find setting.Surface valid.Surfaces with Unsupported _ -> true | _ -> false) with
                | Some setting -> Error(UnsupportedDesiredSurface setting.Surface)
                | None ->
                    let current = valid.Surfaces |> Map.toList |> List.collect (fun (_, value) -> match value with Supported(_, _, settings) -> settings | _ -> []) |> List.map (fun setting -> settingKey setting, setting) |> Map.ofList
                    let wanted = desired.Settings |> List.map (fun setting -> settingKey setting, setting) |> Map.ofList
                    let keys = Set.union (current |> Map.keys |> Set.ofSeq) (wanted |> Map.keys |> Set.ofSeq) |> Set.toList
                    let operations =
                        keys |> List.choose (fun key ->
                            let before = current |> Map.tryFind key
                            let after = wanted |> Map.tryFind key
                            if before |> Option.map _.Value = (after |> Option.map _.Value) then None else
                            let exemplar = after |> Option.orElse before |> Option.get
                            Some { OperationId = operationId valid.Digest desired.Digest exemplar (after |> Option.map _.Value); Surface = exemplar.Surface; Subject = exemplar.Subject; Name = exemplar.Name; Before = before |> Option.map _.Value; After = after |> Option.map _.Value; RequiredPermission = permission exemplar.Surface; ObservationDigest = valid.Digest; DesiredDigest = desired.Digest })
                        |> List.sortBy (fun operation -> surfaces |> List.findIndex ((=) operation.Surface), operation.Subject, operation.Name, operation.OperationId)
                    Ok { Identity = valid.Identity; ObservationRevision = valid.CapturedRevision; ObservationDigest = valid.Digest; DesiredDigest = desired.Digest; Operations = operations }))
    let private desiredFromPlan (plan: RepositorySettingsPlan) =
        plan.Operations |> List.choose (fun operation -> operation.After |> Option.map (fun value -> { Surface = operation.Surface; Subject = operation.Subject; Name = operation.Name; Value = value }))
    let private exactPoststate (plan: RepositorySettingsPlan) (observation: RepositorySettingsObservation) =
        match validate observation with
        | Error failure -> Error failure
        | Ok valid when valid.Identity <> plan.Identity -> Error IdentityDrift
        | Ok valid ->
            let actual = valid.Surfaces |> Map.toList |> List.collect (fun (_, value) -> match value with Supported(_, _, settings) -> settings | _ -> [])
            let changedKeys = plan.Operations |> List.map (fun operation -> operation.Surface, operation.Subject, operation.Name) |> Set.ofList
            let unchanged = actual |> List.filter (fun setting -> not (Set.contains (settingKey setting) changedKeys))
            let expected = unchanged @ desiredFromPlan plan
            Ok(desiredDigest plan.Identity expected = desiredDigest plan.Identity actual)
    let reconcile (plan: RepositorySettingsPlan) (outcome: SettingsTransportOutcome) (poststate: RepositorySettingsObservation) =
        match outcome with
        | SettingsDefiniteRefusal reason -> SettingsRefused reason
        | _ ->
            match exactPoststate plan poststate with
            | Ok true -> SettingsVerified
            | Error failure -> SettingsIndeterminate failure
            | Ok false ->
                match outcome with
                | SettingsAccepted | SettingsResponseUnknown -> SettingsRereadAndReplan
                | SettingsPartiallyApplied ids ->
                    let applied, remaining = plan.Operations |> List.partition (fun operation -> List.contains operation.OperationId ids)
                    if List.isEmpty remaining then SettingsRereadAndReplan
                    elif applied.Length * 2 > plan.Operations.Length then SettingsForwardRepair remaining
                    else SettingsRollback(List.rev applied)
                | SettingsDefiniteRefusal _ -> failwith "unreachable"
