namespace FS.GG.Coordination.GitHub

open FS.GG.Coordination.Qualification.Contracts

type MigrationInspectProviderOptions =
    { Cohort: GitHubMigrationCopyCohort
      Repository: MigrationGitHubReadOptions
      Project: MigrationProjectReadOptions }

/// Only issue census and Project item membership have concrete raw-to-typed adapters.
/// Every other GS2-09.1 authority returns an explicit refusal.
type MigrationInspectProviderAdapter =
    new: options:MigrationInspectProviderOptions * transport:IMigrationGitHubReadTransport -> MigrationInspectProviderAdapter
    interface IGitHubMigrationInspectSource

[<RequireQualifiedAccess>]
module MigrationInspectProviderAdapter =
    /// Refuses requests outside one declared read-only authority before dispatching to inner.
    val guardReadTransport:
        options:MigrationInspectProviderOptions -> authority:string ->
        inner:IMigrationGitHubReadTransport -> IMigrationGitHubReadTransport

    /// Exposed for independent adapter controls; captured requests must be from one read-only provider call.
    val bindIssues:
        options:MigrationInspectProviderOptions ->
        population:MigrationIssuePopulation ->
        captures:(GitHubRequest * TransportOutcome) list ->
            Result<GitHubMigrationInspectAuthority, string>

    /// Exposed for independent adapter controls; captured requests must be from one read-only provider call.
    val bindProjectItems:
        options:MigrationInspectProviderOptions ->
        population:MigrationProjectItemPopulation ->
        captures:(GitHubRequest * TransportOutcome) list ->
            Result<GitHubMigrationInspectAuthority, string>

    val bindProjectFields:
        options:MigrationInspectProviderOptions ->
        population:MigrationProjectFieldPopulation ->
        captures:(GitHubRequest * TransportOutcome) list ->
            Result<GitHubMigrationInspectAuthority, string>

    val bindProjectValues:
        options:MigrationInspectProviderOptions ->
        population:MigrationProjectValuePopulation ->
        captures:(GitHubRequest * TransportOutcome) list ->
            Result<GitHubMigrationInspectAuthority, string>
