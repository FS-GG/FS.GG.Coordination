namespace FS.GG.Coordination.WorkspaceCompiler.Tests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Fsgg
open FS.GG.SDD.Artifacts
open FS.GG.SDD.Artifacts.ArtifactRef
open FS.GG.SDD.Artifacts.CatalogScaffoldProvenance
open FS.GG.SDD.Artifacts.ScaffoldProvenance
open FS.GG.Coordination.WorkspaceCompiler

/// Deliberately synthetic declaration/provenance controls; no tool or scaffold is run or certified.
module GeneratedControls =
    let private require =
        function
        | Ok x -> x
        | Error e -> failwithf "%A" e

    let private digest bytes =
        "sha256:" + (SHA256.HashData(bytes: byte array) |> Convert.ToHexStringLower)

    let private source revision path (raw: byte array) : SourceBytes =
        {
            Source =
                {
                    Repository = "FS-GG/FS.GG.Coordination"
                    Revision = revision
                    Path = path
                    Sha256 = SHA256.HashData raw |> Convert.ToHexStringLower
                }
            Bytes = raw
        }

    let values revision =
        let parameter key value : ProviderCatalog.Parameter =
            {
                Key = key
                Kind = ProviderCatalog.String
                Required = true
                Prompt = key
                Help = key
                Default = Some value
                Values = []
                Validation =
                    {
                        NonEmpty = true
                        MinLength = 1
                        MaxLength = 100
                        AllowedValues = []
                    }
            }

        let descriptor: ProviderCatalog.Descriptor =
            {
                Id = "opaque-control"
                DisplayName = "Synthetic control"
                Help = "No runtime evidence"
                Language = "opaque"
                ProductShape = "control"
                DescriptorId = "opaque-control-descriptor"
                DescriptorRevision = "1"
                DescriptorDigest = digest [||]
                ContractVersion = "3.0.0"
                TemplateSource = "opaque-control::1.0.0"
                TemplateId = "opaque-control"
                Platforms = [ "control-os" ]
                Parameters =
                    [
                        parameter "raw" "raw"
                        parameter "package" "package"
                        parameter "code" "Code"
                    ]
                Identities =
                    {
                        RawName = "raw"
                        PackageIdentity = "package"
                        CodeIdentifier = "code"
                    }
                Tools =
                    [
                        {
                            Id = "control-tool"
                            Version = "1.0.0"
                            Platforms = [ "control-os" ]
                        }
                    ]
                Capabilities =
                    [
                        {
                            Id = "control:check"
                            Required = true
                            Platforms = [ "control-os" ]
                            ToolIds = [ "control-tool" ]
                            EvidenceIds = [ "control-proof" ]
                            Binding =
                                ProviderCatalog.Command(
                                    {
                                        Executable = "control-tool"
                                        Arguments = [ "check" ]
                                    },
                                    {
                                        WorkingDirectory = "."
                                        TimeoutSeconds = 1
                                        CostClass = "cheap"
                                        EnvironmentIds = [ "control-environment" ]
                                    }
                                )
                        }
                    ]
                Evidence =
                    [
                        {
                            Id = "control-proof"
                            Format = "control-format@1.0.0"
                            Path = "out/proof.json"
                            Required = true
                        }
                    ]
                Skills = [ "control-skill" ]
            }

        let descriptor =
            { descriptor with
                DescriptorDigest = ProviderCatalogIntegrity.descriptorBytes descriptor |> digest
            }

        let catalog: ProviderCatalog.Catalog =
            {
                SchemaVersion = 2
                Id = "control-catalog"
                Revision = "1"
                Digest = digest [||]
                Providers = [ descriptor ]
            }

        let catalog =
            { catalog with
                Digest = ProviderCatalogIntegrity.catalogBytes catalog |> digest
            }

        let node = JsonNode.Parse(ProviderCatalogIntegrity.catalogBytes catalog)
        node["digest"] <- JsonValue.Create catalog.Digest
        let raw = Encoding.UTF8.GetBytes(node.ToJsonString())

        let catalogSource =
            source revision "work/1081-workspace-obligations-compiler/control-catalog.json" raw

        let prepared = ProviderCatalog.resolve catalog descriptor.Id [] |> require
        let hash = digest [||]

        let generator: SchemaVersion.GeneratorVersion =
            {
                Id = "synthetic-control"
                Version = "1.0.0"
            }

        let produced: ScaffoldProducedPath =
            {
                Path = "product.txt"
                Owner = GeneratedProduct
                Sha256 = Some hash
            }

        let ownership: ScaffoldProvenanceRecord =
            {
                SchemaVersion = 1
                Generator = generator
                RequiredMinimumCliVersion = None
                ProviderName = descriptor.Id
                ProviderContractVersion = descriptor.ContractVersion
                TemplateRef = descriptor.TemplateSource
                Outcome = "providerSucceeded"
                ProducedPaths = [ produced ]
                MirroredPaths = []
                SddOwnedPaths = []
                DriverPaths = []
                GameSkillPaths = []
                RenderingSkillPaths = []
                EffectiveParameters = prepared.EffectiveParameters
            }

        let record: CatalogScaffoldProvenanceRecord =
            {
                SchemaVersion = 2
                Generator = generator
                Ownership = ownership
                Observation =
                    {
                        Platform = "control-os"
                        Tools =
                            [
                                {
                                    Id = "control-tool"
                                    Version = "1.0.0"
                                    Executable = "control-tool"
                                }
                            ]
                        ConsumedArchiveDigest = hash
                        Transport =
                            {
                                Executable = "control-transport"
                                Version = "1.0.0"
                            }
                        Invocations =
                            [
                                {
                                    Executable = "control-transport"
                                    Arguments = [ "create" ]
                                    WorkingRoot = "staging"
                                    WorkingDirectory = "."
                                    EnvironmentRoots = []
                                    TimeoutSeconds = 1
                                    ExitCode = 0
                                }
                            ]
                        Result = "succeeded"
                        ProducedPaths = [ produced ]
                    }
                Declaration =
                    {
                        RawCatalogDigest = digest raw
                        CatalogId = catalog.Id
                        CatalogRevision = catalog.Revision
                        CatalogDigest = catalog.Digest
                        Descriptor = descriptor
                        EffectiveParameters = prepared.EffectiveParameters
                        RawProductName = prepared.RawProductName
                        PackageIdentity = prepared.PackageIdentity
                        CodeIdentifier = prepared.CodeIdentifier
                        Archive =
                            {
                                Id = "control-archive"
                                Version = "1.0.0"
                                Digest = hash
                            }
                        Policy =
                            {
                                Id = "control-policy"
                                Version = "1"
                                Digest = hash
                            }
                        EvidenceMap =
                            {
                                Id = "control-map"
                                Version = "1"
                                Digest = hash
                            }
                        Budgets =
                            {
                                RequestedPreflightSeconds = 1
                                RequestedScaffoldSeconds = 1
                                AdmittedPreflightSeconds = 1
                                AdmittedScaffoldSeconds = 1
                            }
                        Admission =
                            {
                                ContractVersion = "1.0.0"
                                Platform = "control-os"
                                RequiredCapabilityIds = []
                                SemanticOnlyCapabilityIds = []
                                SupportedPlatforms = [ "control-os" ]
                                SupportedEnvironmentIds = [ "control-environment" ]
                                SupportedFormats =
                                    [
                                        {
                                            Id = "control-format"
                                            Version = "1.0.0"
                                        }
                                    ]
                                EvidenceMappings =
                                    [
                                        {
                                            Format = "control-format@1.0.0"
                                            Id = "control-format"
                                            Version = "1.0.0"
                                        }
                                    ]
                                Descriptor = descriptor
                            }
                    }
            }

        let provenance value =
            CatalogScaffoldProvenance.serialize value
            |> require
            |> Encoding.UTF8.GetBytes
            |> source revision "work/1081-workspace-obligations-compiler/control-provenance.json"

        catalogSource, descriptor.Id, record, provenance

    let source3 revision (catalog: CatalogScaffoldProvenanceRecord) =
        let hash = "sha256:" + String('a', 64)

        let value: SourceScaffoldProvenance.Record =
            {
                SchemaVersion = 3
                Catalog = catalog
                Source =
                    {
                        Schema = "fsgg.provider-source-observation/1"
                        SourceSetSha256 = hash
                        AcquisitionPolicySha256 = hash
                        Source =
                            {
                                Id = "synthetic-control"
                                Origin = ProviderSource.Explicit
                                Locator = ProviderSource.LocalDirectory "/synthetic/control"
                                ManifestSha256 = hash
                            }
                        Revision = "synthetic-control"
                        Acquisition = ProviderSource.LocalCopy
                        ResolvedUri = None
                        Redirects = []
                        NetworkHops = []
                        CatalogSha256 = catalog.Declaration.RawCatalogDigest
                        TemplateSource = catalog.Declaration.Descriptor.TemplateSource
                        ArchiveSha256 = catalog.Declaration.Archive.Digest
                    }
            }

        SourceScaffoldProvenance.serialize value
        |> require
        |> Encoding.UTF8.GetBytes
        |> source revision "work/1081-workspace-obligations-compiler/control-provenance3.json"
