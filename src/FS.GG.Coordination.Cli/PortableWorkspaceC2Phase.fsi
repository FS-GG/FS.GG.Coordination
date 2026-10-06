namespace FS.GG.Coordination.Cli

/// Closed C2 byte contracts and supplied-declaration consistency only.
/// No member enrolls a profile, checks a physical input, or authorizes execution.
module internal PortableWorkspaceC2Phase =
    /// At most 32 diagnostics; bounded JSON field path and stable reason token.
    type Diagnostic = { Field: string; Code: string }

    type Request
    type ResultDocument
    type BoundDeclaration

    /// Immutable description of caller-declared identity, never captured authority.
    type RequestIdentity =
        { EnrollmentId: string
          ProfileId: string
          ProfileSha256: string
          OperationId: string
          IdempotencyId: string
          RequestSha256: string }

    /// Supplied expected bytes are declarations, not filesystem observations.
    type InputDeclaration =
        { Id: string
          Role: string
          HostPath: string
          RelativePath: string
          Bytes: int64
          Sha256: string }

    type RolePath = { Role: string; HostPath: string; ContainerPath: string }
    type Translation =
        { ArgumentIndex: int; Role: string; Prefix: string; RelativePath: string }
    type StepDeclaration =
        { StepId: string
          Executable: string
          DeclaredArguments: string list
          ContainerArguments: string list
          WorkingRole: string
          Translations: Translation list
          Environment: Map<string, string> }

    /// The original phase window is supplied once, including caller setup.
    type PhaseWindow =
        { PhaseStartedUtc: string
          NotAfterUtc: string
          MaximumPhaseMs: int64
          MaximumWorkMs: int64
          CleanupReserveMs: int64
          CallerFinishReserveMs: int64 }

    /// Validated caller input/window declarations, never physical observations.
    type RequestReadDeclaration =
        { ConsumerRepository: string
          ConsumerSource: string
          Inputs: InputDeclaration list
          InputInventorySha256: string
          OwnedRoots: (string * string) list
          Window: PhaseWindow }

    /// Producer-local fixed selection data. Construction confers no authority.
    /// Only the two C2 fixture profiles and their fixed ordered steps are supported.
    type DeclaredSelection =
        { EnrollmentId: string
          ProfileId: string
          ProfileSha256: string
          ConsumerRepository: string
          ConsumerSource: string
          Inputs: InputDeclaration list
          RolePaths: RolePath list
          Steps: StepDeclaration list
          Window: PhaseWindow
          MaximumCapturedBytes: int64
          MaximumHandoffBytes: int64
          MaximumHandoffEntries: int }

    /// Strict UTF-8, closed shapes, bounded integers/arrays and canonical bytes.
    /// Refuse whole requests above 1 MiB and whole results above 2 MiB before
    /// parsing; permitted per-field limits never enlarge those document caps.
    /// Result streams require canonical padded Base64 without whitespace.
    /// Compact ordinal-key JSON uses literal Unicode and exactly one terminal LF.
    val decodeRequest: bytes: byte array -> Result<Request, Diagnostic list>
    val encodeRequest: request: Request -> byte array
    val requestSha256: request: Request -> string
    /// Six identity fields copied from validated bytes; does no read, clock or trust check.
    val describeRequest: request: Request -> RequestIdentity
    /// Immutable declaration copy for producer observation; never roots/mount authority.
    val describeReads: request: Request -> RequestReadDeclaration
    val decodeResult: bytes: byte array -> Result<ResultDocument, Diagnostic list>
    val encodeResult: document: ResultDocument -> byte array

    /// Checks fixed profile/order, exact inputs/argv/env, explicit role translation,
    /// and the supplied original window. Does no clock read or native admission.
    val bindDeclared:
        selection: DeclaredSelection -> request: Request -> Result<BoundDeclaration, Diagnostic list>

    /// Checks request digest/profile/operation, ordered step prefix, stream/handoff
    /// bounds and completed-result cutoff. Consistency does not attest custody.
    /// Honest late or unknown observations remain representable without permission.
    val validateResultJoin:
        request: Request -> document: ResultDocument -> Result<unit, Diagnostic list>
