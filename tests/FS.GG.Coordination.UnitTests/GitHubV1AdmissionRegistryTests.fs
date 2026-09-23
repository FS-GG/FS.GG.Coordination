module FS.GG.Coordination.GitHubV1AdmissionRegistryTests

open System
open System.Security.Cryptography
open System.Text
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
let ``genesis signature binds anchored signer, exact intent, run and expiry`` () =
    use rsa = RSA.Create(2048)
    let spki = SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()) |> Convert.ToHexString |> _.ToLowerInvariant()
    let receiptDigest = Registry.sha256Value (digest "c")
    let manifestDigest = Registry.sha256Value (digest "a")
    let trustJson =
        $"{{\"acceptedGenesisReceiptDigest\":\"{receiptDigest}\",\"authorizer\":{{\"algorithm\":\"RSA-PSS-SHA256\",\"keyId\":\"test-key\",\"publicKeySpkiSha256\":\"{spki}\"}},\"manifestSha256\":\"{manifestDigest}\",\"schema\":\"fsgg.github-ledger-initial-trust/1\"}}"
    let trustBytes =
        ShardedJournalAdapter.canonicalJson trustJson
        |> Result.defaultWith failwith
        |> fun bytes -> Array.append bytes [| 10uy |]
    let trustDigest = SHA256.HashData trustBytes |> Convert.ToHexString |> _.ToLowerInvariant() |> Registry.sha256Digest |> Result.defaultWith failwith
    let snapshot, _, _, _, _ = authorityWithTrust trustDigest "OperatingV1" 1L None id
    let plan = Registry.planGenesis "protected-genesis" snapshot (absentRead ()) |> Result.defaultWith (String.concat "," >> failwith)
    let intent: GenesisAuthorizationIntent =
        { SourceCommit = oid "1"; SourceTree = oid "2"; WorkflowRevision = oid "3"; WorkflowSha256 = digest "4" }
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
    let provider: ProviderReconciliationPort = { Read = fun _ _ _ _ -> Ok(ProviderPartial "pending") }
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
    let provider: ProviderReconciliationPort = { Read = fun _ _ _ _ -> Ok(ProviderPartial "provider-pending") }
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
    let wrong: ProviderReconciliationPort = { Read = fun _ _ _ _ -> Ok(ProviderStronglyAbsent(ProviderIdempotencyExclusion(digest "f", digest "e"))) }
    Assert.True(Registry.reconcileEffect wrong handle "effect-1" inFlight |> Result.isError)
    let requestDigest = match request with MutationRequest value -> value.RequestDigest | _ -> failwith "mutation"
    let provider: ProviderReconciliationPort = { Read = fun _ _ _ _ -> Ok(ProviderStronglyAbsent(ConditionalFenceExclusion(requestDigest, digest "e"))) }
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
    let provider: ProviderReconciliationPort = { Read = fun _ _ _ _ -> Ok(ProviderApplied(digest "f")) }
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
    let provider: ProviderReconciliationPort = { Read = fun _ _ _ _ -> Ok(ProviderStronglyAbsent(ConditionalFenceExclusion(requestDigest, digest "e"))) }
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
    let provider: ProviderReconciliationPort = { Read = fun _ _ _ _ -> Ok(ProviderApplied(digest "f")) }
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
        let provider: ProviderReconciliationPort = { Read = fun _ _ _ _ -> Ok observation }
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
