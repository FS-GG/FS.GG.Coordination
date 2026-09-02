namespace FS.GG.Coordination.Qualification.Contracts

open System

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
    | ThirdPartyActionPins | ReusableWorkflowPins | WorkflowDigestBinding
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
    val compile: ImmutableExecutionPinsSnapshot -> Result<ImmutableExecutionPinsReport, ImmutableExecutionPinsError list>
    val verify: expectedSeal: string -> ImmutableExecutionPinsSnapshot -> Result<ImmutableExecutionPinsReport, ImmutableExecutionPinsError list>
    val requiredControls: GitHubImmutableExecutionPinsControl list
    val controlId: GitHubImmutableExecutionPinsControl -> string
    val validate: generated: GitHubImmutableExecutionPinsControlResult list -> independent: GitHubImmutableExecutionPinsControlResult list -> Result<unit, GitHubImmutableExecutionPinsFinding list>
