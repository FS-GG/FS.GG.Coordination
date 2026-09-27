#r "../../src/FS.GG.Coordination.GitHub/bin/Debug/net10.0/FS.GG.Coordination.GitHub.dll"

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
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
          CanonicalTarget="repository:999/name:FS-GG/copy/project:PVT_copy"; Claim=NoClaimRequired
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
let admissionCommit = oid (String.replicate 40 "a")
let operationCommit = oid (String.replicate 40 "b")
let sealCommit = oid (String.replicate 40 "d")
let baseSelection =
    { Repository="FS-GG/copy"; RepositoryId=999L; ProjectNodeId="PVT_copy"
      CandidateSha256=candidate; ArtifactSha256=digest (String.replicate 64 "0")
      RecoveryOwner="recovery-owner"; AdmissionCommit=admissionCommit
      AdmissionGeneration=Registry.generation admittedRegistry
      OperationCommit=operationCommit; OperationGeneration=1L; SealCommit=sealCommit }
let step =
    { OperationId="migration-copy-op"; IdempotencyKey="effect-1"
      ManifestSeal=Registry.sha256Value candidate
      Effect=MigrationEffect.SetIssueType(999L, "ISSUE_copy", "TYPE_copy")
      TargetIdentity="repository:999/issue:ISSUE_copy/type"
      ExpectedTargetRevision="revision-1"; ExpectedTargetSha256=String.replicate 64 "1"
      DesiredTargetSha256=String.replicate 64 "2"
      EpochGeneration=1L; EpochCommit=Registry.gitObjectIdValue epochCommit
      AuthorityFence={ AdmissionGeneration=baseSelection.AdmissionGeneration
                       AdmissionCommit=Registry.gitObjectIdValue admissionCommit
                       OperationGeneration=1L; OperationCommit=Registry.gitObjectIdValue operationCommit
                       Claim=None; SealCommit=Registry.gitObjectIdValue sealCommit
                       RegistryCommit=Registry.gitObjectIdValue (Registry.head admittedRegistry) }
      JournalGeneration=0L; JournalHead=String.replicate 40 "c"; Seal="" }
    |> MigrationStepExecution.sealStep |> Result.defaultWith (sprintf "%A" >> failwith)
let selectedArtifact = MigrationEffectAuthority.canonicalRequest baseSelection step |> hex |> digest
let selection = { baseSelection with ArtifactSha256=selectedArtifact }

let mutable writes = 0
let mutable current = admittedRead
let ports selected outcome =
    { ReadVerifiedSelection=(fun () -> Ok selected)
      Admission={ Authority=authority
                  Journal={ Read=(fun _ -> current)
                            Write=(fun proposal ->
                                writes <- writes + 1
                                match outcome with
                                | ReceiveAccepted | ReceiveResponseUnknown -> current <- appendRead current proposal
                                | _ -> ()
                                outcome) } } }
let expectRefusal reason result =
    match result with
    | EffectIntentRefused reasons when List.contains reason reasons -> ()
    | other -> failwithf "expected %s refusal, got %A" reason other
let expectIndeterminate reason result =
    match result with
    | EffectIntentIndeterminate reasons when List.contains reason reasons -> ()
    | other -> failwithf "expected %s indeterminate result, got %A" reason other

let run selected s outcome = MigrationEffectAuthority.prepare (ports selected outcome) s
expectRefusal "sandbox-target" (run selection { step with Effect=MigrationEffect.SetIssueType(1000L, "ISSUE_copy", "TYPE_copy") } ReceiveAccepted)
expectRefusal "sandbox-target" (run selection { step with Effect=MigrationEffect.SetProjectField("PVT_other", "ITEM", "FIELD", "VALUE") } ReceiveAccepted)
expectRefusal "manifest-seal" (run { selection with CandidateSha256=digest (String.replicate 64 "f") } step ReceiveAccepted)
expectRefusal "artifact-digest" (run { selection with ArtifactSha256=digest (String.replicate 64 "f") } step ReceiveAccepted)
expectRefusal "recovery-owner-admission-binding" (run { selection with RecoveryOwner="foreign-owner" } step ReceiveAccepted)
expectRefusal "operation-binding" (run { selection with OperationGeneration=2L } step ReceiveAccepted)
expectRefusal "authority-commit-alias" (run { selection with SealCommit=operationCommit } step ReceiveAccepted)
expectRefusal "admission-binding" (run { selection with AdmissionCommit=oid (String.replicate 40 "f") } step ReceiveAccepted)
let rebound selected selectedStep =
    { selected with ArtifactSha256=MigrationEffectAuthority.canonicalRequest selected selectedStep |> hex |> digest }
let differentName = rebound { selection with Repository="FS-GG/other" } step
expectRefusal "copy-admission-binding" (run differentName step ReceiveAccepted)
let differentOwner = rebound { selection with RecoveryOwner="foreign-owner" } step
expectRefusal "recovery-owner-admission-binding" (run differentOwner step ReceiveAccepted)
let differentRepositoryStep =
    { step with Effect=MigrationEffect.SetIssueType(1000L, "ISSUE_copy", "TYPE_copy")
                TargetIdentity="repository:1000/issue:ISSUE_copy/type" }
    |> MigrationStepExecution.sealStep |> Result.defaultWith (sprintf "%A" >> failwith)
let differentRepository = rebound { selection with RepositoryId=1000L } differentRepositoryStep
expectRefusal "copy-admission-binding" (run differentRepository differentRepositoryStep ReceiveAccepted)
let differentProjectStep =
    { step with Effect=MigrationEffect.SetProjectField("PVT_other", "ITEM", "FIELD", "VALUE")
                TargetIdentity="project:PVT_other/item:ITEM/field:FIELD" }
    |> MigrationStepExecution.sealStep |> Result.defaultWith (sprintf "%A" >> failwith)
let differentProject = rebound { selection with ProjectNodeId="PVT_other" } differentProjectStep
expectRefusal "copy-admission-binding" (run differentProject differentProjectStep ReceiveAccepted)
let differentId =
    { step with IdempotencyKey="effect-foreign" }
    |> MigrationStepExecution.sealStep |> Result.defaultWith (sprintf "%A" >> failwith)
expectRefusal "artifact-digest" (run selection differentId ReceiveAccepted)
check (writes = 0) "invalid preflight reached journal writer"
let mutable selectionReads = 0
let movingPorts =
    { ports selection ReceiveAccepted with
        ReadVerifiedSelection=(fun () ->
            selectionReads <- selectionReads + 1
            Ok(if selectionReads = 1 then selection else differentName)) }
expectIndeterminate "effect-authority-moved" (MigrationEffectAuthority.prepare movingPorts step)
check (selectionReads = 2 && writes = 0) "moved selection reached journal writer"

match run selection step ReceiveAccepted with
| EffectIntentDurable _ -> ()
| other -> failwithf "expected durable effect intent: %A" other
check (writes = 1) "durable intent was not appended once"
let encodedIntent =
    match current.Observation with
    | JournalComplete(_, commits) -> commits |> List.last |> _.Event.Bytes
    | _ -> failwith "intent journal not complete"
let intentDoc = JsonDocument.Parse encodedIntent
let intentPayload = intentDoc.RootElement.GetProperty("payload").GetProperty("intent")
check (intentPayload.GetProperty("effectId").GetString() = step.IdempotencyKey)
      "persisted effect id differs from sealed step key"
let persistedBytes = intentPayload.GetProperty("canonicalRequestBase64").GetString() |> Convert.FromBase64String
check (persistedBytes = MigrationEffectAuthority.canonicalRequest selection step)
      "persisted replay bytes differ from sealed selection and step"
let replayDoc = JsonDocument.Parse persistedBytes
let replayFields = replayDoc.RootElement.EnumerateArray() |> Seq.map _.GetString() |> Seq.toArray
check (Array.contains selection.Repository replayFields && Array.contains step.Seal replayFields
       && Array.contains (Registry.gitObjectIdValue sealCommit) replayFields)
      "encoded replay lost copy or authority coordinates"
expectRefusal "registry-binding" (run selection step ReceiveAccepted)
check (writes = 1) "stale admission reached journal writer"
let currentRegistry = Registry.restore current |> Result.defaultWith (String.concat "," >> failwith)
let repeatedStep =
    { step with AuthorityFence=
                    { step.AuthorityFence with RegistryCommit=Registry.gitObjectIdValue (Registry.head currentRegistry) } }
    |> MigrationStepExecution.sealStep |> Result.defaultWith (sprintf "%A" >> failwith)
let repeatedSelection =
    { selection with ArtifactSha256=MigrationEffectAuthority.canonicalRequest selection repeatedStep |> hex |> digest }
expectRefusal "effect-id-request-conflict" (run repeatedSelection repeatedStep ReceiveAccepted)
check (writes = 1) "reused effect reached journal writer"

current <- { admittedRead with Repository="FS-GG/foreign" }
writes <- 0
expectIndeterminate "admission-journal-identity" (run selection step ReceiveAccepted)
check (writes = 0) "wrong authority repository reached journal writer"

current <- admittedRead
writes <- 0
match run selection step ReceiveParentConflict with
| EffectIntentParentConflict -> ()
| other -> failwithf "expected parent conflict: %A" other
check (writes = 1) "parent conflict wrote more than once"

current <- admittedRead
writes <- 0
match run selection step ReceiveResponseUnknown with
| EffectIntentIndeterminate [ "effect-permit-unavailable" ] -> ()
| other -> failwithf "expected ambiguous append refusal: %A" other
check (writes = 1) "ambiguous append wrote more than once"

printfn "MigrationEffectAuthorityTests: PASS"
