namespace FS.GG.Coordination.Orchestration.Runner.Client

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type LearningTreatmentArm =
    | Current
    | Focused

/// Immutable fields already persisted by DurableLearningTreatment.
type LearningTreatmentTelemetrySource =
    {
        OriginalItemId: string
        Arm: LearningTreatmentArm
        AssignmentSha256: string
        ProposalSha256: string
        ContextManifestSha256: string
        OwnerPrincipalId: string
        WorkflowRevision: string
        Generation: int64
        AssignedAt: DateTimeOffset
    }

/// Values required by the pinned receiver but absent from DurableLearningTreatment.
/// J4 must retrieve these from selected configuration/source; callers cannot substitute
/// them for the durable treatment hashes validated by prepare.
type LearningTelemetryConfiguration =
    {
        SubjectBindingSha256: string
        ExperimentContractId: string
        PolicyRepository: string
        PolicyRevision: string
        PolicyPath: string
        PolicySha256: string
        PolicyStatus: string
        WorkClassId: string
        QualificationOnly: bool
        SnapshotId: string
        RubricVersion: string
        SnapshotDigest: string
        CapturedAt: DateTimeOffset
        RecipeId: string
        RecipeDigest: string
        ManifestId: string
        ManifestDigest: string
        ManifestVersion: string
    }

[<Sealed>]
type PreparedLearningTelemetry internal
    (
        treatment: LearningTreatmentTelemetrySource,
        configuration: LearningTelemetryConfiguration,
        snapshotIdentity: string,
        manifestIdentity: string,
        assignmentIdentity: string,
        deviation: string option
    ) =
    member _.Treatment = treatment
    member _.Configuration = configuration
    member _.SnapshotIdentity = snapshotIdentity
    member _.ManifestIdentity = manifestIdentity
    member _.AssignmentIdentity = assignmentIdentity
    member _.Deviation = deviation

[<RequireQualifiedAccess>]
module LearningTelemetryFacts =
    [<Literal>]
    let ExperimentContractId = "learn-01-current-focused-v1"

    [<Literal>]
    let PolicyRepository = "FS-GG/.github"

    [<Literal>]
    let PolicyRevision = "2e553e41e58ee2f5e27aedcffc7403ce50e7cdd4"

    [<Literal>]
    let PolicyPath = "policy/learn-01-current-focused-v1.json"

    [<Literal>]
    let PolicySha256 = "91713679fd486459188f2144e75cc69b77720c7841b6e75cd5d4d35620ed4179"

    [<Literal>]
    let PolicyStatus = "source-contract-not-enrolled"

    [<Literal>]
    let WorkClassId = "github-routine-source-with-valid-plan-v1"

    let private digestPattern = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private sha256 (value: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes value)
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private present limit (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim() && value.Length <= limit

    let private validDigest value = not (isNull value) && digestPattern.IsMatch value

    let private identity prefix originalItemId digest =
        prefix + "-" + sha256 (originalItemId + "\u001f" + digest)

    let prepare (treatment: LearningTreatmentTelemetrySource) (configuration: LearningTelemetryConfiguration) =
        let treatmentText =
            [ treatment.OriginalItemId; treatment.OwnerPrincipalId; treatment.WorkflowRevision ]

        let configurationText =
            [ configuration.SnapshotId
              configuration.RubricVersion
              configuration.RecipeId
              configuration.ManifestId
              configuration.ManifestVersion
              configuration.ExperimentContractId
              configuration.PolicyRepository
              configuration.PolicyRevision
              configuration.PolicyPath
              configuration.PolicyStatus
              configuration.WorkClassId ]

        let treatmentDigests =
            [ treatment.AssignmentSha256
              treatment.ProposalSha256
              treatment.ContextManifestSha256 ]

        let configurationDigests =
            [ configuration.SnapshotDigest
              configuration.RecipeDigest
              configuration.ManifestDigest
              configuration.SubjectBindingSha256
              configuration.PolicySha256 ]

        if treatmentText @ configurationText |> List.exists (present 512 >> not) then
            Error "learning-telemetry-field-invalid"
        elif treatment.Generation < 0L then
            Error "learning-telemetry-revision-invalid"
        elif treatmentDigests @ configurationDigests |> List.exists (validDigest >> not) then
            Error "learning-telemetry-digest-invalid"
        elif
            not configuration.QualificationOnly
            || configuration.ExperimentContractId <> ExperimentContractId
            || configuration.PolicyRepository <> PolicyRepository
            || configuration.PolicyRevision <> PolicyRevision
            || configuration.PolicyPath <> PolicyPath
            || configuration.PolicySha256 <> PolicySha256
            || configuration.PolicyStatus <> PolicyStatus
            || configuration.WorkClassId <> WorkClassId
            || configuration.RubricVersion <> "1"
        then
            Error "learning-telemetry-not-qualification-only"
        elif configuration.ManifestDigest <> treatment.ContextManifestSha256 then
            Error "learning-telemetry-manifest-mismatch"
        else
            Ok(
                PreparedLearningTelemetry(
                    treatment,
                    configuration,
                    identity "learn-snapshot" treatment.OriginalItemId configuration.SnapshotDigest,
                    identity "learn-manifest" treatment.OriginalItemId configuration.ManifestDigest,
                    "learn-assignment-" + treatment.AssignmentSha256,
                    Some "qualification-only-not-enrolled"
                )
            )

    let arm (prepared: PreparedLearningTelemetry) =
        match prepared.Treatment.Arm with
        | LearningTreatmentArm.Current -> "current"
        | LearningTreatmentArm.Focused -> "focused"
