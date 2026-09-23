namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type GitHubTransformFamily =
    | Taxonomy
    | PlanningFields
    | RepositoryScope
    | BodyMetadata
    | Blockers
    | Hierarchy
    | SchedulingHolds
    | TouchSets
    | LifecycleReceipts
    | DesiredSettings

type GitHubTransformObligation =
    { SubjectIdentity: string; Family: GitHubTransformFamily }

type GitHubMigratedTransform =
    { TargetIdentity: string; GlobalId: string; TargetSchema: string; PayloadSha256: string; MappingSha256: string }

type GitHubAmbiguousCandidate =
    { TargetIdentity: string; TargetSchema: string; PayloadSha256: string; MappingSha256: string }

type GitHubAmbiguousTransform =
    { Reason: string; Candidates: GitHubAmbiguousCandidate list; EvidenceSha256: string }

type GitHubUnsupportedTransform =
    { Code: string; Reason: string; EvidenceSha256: string }

[<RequireQualifiedAccess>]
type GitHubTypedTransformDecision =
    | Migrated of GitHubMigratedTransform
    | Ambiguous of GitHubAmbiguousTransform
    | Unsupported of GitHubUnsupportedTransform

type GitHubTypedTransform =
    {
        SubjectIdentity: string
        GlobalId: string
        Family: GitHubTransformFamily
        SourceSchema: string
        SourceBytesSha256: string
        SourceValueSha256: string
        Decision: GitHubTypedTransformDecision
    }

type GitHubTypedTransformQualification =
    {
        SchemaVersion: int
        QualificationId: string
        RoadmapRevision: string
        RoadmapSha256: string
        UnitContractSha256: string
        PredecessorReceiptDigest: string
        ManifestNormalizedDigest: string
        ManifestSeal: string
        Transformer: GitHubManifestFingerprint
        Obligations: GitHubTransformObligation list
        Transforms: GitHubTypedTransform list
        CreatedAt: DateTimeOffset
        NormalizedDigest: string
        Seal: string
    }

[<RequireQualifiedAccess>]
type GitHubTypedTransformFinding =
    | InvalidTransformField of string
    | InvalidTransformerFingerprint
    | InvalidObligationPopulation
    | InvalidTransformPopulation
    | InvalidMigratedTransform of string
    | InvalidAmbiguousTransform of string
    | InvalidUnsupportedTransform of string
    | AlteredTransformDigest
    | AlteredTransformSeal

[<RequireQualifiedAccess>]
type GitHubTypedTransformControl =
    | TransformPrerequisiteReceipt
    | TransformManifestBinding
    | TransformTaxonomy
    | TransformPlanningFields
    | TransformRepositoryScope
    | TransformBodyMetadata
    | TransformBlockers
    | TransformHierarchy
    | TransformSchedulingHolds
    | TransformTouchSets
    | TransformLifecycleReceipts
    | TransformDesiredSettings
    | TransformCompleteCoverage
    | TransformTypedOutcomes
    | TransformTamperRefusal
    | TransformReplay
    | TransformNoMutation

type GitHubTypedTransformControlResult =
    { Control: GitHubTypedTransformControl; ControlPassed: bool; BaselineGreen: bool }

type GitHubTypedTransformQualificationFinding =
    { Code: string; ControlId: string; Message: string }

module GitHubTypedTransformQualification =
    let requiredFamilies =
        [
            GitHubTransformFamily.Blockers
            GitHubTransformFamily.BodyMetadata
            GitHubTransformFamily.DesiredSettings
            GitHubTransformFamily.Hierarchy
            GitHubTransformFamily.LifecycleReceipts
            GitHubTransformFamily.PlanningFields
            GitHubTransformFamily.RepositoryScope
            GitHubTransformFamily.SchedulingHolds
            GitHubTransformFamily.Taxonomy
            GitHubTransformFamily.TouchSets
        ]

    let familyId =
        function
        | GitHubTransformFamily.Taxonomy -> "taxonomy"
        | GitHubTransformFamily.PlanningFields -> "planning-fields"
        | GitHubTransformFamily.RepositoryScope -> "repository-scope"
        | GitHubTransformFamily.BodyMetadata -> "body-metadata"
        | GitHubTransformFamily.Blockers -> "blockers"
        | GitHubTransformFamily.Hierarchy -> "hierarchy"
        | GitHubTransformFamily.SchedulingHolds -> "scheduling-holds"
        | GitHubTransformFamily.TouchSets -> "touch-sets"
        | GitHubTransformFamily.LifecycleReceipts -> "lifecycle-receipts"
        | GitHubTransformFamily.DesiredSettings -> "desired-settings"

    let requiredControls =
        [
            GitHubTypedTransformControl.TransformPrerequisiteReceipt
            GitHubTypedTransformControl.TransformManifestBinding
            GitHubTypedTransformControl.TransformTaxonomy
            GitHubTypedTransformControl.TransformPlanningFields
            GitHubTypedTransformControl.TransformRepositoryScope
            GitHubTypedTransformControl.TransformBodyMetadata
            GitHubTypedTransformControl.TransformBlockers
            GitHubTypedTransformControl.TransformHierarchy
            GitHubTypedTransformControl.TransformSchedulingHolds
            GitHubTypedTransformControl.TransformTouchSets
            GitHubTypedTransformControl.TransformLifecycleReceipts
            GitHubTypedTransformControl.TransformDesiredSettings
            GitHubTypedTransformControl.TransformCompleteCoverage
            GitHubTypedTransformControl.TransformTypedOutcomes
            GitHubTypedTransformControl.TransformTamperRefusal
            GitHubTypedTransformControl.TransformReplay
            GitHubTypedTransformControl.TransformNoMutation
        ]

    let controlId =
        function
        | GitHubTypedTransformControl.TransformPrerequisiteReceipt -> "transform-prerequisite-receipt"
        | GitHubTypedTransformControl.TransformManifestBinding -> "transform-manifest-binding"
        | GitHubTypedTransformControl.TransformTaxonomy -> "transform-taxonomy"
        | GitHubTypedTransformControl.TransformPlanningFields -> "transform-planning-fields"
        | GitHubTypedTransformControl.TransformRepositoryScope -> "transform-repository-scope"
        | GitHubTypedTransformControl.TransformBodyMetadata -> "transform-body-metadata"
        | GitHubTypedTransformControl.TransformBlockers -> "transform-blockers"
        | GitHubTypedTransformControl.TransformHierarchy -> "transform-hierarchy"
        | GitHubTypedTransformControl.TransformSchedulingHolds -> "transform-scheduling-holds"
        | GitHubTypedTransformControl.TransformTouchSets -> "transform-touch-sets"
        | GitHubTypedTransformControl.TransformLifecycleReceipts -> "transform-lifecycle-receipts"
        | GitHubTypedTransformControl.TransformDesiredSettings -> "transform-desired-settings"
        | GitHubTypedTransformControl.TransformCompleteCoverage -> "transform-complete-coverage"
        | GitHubTypedTransformControl.TransformTypedOutcomes -> "transform-typed-outcomes"
        | GitHubTypedTransformControl.TransformTamperRefusal -> "transform-tamper-refusal"
        | GitHubTypedTransformControl.TransformReplay -> "transform-replay"
        | GitHubTypedTransformControl.TransformNoMutation -> "transform-no-mutation"

    let private validText value = not (String.IsNullOrWhiteSpace value)

    let private isSha length value =
        validText value
        && value.Length = length
        && Regex.IsMatch(value, "^[0-9a-f]+$", RegexOptions.CultureInvariant)

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private framed values = values |> List.map frame |> String.concat ""

    let private hash (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private unique values = List.length values = (values |> Set.ofList |> Set.count)
    let private key subject family = subject, familyId family
    let private obligationKey (value: GitHubTransformObligation) = key value.SubjectIdentity value.Family
    let private transformKey (value: GitHubTypedTransform) = key value.SubjectIdentity value.Family

    let private migratedParts (value: GitHubMigratedTransform) =
        [ "migrated"; value.TargetIdentity; value.GlobalId; value.TargetSchema; value.PayloadSha256; value.MappingSha256 ]

    let private candidateParts (value: GitHubAmbiguousCandidate) =
        [ value.TargetIdentity; value.TargetSchema; value.PayloadSha256; value.MappingSha256 ]

    let private decisionParts =
        function
        | GitHubTypedTransformDecision.Migrated value -> migratedParts value
        | GitHubTypedTransformDecision.Ambiguous value ->
            [
                yield "ambiguous"
                yield value.Reason
                for candidate in value.Candidates do yield! candidateParts candidate
                yield value.EvidenceSha256
            ]
        | GitHubTypedTransformDecision.Unsupported value ->
            [ "unsupported"; value.Code; value.Reason; value.EvidenceSha256 ]

    let private payloadParts (qualification: GitHubTypedTransformQualification) =
        [
            yield "fsgg.coordination.github-typed-transform-qualification/1"
            yield qualification.QualificationId
            yield qualification.RoadmapRevision
            yield qualification.RoadmapSha256
            yield qualification.UnitContractSha256
            yield qualification.PredecessorReceiptDigest
            yield qualification.ManifestNormalizedDigest
            yield qualification.ManifestSeal
            yield qualification.Transformer.Name
            yield qualification.Transformer.Version
            yield qualification.Transformer.Sha256
            yield string qualification.Transformer.Bytes
            for obligation in qualification.Obligations do
                yield obligation.SubjectIdentity
                yield familyId obligation.Family
            for transform in qualification.Transforms do
                yield transform.SubjectIdentity
                yield transform.GlobalId
                yield familyId transform.Family
                yield transform.SourceSchema
                yield transform.SourceBytesSha256
                yield transform.SourceValueSha256
                yield! decisionParts transform.Decision
            yield qualification.CreatedAt.ToUniversalTime().ToString("O")
        ]

    let private seal digest =
        [ "fsgg.coordination.github-typed-transform-seal/1"; digest ] |> framed |> hash

    let private validate expectedObligations (qualification: GitHubTypedTransformQualification) =
        let findings = ResizeArray<GitHubTypedTransformFinding>()
        let require condition field =
            if not condition then findings.Add(GitHubTypedTransformFinding.InvalidTransformField field)

        require (qualification.SchemaVersion = 1) "schemaVersion"
        require (validText qualification.QualificationId) "qualificationId"
        require (isSha 40 qualification.RoadmapRevision) "roadmapRevision"
        require (isSha 64 qualification.RoadmapSha256) "roadmapSha256"
        require (isSha 64 qualification.UnitContractSha256) "unitContractSha256"
        require (isSha 64 qualification.PredecessorReceiptDigest) "predecessorReceiptDigest"
        require (isSha 64 qualification.ManifestNormalizedDigest) "manifestNormalizedDigest"
        require (isSha 64 qualification.ManifestSeal) "manifestSeal"

        if not (
            validText qualification.Transformer.Name
            && validText qualification.Transformer.Version
            && isSha 64 qualification.Transformer.Sha256
            && qualification.Transformer.Bytes > 0L
        ) then
            findings.Add GitHubTypedTransformFinding.InvalidTransformerFingerprint

        let obligationKeys = qualification.Obligations |> List.map obligationKey
        let expectedKeys = expectedObligations |> List.map obligationKey

        if List.isEmpty expectedKeys
           || expectedKeys <> List.sort expectedKeys
           || not (unique expectedKeys)
           || qualification.Obligations <> expectedObligations then
            findings.Add GitHubTypedTransformFinding.InvalidObligationPopulation

        if qualification.Obligations |> List.exists (fun value -> not (validText value.SubjectIdentity)) then
            findings.Add GitHubTypedTransformFinding.InvalidObligationPopulation

        let transformKeys = qualification.Transforms |> List.map transformKey
        if transformKeys <> obligationKeys || not (unique transformKeys) then
            findings.Add GitHubTypedTransformFinding.InvalidTransformPopulation

        let representedFamilies = qualification.Transforms |> List.map _.Family |> Set.ofList
        if requiredFamilies |> List.exists (fun family -> not (Set.contains family representedFamilies)) then
            findings.Add GitHubTypedTransformFinding.InvalidTransformPopulation

        for transform in qualification.Transforms do
            let identity = $"{transform.SubjectIdentity}:{familyId transform.Family}"
            if not (
                validText transform.SubjectIdentity
                && validText transform.GlobalId
                && validText transform.SourceSchema
                && isSha 64 transform.SourceBytesSha256
                && isSha 64 transform.SourceValueSha256
            ) then
                findings.Add(GitHubTypedTransformFinding.InvalidTransformField identity)

            match transform.Decision with
            | GitHubTypedTransformDecision.Migrated migrated ->
                if not (
                    validText migrated.TargetIdentity
                    && migrated.GlobalId = transform.GlobalId
                    && validText migrated.TargetSchema
                    && isSha 64 migrated.PayloadSha256
                    && isSha 64 migrated.MappingSha256
                ) then
                    findings.Add(GitHubTypedTransformFinding.InvalidMigratedTransform identity)
            | GitHubTypedTransformDecision.Ambiguous ambiguous ->
                let candidateIds = ambiguous.Candidates |> List.map _.TargetIdentity
                let validCandidate candidate =
                    validText candidate.TargetIdentity
                    && validText candidate.TargetSchema
                    && isSha 64 candidate.PayloadSha256
                    && isSha 64 candidate.MappingSha256

                if not (
                    validText ambiguous.Reason
                    && isSha 64 ambiguous.EvidenceSha256
                    && ambiguous.Candidates.Length >= 2
                    && candidateIds = List.sort candidateIds
                    && unique candidateIds
                    && List.forall validCandidate ambiguous.Candidates
                ) then
                    findings.Add(GitHubTypedTransformFinding.InvalidAmbiguousTransform identity)
            | GitHubTypedTransformDecision.Unsupported unsupported ->
                if not (validText unsupported.Code && validText unsupported.Reason && isSha 64 unsupported.EvidenceSha256) then
                    findings.Add(GitHubTypedTransformFinding.InvalidUnsupportedTransform identity)

        findings |> Seq.distinct |> Seq.toList

    let qualify qualificationId roadmapRevision roadmapSha256 unitContractSha256 predecessorReceiptDigest manifestNormalizedDigest manifestSeal transformer obligations transforms createdAt =
        let draft =
            {
                SchemaVersion = 1
                QualificationId = qualificationId
                RoadmapRevision = roadmapRevision
                RoadmapSha256 = roadmapSha256
                UnitContractSha256 = unitContractSha256
                PredecessorReceiptDigest = predecessorReceiptDigest
                ManifestNormalizedDigest = manifestNormalizedDigest
                ManifestSeal = manifestSeal
                Transformer = transformer
                Obligations = obligations
                Transforms = transforms
                CreatedAt = createdAt
                NormalizedDigest = ""
                Seal = ""
            }

        match validate obligations draft with
        | _ :: _ as findings -> Error findings
        | [] ->
            let digest = payloadParts draft |> framed |> hash
            Ok { draft with NormalizedDigest = digest; Seal = seal digest }

    let verify expectedObligations expectedSeal qualification =
        match validate expectedObligations qualification with
        | _ :: _ as findings -> Error findings
        | [] ->
            let digest = payloadParts qualification |> framed |> hash
            if digest <> qualification.NormalizedDigest then
                Error [ GitHubTypedTransformFinding.AlteredTransformDigest ]
            elif not (isSha 64 expectedSeal) || qualification.Seal <> seal digest || qualification.Seal <> expectedSeal then
                Error [ GitHubTypedTransformFinding.AlteredTransformSeal ]
            else
                Ok qualification

    let validateControls generated independent =
        let validateLane lane rows =
            let controls = rows |> List.map _.Control
            [
                if controls <> requiredControls || not (unique controls) then
                    yield { Code = "TRANSFORM-CONTROLS"; ControlId = lane; Message = "control inventory differs" }

                for row in rows do
                    if not (row.ControlPassed && row.BaselineGreen) then
                        yield { Code = "TRANSFORM-CONTROL-RED"; ControlId = controlId row.Control; Message = $"{lane} control did not pass" }
            ]

        match validateLane "generated" generated @ validateLane "independent" independent with
        | [] -> Ok()
        | findings -> Error findings
