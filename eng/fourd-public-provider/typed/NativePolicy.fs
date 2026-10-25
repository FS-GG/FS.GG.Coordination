namespace FS.GG.FourD.Typed

open System
open System.Text.Json
open System.Text.Json.Nodes

/// One owner, one original clock, and distinct outcome/retirement evidence. History is a
/// qualified local IO journal, not an authenticated portable state or new custody protocol.
module NativePolicy =
    open NativeJoin
    type Intent = { Operation:string; Resource:string; Identity:string; Started:int; Pid:int64 option; StartIdentity:string option }
    type State = private {
        Mode:string; StartedUnix:int64; Elapsed:int; Phase:string
        Producer:Producer option; Root:Root option; SourceIdentity:string option; CurrentIdentity:string option
        ArchiveProof:ArchiveProof option; CurrentArchiveIdentity:string option; Image:JsonElement option; P2:JsonElement option; Resources:(int64*int64) option; P2Ready:bool; RevalidatedPhase:string option; Intents:Map<string,Intent>
        Owned:Map<string,string>; Closed:Set<string>; Cancelled:bool; Refusal:string option
        Outcome:string; RemoteConclusion:string option; RemoteRetirement:JsonElement option; LastRemoteElapsed:int option; RootRefusal:string option; CleanupStarted:int option; CollectionStarted:int option
    }
    let initial mode started =
        require ((mode="full" || mode="capability") && started > 0L) "native-initial-refused"
        { Mode=mode; StartedUnix=started; Elapsed=0; Phase="bootstrap"; Producer=None; Root=None
          SourceIdentity=None; CurrentIdentity=None; ArchiveProof=None; CurrentArchiveIdentity=None; Image=None; P2=None; Resources=None; P2Ready=false; RevalidatedPhase=None; Intents=Map.empty
          Owned=Map.empty; Closed=Set.empty; Cancelled=false; Refusal=None; Outcome="none"
          RemoteConclusion=None; RemoteRetirement=None; LastRemoteElapsed=None; RootRefusal=None; CleanupStarted=None; CollectionStarted=None }
    let budget s = if s.Mode="full" then 5400 else 1200
    let cleanupComplete s = s.Phase="finished" && s.Closed = (s.Owned |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
    let successful s = s.Mode="full" && s.Outcome="success" && s.P2.IsSome && not s.Cancelled && s.Refusal.IsNone && cleanupComplete s
    let private sticky reason s =
        { s with Refusal=(if s.Refusal.IsSome then s.Refusal else Some reason)
                 Outcome=(if s.Outcome="none" || s.Outcome="refused" then "refused" else "unknown") }
    let private alive s = not s.Cancelled && s.Refusal.IsNone
    let private root s = s.Root |> Option.defaultWith (fun () -> fail "native-root-required")
    let private owned s resource identity =
        require (token resource && token identity && not (s.Owned.ContainsKey resource)) "native-resource-ownership-refused"
        { s with Owned=s.Owned.Add(resource,identity) }
    let private acknowledged op s =
        match s.Intents.TryFind op with Some i -> i.Pid.IsSome && i.StartIdentity.IsSome | None -> false
    let private expectedPhase = function
        | "source" -> "key-ready" | "preflight" -> "source-ready" | "image" -> "preflight-ready"
        | "p2" -> "image-ready" | "cleanup" -> "cleanup" | "collection" -> "retired"
        | _ -> fail "native-operation-kind-refused"
    let private deadline s op =
        if s.Mode="capability" then
            match op with "source" | "preflight" -> 540 | "cleanup" -> 1020 | "collection" -> 1200 | _ -> 0
        else match op with "source" | "preflight" -> 600 | "image" -> 3325 | "p2" -> 4740 | "cleanup" -> 5220 | "collection" -> 5400 | _ -> 0
    let actions s =
        if s.Phase="finished" || s.Phase="incomplete" then [||]
        elif not (alive s) && s.Phase<>"cleanup" && s.Phase<>"retired" && s.Phase<>"collecting" then [|"begin-cleanup"|]
        else
            match s.Phase with
            | "bootstrap" -> [|"acquire-producer"|] | "packet-wait" -> [|"wait-packet"|]
            | "bound" -> [|"acquire-capsule"|] | "capsule-ready" -> [|"expose-recipient-key"|] | "key-ready" -> [|"acquire-source"|]
            | "source-ready" -> [|"preflight"|] | "preflight-ready" when s.Mode="full" -> [|"build-image"|]
            | "preflight-ready" -> [|"begin-cleanup"|] | "image-ready" -> [|"p2"|]
            | "p2-ready" -> [|"begin-cleanup"|]
            | "image-running" when s.Elapsed<deadline s "image" && s.ArchiveProof.IsSome && s.CurrentArchiveIdentity=Some(archiveIdentity s.ArchiveProof.Value) && s.RevalidatedPhase=Some "image-running" && s.SourceIdentity=s.CurrentIdentity -> [|"inspect-image-custody";"load-image"|]
            | "image-running" when s.Elapsed<deadline s "image" && s.RevalidatedPhase=Some "image-running" && s.SourceIdentity=s.CurrentIdentity -> [|"execute-image-command"|] | "cleanup" -> [|"settle-owned-resources"|]
            | "retired" -> [|"collect"|] | "collecting" -> [|"finish"|] | _ -> [|"observe-child"|]
    let projection s =
        let capability=s.Mode="capability" && s.Outcome="capability-complete" && not s.Cancelled && s.Refusal.IsNone && cleanupComplete s
        let disposition=if successful s then "qualified" elif capability then "capability-only" elif s.Phase="incomplete" then "incomplete" elif s.Refusal.IsSome && s.Outcome="refused" then "refused" elif s.Phase="finished" || s.Cancelled || s.Refusal.IsSome then "unknown" else "running"
        {| disposition=disposition;exitCode=(if successful s || capability then 0 else 2);qualified=successful s; schema="fsgg.fourd.native-state/1"; mode=s.Mode; startedUnixSeconds=s.StartedUnix; elapsed=s.Elapsed
           deadlines={|preflight=deadline s "preflight";image=deadline s "image";p2=deadline s "p2";cleanup=deadline s "cleanup";collection=deadline s "collection"|}
           allowances={|collection=180;cleanup=480;p2Command=1200;p2Operation=900;wrapper=120;termination=20;drain=15;handoff=60|}
           remaining=max 0 (budget s-s.Elapsed); phase=s.Phase; outcome=s.Outcome
           producerIdentity=s.Producer |> Option.map producerIdentity |> Option.toObj
           rootIdentity=s.Root |> Option.map rootIdentity |> Option.toObj
           sourceIdentity=Option.toObj s.SourceIdentity; currentIdentity=Option.toObj s.CurrentIdentity
           archiveProof=s.ArchiveProof |> Option.map archiveProjection |> Option.toObj;currentArchiveIdentity=Option.toObj s.CurrentArchiveIdentity
           imageObserved=s.Image.IsSome; p2Observed=s.P2.IsSome; cancelled=s.Cancelled; refusal=Option.toObj s.Refusal
           owned=s.Owned |> Map.toArray |> Array.map (fun (role,id) -> {|resource=role;identity=id|})
           closed=s.Closed |> Set.toArray; intents=s.Intents |> Map.toArray |> Array.map (fun (_,i) -> {|operation=i.Operation;resource=i.Resource;identity=i.Identity;started=i.Started;pid=Option.toNullable i.Pid;startIdentity=Option.toObj i.StartIdentity|})
           cleanupComplete=cleanupComplete s; successful=successful s; localQualified=successful s; remoteObservationElapsed=Option.toNullable s.LastRemoteElapsed; wholeAccepted=(successful s && s.RemoteConclusion=Some "success" && s.RemoteRetirement.IsSome && s.RootRefusal.IsNone); rootObservationRefusal=Option.toObj s.RootRefusal; remoteCredentialRetirement=Option.toNullable s.RemoteRetirement; remoteConclusion=Option.toObj s.RemoteConclusion |}

    /// The P2 compatibility binding derives solely from the already validated raw image.
    /// Its legacy flags are a projection of the native joins, never fresh input authority.
    let admittedImageBinding s =
        match s.Image with
        | None -> JsonSerializer.SerializeToElement(null:obj)
        | Some image ->
            let binding=JsonNode.Parse(image.GetRawText()).AsObject()
            binding["temporaryMountNamespaceExited"]<-JsonValue.Create true
            binding["postNamespaceIdentityVerified"]<-JsonValue.Create true
            JsonSerializer.SerializeToElement binding

    let private sourceProof s proof =
        closed ["identity";"revision";"tree";"inventorySha256"] proof
        let revision,tree,inventory = product (root s)
        let id = identity [revision;tree;inventory]
        require (str proof "identity"=id && str proof "revision"=revision && str proof "tree"=tree && str proof "inventorySha256"=inventory) "native-source-proof-refused"
        { s with Phase="source-ready"; SourceIdentity=Some id; CurrentIdentity=Some id }

    let private acceptArchive s proof =
        let next={s with CurrentArchiveIdentity=Some(archiveIdentity proof)}
        match s.ArchiveProof with
        | Some previous when archiveIdentity previous<>archiveIdentity proof -> sticky "native-archive-rebind-refused" next
        | _ -> {next with ArchiveProof=Some proof}

    /// The fresh event uses actual acquired bytes. Historical source-proof is accepted only
    /// in the prior qualified journal; retiring plaintext never requires reopening it.
    let apply historical s (e:JsonElement) =
        let kind,elapsed = str e "kind",number e "elapsed"
        let remote=kind="remote" || kind="remote-credential-retirement" || kind="root-refuse"
        require (elapsed>=int64 s.Elapsed && elapsed<=int64 Int32.MaxValue) "native-original-clock-refused"
        let s =
            if remote then
                require (elapsed=int64 s.Elapsed && (s.Phase="finished" || s.Phase="incomplete")) "native-root-observer-boundary-refused"
                // The independently admitted Root observer owns its finite read deadline.
                // Cross-machine clocks never renew or charge this frozen local window.
                {s with LastRemoteElapsed=Some s.Elapsed}
            else
                require (elapsed<=int64(budget s)) "native-original-clock-refused"
                {s with Elapsed=int elapsed}
        let shape names = closed ("kind"::"elapsed"::names) e
        let requireAlive () = require (alive s) "native-sticky-refusal"
        let normalized,next =
            match kind with
            | "producer" ->
                shape ["evidence"]; requireAlive(); require (s.Phase="bootstrap" && s.Elapsed < 600) "native-bootstrap-order-refused"
                let p=acquireProducer (e.GetProperty "evidence")
                e,{ s with Producer=Some p; Phase="packet-wait" }
            | "packet" ->
                shape ["evidence"]; requireAlive(); require (s.Phase="packet-wait" && s.Elapsed < deadline s "source") "native-packet-wait-expired"
                let evidence=e.GetProperty "evidence"
                require (number evidence "nowUnixSeconds" = s.StartedUnix + int64 s.Elapsed) "native-packet-clock-refused"
                let r=acquireRoot s.Producer.Value evidence
                require (rootMode r=s.Mode) "native-branch-refused"
                e,{s with Root=Some r;Phase="bound"}
            | "wait" ->
                shape []; require (s.Phase="packet-wait") "native-wait-order-refused"
                e,(if s.Elapsed >= deadline s "source" then sticky "native-packet-wait-expired" s else s)
            | "capsule" ->
                shape ["evidence"]; require (not historical) "native-historical-ciphertext-refused"; requireAlive()
                require (s.Phase="bound" && s.StartedUnix+int64 s.Elapsed<rootExpiry(root s)) "native-capsule-order-refused"
                let proof=acquireCapsule (root s) (e.GetProperty "evidence")
                JsonSerializer.SerializeToElement({|kind="capsule-proof";elapsed=s.Elapsed;identity=proof|}),{s with Phase="capsule-ready"}
            | "capsule-proof" ->
                shape ["identity"]; require historical "native-self-authored-capsule-proof-refused"; requireAlive()
                require (s.Phase="bound" && str e "identity"=capsuleProofIdentity(root s) && s.StartedUnix+int64 s.Elapsed<rootExpiry(root s)) "native-capsule-proof-refused"
                e,{s with Phase="capsule-ready"}
            | "key-exposed" ->
                shape ["resource";"identity";"publicKeySha256"]; requireAlive(); require (str e "publicKeySha256"=str ((packet (root s)).GetProperty "sourceCapsule") "recipientPublicKeySha256") "native-key-identity-refused"; require (s.Phase="capsule-ready" && s.StartedUnix+int64 s.Elapsed < rootExpiry (root s)) "native-key-before-binding-refused"
                e,{ owned s (str e "resource") (str e "identity") with Phase="key-ready" }
            | "intent" ->
                shape ["operation";"resource";"identity"]
                let op=str e "operation"
                if op<>"cleanup" && op<>"collection" then requireAlive()
                require (s.Mode="full" || (op<>"image" && op<>"p2")) "native-capability-effect-refused"
                require (s.Phase=expectedPhase op && not (s.Intents.ContainsKey op) && s.Elapsed < deadline s op) "native-intent-order-refused"
                // Reserve the entire P2 command + wrapper + termination + drain + handoff
                // from the original deadline. Starting a child never renews this clock.
                if op="preflight" then require s.Resources.IsSome "native-pre-effect-resources-required"
                if op="p2" then require s.P2Ready "native-p2-consumption-proof-required"
                if op="p2" then require (deadline s op-s.Elapsed >= 1415) "native-insufficient-p2-budget"
                if op="source" || op="image" || op="p2" then
                    require (s.StartedUnix+int64 s.Elapsed < rootExpiry (root s)) "native-packet-expired"
                if op="image" || op="p2" then require (s.RevalidatedPhase=Some s.Phase && s.SourceIdentity=s.CurrentIdentity) "native-consumption-revalidation-required"
                let resource,id = str e "resource",str e "identity"
                let next=owned s resource id
                let intent={Operation=op;Resource=resource;Identity=id;Started=s.Elapsed;Pid=None;StartIdentity=None}
                e,{ next with RevalidatedPhase=None; Intents=next.Intents.Add(op,intent); Phase=(if op="cleanup" then "cleanup" elif op="collection" then "collecting" else op+"-running"); Outcome=(if op="image" || op="p2" then "unknown" else next.Outcome); CollectionStarted=(if op="collection" then Some s.Elapsed else s.CollectionStarted) }
            | "ack" ->
                shape ["operation";"pid";"startIdentity"]
                let op=str e "operation"
                let i=s.Intents.TryFind op |> Option.defaultWith (fun () -> fail "native-ack-without-intent")
                require (i.Pid.IsNone && number e "pid">0L && token (str e "startIdentity") && s.Elapsed < deadline s op) "native-child-ack-refused"
                e,{s with Intents=s.Intents.Add(op,{i with Pid=Some(number e "pid");StartIdentity=Some(str e "startIdentity")})}
            | "source" ->
                shape ["evidence"]; require (not historical) "native-historical-plaintext-refused"; requireAlive()
                require (s.Phase="source-running" && acknowledged "source" s && s.Elapsed < deadline s "source") "native-source-order-refused"
                let snapshot=acquireSource (root s) (e.GetProperty "evidence")
                let revision,tree,inventory=product (root s)
                let proof=JsonSerializer.SerializeToElement({|identity=sourceIdentity snapshot;revision=revision;tree=tree;inventorySha256=inventory|})
                let normalized=JsonSerializer.SerializeToElement({|kind="source-proof";elapsed=s.Elapsed;proof=proof|})
                normalized,sourceProof s proof
            | "source-proof" ->
                shape ["proof"]; require historical "native-self-authored-source-proof-refused"; requireAlive()
                require (s.Phase="source-running" && acknowledged "source" s && s.Elapsed < deadline s "source") "native-source-proof-order-refused"
                e,sourceProof s (e.GetProperty "proof")
            | "revalidate-source" ->
                shape ["evidence"]; require (not historical) "native-historical-plaintext-refused"; requireAlive()
                require (s.Phase="source-ready" || s.Phase="preflight-ready" || s.Phase="image-ready" || s.Phase="image-running") "native-revalidation-order-refused"
                if s.Phase="image-running" then require (s.Elapsed<deadline s "image") "native-build-deadline-refused"
                let snapshot=acquireSource (root s) (e.GetProperty "evidence")
                require (Some(sourceIdentity snapshot)=s.CurrentIdentity && s.SourceIdentity=s.CurrentIdentity) "native-changed-source-refused"
                let proof=JsonSerializer.SerializeToElement({|identity=sourceIdentity snapshot;phase=s.Phase|})
                JsonSerializer.SerializeToElement({|kind="revalidated-proof";elapsed=s.Elapsed;proof=proof|}),{s with RevalidatedPhase=Some s.Phase}
            | "revalidated-proof" ->
                shape ["proof"]; require historical "native-self-authored-source-proof-refused"
                let proof=e.GetProperty "proof"
                closed ["identity";"phase"] proof
                require (Some(str proof "identity")=s.CurrentIdentity && str proof "phase"=s.Phase) "native-source-proof-refused"
                e,{s with RevalidatedPhase=Some s.Phase}
            | "revalidate-p2" ->
                shape ["evidence"]; require (not historical) "native-historical-p2-bytes-refused"; requireAlive()
                require (s.Phase="image-ready" && s.Mode="full") "native-p2-revalidation-order-refused"
                let proof=e.GetProperty "evidence"
                closed ["sourceSha";"sourceTree";"executorDllPath"] proof
                let expected=(packet (root s)).GetProperty "p2"
                require (str proof "sourceSha"=str expected "revision" && str proof "sourceTree"=str expected "tree") "native-p2-source-refused"
                hashFile (str expected "executorDllSha256") (str proof "executorDllPath") |> ignore
                let normalized=JsonSerializer.SerializeToElement({|kind="p2-proof";elapsed=s.Elapsed;identity=str expected "executorDllSha256";revision=str expected "revision";tree=str expected "tree"|})
                normalized,{s with P2Ready=true}
            | "p2-proof" ->
                shape ["identity";"revision";"tree"]; require historical "native-self-authored-p2-proof-refused"; requireAlive()
                let expected=(packet (root s)).GetProperty "p2"
                require (s.Phase="image-ready" && s.Mode="full" && str e "identity"=str expected "executorDllSha256" && str e "revision"=str expected "revision" && str e "tree"=str expected "tree") "native-p2-proof-refused"
                e,{s with P2Ready=true}
            | "resources" ->
                shape ["path";"launcherUid";"launcherGid"]; requireAlive()
                require ((s.Phase="source-ready" || s.Phase="preflight-running") && s.Elapsed<deadline s "preflight"
                         && number e "launcherUid">0L && number e "launcherGid">=0L) "native-resources-order-refused"
                resources (str e "path") |> ignore
                e,{s with Resources=Some(number e "launcherUid",number e "launcherGid")}
            | "checkpoint" ->
                shape ["path"]; requireAlive()
                require (s.Phase="image-running" && s.Elapsed<deadline s "image") "native-checkpoint-order-refused"
                let facts=json (str e "path")
                closed ["schema";"phase";"scratchUsedBytes";"scratchFreeBytes";"requiredHeadroomBytes";"availableHeadroomBytes";"admitted";"memory"] facts
                let used,free=number facts "scratchUsedBytes",number facts "scratchFreeBytes"
                let remaining=max 0L (5368709120L-used)
                let required=remaining+2147483648L
                let observed=memoryHeadroom (facts.GetProperty "memory")
                require (str facts "schema"="fsgg.fourd.image-temporary-checkpoint/1"
                         && (Set ["before-prepare";"before-fresh-load";"before-build";"before-save"] |> Set.contains (str facts "phase"))) "native-checkpoint-schema-refused"
                require (used>=0L && free>=remaining && observed>=required && number facts "availableHeadroomBytes"=observed
                         && number facts "requiredHeadroomBytes"=required) "native-checkpoint-headroom-refused"
                e,s
            | "archive" ->
                shape ["path"]; require (not historical) "native-historical-archive-payload-refused"; requireAlive()
                require (s.Mode="full" && s.Phase="image-running" && acknowledged "image" s && s.RevalidatedPhase=Some "image-running" && s.SourceIdentity.IsSome && s.SourceIdentity=s.CurrentIdentity && s.Elapsed<deadline s "image") "native-archive-consumption-order-refused"
                let proof=acquireArchive (root s) s.SourceIdentity.Value (str e "path")
                let normalized=JsonSerializer.SerializeToElement({|kind="archive-proof";elapsed=s.Elapsed;proof=archiveProjection proof|})
                normalized,acceptArchive s proof
            | "archive-proof" ->
                shape ["proof"]; require historical "native-self-authored-archive-proof-refused"; requireAlive()
                require (s.Mode="full" && s.Phase="image-running" && acknowledged "image" s && s.RevalidatedPhase=Some "image-running" && s.SourceIdentity.IsSome && s.SourceIdentity=s.CurrentIdentity && s.Elapsed<deadline s "image") "native-archive-proof-order-refused"
                let proof=restoreArchive (root s) s.SourceIdentity.Value (e.GetProperty "proof")
                e,acceptArchive s proof
            | "preflight" | "image" | "p2" ->
                shape ["path"]; requireAlive()
                require (s.Phase=kind+"-running" && acknowledged kind s && s.Elapsed < deadline s kind && s.SourceIdentity=s.CurrentIdentity && s.SourceIdentity.IsSome) "native-result-without-acquired-join"
                let path=str e "path"
                if kind="preflight" then
                    let uid,gid=s.Resources.Value
                    preflight (root s) uid gid path |> ignore
                    e,{s with Phase="preflight-ready";Outcome=(if s.Mode="capability" then "capability-complete" else s.Outcome)}
                elif kind="image" then
                    require (s.Mode="full") "native-capability-effect-refused"
                    require (s.RevalidatedPhase=Some "image-running") "native-build-consumption-proof-required"
                    require (s.ArchiveProof.IsSome && s.CurrentArchiveIdentity=Some(archiveIdentity s.ArchiveProof.Value)) "native-image-archive-proof-required"
                    let image=image (root s) (not historical) path
                    require (matchesArchive (root s) s.SourceIdentity.Value s.ArchiveProof.Value image) "native-image-archive-binding-refused"
                    e,{s with Phase="image-ready";Image=Some image;RevalidatedPhase=None}
                else
                    require (s.Mode="full" && s.Image.IsSome) "native-p2-image-required"
                    let result=p2 (root s) s.Image.Value path
                    e,{s with Phase="p2-ready";P2=Some result;Outcome="success"}
            | "mutate" ->
                shape ["identity"]; require (token (str e "identity") && Some(str e "identity")<>s.CurrentIdentity) "native-mutation-refused"
                e,sticky "native-changed-source-refused" {s with CurrentIdentity=Some(str e "identity")}
            | "cancel" -> shape []; e,{sticky "native-cancelled" s with Cancelled=true}
            | "refuse" -> shape ["reason"]; require (token (str e "reason")) "native-refusal-token-refused"; e,sticky (str e "reason") s
            | "begin-cleanup" ->
                shape []; require (s.Phase<>"finished" && s.Phase<>"incomplete" && s.CleanupStarted.IsNone) "native-cleanup-order-refused"
                e,{s with Phase="cleanup";CleanupStarted=Some s.Elapsed;Outcome=(if s.Outcome="none" then "unknown" else s.Outcome)}
            | "close" ->
                shape ["resource";"identity";"childrenRemaining";"readersRemaining";"pathsRemaining"]
                let role,id=str e "resource",str e "identity"
                require ((s.Phase="cleanup" || (s.Phase="collecting" && (s.Intents.TryFind "collection" |> Option.exists (fun i -> i.Resource=role)))) && s.Owned.TryFind role=Some id && not (s.Closed.Contains role) && number e "childrenRemaining"=0L && number e "readersRemaining"=0L && number e "pathsRemaining"=0L) "native-owned-settlement-refused"
                e,{s with Closed=s.Closed.Add role}
            | "retired" ->
                shape []; require (s.Phase="cleanup") "native-retirement-order-refused"
                let complete=s.Closed=(s.Owned |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
                let timely=s.Elapsed <= deadline s "cleanup" && (s.CleanupStarted |> Option.exists (fun start -> s.Elapsed-start<=480))
                e,{s with Phase=(if complete && timely then "retired" else "incomplete")}
            | "remote" -> shape ["evidence"]; e,{s with RemoteConclusion=Some(remoteSettled (root s) (e.GetProperty "evidence"))}
            | "root-refuse" ->
                shape ["reason"]
                require (token (str e "reason")) "native-root-refusal-token-refused"
                e,{s with RootRefusal=(if s.RootRefusal.IsSome then s.RootRefusal else Some(str e "reason"))}
            | "remote-credential-retirement" ->
                shape ["evidence"]
                let facts=remoteCredentialRetirement (root s) (e.GetProperty "evidence")
                e,{s with RemoteRetirement=Some facts}
            | "finish" ->
                shape []; require (s.Phase="collecting" && s.CollectionStarted.IsSome) "native-collection-order-refused"
                e,{s with Phase=(if s.Elapsed-s.CollectionStarted.Value<=180 && s.Elapsed<=budget s && acknowledged "collection" s && s.Closed=(s.Owned |> Map.toSeq |> Seq.map fst |> Set.ofSeq) then "finished" else "incomplete")}
            | _ -> fail "native-observation-kind-refused"
        // A sticky refusal/cancellation remains effective through later positive evidence.
        require (not s.Cancelled || next.Cancelled) "native-cancellation-renewal-refused"
        normalized,next

    let modelProjection s =
        let current=if s.CurrentIdentity.IsNone then 0 elif s.CurrentIdentity=s.SourceIdentity then 1 else 2
        let latestOperation=
            match s.Phase with
            | "source-running" | "source-ready" -> "source"
            | "preflight-running" | "preflight-ready" -> "preflight"
            | "image-running" | "image-ready" -> "image"
            | "p2-running" | "p2-ready" -> "p2"
            | "cleanup" | "retired" -> if s.Intents.ContainsKey "cleanup" then "cleanup" else (s.Intents |> Map.toSeq |> Seq.sortBy (fun (_,i) -> i.Started) |> Seq.tryLast |> Option.map fst |> Option.defaultValue "")
            | "collecting" | "finished" | "incomplete" -> "collection"
            | _ -> ""
        let role resource =
            s.Intents |> Map.toSeq |> Seq.tryFind (fun (_,i) -> i.Resource=resource) |> Option.map fst |> Option.defaultValue "key"
        let archive=if s.ArchiveProof.IsSome then 1 else 0
        let currentArchive=if s.CurrentArchiveIdentity.IsNone then 0 elif s.ArchiveProof |> Option.exists (fun p -> Some(archiveIdentity p)=s.CurrentArchiveIdentity) then 1 else 2
        {| archive=archive;currentArchive=currentArchive;ownedRoles=s.Owned |> Map.toArray |> Array.map (fst >> role);closedRoles=s.Closed |> Set.toArray |> Array.map role
           issued=s.Intents |> Map.toArray |> Array.map fst;acknowledged=s.Intents |> Map.toArray |> Array.filter (fun (_,i) -> i.Pid.IsSome) |> Array.map fst
           preflightDeadline=deadline s "preflight";imageDeadline=deadline s "image";p2Deadline=deadline s "p2";cleanupDeadline=deadline s "cleanup";collectionDeadline=deadline s "collection"
           collectionAllowance=180;cleanupAllowance=480;p2Command=1200;p2Operation=900;wrapper=120;termination=20;drain=15;handoff=60
           rootRefused=s.RootRefusal.IsSome;remoteObservationElapsed=Option.defaultValue -1 s.LastRemoteElapsed;remoteConclusion=Option.defaultValue "unknown" s.RemoteConclusion;remoteCredentialRetirement=s.RemoteRetirement.IsSome
           mode=s.Mode;phase=s.Phase;elapsed=s.Elapsed;producer=s.Producer.IsSome;root=s.Root.IsSome
           source=(if s.SourceIdentity.IsSome then 1 else 0);current=current;resources=s.Resources.IsSome
           revalidated=s.RevalidatedPhase.IsSome;image=s.Image.IsSome;p2=s.P2.IsSome;p2Ready=s.P2Ready
           intents=s.Intents.Count;acks=(s.Intents |> Map.filter (fun _ i -> i.Pid.IsSome) |> Map.count)
           latestAck=(s.Intents.TryFind latestOperation |> Option.exists (fun i -> i.Pid.IsSome));cleanupIssued=s.Intents.ContainsKey "cleanup"
           owned=s.Owned.Count;closed=s.Closed.Count;cancelled=s.Cancelled;refused=s.Refusal.IsSome;outcome=s.Outcome
           cleanupStarted=Option.defaultValue -1 s.CleanupStarted;collectionStarted=Option.defaultValue -1 s.CollectionStarted |}
