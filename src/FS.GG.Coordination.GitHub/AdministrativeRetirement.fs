namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type RetirementPullRequestDisposition = Open | ClosedUnmerged | MergedExactBeforeRetirement | MergedOther
type RetirementIssueDisposition = IssueOpen | ClosedNotPlanned | ClosedCompleted

type RetirementIdentity =
    { Repository: string; RepositoryId: int64; IssueNumber: int; PullRequestNumber: int; BranchRef: string
      CandidateHead: string; CandidateTree: string; CandidateParent: string; AcceptedClientCommit: string
      AcceptedClientArtifactDigest: string; OperationAuthorityDigest: string }

type RetirementObservation =
    { ObservedAt: DateTimeOffset; CompleteNativeCensus: bool; PullRequestDisposition: RetirementPullRequestDisposition
      PullRequestHead: string; MergeCommit: string option; MergedCandidateHead: string option; MergedCandidateTree: string option
      MergedCandidateParent: string option; ProtectedBaseRef: string option; DeliveredPathDigest: string option
      AutoMergeEnabled: bool; MergeQueueEntry: string option; CandidateArchiveDigest: string; CandidateArchiveLocation: string
      ArchivedHead: string; ArchivedTree: string; ArchivedParent: string; CandidateArchiveIndependent: bool
      NativeCensusDigest: string; NativeCensusLocation: string; BranchFenceRuleId: int64 option
      BranchFenceDigest: string option; BranchFenceActive: bool; BranchFenceHasBypass: bool
      TemporaryMainRuleId: int64 option; TemporaryMainRuleDigest: string option; TemporaryMainRuleActive: bool
      SubjectExcluded: bool; IssueDisposition: RetirementIssueDisposition }

type AdministrativeRetirementReceipt =
    { Schema: string; IdentityDigest: string; NativeCensusDigest: string; ObservationDigest: string; Disposition: RetirementPullRequestDisposition
      CandidateHead: string; CandidateArchiveDigest: string; BranchFenceRuleId: int64; SubjectExcluded: bool
      OriginalJournalAvailable: bool; OriginalCompletionRecorded: bool; OriginalUsageKnown: bool
      RetiredAt: DateTimeOffset; ReceiptDigest: string }

type AdministrativeRetirementResult = RetirementPending | AdministrativelyRetiredWithLostHistory of AdministrativeRetirementReceipt

type AdministrativeRetirementFailure =
    | InvalidIdentity of string | StaleOrFutureObservation | IncompleteNativeCensus | CandidateIdentityMismatch
    | CandidateNotIndependentlyPreserved | NativeCensusEvidenceMissing | PullRequestOutcomeUnresolved | UnexpectedMergeOutcome
    | PullRequestMutationStillEnabled | BranchFenceMissing | BranchFenceHasBypass | SubjectExclusionMissing
    | IssueDispositionMissing | TemporaryMainHoldStillActive | FabricatedOriginalHistory | ReceiptDigestMismatch

[<RequireQualifiedAccess>]
module AdministrativeRetirement =
    [<Literal>]
    let ReceiptSchema = "fsgg.coordination.administrative-retirement/1"

    let private sha256 (value:string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private field (value:string) = let value = if isNull value then "" else value in $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private join values = values |> Seq.map field |> String.concat "|"
    let private option value = value |> Option.defaultValue ""
    let private digestLike (value:string) = not(isNull value) && value.Length=64 && value |> Seq.forall Uri.IsHexDigit
    let private oidLike (value:string) = not(isNull value) && value.Length=40 && value |> Seq.forall Uri.IsHexDigit
    let private disposition = function Open->"open" | ClosedUnmerged->"closed-unmerged" | MergedExactBeforeRetirement->"merged-exact-before-retirement" | MergedOther->"merged-other"

    let identityDigest value =
        [ value.Repository; string value.RepositoryId; string value.IssueNumber; string value.PullRequestNumber; value.BranchRef
          value.CandidateHead; value.CandidateTree; value.CandidateParent; value.AcceptedClientCommit; value.AcceptedClientArtifactDigest; value.OperationAuthorityDigest ] |> join |> sha256

    let observationDigest identity value =
        [ identityDigest identity; value.ObservedAt.ToUniversalTime().ToString("O"); string value.CompleteNativeCensus
          disposition value.PullRequestDisposition; value.PullRequestHead; option value.MergeCommit; option value.MergedCandidateHead
          option value.MergedCandidateTree; option value.MergedCandidateParent; option value.ProtectedBaseRef; option value.DeliveredPathDigest
          string value.AutoMergeEnabled; option value.MergeQueueEntry; value.CandidateArchiveDigest; value.CandidateArchiveLocation
          value.ArchivedHead; value.ArchivedTree; value.ArchivedParent; string value.CandidateArchiveIndependent
          value.NativeCensusDigest; value.NativeCensusLocation
          value.BranchFenceRuleId |> Option.map string |> option; option value.BranchFenceDigest; string value.BranchFenceActive
          string value.BranchFenceHasBypass; value.TemporaryMainRuleId |> Option.map string |> option
          option value.TemporaryMainRuleDigest; string value.TemporaryMainRuleActive; string value.SubjectExcluded
          (match value.IssueDisposition with IssueOpen->"open" | ClosedNotPlanned->"closed-not-planned" | ClosedCompleted->"closed-completed") ] |> join |> sha256

    let receiptDigest value =
        [ value.Schema; value.IdentityDigest; value.NativeCensusDigest; value.ObservationDigest; disposition value.Disposition; value.CandidateHead
          value.CandidateArchiveDigest; string value.BranchFenceRuleId; string value.SubjectExcluded
          string value.OriginalJournalAvailable; string value.OriginalCompletionRecorded; string value.OriginalUsageKnown
          value.RetiredAt.ToUniversalTime().ToString("O") ] |> join |> sha256

    let private identityFailures value =
        [ if String.IsNullOrWhiteSpace value.Repository || not(value.Repository.Contains('/')) then InvalidIdentity "repository"
          if value.RepositoryId <= 0L then InvalidIdentity "repositoryId"
          if value.IssueNumber <= 0 then InvalidIdentity "issueNumber"
          if value.PullRequestNumber <= 0 then InvalidIdentity "pullRequestNumber"
          if String.IsNullOrWhiteSpace value.BranchRef || not(value.BranchRef.StartsWith("refs/heads/",StringComparison.Ordinal)) then InvalidIdentity "branchRef"
          if not(oidLike value.CandidateHead) then InvalidIdentity "candidateHead"
          if not(oidLike value.CandidateTree) then InvalidIdentity "candidateTree"
          if not(oidLike value.CandidateParent) then InvalidIdentity "candidateParent"
          if not(oidLike value.AcceptedClientCommit) then InvalidIdentity "acceptedClientCommit"
          if not(digestLike value.AcceptedClientArtifactDigest) then InvalidIdentity "acceptedClientArtifactDigest"
          if not(digestLike value.OperationAuthorityDigest) then InvalidIdentity "operationAuthorityDigest" ]

    let validatePreview now maxAge identity observation =
        let findings =
            [ yield! identityFailures identity
              if maxAge <= TimeSpan.Zero || observation.ObservedAt > now || now-observation.ObservedAt > maxAge then StaleOrFutureObservation
              if not observation.CompleteNativeCensus then IncompleteNativeCensus
              if observation.PullRequestHead <> identity.CandidateHead then CandidateIdentityMismatch
              if not(digestLike observation.CandidateArchiveDigest) || String.IsNullOrWhiteSpace observation.CandidateArchiveLocation || not observation.CandidateArchiveIndependent || observation.ArchivedHead<>identity.CandidateHead || observation.ArchivedTree<>identity.CandidateTree || observation.ArchivedParent<>identity.CandidateParent then CandidateNotIndependentlyPreserved
              if not(digestLike observation.NativeCensusDigest) || String.IsNullOrWhiteSpace observation.NativeCensusLocation then NativeCensusEvidenceMissing
              match observation.PullRequestDisposition with
              | Open -> PullRequestOutcomeUnresolved
              | MergedOther -> UnexpectedMergeOutcome
              | MergedExactBeforeRetirement when observation.MergeCommit |> Option.exists (oidLike >> not) -> UnexpectedMergeOutcome
              | MergedExactBeforeRetirement when observation.MergeCommit.IsNone || observation.MergedCandidateHead<>Some identity.CandidateHead || observation.MergedCandidateTree<>Some identity.CandidateTree || observation.MergedCandidateParent<>Some identity.CandidateParent || observation.ProtectedBaseRef<>Some "refs/heads/main" || observation.DeliveredPathDigest |> Option.exists (digestLike >> not) || observation.DeliveredPathDigest.IsNone -> UnexpectedMergeOutcome
              | ClosedUnmerged when observation.MergeCommit.IsSome -> UnexpectedMergeOutcome
              | _ -> ()
              if observation.AutoMergeEnabled || observation.MergeQueueEntry.IsSome then PullRequestMutationStillEnabled
              if not observation.BranchFenceActive || observation.BranchFenceRuleId |> Option.exists ((>=) 0L) || observation.BranchFenceRuleId.IsNone || observation.BranchFenceDigest |> Option.exists (digestLike >> not) || observation.BranchFenceDigest.IsNone then BranchFenceMissing
              if observation.BranchFenceHasBypass then BranchFenceHasBypass
              if not observation.SubjectExcluded then SubjectExclusionMissing
              match observation.PullRequestDisposition,observation.IssueDisposition with
              | ClosedUnmerged,ClosedNotPlanned | MergedExactBeforeRetirement,ClosedCompleted -> ()
              | _ -> IssueDispositionMissing
              if observation.TemporaryMainRuleActive || observation.TemporaryMainRuleId.IsSome || observation.TemporaryMainRuleDigest.IsSome then TemporaryMainHoldStillActive ] |> List.distinct
        if findings.IsEmpty then Ok observation else Error findings

    let settle now maxAge identity observation =
        validatePreview now maxAge identity observation
        |> Result.map(fun value ->
            let receipt =
                { Schema=ReceiptSchema; IdentityDigest=identityDigest identity; NativeCensusDigest=value.NativeCensusDigest; ObservationDigest=observationDigest identity value
                  Disposition=value.PullRequestDisposition; CandidateHead=identity.CandidateHead; CandidateArchiveDigest=value.CandidateArchiveDigest
                  BranchFenceRuleId=value.BranchFenceRuleId.Value; SubjectExcluded=true; OriginalJournalAvailable=false
                  OriginalCompletionRecorded=false; OriginalUsageKnown=false; RetiredAt=value.ObservedAt; ReceiptDigest="" }
            let receipt={receipt with ReceiptDigest=receiptDigest receipt}
            AdministrativelyRetiredWithLostHistory receipt)

    let verify now maxAge identity observation receipt =
        match settle now maxAge identity observation with
        | Error errors -> Error errors
        | Ok(AdministrativelyRetiredWithLostHistory expected) ->
            let findings =
                [ if receipt.OriginalJournalAvailable || receipt.OriginalCompletionRecorded || receipt.OriginalUsageKnown then FabricatedOriginalHistory
                  if receipt <> expected || receipt.ReceiptDigest <> receiptDigest receipt then ReceiptDigestMismatch ]
            if findings.IsEmpty then Ok(AdministrativelyRetiredWithLostHistory receipt) else Error findings
        | Ok RetirementPending -> Ok RetirementPending
