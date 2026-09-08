namespace FS.GG.Coordination.Qualification.Contracts

open System

type EpochPhase =
    | OperatingV1 | Preparing | FreezeRequested | Frozen | SwitchedV2 | VerifiedV2
    | OpenV2 | ObservingV2 | ContractingV1 | OperatingV2 | RollingBack
type WriterClass = NewOrdinaryV1 | IncumbentV1Effect | OrdinaryV2 | CutoverControl | RollbackControl
type AuthorityRead = Observed | Unreadable | Contradictory | Partial
type Admission = Authorized | Refused of string list | Indeterminate of string
type Settlement = KnownApplied | ProvenAbsentMayRetry | EffectPartial | SettlementIndeterminate
type EpochAuthority = {
    Schema: string; FleetId: string; Repository: string; RepositoryId: int64; Ref: string
    Tag: string; GenesisCommit: string; TrustAnchorSha256: string; ManifestSha256: string
    Phase: EpochPhase; Commit: string; Parent: string; Generation: int64
    Complete: bool; Fresh: bool; CacheUsedAsAuthority: bool; UnknownFields: string list; DuplicateFields: string list }
type EffectFence = {
    Writer: WriterClass; ExpectedManifestSha256: string; ExpectedEpochCommit: string
    ExpectedEpochGeneration: int64; ExpectedClaimGeneration: int64 option; CurrentClaimGeneration: int64 option
    ExpectedOperationGeneration: int64; CurrentOperationGeneration: int64
    OperationId: string; EligibleIncumbent: bool }
type EffectReadback = {
    Read: AuthorityRead; OperationId: string option; EpochCommit: string option
    EpochGeneration: int64 option; EffectDigest: string option; PartialEffect: bool }
type IssueProjection = {
    FleetId: string; ManifestSha256: string; EpochCommit: string; Generation: int64; Phase: EpochPhase
    SourceRef: string; Authoritative: bool }

module GitHubEpochWireQualification =
    let schema = "fsgg.github-substrate.epoch-wire/1"
    let fleetId = "fs-gg-production"
    let repository = "FS-GG/FS.GG.Coordination.Authority"
    let repositoryId = 1351660651L
    let epochRef = "refs/heads/fsgg/v2/journal/fleet-cutover/9f"
    let tagPrefix = "refs/tags/fsgg/v2/fleet-cutover/"
    let requiredPhases =
        [ OperatingV1; Preparing; FreezeRequested; Frozen; SwitchedV2; VerifiedV2
          OpenV2; ObservingV2; ContractingV1; OperatingV2; RollingBack ]
    let requiredTransitions =
        [ OperatingV1, Preparing; Preparing, FreezeRequested; FreezeRequested, Frozen
          Frozen, SwitchedV2; SwitchedV2, VerifiedV2; VerifiedV2, OpenV2
          OpenV2, ObservingV2; ObservingV2, ObservingV2; ObservingV2, ContractingV1
          ContractingV1, OperatingV2
          Preparing, RollingBack; FreezeRequested, RollingBack; Frozen, RollingBack
          SwitchedV2, RollingBack; VerifiedV2, RollingBack; RollingBack, OperatingV1 ]
    let phaseName = function
        | OperatingV1 -> "OperatingV1" | Preparing -> "Preparing" | FreezeRequested -> "FreezeRequested"
        | Frozen -> "Frozen" | SwitchedV2 -> "SwitchedV2" | VerifiedV2 -> "VerifiedV2"
        | OpenV2 -> "OpenV2" | ObservingV2 -> "ObservingV2" | ContractingV1 -> "ContractingV1"
        | OperatingV2 -> "OperatingV2" | RollingBack -> "RollingBack"
    let legalTransition current proposed = List.contains (current, proposed) requiredTransitions
    let private digest value = value.Length = 64 && value |> Seq.forall Uri.IsHexDigit
    let validateAuthority read authority =
        [ if read = Unreadable then "authority-unreadable"
          if read = Contradictory then "authority-contradictory"
          if read = Partial || not authority.Complete then "authority-partial"
          if not authority.Fresh then "authority-stale"
          if authority.CacheUsedAsAuthority then "cache-cannot-authorize"
          if authority.Schema <> schema then "wrong-schema"
          if authority.FleetId <> fleetId then "wrong-fleet"
          if authority.Repository <> repository || authority.RepositoryId <> repositoryId then "wrong-authority-repository"
          if authority.Ref <> epochRef then "wrong-ref"
          if not(authority.Tag.StartsWith(tagPrefix, StringComparison.Ordinal)) then "missing-or-wrong-tag"
          if authority.GenesisCommit = "" || authority.Parent = "" || authority.Commit = "" then "missing-parent-or-genesis"
          if authority.Generation < 1L then "invalid-generation"
          if not(digest authority.ManifestSha256) then "wrong-manifest"
          if not(digest authority.TrustAnchorSha256) then "wrong-trust-anchor"
          if not authority.UnknownFields.IsEmpty then "unknown-fields"
          if not authority.DuplicateFields.IsEmpty then "duplicate-fields" ]
    let admit read authority fence =
        let errors = ResizeArray(validateAuthority read authority)
        if authority.ManifestSha256 <> fence.ExpectedManifestSha256 then errors.Add "manifest-mismatch"
        if authority.Commit <> fence.ExpectedEpochCommit || authority.Generation <> fence.ExpectedEpochGeneration then errors.Add "stale-epoch-generation"
        if fence.ExpectedOperationGeneration <> fence.CurrentOperationGeneration then errors.Add "stale-operation-generation"
        if fence.ExpectedClaimGeneration <> fence.CurrentClaimGeneration then errors.Add "stale-claim-generation"
        if String.IsNullOrWhiteSpace fence.OperationId then errors.Add "missing-operation-id"
        let phaseAllows =
            match fence.Writer, authority.Phase with
            | NewOrdinaryV1, OperatingV1 -> true
            | IncumbentV1Effect, OperatingV1 -> fence.EligibleIncumbent
            | IncumbentV1Effect, Preparing -> fence.EligibleIncumbent
            | OrdinaryV2, (OpenV2 | ObservingV2 | ContractingV1 | OperatingV2) -> true
            | CutoverControl, (Preparing | FreezeRequested | Frozen | SwitchedV2 | VerifiedV2 | OpenV2 | ObservingV2 | ContractingV1) -> true
            | RollbackControl, RollingBack -> true
            | _ -> false
        if not phaseAllows then errors.Add "phase-refuses-writer"
        if errors.Count = 0 then Authorized
        elif read = Unreadable || read = Partial then Indeterminate(String.concat "," errors)
        else Refused(List.ofSeq errors)
    let settleLostResponse fence readback =
        if readback.Read <> Observed then SettlementIndeterminate
        elif readback.PartialEffect then EffectPartial
        elif readback.OperationId = Some fence.OperationId
             && readback.EpochCommit = Some fence.ExpectedEpochCommit
             && readback.EpochGeneration = Some fence.ExpectedEpochGeneration
             && readback.EffectDigest |> Option.exists (String.IsNullOrWhiteSpace >> not) then KnownApplied
        elif readback.OperationId.IsNone && readback.EffectDigest.IsNone then ProvenAbsentMayRetry
        else SettlementIndeterminate
    let projectIssue authority =
        { FleetId = authority.FleetId; ManifestSha256 = authority.ManifestSha256; EpochCommit = authority.Commit
          Generation = authority.Generation; Phase = authority.Phase; SourceRef = authority.Ref; Authoritative = false }
    let validateProjection authority projection =
        not projection.Authoritative && projection = projectIssue authority
