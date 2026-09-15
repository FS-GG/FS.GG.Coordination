namespace FS.GG.Coordination.UnitTests

open System
open Xunit
open FS.GG.Coordination.GitHub

module AdministrativeRetirementTests =
    let now=DateTimeOffset.Parse("2026-09-15T15:00:00Z")
    let oid c=String.replicate 40 c
    let digest c=String.replicate 64 c
    let identity={Repository="FS-GG/example";RepositoryId=1L;IssueNumber=3421;PullRequestNumber=3481;BranchRef="refs/heads/fsgg/pilot/old";CandidateHead=oid "a";CandidateTree=oid "b";CandidateParent=oid "c";AcceptedClientSource=digest "d";OperationAuthorityDigest=digest "e"}
    let observation={ObservedAt=now;CompleteNativeCensus=true;PullRequestDisposition=ClosedUnmerged;PullRequestHead=identity.CandidateHead;MergeCommit=None;AutoMergeEnabled=false;MergeQueueEntry=None;CandidateArchiveDigest=digest "f";CandidateArchiveIndependent=true;BranchFenceRuleId=Some 42L;BranchFenceDigest=Some(digest "1");BranchFenceActive=true;BranchFenceHasBypass=false;TemporaryMainRuleId=None;TemporaryMainRuleDigest=None;TemporaryMainRuleActive=false;SubjectExcluded=true;IssueClosedNotPlanned=true}

    [<Fact>]
    let ``lost host retirement is distinct and never fabricates original settlement`` () =
        match AdministrativeRetirement.settle now (TimeSpan.FromMinutes 5.) identity observation with
        | Ok(AdministrativelyRetiredWithLostHistory receipt) ->
            Assert.False(receipt.OriginalJournalAvailable)
            Assert.False(receipt.OriginalCompletionRecorded)
            Assert.False(receipt.OriginalUsageKnown)
            Assert.Equal(identity.CandidateHead,receipt.CandidateHead)
            Assert.Equal(receipt.ReceiptDigest,AdministrativeRetirement.receiptDigest receipt)
        | value -> failwithf "unexpected %A" value

    [<Fact>]
    let ``terminal retirement requires exact closed outcome permanent fence exclusion and complete census`` () =
        let altered={observation with CompleteNativeCensus=false;PullRequestDisposition=MergedOther;PullRequestHead=oid "9";AutoMergeEnabled=true;CandidateArchiveIndependent=false;BranchFenceActive=false;BranchFenceHasBypass=true;SubjectExcluded=false;IssueClosedNotPlanned=false;TemporaryMainRuleId=Some 9L;TemporaryMainRuleActive=true}
        match AdministrativeRetirement.settle now (TimeSpan.FromMinutes 5.) identity altered with
        | Error failures ->
            [IncompleteNativeCensus;CandidateIdentityMismatch;CandidateNotIndependentlyPreserved;UnexpectedMergeOutcome;PullRequestMutationStillEnabled;BranchFenceMissing;BranchFenceHasBypass;SubjectExclusionMissing;IssueDispositionMissing;TemporaryMainHoldStillActive]
            |> List.iter(fun expected -> Assert.Contains(expected,failures))
        | value -> failwithf "unexpected %A" value

    [<Fact>]
    let ``receipt with invented old history is refused`` () =
        let receipt=
            match AdministrativeRetirement.settle now (TimeSpan.FromMinutes 5.) identity observation with
            | Ok(AdministrativelyRetiredWithLostHistory value)->value
            | value->failwithf "unexpected %A" value
        let altered={receipt with OriginalCompletionRecorded=true;ReceiptDigest=AdministrativeRetirement.receiptDigest {receipt with OriginalCompletionRecorded=true}}
        match AdministrativeRetirement.verify now (TimeSpan.FromMinutes 5.) identity observation altered with
        | Error failures -> Assert.Contains(FabricatedOriginalHistory,failures)
        | value -> failwithf "unexpected %A" value
