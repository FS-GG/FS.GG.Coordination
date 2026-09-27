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

    /// Binds one repository REST document to an explicitly partial core-settings proof.
    /// Its distinct authority name cannot satisfy the complete repository-settings row.
    val bindRepositoryCoreSettings:
        options:MigrationInspectProviderOptions ->
        settings:MigrationRepositoryCoreSettings ->
        captures:(GitHubRequest * TransportOutcome) list ->
            Result<GitHubMigrationInspectAuthority, string>

    /// Binds the repository Actions core policy and its conditional selected allowlist.
    /// This remains a partial settings proof and cannot satisfy repository-settings.
    val bindRepositoryActionsPolicy:
        options:MigrationInspectProviderOptions ->
        settings:MigrationRepositoryActionsPolicy ->
        captures:(GitHubRequest * TransportOutcome) list ->
            Result<GitHubMigrationInspectAuthority, string>

    /// Binds organization property definitions and one repository's explicit values.
    /// Enterprise inheritance and the other settings surfaces remain outside this proof.
    val bindRepositoryCustomProperties:
        options:MigrationInspectProviderOptions ->
        settings:MigrationCustomProperties ->
        captures:(GitHubRequest * TransportOutcome) list ->
            Result<GitHubMigrationInspectAuthority, string>

    /// Revalidates two stable raw snapshots for every declared receiver.
    /// Declaration exhaustiveness and pin bytes remain outside this partial proof.
    val bindDeclaredReceiverIdentities:
        options:MigrationInspectProviderOptions ->
        first:MigrationReceiverSnapshot list ->
        second:MigrationReceiverSnapshot list ->
            Result<GitHubMigrationInspectAuthority, string>

    /// Revalidates declared workflow/package blob bytes over two stable receiver snapshots.
    /// The declaration roster remains caller supplied, so canonical workflow-pins stays unavailable.
    val bindDeclaredWorkflowPins:
        options:MigrationInspectProviderOptions ->
        pinsByReceiver:Map<string, MigrationReceiverPinDeclaration list> ->
        first:MigrationReceiverPinSnapshot list ->
        second:MigrationReceiverPinSnapshot list ->
            Result<GitHubMigrationInspectAuthority, string>

    /// Requires one exact repository census and raw GraphQL pages for every relation endpoint.
    val bindNativeRelations:
        options:MigrationInspectProviderOptions ->
        issues:MigrationIssuePopulation ->
        issueProof:GitHubMigrationInspectAuthority ->
        population:MigrationRelationPopulation ->
        captures:(GitHubRequest * TransportOutcome) list ->
            Result<GitHubMigrationInspectAuthority, string>
