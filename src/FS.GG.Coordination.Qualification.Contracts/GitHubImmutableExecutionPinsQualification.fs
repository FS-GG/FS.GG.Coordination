namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text

type ImmutableExecutionReferenceKind = ThirdPartyAction | ReusableWorkflow

type ImmutableExecutionReference =
    { WorkflowPath: string
      TargetRepository: string
      TargetPath: string option
      Revision: string
      Kind: ImmutableExecutionReferenceKind }

type ImmutableWorkflowDocument =
    { Path: string
      Sha256: string
      References: ImmutableExecutionReference list }

type ImmutableWorkflowPublication =
    { Repository: string
      Path: string
      Revision: string
      ContentSha256: string
      WorkflowCall: bool }

type ImmutablePinUpdaterAuthority =
    { Name: string
      Automated: bool
      PullRequestOnly: bool
      DirectPush: bool
      PolicyRepository: string
      PolicyRevision: string
      PolicyPath: string
      PolicySha256: string
      OwnedManagers: string list }

type ImmutableExecutionPinsSnapshot =
    { SchemaVersion: int
      Repository: string
      SourceRevision: string
      PrerequisiteReceiptDigest: string
      Complete: bool
      Workflows: ImmutableWorkflowDocument list
      Publications: ImmutableWorkflowPublication list
      Updaters: ImmutablePinUpdaterAuthority list
      RequiredManagers: string list }

type ImmutableExecutionPinsReport =
    { Repository: string
      SourceRevision: string
      WorkflowCount: int
      ReferenceCount: int
      PublicationCount: int
      AutomatedUpdater: string
      Managers: string list
      Seal: string }

type ImmutableExecutionPinsError =
    | UnsupportedImmutablePinsSchema
    | InvalidImmutablePinsRepository
    | InvalidImmutablePinsSourceRevision
    | InvalidImmutablePinsPrerequisite
    | IncompleteImmutablePinsCorpus
    | DuplicateImmutableWorkflow
    | InvalidImmutableWorkflowDigest
    | CrossWorkflowReference
    | LocalExecutionReferenceNotImmutable
    | MutableExecutionReference
    | InvalidExecutionReference
    | DuplicateImmutablePublication
    | InvalidImmutablePublication
    | PublicationIsNotReusableWorkflow
    | MissingImmutablePublication
    | ConflictingImmutablePublication
    | InvalidUpdaterAuthority
    | MultipleAutomatedUpdaters
    | RenovateAuthorityMissing
    | RenovateOwnershipIncomplete
    | AlteredImmutableExecutionPinsSeal

type GitHubImmutableExecutionPinsControl =
    | ImmutablePinsPrerequisite | ImmutablePinsCompleteness | ImmutablePinsSourceBinding
    | ThirdPartyActionPins | ReusableWorkflowPins | LocalExecutionReferenceRejection | WorkflowDigestBinding
    | PublicationIdentity | PublicationContent | PublicationWorkflowCall
    | StablePinOrdering | RenovateSoleUpdater | RenovatePullRequestOnly
    | RenovateOwnership | ExactPinsSeal | ExactPinsReplay | QuintPinsUnchanged
    | NoPinsMutationSurface | NoWorkflowPublicationSurface

type GitHubImmutableExecutionPinsControlResult =
    { Control: GitHubImmutableExecutionPinsControl
      ControlPassed: bool
      BaselineGreen: bool }

type GitHubImmutableExecutionPinsFinding = { Code: string; ControlId: string; Message: string }

module GitHubImmutableExecutionPinsQualification =
    let private hexLength length (value: string) =
        not (String.IsNullOrWhiteSpace value) && value.Length = length && value |> Seq.forall Uri.IsHexDigit

    let private validRepository (value: string) =
        not (String.IsNullOrWhiteSpace value) && value.Split('/').Length = 2 && not (value.Contains "..")

    let private validPath (value: string) =
        not (String.IsNullOrWhiteSpace value) && not (value.StartsWith "/") && not (value.Contains "..")

    let private frame (value: string) = $"%d{Encoding.UTF8.GetByteCount value}:%s{value}"
    let private scalar = function Some value -> frame value | None -> frame ""
    let private strings values = values |> Seq.map frame |> String.concat ""
    let private kind = function ThirdPartyAction -> "action" | ReusableWorkflow -> "workflow"

    let classifyReferenceLiteral (literal: string) =
        if String.IsNullOrWhiteSpace literal then
            Error [ InvalidExecutionReference ]
        elif literal.StartsWith("./", StringComparison.Ordinal) then
            Error [ LocalExecutionReferenceNotImmutable ]
        else
            let split = literal.Split('@')
            if split.Length <> 2 || not (hexLength 40 split[1]) then
                Error [ MutableExecutionReference ]
            else
                let identity = split[0].Split('/')
                if identity.Length < 2 then
                    Error [ InvalidExecutionReference ]
                else
                    let repository = String.concat "/" identity[0..1]
                    let isWorkflow = identity.Length >= 5 && identity[2] = ".github" && identity[3] = "workflows"
                    let targetPath = if isWorkflow then Some(String.concat "/" identity[2..]) else None
                    Ok((if isWorkflow then ReusableWorkflow else ThirdPartyAction), repository, targetPath, split[1])

    let private canonical snapshot =
        let workflows = snapshot.Workflows |> List.sortBy _.Path
        let publications = snapshot.Publications |> List.sortBy (fun value -> value.Repository, value.Path, value.Revision)
        let updaters = snapshot.Updaters |> List.sortBy _.Name
        [ frame (string snapshot.SchemaVersion); frame snapshot.Repository; frame snapshot.SourceRevision
          frame snapshot.PrerequisiteReceiptDigest; frame (string snapshot.Complete)
          strings [ for workflow in workflows do
                        yield frame workflow.Path + frame workflow.Sha256
                        for reference in workflow.References |> List.sortBy (fun value -> kind value.Kind, value.TargetRepository, value.TargetPath, value.Revision) do
                            yield frame (kind reference.Kind) + frame reference.WorkflowPath + frame reference.TargetRepository + scalar reference.TargetPath + frame reference.Revision ]
          strings [ for publication in publications do
                        yield frame publication.Repository + frame publication.Path + frame publication.Revision + frame publication.ContentSha256 + frame (string publication.WorkflowCall) ]
          strings [ for updater in updaters do
                        yield frame updater.Name + frame (string updater.Automated) + frame (string updater.PullRequestOnly) + frame (string updater.DirectPush)
                              + frame updater.PolicyRepository + frame updater.PolicyRevision + frame updater.PolicyPath + frame updater.PolicySha256
                              + strings (updater.OwnedManagers |> List.sort) ]
          strings (snapshot.RequiredManagers |> List.sort) ]
        |> String.concat "|"

    let private seal snapshot =
        snapshot |> canonical |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let compile snapshot =
        let errors = ResizeArray<ImmutableExecutionPinsError>()
        if snapshot.SchemaVersion <> 1 then errors.Add UnsupportedImmutablePinsSchema
        if not (validRepository snapshot.Repository) then errors.Add InvalidImmutablePinsRepository
        if not (hexLength 40 snapshot.SourceRevision) then errors.Add InvalidImmutablePinsSourceRevision
        if not (hexLength 64 snapshot.PrerequisiteReceiptDigest) then errors.Add InvalidImmutablePinsPrerequisite
        if not snapshot.Complete || snapshot.Workflows.IsEmpty then errors.Add IncompleteImmutablePinsCorpus
        if snapshot.Workflows |> List.countBy _.Path |> List.exists (fun (_, count) -> count <> 1) then errors.Add DuplicateImmutableWorkflow
        for workflow in snapshot.Workflows do
            if not (validPath workflow.Path) || not (hexLength 64 workflow.Sha256) then errors.Add InvalidImmutableWorkflowDigest
            for reference in workflow.References do
                if reference.WorkflowPath <> workflow.Path then errors.Add CrossWorkflowReference
                if not (validRepository reference.TargetRepository) || not (hexLength 40 reference.Revision) then errors.Add MutableExecutionReference
                match reference.Kind, reference.TargetPath with
                | ThirdPartyAction, None -> ()
                | ReusableWorkflow, Some path when validPath path -> ()
                | _ -> errors.Add InvalidExecutionReference
        if snapshot.Publications |> List.countBy (fun value -> value.Repository, value.Path, value.Revision) |> List.exists (fun (_, count) -> count <> 1) then errors.Add DuplicateImmutablePublication
        for publication in snapshot.Publications do
            if not (validRepository publication.Repository) || not (validPath publication.Path) || not (hexLength 40 publication.Revision) || not (hexLength 64 publication.ContentSha256) then
                errors.Add InvalidImmutablePublication
            if not publication.WorkflowCall then errors.Add PublicationIsNotReusableWorkflow
        for reference in snapshot.Workflows |> List.collect _.References |> List.filter (fun value -> value.Kind = ReusableWorkflow) do
            let matches = snapshot.Publications |> List.filter (fun value -> value.Repository = reference.TargetRepository && Some value.Path = reference.TargetPath && value.Revision = reference.Revision)
            if matches.IsEmpty then errors.Add MissingImmutablePublication
            elif matches.Length <> 1 then errors.Add ConflictingImmutablePublication
        let automated = snapshot.Updaters |> List.filter _.Automated
        if automated.Length > 1 then errors.Add MultipleAutomatedUpdaters
        if automated.Length <> 1 || automated.Head.Name <> "renovate" then errors.Add RenovateAuthorityMissing
        for updater in snapshot.Updaters do
            if String.IsNullOrWhiteSpace updater.Name
               || not (validRepository updater.PolicyRepository)
               || not (hexLength 40 updater.PolicyRevision)
               || not (validPath updater.PolicyPath)
               || not (hexLength 64 updater.PolicySha256)
               || (updater.Automated && (not updater.PullRequestOnly || updater.DirectPush)) then errors.Add InvalidUpdaterAuthority
        let requiredManagers = snapshot.RequiredManagers |> Set.ofList
        if requiredManagers.IsEmpty || snapshot.RequiredManagers.Length <> requiredManagers.Count then errors.Add RenovateOwnershipIncomplete
        if automated.Length = 1 && (automated.Head.OwnedManagers |> Set.ofList) <> requiredManagers then errors.Add RenovateOwnershipIncomplete
        if errors.Count > 0 then Error(List.ofSeq errors |> List.distinct)
        else
            let references = snapshot.Workflows |> List.sumBy (fun value -> value.References.Length)
            Ok { Repository = snapshot.Repository; SourceRevision = snapshot.SourceRevision
                 WorkflowCount = snapshot.Workflows.Length; ReferenceCount = references
                 PublicationCount = snapshot.Publications.Length; AutomatedUpdater = automated.Head.Name
                 Managers = snapshot.RequiredManagers |> List.sort; Seal = seal snapshot }

    let verify expectedSeal snapshot =
        match compile snapshot with
        | Ok report when report.Seal = expectedSeal -> Ok report
        | Ok _ -> Error [ AlteredImmutableExecutionPinsSeal ]
        | Error errors -> Error errors

    let requiredControls =
        [ ImmutablePinsPrerequisite; ImmutablePinsCompleteness; ImmutablePinsSourceBinding
          ThirdPartyActionPins; ReusableWorkflowPins; LocalExecutionReferenceRejection; WorkflowDigestBinding
          PublicationIdentity; PublicationContent; PublicationWorkflowCall
          StablePinOrdering; RenovateSoleUpdater; RenovatePullRequestOnly
          RenovateOwnership; ExactPinsSeal; ExactPinsReplay; QuintPinsUnchanged
          NoPinsMutationSurface; NoWorkflowPublicationSurface ]

    let controlId = function
        | ImmutablePinsPrerequisite -> "prerequisite-receipt"
        | ImmutablePinsCompleteness -> "corpus-completeness"
        | ImmutablePinsSourceBinding -> "source-binding"
        | ThirdPartyActionPins -> "third-party-action-pins"
        | ReusableWorkflowPins -> "reusable-workflow-pins"
        | LocalExecutionReferenceRejection -> "local-execution-reference-rejection"
        | WorkflowDigestBinding -> "workflow-digest-binding"
        | PublicationIdentity -> "publication-identity"
        | PublicationContent -> "publication-content"
        | PublicationWorkflowCall -> "publication-workflow-call"
        | StablePinOrdering -> "stable-ordering"
        | RenovateSoleUpdater -> "renovate-sole-updater"
        | RenovatePullRequestOnly -> "renovate-pull-request-only"
        | RenovateOwnership -> "renovate-ownership"
        | ExactPinsSeal -> "exact-seal"
        | ExactPinsReplay -> "exact-replay"
        | QuintPinsUnchanged -> "quint-unchanged"
        | NoPinsMutationSurface -> "no-mutation-surface"
        | NoWorkflowPublicationSurface -> "no-publication-surface"

    let validate generated independent =
        let expected = requiredControls |> List.map controlId |> Set.ofList
        let findingsFor source values =
            let grouped = values |> List.groupBy (fun value -> controlId value.Control)
            [ for missing in Set.difference expected (grouped |> List.map fst |> Set.ofList) do
                  { Code = "IEP-CONTROL-MISSING"; ControlId = missing; Message = $"%s{source} omitted the required control" }
              for control, results in grouped do
                  if results.Length <> 1 then
                      { Code = "IEP-CONTROL-DUPLICATE"; ControlId = control; Message = $"%s{source} supplied the control more than once" }
                  else
                      let result = results.Head
                      if not result.BaselineGreen then
                          { Code = "IEP-BASELINE-RED"; ControlId = control; Message = $"%s{source} baseline is not green" }
                      if not result.ControlPassed then
                          { Code = "IEP-CONTROL-FAILED"; ControlId = control; Message = $"%s{source} control did not pass" } ]
        let findings = findingsFor "generated" generated @ findingsFor "independent" independent
        if findings.IsEmpty then Ok() else Error findings
