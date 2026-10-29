namespace FS.GG.Coordination.WorkspaceCompiler

open System
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.Core.OrdinaryAssignment
open FS.GG.SDD.Artifacts
open FS.GG.SDD.Artifacts.TypedSpecifications

type SourceBytes = { Source: Source; Bytes: byte array }

type ProviderInput =
    | Framework of repositoryProfile: SpecificationId
    | Generated of
        catalog: SourceBytes *
        providerId: string *
        parameters: (string * string) list *
        provenance: SourceBytes

module Compiler =
    exception private Rejected of Refusal

    let private missing reason =
        raise (Rejected(NeedsEvidence [ reason ]))

    let private conflict reason =
        raise (Rejected(ConflictingEvidence [ reason ]))

    let private text (bytes: byte array) =
        UTF8Encoding(false, true).GetString bytes

    let private sha bytes =
        SHA256.HashData(bytes: byte array) |> Convert.ToHexStringLower

    let private parsed label =
        function
        | Ok value -> value
        | Error _ -> missing label

    let compile
        (accepted: SourceBytes)
        acceptedFingerprint
        (moduleSources: Map<SpecificationId, Source>)
        (workModel: SourceBytes)
        (authored: SourceBytes list)
        (sourceClosure: SourceBytes list)
        provider
        workspace
        (repositories: Repository list)
        (components: Component list)
        (observedAt: DateTimeOffset)
        (completeness: Completeness)
        (originalFacts: Requirements list)
        : Result<AuthoritativeObligations, Refusal> =
        try
            // Capture each input once. Caller-owned arrays are never retained in the result.
            let capture (value: SourceBytes) =
                if isNull value.Bytes || value.Bytes.Length = 0 || value.Bytes.Length > 1024 * 1024 then
                    missing "source-byte-bound"

                let bytes = Array.copy value.Bytes

                if sha bytes <> value.Source.Sha256 then
                    conflict ("source-digest:" + value.Source.Path)

                { value with Bytes = bytes }

            let providerSourceCount =
                match provider with
                | Framework _ -> 0
                | Generated _ -> 2

            if
                authored.Length + sourceClosure.Length + 2 + providerSourceCount > 128
                || originalFacts.Length > 32
                || repositories.Length > 8
                || components.Length > 16
                || moduleSources.Count > 128
                || completeness.ExpectedWorkKeys.Count > 32
                || completeness.Sources.Length > 128
                || (components
                    |> List.exists (fun p -> p.Sources.Length > 128 || p.Commands.Count > 128))
            then
                raise (Rejected(NeedsDecomposition [ "source-or-work-bound" ]))

            if
                repositories
                |> List.exists (fun r -> String.IsNullOrWhiteSpace r.Identity || String.IsNullOrWhiteSpace r.Revision)
            then
                missing "repository-identity-and-custody-required"

            let accepted = capture accepted
            let workModel = capture workModel

            let extras =
                match provider with
                | Framework _ -> []
                | Generated(c, _, _, p) -> [ c; p ]

            let captured =
                (accepted :: workModel :: (authored @ sourceClosure @ extras))
                |> List.map capture

            if captured |> List.sumBy (fun s -> int64 s.Bytes.Length) > 8L * 1024L * 1024L then
                raise (Rejected(NeedsDecomposition [ "whole-source-byte-bound" ]))

            let sources = captured |> List.map _.Source |> List.distinct |> List.sort
            let identities = sources |> List.map (fun s -> s.Repository, s.Revision, s.Path)

            if identities.Length <> (identities |> Set.ofList |> Set.count) then
                conflict "source-identity-conflict"

            if (components |> List.map _.Id |> Set.ofList |> Set.count) <> components.Length then
                conflict "component-identity-conflict"

            for part in components do
                if
                    String.IsNullOrWhiteSpace part.Id
                    || String.IsNullOrWhiteSpace part.Root
                    || String.IsNullOrWhiteSpace part.Language
                then
                    missing "component-identity-required"

                if part.Root = "." then
                    missing "component-root-unsupported"

                if
                    part.Sources
                    |> List.exists (fun s ->
                        String.IsNullOrWhiteSpace s.Path
                        || (s.Path <> part.Root
                            && not (s.Path.StartsWith(part.Root + "/", StringComparison.Ordinal))))
                then
                    conflict "component-source-path-mismatch"

            let contains source = List.contains source sources

            let model =
                WorkspaceLifecycle.deserializeModel (text accepted.Bytes)
                |> parsed "accepted-workspace-invalid"

            let fingerprint =
                WorkspaceLifecycle.fingerprint model |> parsed "accepted-workspace-invalid"

            if fingerprint <> acceptedFingerprint then
                conflict "accepted-workspace-fingerprint"

            let moduleIds = model.Modules |> List.map _.Id |> Set.ofList

            if moduleIds <> (moduleSources |> Map.keys |> Set.ofSeq) then
                conflict "accepted-module-population"

            for item in model.Modules do
                let source = moduleSources[item.Id]

                if not (contains source) || item.ContentSha256 <> source.Sha256 then
                    conflict "accepted-module-source"

            let snapshot (value: SourceBytes) : FileSnapshot =
                {
                    Path = value.Source.Path
                    Text = text value.Bytes
                    RawBytes = Some value.Bytes
                }

            let supplied =
                WorkModel.parseWorkModel (snapshot workModel) |> parsed "work-model-invalid"
            // Regenerate from authored inputs with the existing producer, rather than trusting a
            // generated task list whose source-digest labels merely look correct.
            let authored = authored |> List.map capture
            let snapshots = authored |> List.map snapshot

            let regenerated =
                WorkItem.loadWorkItemFromSnapshots snapshots supplied.WorkId
                |> WorkModel.fromParsedWorkItem

            if not (WorkModel.blockingDiagnostics regenerated).IsEmpty then
                missing "authored-work-invalid"

            if
                supplied.WorkId <> regenerated.WorkId
                || supplied.Project <> regenerated.Project
                || supplied.WorkItem <> regenerated.WorkItem
                || supplied.Tasks <> regenerated.Tasks
                || supplied.Requirements <> regenerated.Requirements
                || supplied.Decisions <> regenerated.Decisions
                || supplied.Evidence <> regenerated.Evidence
            then
                conflict "work-model-authored-correspondence"

            if supplied.Sources <> regenerated.Sources then
                conflict "work-model-source-population"

            let byPath = authored |> List.groupBy (fun s -> s.Source.Path) |> Map.ofList

            for source in supplied.Sources do
                match Map.tryFind source.Path byPath with
                | Some [ actual ] when
                    source.SourceDigest.Algorithm = "sha256"
                    && source.SourceDigest.Value = actual.Source.Sha256
                    ->
                    ()
                | _ -> conflict ("work-model-source:" + source.Path)

                if
                    source.Kind <> "agentsConfig"
                    && source.Kind <> "projectConfig"
                    && source.Kind <> "sddConfig"
                    && not (moduleSources |> Map.values |> Seq.exists (fun s -> s.Path = source.Path))
                then
                    conflict "authored-source-not-in-accepted-workspace"

            let tasks = regenerated.Tasks
            let keys = tasks |> List.map _.Id |> Set.ofList

            if tasks.IsEmpty || keys.Count <> tasks.Length then
                missing "authored-task-population"

            if completeness.ExpectedWorkKeys <> keys then
                conflict "independent-expected-work-population"

            if
                not completeness.SourceBound
                || completeness.Sources.IsEmpty
                || not (completeness.Sources |> List.forall contains)
            then
                missing "independent-completeness-sources"

            let tasksSources =
                supplied.Sources
                |> List.filter (fun s -> s.Kind = "tasks")
                |> List.map _.Path
                |> Set.ofList

            if
                tasksSources.IsEmpty
                || not (completeness.Sources |> List.exists (fun s -> tasksSources.Contains s.Path))
            then
                missing "completeness-authored-population-source"

            let facts = originalFacts |> List.map (fun f -> f.Key, f) |> Map.ofList

            if facts.Count <> originalFacts.Length || (facts |> Map.keys |> Set.ofSeq) <> keys then
                missing "original-work-facts-population"

            if
                originalFacts
                |> List.exists (fun f ->
                    f.SelectedAt > observedAt
                    || f.Deadline |> Option.exists (fun d -> d <= observedAt))
            then
                missing "original-source-window-exhausted"

            let providerId, capabilities, tools, skills, outputs =
                match provider with
                | Framework profile ->
                    if
                        not (
                            model.Modules
                            |> List.exists (fun m -> m.Id = profile && m.Kind = WorkspaceModuleKind.RepositoryProfile)
                        )
                    then
                        conflict "accepted-repository-profile"

                    let profileSource = moduleSources[profile]

                    if not (components |> List.exists (fun part -> List.contains profileSource part.Sources)) then
                        missing "component-repository-profile-binding"

                    SpecificationId.value profile, Set.empty, Set.empty, Set.empty, Set.empty
                | Generated(catalog, id, parameters, provenance) ->
                    if
                        parameters.Length > 64
                        || parameters
                           |> List.exists (fun (k, v) -> isNull k || isNull v || k.Length > 512 || v.Length > 4096)
                    then
                        raise (Rejected(NeedsDecomposition [ "provider-parameter-bound" ]))

                    let catalog = capture catalog
                    let provenance = capture provenance

                    let catalogValue =
                        ProviderCatalogIntegrity.verify ("sha256:" + catalog.Source.Sha256) catalog.Bytes
                        |> parsed "catalog-invalid"

                    let prepared =
                        Fsgg.ProviderCatalog.resolve catalogValue id parameters
                        |> parsed "provider-selection-invalid"

                    let declaration =
                        match
                            ScaffoldProvenanceDocument.parse (text provenance.Bytes)
                            |> parsed "provenance-invalid"
                        with
                        | ScaffoldProvenanceDocument.Catalog p -> p.Declaration
                        | ScaffoldProvenanceDocument.SourceCatalog p -> p.Catalog.Declaration
                        | _ -> missing "catalog-provenance-schema-required"

                    if
                        declaration.CatalogId <> prepared.CatalogId
                        || declaration.CatalogRevision <> prepared.CatalogRevision
                        || declaration.CatalogDigest <> prepared.CatalogDigest
                        || declaration.Descriptor <> prepared.Descriptor
                        || declaration.RawCatalogDigest <> "sha256:" + catalog.Source.Sha256
                        || declaration.EffectiveParameters <> prepared.EffectiveParameters
                        || declaration.RawProductName <> prepared.RawProductName
                        || declaration.PackageIdentity <> prepared.PackageIdentity
                        || declaration.CodeIdentifier <> prepared.CodeIdentifier
                    then
                        conflict "provider-provenance-correspondence"

                    let required = prepared.Descriptor.Capabilities |> List.filter _.Required

                    if not (components |> List.exists (fun p -> p.Language = prepared.Descriptor.Language)) then
                        missing "provider-language-component-binding"

                    let commands =
                        components |> List.collect (fun c -> Set.toList c.Commands) |> Set.ofList

                    for c in required do
                        match c.Binding with
                        | Fsgg.ProviderCatalog.Command _ when not (commands.Contains c.Id) ->
                            missing "provider-command-source-binding"
                        | _ -> ()

                    prepared.Descriptor.Id,
                    (required |> List.map _.Id |> Set.ofList),
                    (prepared.Descriptor.Tools |> List.map _.Id |> Set.ofList),
                    Set.ofList prepared.Descriptor.Skills,
                    (prepared.Descriptor.Evidence
                     |> List.filter _.Required
                     |> List.map _.Id
                     |> Set.ofList)

            let workspaceContract: WorkspaceExecutionContract =
                {
                    Version = "workspace-execution/1"
                    Workspace = workspace
                    Provider = providerId
                    Repositories = repositories
                    Components = components
                    Sources = sources
                }

            let works =
                tasks
                |> List.map (fun task ->
                    let original = facts[task.Id]

                    { original with
                        Workspace = workspaceContract
                        Dependencies = Set.union original.Dependencies (Set.ofList task.Dependencies)
                        Capabilities = Set.union original.Capabilities capabilities
                        Tools = Set.union original.Tools tools
                        Skills = Set.unionMany [ original.Skills; skills; Set.ofList task.RequiredSkills ]
                        RequiredOutputs =
                            Set.unionMany [ original.RequiredOutputs; outputs; Set.ofList task.RequiredEvidence ]
                    })

            {
                Version = "ordinary-authority/1"
                Works = works
                Completeness = completeness
            }
            |> bindAuthority
            |> validateAuthority
        with
        | Rejected refusal -> Error refusal
        | :? DecoderFallbackException -> Error(NeedsEvidence [ "source-utf8" ])
        | :? ArgumentException -> Error(NeedsEvidence [ "source-input-invalid" ])

    let productionProjection (_: AuthoritativeObligations) : Result<unit, Refusal> =
        Error(RequiresOperatorDecision [ "reference-compiler-has-no-production-projection" ])
