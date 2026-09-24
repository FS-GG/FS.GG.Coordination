namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type MigrationInspectProviderOptions =
    { Cohort: GitHubMigrationCopyCohort
      Repository: MigrationGitHubReadOptions
      Project: MigrationProjectReadOptions }

/// Issue census, Project membership/schema/values and single-repository native relations
/// have concrete raw-to-typed adapters. Remaining GS2-09.1 authorities refuse.
type MigrationInspectProviderAdapter =
    new: options:MigrationInspectProviderOptions * transport:IMigrationGitHubReadTransport -> MigrationInspectProviderAdapter
    interface IGitHubMigrationInspectSource

[<RequireQualifiedAccess>]
module MigrationInspectProviderAdapter =
    /// Refuses requests outside one declared read-only authority before dispatching to inner.
    val guardReadTransport:
        options:MigrationInspectProviderOptions -> authority:string ->
        inner:IMigrationGitHubReadTransport -> IMigrationGitHubReadTransport

    /// Refuses any relation query outside the exact source issue NodeIds before dispatch.
    val guardRelationTransport:
        options:MigrationInspectProviderOptions -> issueNodeIds:string list ->
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

    /// Requires one exact repository census and raw GraphQL pages for every relation endpoint.
    val bindNativeRelations:
        options:MigrationInspectProviderOptions ->
        issues:MigrationIssuePopulation ->
        issueProof:GitHubMigrationInspectAuthority ->
        population:MigrationRelationPopulation ->
        captures:(GitHubRequest * TransportOutcome) list ->
            Result<GitHubMigrationInspectAuthority, string>
