module FS.GG.Coordination.GitHubV1AdmissionRegistryTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open FS.GG.Coordination.GitHub
open Xunit

module Registry = V1AdmissionRegistry

let private digest c = String.replicate 64 c |> Registry.sha256Digest |> Result.defaultWith failwith
let private oid c = String.replicate 40 c |> Registry.gitObjectId |> Result.defaultWith failwith

let private gitOid kind (bytes: byte array) =
    let header = Encoding.UTF8.GetBytes($"{kind} {bytes.Length}\u0000")
    SHA1.HashData(Array.append header bytes)
    |> Convert.ToHexString |> _.ToLowerInvariant()
    |> Registry.gitObjectId |> Result.defaultWith failwith

let private treeBytes entries =
    entries
    |> List.sortBy fst
    |> List.collect (fun (name, value) ->
        (Encoding.UTF8.GetBytes($"100644 {name}\u0000") |> Array.toList)
        @ (Registry.gitObjectIdValue value |> Convert.FromHexString |> Array.toList))
    |> List.toArray

let private authorityWithTrust trust phase generation seal (transform: AuthorityGitObjects -> AuthorityGitObjects) =
    let manifest = digest "a"
    let sealFields =
        match seal with
        | None -> ""
        | Some(commit, sealGeneration, cohort) ->
            $",\"admissionSealCommit\":\"{Registry.gitObjectIdValue commit}\",\"admissionSealGeneration\":{sealGeneration},\"admissionCohortSha256\":\"{Registry.sha256Value cohort}\""
    let schema = if phase = "OperatingV1" then "fsgg.github-substrate.epoch-event/1" else "fsgg.github-substrate.epoch-event/2"
    let eventBytes = Encoding.UTF8.GetBytes($"{{\"fleetId\":\"fs-gg-production\",\"manifestSha256\":\"{Registry.sha256Value manifest}\",\"phase\":\"{phase}\",\"schema\":\"{schema}\",\"trustAnchorSha256\":\"{Registry.sha256Value trust}\"{sealFields}}}")
    let eventOid = gitOid "blob" eventBytes
    let address = ShardedJournalAdapter.address Cutover "fleet-cutover:fs-gg-production" |> Result.defaultWith (string >> failwith)
    let headBytes = ShardedJournalAdapter.journalHeadBytes
                        { SchemaVersion = 1; Address = address; Generation = generation
                          EventDigest = ShardedJournalAdapter.sha256 eventBytes; SnapshotDigest = None
                          Terminal = false; PriorHeadDigest = None; HeadDigest = "" }
    let headOid = gitOid "blob" headBytes
    let entries = Map.ofList [ "event.json", eventOid; "head.json", headOid ]
    let tree = treeBytes (Map.toList entries)
    let treeOid = gitOid "tree" tree
    let parent = if generation = 1L then None else Some(oid "c")
    let parentLine = parent |> Option.map (Registry.gitObjectIdValue >> sprintf "parent %s\n") |> Option.defaultValue ""
    let commitBytes = Encoding.UTF8.GetBytes($"tree {Registry.gitObjectIdValue treeOid}\n{parentLine}author test <test@fs.gg> 0 +0000\ncommitter test <test@fs.gg> 0 +0000\n\nfixture\n")
    let commit = gitOid "commit" commitBytes
    let observed =
        { Repository = "FS-GG/FS.GG.Coordination.Authority"; RepositoryId = 1351660651L
          Ref = "refs/heads/fsgg/v2/journal/cutover/d5"; FirstHead = commit; TagTarget = commit
          Commit = commit; Parent = parent; GenesisCommit = parent |> Option.defaultValue commit
          Ancestry = commit :: (parent |> Option.toList); CommitTree = treeOid; CommitBytes = commitBytes
          TreeBytes = tree; TreeEntries = entries; EventBlob = eventOid, eventBytes; HeadBlob = headOid, headBytes
          TrustAnchorSha256 = trust; ManifestSha256 = manifest; ClaimJournals = Map.empty }
        |> transform
    let port: AuthorityGitPort =
        { ReadObjects = fun () -> Ok observed
          RereadHead = fun () -> Ok observed.FirstHead }
    Registry.readVerified port |> Result.defaultWith (String.concat "," >> failwith), commit, manifest, observed, port

let private authority phase generation seal transform =
    authorityWithTrust (digest "b") phase generation seal transform

let private registryAddress () =
    ShardedJournalAdapter.address Operation "fleet-v1-admission:fs-gg-production"
    |> Result.defaultWith (string >> failwith)

let private absentRead () =
    let address = registryAddress ()
    { Repository = "FS-GG/FS.GG.Coordination.Authority"; RepositoryId = 1351660651L; Ref = address.Ref
      FirstHead = None; SecondHead = None; Observation = JournalDeleted
      CommitBytes = Map.empty; TreeBytes = Map.empty }

let private genesisRead manifest =
    let address = registryAddress ()
    let eventBytes =
        ShardedJournalAdapter.canonicalJson($"{{\"commandId\":\"fixture-initialize\",\"kind\":\"initialize\",\"manifestSha256\":\"{Registry.sha256Value manifest}\",\"payload\":{{}},\"round\":1,\"schema\":\"fsgg.github-substrate.admission-event/1\"}}")
        |> Result.defaultWith failwith
    let eventDigest = ShardedJournalAdapter.sha256 eventBytes
    let provisional =
        { SchemaVersion = 1; Address = address; Generation = 1L; EventDigest = eventDigest
          SnapshotDigest = None; Terminal = false; PriorHeadDigest = None; HeadDigest = String.replicate 64 "0" }
    let head = { provisional with HeadDigest = ShardedJournalAdapter.journalHeadBytes provisional |> ShardedJournalAdapter.sha256 }
    let headBytes = ShardedJournalAdapter.journalHeadBytes head
    let eventOid, headOid = gitOid "blob" eventBytes, gitOid "blob" headBytes
    let tree = treeBytes [ "event.json", eventOid; "head.json", headOid ]
    let treeOid = gitOid "tree" tree
    let commitBytes = Encoding.UTF8.GetBytes($"tree {Registry.gitObjectIdValue treeOid}\nauthor FS.GG Coordination <coordination@fs.gg> 0 +0000\ncommitter FS.GG Coordination <coordination@fs.gg> 0 +0000\n\nfsgg admission fixture-initialize\n")
    let commitOid = gitOid "commit" commitBytes
    let commit =
        { CommitOid = Registry.gitObjectIdValue commitOid; ParentOid = None; TreeOid = Registry.gitObjectIdValue treeOid
          OperationId = "fixture-initialize"; Head = head; HeadBytes = headBytes
          Event = { Bytes = eventBytes; Digest = eventDigest }; Checkpoint = None }
    { Repository = "FS-GG/FS.GG.Coordination.Authority"; RepositoryId = 1351660651L; Ref = address.Ref
      FirstHead = Some commitOid; SecondHead = Some commitOid; Observation = JournalComplete("fixture-genesis", [ commit ])
      CommitBytes = Map.ofList [ commit.CommitOid, commitBytes ]; TreeBytes = Map.ofList [ commit.TreeOid, tree ] }

let private commitsOf read =
    match read.Observation with
    | JournalComplete(_, commits) -> commits
    | JournalDeleted -> []
    | _ -> failwith "invalid fixture journal"

let private appendRead read proposal =
    let cas, objects = Registry.proposalCas proposal, Registry.proposalObjects proposal
    { read with
        FirstHead = Some objects.CommitObjectId; SecondHead = Some objects.CommitObjectId
        Observation = JournalComplete("test-revision", commitsOf read @ [ cas.ProposedCommit ])
        CommitBytes = Map.add (Registry.gitObjectIdValue objects.CommitObjectId) objects.CommitBytes read.CommitBytes
        TreeBytes = Map.add (Registry.gitObjectIdValue objects.TreeObjectId) objects.TreeBytes read.TreeBytes }

let private replaceLastEvent (read: RegistryJournalRead) (eventBytes: byte array) =
    let commits = commitsOf read
    let prior = commits |> List.take (commits.Length - 1)
    let old = List.last commits
    let eventDigest = ShardedJournalAdapter.sha256 eventBytes
    let provisional = { old.Head with EventDigest = eventDigest; HeadDigest = String.replicate 64 "0" }
    let head = { provisional with HeadDigest = ShardedJournalAdapter.journalHeadBytes provisional |> ShardedJournalAdapter.sha256 }
    let headBytes = ShardedJournalAdapter.journalHeadBytes head
    let eventOid, headOid = gitOid "blob" eventBytes, gitOid "blob" headBytes
    let tree = treeBytes [ "event.json", eventOid; "head.json", headOid ]
    let treeOid = gitOid "tree" tree
    let parentLine = old.ParentOid |> Option.map (sprintf "parent %s\n") |> Option.defaultValue ""
    let commitBytes = Encoding.UTF8.GetBytes($"tree {Registry.gitObjectIdValue treeOid}\n{parentLine}author FS.GG Coordination <coordination@fs.gg> 0 +0000\ncommitter FS.GG Coordination <coordination@fs.gg> 0 +0000\n\nfsgg admission {old.OperationId}\n")
    let commitOid = gitOid "commit" commitBytes
    let replacement =
        { old with CommitOid = Registry.gitObjectIdValue commitOid; TreeOid = Registry.gitObjectIdValue treeOid
                   Head = head; HeadBytes = headBytes; Event = { Bytes = eventBytes; Digest = eventDigest } }
    { read with FirstHead = Some commitOid; SecondHead = Some commitOid
                Observation = JournalComplete("tampered-revision", prior @ [ replacement ])
                CommitBytes = Map.add replacement.CommitOid commitBytes read.CommitBytes
                TreeBytes = Map.add replacement.TreeOid tree read.TreeBytes }

let private replaceLastTree (read: RegistryJournalRead) (tree: byte array) extraParent =
    let commits = commitsOf read
    let prior = commits |> List.take (commits.Length - 1)
    let old = List.last commits
    let treeOid = gitOid "tree" tree
    let extraParentOid = String.replicate 40 "f"
    let parentLines =
        (old.ParentOid |> Option.map (sprintf "parent %s\n") |> Option.defaultValue "")
        + (if extraParent then $"parent {extraParentOid}\n" else "")
    let commitBytes = Encoding.UTF8.GetBytes($"tree {Registry.gitObjectIdValue treeOid}\n{parentLines}author FS.GG Coordination <coordination@fs.gg> 0 +0000\ncommitter FS.GG Coordination <coordination@fs.gg> 0 +0000\n\nfsgg admission {old.OperationId}\n")
    let commitOid = gitOid "commit" commitBytes
    let replacement = { old with CommitOid = Registry.gitObjectIdValue commitOid; TreeOid = Registry.gitObjectIdValue treeOid }
    { read with FirstHead = Some commitOid; SecondHead = Some commitOid
                Observation = JournalComplete("tampered-git-revision", prior @ [ replacement ])
                CommitBytes = Map.add replacement.CommitOid commitBytes read.CommitBytes
                TreeBytes = Map.add replacement.TreeOid tree read.TreeBytes }

type private Harness(initial: RegistryJournalRead) =
    let mutable current = initial
    member _.Current with get () = current and set value = current <- value
    member _.ReadPort: RegistryJournalPort =
        { Read = fun _ -> current
          Write = fun _ -> ReceiveDefiniteRefusal "read-only" }
    member _.Plan candidate =
        Registry.planAppend (Guid.NewGuid().ToString("N")) current candidate
        |> Result.defaultWith (String.concat "," >> failwith)
    member this.Confirm(candidate, outcome) =
        let proposal = this.Plan candidate
        let port: RegistryJournalPort =
            { Read = fun _ -> current
              Write = fun value ->
                  current <- appendRead current value
                  outcome }
        match Registry.appendAndReconcile port proposal with
        | DurableAppendAccepted(registry, permit) -> registry, permit, proposal
        | other -> failwithf "append failed: %A" other

let private context commit manifest operation generation =
    { Round = 1L; Manifest = manifest; OperationId = operation; OperationGeneration = generation
      Actor = "worker-a"; Receiver = "coordination"; Kind = "issue-edit"; CanonicalTarget = "FS-GG/example#17"
      Claim = NoClaimRequired; IntentDigest = digest "d"; TouchSetDigest = digest "e"
      OriginatingEpochCommit = commit; OriginatingEpochGeneration = 1L }

let private bootstrap (harness: Harness) manifest =
    let registry = Registry.restore harness.Current |> Result.defaultWith (String.concat "," >> failwith)
    Assert.Equal(manifest, digest "a")
    registry

let private admitted snapshot commit manifest operation =
    let harness = Harness(genesisRead manifest)
    let registry = bootstrap harness manifest
    let candidate =
        match Registry.admit (Registry.head registry) snapshot (context commit manifest operation 1L) registry with
        | RegistryAdmissionAppended value -> value
        | other -> failwithf "admission failed: %A" other
    let durable, _, _ = harness.Confirm(candidate, ReceiveAccepted)
    let handle = Registry.recoverOperation operation durable |> Result.defaultWith (String.concat "," >> failwith)
    harness, durable, handle

let private mutationRequest commit generation effectId marker =
    let bytes = Encoding.UTF8.GetBytes($"{{\"marker\":\"{marker}\"}}")
    let requestDigest = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant() |> Registry.sha256Digest |> Result.defaultWith failwith
    MutationRequest
        { EffectId = effectId; RequestDigest = requestDigest; CanonicalRequestBytes = bytes
          Preconditions = { ExpectedEpochCommit = commit; ExpectedEpochGeneration = generation
                            ExpectedClaimGeneration = None; ExpectedOperationGeneration = 1L } }

let private prepare (harness: Harness) snapshot handle registry owner request =
    let candidate =
        match Registry.prepareEffect (Registry.head registry) snapshot handle owner request registry with
        | EffectIntentAppended value -> value
        | other -> failwithf "prepare failed: %A" other
    let durable, permit, proposal = harness.Confirm(candidate, ReceiveAccepted)
    durable, permit |> Option.defaultWith (fun () -> failwith "permit missing"), proposal

[<Fact>]
let ``admission service publishes a handle only after exact durable append`` () =
    let _, commit, manifest, _, authorityPort = authority "OperatingV1" 1L None id
    let mutable current = genesisRead manifest
    let mutable writes = 0
    let journal: RegistryJournalPort =
        { Read = fun _ -> current
          Write = fun proposal ->
              writes <- writes + 1
              current <- appendRead current proposal
              ReceiveAccepted }
    let ports: AdmissionServicePorts = { Authority = authorityPort; Journal = journal }
    let requested = context commit manifest "service-op" 1L
    match V1AdmissionService.admit ports requested with
    | AdmissionDurablyAppended(handle, confirmed) ->
        Assert.Equal(current.FirstHead, Some confirmed)
        Assert.True(Registry.recoverOperation "service-op" (Registry.restore current |> Result.defaultWith (String.concat "," >> failwith)) |> Result.isOk)
        Assert.NotNull handle
    | other -> Assert.Fail($"durable admission expected: {other}")
    match V1AdmissionService.admit ports requested with
    | AdmissionAlreadyDurable(_, confirmed) -> Assert.Equal(current.FirstHead, Some confirmed)
    | other -> Assert.Fail($"idempotent recovery expected: {other}")
    Assert.Equal(1, writes)

[<Fact>]
let ``admission service refuses an absent journal and moved authority before writing`` () =
    let _, commit, manifest, observedAuthority, _ = authority "OperatingV1" 1L None id
    let mutable writes = 0
    let absent: RegistryJournalPort =
        { Read = fun _ -> absentRead ()
          Write = fun _ -> writes <- writes + 1; ReceiveAccepted }
    let movedAuthority: AuthorityGitPort =
        { ReadObjects = fun () -> Ok observedAuthority
          RereadHead = fun () -> Ok(oid "f") }
    let installed: RegistryJournalPort = { absent with Read = fun _ -> genesisRead manifest }
    let requested = context commit manifest "service-op" 1L
    Assert.True(match V1AdmissionService.admit { Authority = movedAuthority; Journal = absent } requested with AdmissionServiceIndeterminate _ -> true | _ -> false)
    Assert.True(match V1AdmissionService.admit { Authority = movedAuthority; Journal = installed } requested with AdmissionServiceIndeterminate _ -> true | _ -> false)
    Assert.Equal(0, writes)

[<Fact>]
let ``admission service rereads fleet authority immediately before CAS`` () =
    let _, commit, manifest, first, _ = authority "OperatingV1" 1L None id
    let _, _, _, second, _ = authorityWithTrust (digest "c") "OperatingV1" 1L None id
    let mutable reads = 0
    let mutable observed = first
    let mutable writes = 0
    let moving: AuthorityGitPort =
        { ReadObjects = fun () ->
              reads <- reads + 1
              observed <- if reads = 1 then first else second
              Ok observed
          RereadHead = fun () -> Ok observed.FirstHead }
    let journal: RegistryJournalPort =
        { Read = fun _ -> genesisRead manifest
          Write = fun _ -> writes <- writes + 1; ReceiveAccepted }
    match V1AdmissionService.admit { Authority = moving; Journal = journal } (context commit manifest "service-op" 1L) with
    | AdmissionServiceIndeterminate reasons -> Assert.Contains("admission-authority-moved", reasons)
    | other -> Assert.Fail($"authority movement refusal expected: {other}")
    Assert.Equal(2, reads)
    Assert.Equal(0, writes)

[<Fact>]
let ``admission service distinguishes lost success from unknown unobserved write`` () =
    let _, commit, manifest, _, authorityPort = authority "OperatingV1" 1L None id
    let mutable current = genesisRead manifest
    let mutable persist = false
    let journal: RegistryJournalPort =
        { Read = fun _ -> current
          Write = fun proposal ->
              if persist then current <- appendRead current proposal
              ReceiveResponseUnknown }
    let ports: AdmissionServicePorts = { Authority = authorityPort; Journal = journal }
    let requested = context commit manifest "service-op" 1L
    Assert.True(match V1AdmissionService.admit ports requested with AdmissionServiceIndeterminate _ -> true | _ -> false)
    Assert.Equal(1, commitsOf current |> List.length)
    persist <- true
    Assert.True(match V1AdmissionService.admit ports requested with AdmissionDurablyAppended _ -> true | _ -> false)
    Assert.Equal(2, commitsOf current |> List.length)

[<Fact>]
let ``admission service reports concurrent parent movement without reusing the stale plan`` () =
    let _, commit, manifest, _, authorityPort = authority "OperatingV1" 1L None id
    let mutable current = genesisRead manifest
    let mutable moved = false
    let journal: RegistryJournalPort =
        { Read = fun _ -> current
          Write = fun _ ->
              let registry = Registry.restore current |> Result.defaultWith (String.concat "," >> failwith)
              let close =
                  match Registry.closeAdmissions (Registry.head registry) registry with
                  | RegistryAppended candidate -> candidate
                  | other -> failwithf "%A" other
              let proposal = Registry.planAppend "concurrent-close" current close |> Result.defaultWith (String.concat "," >> failwith)
              current <- appendRead current proposal
              moved <- true
              ReceiveParentConflict }
    let requested = context commit manifest "service-op" 1L
    match V1AdmissionService.admit { Authority = authorityPort; Journal = journal } requested with
    | AdmissionParentConflict(Some latest) -> Assert.Equal(current.FirstHead, Some latest)
    | other -> Assert.Fail($"parent conflict expected: {other}")
    Assert.True moved
    Assert.True(Registry.recoverOperation "service-op" (Registry.restore current |> Result.defaultWith (String.concat "," >> failwith)) |> Result.isError)

let private journalEvidence (now: DateTimeOffset) (read: RegistryJournalRead) =
    let root = JsonObject()
    root["schema"] <- JsonValue.Create("fsgg.v1-admission-journal-git-read/1")
    root["observedAt"] <- JsonValue.Create(now.ToString("yyyy-MM-ddTHH:mm:ss'Z'"))
    root["repository"] <- JsonValue.Create(read.Repository)
    root["repositoryId"] <- JsonValue.Create(read.RepositoryId)
    root["ref"] <- JsonValue.Create(read.Ref)
    root["firstHead"] <- JsonValue.Create(read.FirstHead.Value |> Registry.gitObjectIdValue)
    root["secondHead"] <- JsonValue.Create(read.SecondHead.Value |> Registry.gitObjectIdValue)
    let commits = JsonArray()
    for commit in commitsOf read do
        let entry = JsonObject()
        entry["commitOid"] <- JsonValue.Create(commit.CommitOid)
        entry["commitBytesBase64"] <- JsonValue.Create(Convert.ToBase64String(read.CommitBytes[commit.CommitOid]))
        entry["treeOid"] <- JsonValue.Create(commit.TreeOid)
        entry["treeBytesBase64"] <- JsonValue.Create(Convert.ToBase64String(read.TreeBytes[commit.TreeOid]))
        entry["eventBytesBase64"] <- JsonValue.Create(Convert.ToBase64String(commit.Event.Bytes))
        entry["headBytesBase64"] <- JsonValue.Create(Convert.ToBase64String(commit.HeadBytes))
        commits.Add entry
    root["commits"] <- commits
    root

[<Fact>]
let ``native admission journal evidence restores a parented operation history`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, durable, _ = admitted snapshot commit manifest "native-op"
    let now = DateTimeOffset.UtcNow
    let evidence = journalEvidence now harness.Current
    let bytes = Encoding.UTF8.GetBytes(evidence.ToJsonString())
    let restored =
        V1AdmissionJournalGitRead.decode now (ReadOnlyMemory bytes)
        |> Result.defaultWith (String.concat "," >> failwith)
        |> Registry.restore
        |> Result.defaultWith (String.concat "," >> failwith)
    Assert.Equal(Registry.head durable, Registry.head restored)
    Assert.Equal(2L, Registry.generation restored)

[<Fact>]
let ``native admission journal evidence rejects changed bytes stale heads and incomplete order`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, _, _ = admitted snapshot commit manifest "native-op"
    let now = DateTimeOffset.UtcNow
    let valid = journalEvidence now harness.Current
    let decode (root: JsonObject) =
        root.ToJsonString() |> Encoding.UTF8.GetBytes |> ReadOnlyMemory
        |> V1AdmissionJournalGitRead.decode now
    Assert.True(decode valid |> Result.isOk)
    let changedBytes = JsonNode.Parse(valid.ToJsonString()).AsObject()
    let changedEntries = changedBytes["commits"].AsArray()
    let changedEvent = changedEntries[1].AsObject()
    changedEvent["eventBytesBase64"] <- JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes("{}\n")))
    Assert.True(decode changedBytes |> Result.isError)
    let moved = JsonNode.Parse(valid.ToJsonString()).AsObject()
    moved["secondHead"] <- JsonValue.Create(String.replicate 40 "f")
    Assert.True(decode moved |> Result.isError)
    let stale = JsonNode.Parse(valid.ToJsonString()).AsObject()
    stale["observedAt"] <- JsonValue.Create(now.AddMinutes(-3.).ToString("yyyy-MM-ddTHH:mm:ss'Z'"))
    Assert.True(decode stale |> Result.isError)
    let reversed = JsonNode.Parse(valid.ToJsonString()).AsObject()
    let entries = reversed["commits"].AsArray()
    let first, second = entries[0].DeepClone(), entries[1].DeepClone()
    entries[0] <- second
    entries[1] <- first
    Assert.True(decode reversed |> Result.isError)

[<Fact>]
let ``native admission journal binding is fresh read-only and never turns failure into absence`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, durable, _ = admitted snapshot commit manifest "native-op"
    let now = DateTimeOffset.UtcNow
    let bytes = journalEvidence now harness.Current |> _.ToJsonString() |> Encoding.UTF8.GetBytes
    let mutable reads = 0
    let native () =
        reads <- reads + 1
        if reads = 2 then Error "failed-read" else Ok bytes
    let port = V1AdmissionJournalGitRead.createReadOnlyPort (fun () -> now) native
    let first = port.Read(registryAddress ())
    Assert.Equal(Registry.head durable, Registry.restore first |> Result.map Registry.head |> Result.defaultWith (String.concat "," >> failwith))
    let second = port.Read(registryAddress ())
    Assert.True(match second.Observation with JournalUnreadable _ -> true | _ -> false)
    Assert.True(Registry.restore second |> Result.isError)
    Assert.Equal(2, reads)
    let wrong = ShardedJournalAdapter.address Operation "different-operation" |> Result.defaultWith (string >> failwith)
    Assert.True(match (port.Read wrong).Observation with JournalUnreadable _ -> true | _ -> false)
    Assert.Equal(2, reads)
    let next =
        match Registry.admit (Registry.head durable) snapshot (context commit manifest "second-op" 1L) durable with
        | RegistryAdmissionAppended value -> value
        | other -> failwithf "%A" other
    let proposal = Registry.planAppend "second-admission" harness.Current next |> Result.defaultWith (String.concat "," >> failwith)
    Assert.True(match port.Write proposal with ReceiveDefiniteRefusal "admission-journal-read-only" -> true | _ -> false)

[<Fact>]
let ``typed admission append encodes exact public CAS plan without credentials`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, durable, _ = admitted snapshot commit manifest "native-op"
    let next =
        match Registry.admit (Registry.head durable) snapshot (context commit manifest "second-op" 1L) durable with
        | RegistryAdmissionAppended value -> value
        | other -> failwithf "%A" other
    let proposal = Registry.planAppend "second-admission" harness.Current next |> Result.defaultWith (String.concat "," >> failwith)
    let cas = Registry.proposalCas proposal
    let objects = Registry.proposalObjects proposal
    let bytes = V1AdmissionJournalCasPlan.encode proposal |> Result.defaultWith (String.concat "," >> failwith)
    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
    let root = document.RootElement
    Assert.Equal("fsgg.v1-admission-journal-cas/1", root.GetProperty("schema").GetString())
    Assert.Equal(1351660651L, root.GetProperty("repositoryId").GetInt64())
    Assert.Equal((registryAddress ()).Ref, root.GetProperty("ref").GetString())
    Assert.Equal(cas.ObservedObjectId, root.GetProperty("expectedParent").GetString())
    Assert.Equal(Registry.gitObjectIdValue objects.CommitObjectId, root.GetProperty("proposedCommit").GetString())
    Assert.Equal("second-admission", root.GetProperty("operationId").GetString())
    let entries = root.GetProperty("objects").EnumerateArray() |> Seq.toArray
    Assert.True((entries |> Array.map (fun entry -> entry.GetProperty("kind").GetString())) = [| "blob"; "blob"; "tree"; "commit" |])
    for entry in entries do
        let kind = entry.GetProperty("kind").GetString()
        let raw = entry.GetProperty("bytesBase64").GetString() |> Convert.FromBase64String
        Assert.Equal(entry.GetProperty("oid").GetString(), Registry.gitObjectIdValue (gitOid kind raw))
    Assert.DoesNotContain("token", Encoding.UTF8.GetString bytes, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``conservative native CAS port confirms lost append but never issues dispatch permit`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, registry, handle = admitted snapshot commit manifest "cas-port-op"
    let request = mutationRequest commit 1L "cas-port-effect" "one"
    let candidate =
        match Registry.prepareEffect (Registry.head registry) snapshot handle "worker-a" request registry with
        | EffectIntentAppended value -> value
        | other -> failwithf "%A" other
    let proposal = harness.Plan candidate
    let now = DateTimeOffset.UtcNow
    let mutable current = harness.Current
    let mutable writes = 0
    let readRaw () =
        journalEvidence now current |> _.ToJsonString() |> Encoding.UTF8.GetBytes |> Ok
    let writeRaw (raw: ReadOnlyMemory<byte>) =
        use document = JsonDocument.Parse raw
        Assert.Equal((registryAddress ()).Ref, document.RootElement.GetProperty("ref").GetString())
        Assert.Equal(Registry.proposalCas proposal |> _.ObservedObjectId,
                     document.RootElement.GetProperty("expectedParent").GetString())
        writes <- writes + 1
        current <- appendRead current proposal
        Error "lost-success"
    let port = V1AdmissionJournalCasPort.create (fun () -> now) readRaw writeRaw
    match Registry.appendAndReconcile port proposal with
    | DurableAppendAccepted(durable, None) ->
        Assert.Equal(current.FirstHead, Some(Registry.head durable))
    | other -> Assert.Fail($"exact readback must accept without permit: {other}")
    Assert.Equal(1, writes)

[<Fact>]
let ``unreadable native CAS preflight and reread remain indeterminate`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, registry, _ = admitted snapshot commit manifest "cas-port-op"
    let candidate =
        match Registry.admit (Registry.head registry) snapshot (context commit manifest "next-op" 1L) registry with
        | RegistryAdmissionAppended value -> value
        | other -> failwithf "%A" other
    let proposal = harness.Plan candidate
    let now = DateTimeOffset.UtcNow
    let mutable writes = 0
    let writeRaw (_: ReadOnlyMemory<byte>) =
        writes <- writes + 1
        Ok()
    let unavailable = V1AdmissionJournalCasPort.create (fun () -> now) (fun () -> Error "read-failed") writeRaw
    Assert.True(match Registry.appendAndReconcile unavailable proposal with DurableAppendIndeterminate _ -> true | _ -> false)
    Assert.Equal(0, writes)
    let mutable reads = 0
    let moved () =
        reads <- reads + 1
        if reads = 1 then
            journalEvidence now harness.Current |> _.ToJsonString() |> Encoding.UTF8.GetBytes |> Ok
        else Error "reread-failed"
    let port = V1AdmissionJournalCasPort.create (fun () -> now) moved writeRaw
    Assert.True(match Registry.appendAndReconcile port proposal with DurableAppendIndeterminate _ -> true | _ -> false)
    Assert.Equal(1, writes)
    let competitor =
        match Registry.closeAdmissions (Registry.head registry) registry with
        | RegistryAppended value -> value
        | other -> failwithf "%A" other
    let competingProposal = harness.Plan competitor
    let competingRead = appendRead harness.Current competingProposal
    let unreadable =
        { competingRead with
            FirstHead = None; SecondHead = None
            Observation = JournalUnreadable "failed-conflict-reread" }
    let mutable conflictReads = 0
    let conflictPort: RegistryJournalPort =
        { Read = fun _ ->
              conflictReads <- conflictReads + 1
              if conflictReads = 1 then competingRead else unreadable
          Write = fun _ -> failwith "stale parent must not write" }
    Assert.True(match Registry.appendAndReconcile conflictPort proposal with DurableAppendIndeterminate _ -> true | _ -> false)

[<Fact>]
let ``reader validates actual initializer objects and rejects moved head`` () =
    let snapshot, _, _, observed, _ = authority "OperatingV1" 1L None id
    Assert.NotNull snapshot
    let moved: AuthorityGitPort =
        { ReadObjects = fun () -> Ok observed
          RereadHead = fun () -> Ok(oid "f") }
    match Registry.readVerified moved with
    | Error reasons -> Assert.Contains("authority-head-moved", reasons)
    | Ok _ -> Assert.Fail "moved head must refuse"

[<Fact>]
let ``producer restore refuses deleted journal and accepts pinned initializer genesis`` () =
    Assert.True(Registry.restore (absentRead ()) |> Result.isError)
    let read = genesisRead (digest "a")
    Assert.True(Registry.restore read |> Result.isOk)
    Assert.True(Registry.restore { read with SecondHead = Some(oid "1") } |> Result.isError)
    Assert.True(Registry.restore { read with RepositoryId = 7L } |> Result.isError)

[<Fact>]
let ``protected genesis plan binds verified OperatingV1 authority and restores exact objects`` () =
    let snapshot, authorityCommit, manifest, _, _ = authority "OperatingV1" 1L None id
    let plan =
        Registry.planGenesis "protected-genesis" snapshot (absentRead ())
        |> Result.defaultWith (String.concat "," >> failwith)
    let commit = Registry.genesisCommit plan
    let objects = Registry.genesisObjects plan
    Assert.Equal(registryAddress (), Registry.genesisAddress plan)
    Assert.Equal(authorityCommit, Registry.genesisAuthorityCommit plan)
    Assert.Equal(Registry.gitObjectIdValue objects.CommitObjectId, commit.CommitOid)
    Assert.Equal(Registry.gitObjectIdValue objects.TreeObjectId, commit.TreeOid)
    let read =
        { absentRead () with
            FirstHead = Some objects.CommitObjectId
            SecondHead = Some objects.CommitObjectId
            Observation = JournalComplete("protected-genesis", [ commit ])
            CommitBytes = Map.ofList [ commit.CommitOid, objects.CommitBytes ]
            TreeBytes = Map.ofList [ commit.TreeOid, objects.TreeBytes ] }
    let restored = Registry.restore read |> Result.defaultWith (String.concat "," >> failwith)
    Assert.True(Registry.verifyGenesisReadback plan read |> Result.isOk)
    Assert.Equal(1L, Registry.generation restored)
    Assert.Equal(AdmissionsOpen, Registry.phase restored)
    Assert.Equal(Registry.gitObjectIdValue objects.CommitObjectId, Registry.gitObjectIdValue (Registry.head restored))
    use eventDocument = System.Text.Json.JsonDocument.Parse objects.EventBytes
    Assert.Equal(Registry.sha256Value manifest, eventDocument.RootElement.GetProperty("manifestSha256").GetString())
    objects.EventBytes[0] <- 0uy
    Assert.NotEqual(0uy, (Registry.genesisObjects plan).EventBytes[0])
    let altered = { read with TreeBytes = Map.empty }
    match Registry.verifyGenesisReadback plan altered with
    | Error reasons -> Assert.Contains("registry-genesis-readback-not-exact", reasons)
    | Ok _ -> Assert.Fail "a changed readback must not confirm genesis"

[<Fact>]
let ``protected genesis plan refuses ambiguous absence and existing journal`` () =
    let snapshot, _, manifest, _, _ = authority "OperatingV1" 1L None id
    let absent = absentRead ()
    let cases =
        [ { absent with SecondHead = Some(oid "1") }
          { absent with Observation = JournalIncomplete "unreadable" }
          { absent with Observation = JournalUnauthorized "no permission" }
          genesisRead manifest ]
    for read in cases do
        match Registry.planGenesis "protected-genesis" snapshot read with
        | Error reasons -> Assert.Contains("registry-genesis-journal-not-proven-absent", reasons)
        | Ok _ -> Assert.Fail "genesis must require two known-absent heads"
    Assert.True(Registry.planGenesis "bad\noperation" snapshot absent |> Result.isError)
    Assert.True(Registry.planGenesis "bad\u0000operation" snapshot absent |> Result.isError)

[<Fact>]
let ``protected genesis plan refuses a verified non-OperatingV1 authority`` () =
    let snapshot, _, _, _, _ = authority "Preparing" 2L (Some(oid "c", 1L, digest "d")) id
    match Registry.planGenesis "protected-genesis" snapshot (absentRead ()) with
    | Error reasons -> Assert.Contains("registry-genesis-authority-phase", reasons)
    | Ok _ -> Assert.Fail "an incumbent-only epoch must not authorize admission genesis"

[<Fact>]
let ``protected genesis binds signature native approval and expected absent install`` () =
    use rsa = RSA.Create(2048)
    let spki = SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()) |> Convert.ToHexString |> _.ToLowerInvariant()
    let receiptDigest = Registry.sha256Value (digest "c")
    let manifestDigest = Registry.sha256Value (digest "a")
    let trustJson =
        $"{{\"acceptedGenesisReceiptDigest\":\"{receiptDigest}\",\"authorizer\":{{\"algorithm\":\"RSA-PSS-SHA256\",\"keyId\":\"test-key\",\"publicKeySpkiSha256\":\"{spki}\"}},\"manifestSha256\":\"{manifestDigest}\",\"schema\":\"fsgg.github-ledger-initial-trust/1\"}}"
    let trustBytes =
        ShardedJournalAdapter.canonicalJson trustJson
        |> Result.defaultWith failwith
    Assert.Equal(10uy, Array.last trustBytes)
    Assert.NotEqual(10uy, trustBytes[trustBytes.Length - 2])
    let trustDigest = SHA256.HashData trustBytes |> Convert.ToHexString |> _.ToLowerInvariant() |> Registry.sha256Digest |> Result.defaultWith failwith
    let snapshot, _, _, _, authorityPort = authorityWithTrust trustDigest "OperatingV1" 1L None id
    let plan = Registry.planGenesis "protected-genesis" snapshot (absentRead ()) |> Result.defaultWith (String.concat "," >> failwith)
    let intent: GenesisAuthorizationIntent =
        { SourceCommit = oid "1"; SourceTree = oid "2"; WorkflowRevision = oid "3"
          WorkflowSha256 =
            Registry.sha256Digest "07435f26a2e22b6bd597aa89ce83192b39c8ab19d7aeabd74c9a67e16be4adf3"
            |> Result.defaultWith failwith }
    let now = DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.Zero)
    let unsigned: GenesisSignature =
        { KeyId = "test-key"; PublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem(); ProtectedRunId = 42L
          AuthorizedAt = now.AddMinutes(-5.); ExpiresAt = now.AddMinutes(85.); Signature = Array.empty }
    let signature =
        { unsigned with
            Signature = rsa.SignData(
                V1AdmissionGenesisAuthorization.canonicalSignaturePayload plan intent unsigned,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss
            ) }
    let intentSha256 =
        SHA256.HashData(V1AdmissionGenesisAuthorization.canonicalIntent plan intent)
        |> Convert.ToHexString
        |> _.ToLowerInvariant()
    let wireShapeSignature =
        { unsigned with AuthorizedAt = now; ExpiresAt = now.AddMinutes 90. }
    let expectedWirePayload =
        File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-admission-signing-payload.json")
        ).Replace(String.replicate 64 "a", intentSha256)
        |> Encoding.UTF8.GetBytes
    Assert.Equal(
        expectedWirePayload,
        V1AdmissionGenesisAuthorization.canonicalSignaturePayload plan intent wireShapeSignature
    )
    let envelope =
        JsonSerializer.SerializeToUtf8Bytes
            {| schema = "fsgg.github-substrate.v1-admission-genesis-signature-envelope/1"
               intentSha256 = intentSha256
               keyId = signature.KeyId
               protectedRunId = signature.ProtectedRunId
               authorizedAt = signature.AuthorizedAt.ToUniversalTime().ToString("O")
               expiresAt = signature.ExpiresAt.ToUniversalTime().ToString("O")
               publicKeyPem = signature.PublicKeyPem
               signatureBase64 = Convert.ToBase64String signature.Signature |}
    let decoded =
        V1AdmissionGenesisAuthorization.decodeEnvelope plan intent (ReadOnlyMemory envelope)
        |> Result.defaultWith (String.concat "," >> failwith)
    Assert.True(V1AdmissionGenesisAuthorization.verify now trustBytes plan intent decoded |> Result.isOk)
    let changed action =
        let root = JsonNode.Parse envelope
        action root
        V1AdmissionGenesisAuthorization.decodeEnvelope
            plan intent (ReadOnlyMemory(Encoding.UTF8.GetBytes(root.ToJsonString())))
    Assert.True(changed (fun root -> root["intentSha256"] <- JsonValue.Create(String.replicate 64 "f")) |> Result.isError)
    Assert.True(changed (fun root -> root["publicKeyPem"] <- JsonValue.Create(rsa.ExportPkcs8PrivateKeyPem())) |> Result.isError)
    Assert.True(changed (fun root -> root["signatureBase64"] <- JsonValue.Create("?")) |> Result.isError)
    Assert.True(changed (fun root -> root["unreviewed"] <- JsonValue.Create(1)) |> Result.isError)
    let wrongSignature = Array.copy signature.Signature
    wrongSignature[0] <- wrongSignature[0] ^^^ 1uy
    let parsedWrongSignature =
        changed (fun root -> root["signatureBase64"] <- JsonValue.Create(Convert.ToBase64String wrongSignature))
        |> Result.defaultWith (String.concat "," >> failwith)
    Assert.True(V1AdmissionGenesisAuthorization.verify now trustBytes plan intent parsedWrongSignature |> Result.isError)
    Assert.True(V1AdmissionGenesisAuthorization.decodeEnvelope plan intent (ReadOnlyMemory(Array.zeroCreate 8193)) |> Result.isError)
    let duplicate = Encoding.UTF8.GetString envelope
                    |> fun text -> text.Replace("\"keyId\":\"test-key\"", "\"keyId\":\"test-key\",\"keyId\":\"test-key\"")
                    |> Encoding.UTF8.GetBytes
    Assert.NotEqual(envelope, duplicate)
    Assert.True(V1AdmissionGenesisAuthorization.decodeEnvelope plan intent (ReadOnlyMemory duplicate) |> Result.isError)
    let verified =
        V1AdmissionGenesisAuthorization.verify now trustBytes plan intent signature
        |> Result.defaultWith (String.concat "," >> failwith)
    Assert.Equal(42L, V1AdmissionGenesisAuthorization.protectedRunId verified)
    Assert.Equal(
        SHA256.HashData(V1AdmissionGenesisAuthorization.canonicalIntent plan intent) |> Convert.ToHexString |> _.ToLowerInvariant(),
        V1AdmissionGenesisAuthorization.intentSha256 verified |> Registry.sha256Value
    )
    Assert.True(V1AdmissionGenesisAuthorization.verify now trustBytes plan { intent with SourceCommit = oid "5" } signature |> Result.isError)
    Assert.True(V1AdmissionGenesisAuthorization.verify now trustBytes plan { intent with WorkflowSha256 = digest "6" } signature |> Result.isError)
    Assert.True(V1AdmissionGenesisAuthorization.verify now trustBytes plan intent { signature with ProtectedRunId = 43L } |> Result.isError)
    Assert.True(V1AdmissionGenesisAuthorization.verify now trustBytes plan intent { signature with KeyId = "other" } |> Result.isError)
    let alteredPlan = Registry.planGenesis "other-genesis" snapshot (absentRead ()) |> Result.defaultWith (String.concat "," >> failwith)
    Assert.True(V1AdmissionGenesisAuthorization.verify now trustBytes alteredPlan intent signature |> Result.isError)
    use other = RSA.Create(2048)
    Assert.True(
        V1AdmissionGenesisAuthorization.verify
            now
            trustBytes
            plan
            intent
            { signature with PublicKeyPem = other.ExportSubjectPublicKeyInfoPem() }
        |> Result.isError
    )
    Assert.True(V1AdmissionGenesisAuthorization.verify (now.AddMinutes 86.) trustBytes plan intent signature |> Result.isError)
    Assert.True(V1AdmissionGenesisAuthorization.verify now (Array.append trustBytes [| 10uy |]) plan intent signature |> Result.isError)

    let artifact =
        {| schema = "fsgg.v1-admission-genesis-protected-authorization/2"
           operationId = "protected-genesis"
           repository = "FS-GG/.github"
           runId = 42L
           workflowRevision = Registry.gitObjectIdValue intent.WorkflowRevision
           coordinationRevision = Registry.gitObjectIdValue intent.SourceCommit
           coordinationTree = Registry.gitObjectIdValue intent.SourceTree
           environment = "fleet-v1-admission-owner"
           genesisIntentSha256 = V1AdmissionGenesisAuthorization.intentSha256 verified |> Registry.sha256Value
           approvedAt = unsigned.AuthorizedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'")
           expiresAt = unsigned.ExpiresAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'")
           conclusion = "success" |}
    let native: GenesisProtectedNativeRead =
        { ObservedAt = now
          RunRepositoryId = 1269292704L
          RunId = 42L
          RunEvent = "workflow_dispatch"
          RunPath = ".github/workflows/gs2-v1-admission-protected-authorization.yml"
          RunRef = "refs/heads/main"
          RunHead = intent.WorkflowRevision
          RunConclusion = "success"
          RunActorId = 1645484L
          RunAttempt = 1
          WorkflowReadRevision = intent.WorkflowRevision
          WorkflowBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gs2-v1-admission-protected-authorization.yml"))
          ArtifactReadRunId = 42L
          ArtifactBytes = JsonSerializer.SerializeToUtf8Bytes artifact
          EnvironmentId = 22582241959L
          EnvironmentName = "fleet-v1-admission-owner"
          EnvironmentBranchPolicy = "custom-main"
          EnvironmentWaitMinutes = 5
          EnvironmentReviewerIds = [ 1645484L ]
          EnvironmentPreventsSelfReview = false
          Approvals =
            [ { ReviewerId = 1645484L; State = "approved"; EnvironmentIds = [ 22582241959L ] }
              { ReviewerId = 41898282L; State = "approved"; EnvironmentIds = [ 22582241959L ] } ] }
    let approved =
        V1AdmissionGenesisProtectedApproval.verify now plan intent verified native
        |> Result.defaultWith (String.concat "," >> failwith)
    Assert.Equal(42L, V1AdmissionGenesisProtectedApproval.runId approved)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with RunId = 43L } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with ObservedAt = now.AddMinutes(-3.) } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with RunActorId = 777L } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with RunAttempt = 2 } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with RunRef = "refs/heads/other" } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now alteredPlan intent verified native |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with WorkflowBytes = Encoding.UTF8.GetBytes "changed" } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with WorkflowReadRevision = oid "7" } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with ArtifactReadRunId = 43L } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with EnvironmentPreventsSelfReview = true } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with EnvironmentWaitMinutes = 0 } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with EnvironmentReviewerIds = [ 1645484L; 4456104L ] } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with EnvironmentId = 9L } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with EnvironmentBranchPolicy = "unrestricted" } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with Approvals = [ { ReviewerId = 9L; State = "approved"; EnvironmentIds = [ 22582241959L ] } ] } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with Approvals = [ { ReviewerId = 1645484L; State = "approved"; EnvironmentIds = [ 9L ] } ] } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with Approvals = [ { ReviewerId = 1645484L; State = "approved"; EnvironmentIds = [ 22582241959L ] }; { ReviewerId = 1645484L; State = "approved"; EnvironmentIds = [ 22582241959L ] } ] } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with Approvals = native.Approvals |> List.take 1 } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with Approvals = [ native.Approvals[0]; { native.Approvals[1] with ReviewerId = 9L } ] } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with Approvals = [ native.Approvals[0]; { native.Approvals[1] with EnvironmentIds = [ 9L ] } ] } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with Approvals = [ native.Approvals[0]; { native.Approvals[1] with State = "rejected" } ] } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify now plan intent verified { native with ArtifactBytes = JsonSerializer.SerializeToUtf8Bytes {| artifact with runId = 43L |} } |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.verify (now.AddMinutes 86.) plan intent verified native |> Result.isError)

    let source: GenesisSourceRead =
        { ObservedAt = now; RepositoryId = 1346720714L; Commit = intent.SourceCommit
          Tree = intent.SourceTree; IsOnMain = true }
    let protection: GenesisProtectionRead =
        { ObservedAt = now; RepositoryId = 1351660651L
          WriterRulesetId = 21872113L; WriterRulesetActive = true; WriterRulesetMatchesRef = true
          WriterBypassAppIds = [ 4882140L ]
          IntegrityRulesetId = 21872115L; IntegrityRulesetActive = true
          IntegrityRulesetMatchesRef = true; IntegrityRejectsDeletion = true
          IntegrityRejectsNonFastForward = true; IntegrityBypassAppIds = []
          CredentialAppId = 4882140L; CredentialInstallationId = 160261608L
          CredentialRepositoryIds = [ 1351660651L ]; CredentialContentsWrite = true
          CredentialHasOtherWritePermissions = false }
    let genesisObjects = Registry.genesisObjects plan
    let genesisCommit = Registry.genesisCommit plan
    let installedRead =
        { absentRead () with
            FirstHead = Some genesisObjects.CommitObjectId
            SecondHead = Some genesisObjects.CommitObjectId
            Observation = JournalComplete("protected-genesis", [ genesisCommit ])
            CommitBytes = Map.ofList [ genesisCommit.CommitOid, genesisObjects.CommitBytes ]
            TreeBytes = Map.ofList [ genesisCommit.TreeOid, genesisObjects.TreeBytes ] }
    let mutable journal = absentRead ()
    let mutable refRead = GenesisRefAbsent
    let mutable writes = 0
    let mutable objectStore = Map.empty<string * GitObjectId, byte array>
    let installer: GenesisInstallerPort =
        { Now = fun () -> now
          Authority = authorityPort
          ReadRegistry = fun _ -> Ok journal
          ReadRef = fun _ -> refRead
          ReadTrustAnchor = fun () -> Ok trustBytes
          ReadSource = fun () -> Ok source
          ReadProtection = fun () -> Ok protection
          ReadApproval = fun _ -> Ok native
          PutObject = fun kind id bytes ->
              writes <- writes + 1
              objectStore <- Map.add (kind, id) (Array.copy bytes) objectStore
              Ok id
          ReadObject = fun kind id ->
              match Map.tryFind (kind, id) objectStore with
              | Some bytes -> Ok bytes
              | None -> Error "missing-object"
          CreateRefExpectedAbsent = fun _ id ->
              if refRead <> GenesisRefAbsent || id <> genesisObjects.CommitObjectId then
                  Error "expected-absence-conflict"
              else
                  refRead <- GenesisRefAt id
                  journal <- installedRead
                  Ok() }
    Assert.Equal(GenesisInstalled, V1AdmissionGenesisInstaller.apply installer plan intent signature)
    Assert.Equal(4, writes)
    Assert.Equal(GenesisAlreadyInstalled, V1AdmissionGenesisInstaller.apply installer plan intent signature)
    Assert.Equal(4, writes)
    Assert.Equal(
        GenesisInstallRefused [ "genesis-protection-or-writer-drift" ],
        V1AdmissionGenesisInstaller.apply
            { installer with ReadProtection = fun () -> Ok { protection with WriterBypassAppIds = [ 9L ] } }
            plan intent signature
    )
    Assert.Equal(4, writes)
    journal <- absentRead ()
    refRead <- GenesisRefAbsent
    Assert.Equal(
        GenesisInstalled,
        V1AdmissionGenesisInstaller.apply
            { installer with
                CreateRefExpectedAbsent =
                    fun _ id ->
                        refRead <- GenesisRefAt id
                        journal <- installedRead
                        Error "response-lost" }
            plan intent signature
    )
    journal <- absentRead ()
    refRead <- GenesisRefAbsent
    Assert.Equal(
        GenesisInstallIndeterminate [ "genesis-final-readback-indeterminate" ],
        V1AdmissionGenesisInstaller.apply
            { installer with CreateRefExpectedAbsent = fun _ _ -> Error "response-lost" }
            plan intent signature
    )
    Assert.Equal(GenesisRefAbsent, refRead)

    let mutable protectionReads = 0
    Assert.Equal(
        GenesisInstallRefused [ "genesis-protection-or-writer-drift" ],
        V1AdmissionGenesisInstaller.apply
            { installer with
                ReadProtection = fun () ->
                    protectionReads <- protectionReads + 1
                    if protectionReads = 1 then Ok protection
                    else Ok { protection with CredentialContentsWrite = false } }
            plan intent signature
    )
    Assert.Equal(GenesisRefAbsent, refRead)
    Assert.Equal(
        GenesisInstallIndeterminate [ "genesis-object-readback-unknown" ],
        V1AdmissionGenesisInstaller.apply
            { installer with ReadObject = fun _ _ -> Error "provider-unreadable" }
            plan intent signature
    )
    Assert.Equal(GenesisRefAbsent, refRead)
    Assert.Equal(
        GenesisInstallIndeterminate [ "genesis-ref-read-unknown" ],
        V1AdmissionGenesisInstaller.apply
            { installer with ReadRef = fun _ -> GenesisRefUnknown "provider-unreadable" }
            plan intent signature
    )
    Assert.Equal(GenesisRefAbsent, refRead)
    let mutable refReads = 0
    Assert.Equal(
        GenesisInstallRefused [ "genesis-competing-ref" ],
        V1AdmissionGenesisInstaller.apply
            { installer with
                ReadRef =
                    fun _ ->
                        refReads <- refReads + 1
                        if refReads = 1 then GenesisRefAbsent else GenesisRefAt(oid "f") }
            plan intent signature
    )
    Assert.Equal(GenesisRefAbsent, refRead)
    let initialHead = authorityPort.RereadHead() |> Result.defaultWith failwith
    let mutable authorityReads = 0
    let movingAuthority =
        { authorityPort with
            RereadHead = fun () ->
                authorityReads <- authorityReads + 1
                if authorityReads = 1 then Ok initialHead else Ok(oid "f") }
    Assert.Equal(
        GenesisInstallIndeterminate [ "authority-head-moved" ],
        V1AdmissionGenesisInstaller.apply
            { installer with Authority = movingAuthority }
            plan intent signature
    )
    Assert.Equal(GenesisRefAbsent, refRead)
    Assert.Equal(
        GenesisInstallIndeterminate [ "genesis-object-oid-mismatch" ],
        V1AdmissionGenesisInstaller.apply
            { installer with PutObject = fun _ _ _ -> Ok(oid "f") }
            plan intent signature
    )
    Assert.Equal(GenesisRefAbsent, refRead)

[<Fact>]
let ``native read-only collector evidence has a bounded typed decoder`` () =
    let bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-admission-native-read.json"))
    let read =
        V1AdmissionGenesisProtectedApproval.decodeNativeRead(ReadOnlyMemory bytes)
        |> Result.defaultWith (String.concat "," >> failwith)
    Assert.Equal(42L, read.RunId)
    Assert.Equal(22582241959L, read.EnvironmentId)
    Assert.Equal(5, read.EnvironmentWaitMinutes)
    Assert.True((read.Approvals |> List.map _.ReviewerId) = [ 1645484L; 41898282L ])
    Assert.Equal("refs/heads/main", read.RunRef)
    Assert.Equal("07435f26a2e22b6bd597aa89ce83192b39c8ab19d7aeabd74c9a67e16be4adf3",
                 SHA256.HashData(read.WorkflowBytes) |> Convert.ToHexString |> _.ToLowerInvariant())
    let changed = JsonNode.Parse bytes
    changed["workflowBytesBase64"] <- JsonValue.Create("?")
    Assert.True(V1AdmissionGenesisProtectedApproval.decodeNativeRead(ReadOnlyMemory(Encoding.UTF8.GetBytes(changed.ToJsonString()))) |> Result.isError)
    changed["workflowBytesBase64"] <- JsonValue.Create(Convert.ToBase64String read.WorkflowBytes)
    changed["unreviewedField"] <- JsonValue.Create(1)
    Assert.True(V1AdmissionGenesisProtectedApproval.decodeNativeRead(ReadOnlyMemory(Encoding.UTF8.GetBytes(changed.ToJsonString()))) |> Result.isError)
    Assert.True(V1AdmissionGenesisProtectedApproval.decodeNativeRead(ReadOnlyMemory(Array.zeroCreate 32769)) |> Result.isError)

[<Fact>]
let ``raw Git collector evidence decodes and verifies an OperatingV1 genesis plan`` () =
    let fixtureBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-admission-git-read.json"))
    let fixture =
        V1AdmissionGenesisGitRead.decode(ReadOnlyMemory fixtureBytes)
        |> Result.defaultWith (String.concat "," >> failwith)
    Assert.True(V1AdmissionGenesisGitRead.verifyPlan (DateTimeOffset.Parse "2026-09-23T14:00:00Z") "protected-genesis" fixture |> Result.isError)
    let _, commit, manifest, observed, _ = authority "OperatingV1" 1L None id
    let raw = JsonNode.Parse fixtureBytes
    let cutover = raw["cutover"].AsObject()
    let value id = Registry.gitObjectIdValue id
    let eventOid, eventBytes = observed.EventBlob
    let headOid, headBytes = observed.HeadBlob
    cutover["firstHead"] <- JsonValue.Create(value commit)
    cutover["secondHead"] <- JsonValue.Create(value commit)
    cutover["tagTarget"] <- JsonValue.Create(value commit)
    cutover["tagRef"] <- JsonValue.Create("refs/tags/fsgg/v2/fleet-cutover/operating-v1/genesis-" + (Registry.sha256Value manifest).Substring(0, 16))
    cutover["commit"] <- JsonValue.Create(value commit)
    cutover["parent"] <- null
    cutover["genesisCommit"] <- JsonValue.Create(value commit)
    cutover["ancestry"] <- JsonArray(JsonValue.Create(value commit))
    cutover["commitTree"] <- JsonValue.Create(value observed.CommitTree)
    cutover["commitBytesBase64"] <- JsonValue.Create(Convert.ToBase64String observed.CommitBytes)
    cutover["treeBytesBase64"] <- JsonValue.Create(Convert.ToBase64String observed.TreeBytes)
    let entries = JsonObject()
    for KeyValue(name, id) in observed.TreeEntries do
        entries[name] <- JsonValue.Create(value id)
    cutover["treeEntries"] <- entries
    cutover["eventOid"] <- JsonValue.Create(value eventOid)
    cutover["eventBytesBase64"] <- JsonValue.Create(Convert.ToBase64String eventBytes)
    cutover["headOid"] <- JsonValue.Create(value headOid)
    cutover["headBytesBase64"] <- JsonValue.Create(Convert.ToBase64String headBytes)
    cutover["manifestSha256"] <- JsonValue.Create(Registry.sha256Value manifest)
    cutover["trustAnchorSha256"] <- JsonValue.Create(Registry.sha256Value (digest "b"))
    let now = DateTimeOffset.Parse "2026-09-23T14:00:00Z"
    let encoded () = ReadOnlyMemory(Encoding.UTF8.GetBytes(raw.ToJsonString()))
    let read = V1AdmissionGenesisGitRead.decode(encoded ()) |> Result.defaultWith (String.concat "," >> failwith)
    let plan = V1AdmissionGenesisGitRead.verifyPlan now "protected-genesis" read |> Result.defaultWith (String.concat "," >> failwith)
    Assert.Equal(commit, Registry.genesisAuthorityCommit plan)
    Assert.True(V1AdmissionGenesisGitRead.verifyPlan (now.AddMinutes 3.) "protected-genesis" read |> Result.isError)
    Assert.True(V1AdmissionGenesisGitRead.verifyPlan (now.AddSeconds(-1.)) "protected-genesis" read |> Result.isError)
    Assert.True(V1AdmissionGenesisGitRead.decode(ReadOnlyMemory(Array.zeroCreate 32769)) |> Result.isError)
    cutover["commitBytesBase64"] <- JsonValue.Create("not-base64")
    Assert.True(V1AdmissionGenesisGitRead.decode(encoded ()) |> Result.isError)
    cutover["commitBytesBase64"] <- JsonValue.Create(Convert.ToBase64String observed.CommitBytes)
    raw["operation"]["secondHead"] <- JsonValue.Create(String.replicate 40 "f")
    Assert.True(V1AdmissionGenesisGitRead.decode(encoded ()) |> Result.isError)
    raw["operation"]["secondHead"] <- null
    cutover["tagTarget"] <- JsonValue.Create(String.replicate 40 "f")
    Assert.True(V1AdmissionGenesisGitRead.decode(encoded ()) |> Result.isError)

[<Fact>]
let ``canonical command log restores admission after process restart`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, registry, _ = admitted snapshot commit manifest "op-1"
    let restored = Registry.restore harness.Current |> Result.defaultWith (String.concat "," >> failwith)
    Assert.Equal(Registry.head registry, Registry.head restored)
    Assert.True(Registry.recoverOperation "op-1" restored |> Result.isOk)

[<Fact>]
let ``lost append response confirms exact proposal but mints no send permit`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, registry, handle = admitted snapshot commit manifest "op-1"
    let request = mutationRequest commit 1L "effect-1" "one"
    let candidate =
        match Registry.prepareEffect (Registry.head registry) snapshot handle "worker-a" request registry with
        | EffectIntentAppended value -> value
        | other -> failwithf "%A" other
    let proposal = harness.Plan candidate
    let port: RegistryJournalPort =
        { Read = fun _ -> harness.Current
          Write = fun value ->
              harness.Current <- appendRead harness.Current value
              ReceiveResponseUnknown }
    let durable =
        match Registry.appendAndReconcile port proposal with
        | DurableAppendAccepted(value, None) -> value
        | other -> failwithf "%A" other
    match Registry.appendAndReconcile port proposal with
    | DurableAppendAccepted(replayed, None) -> Assert.Equal(Registry.head durable, Registry.head replayed)
    | other -> Assert.Fail($"replay minted authority: {other}")

[<Fact>]
let ``independent identical proposals permit only explicit accepted writer`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, registry, handle = admitted snapshot commit manifest "op-1"
    let request = mutationRequest commit 1L "effect-1" "one"
    let candidate =
        match Registry.prepareEffect (Registry.head registry) snapshot handle "worker-a" request registry with
        | EffectIntentAppended value -> value
        | other -> failwithf "%A" other
    let commandId = Guid.NewGuid().ToString("N")
    let first = Registry.planAppend commandId harness.Current candidate |> Result.defaultWith (String.concat "," >> failwith)
    let second = Registry.planAppend commandId harness.Current candidate |> Result.defaultWith (String.concat "," >> failwith)
    let mutable ambiguous = DurableAppendIndeterminate([ "not-run" ], None)
    let unknownPort: RegistryJournalPort =
        { Read = fun _ -> harness.Current
          Write = fun value ->
              harness.Current <- appendRead harness.Current value
              ReceiveResponseUnknown }
    let acceptedPort: RegistryJournalPort =
        { Read = fun _ -> harness.Current
          Write = fun _ ->
              ambiguous <- Registry.appendAndReconcile unknownPort second
              ReceiveAccepted }
    let accepted = Registry.appendAndReconcile acceptedPort first
    Assert.True(match accepted with DurableAppendAccepted(_, Some _) -> true | _ -> false)
    Assert.True(match ambiguous with DurableAppendAccepted(_, None) -> true | _ -> false)

[<Fact>]
let ``exact proposal remains reconciled when a later append advances the head`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, registry, handle = admitted snapshot commit manifest "op-1"
    let inFlight, _, intentProposal = prepare harness snapshot handle registry "worker-a" (mutationRequest commit 1L "effect-1" "one")
    let provider: ProviderReconciliationPort = { Read = fun _ _ _ _ _ -> Ok(ProviderPartial "pending") }
    let proof = Registry.reconcileEffect provider handle "effect-1" inFlight |> Result.defaultWith (String.concat "," >> failwith)
    let laterCandidate = match Registry.settleEffect (Registry.head inFlight) "worker-a" "effect-1" proof inFlight with RegistryAppended value -> value | other -> failwithf "%A" other
    let later, _, _ = harness.Confirm(laterCandidate, ReceiveAccepted)
    let replayPort: RegistryJournalPort =
        { Read = fun _ -> harness.Current
          Write = fun _ -> ReceiveParentConflict }
    match Registry.appendAndReconcile replayPort intentProposal with
    | DurableAppendAccepted(reconciled, None) -> Assert.Equal(Registry.head later, Registry.head reconciled)
    | other -> Assert.Fail($"ancestor proposal did not reconcile: {other}")

[<Fact>]
let ``concurrent admission and close proposals contend on one expected parent`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness = Harness(genesisRead manifest)
    let registry = bootstrap harness manifest
    let admitCandidate =
        match Registry.admit (Registry.head registry) snapshot (context commit manifest "op-1" 1L) registry with
        | RegistryAdmissionAppended value -> value
        | other -> failwithf "%A" other
    let closeCandidate = match Registry.closeAdmissions (Registry.head registry) registry with RegistryAppended value -> value | other -> failwithf "%A" other
    let admitProposal, closeProposal = harness.Plan admitCandidate, harness.Plan closeCandidate
    let port: RegistryJournalPort =
        { Read = fun _ -> harness.Current
          Write = fun value ->
              harness.Current <- appendRead harness.Current value
              ReceiveAccepted }
    Assert.True(match Registry.appendAndReconcile port admitProposal with DurableAppendAccepted _ -> true | _ -> false)
    Assert.True(match Registry.appendAndReconcile port closeProposal with DurableAppendParentConflict _ -> true | _ -> false)

[<Fact>]
let ``restored inflight has no permit and fresh journal movement refuses old fence`` () =
    let snapshot, commit, manifest, _, authorityPort = authority "OperatingV1" 1L None id
    let harness, registry, handle = admitted snapshot commit manifest "op-1"
    let inFlight, permit, _ = prepare harness snapshot handle registry "worker-a" (mutationRequest commit 1L "effect-1" "one")
    let fence = Registry.refreshDispatch harness.ReadPort authorityPort permit handle "worker-a" "effect-1" inFlight |> Result.defaultWith (String.concat "," >> failwith)
    let provider: ProviderReconciliationPort = { Read = fun _ _ _ _ _ -> Ok(ProviderPartial "provider-pending") }
    let proof = Registry.reconcileEffect provider handle "effect-1" inFlight |> Result.defaultWith (String.concat "," >> failwith)
    let settlement = match Registry.settleEffect (Registry.head inFlight) "worker-a" "effect-1" proof inFlight with RegistryAppended value -> value | other -> failwithf "%A" other
    let settled, _, _ = harness.Confirm(settlement, ReceiveAccepted)
    match Registry.authorizeDispatch harness.ReadPort authorityPort fence handle "worker-a" "effect-1" inFlight with
    | DispatchRefused reasons -> Assert.Contains("registry-head-moved", reasons)
    | DispatchAuthorized -> Assert.Fail "stale registry authorized"
    Assert.True(Registry.unresolvedEffects settled |> List.contains "effect-1")

[<Fact>]
let ``dispatch token is single use`` () =
    let snapshot, commit, manifest, _, authorityPort = authority "OperatingV1" 1L None id
    let harness, registry, handle = admitted snapshot commit manifest "op-1"
    let inFlight, permit, _ = prepare harness snapshot handle registry "worker-a" (mutationRequest commit 1L "effect-1" "one")
    let fence = Registry.refreshDispatch harness.ReadPort authorityPort permit handle "worker-a" "effect-1" inFlight |> Result.defaultWith (String.concat "," >> failwith)
    Assert.Equal(DispatchAuthorized, Registry.authorizeDispatch harness.ReadPort authorityPort fence handle "worker-a" "effect-1" inFlight)
    match Registry.authorizeDispatch harness.ReadPort authorityPort fence handle "worker-a" "effect-1" inFlight with
    | DispatchRefused reasons -> Assert.Contains("dispatch-fence-consumed", reasons)
    | _ -> Assert.Fail "fence reused"
    Assert.True(Registry.refreshDispatch harness.ReadPort authorityPort permit handle "worker-a" "effect-1" inFlight |> Result.isError)

[<Fact>]
let ``replacement refuses unresolved effect`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, registry, handle = admitted snapshot commit manifest "op-1"
    let inFlight, _, _ = prepare harness snapshot handle registry "worker-a" (mutationRequest commit 1L "effect-1" "one")
    match Registry.admit (Registry.head inFlight) snapshot (context commit manifest "op-1" 2L) inFlight with
    | RegistryAdmissionRefused reasons -> Assert.Contains("operation-has-unresolved-effects", reasons)
    | other -> Assert.Fail($"replacement accepted: {other}")

[<Fact>]
let ``retry requires provider proof that binds and excludes delayed original`` () =
    let snapshot, commit, manifest, _, authorityPort = authority "OperatingV1" 1L None id
    let harness, registry, handle = admitted snapshot commit manifest "op-1"
    let request = mutationRequest commit 1L "effect-1" "one"
    let inFlight, _, _ = prepare harness snapshot handle registry "worker-a" request
    let wrong: ProviderReconciliationPort = { Read = fun _ _ _ _ _ -> Ok(ProviderStronglyAbsent(ProviderIdempotencyExclusion(digest "f", digest "e"))) }
    Assert.True(Registry.reconcileEffect wrong handle "effect-1" inFlight |> Result.isError)
    let requestDigest = match request with MutationRequest value -> value.RequestDigest | _ -> failwith "mutation"
    let provider: ProviderReconciliationPort =
        { Read = fun operation generation effect attempt bytes ->
            Assert.Equal("op-1", operation)
            Assert.Equal(1L, generation)
            Assert.Equal("effect-1", effect)
            Assert.Equal(1L, attempt)
            let expectedBytes = match request with MutationRequest value -> value.CanonicalRequestBytes | _ -> failwith "mutation"
            Assert.Equal(expectedBytes, bytes)
            Ok(ProviderStronglyAbsent(ConditionalFenceExclusion(requestDigest, digest "e"))) }
    let proof = Registry.reconcileEffect provider handle "effect-1" inFlight |> Result.defaultWith (String.concat "," >> failwith)
    let settledCandidate = match Registry.settleEffect (Registry.head inFlight) "worker-a" "effect-1" proof inFlight with RegistryAppended value -> value | other -> failwithf "%A" other
    let settled, _, _ = harness.Confirm(settledCandidate, ReceiveAccepted)
    match Registry.retryAfterProvenAbsence (Registry.head settled) authorityPort handle "worker-b" "effect-1" settled with
    | EffectIntentAppended _ -> ()
    | other -> Assert.Fail($"strongly fenced retry refused: {other}")

[<Fact>]
let ``only confirmed seal yields Preparing reference`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, registry, _ = admitted snapshot commit manifest "op-1"
    let closingCandidate = match Registry.closeAdmissions (Registry.head registry) registry with RegistryAppended value -> value | other -> failwithf "%A" other
    let closing, _, _ = harness.Confirm(closingCandidate, ReceiveAccepted)
    let sealCandidate = match Registry.sealAdmissions (Registry.head closing) snapshot closing with RegistryAppended value -> value | other -> failwithf "%A" other
    Assert.True(Registry.preparingReference sealCandidate |> Result.isError)
    let sealedRegistry, _, _ = harness.Confirm(sealCandidate, ReceiveAccepted)
    let sealCommit, sealGeneration, cohort = Registry.preparingReference sealedRegistry |> Result.defaultWith (String.concat "," >> failwith)
    Assert.Equal(Registry.head sealedRegistry, sealCommit)
    Assert.Equal(Registry.generation sealedRegistry, sealGeneration)
    Assert.Equal(Registry.cohortDigest sealedRegistry, Some cohort)

[<Fact>]
let ``malformed utf8 and unknown event fields are refused`` () =
    Assert.True(Registry.decodeEvent [| 0xffuy |] |> Result.isError)
    let bytes = Encoding.UTF8.GetBytes("{\"commandId\":\"x\",\"kind\":\"close\",\"manifestSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"payload\":{},\"round\":1,\"schema\":\"fsgg.github-substrate.admission-event/1\",\"unknown\":true}\n")
    Assert.True(Registry.decodeEvent bytes |> Result.isError)

[<Fact>]
let ``stable command recovery accepts exact bytes and refuses conflicting reuse`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, registry, _ = admitted snapshot commit manifest "op-1"
    let last = commitsOf harness.Current |> List.last
    Assert.True(Registry.recoverCommand last.OperationId last.Event.Bytes harness.Current |> Result.isOk)
    let conflicting = Array.copy last.Event.Bytes
    conflicting[0] <- byte '['
    match Registry.recoverCommand last.OperationId conflicting harness.Current with
    | Error reasons -> Assert.Contains("registry-command-id-conflict", reasons)
    | Ok _ -> Assert.Fail "conflicting command id recovered"

[<Fact>]
let ``raw Git replay refuses duplicate tree names and extra parent headers`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, _, _ = admitted snapshot commit manifest "op-1"
    let current = commitsOf harness.Current |> List.last
    let originalTree = harness.Current.TreeBytes[current.TreeOid]
    let duplicateTree = Array.append originalTree originalTree
    Assert.True(Registry.restore (replaceLastTree harness.Current duplicateTree false) |> Result.isError)
    Assert.True(Registry.restore (replaceLastTree harness.Current originalTree true) |> Result.isError)

[<Fact>]
let ``settlement decoder refuses unknown and duplicate fields`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, registry, handle = admitted snapshot commit manifest "op-1"
    let inFlight, _, _ = prepare harness snapshot handle registry "worker-a" (mutationRequest commit 1L "effect-1" "one")
    let provider: ProviderReconciliationPort = { Read = fun _ _ _ _ _ -> Ok(ProviderApplied(digest "f")) }
    let proof = Registry.reconcileEffect provider handle "effect-1" inFlight |> Result.defaultWith (String.concat "," >> failwith)
    let candidate = match Registry.settleEffect (Registry.head inFlight) "worker-a" "effect-1" proof inFlight with RegistryAppended value -> value | other -> failwithf "%A" other
    let eventBytes = Registry.proposalObjects (harness.Plan candidate) |> _.EventBytes
    let text = Encoding.UTF8.GetString eventBytes
    let unknown = text.Replace("\"status\":\"applied\"", "\"status\":\"applied\",\"unknown\":true") |> Encoding.UTF8.GetBytes
    let duplicate = text.Replace("\"status\":\"applied\"", "\"status\":\"applied\",\"status\":\"applied\"") |> Encoding.UTF8.GetBytes
    Assert.True(Registry.decodeEvent unknown |> Result.isError)
    Assert.True(Registry.decodeEvent duplicate |> Result.isError)

[<Fact>]
let ``replay refuses self-consistent strong absence bound to another request`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, registry, handle = admitted snapshot commit manifest "op-1"
    let request = mutationRequest commit 1L "effect-1" "one"
    let requestDigest = match request with MutationRequest value -> value.RequestDigest | _ -> failwith "mutation"
    let inFlight, _, _ = prepare harness snapshot handle registry "worker-a" request
    let provider: ProviderReconciliationPort = { Read = fun _ _ _ _ _ -> Ok(ProviderStronglyAbsent(ConditionalFenceExclusion(requestDigest, digest "e"))) }
    let proof = Registry.reconcileEffect provider handle "effect-1" inFlight |> Result.defaultWith (String.concat "," >> failwith)
    let candidate = match Registry.settleEffect (Registry.head inFlight) "worker-a" "effect-1" proof inFlight with RegistryAppended value -> value | other -> failwithf "%A" other
    let settled, _, proposal = harness.Confirm(candidate, ReceiveAccepted)
    Assert.Empty(Registry.unresolvedEffects settled)
    let validEvent = Registry.proposalObjects proposal |> _.EventBytes |> Encoding.UTF8.GetString
    let wrongEvent = validEvent.Replace(Registry.sha256Value requestDigest, Registry.sha256Value(digest "f")) |> Encoding.UTF8.GetBytes
    let selfConsistentWrongHistory = replaceLastEvent harness.Current wrongEvent
    Assert.True(Registry.restore selfConsistentWrongHistory |> Result.isError)

[<Fact>]
let ``typed canonical binding supports delimiter-bearing context without collision`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness = Harness(genesisRead manifest)
    let registry = bootstrap harness manifest
    let unusual = { context commit manifest "op|one" 1L with Actor = "worker|a"; CanonicalTarget = "FS-GG/example#17\npart" }
    let candidate = match Registry.admit (Registry.head registry) snapshot unusual registry with RegistryAdmissionAppended value -> value | other -> failwithf "%A" other
    let durable, _, _ = harness.Confirm(candidate, ReceiveAccepted)
    Assert.True(Registry.recoverOperation "op|one" durable |> Result.isOk)
    Assert.True(Registry.restore harness.Current |> Result.isOk)

[<Fact>]
let ``settlement response loss is idempotent and terminal state survives restart`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness, registry, handle = admitted snapshot commit manifest "op-1"
    let inFlight, _, _ = prepare harness snapshot handle registry "worker-a" (mutationRequest commit 1L "effect-1" "one")
    let provider: ProviderReconciliationPort = { Read = fun _ _ _ _ _ -> Ok(ProviderApplied(digest "f")) }
    let proof = Registry.reconcileEffect provider handle "effect-1" inFlight |> Result.defaultWith (String.concat "," >> failwith)
    let candidate = match Registry.settleEffect (Registry.head inFlight) "worker-a" "effect-1" proof inFlight with RegistryAppended value -> value | other -> failwithf "%A" other
    let proposal = harness.Plan candidate
    let port: RegistryJournalPort =
        { Read = fun _ -> harness.Current
          Write = fun value -> harness.Current <- appendRead harness.Current value; ReceiveResponseUnknown }
    let settled = match Registry.appendAndReconcile port proposal with DurableAppendAccepted(value, None) -> value | other -> failwithf "%A" other
    Assert.True(Registry.recoverCommand (Registry.proposalCas proposal).OperationId (Registry.proposalObjects proposal).EventBytes harness.Current |> Result.isOk)
    match Registry.settleEffect (Registry.head settled) "worker-a" "effect-1" proof settled with
    | RegistryRefused reasons -> Assert.Contains("effect-already-terminal", reasons)
    | other -> Assert.Fail($"terminal settlement repeated: {other}")

[<Fact>]
let ``partial and indeterminate effects survive replay and block replacement and seal`` () =
    for observation in [ ProviderPartial "partial"; ProviderIndeterminate "unknown" ] do
        let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
        let harness, registry, handle = admitted snapshot commit manifest "op-1"
        let inFlight, _, _ = prepare harness snapshot handle registry "worker-a" (mutationRequest commit 1L "effect-1" "one")
        let provider: ProviderReconciliationPort = { Read = fun _ _ _ _ _ -> Ok observation }
        let proof = Registry.reconcileEffect provider handle "effect-1" inFlight |> Result.defaultWith (String.concat "," >> failwith)
        let candidate = match Registry.settleEffect (Registry.head inFlight) "worker-a" "effect-1" proof inFlight with RegistryAppended value -> value | other -> failwithf "%A" other
        let durable, _, _ = harness.Confirm(candidate, ReceiveAccepted)
        let restored = Registry.restore harness.Current |> Result.defaultWith (String.concat "," >> failwith)
        Assert.Equal<string list>([ "effect-1" ], Registry.unresolvedEffects restored)
        match Registry.admit (Registry.head restored) snapshot (context commit manifest "op-1" 2L) restored with
        | RegistryAdmissionRefused reasons -> Assert.Contains("operation-has-unresolved-effects", reasons)
        | other -> Assert.Fail($"replacement accepted: {other}")
        let closingCandidate = match Registry.closeAdmissions (Registry.head durable) durable with RegistryAppended value -> value | other -> failwithf "%A" other
        let closing, _, _ = harness.Confirm(closingCandidate, ReceiveAccepted)
        match Registry.sealAdmissions (Registry.head closing) snapshot closing with
        | RegistryRefused reasons -> Assert.Contains("unsettled-effects", reasons)
        | other -> Assert.Fail($"unresolved seal accepted: {other}")

[<Fact>]
let ``unconfirmed candidates grant no handle seal or dispatch authority`` () =
    let snapshot, commit, manifest, _, _ = authority "OperatingV1" 1L None id
    let harness = Harness(genesisRead manifest)
    let registry = bootstrap harness manifest
    let candidate = match Registry.admit (Registry.head registry) snapshot (context commit manifest "op-1" 1L) registry with RegistryAdmissionAppended value -> value | other -> failwithf "%A" other
    Assert.True(Registry.recoverOperation "op-1" candidate |> Result.isError)
    let durable, _, _ = harness.Confirm(candidate, ReceiveAccepted)
    let closingCandidate = match Registry.closeAdmissions (Registry.head durable) durable with RegistryAppended value -> value | other -> failwithf "%A" other
    Assert.True(Registry.preparingReference closingCandidate |> Result.isError)

[<Fact>]
let ``installed raw Git readback requires exact planned genesis objects and stable native refs`` () =
    let snapshot, authorityCommit, _, _, _ = authority "OperatingV1" 1L None id
    let plan =
        Registry.planGenesis "protected-genesis" snapshot (absentRead ())
        |> Result.defaultWith (String.concat "," >> failwith)
    let objects = Registry.genesisObjects plan
    let address = Registry.genesisAddress plan
    let value = Registry.gitObjectIdValue
    let timestamp = "2026-09-23T14:00:00Z"
    let asOf = DateTimeOffset.Parse timestamp
    let encoded =
        JsonSerializer.SerializeToUtf8Bytes
            {| schema = "fsgg.v1-admission-genesis-installed-read/1"
               observedAt = timestamp
               repository = "FS-GG/FS.GG.Coordination.Authority"
               repositoryId = 1351660651L
               cutoverFirstHead = value authorityCommit
               cutoverSecondHead = value authorityCommit
               operation =
                 {| ``ref`` = address.Ref
                    firstHead = value objects.CommitObjectId
                    secondHead = value objects.CommitObjectId
                    commitOid = value objects.CommitObjectId
                    commitBytesBase64 = Convert.ToBase64String objects.CommitBytes
                    treeOid = value objects.TreeObjectId
                    treeBytesBase64 = Convert.ToBase64String objects.TreeBytes
                    eventOid = value objects.EventObjectId
                    eventBytesBase64 = Convert.ToBase64String objects.EventBytes
                    headOid = value objects.HeadObjectId
                    headBytesBase64 = Convert.ToBase64String objects.HeadBytes |} |}
    let decode bytes = V1AdmissionGenesisGitRead.decodeInstalled asOf plan (ReadOnlyMemory bytes)
    let read = decode encoded |> Result.defaultWith (String.concat "," >> failwith)
    Assert.True(Registry.verifyGenesisReadback plan read |> Result.isOk)
    Assert.Equal(Some objects.CommitObjectId, read.FirstHead)

    let changed action =
        let root = JsonNode.Parse encoded
        action root
        decode (Encoding.UTF8.GetBytes(root.ToJsonString()))

    Assert.True(changed (fun root -> root["cutoverSecondHead"] <- JsonValue.Create(String.replicate 40 "f")) |> Result.isError)
    Assert.True(changed (fun root -> root["operation"]["secondHead"] <- JsonValue.Create(String.replicate 40 "f")) |> Result.isError)
    Assert.True(changed (fun root -> root["operation"]["eventBytesBase64"] <- JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes "changed"))) |> Result.isError)
    Assert.True(changed (fun root -> root["operation"]["commitBytesBase64"] <- JsonValue.Create("not-base64")) |> Result.isError)
    Assert.True(changed (fun root -> root["observedAt"] <- JsonValue.Create("2026-09-23T13:57:00Z")) |> Result.isError)
    Assert.True(changed (fun root -> root["unreviewedField"] <- JsonValue.Create(1)) |> Result.isError)
    Assert.True(V1AdmissionGenesisGitRead.decodeInstalled asOf plan (ReadOnlyMemory(Array.zeroCreate 32769)) |> Result.isError)

[<Fact>]
let ``installer port binds fresh absent and installed evidence without caching a success`` () =
    let asOf = DateTimeOffset.Parse "2026-09-23T14:00:00Z"
    let _, authorityHead, manifest, observed, _ = authority "OperatingV1" 1L None id
    let raw =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-admission-git-read.json"))
        |> JsonNode.Parse
    let cutover = raw["cutover"].AsObject()
    let value id = Registry.gitObjectIdValue id
    let eventOid, eventBytes = observed.EventBlob
    let headOid, headBytes = observed.HeadBlob
    for name in [ "firstHead"; "secondHead"; "tagTarget"; "commit"; "genesisCommit" ] do
        cutover[name] <- JsonValue.Create(value authorityHead)
    cutover["tagRef"] <- JsonValue.Create("refs/tags/fsgg/v2/fleet-cutover/operating-v1/genesis-" + (Registry.sha256Value manifest).Substring(0, 16))
    cutover["parent"] <- null
    cutover["ancestry"] <- JsonArray(JsonValue.Create(value authorityHead))
    cutover["commitTree"] <- JsonValue.Create(value observed.CommitTree)
    cutover["commitBytesBase64"] <- JsonValue.Create(Convert.ToBase64String observed.CommitBytes)
    cutover["treeBytesBase64"] <- JsonValue.Create(Convert.ToBase64String observed.TreeBytes)
    let entries = JsonObject()
    for KeyValue(name, id) in observed.TreeEntries do
        entries[name] <- JsonValue.Create(value id)
    cutover["treeEntries"] <- entries
    cutover["eventOid"] <- JsonValue.Create(value eventOid)
    cutover["eventBytesBase64"] <- JsonValue.Create(Convert.ToBase64String eventBytes)
    cutover["headOid"] <- JsonValue.Create(value headOid)
    cutover["headBytesBase64"] <- JsonValue.Create(Convert.ToBase64String headBytes)
    cutover["manifestSha256"] <- JsonValue.Create(Registry.sha256Value manifest)
    cutover["trustAnchorSha256"] <- JsonValue.Create(Registry.sha256Value (digest "b"))
    let initial = Encoding.UTF8.GetBytes(raw.ToJsonString())
    let evidence =
        V1AdmissionGenesisGitRead.decode (ReadOnlyMemory initial)
        |> Result.defaultWith (String.concat "," >> failwith)
    let plan =
        V1AdmissionGenesisGitRead.verifyPlan asOf "protected-genesis" evidence
        |> Result.defaultWith (String.concat "," >> failwith)
    let objects = Registry.genesisObjects plan
    let address = Registry.genesisAddress plan
    let authorityCommit = Registry.genesisAuthorityCommit plan
    let installed =
        JsonSerializer.SerializeToUtf8Bytes
            {| schema = "fsgg.v1-admission-genesis-installed-read/1"
               observedAt = "2026-09-23T14:00:00Z"
               repository = "FS-GG/FS.GG.Coordination.Authority"
               repositoryId = 1351660651L
               cutoverFirstHead = value authorityCommit
               cutoverSecondHead = value authorityCommit
               operation =
                 {| ``ref`` = address.Ref
                    firstHead = value objects.CommitObjectId
                    secondHead = value objects.CommitObjectId
                    commitOid = value objects.CommitObjectId
                    commitBytesBase64 = Convert.ToBase64String objects.CommitBytes
                    treeOid = value objects.TreeObjectId
                    treeBytesBase64 = Convert.ToBase64String objects.TreeBytes
                    eventOid = value objects.EventObjectId
                    eventBytesBase64 = Convert.ToBase64String objects.EventBytes
                    headOid = value objects.HeadObjectId
                    headBytesBase64 = Convert.ToBase64String objects.HeadBytes |} |}
    let current = ref asOf
    let refState = ref GenesisRefAbsent
    let installedRead = ref installed
    let absentCalls = ref 0
    let installedCalls = ref 0
    let native: GenesisInstallerNativeReaders =
        { Now = fun () -> current.Value
          ReadAbsentGit = fun () -> absentCalls.Value <- absentCalls.Value + 1; Ok initial
          ReadInstalledGit = fun expected ->
              Assert.Equal(objects.CommitObjectId, expected)
              installedCalls.Value <- installedCalls.Value + 1
              Ok installedRead.Value
          ReadCutoverHead = fun () -> Ok authorityCommit
          ReadRef = fun name -> Assert.Equal(address.Ref, name); refState.Value
          ReadTrustAnchor = fun () -> Error "not-used"
          ReadSource = fun () -> Error "not-used"
          ReadProtection = fun () -> Error "not-used"
          ReadApproval = fun _ -> Error "not-used" }
    let writer: GenesisInstallerObjectPort =
        { PutObject = fun _ _ _ -> Error "not-used"
          ReadObject = fun _ _ -> Error "not-used"
          CreateRefExpectedAbsent = fun _ _ -> Error "not-used" }
    let port =
        V1AdmissionGenesisPortBinding.create (ReadOnlyMemory initial) plan native writer
        |> Result.defaultWith (String.concat "," >> failwith)
    Assert.True(port.Authority.ReadObjects() |> Result.isOk)
    Assert.True(port.ReadRegistry address |> Result.isOk)
    Assert.True(port.ReadRegistry address |> Result.isOk)
    Assert.Equal(2, absentCalls.Value)
    refState.Value <- GenesisRefAt objects.CommitObjectId
    let read = port.ReadRegistry address |> Result.defaultWith failwith
    Assert.True(Registry.verifyGenesisReadback plan read |> Result.isOk)
    Assert.Equal(1, installedCalls.Value)
    installedRead.Value <-
        let altered = JsonNode.Parse installed
        altered["operation"]["eventBytesBase64"] <- JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes "wrong"))
        Encoding.UTF8.GetBytes(altered.ToJsonString())
    Assert.True(port.ReadRegistry address |> Result.isError)
    Assert.Equal(2, installedCalls.Value)
    refState.Value <- GenesisRefAt(oid "f")
    Assert.True(port.ReadRegistry address |> Result.isError)
    current.Value <- asOf.AddMinutes 3.
    Assert.True(port.Authority.ReadObjects() |> Result.isError)
    Assert.True(port.Authority.RereadHead() |> Result.isError)
    refState.Value <- GenesisRefAbsent
    Assert.True(port.ReadRegistry address |> Result.isError)
    Assert.True(V1AdmissionGenesisPortBinding.create (ReadOnlyMemory initial) plan native writer |> Result.isError)

    current.Value <- asOf
    let sourceCommit = String.replicate 40 "a"
    let sourceTree = String.replicate 40 "b"
    let sourceBytes =
        JsonSerializer.SerializeToUtf8Bytes
            {| schema = "fsgg.v1-admission-genesis-source-read/1"
               observedAt = "2026-09-23T14:00:00Z"
               repository = "FS-GG/FS.GG.Coordination"
               repositoryId = 1346720714L
               sourceCommit = sourceCommit
               sourceTree = sourceTree
               firstMainHead = sourceCommit
               secondMainHead = sourceCommit
               compareStatus = "identical"
               compareBase = sourceCommit
               compareHead = sourceCommit
               mergeBase = sourceCommit |}
    let protectionBytes =
        JsonSerializer.SerializeToUtf8Bytes
            {| schema = "fsgg.v1-admission-genesis-protection-read/1"
               observedAt = "2026-09-23T14:00:00Z"
               repositoryId = 1351660651L
               writerRulesetId = 21872113L
               writerRulesetActive = true
               writerRulesetMatchesRef = true
               writerBypassAppIds = [ 4882140L ]
               integrityRulesetId = 21872115L
               integrityRulesetActive = true
               integrityRulesetMatchesRef = true
               integrityRejectsDeletion = true
               integrityRejectsNonFastForward = true
               integrityBypassAppIds = List.empty<int64>
               credentialAppId = 4882140L
               credentialInstallationId = 160261608L
               credentialRepositoryIds = [ 1351660651L ]
               credentialContentsWrite = true
               credentialHasOtherWritePermissions = false |}
    let approvalBytes =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-admission-native-read.json"))
    let sourceRead = ref sourceBytes
    let protectionRead = ref protectionBytes
    let approvalRead = ref approvalBytes
    let sourceCalls = ref 0
    let protectionCalls = ref 0
    let approvalCalls = ref 0
    let rawNative: GenesisInstallerRawReaders =
        { Now = native.Now
          ReadAbsentGit = native.ReadAbsentGit
          ReadInstalledGit = native.ReadInstalledGit
          ReadCutoverHead = native.ReadCutoverHead
          ReadRef = native.ReadRef
          ReadTrustAnchor = native.ReadTrustAnchor
          ReadSource = fun () -> sourceCalls.Value <- sourceCalls.Value + 1; Ok sourceRead.Value
          ReadProtection = fun () -> protectionCalls.Value <- protectionCalls.Value + 1; Ok protectionRead.Value
          ReadApproval = fun _ -> approvalCalls.Value <- approvalCalls.Value + 1; Ok approvalRead.Value }
    let rawPort =
        V1AdmissionGenesisPortBinding.createRaw (ReadOnlyMemory initial) plan rawNative writer
        |> Result.defaultWith (String.concat "," >> failwith)
    Assert.True(rawPort.ReadSource() |> Result.isOk)
    Assert.True(rawPort.ReadProtection() |> Result.isOk)
    Assert.True(rawPort.ReadApproval 42L |> Result.isOk)
    Assert.True(rawPort.ReadApproval 43L |> Result.isError)
    sourceRead.Value <- Array.zeroCreate 8193
    protectionRead.Value <- Array.zeroCreate 8193
    approvalRead.Value <- Array.zeroCreate 32769
    Assert.True(rawPort.ReadSource() |> Result.isError)
    Assert.True(rawPort.ReadProtection() |> Result.isError)
    Assert.True(rawPort.ReadApproval 42L |> Result.isError)
    Assert.Equal(2, sourceCalls.Value)
    Assert.Equal(2, protectionCalls.Value)
    Assert.Equal(3, approvalCalls.Value)
    sourceRead.Value <- sourceBytes
    protectionRead.Value <- protectionBytes
    approvalRead.Value <- approvalBytes
    current.Value <- asOf.AddMinutes 3.
    Assert.True(rawPort.ReadSource() |> Result.isError)
    Assert.True(rawPort.ReadProtection() |> Result.isError)
    Assert.True(rawPort.ReadApproval 42L |> Result.isError)

[<Fact>]
let ``native source read requires stable main ancestry and exact source tree`` () =
    let timestamp = "2026-09-23T14:00:00Z"
    let asOf = DateTimeOffset.Parse timestamp
    let sourceCommit = String.replicate 40 "a"
    let sourceTree = String.replicate 40 "b"
    let mainHead = String.replicate 40 "c"
    let raw =
        JsonSerializer.SerializeToUtf8Bytes
            {| schema = "fsgg.v1-admission-genesis-source-read/1"
               observedAt = timestamp
               repository = "FS-GG/FS.GG.Coordination"
               repositoryId = 1346720714L
               sourceCommit = sourceCommit
               sourceTree = sourceTree
               firstMainHead = mainHead
               secondMainHead = mainHead
               compareStatus = "ahead"
               compareBase = sourceCommit
               compareHead = mainHead
               mergeBase = sourceCommit |}
    let decode bytes = V1AdmissionGenesisSourceRead.decode asOf (ReadOnlyMemory bytes)
    let read = decode raw |> Result.defaultWith (String.concat "," >> failwith)
    Assert.True(read.IsOnMain)
    Assert.Equal(oid "b", read.Tree)
    let changed action =
        let root = JsonNode.Parse raw
        action root
        decode (Encoding.UTF8.GetBytes(root.ToJsonString()))
    Assert.True(changed (fun root -> root["compareStatus"] <- JsonValue.Create("diverged")) |> Result.isOk)
    Assert.False(
        changed (fun root -> root["compareStatus"] <- JsonValue.Create("diverged"))
        |> Result.defaultWith (String.concat "," >> failwith)
        |> _.IsOnMain
    )
    Assert.True(changed (fun root -> root["secondMainHead"] <- JsonValue.Create(String.replicate 40 "d")) |> Result.isError)
    Assert.True(changed (fun root -> root["mergeBase"] <- JsonValue.Create(String.replicate 40 "d")) |> Result.isError)
    Assert.True(changed (fun root -> root["compareHead"] <- JsonValue.Create(String.replicate 40 "d")) |> Result.isError)
    Assert.True(changed (fun root -> root["observedAt"] <- JsonValue.Create("2026-09-23T13:57:00Z")) |> Result.isError)
    Assert.True(changed (fun root -> root["unreviewed"] <- JsonValue.Create(1)) |> Result.isError)

[<Fact>]
let ``native protection read requires visible exact rules and scoped ordinary App`` () =
    let timestamp = "2026-09-23T14:00:00Z"
    let asOf = DateTimeOffset.Parse timestamp
    let raw =
        JsonSerializer.SerializeToUtf8Bytes
            {| schema = "fsgg.v1-admission-genesis-protection-read/1"
               observedAt = timestamp
               repositoryId = 1351660651L
               writerRulesetId = 21872113L
               writerRulesetActive = true
               writerRulesetMatchesRef = true
               writerBypassAppIds = [ 4882140L ]
               integrityRulesetId = 21872115L
               integrityRulesetActive = true
               integrityRulesetMatchesRef = true
               integrityRejectsDeletion = true
               integrityRejectsNonFastForward = true
               integrityBypassAppIds = List.empty<int64>
               credentialAppId = 4882140L
               credentialInstallationId = 160261608L
               credentialRepositoryIds = [ 1351660651L ]
               credentialContentsWrite = true
               credentialHasOtherWritePermissions = false |}
    let decode bytes = V1AdmissionGenesisProtectionRead.decode asOf (ReadOnlyMemory bytes)
    let read = decode raw |> Result.defaultWith (String.concat "," >> failwith)
    Assert.True(read.WriterBypassAppIds = [ 4882140L ])
    Assert.True(read.CredentialRepositoryIds = [ 1351660651L ])
    let changed action =
        let root = JsonNode.Parse raw
        action root
        decode (Encoding.UTF8.GetBytes(root.ToJsonString()))
    Assert.True(changed (fun root -> root["writerBypassAppIds"] <- JsonArray()) |> Result.isError)
    Assert.True(changed (fun root -> root["integrityBypassAppIds"] <- JsonArray(JsonValue.Create(1))) |> Result.isError)
    Assert.True(changed (fun root -> root["credentialHasOtherWritePermissions"] <- JsonValue.Create(true)) |> Result.isError)
    Assert.True(changed (fun root -> root["credentialRepositoryIds"] <- JsonArray(JsonValue.Create(1))) |> Result.isError)
    Assert.True(changed (fun root -> root["observedAt"] <- JsonValue.Create("2026-09-23T13:57:00Z")) |> Result.isError)
    Assert.True(changed (fun root -> root["unreviewed"] <- JsonValue.Create(1)) |> Result.isError)
