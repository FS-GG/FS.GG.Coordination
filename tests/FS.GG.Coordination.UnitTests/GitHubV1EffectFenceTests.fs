module FS.GG.Coordination.GitHubV1EffectFenceTests

open System.Text
open FS.GG.Coordination.GitHub
open Xunit

let private sha c = System.String(c, 64)
let private manifest = sha 'a'
let private commit = sha 'b'

let private evidence phase fresh cache extra =
    Encoding.UTF8.GetBytes($"""{{"schema":"fsgg.github-substrate.epoch-wire/1","fleetId":"fs-gg-production","repository":"FS-GG/FS.GG.Coordination.Authority","repositoryId":1351660651,"ref":"refs/heads/fsgg/v2/journal/cutover/d5","tag":"refs/tags/fsgg/v2/fleet-cutover/1","genesisCommit":"{sha 'c'}","trustAnchorSha256":"{sha 'd'}","manifestSha256":"{manifest}","phase":"{phase}","commit":"{commit}","parent":"{sha 'e'}","generation":7,"complete":true,"fresh":{fresh.ToString().ToLowerInvariant()},"cacheUsedAsAuthority":{cache.ToString().ToLowerInvariant()}{extra}}}""")

let private expectation effectClass eligible =
    { EffectClass = effectClass; EligibleIncumbent = eligible; ManifestSha256 = manifest; EpochCommit = commit
      EpochGeneration = 7L; ExpectedClaimGeneration = Some 3L; CurrentClaimGeneration = Some 3L
      ExpectedOperationGeneration = 5L; CurrentOperationGeneration = 5L }

let private request = { OperationId = "operation-1"; PayloadDigest = sha 'f' }

let private run bytes expected attempt =
    let events = ResizeArray<string>()
    let reader = { ReadFresh = fun () -> events.Add "read"; FreshEpochBytes bytes }
    let effect = { ApplyOnce = fun observed -> events.Add $"effect:{observed.OperationId}"; attempt }
    let outcome = V1EffectFenceAdapter.execute reader effect expected request
    outcome, List.ofSeq events

[<Fact>]
let ``OperatingV1 reads verifies then applies exactly once`` () =
    let outcome, events = run (evidence "OperatingV1" true false "") (expectation NewV1Admission false) (EffectApplied (sha '1'))
    Assert.Equal<V1EffectOutcome>(V1Applied (sha '1'), outcome)
    Assert.Equal<string list>([ "read"; "effect:operation-1" ], events)

[<Fact>]
let ``Preparing admits only eligible incumbent already admitted`` () =
    let allowed, allowedEvents = run (evidence "Preparing" true false "") (expectation EligibleIncumbentV1 true) EffectProvenAbsent
    Assert.Equal<V1EffectOutcome>(V1ProvenAbsent, allowed)
    Assert.Equal<string list>([ "read"; "effect:operation-1" ], allowedEvents)
    for expected in [ expectation NewV1Admission false; expectation EligibleIncumbentV1 false ] do
        let refused, events = run (evidence "Preparing" true false "") expected (EffectApplied (sha '1'))
        Assert.Equal<string list>([ "read" ], events)
        match refused with | V1RefusedBeforeEffect reasons -> Assert.Contains("phase-refuses-writer", reasons) | _ -> Assert.Fail "expected refusal"

[<Fact>]
let ``freeze switch open rollback and v2 phases refuse before effect`` () =
    for phase in [ "FreezeRequested"; "Frozen"; "SwitchedV2"; "VerifiedV2"; "OpenV2"; "ObservingV2"; "ContractingV1"; "OperatingV2"; "RollingBack" ] do
        let outcome, events = run (evidence phase true false "") (expectation EligibleIncumbentV1 true) (EffectApplied (sha '1'))
        Assert.Equal<string list>([ "read" ], events)
        match outcome with | V1RefusedBeforeEffect reasons -> Assert.Contains("phase-refuses-writer", reasons) | _ -> Assert.Fail $"{phase} must refuse"

[<Fact>]
let ``stale cache manifest and generation mismatches refuse with zero effects`` () =
    let cases =
        [ evidence "OperatingV1" false false "", expectation NewV1Admission false, "authority-stale"
          evidence "OperatingV1" true true "", expectation NewV1Admission false, "cache-cannot-authorize"
          evidence "OperatingV1" true false "", { expectation NewV1Admission false with ManifestSha256 = sha '9' }, "manifest-mismatch"
          evidence "OperatingV1" true false "", { expectation NewV1Admission false with EpochGeneration = 8L }, "stale-epoch-generation"
          evidence "OperatingV1" true false "", { expectation NewV1Admission false with CurrentClaimGeneration = Some 4L }, "stale-claim-generation"
          evidence "OperatingV1" true false "", { expectation NewV1Admission false with CurrentOperationGeneration = 6L }, "stale-operation-generation" ]
    for bytes, expected, reason in cases do
        let outcome, events = run bytes expected (EffectApplied (sha '1'))
        Assert.Equal<string list>([ "read" ], events)
        match outcome with | V1RefusedBeforeEffect reasons -> Assert.Contains(reason, reasons) | _ -> Assert.Fail reason

[<Fact>]
let ``strict evidence rejects unknown duplicate missing and unreadable fields`` () =
    let unknown = evidence "OperatingV1" true false ",\"surprise\":true"
    let duplicate = evidence "OperatingV1" true false ",\"phase\":\"Frozen\""
    let unreadable = Encoding.UTF8.GetBytes("{")
    for bytes, reason in [ unknown, "unknown-field:surprise"; duplicate, "duplicate-field:phase"; unreadable, "unreadable-json" ] do
        let outcome, events = run bytes (expectation NewV1Admission false) (EffectApplied (sha '1'))
        Assert.Equal<string list>([ "read" ], events)
        match outcome with | V1RefusedBeforeEffect reasons -> Assert.Contains(reason, reasons) | _ -> Assert.Fail reason

[<Fact>]
let ``unreadable partial and contradictory reads never call effect`` () =
    for read in [ EpochUnreadable "denied"; EpochPartial "page"; EpochContradictory "rewind" ] do
        let mutable effects = 0
        let reader = { ReadFresh = fun () -> read }
        let effect = { ApplyOnce = fun _ -> effects <- effects + 1; EffectProvenAbsent }
        match V1EffectFenceAdapter.execute reader effect (expectation NewV1Admission false) request with
        | V1RefusedBeforeEffect _ -> Assert.Equal(0, effects)
        | _ -> Assert.Fail "expected refusal"

[<Fact>]
let ``one effect result is projected without blind retry`` () =
    for attempt, expected in
        [ EffectApplied (sha '1'), V1Applied (sha '1')
          EffectProvenAbsent, V1ProvenAbsent
          EffectPartiallyApplied "partial", V1Partial "partial"
          EffectOutcomeIndeterminate "lost-response", V1Indeterminate "lost-response" ] do
        let actual, events = run (evidence "OperatingV1" true false "") (expectation NewV1Admission false) attempt
        Assert.Equal<V1EffectOutcome>(expected, actual)
        Assert.Equal(2, events.Length)
