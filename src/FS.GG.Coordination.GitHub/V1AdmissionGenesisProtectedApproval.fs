namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json

type GenesisNativeApproval = { ReviewerId: int64; State: string; EnvironmentIds: int64 list }

type GenesisProtectedNativeRead =
    {
        ObservedAt: DateTimeOffset
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
        EnvironmentId: int64
        EnvironmentName: string
        EnvironmentBranchPolicy: string
        EnvironmentWaitMinutes: int
        EnvironmentReviewerIds: int64 list
        EnvironmentPreventsSelfReview: bool
        Approvals: GenesisNativeApproval list
    }

type VerifiedGenesisProtectedApproval = private VerifiedGenesisProtectedApproval of int64

[<RequireQualifiedAccess>]
module V1AdmissionGenesisProtectedApproval =
    let private workflowPath = ".github/workflows/gs2-v1-admission-protected-authorization.yml"
    let private workflowSha256 = "07435f26a2e22b6bd597aa89ce83192b39c8ab19d7aeabd74c9a67e16be4adf3"
    let private accountableOwnerId = 1645484L
    let private waitTimerBotId = 41898282L
    let private environmentId = 22582241959L
    let private environmentName = "fleet-v1-admission-owner"

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

    let decodeNativeRead (bytes: ReadOnlyMemory<byte>) =
        try
            if bytes.Length > 32768 then
                Error [ "genesis-native-evidence-size" ]
            else
                use document = JsonDocument.Parse bytes
                let root = document.RootElement
                let expected =
                    [ "schema"; "observedAt"; "runRepositoryId"; "runId"; "runEvent"
                      "runPath"; "runRef"; "runHead"; "runConclusion"; "runActorId"
                      "runAttempt"; "workflowReadRevision"; "workflowBytesBase64"
                      "artifactReadRunId"; "artifactBytesBase64"; "environmentId"
                      "environmentName"; "environmentBranchPolicy"; "environmentWaitMinutes"; "environmentReviewerIds"
                      "environmentPreventsSelfReview"; "approvals" ]

                if not (exactProperties expected root)
                   || root.GetProperty("schema").GetString() <> "fsgg.v1-admission-genesis-native-read/2" then
                    Error [ "genesis-native-evidence-shape" ]
                else
                    let string (name: string) = root.GetProperty(name).GetString()
                    let oid name =
                        V1AdmissionRegistry.gitObjectId (string name)
                        |> Result.defaultWith invalidOp
                    let base64 name ceiling =
                        let encoded = string name
                        let decoded = Convert.FromBase64String encoded
                        if decoded.Length > ceiling || Convert.ToBase64String decoded <> encoded then
                            invalidOp "genesis-native-evidence-base64"
                        decoded
                    let reviewerIds =
                        root.GetProperty("environmentReviewerIds").EnumerateArray()
                        |> Seq.map _.GetInt64()
                        |> Seq.toList
                    let approvals =
                        root.GetProperty("approvals").EnumerateArray()
                        |> Seq.map (fun entry ->
                            if not (exactProperties [ "reviewerId"; "state"; "environmentIds" ] entry) then
                                invalidOp "genesis-native-approval-shape"
                            { ReviewerId = entry.GetProperty("reviewerId").GetInt64()
                              State = entry.GetProperty("state").GetString()
                              EnvironmentIds =
                                entry.GetProperty("environmentIds").EnumerateArray()
                                |> Seq.map _.GetInt64()
                                |> Seq.toList })
                        |> Seq.toList
                    Ok
                        { ObservedAt = receiptTime (string "observedAt")
                          RunRepositoryId = root.GetProperty("runRepositoryId").GetInt64()
                          RunId = root.GetProperty("runId").GetInt64()
                          RunEvent = string "runEvent"
                          RunPath = string "runPath"
                          RunRef = string "runRef"
                          RunHead = oid "runHead"
                          RunConclusion = string "runConclusion"
                          RunActorId = root.GetProperty("runActorId").GetInt64()
                          RunAttempt = root.GetProperty("runAttempt").GetInt32()
                          WorkflowReadRevision = oid "workflowReadRevision"
                          WorkflowBytes = base64 "workflowBytesBase64" 16384
                          ArtifactReadRunId = root.GetProperty("artifactReadRunId").GetInt64()
                          ArtifactBytes = base64 "artifactBytesBase64" 8192
                          EnvironmentId = root.GetProperty("environmentId").GetInt64()
                          EnvironmentName = string "environmentName"
                          EnvironmentBranchPolicy = string "environmentBranchPolicy"
                          EnvironmentWaitMinutes = root.GetProperty("environmentWaitMinutes").GetInt32()
                          EnvironmentReviewerIds = reviewerIds
                          EnvironmentPreventsSelfReview = root.GetProperty("environmentPreventsSelfReview").GetBoolean()
                          Approvals = approvals }
        with _ ->
            Error [ "genesis-native-evidence-invalid" ]

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
            let expectedApprovals =
                [ { ReviewerId = accountableOwnerId; State = "approved"; EnvironmentIds = [ environmentId ] }
                  { ReviewerId = waitTimerBotId; State = "approved"; EnvironmentIds = [ environmentId ] } ]
            let plannedIntentSha256 =
                V1AdmissionGenesisAuthorization.canonicalIntent plan intent
                |> sha256
            let errors =
                [
                    if native.ObservedAt > asOf || asOf - native.ObservedAt > TimeSpan.FromMinutes 2. then
                        "genesis-protected-native-stale"
                    if plannedIntentSha256
                       <> (V1AdmissionGenesisAuthorization.intentSha256 signature |> V1AdmissionRegistry.sha256Value) then
                        "genesis-protected-signature-intent"
                    if schema <> "fsgg.v1-admission-genesis-protected-authorization/2"
                       || repository <> "FS-GG/.github"
                       || operationId <> (V1AdmissionRegistry.genesisCommit plan).OperationId
                       || artifactRunId <> native.RunId
                       || artifactRunId <> V1AdmissionGenesisAuthorization.protectedRunId signature
                       || workflowRevision <> (V1AdmissionRegistry.gitObjectIdValue intent.WorkflowRevision)
                       || coordinationRevision <> (V1AdmissionRegistry.gitObjectIdValue intent.SourceCommit)
                       || coordinationTree <> (V1AdmissionRegistry.gitObjectIdValue intent.SourceTree)
                       || intentSha256 <> (V1AdmissionGenesisAuthorization.intentSha256 signature |> V1AdmissionRegistry.sha256Value)
                       || environment <> environmentName
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
                       || native.RunAttempt <> 1
                       || native.RunActorId <> accountableOwnerId then
                        "genesis-protected-native-run"
                    if obj.ReferenceEquals(native.WorkflowBytes, null)
                       || native.WorkflowBytes.Length > 16384
                       || native.WorkflowReadRevision <> native.RunHead
                       || sha256 native.WorkflowBytes <> workflowSha256
                       || workflowSha256 <> (V1AdmissionRegistry.sha256Value intent.WorkflowSha256) then
                        "genesis-protected-workflow-drift"
                    if native.ArtifactReadRunId <> native.RunId then
                        "genesis-protected-artifact-provenance"
                    if native.EnvironmentId <> environmentId
                       || native.EnvironmentName <> environmentName
                       || native.EnvironmentBranchPolicy <> "custom-main"
                       || native.EnvironmentWaitMinutes <> 5
                       || native.EnvironmentPreventsSelfReview
                       || native.EnvironmentReviewerIds <> [ accountableOwnerId ] then
                        "genesis-protected-environment"
                    if native.Approvals <> expectedApprovals
                       || native.RunActorId <> accountableOwnerId then
                        "genesis-protected-native-approvals"
                ]

            match errors with
            | [] -> Ok(VerifiedGenesisProtectedApproval native.RunId)
            | _ -> Error errors)

    let runId (VerifiedGenesisProtectedApproval value) = value
