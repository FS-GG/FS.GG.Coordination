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
                            Effects = []
                            Refusal = None
                            Phase = 0
                        }
                )
        Apply =
            fun step runtime _ ->
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
                ]

        let! report =
            Replay.run (TimeSpan.FromSeconds 10.) CancellationToken.None observeDriver expected

        Assert.Equal(ReplayOutcome.Equivalent, report.Outcome)
        Assert.Equal(10, report.AppliedSteps)
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
