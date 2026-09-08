module FS.GG.Coordination.GitHubEpochWireTests

open Xunit
open FS.GG.Coordination.Qualification.Contracts
module Q = GitHubEpochWireQualification

let digest c = String.replicate 64 c
let authority phase =
    { Schema = Q.schema; FleetId = Q.fleetId; Repository = Q.repository; RepositoryId = Q.repositoryId
      Ref = Q.epochRef; Tag = Q.tagPrefix + Q.phaseName phase + "/" + digest "a"
      GenesisCommit = digest "1"; TrustAnchorSha256 = digest "2"; ManifestSha256 = digest "3"
      Phase = phase; Commit = digest "4"; Parent = digest "5"; Generation = 7L; Complete = true; Fresh = true
      CacheUsedAsAuthority = false; UnknownFields = []; DuplicateFields = [] }
let fence writer eligible =
    { Writer = writer; ExpectedManifestSha256 = digest "3"; ExpectedEpochCommit = digest "4"
      ExpectedEpochGeneration = 7L; ExpectedClaimGeneration = Some 11L; CurrentClaimGeneration = Some 11L
      ExpectedOperationGeneration = 13L; CurrentOperationGeneration = 13L; OperationId = "operation-17"
      EligibleIncumbent = eligible }
let refused = function AdmissionRefused _ -> true | _ -> false

[<Fact>]
let ``state and transition catalogues are complete and exclude obsolete retirement`` () =
    Assert.Equal(11, Q.requiredPhases.Length)
    Assert.Equal(16, Q.requiredTransitions.Length)
    Assert.True(Q.legalTransition OpenV2 ObservingV2)
    Assert.True(Q.legalTransition ObservingV2 ContractingV1)
    Assert.True(Q.legalTransition ContractingV1 OperatingV2)
    Assert.False(Q.legalTransition OpenV2 OperatingV1)
    Assert.False(Q.requiredPhases |> List.map Q.phaseName |> List.contains "RetiringV1")

[<Fact>]
let ``OperatingV1 and Preparing implement the incumbent admission table`` () =
    Assert.Equal(AdmissionAuthorized, Q.admit AuthorityObserved (authority OperatingV1) (fence NewOrdinaryV1 true))
    Assert.Equal(AdmissionAuthorized, Q.admit AuthorityObserved (authority Preparing) (fence IncumbentV1Effect true))
    Assert.True(Q.admit AuthorityObserved (authority Preparing) (fence NewOrdinaryV1 true) |> refused)
    Assert.True(Q.admit AuthorityObserved (authority Preparing) (fence IncumbentV1Effect false) |> refused)
    Assert.True(Q.admit AuthorityObserved (authority RollingBack) (fence IncumbentV1Effect true) |> refused)

[<Fact>]
let ``freeze and every later phase fence ordinary v1 at the effect boundary`` () =
    for phase in [ FreezeRequested; Frozen; SwitchedV2; VerifiedV2; OpenV2; ObservingV2; ContractingV1; OperatingV2 ] do
        Assert.True(Q.admit AuthorityObserved (authority phase) (fence NewOrdinaryV1 true) |> refused, Q.phaseName phase)
        Assert.True(Q.admit AuthorityObserved (authority phase) (fence IncumbentV1Effect true) |> refused, Q.phaseName phase)

[<Fact>]
let ``fresh exact content addressed authority is mandatory`` () =
    let current = authority OperatingV1
    Assert.True(Q.admit AuthorityObserved { current with Fresh = false } (fence NewOrdinaryV1 true) |> refused)
    Assert.True(Q.admit AuthorityObserved { current with CacheUsedAsAuthority = true } (fence NewOrdinaryV1 true) |> refused)
    Assert.True(Q.admit AuthorityObserved { current with Parent = "" } (fence NewOrdinaryV1 true) |> refused)
    Assert.True(Q.admit AuthorityObserved { current with Tag = "" } (fence NewOrdinaryV1 true) |> refused)
    Assert.True(Q.admit AuthorityObserved { current with UnknownFields = [ "future" ] } (fence NewOrdinaryV1 true) |> refused)
    Assert.True(Q.admit AuthorityObserved { current with DuplicateFields = [ "phase" ] } (fence NewOrdinaryV1 true) |> refused)
    Assert.True(Q.admit AuthorityContradictory current (fence NewOrdinaryV1 true) |> refused)
    Assert.True(match Q.admit AuthorityUnreadable current (fence NewOrdinaryV1 true) with AdmissionIndeterminate _ -> true | _ -> false)

[<Fact>]
let ``manifest claim operation and epoch generations fence every effect`` () =
    let current = authority OperatingV1
    let valid = fence NewOrdinaryV1 true
    Assert.True(Q.admit AuthorityObserved current { valid with ExpectedManifestSha256 = digest "9" } |> refused)
    Assert.True(Q.admit AuthorityObserved current { valid with ExpectedEpochGeneration = 6L } |> refused)
    Assert.True(Q.admit AuthorityObserved current { valid with CurrentClaimGeneration = Some 12L } |> refused)
    Assert.True(Q.admit AuthorityObserved current { valid with CurrentOperationGeneration = 14L } |> refused)

[<Fact>]
let ``lost response settles only by exact authoritative reread`` () =
    let expected = fence NewOrdinaryV1 true
    let applied = { Read = AuthorityObserved; OperationId = Some expected.OperationId; EpochCommit = Some expected.ExpectedEpochCommit
                    EpochGeneration = Some expected.ExpectedEpochGeneration; EffectDigest = Some(digest "8"); PartialEffect = false }
    Assert.Equal(SettlementKnownApplied, Q.settleLostResponse expected applied)
    Assert.Equal(SettlementProvenAbsentMayRetry, Q.settleLostResponse expected { applied with OperationId = None; EffectDigest = None })
    Assert.Equal(SettlementPartial, Q.settleLostResponse expected { applied with PartialEffect = true })
    Assert.Equal(SettlementIndeterminate, Q.settleLostResponse expected { applied with Read = AuthorityUnreadable })
    Assert.Equal(SettlementIndeterminate, Q.settleLostResponse expected { applied with OperationId = Some "other" })

[<Fact>]
let ``issue projection is exact and never authority`` () =
    let current = authority ObservingV2
    let projection = Q.projectIssue current
    Assert.False(projection.Authoritative)
    Assert.True(Q.validateProjection current projection)
    Assert.False(Q.validateProjection current { projection with Generation = projection.Generation - 1L })
