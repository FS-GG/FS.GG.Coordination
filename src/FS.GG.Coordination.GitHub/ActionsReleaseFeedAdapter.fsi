namespace FS.GG.Coordination.GitHub

type ArtifactSurface =
    | ActionsRuns | Checks | MergeGroups | Releases | Attestations | Packages | Feeds | ServedDownloads

type LifecycleOutcome =
    | Requested | Queued | InProgress | Completed | Skipped | Cancelled | Stale | Neutral | TimedOut | ActionRequired

type ArtifactState = Present | Immutable | Deleted | Tampered | Expired

type ArtifactEvidence =
    { Surface: ArtifactSurface
      Identity: string
      Repository: string
      Subject: string
      Attempt: int option
      Lifecycle: LifecycleOutcome option
      State: ArtifactState
      Attributes: Map<string, string>
      Digest: string option }

type ArtifactSurfaceObservation =
    | Supported of Revision: string * Complete: bool * Pages: int * Evidence: ArtifactEvidence list
    | Unauthorized of reason: string
    | Unavailable of reason: string
    | Incomplete of reason: string
    | ExpiredSurface of reason: string
    | DeletedSurface of reason: string
    | Unreadable of reason: string
    | StaleSurface of expectedRevision: string * actualRevision: string

type ActionsReleaseFeedObservation =
    { Repository: string
      RepositoryNodeId: string
      CapturedRevision: string
      Surfaces: Map<ArtifactSurface, ArtifactSurfaceObservation>
      Fingerprint: string }

type RetrievalClass = AuthenticatedPackage | AuthenticatedFeed | AnonymousPublic

type ServedContent =
    { RequestUri: string
      Redirects: string list
      FinalUri: string
      Status: int
      ContentType: string
      Length: int64
      Retrieval: RetrievalClass
      Sha256: string }

type EvidenceStage =
    | UploadAccepted of requestId: string
    | DurableMetadata of identity: string * digest: string
    | AuthenticatedRetrieval of ServedContent
    | PublicServedBytes of ServedContent

type ArtifactFailure =
    | InvalidRepository
    | MissingSurface of ArtifactSurface
    | PartialSurface of ArtifactSurface * string
    | DuplicateIdentity of ArtifactSurface * string
    | IdentityDrift of ArtifactSurface * string
    | InvalidLifecycle of ArtifactSurface * string
    | InvalidDigest of ArtifactSurface * string
    | InvalidAttestation of string
    | InvalidPackageCoordinates of string
    | InvalidRedirect of string
    | SecretMaterialForbidden
    | InvalidFingerprint

[<RequireQualifiedAccess>]
module ActionsReleaseFeedAdapter =
    val surfaces: ArtifactSurface list
    val surfaceId: ArtifactSurface -> string
    val lifecycleId: LifecycleOutcome -> string
    val sha256: byte array -> string
    val fingerprint: repository: string -> repositoryNodeId: string -> capturedRevision: string -> Map<ArtifactSurface, ArtifactSurfaceObservation> -> string
    val validate: ActionsReleaseFeedObservation -> Result<ActionsReleaseFeedObservation, ArtifactFailure>
    val observeServedContent: requestUri: string -> redirects: string list -> finalUri: string -> status: int -> contentType: string -> retrieval: RetrievalClass -> bytes: byte array -> Result<ServedContent, ArtifactFailure>
    val validateStages: EvidenceStage list -> Result<EvidenceStage list, ArtifactFailure>
