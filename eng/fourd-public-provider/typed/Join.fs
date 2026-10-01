namespace FS.GG.FourD.Typed

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

module Join =
    let private rootKeys = Set ["placementSha";"runId";"runAttempt";"runNonce";"originalActorId";"triggeringActorId"
                                "capacityRunId";"capacityRunAttempt";"capacityArtifactId";"capacityArtifactDigest"
                                "environmentReadbackSha256"]
    let private admissionKeys = Set [
        "schema"; "repository"; "repositoryId"; "environment"; "workflowPath"; "workflowRef"
        "placementSha"; "phase"; "operationId"; "runId"; "runAttempt"; "runNonce"
        "originalActorId"; "triggeringActorId"; "fourdRepository"; "fourdRepositoryId"
        "fourdSourceSha"; "fourdSourceTree"; "fourdInventorySha256"; "p2SourceSha"; "p2SourceTree"
        "capacityRunId"; "capacityRunAttempt"; "capacityArtifactId"; "capacityArtifactDigest"
        "sourceCapsule"; "sealerRecipeSha"; "sealerSha256"; "custodyPublicKeySha256"
        "nativePolicySha256"; "custodyPolicySha256"; "environmentReadbackSha256"; "issuedAt"; "expiresAt"
    ]
    let private rootObservationKeys = Set [
        "placementRefSha"; "placementCommitSha"; "placementTree"; "reservationRunId"
        "reservationRunAttempt"; "reservationHeadSha"; "reservationStatus"; "reservationConclusion"
        "originalActorId"; "triggeringActorId"; "reservationArtifactId"; "reservationArtifactDigest"
        "reservationResultRunId"; "reservationResultRunAttempt"; "reservationResultPlacementSha"
        "reservationResultPlacementTree"; "reservationResultOutcome"; "reservationResultQualified"
        "environmentReadbackSha256"; "capacityRunId"; "capacityRunAttempt"; "capacityRunConclusion"
        "capacityArtifactId"; "capacityArtifactDigest"; "secretCount"; "releaseCount"
    ]
    let private sourceSha = "d5d8b6d242b13dd79007fcbbb6e5ee4069fd3264"
    let private sourceTree = "ae626190a30a784db8968157a1ef1c9c5c499770"
    let private inventory = "bf3ec0ab2fe639bc9f4bc53da8f33c8adf8f237a505f9eee6eca0002b1cd1c49"
    let private p2Sha = "c069263c3e9e8780b1596eee82d2f6c017daa8df"
    let private p2Tree = "d525a227f5df61b551e09915df51d7d4bd9ec11e"
    let private nativePolicy = "92796150e503ee06f7dcda8e363d65d8d26a66563b142d8d1a58bfcfbd69ee45"
    let private custodyPolicy = "50cd8ee14ce64f5073f68286bf70f5a4e15d919728e31abaa1674e6ab3abd46b"
    let private sealerRecipe = "88d65daa1a1262d563c2312698e4a5e57109a824"
    let private sealer = "4f46d5a1762eaee9ba800e5fb58933a9c312e22e4d416b9489e632fd9b2ce85d"
    let private publicKey = "de40516580a5e6e97c154a8889c3ac6edf047b695ff5d4187386aeaccd61d3e7"
    let private getString (value: JsonElement) name =
        match value.TryGetProperty(name: string) with
        | true, item when item.ValueKind = JsonValueKind.String -> item.GetString()
        | _ -> null
    let private exact value name expected = getString value name = expected
    let private decimal (value: string) = not (String.IsNullOrEmpty value) && value[0] <> '0' && value |> Seq.forall Char.IsDigit
    let private nonce (value: string) =
        not (isNull value) && value.Length >= 8 && value.Length <= 48
        && (Char.IsLower value[0] || Char.IsDigit value[0])
        && value |> Seq.forall (fun c -> Char.IsLower c || Char.IsDigit c || c = '-')
    let private time (value: string) =
        match DateTimeOffset.TryParse(value, Globalization.CultureInfo.InvariantCulture,
                                      Globalization.DateTimeStyles.AssumeUniversal) with
        | true, parsed -> Some parsed | _ -> None

    let private hex length (value: string) = not (isNull value) && value.Length = length && value |> Seq.forall (fun c -> Char.IsDigit c || c >= 'a' && c <= 'f')
    let validate (admission: JsonElement) placement runId runAttempt observedSha observedTree observedInventory observedNow =
        let keys = admission.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
        let closed = keys = admissionKeys
        let fixedBindings =
            exact admission "schema" "fsgg.fourd.public-provider-admission/2"
            && exact admission "repository" "FS-GG/FS.GG.Coordination"
            && exact admission "repositoryId" "1346720714"
            && exact admission "environment" "fourd-native-private-source"
            && exact admission "workflowPath" ".github/workflows/fourd-public-provider-qualification.yml"
            && exact admission "workflowRef" "FS-GG/FS.GG.Coordination/.github/workflows/fourd-public-provider-qualification.yml@refs/heads/qualification/fourd-native-20261001"
            && exact admission "phase" "qualification"
            && exact admission "operationId" "fourd-portable-technical"
            && exact admission "fourdRepository" "FS-GG/FS.GG.FourD"
            && exact admission "fourdRepositoryId" "1390568106"
            && exact admission "fourdSourceSha" sourceSha && exact admission "fourdSourceTree" sourceTree
            && exact admission "fourdInventorySha256" inventory
            && exact admission "p2SourceSha" p2Sha && exact admission "p2SourceTree" p2Tree
            && exact admission "nativePolicySha256" nativePolicy
            && exact admission "custodyPolicySha256" custodyPolicy
            && exact admission "sealerRecipeSha" sealerRecipe && exact admission "sealerSha256" sealer
            && exact admission "custodyPublicKeySha256" publicKey
        let actors = decimal (getString admission "originalActorId") && decimal (getString admission "triggeringActorId")
        let times =
            match time (getString admission "issuedAt"), time (getString admission "expiresAt"), time observedNow with
            | Some issued, Some expires, Some current -> expires > issued && expires - issued <= TimeSpan.FromMinutes 45.
                                                         && current >= issued && current < expires
            | _ -> false
        let capsule = admission.GetProperty "sourceCapsule"
        let capsuleKeys = capsule.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
        let capsuleShape = capsule.ValueKind = JsonValueKind.Object && capsuleKeys = Set [
            "transport"; "releaseId"; "assetId"; "tag"; "name"; "ciphertextBytes"; "ciphertextSha256"
            "descriptor"; "descriptorSha256"; "recipientPublicKeySha256" ]
        let capsuleValues = capsuleShape && exact capsule "transport" "coordination-release-asset"
                            && decimal (getString capsule "releaseId") && decimal (getString capsule "assetId")
                            && hex 64 (getString capsule "ciphertextSha256")
                            && hex 64 (getString capsule "descriptorSha256")
                            && hex 64 (getString capsule "recipientPublicKeySha256")
                            && (match capsule.TryGetProperty("ciphertextBytes") with
                                | true, value when value.ValueKind=JsonValueKind.Number ->
                                    let bytes=value.GetInt32() in bytes>=2 && bytes<=24*1024*1024
                                | _ -> false)
                            && (match capsule.TryGetProperty("descriptor") with
                                | true, value -> value.ValueKind=JsonValueKind.Object
                                | _ -> false)
        let descriptor = if capsuleShape then capsule.GetProperty "descriptor" else Unchecked.defaultof<JsonElement>
        let descriptorKeys = if descriptor.ValueKind=JsonValueKind.Object then descriptor.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq else Set.empty
        let descriptorShape = descriptorKeys = Set ["schema";"purpose";"placementSha";"runId";"runAttempt";"runNonce";
            "coordinationRepositoryId";"fourdRepositoryId";"sourceSha";"sourceTree";"inventorySha256";
            "plaintextBytes";"plaintextSha256";"recipientPublicKeySha256";"sealerSha256";"issuedAt";"expiresAt"]
        let descriptorRaw = if descriptorShape then Encoding.UTF8.GetBytes(descriptor.GetRawText() + "\n") else Array.empty
        let descriptorDigest = if descriptorRaw.Length > 0 then Convert.ToHexString(SHA256.HashData(descriptorRaw)).ToLowerInvariant() else ""
        let descriptorValues = capsuleValues && descriptorShape
                               && exact descriptor "schema" "fsgg.fourd.source-capsule-descriptor/1"
                               && exact descriptor "purpose" "fourd-source-acquisition"
                               && exact descriptor "placementSha" placement && exact descriptor "runId" runId
                               && exact descriptor "runAttempt" runAttempt
                               && exact descriptor "runNonce" (getString admission "runNonce")
                               && exact descriptor "coordinationRepositoryId" "1346720714"
                               && exact descriptor "fourdRepositoryId" "1390568106"
                               && exact descriptor "sourceSha" sourceSha && exact descriptor "sourceTree" sourceTree
                               && exact descriptor "inventorySha256" inventory
                               && exact descriptor "sealerSha256" sealer
                               && exact descriptor "recipientPublicKeySha256" (getString capsule "recipientPublicKeySha256")
                               && exact descriptor "issuedAt" (getString admission "issuedAt")
                               && exact descriptor "expiresAt" (getString admission "expiresAt")
                               && hex 64 (getString descriptor "plaintextSha256")
                               && (match descriptor.TryGetProperty("plaintextBytes") with
                                   | true,value when value.ValueKind=JsonValueKind.Number ->
                                       let bytes = value.GetInt32()
                                       bytes >= 2 && bytes <= 16 * 1024 * 1024
                                   | _ -> false)
                               && descriptorDigest = getString capsule "descriptorSha256"
                               && nonce (getString admission "runNonce") && nonce (getString capsule "tag")
                               && (let name = getString capsule "name"
                                   not (isNull name) && Regex.IsMatch(name, "\\Afourd-source-[a-z0-9-]{8,64}\\.capsule\\.json\\z"))
        let capacity = decimal (getString admission "capacityRunId") && decimal (getString admission "capacityRunAttempt")
                       && decimal (getString admission "capacityArtifactId")
                       && hex 64 (getString admission "capacityArtifactDigest")
                       && hex 64 (getString admission "environmentReadbackSha256")
        let observed =
            exact admission "placementSha" placement && exact admission "runId" runId
            && exact admission "runAttempt" runAttempt
            && observedSha = sourceSha && observedTree = sourceTree && observedInventory = inventory
        if not closed then Error "typed-admission-shape-refused"
        elif not fixedBindings || not actors || not times || not descriptorValues || not capacity then Error "typed-admission-binding-refused"
        elif not observed then Error "typed-source-join-refused"
        else
            let canonical = JsonSerializer.SerializeToUtf8Bytes(admission)
            Ok(Convert.ToHexString(SHA256.HashData canonical).ToLowerInvariant())

    let validateRoot (context: JsonElement) (observed: JsonElement) placement tree runId =
        let keys = context.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
        let observedKeys = observed.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
        let exactInt name expected =
            match observed.TryGetProperty(name: string) with
            | true, value when value.ValueKind = JsonValueKind.Number -> value.TryGetInt32() = (true, expected)
            | _ -> false
        let exactBool name expected =
            match observed.TryGetProperty(name: string) with
            | true, value when value.ValueKind = JsonValueKind.True -> expected
            | true, value when value.ValueKind = JsonValueKind.False -> not expected
            | _ -> false
        if keys <> rootKeys then Error "typed-root-context-shape-refused"
        elif observedKeys <> rootObservationKeys then Error "typed-root-observation-shape-refused"
        elif not (hex 40 placement && hex 40 tree && decimal runId) then Error "typed-root-identity-refused"
        elif not (exact context "placementSha" placement && exact context "runId" runId && exact context "runAttempt" "2") then
            Error "typed-root-binding-refused"
        elif not (decimal (getString context "originalActorId") && decimal (getString context "triggeringActorId")) then
            Error "typed-root-actor-refused"
        elif not (hex 64 (getString context "capacityArtifactDigest") && hex 64 (getString context "environmentReadbackSha256")) then
            Error "typed-root-digest-refused"
        elif not (
            exact observed "placementRefSha" placement && exact observed "placementCommitSha" placement
            && exact observed "placementTree" tree && exact observed "reservationRunId" runId
            && exact observed "reservationRunAttempt" "1" && exact observed "reservationHeadSha" placement
            && exact observed "reservationStatus" "completed" && exact observed "reservationConclusion" "success"
            && exact observed "originalActorId" (getString context "originalActorId")
            && exact observed "triggeringActorId" (getString context "triggeringActorId")
            && getString observed "reservationArtifactId" <> getString context "capacityArtifactId"
            && decimal (getString observed "reservationArtifactId")
            && hex 64 (getString observed "reservationArtifactDigest")
            && exact observed "reservationResultRunId" runId && exact observed "reservationResultRunAttempt" "1"
            && exact observed "reservationResultPlacementSha" placement && exact observed "reservationResultPlacementTree" tree
            && exact observed "reservationResultOutcome" "awaiting-exact-admission"
            && exactBool "reservationResultQualified" false
            && exact observed "environmentReadbackSha256" (getString context "environmentReadbackSha256")
            && exact observed "capacityRunId" (getString context "capacityRunId")
            && exact observed "capacityRunAttempt" (getString context "capacityRunAttempt")
            && exact observed "capacityRunConclusion" "success"
            && exact observed "capacityArtifactId" (getString context "capacityArtifactId")
            && exact observed "capacityArtifactDigest" (getString context "capacityArtifactDigest")
            && exactInt "secretCount" 0 && exactInt "releaseCount" 0) then
            Error "typed-root-observation-refused"
        else
            let canonical = JsonSerializer.SerializeToUtf8Bytes(
                {| context = context; observed = observed; placementSha = placement; placementTree = tree; runId = runId |})
            Ok(Convert.ToHexString(SHA256.HashData canonical).ToLowerInvariant())
