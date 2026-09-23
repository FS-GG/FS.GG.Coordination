namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json

type GenesisNativeApproval = { ReviewerId: int64; State: string }

type GenesisProtectedNativeRead =
    {
        RunRepositoryId: int64
        RunId: int64
        RunEvent: string
        RunPath: string
        RunRef: string
        RunHead: GitObjectId
        RunConclusion: string
        RunActorId: int64
        RunAttempt: int
        WorkflowReadRevision: GitObjectId
        WorkflowBytes: byte array
        ArtifactReadRunId: int64
        ArtifactBytes: byte array
        EnvironmentName: string
        EnvironmentBranchPolicy: string
        EnvironmentReviewerIds: int64 list
        EnvironmentPreventsSelfReview: bool
        Approvals: GenesisNativeApproval list
    }

type VerifiedGenesisProtectedApproval = private VerifiedGenesisProtectedApproval of int64

[<RequireQualifiedAccess>]
module V1AdmissionGenesisProtectedApproval =
    let private workflowPath = ".github/workflows/gs2-v1-admission-protected-authorization.yml"
    let private workflowSha256 = "e0682cdcb201781ef67308b1546e0e21090a6efebe1c92316cc9fbe110040767"
    let private eligibleReviewers = Set.ofList [ 1645484L; 4456104L ]

    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private receiptTime (value: string) =
        DateTimeOffset.ParseExact(
            value,
            "yyyy-MM-ddTHH:mm:ss'Z'",
            Globalization.CultureInfo.InvariantCulture,
            Globalization.DateTimeStyles.AssumeUniversal ||| Globalization.DateTimeStyles.AdjustToUniversal
        )

    let private exactProperties expected (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then
            false
        else
            let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            names.Length = (names |> List.distinct |> List.length)
            && Set.ofList names = Set.ofList expected

    let private receipt (bytes: byte array) =
        try
            if obj.ReferenceEquals(bytes, null) || bytes.Length > 8192 then
                Error [ "genesis-protected-artifact-bytes" ]
            else
                let content = UTF8Encoding(false, true).GetString bytes
                use document = JsonDocument.Parse content
                let root = document.RootElement
                let expected =
                    [ "schema"; "operationId"; "repository"; "runId"; "workflowRevision"
                      "coordinationRevision"; "coordinationTree"; "environment"
                      "genesisIntentSha256"; "approvedAt"; "expiresAt"; "conclusion" ]

                if not (exactProperties expected root) then
                    Error [ "genesis-protected-artifact-shape" ]
                else
                    Ok(
                        root.GetProperty("schema").GetString(),
                        root.GetProperty("operationId").GetString(),
                        root.GetProperty("repository").GetString(),
                        root.GetProperty("runId").GetInt64(),
                        root.GetProperty("workflowRevision").GetString(),
                        root.GetProperty("coordinationRevision").GetString(),
                        root.GetProperty("coordinationTree").GetString(),
                        root.GetProperty("environment").GetString(),
                        root.GetProperty("genesisIntentSha256").GetString(),
                        receiptTime (root.GetProperty("approvedAt").GetString()),
                        receiptTime (root.GetProperty("expiresAt").GetString()),
                        root.GetProperty("conclusion").GetString()
                    )
        with _ ->
            Error [ "genesis-protected-artifact-invalid" ]

    let verify asOf plan intent signature native =
        receipt native.ArtifactBytes
        |> Result.bind (fun (schema, operationId, repository, artifactRunId, workflowRevision, coordinationRevision, coordinationTree, environment, intentSha256, approvedAt, expiresAt, conclusion) ->
            let approved = native.Approvals |> List.filter (fun approval -> approval.State = "approved")
            let plannedIntentSha256 =
                V1AdmissionGenesisAuthorization.canonicalIntent plan intent
                |> sha256
            let errors =
                [
                    if plannedIntentSha256
                       <> (V1AdmissionGenesisAuthorization.intentSha256 signature |> V1AdmissionRegistry.sha256Value) then
                        "genesis-protected-signature-intent"
                    if schema <> "fsgg.v1-admission-genesis-protected-authorization/1"
                       || repository <> "FS-GG/.github"
                       || operationId <> (V1AdmissionRegistry.genesisCommit plan).OperationId
                       || artifactRunId <> native.RunId
                       || artifactRunId <> V1AdmissionGenesisAuthorization.protectedRunId signature
                       || workflowRevision <> (V1AdmissionRegistry.gitObjectIdValue intent.WorkflowRevision)
                       || coordinationRevision <> (V1AdmissionRegistry.gitObjectIdValue intent.SourceCommit)
                       || coordinationTree <> (V1AdmissionRegistry.gitObjectIdValue intent.SourceTree)
                       || intentSha256 <> (V1AdmissionGenesisAuthorization.intentSha256 signature |> V1AdmissionRegistry.sha256Value)
                       || environment <> "fleet-cutover"
                       || conclusion <> "success" then
                        "genesis-protected-artifact-binding"
                    if approvedAt > asOf || asOf >= expiresAt
                       || expiresAt <= approvedAt
                       || expiresAt - approvedAt > TimeSpan.FromHours 2. then
                        "genesis-protected-artifact-expiry"
                    if native.RunRepositoryId <> 1269292704L
                       || native.RunId < 1L
                       || native.RunEvent <> "workflow_dispatch"
                       || native.RunPath <> workflowPath
                       || native.RunRef <> "refs/heads/main"
                       || native.RunHead <> intent.WorkflowRevision
                       || native.RunConclusion <> "success"
                       || native.RunAttempt <> 1 then
                        "genesis-protected-native-run"
                    if obj.ReferenceEquals(native.WorkflowBytes, null)
                       || native.WorkflowBytes.Length > 16384
                       || native.WorkflowReadRevision <> native.RunHead
                       || sha256 native.WorkflowBytes <> workflowSha256
                       || workflowSha256 <> (V1AdmissionRegistry.sha256Value intent.WorkflowSha256) then
                        "genesis-protected-workflow-drift"
                    if native.ArtifactReadRunId <> native.RunId then
                        "genesis-protected-artifact-provenance"
                    if native.EnvironmentName <> "fleet-cutover"
                       || native.EnvironmentBranchPolicy <> "custom-main"
                       || not native.EnvironmentPreventsSelfReview
                       || Set.ofList native.EnvironmentReviewerIds <> eligibleReviewers
                       || native.EnvironmentReviewerIds.Length <> eligibleReviewers.Count then
                        "genesis-protected-environment"
                    if approved.IsEmpty
                       || approved.Length > eligibleReviewers.Count
                       || (approved |> List.map _.ReviewerId |> List.distinct |> List.length) <> approved.Length
                       || native.Approvals.Length <> approved.Length
                       || (approved
                           |> List.exists (fun approval ->
                               not (eligibleReviewers.Contains approval.ReviewerId)
                               || approval.ReviewerId = native.RunActorId)) then
                        "genesis-protected-native-approvals"
                ]

            match errors with
            | [] -> Ok(VerifiedGenesisProtectedApproval native.RunId)
            | _ -> Error errors)

    let runId (VerifiedGenesisProtectedApproval value) = value
