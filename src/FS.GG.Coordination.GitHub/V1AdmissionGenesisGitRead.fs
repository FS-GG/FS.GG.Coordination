namespace FS.GG.Coordination.GitHub

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

type private GenesisGitReadData =
    {
        ObservedAt: DateTimeOffset
        Authority: AuthorityGitObjects
        SecondHead: GitObjectId
        Registry: RegistryJournalRead
    }

type GenesisGitRead = private GenesisGitRead of GenesisGitReadData

[<RequireQualifiedAccess>]
module V1AdmissionGenesisGitRead =
    let private exactProperties expected (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then
            false
        else
            let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            names.Length = (names |> List.distinct |> List.length)
            && Set.ofList names = Set.ofList expected

    let private oid value =
        V1AdmissionRegistry.gitObjectId value
        |> Result.defaultWith invalidOp

    let private digest value =
        V1AdmissionRegistry.sha256Digest value
        |> Result.defaultWith invalidOp

    let private bytes ceiling (value: JsonElement) =
        let encoded = value.GetString()
        let decoded = Convert.FromBase64String encoded
        if decoded.Length > ceiling || Convert.ToBase64String decoded <> encoded then
            invalidOp "genesis-git-evidence-base64"
        decoded

    let private time value =
        DateTimeOffset.ParseExact(
            value,
            "yyyy-MM-ddTHH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
        )

    let decode (raw: ReadOnlyMemory<byte>) =
        try
            if raw.Length > 32768 then
                Error [ "genesis-git-evidence-size" ]
            else
                use document = JsonDocument.Parse raw
                let root = document.RootElement
                let cutover = root.GetProperty("cutover")
                let operation = root.GetProperty("operation")
                let rootFields = [ "schema"; "observedAt"; "repository"; "repositoryId"; "cutover"; "operation" ]
                let cutoverFields =
                    [ "ref"; "firstHead"; "secondHead"; "tagRef"; "tagTarget"; "commit"
                      "parent"; "genesisCommit"; "ancestry"; "commitTree"; "commitBytesBase64"
                      "treeBytesBase64"; "treeEntries"; "eventOid"; "eventBytesBase64"
                      "headOid"; "headBytesBase64"; "manifestSha256"; "trustAnchorSha256"
                      "claimRefs" ]
                let operationFields = [ "ref"; "firstHead"; "secondHead"; "observation" ]
                let text (entry: JsonElement) (name: string) = entry.GetProperty(name).GetString()
                let address =
                    ShardedJournalAdapter.address Operation "fleet-v1-admission:fs-gg-production"
                    |> Result.defaultWith (string >> invalidOp)

                if not (exactProperties rootFields root)
                   || not (exactProperties cutoverFields cutover)
                   || not (exactProperties operationFields operation)
                   || text root "schema" <> "fsgg.v1-admission-genesis-git-read/1"
                   || text root "repository" <> "FS-GG/FS.GG.Coordination.Authority"
                   || root.GetProperty("repositoryId").GetInt64() <> 1351660651L
                   || text cutover "ref" <> "refs/heads/fsgg/v2/journal/cutover/d5"
                   || text operation "ref" <> address.Ref
                   || text operation "observation" <> "deleted"
                   || operation.GetProperty("firstHead").ValueKind <> JsonValueKind.Null
                   || operation.GetProperty("secondHead").ValueKind <> JsonValueKind.Null
                   || cutover.GetProperty("claimRefs").GetArrayLength() <> 0 then
                    Error [ "genesis-git-evidence-shape" ]
                else
                    let manifest = text cutover "manifestSha256" |> digest
                    let trust = text cutover "trustAnchorSha256" |> digest
                    let firstHead = text cutover "firstHead" |> oid
                    let secondHead = text cutover "secondHead" |> oid
                    let commit = text cutover "commit" |> oid
                    let tagTarget = text cutover "tagTarget" |> oid
                    let tagRef = text cutover "tagRef"
                    let expectedTag =
                        "refs/tags/fsgg/v2/fleet-cutover/operating-v1/genesis-"
                        + (V1AdmissionRegistry.sha256Value manifest).Substring(0, 16)
                    let parent =
                        match cutover.GetProperty("parent") with
                        | entry when entry.ValueKind = JsonValueKind.Null -> None
                        | entry -> Some(entry.GetString() |> oid)
                    let ancestry =
                        cutover.GetProperty("ancestry").EnumerateArray()
                        |> Seq.map (_.GetString() >> oid)
                        |> Seq.toList
                    let treeEntries = cutover.GetProperty("treeEntries")
                    if not (exactProperties [ "event.json"; "head.json" ] treeEntries)
                       || tagRef <> expectedTag
                       || firstHead <> secondHead
                       || firstHead <> commit
                       || tagTarget <> commit then
                        Error [ "genesis-git-evidence-binding" ]
                    else
                        let authority =
                            { Repository = "FS-GG/FS.GG.Coordination.Authority"
                              RepositoryId = 1351660651L
                              Ref = text cutover "ref"
                              FirstHead = firstHead
                              TagTarget = tagTarget
                              Commit = commit
                              Parent = parent
                              GenesisCommit = text cutover "genesisCommit" |> oid
                              Ancestry = ancestry
                              CommitTree = text cutover "commitTree" |> oid
                              CommitBytes = cutover.GetProperty("commitBytesBase64") |> bytes 8192
                              TreeBytes = cutover.GetProperty("treeBytesBase64") |> bytes 8192
                              TreeEntries =
                                [ "event.json"; "head.json" ]
                                |> List.map (fun name -> name, (text treeEntries name |> oid))
                                |> Map.ofList
                              EventBlob =
                                text cutover "eventOid" |> oid,
                                cutover.GetProperty("eventBytesBase64") |> bytes 8192
                              HeadBlob =
                                text cutover "headOid" |> oid,
                                cutover.GetProperty("headBytesBase64") |> bytes 8192
                              TrustAnchorSha256 = trust
                              ManifestSha256 = manifest
                              ClaimJournals = Map.empty }
                        let registry =
                            { Repository = "FS-GG/FS.GG.Coordination.Authority"
                              RepositoryId = 1351660651L
                              Ref = address.Ref
                              FirstHead = None
                              SecondHead = None
                              Observation = JournalDeleted
                              CommitBytes = Map.empty
                              TreeBytes = Map.empty }
                        Ok(GenesisGitRead
                            { ObservedAt = text root "observedAt" |> time
                              Authority = authority
                              SecondHead = secondHead
                              Registry = registry })
        with _ ->
            Error [ "genesis-git-evidence-invalid" ]

    let observedAt (GenesisGitRead evidence) = evidence.ObservedAt

    let private claimObject kind (value: JsonElement) =
        let raw = bytes 8192 value
        if raw.Length = 0 then invalidOp "operating-claim-empty-object"
        let prefix = Encoding.ASCII.GetBytes($"{kind} {raw.Length}\u0000")
        let objectId =
            SHA1.HashData(Array.append prefix raw)
            |> Convert.ToHexString
            |> _.ToLowerInvariant()
        raw, objectId

    let private claimTree (raw: byte array) =
        let mutable offset = 0
        let mutable entries = Map.empty
        let names = ResizeArray<string>()
        while offset < raw.Length do
            let nul = Array.IndexOf(raw, 0uy, offset)
            if nul < offset || nul + 21 > raw.Length then invalidOp "operating-claim-tree-entry"
            let entry = Encoding.UTF8.GetString(raw, offset, nul - offset)
            let name = entry.Substring("100644 ".Length)
            if not (entry.StartsWith("100644 ", StringComparison.Ordinal))
               || not (Set.contains name (Set.ofList [ "event.json"; "head.json" ]))
               || Map.containsKey name entries then invalidOp "operating-claim-tree-entry"
            let objectId = raw[(nul + 1) .. (nul + 20)] |> Convert.ToHexString |> _.ToLowerInvariant()
            entries <- Map.add name objectId entries
            names.Add name
            offset <- nul + 21
        if List.ofSeq names <> [ "event.json"; "head.json" ] then
            invalidOp "operating-claim-tree-shape"
        entries

    let private claimCommit (raw: byte array) =
        let value = UTF8Encoding(false, true).GetString raw
        let boundary = value.IndexOf("\n\n", StringComparison.Ordinal)
        if boundary < 0 then invalidOp "operating-claim-commit-boundary"
        let headers = value.Substring(0, boundary).Split('\n') |> Array.toList
        let fields = headers |> List.map (fun line -> line.Split(' ')[0])
        let values prefix =
            headers
            |> List.choose (fun line ->
                if line.StartsWith(prefix, StringComparison.Ordinal) then
                    Some(line.Substring(prefix.Length))
                else None)
        let parent =
            match values "parent " with
            | [] -> None
            | [ value ] -> Some(oid value)
            | _ -> invalidOp "operating-claim-commit-parent"
        let expected =
            if parent.IsSome then [ "tree"; "parent"; "author"; "committer" ]
            else [ "tree"; "author"; "committer" ]
        let message = value.Substring(boundary + 2)
        if fields <> expected || values "author " |> List.length <> 1
           || values "committer " |> List.length <> 1
           || not (message.EndsWith("\n", StringComparison.Ordinal))
           || message.Length < 2
           || message.AsSpan(0, message.Length - 1).IndexOf('\n') >= 0 then
            invalidOp "operating-claim-commit-shape"
        let tree = match values "tree " with [ value ] -> oid value | _ -> invalidOp "operating-claim-commit-tree"
        tree, parent, message.Substring(0, message.Length - 1)

    let private claimObservation (value: JsonElement) =
        if not (exactProperties [ "ref"; "firstHead"; "secondHead"; "commits" ] value) then
            invalidOp "operating-claim-shape"
        let ref = value.GetProperty("ref").GetString()
        let first = value.GetProperty("firstHead").GetString() |> oid
        let second = value.GetProperty("secondHead").GetString() |> oid
        let commits = value.GetProperty("commits").EnumerateArray() |> Seq.toArray
        if first <> second || commits.Length = 0 || commits.Length > 4096 then
            invalidOp "operating-claim-head-or-bound"
        let mutable claimAddress: AggregateAddress option = None
        let mutable total = 0
        let decoded =
            commits
            |> Array.map (fun item ->
                if not (exactProperties
                            [ "commitOid"; "commitBytesBase64"; "treeOid"; "treeBytesBase64"
                              "eventBytesBase64"; "headBytesBase64" ] item) then
                    invalidOp "operating-claim-commit-shape"
                let commitBytes, commitHash = claimObject "commit" (item.GetProperty("commitBytesBase64"))
                let treeBytes, treeHash = claimObject "tree" (item.GetProperty("treeBytesBase64"))
                let eventBytes, eventHash = claimObject "blob" (item.GetProperty("eventBytesBase64"))
                let headBytes, headHash = claimObject "blob" (item.GetProperty("headBytesBase64"))
                total <- total + commitBytes.Length + treeBytes.Length + eventBytes.Length + headBytes.Length
                if total > 32_000_000
                   || commitHash <> item.GetProperty("commitOid").GetString()
                   || treeHash <> item.GetProperty("treeOid").GetString() then
                    invalidOp "operating-claim-object-hash"
                let tree, parent, operationId = claimCommit commitBytes
                let entries = claimTree treeBytes
                if V1AdmissionRegistry.gitObjectIdValue tree <> treeHash
                   || entries["event.json"] <> eventHash
                   || entries["head.json"] <> headHash then
                    invalidOp "operating-claim-object-binding"
                use headDocument = JsonDocument.Parse headBytes
                let head = headDocument.RootElement
                let fields =
                    [ "aggregateDigest"; "aggregateId"; "eventDigest"; "generation"; "journalKind"
                      "priorHeadDigest"; "schemaVersion"; "shard"; "snapshotDigest"; "terminal" ]
                if not (exactProperties fields head)
                   || head.GetProperty("journalKind").GetString() <> "claim"
                   || head.GetProperty("eventDigest").GetString() <>
                      (SHA256.HashData eventBytes |> Convert.ToHexString |> _.ToLowerInvariant()) then
                    invalidOp "operating-claim-head-binding"
                let address =
                    ShardedJournalAdapter.address Claim (head.GetProperty("aggregateId").GetString())
                    |> Result.defaultWith (fun _ -> invalidOp "operating-claim-address")
                if address.Ref <> ref
                   || head.GetProperty("aggregateDigest").GetString() <> address.Digest
                   || head.GetProperty("shard").GetString() <> address.Shard
                   || (claimAddress |> Option.exists ((<>) address)) then
                    invalidOp "operating-claim-address-binding"
                claimAddress <- Some address
                let optionalDigest (name: string) =
                    let field = head.GetProperty name
                    if field.ValueKind = JsonValueKind.Null then None else Some(field.GetString())
                let journalHead =
                    { SchemaVersion = head.GetProperty("schemaVersion").GetInt32()
                      Address = address
                      Generation = head.GetProperty("generation").GetInt64()
                      EventDigest = head.GetProperty("eventDigest").GetString()
                      SnapshotDigest = optionalDigest "snapshotDigest"
                      Terminal = head.GetProperty("terminal").GetBoolean()
                      PriorHeadDigest = optionalDigest "priorHeadDigest"
                      HeadDigest = SHA256.HashData headBytes |> Convert.ToHexString |> _.ToLowerInvariant() }
                if ShardedJournalAdapter.journalHeadBytes journalHead <> headBytes then
                    invalidOp "operating-claim-head-canonical"
                { CommitOid = commitHash
                  ParentOid = parent |> Option.map V1AdmissionRegistry.gitObjectIdValue
                  TreeOid = treeHash
                  OperationId = operationId
                  Head = journalHead
                  HeadBytes = headBytes
                  Event = { Bytes = eventBytes; Digest = journalHead.EventDigest }
                  Checkpoint = None })
            |> List.ofArray
        if (decoded |> List.last).CommitOid <> V1AdmissionRegistry.gitObjectIdValue first then
            invalidOp "operating-claim-history-head"
        let address = claimAddress.Value
        let observation = JournalComplete(V1AdmissionRegistry.gitObjectIdValue first, decoded)
        ShardedJournalAdapter.validate address observation
        |> Result.defaultWith (fun _ -> invalidOp "operating-claim-journal-invalid")
        |> ignore
        address.CanonicalId, ref, observation

    let decodeOperating (asOf: DateTimeOffset) (raw: ReadOnlyMemory<byte>) =
        try
            if raw.Length = 0 || raw.Length > 48_000_000 then
                Error [ "operating-git-evidence-size" ]
            else
                use document = JsonDocument.Parse raw
                let root = document.RootElement
                let operation = root.GetProperty("operation")
                let fields = [ "schema"; "observedAt"; "repository"; "repositoryId"; "cutover"; "operation"; "claims" ]
                let cutoverFields =
                    [ "ref"; "firstHead"; "secondHead"; "tagRef"; "tagTarget"; "commit"
                      "parent"; "genesisCommit"; "ancestry"; "commitTree"; "commitBytesBase64"
                      "treeBytesBase64"; "treeEntries"; "eventOid"; "eventBytesBase64"
                      "headOid"; "headBytesBase64"; "manifestSha256"; "trustAnchorSha256"
                      "claimRefs" ]
                let operationFields = [ "ref"; "firstHead"; "secondHead"; "observation" ]
                let observed = time (root.GetProperty("observedAt").GetString())
                let installedHead = operation.GetProperty("firstHead").GetString() |> oid
                if not (exactProperties fields root)
                   || not (exactProperties cutoverFields (root.GetProperty("cutover")))
                   || not (exactProperties operationFields operation)
                   || root.GetProperty("schema").GetString() <> "fsgg.v1-admission-operating-git-read/1"
                   || operation.GetProperty("ref").GetString() <> "refs/heads/fsgg/v2/journal/operation/79"
                   || operation.GetProperty("observation").GetString() <> "present"
                   || installedHead <> (operation.GetProperty("secondHead").GetString() |> oid)
                   || observed > asOf.AddSeconds(30.)
                   || observed < asOf.AddMinutes(-2.) then
                    Error [ "operating-git-evidence-binding-or-freshness" ]
                else
                    let claimRefs =
                        root.GetProperty("cutover").GetProperty("claimRefs").EnumerateArray()
                        |> Seq.map _.GetString()
                        |> Seq.toList
                    let claims =
                        root.GetProperty("claims").EnumerateArray()
                        |> Seq.map claimObservation
                        |> Seq.toList
                    if claimRefs.Length > 64
                       || claimRefs <> (claimRefs |> List.distinct |> List.sort)
                       || claimRefs <> (claims |> List.map (fun (_, ref, _) -> ref))
                       || (claims |> List.map (fun (id, _, _) -> id) |> List.distinct |> List.length) <> claims.Length then
                        invalidOp "operating-claim-census-binding"
                    // Reuse the strict cutover object decoder. Its genesis-only operation
                    // absence check is satisfied only in this private copy, after the
                    // installed ref and stable census above have been checked.
                    let copy = JsonNode.Parse(Encoding.UTF8.GetString raw.Span)
                    copy["schema"] <- JsonValue.Create("fsgg.v1-admission-genesis-git-read/1")
                    copy.AsObject().Remove("claims") |> ignore
                    copy["cutover"]["claimRefs"] <- JsonArray()
                    let operationCopy = copy["operation"]
                    operationCopy["firstHead"] <- null
                    operationCopy["secondHead"] <- null
                    operationCopy["observation"] <- JsonValue.Create("deleted")
                    decode (ReadOnlyMemory(Encoding.UTF8.GetBytes(copy.ToJsonString())))
                    |> Result.map (fun read ->
                        let (GenesisGitRead evidence) = read
                        let authority =
                            { evidence.Authority with
                                ClaimJournals = claims |> List.map (fun (id, _, observation) -> id, observation) |> Map.ofList }
                        authority, evidence.SecondHead, installedHead)
        with _ ->
            Error [ "operating-git-evidence-invalid" ]

    let createOperatingPort (now: unit -> DateTimeOffset) (readRaw: unit -> Result<byte array, string>) =
        let read () =
            try
                readRaw()
                |> Result.mapError (fun _ -> "operating-git-native-unavailable")
                |> Result.bind (fun raw ->
                    decodeOperating (now()) (ReadOnlyMemory raw)
                    |> Result.mapError (String.concat ","))
            with _ ->
                Error "operating-git-native-exception"
        { ReadObjects =
            fun () -> read () |> Result.map (fun (authority, _, _) -> authority)
          RereadHead =
            fun () -> read () |> Result.map (fun (_, cutoverHead, _) -> cutoverHead) }

    let authorityPort (GenesisGitRead evidence) =
        { ReadObjects =
            fun () ->
                let value = evidence.Authority
                let eventOid, eventBytes = value.EventBlob
                let headOid, headBytes = value.HeadBlob
                Ok
                    { value with
                        CommitBytes = Array.copy value.CommitBytes
                        TreeBytes = Array.copy value.TreeBytes
                        EventBlob = eventOid, Array.copy eventBytes
                        HeadBlob = headOid, Array.copy headBytes }
          RereadHead = fun () -> Ok evidence.SecondHead }

    let registryRead (GenesisGitRead evidence) = evidence.Registry

    let verifyPlan asOf operationId (GenesisGitRead evidence as read) =
        if evidence.ObservedAt > asOf || asOf - evidence.ObservedAt > TimeSpan.FromMinutes 2. then
            Error [ "genesis-git-evidence-stale" ]
        else
            V1AdmissionRegistry.readVerified (authorityPort read)
            |> Result.bind (fun authority ->
                V1AdmissionRegistry.planGenesis operationId authority evidence.Registry)

    let decodeInstalled asOf plan (raw: ReadOnlyMemory<byte>) =
        try
            if raw.Length > 32768 then
                Error [ "genesis-installed-evidence-size" ]
            else
                use document = JsonDocument.Parse raw
                let root = document.RootElement
                let operation = root.GetProperty("operation")
                let rootFields =
                    [ "schema"; "observedAt"; "repository"; "repositoryId"
                      "cutoverFirstHead"; "cutoverSecondHead"; "operation" ]
                let operationFields =
                    [ "ref"; "firstHead"; "secondHead"; "commitOid"; "commitBytesBase64"
                      "treeOid"; "treeBytesBase64"; "eventOid"; "eventBytesBase64"
                      "headOid"; "headBytesBase64" ]
                let text (entry: JsonElement) (name: string) = entry.GetProperty(name).GetString()
                let observedAt = text root "observedAt" |> time
                let address = V1AdmissionRegistry.genesisAddress plan
                let objects = V1AdmissionRegistry.genesisObjects plan
                let commit = V1AdmissionRegistry.genesisCommit plan
                let authorityCommit =
                    V1AdmissionRegistry.genesisAuthorityCommit plan
                    |> V1AdmissionRegistry.gitObjectIdValue
                let objectId = V1AdmissionRegistry.gitObjectIdValue

                if not (exactProperties rootFields root)
                   || not (exactProperties operationFields operation)
                   || text root "schema" <> "fsgg.v1-admission-genesis-installed-read/1"
                   || text root "repository" <> "FS-GG/FS.GG.Coordination.Authority"
                   || root.GetProperty("repositoryId").GetInt64() <> 1351660651L
                   || observedAt > asOf
                   || asOf - observedAt > TimeSpan.FromMinutes 2.
                   || text root "cutoverFirstHead" <> authorityCommit
                   || text root "cutoverSecondHead" <> authorityCommit
                   || text operation "ref" <> address.Ref
                   || text operation "firstHead" <> commit.CommitOid
                   || text operation "secondHead" <> commit.CommitOid
                   || text operation "commitOid" <> commit.CommitOid
                   || text operation "treeOid" <> objectId objects.TreeObjectId
                   || text operation "eventOid" <> objectId objects.EventObjectId
                   || text operation "headOid" <> objectId objects.HeadObjectId then
                    Error [ "genesis-installed-evidence-binding" ]
                else
                    let commitBytes = operation.GetProperty("commitBytesBase64") |> bytes 8192
                    let treeBytes = operation.GetProperty("treeBytesBase64") |> bytes 8192
                    let eventBytes = operation.GetProperty("eventBytesBase64") |> bytes 8192
                    let headBytes = operation.GetProperty("headBytesBase64") |> bytes 8192

                    if commitBytes <> objects.CommitBytes
                       || treeBytes <> objects.TreeBytes
                       || eventBytes <> objects.EventBytes
                       || headBytes <> objects.HeadBytes then
                        Error [ "genesis-installed-object-drift" ]
                    else
                        let read: RegistryJournalRead =
                            { Repository = "FS-GG/FS.GG.Coordination.Authority"
                              RepositoryId = 1351660651L
                              Ref = address.Ref
                              FirstHead = Some objects.CommitObjectId
                              SecondHead = Some objects.CommitObjectId
                              Observation = JournalComplete(commit.CommitOid, [ commit ])
                              CommitBytes = Map.ofList [ commit.CommitOid, commitBytes ]
                              TreeBytes = Map.ofList [ commit.TreeOid, treeBytes ] }

                        V1AdmissionRegistry.verifyGenesisReadback plan read
                        |> Result.map (fun _ -> read)
        with _ ->
            Error [ "genesis-installed-evidence-invalid" ]
