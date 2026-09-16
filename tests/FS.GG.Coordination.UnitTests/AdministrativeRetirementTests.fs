module FS.GG.Coordination.AdministrativeRetirementTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open FS.GG.Coordination.GitHub

let now=DateTimeOffset.Parse("2026-09-15T15:00:00Z")
let oid c=String.replicate 40 c
let digest c=String.replicate 64 c
let identity={Repository="FS-GG/example";RepositoryId=1L;IssueNumber=3421;PullRequestNumber=3481;BranchRef="refs/heads/fsgg/pilot/old";ProtectedBaseRef="refs/heads/main";CandidateHead=oid "a";CandidateTree=oid "b";CandidateParent=oid "c";RetirementHead=oid "d";RetirementTree=oid "b";RetirementParent=oid "a";AcceptedClientCommit=oid "e";AcceptedClientArtifactDigest=digest "2";OperationAuthorityDigest=digest "e"}
let observation={ObservedAt=now;CompleteNativeCensus=true;PullRequestDisposition=ClosedUnmerged;PullRequestHead=identity.RetirementHead;MergeCommit=None;MergedCandidateHead=None;MergedCandidateTree=None;MergedCandidateParent=None;ProtectedBaseRef=None;DeliveredPathDigest=None;ObservedBranchHead=Some identity.RetirementHead;RetirementCommitObserved=true;AutoMergeEnabled=false;MergeQueueEntry=None;CandidateArchiveDigest=digest "f";CandidateArchiveLocation="archive://candidate";ArchivedHead=identity.CandidateHead;ArchivedTree=identity.CandidateTree;ArchivedParent=identity.CandidateParent;CandidateArchiveIndependent=true;NativeCensusDigest=digest "3";NativeCensusLocation="evidence://census";BranchFenceRuleId=Some 42L;BranchFenceDigest=Some(digest "1");BranchFenceRef=Some identity.BranchRef;BranchFenceRules=Set ["creation";"deletion";"update:no-fetch-and-merge"];BranchFenceActive=true;BranchFenceHasBypass=false;TemporaryMainRuleId=None;TemporaryMainRuleDigest=None;TemporaryMainRuleRef=None;TemporaryMainRuleRules=Set.empty;TemporaryMainRuleActive=false;SubjectExcluded=true;IssueDisposition=RetirementIssueDisposition.ClosedNotPlanned}

[<Fact>]
let ``lost host retirement is distinct and never fabricates original settlement`` () =
    match AdministrativeRetirement.settle now (TimeSpan.FromMinutes 5.) identity observation with
    | Ok(AdministrativelyRetiredWithLostHistory receipt) ->
        Assert.False(receipt.OriginalJournalAvailable)
        Assert.False(receipt.OriginalCompletionRecorded)
        Assert.False(receipt.OriginalUsageKnown)
        Assert.Equal(identity.CandidateHead,receipt.CandidateHead)
        Assert.Equal(observation.NativeCensusDigest,receipt.NativeCensusDigest)
        Assert.Equal(AdministrativeRetirement.observationDigest identity observation,receipt.ObservationDigest)
        Assert.Equal(receipt.ReceiptDigest,AdministrativeRetirement.receiptDigest receipt)
    | value -> failwithf "unexpected %A" value

[<Fact>]
let ``terminal retirement requires exact closed outcome permanent fence exclusion and complete census`` () =
    let altered={observation with CompleteNativeCensus=false;NativeCensusDigest="";PullRequestDisposition=MergedOther;PullRequestHead=oid "9";AutoMergeEnabled=true;CandidateArchiveIndependent=false;BranchFenceActive=false;BranchFenceHasBypass=true;SubjectExcluded=false;IssueDisposition=RetirementIssueDisposition.IssueOpen;TemporaryMainRuleId=Some 9L;TemporaryMainRuleActive=true}
    match AdministrativeRetirement.settle now (TimeSpan.FromMinutes 5.) identity altered with
    | Error failures ->
        [IncompleteNativeCensus;NativeCensusEvidenceMissing;CandidateIdentityMismatch;CandidateNotIndependentlyPreserved;UnexpectedMergeOutcome;PullRequestMutationStillEnabled;BranchFenceMissing;BranchFenceHasBypass;SubjectExclusionMissing;IssueDispositionMissing;TemporaryMainHoldStillActive]
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

[<Fact>]
let ``preflight permits an exact open pull request while terminal settlement refuses it`` () =
    let initial={observation with PullRequestDisposition=Open;BranchFenceRuleId=None;BranchFenceDigest=None;BranchFenceRef=None;BranchFenceRules=Set.empty;BranchFenceActive=false;SubjectExcluded=false;IssueDisposition=RetirementIssueDisposition.IssueOpen}
    Assert.True(AdministrativeRetirement.validatePreflight now (TimeSpan.FromMinutes 5.) identity initial |> Result.isOk)
    match AdministrativeRetirement.settle now (TimeSpan.FromMinutes 5.) identity initial with
    | Error failures ->
        Assert.Contains(PullRequestOutcomeUnresolved,failures)
        Assert.Contains(BranchFenceMissing,failures)
        Assert.Contains(SubjectExclusionMissing,failures)
    | value -> failwithf "unexpected %A" value

[<Fact>]
let ``merged original head allows only the original tombstone or absent frozen ref`` () =
    let merged={observation with PullRequestDisposition=MergedExactBeforeRetirement;PullRequestHead=identity.CandidateHead;MergeCommit=Some(oid "9");MergedCandidateHead=Some identity.CandidateHead;MergedCandidateTree=Some identity.CandidateTree;MergedCandidateParent=Some identity.CandidateParent;ProtectedBaseRef=Some identity.ProtectedBaseRef;DeliveredPathDigest=Some(digest "8");ObservedBranchHead=Some identity.CandidateHead;RetirementCommitObserved=false;IssueDisposition=ClosedCompleted}
    match AdministrativeRetirement.settle now (TimeSpan.FromMinutes 5.) identity merged with
    | Ok(AdministrativelyRetiredWithLostHistory receipt)->Assert.Equal(Some identity.CandidateHead,receipt.FrozenBranchHead)
    | value->failwithf "unexpected %A" value
    let unrelated={merged with ObservedBranchHead=Some(oid "9")}
    match AdministrativeRetirement.settle now (TimeSpan.FromMinutes 5.) identity unrelated with
    | Error failures->Assert.Contains(RetirementHeadFenceMissing,failures)
    | value->failwithf "unexpected %A" value

[<Fact>]
let ``ruleset scope actions and bypass are validated independently of caller booleans`` () =
    let altered={observation with BranchFenceRef=Some "refs/heads/other";BranchFenceRules=Set ["update:no-fetch-and-merge"];TemporaryMainRuleId=Some 9L;TemporaryMainRuleDigest=Some(digest "8");TemporaryMainRuleRef=Some identity.BranchRef;TemporaryMainRuleRules=Set ["creation"]}
    match AdministrativeRetirement.validatePreflight now (TimeSpan.FromMinutes 5.) identity altered with
    | Error failures ->
        Assert.Contains(BranchFenceScopeMismatch,failures)
        Assert.Contains(TemporaryMainHoldScopeMismatch,failures)
    | value -> failwithf "unexpected %A" value

[<Fact>]
let ``administrative retirement operator adversarial fixtures pass`` () =
    let root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../.."))
    let start=ProcessStartInfo("python3",UseShellExecute=false,WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true)
    start.Environment["PYTHONDONTWRITEBYTECODE"] <- "1"
    start.ArgumentList.Add("eng/test-administrative-retirement.py")
    use child=Process.Start(start)
    Assert.True(child.WaitForExit(60_000),"operator fixture timed out")
    let stdout=child.StandardOutput.ReadToEnd()
    let stderr=child.StandardError.ReadToEnd()
    Assert.True(child.ExitCode=0,$"operator fixture failed\n{stdout}\n{stderr}")

[<Fact>]
let ``operator envelope contains a receipt accepted by the typed contract`` () =
    let root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../.."))
    let output=Path.Combine(Path.GetTempPath(),$"fsgg-retirement-{Guid.NewGuid():N}.json")
    try
        let start=ProcessStartInfo("python3",UseShellExecute=false,WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true)
        start.Environment["PYTHONDONTWRITEBYTECODE"] <- "1"
        start.Environment["FSGG_RETIREMENT_FIXTURE_OUTPUT"] <- output
        start.ArgumentList.Add("eng/test-administrative-retirement.py")
        use child=Process.Start(start)
        let stdout=child.StandardOutput.ReadToEnd()
        let stderr=child.StandardError.ReadToEnd()
        Assert.True(child.WaitForExit(60_000),"operator fixture timed out")
        Assert.True(child.ExitCode=0,$"operator fixture failed\n{stdout}\n{stderr}")
        use document=JsonDocument.Parse(File.ReadAllBytes output)
        let rootJson=document.RootElement
        let i=rootJson.GetProperty("typedIdentity")
        let o=rootJson.GetProperty("typedObservation")
        let r=rootJson.GetProperty("typedReceipt")
        let string (name:string) (value:JsonElement)=value.GetProperty(name).GetString()
        let int64 (name:string) (value:JsonElement)=value.GetProperty(name).GetInt64()
        let int32 (name:string) (value:JsonElement)=value.GetProperty(name).GetInt32()
        let optional (name:string) (value:JsonElement)=let item=value.GetProperty name in if item.ValueKind=JsonValueKind.Null then None else Some(item.GetString())
        let strings (name:string) (value:JsonElement)=value.GetProperty(name).EnumerateArray()|>Seq.map _.GetString()|>Set.ofSeq
        let identity={Repository=string "Repository" i;RepositoryId=int64 "RepositoryId" i;IssueNumber=int32 "IssueNumber" i;PullRequestNumber=int32 "PullRequestNumber" i;BranchRef=string "BranchRef" i;ProtectedBaseRef=string "ProtectedBaseRef" i;CandidateHead=string "CandidateHead" i;CandidateTree=string "CandidateTree" i;CandidateParent=string "CandidateParent" i;RetirementHead=string "RetirementHead" i;RetirementTree=string "RetirementTree" i;RetirementParent=string "RetirementParent" i;AcceptedClientCommit=string "AcceptedClientCommit" i;AcceptedClientArtifactDigest=string "AcceptedClientArtifactDigest" i;OperationAuthorityDigest=string "OperationAuthorityDigest" i}
        let disposition=match string "PullRequestDisposition" o with "closed-unmerged"->ClosedUnmerged|"merged-exact-before-retirement"->MergedExactBeforeRetirement|value->failwith value
        let issue=match string "IssueDisposition" o with "closed-not-planned"->ClosedNotPlanned|"closed-completed"->ClosedCompleted|value->failwith value
        let observation={ObservedAt=DateTimeOffset.Parse(string "ObservedAt" o);CompleteNativeCensus=o.GetProperty("CompleteNativeCensus").GetBoolean();PullRequestDisposition=disposition;PullRequestHead=string "PullRequestHead" o;MergeCommit=optional "MergeCommit" o;MergedCandidateHead=optional "MergedCandidateHead" o;MergedCandidateTree=optional "MergedCandidateTree" o;MergedCandidateParent=optional "MergedCandidateParent" o;ProtectedBaseRef=optional "ProtectedBaseRef" o;DeliveredPathDigest=optional "DeliveredPathDigest" o;ObservedBranchHead=optional "ObservedBranchHead" o;RetirementCommitObserved=o.GetProperty("RetirementCommitObserved").GetBoolean();AutoMergeEnabled=o.GetProperty("AutoMergeEnabled").GetBoolean();MergeQueueEntry=optional "MergeQueueEntry" o;CandidateArchiveDigest=string "CandidateArchiveDigest" o;CandidateArchiveLocation=string "CandidateArchiveLocation" o;ArchivedHead=string "ArchivedHead" o;ArchivedTree=string "ArchivedTree" o;ArchivedParent=string "ArchivedParent" o;CandidateArchiveIndependent=o.GetProperty("CandidateArchiveIndependent").GetBoolean();NativeCensusDigest=string "NativeCensusDigest" o;NativeCensusLocation=string "NativeCensusLocation" o;BranchFenceRuleId=Some(int64 "BranchFenceRuleId" o);BranchFenceDigest=optional "BranchFenceDigest" o;BranchFenceRef=optional "BranchFenceRef" o;BranchFenceRules=strings "BranchFenceRules" o;BranchFenceActive=o.GetProperty("BranchFenceActive").GetBoolean();BranchFenceHasBypass=o.GetProperty("BranchFenceHasBypass").GetBoolean();TemporaryMainRuleId=None;TemporaryMainRuleDigest=None;TemporaryMainRuleRef=None;TemporaryMainRuleRules=strings "TemporaryMainRuleRules" o;TemporaryMainRuleActive=o.GetProperty("TemporaryMainRuleActive").GetBoolean();SubjectExcluded=o.GetProperty("SubjectExcluded").GetBoolean();IssueDisposition=issue}
        let receipt={Schema=string "Schema" r;IdentityDigest=string "IdentityDigest" r;NativeCensusDigest=string "NativeCensusDigest" r;ObservationDigest=string "ObservationDigest" r;Disposition=disposition;CandidateHead=string "CandidateHead" r;CandidateArchiveDigest=string "CandidateArchiveDigest" r;RetirementHead=string "RetirementHead" r;FrozenBranchHead=optional "FrozenBranchHead" r;BranchFenceRuleId=int64 "BranchFenceRuleId" r;SubjectExcluded=r.GetProperty("SubjectExcluded").GetBoolean();OriginalJournalAvailable=r.GetProperty("OriginalJournalAvailable").GetBoolean();OriginalCompletionRecorded=r.GetProperty("OriginalCompletionRecorded").GetBoolean();OriginalUsageKnown=r.GetProperty("OriginalUsageKnown").GetBoolean();RetiredAt=DateTimeOffset.Parse(string "RetiredAt" r);ReceiptDigest=string "ReceiptDigest" r}
        match AdministrativeRetirement.verify observation.ObservedAt (TimeSpan.FromMinutes 1.) identity observation receipt with
        | Ok(AdministrativelyRetiredWithLostHistory accepted)->Assert.Equal(receipt,accepted)
        | value->failwithf "unexpected %A" value
    finally
        if File.Exists output then File.Delete output
