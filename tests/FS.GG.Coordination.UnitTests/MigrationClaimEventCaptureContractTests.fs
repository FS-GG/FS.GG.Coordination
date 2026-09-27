module FS.GG.Coordination.MigrationClaimEventCaptureContractTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Cli

let private sha (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData
    |> Convert.ToHexString |> _.ToLowerInvariant()

let private read path next =
    let request =
        { Kind="rest"; Method="Get"; Uri="https://api.github.test/" + path
          Headers=Map.empty; Body=None; Variables=Map.empty; ApiVersion="2022-11-28"; Idempotency="ReplaySafe" }
    { Request=request; RequestSha256=MigrationReviewDeliveryCaptureContract.requestSha256 request
      StatusCode=200; ResponseHeaders=Map.empty; RawBody="[]"; RawSha256=sha "[]"; NextRequestUri=next }

let private repository =
    { RepositoryId=1L; NodeId="R_1"; FullName="FS-GG/copy"
      Read=read "repos/FS-GG/copy" None }

let private census (namespace': MigrationClaimJournalNamespace) (prefix: string) =
    let relative = prefix.Substring("refs/".Length)
    { ClaimNamespace=namespace'; ClaimPrefix=prefix
      ClaimNamespaceReads=[ read ($"repos/FS-GG/copy/git/matching-refs/{relative}") None ]
      ClaimRefs=[] }

let private emptyPass () =
    let partial =
        { ClaimRepository=repository
          ClaimNamespaces=
            [ census ClaimJournalNamespace MigrationClaimEventCaptureContract.claimRefPrefix
              census OperationJournalNamespace MigrationClaimEventCaptureContract.operationRefPrefix ]
          ClaimHistories=[]; ClaimFingerprint="" }
    { partial with ClaimFingerprint=MigrationClaimEventCaptureContract.passFingerprint partial }

[<Fact>]
let ``claim event contract freezes namespaces native roster and schema families`` () =
    Assert.Equal<string list>(
        [ "refs/heads/fsgg/v2/journal/claim/"
          "refs/heads/fsgg/v2/journal/operation/" ],
        MigrationClaimEventCaptureContract.journalRefPrefixes)
    Assert.Equal<MigrationClaimNativeStreamKind list>(
        [ NativeIssueComments; NativeIssueEvents; NativeIssueTimeline ],
        MigrationClaimEventCaptureContract.requiredNativeStreams NativeIssue)
    Assert.Equal<MigrationClaimNativeStreamKind list>(
        [ NativeIssueComments; NativeIssueEvents; NativeIssueTimeline ],
        MigrationClaimEventCaptureContract.requiredNativeStreams NativePullRequest)
    Assert.True(MigrationClaimEventCaptureContract.schemaFamilyAllowed ClaimJournalNamespace ClaimSchemaFamily)
    Assert.False(MigrationClaimEventCaptureContract.schemaFamilyAllowed ClaimJournalNamespace OrdinarySchemaFamily)
    Assert.True(MigrationClaimEventCaptureContract.schemaFamilyAllowed OperationJournalNamespace AdmissionSchemaFamily)
    Assert.True(MigrationClaimEventCaptureContract.schemaFamilyAllowed OperationJournalNamespace OrdinarySchemaFamily)
    Assert.True(MigrationClaimEventCaptureContract.schemaFamilyAllowed OperationJournalNamespace ReviewSchemaFamily)
    Assert.False(MigrationClaimEventCaptureContract.schemaFamilyAllowed OperationJournalNamespace ClaimSchemaFamily)

[<Fact>]
let ``empty terminal namespace censuses validate across two equal passes`` () =
    let pass = emptyPass ()
    Assert.Equal(
        Ok { ClaimFirst=pass; ClaimSecond=pass },
        MigrationClaimEventCaptureContract.validateTwoPass { ClaimFirst=pass; ClaimSecond=pass })

[<Fact>]
let ``matching refs continuation and pass drift refuse`` () =
    let pass = emptyPass ()
    let claim = pass.ClaimNamespaces.Head
    let paged =
        { claim with
            ClaimNamespaceReads =
                [ read "repos/FS-GG/copy/git/matching-refs/heads/fsgg/v2/journal/claim/"
                    (Some "https://api.github.test/next") ] }
    let changed = { pass with ClaimNamespaces=paged :: pass.ClaimNamespaces.Tail }
    let changed = { changed with ClaimFingerprint=MigrationClaimEventCaptureContract.passFingerprint changed }
    Assert.Equal(
        Error "claim-journal-namespace-census",
        MigrationClaimEventCaptureContract.validateTwoPass { ClaimFirst=changed; ClaimSecond=changed })
    let second = { pass with ClaimRepository={ repository with NodeId="R_2" }; ClaimFingerprint="" }
    let second = { second with ClaimFingerprint=MigrationClaimEventCaptureContract.passFingerprint second }
    Assert.Equal(
        Error "claim-journal-pass-drift",
        MigrationClaimEventCaptureContract.validateTwoPass { ClaimFirst=pass; ClaimSecond=second })
