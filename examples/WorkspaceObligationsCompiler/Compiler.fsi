namespace FS.GG.Coordination.WorkspaceCompiler

open FS.GG.Coordination.Core.OrdinaryAssignment
open FS.GG.SDD.Artifacts.TypedSpecifications
open System

/// Source observations supplied by the caller; no filesystem or operation authority.
type SourceBytes = { Source: Source; Bytes: byte array }

/// These are in-memory call parameters, never another serialized workspace contract.
type ProviderInput =
    | Framework of repositoryProfile: SpecificationId
    | Generated of
        catalog: SourceBytes *
        providerId: string *
        parameters: (string * string) list *
        provenance: SourceBytes

module Compiler =
    /// Compose existing authority from independently supplied sources, population and original facts.
    /// No process, store, network, reservation or production projection is invoked.
    val compile:
        accepted: SourceBytes ->
        acceptedFingerprint: string ->
        moduleSources: Map<SpecificationId, Source> ->
        workModel: SourceBytes ->
        authored: SourceBytes list ->
        sourceClosure: SourceBytes list ->
        provider: ProviderInput ->
        workspace: string ->
        repositories: Repository list ->
        components: Component list ->
        observedAt: DateTimeOffset ->
        completeness: Completeness ->
        originalFacts: Requirements list ->
            Result<AuthoritativeObligations, Refusal>

    /// This reference has no production route; complete domain data cannot authorize one.
    val productionProjection: AuthoritativeObligations -> Result<unit, Refusal>
