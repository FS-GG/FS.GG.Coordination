namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
module MigrationReviewDeliveryInspectBinder =
    val bind:
        cohort:GitHubMigrationCopyCohort ->
        repository:MigrationGitHubReadOptions ->
        native:MigrationReviewDeliveryNativeTwoPass ->
        journals:MigrationJournalTwoPass ->
            Result<MigrationReviewDeliveryCompleteTwoPass, string>

    val authority:
        cohort:GitHubMigrationCopyCohort ->
        passOrdinal:int ->
        capture:MigrationReviewDeliveryCompleteTwoPass ->
            Result<GitHubMigrationInspectAuthority, string>
