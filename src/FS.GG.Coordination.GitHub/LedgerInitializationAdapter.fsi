namespace FS.GG.Coordination.GitHub

open System

type ExpectedLedgerRef = ExpectedAbsent | ExpectedParent of string
type LedgerObject = { Kind: string; Oid: string; Bytes: byte array }
type LedgerInitializationAuthority =
    { KeyId: string; PublicKeyPem: string; PublicKeySpkiSha256: string
      Payload: byte array; Signature: byte array; AuthorizedAt: DateTimeOffset; ExpiresAt: DateTimeOffset }
type LedgerInitializationInput =
    { RepositoryId: int64; Repository: string; FleetId: string; Ref: string; Tag: string
      ManifestSha256: string; TrustAnchorSha256: string; SourceSha256: string
      DesiredPolicySha256: string; FirstCaptureSha256: string; SecondCaptureSha256: string
      AuthorizationKeyId: string; AuthorizationKeySpkiSha256: string
      AuthorizationWorkflowRevision: string; AuthorizationWorkflowSha256: string
      CutoverAppId: int64; CutoverInstallationId: int64; ControlIssueNumber: int64
      ExpectedRef: ExpectedLedgerRef; CreatedAt: DateTimeOffset; AuthorName: string; AuthorEmail: string }
type LedgerInitializationPlan =
    { InputSha256: string; AuthorizationSha256: string; Event: LedgerObject; Head: LedgerObject
      Tree: LedgerObject; Commit: LedgerObject; Ref: string; Tag: string; ExpectedRef: ExpectedLedgerRef
      OperationOrder: string list; Seal: string }
type LedgerRefRead = RefAbsent | RefAt of string | RefUnknown of string
type LedgerInitializationPort =
    { ReadRef: string -> LedgerRefRead; PutObject: LedgerObject -> Result<unit,string>
      CreateRef: string -> string -> ExpectedLedgerRef -> Result<unit,string> }
type LedgerInitializationOutcome = Initialized | AlreadyInitialized | InitializationRefused of string list | InitializationIndeterminate of string
type LedgerInitializationReadback =
    { Ref: LedgerRefRead; Tag: LedgerRefRead; Objects: LedgerObject list }

[<RequireQualifiedAccess>]
module LedgerInitializationAdapter =
    val canonicalInput: LedgerInitializationInput -> byte array
    val plan: asOf: DateTimeOffset -> LedgerInitializationAuthority -> LedgerInitializationInput -> Result<LedgerInitializationPlan,string list>
    val apply: LedgerInitializationPort -> LedgerInitializationPlan -> LedgerInitializationOutcome
    val verify: LedgerInitializationPlan -> LedgerInitializationReadback -> Result<unit,string list>
    val observationEnvelope: LedgerInitializationPlan -> byte array
