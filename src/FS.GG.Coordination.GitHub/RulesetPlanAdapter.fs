namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type RulesetPlanProfileClass = AuthorityPlan | FrameworkPlan | HostedNonParticipantPlan | ObserveOnlyPlan
type RulesetBypassActorKind = TeamActor | IntegrationActor
type RulesetMergeMethod = Squash | MergeCommit | Rebase

type RulesetRequiredCheck =
    { Context: string
      IntegrationId: int64 option }

type ApprovedRulesetBypassPrincipal =
    { ActorId: int64
      Kind: RulesetBypassActorKind
      AllowedProfiles: RulesetPlanProfileClass list }

type RequestedRulesetBypassPrincipal =
    { ActorId: int64
      Kind: RulesetBypassActorKind }

type RulesetExceptionScope =
    | RequiredReviewCount of int
    | ConversationResolution of bool
    | MergeQueue of bool
    | BypassPrincipal of RequestedRulesetBypassPrincipal

type RulesetPlanException =
    { Id: string
      Owner: string
      Rationale: string
      Scope: RulesetExceptionScope
      ApprovedAt: DateTimeOffset
      StartsAt: DateTimeOffset
      ExpiresAt: DateTimeOffset }

type DefaultBranchRulesetTarget =
    { Include: string list
      ProtectDeletion: bool
      BlockNonFastForward: bool
      RequirePullRequest: bool
      DismissStaleReviews: bool
      RequiredApprovals: int
      RequireConversationResolution: bool
      StrictChecks: bool
      RequiredChecks: RulesetRequiredCheck list
      Bypass: RequestedRulesetBypassPrincipal list
      MergeQueueEnabled: bool
      MergeQueueDisabledReason: string option }

type ReleaseTagRulesetTarget =
    { Include: string list
      ProtectDeletion: bool
      BlockNonFastForward: bool
      BlockUpdate: bool
      RequireSignatures: bool
      Bypass: RequestedRulesetBypassPrincipal list }

type RepositoryMergePolicyTarget =
    { AllowedMergeMethods: RulesetMergeMethod list
      AllowAutoMerge: bool
      DeleteBranchOnMerge: bool }

type RulesetPlanSnapshot =
    { SchemaVersion: int
      Repository: string
      PrerequisiteReceiptDigest: string
      ProfileSnapshot: RepositoryRosterSnapshot
      ExpectedProfileSeal: string
      CensusSnapshot: RequiredCheckCensusSnapshot option
      ExpectedCensusSeal: string option
      CurrentPolicyRepository: string
      CurrentPolicyRevision: string
      CurrentPolicyEvidenceSha256: string
      CurrentPolicyObservedAt: DateTimeOffset
      CurrentPolicyComplete: bool
      ObservedAt: DateTimeOffset
      Complete: bool
      ApprovedBypass: ApprovedRulesetBypassPrincipal list
      RequestedBypass: RequestedRulesetBypassPrincipal list
      Exceptions: RulesetPlanException list }

type RulesetPlanReport =
    { Repository: string
      ProfileClass: RulesetPlanProfileClass
      MutationPermitted: bool
      PrerequisiteReceiptDigest: string
      ProfileReportSeal: string
      CensusSeal: string option
      CurrentPolicyRevision: string
      CurrentPolicyEvidenceSha256: string
      DefaultBranch: DefaultBranchRulesetTarget option
      ReleaseTags: ReleaseTagRulesetTarget option
      RepositoryPolicy: RepositoryMergePolicyTarget option
      ActiveExceptions: RulesetPlanException list
      Seal: string }

type RulesetPlanFinding =
    | UnsupportedRulesetPlanSchema of int
    | InvalidRulesetPlanRepository of string
    | IncompleteRulesetPlanObservation
    | StaleRulesetPlanObservation
    | InvalidRulesetPlanBinding of string
    | CrossRepositoryRulesetProfile of string
    | InconsistentRulesetProfileAdministration of string
    | MissingRequiredCheckCensus of string
    | UnexpectedRequiredCheckCensus of string
    | CrossRepositoryRequiredCheckCensus of string
    | AlteredRulesetProfileBinding
    | InvalidRulesetBypassPrincipal of int64
    | DuplicateRulesetBypassPrincipal of int64
    | UnauthorizedRulesetBypassPrincipal of int64
    | InvalidRulesetException of string
    | DuplicateRulesetException of string
    | InactiveRulesetException of string
    | OverlongRulesetException of string
    | AlteredRulesetPlanSeal

module RulesetPlanAdapter =
    let private sha256 (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private digestLike (value: string) = value.Length = 64 && value |> Seq.forall Uri.IsHexDigit
    let private revisionLike (value: string) = value.Length = 40 && value |> Seq.forall Uri.IsHexDigit
    let private validText (value: string) = value <> "" && value.Trim() = value
    let private repositoryLike (value: string) =
        match value.Split('/') with
        | [| owner; repository |] -> owner <> "" && repository <> "" && value.Trim() = value
        | _ -> false

    let private profileClass (profile: RepositoryProfile) =
        match profile.Administration, profile.Role with
        | ExternalObserveOnly, _ -> ObserveOnlyPlan
        | OrganizationAdministered, Authority -> AuthorityPlan
        | OrganizationAdministered, Framework -> FrameworkPlan
        | OrganizationAdministered, NonParticipant -> HostedNonParticipantPlan

    let private classText = function
        | AuthorityPlan -> "authority"
        | FrameworkPlan -> "framework"
        | HostedNonParticipantPlan -> "hosted-non-participant"
        | ObserveOnlyPlan -> "observe-only"

    let private actorText (actor: RequestedRulesetBypassPrincipal) =
        let kind = match actor.Kind with TeamActor -> "team" | IntegrationActor -> "integration"
        $"{kind}:{actor.ActorId}"

    let private approvedActorText (actor: ApprovedRulesetBypassPrincipal) =
        let kind = match actor.Kind with TeamActor -> "team" | IntegrationActor -> "integration"
        let profiles = actor.AllowedProfiles |> List.map classText |> List.sort |> String.concat ","
        $"{kind}:{actor.ActorId}:{profiles}"

    let private methodText = function Squash -> "squash" | MergeCommit -> "merge" | Rebase -> "rebase"

    let private exceptionText (value: RulesetPlanException) =
        let scope =
            match value.Scope with
            | RequiredReviewCount count -> $"reviews:{count}"
            | ConversationResolution required -> $"conversation:{required}"
            | MergeQueue enabled -> $"merge-queue:{enabled}"
            | BypassPrincipal principal -> $"bypass:{actorText principal}"
        [ value.Id; value.Owner; value.Rationale; scope
          value.ApprovedAt.ToUniversalTime().ToString("O")
          value.StartsAt.ToUniversalTime().ToString("O")
          value.ExpiresAt.ToUniversalTime().ToString("O") ]
        |> List.map frame |> String.concat ""

    let private checkText (value: RulesetRequiredCheck) =
        [ value.Context; value.IntegrationId |> Option.map string |> Option.defaultValue "" ]
        |> List.map frame |> String.concat ""

    let private branchText (value: DefaultBranchRulesetTarget) =
        [ String.concat "," value.Include
          string value.ProtectDeletion
          string value.BlockNonFastForward
          string value.RequirePullRequest
          string value.DismissStaleReviews
          string value.RequiredApprovals
          string value.RequireConversationResolution
          string value.StrictChecks
          value.RequiredChecks |> List.map checkText |> String.concat ""
          value.Bypass |> List.map actorText |> String.concat ","
          string value.MergeQueueEnabled
          value.MergeQueueDisabledReason |> Option.defaultValue "" ]
        |> List.map frame |> String.concat ""

    let private tagText (value: ReleaseTagRulesetTarget) =
        [ String.concat "," value.Include
          string value.ProtectDeletion
          string value.BlockNonFastForward
          string value.BlockUpdate
          string value.RequireSignatures
          value.Bypass |> List.map actorText |> String.concat "," ]
        |> List.map frame |> String.concat ""

    let private repositoryPolicyText (value: RepositoryMergePolicyTarget) =
        [ value.AllowedMergeMethods |> List.map methodText |> String.concat ","
          string value.AllowAutoMerge
          string value.DeleteBranchOnMerge ]
        |> List.map frame |> String.concat ""

    let private seal
        (snapshot: RulesetPlanSnapshot)
        (profile: RepositoryProfile)
        (census: RequiredCheckCensusReport option)
        profileClass
        (defaultBranch: DefaultBranchRulesetTarget option)
        (releaseTags: ReleaseTagRulesetTarget option)
        (repositoryPolicy: RepositoryMergePolicyTarget option)
        (exceptions: RulesetPlanException list)
        =
        [ snapshot.Repository
          classText profileClass
          string profile.PropertyMutationPermitted
          snapshot.PrerequisiteReceiptDigest.ToLowerInvariant()
          snapshot.ExpectedProfileSeal.ToLowerInvariant()
          census |> Option.map _.Seal |> Option.defaultValue "" |> _.ToLowerInvariant()
          snapshot.CurrentPolicyRepository
          snapshot.CurrentPolicyRevision.ToLowerInvariant()
          snapshot.CurrentPolicyEvidenceSha256.ToLowerInvariant()
          snapshot.CurrentPolicyObservedAt.ToUniversalTime().ToString("O")
          string snapshot.CurrentPolicyComplete
          snapshot.ApprovedBypass |> List.sortBy (fun value -> value.Kind, value.ActorId) |> List.map approvedActorText |> String.concat ","
          defaultBranch |> Option.map branchText |> Option.defaultValue ""
          releaseTags |> Option.map tagText |> Option.defaultValue ""
          repositoryPolicy |> Option.map repositoryPolicyText |> Option.defaultValue ""
          exceptions |> List.map exceptionText |> String.concat "" ]
        |> List.map frame |> String.concat "" |> sha256

    let private duplicates projection values =
        values |> List.groupBy projection |> List.choose (fun (key, rows) -> if rows.Length > 1 then Some key else None)

    let compile (asOf: DateTimeOffset) (maxAge: TimeSpan) (snapshot: RulesetPlanSnapshot) =
        let profileReport =
            RepositoryProfileAdapter.verify snapshot.ExpectedProfileSeal asOf maxAge snapshot.ProfileSnapshot
            |> Result.toOption
        let selectedProfile =
            profileReport
            |> Option.bind (fun report -> report.Profiles |> List.tryFind (fun value -> value.FullName = snapshot.Repository))
        let selectedClass = selectedProfile |> Option.map profileClass |> Option.defaultValue ObserveOnlyPlan
        let mutationPermitted = selectedClass <> ObserveOnlyPlan
        let censusReport =
            match snapshot.CensusSnapshot, snapshot.ExpectedCensusSeal with
            | Some censusSnapshot, Some expected -> RequiredCheckCensusAdapter.verify expected asOf maxAge censusSnapshot |> Result.toOption
            | _ -> None
        let requestedKey (value: RequestedRulesetBypassPrincipal) = value.Kind, value.ActorId
        let approvedKey (value: ApprovedRulesetBypassPrincipal) = value.Kind, value.ActorId
        let exceptionWindow (value: RulesetPlanException) = value.ExpiresAt - value.ApprovedAt
        let exceptionScopeKey (value: RulesetPlanException) =
            match value.Scope with
            | RequiredReviewCount _ -> "reviews"
            | ConversationResolution _ -> "conversation"
            | MergeQueue _ -> "merge-queue"
            | BypassPrincipal principal -> $"bypass:{actorText principal}"
        let findings =
            [ if snapshot.SchemaVersion <> 1 then yield UnsupportedRulesetPlanSchema snapshot.SchemaVersion
              if not (repositoryLike snapshot.Repository) then yield InvalidRulesetPlanRepository snapshot.Repository
              if not snapshot.Complete then yield IncompleteRulesetPlanObservation
              if snapshot.ObservedAt > asOf || asOf - snapshot.ObservedAt > maxAge then yield StaleRulesetPlanObservation
              if not (digestLike snapshot.PrerequisiteReceiptDigest) then yield InvalidRulesetPlanBinding "prerequisiteReceiptDigest"
              if not (digestLike snapshot.ExpectedProfileSeal) then yield InvalidRulesetPlanBinding "profileReportSeal"
              if profileReport.IsNone then yield InvalidRulesetPlanBinding "profileReport"
              if not (revisionLike snapshot.CurrentPolicyRevision) then yield InvalidRulesetPlanBinding "currentPolicyRevision"
              if not (digestLike snapshot.CurrentPolicyEvidenceSha256) then yield InvalidRulesetPlanBinding "currentPolicyEvidenceSha256"
              if snapshot.CurrentPolicyRepository <> snapshot.Repository then yield InvalidRulesetPlanBinding "currentPolicyRepository"
              if not snapshot.CurrentPolicyComplete then yield InvalidRulesetPlanBinding "currentPolicyComplete"
              if snapshot.CurrentPolicyObservedAt > asOf || asOf - snapshot.CurrentPolicyObservedAt > maxAge then yield InvalidRulesetPlanBinding "currentPolicyObservedAt"
              match selectedProfile with
              | None -> yield CrossRepositoryRulesetProfile snapshot.Repository
              | Some profile when profile.PropertyMutationPermitted <> mutationPermitted -> yield InconsistentRulesetProfileAdministration profile.FullName
              | Some _ -> ()
              match selectedClass, snapshot.CensusSnapshot, snapshot.ExpectedCensusSeal, censusReport with
              | ObserveOnlyPlan, None, None, None -> ()
              | ObserveOnlyPlan, _, _, _ -> yield UnexpectedRequiredCheckCensus snapshot.Repository
              | _, None, None, None -> yield MissingRequiredCheckCensus snapshot.Repository
              | _, Some _, Some expected, Some census ->
                  if not (digestLike expected) then yield InvalidRulesetPlanBinding "censusSeal"
                  if census.Repository <> snapshot.Repository then yield CrossRepositoryRequiredCheckCensus census.Repository
                  if census.ProfileSeal <> snapshot.ExpectedProfileSeal then yield AlteredRulesetProfileBinding
              | _, _, _, _ -> yield InvalidRulesetPlanBinding "censusReport"
              for principal in snapshot.ApprovedBypass do
                  if principal.ActorId <= 0L || principal.AllowedProfiles.IsEmpty || principal.AllowedProfiles |> List.distinct |> List.length <> principal.AllowedProfiles.Length then
                      yield InvalidRulesetBypassPrincipal principal.ActorId
              for _, actorId in snapshot.ApprovedBypass |> duplicates approvedKey do yield DuplicateRulesetBypassPrincipal actorId
              for principal in snapshot.RequestedBypass do
                  if principal.ActorId <= 0L then yield InvalidRulesetBypassPrincipal principal.ActorId
              for _, actorId in snapshot.RequestedBypass |> duplicates requestedKey do yield DuplicateRulesetBypassPrincipal actorId
              let approved = snapshot.ApprovedBypass |> List.map (fun value -> approvedKey value, value) |> Map.ofList
              for principal in snapshot.RequestedBypass do
                  match Map.tryFind (requestedKey principal) approved with
                  | Some authority when List.contains selectedClass authority.AllowedProfiles -> ()
                  | _ -> yield UnauthorizedRulesetBypassPrincipal principal.ActorId
              for id in snapshot.Exceptions |> duplicates _.Id do yield DuplicateRulesetException id
              for scope in snapshot.Exceptions |> duplicates exceptionScopeKey do yield InvalidRulesetException scope
              for value in snapshot.Exceptions do
                  if not (validText value.Id) || not (validText value.Owner) || not (validText value.Rationale) then yield InvalidRulesetException value.Id
                  match value.Scope with
                  | RequiredReviewCount count when count < 0 || count > 6 -> yield InvalidRulesetException value.Id
                  | MergeQueue true -> yield InvalidRulesetException value.Id
                  | BypassPrincipal principal when principal.ActorId <= 0L -> yield InvalidRulesetException value.Id
                  | _ -> ()
                  if value.ApprovedAt > value.StartsAt || value.StartsAt > asOf || value.ExpiresAt <= asOf then yield InactiveRulesetException value.Id
                  if exceptionWindow value <= TimeSpan.Zero || exceptionWindow value > TimeSpan.FromDays 30 then yield OverlongRulesetException value.Id
                  match value.Scope with
                  | BypassPrincipal principal ->
                      match Map.tryFind (requestedKey principal) approved with
                      | Some authority when List.contains selectedClass authority.AllowedProfiles -> ()
                      | _ -> yield UnauthorizedRulesetBypassPrincipal principal.ActorId
                  | _ -> ()
              if selectedClass = ObserveOnlyPlan && (not snapshot.RequestedBypass.IsEmpty || not snapshot.Exceptions.IsEmpty) then
                  yield InconsistentRulesetProfileAdministration snapshot.Repository ]
        if not findings.IsEmpty then Error findings
        else
            let exceptions = snapshot.Exceptions |> List.sortBy _.Id
            let bypass =
                snapshot.RequestedBypass
                @ (exceptions |> List.choose (fun value -> match value.Scope with BypassPrincipal principal -> Some principal | _ -> None))
                |> List.distinctBy (fun value -> value.Kind, value.ActorId)
                |> List.sortBy (fun value -> value.Kind, value.ActorId)
            let defaultBranch, releaseTags, repositoryPolicy =
                match selectedClass, censusReport with
                | ObserveOnlyPlan, None -> None, None, None
                | _, Some census ->
                    let checks =
                        census.Entries
                        |> List.map (fun entry -> { Context = entry.Context; IntegrationId = entry.IntegrationId })
                        |> List.sortBy (fun value -> value.Context, value.IntegrationId)
                    let mergeQueueEnabled = census.Aggregate.PullRequestReady && census.Aggregate.MergeGroupReady
                    let disabledReason = if mergeQueueEnabled then None else Some "required-check-census-not-merge-group-ready"
                    let requiredApprovals =
                        exceptions |> List.tryPick (fun value -> match value.Scope with RequiredReviewCount count -> Some count | _ -> None) |> Option.defaultValue 0
                    let conversationResolution =
                        exceptions |> List.tryPick (fun value -> match value.Scope with ConversationResolution required -> Some required | _ -> None) |> Option.defaultValue true
                    let mergeQueueEnabled =
                        if exceptions |> List.exists (fun value -> value.Scope = MergeQueue false) then false else mergeQueueEnabled
                    let disabledReason = if mergeQueueEnabled then None elif disabledReason.IsSome then disabledReason else Some "bounded-exception-disabled"
                    Some
                        { Include = [ "~DEFAULT_BRANCH" ]
                          ProtectDeletion = true
                          BlockNonFastForward = true
                          RequirePullRequest = true
                          DismissStaleReviews = true
                          RequiredApprovals = requiredApprovals
                          RequireConversationResolution = conversationResolution
                          StrictChecks = true
                          RequiredChecks = checks
                          Bypass = bypass
                          MergeQueueEnabled = mergeQueueEnabled
                          MergeQueueDisabledReason = disabledReason },
                    Some
                        { Include = [ "refs/tags/v*" ]
                          ProtectDeletion = true
                          BlockNonFastForward = true
                          BlockUpdate = true
                          RequireSignatures = true
                          Bypass = bypass },
                    Some
                        { AllowedMergeMethods = [ Squash ]
                          AllowAutoMerge = true
                          DeleteBranchOnMerge = true }
                | _ -> failwith "validated administered plan has census"
            Ok
                { Repository = snapshot.Repository
                  ProfileClass = selectedClass
                  MutationPermitted = mutationPermitted
                  PrerequisiteReceiptDigest = snapshot.PrerequisiteReceiptDigest.ToLowerInvariant()
                  ProfileReportSeal = snapshot.ExpectedProfileSeal.ToLowerInvariant()
                  CensusSeal = censusReport |> Option.map (_.Seal >> _.ToLowerInvariant())
                  CurrentPolicyRevision = snapshot.CurrentPolicyRevision.ToLowerInvariant()
                  CurrentPolicyEvidenceSha256 = snapshot.CurrentPolicyEvidenceSha256.ToLowerInvariant()
                  DefaultBranch = defaultBranch
                  ReleaseTags = releaseTags
                  RepositoryPolicy = repositoryPolicy
                  ActiveExceptions = exceptions
                  Seal = seal snapshot selectedProfile.Value censusReport selectedClass defaultBranch releaseTags repositoryPolicy exceptions }

    let verify expectedSeal asOf maxAge snapshot =
        match compile asOf maxAge snapshot with
        | Ok report when report.Seal = expectedSeal -> Ok report
        | Ok _ -> Error [ AlteredRulesetPlanSeal ]
        | Error findings -> Error findings
