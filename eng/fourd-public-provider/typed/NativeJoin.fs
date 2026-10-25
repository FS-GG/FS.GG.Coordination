namespace FS.GG.FourD.Typed

open System
open System.IO
open System.Text
open System.Text.Json
open System.Security.Cryptography
open System.Formats.Tar

/// Closed acquired-byte joins for the selected native route. Expectations originate in
/// existing authenticated Root custody; this module creates no new attestation authority.
module NativeJoin =
    let fail reason = invalidArg "native" reason
    let fields (e: JsonElement) =
        if e.ValueKind <> JsonValueKind.Object then fail "native-object-refused"
        e.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
    let closed names e = if fields e <> Set.ofList names then fail "native-shape-refused"
    let str (e: JsonElement) name =
        let v = e.GetProperty(name: string)
        if v.ValueKind <> JsonValueKind.String then fail "native-string-refused"
        let s = v.GetString()
        if String.IsNullOrWhiteSpace s || s.Length > 4096 then fail "native-string-refused"
        s
    let number (e: JsonElement) name =
        let v = e.GetProperty(name: string)
        if v.ValueKind <> JsonValueKind.Number then fail "native-integer-refused"
        match v.TryGetInt64() with true, n -> n | _ -> fail "native-integer-refused"
    let boolean (e: JsonElement) name =
        let v = e.GetProperty(name: string)
        match v.ValueKind with JsonValueKind.True -> true | JsonValueKind.False -> false | _ -> fail "native-boolean-refused"
    let hex length (s: string) = s.Length = length && s |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f')
    let digest (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
    let identity parts = digest (Encoding.UTF8.GetBytes(String.concat "\000" parts))
    let token s = Policy.validToken s
    let require test reason = if not test then fail reason
    let private noLinks (path:string) =
        require (Path.IsPathFullyQualified path && Path.GetFullPath path = path) "native-path-refused"
        let mutable p = path
        while p <> "/" do
            require (not ((File.GetAttributes p).HasFlag FileAttributes.ReparsePoint)) "native-link-refused"
            p <- Path.GetDirectoryName p
    let bytes maximum path =
        noLinks path
        let info = FileInfo path
        require (info.Exists && info.Length > 0L && info.Length <= maximum) "native-file-bound-refused"
        let result = File.ReadAllBytes path
        require (int64 result.Length <= maximum) "native-file-bound-refused"
        result
    let parse (raw: byte array) =
        let mutable reader = Utf8JsonReader(ReadOnlySpan raw, JsonReaderOptions(MaxDepth=32))
        let scopes = Collections.Generic.Stack<Collections.Generic.HashSet<string>>()
        while reader.Read() do
            match reader.TokenType with
            | JsonTokenType.StartObject -> scopes.Push(Collections.Generic.HashSet(StringComparer.Ordinal))
            | JsonTokenType.EndObject -> scopes.Pop() |> ignore
            | JsonTokenType.PropertyName -> require (scopes.Peek().Add(reader.GetString())) "native-duplicate-key-refused"
            | _ -> ()
        use document = JsonDocument.Parse(raw, JsonDocumentOptions(MaxDepth=32))
        document.RootElement.Clone()
    let json path = bytes 1048576L path |> parse
    let hashFile expected path =
        require (hex 64 expected) "native-digest-refused"
        let raw = bytes (128L*1024L*1024L) path
        require (digest raw = expected) "native-acquired-bytes-refused"
        raw

    type Producer = private { Identity: string; SourceSha: string; SourceTree: string; Binding: string; Provenance: string; Files: Map<string,string> }
    let producerIdentity p = p.Identity
    let runtimeNames = Set ["FourD.Typed";"FourD.Typed.dll";"FourD.Typed.deps.json";"FourD.Typed.runtimeconfig.json";"FSharp.Core.dll"]
    let private fileMap (e: JsonElement) =
        require (fields e = runtimeNames) "native-runtime-closure-refused"
        e.EnumerateObject() |> Seq.map (fun p -> let h = str e p.Name in require (hex 64 h) "native-runtime-digest-refused"; p.Name,h) |> Map.ofSeq
    let acquireProducer request =
        closed ["runtimeRoot";"bindingPath";"provenancePath";"hostPath";"expectedSourceSha";"expectedSourceTree";"expectedBindingSha256";"expectedProvenanceSha256";"expectedFiles"] request
        let source,tree = str request "expectedSourceSha",str request "expectedSourceTree"
        require (hex 40 source && hex 40 tree) "native-producer-source-refused"
        let expectedFiles = fileMap (request.GetProperty "expectedFiles")
        // A matching foreign closure is insufficient: the command making this decision
        // must itself be the qualified assembly and standalone apphost.
        let executingAssembly=typeof<NativeSourceSnapshot>.Assembly.Location
        require (not(String.IsNullOrWhiteSpace executingAssembly)) "native-producer-executing-assembly-required"
        hashFile expectedFiles["FourD.Typed.dll"] executingAssembly |> ignore
        let executingHost=Environment.ProcessPath
        require (not(String.IsNullOrWhiteSpace executingHost) && Path.GetFileName executingHost="FourD.Typed") "native-producer-standalone-apphost-required"
        hashFile expectedFiles["FourD.Typed"] executingHost |> ignore
        let executingRoot=Path.GetDirectoryName executingHost
        for KeyValue(name,expected) in expectedFiles do
            hashFile expected (Path.Combine(executingRoot,name)) |> ignore
        hashFile expectedFiles["FSharp.Core.dll"] (typeof<option<int>>.Assembly.Location) |> ignore
        let root = str request "runtimeRoot"
        for KeyValue(name, expected) in expectedFiles do hashFile expected (Path.Combine(root,name)) |> ignore
        let bindingHash,provenanceHash = str request "expectedBindingSha256",str request "expectedProvenanceSha256"
        let binding = hashFile bindingHash (str request "bindingPath") |> parse
        closed ["schema";"sourceSha";"files"] binding
        require (str binding "schema" = "fsgg.fourd.typed-policy-binding/1" && str binding "sourceSha" = source && fileMap (binding.GetProperty "files") = expectedFiles) "native-binding-refused"
        let provenance = hashFile provenanceHash (str request "provenancePath") |> parse
        closed ["schema";"sourceSha";"sourceTree";"bindingSha256";"files";"sdkVersion";"runtimeVersion";"hostSha256"] provenance
        require (str provenance "schema" = "fsgg.fourd.native-runtime-provenance/1" && str provenance "sourceSha" = source && str provenance "sourceTree" = tree && str provenance "bindingSha256" = bindingHash && fileMap (provenance.GetProperty "files") = expectedFiles) "native-runtime-provenance-refused"
        require (str provenance "sdkVersion"="10.0.400" && str provenance "runtimeVersion"=Environment.Version.ToString()) "native-runtime-version-refused"
        hashFile (str provenance "hostSha256") (str request "hostPath") |> ignore
        { Identity=identity [source;tree;bindingHash;provenanceHash]; SourceSha=source; SourceTree=tree; Binding=bindingHash; Provenance=provenanceHash; Files=expectedFiles }

    type Root = private { Identity:string; Packet:JsonElement; ProductRevision:string; ProductTree:string; Inventory:string; P2Revision:string; P2Tree:string; Operation:string; Mode:string; Expires:int64; RunId:int64; Placement:string; RunnerId:int64; JobId:int64 }
    let rootIdentity r = r.Identity
    let rootMode r = r.Mode
    let rootExpiry r = r.Expires
    let product r = r.ProductRevision,r.ProductTree,r.Inventory
    let operation r = r.Operation
    let packet r = r.Packet
    let p2Revision r = r.P2Revision
    let acquireRoot producer request =
        closed ["packetPath";"releasePath";"packetAssetId";"locator";"runPath";"jobPath";"jobLinkHeaders";"expectedRunId";"expectedPlacementSha";"expectedActorId";"nowUnixSeconds"] request
        let locator=request.GetProperty "locator"
        closed ["repository";"tagPrefix";"packetName";"authorId"] locator
        require (str locator "repository"="FS-GG/FS.GG.Coordination" && str locator "tagPrefix"="fourd-native" && str locator "packetName"="fourd-native-packet.json" && number locator "authorId"=number request "expectedActorId") "native-packet-locator-refused"
        let release=json (str request "releasePath")
        require (number release "id">0L && str release "tag_name"=str locator "tagPrefix"+"-"+string(number request "expectedRunId") && number (release.GetProperty "author") "id"=number locator "authorId" && not (boolean release "draft") && not (boolean release "prerelease")) "native-release-custody-refused"
        let assets=release.GetProperty("assets").EnumerateArray() |> Seq.filter (fun a -> str a "name"=str locator "packetName") |> Seq.toArray
        require (assets.Length=1) "native-packet-asset-ambiguity-refused"
        let asset=assets[0]
        require (number asset "id"=number request "packetAssetId" && number asset "id">0L && number asset "size">0L && number asset "size"<=1048576L && str asset "state"="uploaded" && number (asset.GetProperty "uploader") "id"=number locator "authorId") "native-packet-asset-refused"
        let githubDigest=str asset "digest"
        require (githubDigest.StartsWith("sha256:",StringComparison.Ordinal) && hex 64 (githubDigest.Substring 7)) "native-packet-asset-digest-required"
        let packetHash=githubDigest.Substring 7
        let raw=hashFile packetHash (str request "packetPath")
        require (int64 raw.Length=number asset "size") "native-packet-asset-size-refused"
        let p = parse raw
        closed ["schema";"mode";"runId";"runAttempt";"placementSha";"placementTree";"originalActorId";"triggeringActorId";"runNonce";"issuedAt";"expiresAt";"producer";"product";"p2";"runner";"sourceCapsule";"operationId"] p
        let runId,placement,actor,now = number request "expectedRunId",str request "expectedPlacementSha",number request "expectedActorId",number request "nowUnixSeconds"
        require (str p "schema" = "fsgg.fourd.native-root-packet/1" && runId > 0L && hex 40 placement && number p "runId" = runId && number p "runAttempt" = 1L && str p "placementSha" = placement && hex 40 (str p "placementTree") && number p "originalActorId" = actor && number p "triggeringActorId" = actor && token (str p "runNonce")) "native-packet-binding-refused"
        let mode = str p "mode"
        let issued,expires = number p "issuedAt",number p "expiresAt"
        require ((mode="full" || mode="capability") && issued <= now && now < expires && expires-issued <= 5400L && expires > issued) "native-packet-expired"
        let prod = p.GetProperty "producer"
        closed ["sourceSha";"sourceTree";"bindingSha256";"provenanceSha256";"files"] prod
        require (str prod "sourceSha" = producer.SourceSha && str prod "sourceTree" = producer.SourceTree && str prod "bindingSha256" = producer.Binding && str prod "provenanceSha256" = producer.Provenance && fileMap (prod.GetProperty "files") = producer.Files) "native-packet-producer-refused"
        let product = p.GetProperty "product"
        closed ["revision";"tree";"inventorySha256"] product
        require (hex 40 (str product "revision") && hex 40 (str product "tree") && hex 64 (str product "inventorySha256")) "native-product-refused"
        require (str product "revision"=placement && str product "tree"=str p "placementTree") "native-product-placement-refused"
        let p2 = p.GetProperty "p2"
        closed ["revision";"tree";"executorDllSha256"] p2
        require (str p2 "revision" = "9d99573628ee31e966a61a28b9b36f7414b10f33" && str p2 "tree" = "e96774e11969b2730ebcf40ab47a06d7a17d46d6" && hex 64 (str p2 "executorDllSha256")) "native-p2-source-refused"
        let runner = p.GetProperty "runner"
        closed ["id";"name";"groupId";"labels"] runner
        require (number runner "id" > 0L && number runner "groupId" > 0L && token (str runner "name")) "native-runner-refused"
        let labels = runner.GetProperty("labels").EnumerateArray() |> Seq.map (fun v -> v.GetString()) |> Seq.toList
        require (labels.Length > 0 && labels.Length = (Set.ofList labels).Count && List.forall token labels) "native-runner-labels-refused"
        let run,jobs = json (str request "runPath"),json (str request "jobPath")
        let links=request.GetProperty("jobLinkHeaders").EnumerateArray() |> Seq.toArray
        require (links.Length=0 && number jobs "total_count"=1L && jobs.GetProperty("jobs").GetArrayLength()=1) "native-job-census-refused"
        let job=jobs.GetProperty("jobs")[0]
        require (str job "name"="native-qualification") "native-job-name-refused"
        require (number run "id" = runId && number run "run_attempt" = 1L && str run "head_sha" = placement && str run "event" = "workflow_dispatch" && str run "path" = ".github/workflows/fourd-native-image-qualification.yml" && str run "head_branch" = "main" && str (run.GetProperty "repository") "full_name" = "FS-GG/FS.GG.FourD" && number (run.GetProperty "repository") "id" = 1390568106L && number (run.GetProperty "actor") "id" = actor && number (run.GetProperty "triggering_actor") "id" = actor && str run "status" = "in_progress") "native-run-readback-refused"
        require (number job "run_id" = runId && number job "run_attempt" = 1L && str job "head_sha" = placement && str job "status" = "in_progress" && number job "runner_id" = number runner "id" && str job "runner_name" = str runner "name" && number job "runner_group_id" = number runner "groupId" && (job.GetProperty("labels").EnumerateArray() |> Seq.map (fun v -> v.GetString()) |> Set.ofSeq) = Set.ofList labels) "native-job-readback-refused"
        let capsule = p.GetProperty "sourceCapsule"
        closed ["transport";"releaseId";"assetId";"tag";"name";"ciphertextBytes";"ciphertextSha256";"descriptor";"descriptorSha256";"recipientPublicKeySha256"] capsule
        require (str capsule "transport" = "coordination-release-asset" && number capsule "releaseId" > 0L && number capsule "assetId" > 0L && number capsule "ciphertextBytes" >= 2L && number capsule "ciphertextBytes" <= 25165824L && hex 64 (str capsule "ciphertextSha256") && hex 64 (str capsule "recipientPublicKeySha256") && token (str capsule "tag") && token (str capsule "name")) "native-capsule-refused"
        let descriptor = capsule.GetProperty "descriptor"
        closed ["schema";"purpose";"placementSha";"runId";"runAttempt";"runNonce";"coordinationRepositoryId";"fourdRepositoryId";"sourceSha";"sourceTree";"inventorySha256";"plaintextBytes";"plaintextSha256";"recipientPublicKeySha256";"sealerSha256";"issuedAt";"expiresAt"] descriptor
        // Preserve the existing descriptor's exact bytes, including its newline hash.
        require (digest (Encoding.UTF8.GetBytes(descriptor.GetRawText()+"\n")) = str capsule "descriptorSha256" && str descriptor "schema" = "fsgg.fourd.source-capsule-descriptor/1" && str descriptor "purpose" = "fourd-source-acquisition" && str descriptor "placementSha" = placement && str descriptor "runId" = string runId && str descriptor "runAttempt" = "1" && str descriptor "runNonce" = str p "runNonce" && str descriptor "coordinationRepositoryId" = "1346720714" && str descriptor "fourdRepositoryId" = "1390568106" && str descriptor "sourceSha" = str product "revision" && str descriptor "sourceTree" = str product "tree" && str descriptor "inventorySha256" = str product "inventorySha256" && str descriptor "recipientPublicKeySha256" = str capsule "recipientPublicKeySha256" && hex 64 (str descriptor "plaintextSha256") && hex 64 (str descriptor "sealerSha256") && number descriptor "plaintextBytes" >= 2L && number descriptor "plaintextBytes" <= 16777216L) "native-descriptor-binding-refused"
        let parseTime name =
            match DateTimeOffset.TryParse(str descriptor name, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal) with
            | true,value -> value.ToUnixTimeSeconds()
            | _ -> fail "native-descriptor-time-refused"
        require (parseTime "issuedAt"=issued && parseTime "expiresAt"=expires) "native-descriptor-time-refused"
        require (str p "operationId" = "fourd-portable-technical") "native-operation-refused"
        { Identity=identity [producer.Identity;packetHash;string runId;string (number job "id")]; Packet=p; ProductRevision=str product "revision"; ProductTree=str product "tree"; Inventory=str product "inventorySha256"; P2Revision=str p2 "revision"; P2Tree=str p2 "tree"; Operation=str p "operationId"; Mode=mode; Expires=expires; RunId=runId; Placement=placement; RunnerId=number runner "id"; JobId=number job "id" }

    let acquireCapsule root request =
        closed ["releasePath";"assetPath";"sealerPath"] request
        let capsule=root.Packet.GetProperty "sourceCapsule"
        let release=json (str request "releasePath")
        hashFile (str (capsule.GetProperty "descriptor") "sealerSha256") (str request "sealerPath") |> ignore
        let actor=number root.Packet "originalActorId"
        require (number release "id"=number capsule "releaseId" && str release "tag_name"=str capsule "tag" && number (release.GetProperty "author") "id"=actor && not(boolean release "draft") && not(boolean release "prerelease")) "native-capsule-release-refused"
        let assets=release.GetProperty("assets").EnumerateArray() |> Seq.toArray
        let matches=assets |> Array.filter (fun a -> number a "id"=number capsule "assetId" || str a "name"=str capsule "name")
        require (matches.Length=1) "native-capsule-asset-ambiguity-refused"
        let asset=matches[0]
        require (number asset "id"=number capsule "assetId" && str asset "name"=str capsule "name" && number asset "size"=number capsule "ciphertextBytes" && str asset "digest"="sha256:"+str capsule "ciphertextSha256" && str asset "state"="uploaded" && number (asset.GetProperty "uploader") "id"=actor) "native-capsule-asset-binding-refused"
        let raw=hashFile (str capsule "ciphertextSha256") (str request "assetPath")
        require (int64 raw.Length=number capsule "ciphertextBytes") "native-capsule-acquired-size-refused"
        identity [root.Identity;str capsule "ciphertextSha256";str capsule "descriptorSha256"]
    let capsuleProofIdentity root =
        let c=root.Packet.GetProperty "sourceCapsule"
        identity [root.Identity;str c "ciphertextSha256";str c "descriptorSha256"]

    let private encoded (e:JsonElement) name =
        let v=e.GetProperty(name:string)
        require (v.ValueKind=JsonValueKind.String) "native-base64-refused"
        let text=v.GetString()
        require (not(isNull text) && text.Length<=1048576) "native-base64-bound-refused"
        Convert.FromBase64String text
    let private gitHash kind (raw:byte array) =
        let header=Encoding.ASCII.GetBytes(kind+" "+string raw.Length+"\000")
        Convert.ToHexString(SHA1.HashData(Array.append header raw)).ToLowerInvariant()
    let acquireSource root request =
        closed ["sourceRoot";"observedRevision";"observedTree";"gitObservationPath"] request
        let source,tree,inventory = product root
        let sourceRoot=str request "sourceRoot"
        let git=json (str request "gitObservationPath")
        closed ["revision";"tree";"rawCommitBase64";"treeObjects"] git
        let commit=encoded git "rawCommitBase64"
        require (str git "revision"=source && str git "tree"=tree && gitHash "commit" commit=source && (Encoding.UTF8.GetString commit).StartsWith("tree "+tree+"\n",StringComparison.Ordinal)) "native-raw-commit-refused"
        let trees=git.GetProperty("treeObjects").EnumerateArray() |> Seq.map (fun item ->
            closed ["sha";"base64"] item
            let hash,raw=str item "sha",encoded item "base64"
            require (hex 40 hash && raw.Length<=1048576 && gitHash "tree" raw=hash) "native-raw-tree-refused"
            hash,raw) |> Seq.toArray
        require (trees.Length>0 && trees.Length<=8192 && (trees |> Array.map fst |> Set.ofArray).Count=trees.Length) "native-raw-tree-closure-refused"
        let treeMap=Map.ofArray trees
        let mutable count=0
        let visited=Collections.Generic.HashSet<string>()
        let rec visit depth prefix hash =
            require (depth<=128) "native-git-tree-depth-refused"
            visited.Add hash |> ignore
            let raw=treeMap.TryFind hash |> Option.defaultWith (fun () -> fail "native-git-tree-missing")
            let mutable pos=0
            while pos<raw.Length do
                let space=Array.IndexOf(raw,byte ' ',pos)
                let zero=if space>=0 then Array.IndexOf(raw,0uy,space+1) else -1
                require (space>pos && zero>space+1 && zero+21<=raw.Length) "native-git-tree-entry-refused"
                let mode=Encoding.ASCII.GetString(raw,pos,space-pos)
                let name=UTF8Encoding(false,true).GetString(raw,space+1,zero-space-1)
                require (name<>"." && name<>".." && not(name.Contains '/') && not(name.Contains '\\')) "native-git-path-refused"
                let hash=Convert.ToHexString(raw.AsSpan(zero+1,20)).ToLowerInvariant()
                let relative=if prefix="" then name else prefix+"/"+name
                if mode="40000" then visit (depth+1) relative hash
                else
                    require (mode="100644" || mode="100755") "native-git-mode-refused"
                    let path=Path.Combine(sourceRoot,relative)
                    noLinks path
                    require ((FileInfo path).Length<=67108864L) "native-source-file-bound-refused"
                    let content=File.ReadAllBytes path
                    require (gitHash "blob" content=hash) "native-git-blob-refused"
                    let executable=File.GetUnixFileMode(path) &&& (UnixFileMode.UserExecute ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherExecute) <> enum<UnixFileMode> 0
                    require (executable=(mode="100755")) "native-context-mode-refused"
                    count<-count+1
                    require (count<=8192) "native-git-file-count-refused"
                pos<-zero+21
        visit 0 "" tree
        require (visited.Count=treeMap.Count) "native-unreachable-tree-refused"
        let snapshot=NativeSource.acquire source tree inventory (str request "observedRevision") (str request "observedTree") sourceRoot
        require ((NativeSource.projection snapshot).fileCount=count) "native-context-closure-refused"
        snapshot

    let memoryHeadroom e =
        require (str e "schema"="fsgg.fourd.image-temporary-memory/1") "native-memory-schema-refused"
        let mutable available=number e "memAvailableBytes"
        require (available>=0L && available<=number e "memTotalBytes" && number e "processId">0L) "native-memory-host-refused"
        let ancestors=e.GetProperty("cgroupAncestors").EnumerateArray() |> Seq.toArray
        require (ancestors.Length>0 && ancestors.Length<=128) "native-memory-cgroup-refused"
        for row in ancestors do
            if fields row=Set ["path";"hierarchyRoot"] then require (str row "path"="." && boolean row "hierarchyRoot") "native-memory-root-refused"
            else
                closed ["path";"current";"max";"high";"events"] row
                let used=number row "current"
                require (used>=0L) "native-memory-current-refused"
                for key in ["max";"high"] do
                    let v=row.GetProperty key
                    if v.ValueKind=JsonValueKind.String then require (v.GetString()="max") "native-memory-limit-refused"
                    else let ceiling=number row key in require (ceiling>=0L) "native-memory-limit-refused"; available<-min available (max 0L (ceiling-used))
        require (number e "availableHeadroomBytes"=available && number e "scratchBytes"=5368709120L && number e "reserveBytes"=2147483648L && number e "requiredHeadroomBytes"=7516192768L) "native-memory-derived-refused"
        available
    let resources path =
        let e=json path
        closed ["schema";"admitted";"memory";"cpu";"filesystems";"metadataFloorBytes";"metadataFloorInodes";"fullImageDiskAdmitted";"mountNamespace"] e
        require (str e "schema"="fsgg.fourd.pre-effect-resources/1" && memoryHeadroom (e.GetProperty "memory")>=7516192768L && not(boolean e "fullImageDiskAdmitted")) "native-resource-memory-refused"
        let cpu=e.GetProperty "cpu"
        closed ["affinity";"effectiveCpuUpperBound";"cgroupAncestors"] cpu
        let affinity=cpu.GetProperty("affinity").EnumerateArray() |> Seq.map (_.GetInt32()) |> Seq.toArray
        require (affinity.Length>0 && affinity.Length=(Set.ofArray affinity).Count && Array.forall (fun n -> n>=0) affinity) "native-cpu-affinity-refused"
        let cpuRows=cpu.GetProperty("cgroupAncestors").EnumerateArray() |> Seq.toArray
        let memoryRows=e.GetProperty("memory").GetProperty("cgroupAncestors").EnumerateArray() |> Seq.toArray
        let paths rows=rows |> Array.map (fun row -> str row "path")
        require (cpuRows.Length=memoryRows.Length && Set.ofArray(paths cpuRows)=Set.ofArray(paths memoryRows) && (Set.ofArray(paths cpuRows)).Count=cpuRows.Length) "native-cpu-cgroup-coordinate-refused"
        let mutable effective=float affinity.Length
        for row in cpuRows do
            if fields row=Set ["path";"hierarchyRoot"] then require (str row "path"="." && boolean row "hierarchyRoot") "native-cpu-root-refused"
            else
                closed ["path";"quota";"period"] row
                let period=number row "period"
                require (period>0L) "native-cpu-period-refused"
                let quota=str row "quota"
                if quota<>"max" then
                    match Int64.TryParse quota with
                    | true,n when n>0L -> effective<-min effective (float n/float period)
                    | _ -> fail "native-cpu-quota-refused"
        require (effective>=2.0 && cpu.GetProperty("effectiveCpuUpperBound").GetDouble()=effective) "native-cpu-derived-refused"
        let filesystems=e.GetProperty("filesystems").EnumerateArray() |> Seq.toArray
        require (filesystems.Length>=2 && filesystems.Length<=4 && number e "metadataFloorBytes"=67108864L && number e "metadataFloorInodes"=1024L) "native-filesystem-observation-refused"
        for fs in filesystems |> Array.take (filesystems.Length-1) do
            require (str fs "schema"="fsgg.fourd.persistent-storage/1" && number fs "device">=0L && number fs "freeBytes">=67108864L && number fs "freeInodes">=1024L && number fs "freeBytes"<=number fs "totalBytes" && number fs "freeInodes"<=number fs "totalInodes") "native-filesystem-headroom-refused"
        e
    let sourceIdentity snapshot = (NativeSource.projection snapshot).identity
    let sourceBound root e =
        let source,tree,inventory = product root
        require (str e "sourceRevision" = source && str e "sourceTree" = tree && str e "sourceInventorySha256" = inventory) "native-evidence-source-refused"
    let preflight root launcherUid launcherGid path =
        let e = json path
        closed ["schema";"sourceRevision";"sourceTree";"sourceInventorySha256";"podmanVersion";"utilLinuxUnshareVersion";"utilLinuxMountVersion";"rootlessUidMapSha256";"rootlessGidMapSha256";"operationUidGid32768Mapped";"namespaceRootMapsLaunchingUser";"persistentStorage";"targetStores";"runrootsShortDistinctPrivate";"imageCopyTemporaryStorage";"targetStoreSurvivorListingProbed";"noSudoOrHostFallback"] e
        sourceBound root e
        require (str e "schema" = "fsgg.fourd.native-image-preflight/1" && (["operationUidGid32768Mapped";"namespaceRootMapsLaunchingUser";"runrootsShortDistinctPrivate";"targetStoreSurvivorListingProbed";"noSudoOrHostFallback"] |> List.forall (boolean e))) "native-preflight-refused"
        require (hex 64 (str e "rootlessUidMapSha256") && hex 64 (str e "rootlessGidMapSha256")) "native-preflight-mapping-refused"
        let temporary = e.GetProperty "imageCopyTemporaryStorage"
        require (str temporary "schema" = "fsgg.fourd.image-temporary-capability/2" && memoryHeadroom (temporary.GetProperty "memory")>=7516192768L && number temporary "hostUid"=launcherUid && str temporary "hostMountNamespace"<>str temporary "childMountNamespace") "native-preflight-temporary-refused"
        let mappings=temporary.GetProperty "namespaceMappings"
        for name,launcher in ["uidMap",launcherUid;"gidMap",launcherGid] do
            let rows=mappings.GetProperty(name).EnumerateArray() |> Seq.map (fun row -> row.EnumerateArray() |> Seq.map (_.GetInt64()) |> Seq.toArray) |> Seq.toArray
            require (rows.Length>0 && rows.Length<=16 && (rows |> Array.forall (fun row -> row.Length=3 && Array.forall (fun n -> n>=0L) row && row[2]>0L))) "native-namespace-map-refused"
            require ((rows |> Array.exists (fun row -> row[0]=0L && row[1]=launcher)) && (rows |> Array.exists (fun row -> row[0]<=32768L && 32768L-row[0]<row[2]))) "native-namespace-map-binding-refused"
        let probes=temporary.GetProperty("probes").EnumerateArray() |> Seq.toArray
        require (probes.Length=2 && (probes |> Array.map (fun p -> number p "requestedBytes") |> Set.ofArray)=Set [16777216L;5368709120L]) "native-probes-refused"
        for probe in probes do
            let requested=number probe "requestedBytes"
            let before,after=probe.GetProperty "initial",probe.GetProperty "afterProbeCleanup"
            require (number before "device"=number after "device" && number before "capacityBytes">=requested && number after "freeBytes">=requested && hex 64 (str before "mountInfoSha256") && hex 64 (str after "mountInfoSha256") && boolean probe "subprocessDeviceMatched" && boolean probe "podmanChildDeviceMatched") "native-probe-device-refused"
        let cancellation=temporary.GetProperty "mappedCancellation"
        require (number cancellation "uid"=32768L && number cancellation "gid"=32768L && boolean cancellation "cleanupComplete" && boolean temporary "cleanupComplete") "native-probe-retirement-refused"
        let stores=e.GetProperty "targetStores"
        require (fields stores=Set ["build";"fresh-load"]) "native-private-stores-refused"
        for name in ["build";"fresh-load"] do
            let store=stores.GetProperty name
            closed ["graphDriver";"rootless"] store
            require (str store "graphDriver"="vfs" && boolean store "rootless") "native-store-profile-refused"
        e
    let private archiveJoin (e:JsonElement) =
        let path=str e "archivePath"
        noLinks path
        use archive=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.SequentialScan)
        require (archive.Length=number e "archiveBytes" && archive.Length<=4294967296L) "native-archive-size-refused"
        let archiveHash=Convert.ToHexString(SHA256.HashData archive).ToLowerInvariant()
        require (archiveHash=str e "archiveSha256") "native-archive-byte-identity-refused"
        archive.Position<-0L
        use reader=new TarReader(archive,true)
        let files=Collections.Generic.Dictionary<string,int64*string>(StringComparer.Ordinal)
        let metadata=Collections.Generic.Dictionary<string,byte array>(StringComparer.Ordinal)
        let inventory=ResizeArray<string*obj>()
        let seen=Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        let buffer=Array.zeroCreate<byte> 65536
        let mutable entry=reader.GetNextEntry()
        let mutable cached=0L
        while not(isNull entry) do
            let original=if entry.Name.StartsWith("./",StringComparison.Ordinal) then entry.Name.Substring 2 else entry.Name
            // Python tarfile's existing OCI inventory strips directory suffix slashes.
            // Normalize the same transport spelling before duplicate/inventory checks.
            let name=if entry.EntryType=TarEntryType.Directory then original.TrimEnd('/') else original
            require (name<>"" && not(name.StartsWith("/",StringComparison.Ordinal)) && not(name.Split('/') |> Array.contains "..") && seen.Add name && seen.Count<=4096 && (name |> Seq.forall (fun c -> c>=' ' && c<='~'))) "native-archive-path-refused"
            if entry.EntryType=TarEntryType.Directory then
                inventory.Add(name, box {|bytes=0L;path=name;``type``="directory"|})
            else
                require (entry.EntryType=TarEntryType.RegularFile || entry.EntryType=TarEntryType.V7RegularFile) "native-archive-special-file-refused"
                use hash=IncrementalHash.CreateHash HashAlgorithmName.SHA256
                let save=entry.Length<=16777216L && cached+entry.Length<=67108864L
                use saved=new MemoryStream()
                let mutable count=0L
                let mutable read=entry.DataStream.Read(buffer,0,buffer.Length)
                while read>0 do
                    count<-count+int64 read
                    require (count<=4294967296L) "native-archive-member-bound-refused"
                    hash.AppendData(buffer,0,read)
                    if save then saved.Write(buffer,0,read)
                    read<-entry.DataStream.Read(buffer,0,buffer.Length)
                require (count=entry.Length) "native-archive-member-size-refused"
                let digest=Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()
                files.Add(name,(count,digest))
                inventory.Add(name,box {|bytes=count;path=name;sha256=digest;``type``="file"|})
                if save then metadata.Add(name,saved.ToArray()); cached<-cached+count
            entry<-reader.GetNextEntry()
        let readJson name =
            match metadata.TryGetValue name with
            | true,raw -> parse raw
            | _ ->
                require (files.ContainsKey name && fst files[name]<=16777216L) "native-oci-metadata-bound-refused"
                use raw=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)
                use tar=new TarReader(raw)
                let mutable item=tar.GetNextEntry()
                while not(isNull item) && (if item.Name.StartsWith("./",StringComparison.Ordinal) then item.Name.Substring 2 else item.Name)<>name do item<-tar.GetNextEntry()
                require (not(isNull item)) "native-oci-member-missing"
                use body=new MemoryStream()
                item.DataStream.CopyTo body
                parse (body.ToArray())
        let layout=readJson "oci-layout"
        closed ["imageLayoutVersion"] layout
        require (str layout "imageLayoutVersion"="1.0.0") "native-oci-layout-refused"
        let index=readJson "index.json"
        let manifests=index.GetProperty("manifests").EnumerateArray() |> Seq.toArray
        require (manifests.Length=1) "native-oci-manifest-count-refused"
        let allowed=Collections.Generic.HashSet<string>([|"oci-layout";"index.json"|],StringComparer.Ordinal)
        let descriptors=ResizeArray<string*string*int64*string>()
        let descriptor role (d:JsonElement) =
            let digest=str d "digest"
            require (digest.StartsWith("sha256:",StringComparison.Ordinal) && hex 64 (digest.Substring 7)) "native-oci-descriptor-digest-refused"
            let name="blobs/sha256/"+digest.Substring 7
            require (files.ContainsKey name && files[name]=(number d "size",digest.Substring 7)) "native-oci-descriptor-bytes-refused"
            allowed.Add name |> ignore
            descriptors.Add(role,digest,number d "size",str d "mediaType")
            name,digest
        let manifestName,manifestDigest=descriptor "manifest" manifests[0]
        let manifest=readJson manifestName
        let configName,configDigest=descriptor "config" (manifest.GetProperty "config")
        let config=readJson configName
        require (str manifests[0] "mediaType"="application/vnd.oci.image.manifest.v1+json" && str (manifest.GetProperty "config") "mediaType"="application/vnd.oci.image.config.v1+json") "native-oci-media-type-refused"
        let layers=manifest.GetProperty("layers").EnumerateArray() |> Seq.toArray
        require (layers.Length>0 && layers.Length<=128) "native-oci-layers-refused"
        let layerDigests=layers |> Array.map (fun layer -> descriptor "layer" layer |> snd)
        require ((Set.ofArray layerDigests).Count=layers.Length) "native-oci-duplicate-layer-refused"
        let diffIds=config.GetProperty("rootfs").GetProperty("diff_ids").EnumerateArray() |> Seq.map (_.GetString()) |> Seq.toArray
        require (diffIds.Length=layers.Length && (diffIds |> Array.forall (fun d -> not(isNull d) && d.StartsWith("sha256:",StringComparison.Ordinal) && hex 64 (d.Substring 7)))) "native-oci-rootfs-refused"
        require (str config "os"="linux" && str config "architecture"="amd64") "native-oci-platform-refused"
        let runtime=config.GetProperty "config"
        require (Set ["32768";"32768:32768"] |> Set.contains (str runtime "User")) "native-oci-user-refused"
        for name in ["Entrypoint";"Cmd";"Volumes"] do
            match runtime.TryGetProperty name with
            | false,_ -> ()
            | true,v -> require (v.ValueKind=JsonValueKind.Null || (v.ValueKind=JsonValueKind.Array && v.GetArrayLength()=0) || (name="Volumes" && v.ValueKind=JsonValueKind.Object && fields v=Set.empty)) "native-oci-runtime-hook-refused"
        match runtime.TryGetProperty "Env" with
        | true,env when env.ValueKind<>JsonValueKind.Null ->
            let allowed=Set ["HOME";"PATH";"LANG";"LC_ALL";"PYTHON_SHA256";"PYTHON_VERSION";"container"]
            for v in env.EnumerateArray() do require (allowed.Contains(v.GetString().Split('=')[0])) "native-oci-environment-refused"
        | _ -> ()
        require (files.Keys |> Seq.forall allowed.Contains) "native-oci-unreachable-member-refused"
        require (manifestDigest=str e "manifestDigest" && configDigest=str e "configDigest") "native-oci-image-join-refused"
        let declared=e.GetProperty("ociDescriptors").EnumerateArray() |> Seq.map (fun d -> str d "role",str d "digest",number d "bytes",str d "mediaType") |> Seq.toArray
        require (declared=descriptors.ToArray()) "native-oci-descriptor-closure-refused"
        let entries=inventory |> Seq.sortBy fst |> Seq.map snd |> Seq.toArray
        require (digest (JsonSerializer.SerializeToUtf8Bytes entries)=str e "archiveInventorySha256") "native-oci-inventory-refused"

    /// A byte-qualified archive generation, separate from any subsequent Podman load.
    type ArchiveProof = private {
        Identity:string; ArchiveSha256:string; ArchiveBytes:int64; Inventory:string
        Manifest:string; Config:string; BuildConfig:string
    }
    let archiveIdentity proof = proof.Identity
    let archiveProjection proof =
        {| identity=proof.Identity;archiveSha256=proof.ArchiveSha256;archiveBytes=proof.ArchiveBytes
           archiveInventorySha256=proof.Inventory;manifestDigest=proof.Manifest
           configDigest=proof.Config;buildConfigId=proof.BuildConfig |}
    let private archiveProofFrom root sourceIdentity (e:JsonElement) =
        let revision,tree,inventory=product root
        require (sourceIdentity=identity [revision;tree;inventory]) "native-archive-source-proof-refused"
        let hash,bytes,inventory=str e "archiveSha256",number e "archiveBytes",str e "archiveInventorySha256"
        let manifest,config,build=str e "manifestDigest",str e "configDigest",str e "buildConfigId"
        let digestId (value:string) = value.StartsWith("sha256:",StringComparison.Ordinal) && hex 64 (value.Substring 7)
        require (hex 64 hash && hex 64 inventory && bytes>=2L && bytes<=4294967296L && digestId manifest && digestId config && build=config) "native-archive-profile-refused"
        {Identity=identity [rootIdentity root;sourceIdentity;hash;string bytes;inventory;manifest;config;build]
         ArchiveSha256=hash;ArchiveBytes=bytes;Inventory=inventory;Manifest=manifest;Config=config;BuildConfig=build}
    let acquireArchive root sourceIdentity path =
        let e=json path
        closed ["schema";"sourceRevision";"sourceTree";"sourceInventorySha256";"archivePath";"archiveSha256";"archiveBytes";"archiveInventorySha256";"ociDescriptors";"buildConfigId";"configDigest";"manifestDigest"] e
        require (str e "schema"="fsgg.fourd.native-archive-candidate/1") "native-archive-candidate-schema-refused"
        sourceBound root e
        let proof=archiveProofFrom root sourceIdentity e
        archiveJoin e
        proof
    /// Historical journal restoration does not reopen an intentionally retired archive.
    let restoreArchive root sourceIdentity e =
        closed ["identity";"archiveSha256";"archiveBytes";"archiveInventorySha256";"manifestDigest";"configDigest";"buildConfigId"] e
        let proof=archiveProofFrom root sourceIdentity e
        require (str e "identity"=proof.Identity) "native-archive-proof-refused"
        proof
    let matchesArchive root sourceIdentity proof e =
        archiveIdentity (archiveProofFrom root sourceIdentity e)=archiveIdentity proof

    let image root acquireArchive path =
        let e = json path
        closed ["schema";"qualified";"sourceRevision";"sourceTree";"sourceInventorySha256";"imageReference";"imageId";"buildConfigId";"manifestDigest";"configDigest";"archivePath";"archiveSha256";"archiveBytes";"archiveInventorySha256";"ociDescriptors";"buildIdentity";"freshLoadStore";"freshLoadRunroot";"loadOutputSha256";"remainingGate";"temporaryMountNamespaceExited";"postNamespaceIdentityVerified";"nativeObservations"] e
        sourceBound root e
        let digestId name = let s = str e name in s.StartsWith("sha256:",StringComparison.Ordinal) && hex 64 (s.Substring 7)
        require (str e "schema" = "fsgg.fourd.native-image-binding/1" && not (boolean e "qualified") && (["imageId";"manifestDigest";"buildConfigId";"configDigest"] |> List.forall digestId)) "native-image-binding-refused"
        require (str e "imageId" = str e "buildConfigId" && str e "imageId" = str e "configDigest" && (str e "imageReference").EndsWith("@"+str e "manifestDigest",StringComparison.Ordinal) && hex 64 (str e "archiveSha256") && number e "archiveBytes" >= 2L && number e "archiveBytes" <= 4294967296L) "native-image-identity-refused"
        // Keep the historical wire scalars, but derive native settlement from actual
        // inspect/namespace/device/supervisor observations rather than two summary flags.
        boolean e "temporaryMountNamespaceExited" |> ignore
        boolean e "postNamespaceIdentityVerified" |> ignore
        let facts=e.GetProperty "nativeObservations"
        closed ["loadedImageId";"loadedManifestDigest";"postNamespaceImageId";"postNamespaceManifestDigest";"hostNamespaceBefore";"hostNamespaceAfter";"temporaryDeviceBefore";"temporaryDeviceAfter";"supervisorChildrenRemaining";"supervisorReadersRemaining"] facts
        let loadedId name =
            let value=str facts name
            require (hex 64 value) "native-loaded-image-id-refused"
            "sha256:"+value
        require (loadedId "loadedImageId"=str e "configDigest" && loadedId "postNamespaceImageId"=str e "configDigest" && str facts "loadedManifestDigest"=str e "manifestDigest" && str facts "postNamespaceManifestDigest"=str e "manifestDigest") "native-loaded-image-join-refused"
        require (token(str facts "hostNamespaceBefore") && str facts "hostNamespaceBefore"=str facts "hostNamespaceAfter" && number facts "temporaryDeviceBefore">=0L && number facts "temporaryDeviceBefore"=number facts "temporaryDeviceAfter") "native-post-namespace-boundary-refused"
        require (number facts "supervisorChildrenRemaining"=0L && number facts "supervisorReadersRemaining"=0L) "native-image-supervisor-unsettled"
        if acquireArchive then archiveJoin e
        e
    let p2 root image path =
        let e = json path
        closed ["schema";"outcome";"qualified";"coordinationRevision";"executorDllSha256";"sourceRevision";"sourceTree";"sourceInventorySha256";"imageReference";"imageId";"manifestDigest";"operationIdentity";"verificationSha256";"outputSha256";"executionStarted";"cleanupCompleted";"cancellationRequested";"terminationObserved";"duplicateSuppressedAfterReconstruction";"settledRecoveryWithoutRelaunch";"remainingExecutionRoots";"closedOperationOnly";"prelaunchRefusals"] e
        sourceBound root e
        require (str e "schema" = "fsgg.fourd.native-p2-qualification/1" && str e "outcome" = "passed" && boolean e "qualified" && str e "coordinationRevision" = root.P2Revision && str e "executorDllSha256" = str (root.Packet.GetProperty "p2") "executorDllSha256" && (["imageReference";"imageId";"manifestDigest"] |> List.forall (fun name -> str e name = str image name))) "native-p2-join-refused"
        let refusals = e.GetProperty "prelaunchRefusals"
        closed ["source";"scope";"operation"] refusals
        require (["executionStarted";"cleanupCompleted";"duplicateSuppressedAfterReconstruction";"settledRecoveryWithoutRelaunch";"closedOperationOnly"] |> List.forall (boolean e)) "native-p2-result-refused"
        require (not (boolean e "cancellationRequested") && number e "remainingExecutionRoots" = 0L && (["verificationSha256";"outputSha256"] |> List.forall (fun name -> hex 64 (str e name)))) "native-p2-settlement-refused"
        require (str e "operationIdentity"="test" && str e "verificationSha256"="ce00be26c8836753bc8c7518c43727835f813b24fdc36ed76dbc44016f75c1a3" && (["source";"scope";"operation"] |> List.forall (fun name -> token (str refusals name)))) "native-p2-operation-refused"
        e
    let remoteSettled root request =
        closed ["runPath";"jobPath";"jobLinkHeaders"] request
        let run,jobs=json (str request "runPath"),json (str request "jobPath")
        require (request.GetProperty("jobLinkHeaders").GetArrayLength()=0 && number jobs "total_count"=1L && jobs.GetProperty("jobs").GetArrayLength()=1) "native-remote-job-census-refused"
        let job=jobs.GetProperty("jobs")[0]
        require (number run "id"=root.RunId && number run "run_attempt"=1L && str run "head_sha"=root.Placement && str run "status"="completed" && str run "path"=".github/workflows/fourd-native-image-qualification.yml" && str run "head_branch"="main" && str run "event"="workflow_dispatch" && number (run.GetProperty "repository") "id"=1390568106L && str (run.GetProperty "repository") "full_name"="FS-GG/FS.GG.FourD" && number (run.GetProperty "actor") "id"=number root.Packet "originalActorId" && number (run.GetProperty "triggering_actor") "id"=number root.Packet "triggeringActorId") "native-remote-settlement-refused"
        require (number job "id"=root.JobId && number job "run_id"=root.RunId && number job "run_attempt"=1L && str job "head_sha"=root.Placement && str job "name"="native-qualification" && number job "runner_id"=root.RunnerId && str job "status"="completed") "native-remote-job-binding-refused"
        let runner=root.Packet.GetProperty "runner"
        require (str job "runner_name"=str runner "name" && number job "runner_group_id"=number runner "groupId" && (job.GetProperty("labels").EnumerateArray() |> Seq.map (_.GetString()) |> Set.ofSeq)=(runner.GetProperty("labels").EnumerateArray() |> Seq.map (_.GetString()) |> Set.ofSeq)) "native-remote-runner-binding-refused"
        let conclusion=str run "conclusion"
        require (conclusion<>"success" || str job "conclusion"="success") "native-remote-job-conclusion-refused"
        require (Set ["success";"failure";"cancelled";"timed_out";"action_required";"neutral";"skipped";"stale";"startup_failure"] |> Set.contains conclusion) "native-remote-conclusion-refused"
        conclusion

    /// Existing custody facts retain their original authority and are separate from local
    /// recipient file retirement. These observations must come from Root's fresh readback.
    let remoteCredentialRetirement root e =
        closed ["rootIdentity";"runId";"runAttempt";"releaseId";"assetId";"tag";"secretCount";"releaseCount";"assetStatus";"releaseStatus";"tagStatus";"privateKeyAbsent";"publicKeyAbsent";"failureCount"] e
        let capsule=root.Packet.GetProperty "sourceCapsule"
        require (str e "rootIdentity"=root.Identity && number e "runId"=root.RunId && number e "runAttempt"=1L && number e "releaseId"=number capsule "releaseId" && number e "assetId"=number capsule "assetId" && str e "tag"=str capsule "tag") "native-remote-retirement-identity-refused"
        require (number e "secretCount"=0L && number e "releaseCount"=0L && number e "failureCount"=0L && str e "assetStatus"="404" && str e "releaseStatus"="404" && str e "tagStatus"="404" && boolean e "privateKeyAbsent" && boolean e "publicKeyAbsent") "native-remote-credential-retirement-refused"
        e
