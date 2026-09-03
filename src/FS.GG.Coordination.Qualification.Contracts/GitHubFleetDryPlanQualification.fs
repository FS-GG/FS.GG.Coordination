namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type GitHubFleetDisposition =
    | Supported | Unsupported | Unauthorized | Unavailable | Incomplete | Unreadable
    | Stale | Indeterminate | ExternalObserveOnly | NoOp
type GitHubFleetPaginationProof = { Kind: string; Pages: int; ItemCount: int; Terminal: bool; Next: string option }
type GitHubFleetEndpointObservation =
    { Endpoint: string; StatusCode: int; Permission: string; Pagination: GitHubFleetPaginationProof
      PayloadSha256: string; RelevantFingerprint: string; Disposition: GitHubFleetDisposition }
type GitHubFleetRepositoryObservation =
    { Repository: string; DefaultBranch: string; ObservedAt: DateTimeOffset
      Complete: bool; Endpoints: GitHubFleetEndpointObservation list }
type GitHubFleetDesiredSetting =
    { Setting: string; DesiredSha256: string; RequiredPermission: string; RollbackOrForwardRepair: string }
type GitHubFleetRepositoryTarget = { Repository: string; ExternalOwner: bool; Settings: GitHubFleetDesiredSetting list }
type GitHubFleetOperation =
    { Id: string; Repository: string; Setting: string; Action: string; PreStateSha256: string
      DesiredSha256: string; RequiredPermission: string; RollbackOrForwardRepair: string }
type GitHubFleetRepositoryPlan =
    { Repository: string; DefaultBranch: string; ObservedAt: DateTimeOffset; ObservationComplete: bool; PreStateSha256: string
      DesiredStateSha256: string; Disposition: GitHubFleetDisposition
      Settings: GitHubFleetEndpointObservation list; DesiredSettings: GitHubFleetDesiredSetting list; Operations: GitHubFleetOperation list
      PreservesUnrelatedSettings: bool }
type GitHubFleetDryPlan =
    { SchemaVersion: int; RoadmapRevision: string; RoadmapSha256: string; UnitContractSha256: string
      SourceRevision: string; CompiledAt: DateTimeOffset; MaxObservationAge: TimeSpan; Author: string
      ReceiptDigests: string list; Roster: string list
      Plans: GitHubFleetRepositoryPlan list; Seal: string }
type GitHubFleetPlanReview =
    { Author: string; Reviewer: string; ReviewedAt: DateTimeOffset; PlanSha256: string
      Independent: bool; Accepted: bool; EvidenceSha256: string }
type GitHubFleetReviewedPlan = { Plan: GitHubFleetDryPlan; Review: GitHubFleetPlanReview; Seal: string }
type GitHubFleetReinspection =
    { Repository: string; ObservedAt: DateTimeOffset; RelevantFingerprint: string
      Complete: bool; Authoritative: bool }
type GitHubFleetReinspectionResult = Confirmed | PlanStale of repositories: string list
[<RequireQualifiedAccess>]
type GitHubFleetDryPlanFinding =
    | InvalidFleetField of string | InvalidFleetAuthority of string | InvalidFleetRoster
    | IncompleteFleetObservation of string | InvalidFleetPagination of string
    | InvalidFleetDisposition of string | InvalidFleetTarget of string
    | InvalidFleetOperation of string | InvalidFleetReview | InvalidFleetReinspection of string
    | AlteredFleetSeal | InvalidFleetSerialization of string
type GitHubFleetControl =
    | FleetPrerequisites | FleetRoadmap | FleetRoster | FleetCompleteness | FleetPagination
    | FleetRepositoryIdentity | FleetDefaultBranch | FleetObservationTime | FleetPreState
    | FleetDesiredState | FleetOperationIdentity | FleetOrdering | FleetLeastPermission
    | FleetSupported | FleetUnsupported | FleetUnauthorized | FleetUnavailable | FleetIncomplete
    | FleetUnreadable | FleetStale | FleetIndeterminate | FleetExternalOwner | FleetNoOp
    | FleetUnrelatedSetting | FleetReview | FleetReinspection | FleetSerialization | FleetReplay
    | FleetComprehensiveGate | FleetOmission | FleetQuintPreservation | FleetNoApply | FleetNoMutation
type GitHubFleetControlResult = { Control: GitHubFleetControl; ControlPassed: bool; BaselineGreen: bool }
type GitHubFleetQualificationFinding = { Code: string; ControlId: string; Message: string }

module GitHubFleetDryPlanQualification =
    let expectedRepositories =
        [ "EHotwagner/S.I.R."; "FS-GG/.github"; "FS-GG/FS.GG.Audio"; "FS-GG/FS.GG.Coordination"
          "FS-GG/FS.GG.Game"; "FS-GG/FS.GG.Governance"; "FS-GG/FS.GG.Net"
          "FS-GG/FS.GG.Rendering"; "FS-GG/FS.GG.SDD"; "FS-GG/FS.GG.Templates" ]

    let expectedEndpoints =
        [ "actions-permissions"; "actions-selected-actions"; "actions-workflow-permissions"
          "automated-security-fixes"; "branch-protection"; "code-security-configuration"
          "environments"; "releases-and-tags"; "repository"; "rulesets"; "vulnerability-alerts"; "workflows" ]

    let expectedReceiptDigests =
        [ "0f6a142023f21a266242997ae896e494dfa668e895e308ad73d2d5e01404c042"
          "7157ad56a4879e48642dbb055b0b35158353cbc020fca9a008ed901446d74d0c"
          "eec15747e2e5c1cf0ae91fbf370eb82a3e6ea88d6fe3c0f2f738a556e63e5063"
          "9f2476ebea520372f836b69fc8b1d11300d5299ed1796fc34cc70afead9e2a76"
          "9227977242b530755cbc28ff9093fa810aab9647037d3ae4b60cd7311c86cd0f"
          "517172e0eb31d3fd2eefb5844ed426d67d128f795c16195010eb772b7fcd2a5f"
          "c6d1662e7df93f8b6ca8f577b5143e1e8a45eb9ac6fe55922488659ff9363036" ]

    let requiredControls =
        [ FleetPrerequisites; FleetRoadmap; FleetRoster; FleetCompleteness; FleetPagination
          FleetRepositoryIdentity; FleetDefaultBranch; FleetObservationTime; FleetPreState
          FleetDesiredState; FleetOperationIdentity; FleetOrdering; FleetLeastPermission
          FleetSupported; FleetUnsupported; FleetUnauthorized; FleetUnavailable; FleetIncomplete
          FleetUnreadable; FleetStale; FleetIndeterminate; FleetExternalOwner; FleetNoOp
          FleetUnrelatedSetting; FleetReview; FleetReinspection; FleetSerialization; FleetReplay
          FleetComprehensiveGate; FleetOmission; FleetQuintPreservation; FleetNoApply; FleetNoMutation ]

    let dispositionId = function
        | GitHubFleetDisposition.Supported -> "supported" | GitHubFleetDisposition.Unsupported -> "unsupported" | GitHubFleetDisposition.Unauthorized -> "unauthorized"
        | GitHubFleetDisposition.Unavailable -> "unavailable" | GitHubFleetDisposition.Incomplete -> "incomplete" | GitHubFleetDisposition.Unreadable -> "unreadable"
        | GitHubFleetDisposition.Stale -> "stale" | GitHubFleetDisposition.Indeterminate -> "indeterminate"
        | GitHubFleetDisposition.ExternalObserveOnly -> "external-observe-only" | GitHubFleetDisposition.NoOp -> "no-op"

    let private dispositionOf = function
        | "supported" -> Some GitHubFleetDisposition.Supported | "unsupported" -> Some GitHubFleetDisposition.Unsupported | "unauthorized" -> Some GitHubFleetDisposition.Unauthorized
        | "unavailable" -> Some GitHubFleetDisposition.Unavailable | "incomplete" -> Some GitHubFleetDisposition.Incomplete | "unreadable" -> Some GitHubFleetDisposition.Unreadable
        | "stale" -> Some GitHubFleetDisposition.Stale | "indeterminate" -> Some GitHubFleetDisposition.Indeterminate
        | "external-observe-only" -> Some GitHubFleetDisposition.ExternalObserveOnly | "no-op" -> Some GitHubFleetDisposition.NoOp | _ -> None

    let controlId = function
        | FleetPrerequisites -> "fleet-prerequisites" | FleetRoadmap -> "fleet-roadmap"
        | FleetRoster -> "fleet-roster" | FleetCompleteness -> "fleet-completeness"
        | FleetPagination -> "fleet-pagination" | FleetRepositoryIdentity -> "fleet-repository-identity"
        | FleetDefaultBranch -> "fleet-default-branch" | FleetObservationTime -> "fleet-observation-time"
        | FleetPreState -> "fleet-pre-state" | FleetDesiredState -> "fleet-desired-state"
        | FleetOperationIdentity -> "fleet-operation-identity" | FleetOrdering -> "fleet-ordering"
        | FleetLeastPermission -> "fleet-least-permission" | FleetSupported -> "fleet-supported"
        | FleetUnsupported -> "fleet-unsupported" | FleetUnauthorized -> "fleet-unauthorized"
        | FleetUnavailable -> "fleet-unavailable" | FleetIncomplete -> "fleet-incomplete"
        | FleetUnreadable -> "fleet-unreadable" | FleetStale -> "fleet-stale"
        | FleetIndeterminate -> "fleet-indeterminate" | FleetExternalOwner -> "fleet-external-owner"
        | FleetNoOp -> "fleet-no-op" | FleetUnrelatedSetting -> "fleet-unrelated-setting"
        | FleetReview -> "fleet-review" | FleetReinspection -> "fleet-reinspection"
        | FleetSerialization -> "fleet-serialization" | FleetReplay -> "fleet-replay"
        | FleetComprehensiveGate -> "fleet-comprehensive-gate" | FleetOmission -> "fleet-omission"
        | FleetQuintPreservation -> "fleet-quint-preservation" | FleetNoApply -> "fleet-no-apply"
        | FleetNoMutation -> "fleet-no-mutation"

    let private validText (value: string) = not (String.IsNullOrWhiteSpace value)
    let private isSha length (value: string) =
        validText value && value.Length = length && Regex.IsMatch(value, "^[0-9a-f]+$", RegexOptions.CultureInvariant)
    let private hash (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private framed values = values |> List.map frame |> String.concat ""
    let private unique values = List.length values = (values |> Set.ofList |> Set.count)
    let private ordered values = values = List.sort values

    let private observationFingerprint (value: GitHubFleetRepositoryObservation) =
        value.Endpoints
        |> List.map (fun endpoint -> $"{endpoint.Endpoint}|{endpoint.RelevantFingerprint}")
        |> framed |> hash

    let private desiredFingerprint (value: GitHubFleetRepositoryTarget) =
        value.Settings
        |> List.map (fun setting -> $"{setting.Setting}|{setting.DesiredSha256}|{setting.RequiredPermission}|{setting.RollbackOrForwardRepair}")
        |> framed |> hash

    let private operationId repository setting action pre desired permission repair =
        [ repository; setting; action; pre; desired; permission; repair ] |> framed |> hash

    let private endpointText (endpoint: GitHubFleetEndpointObservation) =
        [ endpoint.Endpoint; string endpoint.StatusCode; endpoint.Permission; endpoint.Pagination.Kind
          string endpoint.Pagination.Pages; string endpoint.Pagination.ItemCount; string endpoint.Pagination.Terminal
          defaultArg endpoint.Pagination.Next ""; endpoint.PayloadSha256; endpoint.RelevantFingerprint; dispositionId endpoint.Disposition ] |> framed
    let private desiredText (setting: GitHubFleetDesiredSetting) =
        [ setting.Setting; setting.DesiredSha256; setting.RequiredPermission; setting.RollbackOrForwardRepair ] |> framed
    let private planSeal (roadmapRevision: string) (roadmapSha: string) (unitContract: string) (sourceRevision: string) (compiledAt: DateTimeOffset) (maxAge: TimeSpan) (author: string) (receipts: string list) (roster: string list) (plans: GitHubFleetRepositoryPlan list) =
        let planText (plan: GitHubFleetRepositoryPlan) =
            let operations =
                plan.Operations
                |> List.map (fun (op: GitHubFleetOperation) ->
                    [ op.Id; op.Repository; op.Setting; op.Action; op.PreStateSha256; op.DesiredSha256
                      op.RequiredPermission; op.RollbackOrForwardRepair ] |> framed)
                |> framed
            [ plan.Repository; plan.DefaultBranch; plan.ObservedAt.ToString("O", CultureInfo.InvariantCulture); string plan.ObservationComplete
              plan.PreStateSha256; plan.DesiredStateSha256; dispositionId plan.Disposition
              plan.Settings |> List.map endpointText |> framed; plan.DesiredSettings |> List.map desiredText |> framed
              string plan.PreservesUnrelatedSettings; operations ] |> framed
        [ "1"; roadmapRevision; roadmapSha; unitContract; sourceRevision; compiledAt.ToString("O", CultureInfo.InvariantCulture)
          maxAge.Ticks.ToString(CultureInfo.InvariantCulture); author
          framed receipts; framed roster; plans |> List.map planText |> framed ] |> framed |> hash

    let private reviewEvidence (review: GitHubFleetPlanReview) =
        [ review.Author; review.Reviewer; review.ReviewedAt.ToString("O", CultureInfo.InvariantCulture); review.PlanSha256
          string review.Independent; string review.Accepted ] |> framed |> hash
    let private reviewedSeal (plan: GitHubFleetDryPlan) (review: GitHubFleetPlanReview) =
        [ plan.Seal; review.Author; review.Reviewer; review.ReviewedAt.ToString("O", CultureInfo.InvariantCulture)
          review.PlanSha256; string review.Independent; string review.Accepted; review.EvidenceSha256 ] |> framed |> hash

    let private expectedReadPermission = function
        | "repository" -> "metadata:read" | "workflows" -> "actions:read"
        | "environments" -> "deployments:read" | "releases-and-tags" -> "contents:read"
        | "automated-security-fixes" | "code-security-configuration" | "vulnerability-alerts" -> "security_events:read"
        | _ -> "administration:read"
    let private expectedWritePermission = function
        | "workflows" -> "actions:write" | "releases-and-tags" -> "contents:write"
        | "automated-security-fixes" | "code-security-configuration" | "vulnerability-alerts" -> "security_events:write"
        | _ -> "administration:write"

    let private statusMatches (endpoint: GitHubFleetEndpointObservation) =
        match endpoint.Disposition with
        | GitHubFleetDisposition.Supported | GitHubFleetDisposition.NoOp -> endpoint.StatusCode = 200 || endpoint.StatusCode = 204
        | GitHubFleetDisposition.Unsupported -> endpoint.StatusCode = 404 || endpoint.StatusCode = 422
        | GitHubFleetDisposition.Unauthorized -> endpoint.StatusCode = 401 || endpoint.StatusCode = 403
        | GitHubFleetDisposition.Unavailable -> endpoint.StatusCode >= 500
        | GitHubFleetDisposition.Incomplete -> not endpoint.Pagination.Terminal || endpoint.Pagination.Next.IsSome
        | GitHubFleetDisposition.Unreadable -> endpoint.StatusCode = 0
        | GitHubFleetDisposition.Stale | GitHubFleetDisposition.Indeterminate | GitHubFleetDisposition.ExternalObserveOnly -> endpoint.StatusCode >= 0

    let private repositoryDisposition (externalOwner: bool) (observation: GitHubFleetRepositoryObservation) (operations: GitHubFleetOperation list) =
        if externalOwner then GitHubFleetDisposition.ExternalObserveOnly
        else
            let has value = observation.Endpoints |> List.exists (fun (endpoint: GitHubFleetEndpointObservation) -> endpoint.Disposition = value)
            if has GitHubFleetDisposition.Unreadable then GitHubFleetDisposition.Unreadable elif has GitHubFleetDisposition.Unauthorized then GitHubFleetDisposition.Unauthorized
            elif has GitHubFleetDisposition.Unavailable then GitHubFleetDisposition.Unavailable elif has GitHubFleetDisposition.Incomplete || not observation.Complete then GitHubFleetDisposition.Incomplete
            elif has GitHubFleetDisposition.Stale then GitHubFleetDisposition.Stale elif has GitHubFleetDisposition.Indeterminate then GitHubFleetDisposition.Indeterminate elif has GitHubFleetDisposition.Unsupported then GitHubFleetDisposition.Unsupported
            elif List.isEmpty operations then GitHubFleetDisposition.NoOp else GitHubFleetDisposition.Supported

    let compile (roadmapRevision: string) (roadmapSha256: string) (unitContractSha256: string) (sourceRevision: string) (compiledAt: DateTimeOffset) (maxObservationAge: TimeSpan) (author: string) (receiptDigests: string list) (roster: string list) (observations: GitHubFleetRepositoryObservation list) (targets: GitHubFleetRepositoryTarget list) =
        let findings = ResizeArray<GitHubFleetDryPlanFinding>()
        if roadmapRevision <> "ac05985f0d60c33fb40a5dccecb271a3e00bec4b"
           || roadmapSha256 <> "888d1c3307ba119f6c7075b0d8963f7fa14d1e357ce1f97fdb7c803f1aa5465f" then
            findings.Add(GitHubFleetDryPlanFinding.InvalidFleetAuthority "roadmap")
        if unitContractSha256 <> "316343c921c7444cb95bee292bec8d6da3c6546ffe8805bf93a0490249c76717" then
            findings.Add(GitHubFleetDryPlanFinding.InvalidFleetAuthority "unit-contract")
        if not (isSha 40 sourceRevision) then findings.Add(GitHubFleetDryPlanFinding.InvalidFleetField "sourceRevision")
        if compiledAt = DateTimeOffset.MinValue || maxObservationAge <= TimeSpan.Zero || not (validText author) then findings.Add(GitHubFleetDryPlanFinding.InvalidFleetField "compilation-authority")
        if receiptDigests <> expectedReceiptDigests then findings.Add(GitHubFleetDryPlanFinding.InvalidFleetAuthority "receipts")
        if roster <> expectedRepositories || not (unique roster) then findings.Add GitHubFleetDryPlanFinding.InvalidFleetRoster
        let observationNames = observations |> List.map (fun (value: GitHubFleetRepositoryObservation) -> value.Repository)
        let targetNames = targets |> List.map (fun (value: GitHubFleetRepositoryTarget) -> value.Repository)
        if observationNames <> expectedRepositories || not (unique observationNames) then findings.Add GitHubFleetDryPlanFinding.InvalidFleetRoster
        if targetNames <> expectedRepositories || not (unique targetNames) then findings.Add GitHubFleetDryPlanFinding.InvalidFleetRoster

        for (observation: GitHubFleetRepositoryObservation) in observations do
            if not (validText observation.DefaultBranch) then findings.Add(GitHubFleetDryPlanFinding.InvalidFleetField $"{observation.Repository}:defaultBranch")
            if observation.ObservedAt = DateTimeOffset.MinValue || observation.ObservedAt > compiledAt || compiledAt - observation.ObservedAt > maxObservationAge then findings.Add(GitHubFleetDryPlanFinding.InvalidFleetField $"{observation.Repository}:observedAt")
            let endpointNames = observation.Endpoints |> List.map (fun (value: GitHubFleetEndpointObservation) -> value.Endpoint)
            if endpointNames <> expectedEndpoints || not (unique endpointNames) then
                findings.Add(GitHubFleetDryPlanFinding.IncompleteFleetObservation observation.Repository)
            for (endpoint: GitHubFleetEndpointObservation) in observation.Endpoints do
                if not (isSha 64 endpoint.PayloadSha256 && isSha 64 endpoint.RelevantFingerprint)
                   || endpoint.Permission <> expectedReadPermission endpoint.Endpoint then findings.Add(GitHubFleetDryPlanFinding.InvalidFleetField $"{observation.Repository}:{endpoint.Endpoint}")
                if endpoint.Pagination.Pages < 1 || endpoint.Pagination.ItemCount < 0
                   || not (validText endpoint.Pagination.Kind) then findings.Add(GitHubFleetDryPlanFinding.InvalidFleetPagination $"{observation.Repository}:{endpoint.Endpoint}")
                if endpoint.Pagination.Terminal = endpoint.Pagination.Next.IsSome then
                    findings.Add(GitHubFleetDryPlanFinding.InvalidFleetPagination $"{observation.Repository}:{endpoint.Endpoint}")
                if not (statusMatches endpoint) then findings.Add(GitHubFleetDryPlanFinding.InvalidFleetDisposition $"{observation.Repository}:{endpoint.Endpoint}")

        for (target: GitHubFleetRepositoryTarget) in targets do
            let names = target.Settings |> List.map (fun (value: GitHubFleetDesiredSetting) -> value.Setting)
            if target.ExternalOwner <> (target.Repository = expectedRepositories.Head)
               || names <> expectedEndpoints || not (unique names && ordered names) then findings.Add(GitHubFleetDryPlanFinding.InvalidFleetTarget target.Repository)
            for (setting: GitHubFleetDesiredSetting) in target.Settings do
                if not (List.contains setting.Setting expectedEndpoints) || not (isSha 64 setting.DesiredSha256)
                   || setting.RequiredPermission <> expectedWritePermission setting.Setting || not (validText setting.RollbackOrForwardRepair) then
                    findings.Add(GitHubFleetDryPlanFinding.InvalidFleetTarget $"{target.Repository}:{setting.Setting}")

        if findings.Count > 0 then Error(List.ofSeq findings)
        else
            let plans =
                List.map2 (fun (observation: GitHubFleetRepositoryObservation) (target: GitHubFleetRepositoryTarget) ->
                    let operations =
                        let actionable =
                            observation.Endpoints
                            |> List.forall (fun endpoint -> endpoint.Disposition = GitHubFleetDisposition.Supported || endpoint.Disposition = GitHubFleetDisposition.NoOp)
                        if target.ExternalOwner || not observation.Complete || not actionable then [] else
                        target.Settings
                        |> List.choose (fun (setting: GitHubFleetDesiredSetting) ->
                            let endpoint = observation.Endpoints |> List.find (fun (item: GitHubFleetEndpointObservation) -> item.Endpoint = setting.Setting)
                            if endpoint.Disposition <> GitHubFleetDisposition.Supported || endpoint.RelevantFingerprint = setting.DesiredSha256 then None
                            else
                                let action = "would-update"
                                let id = operationId target.Repository setting.Setting action endpoint.RelevantFingerprint setting.DesiredSha256 setting.RequiredPermission setting.RollbackOrForwardRepair
                                Some { Id = id; Repository = target.Repository; Setting = setting.Setting; Action = action
                                       PreStateSha256 = endpoint.RelevantFingerprint; DesiredSha256 = setting.DesiredSha256
                                       RequiredPermission = setting.RequiredPermission
                                       RollbackOrForwardRepair = setting.RollbackOrForwardRepair })
                        |> List.sortBy (fun (value: GitHubFleetOperation) -> value.Repository, value.Setting, value.Action)
                    { Repository = observation.Repository; DefaultBranch = observation.DefaultBranch; ObservationComplete = observation.Complete
                      ObservedAt = observation.ObservedAt; PreStateSha256 = observationFingerprint observation
                      DesiredStateSha256 = desiredFingerprint target
                      Disposition = repositoryDisposition target.ExternalOwner observation operations
                      Settings = observation.Endpoints; DesiredSettings = target.Settings
                      Operations = operations; PreservesUnrelatedSettings = true }) observations targets
            let seal = planSeal roadmapRevision roadmapSha256 unitContractSha256 sourceRevision compiledAt maxObservationAge author receiptDigests roster plans
            Ok { SchemaVersion = 1; RoadmapRevision = roadmapRevision; RoadmapSha256 = roadmapSha256
                 UnitContractSha256 = unitContractSha256; SourceRevision = sourceRevision; CompiledAt = compiledAt
                 MaxObservationAge = maxObservationAge; Author = author
                 ReceiptDigests = receiptDigests; Roster = roster; Plans = plans; Seal = seal }

    let private writePlan (writer: Utf8JsonWriter) (plan: GitHubFleetRepositoryPlan) =
        writer.WriteStartObject()
        writer.WriteString("repository", plan.Repository); writer.WriteString("defaultBranch", plan.DefaultBranch)
        writer.WriteString("observedAt", plan.ObservedAt.ToString("O", CultureInfo.InvariantCulture))
        writer.WriteBoolean("observationComplete", plan.ObservationComplete)
        writer.WriteString("preStateSha256", plan.PreStateSha256); writer.WriteString("desiredStateSha256", plan.DesiredStateSha256)
        writer.WriteString("disposition", dispositionId plan.Disposition)
        writer.WriteBoolean("preservesUnrelatedSettings", plan.PreservesUnrelatedSettings)
        writer.WriteStartArray("settings")
        for setting in plan.Settings do
            writer.WriteStartObject(); writer.WriteString("endpoint", setting.Endpoint); writer.WriteNumber("statusCode", setting.StatusCode)
            writer.WriteString("permission", setting.Permission); writer.WriteString("paginationKind", setting.Pagination.Kind)
            writer.WriteNumber("pages", setting.Pagination.Pages); writer.WriteNumber("itemCount", setting.Pagination.ItemCount)
            writer.WriteBoolean("terminal", setting.Pagination.Terminal)
            match setting.Pagination.Next with Some value -> writer.WriteString("next", value) | None -> writer.WriteNull("next")
            writer.WriteString("payloadSha256", setting.PayloadSha256); writer.WriteString("relevantFingerprint", setting.RelevantFingerprint)
            writer.WriteString("disposition", dispositionId setting.Disposition); writer.WriteEndObject()
        writer.WriteEndArray()
        writer.WriteStartArray("desiredSettings")
        for setting in plan.DesiredSettings do
            writer.WriteStartObject(); writer.WriteString("setting", setting.Setting); writer.WriteString("desiredSha256", setting.DesiredSha256)
            writer.WriteString("requiredPermission", setting.RequiredPermission); writer.WriteString("rollbackOrForwardRepair", setting.RollbackOrForwardRepair); writer.WriteEndObject()
        writer.WriteEndArray()
        writer.WriteStartArray("operations")
        for op in plan.Operations do
            writer.WriteStartObject(); writer.WriteString("id", op.Id); writer.WriteString("repository", op.Repository)
            writer.WriteString("setting", op.Setting); writer.WriteString("action", op.Action)
            writer.WriteString("preStateSha256", op.PreStateSha256); writer.WriteString("desiredSha256", op.DesiredSha256)
            writer.WriteString("requiredPermission", op.RequiredPermission)
            writer.WriteString("rollbackOrForwardRepair", op.RollbackOrForwardRepair); writer.WriteEndObject()
        writer.WriteEndArray(); writer.WriteEndObject()

    let serializeDraft (plan: GitHubFleetDryPlan) =
        use stream = new IO.MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))
        writer.WriteStartObject(); writer.WriteNumber("schemaVersion", plan.SchemaVersion)
        writer.WriteString("roadmapRevision", plan.RoadmapRevision); writer.WriteString("roadmapSha256", plan.RoadmapSha256)
        writer.WriteString("unitContractSha256", plan.UnitContractSha256); writer.WriteString("sourceRevision", plan.SourceRevision)
        writer.WriteString("compiledAt", plan.CompiledAt.ToString("O", CultureInfo.InvariantCulture)); writer.WriteNumber("maxObservationAgeTicks", plan.MaxObservationAge.Ticks)
        writer.WriteString("author", plan.Author)
        writer.WriteStartArray("receiptDigests"); plan.ReceiptDigests |> List.iter writer.WriteStringValue; writer.WriteEndArray()
        writer.WriteStartArray("roster"); plan.Roster |> List.iter writer.WriteStringValue; writer.WriteEndArray()
        writer.WriteStartArray("plans"); plan.Plans |> List.iter (writePlan writer); writer.WriteEndArray()
        writer.WriteString("seal", plan.Seal); writer.WriteEndObject(); writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    let private requiredString (name: string) (root: JsonElement) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String && validText (value.GetString()) -> value.GetString()
        | _ -> raise (FormatException $"{name} must be a non-empty string")
    let private requiredBool (name: string) (root: JsonElement) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False -> value.GetBoolean()
        | _ -> raise (FormatException $"{name} must be a boolean")
    let private strings (name: string) (root: JsonElement) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
        | _ -> raise (FormatException $"{name} must be an array")
    let private exactProperties (expected: string list) (root: JsonElement) =
        let actual = root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
        if actual <> Set.ofList expected then raise (FormatException "object shape is not canonical")

    let private parseDraft (raw: string) =
        try
            use document = JsonDocument.Parse(raw)
            let root = document.RootElement
            exactProperties [ "schemaVersion"; "roadmapRevision"; "roadmapSha256"; "unitContractSha256"; "sourceRevision"; "compiledAt"; "maxObservationAgeTicks"; "author"; "receiptDigests"; "roster"; "plans"; "seal" ] root
            let plans =
                root.GetProperty("plans").EnumerateArray()
                |> Seq.map (fun (value: JsonElement) ->
                    exactProperties [ "repository"; "defaultBranch"; "observedAt"; "observationComplete"; "preStateSha256"; "desiredStateSha256"; "disposition"; "preservesUnrelatedSettings"; "settings"; "desiredSettings"; "operations" ] value
                    let settings = value.GetProperty("settings").EnumerateArray() |> Seq.map (fun setting ->
                        exactProperties [ "endpoint"; "statusCode"; "permission"; "paginationKind"; "pages"; "itemCount"; "terminal"; "next"; "payloadSha256"; "relevantFingerprint"; "disposition" ] setting
                        let nextNode = setting.GetProperty("next")
                        { Endpoint = requiredString "endpoint" setting; StatusCode = setting.GetProperty("statusCode").GetInt32(); Permission = requiredString "permission" setting
                          Pagination = { Kind = requiredString "paginationKind" setting; Pages = setting.GetProperty("pages").GetInt32(); ItemCount = setting.GetProperty("itemCount").GetInt32(); Terminal = requiredBool "terminal" setting; Next = if nextNode.ValueKind = JsonValueKind.Null then None else Some(nextNode.GetString()) }
                          PayloadSha256 = requiredString "payloadSha256" setting; RelevantFingerprint = requiredString "relevantFingerprint" setting
                          Disposition = requiredString "disposition" setting |> dispositionOf |> Option.defaultWith (fun () -> raise (FormatException "unknown disposition")) }) |> Seq.toList
                    let desiredSettings = value.GetProperty("desiredSettings").EnumerateArray() |> Seq.map (fun setting ->
                        exactProperties [ "setting"; "desiredSha256"; "requiredPermission"; "rollbackOrForwardRepair" ] setting
                        { Setting = requiredString "setting" setting; DesiredSha256 = requiredString "desiredSha256" setting
                          RequiredPermission = requiredString "requiredPermission" setting; RollbackOrForwardRepair = requiredString "rollbackOrForwardRepair" setting }) |> Seq.toList
                    let operations =
                        value.GetProperty("operations").EnumerateArray()
                        |> Seq.map (fun (op: JsonElement) ->
                            exactProperties [ "id"; "repository"; "setting"; "action"; "preStateSha256"; "desiredSha256"; "requiredPermission"; "rollbackOrForwardRepair" ] op
                            { Id = requiredString "id" op; Repository = requiredString "repository" op
                              Setting = requiredString "setting" op; Action = requiredString "action" op
                              PreStateSha256 = requiredString "preStateSha256" op; DesiredSha256 = requiredString "desiredSha256" op
                              RequiredPermission = requiredString "requiredPermission" op
                              RollbackOrForwardRepair = requiredString "rollbackOrForwardRepair" op }) |> Seq.toList
                    let disposition = requiredString "disposition" value |> dispositionOf |> Option.defaultWith (fun () -> raise (FormatException "unknown disposition"))
                    { Repository = requiredString "repository" value; DefaultBranch = requiredString "defaultBranch" value; ObservationComplete = requiredBool "observationComplete" value
                      ObservedAt = DateTimeOffset.Parse(requiredString "observedAt" value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                      PreStateSha256 = requiredString "preStateSha256" value; DesiredStateSha256 = requiredString "desiredStateSha256" value
                      Disposition = disposition; Settings = settings; DesiredSettings = desiredSettings; Operations = operations
                      PreservesUnrelatedSettings = requiredBool "preservesUnrelatedSettings" value }) |> Seq.toList
            let result =
                { SchemaVersion = root.GetProperty("schemaVersion").GetInt32()
                  RoadmapRevision = requiredString "roadmapRevision" root; RoadmapSha256 = requiredString "roadmapSha256" root
                  UnitContractSha256 = requiredString "unitContractSha256" root; SourceRevision = requiredString "sourceRevision" root
                  CompiledAt = DateTimeOffset.Parse(requiredString "compiledAt" root, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                  MaxObservationAge = TimeSpan.FromTicks(root.GetProperty("maxObservationAgeTicks").GetInt64()); Author = requiredString "author" root
                  ReceiptDigests = strings "receiptDigests" root; Roster = strings "roster" root
                  Plans = plans; Seal = requiredString "seal" root }
            let expected = planSeal result.RoadmapRevision result.RoadmapSha256 result.UnitContractSha256 result.SourceRevision result.CompiledAt result.MaxObservationAge result.Author result.ReceiptDigests result.Roster result.Plans
            if result.SchemaVersion <> 1 || result.Seal <> expected || serializeDraft result <> raw then
                Error [ GitHubFleetDryPlanFinding.InvalidFleetSerialization "noncanonical or altered plan" ]
            else Ok result
        with error -> Error [ GitHubFleetDryPlanFinding.InvalidFleetSerialization error.Message ]

    let private validateDraft (plan: GitHubFleetDryPlan) =
        let observations = plan.Plans |> List.map (fun value -> { Repository = value.Repository; DefaultBranch = value.DefaultBranch; ObservedAt = value.ObservedAt; Complete = value.ObservationComplete; Endpoints = value.Settings })
        let targets = plan.Plans |> List.map (fun value -> { Repository = value.Repository; ExternalOwner = value.Disposition = GitHubFleetDisposition.ExternalObserveOnly; Settings = value.DesiredSettings })
        match compile plan.RoadmapRevision plan.RoadmapSha256 plan.UnitContractSha256 plan.SourceRevision plan.CompiledAt plan.MaxObservationAge plan.Author plan.ReceiptDigests plan.Roster observations targets with
        | Ok expected when expected = plan -> Ok plan
        | Ok _ -> Error [ GitHubFleetDryPlanFinding.InvalidFleetSerialization "semantic plan mismatch" ]
        | Error findings -> Error findings

    let review (author: string) (reviewer: string) (reviewedAt: DateTimeOffset) (plan: GitHubFleetDryPlan) =
        let planSha = hash (serializeDraft plan)
        let independent = validText author && validText reviewer && author <> reviewer
        let accepted = independent && validateDraft plan |> Result.isOk
        let draft = { Author = author; Reviewer = reviewer; ReviewedAt = reviewedAt; PlanSha256 = planSha; Independent = independent; Accepted = accepted; EvidenceSha256 = "" }
        { draft with EvidenceSha256 = reviewEvidence draft }

    let acceptReview (plan: GitHubFleetDryPlan) (review: GitHubFleetPlanReview) =
        if validateDraft plan |> Result.isError || review.Author <> plan.Author || review.Author = review.Reviewer
           || not review.Independent || not review.Accepted || review.ReviewedAt < plan.CompiledAt
           || review.PlanSha256 <> hash (serializeDraft plan) || review.EvidenceSha256 <> reviewEvidence review then
            Error [ GitHubFleetDryPlanFinding.InvalidFleetReview ]
        else Ok { Plan = plan; Review = review; Seal = reviewedSeal plan review }

    let serialize (reviewed: GitHubFleetReviewedPlan) =
        use stream = new IO.MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))
        writer.WriteStartObject(); writer.WriteString("plan", serializeDraft reviewed.Plan)
        writer.WriteStartObject("review"); writer.WriteString("author", reviewed.Review.Author); writer.WriteString("reviewer", reviewed.Review.Reviewer)
        writer.WriteString("reviewedAt", reviewed.Review.ReviewedAt.ToString("O", CultureInfo.InvariantCulture)); writer.WriteString("planSha256", reviewed.Review.PlanSha256)
        writer.WriteBoolean("independent", reviewed.Review.Independent); writer.WriteBoolean("accepted", reviewed.Review.Accepted); writer.WriteString("evidenceSha256", reviewed.Review.EvidenceSha256)
        writer.WriteEndObject(); writer.WriteString("seal", reviewed.Seal); writer.WriteEndObject(); writer.Flush(); Encoding.UTF8.GetString(stream.ToArray())

    let parse (raw: string) =
        try
            use document = JsonDocument.Parse(raw)
            let root = document.RootElement
            exactProperties [ "plan"; "review"; "seal" ] root
            let reviewNode = root.GetProperty("review")
            exactProperties [ "author"; "reviewer"; "reviewedAt"; "planSha256"; "independent"; "accepted"; "evidenceSha256" ] reviewNode
            let plan = parseDraft (requiredString "plan" root) |> Result.defaultWith (fun findings -> raise (FormatException(sprintf "%A" findings)))
            let review = { Author = requiredString "author" reviewNode; Reviewer = requiredString "reviewer" reviewNode
                           ReviewedAt = DateTimeOffset.Parse(requiredString "reviewedAt" reviewNode, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                           PlanSha256 = requiredString "planSha256" reviewNode; Independent = requiredBool "independent" reviewNode
                           Accepted = requiredBool "accepted" reviewNode; EvidenceSha256 = requiredString "evidenceSha256" reviewNode }
            let result = { Plan = plan; Review = review; Seal = requiredString "seal" root }
            match acceptReview plan review with
            | Ok expected when expected = result && serialize result = raw -> Ok result
            | _ -> Error [ GitHubFleetDryPlanFinding.InvalidFleetSerialization "noncanonical or invalid reviewed plan" ]
        with error -> Error [ GitHubFleetDryPlanFinding.InvalidFleetSerialization error.Message ]

    let reinspect (reviewed: GitHubFleetReviewedPlan) (observations: GitHubFleetReinspection list) =
        let findings = ResizeArray<GitHubFleetDryPlanFinding>()
        let plan = reviewed.Plan
        if acceptReview plan reviewed.Review <> Ok reviewed || reviewed.Seal <> reviewedSeal plan reviewed.Review then findings.Add GitHubFleetDryPlanFinding.InvalidFleetReview
        let names = observations |> List.map (fun (value: GitHubFleetReinspection) -> value.Repository)
        if names <> expectedRepositories || not (unique names) then findings.Add(GitHubFleetDryPlanFinding.InvalidFleetReinspection "roster")
        for (item: GitHubFleetReinspection) in observations do
            if not item.Complete || not item.Authoritative || item.ObservedAt = DateTimeOffset.MinValue
               || not (isSha 64 item.RelevantFingerprint) then findings.Add(GitHubFleetDryPlanFinding.InvalidFleetReinspection item.Repository)
        if findings.Count > 0 then Error(List.ofSeq findings)
        else
            let stale =
                List.map2 (fun (expected: GitHubFleetRepositoryPlan) (actual: GitHubFleetReinspection) ->
                    if expected.Repository <> actual.Repository || actual.ObservedAt <= expected.ObservedAt
                       || expected.PreStateSha256 <> actual.RelevantFingerprint then Some expected.Repository else None) plan.Plans observations
                |> List.choose id
            Ok(if List.isEmpty stale then Confirmed else PlanStale stale)

    let verify (expectedSeal: string) (reviewed: GitHubFleetReviewedPlan) =
        match parse (serialize reviewed) with
        | Ok value when value.Seal = expectedSeal -> Ok value
        | _ -> Error [ GitHubFleetDryPlanFinding.AlteredFleetSeal ]

    let validateControls (generated: GitHubFleetControlResult list) (independent: GitHubFleetControlResult list) =
        let findings = ResizeArray<GitHubFleetQualificationFinding>()
        let validate (side: string) (values: GitHubFleetControlResult list) =
            let actual = values |> List.map (fun value -> controlId value.Control)
            let expected = requiredControls |> List.map controlId
            if actual <> expected then findings.Add { Code = "Q5-INVENTORY"; ControlId = side; Message = $"{side} control inventory differs" }
            for value in values do
                if not value.BaselineGreen then findings.Add { Code = "Q5-BASELINE"; ControlId = controlId value.Control; Message = $"{side} baseline is not green" }
                if not value.ControlPassed then findings.Add { Code = "Q5-CONTROL"; ControlId = controlId value.Control; Message = $"{side} control did not turn red" }
        validate "generated" generated; validate "independent" independent
        if findings.Count = 0 then Ok () else Error(List.ofSeq findings)
