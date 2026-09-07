module FS.GG.Coordination.GitHubQueueSandboxTests

open System
open Xunit
open FS.GG.Coordination.Qualification.Contracts
module Sandbox = FS.GG.Coordination.Qualification.Contracts.GitHubQueueSandbox

let private sha character = String.replicate 40 character
let private digest character = String.replicate 64 character
let private originalChecks = [ "architecture" ]
let private grownChecks = [ "architecture"; "queue-growth" ]

let private pilotFacts () =
    { Repository = Sandbox.repository
      RepositoryId = Sandbox.repositoryId
      IsProduction = false
      ProviderCapability = QueueProviderCapability.Supported
      PreVisibility = "private"
      RepositorySecretCount = 0
      EnvironmentSecretCount = 0
      PilotId = "gs2-07-6-pilot"
      CandidateSha = sha "1"
      CurrentCandidateSha = sha "1"
      MergeGroupHeadSha = sha "2"
      BaseRef = "refs/heads/main"
      ObservedBaseSha = sha "3"
      CurrentBaseSha = sha "4"
      ReevaluatedBaseSha = sha "4"
      BaseObservationRevision = 10L
      CurrentBaseObservationRevision = 11L
      OriginalRequiredChecks = originalChecks
      CurrentRequiredChecks = grownChecks
      CheckResults = grownChecks |> List.map (fun name -> { Name = name; HeadSha = sha "2"; EventName = "merge_group"; Conclusion = QueueCheckConclusion.Success })
      ObservedClaimGeneration = 5565937139L
      CurrentClaimGeneration = 5565937139L
      ObservedReviewDigest = digest "a"
      CurrentReviewDigest = digest "a"
      ObservedDependencyDigest = digest "b"
      CurrentDependencyDigest = digest "b"
      ObservedReleaseObligationsMet = true
      CurrentReleaseObligationsMet = true
      ObservedSettingsDigest = digest "c"
      CurrentSettingsDigest = digest "c"
      AdmittedAtUnixSeconds = 100L
      ExpiresAtUnixSeconds = 200L
      EvaluatedAtUnixSeconds = 150L }

let private recoveryFacts pilotSeal =
    let effects =
        [ "visibility-public"; "base-advance"; "required-check-growth"; "queue-admission" ]
        |> List.mapi (fun index operation -> { OperationId = operation; Attempt = 1; ResultDigest = digest (string (index + 1)) })
    { Repository = Sandbox.repository
      RepositoryId = Sandbox.repositoryId
      PilotSeal = pilotSeal
      DurableCheckpointDigest = digest "d"
      ResumeCheckpointDigest = digest "d"
      Interrupted = true
      FailedStepInjected = true
      AppliedEffects = effects
      RetryEffects = effects |> List.map (fun effect -> { effect with Attempt = 2 })
      Compensations = effects |> List.rev |> List.mapi (fun index effect -> { OperationId = effect.OperationId; CompensationId = $"rollback-{index + 1}"; FinalStateDigest = digest "e" })
      DuplicateEffectCount = 0
      TemporaryResourceCount = 0
      PreVisibility = "private"
      FinalVisibility = "private"
      PreSettingsDigest = digest "f"
      FinalSettingsDigest = digest "f"
      Recoverable = true }

let private burstFacts () =
    let primary = "pr-14"
    let unrelated = "pr-15"
    let decisions =
        [ { Subject=primary; PriorHeadSha=sha "1"; HeadSha=sha "2"; AuthorizationHeadSha=sha "2"; PriorBaseSha=sha "3"; BaseSha=sha "4"; PriorRequiredChecks=[ "queue-pilot" ]; RequiredChecks=[ "queue-growth"; "queue-pilot" ]; SuccessfulChecks=[ "queue-growth"; "queue-pilot" ]; WorkMilliseconds=1200L; WaitingMilliseconds=8000L; Delivered=false }
          { Subject=unrelated; PriorHeadSha=sha "5"; HeadSha=sha "5"; AuthorizationHeadSha=sha "5"; PriorBaseSha=sha "3"; BaseSha=sha "4"; PriorRequiredChecks=[ "queue-pilot" ]; RequiredChecks=[ "queue-pilot" ]; SuccessfulChecks=[ "queue-pilot" ]; WorkMilliseconds=600L; WaitingMilliseconds=3000L; Delivered=true } ]
    { Hints=
        [ { Subject=primary; HintId="primary-1"; Sequence=1; SupersedesHintId=None }
          { Subject=primary; HintId="primary-2"; Sequence=2; SupersedesHintId=Some "primary-1" }
          { Subject=primary; HintId="primary-3"; Sequence=3; SupersedesHintId=Some "primary-2" }
          { Subject=unrelated; HintId="unrelated-1"; Sequence=1; SupersedesHintId=None } ]
      Decisions=decisions; MaxHintsPerSubject=4; InFlightEffectCount=2; CancelledInFlightEffectCount=0
      WorkMilliseconds=decisions |> List.sumBy _.WorkMilliseconds; WaitingMilliseconds=decisions |> List.sumBy _.WaitingMilliseconds }

let private get = function Ok value -> value | Error findings -> failwithf "unexpected refusal: %A" findings
let private findings = function Error values -> values | Ok value -> failwithf "expected refusal: %A" value

[<Fact>]
let ``pilot binds distinct candidate and merge-group heads plus forward base and grown checks`` () =
    let facts = pilotFacts ()
    let plan = Sandbox.compilePilot facts |> get
    Assert.Equal(facts.CandidateSha, plan.CandidateSha)
    Assert.Equal(facts.MergeGroupHeadSha, plan.MergeGroupHeadSha)
    Assert.NotEqual(plan.CandidateSha, plan.MergeGroupHeadSha)
    Assert.Equal(facts.ObservedBaseSha, plan.PriorBaseSha)
    Assert.Equal(facts.CurrentBaseSha, plan.BaseSha)
    Assert.Equal<string list>(grownChecks, plan.RequiredChecks)
    Assert.Equal(Sandbox.pilotDisposition, plan.Disposition)

[<Fact>]
let ``pilot refuses capability prestate visibility identity and authority drift`` () =
    let baseline = pilotFacts ()
    Assert.Contains(GitHubQueueSandboxFinding.WrongRepository, Sandbox.compilePilot { baseline with RepositoryId = 1L } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.ProductionTarget, Sandbox.compilePilot { baseline with IsProduction = true } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.UnsupportedCapability, Sandbox.compilePilot { baseline with ProviderCapability = QueueProviderCapability.Unsupported } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.UnknownCapability, Sandbox.compilePilot { baseline with ProviderCapability = QueueProviderCapability.Unknown } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.SecretPresent, Sandbox.compilePilot { baseline with RepositorySecretCount = 1 } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.VisibilityTransitionNotBound, Sandbox.compilePilot { baseline with PreVisibility = "public" } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.CandidateMoved, Sandbox.compilePilot { baseline with CurrentCandidateSha = sha "9" } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.AuthorityChanged "claim", Sandbox.compilePilot { baseline with CurrentClaimGeneration = baseline.CurrentClaimGeneration + 1L } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.AuthorityChanged "review", Sandbox.compilePilot { baseline with CurrentReviewDigest = digest "9" } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.AuthorityChanged "dependency", Sandbox.compilePilot { baseline with CurrentDependencyDigest = digest "9" } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.AuthorityChanged "release", Sandbox.compilePilot { baseline with CurrentReleaseObligationsMet = false } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.AuthorityChanged "settings", Sandbox.compilePilot { baseline with CurrentSettingsDigest = digest "9" } |> findings)

[<Fact>]
let ``pilot refuses stale base incomplete or unsuccessful grown checks and expiry`` () =
    let baseline = pilotFacts ()
    Assert.Contains(GitHubQueueSandboxFinding.BaseNotAdvanced, Sandbox.compilePilot { baseline with CurrentBaseSha = baseline.ObservedBaseSha; ReevaluatedBaseSha = baseline.ObservedBaseSha } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.BaseNotReevaluated, Sandbox.compilePilot { baseline with ReevaluatedBaseSha = sha "8" } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.RequiredChecksNotGrown, Sandbox.compilePilot { baseline with CurrentRequiredChecks = originalChecks; CheckResults = baseline.CheckResults.Tail } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.CheckInventoryIncomplete, Sandbox.compilePilot { baseline with CheckResults = baseline.CheckResults.Tail } |> findings)
    let pending = { baseline.CheckResults.Head with Conclusion = QueueCheckConclusion.Pending }
    Assert.Contains(GitHubQueueSandboxFinding.CheckNotSuccessful "architecture", Sandbox.compilePilot { baseline with CheckResults = pending :: baseline.CheckResults.Tail } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.AdmissionExpired, Sandbox.compilePilot { baseline with EvaluatedAtUnixSeconds = baseline.ExpiresAtUnixSeconds } |> findings)

[<Fact>]
let ``pilot serialization seal and replay are exact`` () =
    let baseline = pilotFacts ()
    let plan = Sandbox.compilePilot baseline |> get
    let bytes = Sandbox.serializePilot plan
    Assert.Equal(Ok plan, Sandbox.parsePilot bytes)
    Assert.Equal(Error [ GitHubQueueSandboxFinding.InvalidSerialization ], Sandbox.parsePilot (bytes + " "))
    Assert.Equal(Error [ GitHubQueueSandboxFinding.AlteredSeal ], Sandbox.verifyPilot (digest "0") plan)
    Assert.Equal(Ok plan, Sandbox.replayPilot plan baseline)
    Assert.Equal(Error [ GitHubQueueSandboxFinding.ReplayConflict ], Sandbox.replayPilot plan { baseline with EvaluatedAtUnixSeconds = 151L })

[<Fact>]
let ``recovery requires sealed resume deterministic retry reverse compensation and cleanup`` () =
    let pilot = pilotFacts () |> Sandbox.compilePilot |> get
    let baseline = recoveryFacts pilot.Seal
    let receipt = Sandbox.compileRecovery baseline |> get
    Assert.Equal(Sandbox.recoveryDisposition, receipt.Disposition)
    Assert.Equal<string list>(baseline.AppliedEffects |> List.map _.OperationId |> List.rev, receipt.Compensations |> List.map _.OperationId)
    Assert.Contains(GitHubQueueSandboxFinding.UnsealedResume, Sandbox.compileRecovery { baseline with ResumeCheckpointDigest = digest "0" } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.MissingInterruption, Sandbox.compileRecovery { baseline with Interrupted = false } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.MissingFailedStep, Sandbox.compileRecovery { baseline with FailedStepInjected = false } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.RetryDiverged, Sandbox.compileRecovery { baseline with RetryEffects = baseline.RetryEffects.Tail } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.DuplicateEffect, Sandbox.compileRecovery { baseline with DuplicateEffectCount = 1 } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.CompensationOrderInvalid, Sandbox.compileRecovery { baseline with Compensations = List.rev baseline.Compensations } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.CleanupIncomplete, Sandbox.compileRecovery { baseline with TemporaryResourceCount = 1 } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.RollbackMismatch "visibility", Sandbox.compileRecovery { baseline with FinalVisibility = "public" } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.RollbackMismatch "settings", Sandbox.compileRecovery { baseline with FinalSettingsDigest = digest "0" } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.Unrecoverable, Sandbox.compileRecovery { baseline with Recoverable = false } |> findings)

[<Fact>]
let ``recovery serialization seal replay and control inventory are exact`` () =
    let pilot = pilotFacts () |> Sandbox.compilePilot |> get
    let baseline = recoveryFacts pilot.Seal
    let receipt = Sandbox.compileRecovery baseline |> get
    let bytes = Sandbox.serializeRecovery receipt
    Assert.Equal(Ok receipt, Sandbox.parseRecovery bytes)
    Assert.Equal(Error [ GitHubQueueSandboxFinding.InvalidSerialization ], Sandbox.parseRecovery (bytes + "\n"))
    Assert.Equal(Error [ GitHubQueueSandboxFinding.AlteredSeal ], Sandbox.verifyRecovery (digest "0") receipt)
    Assert.Equal(Ok receipt, Sandbox.replayRecovery receipt baseline)
    Assert.Equal(Error [ GitHubQueueSandboxFinding.ReplayConflict ], Sandbox.replayRecovery receipt { baseline with FinalSettingsDigest = digest "a"; PreSettingsDigest = digest "a" })
    let green ids = ids |> List.map (fun id -> { ControlId = id; ControlPassed = true; BaselineGreen = true })
    Assert.Equal(Ok (), Sandbox.validateControls Sandbox.pilotControlIds (green Sandbox.pilotControlIds) (green Sandbox.pilotControlIds))
    Assert.Contains("generated: inventory mismatch", Sandbox.validateControls Sandbox.pilotControlIds (green Sandbox.pilotControlIds |> List.tail) (green Sandbox.pilotControlIds) |> findings)

[<Fact>]
let ``routine burst preserves distinct subjects and current authorization while coalescing hints`` () =
    let baseline = burstFacts ()
    let receipt = Sandbox.compileBurst baseline |> get
    Assert.Equal<string list>([ "pr-14"; "pr-15" ], receipt.Subjects)
    Assert.Equal(4, receipt.HintCount)
    Assert.Equal(2, receipt.SupersededHintCount)
    Assert.Equal<string list>([ "pr-15" ], receipt.DeliveredSubjects)
    Assert.Equal(Ok receipt, Sandbox.verifyBurst receipt.Seal receipt)
    Assert.Equal(Error [ GitHubQueueSandboxFinding.AlteredSeal ], Sandbox.verifyBurst (digest "0") receipt)

[<Fact>]
let ``routine burst refuses cancellation lost subjects missing contexts stale green and absent movement`` () =
    let baseline = burstFacts ()
    Assert.Contains(GitHubQueueSandboxFinding.InFlightEffectCancelled, Sandbox.compileBurst { baseline with CancelledInFlightEffectCount=1 } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.DistinctSubjectLost, Sandbox.compileBurst { baseline with Decisions=baseline.Decisions.Tail } |> findings)
    let primary=baseline.Decisions.Head
    Assert.Contains(GitHubQueueSandboxFinding.MissingRequiredContext primary.Subject, Sandbox.compileBurst { baseline with Decisions={ primary with SuccessfulChecks=[ "queue-pilot" ] }::baseline.Decisions.Tail } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.StaleGreenAuthorization primary.Subject, Sandbox.compileBurst { baseline with Decisions={ primary with AuthorizationHeadSha=primary.PriorHeadSha }::baseline.Decisions.Tail } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.MovementNotExercised "source", Sandbox.compileBurst { baseline with Decisions=baseline.Decisions |> List.map(fun d -> { d with PriorHeadSha=d.HeadSha }) } |> findings)
    Assert.Contains(GitHubQueueSandboxFinding.MetricMismatch "work", Sandbox.compileBurst { baseline with WorkMilliseconds=baseline.WorkMilliseconds+1L } |> findings)
