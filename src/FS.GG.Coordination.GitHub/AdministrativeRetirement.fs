namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type RetirementPullRequestDisposition =
    | Open
    | ClosedUnmerged
    | MergedExactBeforeRetirement
    | MergedOther

type RetirementIssueDisposition =
    | IssueOpen
    | ClosedNotPlanned
    | ClosedCompleted

type RetirementIdentity =
    {
        Repository: string
        RepositoryId: int64
        IssueNumber: int
        PullRequestNumber: int
        BranchRef: string
        ProtectedBaseRef: string
        CandidateHead: string
        CandidateTree: string
        CandidateParent: string
        RetirementHead: string
        RetirementTree: string
        RetirementParent: string
        AcceptedClientCommit: string
        AcceptedClientArtifactDigest: string
        OperationAuthorityDigest: string
    }

type RetirementObservation =
    {
        ObservedAt: DateTimeOffset
        CompleteNativeCensus: bool
        PullRequestDisposition: RetirementPullRequestDisposition
        PullRequestHead: string
        MergeCommit: string option
        MergedCandidateHead: string option
        MergedCandidateTree: string option
        MergedCandidateParent: string option
        ProtectedBaseRef: string option
        DeliveredPathDigest: string option
        ObservedBranchHead: string option
        RetirementCommitObserved: bool
        AutoMergeEnabled: bool
        MergeQueueEntry: string option
        CandidateArchiveDigest: string
        CandidateArchiveLocation: string
        ArchivedHead: string
        ArchivedTree: string
        ArchivedParent: string
        CandidateArchiveIndependent: bool
        NativeCensusDigest: string
        NativeCensusLocation: string
        BranchFenceRuleId: int64 option
        BranchFenceDigest: string option
        BranchFenceRef: string option
        BranchFenceRules: Set<string>
        BranchFenceActive: bool
        BranchFenceHasBypass: bool
        TemporaryMainRuleId: int64 option
        TemporaryMainRuleDigest: string option
        TemporaryMainRuleRef: string option
        TemporaryMainRuleRules: Set<string>
        TemporaryMainRuleActive: bool
        SubjectExcluded: bool
        IssueDisposition: RetirementIssueDisposition
    }

type AdministrativeRetirementReceipt =
    {
        Schema: string
        IdentityDigest: string
        NativeCensusDigest: string
        ObservationDigest: string
        Disposition: RetirementPullRequestDisposition
        CandidateHead: string
        CandidateArchiveDigest: string
        RetirementHead: string
        FrozenBranchHead: string option
        BranchFenceRuleId: int64
        SubjectExcluded: bool
        OriginalJournalAvailable: bool
        OriginalCompletionRecorded: bool
        OriginalUsageKnown: bool
        RetiredAt: DateTimeOffset
        ReceiptDigest: string
    }

type AdministrativeRetirementResult =
    | RetirementPending
    | AdministrativelyRetiredWithLostHistory of AdministrativeRetirementReceipt

type AdministrativeRetirementFailure =
    | InvalidIdentity of string
    | StaleOrFutureObservation
    | IncompleteNativeCensus
    | CandidateIdentityMismatch
    | CandidateNotIndependentlyPreserved
    | NativeCensusEvidenceMissing
    | PullRequestOutcomeUnresolved
    | UnexpectedMergeOutcome
    | PullRequestMutationStillEnabled
    | BranchFenceMissing
    | BranchFenceScopeMismatch
    | BranchFenceHasBypass
    | SubjectExclusionMissing
    | RetirementHeadFenceMissing
    | IssueDispositionMissing
    | TemporaryMainHoldStillActive
    | TemporaryMainHoldScopeMismatch
    | FabricatedOriginalHistory
    | ReceiptDigestMismatch

[<RequireQualifiedAccess>]
module AdministrativeRetirement =
    [<Literal>]
    let ReceiptSchema = "fsgg.coordination.administrative-retirement/1"

    let private sha256 (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private field (value: string) =
        let value = if isNull value then "" else value in $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private join values =
        values |> Seq.map field |> String.concat "|"

    let private option value = value |> Option.defaultValue ""

    let private digestLike (value: string) =
        not (isNull value) && value.Length = 64 && value |> Seq.forall Uri.IsHexDigit

    let private oidLike (value: string) =
        not (isNull value) && value.Length = 40 && value |> Seq.forall Uri.IsHexDigit

    let private disposition =
        function
        | Open -> "open"
        | ClosedUnmerged -> "closed-unmerged"
        | MergedExactBeforeRetirement -> "merged-exact-before-retirement"
        | MergedOther -> "merged-other"

    let identityDigest value =
        [
            value.Repository
            string value.RepositoryId
            string value.IssueNumber
            string value.PullRequestNumber
            value.BranchRef
            value.ProtectedBaseRef
            value.CandidateHead
            value.CandidateTree
            value.CandidateParent
            value.RetirementHead
            value.RetirementTree
            value.RetirementParent
            value.AcceptedClientCommit
            value.AcceptedClientArtifactDigest
            value.OperationAuthorityDigest
        ]
        |> join
        |> sha256

    let observationDigest identity value =
        [
            identityDigest identity
            value.ObservedAt.ToUniversalTime().ToString("O")
            string value.CompleteNativeCensus
            disposition value.PullRequestDisposition
            value.PullRequestHead
            option value.MergeCommit
            option value.MergedCandidateHead
            option value.MergedCandidateTree
            option value.MergedCandidateParent
            option value.ProtectedBaseRef
            option value.DeliveredPathDigest
            option value.ObservedBranchHead
            string value.RetirementCommitObserved
            string value.AutoMergeEnabled
            option value.MergeQueueEntry
            value.CandidateArchiveDigest
            value.CandidateArchiveLocation
            value.ArchivedHead
            value.ArchivedTree
            value.ArchivedParent
            string value.CandidateArchiveIndependent
            value.NativeCensusDigest
            value.NativeCensusLocation
            value.BranchFenceRuleId |> Option.map string |> option
            option value.BranchFenceDigest
            string value.BranchFenceActive
            option value.BranchFenceRef
            value.BranchFenceRules |> Set.toList |> List.sort |> String.concat ","
            string value.BranchFenceHasBypass
            value.TemporaryMainRuleId |> Option.map string |> option
            option value.TemporaryMainRuleDigest
            option value.TemporaryMainRuleRef
            value.TemporaryMainRuleRules |> Set.toList |> List.sort |> String.concat ","
            string value.TemporaryMainRuleActive
            string value.SubjectExcluded
            (match value.IssueDisposition with
             | IssueOpen -> "open"
             | ClosedNotPlanned -> "closed-not-planned"
             | ClosedCompleted -> "closed-completed")
        ]
        |> join
        |> sha256

    let receiptDigest value =
        [
            value.Schema
            value.IdentityDigest
            value.NativeCensusDigest
            value.ObservationDigest
            disposition value.Disposition
            value.CandidateHead
            value.CandidateArchiveDigest
            value.RetirementHead
            option value.FrozenBranchHead
            string value.BranchFenceRuleId
            string value.SubjectExcluded
            string value.OriginalJournalAvailable
            string value.OriginalCompletionRecorded
            string value.OriginalUsageKnown
            value.RetiredAt.ToUniversalTime().ToString("O")
        ]
        |> join
        |> sha256

    let private identityFailures value =
        [
            if
                String.IsNullOrWhiteSpace value.Repository
                || not (value.Repository.Contains('/'))
            then
                InvalidIdentity "repository"
            if value.RepositoryId <= 0L then
                InvalidIdentity "repositoryId"
            if value.IssueNumber <= 0 then
                InvalidIdentity "issueNumber"
            if value.PullRequestNumber <= 0 then
                InvalidIdentity "pullRequestNumber"
            if
                String.IsNullOrWhiteSpace value.BranchRef
                || not (value.BranchRef.StartsWith("refs/heads/", StringComparison.Ordinal))
            then
                InvalidIdentity "branchRef"
            if
                String.IsNullOrWhiteSpace value.ProtectedBaseRef
                || not (value.ProtectedBaseRef.StartsWith("refs/heads/", StringComparison.Ordinal))
                || value.ProtectedBaseRef = value.BranchRef
            then
                InvalidIdentity "protectedBaseRef"
            if not (oidLike value.CandidateHead) then
                InvalidIdentity "candidateHead"
            if not (oidLike value.CandidateTree) then
                InvalidIdentity "candidateTree"
            if not (oidLike value.CandidateParent) then
                InvalidIdentity "candidateParent"
            if not (oidLike value.RetirementHead) || value.RetirementHead = value.CandidateHead then
                InvalidIdentity "retirementHead"
            if value.RetirementTree <> value.CandidateTree then
                InvalidIdentity "retirementTree"
            if value.RetirementParent <> value.CandidateHead then
                InvalidIdentity "retirementParent"
            if not (oidLike value.AcceptedClientCommit) then
                InvalidIdentity "acceptedClientCommit"
            if not (digestLike value.AcceptedClientArtifactDigest) then
                InvalidIdentity "acceptedClientArtifactDigest"
            if not (digestLike value.OperationAuthorityDigest) then
                InvalidIdentity "operationAuthorityDigest"
        ]

    let validatePreflight now maxAge identity observation =
        let findings =
            [
                yield! identityFailures identity
                if
                    maxAge <= TimeSpan.Zero
                    || observation.ObservedAt > now
                    || now - observation.ObservedAt > maxAge
                then
                    StaleOrFutureObservation
                if not observation.CompleteNativeCensus then
                    IncompleteNativeCensus
                if
                    observation.PullRequestHead <> identity.CandidateHead
                    && observation.PullRequestHead <> identity.RetirementHead
                then
                    CandidateIdentityMismatch
                if
                    observation.ObservedBranchHead
                    |> Option.exists (fun head -> head <> identity.CandidateHead && head <> identity.RetirementHead)
                then
                    RetirementHeadFenceMissing
                if
                    observation.ObservedBranchHead = Some identity.RetirementHead
                    && not observation.RetirementCommitObserved
                then
                    RetirementHeadFenceMissing
                if
                    not (digestLike observation.CandidateArchiveDigest)
                    || String.IsNullOrWhiteSpace observation.CandidateArchiveLocation
                    || not observation.CandidateArchiveIndependent
                    || observation.ArchivedHead <> identity.CandidateHead
                    || observation.ArchivedTree <> identity.CandidateTree
                    || observation.ArchivedParent <> identity.CandidateParent
                then
                    CandidateNotIndependentlyPreserved
                if
                    not (digestLike observation.NativeCensusDigest)
                    || String.IsNullOrWhiteSpace observation.NativeCensusLocation
                then
                    NativeCensusEvidenceMissing
                match observation.PullRequestDisposition with
                | Open -> ()
                | MergedOther -> UnexpectedMergeOutcome
                | MergedExactBeforeRetirement when observation.MergeCommit |> Option.exists (oidLike >> not) ->
                    UnexpectedMergeOutcome
                | MergedExactBeforeRetirement when
                    observation.MergeCommit.IsNone
                    || observation.MergedCandidateHead <> Some identity.CandidateHead
                    || observation.MergedCandidateTree <> Some identity.CandidateTree
                    || observation.MergedCandidateParent <> Some identity.CandidateParent
                    || observation.ProtectedBaseRef <> Some identity.ProtectedBaseRef
                    || observation.DeliveredPathDigest |> Option.exists (digestLike >> not)
                    || observation.DeliveredPathDigest.IsNone
                    ->
                    UnexpectedMergeOutcome
                | ClosedUnmerged when observation.MergeCommit.IsSome -> UnexpectedMergeOutcome
                | _ -> ()
                if
                    observation.BranchFenceRuleId.IsSome
                    && (observation.BranchFenceRef <> Some identity.BranchRef
                        || observation.BranchFenceRules
                           <> Set [ "creation"; "deletion"; "update:no-fetch-and-merge" ])
                then
                    BranchFenceScopeMismatch
                if
                    observation.TemporaryMainRuleId.IsSome
                    && (observation.TemporaryMainRuleRef <> Some identity.ProtectedBaseRef
                        || observation.TemporaryMainRuleRules <> Set [ "update:no-fetch-and-merge" ])
                then
                    TemporaryMainHoldScopeMismatch
                if observation.BranchFenceDigest |> Option.exists (digestLike >> not) then
                    BranchFenceMissing
                if observation.TemporaryMainRuleDigest |> Option.exists (digestLike >> not) then
                    TemporaryMainHoldScopeMismatch
                if observation.BranchFenceHasBypass then
                    BranchFenceHasBypass
            ]
            |> List.distinct

        if findings.IsEmpty then Ok observation else Error findings

    let validatePreview now maxAge identity observation =
        let findings =
            [
                match validatePreflight now maxAge identity observation with
                | Error values -> yield! values
                | Ok _ -> ()
                if observation.PullRequestDisposition = Open then
                    PullRequestOutcomeUnresolved
                match observation.PullRequestDisposition with
                | ClosedUnmerged when
                    observation.PullRequestHead <> identity.RetirementHead
                    || observation.ObservedBranchHead <> Some identity.RetirementHead
                    || not observation.RetirementCommitObserved
                    ->
                    RetirementHeadFenceMissing
                | MergedExactBeforeRetirement when observation.PullRequestHead <> identity.CandidateHead ->
                    RetirementHeadFenceMissing
                | MergedExactBeforeRetirement when
                    observation.ObservedBranchHead = Some identity.RetirementHead
                    && not observation.RetirementCommitObserved
                    ->
                    RetirementHeadFenceMissing
                | _ -> ()
                if observation.AutoMergeEnabled || observation.MergeQueueEntry.IsSome then
                    PullRequestMutationStillEnabled
                if
                    not observation.BranchFenceActive
                    || observation.BranchFenceRuleId |> Option.exists ((>=) 0L)
                    || observation.BranchFenceRuleId.IsNone
                    || observation.BranchFenceDigest.IsNone
                then
                    BranchFenceMissing
                if not observation.SubjectExcluded then
                    SubjectExclusionMissing
                match observation.PullRequestDisposition, observation.IssueDisposition with
                | ClosedUnmerged, ClosedNotPlanned
                | MergedExactBeforeRetirement, ClosedCompleted -> ()
                | _ -> IssueDispositionMissing
                if
                    observation.TemporaryMainRuleActive
                    || observation.TemporaryMainRuleId.IsSome
                    || observation.TemporaryMainRuleDigest.IsSome
                    || observation.TemporaryMainRuleRef.IsSome
                    || not observation.TemporaryMainRuleRules.IsEmpty
                then
                    TemporaryMainHoldStillActive
            ]
            |> List.distinct

        if findings.IsEmpty then Ok observation else Error findings

    let settle now maxAge identity observation =
        validatePreview now maxAge identity observation
        |> Result.map (fun value ->
            let receipt =
                {
                    Schema = ReceiptSchema
                    IdentityDigest = identityDigest identity
                    NativeCensusDigest = value.NativeCensusDigest
                    ObservationDigest = observationDigest identity value
                    Disposition = value.PullRequestDisposition
                    CandidateHead = identity.CandidateHead
                    CandidateArchiveDigest = value.CandidateArchiveDigest
                    RetirementHead = identity.RetirementHead
                    FrozenBranchHead = value.ObservedBranchHead
                    BranchFenceRuleId = value.BranchFenceRuleId.Value
                    SubjectExcluded = true
                    OriginalJournalAvailable = false
                    OriginalCompletionRecorded = false
                    OriginalUsageKnown = false
                    RetiredAt = value.ObservedAt
                    ReceiptDigest = ""
                }

            let receipt =
                { receipt with
                    ReceiptDigest = receiptDigest receipt
                }

            AdministrativelyRetiredWithLostHistory receipt)

    let verify now maxAge identity observation receipt =
        match settle now maxAge identity observation with
        | Error errors -> Error errors
        | Ok(AdministrativelyRetiredWithLostHistory expected) ->
            let findings =
                [
                    if
                        receipt.OriginalJournalAvailable
                        || receipt.OriginalCompletionRecorded
                        || receipt.OriginalUsageKnown
                    then
                        FabricatedOriginalHistory
                    if receipt <> expected || receipt.ReceiptDigest <> receiptDigest receipt then
                        ReceiptDigestMismatch
                ]

            if findings.IsEmpty then
                Ok(AdministrativelyRetiredWithLostHistory receipt)
            else
                Error findings
        | Ok RetirementPending -> Ok RetirementPending
