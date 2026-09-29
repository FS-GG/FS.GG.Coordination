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
        [ value.Schema; value.TreatmentAssignmentSha256; value.SubjectBindingSha256; value.ItemId
          value.OriginalItemId; value.Relation; optional value.ParentItemId; string value.AssignmentId
          string value.AttemptId; string value.Generation; value.ProposalSha256; value.ContextManifestSha256
          value.RenderedInputSha256; optional value.Requested.Model; optional value.Requested.Effort
          value.Deadline.ToString("O"); string value.MaximumRuntimeSeconds; string value.MaximumAttempts
          value.SnapshotId; value.SnapshotDigest; value.SnapshotCapturedAt.ToString("O"); value.ManifestId
          value.ManifestVersion; value.ExperimentContractId; value.PolicyRepository; value.PolicyRevision
          value.PolicyPath; value.PolicySha256; value.PolicyStatus; value.WorkClassId; value.RubricVersion
          value.RecipeId; value.Arm; string value.QualificationOnly ]
        |> List.map line
        |> String.concat "\n"
        |> Encoding.UTF8.GetBytes

    let digest value = SHA256.HashData(canonicalBytes { value with BindingSha256 = "" }) |> Convert.ToHexString |> _.ToLowerInvariant()

    let private sha (value: string) =
        not (isNull value) && value.Length = 64
        && value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f')

    let private text maximum (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim() && value.Length <= maximum

    let validate value =
        if value.Schema <> schema || value.BindingSha256 <> digest value then Error "learning-execution-binding-digest-refused"
        elif [ value.TreatmentAssignmentSha256; value.SubjectBindingSha256; value.ProposalSha256; value.ContextManifestSha256; value.RenderedInputSha256; value.SnapshotDigest; value.PolicySha256 ] |> List.exists (sha >> not) then Error "learning-execution-binding-sha-refused"
        elif value.AssignmentId = Guid.Empty || value.AttemptId = Guid.Empty || value.Generation < 0L then Error "learning-execution-binding-identity-refused"
        elif value.Deadline.Offset <> TimeSpan.Zero || value.SnapshotCapturedAt.Offset <> TimeSpan.Zero || value.MaximumRuntimeSeconds < 1L || value.MaximumAttempts < 1 then Error "learning-execution-binding-limit-refused"
        elif not value.QualificationOnly || value.PolicyStatus <> "source-contract-not-enrolled" then Error "learning-execution-binding-enrollment-refused"
        elif [ value.ItemId; value.OriginalItemId; value.Relation; value.SnapshotId; value.ManifestId; value.ManifestVersion; value.ExperimentContractId; value.PolicyRepository; value.PolicyRevision; value.PolicyPath; value.WorkClassId; value.RubricVersion; value.RecipeId; value.Arm ] |> List.exists (text 512 >> not) then Error "learning-execution-binding-text-refused"
        else Ok value

    let decode (bytes: byte array) =
        try
            let values =
                (Encoding.UTF8.GetString bytes).Split('\n', StringSplitOptions.None)
                |> Array.map (fun value -> Convert.FromBase64String value |> Encoding.UTF8.GetString)

            if values.Length <> 34 then Error "learning-execution-binding-shape-refused"
            else
                let option value = if value = "" then None else Some value
                { Schema = values[0]
                  BindingSha256 = ""
                  TreatmentAssignmentSha256 = values[1]
                  SubjectBindingSha256 = values[2]
                  ItemId = values[3]
                  OriginalItemId = values[4]
                  Relation = values[5]
                  ParentItemId = option values[6]
                  AssignmentId = Guid.Parse values[7]
                  AttemptId = Guid.Parse values[8]
                  Generation = Int64.Parse values[9]
                  ProposalSha256 = values[10]
                  ContextManifestSha256 = values[11]
                  RenderedInputSha256 = values[12]
                  Requested = { Model = option values[13]; Effort = option values[14] }
                  Deadline = DateTimeOffset.Parse values[15]
                  MaximumRuntimeSeconds = Int64.Parse values[16]
                  MaximumAttempts = Int32.Parse values[17]
                  SnapshotId = values[18]
                  SnapshotDigest = values[19]
                  SnapshotCapturedAt = DateTimeOffset.Parse values[20]
                  ManifestId = values[21]
                  ManifestVersion = values[22]
                  ExperimentContractId = values[23]
                  PolicyRepository = values[24]
                  PolicyRevision = values[25]
                  PolicyPath = values[26]
                  PolicySha256 = values[27]
                  PolicyStatus = values[28]
                  WorkClassId = values[29]
                  RubricVersion = values[30]
                  RecipeId = values[31]
                  Arm = values[32]
                  QualificationOnly = Boolean.Parse values[33] }
                |> fun value -> { value with BindingSha256 = digest value }
                |> validate
        with
        | :? FormatException -> Error "learning-execution-binding-format-refused"
        | :? ArgumentException -> Error "learning-execution-binding-format-refused"
