namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub

type MigrationClaimJournalNamespace =
    | ClaimJournalNamespace
    | OperationJournalNamespace

type MigrationClaimJournalSchemaFamily =
    | ClaimSchemaFamily
    | AdmissionSchemaFamily
    | OrdinarySchemaFamily
    | ReviewSchemaFamily

type MigrationClaimNativeSubjectKind =
    | NativeIssue
    | NativePullRequest

type MigrationClaimNativeStreamKind =
    | NativeIssueComments
    | NativeIssueEvents
    | NativeIssueTimeline

type MigrationClaimJournalRef =
    { ClaimRefName: string
      ClaimHeadSha: string }

type MigrationClaimJournalDecodedRecord =
    { Namespace: MigrationClaimJournalNamespace
      Schema: string
      Family: MigrationClaimJournalSchemaFamily
      CanonicalId: string
      OperationId: string option
      Generation: int64 }

type MigrationClaimJournalHistoryEntry =
    { ClaimCommitSha: string
      ClaimParentSha: string option
      ClaimTreeSha: string
      ClaimReads: MigrationReviewDeliveryRead list
      ClaimRecord: MigrationClaimJournalDecodedRecord }

type MigrationClaimJournalHistory =
    { ClaimHistoryRefName: string
      ClaimHistoryHeadSha: string
      ClaimEntries: MigrationClaimJournalHistoryEntry list }

type MigrationClaimJournalNamespaceCensus =
    { ClaimNamespace: MigrationClaimJournalNamespace
      ClaimPrefix: string
      ClaimNamespaceReads: MigrationReviewDeliveryRead list
      ClaimRefs: MigrationClaimJournalRef list }

type MigrationClaimJournalPass =
    { ClaimRepository: MigrationReviewDeliveryRepository
      ClaimNamespaces: MigrationClaimJournalNamespaceCensus list
      ClaimHistories: MigrationClaimJournalHistory list
      ClaimFingerprint: string }

type MigrationClaimJournalTwoPass =
    { ClaimFirst: MigrationClaimJournalPass
      ClaimSecond: MigrationClaimJournalPass }

/// A source roster derived from exact producer bytes. A caller-authored list or completeness bit
/// cannot qualify legacy receipts without these producer reads and their independent binder.
type MigrationLegacyReceiptSource =
    { ProducerId: string
      ProducerRevision: string
      SourceIdentity: string
      SchemaFamily: string }

type MigrationLegacyReceiptInventory =
    { ProducerReads: MigrationReviewDeliveryRead list
      Sources: MigrationLegacyReceiptSource list
      Fingerprint: string }

[<RequireQualifiedAccess>]
module MigrationClaimEventCaptureContract =
    val claimRefPrefix: string
    val operationRefPrefix: string
    val journalRefPrefixes: string list
    val namespacePrefix: MigrationClaimJournalNamespace -> string

    /// Comments, issue events and timeline are mandatory for every independently censused issue and pull request.
    val requiredNativeStreams: MigrationClaimNativeSubjectKind -> MigrationClaimNativeStreamKind list

    /// Claim refs accept claim schemas; operation refs accept admission, ordinary and review schemas.
    /// A decoder must refuse every schema that it cannot map to one of these known families.
    val schemaFamilyAllowed:
        namespace':MigrationClaimJournalNamespace ->
        family:MigrationClaimJournalSchemaFamily -> bool

    val passFingerprint: MigrationClaimJournalPass -> string
    val validateTwoPass: MigrationClaimJournalTwoPass -> Result<MigrationClaimJournalTwoPass, string>
