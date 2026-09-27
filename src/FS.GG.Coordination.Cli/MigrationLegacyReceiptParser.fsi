namespace FS.GG.Coordination.Cli

type MigrationLegacyReceiptLocation =
    | PullRequestComment
    | WorkItemComment

type MigrationLegacyReceiptParserSourceKind =
    | ReceiptProtectedWriterAndParser
    | ReceiptProtectedParserOnly

type MigrationLegacyReceiptParserSource =
    {
        Family: string
        Location: MigrationLegacyReceiptLocation
        Marker: string
        SourceKind: MigrationLegacyReceiptParserSourceKind
        SourceRepository: string
        SourceRevision: string
        ParserSourcePath: string
        ParserSourceSha256: string
        WriterSourcePath: string option
        WriterSourceSha256: string option
    }

type MigrationLegacyReceipt =
    | DeliveryReceipt of obligationId: string * head: string * evidence: string
    | DeliveryCompletion of item: string * pullRequest: int * mergeSha: string * digest: string
    | CompletionCorrection of item: string * destination: string * observedAt: System.DateTimeOffset * digest: string
    | LegacyDoneReceipt

type MigrationLegacyReceiptRoster =
    {
        Sources: MigrationLegacyReceiptParserSource list
        RosterComplete: bool
        MissingProtectedProducers: string list
        MissingReservedPrefixRegistry: bool
    }

[<RequireQualifiedAccess>]
module MigrationLegacyReceiptParser =
    /// Source-bound parser/receiver inventory from immutable protected `.github` bytes.
    /// It remains explicitly incomplete and cannot qualify `claim-and-event-streams`.
    val roster: MigrationLegacyReceiptRoster

    /// Parse a retained comment only at the location permitted by its protected source.
    /// Known marker prefixes with malformed, duplicate-member or digest-invalid payloads refuse.
    val tryParse:
        location: MigrationLegacyReceiptLocation -> body: string -> Result<MigrationLegacyReceipt option, string>
