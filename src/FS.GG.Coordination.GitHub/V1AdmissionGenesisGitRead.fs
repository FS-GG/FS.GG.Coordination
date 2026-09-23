namespace FS.GG.Coordination.GitHub

open System
open System.Globalization
open System.Text.Json

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
