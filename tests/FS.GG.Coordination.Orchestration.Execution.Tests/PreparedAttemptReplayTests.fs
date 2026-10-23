module FS.GG.Coordination.Orchestration.Execution.Tests.PreparedAttemptReplayTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution
open FsQuint
open Xunit

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"{error}"

let private hash bytes =
    SHA256.HashData(bytes: byte array)
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let private root () =
    let rec find (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "src/FS.GG.Coordination.Protocol/Protocol.md")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "canonical protocol missing"
        else
            find directory.Parent

    find (DirectoryInfo AppContext.BaseDirectory)

let private sourceFile () =
    Path.Combine(root (), "src/FS.GG.Coordination.Protocol/Protocol.md")

let private fixture name =
    Path.Combine(AppContext.BaseDirectory, "Fixtures/preflight", name)

let private text = QuintReplayValue.Text
let private integer value = QuintReplayValue.Integer(string value)
let private boolean = QuintReplayValue.Boolean

let private strings values =
    values |> Seq.map text |> Seq.toList |> QuintReplayValue.Set

let private state bindings =
    let draft =
        {
            Identity = String.replicate 64 "0"
            Bindings = bindings
        }

    { draft with
        Identity = QuintReplay.stateFingerprint draft |> ok
    }

let private trace (model: string) (actions: string list) =
    use manifest = JsonDocument.Parse(File.ReadAllBytes(fixture "manifest.json"))
    Assert.Equal(manifest.RootElement.GetProperty("sourceSha256").GetString(), File.ReadAllBytes(sourceFile ()) |> hash)
    let lines = File.ReadAllLines(sourceFile ())
    let modelName = model.Replace("Replay", "Model")
    let start = lines |> Array.findIndex (fun line -> line = $"module {modelName} {{")

    let binding action =
        let line =
            lines
            |> Array.skip start
            |> Array.findIndex (fun line ->
                line.TrimStart().StartsWith("action " + action + " =", StringComparison.Ordinal))

        {
            Path = "src/FS.GG.Coordination.Protocol/Protocol.md"
            Line = start + line + 1
            Column = 3
        }

    let assembly = File.ReadAllBytes(typeof<PreparedAttempt>.Assembly.Location) |> hash

    let context =
        {
            Environment =
                {
                    Seed = "37"
                    Bounds = [ "maxSteps", int64 actions.Length; "maxSamples", 1L ]
                    ToolFingerprint = "939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f"
                    ProfileFingerprint = Encoding.UTF8.GetBytes("fsgg-quint-profile/2") |> hash
                    ContractFingerprint =
                        File.ReadAllBytes(
                            Path.Combine(root (), "src/FS.GG.Coordination.Protocol/Generated/contract.json")
                        )
                        |> hash
                    AdapterFingerprint = File.ReadAllBytes(typeof<PreparedArtifactState>.Assembly.Location) |> hash
                    ImplementationFingerprint = assembly
                }
            Steps =
                actions
                |> List.mapi (fun index action ->
                    {
                        Index = index + 1
                        Action = action
                        Source = binding action
                    })
        }
    // Retain the raw producer trace; only nonsemantic native-tool timestamp metadata is excluded.
    let fixtureName = model + ".itf.json"

    let expectedFixture =
        manifest.RootElement.GetProperty("traces").EnumerateArray()
        |> Seq.find (fun row -> row.GetProperty("name").GetString() = fixtureName)

    Assert.Equal(expectedFixture.GetProperty("sha256").GetString(), File.ReadAllBytes(fixture fixtureName) |> hash)
    let raw = File.ReadAllText(fixture fixtureName) |> JsonNode.Parse
    let metadata = raw["#meta"].AsObject()
    metadata.Remove("description") |> ignore
    metadata.Remove("timestamp") |> ignore
    raw.ToJsonString() |> QuintReplay.decodeItf context |> ok

type private ObservationRuntime =
    {
        mutable State: PreparationObservationState
        mutable Custody: PreparationCustodyState
        mutable Effects: string list
        mutable Refusal: string option
        mutable Phase: int
    }

let private limits =
    {
        MaximumDistinct = 2
        MaximumEvents = 3
        MaximumBytes = 3L
        Deadline = 3L
    }

let private observeDriver: ReplayDriver<ObservationRuntime> =
    {
        Initialize =
            fun _ _ ->
                Task.FromResult(
                    Ok
                        {
                            State = PreparationObservation.initial
                            Custody = PreparationCustody.initial
                            Effects = []
                            Refusal = None
                            Phase = 0
                        }
                )
        Apply =
            fun step runtime _ ->
                let custodyEvents =
                    Map
                        [
                            "custodyBind", "bind"
                            "custodyPermit", "permit"
                            "custodyFiltered", "filtered"
                            "custodyLeaderExit", "leaderExit"
                            "custodyTerminated", "terminated"
                            "custodyResult", "result"
                            "custodyCleaned", "cleanup"
                            "custodyAdmit", "admit"
                        ]

                if custodyEvents.ContainsKey step.Action then
                    let decision =
                        PreparationCustody.step custodyEvents[step.Action] "a" runtime.Custody

                    runtime.Custody <- decision.State
                    runtime.Effects <- decision.Effects
                    runtime.Refusal <- decision.Refusal
                else
                    let decision =
                        match step.Action with
                        | "first"
                        | "repeat" -> PreparationObservation.observe limits 0L "a" 1L runtime.State
                        | "invalid" -> PreparationObservation.observe limits 3L "" 0L runtime.State
                        | "second" -> PreparationObservation.observe limits 0L "b" 1L runtime.State
                        | "distinctOverflow" -> PreparationObservation.observe limits 0L "c" 0L runtime.State
                        | "eventOverflow" -> PreparationObservation.observe limits 0L "a" 0L runtime.State
                        | "byteOverflow" -> PreparationObservation.observe limits 0L "a" 4L runtime.State
                        | "expiry" -> PreparationObservation.observe limits 3L "c" 0L runtime.State
                        | "cleaned" -> PreparationObservation.cleanup "a" true runtime.State
                        | "cleanedB" -> PreparationObservation.cleanup "b" true runtime.State
                        | other -> failwith other

                    runtime.State <- decision.State
                    runtime.Effects <- decision.Effects
                    runtime.Refusal <- decision.Refusal

                runtime.Phase <- runtime.Phase + 1
                Task.FromResult(Ok())
        Observe =
            fun runtime _ ->
                let s = runtime.State

                Task.FromResult(
                    Ok(
                        state
                            [
                                "observation",
                                QuintReplayValue.Record
                                    [
                                        "time", integer s.Time
                                        "identities", strings s.Identities
                                        "owned", strings (s.Owned |> Map.keys)
                                        "events", integer s.Events
                                        "bytes", integer s.Bytes
                                        "pending", strings s.CleanupPending
                                        "cleaned", strings s.CleanupObserved
                                    ]
                                "custody",
                                QuintReplayValue.Record
                                    [
                                        "identity", text runtime.Custody.Identity
                                        "bound", boolean runtime.Custody.Bound
                                        "permitted", boolean runtime.Custody.Permitted
                                        "filtered", boolean runtime.Custody.Filtered
                                        "resultObserved", boolean runtime.Custody.ResultObserved
                                        "leaderExited", boolean runtime.Custody.LeaderExited
                                        "groupLive", boolean runtime.Custody.GroupLive
                                        "groupTerminated", boolean runtime.Custody.GroupTerminated
                                        "cleanupObserved", boolean runtime.Custody.CleanupObserved
                                        "pending", boolean runtime.Custody.Pending
                                        "refused", boolean runtime.Custody.Refused
                                        "admitted", boolean runtime.Custody.Admitted
                                    ]
                                "effects", QuintReplayValue.Sequence(List.map text runtime.Effects)
                                "refusal", text (defaultArg runtime.Refusal "")
                                "phase", integer runtime.Phase
                            ]
                    )
                )
        Cleanup = fun _ _ -> Task.FromResult(Ok())
    }

type private ArtifactRuntime =
    {
        mutable State: PreparedArtifactState
        mutable Phase: int
        mutable ImportsAt: int
        mutable DiscoveryAt: int
        mutable Effects: int
    }

let private artifactDriver: ReplayDriver<ArtifactRuntime> =
    {
        Initialize =
            fun _ _ ->
                Task.FromResult(
                    Ok
                        {
                            State =
                                PreparedArtifact.initial
                                    "0"
                                    (set["imports"
                                         "discovery"])
                            Phase = 0
                            ImportsAt = -1
                            DiscoveryAt = -1
                            Effects = 0
                        }
                )
        Apply =
            fun step runtime _ ->
                match step.Action with
                | "importCheck" ->
                    runtime.ImportsAt <- int runtime.State.InputIdentity
                    runtime.State <- PreparedArtifact.observe "imports" runtime.State |> ok
                | "discoverCheck" ->
                    runtime.DiscoveryAt <- int runtime.State.InputIdentity
                    runtime.State <- PreparedArtifact.observe "discovery" runtime.State |> ok
                | "mutateInput" ->
                    runtime.State <-
                        PreparedArtifact.invalidate (string (int runtime.State.InputIdentity + 1)) runtime.State
                | "consume" ->
                    let next, effects, _ =
                        PreparedArtifact.consume runtime.State.InputIdentity runtime.State

                    runtime.State <- next
                    runtime.Effects <- runtime.Effects + effects.Length
                | "cleanup" -> let next, _, _ = PreparedArtifact.cleanup true runtime.State in runtime.State <- next
                | other -> failwith other

                runtime.Phase <- runtime.Phase + 1
                Task.FromResult(Ok())
        Observe =
            fun runtime _ ->
                let s = runtime.State

                Task.FromResult(
                    Ok(
                        state
                            [
                                "phase", integer runtime.Phase
                                "artifact",
                                QuintReplayValue.Record
                                    [
                                        "identity", integer (int s.InputIdentity)
                                        "imports", boolean (s.ObservedChecks.Contains "imports")
                                        "discovery", boolean (s.ObservedChecks.Contains "discovery")
                                        "prepared",
                                        integer (s.PreparedIdentity |> Option.map int |> Option.defaultValue -1)
                                        "importsAt", integer runtime.ImportsAt
                                        "discoveryAt", integer runtime.DiscoveryAt
                                        "effects", integer runtime.Effects
                                        "allowed", boolean true
                                        "cleanup", boolean s.CleanupObserved
                                    ]
                            ]
                    )
                )
        Cleanup = fun _ _ -> Task.FromResult(Ok())
    }

[<Fact>]
let ``genuine bounded Quint observation trace replays production state and ordered effects`` () =
    task {
        let expected =
            trace
                "PreflightObservationReplay"
                [
                    "first"
                    "repeat"
                    "invalid"
                    "byteOverflow"
                    "second"
                    "distinctOverflow"
                    "eventOverflow"
                    "expiry"
                    "cleaned"
                    "cleanedB"
                    "custodyBind"
                    "custodyPermit"
                    "custodyFiltered"
                    "custodyLeaderExit"
                    "custodyTerminated"
                    "custodyResult"
                    "custodyCleaned"
                    "custodyAdmit"
                ]

        let! report =
            Replay.run (TimeSpan.FromSeconds 10.) CancellationToken.None observeDriver expected

        Assert.Equal(ReplayOutcome.Equivalent, report.Outcome)
        Assert.Equal(18, report.AppliedSteps)
    }

[<Fact>]
let ``genuine bounded Quint artifact trace replays actual preparation and invalidation reducer`` () =
    task {
        let expected =
            trace
                "PreflightArtifactReplay"
                [
                    "importCheck"
                    "discoverCheck"
                    "mutateInput"
                    "consume"
                    "importCheck"
                    "discoverCheck"
                    "consume"
                    "cleanup"
                ]

        let! report =
            Replay.run (TimeSpan.FromSeconds 10.) CancellationToken.None artifactDriver expected

        Assert.Equal(ReplayOutcome.Equivalent, report.Outcome)
        Assert.Equal(8, report.AppliedSteps)
    }

[<Fact>]
let ``invalid observations preserve state and expired valid refusal retains ordered cleanup effects`` () =
    let current =
        PreparationObservation.observe limits 0L "a" 1L PreparationObservation.initial

    let invalid = PreparationObservation.observe limits 3L "" 0L current.State
    Assert.Equal(current.State, invalid.State)
    Assert.Empty(invalid.Effects)
    let expired = PreparationObservation.observe limits 3L "c" 0L current.State
    Assert.Equal(Some "deadline", expired.Refusal)
    Assert.Equal<string list>([ "retire:a"; "cleanup:a" ], expired.Effects)
    Assert.Equal<Set<string>>(Set.singleton "a", expired.State.CleanupPending)
    let unknown = PreparationObservation.cleanup "a" false expired.State
    Assert.Equal(expired.State, unknown.State)
    Assert.Equal(Some "cleanup-unobserved", unknown.Refusal)

[<Theory>]
[<InlineData("filter")>]
[<InlineData("json")>]
[<InlineData("leader")>]
[<InlineData("timeout")>]
let ``custody requirements independently refuse causal shortcuts and preserve cleanup`` (scenario: string) =
    let advance event state =
        (PreparationCustody.step event "owned" state).State

    let bound = PreparationCustody.initial |> advance "bind" |> advance "permit"

    match scenario with
    | "filter" ->
        let rejected = PreparationCustody.step "result" "owned" bound
        Assert.Equal(Some "preparation-custody-order-refused", rejected.Refusal)
        Assert.False(rejected.State.ResultObserved)
        Assert.True(rejected.State.Pending)
    | "json" ->
        let validJson = bound |> advance "filtered" |> advance "result"
        let rejected = PreparationCustody.step "cleanup" "owned" validJson
        Assert.Equal(Some "preparation-custody-order-refused", rejected.Refusal)
        Assert.False(rejected.State.CleanupObserved)
        Assert.True(rejected.State.GroupLive)
        Assert.False(PreparationCustody.ready rejected.State)
    | "leader" ->
        let leader = PreparationCustody.step "leaderExit" "owned" (advance "filtered" bound)
        Assert.Equal<string list>([ "observe-leader-exit" ], leader.Effects)
        Assert.True(leader.State.LeaderExited)
        Assert.True(leader.State.GroupLive)
        Assert.False(leader.State.GroupTerminated)
        Assert.False(PreparationCustody.ready leader.State)
    | "timeout" ->
        let timed = PreparationCustody.step "timeout" "owned" (advance "filtered" bound)
        Assert.Equal<string list>([ "signal-owned-group"; "await-group-termination" ], timed.Effects)
        Assert.True(timed.State.Pending)
        Assert.True(timed.State.GroupLive)
        Assert.False(timed.State.GroupTerminated)
        let closed = timed.State |> advance "terminated" |> advance "cleanup"
        Assert.True(closed.Refused)
        Assert.True(closed.CleanupObserved)
        Assert.False(closed.Pending)
        Assert.False(PreparationCustody.ready closed)
    | _ -> failwith scenario

// Replay one actual source-primitive operation's observations, captured at its existing
// finite cut boundaries. Initialize runs the real private I/O path; Apply consumes its
// retained observations in order rather than manufacturing filesystem effects from Quint.
// This checks syscall-order correspondence, never filesystem/power-loss qualification.
type private SourceDirectoryObservation =
    { Action: string
      Phase: int }

type private SourceDirectoryRuntime =
    { Root: string
      Observations: SourceDirectoryObservation array
      mutable Index: int }

let private sourceDirectoryDriver: ReplayDriver<SourceDirectoryRuntime> =
    {
        Initialize = fun _ _ ->
            let directory = Path.Combine(Path.GetTempPath(), "source-directory-replay-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory directory |> ignore
            try
                let observations = ResizeArray<SourceDirectoryObservation>()
                let observe action phase = observations.Add { Action = action; Phase = phase }
                observe "init" 0
                let path = Path.Combine(directory, "key.bin")
                use owner = new FileStream(Path.ChangeExtension(path, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                use source = new PortableSourceJournalIO.Context(directory, path, owner.SafeFileHandle)
                observe "lockA" 1
                let candidate = [|1uy;2uy;3uy|]
                source.Write(candidate,
                    (fun () ->
                        Assert.False(File.Exists path)
                        Assert.Empty(Directory.GetFiles(directory, "*.tmp"))
                        observe "boundCandidate" 2),
                    (fun () ->
                        Assert.Single(Directory.GetFiles(directory, "*.tmp")) |> ignore
                        observe "createAndWrite" 3),
                    (fun () ->
                        Assert.False(File.Exists path)
                        observe "fileFlush" 4),
                    (fun () ->
                        Assert.Equal<byte>(candidate, File.ReadAllBytes path)
                        Assert.Empty(Directory.GetFiles(directory, "*.tmp"))
                        observe "replace" 5),
                    (fun () -> observe "directorySync" 6))
                use cold = source.OpenRead() |> Option.defaultWith (fun () -> failwith "source cold read missing")
                let actual = Array.zeroCreate<byte> candidate.Length
                cold.ReadExactly(actual.AsSpan())
                Assert.Equal(-1, cold.ReadByte())
                Assert.Equal<byte>(candidate, actual)
                observe "coldRead" 7
                observe "respond" 8
                Task.FromResult(Ok { Root = directory; Observations = observations.ToArray(); Index = 0 })
            with _ ->
                Directory.Delete(directory, true)
                reraise ()
        Apply = fun step runtime _ ->
            let next = runtime.Index + 1
            if next >= runtime.Observations.Length then failwith "source observation population exhausted"
            Assert.Equal(runtime.Observations[next].Action, step.Action)
            runtime.Index <- next
            Task.FromResult(Ok())
        Observe = fun runtime _ ->
            let phase = runtime.Observations[runtime.Index].Phase
            Task.FromResult(
                Ok(state [
                    "phase", integer phase
                    "commit", QuintReplayValue.Record [
                        "owner", integer (if phase = 0 then 0 else 1)
                        "phase", integer phase
                        "original", integer 1
                        "bounded", boolean (phase >= 2)
                        "fileFlushed", boolean (phase >= 4)
                        "renamed", boolean (phase >= 5)
                        "directorySynced", boolean (phase >= 6)
                        "coldRead", boolean (phase >= 7)
                        "responded", boolean (phase >= 8)
                        "visible", integer (if phase >= 5 then 1 else 0)
                        // Conditional model assumption, not authenticated hardware evidence.
                        "durable", integer (if phase >= 6 then 1 else 0)
                        "consumed", boolean (phase >= 5)
                        "uncertain", boolean false
                        "qualifiedBacking", boolean true
                        "issued", integer (if phase >= 8 then 1 else 0)
                        "firstFailure", boolean false
                        "effects", strings (if phase >= 3 then ["temp"] else [])
                    ]
                ]))
        Cleanup = fun runtime _ ->
            Directory.Delete(runtime.Root, true)
            Task.FromResult(Ok())
    }

[<Fact>]
let ``genuine source directory commit trace replays actual descriptor write flush rename sync and cold read observations`` () =
    task {
        if OperatingSystem.IsLinux() then
            let expected = trace "SourceDirectoryCommitReplay"
                                ["lockA"; "boundCandidate"; "createAndWrite"; "fileFlush"; "replace"; "directorySync"; "coldRead"; "respond"]
            let! report = Replay.run (TimeSpan.FromSeconds 10.) CancellationToken.None sourceDirectoryDriver expected
            Assert.Equal(ReplayOutcome.Equivalent, report.Outcome)
            Assert.Equal(8, report.AppliedSteps)
            Assert.Equal(None, report.CleanupFailure)
    }
