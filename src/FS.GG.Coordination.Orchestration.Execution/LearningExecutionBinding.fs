namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

type LearningExecutionBinding =
    { Schema: string
      BindingSha256: string
      TreatmentAssignmentSha256: string
      TreatmentOwnerPrincipalId: string
      TreatmentWorkflowRevision: string
      TreatmentGeneration: int64
      TreatmentAssignedAt: DateTimeOffset
      TreatmentProposalSha256: string
      TreatmentContextManifestSha256: string
      TreatmentArm: string
      SubjectBindingSha256: string
      ItemId: string
      OriginalItemId: string
      Relation: string
      ParentItemId: string option
      AssignmentId: Guid
      AttemptId: Guid
      Generation: int64
      ProposalSha256: string
      ContextManifestSha256: string
      RenderedInputSha256: string
      Requested: RequestedSelection
      Deadline: DateTimeOffset
      MaximumRuntimeSeconds: int64
      MaximumAttempts: int
      SnapshotId: string
      SnapshotDigest: string
      SnapshotCapturedAt: DateTimeOffset
      ManifestId: string
      ManifestVersion: string
      ExperimentContractId: string
      PolicyRepository: string
      PolicyRevision: string
      PolicyPath: string
      PolicySha256: string
      PolicyStatus: string
      WorkClassId: string
      RubricVersion: string
      RecipeId: string
      RecipeDigest: string
      Arm: string
      QualificationOnly: bool }

type ILearningExecutionBindingStore =
    abstract BindLearningExecution:
        LearningExecutionBinding * CancellationToken -> Task<Result<LearningExecutionBinding, string>>

    abstract ReadLearningExecution:
        assignmentId: Guid * attemptId: Guid * CancellationToken -> Task<Result<LearningExecutionBinding, string>>

[<RequireQualifiedAccess>]
module LearningExecutionBinding =
    [<Literal>]
    let schema = "fsgg.orchestration.learning-execution-binding/1"

    let private line (value: string) = Convert.ToBase64String(Encoding.UTF8.GetBytes value)
    let private optional (value: string option) = value |> Option.defaultValue ""

    let canonicalBytes (value: LearningExecutionBinding) =
        [ value.Schema; value.TreatmentAssignmentSha256; value.TreatmentOwnerPrincipalId
          value.TreatmentWorkflowRevision; string value.TreatmentGeneration; value.TreatmentAssignedAt.ToString("O")
          value.TreatmentProposalSha256; value.TreatmentContextManifestSha256; value.TreatmentArm
          value.SubjectBindingSha256; value.ItemId
          value.OriginalItemId; value.Relation; optional value.ParentItemId; string value.AssignmentId
          string value.AttemptId; string value.Generation; value.ProposalSha256; value.ContextManifestSha256
          value.RenderedInputSha256; optional value.Requested.Model; optional value.Requested.Effort
          value.Deadline.ToString("O"); string value.MaximumRuntimeSeconds; string value.MaximumAttempts
          value.SnapshotId; value.SnapshotDigest; value.SnapshotCapturedAt.ToString("O"); value.ManifestId
          value.ManifestVersion; value.ExperimentContractId; value.PolicyRepository; value.PolicyRevision
          value.PolicyPath; value.PolicySha256; value.PolicyStatus; value.WorkClassId; value.RubricVersion
          value.RecipeId; value.RecipeDigest; value.Arm; string value.QualificationOnly ]
        |> List.map line
        |> String.concat "\n"
        |> Encoding.UTF8.GetBytes

    let digest value = SHA256.HashData(canonicalBytes { value with BindingSha256 = "" }) |> Convert.ToHexString |> _.ToLowerInvariant()

    let private sha (value: string) =
        not (isNull value) && value.Length = 64
        && value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f')

    let private text maximum (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim() && value.Length <= maximum

    let private arm value = value = "current" || value = "focused"

    let private lineage value =
        match value.Relation, value.ParentItemId with
        | "original", None -> value.ItemId = value.OriginalItemId
        | ("descendant" | "retry"), Some parent ->
            value.ItemId <> value.OriginalItemId && parent <> value.ItemId
        | _ -> false

    let validate value =
        if value.Schema <> schema || value.BindingSha256 <> digest value then Error "learning-execution-binding-digest-refused"
        elif [ value.TreatmentAssignmentSha256; value.TreatmentProposalSha256; value.TreatmentContextManifestSha256; value.SubjectBindingSha256; value.ProposalSha256; value.ContextManifestSha256; value.RenderedInputSha256; value.SnapshotDigest; value.PolicySha256; value.RecipeDigest ] |> List.exists (sha >> not) then Error "learning-execution-binding-sha-refused"
        elif value.AssignmentId = Guid.Empty || value.AttemptId = Guid.Empty || value.Generation < 0L then Error "learning-execution-binding-identity-refused"
        elif value.Deadline.Offset <> TimeSpan.Zero || value.SnapshotCapturedAt.Offset <> TimeSpan.Zero || value.TreatmentAssignedAt.Offset <> TimeSpan.Zero || value.TreatmentGeneration < 0L || value.MaximumRuntimeSeconds < 1L || value.MaximumAttempts < 1 then Error "learning-execution-binding-limit-refused"
        elif not value.QualificationOnly || value.PolicyStatus <> "source-contract-not-enrolled" then Error "learning-execution-binding-enrollment-refused"
        elif not (arm value.TreatmentArm) || not (arm value.Arm) || value.TreatmentArm <> value.Arm then Error "learning-execution-binding-arm-refused"
        elif not (lineage value) then Error "learning-execution-binding-lineage-refused"
        elif [ value.TreatmentOwnerPrincipalId; value.TreatmentWorkflowRevision; value.TreatmentArm; value.ItemId; value.OriginalItemId; value.Relation; value.SnapshotId; value.ManifestId; value.ManifestVersion; value.ExperimentContractId; value.PolicyRepository; value.PolicyRevision; value.PolicyPath; value.WorkClassId; value.RubricVersion; value.RecipeId; value.Arm ] |> List.exists (text 512 >> not) then Error "learning-execution-binding-text-refused"
        else Ok value

    let decode (bytes: byte array) =
        try
            let values =
                (Encoding.UTF8.GetString bytes).Split('\n', StringSplitOptions.None)
                |> Array.map (fun value -> Convert.FromBase64String value |> Encoding.UTF8.GetString)

            if values.Length <> 42 then Error "learning-execution-binding-shape-refused"
            else
                let option value = if value = "" then None else Some value
                { Schema = values[0]
                  BindingSha256 = ""
                  TreatmentAssignmentSha256 = values[1]
                  TreatmentOwnerPrincipalId = values[2]
                  TreatmentWorkflowRevision = values[3]
                  TreatmentGeneration = Int64.Parse values[4]
                  TreatmentAssignedAt = DateTimeOffset.Parse values[5]
                  TreatmentProposalSha256 = values[6]
                  TreatmentContextManifestSha256 = values[7]
                  TreatmentArm = values[8]
                  SubjectBindingSha256 = values[9]
                  ItemId = values[10]
                  OriginalItemId = values[11]
                  Relation = values[12]
                  ParentItemId = option values[13]
                  AssignmentId = Guid.Parse values[14]
                  AttemptId = Guid.Parse values[15]
                  Generation = Int64.Parse values[16]
                  ProposalSha256 = values[17]
                  ContextManifestSha256 = values[18]
                  RenderedInputSha256 = values[19]
                  Requested = { Model = option values[20]; Effort = option values[21] }
                  Deadline = DateTimeOffset.Parse values[22]
                  MaximumRuntimeSeconds = Int64.Parse values[23]
                  MaximumAttempts = Int32.Parse values[24]
                  SnapshotId = values[25]
                  SnapshotDigest = values[26]
                  SnapshotCapturedAt = DateTimeOffset.Parse values[27]
                  ManifestId = values[28]
                  ManifestVersion = values[29]
                  ExperimentContractId = values[30]
                  PolicyRepository = values[31]
                  PolicyRevision = values[32]
                  PolicyPath = values[33]
                  PolicySha256 = values[34]
                  PolicyStatus = values[35]
                  WorkClassId = values[36]
                  RubricVersion = values[37]
                  RecipeId = values[38]
                  RecipeDigest = values[39]
                  Arm = values[40]
                  QualificationOnly = Boolean.Parse values[41] }
                |> fun value -> { value with BindingSha256 = digest value }
                |> validate
        with
        | :? FormatException -> Error "learning-execution-binding-format-refused"
        | :? ArgumentException -> Error "learning-execution-binding-format-refused"
