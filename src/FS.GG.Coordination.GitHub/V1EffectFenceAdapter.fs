namespace FS.GG.Coordination.GitHub

open System
open System.Text.Json

type V1EffectClass = NewV1Admission | EligibleIncumbentV1
type V1EpochPhase =
    | V1OperatingV1 | V1Preparing | V1FreezeRequested | V1Frozen | V1SwitchedV2 | V1VerifiedV2
    | V1OpenV2 | V1ObservingV2 | V1ContractingV1 | V1OperatingV2 | V1RollingBack
type V1EpochEvidence =
    { Schema: string; FleetId: string; Repository: string; RepositoryId: int64; Ref: string; Tag: string
      GenesisCommit: string; TrustAnchorSha256: string; ManifestSha256: string; Phase: V1EpochPhase
      Commit: string; Parent: string; Generation: int64; Complete: bool; Fresh: bool; CacheUsedAsAuthority: bool }
type FreshEpochRead = FreshEpochBytes of byte array | EpochUnreadable of string | EpochPartial of string | EpochContradictory of string
type V1EffectExpectation =
    { EffectClass: V1EffectClass; EligibleIncumbent: bool; ManifestSha256: string; EpochCommit: string
      EpochGeneration: int64; ExpectedClaimGeneration: int64 option; CurrentClaimGeneration: int64 option
      ExpectedOperationGeneration: int64; CurrentOperationGeneration: int64 }
type V1EffectRequest = { OperationId: string; PayloadDigest: string }
type V1EffectAttempt = EffectApplied of effectDigest: string | EffectProvenAbsent | EffectPartiallyApplied of reason: string | EffectOutcomeIndeterminate of reason: string
type V1EffectOutcome = V1RefusedBeforeEffect of reasons: string list | V1Applied of effectDigest: string | V1ProvenAbsent | V1Partial of reason: string | V1Indeterminate of reason: string
type FreshEpochReader = { ReadFresh: unit -> FreshEpochRead }
type V1EffectPort = { ApplyOnce: V1EffectRequest -> V1EffectAttempt }

[<RequireQualifiedAccess>]
module V1EffectFenceAdapter =
    let private required =
        Set.ofList [ "schema"; "fleetId"; "repository"; "repositoryId"; "ref"; "tag"; "genesisCommit"
                     "trustAnchorSha256"; "manifestSha256"; "phase"; "commit"; "parent"; "generation"
                     "complete"; "fresh"; "cacheUsedAsAuthority" ]

    let private phase = function
        | "OperatingV1" -> Ok V1OperatingV1 | "Preparing" -> Ok V1Preparing
        | "FreezeRequested" -> Ok V1FreezeRequested | "Frozen" -> Ok V1Frozen | "SwitchedV2" -> Ok V1SwitchedV2
        | "VerifiedV2" -> Ok V1VerifiedV2 | "OpenV2" -> Ok V1OpenV2 | "ObservingV2" -> Ok V1ObservingV2
        | "ContractingV1" -> Ok V1ContractingV1 | "OperatingV2" -> Ok V1OperatingV2 | "RollingBack" -> Ok V1RollingBack
        | value -> Error $"unknown-phase:{value}"

    let parseStrict (bytes: byte array) =
        try
            use document = JsonDocument.Parse(bytes)
            let root = document.RootElement
            if root.ValueKind <> JsonValueKind.Object then Error [ "evidence-not-object" ] else
            let names = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            let duplicates = names |> List.countBy id |> List.choose (fun (name, count) -> if count > 1 then Some name else None)
            let observed = Set.ofList names
            let unknown = Set.difference observed required |> Set.toList
            let missing = Set.difference required observed |> Set.toList
            let shapeErrors =
                [ yield! duplicates |> List.map (sprintf "duplicate-field:%s")
                  yield! unknown |> List.map (sprintf "unknown-field:%s")
                  yield! missing |> List.map (sprintf "missing-field:%s") ]
            if not shapeErrors.IsEmpty then Error shapeErrors else
            try
                let getString (name: string) =
                    let value = root.GetProperty(name)
                    if value.ValueKind <> JsonValueKind.String || String.IsNullOrWhiteSpace(value.GetString()) then
                        raise (InvalidOperationException $"invalid-{name}")
                    value.GetString()
                match phase (getString "phase") with
                | Error reason -> Error [ reason ]
                | Ok parsedPhase ->
                    Ok { Schema = getString "schema"; FleetId = getString "fleetId"; Repository = getString "repository"
                         RepositoryId = root.GetProperty("repositoryId").GetInt64(); Ref = getString "ref"; Tag = getString "tag"
                         GenesisCommit = getString "genesisCommit"; TrustAnchorSha256 = getString "trustAnchorSha256"
                         ManifestSha256 = getString "manifestSha256"; Phase = parsedPhase; Commit = getString "commit"
                         Parent = getString "parent"; Generation = root.GetProperty("generation").GetInt64()
                         Complete = root.GetProperty("complete").GetBoolean(); Fresh = root.GetProperty("fresh").GetBoolean()
                         CacheUsedAsAuthority = root.GetProperty("cacheUsedAsAuthority").GetBoolean() }
            with :? InvalidOperationException as error -> Error [ error.Message ]
        with :? JsonException -> Error [ "unreadable-json" ]

    let private validDigest (value: string) =
        not (String.IsNullOrWhiteSpace value) && value.Length = 64
        && value |> Seq.forall (fun c -> Char.IsDigit c || (c >= 'a' && c <= 'f'))

    let private refusalReasons (evidence: V1EpochEvidence) (expectation: V1EffectExpectation) (request: V1EffectRequest) =
        let address = ShardedJournalAdapter.address Cutover "fleet-cutover:fs-gg-production"
        [ if evidence.Schema <> "fsgg.github-substrate.epoch-wire/1" then "wrong-schema"
          if evidence.FleetId <> "fs-gg-production" then "wrong-fleet"
          if evidence.Repository <> "FS-GG/FS.GG.Coordination.Authority" || evidence.RepositoryId <> 1351660651L then "wrong-authority-repository"
          match address with | Ok value when evidence.Ref <> value.Ref -> "wrong-ref" | Error _ -> "canonical-address-unavailable" | _ -> ()
          if not (evidence.Tag.StartsWith("refs/tags/fsgg/v2/fleet-cutover/", StringComparison.Ordinal)) then "missing-or-wrong-tag"
          if not evidence.Complete then "authority-partial"
          if not evidence.Fresh then "authority-stale"
          if evidence.CacheUsedAsAuthority then "cache-cannot-authorize"
          if evidence.Generation < 1L then "invalid-generation"
          if not (validDigest evidence.GenesisCommit) || not (validDigest evidence.Commit) || not (validDigest evidence.Parent) then "missing-parent-or-genesis"
          if not (validDigest evidence.TrustAnchorSha256) then "wrong-trust-anchor"
          if not (validDigest evidence.ManifestSha256) || evidence.ManifestSha256 <> expectation.ManifestSha256 then "manifest-mismatch"
          if evidence.Commit <> expectation.EpochCommit || evidence.Generation <> expectation.EpochGeneration then "stale-epoch-generation"
          if expectation.ExpectedClaimGeneration <> expectation.CurrentClaimGeneration then "stale-claim-generation"
          if expectation.ExpectedOperationGeneration <> expectation.CurrentOperationGeneration then "stale-operation-generation"
          if String.IsNullOrWhiteSpace request.OperationId then "missing-operation-id"
          if not (validDigest request.PayloadDigest) then "invalid-payload-digest"
          match expectation.EffectClass, evidence.Phase with
          | NewV1Admission, V1OperatingV1 -> ()
          | EligibleIncumbentV1, V1OperatingV1 when expectation.EligibleIncumbent -> ()
          | EligibleIncumbentV1, V1Preparing when expectation.EligibleIncumbent -> ()
          | _ -> "phase-refuses-writer" ]

    let execute reader effect expectation request =
        match reader.ReadFresh() with
        | EpochUnreadable reason -> V1RefusedBeforeEffect [ $"authority-unreadable:{reason}" ]
        | EpochPartial reason -> V1RefusedBeforeEffect [ $"authority-partial:{reason}" ]
        | EpochContradictory reason -> V1RefusedBeforeEffect [ $"authority-contradictory:{reason}" ]
        | FreshEpochBytes bytes ->
            match parseStrict bytes with
            | Error reasons -> V1RefusedBeforeEffect reasons
            | Ok evidence ->
                match refusalReasons evidence expectation request with
                | _ :: _ as reasons -> V1RefusedBeforeEffect reasons
                | [] ->
                    match effect.ApplyOnce request with
                    | EffectApplied digest -> V1Applied digest
                    | EffectProvenAbsent -> V1ProvenAbsent
                    | EffectPartiallyApplied reason -> V1Partial reason
                    | EffectOutcomeIndeterminate reason -> V1Indeterminate reason
