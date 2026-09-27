namespace FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
type MigrationProjectStatusContentKind =
    | Issue
    | PullRequest

/// Copy-specific identities and immutable prestate used by one Status effect.
/// DesiredOptionId is the ProjectV2 single-select option id, never the id of a
/// ProjectV2ItemFieldSingleSelectValue object.
type MigrationProjectStatusBinding =
    { ProjectNodeId: string
      RepositoryId: int64
      ItemNodeId: string
      ContentNodeId: string
      ContentKind: MigrationProjectStatusContentKind
      FieldNodeId: string
      ExpectedItemRevision: string
      FieldPayloadSha256: string
      StatusOptions: MigrationProjectFieldOption list
      DesiredOptionId: string }

[<RequireQualifiedAccess>]
module MigrationProjectStatusStepRuntime =
    /// Revisioned digest of the bound target with an explicit absent-or-selected
    /// discriminator, so no provider option id can alias an absent prestate.
    val targetSha256: binding:MigrationProjectStatusBinding -> selectedOptionId:string option -> string

    /// Bind one sealed SetProjectField step to two matching full reconciled
    /// Project snapshots, durable journal authority and updateProjectV2ItemFieldValue.
    val create:
        step:MigrationExecutionStep ->
        binding:MigrationProjectStatusBinding ->
        readOptions:MigrationProjectReadOptions ->
        providerOptions:MigrationStepProviderOptions ->
        authority:MigrationStepAuthorityPort ->
        transport:IMigrationStepProviderTransport ->
            Result<IMigrationStepRuntime, string>
