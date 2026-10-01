namespace FS.GG.FourD.Typed

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json

module Join =
    let private admissionKeys = Set [
        "schema"; "repository"; "repositoryId"; "environment"; "workflowPath"; "workflowRef"
        "placementSha"; "phase"; "operationId"; "runId"; "runAttempt"; "runNonce"
        "originalActorId"; "triggeringActorId"; "fourdRepository"; "fourdRepositoryId"
        "fourdSourceSha"; "fourdSourceTree"; "fourdInventorySha256"; "p2SourceSha"; "p2SourceTree"
        "capacityRunId"; "capacityRunAttempt"; "capacityArtifactId"; "capacityArtifactDigest"
        "sourceCapsule"; "sealerRecipeSha"; "sealerSha256"; "custodyPublicKeySha256"
        "nativePolicySha256"; "custodyPolicySha256"; "environmentReadbackSha256"; "issuedAt"; "expiresAt"
    ]
    let private sourceSha = "d5d8b6d242b13dd79007fcbbb6e5ee4069fd3264"
    let private sourceTree = "ae626190a30a784db8968157a1ef1c9c5c499770"
    let private inventory = "bf3ec0ab2fe639bc9f4bc53da8f33c8adf8f237a505f9eee6eca0002b1cd1c49"
    let private p2Sha = "c069263c3e9e8780b1596eee82d2f6c017daa8df"
    let private p2Tree = "d525a227f5df61b551e09915df51d7d4bd9ec11e"
    let private getString (value: JsonElement) name =
        match value.TryGetProperty(name: string) with
        | true, item when item.ValueKind = JsonValueKind.String -> item.GetString()
        | _ -> null
    let private exact value name expected = getString value name = expected

    let validate (admission: JsonElement) placement runId runAttempt observedSha observedTree observedInventory =
        let keys = admission.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
        let closed = keys = admissionKeys
        let fixedBindings =
            exact admission "schema" "fsgg.fourd.public-provider-admission/2"
            && exact admission "repository" "FS-GG/FS.GG.Coordination"
            && exact admission "repositoryId" "1346720714"
            && exact admission "environment" "fourd-native-private-source"
            && exact admission "workflowPath" ".github/workflows/fourd-public-provider-qualification.yml"
            && exact admission "phase" "qualification"
            && exact admission "operationId" "fourd-portable-technical"
            && exact admission "fourdRepository" "FS-GG/FS.GG.FourD"
            && exact admission "fourdRepositoryId" "1390568106"
            && exact admission "fourdSourceSha" sourceSha && exact admission "fourdSourceTree" sourceTree
            && exact admission "fourdInventorySha256" inventory
            && exact admission "p2SourceSha" p2Sha && exact admission "p2SourceTree" p2Tree
        let observed =
            exact admission "placementSha" placement && exact admission "runId" runId
            && exact admission "runAttempt" runAttempt
            && observedSha = sourceSha && observedTree = sourceTree && observedInventory = inventory
        if not closed then Error "typed-admission-shape-refused"
        elif not fixedBindings then Error "typed-admission-binding-refused"
        elif not observed then Error "typed-source-join-refused"
        else
            let canonical = JsonSerializer.SerializeToUtf8Bytes(admission)
            Ok(Convert.ToHexString(SHA256.HashData canonical).ToLowerInvariant())
