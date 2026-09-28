namespace FS.GG.Coordination.Cli

type MigrationReceiverCopyRefUpdate =
    { RefName: string
      ExpectedOldCommit: string option
      NewCommit: string option }

type MigrationReceiverCopyGitReadback =
    { TargetRepository: string
      Refs: Map<string, string>
      Objects: MigrationReceiverCopyTargetObject list
      UnrelatedRefsFingerprint: string }

and MigrationReceiverCopyTargetObject =
    { RefName: string
      CommitOid: string
      TreeOid: string
      ParentOids: string list
      AuthorIdentity: string
      CommitterIdentity: string
      SignatureStatus: string
      RequestIdentitySha256: string }

type IMigrationReceiverCopyGitTransport =
    abstract SupportsAtomic: bool
    abstract Read: targetRepository:string * refPrefix:string -> Result<MigrationReceiverCopyGitReadback, string>
    abstract PushAtomic: sourceRepository:string * targetRepository:string * updates:MigrationReceiverCopyRefUpdate list -> Result<unit, string>
    abstract VerifyFresh: targetRepository:string * verificationRoot:string * manifest:MigrationReceiverCopyTransferManifest -> Result<MigrationReceiverCopyGitReadback, string>

[<RequireQualifiedAccess>]
module MigrationReceiverCopyGitTransport =
    /// Test/lab transport over a caller-created local bare repository.
    val localBare: targetRepositoryPath:string -> Result<IMigrationReceiverCopyGitTransport, string>

    /// Production-shaped transport remains inert until a protected host supplies
    /// scoped authority and secret handling outside this source-only window.
    val inertHttps: unit -> IMigrationReceiverCopyGitTransport

    val internal localBareWithAtomicSupportForTests:
        targetRepositoryPath:string -> supportsAtomic:bool -> Result<IMigrationReceiverCopyGitTransport, string>
