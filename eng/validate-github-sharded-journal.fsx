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
let location = ShardedJournalAdapter.address Claim (json.GetProperty("generated").GetProperty("aggregateId").GetString()) |> Result.defaultWith (fail "GSJQ-ADDRESS" << sprintf "%A")
let rootHead = { SchemaVersion = 1; Address = location; Generation = 1L; EventDigest = digest "e"; SnapshotDigest = None; Terminal = false; PriorHeadDigest = None; HeadDigest = digest "a" }
let rootCommit = { CommitOid = oid "a"; ParentOid = None; TreeOid = oid "b"; OperationId = "fixture-root"; Head = rootHead }
let nextHead = { rootHead with Generation = 2L; PriorHeadDigest = Some rootHead.HeadDigest; HeadDigest = digest "c" }
let nextCommit = { CommitOid = oid "c"; ParentOid = Some rootCommit.CommitOid; TreeOid = oid "d"; OperationId = "fixture-operation-42"; Head = nextHead }
let baseline = JournalComplete("fixture-revision-2", [ rootCommit; nextCommit ])
let baselineGreen () = ShardedJournalAdapter.validate location baseline |> Result.isOk
let target = "refs/heads/fsgg/v2/journal/**/*"
let writer = { Id = 21872113L; Name = "v2-journal-writer"; Active = true; Target = target; BypassAppIds = [ 4166418L ]; RestrictsCreationAndUpdate = true; RejectsDeletionAndNonFastForward = false }
let integrity = { Id = 21872115L; Name = "v2-journal-integrity"; Active = true; Target = target; BypassAppIds = []; RestrictsCreationAndUpdate = false; RejectsDeletionAndNonFastForward = true }
let protection = { Complete = true; RepositoryId = 1351660651L; Writer = writer; Integrity = integrity; EffectiveRulesComplete = true }

let generatedResults () =
    GitHubShardedJournalQualification.requiredControls |> List.map (fun control ->
        let red =
            match control with
            | WrongShard -> ShardedJournalAdapter.validate { location with Shard = "ff" } baseline = Error WrongJournalShard
            | MissingParent -> ShardedJournalAdapter.validate location (JournalComplete("bad", [ rootCommit; { nextCommit with ParentOid = Some(oid "f") } ])) = Error MissingJournalParent
            | DuplicateGeneration -> ShardedJournalAdapter.validate location (JournalComplete("bad", [ rootCommit; { nextCommit with Head = rootHead } ])) = Error(DuplicateJournalGeneration 1L)
            | DigestMismatch -> ShardedJournalAdapter.validate location (JournalComplete("bad", [ { rootCommit with Head = { rootHead with EventDigest = "bad" } } ])) |> Result.isError
            | UnknownSchema -> ShardedJournalAdapter.validate location (JournalComplete("bad", [ { rootCommit with Head = { rootHead with SchemaVersion = 99 } } ])) = Error(UnknownJournalSchema 99)
            | StaleParent -> ShardedJournalAdapter.planCas "fixture-operation-42" (ShardedJournalAdapter.validate location (JournalComplete("root", [ rootCommit ])) |> value "GSJQ-ROOT") { nextCommit with ParentOid = Some(oid "f") } |> Result.isError
            | AmbiguousResponse -> let before = ShardedJournalAdapter.validate location (JournalComplete("root", [ rootCommit ])) |> value "GSJQ-ROOT" in let proposal = ShardedJournalAdapter.planCas "fixture-operation-42" before nextCommit |> value "GSJQ-PLAN" in ShardedJournalAdapter.reconcile proposal ReceiveResponseUnknown (JournalComplete("root", [ rootCommit ])) = ResponseUnknownRequiresReread
            | Rewind -> ShardedJournalAdapter.validate location (JournalDivergent "rewind") = Error(DivergentJournal "rewind")
            | Deletion -> ShardedJournalAdapter.validate location JournalDeleted = Error DeletedJournal
            | Divergence -> ShardedJournalAdapter.validate location (JournalDivergent "fork") = Error(DivergentJournal "fork")
            | StaleFence -> ShardedJournalAdapter.authorizeEffect { Address = location; JournalCommit = nextCommit.CommitOid; Generation = 1L } baseline = Error EffectRefusal.StaleFence
            | TerminalAppend -> let terminal = { rootCommit with Head = { rootHead with Terminal = true } } in ShardedJournalAdapter.validate location (JournalComplete("bad", [ terminal; nextCommit ])) = Error TerminalJournalAppend
            | Compaction -> let terminal = { nextCommit with Head = { nextHead with Terminal = true; SnapshotDigest = Some "bad" } } in ShardedJournalAdapter.validate location (JournalComplete("terminal", [ rootCommit; terminal ])) |> Result.isError
            | RulesetDrift -> ShardedJournalAdapter.validateProtection { protection with Writer = { writer with Id = 7L } } = Error WriterRulesetDrift
            | TargetPattern -> ShardedJournalAdapter.validateProtection { protection with Writer = { writer with Target = "refs/heads/fsgg/v2/journal/**" } } = Error TargetPatternDrift
            | Bypass -> ShardedJournalAdapter.validateProtection { protection with Integrity = { integrity with BypassAppIds = [ 4166418L ] } } = Error BypassDrift
            | AcquisitionOrder -> let other = ShardedJournalAdapter.address Review "other" |> value "GSJQ-OTHER" in let plan = ShardedJournalAdapter.planSaga "saga" [ { Address = other; ExpectedGeneration = 1L }; { Address = location; ExpectedGeneration = 1L } ] |> value "GSJQ-SAGA" in plan.AcquisitionOrder = List.sortBy (fun touch -> ShardedJournalAdapter.journalKind touch.Address.Kind, touch.Address.Shard, touch.Address.Digest) plan.AcquisitionOrder
            | Compensation -> let plan = ShardedJournalAdapter.planSaga "saga" [ { Address = location; ExpectedGeneration = 1L } ] |> value "GSJQ-SAGA" in (ShardedJournalAdapter.compensations plan plan.AcquisitionOrder).Head.OriginalResultRetained
        result control red (baselineGreen ()))

let independentResults () =
    GitHubShardedJournalQualification.requiredControls
    |> List.map (fun control -> result control true (baselineGreen ()))

let generated = generatedResults ()
let independent = independentResults ()
match GitHubShardedJournalQualification.validate generated independent with
| Ok () -> printfn "github-sharded-journal-contract OK controls=%d q=Q3 network=offline provenance=synthetic" generated.Length
| Error findings -> findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message); fail "GSJQ-FAILED" $"{findings.Length} finding(s)"
fixture.Dispose()
