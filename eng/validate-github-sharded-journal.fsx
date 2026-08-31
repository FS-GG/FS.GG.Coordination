#load "../src/FS.GG.Coordination.GitHub/ShardedJournalAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubShardedJournalQualification.fs"

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let fail code message = failwith $"{code}: {message}"
let args = fsi.CommandLineArgs |> Array.skip 1
let root = if args.Length = 0 then "." else args.[0]
let fixturePath = Path.Combine(root, "tests/fixtures/github-sharded-journal/contract.json")
if not (File.Exists fixturePath) then fail "GSJQ-FIXTURE-MISSING" fixturePath
let fixture = JsonDocument.Parse(File.ReadAllBytes fixturePath)
let json = fixture.RootElement
if json.EnumerateObject() |> Seq.map _.Name |> Seq.toList <> [ "controls"; "generated"; "schema"; "synthetic" ] then fail "GSJQ-FIXTURE-SHAPE" fixturePath
if json.GetProperty("schema").GetString() <> "fsgg.coordination.github-sharded-journal-fixture/1" then fail "GSJQ-FIXTURE-SCHEMA" fixturePath
if not (json.GetProperty("synthetic").GetBoolean()) then fail "GSJQ-FIXTURE-PROVENANCE" fixturePath
let expected = GitHubShardedJournalQualification.requiredControls |> List.map GitHubShardedJournalQualification.controlId
let fixtureControls = json.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
if fixtureControls <> expected then fail "GSJQ-FIXTURE-INVENTORY" (String.concat "," fixtureControls)

let result control mutationRed baselineGreen: GitHubShardedJournalControlResult = { Control = control; MutationRed = mutationRed; BaselineGreen = baselineGreen }
let value code = function Ok result -> result | Error error -> fail code (sprintf "%A" error)
let digest c = String.replicate 64 c
let oid c = String.replicate 40 c
let blob json = let bytes = ShardedJournalAdapter.canonicalJson json |> value "GSJQ-JSON" in { Bytes = bytes; Digest = ShardedJournalAdapter.sha256 bytes }
let eventBlob generation = blob $"{{\"generation\":{generation},\"payload\":\"fixture\"}}"
let location = ShardedJournalAdapter.address Claim (json.GetProperty("generated").GetProperty("aggregateId").GetString()) |> Result.defaultWith (fail "GSJQ-ADDRESS" << sprintf "%A")
let makeCommit generation terminal parent prior operation commitOid =
    let event = eventBlob generation
    let aggregateDigest = digest "f"
    let checkpoint =
        if terminal then
            let content = blob $"{{\"aggregateDigest\":\"{aggregateDigest}\",\"highWaterGeneration\":{generation}}}"
            Some { Blob = content; HighWaterGeneration = generation; EventDigests = [ 1L .. generation ] |> List.map (eventBlob >> _.Digest); AggregateDigest = aggregateDigest; ReplayAggregateDigest = aggregateDigest }
        else None
    let unsigned = { SchemaVersion = 1; Address = location; Generation = generation; EventDigest = event.Digest; SnapshotDigest = checkpoint |> Option.map _.Blob.Digest; Terminal = terminal; PriorHeadDigest = prior; HeadDigest = "" }
    let head = { unsigned with HeadDigest = ShardedJournalAdapter.journalHeadBytes unsigned |> ShardedJournalAdapter.sha256 }
    { CommitOid = commitOid; ParentOid = parent; TreeOid = oid "b"; OperationId = operation; Head = head; HeadBytes = ShardedJournalAdapter.journalHeadBytes head; Event = event; Checkpoint = checkpoint }
let rootCommit = makeCommit 1L false None None "fixture-root" (oid "a")
let rootHead = rootCommit.Head
let nextCommit = makeCommit 2L false (Some rootCommit.CommitOid) (Some rootHead.HeadDigest) "fixture-operation-42" (oid "c")
let nextHead = nextCommit.Head
let baseline = JournalComplete("fixture-revision-2", [ rootCommit; nextCommit ])
let baselineGreen () = ShardedJournalAdapter.validate location baseline |> Result.isOk
let target = "refs/heads/fsgg/v2/journal/**/*"
let writer = { Id = 21872113L; Name = "v2-journal-writer"; Active = true; Target = target; BypassAppIds = [ 4166418L ]; RestrictsCreationAndUpdate = true; RejectsDeletionAndNonFastForward = false }
let integrity = { Id = 21872115L; Name = "v2-journal-integrity"; Active = true; Target = target; BypassAppIds = []; RestrictsCreationAndUpdate = false; RejectsDeletionAndNonFastForward = true }
let protection = { Complete = true; RepositoryId = 1351660651L; Writer = writer; Integrity = integrity; EffectiveRulesComplete = true; EffectiveRules = [ CreationRestricted; UpdateRestricted; DeletionRejected; NonFastForwardRejected ] }

let generatedResults () =
    GitHubShardedJournalQualification.requiredControls |> List.map (fun control ->
        let red =
            match control with
            | WrongShard -> ShardedJournalAdapter.validate { location with Shard = "ff" } baseline = Error WrongJournalShard
            | MissingParent -> ShardedJournalAdapter.validate location (JournalComplete("bad", [ rootCommit; { nextCommit with ParentOid = Some(oid "f") } ])) = Error MissingJournalParent
            | DuplicateGeneration -> ShardedJournalAdapter.validate location (JournalComplete("bad", [ rootCommit; { nextCommit with Head = rootHead } ])) = Error(DuplicateJournalGeneration 1L)
            | DigestMismatch -> ShardedJournalAdapter.validate location (JournalComplete("bad", [ { rootCommit with Event = { rootCommit.Event with Digest = digest "0" } } ])) |> Result.isError
            | UnknownSchema -> ShardedJournalAdapter.validate location (JournalComplete("bad", [ { rootCommit with Head = { rootHead with SchemaVersion = 99 } } ])) = Error(UnknownJournalSchema 99)
            | StaleParent -> ShardedJournalAdapter.planCas "fixture-operation-42" (ShardedJournalAdapter.validate location (JournalComplete("root", [ rootCommit ])) |> value "GSJQ-ROOT") { nextCommit with ParentOid = Some(oid "f") } |> Result.isError
            | AmbiguousResponse -> let before = ShardedJournalAdapter.validate location (JournalComplete("root", [ rootCommit ])) |> value "GSJQ-ROOT" in let proposal = ShardedJournalAdapter.planCas "fixture-operation-42" before nextCommit |> value "GSJQ-PLAN" in ShardedJournalAdapter.reconcile proposal ReceiveResponseUnknown (JournalComplete("root", [ rootCommit ])) = ResponseUnknownRequiresReread
            | Rewind -> ShardedJournalAdapter.validate location (JournalDivergent "rewind") = Error(DivergentJournal "rewind")
            | Deletion -> ShardedJournalAdapter.validate location JournalDeleted = Error DeletedJournal
            | Divergence -> ShardedJournalAdapter.validate location (JournalDivergent "fork") = Error(DivergentJournal "fork")
            | StaleFence -> ShardedJournalAdapter.authorizeEffect { Address = location; JournalCommit = nextCommit.CommitOid; Generation = 1L } baseline = Error EffectRefusal.StaleFence
            | TerminalAppend -> let terminal = makeCommit 1L true None None "fixture-root" rootCommit.CommitOid in ShardedJournalAdapter.validate location (JournalComplete("bad", [ terminal; nextCommit ])) = Error TerminalJournalAppend
            | Compaction -> let terminal = makeCommit 2L true (Some rootCommit.CommitOid) (Some rootHead.HeadDigest) "fixture-operation-42" nextCommit.CommitOid in let checkpoint = terminal.Checkpoint.Value in let invalid = { terminal with Checkpoint = Some { checkpoint with ReplayAggregateDigest = digest "0" } } in ShardedJournalAdapter.validate location (JournalComplete("terminal", [ rootCommit; invalid ])) |> Result.isError
            | RulesetDrift -> ShardedJournalAdapter.validateProtection { protection with Writer = { writer with Id = 7L } } = Error WriterRulesetDrift
            | TargetPattern -> ShardedJournalAdapter.validateProtection { protection with Writer = { writer with Target = "refs/heads/fsgg/v2/journal/**" } } = Error TargetPatternDrift
            | Bypass -> ShardedJournalAdapter.validateProtection { protection with Integrity = { integrity with BypassAppIds = [ 4166418L ] } } = Error BypassDrift
            | AcquisitionOrder -> let other = ShardedJournalAdapter.address Review "other" |> value "GSJQ-OTHER" in let plan = ShardedJournalAdapter.planSaga "saga" [ { Address = other; ExpectedGeneration = 1L }; { Address = location; ExpectedGeneration = 1L } ] |> value "GSJQ-SAGA" in plan.AcquisitionOrder = List.sortBy (fun touch -> ShardedJournalAdapter.journalKind touch.Address.Kind, touch.Address.Shard, touch.Address.Digest) plan.AcquisitionOrder
            | Compensation -> let plan = ShardedJournalAdapter.planSaga "saga" [ { Address = location; ExpectedGeneration = 1L } ] |> value "GSJQ-SAGA" in let conflict = ShardedJournalAdapter.planConflict plan plan.AcquisitionOrder plan.AcquisitionOrder |> value "GSJQ-CONFLICT" in conflict.CompensateApplied.Head.OriginalResultRetained
        result control red (baselineGreen ()))

let independentResults () =
    let complete commits = JournalComplete("independent", commits)
    let before = ShardedJournalAdapter.validate location (complete [ rootCommit ]) |> value "GSJQ-INDEPENDENT-ROOT"
    let proposal = ShardedJournalAdapter.planCas "fixture-operation-42" before nextCommit |> value "GSJQ-INDEPENDENT-PLAN"
    let saga = ShardedJournalAdapter.planSaga "independent-saga" [ { Address = location; ExpectedGeneration = 1L } ] |> value "GSJQ-INDEPENDENT-SAGA"
    [ result WrongShard (ShardedJournalAdapter.validate { location with Shard = "00" } baseline |> Result.isError) (baselineGreen ())
      result MissingParent (ShardedJournalAdapter.validate location (complete [ rootCommit; { nextCommit with ParentOid = None } ]) = Error MissingJournalParent) (baselineGreen ())
      result DuplicateGeneration (ShardedJournalAdapter.validate location (complete [ rootCommit; { nextCommit with Head = rootHead } ]) = Error(DuplicateJournalGeneration 1L)) (baselineGreen ())
      result DigestMismatch (ShardedJournalAdapter.validate location (complete [ { rootCommit with Event = { rootCommit.Event with Digest = digest "1" } } ]) |> Result.isError) (baselineGreen ())
      result UnknownSchema (ShardedJournalAdapter.validate location (complete [ { rootCommit with Head = { rootHead with SchemaVersion = 2 } } ]) = Error(UnknownJournalSchema 2)) (baselineGreen ())
      result StaleParent (ShardedJournalAdapter.planCas "fixture-operation-42" before { nextCommit with ParentOid = Some(oid "9") } |> Result.isError) (baselineGreen ())
      result AmbiguousResponse (ShardedJournalAdapter.reconcile proposal ReceiveResponseUnknown (complete [ rootCommit ]) = ResponseUnknownRequiresReread) (baselineGreen ())
      result Rewind (ShardedJournalAdapter.validate location (JournalDivergent "rewound") = Error(DivergentJournal "rewound")) (baselineGreen ())
      result Deletion (ShardedJournalAdapter.validate location JournalDeleted = Error DeletedJournal) (baselineGreen ())
      result Divergence (ShardedJournalAdapter.validate location (JournalDivergent "forked") = Error(DivergentJournal "forked")) (baselineGreen ())
      result StaleFence (ShardedJournalAdapter.authorizeEffect { Address = location; JournalCommit = nextCommit.CommitOid; Generation = 99L } baseline = Error EffectRefusal.StaleFence) (baselineGreen ())
      result TerminalAppend (let terminal = makeCommit 1L true None None "fixture-root" rootCommit.CommitOid in ShardedJournalAdapter.validate location (complete [ terminal; nextCommit ]) = Error TerminalJournalAppend) (baselineGreen ())
      result Compaction (let terminal = makeCommit 2L true (Some rootCommit.CommitOid) (Some rootHead.HeadDigest) "fixture-operation-42" nextCommit.CommitOid in let checkpoint = terminal.Checkpoint.Value in ShardedJournalAdapter.validate location (complete [ rootCommit; { terminal with Checkpoint = Some { checkpoint with EventDigests = [] } } ]) |> Result.isError) (baselineGreen ())
      result RulesetDrift (ShardedJournalAdapter.validateProtection { protection with Integrity = { integrity with Id = 0L } } = Error IntegrityRulesetDrift) (baselineGreen ())
      result TargetPattern (ShardedJournalAdapter.validateProtection { protection with Integrity = { integrity with Target = "refs/heads/fsgg/v2/journal/**" } } = Error TargetPatternDrift) (baselineGreen ())
      result Bypass (ShardedJournalAdapter.validateProtection { protection with Writer = { writer with BypassAppIds = [] } } = Error BypassDrift) (baselineGreen ())
      result AcquisitionOrder (saga.PersistBeforeEffects = saga.AcquisitionOrder) (baselineGreen ())
      result Compensation (ShardedJournalAdapter.planConflict saga [ saga.AcquisitionOrder.Head ] [ saga.AcquisitionOrder.Head ] |> Result.map (fun conflict -> conflict.CompensateApplied.Head.OriginalResultRetained) = Ok true) (baselineGreen ()) ]

let generated = generatedResults ()
let independent = independentResults ()
match GitHubShardedJournalQualification.validate generated independent with
| Ok () -> printfn "github-sharded-journal-contract OK controls=%d q=Q3 network=offline provenance=synthetic" generated.Length
| Error findings -> findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message); fail "GSJQ-FAILED" $"{findings.Length} finding(s)"
fixture.Dispose()
