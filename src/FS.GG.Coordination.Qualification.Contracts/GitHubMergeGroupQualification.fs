namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type MergeGroupCheckConclusion = Success | Pending | Failure
type MergeGroupCheckFact = { Name: string; EventName: string; HeadSha: string; Conclusion: MergeGroupCheckConclusion }
type GitHubMergeGroupFacts =
    { EventName: string; Repository: string; MergeGroupId: string; MergeGroupHeadSha: string
      ObservedBaseRepository: string; CurrentBaseRepository: string
      ObservedBaseRef: string; CurrentBaseRef: string; ObservedBaseSha: string; CurrentBaseSha: string
      BaseObservationRevision: int64; CurrentBaseObservationRevision: int64
      ObservedAtUnixSeconds: int64; FreshUntilUnixSeconds: int64; EvaluatedAtUnixSeconds: int64
      ExpectedRequiredChecks: string list; ObservedRequiredChecks: string list; CheckResults: MergeGroupCheckFact list
      ObservedClaimGeneration: int64; CurrentClaimGeneration: int64
      ObservedReviewDigest: string; CurrentReviewDigest: string
      ObservedCandidateHeadSha: string; CurrentCandidateHeadSha: string
      ObservedDependencyDigest: string; CurrentDependencyDigest: string
      ObservedReleaseObligationsMet: bool; CurrentReleaseObligationsMet: bool
      ObservedSettingsDigest: string; CurrentSettingsDigest: string
      HasConflictingGroup: bool; AttemptsDirectMerge: bool }
type GitHubMergeGroupPlan =
    { SchemaVersion: int; EventName: string; Repository: string; MergeGroupId: string; MergeGroupHeadSha: string
      BaseRepository: string; BaseRef: string; BaseSha: string; BaseObservationRevision: int64
      ObservedAtUnixSeconds: int64; FreshUntilUnixSeconds: int64; EvaluatedAtUnixSeconds: int64
      RequiredChecks: string list; CheckResults: MergeGroupCheckFact list
      ClaimGeneration: int64; ReviewDigest: string; CandidateHeadSha: string
      DependencyDigest: string; ReleaseObligationsMet: bool; SettingsDigest: string; Disposition: string; Seal: string }
[<RequireQualifiedAccess>]
type GitHubMergeGroupFinding =
    | MissingField of string | MalformedField of string | UnknownEvent of string
    | BaseObservationStale of int64 | BaseRepositoryChanged of string * string | BaseRefChanged of string * string
    | BaseChanged of string * string | BaseRevisionChanged of int64 * int64
    | NonCanonicalCheckInventory | IncompleteCheckInventory | CheckDidNotRunForMergeGroup of string
    | CheckHeadMismatch of string | CheckPending of string | CheckFailed of string
    | ClaimChanged of int64 * int64 | ReviewChanged | CandidateHeadChanged | DependencyChanged
    | ReleaseObligationsUnmet | ReleaseAuthorityChanged | SettingsChanged | ConflictingGroup
    | DirectMergeAttempt | AlteredSeal | ReplayConflict | InvalidSerialization of string
type GitHubMergeGroupControl =
    | MergeGroupPrerequisite | MergeGroupRoadmap | MergeGroupPositive | MergeGroupEvent
    | MergeGroupIdentity | MergeGroupHead | BaseRepositoryIdentity | BaseRef | BaseSha
    | BaseRevision | BaseFreshness | BaseChanged | RequiredCheckInventory | RequiredCheckEvent
    | RequiredCheckHead | RequiredCheckPending | RequiredCheckFailure | ClaimFreshness
    | ReviewFreshness | CandidateHeadFreshness | DependencyFreshness | ReleaseFreshness
    | SettingsFreshness | ConflictingGroup | DirectMerge | MergeGroupOrdering | MergeGroupSeal
    | MergeGroupReplay | MergeGroupQuintPreservation | MergeGroupNoNetwork | MergeGroupNoProductionMutation
type GitHubMergeGroupControlResult = { Control: GitHubMergeGroupControl; ControlPassed: bool; BaselineGreen: bool }

module GitHubMergeGroupQualification =
    let eventName = "merge_group:checks_requested"
    let disposition = "merge-group-qualified"
    let requiredControls =
        [ MergeGroupPrerequisite; MergeGroupRoadmap; MergeGroupPositive; MergeGroupEvent
          MergeGroupIdentity; MergeGroupHead; BaseRepositoryIdentity; BaseRef; BaseSha; BaseRevision
          BaseFreshness; BaseChanged; RequiredCheckInventory; RequiredCheckEvent; RequiredCheckHead
          RequiredCheckPending; RequiredCheckFailure; ClaimFreshness; ReviewFreshness
          CandidateHeadFreshness; DependencyFreshness; ReleaseFreshness; SettingsFreshness
          ConflictingGroup; DirectMerge; MergeGroupOrdering; MergeGroupSeal; MergeGroupReplay
          MergeGroupQuintPreservation; MergeGroupNoNetwork; MergeGroupNoProductionMutation ]
    let controlId = function
        | MergeGroupPrerequisite -> "prerequisite" | MergeGroupRoadmap -> "roadmap"
        | MergeGroupPositive -> "positive" | MergeGroupEvent -> "event"
        | MergeGroupIdentity -> "merge-group-identity" | MergeGroupHead -> "merge-group-head"
        | BaseRepositoryIdentity -> "base-repository" | BaseRef -> "base-ref" | BaseSha -> "base-sha"
        | BaseRevision -> "base-revision" | BaseFreshness -> "base-freshness" | BaseChanged -> "base-changed"
        | RequiredCheckInventory -> "check-inventory" | RequiredCheckEvent -> "check-event"
        | RequiredCheckHead -> "check-head" | RequiredCheckPending -> "check-pending"
        | RequiredCheckFailure -> "check-failure" | ClaimFreshness -> "claim-freshness"
        | ReviewFreshness -> "review-freshness" | CandidateHeadFreshness -> "candidate-head-freshness"
        | DependencyFreshness -> "dependency-freshness" | ReleaseFreshness -> "release-freshness"
        | SettingsFreshness -> "settings-freshness" | ConflictingGroup -> "conflicting-group"
        | DirectMerge -> "direct-merge" | MergeGroupOrdering -> "ordering" | MergeGroupSeal -> "seal"
        | MergeGroupReplay -> "replay" | MergeGroupQuintPreservation -> "quint-preservation"
        | MergeGroupNoNetwork -> "no-network" | MergeGroupNoProductionMutation -> "no-production-mutation"

    let private token = Regex("^[A-Za-z0-9][A-Za-z0-9._:/-]*$", RegexOptions.CultureInvariant)
    let private sha = Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)
    let private digest = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private strings values = values |> List.map frame |> String.concat ""
    let private hash (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private conclusionText = function
        | MergeGroupCheckConclusion.Success -> "success"
        | MergeGroupCheckConclusion.Pending -> "pending"
        | MergeGroupCheckConclusion.Failure -> "failure"
    let private checkFrame (check: MergeGroupCheckFact) =
        strings [ check.Name; check.EventName; check.HeadSha; conclusionText check.Conclusion ]
    let private sealOf (plan: GitHubMergeGroupPlan) =
        [ "github-merge-group/v1"; plan.EventName; plan.Repository; plan.MergeGroupId; plan.MergeGroupHeadSha
          plan.BaseRepository; plan.BaseRef; plan.BaseSha; string plan.BaseObservationRevision
          string plan.ObservedAtUnixSeconds; string plan.FreshUntilUnixSeconds; string plan.EvaluatedAtUnixSeconds
          strings plan.RequiredChecks; strings (plan.CheckResults |> List.map checkFrame)
          string plan.ClaimGeneration; plan.ReviewDigest; plan.CandidateHeadSha
          plan.DependencyDigest; string plan.ReleaseObligationsMet; plan.SettingsDigest; plan.Disposition ]
        |> strings |> hash
    let private canonical (values: string list) = not values.IsEmpty && values = (values |> List.distinct |> List.sort)

    let compile (facts: GitHubMergeGroupFacts) =
        let errors = ResizeArray<GitHubMergeGroupFinding>()
        let requireText name value =
            if String.IsNullOrWhiteSpace value then errors.Add(GitHubMergeGroupFinding.MissingField name)
            elif not(token.IsMatch value) then errors.Add(GitHubMergeGroupFinding.MalformedField name)
        let requireSha name value =
            if String.IsNullOrWhiteSpace value then errors.Add(GitHubMergeGroupFinding.MissingField name)
            elif not(sha.IsMatch value) then errors.Add(GitHubMergeGroupFinding.MalformedField name)
        let requireDigest name value =
            if String.IsNullOrWhiteSpace value then errors.Add(GitHubMergeGroupFinding.MissingField name)
            elif not(digest.IsMatch value) then errors.Add(GitHubMergeGroupFinding.MalformedField name)
        if facts.EventName <> eventName then errors.Add(GitHubMergeGroupFinding.UnknownEvent facts.EventName)
        requireText "repository" facts.Repository; requireText "mergeGroupId" facts.MergeGroupId
        requireSha "mergeGroupHeadSha" facts.MergeGroupHeadSha
        requireText "observedBaseRepository" facts.ObservedBaseRepository
        requireText "currentBaseRepository" facts.CurrentBaseRepository
        if facts.ObservedBaseRepository <> facts.CurrentBaseRepository then errors.Add(GitHubMergeGroupFinding.BaseRepositoryChanged(facts.ObservedBaseRepository, facts.CurrentBaseRepository))
        if facts.CurrentBaseRepository <> facts.Repository then errors.Add(GitHubMergeGroupFinding.MalformedField "baseRepositoryScope")
        let requireBaseRef name value =
            if String.IsNullOrWhiteSpace value then errors.Add(GitHubMergeGroupFinding.MissingField name)
            elif not(value.StartsWith("refs/heads/", StringComparison.Ordinal)) || not(token.IsMatch value) then errors.Add(GitHubMergeGroupFinding.MalformedField name)
        requireBaseRef "observedBaseRef" facts.ObservedBaseRef
        requireBaseRef "currentBaseRef" facts.CurrentBaseRef
        if facts.ObservedBaseRef <> facts.CurrentBaseRef then errors.Add(GitHubMergeGroupFinding.BaseRefChanged(facts.ObservedBaseRef, facts.CurrentBaseRef))
        requireSha "observedBaseSha" facts.ObservedBaseSha; requireSha "currentBaseSha" facts.CurrentBaseSha
        if facts.BaseObservationRevision <= 0L || facts.CurrentBaseObservationRevision <= 0L then errors.Add(GitHubMergeGroupFinding.MalformedField "baseObservationRevision")
        elif facts.BaseObservationRevision <> facts.CurrentBaseObservationRevision then errors.Add(GitHubMergeGroupFinding.BaseRevisionChanged(facts.BaseObservationRevision, facts.CurrentBaseObservationRevision))
        if facts.ObservedAtUnixSeconds < 0L || facts.FreshUntilUnixSeconds < facts.ObservedAtUnixSeconds || facts.EvaluatedAtUnixSeconds < facts.ObservedAtUnixSeconds then errors.Add(GitHubMergeGroupFinding.MalformedField "baseFreshness")
        elif facts.EvaluatedAtUnixSeconds > facts.FreshUntilUnixSeconds then errors.Add(GitHubMergeGroupFinding.BaseObservationStale facts.FreshUntilUnixSeconds)
        if sha.IsMatch facts.ObservedBaseSha && sha.IsMatch facts.CurrentBaseSha && facts.ObservedBaseSha <> facts.CurrentBaseSha then errors.Add(GitHubMergeGroupFinding.BaseChanged(facts.ObservedBaseSha, facts.CurrentBaseSha))
        if not(canonical facts.ExpectedRequiredChecks) || not(canonical facts.ObservedRequiredChecks) then errors.Add GitHubMergeGroupFinding.NonCanonicalCheckInventory
        if facts.ObservedRequiredChecks <> facts.ExpectedRequiredChecks then errors.Add GitHubMergeGroupFinding.IncompleteCheckInventory
        let resultNames = facts.CheckResults |> List.map _.Name
        if resultNames <> facts.ExpectedRequiredChecks then errors.Add GitHubMergeGroupFinding.IncompleteCheckInventory
        for result in facts.CheckResults do
            requireText "checkName" result.Name
            if result.EventName <> "merge_group" then errors.Add(GitHubMergeGroupFinding.CheckDidNotRunForMergeGroup result.Name)
            if result.HeadSha <> facts.MergeGroupHeadSha then errors.Add(GitHubMergeGroupFinding.CheckHeadMismatch result.Name)
            match result.Conclusion with
            | MergeGroupCheckConclusion.Success -> ()
            | MergeGroupCheckConclusion.Pending -> errors.Add(GitHubMergeGroupFinding.CheckPending result.Name)
            | MergeGroupCheckConclusion.Failure -> errors.Add(GitHubMergeGroupFinding.CheckFailed result.Name)
        if facts.ObservedClaimGeneration <= 0L || facts.CurrentClaimGeneration <= 0L then errors.Add(GitHubMergeGroupFinding.MalformedField "claimGeneration")
        elif facts.ObservedClaimGeneration <> facts.CurrentClaimGeneration then errors.Add(GitHubMergeGroupFinding.ClaimChanged(facts.ObservedClaimGeneration, facts.CurrentClaimGeneration))
        requireDigest "observedReviewDigest" facts.ObservedReviewDigest; requireDigest "currentReviewDigest" facts.CurrentReviewDigest
        if facts.ObservedReviewDigest <> facts.CurrentReviewDigest then errors.Add GitHubMergeGroupFinding.ReviewChanged
        requireSha "observedCandidateHeadSha" facts.ObservedCandidateHeadSha; requireSha "currentCandidateHeadSha" facts.CurrentCandidateHeadSha
        if facts.ObservedCandidateHeadSha <> facts.CurrentCandidateHeadSha || facts.CurrentCandidateHeadSha <> facts.MergeGroupHeadSha then errors.Add GitHubMergeGroupFinding.CandidateHeadChanged
        requireDigest "observedDependencyDigest" facts.ObservedDependencyDigest; requireDigest "currentDependencyDigest" facts.CurrentDependencyDigest
        if facts.ObservedDependencyDigest <> facts.CurrentDependencyDigest then errors.Add GitHubMergeGroupFinding.DependencyChanged
        if not facts.ObservedReleaseObligationsMet || not facts.CurrentReleaseObligationsMet then errors.Add GitHubMergeGroupFinding.ReleaseObligationsUnmet
        elif facts.ObservedReleaseObligationsMet <> facts.CurrentReleaseObligationsMet then errors.Add GitHubMergeGroupFinding.ReleaseAuthorityChanged
        requireDigest "observedSettingsDigest" facts.ObservedSettingsDigest; requireDigest "currentSettingsDigest" facts.CurrentSettingsDigest
        if facts.ObservedSettingsDigest <> facts.CurrentSettingsDigest then errors.Add GitHubMergeGroupFinding.SettingsChanged
        if facts.HasConflictingGroup then errors.Add GitHubMergeGroupFinding.ConflictingGroup
        if facts.AttemptsDirectMerge then errors.Add GitHubMergeGroupFinding.DirectMergeAttempt
        if errors.Count > 0 then Error(List.ofSeq errors)
        else
            let unsigned =
                { SchemaVersion = 1; EventName = facts.EventName; Repository = facts.Repository; MergeGroupId = facts.MergeGroupId
                  MergeGroupHeadSha = facts.MergeGroupHeadSha; BaseRepository = facts.CurrentBaseRepository; BaseRef = facts.CurrentBaseRef
                  BaseSha = facts.CurrentBaseSha; BaseObservationRevision = facts.CurrentBaseObservationRevision
                  ObservedAtUnixSeconds = facts.ObservedAtUnixSeconds; FreshUntilUnixSeconds = facts.FreshUntilUnixSeconds
                  EvaluatedAtUnixSeconds = facts.EvaluatedAtUnixSeconds; RequiredChecks = facts.ExpectedRequiredChecks
                  CheckResults = facts.CheckResults
                  ClaimGeneration = facts.CurrentClaimGeneration; ReviewDigest = facts.CurrentReviewDigest
                  CandidateHeadSha = facts.CurrentCandidateHeadSha; DependencyDigest = facts.CurrentDependencyDigest
                  ReleaseObligationsMet = facts.CurrentReleaseObligationsMet; SettingsDigest = facts.CurrentSettingsDigest
                  Disposition = disposition; Seal = "" }
            Ok { unsigned with Seal = sealOf unsigned }

    let serialize (plan: GitHubMergeGroupPlan) =
        JsonSerializer.Serialize(
            {| schemaVersion = plan.SchemaVersion; eventName = plan.EventName; repository = plan.Repository
               mergeGroupId = plan.MergeGroupId; mergeGroupHeadSha = plan.MergeGroupHeadSha
               baseRepository = plan.BaseRepository; baseRef = plan.BaseRef; baseSha = plan.BaseSha
               baseObservationRevision = plan.BaseObservationRevision; observedAtUnixSeconds = plan.ObservedAtUnixSeconds
               freshUntilUnixSeconds = plan.FreshUntilUnixSeconds; evaluatedAtUnixSeconds = plan.EvaluatedAtUnixSeconds
               requiredChecks = plan.RequiredChecks
               checkResults = plan.CheckResults |> List.map (fun check -> {| name = check.Name; eventName = check.EventName; headSha = check.HeadSha; conclusion = conclusionText check.Conclusion |})
               claimGeneration = plan.ClaimGeneration
               reviewDigest = plan.ReviewDigest; candidateHeadSha = plan.CandidateHeadSha
               dependencyDigest = plan.DependencyDigest; releaseObligationsMet = plan.ReleaseObligationsMet
               settingsDigest = plan.SettingsDigest; disposition = plan.Disposition; seal = plan.Seal |})

    let verify (expectedSeal: string) (plan: GitHubMergeGroupPlan) =
        let errors = ResizeArray<GitHubMergeGroupFinding>()
        let requireToken name value =
            if String.IsNullOrWhiteSpace value then errors.Add(GitHubMergeGroupFinding.MissingField name)
            elif not(token.IsMatch value) then errors.Add(GitHubMergeGroupFinding.MalformedField name)
        let requireSha name value =
            if String.IsNullOrWhiteSpace value then errors.Add(GitHubMergeGroupFinding.MissingField name)
            elif not(sha.IsMatch value) then errors.Add(GitHubMergeGroupFinding.MalformedField name)
        let requireDigest name value =
            if String.IsNullOrWhiteSpace value then errors.Add(GitHubMergeGroupFinding.MissingField name)
            elif not(digest.IsMatch value) then errors.Add(GitHubMergeGroupFinding.MalformedField name)
        if plan.SchemaVersion <> 1 then errors.Add(GitHubMergeGroupFinding.InvalidSerialization "schemaVersion")
        if plan.EventName <> eventName then errors.Add(GitHubMergeGroupFinding.UnknownEvent plan.EventName)
        requireToken "repository" plan.Repository; requireToken "mergeGroupId" plan.MergeGroupId
        requireSha "mergeGroupHeadSha" plan.MergeGroupHeadSha; requireToken "baseRepository" plan.BaseRepository
        if plan.BaseRepository <> plan.Repository then errors.Add(GitHubMergeGroupFinding.MalformedField "baseRepositoryScope")
        if String.IsNullOrWhiteSpace plan.BaseRef then errors.Add(GitHubMergeGroupFinding.MissingField "baseRef")
        elif not(plan.BaseRef.StartsWith("refs/heads/", StringComparison.Ordinal)) || not(token.IsMatch plan.BaseRef) then errors.Add(GitHubMergeGroupFinding.MalformedField "baseRef")
        requireSha "baseSha" plan.BaseSha
        if plan.BaseObservationRevision <= 0L then errors.Add(GitHubMergeGroupFinding.MalformedField "baseObservationRevision")
        if plan.ObservedAtUnixSeconds < 0L || plan.FreshUntilUnixSeconds < plan.ObservedAtUnixSeconds || plan.EvaluatedAtUnixSeconds < plan.ObservedAtUnixSeconds || plan.EvaluatedAtUnixSeconds > plan.FreshUntilUnixSeconds then errors.Add(GitHubMergeGroupFinding.MalformedField "baseFreshness")
        if not(canonical plan.RequiredChecks) then errors.Add GitHubMergeGroupFinding.NonCanonicalCheckInventory
        if plan.CheckResults |> List.map _.Name <> plan.RequiredChecks then errors.Add GitHubMergeGroupFinding.IncompleteCheckInventory
        for check in plan.CheckResults do
            requireToken "checkName" check.Name
            if check.EventName <> "merge_group" then errors.Add(GitHubMergeGroupFinding.CheckDidNotRunForMergeGroup check.Name)
            if check.HeadSha <> plan.MergeGroupHeadSha then errors.Add(GitHubMergeGroupFinding.CheckHeadMismatch check.Name)
            match check.Conclusion with
            | MergeGroupCheckConclusion.Success -> ()
            | MergeGroupCheckConclusion.Pending -> errors.Add(GitHubMergeGroupFinding.CheckPending check.Name)
            | MergeGroupCheckConclusion.Failure -> errors.Add(GitHubMergeGroupFinding.CheckFailed check.Name)
        if plan.ClaimGeneration <= 0L then errors.Add(GitHubMergeGroupFinding.MalformedField "claimGeneration")
        requireDigest "reviewDigest" plan.ReviewDigest; requireSha "candidateHeadSha" plan.CandidateHeadSha
        if plan.CandidateHeadSha <> plan.MergeGroupHeadSha then errors.Add GitHubMergeGroupFinding.CandidateHeadChanged
        requireDigest "dependencyDigest" plan.DependencyDigest
        if not plan.ReleaseObligationsMet then errors.Add GitHubMergeGroupFinding.ReleaseObligationsUnmet
        requireDigest "settingsDigest" plan.SettingsDigest
        if plan.Disposition <> disposition then errors.Add(GitHubMergeGroupFinding.InvalidSerialization "disposition")
        if errors.Count > 0 then Error(List.ofSeq errors)
        elif plan.Seal <> sealOf plan || plan.Seal <> expectedSeal then Error [ GitHubMergeGroupFinding.AlteredSeal ]
        else Ok plan

    let parse (value: string) =
        try
            use document = JsonDocument.Parse value
            let root = document.RootElement
            let text (name: string) = root.GetProperty(name).GetString()
            let texts (name: string) = root.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
            let checks (name: string) =
                root.GetProperty(name).EnumerateArray()
                |> Seq.map (fun value ->
                    let conclusion =
                        match value.GetProperty("conclusion").GetString() with
                        | "success" -> MergeGroupCheckConclusion.Success
                        | "pending" -> MergeGroupCheckConclusion.Pending
                        | "failure" -> MergeGroupCheckConclusion.Failure
                        | other -> raise (JsonException $"unknown check conclusion {other}")
                    { Name = value.GetProperty("name").GetString(); EventName = value.GetProperty("eventName").GetString()
                      HeadSha = value.GetProperty("headSha").GetString(); Conclusion = conclusion })
                |> Seq.toList
            let plan =
                { SchemaVersion = root.GetProperty("schemaVersion").GetInt32(); EventName = text "eventName"
                  Repository = text "repository"; MergeGroupId = text "mergeGroupId"; MergeGroupHeadSha = text "mergeGroupHeadSha"
                  BaseRepository = text "baseRepository"; BaseRef = text "baseRef"; BaseSha = text "baseSha"
                  BaseObservationRevision = root.GetProperty("baseObservationRevision").GetInt64()
                  ObservedAtUnixSeconds = root.GetProperty("observedAtUnixSeconds").GetInt64()
                  FreshUntilUnixSeconds = root.GetProperty("freshUntilUnixSeconds").GetInt64()
                  EvaluatedAtUnixSeconds = root.GetProperty("evaluatedAtUnixSeconds").GetInt64()
                  RequiredChecks = texts "requiredChecks"; CheckResults = checks "checkResults"
                  ClaimGeneration = root.GetProperty("claimGeneration").GetInt64()
                  ReviewDigest = text "reviewDigest"; CandidateHeadSha = text "candidateHeadSha"
                  DependencyDigest = text "dependencyDigest"; ReleaseObligationsMet = root.GetProperty("releaseObligationsMet").GetBoolean()
                  SettingsDigest = text "settingsDigest"; Disposition = text "disposition"; Seal = text "seal" }
            match verify plan.Seal plan with
            | Error findings -> Error findings
            | Ok parsed when serialize parsed <> value -> Error [ GitHubMergeGroupFinding.InvalidSerialization "non-canonical bytes" ]
            | Ok parsed -> Ok parsed
        with error -> Error [ GitHubMergeGroupFinding.InvalidSerialization error.Message ]

    let replay prior facts =
        match verify prior.Seal prior, compile facts with
        | Error findings, _ -> Error findings
        | _, Error findings -> Error findings
        | Ok _, Ok candidate when serialize candidate = serialize prior -> Ok prior
        | Ok _, Ok _ -> Error [ GitHubMergeGroupFinding.ReplayConflict ]

    let validateControls generated independent =
        let expected = requiredControls |> List.map controlId
        let validate label rows =
            [ if rows |> List.map (fun row -> controlId row.Control) <> expected then yield $"{label} control inventory differs"
              if rows |> List.exists (fun row -> not row.ControlPassed || not row.BaselineGreen) then yield $"{label} control failed" ]
        let errors = validate "generated" generated @ validate "independent" independent
        if errors.IsEmpty then Ok () else Error errors
