namespace FS.GG.Coordination.GitHub

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json

[<RequireQualifiedAccess>]
module V1AdmissionJournalGitRead =
    let private utf8 = UTF8Encoding(false, true)
    let private address =
        ShardedJournalAdapter.address Operation "fleet-v1-admission:fs-gg-production"
        |> Result.defaultWith (string >> invalidOp)

    let private exact (names: string list) (value: JsonElement) =
        value.ValueKind = JsonValueKind.Object
        && (let actual = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            actual.Length = names.Length && Set.ofList actual = Set.ofList names)

    let private oid value =
        V1AdmissionRegistry.gitObjectId value |> Result.defaultWith invalidOp

    let private gitOid kind (bytes: byte array) =
        let header = Encoding.ASCII.GetBytes($"{kind} {bytes.Length}\u0000")
        SHA1.HashData(Array.append header bytes)
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private decoded (value: JsonElement) =
        let encoded = value.GetString()
        let bytes = Convert.FromBase64String encoded
        if bytes.Length = 0 || bytes.Length > 8192
           || Convert.ToBase64String bytes <> encoded then
            invalidOp "admission-journal-object-size-or-encoding"
        bytes

    let private optionalText (value: JsonElement) =
        if value.ValueKind = JsonValueKind.Null then None
        else Some(value.GetString())

    let private commitParts (bytes: byte array) =
        let value = utf8.GetString bytes
        let boundary = value.IndexOf("\n\n", StringComparison.Ordinal)
        if boundary < 0 then invalidOp "admission-journal-commit-boundary"
        let headers = value.Substring(0, boundary).Split('\n') |> Array.toList
        let named prefix =
            headers
            |> List.choose (fun line ->
                if line.StartsWith(prefix, StringComparison.Ordinal) then
                    Some(line.Substring(prefix.Length))
                else None)
        let tree = match named "tree " with [ value ] -> oid value | _ -> invalidOp "admission-journal-commit-tree"
        let parent = match named "parent " with [] -> None | [ value ] -> Some(oid value) | _ -> invalidOp "admission-journal-commit-parent"
        let person = "FS.GG Coordination <coordination@fs.gg> 0 +0000"
        let order = headers |> List.map (fun line -> line.Split(' ')[0])
        let expectedOrder =
            if parent.IsSome then [ "tree"; "parent"; "author"; "committer" ]
            else [ "tree"; "author"; "committer" ]
        if order <> expectedOrder
           || named "author " <> [ person ]
           || named "committer " <> [ person ] then
            invalidOp "admission-journal-commit-author"
        let message = value.Substring(boundary + 2)
        let prefix = "fsgg admission "
        if not (message.StartsWith(prefix, StringComparison.Ordinal))
           || not (message.EndsWith("\n", StringComparison.Ordinal))
           || message.AsSpan(0, message.Length - 1).IndexOf('\n') >= 0 then
            invalidOp "admission-journal-commit-message"
        let operation = message.Substring(prefix.Length, message.Length - prefix.Length - 1)
        if String.IsNullOrWhiteSpace operation then invalidOp "admission-journal-commit-operation"
        tree, parent, operation

    let private headParts (eventBytes: byte array) (bytes: byte array) =
        use document = JsonDocument.Parse bytes
        let root = document.RootElement
        let fields =
            [ "aggregateDigest"; "aggregateId"; "eventDigest"; "generation"; "journalKind"
              "priorHeadDigest"; "schemaVersion"; "shard"; "snapshotDigest"; "terminal" ]
        if not (exact fields root)
           || root.GetProperty("aggregateDigest").GetString() <> address.Digest
           || root.GetProperty("aggregateId").GetString() <> address.CanonicalId
           || root.GetProperty("journalKind").GetString() <> "operation"
           || root.GetProperty("shard").GetString() <> address.Shard
           || root.GetProperty("eventDigest").GetString() <> sha256 eventBytes then
            invalidOp "admission-journal-head-binding"
        let optional (name: string) =
            root.GetProperty(name)
            |> optionalText
            |> Option.map (fun value ->
                V1AdmissionRegistry.sha256Digest value |> Result.defaultWith invalidOp
                |> V1AdmissionRegistry.sha256Value)
        let head =
            { SchemaVersion = root.GetProperty("schemaVersion").GetInt32()
              Address = address
              Generation = root.GetProperty("generation").GetInt64()
              EventDigest = sha256 eventBytes
              SnapshotDigest = optional "snapshotDigest"
              Terminal = root.GetProperty("terminal").GetBoolean()
              PriorHeadDigest = optional "priorHeadDigest"
              HeadDigest = sha256 bytes }
        if ShardedJournalAdapter.journalHeadBytes head <> bytes then
            invalidOp "admission-journal-head-not-canonical"
        head

    let decode (asOf: DateTimeOffset) (raw: ReadOnlyMemory<byte>) =
        try
            if raw.Length = 0 || raw.Length > 48_000_000 then
                Error [ "admission-journal-evidence-size" ]
            else
                use document = JsonDocument.Parse raw
                let root = document.RootElement
                let fields =
                    [ "schema"; "observedAt"; "repository"; "repositoryId"; "ref"
                      "firstHead"; "secondHead"; "commits" ]
                if not (exact fields root)
                   || root.GetProperty("schema").GetString() <> "fsgg.v1-admission-journal-git-read/1"
                   || root.GetProperty("repository").GetString() <> "FS-GG/FS.GG.Coordination.Authority"
                   || root.GetProperty("repositoryId").GetInt64() <> 1351660651L
                   || root.GetProperty("ref").GetString() <> address.Ref then
                    Error [ "admission-journal-evidence-identity" ]
                else
                    let observedAt =
                        DateTimeOffset.ParseExact(
                            root.GetProperty("observedAt").GetString(), "yyyy-MM-ddTHH:mm:ss'Z'",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal)
                    let first = root.GetProperty("firstHead").GetString() |> oid
                    let second = root.GetProperty("secondHead").GetString() |> oid
                    let entries = root.GetProperty("commits").EnumerateArray() |> Seq.toArray
                    if observedAt > asOf.AddSeconds(30.) || observedAt < asOf.AddMinutes(-2.)
                       || first <> second || entries.Length = 0 || entries.Length > 4096 then
                        Error [ "admission-journal-evidence-moved-or-stale" ]
                    else
                        let mutable total = 0
                        let commits, rawCommits, rawTrees =
                            entries
                            |> Array.map (fun entry ->
                                let itemFields =
                                    [ "commitOid"; "commitBytesBase64"; "treeOid"; "treeBytesBase64"
                                      "eventBytesBase64"; "headBytesBase64" ]
                                if not (exact itemFields entry) then
                                    invalidOp "admission-journal-evidence-commit-shape"
                                let commitOid = entry.GetProperty("commitOid").GetString() |> oid
                                let treeOid = entry.GetProperty("treeOid").GetString() |> oid
                                let commitBytes = decoded (entry.GetProperty("commitBytesBase64"))
                                let treeBytes = decoded (entry.GetProperty("treeBytesBase64"))
                                let eventBytes = decoded (entry.GetProperty("eventBytesBase64"))
                                let headBytes = decoded (entry.GetProperty("headBytesBase64"))
                                total <- total + commitBytes.Length + treeBytes.Length + eventBytes.Length + headBytes.Length
                                if total > 32_000_000
                                   || gitOid "commit" commitBytes <> V1AdmissionRegistry.gitObjectIdValue commitOid
                                   || gitOid "tree" treeBytes <> V1AdmissionRegistry.gitObjectIdValue treeOid then
                                    invalidOp "admission-journal-evidence-object-id"
                                let parsedTree, parent, operation = commitParts commitBytes
                                if parsedTree <> treeOid then invalidOp "admission-journal-evidence-tree-binding"
                                let head = headParts eventBytes headBytes
                                let commit =
                                    { CommitOid = V1AdmissionRegistry.gitObjectIdValue commitOid
                                      ParentOid = parent |> Option.map V1AdmissionRegistry.gitObjectIdValue
                                      TreeOid = V1AdmissionRegistry.gitObjectIdValue treeOid
                                      OperationId = operation
                                      Head = head
                                      HeadBytes = headBytes
                                      Event = { Bytes = eventBytes; Digest = sha256 eventBytes }
                                      Checkpoint = None }
                                commit, (commit.CommitOid, commitBytes), (commit.TreeOid, treeBytes))
                            |> Array.unzip3
                        if commits[commits.Length - 1].CommitOid <> V1AdmissionRegistry.gitObjectIdValue first then
                            Error [ "admission-journal-evidence-head-binding" ]
                        else
                            let read =
                                { Repository = "FS-GG/FS.GG.Coordination.Authority"
                                  RepositoryId = 1351660651L
                                  Ref = address.Ref
                                  FirstHead = Some first
                                  SecondHead = Some second
                                  Observation = JournalComplete(V1AdmissionRegistry.gitObjectIdValue first, List.ofArray commits)
                                  CommitBytes = Map.ofArray rawCommits
                                  TreeBytes = Map.ofArray rawTrees }
                            V1AdmissionRegistry.restore read |> Result.map (fun _ -> read)
        with _ ->
            Error [ "admission-journal-evidence-invalid" ]

    let createReadOnlyPort (now: unit -> DateTimeOffset) (readRaw: unit -> Result<byte array, string>) =
        let unreadable reason =
            { Repository = "FS-GG/FS.GG.Coordination.Authority"
              RepositoryId = 1351660651L
              Ref = address.Ref
              FirstHead = None
              SecondHead = None
              Observation = JournalUnreadable reason
              CommitBytes = Map.empty
              TreeBytes = Map.empty }
        { Read = fun requested ->
              if requested <> address then
                  unreadable "admission-journal-wrong-aggregate"
              else
                  match readRaw() with
                  | Error _ -> unreadable "admission-journal-native-unavailable"
                  | Ok raw ->
                      match decode (now()) (ReadOnlyMemory raw) with
                      | Ok read -> read
                      | Error _ -> unreadable "admission-journal-native-invalid"
          Write = fun _ -> ReceiveDefiniteRefusal "admission-journal-read-only" }
