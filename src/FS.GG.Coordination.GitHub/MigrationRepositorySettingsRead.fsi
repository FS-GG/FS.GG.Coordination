namespace FS.GG.Coordination.GitHub

/// One retained provider response in a terminal page chain.
type MigrationRepositorySettingsPageEvidence =
    { SettingsStream: string
      SettingsRequestedUri: string
      SettingsPayloadJson: string
      SettingsPayloadSha256: string
      SettingsNextUri: string option }

/// A complete provider-owned reading of exactly one canonical settings surface.
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

/// Provider boundary used to read each canonical surface independently.
type IMigrationRepositorySettingsSurfaceProvider =
    abstract Read:
        identity:RepositoryIdentity * repositoryRevision:string * surface:SettingsSurface ->
            Result<MigrationRepositorySettingsSurfaceRead, MigrationRepositorySettingsSurfaceRefusal>

/// Two independently equal, raw-bound reads of all eleven canonical surfaces.
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
    /// Reads the exact eleven-surface roster twice and refuses any unsupported,
    /// conditional, partial, unstable, unbound, or unterminated surface.
    val captureTwoPass:
        identity:RepositoryIdentity ->
        repositoryRevision:string ->
        provider:IMigrationRepositorySettingsSurfaceProvider ->
            Result<MigrationRepositorySettingsCapture, MigrationRepositorySettingsReadFailure>

    /// Revalidates retained raw hashes, request chains, identities, typed settings,
    /// two-pass equality, and the capture fingerprint without provider calls.
    val validateCapture:
        captured:MigrationRepositorySettingsCapture ->
            Result<MigrationRepositorySettingsCapture, MigrationRepositorySettingsReadFailure>

    /// Produces the existing canonical observation only after complete validation
    /// of all eleven surfaces. Partial readers cannot use this path successfully.
    val composeComplete:
        captured:MigrationRepositorySettingsCapture ->
            Result<RepositorySettingsObservation, MigrationRepositorySettingsReadFailure>
