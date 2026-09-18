module FS.GG.Coordination.QuintReplay.Tests.JournalFencingReplayTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open FS.GG.Coordination.GitHub
open FsQuint
open Xunit

type private ModelState =
    {
        Head0: int64
        Generation0: int64
        Head1: int64
        Generation1: int64
        ObservedA: int64
        ObservedB: int64
        ProposalsReady: bool
        AcceptedA: bool
        AcceptedB: bool
        FirstOwner: int64
        ConflictObserved: bool
        RetryAccepted: bool
        CurrentOwner: int64
        CurrentGrant: int64
        StaleEffectAttempted: bool
        EffectOwner: int64
        Shard1Committed: bool
    }

type private Runtime =
    {
        Model: ModelState
        Address: AggregateAddress
        Root: JournalCommit
        Accepted: JournalCommit option
        ProposalA: CasProposal option
        ProposalB: CasProposal option
    }

let private expectOk label =
    function
    | Ok value -> value
    | Error error -> failwith $"{label}: %A{error}"

let private digest character = String.replicate 64 character
let private oid character = String.replicate 40 character

let private blob json =
    let bytes = ShardedJournalAdapter.canonicalJson json |> expectOk "canonical JSON"

    {
        Bytes = bytes
        Digest = ShardedJournalAdapter.sha256 bytes
    }

let private commit address generation parent prior operation commitOid : JournalCommit =
    let event = blob $"{{\"generation\":{generation},\"payload\":\"event\"}}"

    let unsigned =
        {
            SchemaVersion = 1
            Address = address
            Generation = generation
            EventDigest = event.Digest
            SnapshotDigest = None
            Terminal = false
            PriorHeadDigest = prior
            HeadDigest = ""
        }

    let headBytes = ShardedJournalAdapter.journalHeadBytes unsigned

    let head =
        { unsigned with
            HeadDigest = ShardedJournalAdapter.sha256 headBytes
        }

    {
        CommitOid = commitOid
        ParentOid = parent
        TreeOid = oid "b"
        OperationId = operation
        Head = head
        HeadBytes = ShardedJournalAdapter.journalHeadBytes head
        Event = event
        Checkpoint = None
    }

let private initialModel =
    {
        Head0 = 10L
        Generation0 = 1L
        Head1 = 20L
        Generation1 = 1L
        ObservedA = 0L
        ObservedB = 0L
        ProposalsReady = false
        AcceptedA = false
        AcceptedB = false
        FirstOwner = 0L
        ConflictObserved = false
        RetryAccepted = false
        CurrentOwner = 0L
        CurrentGrant = 0L
        StaleEffectAttempted = false
        EffectOwner = 0L
        Shard1Committed = false
    }

let private integer value = QuintReplayValue.Integer(string value)
let private boolean value = QuintReplayValue.Boolean value

let private project model =
    let draft: QuintReplayState =
        {
            Identity = digest "0"
            Bindings =
                [
                    "state",
                    QuintReplayValue.Record
                        [
                            "acceptedA", boolean model.AcceptedA
                            "acceptedB", boolean model.AcceptedB
                            "conflictObserved", boolean model.ConflictObserved
                            "currentGrant", integer model.CurrentGrant
                            "currentOwner", integer model.CurrentOwner
                            "effectOwner", integer model.EffectOwner
                            "firstOwner", integer model.FirstOwner
                            "generation0", integer model.Generation0
                            "generation1", integer model.Generation1
                            "head0", integer model.Head0
                            "head1", integer model.Head1
                            "observedA", integer model.ObservedA
                            "observedB", integer model.ObservedB
                            "proposalsReady", boolean model.ProposalsReady
                            "retryAccepted", boolean model.RetryAccepted
                            "shard1Committed", boolean model.Shard1Committed
                            "staleEffectAttempted", boolean model.StaleEffectAttempted
                        ]
                ]
        }

    { draft with
        Identity = QuintReplay.stateFingerprint draft |> expectOk "state fingerprint"
    }

let private initialRuntime () =
    let address =
        ShardedJournalAdapter.address Operation "replay:journal-fencing"
        |> expectOk "address"

    let root = commit address 1L None None "root" (oid "a")

    {
        Model = initialModel
        Address = address
        Root = root
        Accepted = None
        ProposalA = None
        ProposalB = None
    }

let private prepareSiblings runtime =
    let snapshot =
        ShardedJournalAdapter.validate runtime.Address (JournalComplete("revision-1", [ runtime.Root ]))
        |> expectOk "validate initial journal"

    let candidateA =
        commit runtime.Address 2L (Some runtime.Root.CommitOid) (Some runtime.Root.Head.HeadDigest) "worker-a" (oid "c")

    let candidateB =
        commit runtime.Address 2L (Some runtime.Root.CommitOid) (Some runtime.Root.Head.HeadDigest) "worker-b" (oid "d")

    { runtime with
        ProposalA =
            Some(
                ShardedJournalAdapter.planCas "worker-a" snapshot candidateA
                |> expectOk "plan worker A"
            )
        ProposalB =
            Some(
                ShardedJournalAdapter.planCas "worker-b" snapshot candidateB
                |> expectOk "plan worker B"
            )
        Model =
            { runtime.Model with
                ObservedA = runtime.Model.Head0
                ObservedB = runtime.Model.Head0
                ProposalsReady = true
            }
    }

let private acceptA runtime =
    let proposal =
        runtime.ProposalA
        |> Option.defaultWith (fun () -> failwith "worker A proposal is missing")

    let observation =
        JournalComplete("revision-2", [ runtime.Root; proposal.ProposedCommit ])

    match ShardedJournalAdapter.reconcile proposal ReceiveAccepted observation with
    | Accepted ->
        { runtime with
            Accepted = Some proposal.ProposedCommit
            Model =
                { runtime.Model with
                    Head0 = 11L
                    Generation0 = 2L
                    AcceptedA = true
                    FirstOwner = 1L
                    CurrentOwner = 1L
                    CurrentGrant = 2L
                }
        }
    | result -> failwith $"worker A was not accepted: %A{result}"

let private observeConflict runtime =
    let proposal =
        runtime.ProposalB
        |> Option.defaultWith (fun () -> failwith "worker B proposal is missing")

    let accepted =
        runtime.Accepted
        |> Option.defaultWith (fun () -> failwith "accepted commit is missing")

    let observation = JournalComplete("revision-2", [ runtime.Root; accepted ])

    match ShardedJournalAdapter.reconcile proposal ReceiveParentConflict observation with
    | ParentConflict ->
        { runtime with
            Model =
                { runtime.Model with
                    ObservedB = runtime.Model.Head0
                    ConflictObserved = true
                }
        }
    | result -> failwith $"worker B conflict was not retained: %A{result}"

let private retryLoser faultyProjection runtime =
    let accepted =
        runtime.Accepted
        |> Option.defaultWith (fun () -> failwith "accepted commit is missing")

    let current =
        ShardedJournalAdapter.validate runtime.Address (JournalComplete("revision-2", [ runtime.Root; accepted ]))
        |> expectOk "validate current journal"

    let retried =
        commit runtime.Address 3L (Some accepted.CommitOid) (Some accepted.Head.HeadDigest) "worker-b-retry" (oid "e")

    let proposal =
        ShardedJournalAdapter.planCas "worker-b-retry" current retried
        |> expectOk "plan worker B retry"

    let observation = JournalComplete("revision-3", [ runtime.Root; accepted; retried ])

    match ShardedJournalAdapter.reconcile proposal ReceiveAccepted observation with
    | Accepted ->
        let currentGrant = if faultyProjection then 2L else 3L

        { runtime with
            Accepted = Some retried
            Model =
                { runtime.Model with
                    Head0 = 21L
                    Generation0 = 3L
                    RetryAccepted = true
                    CurrentOwner = 2L
                    CurrentGrant = currentGrant
                }
        }
    | result -> failwith $"worker B retry was not accepted: %A{result}"

let private apply faultyProjection (step: QuintReplayStep) runtime =
    let next =
        match step.Action with
        | "prepareSiblings" -> prepareSiblings runtime
        | "casA" -> acceptA runtime
        | "observeConflict" -> observeConflict runtime
        | "retryLoser" -> retryLoser faultyProjection runtime
        | action -> failwith $"unbound Quint action: {action}"

    Task.FromResult(Ok next)

let private driver faultyProjection : ReplayDriver<Runtime ref> =
    {
        Initialize = fun _ _ -> Task.FromResult(Ok(ref (initialRuntime ())))
        Apply =
            fun step runtime token ->
                task {
                    token.ThrowIfCancellationRequested()
                    let! next = apply faultyProjection step runtime.Value
                    return next |> Result.map (fun value -> runtime.Value <- value)
                }
        Observe = fun runtime _ -> Task.FromResult(Ok(project runtime.Value.Model))
        Cleanup = fun _ _ -> Task.FromResult(Ok())
    }

let private fixture name =
    Path.Combine(AppContext.BaseDirectory, "Fixtures", name)

let private source action =
    let marker = $"action {action} ="

    let matches =
        fixture "Protocol.md"
        |> File.ReadAllLines
        |> Array.indexed
        |> Array.filter (fun (_, line) -> line.TrimStart().StartsWith(marker, StringComparison.Ordinal))

    match matches with
    | [| index, _ |] ->
        {
            Path = "src/FS.GG.Coordination.Protocol/Protocol.md"
            Line = index + 1
            Column = 3
        }
    | _ -> failwith $"expected one Quint source action named {action}, got {matches.Length}"

let private fingerprintBytes (bytes: byte array) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let private fingerprintText (value: string) =
    value |> Encoding.UTF8.GetBytes |> fingerprintBytes

let private fingerprintFile path =
    File.ReadAllBytes path |> fingerprintBytes

let private normalizeCounterexampleItf (text: string) =
    let retained = JsonNode.Parse(text).AsObject()
    let metadata = retained["#meta"].AsObject()

    if metadata["format"].GetValue<string>() <> "ITF" then
        failwith "retained counterexample is not ITF"

    if metadata["status"].GetValue<string>() <> "violation" then
        failwith "retained counterexample must preserve its violation classification"

    let strictMetadata = JsonObject()
    strictMetadata["format"] <- "ITF"
    strictMetadata["format-description"] <- metadata["format-description"].GetValue<string>()
    strictMetadata["source"] <- "src/FS.GG.Coordination.Protocol/Protocol.md#GS20310JournalModel"
    strictMetadata["status"] <- "ok"

    let root = JsonObject()
    root["#meta"] <- strictMetadata
    root["vars"] <- retained["vars"].DeepClone()
    root["states"] <- retained["states"].DeepClone()
    root.ToJsonString()

let private trace () =
    let manifest =
        fixture "journal-fencing.manifest.json"
        |> File.ReadAllText
        |> JsonNode.Parse
        |> _.AsObject()

    if manifest["id"].GetValue<string>() <> "journal-fencing" then
        failwith "journal-fencing manifest identity drifted"

    if manifest["outcome"].GetValue<string>() <> "temporal-violation" then
        failwith "journal-fencing manifest outcome drifted"

    let bounds = manifest["bounds"].AsObject()
    let maxSteps = bounds["maxSteps"].GetValue<int64>()

    let context: QuintItfDecodeContext =
        {
            Environment =
                {
                    Seed = "tlc-exhaustive"
                    Bounds = [ "maxSteps", maxSteps ]
                    ToolFingerprint = manifest["toolchainSha256"].GetValue<string>()
                    ProfileFingerprint = fingerprintText "fsgg-quint-profile/2"
                    ContractFingerprint = fingerprintFile (fixture "contract.json")
                    AdapterFingerprint = fingerprintFile typeof<Runtime>.Assembly.Location
                    ImplementationFingerprint = fingerprintFile typeof<JournalKind>.Assembly.Location
                }
            Steps =
                [
                    {
                        Index = 1
                        Action = "prepareSiblings"
                        Source = source "prepareSiblings"
                    }
                    {
                        Index = 2
                        Action = "casA"
                        Source = source "casA"
                    }
                    {
                        Index = 3
                        Action = "observeConflict"
                        Source = source "observeConflict"
                    }
                    {
                        Index = 4
                        Action = "retryLoser"
                        Source = source "retryLoser"
                    }
                ]
        }

    fixture "journal-fencing.itf.json"
    |> File.ReadAllText
    |> normalizeCounterexampleItf
    |> QuintReplay.decodeItf context
    |> expectOk "decode journal-fencing ITF"

[<Fact>]
let ``F# journal adapter exactly replays the Quint fencing trace`` () =
    task {
        match!
            Replay.run (TimeSpan.FromSeconds 30.) System.Threading.CancellationToken.None (driver false) (trace ())
        with
        | {
              Outcome = ReplayOutcome.Equivalent
              CleanupFailure = None
          } -> ()
        | result -> Assert.Fail($"expected an equivalent replay, got %A{result}")
    }

[<Fact>]
let ``replay identifies the first faulty F# projection and its Quint source`` () =
    task {
        match!
            Replay.run (TimeSpan.FromSeconds 30.) System.Threading.CancellationToken.None (driver true) (trace ())
        with
        | {
              Outcome = ReplayOutcome.Diverged(step, action, actualSource, path, _, _)
              CleanupFailure = None
          } ->
            Assert.Equal(4, step)
            Assert.Equal(Some "retryLoser", action)
            Assert.Equal(Some(source "retryLoser"), actualSource)
            Assert.StartsWith("$/bindings/", path)
        | result -> Assert.Fail($"expected an exact replay divergence, got %A{result}")
    }
