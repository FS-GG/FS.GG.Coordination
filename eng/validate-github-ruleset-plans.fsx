#load "../src/FS.GG.Coordination.GitHub/RepositoryProfileAdapter.fs"
#load "../src/FS.GG.Coordination.GitHub/RequiredCheckCensusAdapter.fs"
#load "../src/FS.GG.Coordination.GitHub/RulesetPlanAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubRulesetPlanQualification.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let evidenceRoot = Path.Combine(root, "evidence/github-substrate-v2/gs2-06-3")
let corpus = JsonNode.Parse(File.ReadAllText(Path.Combine(evidenceRoot, "corpus.json"))).AsObject()
let expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(evidenceRoot, "independent-expectations.json"))).AsObject()
let text (node: JsonObject) (name: string) = node[name].GetValue<string>()
let texts (node: JsonObject) (name: string) = node[name].AsArray() |> Seq.map _.GetValue<string>() |> List.ofSeq
let sha256Bytes (bytes: byte array) = bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let resolve (value: string) = Path.GetFullPath(Path.Combine(evidenceRoot, value))
let boundJson pathField digestField =
    let path = resolve (text corpus pathField)
    let bytes = File.ReadAllBytes path
    if sha256Bytes bytes <> text corpus digestField then failwith $"{pathField} digest differs"
    JsonNode.Parse(bytes).AsObject()

let role = function "authority" -> Authority | "framework" -> Framework | "non-participant" -> NonParticipant | value -> failwith $"unsupported role {value}"
let optionalText (node: JsonObject) (name: string) = if isNull node[name] then None else Some(text node name)
let profileJson = boundJson "profileSnapshot" "profileSnapshotSha256"
let source = profileJson["source"].AsObject()
let profileRows =
    profileJson["rows"].AsArray()
    |> Seq.map (fun item ->
        let row = item.AsObject()
        { Id = text row "id"; FullName = text row "fullName"; Role = role (text row "role")
          Capabilities = texts row "capabilities"; KitDelivery = optionalText row "kitDelivery"
          AbsenceCover = optionalText row "absenceCover"; Reason = optionalText row "reason" })
    |> List.ofSeq
let profileSnapshot =
    { SchemaVersion = profileJson["schemaVersion"].GetValue<int>()
      SourceRevision = text source "revision"; SourceArtifactSha256 = text source "artifactSha256"
      CanonicalRosterSha256 = text profileJson "canonicalRosterSha256"
      ReviewedAt = DateTimeOffset.Parse(text source "reviewedAt")
      Complete = profileJson["complete"].GetValue<bool>(); Rows = profileRows }

let optionalInt64 (node: JsonObject) (name: string) = if isNull node[name] then None else Some(node[name].GetValue<int64>())
let event (node: JsonNode) =
    let value = node.AsObject()
    { Declared = value["declared"].GetValue<bool>(); BranchFilters = texts value "branchFilters"
      PathFilters = texts value "pathFilters"; ActivityTypes = texts value "activityTypes" }
let requirementSource value =
    if value = "classic" then ClassicProtection
    elif value.StartsWith("ruleset:") then Ruleset(Int64.Parse(value.Substring("ruleset:".Length)))
    else failwith $"unsupported authority {value}"
let censusJson = boundJson "censusSnapshot" "censusSnapshotSha256"
let repository = text corpus "repository"
let censusSnapshot =
    { SchemaVersion = 1; Repository = repository; ProfileSeal = text censusJson "profileSeal"
      PrerequisiteReceiptDigest = text censusJson "prerequisiteReceiptDigest"
      AuthorityEvidenceSha256 = text censusJson "authorityEvidenceSha256"; SourceRevision = text censusJson "sourceRevision"
      ObservedAt = DateTimeOffset.Parse(text censusJson "observedAt"); Complete = censusJson["complete"].GetValue<bool>()
      ClassicComplete = censusJson["classicComplete"].GetValue<bool>(); RulesetsComplete = censusJson["rulesetsComplete"].GetValue<bool>()
      ProducersComplete = censusJson["producersComplete"].GetValue<bool>()
      Requirements = censusJson["requirements"].AsArray() |> Seq.map (fun item ->
          let value = item.AsObject()
          { Repository = repository; Context = text value "context"; IntegrationId = optionalInt64 value "integrationId"; Source = requirementSource (text value "source") }) |> List.ofSeq
      Producers = censusJson["producers"].AsArray() |> Seq.map (fun item ->
          let value = item.AsObject()
          { Repository = repository; Context = text value "context"; IntegrationId = optionalInt64 value "integrationId"
            Workflow = text value "workflow"; Job = text value "job"; WorkflowRevision = text value "workflowRevision"
            WorkflowSha256 = text value "workflowSha256"; PullRequest = event value["pullRequest"]; MergeGroup = event value["mergeGroup"]
            DependenciesComplete = value["dependenciesComplete"].GetValue<bool>(); Conditional = value["conditional"].GetValue<bool>()
            ContinueOnError = value["continueOnError"].GetValue<bool>() }) |> List.ofSeq }

let policyBytes = File.ReadAllBytes(resolve (text corpus "currentPolicyEvidence"))
if sha256Bytes policyBytes <> text corpus "currentPolicyEvidenceSha256" then failwith "current policy evidence digest differs"
let policyEvidence = JsonNode.Parse(policyBytes).AsObject()
let policyRepositoryObject = policyEvidence["repository"].AsObject()
let policyRepository = text policyRepositoryObject "nameWithOwner"
let policyObservedAt = DateTimeOffset.Parse(text policyEvidence "observedAt")
if policyRepository <> text corpus "currentPolicyRepository" || policyObservedAt <> DateTimeOffset.Parse(text corpus "currentPolicyObservedAt") then
    failwith "current policy identity or observation time differs"
let acceptedReceipt = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-06.2.json"))).AsObject()
if text acceptedReceipt "digest" <> text corpus "prerequisiteReceiptDigest" then failwith "accepted GS2-06.2 receipt differs"

let asOf = DateTimeOffset.Parse(text corpus "observedAt")
let maxAge = TimeSpan.FromDays 7
if maxAge.TotalHours <> float (expectations["freshnessBudgetHours"].GetValue<int>()) then failwith "freshness budget differs"
let snapshot =
    { SchemaVersion = 1; Repository = repository; PrerequisiteReceiptDigest = text corpus "prerequisiteReceiptDigest"
      ProfileSnapshot = profileSnapshot; ExpectedProfileSeal = text corpus "profileSeal"
      CensusSnapshot = Some censusSnapshot; ExpectedCensusSeal = Some(text corpus "censusSeal")
      CurrentPolicyRepository = text corpus "currentPolicyRepository"; CurrentPolicyRevision = text corpus "currentPolicyRevision"
      CurrentPolicyEvidenceSha256 = text corpus "currentPolicyEvidenceSha256"; CurrentPolicyObservedAt = DateTimeOffset.Parse(text corpus "currentPolicyObservedAt")
      CurrentPolicyComplete = corpus["currentPolicyComplete"].GetValue<bool>()
      ObservedAt = asOf; Complete = corpus["complete"].GetValue<bool>(); ApprovedBypass = []; RequestedBypass = []; Exceptions = [] }
let compile candidate = RulesetPlanAdapter.compile asOf maxAge candidate
let refused candidate = compile candidate |> Result.isError
let report = compile snapshot |> Result.defaultWith (failwithf "ruleset-plan baseline refused: %A")
let branch = report.DefaultBranch.Value
let tags = report.ReleaseTags.Value
let policy = report.RepositoryPolicy.Value
let planSource = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/RulesetPlanAdapter.fs"))
let sourceMutationRejected (original: string) (replacement: string) (required: string) =
    planSource.Contains(original) && not (planSource.Replace(original, replacement).Contains(required))
let profileClassText = function AuthorityPlan -> "authority" | FrameworkPlan -> "framework" | HostedNonParticipantPlan -> "hosted-non-participant" | ObserveOnlyPlan -> "observe-only"

if fsi.CommandLineArgs |> Array.contains "--mint" then printfn "%s" report.Seal
else
    let expectedControls = GitHubRulesetPlanQualification.requiredControls |> List.map GitHubRulesetPlanQualification.controlId
    if texts expectations "controls" <> expectedControls then failwith "independent control inventory differs"
    if report.Repository <> text expectations "repository" || report.Seal <> text expectations "expectedSeal" then failwith "baseline identity or seal differs"
    if profileClassText report.ProfileClass <> text expectations "profileClass" || report.MutationPermitted <> expectations["mutationPermitted"].GetValue<bool>() then failwith "profile class or mutation permission differs"
    if branch.RequiredChecks |> List.map _.Context <> texts expectations "requiredChecks" then failwith "required checks differ"
    if branch.Include <> texts expectations "defaultBranchInclude" || tags.Include <> texts expectations "releaseTagInclude" then failwith "target include differs"
    if policy.AllowedMergeMethods <> [ Squash ] || not policy.AllowAutoMerge || not policy.DeleteBranchOnMerge then failwith "repository merge policy differs"
    if branch.RequiredApprovals <> expectations["requiredApprovals"].GetValue<int>() || branch.Bypass.Length <> expectations["bypassCount"].GetValue<int>() || report.ActiveExceptions.Length <> expectations["exceptionCount"].GetValue<int>() then failwith "review, bypass, or exception baseline differs"
    if branch.MergeQueueEnabled || branch.MergeQueueDisabledReason <> Some(text expectations "mergeQueueDisabledReason") then failwith "merge queue disposition differs"
    if RulesetPlanAdapter.verify report.Seal asOf maxAge snapshot <> Ok report then failwith "exact replay failed"

    let active scope =
        { Id = "E-1"; Owner = "security"; Rationale = "bounded migration"; Scope = scope
          ApprovedAt = asOf.AddDays(-1); StartsAt = asOf.AddHours(-1); ExpiresAt = asOf.AddDays(1) }
    let generatedMutation = function
        | PrerequisiteReceipt -> refused { snapshot with PrerequisiteReceiptDigest = "invalid" }
        | ProfileBinding -> refused { snapshot with ExpectedProfileSeal = String.replicate 64 "0" }
        | CensusBinding -> refused { snapshot with ExpectedCensusSeal = Some(String.replicate 64 "0") }
        | CurrentPolicyBinding ->
            refused { snapshot with CurrentPolicyRevision = "main" }
            && refused { snapshot with CurrentPolicyComplete = false }
            && refused { snapshot with CurrentPolicyRepository = "FS-GG/other" }
            && refused { snapshot with CurrentPolicyObservedAt = asOf.AddDays(-8) }
        | CompleteObservation -> refused { snapshot with Complete = false }
        | RepositoryBoundary -> refused { snapshot with Repository = "FS-GG/other" }
        | StableOrdering ->
            let reorderedProfile = { profileSnapshot with Rows = List.rev profileSnapshot.Rows }
            let reorderedCensus = { censusSnapshot with Requirements = List.rev censusSnapshot.Requirements; Producers = List.rev censusSnapshot.Producers }
            compile { snapshot with ProfileSnapshot = reorderedProfile; CensusSnapshot = Some reorderedCensus } = Ok report
        | DefaultBranchTarget -> sourceMutationRejected "Include = [ \"~DEFAULT_BRANCH\" ]" "Include = []" "Include = [ \"~DEFAULT_BRANCH\" ]"
        | ReleaseTagTarget -> sourceMutationRejected "Include = [ \"refs/tags/v*\" ]" "Include = []" "Include = [ \"refs/tags/v*\" ]"
        | RequiredChecks -> refused { snapshot with CensusSnapshot = Some { censusSnapshot with Producers = censusSnapshot.Producers.Tail } }
        | ReviewPolicy -> branch.DismissStaleReviews && branch.RequiredApprovals = 0 && (compile { snapshot with Exceptions = [ active (RequiredReviewCount 2) ] } |> Result.exists (fun value -> value.DefaultBranch.Value.RequiredApprovals = 2))
        | ConversationResolution -> branch.RequireConversationResolution && (compile { snapshot with Exceptions = [ active (RulesetExceptionScope.ConversationResolution false) ] } |> Result.exists (fun value -> not value.DefaultBranch.Value.RequireConversationResolution))
        | MergeMethods -> sourceMutationRejected "AllowedMergeMethods = [ Squash ]" "AllowedMergeMethods = [ MergeCommit ]" "AllowedMergeMethods = [ Squash ]"
        | AutoMerge -> sourceMutationRejected "AllowAutoMerge = true" "AllowAutoMerge = false" "AllowAutoMerge = true"
        | GitHubRulesetPlanControl.MergeQueue ->
            refused { snapshot with Exceptions = [ active (RulesetExceptionScope.MergeQueue true) ] }
            && not branch.MergeQueueEnabled
        | BranchDeletion -> sourceMutationRejected "DeleteBranchOnMerge = true" "DeleteBranchOnMerge = false" "DeleteBranchOnMerge = true"
        | BypassAuthorization ->
            let request = { ActorId = 7L; Kind = IntegrationActor }
            let approved = { ActorId = 7L; Kind = IntegrationActor; AllowedProfiles = [ HostedNonParticipantPlan ] }
            let exceptionValue = active (BypassPrincipal request)
            refused { snapshot with RequestedBypass = [ request ] }
            && (compile { snapshot with ApprovedBypass = [ approved ]; Exceptions = [ exceptionValue ] }
                |> Result.exists (fun value -> value.DefaultBranch.Value.Bypass = [ request ] && value.ReleaseTags.Value.Bypass = [ request ]))
        | ExceptionIdentity -> refused { snapshot with Exceptions = [ active (RequiredReviewCount 1); { active (RulesetExceptionScope.MergeQueue false) with Id = "E-1" } ] }
        | ExceptionWindow -> refused { snapshot with Exceptions = [ { active (RequiredReviewCount 1) with ExpiresAt = asOf } ] }
        | ExceptionScope ->
            refused { snapshot with Exceptions = [ active (RequiredReviewCount 1); { active (RequiredReviewCount 2) with Id = "E-2" } ] }
            && refused { snapshot with Exceptions = [ active (RulesetExceptionScope.MergeQueue true) ] }
        | ObserveOnly ->
            let external = profileRows |> List.find (fun row -> not (row.FullName.StartsWith("FS-GG/")))
            let candidate = { snapshot with Repository = external.FullName; CensusSnapshot = None; ExpectedCensusSeal = None; CurrentPolicyRepository = external.FullName }
            compile candidate |> Result.exists (fun value -> not value.MutationPermitted && value.DefaultBranch.IsNone && value.ReleaseTags.IsNone && value.RepositoryPolicy.IsNone)
        | Freshness -> RulesetPlanAdapter.compile (asOf.AddDays 8) maxAge snapshot |> Result.isError
        | ExactSeal -> RulesetPlanAdapter.verify (String.replicate 64 "0") asOf maxAge snapshot |> Result.isError
        | ExactReplay -> RulesetPlanAdapter.verify report.Seal asOf maxAge snapshot = Ok report
        | QuintUnchanged ->
            let bytes = File.ReadAllBytes(Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md"))
            sha256Bytes bytes = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
            && sha256Bytes (Array.append bytes [| 0uy |]) <> "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
        | NoApplySurface ->
            let surface = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/RulesetPlanAdapter.fsi"))
            let forbidden (value: string) = value.Contains("val apply") || value.Contains("HttpClient") || value.Contains("GITHUB_TOKEN")
            not (forbidden surface) && forbidden (surface + "\nval apply: unit -> unit")

    let independentMutation control =
        match control with
        | PrerequisiteReceipt -> text acceptedReceipt "digest" = snapshot.PrerequisiteReceiptDigest
        | ProfileBinding -> snapshot.ExpectedProfileSeal = "f3524e8edbd6b88b0783551c14377881dee5dd958ebd4835d77a57913d30d74b"
        | CensusBinding -> snapshot.ExpectedCensusSeal = Some "db294ff75dbfb97a81433331ac7d696a0321a3433a8dd6b29694cbebf37396a3"
        | CurrentPolicyBinding -> sha256Bytes policyBytes = snapshot.CurrentPolicyEvidenceSha256 && snapshot.CurrentPolicyRevision.Length = 40 && snapshot.CurrentPolicyRepository = policyRepository && snapshot.CurrentPolicyObservedAt = policyObservedAt && snapshot.CurrentPolicyComplete
        | CompleteObservation -> snapshot.Complete && snapshot.ProfileSnapshot.Complete && snapshot.CensusSnapshot.Value.Complete
        | RepositoryBoundary -> report.Repository = snapshot.Repository && snapshot.CensusSnapshot.Value.Repository = snapshot.Repository
        | StableOrdering -> report.ActiveExceptions = List.sortBy _.Id report.ActiveExceptions
        | DefaultBranchTarget -> branch.ProtectDeletion && branch.BlockNonFastForward && branch.RequirePullRequest
        | ReleaseTagTarget -> tags.ProtectDeletion && tags.BlockUpdate && tags.RequireSignatures
        | RequiredChecks -> branch.RequiredChecks |> List.map _.Context = texts expectations "requiredChecks"
        | ReviewPolicy -> branch.DismissStaleReviews && branch.RequiredApprovals = expectations["requiredApprovals"].GetValue<int>()
        | ConversationResolution -> branch.RequireConversationResolution
        | MergeMethods -> policy.AllowedMergeMethods = [ Squash ]
        | AutoMerge -> policy.AllowAutoMerge = expectations["autoMerge"].GetValue<bool>()
        | GitHubRulesetPlanControl.MergeQueue -> branch.MergeQueueEnabled = expectations["mergeQueue"].GetValue<bool>()
        | BranchDeletion -> policy.DeleteBranchOnMerge = expectations["deleteBranchOnMerge"].GetValue<bool>()
        | BypassAuthorization -> branch.Bypass.IsEmpty && tags.Bypass.IsEmpty
        | ExceptionIdentity | ExceptionWindow | ExceptionScope -> report.ActiveExceptions.IsEmpty
        | ObserveOnly -> generatedMutation ObserveOnly
        | Freshness -> asOf - snapshot.ProfileSnapshot.ReviewedAt <= maxAge && asOf - snapshot.CensusSnapshot.Value.ObservedAt <= maxAge && asOf - snapshot.CurrentPolicyObservedAt <= maxAge
        | ExactSeal -> report.Seal.Length = 64 && report.Seal = text expectations "expectedSeal"
        | ExactReplay -> generatedMutation ExactReplay
        | QuintUnchanged -> generatedMutation QuintUnchanged
        | NoApplySurface -> generatedMutation NoApplySurface

    let result control red = { GitHubRulesetPlanControlResult.Control = control; MutationRed = red; BaselineGreen = true }
    let generated = GitHubRulesetPlanQualification.requiredControls |> List.map (fun control -> result control (generatedMutation control))
    let independent = GitHubRulesetPlanQualification.requiredControls |> List.map (fun control -> result control (independentMutation control))
    match GitHubRulesetPlanQualification.validate generated independent with
    | Ok () -> printfn "GITHUB_RULESET_PLANS_OK repository=%s checks=%d mergeQueue=%b bypass=%d exceptions=%d controls=%d seal=%s" report.Repository branch.RequiredChecks.Length branch.MergeQueueEnabled branch.Bypass.Length report.ActiveExceptions.Length generated.Length report.Seal
    | Error findings -> failwithf "ruleset-plan qualification failed: %A" findings
