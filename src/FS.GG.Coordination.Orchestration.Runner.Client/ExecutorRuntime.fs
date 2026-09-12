namespace FS.GG.Coordination.Orchestration.Runner.Client

open System
open System.Buffers.Binary
open System.Collections.Concurrent
open System.IO
open System.Globalization
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Execution.Codex
open FS.GG.Coordination.Orchestration.Runner.Protocol

type ExecutorRuntimeOptions =
    { RepositoryRoot:string;WorkspaceRoot:string;InputRoot:string;StateRoot:string;ArtifactRoot:string
      CodexExecutable:string;ExecutorBinding:string;MaximumFrameBytes:int }

type private Supervised =
    { Command:ExecutorCommandV2; Manifest:ExecutorWorkspaceManifest; Provider:IExecutionProvider
      Inspector:GitCandidateInspector; mutable Session:ProviderSessionReference option; mutable Readiness:ProviderReadiness option }

type private LaunchPersistence = NewLaunch | ExistingLaunch | ConflictingLaunch

type ExecutorRuntime(options:ExecutorRuntimeOptions,clock:TimeProvider) =
    let manifests=ConcurrentDictionary<string,ExecutorWorkspaceManifest>()
    let inputManifests=ConcurrentDictionary<string,ExecutorInputManifest>()
    let sessions=ConcurrentDictionary<string,Supervised>()
    let attemptGenerations=ConcurrentDictionary<string,int64>()
    let artifacts=ConcurrentDictionary<Guid,CandidateArtifact>()
    let outputLock=new SemaphoreSlim(1,1)
    let key (command:ExecutorCommandV2)=command.AssignmentId.ToString("N")+":"+command.AttemptId.ToString("N")+":"+string command.Generation
    let attemptKey (command:ExecutorCommandV2)=command.AssignmentId.ToString("N")+":"+command.AttemptId.ToString("N")
    let admitGeneration (command:ExecutorCommandV2) =
        let memoryKey=attemptKey command
        let directory=Path.Combine(options.StateRoot,"executor-control",command.AssignmentId.ToString("N"),command.AttemptId.ToString("N"))
        Directory.CreateDirectory directory|>ignore
        let path=Path.Combine(directory,"generation")
        let encoded=command.Generation.ToString(CultureInfo.InvariantCulture)
        let temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp"
        let durable =
            try
                use stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)
                use writer=new StreamWriter(stream)
                writer.Write encoded
                writer.Flush();stream.Flush(true)
                File.Move(temporary,path,false)
                command.Generation
            with :? IOException ->
                try if File.Exists temporary then File.Delete temporary with _->()
                try
                    let info=FileInfo path
                    if not info.Exists||not(isNull info.LinkTarget)||info.Length<1L||info.Length>32L then Int64.MinValue
                    else
                        match Int64.TryParse(File.ReadAllText path,NumberStyles.None,CultureInfo.InvariantCulture) with
                        | true,value when value>=0L->value
                        | _->Int64.MinValue
                with _->Int64.MinValue
        let admitted=attemptGenerations.GetOrAdd(memoryKey,durable)
        admitted=command.Generation && durable=command.Generation
    let sameAuthority (left:ExecutorCommandV2) (right:ExecutorCommandV2)=
        left.AssignmentId=right.AssignmentId && left.AttemptId=right.AttemptId && left.CandidateId=right.CandidateId && left.Generation=right.Generation
        && left.RecordedAt=right.RecordedAt && left.Deadline=right.Deadline && left.MaximumRuntimeSeconds=right.MaximumRuntimeSeconds && left.MaximumAttempts=right.MaximumAttempts
        && left.Workspace=right.Workspace && left.WorkspaceManifestSha256=right.WorkspaceManifestSha256 && left.InputDigest=right.InputDigest && left.ExecutorBinding=right.ExecutorBinding
        && left.RequestedModel=right.RequestedModel && left.RequestedEffort=right.RequestedEffort && left.WorkItemPersistenceId=right.WorkItemPersistenceId
    let sha (bytes:byte array)=SHA256.HashData bytes|>Convert.ToHexString|>_.ToLowerInvariant()
    let effectiveExpiry (command:ExecutorCommandV2) =
        let runtime = try command.RecordedAt.AddSeconds(float command.MaximumRuntimeSeconds) with _ -> DateTimeOffset.MinValue
        if runtime=DateTimeOffset.MinValue then runtime elif runtime<command.Deadline then runtime else command.Deadline
    let writeFrame (output:Stream) (bytes:byte array) = task {
        do! outputLock.WaitAsync()
        try
            let header=Array.zeroCreate<byte> 4
            BinaryPrimitives.WriteInt32BigEndian(header,bytes.Length)
            do! output.WriteAsync(header)
            do! output.WriteAsync(bytes)
            do! output.FlushAsync()
        finally outputLock.Release()|>ignore }
    let response (command:ExecutorCommandV2) kind provider adapter authenticationState authenticationProvenance supportsResume session lifecycle requestedModel requestedEffort resolvedModel resolvedEffort (output:OutputReference list) (lifecycleReferences:OutputReference list) (usage:Map<string,UsageValue>) (invocationCost:MonetaryCost) (broaderCost:MonetaryCost) (candidate:CandidateReference option) detail : ExecutorResponse =
        let costFields cost =
            match cost with
            | CostKnown(amount,currency,provenance)->"known",Nullable amount,currency,provenance
            | CostUnknown provenance->"unknown",Nullable(),null,provenance
            | CostNotApplicable provenance->"not-applicable",Nullable(),null,provenance
        let invocationState,invocationAmount,invocationCurrency,invocationProvenance=costFields invocationCost
        let broaderState,broaderAmount,broaderCurrency,broaderProvenance=costFields broaderCost
        let usageValues = usage |> Map.toArray |> Array.map(fun (name,value)->
            match value with
            | UsageKnown(number,unitName,provenance)->{Name=name;State="observed";Value=Nullable number;UnitName=unitName;Provenance=provenance}
            | UsageUnknown provenance->{Name=name;State="unknown";Value=Nullable();UnitName=null;Provenance=provenance}
            | UsageNotApplicable provenance->{Name=name;State="not-applicable";Value=Nullable();UnitName=null;Provenance=provenance})
        let refs (values:OutputReference list)=values|>List.map(fun value->{Kind=value.Kind;Reference=value.Reference;Digest=value.Digest|>Option.toObj}:ExecutorReference)|>List.toArray
        let candidateId,head,tree=match candidate with Some value->value.CandidateId,value.HeadSha,value.TreeSha|None->Guid.Empty,null,null
        {Schema=ExecutorWire.responseSchema;CommandId=command.CommandId;BodySha256=command.BodySha256;Kind=kind;Provider=provider;AdapterVersion=adapter
         AuthenticationState=authenticationState;AuthenticationProvenance=authenticationProvenance;SupportsResume=supportsResume;ProviderSessionReference=session
         Lifecycle=lifecycle;RequestedModel=requestedModel;RequestedEffort=requestedEffort;ResolvedModel=resolvedModel;ResolvedEffort=resolvedEffort
         Output=refs output;LifecycleReferences=refs lifecycleReferences;Usage=usageValues
         InvocationCostState=invocationState;InvocationCostAmount=invocationAmount;InvocationCostCurrency=invocationCurrency;InvocationCostProvenance=invocationProvenance
         BroaderCostState=broaderState;BroaderCostAmount=broaderAmount;BroaderCostCurrency=broaderCurrency;BroaderCostProvenance=broaderProvenance
         CandidateId=candidateId;CandidateHeadSha=head;CandidateTreeSha=tree;ObservedAt=clock.GetUtcNow();Detail=detail}
    let unknownUsage reason=Map["provider-usage",UsageUnknown reason]
    let operation (command:ExecutorCommandV2) operation disposition (session:ProviderSessionReference option) reason : ExecutorOperationOutcome =
        {Schema=ExecutorWire.operationOutcomeSchema;CommandId=command.CommandId;BodySha256=command.BodySha256;Operation=operation;Disposition=disposition
         ProviderSessionReference=session|>Option.map ProviderSessionReference.value|>Option.toObj;ObservedAt=clock.GetUtcNow();Reason=reason}
    let receipt (command:ExecutorCommandV2) disposition processObserved detail =
        {Schema=ExecutorWire.receiptSchema;CommandId=command.CommandId;BodySha256=command.BodySha256;Disposition=disposition;DurableRevision=command.ExpectedRevision;ProcessCreationObserved=processObserved;Detail=detail}
    let lifecycle (value:SessionLifecycle) = match value with Starting->"starting"|Running->"running"|Cancelling->"cancelling"|Succeeded->"succeeded"|Failed->"failed"|Cancelled->"cancelled"|DeadlineExceeded->"deadline-exceeded"|OutcomeUnknown->"outcome-unknown"
    let observationResponse (command:ExecutorCommandV2) (readiness:ProviderReadiness option) (observation:SessionObservation) =
        let authenticationState,authenticationProvenance,supportsResume =
            match readiness with
            | Some value->
                let state,provenance=match value.Authentication with Authenticated p->"authenticated",p|NotAuthenticated p->"not-authenticated",p|AuthenticationUnknown p->"unknown",p
                state,provenance,value.SupportsResume
            | None->"unknown","session-observation-without-fresh-readiness",false
        response command "session-observation" observation.Provider.Provider observation.Provider.AdapterVersion authenticationState authenticationProvenance supportsResume
            (ProviderSessionReference.value observation.Session) (lifecycle observation.Lifecycle)
            (command.RequestedModel) (command.RequestedEffort) (observation.Resolved.Model|>Option.toObj) (observation.Resolved.Effort|>Option.toObj)
            observation.Output observation.LifecycleReferences observation.Usage.Values observation.Usage.Cost (CostUnknown "broader-monetary-attribution-unavailable") observation.Candidate ""
    let build (command:ExecutorCommandV2) (manifest:ExecutorWorkspaceManifest) =
        if manifest.Workspace<>command.Workspace||manifest.InputDigest<>command.InputDigest then Error "executor-manifest-command-conflict"
        elif sha(ExecutorWire.encodeWorkspaceManifest manifest)<>command.WorkspaceManifestSha256 then Error "executor-manifest-digest-mismatch"
        elif command.ExecutorBinding<>options.ExecutorBinding then Error "executor-binding-refused"
        else
            ExecutorWorkspace.materialize options.RepositoryRoot options.WorkspaceRoot command.AssignmentId command.AttemptId command.Generation manifest
            |> Result.map(fun workspace->
                let input=DigestInput(options.InputRoot,command.InputDigest,16L*1024L*1024L):>ICodexExecutionInput
                let inspector=GitCandidateInspector(workspace,manifest,options.ArtifactRoot,command.CommandId,command.CandidateId)
                let providerOptions={CodexExecutionProviderOptions.create options.CodexExecutable options.StateRoot with MaximumStreamBytes=1024*1024}
                let provider=CodexExecution.provider providerOptions input (inspector:>ICodexCandidateInspector) clock
                {Command=command;Manifest=manifest;Provider=provider;Inspector=inspector;Session=None;Readiness=None})
    let persistLaunch (command:ExecutorCommandV2) =
        let directory=Path.Combine(options.StateRoot,"executor-control",command.AssignmentId.ToString("N"),command.AttemptId.ToString("N"),command.Generation.ToString())
        Directory.CreateDirectory directory|>ignore
        let path=Path.Combine(directory,"launch-command.json")
        let bytes=ExecutorWire.encodeCommandV2 command
        try
            use stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough)
            stream.Write bytes;stream.Flush(true);NewLaunch
        with :? IOException->
            try
                let existing=File.ReadAllBytes path|>ExecutorWire.parseCommandV2
                match existing with Ok value when sameAuthority value command->ExistingLaunch|_->ConflictingLaunch
            with _->ConflictingLaunch
    let findArtifact candidateId =
        match artifacts.TryGetValue candidateId with
        | true,value->Some value
        | _->
            let manifestPath=Path.Combine(options.ArtifactRoot,candidateId.ToString("N")+".manifest.json")
            let bundlePath=Path.Combine(options.ArtifactRoot,candidateId.ToString("N")+".bundle")
            if not(File.Exists manifestPath&&File.Exists bundlePath) then None
            else
                try
                    let info=FileInfo manifestPath
                    if not(isNull info.LinkTarget)||info.Length<1L||info.Length>int64 ExecutorWire.maximumControlBytes then None else
                    match File.ReadAllBytes manifestPath|>ExecutorWire.parseArtifactManifest with
                    | Ok manifest when manifest.CandidateId=candidateId->
                        let value={Manifest=manifest;BundlePath=bundlePath}
                        if artifacts.Count<4 then artifacts.TryAdd(candidateId,value)|>ignore
                        Some value
                    | _->None
                with _->None
    let ensureReadiness (value:Supervised) = task {
        match value.Readiness with
        | Some readiness->return readiness
        | None->
            let! readiness=value.Provider.ObserveReadiness CancellationToken.None
            value.Readiness<-Some readiness
            return readiness }
    let handleCommand (output:Stream) (command:ExecutorCommandV2) = task {
        let now=clock.GetUtcNow()
        let itemKey=key command
        if not(admitGeneration command) then
            do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command (if command.Kind="cancel" then "cancel" else "reconcile") "refused" None "executor-generation-stale-or-overlapping"))
        elif command.Kind="launch" && (effectiveExpiry command=DateTimeOffset.MinValue||now>=effectiveExpiry command) then
            do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "launch" "refused" None "original-execution-budget-expired"))
        elif command.Kind="content-read" then
            match findArtifact command.CandidateId with
            | None->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "content-read" "unknown" None "candidate-artifact-not-found"))
            | Some artifact when artifact.Manifest.BundleSha256<>command.ArtifactDigest->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "content-read" "unknown" None "candidate-artifact-digest-conflict"))
            | Some artifact->
                let info=FileInfo artifact.BundlePath
                if not info.Exists||not(isNull info.LinkTarget)||info.Length<>artifact.Manifest.BundleSizeBytes then
                    do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "content-read" "unknown" None "candidate-artifact-tampered"))
                else
                    use stream=File.OpenRead artifact.BundlePath
                    let observedDigest=SHA256.HashData stream|>Convert.ToHexString|>_.ToLowerInvariant()
                    stream.Position<-0L
                    if stream.Length<>artifact.Manifest.BundleSizeBytes||observedDigest<>artifact.Manifest.BundleSha256 then do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "content-read" "unknown" None "candidate-artifact-tampered"))
                    elif command.ContentOffset>stream.Length then do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "content-read" "unknown" None "candidate-artifact-offset-refused"))
                    else
                        stream.Position<-command.ContentOffset
                        let count=min command.ContentLength (int(stream.Length-stream.Position))
                        let bytes=Array.zeroCreate<byte> count
                        stream.ReadExactly bytes
                        let content={Schema=ExecutorWire.artifactContentSchema;CommandId=command.CommandId;CandidateId=command.CandidateId;BundleSha256=artifact.Manifest.BundleSha256;Offset=command.ContentOffset;Final=stream.Position=stream.Length;ContentBase64=Convert.ToBase64String bytes}
                        do! writeFrame output (ExecutorWire.encodeArtifactContent content)
                        do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "content-read" "reconciled" None "candidate-artifact-chunk-observed"))
        else
            let supervised =
                match sessions.TryGetValue itemKey with
                | true,value->Ok value
                | _ when command.Kind<>"readiness"&&sessions.Count>=4->Error "executor-session-capacity-refused"
                | _->match manifests.TryGetValue command.WorkspaceManifestSha256 with true,manifest->build command manifest|_->Error "executor-workspace-manifest-missing"
            match supervised with
            | Error reason->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command command.Kind "refused" None reason))
            | Ok value->
                let selected=if command.Kind="readiness" then value else sessions.GetOrAdd(itemKey,value)
                if not(sameAuthority selected.Command command) then
                    do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command (if command.Kind="cancel" then "cancel" else "reconcile") "refused" None "executor-command-authority-conflict"))
                else
                  match command.Kind with
                  | "readiness"->
                    let! ready=selected.Provider.ObserveReadiness CancellationToken.None
                    let authState,authProvenance=match ready.Authentication with Authenticated p->"authenticated",p|NotAuthenticated p->"not-authenticated",p|AuthenticationUnknown p->"unknown",p
                    let result=response command "readiness" ready.Identity.Provider ready.Identity.AdapterVersion authState authProvenance ready.SupportsResume null (if authState="authenticated" then "ready" else "not-ready") command.RequestedModel command.RequestedEffort null null [] [] (unknownUsage "readiness-no-usage") (CostUnknown "readiness-no-invocation") (CostUnknown "broader-monetary-attribution-unavailable") None ""
                    do! writeFrame output (ExecutorWire.encodeResponse result)
                  | "launch"->
                    let intent={Schema=ExecutionProtocol.launchSchema;Key={AssignmentId=command.AssignmentId;AttemptId=command.AttemptId;Generation=command.Generation};InputDigest=command.InputDigest;Workspace=(value.Inspector|>fun _->Path.Combine(options.WorkspaceRoot,command.AssignmentId.ToString("N"),command.AttemptId.ToString("N"),command.Generation.ToString()));Requested={Model=Option.ofObj command.RequestedModel;Effort=Option.ofObj command.RequestedEffort};Limits={Deadline=command.Deadline;MaximumRuntime=TimeSpan.FromSeconds(float command.MaximumRuntimeSeconds);MaximumAttempts=command.MaximumAttempts};RecordedAt=command.RecordedAt}
                    match persistLaunch command with
                    | ConflictingLaunch->
                        do! writeFrame output (ExecutorWire.encodeReceipt(receipt command "conflict" false "executor-launch-identity-conflict"))
                        do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "launch" "refused" None "executor-launch-identity-conflict"))
                    | ExistingLaunch->
                        do! writeFrame output (ExecutorWire.encodeReceipt(receipt command "duplicate" false "launch-intent-already-persisted"))
                        let! _=ensureReadiness selected
                        let! reconciled=selected.Provider.Reconcile(intent,CancellationToken.None)
                        match reconciled with
                        | Reconciled observation->selected.Session<-Some observation.Session;do! writeFrame output (ExecutorWire.encodeResponse(observationResponse command selected.Readiness observation))
                        | ConfirmedAbsent->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "launch" "ambiguous" None "executor-persisted-launch-local-absence-not-authoritative"))
                        | ReconcileUnknown reason->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "launch" "ambiguous" None reason))
                    | NewLaunch->
                        do! writeFrame output (ExecutorWire.encodeReceipt(receipt command "persisted" false "launch-intent-persisted-before-provider"))
                        let! readiness=ensureReadiness selected
                        match readiness.Authentication with
                        | Authenticated _->
                            let! launched=selected.Provider.Launch(intent,CancellationToken.None)
                            match launched with
                            | LaunchStarted observation->
                                selected.Session<-Some observation.Session
                                do! writeFrame output (ExecutorWire.encodeReceipt(receipt command "observed" true "provider-process-creation-observed"))
                                do! writeFrame output (ExecutorWire.encodeResponse(observationResponse command selected.Readiness observation))
                            | LaunchRefused reason->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "launch" "refused" None reason))
                            | LaunchAmbiguous reason->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "launch" "ambiguous" None reason))
                        | NotAuthenticated reason|AuthenticationUnknown reason->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "launch" "refused" None reason))
                  | "observe"->
                    match selected.Session with
                    | None->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "reconcile" "unknown" None "provider-session-not-observed"))
                    | Some session->
                        let! observed=selected.Provider.Observe(session,CancellationToken.None)
                        match observed with
                        | Error reason->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "reconcile" "unknown" (Some session) reason))
                        | Ok observation->
                            match observation.Lifecycle,observation.Candidate with
                            | Succeeded,Some candidate->
                                match if artifacts.Count>=4 && not(artifacts.ContainsKey candidate.CandidateId) then Error "executor-artifact-capacity-refused" else selected.Inspector.CreateArtifact(candidate,ExecutorWire.maximumContentBytes) with
                                | Ok artifact->
                                    artifacts[candidate.CandidateId]<-artifact
                                    do! writeFrame output (ExecutorWire.encodeArtifactManifest artifact.Manifest)
                                    do! writeFrame output (ExecutorWire.encodeResponse(observationResponse command selected.Readiness observation))
                                | Error reason->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "reconcile" "unknown" (Some session) reason))
                            | _->do! writeFrame output (ExecutorWire.encodeResponse(observationResponse command selected.Readiness observation))
                  | "reconcile"->
                    let intent={Schema=ExecutionProtocol.launchSchema;Key={AssignmentId=command.AssignmentId;AttemptId=command.AttemptId;Generation=command.Generation};InputDigest=command.InputDigest;Workspace=Path.Combine(options.WorkspaceRoot,command.AssignmentId.ToString("N"),command.AttemptId.ToString("N"),command.Generation.ToString());Requested={Model=Option.ofObj command.RequestedModel;Effort=Option.ofObj command.RequestedEffort};Limits={Deadline=command.Deadline;MaximumRuntime=TimeSpan.FromSeconds(float command.MaximumRuntimeSeconds);MaximumAttempts=command.MaximumAttempts};RecordedAt=command.RecordedAt}
                    let! _=ensureReadiness selected
                    let! reconciled=selected.Provider.Reconcile(intent,CancellationToken.None)
                    match reconciled with
                    | Reconciled observation->
                        selected.Session<-Some observation.Session
                        do! writeFrame output (ExecutorWire.encodeResponse(observationResponse command selected.Readiness observation))
                    | ConfirmedAbsent->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "reconcile" "unknown" None "executor-local-absence-not-authoritative"))
                    | ReconcileUnknown reason->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "reconcile" "unknown" None reason))
                  | "cancel"->
                    match ProviderSessionReference.create command.ProviderSessionReference with
                    | Error reason->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "cancel" "refused" None reason))
                    | Ok session->
                        let! cancelled=selected.Provider.Cancel(session,CancellationToken.None)
                        match cancelled with
                        | CancelAccepted->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "cancel" "accepted" (Some session) "termination-requested-not-yet-observed"))
                        | CancelRefused reason->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "cancel" "termination-not-observed" (Some session) reason))
                        | CancelUnknown reason->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "cancel" "unknown" (Some session) reason))
                  | _->do! writeFrame output (ExecutorWire.encodeOperationOutcome(operation command "reconcile" "refused" None "executor-command-kind-not-runnable")) }
    member _.Run(input:Stream,output:Stream,cancellationToken:CancellationToken)=task {
        let running=ResizeArray<Task>()
        let header=Array.zeroCreate<byte> 4
        let mutable finished=false
        while not finished && not cancellationToken.IsCancellationRequested do
            let mutable got=0
            while got<4 && not finished do
                let! read=input.ReadAsync(header.AsMemory(got,4-got),cancellationToken)
                if read=0 then finished<-true else got<-got+read
            if finished && got>0 then raise(EndOfStreamException "executor-frame-header-truncated")
            if not finished then
                let size=BinaryPrimitives.ReadInt32BigEndian header
                if size<1||size>options.MaximumFrameBytes then raise(InvalidDataException "executor-frame-size-refused")
                let bytes=Array.zeroCreate<byte> size
                do! input.ReadExactlyAsync(bytes,cancellationToken)
                use document=JsonDocument.Parse(ReadOnlyMemory bytes,JsonDocumentOptions(MaxDepth=8))
                let schema=document.RootElement.GetProperty("schema").GetString()
                match schema with
                | value when value=ExecutorWire.workspaceManifestSchema->
                    match ExecutorWire.parseWorkspaceManifest bytes with
                    | Ok manifest when manifests.Count>=64 && not(manifests.ContainsKey(sha bytes))->raise(InvalidDataException "executor-manifest-capacity-refused")
                    | Ok manifest->manifests[sha bytes]<-manifest
                    | Error reason->raise(InvalidDataException reason)
                | value when value=ExecutorWire.inputManifestSchema->
                    match ExecutorWire.parseInputManifest bytes with
                    | Ok manifest when inputManifests.Count>=16 && not(inputManifests.ContainsKey manifest.InputDigest)->raise(InvalidDataException "executor-input-capacity-refused")
                    | Ok manifest->inputManifests[manifest.InputDigest]<-manifest
                    | Error reason->raise(InvalidDataException reason)
                | value when value=ExecutorWire.contentSchema->
                    match ExecutorWire.parseContent bytes with
                    | Error reason->raise(InvalidDataException reason)
                    | Ok(content,payload)->
                        match inputManifests.TryGetValue content.InputDigest with
                        | false,_->raise(InvalidDataException "executor-input-manifest-missing")
                        | true,manifest->
                            if payload.Length>manifest.ChunkBytes||content.Offset>manifest.SizeBytes-int64 payload.Length then raise(InvalidDataException "executor-input-chunk-bounds-refused")
                            Directory.CreateDirectory options.InputRoot|>ignore
                            let temporary=Path.Combine(options.InputRoot,content.InputDigest+".partial")
                            let completed=Path.Combine(options.InputRoot,content.InputDigest+".input")
                            if File.Exists completed then
                                use existing=File.OpenRead completed
                                if existing.Length<>manifest.SizeBytes || (SHA256.HashData(existing)|>Convert.ToHexString|>_.ToLowerInvariant())<>manifest.InputDigest then raise(InvalidDataException "executor-input-existing-conflict")
                                existing.Position<-content.Offset
                                let replay=Array.zeroCreate<byte> payload.Length
                                existing.ReadExactly replay
                                if replay<>payload then raise(InvalidDataException "executor-input-replay-conflict")
                            else
                                if File.Exists temporary && not(isNull(FileInfo(temporary).LinkTarget)) then raise(InvalidDataException "executor-input-partial-link-refused")
                                use stream=new FileStream(temporary,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)
                                if content.Offset>stream.Length then raise(InvalidDataException "executor-input-offset-conflict")
                                elif content.Offset<stream.Length then
                                    if content.Offset+int64 payload.Length>stream.Length then raise(InvalidDataException "executor-input-replay-overlap-refused")
                                    let existing=Array.zeroCreate<byte> payload.Length
                                    stream.Position<-content.Offset;stream.ReadExactly existing
                                    if existing<>payload then raise(InvalidDataException "executor-input-replay-conflict")
                                else
                                    stream.Position<-content.Offset;stream.Write payload;stream.Flush(true)
                                if content.Final then
                                    if stream.Length<>manifest.SizeBytes then raise(InvalidDataException "executor-input-size-conflict")
                                    stream.Position<-0L
                                    if SHA256.HashData(stream)|>Convert.ToHexString|>_.ToLowerInvariant()<>manifest.InputDigest then raise(InvalidDataException "executor-input-digest-mismatch")
                                    stream.Close();File.Move(temporary,completed,false)
                | value when value=ExecutorWire.commandSchemaV2->
                    match ExecutorWire.parseCommandV2 bytes with
                    | Error reason->raise(InvalidDataException reason)
                    | Ok command->
                        let completed=running|>Seq.filter _.IsCompleted|>Seq.toArray
                        if completed.Length>0 then
                            do! Task.WhenAll completed
                            for item in completed do running.Remove item|>ignore
                        if running.Count>=64 then raise(InvalidDataException "executor-active-command-capacity-refused")
                        running.Add(task {
                            do! Task.Yield()
                            do! handleCommand output command })
                | _->raise(InvalidDataException "executor-frame-schema-refused")
        do! Task.WhenAll running }
