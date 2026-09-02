module FS.GG.Coordination.GitHubRulesetPlanTests

open System
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private revision = String.replicate 40 "a"
let private digest = String.replicate 64 "b"
let private observedAt = DateTimeOffset.Parse("2026-09-02T00:00:00Z")
let private maxAge = TimeSpan.FromHours 1
let private authority =
    { Id = "authority"; FullName = "FS-GG/.github"; Role = Authority; Capabilities = [ "labels" ]
      KitDelivery = None; AbsenceCover = None; Reason = None }
let private repository =
    { Id = "example"; FullName = "FS-GG/example"; Role = Framework; Capabilities = [ "labels"; "coordination-kit" ]
      KitDelivery = Some "package"; AbsenceCover = Some "required"; Reason = None }
let private roster rows =
    { SchemaVersion = 1; SourceRevision = revision; SourceArtifactSha256 = digest
      CanonicalRosterSha256 = RepositoryProfileAdapter.canonicalRosterDigest rows
      ReviewedAt = observedAt; Complete = true; Rows = rows }
let private profileSnapshot = roster [ authority; repository ]
let private profileReport =
    RepositoryProfileAdapter.compile observedAt maxAge profileSnapshot |> Result.defaultWith (failwithf "%A")
let private event declared = { Declared = declared; BranchFilters = []; PathFilters = []; ActivityTypes = [] }
let private requirement context =
    { Repository = repository.FullName; Context = context; IntegrationId = Some 15368L; Source = Ruleset 42L }
let private producer context mergeGroup =
    { Repository = repository.FullName; Context = context; IntegrationId = Some 15368L
      Workflow = ".github/workflows/ci.yml"; Job = context; WorkflowRevision = revision; WorkflowSha256 = digest
      PullRequest = event true; MergeGroup = event mergeGroup; DependenciesComplete = true; Conditional = false; ContinueOnError = false }
let private censusSnapshot mergeGroup =
    { SchemaVersion = 1; Repository = repository.FullName; ProfileSeal = profileReport.Seal
      PrerequisiteReceiptDigest = digest; AuthorityEvidenceSha256 = digest; SourceRevision = revision
      ObservedAt = observedAt; Complete = true; ClassicComplete = true; RulesetsComplete = true; ProducersComplete = true
      Requirements = [ requirement "build"; requirement "policy" ]
      Producers = [ producer "build" mergeGroup; producer "policy" mergeGroup ] }
let private censusReport mergeGroup =
    RequiredCheckCensusAdapter.compile observedAt maxAge (censusSnapshot mergeGroup) |> Result.defaultWith (failwithf "%A")
let private snapshot mergeGroup =
    { SchemaVersion = 1; Repository = repository.FullName; PrerequisiteReceiptDigest = digest
      ProfileSnapshot = profileSnapshot; ExpectedProfileSeal = profileReport.Seal
      CensusSnapshot = Some(censusSnapshot mergeGroup); ExpectedCensusSeal = Some((censusReport mergeGroup).Seal)
      CurrentPolicyRepository = repository.FullName; CurrentPolicyRevision = revision; CurrentPolicyEvidenceSha256 = digest
      CurrentPolicyObservedAt = observedAt; CurrentPolicyComplete = true; ObservedAt = observedAt; Complete = true
      ApprovedBypass = []; RequestedBypass = []; Exceptions = [] }
let private compile value = RulesetPlanAdapter.compile observedAt maxAge value
let private findings = function Error values -> values | Ok _ -> []

[<Fact>]
let ``administered profile compiles the complete secure desired target`` () =
    let report = compile (snapshot false) |> Result.defaultWith (failwithf "%A")
    Assert.Equal(FrameworkPlan, report.ProfileClass)
    Assert.True(report.MutationPermitted)
    Assert.Equal(repository.FullName, report.CurrentPolicyRepository)
    Assert.Equal(revision, report.CurrentPolicyRevision)
    Assert.Equal(digest, report.CurrentPolicyEvidenceSha256)
    Assert.Equal(observedAt, report.CurrentPolicyObservedAt)
    Assert.True(report.CurrentPolicyComplete)
    let branch = report.DefaultBranch.Value
    Assert.Equal<string list>([ "~DEFAULT_BRANCH" ], branch.Include)
    Assert.True(branch.ProtectDeletion)
    Assert.True(branch.BlockNonFastForward)
    Assert.True(branch.RequirePullRequest)
    Assert.True(branch.DismissStaleReviews)
    Assert.Equal(0, branch.RequiredApprovals)
    Assert.True(branch.RequireConversationResolution)
    Assert.True(branch.StrictChecks)
    Assert.Equal<string list>([ "build"; "policy" ], branch.RequiredChecks |> List.map _.Context)
    Assert.False(branch.MergeQueueEnabled)
    Assert.Equal(Some "required-check-census-not-merge-group-ready", branch.MergeQueueDisabledReason)
    let tags = report.ReleaseTags.Value
    Assert.Equal<string list>([ "refs/tags/v*" ], tags.Include)
    Assert.True(tags.ProtectDeletion && tags.BlockNonFastForward && tags.BlockUpdate && tags.RequireSignatures)
    let policy = report.RepositoryPolicy.Value
    Assert.Equal<RulesetMergeMethod list>([ Squash ], policy.AllowedMergeMethods)
    Assert.True(policy.AllowAutoMerge)
    Assert.True(policy.DeleteBranchOnMerge)

[<Fact>]
let ``merge queue is enabled only by a census ready on both routes`` () =
    let notReady = compile (snapshot false) |> Result.defaultWith (failwithf "%A")
    let ready = compile (snapshot true) |> Result.defaultWith (failwithf "%A")
    Assert.False(notReady.DefaultBranch.Value.MergeQueueEnabled)
    Assert.True(ready.DefaultBranch.Value.MergeQueueEnabled)
    Assert.Null(ready.DefaultBranch.Value.MergeQueueDisabledReason |> Option.toObj)
    Assert.NotEqual(notReady.Seal, ready.Seal)

[<Fact>]
let ``profile census and current-policy source substitutions fail exact verification`` () =
    let baseline = snapshot false
    let report = compile baseline |> Result.defaultWith (failwithf "%A")
    Assert.Equal(Ok report, RulesetPlanAdapter.verify report.Seal observedAt maxAge baseline)
    let changedPolicy = { baseline with CurrentPolicyRevision = String.replicate 40 "c" }
    Assert.Equal(Error [ AlteredRulesetPlanSeal ], RulesetPlanAdapter.verify report.Seal observedAt maxAge changedPolicy)
    let changedCensus = { baseline with ExpectedCensusSeal = Some digest }
    Assert.Contains(InvalidRulesetPlanBinding "censusReport", compile changedCensus |> findings)

[<Fact>]
let ``ordering of checks bypass authorities requests and exceptions is stable`` () =
    let principal id = { ActorId = id; Kind = TeamActor }
    let approved id = { ActorId = id; Kind = TeamActor; AllowedProfiles = [ FrameworkPlan ] }
    let exceptionValue id actor =
        { Id = id; Owner = "security"; Rationale = "bounded migration"; Scope = BypassPrincipal actor
          ApprovedAt = observedAt.AddDays(-1); StartsAt = observedAt.AddHours(-1); ExpiresAt = observedAt.AddDays(1) }
    let first =
        { snapshot true with
            ApprovedBypass = [ approved 2L; approved 1L ]
            RequestedBypass = [ principal 2L; principal 1L ]
            Exceptions = [ exceptionValue "B" (principal 2L); exceptionValue "A" (principal 1L) ] }
    let second =
        { first with
            ApprovedBypass = List.rev first.ApprovedBypass
            RequestedBypass = List.rev first.RequestedBypass
            Exceptions = List.rev first.Exceptions
            CensusSnapshot = first.CensusSnapshot |> Option.map (fun value -> { value with Requirements = List.rev value.Requirements; Producers = List.rev value.Producers }) }
    Assert.Equal(compile first, compile second)

[<Fact>]
let ``bypass is deny by default and exact kind plus profile class`` () =
    let requested = { ActorId = 7L; Kind = IntegrationActor }
    Assert.Contains(UnauthorizedRulesetBypassPrincipal 7L, compile { snapshot true with RequestedBypass = [ requested ] } |> findings)
    let wrongKind = { ActorId = 7L; Kind = TeamActor; AllowedProfiles = [ FrameworkPlan ] }
    Assert.Contains(UnauthorizedRulesetBypassPrincipal 7L, compile { snapshot true with ApprovedBypass = [ wrongKind ]; RequestedBypass = [ requested ] } |> findings)
    let allowed = { ActorId = 7L; Kind = IntegrationActor; AllowedProfiles = [ FrameworkPlan ] }
    Assert.True(compile { snapshot true with ApprovedBypass = [ allowed ]; RequestedBypass = [ requested ] } |> Result.isOk)

[<Theory>]
[<InlineData("expired")>]
[<InlineData("future")>]
[<InlineData("overlong")>]
[<InlineData("empty-owner")>]
let ``exceptions must be identified current bounded and owned`` mutation =
    let baseline: RulesetPlanException =
        { Id = "E-1"; Owner = "security"; Rationale = "bounded migration"; Scope = RulesetExceptionScope.MergeQueue false
          ApprovedAt = observedAt.AddDays(-1); StartsAt = observedAt.AddHours(-1); ExpiresAt = observedAt.AddDays(1) }
    let changed =
        match mutation with
        | "expired" -> { baseline with ExpiresAt = observedAt }
        | "future" -> { baseline with StartsAt = observedAt.AddMinutes 1 }
        | "overlong" -> { baseline with ExpiresAt = baseline.ApprovedAt.AddDays 31 }
        | "empty-owner" -> { baseline with Owner = "" }
        | value -> failwith value
    Assert.True(compile { snapshot true with Exceptions = [ changed ] } |> Result.isError)

[<Fact>]
let ``bounded exceptions project into the target and authorization is seal-bound`` () =
    let review: RulesetPlanException =
        { Id = "E-1"; Owner = "security"; Rationale = "bounded migration"; Scope = RequiredReviewCount 2
          ApprovedAt = observedAt.AddDays(-1); StartsAt = observedAt.AddHours(-1); ExpiresAt = observedAt.AddDays(1) }
    let report = compile { snapshot false with Exceptions = [ review ] } |> Result.defaultWith (failwithf "%A")
    Assert.Equal(2, report.DefaultBranch.Value.RequiredApprovals)
    let approved = { ActorId = 7L; Kind = IntegrationActor; AllowedProfiles = [ FrameworkPlan ] }
    let authorityOnly = compile { snapshot false with ApprovedBypass = [ approved ] } |> Result.defaultWith (failwithf "%A")
    Assert.NotEqual(report.Seal, authorityOnly.Seal)
    let duplicate = { review with Id = "E-2"; Rationale = "second override" }
    Assert.True(compile { snapshot false with Exceptions = [ review; duplicate ] } |> Result.isError)

[<Fact>]
let ``exceptions cannot enable queue before census readiness and authorized bypass scope projects`` () =
    let queue: RulesetPlanException =
        { Id = "E-Q"; Owner = "security"; Rationale = "unsafe request"; Scope = RulesetExceptionScope.MergeQueue true
          ApprovedAt = observedAt.AddDays(-1); StartsAt = observedAt.AddHours(-1); ExpiresAt = observedAt.AddDays(1) }
    Assert.True(compile { snapshot false with Exceptions = [ queue ] } |> Result.isError)
    let principal = { ActorId = 7L; Kind = IntegrationActor }
    let approved = { ActorId = 7L; Kind = IntegrationActor; AllowedProfiles = [ FrameworkPlan ] }
    let bypass = { queue with Id = "E-B"; Rationale = "bounded integration migration"; Scope = BypassPrincipal principal }
    let report = compile { snapshot false with ApprovedBypass = [ approved ]; Exceptions = [ bypass ] } |> Result.defaultWith (failwithf "%A")
    Assert.Equal<RequestedRulesetBypassPrincipal list>([ principal ], report.DefaultBranch.Value.Bypass)
    Assert.Equal<RequestedRulesetBypassPrincipal list>([ principal ], report.ReleaseTags.Value.Bypass)

[<Fact>]
let ``current policy binding is complete repository exact and fresh`` () =
    let baseline = snapshot false
    Assert.True(compile { baseline with CurrentPolicyComplete = false } |> Result.isError)
    Assert.True(compile { baseline with CurrentPolicyRepository = "FS-GG/other" } |> Result.isError)
    Assert.True(compile { baseline with CurrentPolicyObservedAt = observedAt.AddHours(-2) } |> Result.isError)

[<Fact>]
let ``external profile compiles only an observe-only disposition`` () =
    let external =
        { Id = "external"; FullName = "someone/example"; Role = NonParticipant; Capabilities = []
          KitDelivery = None; AbsenceCover = None; Reason = Some "external authority" }
    let externalRoster = roster [ authority; external ]
    let externalReport = RepositoryProfileAdapter.compile observedAt maxAge externalRoster |> Result.defaultWith (failwithf "%A")
    let externalSnapshot =
        { snapshot true with Repository = external.FullName; ProfileSnapshot = externalRoster; ExpectedProfileSeal = externalReport.Seal
                             CensusSnapshot = None; ExpectedCensusSeal = None; CurrentPolicyRepository = external.FullName }
    let report = compile externalSnapshot |> Result.defaultWith (failwithf "%A")
    Assert.Equal(ObserveOnlyPlan, report.ProfileClass)
    Assert.False(report.MutationPermitted)
    Assert.True(report.DefaultBranch.IsNone && report.ReleaseTags.IsNone && report.RepositoryPolicy.IsNone)

[<Fact>]
let ``partial stale and cross-repository evidence refuses`` () =
    Assert.True(compile { snapshot true with Complete = false } |> Result.isError)
    Assert.True(RulesetPlanAdapter.compile (observedAt.AddHours 2) maxAge (snapshot true) |> Result.isError)
    let crossed = (censusSnapshot true) |> fun value -> { value with Repository = "FS-GG/other" }
    Assert.True(compile { snapshot true with CensusSnapshot = Some crossed } |> Result.isError)

[<Fact>]
let ``qualification requires every generated and independent inversion`` () =
    let passing =
        GitHubRulesetPlanQualification.requiredControls
        |> List.map (fun control ->
            { GitHubRulesetPlanControlResult.Control = control
              ControlPassed = true
              BaselineGreen = true })
    Assert.Equal(Ok(), GitHubRulesetPlanQualification.validate passing passing)
    let broken = passing |> List.map (fun value -> if value.Control = GitHubRulesetPlanControl.MergeQueue then { value with ControlPassed = false } else value)
    match GitHubRulesetPlanQualification.validate passing broken with
    | Ok _ -> Assert.Fail("merge-queue mutation survived independent qualification")
    | Error findings -> Assert.Contains("RP-CONTROL-FAILED", findings |> List.map _.Code)
