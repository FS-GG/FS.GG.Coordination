#r "../../src/FS.GG.Coordination.GitHub/bin/Debug/net10.0/FS.GG.Coordination.GitHub.dll"
#load "../../src/FS.GG.Coordination.GitHub/MigrationEffectAuthority.fs"

open System
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.GitHub

module Registry = V1AdmissionRegistry
let check condition message = if not condition then failwith message
let digest value = Registry.sha256Digest value |> Result.defaultWith failwith
let oid value = Registry.gitObjectId value |> Result.defaultWith failwith
let hex (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
let gitOid kind (bytes: byte array) =
    let header = Encoding.UTF8.GetBytes($"{kind} {bytes.Length}\u0000")
    SHA1.HashData(Array.append header bytes) |> Convert.ToHexString |> _.ToLowerInvariant() |> oid
let treeBytes entries =
    entries |> List.sortBy fst |> List.collect (fun (name, value) ->
        (Encoding.UTF8.GetBytes($"100644 {name}\u0000") |> Array.toList)
        @ (Registry.gitObjectIdValue value |> Convert.FromHexString |> Array.toList)) |> List.toArray

let candidate = digest (String.replicate 64 "a")
let artifactBytes = Encoding.UTF8.GetBytes "selected-provider-request"
let artifact = digest (hex artifactBytes)
let authorityPort () =
    let trust = digest (String.replicate 64 "b")
    let eventBytes = Encoding.UTF8.GetBytes($"{{\"fleetId\":\"fs-gg-production\",\"manifestSha256\":\"{Registry.sha256Value candidate}\",\"phase\":\"OperatingV1\",\"schema\":\"fsgg.github-substrate.epoch-event/1\",\"trustAnchorSha256\":\"{Registry.sha256Value trust}\"}}")
    let eventOid = gitOid "blob" eventBytes
    let address = ShardedJournalAdapter.address Cutover "fleet-cutover:fs-gg-production" |> Result.defaultWith (string >> failwith)
    let headBytes = ShardedJournalAdapter.journalHeadBytes
                        { SchemaVersion=1; Address=address; Generation=1L
                          EventDigest=ShardedJournalAdapter.sha256 eventBytes; SnapshotDigest=None
                          Terminal=false; PriorHeadDigest=None; HeadDigest="" }
    let headOid = gitOid "blob" headBytes
    let tree = treeBytes [ "event.json", eventOid; "head.json", headOid ]
    let treeOid = gitOid "tree" tree
    let commitBytes = Encoding.UTF8.GetBytes($"tree {Registry.gitObjectIdValue treeOid}\nauthor test <test@fs.gg> 0 +0000\ncommitter test <test@fs.gg> 0 +0000\n\nfixture\n")
    let commit = gitOid "commit" commitBytes
    let objects =
        { Repository="FS-GG/FS.GG.Coordination.Authority"; RepositoryId=1351660651L
          Ref="refs/heads/fsgg/v2/journal/cutover/d5"; FirstHead=commit; TagTarget=commit
          Commit=commit; Parent=None; GenesisCommit=commit; Ancestry=[commit]
          CommitTree=treeOid; CommitBytes=commitBytes; TreeBytes=tree
          TreeEntries=Map.ofList [ "event.json", eventOid; "head.json", headOid ]
          EventBlob=eventOid, eventBytes; HeadBlob=headOid, headBytes
          TrustAnchorSha256=trust; ManifestSha256=candidate; ClaimJournals=Map.empty }
    let port = { ReadObjects=(fun () -> Ok objects); RereadHead=(fun () -> Ok commit) }
    let snapshot = Registry.readVerified port |> Result.defaultWith (String.concat "," >> failwith)
    port, snapshot, commit

let registryAddress () = ShardedJournalAdapter.address Operation "fleet-v1-admission:fs-gg-production" |> Result.defaultWith (string >> failwith)
let genesisRead () =
    let address = registryAddress ()
    let eventBytes = ShardedJournalAdapter.canonicalJson($"{{\"commandId\":\"fixture-initialize\",\"kind\":\"initialize\",\"manifestSha256\":\"{Registry.sha256Value candidate}\",\"payload\":{{}},\"round\":1,\"schema\":\"fsgg.github-substrate.admission-event/1\"}}") |> Result.defaultWith failwith
    let eventDigest = ShardedJournalAdapter.sha256 eventBytes
    let provisional =
        { SchemaVersion=1; Address=address; Generation=1L; EventDigest=eventDigest
          SnapshotDigest=None; Terminal=false; PriorHeadDigest=None; HeadDigest=String.replicate 64 "0" }
    let head = { provisional with HeadDigest=ShardedJournalAdapter.journalHeadBytes provisional |> ShardedJournalAdapter.sha256 }
    let headBytes = ShardedJournalAdapter.journalHeadBytes head
    let eventOid, headOid = gitOid "blob" eventBytes, gitOid "blob" headBytes
    let tree = treeBytes [ "event.json", eventOid; "head.json", headOid ]
    let treeOid = gitOid "tree" tree
    let commitBytes = Encoding.UTF8.GetBytes($"tree {Registry.gitObjectIdValue treeOid}\nauthor FS.GG Coordination <coordination@fs.gg> 0 +0000\ncommitter FS.GG Coordination <coordination@fs.gg> 0 +0000\n\nfsgg admission fixture-initialize\n")
    let commitOid = gitOid "commit" commitBytes
    let commit =
        { CommitOid=Registry.gitObjectIdValue commitOid; ParentOid=None; TreeOid=Registry.gitObjectIdValue treeOid
          OperationId="fixture-initialize"; Head=head; HeadBytes=headBytes
          Event={ Bytes=eventBytes; Digest=eventDigest }; Checkpoint=None }
    { Repository="FS-GG/FS.GG.Coordination.Authority"; RepositoryId=1351660651L; Ref=address.Ref
      FirstHead=Some commitOid; SecondHead=Some commitOid; Observation=JournalComplete("fixture-genesis", [commit])
      CommitBytes=Map.ofList [ (commit.CommitOid, commitBytes) ]; TreeBytes=Map.ofList [ (commit.TreeOid, tree) ] }

let appendRead (read: RegistryJournalRead) proposal =
    let cas, objects = Registry.proposalCas proposal, Registry.proposalObjects proposal
    let commits = match read.Observation with JournalComplete(_, values) -> values | _ -> failwith "fixture journal"
    { read with FirstHead=Some objects.CommitObjectId; SecondHead=Some objects.CommitObjectId
                Observation=JournalComplete("test", commits @ [cas.ProposedCommit])
                CommitBytes=Map.add (Registry.gitObjectIdValue objects.CommitObjectId) objects.CommitBytes read.CommitBytes
                TreeBytes=Map.add (Registry.gitObjectIdValue objects.TreeObjectId) objects.TreeBytes read.TreeBytes }

let setup () =
    let authority, snapshot, commit = authorityPort ()
    let mutable current = genesisRead ()
    let initial = Registry.restore current |> Result.defaultWith (String.concat "," >> failwith)
    let context =
        { Round=1L; Manifest=candidate; OperationId="migration-copy-op"; OperationGeneration=1L
          Actor="recovery-owner"; Receiver="sandbox-copy"; Kind="issue-edit"
          CanonicalTarget="FS-GG/copy#17"; Claim=NoClaimRequired
          IntentDigest=digest (String.replicate 64 "d"); TouchSetDigest=digest (String.replicate 64 "e")
          OriginatingEpochCommit=commit; OriginatingEpochGeneration=1L }
    let admitted =
        match Registry.admit (Registry.head initial) snapshot context initial with
        | RegistryAdmissionAppended value -> value
        | other -> failwithf "fixture admission failed: %A" other
    let proposal = Registry.planAppend "admit-fixture" current admitted |> Result.defaultWith (String.concat "," >> failwith)
    let admissionPort =
        { Read=(fun _ -> current)
          Write=(fun value -> current <- appendRead current value; ReceiveAccepted) }
    match Registry.appendAndReconcile admissionPort proposal with
    | DurableAppendAccepted(_, None) -> ()
    | other -> failwithf "fixture admission append failed: %A" other
    let registry = Registry.restore current |> Result.defaultWith (String.concat "," >> failwith)
    authority, commit, current, registry

let authority, epochCommit, admittedRead, admittedRegistry = setup ()
let binding =
    { Repository="FS-GG/copy"; RepositoryId=999L; ProjectNodeId="PVT_copy"
      CandidateSha256=candidate; ArtifactSha256=artifact
      ManifestSeal=Registry.sha256Value candidate; OperationId="migration-copy-op"
      OperationGeneration=1L; ClaimGeneration=None; EpochCommit=epochCommit; EpochGeneration=1L
      RegistryHead=Registry.head admittedRegistry; RegistryGeneration=Registry.generation admittedRegistry
      RecoveryOwner="recovery-owner" }
let step =
    { OperationId=binding.OperationId; IdempotencyKey="effect-1"; ManifestSeal=binding.ManifestSeal
      Effect=MigrationEffect.SetIssueType(999L, "ISSUE_copy", "TYPE_copy")
      TargetIdentity="repository:999/issue:ISSUE_copy/type"
      ExpectedTargetRevision="revision-1"; ExpectedTargetSha256=String.replicate 64 "1"
      DesiredTargetSha256=String.replicate 64 "2"
      EpochGeneration=1L; EpochCommit=Registry.gitObjectIdValue epochCommit
      AuthorityFence={ AdmissionGeneration=binding.RegistryGeneration
                       AdmissionCommit=Registry.gitObjectIdValue binding.RegistryHead
                       OperationGeneration=1L; OperationCommit=Registry.gitObjectIdValue binding.RegistryHead
                       Claim=None; SealCommit=Registry.gitObjectIdValue binding.RegistryHead
                       RegistryCommit=Registry.gitObjectIdValue binding.RegistryHead }
      JournalGeneration=0L; JournalHead=String.replicate 40 "c"; Seal="" }
    |> MigrationStepExecution.sealStep |> Result.defaultWith (sprintf "%A" >> failwith)

let mutable writes = 0
let mutable current = admittedRead
let ports outcome =
    { Authority=authority
      Journal={ Read=(fun _ -> current)
                Write=(fun proposal ->
                    writes <- writes + 1
                    match outcome with
                    | ReceiveAccepted | ReceiveResponseUnknown -> current <- appendRead current proposal
                    | _ -> ()
                    outcome) } }
let expectRefusal reason result =
    match result with
    | EffectIntentRefused reasons when List.contains reason reasons -> ()
    | other -> failwithf "expected %s refusal, got %A" reason other
let expectIndeterminate reason result =
    match result with
    | EffectIntentIndeterminate reasons when List.contains reason reasons -> ()
    | other -> failwithf "expected %s indeterminate result, got %A" reason other

let run b s bytes outcome = MigrationEffectAuthority.prepare (ports outcome) b s "effect-1" bytes
expectRefusal "sandbox-target" (run binding { step with Effect=MigrationEffect.SetIssueType(1000L, "ISSUE_copy", "TYPE_copy") } artifactBytes ReceiveAccepted)
expectRefusal "sandbox-target" (run binding { step with Effect=MigrationEffect.SetProjectField("PVT_other", "ITEM", "FIELD", "VALUE") } artifactBytes ReceiveAccepted)
expectRefusal "manifest-seal" (run { binding with ManifestSeal=String.replicate 64 "f" } step artifactBytes ReceiveAccepted)
expectRefusal "artifact-digest" (run binding step (Encoding.UTF8.GetBytes "altered") ReceiveAccepted)
expectRefusal "recovery-owner" (run { binding with RecoveryOwner="" } step artifactBytes ReceiveAccepted)
expectRefusal "claim-binding" (run { binding with ClaimGeneration=Some 2L } step artifactBytes ReceiveAccepted)
expectRefusal "epoch-binding" (run { binding with EpochGeneration=2L } step artifactBytes ReceiveAccepted)
expectRefusal "registry-binding" (run { binding with RegistryHead=oid (String.replicate 40 "f") } step artifactBytes ReceiveAccepted)
check (writes = 0) "invalid preflight reached journal writer"

match run binding step artifactBytes ReceiveAccepted with
| EffectIntentDurable _ -> ()
| other -> failwithf "expected durable effect intent: %A" other
check (writes = 1) "durable intent was not appended once"
expectIndeterminate "stale-admission-head" (run binding step artifactBytes ReceiveAccepted)
check (writes = 1) "stale admission reached journal writer"
let currentRegistry = Registry.restore current |> Result.defaultWith (String.concat "," >> failwith)
let repeatedBinding = { binding with RegistryHead=Registry.head currentRegistry; RegistryGeneration=Registry.generation currentRegistry }
let repeatedStep =
    { step with AuthorityFence=
                    { step.AuthorityFence with RegistryCommit=Registry.gitObjectIdValue repeatedBinding.RegistryHead
                                               AdmissionGeneration=repeatedBinding.RegistryGeneration } }
    |> MigrationStepExecution.sealStep |> Result.defaultWith (sprintf "%A" >> failwith)
expectRefusal "effect-already-in-flight" (run repeatedBinding repeatedStep artifactBytes ReceiveAccepted)
check (writes = 1) "reused effect reached journal writer"

current <- { admittedRead with Repository="FS-GG/foreign" }
writes <- 0
expectIndeterminate "admission-journal-identity" (run binding step artifactBytes ReceiveAccepted)
check (writes = 0) "wrong authority repository reached journal writer"

current <- admittedRead
writes <- 0
match run binding step artifactBytes ReceiveParentConflict with
| EffectIntentParentConflict -> ()
| other -> failwithf "expected parent conflict: %A" other
check (writes = 1) "parent conflict wrote more than once"

current <- admittedRead
writes <- 0
match run binding step artifactBytes ReceiveResponseUnknown with
| EffectIntentIndeterminate [ "effect-permit-unavailable" ] -> ()
| other -> failwithf "expected ambiguous append refusal: %A" other
check (writes = 1) "ambiguous append wrote more than once"

printfn "MigrationEffectAuthorityTests: PASS (copy, manifest, artifact, epoch, claim, owner, reuse, parent, ambiguous)"
