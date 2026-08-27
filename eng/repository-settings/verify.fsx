open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

let fail code message =
    eprintfn "repository-settings: FAIL code=%s detail=%s" code message
    exit 1

let exactProperties code (expected: string list) (value: JsonElement) =
    if value.ValueKind <> JsonValueKind.Object then fail code "expected object"
    let actual = value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
    let wanted = Set.ofList expected
    if actual <> wanted then
        let expectedNames = String.concat "," (Set.toList wanted)
        let actualNames = String.concat "," (Set.toList actual)
        fail code $"property set mismatch expected={expectedNames} actual={actualNames}"

let rec writeCanonical (writer: Utf8JsonWriter) (value: JsonElement) =
    match value.ValueKind with
    | JsonValueKind.Object ->
        writer.WriteStartObject()
        value.EnumerateObject()
        |> Seq.sortBy _.Name
        |> Seq.iter (fun property ->
            writer.WritePropertyName(property.Name)
            writeCanonical writer property.Value)
        writer.WriteEndObject()
    | JsonValueKind.Array ->
        writer.WriteStartArray()
        value.EnumerateArray() |> Seq.iter (writeCanonical writer)
        writer.WriteEndArray()
    | JsonValueKind.String -> writer.WriteStringValue(value.GetString())
    | JsonValueKind.Number -> writer.WriteRawValue(value.GetRawText(), true)
    | JsonValueKind.True -> writer.WriteBooleanValue(true)
    | JsonValueKind.False -> writer.WriteBooleanValue(false)
    | JsonValueKind.Null -> writer.WriteNullValue()
    | kind -> fail "RS-JSON-KIND" $"unsupported JSON kind {kind}"

let canonicalBytes (value: JsonElement) =
    use stream = new MemoryStream()
    let options = JsonWriterOptions(Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
    use writer = new Utf8JsonWriter(stream, options)
    writeCanonical writer value
    writer.Flush()
    stream.ToArray()

let sha256 (bytes: byte array) =
    SHA256.HashData(bytes) |> Convert.ToHexString |> _.ToLowerInvariant()

let loadCanonical code path =
    let bytes = File.ReadAllBytes(path)
    use document = JsonDocument.Parse(bytes)
    let expected = Array.append (canonicalBytes document.RootElement) [| byte '\n' |]
    if bytes <> expected then fail code $"{path} is not compact canonical JSON with one trailing newline"
    JsonDocument.Parse(bytes)

let canonicalText (value: JsonElement) = canonicalBytes value |> Encoding.UTF8.GetString
let requireEqual code name (expected: JsonElement) (actual: JsonElement) =
    if canonicalText expected <> canonicalText actual then fail code $"{name} differs from desired state"

let requireString (code: string) (name: string) (expected: string) (value: JsonElement) =
    let actual = value.GetProperty(name).GetString()
    if actual <> expected then fail code $"{name} expected={expected} actual={actual}"

let hashPattern = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
let requireHash (code: string) (name: string) (value: string) =
    if not (hashPattern.IsMatch value) then fail code $"{name} must be lowercase SHA-256"

let validate (desiredPath: string) (receiptPath: string) =
    use desiredDoc = loadCanonical "RS-DESIRED-CANONICAL" desiredPath
    use receiptDoc = loadCanonical "RS-RECEIPT-CANONICAL" receiptPath
    let desired = desiredDoc.RootElement
    let receipt = receiptDoc.RootElement

    exactProperties "RS-DESIRED-SHAPE"
        [ "actions"; "checks"; "codeSecurityConfiguration"; "codeqlDefaultSetup"; "repository"; "rulesets";
          "schema"; "security"; "teams"; "unsupported" ] desired
    requireString "RS-DESIRED-SCHEMA" "schema" "fsgg.coordination.repository-settings-desired/2" desired
    exactProperties "RS-DESIRED-ACTIONS-SHAPE"
        [ "allowedActions"; "canApprovePullRequestReviews"; "defaultWorkflowPermissions"; "enabled";
          "githubOwnedAllowed"; "patternsAllowed"; "verifiedAllowed" ] (desired.GetProperty("actions"))
    exactProperties "RS-DESIRED-REPOSITORY-SHAPE"
        [ "allowAutoMerge"; "allowMergeCommit"; "allowRebaseMerge"; "allowSquashMerge"; "defaultBranch";
          "deleteBranchOnMerge"; "hasIssues"; "hasProjects"; "hasWiki"; "id"; "nameWithOwner"; "nodeId"; "visibility" ]
        (desired.GetProperty("repository"))
    exactProperties "RS-DESIRED-CODE-SECURITY-SHAPE"
        [ "associationStatus"; "configuration" ] (desired.GetProperty("codeSecurityConfiguration"))
    exactProperties "RS-DESIRED-CODE-SECURITY-CONFIGURATION-SHAPE"
        [ "advancedSecurity"; "codeScanningDefaultSetup"; "codeScanningDefaultSetupOptions";
          "codeScanningDelegatedAlertDismissal"; "dependabotAlerts"; "dependabotDelegatedAlertDismissal";
          "dependabotSecurityUpdates"; "dependencyGraph"; "dependencyGraphAutosubmitAction";
          "dependencyGraphAutosubmitLabeledRunners"; "enforcement"; "id"; "name";
          "privateVulnerabilityReporting"; "secretScanning"; "secretScanningDelegatedAlertDismissal";
          "secretScanningDelegatedBypass"; "secretScanningExtendedMetadata"; "secretScanningGenericSecrets";
          "secretScanningNonProviderPatterns"; "secretScanningPushProtection"; "secretScanningValidityChecks";
          "targetType" ]
        (desired.GetProperty("codeSecurityConfiguration").GetProperty("configuration"))
    exactProperties "RS-DESIRED-CODEQL-SHAPE"
        [ "languages"; "querySuite"; "runnerLabel"; "runnerType"; "schedule"; "state"; "threatModel" ]
        (desired.GetProperty("codeqlDefaultSetup"))
    exactProperties "RS-DESIRED-SECURITY-SHAPE"
        [ "dependabotAlerts"; "dependabotSecurityUpdates"; "dependencyGraph"; "privateVulnerabilityReporting";
          "secretScanning"; "secretScanningExtendedMetadata"; "secretScanningNonProviderPatterns";
          "secretScanningPushProtection"; "secretScanningValidityChecks" ] (desired.GetProperty("security"))
    for check in desired.GetProperty("checks").EnumerateArray() do
        exactProperties "RS-DESIRED-CHECK-SHAPE" [ "context"; "integrationId" ] check
    for ruleset in desired.GetProperty("rulesets").EnumerateArray() do
        match ruleset.GetProperty("target").GetString() with
        | "branch" ->
            exactProperties "RS-DESIRED-RULESET-SHAPE"
                [ "allowedMergeMethods"; "bypassActorCount"; "dismissStaleReviewsOnPush"; "doNotEnforceOnCreate";
                  "enforcement"; "include"; "name"; "requireCodeOwnerReview"; "requireLastPushApproval";
                  "requiredReviewCount"; "requiredReviewThreadResolution"; "ruleTypes"; "strictChecks"; "target" ] ruleset
        | "tag" ->
            exactProperties "RS-DESIRED-RULESET-SHAPE"
                [ "bypassActorCount"; "enforcement"; "include"; "name"; "requiredReviewCount"; "ruleTypes";
                  "strictChecks"; "target"; "updateAllowsFetchAndMerge" ] ruleset
        | target -> fail "RS-DESIRED-RULESET-SHAPE" $"unsupported ruleset target {target}"
    for team in desired.GetProperty("teams").EnumerateArray() do
        exactProperties "RS-DESIRED-TEAM-SHAPE" [ "permission"; "slug" ] team
    for unsupported in desired.GetProperty("unsupported").EnumerateArray() do
        exactProperties "RS-DESIRED-UNSUPPORTED-SHAPE" [ "httpStatus"; "status"; "surface" ] unsupported
    exactProperties "RS-RECEIPT-SHAPE"
        [ "actions"; "checks"; "codeSecurityConfiguration"; "codeqlDefaultSetup"; "desiredSha256"; "digest";
          "observedAt"; "operations"; "preStateSha256"; "repair"; "repository"; "rulesets"; "schema";
          "security"; "teams"; "unsupported" ] receipt
    requireString "RS-RECEIPT-SCHEMA" "schema" "fsgg.coordination.repository-settings-receipt/2" receipt

    for property in [ "actions"; "checks"; "codeSecurityConfiguration"; "codeqlDefaultSetup"; "repository"; "security"; "teams"; "unsupported" ] do
        requireEqual "RS-STATE-MISMATCH" property (desired.GetProperty(property)) (receipt.GetProperty(property))

    let desiredRules = desired.GetProperty("rulesets").EnumerateArray() |> Seq.toArray
    let receiptRules = receipt.GetProperty("rulesets").EnumerateArray() |> Seq.toArray
    if desiredRules.Length <> receiptRules.Length then fail "RS-RULESET-COUNT" "ruleset count differs"
    for index in 0 .. desiredRules.Length - 1 do
        let wanted = desiredRules[index]
        let actual = receiptRules[index]
        let semanticProperties =
            match wanted.GetProperty("target").GetString() with
            | "branch" ->
                [ "allowedMergeMethods"; "bypassActorCount"; "dismissStaleReviewsOnPush"; "doNotEnforceOnCreate";
                  "enforcement"; "include"; "name"; "requireCodeOwnerReview"; "requireLastPushApproval";
                  "requiredReviewCount"; "requiredReviewThreadResolution"; "ruleTypes"; "strictChecks"; "target" ]
            | "tag" ->
                [ "bypassActorCount"; "enforcement"; "include"; "name"; "requiredReviewCount"; "ruleTypes";
                  "strictChecks"; "target"; "updateAllowsFetchAndMerge" ]
            | target -> fail "RS-RULESET-SHAPE" $"unsupported desired ruleset target {target}"
        exactProperties "RS-RULESET-SHAPE" ("id" :: semanticProperties) actual
        if actual.GetProperty("id").GetInt64() <= 0L then fail "RS-RULESET-ID" "ruleset id must be positive"
        for property in semanticProperties do
            requireEqual "RS-RULESET-MISMATCH" property (wanted.GetProperty(property)) (actual.GetProperty(property))

    let desiredBytes = File.ReadAllBytes(desiredPath)
    let desiredDigest = receipt.GetProperty("desiredSha256").GetString()
    requireHash "RS-DESIRED-DIGEST" "desiredSha256" desiredDigest
    if desiredDigest <> sha256 desiredBytes then fail "RS-DESIRED-DIGEST" "desired contract digest mismatch"
    requireHash "RS-PRESTATE-DIGEST" "preStateSha256" (receipt.GetProperty("preStateSha256").GetString())

    let mutable observed = DateTimeOffset.MinValue
    let observedText = receipt.GetProperty("observedAt").GetString()
    if not (DateTimeOffset.TryParseExact(observedText, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                                        DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal, &observed)) then
        fail "RS-OBSERVED-AT" "observedAt must be canonical UTC seconds"

    let operations = receipt.GetProperty("operations").EnumerateArray() |> Seq.toArray
    if operations.Length = 0 then fail "RS-OPERATIONS" "at least one authoritative operation is required"
    let mutable operationNames = Set.empty
    for operation in operations do
        exactProperties "RS-OPERATION-SHAPE" [ "httpStatus"; "method"; "name"; "path"; "responseSha256"; "status" ] operation
        let name = operation.GetProperty("name").GetString()
        if String.IsNullOrWhiteSpace(name) || operationNames.Contains(name) then fail "RS-OPERATIONS" "operation names must be unique and nonempty"
        operationNames <- operationNames.Add(name)
        requireString "RS-OPERATION-STATUS" "status" "verified" operation
        let httpStatus = operation.GetProperty("httpStatus").GetInt32()
        if httpStatus < 200 || httpStatus > 299 then fail "RS-OPERATION-STATUS" $"operation {name} was not successful"
        if String.IsNullOrWhiteSpace(operation.GetProperty("method").GetString()) || String.IsNullOrWhiteSpace(operation.GetProperty("path").GetString()) then
            fail "RS-OPERATIONS" "operation method and path must be nonempty"
        requireHash "RS-RESPONSE-DIGEST" "responseSha256" (operation.GetProperty("responseSha256").GetString())

    let requiredOps =
        [ "repository"; "teams"; "actions-permissions"; "selected-actions"; "security"; "dependabot-alerts";
          "dependency-graph"; "code-security-association"; "code-security-configuration"; "codeql-default-setup";
          "private-vulnerability-reporting"; "main-ruleset"; "release-tag-ruleset" ] |> Set.ofList
    if operationNames <> requiredOps then fail "RS-OPERATIONS" "authoritative response operation set must be exact"
    let staticOperations =
        [ "actions-permissions", ("GET", "/repos/FS-GG/FS.GG.Coordination/actions/permissions", 200)
          "code-security-association", ("GET", "/orgs/FS-GG/code-security/configurations/17/repositories", 200)
          "code-security-configuration", ("GET", "/repos/FS-GG/FS.GG.Coordination/code-security-configuration", 200)
          "codeql-default-setup", ("GET", "/repos/FS-GG/FS.GG.Coordination/code-scanning/default-setup", 200)
          "dependabot-alerts", ("GET", "/repos/FS-GG/FS.GG.Coordination/vulnerability-alerts", 204)
          "dependency-graph", ("GET", "/repos/FS-GG/FS.GG.Coordination/dependency-graph/sbom", 200)
          "private-vulnerability-reporting", ("GET", "/repos/FS-GG/FS.GG.Coordination/private-vulnerability-reporting", 200)
          "repository", ("GET", "/repos/FS-GG/FS.GG.Coordination", 200)
          "security", ("GET", "/repos/FS-GG/FS.GG.Coordination", 200)
          "selected-actions", ("GET", "/repos/FS-GG/FS.GG.Coordination/actions/permissions/selected-actions", 200)
          "teams", ("GET", "/repos/FS-GG/FS.GG.Coordination/teams", 200) ]
    for name, (expectedMethod, expectedPath, expectedStatus) in staticOperations do
        let operation = operations |> Array.find (fun item -> item.GetProperty("name").GetString() = name)
        if operation.GetProperty("method").GetString() <> expectedMethod
           || operation.GetProperty("path").GetString() <> expectedPath
           || operation.GetProperty("httpStatus").GetInt32() <> expectedStatus then
            fail "RS-OPERATION-CONTRACT" $"{name} must be {expectedMethod} {expectedPath} status={expectedStatus}"
    let dependabotOperation = operations |> Array.find (fun item -> item.GetProperty("name").GetString() = "dependabot-alerts")
    if dependabotOperation.GetProperty("responseSha256").GetString() <> "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" then
        fail "RS-OPERATION-CONTRACT" "Dependabot-alerts 204 response must bind the empty response body"
    for ruleset in receiptRules do
        let name = if ruleset.GetProperty("target").GetString() = "branch" then "main-ruleset" else "release-tag-ruleset"
        let operation = operations |> Array.find (fun item -> item.GetProperty("name").GetString() = name)
        let rulesetId = ruleset.GetProperty("id").GetInt64()
        let expectedPath = $"/repos/FS-GG/FS.GG.Coordination/rulesets/{rulesetId}"
        if operation.GetProperty("method").GetString() <> "GET"
           || operation.GetProperty("path").GetString() <> expectedPath
           || operation.GetProperty("httpStatus").GetInt32() <> 200 then
            fail "RS-RULESET-RESPONSE" $"{name} must bind GET {expectedPath} status=200"
    if String.IsNullOrWhiteSpace(receipt.GetProperty("repair").GetString()) then fail "RS-REPAIR" "rollback or forward-repair guidance is required"

    let statedDigest = receipt.GetProperty("digest").GetString()
    requireHash "RS-RECEIPT-DIGEST" "digest" statedDigest
    let unsigned = JsonNode.Parse(receipt.GetRawText()).AsObject()
    unsigned.Remove("digest") |> ignore
    use unsignedDoc = JsonDocument.Parse(unsigned.ToJsonString())
    let computedDigest = sha256 (canonicalBytes unsignedDoc.RootElement)
    if statedDigest <> computedDigest then fail "RS-RECEIPT-DIGEST" "receipt self-digest mismatch"

    printfn "repository-settings: PASS receipt=%s digest=%s operations=%d" receiptPath statedDigest operations.Length

match fsi.CommandLineArgs |> Array.skip 1 |> Array.toList with
| [ desired; receipt ] -> validate desired receipt
| _ -> fail "RS-USAGE" "verify.fsx <desired.json> <receipt.json>"
